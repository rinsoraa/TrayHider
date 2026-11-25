using System.ComponentModel;
using System.Drawing;
using System.Security.Principal;
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

    private const string MutexName = "Global\\TrayHider.SingleInstance.v1";

    /// <summary>
    /// "请把主窗口唤出来"的进程间信号：用户重复双击 exe 时，第二个实例 Set 一下这个事件，
    /// 第一个实例的监听线程收到后把主窗口显示到前台，然后第二个实例静默退出。
    /// （旧版本没有这个事件，此时仍然回退到"已经在运行中"的提示框，保证向后兼容。）
    /// </summary>
    private const string ActivateEventName = "Global\\TrayHider.Activate.v1";

    /// <summary>第一个实例对 Activate 信号的应答，便于第二个实例判断"真的有人接管了"。</summary>
    private const string ActivateAckEventName = "Global\\TrayHider.ActivateAck.v1";

    /// <summary>"以管理员身份重启"派生出来的新实例用这个参数：先等旧实例退出再独占运行。</summary>
    private const string WaitInstanceArg = "--wait-instance";

    /// <summary>等待旧实例退出的上限（毫秒）。</summary>
    private const int WaitInstanceTimeoutMs = 8000;

    /// <summary>
    /// "请把主窗口唤出来"的窗口消息（与命名事件并行的第二条通道）。
    ///
    /// 命名事件偶尔会因为对象已存在、权限/完整性级别不同、上一个实例刚退出尚未释放等原因
    /// 创建/打开失败（实测复现过）。这条通道不依赖任何命名内核对象：
    /// 第二个实例把消息直接发给已运行实例的窗口，对方在 WndProc 里处理后回一个魔数，
    /// 发送方据此确认"真的有人接管了"。
    /// </summary>
    private static readonly uint WmActivateInstance = NativeMethods.RegisterWindowMessage("TrayHider.ActivateInstance.v1");

    /// <summary>回执魔数（"TH"），用于确认消息被真正处理。</summary>
    private const int ActivateMagic = 0x5448;

    private GlobalHotkeyManager? _hotkeys;

    private System.Threading.Mutex? _mutex;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private WindowListViewModel? _windowViewModel;
    private NotifyIcon? _notifyIcon;
    private AppConfig _config = new();
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHandle = IntPtr.Zero;
    private bool _shuttingDown;

    // ---- 单实例互唤 ----
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _activateAckEvent;
    private EventWaitHandle? _listenerShutdown;
    private System.Threading.Thread? _activateListener;

    /// <summary>空闲裁剪计时器（只在主窗口隐藏时生效）。</summary>
    private DispatcherTimer? _idleTrimTimer;

    // ---- 自身托盘菜单：改为 TrayPopupMenu（原生 TrackPopupMenuEx），不再持有 WinForms 菜单 ----

    /// <summary>当前配置（设置界面读取快捷键文本、窗口选项时使用）。</summary>
    public AppConfig Config => _config;

    /// <summary>窗口隐藏功能的视图模型（设置界面绑定窗口相关开关时使用）。</summary>
    public WindowListViewModel? WindowViewModel => _windowViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 不常驻模式（计划任务 / 开机自启调用）：只应用一次隐藏状态就退出 ----
        // 不建主窗口、不建托盘图标、不注册快捷键、不抢单实例互斥体，几百毫秒内结束。
        if (e.Args.Any(a => string.Equals(a, RunOnceService.ApplyOnceArg, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var onceConfig = AppConfig.Load();
                var started = Environment.TickCount64;
                var hidden = RunOnceService.ApplyRecordedHiddenState(onceConfig);

                // 登录后很多程序还没起来：等到"统计里的记录全部隐藏完"再退出，
                // 期间每 5 秒补一次，最多等 10 分钟，避免长期空转。
                var deadline = started + 10 * 60_000L;
                var pending = RunOnceService.CountPending(onceConfig);
                while (pending > 0 && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(5000);
                    hidden += RunOnceService.ApplyRecordedHiddenState(onceConfig);
                    pending = RunOnceService.CountPending(onceConfig);
                }

                RunOnceService.WriteStats(onceConfig, hidden, pending, Environment.TickCount64 - started);
            }
            catch
            {
                // 静默失败：下一次计划任务会重试，不打扰用户
            }

            Shutdown(0);
            return;
        }

        // ---- 单实例检查：第二次启动不再弹提示框，而是把已运行的那个实例的主窗口唤出来 ----
        _mutex = TryAcquireInstanceMutex(e.Args, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!TryActivateExistingInstance())
            {
                // 两条通道都没能唤出已运行的实例：通常是"正在运行的是旧版本"（它不认识唤出请求），
                // 或者是跨权限级别（提权/非提权）导致消息与事件都被拦下。
                MessageBox.Show(
                    Loc.T("App.AlreadyRunning"),
                    Loc.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 先把"唤出主窗口"的两条通道准备好（越早越好：第二个实例随时可能启动来敲门）
        CreateActivateEvents();

        _config = AppConfig.Load();

        // 界面语言：auto 跟随系统，其余按用户选择；切换后由 Loc 广播刷新绑定
        Loc.ApplyLanguage(_config.Language);
        Loc.LanguageChanged += (_, _) =>
        {
            if (_notifyIcon != null) _notifyIcon.Text = Loc.T("App.Tray.Tooltip");
        };

        // 不常驻模式：自启动参数改为 --apply-once，并确保计划任务存在（任务被删掉时自动补回）
        StartupManager.ArgumentsProvider = () => RunOnceService.StartupArguments(_config!);
        if (_config.RunOnceAndExit && !RunOnceService.IsScheduleInstalled())
        {
            RunOnceService.SyncSchedule(_config);
        }

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

        // 监听"再次双击 exe"的唤出请求（事件在主窗口创建之后启动监听线程，
        // 事件对象本身已在更早的 OnStartup 开头创建好，尽量缩短"探测不到"的窗口期）
        StartActivateListener();

        CreateTrayIcon();
        ApplySelfTrayIconVisibility(_config.HideSelfTrayIcon);

        // 空闲内存：主窗口收起后立刻让出工作集，之后每分钟做一次激进裁剪（内部 2 分钟冷却）
        _mainWindow.IsVisibleChanged += (_, _) =>
        {
            if (_mainWindow is { IsLoaded: true, IsVisible: false }) MemoryTrimmer.TrimIdle(aggressive: false);
        };
        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _idleTrimTimer.Tick += (_, _) =>
        {
            if (_mainWindow is { IsVisible: false }) MemoryTrimmer.TrimIdle(aggressive: true);
        };
        _idleTrimTimer.Start();

        // --minimized 由开机自启传入，直接缩到托盘
        if (e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            _mainWindow.Hide();
        }

        WatchSystemTheme();
    }

    /// <summary>
    /// 取得单实例互斥体。带 <see cref="WaitInstanceArg"/> 参数时（"以管理员身份重启"派生出来的新实例）
    /// 会先等旧实例退出，最多等 <see cref="WaitInstanceTimeoutMs"/> 毫秒，避免刚重启就撞上单实例锁
    /// 反而把旧实例唤出来。
    /// </summary>
    private static System.Threading.Mutex? TryAcquireInstanceMutex(string[] args, out bool acquired)
    {
        var shouldWait = args.Any(a => string.Equals(a, WaitInstanceArg, StringComparison.OrdinalIgnoreCase));
        var deadline = Environment.TickCount64 + (shouldWait ? WaitInstanceTimeoutMs : 0);

        while (true)
        {
            try
            {
                var mutex = new System.Threading.Mutex(true, MutexName, out var createdNew);
                if (createdNew)
                {
                    acquired = true;
                    return mutex;
                }

                mutex.Dispose();
            }
            catch
            {
                // 极少数情况下创建互斥体失败（权限/名字冲突），当作"拿不到"处理
            }

            if (Environment.TickCount64 >= deadline)
            {
                acquired = false;
                return null;
            }

            System.Threading.Thread.Sleep(250);
        }
    }

    // ---------------------------------------------------------------- 单实例互唤

    /// <summary>
    /// 作为"第二个实例"：通知已运行的实例把主窗口唤出来。
    /// 返回 true 表示信号已送达（可以静默退出），false 表示对方不理解这个信号（旧版本），
    /// 调用方应回退到"已经在运行中"的提示框。
    /// </summary>
    private static bool TryActivateExistingInstance()
    {
        // 通道一：命名事件（有回执，最可靠）
        if (TryActivateByEvent()) return true;

        // 通道二：窗口消息（不依赖命名内核对象，事件创建失败/权限受限时兜底）
        if (TryActivateByMessage()) return true;

        return false;
    }

    /// <summary>通道一：Set 命名事件，并等对方回执。</summary>
    private static bool TryActivateByEvent()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate)) return false;
            using (activate)
            {
                activate.Set();
            }

            if (EventWaitHandle.TryOpenExisting(ActivateAckEventName, out var ack))
            {
                using (ack)
                {
                    if (ack.WaitOne(TimeSpan.FromMilliseconds(1500))) return true;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通道二：向"另一个本软件实例"的窗口发送唤出消息，靠回执魔数确认已被处理。
    /// 找不到目标窗口或没人应答时返回 false。
    /// </summary>
    private static bool TryActivateByMessage()
    {
        if (WmActivateInstance == 0) return false;

        try
        {
            var exeName = Environment.ProcessPath is { Length: > 0 } path
                ? System.IO.Path.GetFileName(path)
                : string.Empty;
            if (exeName.Length == 0) return false;

            var baseName = System.IO.Path.GetFileNameWithoutExtension(exeName);
            var pids = new HashSet<int>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(baseName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    pids.Add(process.Id);
                }
            }

            if (pids.Count == 0) return false;

            var delivered = false;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (delivered) return false;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;

                try
                {
                    NativeMethods.SendMessageTimeout(hwnd, WmActivateInstance, IntPtr.Zero, IntPtr.Zero,
                        NativeMethods.SMTO_ABORTIFHUNG, 1000, out var result);
                    if (result.ToInt64() == ActivateMagic) delivered = true;
                }
                catch
                {
                    // 该窗口没应答，继续找下一个
                }

                return !delivered;
            }, IntPtr.Zero);

            return delivered;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 创建互唤用的命名事件。逐个创建：任何一个失败都不影响另一条通道（窗口消息通道始终可用）。
    /// 命名事件在"对象已存在且权限不同""上一个实例刚退出尚未释放"等情况下可能创建失败，实测踩过。
    /// </summary>
    private void CreateActivateEvents()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch
        {
            _activateEvent = null;
        }

        try
        {
            _activateAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateAckEventName);
        }
        catch
        {
            _activateAckEvent = null;
        }

        try
        {
            _listenerShutdown = new EventWaitHandle(false, EventResetMode.AutoReset);
        }
        catch
        {
            _listenerShutdown = null;
        }
    }

    /// <summary>第一个实例：起一个后台线程等待"唤出主窗口"信号（事件通道不可用时由消息通道兜底）。</summary>
    private void StartActivateListener()
    {
        if (_activateEvent == null || _listenerShutdown == null) return;

        _activateListener = new System.Threading.Thread(ActivateListenerLoop)
        {
            IsBackground = true,
            Name = "TrayHider.ActivateListener"
        };
        _activateListener.Start();
    }

    private void ActivateListenerLoop()
    {
        var handles = new WaitHandle[] { _activateEvent!, _listenerShutdown! };

        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch
            {
                return;
            }

            if (index != 0) return;   // 退出信号

            // 先回执，再唤出（顺序很重要：第二个实例拿到回执才会静默退出）
            try
            {
                _activateAckEvent?.Set();
            }
            catch
            {
                // 忽略
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(ActivateMainWindowFromRequest), DispatcherPriority.Normal);
            }
            catch
            {
                // 程序正在退出，忽略
            }
        }
    }

    /// <summary>被"再次启动"的请求唤出主窗口（事件通道与消息通道共用）。</summary>
    private void ActivateMainWindowFromRequest()
    {
        if (_shuttingDown) return;

        // 窗口被隐藏/最小化时恢复，并尽量拿到前台
        _mainWindow?.RestoreFromTray();
        _viewModel?.SetStatus(Loc.T("App.Status.Activated"));
    }

    private void StopActivateListener()
    {
        try
        {
            _listenerShutdown?.Set();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _activateListener?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // 忽略
        }

        _activateListener = null;

        _activateEvent?.Dispose();
        _activateEvent = null;
        _activateAckEvent?.Dispose();
        _activateAckEvent = null;
        _listenerShutdown?.Dispose();
        _listenerShutdown = null;
    }

    // ---------------------------------------------------------------- 权限

    /// <summary>本软件当前是否以管理员身份运行。</summary>
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重新启动本软件（用户取消 UAC 时返回 false）。
    ///
    /// 用途：部分程序的托盘图标由**更高权限**的进程注册（例如安全软件、驱动服务），
    /// 普通权限下既无法向它投递"托盘重建"消息，也无法让它重注册，恢复后图标会失去点击响应。
    /// 以管理员身份运行时这条路才通。
    /// </summary>
    public bool RelaunchAsAdministrator()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                // 新实例会先等旧实例退出再独占运行，避免两者抢单实例锁
                Arguments = WaitInstanceArg,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 对话框上点了"否"
            return false;
        }
        catch
        {
            return false;
        }

        _viewModel?.SetStatus(Loc.T("App.Status.RestartElevated"));
        ShutdownApp();
        return true;
    }

    // ---------------------------------------------------------------- 快捷键

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_mainWindow!).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(WndProc);
        _hotkeyHandle = handle;
        _hotkeys = new GlobalHotkeyManager(
            (id, binding) => NativeMethods.RegisterHotKey(handle, id, (uint)binding.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)binding.VirtualKey),
            id => NativeMethods.UnregisterHotKey(handle, id));
        var failed = _hotkeys.RegisterConfigured(_config);
        if (failed.Count > 0)
            _viewModel?.SetStatus(Loc.T("App.Status.HotkeyFailed", string.Join("、", failed)));
    }

    private bool TryApplyHotkeys(Dictionary<string, HotkeyBinding> changes, out string error, List<string>? unavailableOnReset = null)
    {
        error = Loc.T("App.Error.HotkeyNotReady");
        if (_hotkeys == null || !_hotkeys.TryApply(_config, changes, out error, unavailableOnReset)) return false;
        _config.Save();
        _viewModel?.NotifyHotkeyChanged();
        _windowViewModel?.NotifyHotkeyChanged();
        return true;
    }

    public bool TryUpdateHotkey(string action, int modifiers, int virtualKey)
        => TryApplyHotkeys(new() { [action] = new() { Modifiers = modifiers, VirtualKey = virtualKey } }, out _);

    public bool TryClearHotkey(string action) => TryApplyHotkeys(new() { [action] = new() }, out _);

    public bool TryResetHotkeys(out string message)
    {
        var unavailable = new List<string>();
        if (!TryApplyHotkeys(HotkeyBinding.CreateDefaults(), out message, unavailable)) return false;
        if (unavailable.Count != 0)
            message = Loc.T("App.Hotkey.UnavailableDefaults", string.Join("、", unavailable));
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 通道二：另一个实例通过窗口消息请求唤出主窗口。
        // 这里立刻回魔数（让对方尽快退出），真正的唤出动作异步投递到 Dispatcher，
        // 避免在跨进程 SendMessage 的处理过程中做 Show/Activate 造成重入。
        if (WmActivateInstance != 0 && msg == (int)WmActivateInstance)
        {
            handled = true;

            try
            {
                Dispatcher.BeginInvoke(new Action(ActivateMainWindowFromRequest), DispatcherPriority.Normal);
            }
            catch
            {
                // 正在退出，忽略
            }

            return new IntPtr(ActivateMagic);
        }

        if (msg == NativeMethods.WM_HOTKEY && _hotkeys != null)
        {
            // 清空/换绑后可能还有旧消息在排队；ID 和实际组合同时一致才分发。
            var bits = unchecked((uint)lParam.ToInt64());
            var mods = (int)(bits & 0xF);
            var vk = (int)(bits >> 16);
            if (_hotkeys.TryMatch(wParam.ToInt32(), mods, vk, out var action))
            {
                handled = true;
                if (_mainWindow?.TryCaptureRegisteredHotkey(mods, vk) != true) DispatchHotkey(action);
            }
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
            Text = Loc.T("App.Tray.Tooltip"),
            Icon = LoadAppIcon(),
            Visible = true,
            // 菜单改用原生 TrackPopupMenuEx（见 TrayPopupMenu）：不使用 WinForms 的自动弹菜单，
            // 因为它的位置要经过 WinForms 内部的 DPI 换算，跨缩放显示器时容易漂。
            ContextMenuStrip = null
        };

        _notifyIcon.DoubleClick += (_, _) => _mainWindow?.RestoreFromTray();

        // 右键：必须在 MouseUp 里同步弹菜单 —— 此刻"当前消息"就是 shell 投递的右键回调，
        // TrayPopupMenu 用 GetMessagePos 才能拿到右击瞬间的物理像素坐标（不会因消息延迟而漂）。
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowOwnTrayMenu();
        };
    }

    /// <summary>
    /// 自身托盘图标的右键菜单：用原生 TrackPopupMenuEx 在右击位置（物理像素）弹出，
    /// 全程不做 DPI 换算、不移动用户光标。菜单项与行为跟原 ContextMenuStrip 版本完全一致。
    /// </summary>
    private void ShowOwnTrayMenu()
    {
        const int commandOpenMainWindow = 1;
        const int commandHideAllTrayIcons = 2;
        const int commandShowAllTrayIcons = 3;
        const int commandHideAllWindows = 4;
        const int commandShowAllWindows = 5;
        const int commandRepairIcons = 6;
        const int commandExit = 7;

        var command = TrayPopupMenu.Show(
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.OpenMain"), CommandId = commandOpenMainWindow },
            TrayPopupMenuItem.Separator,
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.HideAllTrayIcons"), CommandId = commandHideAllTrayIcons },
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.ShowAllTrayIcons"), CommandId = commandShowAllTrayIcons },
            TrayPopupMenuItem.Separator,
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.HideAllWindows"), CommandId = commandHideAllWindows },
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.ShowAllWindows"), CommandId = commandShowAllWindows },
            TrayPopupMenuItem.Separator,
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.RetryRecovery"), CommandId = commandRepairIcons },
            TrayPopupMenuItem.Separator,
            new TrayPopupMenuItem { Text = Loc.T("App.Menu.Exit"), CommandId = commandExit });

        switch (command)
        {
            case commandOpenMainWindow:
                _mainWindow?.RestoreFromTray();
                break;

            case commandHideAllTrayIcons:
                _viewModel?.HideAll();
                break;

            case commandShowAllTrayIcons:
                _viewModel?.RestoreAll();
                break;

            case commandHideAllWindows:
                _windowViewModel?.HideAllWindows();
                break;

            case commandShowAllWindows:
                _windowViewModel?.RestoreAllWindows();
                break;

            case commandRepairIcons:
                RepairDegradedFromMenu();
                break;

            case commandExit:
                ShutdownApp();
                break;
        }
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
            _notifyIcon.Text = Loc.T("App.Tray.Tooltip");
        }

        if (_viewModel != null)
        {
            _viewModel.StatusText = hide
                ? _viewModel.SelfIconHint
                : Loc.T("App.Status.SelfIconShown");
        }
    }

    /// <summary>托盘菜单里触发"修复右键菜单失效的图标"：重新让目标程序重建托盘图标。</summary>
    private void RepairDegradedFromMenu()
    {
        // 显示 VM 的实际结果（包括仍需原程序重启的项），而不是把失败也报道成“没有需要修复”。
        _viewModel?.RetryDegradedIcons();
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

        StopActivateListener();

        if (_hotkeySource != null && _hotkeyHandle != IntPtr.Zero)
        {
            _hotkeys?.UnregisterAll();
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

        // 释放原生菜单的宿主窗口（类注册 + 隐藏窗口），避免退出时残留 USER 句柄
        try
        {
            TrayPopupMenu.ReleaseOwnerWindow();
        }
        catch
        {
            // 忽略：退出流程不抛异常
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
        StopActivateListener();

        try
        {
            _notifyIcon?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 互斥体可能已经被释放/不属于当前线程，退出流程不应因此中断
        }

        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }

        base.OnExit(e);
    }
}
