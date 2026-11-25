using System.Drawing;
using System.IO;
using TrayHider.Interop;
using Forms = System.Windows.Forms;

namespace TrayHider.Services;

/// <summary>
/// 一个被隐藏窗口的记录：隐藏前的位置/状态 + 该窗口专属的托盘图标。
/// </summary>
internal sealed class HiddenWindow
{
    /// <summary>被隐藏的顶层窗口句柄。</summary>
    public IntPtr Hwnd { get; init; }

    /// <summary>隐藏时的窗口标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>所属进程名（不含扩展名）。</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>所属进程可执行文件完整路径。</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>所属进程 ID。</summary>
    public int ProcessId { get; init; }

    /// <summary>隐藏前记录的位置与显示状态，恢复时原样还原。</summary>
    public WindowPlacement Placement { get; init; }

    /// <summary>本窗口专属的托盘图标（左键单击恢复该窗口）。</summary>
    public Forms.NotifyIcon? TrayIcon { get; set; }

    /// <summary>托盘图标的右键菜单。NotifyIcon 不负责释放它，必须自己 Dispose。</summary>
    public Forms.ContextMenuStrip? TrayMenu { get; set; }

    /// <summary>需要由本服务释放的图标（从窗口图标复制 / 从可执行文件提取的那一份）。</summary>
    public Icon? OwnedIcon { get; set; }

    /// <summary>记录创建时刻，用于延时校验"隐藏是否真的生效"。</summary>
    public DateTime HiddenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 窗口隐藏服务：维护"被隐藏窗口"的集合，并为每个被隐藏的窗口创建、销毁一个专属托盘图标。
///
/// 设计要点：
///   · 集合以窗口句柄为键，重复隐藏不会产生第二个图标；
///   · 托盘图标的创建/销毁全部收口在这里，Dispose 之后不残留任何悬挂引用；
///   · 托盘图标的点击与右键菜单只抛事件（RestoreRequested / CloseRequested / RestoreAllRequested），
///     真正的恢复动作（含"把该程序的托盘图标还回去"）由视图模型统一编排。
/// </summary>
internal sealed class WindowHiderService : IDisposable
{
    private readonly Dictionary<IntPtr, HiddenWindow> _hidden = new();

    /// <summary>用户在托盘图标上左键单击 / 双击，请求恢复该窗口。</summary>
    public event EventHandler<IntPtr>? RestoreRequested;

    /// <summary>用户在托盘图标右键菜单里选择"关闭窗口"。</summary>
    public event EventHandler<IntPtr>? CloseRequested;

    /// <summary>用户在托盘图标右键菜单里选择"显示全部窗口"。</summary>
    public event EventHandler? RestoreAllRequested;

    /// <summary>当前被隐藏的窗口数量。</summary>
    public int Count => _hidden.Count;

    /// <summary>当前被隐藏的窗口记录（只读视图）。</summary>
    public IReadOnlyCollection<HiddenWindow> Items => _hidden.Values;

    /// <summary>该窗口是否已被本软件隐藏。</summary>
    public bool IsHidden(IntPtr hwnd) => _hidden.ContainsKey(hwnd);

    /// <summary>按句柄取隐藏记录，未隐藏时返回 null。</summary>
    public HiddenWindow? Find(IntPtr hwnd) => _hidden.TryGetValue(hwnd, out var record) ? record : null;

    // ---------------------------------------------------------------- 隐藏 / 恢复

    /// <summary>
    /// 隐藏一个顶层窗口：记录位置/状态 → ShowWindowAsync(SW_HIDE) → 创建专属托盘图标。
    /// 不结束进程，也不修改目标程序的任何文件。
    /// </summary>
    public bool Hide(IntPtr hwnd, string title, string processName, string executablePath, int processId)
    {
        if (!WindowController.IsWindowAlive(hwnd)) return false;
        if (_hidden.ContainsKey(hwnd)) return true;

        var placement = WindowController.GetPlacement(hwnd);
        if (!WindowController.Hide(hwnd)) return false;

        var record = new HiddenWindow
        {
            Hwnd = hwnd,
            Title = title,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            Placement = placement
        };

        CreateTrayIcon(record);
        _hidden[hwnd] = record;
        return true;
    }

    /// <summary>恢复一个被隐藏的窗口，并销毁它的托盘图标。</summary>
    public bool Restore(IntPtr hwnd)
    {
        if (!_hidden.Remove(hwnd, out var record)) return false;

        DisposeTrayIcon(record);
        return WindowController.Restore(record.Hwnd, record.Placement);
    }

    /// <summary>恢复全部被隐藏的窗口，返回恢复成功数。</summary>
    public int RestoreAll()
    {
        var records = _hidden.Values.ToList();
        _hidden.Clear();

        var restored = 0;
        foreach (var record in records)
        {
            DisposeTrayIcon(record);
            if (WindowController.Restore(record.Hwnd, record.Placement)) restored++;
        }

        return restored;
    }

    /// <summary>
    /// 清理已经失效的记录：窗口句柄没了说明目标程序自己退出了，
    /// 此时只需销毁托盘图标，不需要（也无法）恢复窗口。
    /// </summary>
    public List<HiddenWindow> Prune()
    {
        var removed = new List<HiddenWindow>();

        foreach (var record in _hidden.Values.ToList())
        {
            if (WindowController.IsWindowAlive(record.Hwnd)) continue;

            _hidden.Remove(record.Hwnd);
            DisposeTrayIcon(record);
            removed.Add(record);
        }

        return removed;
    }

    /// <summary>
    /// 撤销一条隐藏记录但不恢复窗口。
    /// 用于"其实没隐藏成功"（例如目标是更高权限进程、隐藏被 UIPI 丢弃）的自愈场景。
    /// </summary>
    public bool Discard(IntPtr hwnd)
    {
        if (!_hidden.Remove(hwnd, out var record)) return false;

        DisposeTrayIcon(record);
        return true;
    }

    // ---------------------------------------------------------------- 托盘图标

    private void CreateTrayIcon(HiddenWindow record)
    {
        try
        {
            var (icon, ownsIcon) = LoadTrayIcon(record.Hwnd, record.ExecutablePath);
            if (ownsIcon) record.OwnedIcon = icon;

            var tooltip = string.IsNullOrWhiteSpace(record.Title) ? record.ProcessName : record.Title;
            if (string.IsNullOrWhiteSpace(tooltip)) tooltip = "已隐藏的窗口";
            // NotifyIcon.Text 有长度上限，超出会被 WinForms 直接拒绝
            if (tooltip.Length > 60) tooltip = tooltip.Substring(0, 60) + "...";

            var hwnd = record.Hwnd;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (_, _) => RestoreRequested?.Invoke(this, hwnd));
            menu.Items.Add("关闭窗口", null, (_, _) => CloseRequested?.Invoke(this, hwnd));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("显示全部窗口", null, (_, _) => RestoreAllRequested?.Invoke(this, EventArgs.Empty));

            var notifyIcon = new Forms.NotifyIcon
            {
                Text = tooltip,
                Icon = icon,
                ContextMenuStrip = menu,
                Visible = true
            };

            // 左键单击与双击都直接恢复窗口（双击时先收到一次单击，恢复是幂等的）
            notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) RestoreRequested?.Invoke(this, hwnd);
            };
            notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, hwnd);

            record.TrayIcon = notifyIcon;
            record.TrayMenu = menu;
        }
        catch
        {
            // 托盘图标创建失败不影响窗口隐藏本身，只是少一个恢复入口（列表里仍可恢复）
        }
    }

    private static void DisposeTrayIcon(HiddenWindow record)
    {
        try
        {
            if (record.TrayIcon != null)
            {
                record.TrayIcon.Visible = false;
                record.TrayIcon.Dispose();
                record.TrayIcon = null;
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (record.TrayMenu != null)
            {
                record.TrayMenu.Dispose();
                record.TrayMenu = null;
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (record.OwnedIcon != null)
            {
                record.OwnedIcon.Dispose();
                record.OwnedIcon = null;
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 取托盘图标：优先窗口自身图标（复制一份自己持有，避免目标程序销毁 HICON 后失效），
    /// 其次可执行文件图标，最后系统默认图标。
    /// </summary>
    private static (Icon Icon, bool OwnsIcon) LoadTrayIcon(IntPtr hwnd, string executablePath)
    {
        var iconHandle = WindowController.TryGetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            try
            {
                using var borrowed = Icon.FromHandle(iconHandle);
                return ((Icon)borrowed.Clone(), true);
            }
            catch
            {
                // 复制失败则继续回退
            }
        }

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted != null) return (extracted, true);
            }
            catch
            {
                // 忽略，回退到系统默认图标
            }
        }

        return (SystemIcons.Application, false);
    }

    // ---------------------------------------------------------------- 释放

    /// <summary>销毁全部托盘图标（不恢复窗口，恢复由调用方负责）。</summary>
    public void Dispose()
    {
        foreach (var record in _hidden.Values.ToList())
        {
            DisposeTrayIcon(record);
        }

        _hidden.Clear();
    }
}
