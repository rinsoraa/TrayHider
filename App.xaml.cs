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
///  - 全局快捷键唤出主窗口
///  - 跟随系统深浅色切换
/// </summary>
public partial class App : Application
{
    private const int HotkeyId = 0x5452;
    private const string MutexName = "Global\\TrayHider.SingleInstance.v1";

    private System.Threading.Mutex? _mutex;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private NotifyIcon? _notifyIcon;
    private AppConfig _config = new();
    private HwndSource? _hotkeySource;
    private bool _hotkeyRegistered;

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

        _mainWindow = new MainWindow(_viewModel);

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
        RegisterHotkey(handle);
    }

    private void RegisterHotkey(IntPtr handle)
    {
        UnregisterHotkey(handle);

        var mods = (uint)_config.HotkeyModifiers | NativeMethods.MOD_NOREPEAT;
        var vk = (uint)_config.HotkeyVirtualKey;

        _hotkeyRegistered = NativeMethods.RegisterHotKey(handle, HotkeyId, mods, vk);

        if (!_hotkeyRegistered)
        {
            _viewModel?.SetStatus("全局快捷键注册失败，可能被其他软件占用；请在设置中修改快捷键。");
        }
    }

    /// <summary>设置并注册自定义全局快捷键。注册失败时回滚为原快捷键。</summary>
    public bool TryUpdateHotkey(int modifiers, int virtualKey)
    {
        if (_mainWindow == null) return false;

        var handle = new WindowInteropHelper(_mainWindow).Handle;
        if (handle == IntPtr.Zero) return false;

        var oldModifiers = _config.HotkeyModifiers;
        var oldVirtualKey = _config.HotkeyVirtualKey;

        UnregisterHotkey(handle);
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            handle, HotkeyId, (uint)modifiers | NativeMethods.MOD_NOREPEAT, (uint)virtualKey);

        if (_hotkeyRegistered)
        {
            _config.HotkeyModifiers = modifiers;
            _config.HotkeyVirtualKey = virtualKey;
            _config.Save();
            _viewModel?.NotifyHotkeyChanged();
            return true;
        }

        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            handle, HotkeyId, (uint)oldModifiers | NativeMethods.MOD_NOREPEAT, (uint)oldVirtualKey);
        return false;
    }

    private void UnregisterHotkey(IntPtr handle)
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _mainWindow?.RestoreFromTray();
            handled = true;
        }
        return IntPtr.Zero;
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
        menu.Items.Add("显示全部图标", null, (_, _) => _viewModel?.RestoreAll());
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
        if (_hotkeySource != null && _mainWindow != null)
        {
            var handle = new WindowInteropHelper(_mainWindow).Handle;
            UnregisterHotkey(handle);
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _viewModel?.Dispose();
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
