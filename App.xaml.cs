using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayHider.Interop;
using TrayHider.Services;
using TrayHider.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace TrayHider;

/// <summary>
/// 应用入口。职责：
///  - 单实例互斥（避免重复隐藏导致状态错乱）
///  - 创建主窗口与视图模型
///  - 本软件自身托盘图标（可隐藏）
///  - 多个全局快捷键：唤出主窗口、隐藏/恢复当前窗口、全部隐藏窗口、显示全部窗口、全部隐藏/恢复托盘图标
///  - 跟随系统深浅色切换
/// </summary>
public partial class App : Application
{
    /// <summary>快捷键 ID 基址，实际 ID = 基址 + 动作下标。</summary>
    private const int HotkeyIdBase = 0x5452;

    private const string MutexName = "Global\\TrayHider.SingleInstance.v1";

    /// <summary>快捷键 ID → 动作名。</summary>
    private readonly Dictionary<int, string> _hotkeyActionById = new();

    /// <summary>当前已成功注册的动作。</summary>
    private readonly HashSet<string> _registeredHotkeyActions = new(StringComparer.Ordinal);

    private System.Threading.Mutex? _mutex;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private WindowListViewModel? _windowViewModel;
    private NotifyIcon? _notifyIcon;
    private AppConfig _config = new();
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHandle = IntPtr.Zero;
    private bool _shuttingDown;

    /// <summary>当前配置（设置界面读取快捷键文本、窗口选项时使用）。</summary>
    public AppConfig Config => _config;

    /// <summary>窗口隐藏功能的视图模型（设置界面绑定窗口相关开关时使用）。</summary>
    public WindowListViewModel? WindowViewModel => _windowViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例检查 ----
        _mutex = new System.Threading.Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("托盘图标管家已经在运行中。\n请在系统托盘中查找它的图标，或使用全局快捷键唤出主窗口。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _config = AppConfig.Load();

        // 注册表是开机自启的真实来源，启动时把配置与注册表对齐
        var actuallyEnabled = StartupManager.IsEnabled();
        if (_config.RunAtStartup != actuallyEnabled)
        {
            _config.RunAtStartup = actuallyEnabled;
            _config.Save();
        }

        _viewModel = new MainViewModel(_config);
        _viewModel.SelfTrayIconVisibilityChanged += (_, hide) => ApplySelfTrayIconVisibility(hide);

        // 窗口隐藏功能：与托盘图标左右互不影响，但可选联动（隐藏窗口时顺带隐藏该程序的托盘图标）
        _windowViewModel = new WindowListViewModel(_config, _viewModel);

        _mainWindow = new MainWindow(_viewModel, _windowViewModel);

        // 用隐藏窗口承载全局快捷键消息
        _mainWindow.SourceInitialized += MainWindow_SourceInitialized;
        _mainWindow.Show();

        CreateTrayIcon();
        ApplySelfTrayIconVisibility(_config.HideSelfTrayIcon);

        // --minimized 由开机自启传入，直接缩到托盘
        if (e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.Hide();
        }

        WatchSystemTheme();
    }

    // ---------------------------------------------------------------- 快捷键

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_mainWindow!).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(WndProc);
        _hotkeyHandle = handle;
        RegisterAllHotkeys();
    }

    /// <summary>注册全部快捷键；个别失败不影响其它动作，只在状态栏提示。</summary>
    private void RegisterAllHotkeys()
    {
        if (_hotkeyHandle == IntPtr.Zero) return;

        UnregisterAllHotkeys();
        _hotkeyActionById.Clear();

        var failed = new List<string>();
        for (var i = 0; i < HotkeyActions.All.Length; i++)
        {
            var action = HotkeyActions.All[i];
            var id = HotkeyIdBase + i;
            _hotkeyActionById[id] = action;

            if (TryRegisterOne(id, _config.GetHotkey(action)))
            {
                _registeredHotkeyActions.Add(action);
            }
            else
            {
                failed.Add(HotkeyCatalog.Title(action));
            }
        }

        if (failed.Count > 0)
        {
            _viewModel?.SetStatus($"快捷键注册失败（可能被其他软件占用）：{string.Join("、", failed)}；可在设置中修改。");
        }
    }

    private bool TryRegisterOne(int id, HotkeyBinding binding)
    {
        if (_hotkeyHandle == IntPtr.Zero || binding is not { IsValid: true }) return false;

        var mods = (uint)binding.Modifiers | NativeMethods.MOD_NOREPEAT;
        return NativeMethods.RegisterHotKey(_hotkeyHandle, id, mods, (uint)binding.VirtualKey);
    }

    private void UnregisterAllHotkeys()
    {
        if (_hotkeyHandle == IntPtr.Zero) return;

        foreach (var id in _hotkeyActionById.Keys)
        {
            if (_registeredHotkeyActions.Contains(_hotkeyActionById[id]))
            {
                NativeMethods.UnregisterHotKey(_hotkeyHandle, id);
            }
        }

        _registeredHotkeyActions.Clear();
    }

    /// <summary>
    /// 设置并注册指定动作的快捷键。注册失败时回滚为原快捷键。
    /// </summary>
    public bool TryUpdateHotkey(string action, int modifiers, int virtualKey)
    {
        var index = Array.IndexOf(HotkeyActions.All, action);
        if (index < 0 || _hotkeyHandle == IntPtr.Zero) return false;

        var id = HotkeyIdBase + index;
        var previous = _config.GetHotkey(action).Clone();
        var candidate = new HotkeyBinding { Modifiers = modifiers & ~0x4000, VirtualKey = virtualKey };

        _hotkeyActionById[id] = action;

        // 先释放旧键，再尝试新键
        if (_registeredHotkeyActions.Remove(action))
        {
            NativeMethods.UnregisterHotKey(_hotkeyHandle, id);
        }

        if (candidate.IsValid && TryRegisterOne(id, candidate))
        {
            _registeredHotkeyActions.Add(action);
            _config.Hotkeys[action] = candidate;
            _config.Save();
            _viewModel?.NotifyHotkeyChanged();
            return true;
        }

        // 回滚
        if (TryRegisterOne(id, previous))
        {
            _registeredHotkeyActions.Add(action);
        }
        else
        {
            _viewModel?.SetStatus($"快捷键「{HotkeyCatalog.Title(action)}」恢复失败，请重新设置。");
        }
        return false;
    }

    /// <summary>兼容旧调用：等价于修改“唤出主窗口”的快捷键。</summary>
    public bool TryUpdateHotkey(int modifiers, int virtualKey)
        => TryUpdateHotkey(HotkeyActions.ShowMain, modifiers, virtualKey);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _hotkeyActionById.TryGetValue(wParam.ToInt32(), out var action))
        {
            DispatchHotkey(action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>快捷键动作分发。</summary>
    private void DispatchHotkey(string action)
    {
        switch (action)
        {
            case HotkeyActions.ShowMain:
                _mainWindow?.RestoreFromTray();
                break;

            case HotkeyActions.ToggleHideWindow:
                _windowViewModel?.ToggleForegroundWindow();
                break;

            case HotkeyActions.HideAllWindows:
                _windowViewModel?.HideAllWindows();
                break;

            case HotkeyActions.RestoreAllWindows:
                _windowViewModel?.RestoreAllWindows();
                break;

            case HotkeyActions.ToggleTrayIcons:
                ToggleAllTrayIcons();
                break;
        }
    }

    /// <summary>托盘图标一键切换：还有可见的就全部隐藏，否则全部恢复。</summary>
    private void ToggleAllTrayIcons()
    {
        if (_viewModel == null) return;

        var anyVisible = _viewModel.Icons.Any(i => !i.IsHidden && i.IsRunning);
        if (anyVisible)
        {
            _viewModel.HideAll();
        }
        else
        {
            _viewModel.RestoreAll();
        }
    }

    // ---------------------------------------------------------------- 自身托盘图标

    private void CreateTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "托盘图标管家",
            Icon = LoadAppIcon(),
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => _mainWindow?.RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => _mainWindow?.RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("隐藏全部托盘图标", null, (_, _) => _viewModel?.HideAll());
        menu.Items.Add("显示全部托盘图标", null, (_, _) => _viewModel?.RestoreAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("隐藏全部窗口", null, (_, _) => _windowViewModel?.HideAllWindows());
        menu.Items.Add("显示全部窗口", null, (_, _) => _windowViewModel?.RestoreAllWindows());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ShutdownApp());

        _notifyIcon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// 切换本软件自身托盘图标的可见性。
    /// 注意：这里只是把 Visibility 设为 false，程序仍在后台正常运行，
    /// 全局快捷键与守护逻辑都不受影响。
    /// </summary>
    private void ApplySelfTrayIconVisibility(bool hide)
    {
        if (_notifyIcon == null) return;

        if (hide)
        {
            _notifyIcon.Visible = false;
        }
        else
        {
            // 重新显示时需要重建句柄，否则部分系统上图标不会立刻回来
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "托盘图标管家";
        }

        if (_viewModel != null)
        {
            _viewModel.StatusText = hide
                ? "本软件图标已隐藏，可用快捷键唤出主窗口"
                : "本软件图标已显示在托盘";
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch
        {
            // 忽略
        }

        return SystemIcons.Application;
    }

    // ---------------------------------------------------------------- 跟随系统主题

    private void WatchSystemTheme()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General &&
                    e.Category != UserPreferenceCategory.Color)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (_mainWindow != null) WindowChrome.ApplyTheme(_mainWindow);
                }, DispatcherPriority.Background);
            };
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- 退出

    public void ShutdownApp()
    {
        // 退出流程可能由主窗口、设置窗口或托盘菜单触发，这里保证只执行一次
        if (_shuttingDown) return;
        _shuttingDown = true;

        if (_hotkeySource != null && _hotkeyHandle != IntPtr.Zero)
        {
            UnregisterAllHotkeys();
        }

        // 退出时把被隐藏的窗口恢复，绝不把用户窗口留在隐藏状态
        try
        {
            _windowViewModel?.RestoreAllOnExit();
        }
        catch
        {
            // 忽略：退出流程不应因单个窗口恢复失败而中断
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        try
        {
            _windowViewModel?.Dispose();
            _viewModel?.Dispose();
        }
        catch
        {
            // 忽略
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
