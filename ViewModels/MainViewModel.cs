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
///   通过 Shell_NotifyIcon 的 NIM_DELETE 让 Shell 立即删除目标图标（跨进程），
///   图标从任务栏托盘与溢出面板同时消失，进程照常运行。绝不删除注册表子键，
///   也绝不触碰目标进程本身。
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

    /// <summary>后台自动重试的探测预算（毫秒）：跑在 Refresh 里，必须短，不能让刷新卡顿。</summary>
    private const int AutoRetryProbeBudgetMs = 250;

    /// <summary>后台自动重试的节流间隔（毫秒）：每 30 秒最多一轮。</summary>
    private const int AutoRetryIntervalMs = 30_000;

    /// <summary>后台自动重试每轮最多处理的进程数。</summary>
    private const int AutoRetryMaxProcesses = 3;

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
    private string _statusText = "就绪";
    private int _selectedCount;

    public MainViewModel(AppConfig config)
    {
        _config = config;
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
        ? "未选择任何图标"
        : $"已选择 {_selectedCount} 个图标";

    public int HiddenCount => Icons.Count(i => i.IsHidden);

    public bool HasIcons => Icons.Count > 0;

    public string EmptyHint => "未发现可管理的托盘图标。\n请先运行带有托盘图标的程序，再回来刷新。";

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
        ? "本软件图标已隐藏。仍可用全局快捷键唤出主窗口。"
        : "本软件图标显示在系统托盘中。";

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
                StatusText = value ? "已设置开机自动启动" : "已取消开机自动启动";
                OnPropertyChanged();
            }
            else
            {
                StatusText = "开机自启设置失败，请检查注册表权限";
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
            return binding.IsValid ? binding.ToString() : "未设置";
        }
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    /// <summary>保存自定义快捷键后刷新界面显示。</summary>
    public void NotifyHotkeyChanged()
    {
        OnPropertyChanged(nameof(HotkeyText));
    }

    public void SetStatus(string status)
    {
        StatusText = status;
    }

    public event EventHandler<bool>? SelfTrayIconVisibilityChanged;

    /// <summary>
    /// 隐藏『已知不支持托盘重建』的程序前的确认回调；返回 false 表示不隐藏该项。
    /// 由界面接线（弹一个"确定要隐藏吗？该程序恢复后图标可能点不动"的对话框）。
    /// 为 null 时保守跳过这些图标，并在状态栏说明原因。
    /// </summary>
    public Func<TrayIconItem, bool>? ConfirmHideUnsupported { get; set; }

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
    /// 刷新时对已隐藏项重新执行 NIM_DELETE，应对程序周期性重新注册图标。
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

            // 4) 监控：对已隐藏且运行中的图标重新 NIM_DELETE，保持隐藏
            ReapplyHidden();

            if (_sortDirty) SortIcons();
            UpdateCounters();

            var hidden = 0;
            for (var i = 0; i < Icons.Count; i++)
            {
                if (Icons[i].IsHidden) hidden++;
            }

            var status = $"共 {Icons.Count} 个运行中的托盘图标，其中 {hidden} 个已隐藏";
            if (!string.Equals(_statusText, status, StringComparison.Ordinal))
            {
                StatusText = status;
            }

            // 5) explorer 换了进程号 = 它刚重启过 = 系统广播过 TaskbarCreated，
            //    各程序其实都已经用正确的回调重新注册了 → 清掉过期的降级告警。
            DetectExplorerRestart();

            // 6) 后台自动重试降级图标（主窗口可见时才有意义，内部自己节流）
            AutoRetryDegradedIcons();
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _hasInitialRefresh = true;
            _lastRefreshTicks = Environment.TickCount64;
            _isScanning = false;
        }
    }

    /// <summary>
    /// 对"已隐藏且运行中"的图标重新执行 NIM_DELETE。两个用途：
    ///   1. 刷新时对抗程序周期性重新注册图标；
    ///   2. 恢复后的副作用回收 —— 目标程序收到 TaskbarCreated 会把它的**全部**图标
    ///      都加回来，其中仍应保持隐藏的必须再删一次，否则"恢复一个 = 把所有隐藏的都放出来"。
    /// </summary>
    private void ReapplyHidden()
    {
        foreach (var item in Icons)
        {
            if (!item.IsHidden || !item.IsRunning) continue;
            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity)) continue;

            // 句柄失效（程序换了窗口重新注册，例如刚收到 TaskbarCreated）→ 先重新定位
            if (!TrayIconController.IsWindowAlive(item.Hwnd) &&
                identity.IsValid &&
                !string.IsNullOrEmpty(item.ProcessName))
            {
                var hwnd = TrayIconController.FindTrayWindow(item.ProcessName, identity);
                if (hwnd != IntPtr.Zero) item.Hwnd = hwnd;
            }

            if (item.Hwnd != IntPtr.Zero)
            {
                TrayIconController.HideIcon(item.Hwnd, identity);
            }
        }
    }

    /// <summary>
    /// 低频守护已隐藏图标：只复用隐藏时缓存的窗口句柄重新 NIM_DELETE。
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

            if (!_processIndex.TryFind(item.ExecutablePath, out _))
            {
                if (item.IsRunning)
                {
                    item.IsRunning = false;
                    item.OnSubTitleRefresh();
                }
                continue;
            }

            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity)) continue;
            if (item.Hwnd != IntPtr.Zero)
            {
                TrayIconController.HideIcon(item.Hwnd, identity);
            }
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

        // 过滤：图标不在托盘（探测不到窗口）且未隐藏 → 多余条目，不显示
        if (hwnd == IntPtr.Zero && !isHidden)
        {
            return false;
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
            IsHidden = isHidden,
            IsSelected = false
        };

        item.Icon = IconExtractor.FromExecutable(entry.ExecutablePath)
                    ?? IconExtractor.FromSnapshot(entry.SubKeyName);

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
            IsHidden = true,
            IsSelected = false
        };

        item.Icon = IconExtractor.FromExecutable(backup.ExecutablePath)
                    ?? IconExtractor.FromSnapshot(subKeyName);
        if (item.Icon == null && backup.IconSnapshot is { Length: > 0 })
        {
            item.Icon = IconExtractor.FromSnapshotBytes(backup.IconSnapshot);
        }

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
            StatusText = "请先勾选要隐藏的图标";
            return;
        }

        var success = 0;
        var skipped = 0;
        List<TrayIconItem>? skippedItems = null;
        var confirmCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets)
        {
            switch (HideItem(item, confirmCache))
            {
                case HideResult.Hidden:
                    success++;
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
        StatusText = $"已隐藏 {success} 个图标，对应程序仍在后台正常运行{BuildSkipSuffix(skipped)}";
    }

    /// <summary>隐藏当前列表中的全部图标。</summary>
    public void HideAll() => RunOnUiThread(HideAllCore);

    private void HideAllCore()
    {
        if (GuardRestoreInProgress()) return;

        var success = 0;
        var skipped = 0;
        var total = 0;
        var confirmCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Icons)
        {
            if (item.IsHidden) continue;
            total++;

            switch (HideItem(item, confirmCache))
            {
                case HideResult.Hidden:
                    success++;
                    break;
                case HideResult.Skipped:
                    skipped++;
                    break;
            }
        }

        Persist();
        UpdateCounters();
        ApplyPendingSort();
        StatusText = total == 0
            ? "所有图标都已处于隐藏状态"
            : success == 0 && skipped > 0
                ? $"已跳过 {skipped} 个图标：该程序不支持托盘重建"
                : $"已隐藏全部 {success} 个图标{BuildSkipSuffix(skipped)}";
    }

    /// <summary>隐藏单个图标的结果。</summary>
    private enum HideResult
    {
        /// <summary>已隐藏（含"本来就已经处于隐藏状态"）。</summary>
        Hidden,

        /// <summary>失败：定位不到图标，或 NIM_DELETE 被 Shell 拒绝。</summary>
        Failed,

        /// <summary>被跳过：本软件自身，或该程序已知不支持托盘重建且用户选择不隐藏。</summary>
        Skipped
    }

    /// <summary>彻底隐藏单个图标：NIM_DELETE（图标立即从托盘与溢出面板消失）。</summary>
    /// <param name="item">目标条目。</param>
    /// <param name="confirmCache">
    /// 同一批隐藏里"进程名 → 用户是否同意隐藏"的缓存：同一进程只弹一次确认框。
    /// 单条操作（双击）可以传 null，等价于每项都问一次。
    /// </param>
    private HideResult HideItem(TrayIconItem item, Dictionary<string, bool>? confirmCache = null)
    {
        if (item.IsHidden) return HideResult.Hidden;

        // 兜底守卫：绝不能隐藏本软件自身（会把本软件的托盘图标/窗口隐藏占位图标删掉）
        if (IsSelfItem(item))
        {
            StatusText = "不能隐藏本软件自身的托盘图标，请用设置里的「隐藏本软件的托盘图标」";
            return HideResult.Skipped;
        }

        // 已知"不支持托盘重建"的程序：隐藏后恢复只能伪造注册、图标会点不动 —— 先问一次
        if (!ConfirmHideAllowed(item, confirmCache)) return HideResult.Skipped;

        if (item.Hwnd == IntPtr.Zero ||
            !TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity))
        {
            StatusText = $"隐藏 {item.DisplayName} 失败：无法定位图标";
            return HideResult.Failed;
        }

        // 记录恢复信息（含窗口类名，供重启后按类名重新定位窗口）。
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
            WindowClassName = windowClassName
        };

        if (!TrayIconController.HideIcon(item.Hwnd, identity))
        {
            _hiddenBackups.Remove(item.SubKeyName);
            StatusText = $"隐藏 {item.DisplayName} 失败";
            return HideResult.Failed;
        }

        item.WindowClassName = windowClassName;
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
                StatusText = $"没有找到 {name} 可隐藏的托盘图标";
                return 0;
            }

            var success = 0;
            var skipped = 0;
            var confirmCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in targets)
            {
                switch (HideItem(item, confirmCache))
                {
                    case HideResult.Hidden:
                        success++;
                        break;
                    case HideResult.Skipped:
                        skipped++;
                        break;
                }
            }

            Persist();
            UpdateCounters();
            ApplyPendingSort();
            StatusText = success > 0
                ? $"已隐藏 {name} 的 {success} 个托盘图标{BuildSkipSuffix(skipped)}"
                : skipped > 0
                    ? $"已跳过 {name}：该程序不支持托盘重建"
                    : $"隐藏 {name} 的托盘图标失败";
            return success;
        }
        catch (Exception ex)
        {
            StatusText = $"隐藏 {processName} 的托盘图标失败：{ex.Message}";
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
            StatusText = hidden.Count == 0 && orphans > 0
                ? $"已清理 {orphans} 条已退出程序的隐藏记录"
                : BuildRestoreStatusText(outcome);
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
            StatusText = "请先勾选要显示的图标";
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
                StatusText = $"没有找到 {name} 被隐藏的托盘图标";
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
            StatusText = $"恢复 {processName} 的托盘图标失败：{ex.Message}";
            return 0;
        }
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
                StatusText = $"已跳过 {item.DisplayName}：该程序不支持托盘重建，隐藏后图标会点不动";
            }
            return;
        }

        Persist();
        UpdateCounters();
        ApplyPendingSort();
    }

    /// <summary>
    /// 批量恢复被隐藏的图标 —— 本文件的核心逻辑。步骤与原因：
    ///
    ///   1. 先探测：图标是不是已经在托盘里了（程序可能自己又注册过）。在就视为已完成，
    ///      重复注册反而可能破坏目标程序原有的回调。
    ///   2. 仍缺失的，按"进程"去重后向该程序**全部实例进程**的窗口 PostMessage(TaskbarCreated)。
    ///      这是唯一能让目标程序用**它自己的** hWnd/uID/uCallbackMessage 重新 NIM_ADD 的办法，
    ///      恢复后的图标右键菜单/双击才是正常的（issue#1 的根因见 TrayIconController 注释）。
    ///   3. 短轮询（15ms 一步、总预算 600ms）用 NIM_MODIFY（uFlags=0，无副作用）探测图标是否回归，
    ///      回归 = 恢复成功。实测回归耗时 min 6ms / 中位 11ms / 最大 109ms，详见
    ///      build/TrayProbe/results/report.txt 的双进程探针报告。
    ///   4. 预算用尽仍未回归 = 该程序不处理 TaskbarCreated → 降级用本进程 NIM_ADD 重建，
    ///      并置 CallbackDegraded 提醒用户"右键菜单可能失效，重启该程序即可恢复"。
    ///   5. 收尾做副作用回收：目标程序收到 TaskbarCreated 会把它的**全部**图标都加回来，
    ///      其中本该保持隐藏的必须再 NIM_DELETE 一次（ReapplyHidden），
    ///      否则"恢复一个图标 = 把所有隐藏的都放出来"。
    ///
    /// 整个过程同步执行、总阻塞不超过 RestoreProbeBudgetMs，等待期间会抽取消息泵，
    /// 因此界面不会假死（重入由 _isRestoring 拦住）。
    /// </summary>
    private RestoreOutcome RestoreItems(IEnumerable<TrayIconItem> source)
    {
        var plans = new List<RestorePlan>();
        var failed = 0;

        foreach (var item in source)
        {
            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity))
            {
                failed++;
                continue;
            }

            var hwnd = ResolveIconWindow(item, identity);
            plans.Add(new RestorePlan(item, identity, hwnd));
        }

        if (plans.Count == 0)
        {
            return new RestoreOutcome(0, failed, 0, Array.Empty<string>());
        }

        // 1) 已经在托盘里的直接算完成（不要重复注册）
        foreach (var plan in plans)
        {
            plan.Registered = TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity);
        }

        // 2) 仍缺失的按进程去重投递 TaskbarCreated（同一进程只投一次）
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var posted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
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

                foreach (var plan in plans)
                {
                    if (plan.Registered || !plan.Waiting) continue;

                    if (TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity))
                    {
                        plan.Registered = true;
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
        foreach (var plan in plans)
        {
            if (plan.Registered || !plan.Waiting) continue;

            var hwnd = TrayIconController.FindTrayWindow(plan.Item.ProcessName, plan.Identity);
            if (hwnd == IntPtr.Zero) continue;

            plan.Hwnd = hwnd;
            plan.Registered = true;
        }

        // 4) 收尾：成功项清理备份；失败项降级重建（只保证可见性）
        var restored = 0;
        var degraded = 0;
        var degradedNames = new List<string>(2);

        foreach (var plan in plans)
        {
            if (plan.Registered)
            {
                CompleteRestore(plan.Item, plan.Hwnd);

                // 这个程序能用自己的回调注册回来：如果以前被记成"不支持托盘重建"，撤销那条记录
                ClearRebuildUnsupported(ProcessNameOf(plan.Item));

                restored++;
                continue;
            }

            if (!AddIconFallback(plan))
            {
                failed++;
                continue;
            }

            CompleteRestore(plan.Item, plan.Hwnd);
            plan.Item.CallbackDegraded = true;

            // 学习：这个程序不响应 TaskbarCreated，下次隐藏它之前必须先提醒用户
            MarkRebuildUnsupported(ProcessNameOf(plan.Item));

            restored++;
            degraded++;

            var name = plan.Item.ProcessName;
            if (!string.IsNullOrEmpty(name) && !degradedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                degradedNames.Add(name);
            }
        }

        // 5) 副作用回收：目标程序把它的全部图标都加回来了，仍应隐藏的要再删一次
        if (posted.Count > 0)
        {
            ReapplyHidden();
        }

        return new RestoreOutcome(restored, failed, degraded, degradedNames.ToArray());
    }

    /// <summary>
    /// 定位注册该图标所用的窗口：先复用缓存句柄，失效后依次尝试
    ///   1. NIM_MODIFY 探测（图标还在托盘里时最准）；
    ///   2. 隐藏前记录的窗口类名；
    ///   3. 同进程内的托盘特征窗口（应用重启后 ATL 动态类名会变，只能退到这一步）。
    /// </summary>
    private static IntPtr ResolveIconWindow(TrayIconItem item, TrayIconIdentity identity)
    {
        if (TrayIconController.IsWindowAlive(item.Hwnd)) return item.Hwnd;

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

    /// <summary>
    /// 降级路径：用本进程伪造的 NIM_ADD 把图标加回来，只保证"图标可见"。
    /// 目标程序不处理我们伪造的回调消息，所以这条路径下右键菜单可能失效。
    /// </summary>
    private bool AddIconFallback(RestorePlan plan)
    {
        var hwnd = plan.Hwnd;
        if (!TrayIconController.IsWindowAlive(hwnd))
        {
            hwnd = ResolveIconWindow(plan.Item, plan.Identity);
        }

        if (hwnd == IntPtr.Zero) return false;

        var snapshot = TrayIconReader.ReadIconSnapshot(plan.Item.SubKeyName)
            ?? plan.Item.IconSnapshot
            ?? (_hiddenBackups.TryGetValue(plan.Item.SubKeyName, out var backup) ? backup.IconSnapshot : null);

        if (!TrayIconController.RecreateIconFallback(hwnd, plan.Identity, snapshot, plan.Item.DisplayName))
        {
            return false;
        }

        plan.Hwnd = hwnd;
        return true;
    }

    /// <summary>恢复成功后同步内存状态：清掉隐藏备份，标记图标重新显示。</summary>
    private void CompleteRestore(TrayIconItem item, IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) item.Hwnd = hwnd;

        item.IsHidden = false;
        item.CallbackDegraded = false;
        item.IconSnapshot = null;
        _hiddenBackups.Remove(item.SubKeyName);
        _sortDirty = true;
    }

    /// <summary>拼装恢复结果提示。降级项必须写清楚，否则用户会以为功能坏了。</summary>
    private static string BuildRestoreStatusText(RestoreOutcome outcome)
    {
        if (outcome.Restored == 0)
        {
            // 失败原因有两种：定位不到目标窗口，或窗口找到了但 NIM_ADD 被系统拒绝（提权/受保护进程）。
            // 两者对用户的处理建议相同（重启该程序），因此合并成一句可操作的提示。
            return outcome.Failed > 0
                ? "恢复失败：无法定位或重建该程序的托盘图标，重启该程序后即可恢复"
                : "当前没有被隐藏的图标";
        }

        var text = $"已恢复 {outcome.Restored} 个图标到任务栏托盘";
        if (outcome.Failed > 0) text += $"（{outcome.Failed} 个失败）";

        if (outcome.Degraded > 0)
        {
            var names = string.Join("、", outcome.DegradedNames);
            text += $"。{names} 未响应托盘重建，右键菜单可能失效，重启该程序即可恢复";
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
        StatusText = "正在恢复托盘图标，请稍候";
        return true;
    }

    /// <summary>恢复流程的重入保护：过程中会抽取消息泵，期间可能再次触发按钮点击。</summary>
    private bool TryEnterRestore()
    {
        if (_disposed) return false;

        if (_isRestoring)
        {
            StatusText = "正在恢复托盘图标，请稍候";
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

    // ---------------------------------------------------------------- 降级修复

    /// <summary>
    /// 重试"已验证降级"的图标（设置页按钮）：重新投递 TaskbarCreated，让程序用**它自己的**
    /// 回调重新注册。成功即清除"右键菜单可能失效"的标记，并把该程序移出"不支持托盘重建"名单。
    ///
    /// 为什么必须先删掉当前注册：降级后的图标是本软件伪造的，NIM_MODIFY 探测对它同样返回
    /// TRUE —— "图标存在"无法区分真伪。所以只能先 NIM_DELETE、再让程序自己注册回来；
    /// 若它仍不响应，则用伪造注册把图标加回去，保证可见性不倒退（但**不算**修复成功）。
    /// </summary>
    /// <returns>修复成功的图标数量。</returns>
    public int RetryDegradedIcons() => RunOnUiThread(RetryDegradedIconsCore);

    private int RetryDegradedIconsCore()
    {
        if (!TryEnterRestore()) return 0;

        try
        {
            var targets = new List<TrayIconItem>();
            foreach (var item in Icons)
            {
                if (item.CallbackDegraded && item.IsRunning) targets.Add(item);
            }

            if (targets.Count == 0)
            {
                StatusText = "没有需要重试的图标";
                return 0;
            }

            var fixedCount = RetryDegraded(targets, RetryProbeBudgetMs);

            Persist();
            UpdateCounters();
            ApplyPendingSort();
            StatusText = fixedCount > 0
                ? $"已修复 {fixedCount} 个图标的右键菜单"
                : "重试失败：这些程序仍未响应托盘重建，重启该程序后即可恢复";
            return fixedCount;
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>
    /// 后台自动重试（跑在 Refresh 里，主窗口可见时才有意义）：
    /// 每 <see cref="AutoRetryIntervalMs"/> 最多一轮、每轮最多 <see cref="AutoRetryMaxProcesses"/> 个进程，
    /// 用很短的探测预算，成功就清掉过期的降级提示。
    ///
    /// 已知"不支持托盘重建"的程序不在自动重试范围内 —— 重试必须先删注册，那会让它们的图标
    /// 每隔半分钟闪一下；这类进程靠"程序自己重启（进程号变化）"或用户手动"重试修复"来恢复。
    /// </summary>
    private void AutoRetryDegradedIcons()
    {
        if (!_isMainWindowVisible || _isRestoring) return;
        if (Environment.TickCount64 - _lastAutoRetryTicks < AutoRetryIntervalMs) return;

        var targets = new List<TrayIconItem>();
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Icons)
        {
            if (!item.CallbackDegraded || !item.IsRunning) continue;

            var name = ProcessNameOf(item);
            if (name.Length == 0) continue;
            if (_rebuildUnsupported.Contains(name)) continue;        // 已知不支持：不打扰（避免图标闪烁）

            if (!processes.Contains(name) && processes.Count >= AutoRetryMaxProcesses) continue;
            processes.Add(name);
            targets.Add(item);
        }

        if (targets.Count == 0) return;

        _lastAutoRetryTicks = Environment.TickCount64;

        _isRestoring = true;   // 复用恢复期的重入守卫：期间不允许隐藏类操作插进来
        try
        {
            var fixedCount = RetryDegraded(targets, AutoRetryProbeBudgetMs);
            if (fixedCount > 0)
            {
                Persist();
                ApplyPendingSort();
                StatusText = $"已自动修复 {fixedCount} 个图标的右键菜单";
            }
        }
        catch
        {
            // 后台重试失败不能影响刷新
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>
    /// 重试的公共实现：删掉当前注册 → 按进程去重投递 TaskbarCreated → 短轮询 → 失败项用伪造注册加回。
    /// </summary>
    private int RetryDegraded(List<TrayIconItem> targets, int budgetMs)
    {
        var plans = new List<RestorePlan>();

        foreach (var item in targets)
        {
            if (!TrayIconController.TryCreateIdentity(item.Uid, item.IconGuid, out var identity)) continue;

            var hwnd = ResolveIconWindow(item, identity);
            if (hwnd == IntPtr.Zero) continue;

            plans.Add(new RestorePlan(item, identity, hwnd));
        }

        if (plans.Count == 0) return 0;

        // 1) 先删掉当前注册（可能是本软件伪造的那一个），否则"存在"无法区分真伪
        foreach (var plan in plans)
        {
            TrayIconController.HideIcon(plan.Hwnd, plan.Identity);
        }

        // 2) 按进程去重投递 TaskbarCreated
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var posted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in plans)
        {
            var name = ProcessNameOf(plan.Item);
            if (name.Length == 0) continue;

            if (tried.Add(name) && TrayIconController.RequestTrayRebuild(name, plan.Hwnd) > 0)
            {
                posted.Add(name);
            }

            plan.Waiting = posted.Contains(name);
        }

        // 3) 短轮询
        if (posted.Count > 0)
        {
            var deadline = Environment.TickCount64 + budgetMs;

            while (true)
            {
                var waiting = false;

                foreach (var plan in plans)
                {
                    if (plan.Registered || !plan.Waiting) continue;

                    if (TrayIconController.IsIconRegistered(plan.Hwnd, plan.Identity))
                    {
                        plan.Registered = true;
                        continue;
                    }

                    waiting = true;
                }

                if (!waiting) break;
                if (_disposed || Environment.TickCount64 >= deadline) break;
                if (!PumpWait(RestoreProbeIntervalMs)) break;
            }

            // 3.5) 最后完整搜一次（程序可能换了新窗口注册）
            foreach (var plan in plans)
            {
                if (plan.Registered || !plan.Waiting) continue;

                var hwnd = TrayIconController.FindTrayWindow(ProcessNameOf(plan.Item), plan.Identity);
                if (hwnd == IntPtr.Zero) continue;

                plan.Hwnd = hwnd;
                plan.Registered = true;
            }
        }

        // 4) 成功：清降级标记 + 移出"不支持"名单；失败：把图标用伪造注册加回去，保证可见性
        var fixedCount = 0;

        foreach (var plan in plans)
        {
            if (plan.Registered)
            {
                plan.Item.CallbackDegraded = false;
                ClearRebuildUnsupported(ProcessNameOf(plan.Item));
                fixedCount++;
                _sortDirty = true;
                continue;
            }

            AddIconFallback(plan);
        }

        return fixedCount;
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
            StatusText = $"资源管理器已重启，{cleared} 个图标的右键菜单已自动恢复";
        }
    }

    /// <summary>
    /// 对"已知不支持托盘重建"的程序做隐藏前确认：命中名单时才问，
    /// 同一批里同一进程只问一次（答案缓存在 <paramref name="confirmCache"/>）。
    /// 回调为 null 时保守跳过（不能保证恢复后图标可用，就不动它）。
    /// </summary>
    private bool ConfirmHideAllowed(TrayIconItem item, Dictionary<string, bool>? confirmCache)
    {
        var name = ProcessNameOf(item);
        if (name.Length == 0 || !_rebuildUnsupported.Contains(name)) return true;

        if (confirmCache != null && confirmCache.TryGetValue(name, out var cached)) return cached;

        var confirm = ConfirmHideUnsupported;
        var approved = confirm != null && confirm(item);

        if (confirmCache != null) confirmCache[name] = approved;
        return approved;
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

    // ---------------------------------------------------------------- 辅助

    public void UpdateCounters() => RunOnUiThread(UpdateCountersCore);

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

    /// <summary>隐藏操作里"被跳过"的汇总后缀。</summary>
    private static string BuildSkipSuffix(int skipped)
        => skipped > 0 ? $"（跳过 {skipped} 个：该程序不支持托盘重建）" : string.Empty;

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

        /// <summary>图标已经在托盘里（目标程序自己注册回来，或本来就没被真正删除）。</summary>
        public bool Registered { get; set; }

        /// <summary>已经向该图标所属进程投递过 TaskbarCreated，值得在预算内继续等。</summary>
        public bool Waiting { get; set; }
    }

    /// <summary>一次批量恢复的结果，用于拼装状态栏文案。</summary>
    private readonly record struct RestoreOutcome(int Restored, int Failed, int Degraded, string[] DegradedNames);

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
