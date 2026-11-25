using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using TrayHider.Interop;
using TrayHider.Models;
using TrayHider.Services;
using Application = System.Windows.Application;

namespace TrayHider.ViewModels;

/// <summary>
/// 窗口视图模型：把普通窗口"隐藏到系统托盘"。
///
/// 与『托盘图标』分区完全并列的一套能力：
///   · 枚举可隐藏的顶层窗口（标题 / 进程名 / 窗口图标），低频增量刷新，不整表重建；
///   · 隐藏用 ShowWindowAsync(SW_HIDE)（异步投递，目标程序卡死也不会拖住本软件），
///     隐藏前记录 WINDOWPLACEMENT，恢复时原样还原位置与状态，绝不结束进程；
///   · 每个被隐藏的窗口在托盘区拥有一个自己的图标：左键单击恢复，右键可关闭窗口 / 显示全部；
///   · 可选"隐藏窗口时一并隐藏该程序原本的托盘图标"（HideWindowHidesAppTrayIcon），恢复时还回去；
///   · 退出本软件时（RestoreWindowsOnExit 默认开启）把被隐藏的窗口全部恢复，
///     绝不把用户的窗口留在隐藏态。
/// </summary>
public sealed class WindowListViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>主窗口不可见时的低频巡检间隔（毫秒），主要用于清理已自行退出的隐藏窗口。</summary>
    private const int BackgroundRefreshIntervalMs = 10000;

    /// <summary>隐藏成功后的校验延时：小于这个时间的记录不参与"是否真隐藏"的判定。</summary>
    private const int HideVerifyDelayMs = 1500;

    /// <summary>连按同一个快捷键时，"撤销刚才那次隐藏"的时间窗（毫秒）。</summary>
    private const int RestoreLastHiddenWindowMs = 2000;

    private readonly AppConfig _config;
    private readonly MainViewModel _trayViewModel;
    private readonly WindowHiderService _hider = new();

    /// <summary>窗口句柄 → 列表条目，用于增量刷新（不整表 Clear，避免闪烁与勾选丢失）。</summary>
    private readonly Dictionary<IntPtr, WindowItem> _itemsByHwnd = new();

    /// <summary>因为"隐藏窗口"而一并隐藏了托盘图标的进程名。</summary>
    private readonly HashSet<string> _trayIconsHiddenForProcesses = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _refreshTimer;
    private ExitEventHandler? _applicationExitHandler;

    private bool _isRefreshing;
    private bool _isWindowVisible;
    private bool _hasInitialRefresh;
    private bool _disposed;
    private string _statusText = "就绪";
    private int _selectedCount;

    /// <summary>最近一次由快捷键隐藏的窗口句柄，用于"再按一次快捷键 = 恢复"。</summary>
    private IntPtr _lastHiddenHwnd;

    /// <summary>最近一次由快捷键隐藏的时刻（Environment.TickCount64，毫秒）。</summary>
    private long _lastHiddenTick;

    public WindowListViewModel(AppConfig config, MainViewModel trayViewModel)
    {
        _config = config;
        _trayViewModel = trayViewModel;

        // 托盘图标的点击/右键只抛事件，具体动作由本视图模型统一编排
        _hider.RestoreRequested += (_, hwnd) => RestoreHiddenWindow(hwnd);
        _hider.CloseRequested += (_, hwnd) => CloseHiddenWindow(hwnd);
        _hider.RestoreAllRequested += (_, _) => RestoreAllWindows();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs)
        };
        _refreshTimer.Tick += (_, _) => OnRefreshTick();

        // 退出兜底：不论从主窗口还是托盘菜单退出，都先把被隐藏的窗口恢复回去
        if (Application.Current != null)
        {
            _applicationExitHandler = (_, _) =>
            {
                RestoreAllOnExit();
                Dispose();
            };
            Application.Current.Exit += _applicationExitHandler;
        }
    }

    // ---------------------------------------------------------------- 绑定属性

    /// <summary>窗口列表。</summary>
    public ObservableCollection<WindowItem> Windows { get; } = new();

    /// <summary>当前状态文案（中文，与托盘图标分区同风格）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>已勾选的窗口数。</summary>
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (Set(ref _selectedCount, value))
            {
                OnPropertyChanged(nameof(SelectionSummary));
            }
        }
    }

    /// <summary>勾选摘要文案。</summary>
    public string SelectionSummary => _selectedCount == 0
        ? "未选择任何窗口"
        : "已选择 " + _selectedCount + " 个窗口";

    /// <summary>当前被隐藏到托盘的窗口数。</summary>
    public int HiddenWindowCount => _hider.Count;

    /// <summary>列表是否非空（用于空状态提示）。</summary>
    public bool HasWindows => Windows.Count > 0;

    /// <summary>空状态提示。</summary>
    public string EmptyHint => "未发现可隐藏的窗口。\n请先打开其它程序的窗口（桌面、任务栏与本软件自身不会出现在列表里）。";

    /// <summary>隐藏窗口时是否一并隐藏该程序原本的托盘图标。</summary>
    public bool HideAppTrayIconWithWindow
    {
        get => _config.HideWindowHidesAppTrayIcon;
        set
        {
            if (_config.HideWindowHidesAppTrayIcon == value) return;
            _config.HideWindowHidesAppTrayIcon = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>退出本软件时是否把被隐藏的窗口全部恢复。</summary>
    public bool RestoreWindowsOnExit
    {
        get => _config.RestoreWindowsOnExit;
        set
        {
            if (_config.RestoreWindowsOnExit == value) return;
            _config.RestoreWindowsOnExit = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>窗口分区底部提示：当前窗口相关快捷键。</summary>
    public string HotkeyHintText
    {
        get
        {
            var toggle = _config.GetHotkey(HotkeyActions.ToggleHideWindow);
            var hideAll = _config.GetHotkey(HotkeyActions.HideAllWindows);
            var restoreAll = _config.GetHotkey(HotkeyActions.RestoreAllWindows);
            return "快捷键：" + toggle + " 隐藏/恢复当前窗口 · "
                   + hideAll + " 隐藏全部 · "
                   + restoreAll + " 恢复全部";
        }
    }

    /// <summary>快捷键被修改后刷新底部提示。</summary>
    public void NotifyHotkeyChanged() => OnPropertyChanged(nameof(HotkeyHintText));

    /// <summary>快捷键打到本软件自己的窗口上时，请求把主窗口收到托盘。</summary>
    public event EventHandler? HideSelfRequested;

    /// <summary>窗口列表刷新间隔（毫秒）。配置异常时收敛到 1.5s ~ 10s，避免频繁枚举。</summary>
    private int RefreshIntervalMs
    {
        get
        {
            var interval = _config.WindowRefreshIntervalMs;
            if (interval < 1500) interval = 1500;
            if (interval > 10000) interval = 10000;
            return interval;
        }
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>主窗口首次显示时启动低频刷新。</summary>
    public void Start()
    {
        if (_disposed) return;

        _isWindowVisible = true;
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs);
        _refreshTimer.Start();

        if (!_hasInitialRefresh) Refresh();

        OnPropertyChanged(nameof(HotkeyHintText));
    }

    /// <summary>暂停刷新。</summary>
    public void Stop() => _refreshTimer.Stop();

    /// <summary>主窗口显示/隐藏时切换刷新频率：不可见时降频巡检，仍然及时清理已退出的隐藏窗口。</summary>
    public void SetWindowVisible(bool visible)
    {
        if (_disposed || _isWindowVisible == visible) return;
        _isWindowVisible = visible;

        if (visible)
        {
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs);
            _refreshTimer.Start();
            Refresh();
        }
        else
        {
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(RefreshIntervalMs, BackgroundRefreshIntervalMs));
            _refreshTimer.Start();
        }
    }

    /// <summary>刷新定时器回调。主窗口不可见时只做低频清理，不做全量枚举。</summary>
    private void OnRefreshTick()
    {
        if (_disposed) return;

        if (_isWindowVisible)
        {
            Refresh();
            return;
        }

        var removed = PruneHiddenWindows();
        if (removed > 0)
        {
            StatusText = "有 " + removed + " 个隐藏窗口已自行关闭，托盘图标已清理";
        }
    }

    // ---------------------------------------------------------------- 刷新

    /// <summary>
    /// 增量刷新窗口列表：
    /// 以句柄为键更新已有条目、只新增真正的新窗口、只移除确实消失的条目；
    /// 被本软件隐藏的窗口对 EnumWindows 不可见，必须靠内部隐藏集合补回，
    /// 并以隐藏集合为准标记"已隐藏"，绝不因为一次刷新就把它们"复活"成显示中。
    /// </summary>
    public void Refresh()
    {
        if (_disposed || _isRefreshing) return;
        _isRefreshing = true;

        try
        {
            // 1) 先自愈：撤销"其实没隐藏成功"的记录，清理目标程序已退出的记录
            var failedHides = ReconcileHiddenWindows();
            PruneHiddenWindows();

            // 2) 枚举当前可见窗口，增量对齐列表
            var present = new HashSet<IntPtr>();
            foreach (var descriptor in WindowController.EnumerateHideableWindows())
            {
                present.Add(descriptor.Hwnd);
                UpsertWindow(descriptor);
            }

            // 3) 补回被隐藏的窗口（它们在枚举结果里看不到）
            foreach (var hidden in _hider.Items)
            {
                present.Add(hidden.Hwnd);
                UpsertHiddenWindow(hidden);
            }

            // 4) 移除确实已经不存在的条目
            for (var i = Windows.Count - 1; i >= 0; i--)
            {
                if (present.Contains(Windows[i].Hwnd)) continue;
                _itemsByHwnd.Remove(Windows[i].Hwnd);
                Windows.RemoveAt(i);
            }

            ApplyOrder();
            UpdateCounters();
            StatusText = failedHides == 0
                ? BuildStatusText()
                : "有 " + failedHides + " 个窗口隐藏失败（目标程序可能以管理员权限运行），"
                  + _hider.Count + " 个已隐藏到托盘的窗口列表见下";
        }
        catch (Exception ex)
        {
            StatusText = "刷新窗口列表失败：" + ex.Message;
        }
        finally
        {
            _hasInitialRefresh = true;
            _isRefreshing = false;
        }
    }

    private string BuildStatusText() => "共 " + Windows.Count + " 个可隐藏窗口，其中 "
                                        + _hider.Count + " 个已隐藏到托盘";

    /// <summary>新增或更新一个"当前可见"的窗口条目。</summary>
    private void UpsertWindow(WindowDescriptor descriptor)
    {
        if (_itemsByHwnd.TryGetValue(descriptor.Hwnd, out var existing))
        {
            existing.Title = descriptor.Title;
            existing.ProcessName = descriptor.ProcessName;
            existing.ExecutablePath = descriptor.ExecutablePath;
            existing.ProcessId = descriptor.ProcessId;
            existing.IsRunning = true;
            existing.IsHidden = false;
            if (!existing.IconChecked)
            {
                // 图标只在首次探测：探测失败的窗口不再每轮重试，避免反复跨进程发消息
                existing.Icon = WindowController.GetWindowIcon(descriptor.Hwnd, descriptor.ExecutablePath);
                existing.IconChecked = true;
            }
            return;
        }

        var item = new WindowItem
        {
            Hwnd = descriptor.Hwnd,
            Title = descriptor.Title,
            ProcessName = descriptor.ProcessName,
            ExecutablePath = descriptor.ExecutablePath,
            ProcessId = descriptor.ProcessId,
            IsRunning = true,
            IconChecked = true,
            Icon = WindowController.GetWindowIcon(descriptor.Hwnd, descriptor.ExecutablePath)
        };

        Windows.Add(item);
        _itemsByHwnd[item.Hwnd] = item;
    }

    /// <summary>新增或更新一个"已被本软件隐藏"的窗口条目（保持"已隐藏"状态）。</summary>
    private void UpsertHiddenWindow(HiddenWindow hidden)
    {
        var alive = WindowController.IsWindowAlive(hidden.Hwnd);

        if (_itemsByHwnd.TryGetValue(hidden.Hwnd, out var existing))
        {
            existing.IsHidden = true;
            existing.IsRunning = alive;

            if (!existing.IconChecked)
            {
                existing.Icon = WindowController.GetWindowIcon(hidden.Hwnd, hidden.ExecutablePath);
                existing.IconChecked = true;
            }

            return;
        }

        var item = new WindowItem
        {
            Hwnd = hidden.Hwnd,
            Title = hidden.Title,
            ProcessName = hidden.ProcessName,
            ExecutablePath = hidden.ExecutablePath,
            ProcessId = hidden.ProcessId,
            IsRunning = alive,
            IsHidden = true,
            IconChecked = true,
            Icon = WindowController.GetWindowIcon(hidden.Hwnd, hidden.ExecutablePath)
        };

        Windows.Add(item);
        _itemsByHwnd[item.Hwnd] = item;
    }

    /// <summary>
    /// 自愈校验：如果某条"已隐藏"记录对应的窗口其实仍然可见
    /// （典型原因：目标程序以管理员权限运行，隐藏消息被 UIPI 丢弃），
    /// 就撤销这条记录，把列表状态改回"显示中"，避免界面与实际不一致。
    /// </summary>
    /// <returns>被撤销（隐藏未生效）的记录数。</returns>
    private int ReconcileHiddenWindows()
    {
        var discarded = 0;

        foreach (var record in _hider.Items.ToList())
        {
            if (!WindowController.IsWindowAlive(record.Hwnd)) continue;
            if ((DateTime.UtcNow - record.HiddenAtUtc).TotalMilliseconds < HideVerifyDelayMs) continue;
            if (!WindowController.IsWindowVisibleNow(record.Hwnd)) continue;

            _hider.Discard(record.Hwnd);
            RestoreTrayIconsIfIdle(record.ProcessName);
            discarded++;

            if (_itemsByHwnd.TryGetValue(record.Hwnd, out var item))
            {
                item.IsHidden = false;
            }
        }

        return discarded;
    }

    /// <summary>清理目标程序已经退出的隐藏窗口（只销毁托盘图标，无需恢复窗口）。</summary>
    /// <returns>被清理的记录数。</returns>
    private int PruneHiddenWindows()
    {
        var removed = _hider.Prune();
        if (removed.Count == 0) return 0;

        foreach (var record in removed)
        {
            if (_itemsByHwnd.TryGetValue(record.Hwnd, out var item))
            {
                item.IsHidden = false;
                item.IsRunning = false;
            }

            RestoreTrayIconsIfIdle(record.ProcessName);
        }

        UpdateCounters();
        ApplyOrder();
        return removed.Count;
    }

    /// <summary>列表排序：隐藏的排在前面，其余按标题排序；用 Move 增量搬移，不重建集合。</summary>
    private void ApplyOrder()
    {
        var ordered = Windows
            .OrderByDescending(window => window.IsHidden)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Windows.IndexOf(ordered[target]);
            if (current != target && current >= 0)
            {
                Windows.Move(current, target);
            }
        }
    }

    // ---------------------------------------------------------------- 隐藏 / 恢复

    /// <summary>隐藏当前勾选的窗口。操作完成后清空这些窗口的勾选。</summary>
    public void ApplySelected()
    {
        var targets = Windows.Where(window => window.IsSelected && !window.IsHidden).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先勾选要隐藏的窗口";
            return;
        }

        var success = HideWindows(targets);
        UpdateCounters();
        ApplyOrder();
        StatusText = success == 0
            ? "隐藏失败：请确认目标窗口没有以管理员权限运行"
            : "已隐藏 " + success + " 个窗口，对应程序仍在后台运行；点击托盘里各自的图标即可恢复";
    }

    /// <summary>恢复当前勾选的已隐藏窗口。</summary>
    public void RestoreSelected()
    {
        var targets = Windows.Where(window => window.IsSelected && window.IsHidden).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先勾选要恢复的窗口";
            return;
        }

        var success = 0;
        foreach (var item in targets)
        {
            if (RestoreWindowItem(item)) success++;
        }

        UpdateCounters();
        ApplyOrder();
        StatusText = "已恢复 " + success + " 个窗口";
    }

    /// <summary>隐藏列表中全部未隐藏的窗口。</summary>
    public void HideAllWindows()
    {
        // 全局快捷键可能在主窗口处于托盘时触发，此时列表可能是低频巡检留下的旧数据，
        // 先补一次枚举，避免漏掉刚打开的窗口。
        if (!_isWindowVisible) Refresh();

        var targets = Windows.Where(window => !window.IsHidden).ToList();
        if (targets.Count == 0)
        {
            StatusText = "所有窗口都已处于隐藏状态";
            return;
        }

        var success = HideWindows(targets);
        UpdateCounters();
        ApplyOrder();
        StatusText = "已隐藏全部 " + success + " 个窗口";
    }

    /// <summary>恢复全部被隐藏的窗口。</summary>
    public void RestoreAllWindows()
    {
        if (_hider.Count == 0)
        {
            StatusText = "当前没有被隐藏的窗口";
            return;
        }

        var restored = _hider.RestoreAll();

        foreach (var item in Windows)
        {
            item.IsHidden = false;
        }

        RestoreAllTrayIcons();

        UpdateCounters();
        ApplyOrder();
        StatusText = "已恢复 " + restored + " 个窗口";
    }

    /// <summary>切换单个窗口的隐藏状态（列表行双击）。</summary>
    public void ToggleItem(WindowItem item)
    {
        if (item.IsHidden)
        {
            RestoreWindowItem(item);
        }
        else
        {
            HideWindows(new[] { item });
        }

        UpdateCounters();
        ApplyOrder();
    }

    /// <summary>
    /// 快捷键入口（Ctrl+Win+H）：隐藏 / 恢复当前前台窗口。
    ///
    /// issue 原文要求"通过快捷键再次显示"，所以同一个键必须有"再按一次 = 恢复"的可预期行为：
    ///   1) 前台是本软件自己的窗口 → 把主窗口收到托盘，而不是把"自己"藏进自己的托盘；
    ///   2) 刚由本快捷键隐藏的窗口仍在隐藏状态、且距隐藏不超过 2 秒 → 恢复它（撤销刚才的隐藏）。
    ///      之所以限定 2 秒连按窗口：若"后台有隐藏项就恢复"，"换个窗口再隐藏"会永远失效；
    ///   3) 前台窗口可隐藏 → 隐藏它，并记为"最近隐藏"（句柄 + Environment.TickCount64）；
    ///   4) 前台不可隐藏（桌面 / 任务栏 / Shell 窗口 / 找不到窗口）→ 恢复"最近隐藏"的那个窗口
    ///      （不再限制时间），兜住"焦点掉到桌面后想把窗口找回来"的常见场景；没有才提示。
    ///      真正的批量恢复有独立快捷键（RestoreAllWindows）。
    /// </summary>
    public void ToggleForegroundWindow()
    {
        if (_disposed) return;

        var foreground = WindowController.GetForegroundWindowHandle();

        // 1) 前台是本软件窗口：把主窗口收到托盘
        if (foreground != IntPtr.Zero
            && WindowController.IsWindowAlive(foreground)
            && WindowController.IsOwnProcessWindow(foreground))
        {
            HideSelfRequested?.Invoke(this, EventArgs.Empty);
            StatusText = "已把主窗口收到托盘，可用快捷键唤出";
            return;
        }

        // 2) 连按两下（2 秒内）：撤销刚才那次隐藏
        if (_lastHiddenHwnd != IntPtr.Zero
            && Environment.TickCount64 - _lastHiddenTick <= RestoreLastHiddenWindowMs
            && _hider.IsHidden(_lastHiddenHwnd))
        {
            var recentlyHidden = _lastHiddenHwnd;
            _lastHiddenHwnd = IntPtr.Zero;
            RestoreHiddenWindow(recentlyHidden);
            return;
        }

        // 3) 前台窗口可隐藏 → 隐藏它，并记为"最近隐藏"
        var item = FindWindowItem(foreground);
        if (item != null)
        {
            HandleForegroundWindowItem(item);
            return;
        }

        // 4) 前台不可隐藏：退回到"恢复最近隐藏的窗口"
        if (RestoreLastHiddenWindow()) return;

        StatusText = "当前没有可隐藏的窗口";
    }

    /// <summary>
    /// 处理"可隐藏的前台窗口"：极端情况下（记录里已隐藏，例如隐藏被 UIPI 丢弃）
    /// 先撤销记录；否则隐藏它并记成"最近隐藏"。
    /// </summary>
    private void HandleForegroundWindowItem(WindowItem item)
    {
        if (item.IsHidden || _hider.IsHidden(item.Hwnd))
        {
            // 正常情况下已隐藏的窗口不可能是前台窗口，这里做防御性撤销，让用户再按一次即可重新隐藏
            if (_lastHiddenHwnd == item.Hwnd) _lastHiddenHwnd = IntPtr.Zero;
            RestoreHiddenWindow(item.Hwnd);
            return;
        }

        HideWindows(new[] { item });

        if (item.IsHidden)
        {
            _lastHiddenHwnd = item.Hwnd;
            _lastHiddenTick = Environment.TickCount64;
            StatusText = "已隐藏「" + item.Title + "」，再按一次快捷键即可恢复";
        }

        UpdateCounters();
        ApplyOrder();
    }

    /// <summary>
    /// 恢复"最近隐藏"的那个窗口（不限制时间）；它已经被别的入口恢复过时直接作废记录。
    /// </summary>
    /// <returns>是否真的执行了恢复。</returns>
    private bool RestoreLastHiddenWindow()
    {
        if (_lastHiddenHwnd == IntPtr.Zero) return false;

        var hwnd = _lastHiddenHwnd;
        _lastHiddenHwnd = IntPtr.Zero;

        if (!_hider.IsHidden(hwnd)) return false;

        RestoreHiddenWindow(hwnd);
        return true;
    }

    /// <summary>按句柄找列表条目；找不到时先刷新一次（前台窗口可能刚弹出、还没进列表）。</summary>
    private WindowItem? FindWindowItem(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        if (!_itemsByHwnd.TryGetValue(hwnd, out var item))
        {
            Refresh();
            _itemsByHwnd.TryGetValue(hwnd, out item);
        }

        return item;
    }

    /// <summary>
    /// 批量隐藏窗口。托盘图标联动统一放到最后按进程去重执行：
    /// 托盘图标分区每处理一个进程都要对齐一次托盘列表，逐个窗口调用会明显变慢。
    /// </summary>
    private int HideWindows(IReadOnlyList<WindowItem> targets)
    {
        var success = 0;
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets)
        {
            if (!HideWindowItem(item)) continue;

            success++;
            if (!string.IsNullOrEmpty(item.ProcessName)) processNames.Add(item.ProcessName);
        }

        // 可选：把该程序原本的托盘图标也一起隐藏（恢复窗口时再还回去）
        if (_config.HideWindowHidesAppTrayIcon)
        {
            foreach (var processName in processNames)
            {
                _trayIconsHiddenForProcesses.Add(processName);
                HideTrayIconsForProcess(processName);
            }
        }

        return success;
    }

    /// <summary>隐藏单个窗口：记录状态 → SW_HIDE → 建该窗口专属的托盘图标。</summary>
    private bool HideWindowItem(WindowItem item)
    {
        if (item.IsHidden) return true;

        if (!WindowController.IsWindowAlive(item.Hwnd))
        {
            StatusText = "「" + item.Title + "」已经不存在了";
            return false;
        }

        if (!_hider.Hide(item.Hwnd, item.Title, item.ProcessName, item.ExecutablePath, item.ProcessId))
        {
            StatusText = "隐藏「" + item.Title + "」失败：目标窗口可能以管理员权限运行";
            return false;
        }

        item.IsHidden = true;
        item.IsSelected = false;
        return true;
    }

    /// <summary>恢复单个窗口（并可选地把该程序的托盘图标还回去）。</summary>
    private bool RestoreWindowItem(WindowItem item)
    {
        if (!item.IsHidden) return true;

        var processName = item.ProcessName;
        var restored = _hider.Restore(item.Hwnd);

        item.IsHidden = false;
        item.IsSelected = false;
        item.IsRunning = WindowController.IsWindowAlive(item.Hwnd);
        RestoreTrayIconsIfIdle(processName);

        if (!restored)
        {
            StatusText = "恢复「" + item.Title + "」失败：窗口可能已经关闭";
        }

        return restored;
    }

    /// <summary>从托盘图标点击进入的恢复入口（此时列表条目可能还没刷新出来）。</summary>
    private void RestoreHiddenWindow(IntPtr hwnd)
    {
        var record = _hider.Find(hwnd);
        if (record == null) return;

        var title = record.Title;
        var processName = record.ProcessName;

        var restored = _hider.Restore(hwnd);
        RestoreTrayIconsIfIdle(processName);

        if (_itemsByHwnd.TryGetValue(hwnd, out var item))
        {
            item.IsHidden = false;
            item.IsRunning = WindowController.IsWindowAlive(hwnd);
        }

        UpdateCounters();
        ApplyOrder();
        StatusText = restored
            ? "已恢复「" + title + "」"
            : "恢复「" + title + "」失败：窗口可能已经关闭";
    }

    /// <summary>
    /// 从托盘图标右键菜单关闭窗口。
    /// 先恢复窗口再发 WM_CLOSE：不少程序关闭时会弹"是否保存"对话框，
    /// 窗口还处于隐藏态的话用户根本看不到这个对话框。
    /// </summary>
    private void CloseHiddenWindow(IntPtr hwnd)
    {
        var record = _hider.Find(hwnd);
        if (record == null) return;

        var title = record.Title;
        var processName = record.ProcessName;

        _hider.Restore(hwnd);
        RestoreTrayIconsIfIdle(processName);

        if (_itemsByHwnd.TryGetValue(hwnd, out var item))
        {
            item.IsHidden = false;
        }

        UpdateCounters();
        ApplyOrder();
        StatusText = WindowController.CloseWindow(hwnd)
            ? "已关闭「" + title + "」"
            : "关闭「" + title + "」失败：窗口可能已经关闭";
    }

    // ---------------------------------------------------------------- 托盘图标联动

    /// <summary>隐藏指定进程的托盘图标（由托盘图标分区实现，返回值容错，不关心成功数）。</summary>
    private void HideTrayIconsForProcess(string processName)
    {
        if (_trayViewModel == null) return;

        try
        {
            _trayViewModel.HideTrayIconsForProcess(processName);
        }
        catch
        {
            // 托盘图标联动失败不应影响窗口隐藏本身
        }
    }

    /// <summary>该进程还有窗口处于隐藏状态时不动它的托盘图标，全部恢复后再还回去。</summary>
    private void RestoreTrayIconsIfIdle(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return;

        var stillHidden = _hider.Items.Any(hidden =>
            string.Equals(hidden.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (stillHidden) return;

        if (!_trayIconsHiddenForProcesses.Remove(processName)) return;

        RestoreTrayIconsForProcess(processName);
    }

    private void RestoreTrayIconsForProcess(string processName)
    {
        if (_trayViewModel == null) return;

        try
        {
            _trayViewModel.RestoreTrayIconsForProcess(processName);
        }
        catch
        {
            // 同上，联动失败不影响窗口恢复
        }
    }

    /// <summary>把因为隐藏窗口而一并隐藏的托盘图标全部还回去。</summary>
    private void RestoreAllTrayIcons()
    {
        foreach (var processName in _trayIconsHiddenForProcesses.ToList())
        {
            _trayIconsHiddenForProcesses.Remove(processName);
            RestoreTrayIconsForProcess(processName);
        }
    }

    // ---------------------------------------------------------------- 选择辅助

    public void SelectAll()
    {
        foreach (var item in Windows) item.IsSelected = true;
        UpdateCounters();
    }

    public void InvertSelection()
    {
        foreach (var item in Windows) item.IsSelected = !item.IsSelected;
        UpdateCounters();
    }

    public void ClearSelection()
    {
        foreach (var item in Windows) item.IsSelected = false;
        UpdateCounters();
    }

    /// <summary>重新统计勾选数并刷新派生属性。</summary>
    public void UpdateCounters()
    {
        SelectedCount = Windows.Count(window => window.IsSelected);
        OnPropertyChanged(nameof(HiddenWindowCount));
        OnPropertyChanged(nameof(HasWindows));
    }

    // ---------------------------------------------------------------- 退出

    /// <summary>
    /// 退出本软件前把被隐藏的窗口全部恢复（RestoreWindowsOnExit 关闭时不动它们）。
    /// 幂等：可被主窗口退出流程与 Application.Exit 兜底各调用一次。
    /// </summary>
    public void RestoreAllOnExit()
    {
        if (!_config.RestoreWindowsOnExit) return;
        if (_hider.Count == 0 && _trayIconsHiddenForProcesses.Count == 0) return;

        try
        {
            var restored = _hider.RestoreAll();

            foreach (var item in Windows)
            {
                item.IsHidden = false;
            }

            RestoreAllTrayIcons();
            UpdateCounters();
            StatusText = "退出前已恢复 " + restored + " 个被隐藏的窗口";
        }
        catch
        {
            // 退出路径上的异常不再向外抛
        }
    }

    /// <summary>释放定时器与全部托盘图标（不恢复窗口，恢复由 RestoreAllOnExit 负责）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _refreshTimer.Stop();
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (Application.Current != null && _applicationExitHandler != null)
            {
                Application.Current.Exit -= _applicationExitHandler;
            }
        }
        catch
        {
            // 忽略
        }

        _applicationExitHandler = null;
        _hider.Dispose();
    }

    // ---------------------------------------------------------------- INPC

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
