using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using TrayHider.Interop;
using TrayHider.Models;
using TrayHider.Services;

namespace TrayHider.ViewModels;

/// <summary>
/// 主视图模型。
///
/// 隐藏机制（与 gohide、以及本软件"隐藏自身托盘图标"选项同一底层 API）：
///   通过 Shell_NotifyIcon 的 NIM_DELETE 让 Shell 立即删除目标图标（跨进程），
///   图标从任务栏托盘与溢出面板同时消失，进程照常运行；
///   通过 NIM_ADD 恢复。绝不删除注册表子键，也绝不触碰目标进程本身。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppConfig _config;
    private readonly TrayIconReader _reader = new();
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>被隐藏图标的恢复信息，键为注册表子键名。</summary>
    private readonly Dictionary<string, TrayIconEntry> _hiddenBackups = new(StringComparer.OrdinalIgnoreCase);

    private bool _isScanning;
    private string _statusText = "就绪";
    private int _selectedCount;

    public MainViewModel(AppConfig config)
    {
        _config = config;

        foreach (var kv in _config.HiddenBackups)
        {
            _hiddenBackups[kv.Key] = kv.Value;
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

    public string HotkeyText
    {
        get
        {
            var mods = new List<string>();
            var m = _config.HotkeyModifiers;
            if ((m & 0x0002) != 0) mods.Add("Ctrl");
            if ((m & 0x0008) != 0) mods.Add("Win");
            if ((m & 0x0001) != 0) mods.Add("Alt");
            if ((m & 0x0004) != 0) mods.Add("Shift");
            mods.Add(((char)_config.HotkeyVirtualKey).ToString());
            return string.Join(" + ", mods);
        }
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    public event EventHandler<bool>? SelfTrayIconVisibilityChanged;

    // ---------------------------------------------------------------- 生命周期

    public void Start()
    {
        Refresh();
        _refreshTimer.Start();
    }

    public void Stop() => _refreshTimer.Stop();

    // ---------------------------------------------------------------- 扫描

    /// <summary>
    /// 扫描托盘图标并与现有列表做增量对齐。
    /// 列表只包含"当前正在运行"的图标；已隐藏项靠 _hiddenBackups 标记。
    /// 刷新时对已隐藏项重新执行 NIM_DELETE，应对程序周期性重新注册图标。
    /// </summary>
    public void Refresh()
    {
        if (_isScanning) return;
        _isScanning = true;

        try
        {
            var entries = _reader.ReadAll();
            var runningProcesses = BuildRunningProcessIndex();

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 现存子键：仅保留"图标真在托盘"或"已隐藏"的条目。
            //   进程运行但图标不在托盘（探测不到窗口）的历史残留会被过滤掉。
            foreach (var entry in entries)
            {
                if (!TryFindProcess(runningProcesses, entry.ExecutablePath, out var procInfo) || procInfo == null)
                {
                    continue;
                }

                var isHidden = _hiddenBackups.ContainsKey(entry.SubKeyName);
                if (UpsertEntry(entry, procInfo, isHidden))
                {
                    present.Add(entry.SubKeyName);
                }
            }

            // 2) 兼容旧状态：子键被删但进程仍运行的已隐藏项
            foreach (var (subKey, backup) in _hiddenBackups)
            {
                if (present.Contains(subKey)) continue;
                if (!TryFindProcess(runningProcesses, backup.ExecutablePath, out var procInfo) || procInfo == null)
                {
                    continue;
                }

                present.Add(subKey);
                UpsertBackup(subKey, backup, procInfo);
            }

            // 3) 移除不再符合条件的条目
            for (var i = Icons.Count - 1; i >= 0; i--)
            {
                if (!present.Contains(Icons[i].SubKeyName))
                {
                    Icons.RemoveAt(i);
                }
            }

            // 4) 监控：对已隐藏且运行中的图标重新 NIM_DELETE，保持隐藏
            ReapplyHidden();

            SortIcons();
            UpdateCounters();
            var hidden = Icons.Count(i => i.IsHidden);
            StatusText = $"共 {Icons.Count} 个运行中的托盘图标，其中 {hidden} 个已隐藏";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _isScanning = false;
        }
    }

    private void ReapplyHidden()
    {
        foreach (var item in Icons)
        {
            if (!item.IsHidden || !item.IsRunning) continue;
            if (item.Hwnd == IntPtr.Zero || item.Uid == null) continue;
            TrayIconController.HideIcon(item.Hwnd, item.Uid.Value);
        }
    }

    /// <summary>
    /// 新增或更新一个"现存子键"对应的列表条目。
    /// 返回是否应显示：图标真在托盘（探测到窗口）或已隐藏时返回 true；
    /// 进程运行但图标不在托盘（探测不到窗口且未隐藏）时返回 false（多余条目，过滤）。
    /// </summary>
    private bool UpsertEntry(TrayIconEntry entry, Process procInfo, bool isHidden)
    {
        var existing = Icons.FirstOrDefault(i =>
            string.Equals(i.SubKeyName, entry.SubKeyName, StringComparison.OrdinalIgnoreCase));

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
                hwnd = entry.Uid != null
                    ? TrayIconController.FindTrayWindow(procInfo.ProcessName, entry.Uid.Value, entry.ToolTip)
                    : IntPtr.Zero;
            }
        }
        else
        {
            hwnd = entry.Uid != null
                ? TrayIconController.FindTrayWindow(procInfo.ProcessName, entry.Uid.Value, entry.ToolTip)
                : IntPtr.Zero;
        }

        // 过滤：图标不在托盘（探测不到窗口）且未隐藏 → 多余条目，不显示
        if (hwnd == IntPtr.Zero && !isHidden)
        {
            return false;
        }

        if (existing != null)
        {
            existing.Hwnd = hwnd;
            existing.IsHidden = isHidden;
            existing.IsRunning = true;
            existing.ProcessId = procInfo.Id;
            existing.ProcessName = procInfo.ProcessName;
            existing.DisplayName = entry.DisplayName;
            existing.Uid = entry.Uid;
            existing.IconGuid = entry.IconGuid;
            existing.IconSnapshot = entry.IconSnapshot;
            existing.OnSubTitleRefresh();
            return true;
        }

        var item = new TrayIconItem
        {
            SubKeyName = entry.SubKeyName,
            ExecutablePath = entry.ExecutablePath,
            Uid = entry.Uid,
            IconGuid = entry.IconGuid,
            IconSnapshot = entry.IconSnapshot,
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
        return true;
    }

    /// <summary>新增或更新一个"已隐藏备份"对应的列表条目（子键可能已被删）。</summary>
    private void UpsertBackup(string subKeyName, TrayIconEntry backup, Process procInfo)
    {
        var existing = Icons.FirstOrDefault(i =>
            string.Equals(i.SubKeyName, subKeyName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var procChanged = existing.ProcessId != procInfo.Id;
            if ((existing.Hwnd == IntPtr.Zero || procChanged) && backup.Uid != null)
            {
                existing.Hwnd = TrayIconController.FindTrayWindow(procInfo.ProcessName, backup.Uid.Value, backup.ToolTip);
            }
            existing.IsHidden = true;
            existing.IsRunning = true;
            existing.ProcessId = procInfo.Id;
            existing.ProcessName = procInfo.ProcessName;
            existing.Uid = backup.Uid;
            existing.IconSnapshot = backup.IconSnapshot;
            existing.WindowClassName = backup.WindowClassName;
            existing.OnSubTitleRefresh();
            return;
        }

        var hwnd = backup.Uid != null
            ? TrayIconController.FindTrayWindow(procInfo.ProcessName, backup.Uid.Value, backup.ToolTip)
            : IntPtr.Zero;

        var item = new TrayIconItem
        {
            SubKeyName = subKeyName,
            ExecutablePath = backup.ExecutablePath,
            Uid = backup.Uid,
            IconGuid = backup.IconGuid,
            IconSnapshot = backup.IconSnapshot,
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

        item.Icon = IconExtractor.FromExecutable(backup.ExecutablePath);
        if (item.Icon == null && backup.IconSnapshot is { Length: > 0 })
        {
            item.Icon = IconExtractor.FromSnapshotBytes(backup.IconSnapshot);
        }

        Icons.Add(item);
    }

    /// <summary>
    /// 建立 可执行文件路径 → 进程 的索引，用于判断图标对应的程序是否在运行。
    /// 若主模块路径读取失败（权限不足），回退用进程名做键。
    /// </summary>
    private static Dictionary<string, Process> BuildRunningProcessIndex()
    {
        var map = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    if (!map.ContainsKey(path))
                    {
                        map[path] = process;
                    }
                    else
                    {
                        process.Dispose();
                    }
                    continue;
                }
            }
            catch
            {
                // 主模块读取失败，回退到进程名
            }

            try
            {
                var name = process.ProcessName;
                if (!map.ContainsKey(name))
                {
                    map[name] = process;
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return map;
    }

    /// <summary>按可执行路径（精确）或进程名（回退）在索引中查找进程。</summary>
    private static bool TryFindProcess(Dictionary<string, Process> map, string executablePath, out Process? procInfo)
    {
        if (map.TryGetValue(executablePath, out procInfo))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        return map.TryGetValue(name, out procInfo);
    }

    private void SortIcons()
    {
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

    // ---------------------------------------------------------------- 隐藏 / 显示

    /// <summary>对当前勾选项执行隐藏。操作完成后清空勾选。</summary>
    public void ApplySelected()
    {
        var targets = Icons.Where(i => i.IsSelected && !i.IsHidden).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先勾选要隐藏的图标";
            return;
        }

        var success = 0;
        foreach (var item in targets)
        {
            if (HideItem(item)) success++;
        }

        foreach (var item in targets)
        {
            item.IsSelected = false;
        }

        Persist();
        UpdateCounters();
        StatusText = $"已隐藏 {success} 个图标，对应程序仍在后台正常运行";
    }

    /// <summary>彻底隐藏单个图标：NIM_DELETE（图标立即从托盘与溢出面板消失）。</summary>
    private bool HideItem(TrayIconItem item)
    {
        if (item.IsHidden) return true;

        if (item.Hwnd == IntPtr.Zero || item.Uid == null)
        {
            StatusText = $"隐藏 {item.DisplayName} 失败：无法定位图标";
            return false;
        }

        // 记录恢复信息（含窗口类名，供重启后按类名重新定位窗口）
        var windowClassName = TrayIconController.GetWindowClassName(item.Hwnd);
        _hiddenBackups[item.SubKeyName] = new TrayIconEntry
        {
            SubKeyName = item.SubKeyName,
            ExecutablePath = item.ExecutablePath,
            Uid = item.Uid,
            IconGuid = item.IconGuid,
            IconSnapshot = item.IconSnapshot,
            Publisher = item.Publisher,
            ToolTip = item.DisplayName,
            WindowClassName = windowClassName
        };

        if (!TrayIconController.HideIcon(item.Hwnd, item.Uid.Value))
        {
            _hiddenBackups.Remove(item.SubKeyName);
            StatusText = $"隐藏 {item.DisplayName} 失败";
            return false;
        }

        item.WindowClassName = windowClassName;
        item.IsHidden = true;
        return true;
    }

    /// <summary>恢复单个图标：NIM_ADD（用备份的图标字节重建）。</summary>
    private bool ShowItem(TrayIconItem item)
    {
        if (!item.IsHidden) return true;

        var hwnd = item.Hwnd;
        if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(item.ProcessName))
        {
            // 1) 先探测（图标可能仍存在，例如程序已重新 NIM_ADD）
            if (item.Uid != null)
            {
                hwnd = TrayIconController.FindTrayWindow(item.ProcessName, item.Uid.Value, item.DisplayName);
            }

            // 2) 探测失败（图标已被 NIM_DELETE 删除）→ 按隐藏前记录的窗口类名重新定位
            if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(item.WindowClassName))
            {
                hwnd = TrayIconController.FindWindowByClass(item.ProcessName, item.WindowClassName);
            }
        }

        if (hwnd == IntPtr.Zero || item.Uid == null)
        {
            StatusText = $"显示 {item.DisplayName} 失败：无法定位图标";
            return false;
        }

        if (!TrayIconController.ShowIcon(hwnd, item.Uid.Value, item.IconSnapshot, item.DisplayName))
        {
            StatusText = $"显示 {item.DisplayName} 失败";
            return false;
        }

        item.Hwnd = hwnd;
        _hiddenBackups.Remove(item.SubKeyName);
        item.IsHidden = false;
        return true;
    }

    /// <summary>恢复所有被隐藏的图标。</summary>
    public void RestoreAll()
    {
        var hidden = Icons.Where(i => i.IsHidden).ToList();
        if (hidden.Count == 0)
        {
            StatusText = "当前没有被隐藏的图标";
            return;
        }

        var success = 0;
        foreach (var item in hidden)
        {
            if (ShowItem(item)) success++;
        }

        Persist();
        UpdateCounters();
        StatusText = $"已恢复 {success} 个图标到任务栏托盘";
    }

    /// <summary>恢复（显示）当前勾选的已隐藏图标。操作完成后清空勾选。</summary>
    public void RestoreSelected()
    {
        var targets = Icons.Where(i => i.IsSelected && i.IsHidden).ToList();
        if (targets.Count == 0)
        {
            StatusText = "请先勾选要显示的图标";
            return;
        }

        var success = 0;
        foreach (var item in targets)
        {
            if (ShowItem(item)) success++;
        }

        foreach (var item in targets)
        {
            item.IsSelected = false;
        }

        Persist();
        UpdateCounters();
        StatusText = $"已恢复 {success} 个图标到任务栏托盘";
    }

    /// <summary>隐藏当前列表中的全部图标。</summary>
    public void HideAll()
    {
        var success = 0;
        var total = 0;

        foreach (var item in Icons)
        {
            if (item.IsHidden) continue;
            total++;
            if (HideItem(item)) success++;
        }

        Persist();
        UpdateCounters();
        StatusText = total == 0
            ? "所有图标都已处于隐藏状态"
            : $"已隐藏全部 {success} 个图标";
    }

    /// <summary>切换单个图标的隐藏状态（双击）。</summary>
    public void ToggleItem(TrayIconItem item)
    {
        if (item.IsHidden)
        {
            ShowItem(item);
        }
        else
        {
            HideItem(item);
        }

        Persist();
        UpdateCounters();
    }

    // ---------------------------------------------------------------- 选择辅助

    public void SelectAll()
    {
        foreach (var item in Icons) item.IsSelected = true;
        UpdateCounters();
    }

    public void InvertSelection()
    {
        foreach (var item in Icons) item.IsSelected = !item.IsSelected;
        UpdateCounters();
    }

    public void ClearSelection()
    {
        foreach (var item in Icons) item.IsSelected = false;
        UpdateCounters();
    }

    // ---------------------------------------------------------------- 辅助

    public void UpdateCounters()
    {
        SelectedCount = Icons.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasIcons));
    }

    private void Persist()
    {
        _config.HiddenBackups = new Dictionary<string, TrayIconEntry>(
            _hiddenBackups, StringComparer.OrdinalIgnoreCase);
        _config.Save();
    }

    public void Dispose() => Stop();

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

internal static class TrayIconItemExtensions
{
    /// <summary>进程状态变化后刷新次级文案。</summary>
    public static void OnSubTitleRefresh(this TrayIconItem item)
        => item.RaiseSubTitleChanged();
}
