using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using TrayHider.Interop;
using TrayHider.Models;
using TrayHider.Services;
using Application = System.Windows.Application;

namespace TrayHider.ViewModels;

/// <summary>
/// 主视图模型。
///
/// 隐藏机制（与 gohide、以及本软件"隐藏自身托盘图标"选项同一底层 API）：
///   v1.2.2 起通过 Shell_NotifyIcon 的 NIF_STATE/NIS_HIDDEN 做**非破坏性隐藏**：
///   图标从任务栏托盘与溢出面板消失，但 Shell 里的注册与目标程序自己的回调全部保留，
///   恢复时清除状态位即可 —— 右键菜单/双击不会失效（issue#1/#3 的根治办法）。
///   绝不 NIM_DELETE 目标程序的注册，也绝不删除注册表子键、不触碰目标进程本身。
///
/// 恢复机制（issue#1 的修复核心）：
///   不能由本进程直接 NIM_ADD —— 那样会伪造出目标程序自己的 uCallbackMessage，
///   图标看得见却点不动（右键菜单失效）。正确做法是投递 TaskbarCreated 让目标程序
///   用**它自己的** hWnd/uID/回调重新注册；只有程序不响应时才降级重建，
///   并把"右键菜单可能失效"明确告诉用户。详见 <see cref="RestoreItems"/>。
///
/// 线程归约：Icons / 隐藏备份 / 计时器都只在 UI 线程上改动。所有公开入口
/// 都过了 <see cref="RunOnUiThread(Action)"/>，因此可以被主窗口与窗口列表视图模型同时调用。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// 交互式恢复（用户点"显示"）的探测总预算（毫秒）：超时即降级。
    /// 实测（build/TrayProbe）：NIM_DELETE → Post TaskbarCreated → 图标回归的耗时
    /// 为 min 6ms / 中位 11ms / 最大 109ms。v1.2.1 起从 600ms 放宽到 1000ms，
    /// 给慢机器/刚被唤醒的程序留余量（用户点"显示"时多等 0.4 秒可以接受）。
    /// 后台路径（守护/刷新）不使用这个预算，仍保持短探测，避免界面卡顿。
    /// </summary>
    private const int RestoreProbeBudgetMs = 1000;

    /// <summary>
    /// 恢复期间的探测间隔（毫秒）。实测中位 11ms 回归，15ms 一步通常一两轮就能探到；
    /// 步长再小收益有限，再大（>50ms）则会让"程序已恢复"的感知慢一截。
    /// </summary>
    private const int RestoreProbeIntervalMs = 15;

    /// <summary>配置写盘去抖间隔（毫秒）：把短时间内连续触发的保存合并成一次。</summary>
    private const int PersistDebounceMs = 300;

    /// <summary>
    /// "重试修复"（设置页按钮）的探测预算（毫秒）：这是用户主动操作，给足时间。
    /// 重试必须先删掉当前（可能是本软件伪造的）注册，否则"图标存在"无法区分真伪。
    /// </summary>
    private const int RetryProbeBudgetMs = 1000;

    /// <summary>后台兜底检查的节流间隔（毫秒）。</summary>
    private const int AutoRetryIntervalMs = 30_000;

    /// <summary>
    /// 本软件自身的可执行文件路径（规范化后比较）。exe 可能被改名，因此绝不按进程名判断"自己人"。
    /// 窗口隐藏功能给每个隐藏窗口都建了一个占位 NotifyIcon，它们会在
    /// HKCU\Control Panel\NotifyIconSettings 登记 ExecutablePath = 本软件 exe 的子键，
    /// 被托盘列表当成用户图标枚举出来 —— 必须按路径/进程号识别并剔除。
    /// </summary>
    private static readonly string SelfExecutablePath = NormalizePath(Environment.ProcessPath);

    /// <summary>
    /// 本软件可执行文件名（含扩展名）。
    /// 用户机器上常常留有本软件的多个副本（旧版本目录、桌面便携版、测试构建……），
    /// 它们各自在同一份 NotifyIconSettings 里留着"占位托盘图标"记录；这些条目对本软件
    /// 没有任何管理意义（占位图标无法恢复成原程序的图标），因此一并按文件名排除。
    /// </summary>
    private static readonly string SelfExecutableName =
        SelfExecutablePath.Length == 0 ? string.Empty : Path.GetFileName(SelfExecutablePath);

    private readonly AppConfig _config;
    private readonly TrayIconReader _reader = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _backgroundGuardTimer;
    private readonly Dictionary<string, TrayIconItem> _iconsBySubKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 被隐藏图标的恢复信息，键为注册表子键名。
    /// 直接复用 <see cref="AppConfig.HiddenBackups"/> 这个实例（而不是每次保存前复制整张表），
    /// 省掉一份重复的字典与全部条目引用。
    /// </summary>
    private readonly Dictionary<string, TrayIconEntry> _hiddenBackups;

    /// <summary>运行中进程索引（带 TTL），供刷新与后台守护共享，避免每次扫描全系统进程。</summary>
    private readonly RunningProcessIndex _processIndex = new();

    /// <summary>刷新用的进程名暂存集合（复用，避免每次刷新分配新的 HashSet）。</summary>
    private readonly HashSet<string> _processNameScratch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>刷新用的"存活子键"暂存集合（复用，避免每次刷新分配）。</summary>
    private readonly HashSet<string> _presentScratch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已知"不响应托盘重建"的进程名（与 <see cref="AppConfig.RebuildUnsupported"/> 同步的哈希镜像，
    /// 用于 O(1) 判断是否要在隐藏前弹确认）。恢复成功会自动移出。
    /// </summary>
    private readonly HashSet<string> _rebuildUnsupported = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _persistTimer;
    private bool _persistDirty;
    private bool _isScanning;
    private bool _isRestoring;
    private bool _isMainWindowVisible;
    private bool _sortDirty;
    private bool _hasInitialRefresh;
    private long _lastRefreshTicks;
    private long _lastExplorerProcessId;
    private long _lastAutoRetryTicks;
    private bool _disposed;
    private string _statusText = string.Empty;
    private int _selectedCount;

    /// <summary>
    /// 生成"上一句状态栏文案"的工厂。状态栏是**操作结果**（隐藏了几个、跳过几个…），
    /// 不能靠重读字段重算，所以在写入的同时把它记下来；语言切换时重新求值一次即可。
    /// </summary>
    private Func<string>? _statusFactory;

    public MainViewModel(AppConfig config)
    {
        _config = config;

        // 文案本地化：按配置里的语言偏好加载（幂等；App 之后再次 ApplyLanguage 不会重复触发）
        Loc.ApplyLanguage(_config.Language);
        Loc.LanguageChanged += OnLanguageChanged;
        SetStatus(() => Loc.T("Core.Status.Ready"));

        if (_config.HiddenBackups == null)
        {
            _config.HiddenBackups = new Dictionary<string, TrayIconEntry>(StringComparer.OrdinalIgnoreCase);
        }
        _hiddenBackups = _config.HiddenBackups;

        foreach (var name in _config.RebuildUnsupported)
        {
            var normalized = NormalizeProcessName(name);
            if (normalized.Length > 0) _rebuildUnsupported.Add(normalized);
        }

        // 旧版备份的图标字节可能是 DIB 或大尺寸 PNG：统一规范化成 ≤32×32 的 PNG。
        // 已经是目标格式时 NormalizeSnapshotToPng 会原样返回（同一个引用），
        // 因此普通启动不会每次都重写配置文件。
        var snapshotChanged = false;
        foreach (var backup in _hiddenBackups.Values)
        {
            if (backup.IconSnapshot is not { Length: > 0 } snapshot) continue;

            var normalized = IconExtractor.NormalizeSnapshotToPng(snapshot);
            if (normalized == null || ReferenceEquals(normalized, snapshot)) continue;

            backup.IconSnapshot = normalized;
            snapshotChanged = true;
        }

        // 历史配置里可能混进过"本软件自身"的隐藏备份（窗口隐藏功能登记的占位图标），开局就清掉
        var selfBackups = RemoveSelfBackups();
        if (selfBackups > 0) snapshotChanged = true;

        if (snapshotChanged)
        {
            Persist();
        }

        // 迁移旧版配置（旧版仅存子键名）→ 补全图标信息
        if (_config.HiddenKeys.Count > 0)
        {
            var all = _reader.ReadAll();
            foreach (var key in _config.HiddenKeys)
            {
                if (_hiddenBackups.ContainsKey(key)) continue;
                var entry = all.FirstOrDefault(e =>
                    string.Equals(e.SubKeyName, key, StringComparison.OrdinalIgnoreCase));
                if (entry != null) _hiddenBackups[key] = entry;
            }
            _config.HiddenKeys.Clear();
            Persist();
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(config.RefreshIntervalMs)
        };
        _refreshTimer.Tick += (_, _) => Refresh();

        _backgroundGuardTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(config.BackgroundGuardIntervalMs)
        };
        _backgroundGuardTimer.Tick += (_, _) => GuardHiddenIcons();

        // 构造期的两次 Persist 走"还没有计时器 → 立即落盘"的分支，所以这个计时器可以最后建
        _persistTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PersistDebounceMs)
        };
        _persistTimer.Tick += (_, _) => FlushPersist();
    }

    public ObservableCollection<TrayIconItem> Icons { get; } = new();

    // ---------------------------------------------------------------- 绑定属性

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

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

    public string SelectionSummary => _selectedCount == 0
        ? Loc.T("Core.Selection.None")
        : Loc.T("Core.Selection.Summary", _selectedCount);

    public int HiddenCount => Icons.Count(i => i.IsHidden);

    public bool HasIcons => Icons.Count > 0;

    public string EmptyHint => Loc.T("Core.Empty.TrayIcons");

    public bool HideSelfTrayIcon
    {
        get => _config.HideSelfTrayIcon;
        set
        {
            if (_config.HideSelfTrayIcon == value) return;
            _config.HideSelfTrayIcon = value;
            _config.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelfIconHint));
            SelfTrayIconVisibilityChanged?.Invoke(this, value);
        }
    }

    public string SelfIconHint => _config.HideSelfTrayIcon
        ? (_config.GetHotkey(HotkeyActions.ShowMain).IsValid
            ? Loc.T("Core.SelfIcon.HiddenWithHotkey")
            : Loc.T("Core.SelfIcon.HiddenNoHotkey"))
        : Loc.T("Core.SelfIcon.Visible");

    public bool RunAtStartup
    {
        get => _config.RunAtStartup;
        set
        {
            if (_config.RunAtStartup == value) return;

            if (StartupManager.SetEnabled(value))
            {
                _config.RunAtStartup = value;
                _config.Save();
                SetStatus(() => value ? Loc.T("Core.Startup.Enabled") : Loc.T("Core.Startup.Disabled"));
                OnPropertyChanged();
            }
            else
            {
                SetStatus(() => Loc.T("Core.Startup.Failed"));
                OnPropertyChanged();
            }
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _config.MinimizeToTrayOnClose;
        set
        {
            if (_config.MinimizeToTrayOnClose == value) return;
            _config.MinimizeToTrayOnClose = value;
            _config.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>"唤出主窗口"的全局快捷键文本（多动作快捷键表里的 ShowMain）。</summary>
    public string HotkeyText
    {
        get
        {
            var binding = _config.GetHotkey(HotkeyActions.ShowMain);
            return binding.IsValid ? binding.ToString() : Loc.T("Core.Hotkey.Unset");
        }
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    /// <summary>保存自定义快捷键后刷新界面显示。</summary>
    public void NotifyHotkeyChanged()
    {
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(SelfIconHint));
    }

    public void SetStatus(string status)
    {
        StatusText = status;
    }

    public event EventHandler<bool>? SelfTrayIconVisibilityChanged;

    // ---------------------------------------------------------------- 生命周期

    public void Start() => RunOnUiThread(StartCore);

    private void StartCore()
    {
        if (_disposed) return;

        if (_isMainWindowVisible)
        {
            _refreshTimer.Start();
            if (!_hasInitialRefresh) Refresh();
        }
        else
        {
            _backgroundGuardTimer.Start();
        }
    }

    public void Stop() => RunOnUiThread(StopCore);

    private void StopCore()
    {
        _refreshTimer.Stop();
        _backgroundGuardTimer.Stop();
        _persistTimer?.Stop();
    }

    /// <summary>主窗口显示/隐藏时切换完整扫描与低频隐藏守护。</summary>
    public void SetWindowVisible(bool visible) => RunOnUiThread(() => SetWindowVisibleCore(visible));

    private void SetWindowVisibleCore(bool visible)
    {
        if (_isMainWindowVisible == visible) return;
        _isMainWindowVisible = visible;

        if (visible)
        {
            _backgroundGuardTimer.Stop();
            _refreshTimer.Start();
            Refresh();
        }
        else
        {
            _refreshTimer.Stop();
            _backgroundGuardTimer.Start();
        }
    }

    // ---------------------------------------------------------------- 扫描

    /// <summary>
    /// 扫描托盘图标并与现有列表做增量对齐。
    /// 列表只包含"当前正在运行"的图标；已隐藏项靠 _hiddenBackups 标记。
    /// 刷新时对已隐藏项重新置位状态隐藏（幂等），应对程序周期性重新注册图标。
    /// </summary>
    public void Refresh() => RunOnUiThread(RefreshCore);

    private void RefreshCore()
    {
        // 重入保护：刷新/守护/恢复三者互斥，避免在恢复流程抽消息泵时被嵌套刷新打乱
        if (_isScanning || _isRestoring || _disposed) return;
        _isScanning = true;

        try
        {
            // 0) 兜底清理：历史配置里可能混进过"本软件自身"的隐藏备份
            if (RemoveSelfBackups() > 0) Persist();

            var entries = _reader.ReadAll();

            // 只查询"托盘条目真正需要"的进程名：旧实现遍历全系统进程并逐个读 MainModule，
            // 是最贵的一步（几百个进程 × 跨进程内存读取），现在既少查又多级缓存。
            // 额外带上 explorer：用它是否换过进程号来判断"explorer 是否重启过"（见第 5 步）。
            var names = _processNameScratch;
            names.Clear();
            foreach (var entry in entries) AddProcessName(names, null, entry.ExecutablePath);
            foreach (var backup in _hiddenBackups.Values) AddProcessName(names, null, backup.ExecutablePath);
            names.Add("explorer");
            _processIndex.EnsureFresh(names);

            _presentScratch.Clear();
            _sortDirty = false;

            // 1) 现存子键：仅保留"图标真在托盘"或"已隐藏"的条目。
            //   进程运行但图标不在托盘（探测不到窗口）的历史残留会被过滤掉。
            foreach (var entry in entries)
            {
                if (!_processIndex.TryFind(entry.ExecutablePath, out var procInfo))
                {
                    continue;
                }

                // 本软件自身不是用户可管理的托盘图标：
                // 窗口隐藏功能为每个隐藏窗口建的占位 NotifyIcon 也会登记这样的子键
                // （ExecutablePath = 本软件 exe、tooltip = 窗口标题），必须剔除，
                // 否则列表里会冒出"自己人"，而且隐藏它们会毁掉本软件的托盘表现。
                if (IsSelfEntry(entry.ExecutablePath, procInfo))
                {
                    continue;
                }

                var isHidden = _hiddenBackups.ContainsKey(entry.SubKeyName);

                if (UpsertEntry(entry, procInfo, isHidden))
                {
                    _presentScratch.Add(entry.SubKeyName);
                }
            }

            // 2) 兼容旧状态：子键被删但进程仍运行的已隐藏项
            foreach (var pair in _hiddenBackups)
            {
                if (_presentScratch.Contains(pair.Key)) continue;
                if (!_processIndex.TryFind(pair.Value.ExecutablePath, out var procInfo))
                {
                    continue;
                }

                _presentScratch.Add(pair.Key);
                UpsertBackup(pair.Key, pair.Value, procInfo);
            }

            // 3) 移除不再符合条件的条目
            for (var i = Icons.Count - 1; i >= 0; i--)
            {
                if (!_presentScratch.Contains(Icons[i].SubKeyName))
                {
                    _iconsBySubKey.Remove(Icons[i].SubKeyName);
                    Icons.RemoveAt(i);
                    _sortDirty = true;
                }
            }

            // 4) 监控：对已隐藏且运行中的图标重新置位状态隐藏，保持隐藏
            ReapplyHidden();

            if (_sortDirty) SortIcons();
            UpdateCounters();

            var hidden = 0;
            for (var i = 0; i < Icons.Count; i++)
            {
                if (Icons[i].IsHidden) hidden++;
            }

            var iconCount = Icons.Count;
            var status = Loc.T("Core.Status.ScanSummary", iconCount, hidden);
            if (!string.Equals(_statusText, status, StringComparison.Ordinal))
            {
                SetStatus(() => Loc.T("Core.Status.ScanSummary", iconCount, hidden));
            }

            // 5) explorer 换了进程号 = 它刚重启过 = 系统广播过 TaskbarCreated，
            //    各程序其实都已经用正确的回调重新注册了 → 清掉过期的降级告警。
            DetectExplorerRestart();

            // 6) 后台自动重试降级图标（主窗口可见时才有意义，内部自己节流）
            AutoRetryDegradedIcons();
        }
        catch (Exception ex)
        {
            var scanError = ex.Message;
            SetStatus(() => Loc.T("Core.Status.ScanFailed", scanError));
        }
        finally
        {
            _hasInitialRefresh = true;
            _lastRefreshTicks = Environment.TickCount64;
            _isScanning = false;
        }
    }

    /// <summary>
    /// 对"已隐藏且运行中"的图标重新隐藏（刷新与守护共用）。规则：
    ///   1. **只做非破坏性状态隐藏，绝不再 NIM_DELETE 目标程序的注册**；
    ///   2. 旧版"删除式隐藏"的备份如果发现程序已经重新注册了图标（升级后首次刷新、或 owner 重启过），
    ///      就把这条备份**升级成 State 模式**并重新置位 —— 而不是再删一次，
    ///      否则每次刷新都会把 owner 刚注册的图标删掉，图标永远回不来；
    ///   3. 缓存的窗口句柄必须仍属于目标进程（见 <see cref="IsItemWindowValid"/>），否则按进程名重新定位。
    /// </summary>
    private void ReapplyHidden()
    {
        foreach (var item in Icons)
        {
            if (!item.IsHidden || !item.IsRunning) continue;
            if (item.RestoreBlocked) continue;   // 用户请求过显示、正在等程序重启：不再盖回去
            ReapplyHiddenItem(item);
        }
    }

    /// <summary>
    /// 重新隐藏单个条目。返回是否已经处于（或成功进入）隐藏状态。
    /// 任何情况下都不会删除目标程序的注册 —— 状态隐藏不可用时宁可让它显示。
    /// </summary>
    private bool ReapplyHiddenItem(TrayIconItem item)
    {
        if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity)) return false;

        if (!IsItemWindowValid(item))
        {
            var relocated = ResolveIconWindow(item, identity);
            if (relocated == IntPtr.Zero) return false;
            item.Hwnd = relocated;
        }

        if (TrayIconController.TryHideByState(item.Hwnd, identity))
        {
            UpgradeHiddenBackupToState(item);
            return true;
        }

        if (item.HideMode == TrayHideMode.Delete)
        {
            // 旧备份 + 注册已经不在（删除式隐藏的常态）→ 无需动作
            if (!TrayIconController.IsIconRegistered(item.Hwnd, identity)) return true;
        }

        // 置位失败且可能换了窗口：重新定位后再试一次（仍不删除）
        var retried = ResolveIconWindow(item, identity);
        if (retried == IntPtr.Zero || retried == item.Hwnd) return false;

        item.Hwnd = retried;
        if (!TrayIconController.TryHideByState(retried, identity)) return false;

        UpgradeHiddenBackupToState(item);
        return true;
    }

    /// <summary>
    /// "用户请求显示、等程序重启"的隐藏记录能否解除。
    /// 判据必须是**目标图标真的重新注册了**：
    ///   * IsIconRegistered 用 uFlags=0 精确探测 (hwnd, uID/GUID)，只认这一条图标；
    ///   * State 方式还要求 TryShowByState 成功（顺手把可能残留的隐藏状态清掉）。
    /// 仅凭注册表里存在历史行是不算的 —— NIM_DELETE 之后那一行会一直留着。
    /// </summary>
    private static bool CanReleaseRestorePending(IntPtr hwnd, TrayIconIdentity identity, TrayHideMode mode)
    {
        if (!TrayIconController.IsIconRegistered(hwnd, identity)) return false;
        return mode != TrayHideMode.State || TrayIconController.TryShowByState(hwnd, identity);
    }

    /// <summary>
    /// 旧版删除式备份被"升级"为状态隐藏时同步记录：条目与持久化备份都改成 State，
    /// 这样重启本软件后也不会再用删除方式去处理它。
    /// </summary>
    private void UpgradeHiddenBackupToState(TrayIconItem item)
    {
        if (item.HideMode == TrayHideMode.State) return;

        item.HideMode = TrayHideMode.State;

        if (_hiddenBackups.TryGetValue(item.SubKeyName, out var backup) && backup.HideMode != TrayHideMode.State)
        {
            backup.HideMode = TrayHideMode.State;
            Persist();
        }
    }

    /// <summary>
    /// 低频守护已隐藏图标：复用隐藏时缓存的窗口句柄重新隐藏（按隐藏方式分别处理）。
    /// 顺带用（带 TTL 的）进程索引确认进程是否还活着 —— 进程都退出了，
    /// 就没必要再对着失效句柄反复发消息。
    /// </summary>
    private void GuardHiddenIcons()
    {
        if (_isScanning || _isRestoring || _isMainWindowVisible || _disposed) return;

        var names = _processNameScratch;
        names.Clear();
        foreach (var item in Icons)
        {
            if (!item.IsHidden) continue;
            AddProcessName(names, item.ProcessName, item.ExecutablePath);
        }
        if (names.Count == 0) return;

        _processIndex.EnsureFresh(names);

        foreach (var item in Icons)
        {
            if (!item.IsHidden) continue;
            if (item.RestoreBlocked) continue;   // 同上：不再和程序自己重新注册抢

            if (!_processIndex.TryFind(item.ExecutablePath, out _))
            {
                if (item.IsRunning)
                {
                    item.IsRunning = false;
                    item.OnSubTitleRefresh();
                }
                continue;
            }

            ReapplyHiddenItem(item);   // 只做非破坏性状态隐藏，绝不删除对方的注册
        }
    }

    /// <summary>
    /// 新增或更新一个"现存子键"对应的列表条目。
    /// 返回是否应显示：图标真在托盘（探测到窗口）或已隐藏时返回 true；
    /// 进程运行但图标不在托盘（探测不到窗口且未隐藏）时返回 false（多余条目，过滤）。
    /// </summary>
    private bool UpsertEntry(TrayIconEntry entry, ProcessInfo procInfo, bool isHidden)
    {
        // 自身条目兜底（正常路径 RefreshCore 已过滤）：本软件的占位图标不进列表
        if (IsSelfEntry(entry.ExecutablePath, procInfo)) return false;

        _iconsBySubKey.TryGetValue(entry.SubKeyName, out var existing);
        TrayIconController.TryCreateIdentity(entry.Uid, entry.IconGuid, out var identity);

        IntPtr hwnd;
        if (existing != null)
        {
            var procChanged = existing.ProcessId != procInfo.Id;
            if (existing.Hwnd != IntPtr.Zero && !procChanged)
            {
                hwnd = existing.Hwnd;   // 复用缓存
            }
            else
            {
                hwnd = identity.IsValid
                    ? TrayIconController.FindTrayWindow(procInfo.ProcessName, identity)
                    : IntPtr.Zero;
            }
        }
        else
        {
            hwnd = identity.IsValid
                ? TrayIconController.FindTrayWindow(procInfo.ProcessName, identity)
                : IntPtr.Zero;
        }

        // 用户点过"显示"、只是因为程序不响应托盘重建才在等它重启。
        // 注意：HKCU\Control Panel\NotifyIconSettings 里的行是**永存历史**（NIM_DELETE 之后照样在），
        // "有注册表行"根本不能证明 owner 已经重新注册 —— 必须真的找到该图标的注册窗口、
        // 核对注册确实存在（State 方式还要求状态能清除成功）才解除这条记录。
        if (isHidden &&
            _hiddenBackups.TryGetValue(entry.SubKeyName, out var pendingBackup) &&
            pendingBackup.RestorePending &&
            hwnd != IntPtr.Zero &&
            CanReleaseRestorePending(hwnd, identity, pendingBackup.HideMode))
        {
            _hiddenBackups.Remove(entry.SubKeyName);
            isHidden = false;
            Persist();   // 解除"等重启"的记录要落盘，否则下次启动又会盖回去
        }

        // 过滤：图标不在托盘（探测不到窗口）且未隐藏 → 多余条目，不显示
        if (hwnd == IntPtr.Zero && !isHidden)
        {
            return false;
        }

        // 备份里记录的隐藏方式 + "等程序重启"标记（新建条目也要带上，否则重启本软件后守护会盖回去）
        var backupMode = TrayHideMode.Delete;
        var pendingRestore = false;
        if (isHidden && _hiddenBackups.TryGetValue(entry.SubKeyName, out var entryBackup))
        {
            backupMode = entryBackup.HideMode;
            pendingRestore = entryBackup.RestorePending;
        }

        if (existing != null)
        {
            MarkSortDirtyIfNeeded(existing, entry, procInfo, isHidden);
            if (existing.ProcessId != procInfo.Id)
            {
                // 程序重启过：托盘图标是它自己重新注册的，降级标记自然失效
                existing.CallbackDegraded = false;
            }
            existing.Hwnd = hwnd;
            existing.IsHidden = isHidden;
            existing.IsRunning = true;
            existing.ProcessId = procInfo.Id;
            existing.ProcessName = procInfo.ProcessName;
            existing.DisplayName = entry.DisplayName;
            existing.Uid = entry.Uid;
            existing.IconGuid = entry.IconGuid;
            if (isHidden && _hiddenBackups.TryGetValue(entry.SubKeyName, out var hiddenBackup))
            {
                existing.HideMode = hiddenBackup.HideMode;
            }

            // 备份里记着"等程序重启"→ 条目也要同步，否则重启本软件后守护会把它盖回去
            existing.RestoreBlocked = isHidden &&
                _hiddenBackups.TryGetValue(entry.SubKeyName, out var pendingState) &&
                pendingState.RestorePending;
            existing.OnSubTitleRefresh();
            return true;
        }

        var item = new TrayIconItem
        {
            SubKeyName = entry.SubKeyName,
            ExecutablePath = entry.ExecutablePath,
            Uid = entry.Uid,
            IconGuid = entry.IconGuid,
            Publisher = entry.Publisher,
            DisplayName = entry.DisplayName,
            ProcessName = procInfo.ProcessName,
            IsRunning = true,
            ProcessId = procInfo.Id,
            Hwnd = hwnd,
            HideMode = backupMode,
            RestoreBlocked = pendingRestore,
            IsHidden = isHidden,
            IsSelected = false
        };

        item.Icon = IconExtractor.FromExecutable(entry.ExecutablePath)
                    ?? IconExtractor.FromSnapshot(entry.SubKeyName);

        item.PropertyChanged += OnItemPropertyChanged;
        Icons.Add(item);
        _iconsBySubKey[item.SubKeyName] = item;
        _sortDirty = true;
        return true;
    }

    /// <summary>新增或更新一个"已隐藏备份"对应的列表条目（子键可能已被删）。</summary>
    private void UpsertBackup(string subKeyName, TrayIconEntry backup, ProcessInfo procInfo)
    {
        // 自身备份兜底：绝不能把本软件自己的图标列成"已隐藏"（更不能对它们 NIM_DELETE）
        if (IsSelfEntry(backup.ExecutablePath, procInfo)) return;

        _iconsBySubKey.TryGetValue(subKeyName, out var existing);
        TrayIconController.TryCreateIdentity(backup.Uid, backup.IconGuid, out var identity);

        if (existing != null)
        {
            var procChanged = existing.ProcessId != procInfo.Id;
            if ((existing.Hwnd == IntPtr.Zero || procChanged) && identity.IsValid)
            {
                existing.Hwnd = TrayIconController.FindTrayWindow(procInfo.ProcessName, identity);
            }
            if (procChanged)
            {
                existing.CallbackDegraded = false;
            }
            MarkSortDirtyIfNeeded(existing, backup, procInfo, isHidden: true);
            existing.IsHidden = true;
            existing.IsRunning = true;
            existing.ProcessId = procInfo.Id;
            existing.ProcessName = procInfo.ProcessName;
            existing.DisplayName = backup.DisplayName;
            existing.Publisher = backup.Publisher;
            existing.Uid = backup.Uid;
            existing.IconGuid = backup.IconGuid;
            existing.WindowClassName = backup.WindowClassName;
            existing.HideMode = backup.HideMode;
            existing.RestoreBlocked = backup.RestorePending;   // 重启本软件后仍记得"别再盖回去"
            existing.OnSubTitleRefresh();
            return;
        }

        var hwnd = identity.IsValid
            ? TrayIconController.FindTrayWindow(procInfo.ProcessName, identity)
            : IntPtr.Zero;

        var item = new TrayIconItem
        {
            SubKeyName = subKeyName,
            ExecutablePath = backup.ExecutablePath,
            Uid = backup.Uid,
            IconGuid = backup.IconGuid,
            Publisher = backup.Publisher,
            DisplayName = backup.DisplayName,
            ProcessName = procInfo.ProcessName,
            IsRunning = true,
            ProcessId = procInfo.Id,
            Hwnd = hwnd,
            WindowClassName = backup.WindowClassName,
            HideMode = backup.HideMode,
            RestoreBlocked = backup.RestorePending,
            IsHidden = true,
            IsSelected = false
        };

        item.Icon = IconExtractor.FromExecutable(backup.ExecutablePath)
                    ?? IconExtractor.FromSnapshot(subKeyName);
        if (item.Icon == null && backup.IconSnapshot is { Length: > 0 })
        {
            item.Icon = IconExtractor.FromSnapshotBytes(backup.IconSnapshot);
        }

        item.PropertyChanged += OnItemPropertyChanged;
        Icons.Add(item);
        _iconsBySubKey[subKeyName] = item;
        _sortDirty = true;
    }

    private void MarkSortDirtyIfNeeded(
        TrayIconItem existing,
        TrayIconEntry entry,
        ProcessInfo procInfo,
        bool isHidden)
    {
        if (existing.DisplayName != entry.DisplayName ||
            existing.ProcessName != procInfo.ProcessName ||
            existing.IsHidden != isHidden)
        {
            _sortDirty = true;
        }
    }

    private void SortIcons()
    {
        _sortDirty = false;
        var ordered = Icons
            .OrderByDescending(i => i.IsRunning)
            .ThenByDescending(i => i.IsHidden)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Icons.IndexOf(ordered[target]);
            if (current != target && current >= 0)
            {
                Icons.Move(current, target);
            }
        }
    }

    // ---------------------------------------------------------------- 隐藏

    /// <summary>对当前勾选项执行隐藏。操作完成后清空勾选。</summary>
    public void ApplySelected() => RunOnUiThread(ApplySelectedCore);

    private void ApplySelectedCore()
    {
        if (GuardRestoreInProgress()) return;

        var targets = Icons.Where(i => i.IsSelected && !i.IsHidden).ToList();
        if (targets.Count == 0)
        {
            SetStatus(() => Loc.T("Core.Hide.SelectFirst"));
            return;
        }

        var success = 0;
        var skipped = 0;
        var failed = 0;
        List<TrayIconItem>? skippedItems = null;

        foreach (var item in targets)
        {
            switch (HideItem(item))
            {
                case HideResult.Hidden:
                    success++;
                    break;
                case HideResult.Failed:
                    failed++;
                    break;
                case HideResult.Skipped:
                    skipped++;
                    (skippedItems ??= new List<TrayIconItem>()).Add(item);
                    break;
            }
        }

        // 被跳过的条目保留勾选，用户改主意后不用重新勾
        foreach (var item in targets)
        {
            if (skippedItems == null || !skippedItems.Contains(item)) item.IsSelected = false;
        }

        Persist();
        UpdateCounters();
        ApplyPendingSort();
        SetStatus(() => Loc.T("Core.Status.HideSelected", success) + BuildSkipSuffix(skipped) + BuildFailSuffix(failed));
    }

    /// <summary>隐藏当前列表中的全部图标。</summary>
    public void HideAll() => RunOnUiThread(HideAllCore);

    private void HideAllCore()
    {
        if (GuardRestoreInProgress()) return;

        var success = 0;
        var skipped = 0;
        var failed = 0;
        var total = 0;

        foreach (var item in Icons)
        {
            if (item.IsHidden) continue;
            total++;

            switch (HideItem(item))
            {
                case HideResult.Hidden:
                    success++;
                    break;
                case HideResult.Failed:
                    failed++;
                    break;
                case HideResult.Skipped:
                    skipped++;
                    break;
            }
        }

        Persist();
        UpdateCounters();
        ApplyPendingSort();
        SetStatus(() => total == 0
            ? Loc.T("Core.Status.AllAlreadyHidden")
            : success == 0 && skipped > 0
                ? Loc.T("Core.Status.AllSkipped", skipped)
                : Loc.T("Core.Status.AllHidden", success) + BuildSkipSuffix(skipped) + BuildFailSuffix(failed));
    }

    /// <summary>隐藏单个图标的结果。</summary>
    private enum HideResult
    {
        /// <summary>已隐藏（含"本来就已经处于隐藏状态"）。</summary>
        Hidden,

        /// <summary>失败：定位不到图标，或 Shell 既拒绝了状态隐藏也拒绝了删除。</summary>
        Failed,

        /// <summary>被跳过：本软件自身，或该程序已知不支持托盘重建且用户选择不隐藏。</summary>
        Skipped
    }

    /// <summary>隐藏单个图标：非破坏性状态隐藏（NIF_STATE/NIS_HIDDEN，图标立即从托盘与溢出面板消失）。</summary>
    /// <param name="item">目标条目。</param>
    private HideResult HideItem(TrayIconItem item)
    {
        if (item.IsHidden) return HideResult.Hidden;

        // 兜底守卫：绝不能隐藏本软件自身（会把本软件的托盘图标/窗口隐藏占位图标删掉）
        if (IsSelfItem(item))
        {
            SetStatus(() => Loc.T("Core.Hide.SelfBlocked"));
            return HideResult.Skipped;
        }

        if (item.Hwnd == IntPtr.Zero ||
            !TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity))
        {
            SetStatus(() => Loc.T("Core.Hide.LocateFailed", item.DisplayName));
            return HideResult.Failed;
        }

        // v1.2.2：隐藏**只走非破坏性状态隐藏**（NIF_STATE/NIS_HIDDEN）。
        // 绝不 NIM_DELETE 目标程序的原生注册 —— 删了就只剩"伪造重建"，图标会变成点不动的死图标
        // （issue#1/#3 的根源）。Shell 不支持时宁可明确告诉用户"没隐藏成功"，也不动人家的注册。
        if (!TryHideWithoutDeleting(item.Hwnd, identity))
        {
            SetStatus(() => Loc.T("Core.Hide.StateUnsupported", item.DisplayName));
            return HideResult.Failed;
        }

        var hideMode = TrayHideMode.State;

        // 记录恢复信息（含窗口类名，供删除模式下按类名重新定位窗口）。
        // 备份里的图标字节统一规范化成 ≤32×32 PNG：config.json 的 base64 随之变小。
        var windowClassName = TrayIconController.GetWindowClassName(item.Hwnd);
        var registrySnapshot = TrayIconReader.ReadIconSnapshot(item.SubKeyName);
        var normalized = registrySnapshot is { Length: > 0 }
            ? IconExtractor.NormalizeSnapshotToPng(registrySnapshot)
            : item.IconSnapshot is { Length: > 0 } legacySnapshot
                ? IconExtractor.NormalizeSnapshotToPng(legacySnapshot)
                : null;

        item.IconSnapshot = normalized ?? registrySnapshot;
        _hiddenBackups[item.SubKeyName] = new TrayIconEntry
        {
            SubKeyName = item.SubKeyName,
            ExecutablePath = item.ExecutablePath,
            Uid = item.Uid,
            IconGuid = item.IconGuid,
            IconSnapshot = registrySnapshot == null ? item.IconSnapshot : null,
            Publisher = item.Publisher,
            ToolTip = item.DisplayName,
            WindowClassName = windowClassName,
            HideMode = hideMode
        };

        item.WindowClassName = windowClassName;
        item.HideMode = hideMode;
        item.CallbackDegraded = false;   // 隐藏后不存在"右键菜单失效"的问题
        item.IsHidden = true;
        _sortDirty = true;
        return HideResult.Hidden;
    }

    /// <summary>
    /// 隐藏指定进程（含同名全部实例）当前可见的托盘图标，返回成功隐藏的数量。
    ///
    /// 供"把普通窗口隐藏到托盘"等外部功能复用：隐藏窗口时通常希望把它的托盘图标一起收起。
    /// 入参可以是 "chrome"、"chrome.exe" 或完整路径；内部不抛异常，列表过期时会先刷新一次。
    /// </summary>
    public int HideTrayIconsForProcess(string processName)
        => RunOnUiThread(() => HideTrayIconsForProcessCore(processName));

    private int HideTrayIconsForProcessCore(string processName)
    {
        try
        {
            var name = NormalizeProcessName(processName);
            if (string.IsNullOrEmpty(name)) return 0;

            var targets = CollectIconsOfProcess(Icons, name, hidden: false);
            if (targets.Count == 0 && IsListStale())
            {
                // 列表可能过期（主窗口隐藏时只跑低频守护）→ 全量扫描一次再找
                RefreshCore();
                targets = CollectIconsOfProcess(Icons, name, hidden: false);
            }

            if (targets.Count == 0)
            {
                SetStatus(() => Loc.T("Core.Hide.NoIconsForProcess", name));
                return 0;
            }

            var success = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var item in targets)
            {
                switch (HideItem(item))
                {
                    case HideResult.Hidden:
                        success++;
                        break;
                    case HideResult.Failed:
                        failed++;
                        break;
                    case HideResult.Skipped:
                        skipped++;
                        break;
                }
            }

            Persist();
            UpdateCounters();
            ApplyPendingSort();
            SetStatus(() => success > 0
                ? Loc.T("Core.Status.HideProcess", name, success) + BuildSkipSuffix(skipped) + BuildFailSuffix(failed)
                : skipped > 0
                    ? Loc.T("Core.Hide.ProcessSkipped", name)
                    : failed > 0
                        ? Loc.T("Core.Hide.StateUnsupported", name)
                        : Loc.T("Core.Hide.ProcessFailed", name));
            return success;
        }
        catch (Exception ex)
        {
            var hideError = ex.Message;
            SetStatus(() => Loc.T("Core.Hide.ProcessError", processName, hideError));
            return 0;
        }
    }

    // ---------------------------------------------------------------- 恢复

    /// <summary>恢复所有被隐藏的图标。</summary>
    public void RestoreAll() => RunOnUiThread(RestoreAllCore);

    private void RestoreAllCore()
    {
        if (!TryEnterRestore()) return;

        try
        {
            var hidden = CollectHiddenIcons();
            var outcome = hidden.Count == 0
                ? new RestoreOutcome(0, 0, 0, Array.Empty<string>())
                : RestoreItems(hidden);

            // 已退出进程的隐藏记录没有图标可恢复，但它会在该程序下次启动时被守护逻辑
            // 重新藏起来 —— 用户点的既然是"全部显示"，这些残留记录也一并清掉。
            // 注意：列表为空时也要清，否则残留记录永远清不掉（用户再也看不到、也点不到它）。
            var orphans = RemoveOrphanBackups();

            Persist();
            UpdateCounters();
            ApplyPendingSort();
            SetStatus(() => hidden.Count == 0 && orphans > 0
                ? Loc.T("Core.Status.CleanedOrphans", orphans)
                : BuildRestoreStatusText(outcome));
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>清理没有对应列表条目（进程已退出）的隐藏备份，返回清理条数。</summary>
    private int RemoveOrphanBackups()
    {
        if (_hiddenBackups.Count == 0) return 0;

        List<string>? stale = null;
        foreach (var subKey in _hiddenBackups.Keys)
        {
            if (_iconsBySubKey.ContainsKey(subKey)) continue;
            (stale ??= new List<string>()).Add(subKey);
        }

        if (stale == null) return 0;
        foreach (var subKey in stale) _hiddenBackups.Remove(subKey);
        return stale.Count;
    }

    /// <summary>恢复（显示）当前勾选的已隐藏图标。操作完成后清空勾选。</summary>
    public void RestoreSelected() => RunOnUiThread(RestoreSelectedCore);

    private void RestoreSelectedCore()
    {
        var targets = Icons.Where(i => i.IsSelected && i.IsHidden).ToList();
        if (targets.Count == 0)
        {
            SetStatus(() => Loc.T("Core.Restore.SelectFirst"));
            return;
        }

        if (!TryEnterRestore()) return;

        try
        {
            var outcome = RestoreItems(targets);
            foreach (var item in targets)
            {
                item.IsSelected = false;
            }

            Persist();
            UpdateCounters();
            ApplyPendingSort();
            StatusText = BuildRestoreStatusText(outcome);
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>
    /// 恢复指定进程被隐藏的托盘图标，返回成功恢复的数量。
    /// 入参可以是 "chrome"、"chrome.exe" 或完整路径；内部不抛异常。
    /// </summary>
    public int RestoreTrayIconsForProcess(string processName)
        => RunOnUiThread(() => RestoreTrayIconsForProcessCore(processName));

    private int RestoreTrayIconsForProcessCore(string processName)
    {
        try
        {
            var name = NormalizeProcessName(processName);
            if (string.IsNullOrEmpty(name)) return 0;

            var targets = CollectIconsOfProcess(Icons, name, hidden: true);
            if (targets.Count == 0 && IsListStale())
            {
                RefreshCore();
                targets = CollectIconsOfProcess(Icons, name, hidden: true);
            }

            if (targets.Count == 0)
            {
                SetStatus(() => Loc.T("Core.Restore.NoIconsForProcess", name));
                return 0;
            }

            if (!TryEnterRestore()) return 0;

            try
            {
                var outcome = RestoreItems(targets);
                Persist();
                UpdateCounters();
                ApplyPendingSort();
                StatusText = BuildRestoreStatusText(outcome);
                return outcome.Restored;
            }
            finally
            {
                _isRestoring = false;
            }
        }
        catch (Exception ex)
        {
            var restoreError = ex.Message;
            SetStatus(() => Loc.T("Core.Restore.ProcessError", processName, restoreError));
            return 0;
        }
    }

    /// <summary>
    /// 取消某个图标的隐藏意图：删掉隐藏记录、不再守护它。
    ///
    /// 主要给"恢复失败（程序不响应托盘重建）"的条目用 —— 此时图标已经不在，
    /// 我们也不会伪造，删掉记录后该程序**下次启动时会自己注册**，图标就正常回来了，
    /// 而且守护逻辑不会再去抢它。返回是否真的取消了一条记录。
    /// </summary>
    public bool CancelHide(TrayIconItem item) => RunOnUiThread(() => CancelHideCore(item));

    private bool CancelHideCore(TrayIconItem item)
    {
        var removed = _hiddenBackups.Remove(item.SubKeyName);

        item.RestoreBlocked = false;
        item.CallbackDegraded = false;
        item.IconSnapshot = null;
        item.IsHidden = false;
        item.HideMode = TrayHideMode.Delete;
        _sortDirty = true;

        Persist();
        UpdateCounters();
        ApplyPendingSort();
        SetStatus(() => removed
            ? Loc.T("Core.CancelHide.Done", item.DisplayName)
            : Loc.T("Core.CancelHide.NoRecord", item.DisplayName));
        return removed;
    }

    /// <summary>切换单个图标的隐藏状态（双击）。</summary>
    public void ToggleItem(TrayIconItem item) => RunOnUiThread(() => ToggleItemCore(item));

    private void ToggleItemCore(TrayIconItem item)
    {
        if (item.IsHidden)
        {
            if (!TryEnterRestore()) return;

            try
            {
                var outcome = RestoreItems(new[] { item });
                Persist();
                UpdateCounters();
                ApplyPendingSort();
                StatusText = BuildRestoreStatusText(outcome);
            }
            finally
            {
                _isRestoring = false;
            }
            return;
        }

        if (GuardRestoreInProgress()) return;

        var result = HideItem(item);
        if (result == HideResult.Skipped)
        {
            // 双击操作原本不写状态栏，这里给一个明确反馈（自身的提示已由 HideItem 写好）
            if (!IsSelfItem(item))
            {
                SetStatus(() => Loc.T("Core.Hide.SkippedUnsupported", item.DisplayName));
            }
            return;
        }

        Persist();
        UpdateCounters();
        ApplyPendingSort();
    }

    /// <summary>
    /// 批量恢复被隐藏的图标 —— 本文件的核心逻辑。按隐藏方式分两条路：
    ///
    ///   A. **非破坏性隐藏（TrayHideMode.State，v1.2.2 起的默认方式）**：
    ///      只需清除 NIS_HIDDEN 状态位，Shell 里的注册与目标程序自己的回调从头到尾没被动过，
    ///      因此恢复后右键菜单/双击必然正常 —— 这是 issue#3（火绒这类程序恢复后点不动）的根治办法。
    ///      若清除状态失败（注册已不在，例如程序重启过），退回 B 路径。
    ///
    ///   B. **破坏性隐藏（TrayHideMode.Delete，旧版本留下的记录）**：
    ///      1) 先探测图标是否已在托盘里（程序可能自己重新注册过），在就算完成；
    ///      2) 仍缺失的，按进程去重后向该程序全部实例进程 PostMessage(TaskbarCreated)，
    ///         让它用**自己的** hWnd/uID/uCallbackMessage 重新注册（实测中位 11ms 回归）；
    ///      3) 15ms/步、总预算 RestoreProbeBudgetMs 轮询探测回归；
    ///      4) 仍未回归 = 该程序不响应 TaskbarCreated → **不再伪造图标**（伪造出来的图标
    ///         看得见点不动，正是 issue#1/#3 的根源），保持隐藏并在状态栏说明"请重启该程序"。
    ///
    ///   收尾（两条路都要做）：收到 TaskbarCreated 的程序会把它的**全部**图标都加回来，
    ///   其中仍应保持隐藏的必须重新隐藏一次（ReapplyHidden），否则"恢复一个 = 放出全部"。
    ///
    /// 整个过程同步执行、总阻塞不超过 RestoreProbeBudgetMs，等待期间会抽取消息泵，
    /// 因此界面不会假死（重入由 _isRestoring 拦住）。
    /// </summary>
    private RestoreOutcome RestoreItems(IEnumerable<TrayIconItem> source)
    {
        var plans = new List<RestorePlan>();
        var failed = 0;
        var cleaned = 0;

        foreach (var item in source)
        {
            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity))
            {
                failed++;
                continue;
            }

            var hwnd = ResolveIconWindow(item, identity);

            // 进程已退出 / 窗口都找不到：注册早就随进程消失了，谈不上"恢复"。
            // 直接清掉隐藏记录（程序下次启动会自己注册，图标自然就回来了），不报失败。
            if (hwnd == IntPtr.Zero && !item.IsRunning)
            {
                CompleteRestore(item, IntPtr.Zero);
                cleaned++;
                continue;
            }

            plans.Add(new RestorePlan(item, identity, hwnd));
        }

        if (plans.Count == 0)
        {
            return new RestoreOutcome(0, failed, cleaned, Array.Empty<string>());
        }

        // 0) 非破坏性隐藏（State）：**必须真正清除状态位**才算恢复。
        //    这里绝不能拿"Shell 里还存在注册"当成功 —— 那正是被隐藏的状态位，
        //    会导致误报已恢复、并把隐藏记录删掉（图标其实还藏着）。
        var rebuildables = new List<RestorePlan>();

        foreach (var plan in plans)
        {
            plan.IsStateMode = plan.Item.HideMode == TrayHideMode.State;

            if (!plan.IsStateMode)
            {
                rebuildables.Add(plan);   // 旧版删除式记录：走"让程序自己重新注册"
                continue;
            }

            if (TrayIconController.TryShowByState(plan.Hwnd, plan.Identity))
            {
                plan.Registered = true;   // 清除成功 = 真的恢复了，回调原封不动
                continue;
            }

            if (TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity))
            {
                // 注册还在但状态清不掉：语义不明，按失败处理（保留隐藏记录），绝不谎报恢复
                plan.Ambiguous = true;
                continue;
            }

            // 注册确实没了（程序重启过等）→ 允许让程序自己重新注册
            rebuildables.Add(plan);
        }

        // 1) 需要重建的条目先探测：已经在托盘里的直接算完成（不要重复注册）
        foreach (var plan in rebuildables)
        {
            if (plan.Registered) continue;

            if (TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity))
            {
                plan.Registered = true;
                plan.OwnerRebuilt = true;
            }
        }

        // 2) 仍缺失的按进程去重投递 TaskbarCreated（同一进程只投一次）
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var posted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in rebuildables)
        {
            if (plan.Registered) continue;

            var processName = plan.Item.ProcessName;
            if (string.IsNullOrEmpty(processName)) continue;

            if (tried.Add(processName) &&
                TrayIconController.RequestTrayRebuild(processName, plan.Hwnd) > 0)
            {
                posted.Add(processName);
            }

            plan.Waiting = posted.Contains(processName);
        }

        // 3) 短轮询探测回归（总预算 RestoreProbeBudgetMs，等待时抽消息泵）
        if (posted.Count > 0)
        {
            var deadline = Environment.TickCount64 + RestoreProbeBudgetMs;

            while (true)
            {
                var stillWaiting = false;

                foreach (var plan in rebuildables)
                {
                    if (plan.Registered || !plan.Waiting) continue;

                    if (TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity))
                    {
                        plan.Registered = true;
                        plan.OwnerRebuilt = true;   // 由程序自己重新注册回来的，才算证明它支持重建
                        continue;
                    }

                    stillWaiting = true;
                }

                if (!stillWaiting) break;
                if (_disposed || Environment.TickCount64 >= deadline) break;
                if (!PumpWait(RestoreProbeIntervalMs)) break;
            }
        }

        // 3.5) 预算用尽：最后完整搜一次（个别程序会换一个新窗口来注册）
        foreach (var plan in rebuildables)
        {
            if (plan.Registered || !plan.Waiting) continue;

            var hwnd = TrayIconController.FindTrayWindow(plan.Item.ProcessName, plan.Identity);
            if (hwnd == IntPtr.Zero) continue;

            plan.Hwnd = hwnd;
            plan.Registered = true;
            plan.OwnerRebuilt = true;
        }

        // 4) 收尾：成功项清理隐藏记录；失败项**保持隐藏**并记入学习集合
        var restored = 0;
        var unsupportedNames = new List<string>(2);

        foreach (var plan in plans)
        {
            if (plan.Registered)
            {
                CompleteRestore(plan.Item, plan.Hwnd);

                // 只有"程序自己重新注册回来"才算证明它支持重建；状态位清除不能当证据
                if (plan.OwnerRebuilt) ClearRebuildUnsupported(ProcessNameOf(plan.Item));

                restored++;
                continue;
            }

            // 恢复不了：绝不伪造图标。保留备份但记下"用户请求过显示"（持久化），
            // 守护逻辑从此不再盖回它 —— 重启本软件后同样有效，
            // 程序自己重新注册图标时再自动解除这条记录（不会和 owner 抢）。
            failed++;
            plan.Item.CallbackDegraded = false;
            plan.Item.RestoreBlocked = true;

            if (_hiddenBackups.TryGetValue(plan.Item.SubKeyName, out var pending))
            {
                pending.RestorePending = true;
            }

            // 只有"删除式"条目才谈得上"不支持托盘重建"（状态位清除失败不算这个结论）
            if (!plan.IsStateMode) MarkRebuildUnsupported(ProcessNameOf(plan.Item));

            var name = ProcessNameOf(plan.Item);
            if (name.Length > 0 && !unsupportedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                unsupportedNames.Add(name);
            }
        }

        // 5) 副作用回收：目标程序把它的全部图标都加回来了，仍应隐藏的要再隐藏一次
        if (posted.Count > 0)
        {
            ReapplyHidden();
        }

        return new RestoreOutcome(restored, failed, cleaned, unsupportedNames.ToArray());
    }

    /// <summary>
    /// 缓存的窗口句柄是否还能安全操作：窗口存在**且**仍属于该条目记录的那个进程。
    ///
    /// 只判断 IsWindow 不够 —— 进程退出后窗口句柄会被系统回收再利用，
    /// 拿旧句柄 + 相同 uID 去 NIM_MODIFY 可能操作到**别的程序**的图标。
    /// 因此还要核对进程号，并用进程名兜住"进程号被复用"的情况（读不到进程名就当作不可信，
    /// 交给按进程名重新定位）。
    /// </summary>
    private static bool IsItemWindowValid(TrayIconItem item)
    {
        if (!TrayIconController.IsWindowAlive(item.Hwnd)) return false;

        var pid = TrayIconController.GetWindowProcessId(item.Hwnd);
        if (pid == 0) return false;
        if (item.ProcessId != 0 && pid != item.ProcessId) return false;

        var name = TrayIconController.GetProcessName(pid);
        if (name.Length == 0) return false;

        return item.ProcessName.Length == 0 ||
               string.Equals(name, item.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 定位注册该图标所用的窗口：先复用缓存句柄，失效后依次尝试
    ///   1. NIM_MODIFY 探测（图标还在托盘里时最准）；
    ///   2. 隐藏前记录的窗口类名；
    ///   3. 同进程内的托盘特征窗口（应用重启后 ATL 动态类名会变，只能退到这一步）。
    /// </summary>
    private static IntPtr ResolveIconWindow(TrayIconItem item, TrayIconIdentity identity)
    {
        if (IsItemWindowValid(item)) return item.Hwnd;

        var processName = item.ProcessName;
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;

        var hwnd = identity.IsValid
            ? TrayIconController.FindTrayWindow(processName, identity)
            : IntPtr.Zero;

        if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(item.WindowClassName))
        {
            hwnd = TrayIconController.FindWindowByClass(processName, item.WindowClassName);
        }

        if (hwnd == IntPtr.Zero)
        {
            hwnd = TrayIconController.FindMessageWindowInProcess(processName);
        }

        return hwnd;
    }

    /// <summary>恢复成功后同步内存状态：清掉隐藏备份，标记图标重新显示。</summary>
    private void CompleteRestore(TrayIconItem item, IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) item.Hwnd = hwnd;

        item.IsHidden = false;
        item.CallbackDegraded = false;
        item.RestoreBlocked = false;
        item.IconSnapshot = null;
        _hiddenBackups.Remove(item.SubKeyName);
        _sortDirty = true;
    }

    /// <summary>拼装恢复结果提示。失败项必须写清楚"为什么没恢复"，否则用户会以为功能坏了。</summary>
    private static string BuildRestoreStatusText(RestoreOutcome outcome)
    {
        var separator = Loc.T("Core.Common.ListSeparator");

        if (outcome.Restored == 0 && outcome.Failed == 0)
        {
            return outcome.Cleaned > 0
                ? Loc.T("Core.Status.CleanedOrphans", outcome.Cleaned)
                : Loc.T("Core.Restore.NothingHidden");
        }

        if (outcome.Restored == 0)
        {
            var names = outcome.UnsupportedNames.Length > 0
                ? string.Join(separator, outcome.UnsupportedNames) + " "
                : string.Empty;
            return Loc.T("Core.Restore.FailedAll", names);
        }

        var text = Loc.T("Core.Restore.Restored", outcome.Restored);

        if (outcome.Cleaned > 0) text += Loc.T("Core.Restore.RestoredWithCleaned", outcome.Cleaned);

        if (outcome.Failed > 0)
        {
            var names = outcome.UnsupportedNames.Length > 0
                ? string.Join(separator, outcome.UnsupportedNames)
                : Loc.T("Core.Restore.FailedCount", outcome.Failed);
            text += Loc.T("Core.Restore.PartialUnsupported", names);
        }

        return text;
    }

    /// <summary>
    /// 恢复进行中时拦住"隐藏"类操作：恢复流程会抽消息泵，期间用户仍能点按钮，
    /// 若此时对同一个条目执行 NIM_DELETE，会与恢复流程的状态收尾互相覆盖。
    /// </summary>
    private bool GuardRestoreInProgress()
    {
        if (!_isRestoring) return false;
        SetStatus(() => Loc.T("Core.Restore.Busy"));
        return true;
    }

    /// <summary>恢复流程的重入保护：过程中会抽取消息泵，期间可能再次触发按钮点击。</summary>
    private bool TryEnterRestore()
    {
        if (_disposed) return false;

        if (_isRestoring)
        {
            SetStatus(() => Loc.T("Core.Restore.Busy"));
            return false;
        }

        _isRestoring = true;
        return true;
    }

    /// <summary>
    /// 在 UI 线程上等待一小段时间，同时继续抽取消息泵 —— 恢复流程要等目标程序响应，
    /// 但不能把界面冻住（Thread.Sleep 会阻塞整个窗口）。
    /// 返回 false 表示程序正在退出，调用方应尽快结束流程。
    /// </summary>
    private static bool PumpWait(int milliseconds)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };

        void OnTick(object? sender, EventArgs e)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            frame.Continue = false;
        }

        timer.Tick += OnTick;
        timer.Start();

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
            timer.Tick -= OnTick;
        }

        return !dispatcher.HasShutdownStarted;
    }

    // ---------------------------------------------------------------- 重试（历史遗留）

    /// <summary>
    /// 重试"还没恢复成功"的图标（设置页按钮）。v1.2.2 起行为是**纯恢复、零删除**：
    ///   * 状态隐藏的条目 → 再次尝试清除 NIS_HIDDEN；
    ///   * 旧版删除式、且用户点过"显示"的条目（RestoreBlocked / 备份里的 RestorePending）
    ///     → 再投递一次 TaskbarCreated，让程序自己重新注册；
    ///   * 全程 **不 NIM_DELETE、不伪造图标**：原程序没响应就保持原样，等它自己重启。
    /// </summary>
    /// <returns>恢复成功的数量。</returns>
    public int RetryDegradedIcons() => RunOnUiThread(RetryDegradedIconsCore);

    private int RetryDegradedIconsCore()
    {
        if (!TryEnterRestore()) return 0;

        try
        {
            var targets = new List<TrayIconItem>();
            foreach (var item in Icons)
            {
                if (!item.IsRunning) continue;
                if (!item.RestoreBlocked && !item.CallbackDegraded) continue;
                targets.Add(item);
            }

            if (targets.Count == 0)
            {
                SetStatus(() => Loc.T("Core.Retry.None"));
                return 0;
            }

            // 复用正式的恢复流程：状态模式清除状态位、旧记录让程序自己重新注册；不会删除任何注册
            var outcome = RestoreItems(targets);

            Persist();
            UpdateCounters();
            ApplyPendingSort();

            SetStatus(() => outcome.Restored > 0
                ? Loc.T("Core.Retry.Recovered", outcome.Restored)
                : Loc.T("Core.Retry.Failed"));

            return outcome.Restored;
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>
    /// 后台兜底（跑在 Refresh 里，主窗口可见时才有意义），**只做无破坏的清理**：
    /// 某个"历史降级"条目的图标在 Shell 里已经没有任何注册（程序重启过、或图标被系统收回）时，
    /// 把过期的降级标记清掉。绝不在后台删注册、也不伪造图标 —— 那会让用户图标闪一下甚至消失。
    /// 节流 <see cref="AutoRetryIntervalMs"/>，因此开销可以忽略。
    /// </summary>
    private void AutoRetryDegradedIcons()
    {
        if (!_isMainWindowVisible || _isRestoring) return;
        if (Environment.TickCount64 - _lastAutoRetryTicks < AutoRetryIntervalMs) return;

        var changed = false;

        foreach (var item in Icons)
        {
            if (!item.CallbackDegraded) continue;
            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity)) continue;
            if (TrayIconController.IsIconRegistered(item.Hwnd, identity)) continue;   // 还有注册：不动它

            item.CallbackDegraded = false;
            changed = true;
        }

        _lastAutoRetryTicks = Environment.TickCount64;
        if (changed) ApplyPendingSort();
    }

    /// <summary>
    /// explorer 换了进程号 = 它刚重启过 = 系统向所有进程广播过 TaskbarCreated，
    /// 各程序其实都已用**自己**的回调重新注册完毕 → 清掉所有过期的降级告警。
    /// （这一步是 B4"explorer 重启后能自动恢复"的可靠信号，不需要靠删注册去试探。）
    /// </summary>
    private void DetectExplorerRestart()
    {
        if (!_processIndex.TryGetProcess("explorer", out var explorer)) return;

        var previous = _lastExplorerProcessId;
        _lastExplorerProcessId = explorer.Id;
        if (previous == 0 || previous == explorer.Id) return;

        var cleared = 0;
        foreach (var item in Icons)
        {
            if (!item.CallbackDegraded) continue;
            item.CallbackDegraded = false;
            cleared++;
        }

        if (cleared > 0)
        {
            SetStatus(() => Loc.T("Core.Status.ExplorerRestarted", cleared));
        }
    }

    // ---------------------------------------------------------------- 选择辅助

    public void SelectAll() => RunOnUiThread(SelectAllCore);

    private void SelectAllCore()
    {
        foreach (var item in Icons) item.IsSelected = true;
        UpdateCounters();
    }

    public void InvertSelection() => RunOnUiThread(InvertSelectionCore);

    private void InvertSelectionCore()
    {
        foreach (var item in Icons) item.IsSelected = !item.IsSelected;
        UpdateCounters();
    }

    public void ClearSelection() => RunOnUiThread(ClearSelectionCore);

    private void ClearSelectionCore()
    {
        foreach (var item in Icons) item.IsSelected = false;
        UpdateCounters();
    }

    // ---------------------------------------------------------------- 文案（本地化）

    /// <summary>
    /// 写入状态栏文案，并记下"它怎么算出来的"。
    /// 状态栏显示的是**上一次操作的结果**，语言切换时无法靠重读字段重算，
    /// 所以这里保存生成函数，切换语言后重新求值即可（<see cref="RefreshLocalizedTexts"/>）。
    /// </summary>
    private void SetStatus(Func<string> factory)
    {
        _statusFactory = factory;
        StatusText = factory();
    }

    /// <summary>语言切换：重新生成本地化文案，并通知所有派生属性刷新。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => RunOnUiThread(RefreshLocalizedTexts);

    private void RefreshLocalizedTexts()
    {
        if (_disposed) return;

        StatusText = _statusFactory?.Invoke() ?? Loc.T("Core.Status.Ready");

        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelfIconHint));
        OnPropertyChanged(nameof(HotkeyText));

        foreach (var item in Icons)
        {
            item.RaiseLocalizedChanged();
        }
    }

    // ---------------------------------------------------------------- 辅助

    public void UpdateCounters() => RunOnUiThread(UpdateCountersCore);

    /// <summary>
    /// 复选框是 TwoWay 绑定到 <see cref="TrayIconItem.IsSelected"/> 的，点击事件不参与，
    /// 所以只能靠条目自身的属性通知来即时刷新"已选择 N 个"。
    /// </summary>
    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrayIconItem.IsSelected)) return;
        SelectedCount = Icons.Count(i => i.IsSelected);
    }

    private void UpdateCountersCore()
    {
        SelectedCount = Icons.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasIcons));
    }

    /// <summary>
    /// 图标列表是否已经过期到需要重新扫描。按进程操作（快捷键/窗口功能）会在找不到目标时
    /// 补一次刷新，但外部代码可能连着调用十几次，这里用 2 秒闸门避免反复全量扫描。
    /// </summary>
    private bool IsListStale()
        => !_isScanning && !_isRestoring && Environment.TickCount64 - _lastRefreshTicks > 2000;

    /// <summary>收集当前所有已隐藏条目。</summary>
    private List<TrayIconItem> CollectHiddenIcons()
    {
        var hidden = new List<TrayIconItem>();
        foreach (var item in Icons)
        {
            if (item.IsHidden) hidden.Add(item);
        }
        return hidden;
    }

    /// <summary>收集属于指定进程、且隐藏状态匹配的条目。</summary>
    private static List<TrayIconItem> CollectIconsOfProcess(
        ObservableCollection<TrayIconItem> source,
        string processName,
        bool hidden)
    {
        var results = new List<TrayIconItem>();
        foreach (var item in source)
        {
            if (item.IsHidden != hidden) continue;
            if (IsSameProcess(item, processName)) results.Add(item);
        }
        return results;
    }

    /// <summary>条目是否属于指定进程（优先比进程名，退回比可执行文件名）。</summary>
    private static bool IsSameProcess(TrayIconItem item, string processName)
    {
        if (string.Equals(item.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(GetFileStem(item.ExecutablePath), processName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把外部传入的进程描述规范成"进程名"：去空白、去引号、去目录、去 .exe 后缀。</summary>
    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;

        var name = processName.Trim().Trim('"');
        try
        {
            name = Path.GetFileName(name);
        }
        catch
        {
            // 非法路径就按原样使用
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Trim();
    }

    /// <summary>把进程名加入待查询集合（拿不到进程名时退回可执行文件名）。</summary>
    private static void AddProcessName(HashSet<string> names, string? processName, string executablePath)
    {
        if (!string.IsNullOrEmpty(processName))
        {
            names.Add(processName);
            return;
        }

        var stem = GetFileStem(executablePath);
        if (!string.IsNullOrEmpty(stem)) names.Add(stem);
    }

    private static string GetFileStem(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return string.Empty;

        try
        {
            return Path.GetFileNameWithoutExtension(executablePath);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>条目对应的进程名（缺失时从 exe 路径退回），已归一化为不含 .exe。</summary>
    private static string ProcessNameOf(TrayIconItem item)
        => NormalizeProcessName(
            string.IsNullOrEmpty(item.ProcessName) ? item.ExecutablePath : item.ProcessName);

    /// <summary>把路径规范化为可比较的绝对路径（失败时返回原路径）。</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// 该可执行路径是否属于"本软件"：完全相同的 exe 路径，或者是同名的本软件副本
    /// （例如用户保留下来的旧版本目录、桌面便携版）。后者在本机实测会造成"列表里出现
    /// 以本软件为进程名、图标为占位图标的假条目"，因此按文件名一并排除。
    /// </summary>
    private static bool IsSelfExecutablePath(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) || SelfExecutablePath.Length == 0) return false;

        if (string.Equals(NormalizePath(executablePath), SelfExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SelfExecutableName.Length == 0) return false;

        try
        {
            return string.Equals(Path.GetFileName(executablePath), SelfExecutableName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 注册表条目是否属于本软件自身：可执行路径 = 本软件 exe，或解析出的进程号 = 本进程。
    /// 不按进程名判断 —— exe 被改名后进程名就不叫 TrayHider 了。
    /// </summary>
    private static bool IsSelfEntry(string? executablePath, ProcessInfo procInfo)
        => procInfo.Id == Environment.ProcessId || IsSelfExecutablePath(executablePath);

    /// <summary>列表条目是否属于本软件自身（进程号在刷新时写入，路径比较兜底）。</summary>
    private static bool IsSelfItem(TrayIconItem item)
        => item.ProcessId == Environment.ProcessId || IsSelfExecutablePath(item.ExecutablePath);

    /// <summary>清理 _hiddenBackups 里属于本软件自身的残留备份，返回清理条数。</summary>
    private int RemoveSelfBackups()
    {
        if (_hiddenBackups.Count == 0) return 0;

        List<string>? stale = null;
        foreach (var pair in _hiddenBackups)
        {
            if (!IsSelfExecutablePath(pair.Value.ExecutablePath)) continue;
            (stale ??= new List<string>()).Add(pair.Key);
        }

        if (stale == null) return 0;
        foreach (var key in stale) _hiddenBackups.Remove(key);
        return stale.Count;
    }

    /// <summary>把进程名记入"已知不支持托盘重建"集合（有变化时同步配置，落盘交给 Persist）。</summary>
    private bool MarkRebuildUnsupported(string processName)
    {
        var name = NormalizeProcessName(processName);
        if (name.Length == 0) return false;
        if (!_rebuildUnsupported.Add(name)) return false;

        SyncRebuildUnsupportedConfig();
        return true;
    }

    /// <summary>把进程名从"已知不支持托盘重建"集合里移出（恢复成功后调用）。</summary>
    private bool ClearRebuildUnsupported(string processName)
    {
        var name = NormalizeProcessName(processName);
        if (name.Length == 0) return false;
        if (!_rebuildUnsupported.Remove(name)) return false;

        SyncRebuildUnsupportedConfig();
        return true;
    }

    /// <summary>把内存里的"不支持重建"集合写回配置对象（排序保证 config.json 稳定易读）。</summary>
    private void SyncRebuildUnsupportedConfig()
    {
        var list = new List<string>(_rebuildUnsupported.Count);
        foreach (var name in _rebuildUnsupported) list.Add(name);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        _config.RebuildUnsupported = list;
    }

    /// <summary>
    /// 非破坏性隐藏：NIM_MODIFY + NIF_STATE 置 NIS_HIDDEN，并尽量确认图标确实离开了通知区域。
    ///
    /// 实测（build\tray-mode-probe，自建探针图标）：置位后 Shell 仍持有注册（存在性探测 TRUE），
    /// 文档化只读 API Shell_NotifyIconGetRect 立即不再返回矩形；清除后同一矩形恢复显示。
    /// 因此这条路径保留了目标程序自己的回调，是 issue#3 的根治办法。
    ///
    /// 注意 GetRect 的语义：它失败说明"当前不在通知区域显示"，但**不能**据此断言图标不存在
    /// （图标可能只是收在溢出面板里、或桌面/任务栏处于非常规状态）。所以：
    ///   * 置位前能拿到矩形、置位后拿不到  → 验证通过；
    ///   * 置位前就拿不到（本来就不在显示区）→ 无法验证，相信 Shell 的返回值；
    ///   * 置位后仍能拿到矩形（最多等 80ms）→ 视为未生效，把状态清回去并返回 false，
    ///     由调用方明确告知"未隐藏"，**绝不**改用 NIM_DELETE 去删目标程序的注册。
    /// </summary>
    private static bool TryHideWithoutDeleting(IntPtr hwnd, TrayIconIdentity identity)
    {
        var wasDisplayed = TrayIconController.IsIconDisplayed(hwnd, identity);

        if (!TrayIconController.TryHideByState(hwnd, identity)) return false;
        if (!wasDisplayed) return true;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Thread.Sleep(20);
            if (!TrayIconController.IsIconDisplayed(hwnd, identity)) return true;
        }

        // 状态位设了但图标还在显示：清回去（不留半吊子状态），并如实告诉用户没有隐藏成功
        TrayIconController.TryShowByState(hwnd, identity);
        return false;
    }

    /// <summary>隐藏操作里"未隐藏成功"的汇总后缀（不支持非破坏性隐藏的图标）。</summary>
    private static string BuildFailSuffix(int failed)
        => failed > 0 ? Loc.T("Core.Suffix.HideFailed", failed) : string.Empty;

    /// <summary>隐藏操作里"被跳过"的汇总后缀。</summary>
    private static string BuildSkipSuffix(int skipped)
        => skipped > 0 ? Loc.T("Core.Suffix.SkippedUnsupported", skipped) : string.Empty;

    /// <summary>
    /// 保存隐藏备份到 config.json。
    /// 每次保存都是整份 JSON，短时间内连续操作（批量隐藏/恢复、快捷键连按）没必要写多次，
    /// 因此做一次轻量去抖：只用一个 UI 计时器，不引入后台线程竞态。
    /// </summary>
    private void Persist()
    {
        // 隐藏项的图标字节只在"隐藏瞬间"需要，落盘后就不再占用内存
        foreach (var item in Icons)
        {
            if (item.IsHidden) item.IconSnapshot = null;
        }

        _persistDirty = true;

        if (_persistTimer == null || _disposed)
        {
            FlushPersist();
            return;
        }

        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void FlushPersist()
    {
        _persistTimer?.Stop();
        if (!_persistDirty) return;

        _persistDirty = false;
        _config.Save();
    }

    private void ApplyPendingSort()
    {
        if (_sortDirty) SortIcons();
    }

    /// <summary>停止计时器并把待写的配置落盘。</summary>
    public void Dispose()
    {
        RunOnUiThread(() =>
        {
            _disposed = true;
            Loc.LanguageChanged -= OnLanguageChanged;
            StopCore();
            FlushPersist();
        });
    }

    // ---------------------------------------------------------------- UI 线程

    /// <summary>
    /// 把调用切到 UI 线程执行。Icons / 隐藏备份 / 计时器只允许在 UI 线程上改动 ——
    /// 主窗口与窗口列表视图模型都会调用本类，统一在入口处保证线程归属。
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess() ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException)
        {
            // 程序正在退出，忽略
        }
    }

    private static T RunOnUiThread<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess() ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return func();
        }

        try
        {
            return dispatcher.Invoke(func);
        }
        catch (TaskCanceledException)
        {
            return default!;
        }
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

    // ---------------------------------------------------------------- 内部类型

    /// <summary>单个待恢复图标的执行计划。</summary>
    private sealed class RestorePlan
    {
        public RestorePlan(TrayIconItem item, TrayIconIdentity identity, IntPtr hwnd)
        {
            Item = item;
            Identity = identity;
            Hwnd = hwnd;
        }

        public TrayIconItem Item { get; }

        public TrayIconIdentity Identity { get; }

        /// <summary>注册图标所用的窗口句柄（可能为 0，会在恢复过程中补定位）。</summary>
        public IntPtr Hwnd { get; set; }

        /// <summary>该条目是非破坏性状态隐藏（清除状态位即可恢复）。</summary>
        public bool IsStateMode { get; set; }

        /// <summary>由目标程序自己重新注册回来的（只有这种成功才算"它支持托盘重建"）。</summary>
        public bool OwnerRebuilt { get; set; }

        /// <summary>状态清除失败但注册还在：语义不明，按失败处理，不得谎报恢复。</summary>
        public bool Ambiguous { get; set; }

        /// <summary>图标已经在托盘里（目标程序自己注册回来，或本来就没被真正删除）。</summary>
        public bool Registered { get; set; }

        /// <summary>已经向该图标所属进程投递过 TaskbarCreated，值得在预算内继续等。</summary>
        public bool Waiting { get; set; }
    }

    /// <summary>
    /// 一次批量恢复的结果，用于拼装状态栏文案。
    /// Restored = 成功；Failed = 目标程序不响应托盘重建（保持隐藏，不伪造）；
    /// Cleaned = 进程已退出、记录已清理（不算失败）；UnsupportedNames = 需要重启才能恢复的程序名。
    /// </summary>
    private readonly record struct RestoreOutcome(
        int Restored,
        int Failed,
        int Cleaned,
        string[] UnsupportedNames);

    /// <summary>
    /// 运行中进程索引（可执行路径 / 进程名 → 进程），带 1.5 秒 TTL。
    ///
    /// 旧实现每次刷新都用 Process.GetProcesses() 遍历全系统几百个进程，并且对**每一个**
    /// 进程都读 MainModule（跨进程打开 + 读内存，权限不足还会抛异常）：
    /// 本机实测 112ms / 1.08MB 一次。
    ///
    /// 优化后仍然只枚举一次全系统进程（约 9ms / 0.76MB），但只用系统快照里的
    /// 进程名做过滤，**名字不在托盘条目里的进程一律不读 MainModule** —— 实测降到
    /// 33ms / 0.84MB，其中大部分就是那 ~20 次真正必要的 MainModule 读取。
    /// 结果再按 1.5 秒 TTL 复用，刷新与后台守护共享，重复调用零成本。
    ///
    /// 为什么不逐个 Process.GetProcessesByName：它每调用一次都要全量枚举一遍系统进程，
    /// 本机 96 个待查名字要 810ms（比直接枚举慢两个数量级），实测后放弃。
    /// 只在 UI 线程使用，因此不加锁。
    /// </summary>
    private sealed class RunningProcessIndex
    {
        private const long TtlMs = 1500;

        private readonly Dictionary<string, ProcessInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProcessInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上次查询覆盖过的名字集合（用于判断缓存能否命中）。</summary>
        private readonly HashSet<string> _coveredNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次请求的名字暂存（复用，避免每次调用分配）。</summary>
        private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);

        private long _refreshedAt;

        /// <summary>确保索引覆盖 <paramref name="neededNames"/>（TTL 内且已覆盖则直接复用）。</summary>
        public void EnsureFresh(IEnumerable<string> neededNames)
        {
            _requested.Clear();
            foreach (var name in neededNames)
            {
                if (!string.IsNullOrEmpty(name)) _requested.Add(name);
            }

            if (_requested.Count == 0)
            {
                Reset();
                return;
            }

            if (Environment.TickCount64 - _refreshedAt <= TtlMs && _coveredNames.IsSupersetOf(_requested))
            {
                return;
            }

            Reset();
            _refreshedAt = Environment.TickCount64;
            _coveredNames.UnionWith(_requested);

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string? name;
                    try
                    {
                        name = process.ProcessName;   // 系统快照里就有，不需要打开进程
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(name) || !_requested.Contains(name)) continue;

                    var path = TryGetMainModulePath(process);

                    // 进程名 → 第一个实例（供 TryGetProcess 与"读不到主模块"时的回退使用）
                    if (!_byName.ContainsKey(name))
                    {
                        _byName[name] = new ProcessInfo(process.Id, name, path ?? string.Empty);
                    }

                    if (!string.IsNullOrEmpty(path) && !_byPath.ContainsKey(path))
                    {
                        _byPath[path] = new ProcessInfo(process.Id, name, path);
                    }
                }
            }
        }

        /// <summary>按进程名取进程（仅覆盖本次索引真正查过的名字，用于 explorer 重启检测）。</summary>
        public bool TryGetProcess(string processName, out ProcessInfo info)
            => _byName.TryGetValue(processName, out info);

        /// <summary>按可执行路径（精确）或进程名（回退）查找进程。</summary>
        public bool TryFind(string executablePath, out ProcessInfo info)
        {
            if (!string.IsNullOrEmpty(executablePath) && _byPath.TryGetValue(executablePath, out info))
            {
                return true;
            }

            var name = GetFileStem(executablePath);
            return _byName.TryGetValue(name, out info);
        }

        private void Reset()
        {
            _byPath.Clear();
            _byName.Clear();
            _coveredNames.Clear();
            _refreshedAt = 0;
        }

        private static string? TryGetMainModulePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;   // 权限不足或进程已退出
            }
        }
    }
}

/// <summary>索引里使用的进程信息（进程号 / 进程名 / 可执行文件路径）。</summary>
internal readonly record struct ProcessInfo(int Id, string ProcessName, string ExecutablePath);

internal static class TrayIconItemExtensions
{
    /// <summary>进程状态变化后刷新次级文案。</summary>
    public static void OnSubTitleRefresh(this TrayIconItem item)
        => item.RaiseSubTitleChanged();
}
