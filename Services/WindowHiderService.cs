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

    /// <summary>托盘图标的右键菜单。NotifyIcon 与 WinForms 都不负责释放它，必须由本服务 Dispose。</summary>
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
///   · 先建托盘图标再隐藏窗口：图标建不出来就放弃隐藏，绝不让用户失去恢复入口；
///   · 右键菜单沿用 NotifyIcon.ContextMenuStrip 的默认弹出路径（与 App 自身托盘菜单保持同一口径），
///     只在 Opening 时做光标兜底，见 CreateTrayIcon 里的说明；
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
    /// 隐藏一个顶层窗口：记录位置/状态 → 创建专属托盘图标 → ShowWindowAsync(SW_HIDE)。
    /// 不结束进程，也不修改目标程序的任何文件。
    ///
    /// 顺序说明：**先建托盘图标、再隐藏窗口**。托盘图标是用户唯一"看得见"的恢复入口，
    /// 一旦它创建失败就绝不能把窗口藏起来（否则用户会以为程序被关掉了）；
    /// 反过来，图标建好后窗口却隐藏失败时，把图标销毁、窗口保持原样，_hidden 集合不写入，
    /// 三者的状态始终一致。返回 false 时保证窗口仍可见 / 仍保持原状态。
    /// </summary>
    public bool Hide(IntPtr hwnd, string title, string processName, string executablePath, int processId)
    {
        if (!WindowController.IsWindowAlive(hwnd)) return false;
        if (_hidden.ContainsKey(hwnd)) return true;

        var placement = WindowController.GetPlacement(hwnd);

        var record = new HiddenWindow
        {
            Hwnd = hwnd,
            Title = title,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            Placement = placement
        };

        // 没有恢复入口就不隐藏：托盘图标建不出来直接放弃，窗口保持原样
        if (!CreateTrayIcon(record))
        {
            DisposeTrayIcon(record);
            return false;
        }

        if (!WindowController.Hide(hwnd))
        {
            // 窗口其实没被藏起来（例如查询与隐藏之间被关闭 / 被别的程序最小化）→ 撤掉刚建的图标
            DisposeTrayIcon(record);
            return false;
        }

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

    /// <summary>
    /// 为被隐藏的窗口创建专属托盘图标。
    ///
    /// 返回是否创建成功 —— 失败时调用方（<see cref="Hide"/>）必须放弃隐藏，
    /// 否则窗口会处于"看不见又找不到"的状态。失败时不做清理，由调用方统一 DisposeTrayIcon，
    /// 这样记录里已经挂上的半成品（图标 / 菜单 / Icon）不会泄漏。
    ///
    /// 右键菜单保持 NotifyIcon.ContextMenuStrip 的默认弹出路径（与 App 自身托盘菜单一致）：
    /// WinForms 用弹出瞬间的光标位置定位菜单，这里只在 Opening 做一次光标兜底，
    /// 光标坐标不可信时不至于把菜单丢到屏幕角落。
    /// </summary>
    private bool CreateTrayIcon(HiddenWindow record)
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
            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false
            };
            menu.Items.Add("显示窗口", null, (_, _) => RestoreRequested?.Invoke(this, hwnd));
            menu.Items.Add("关闭窗口", null, (_, _) => CloseRequested?.Invoke(this, hwnd));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("显示全部窗口", null, (_, _) => RestoreAllRequested?.Invoke(this, EventArgs.Empty));

            // 菜单位置兜底：WinForms 弹出菜单时用当前光标位置定位；
            // 万一这一刻光标坐标不可信（例如由键盘 / 自动化触发，或多屏坐标异常），
            // 把光标拉回主屏工作区右下角，避免菜单出现在屏幕角落之类的"错误位置"。
            menu.Opening += (_, _) =>
            {
                var point = Forms.Cursor.Position;
                if (!IsPointOnAnyScreen(point))
                {
                    Forms.Cursor.Position = DefaultTrayMenuPoint();
                }
            };

            var notifyIcon = new Forms.NotifyIcon
            {
                Text = tooltip,
                Icon = icon,
                ContextMenuStrip = menu
            };

            record.TrayIcon = notifyIcon;
            record.TrayMenu = menu;

            // 左键单击 / 双击恢复窗口；右键交给 ContextMenuStrip 自动弹菜单，不触发恢复
            notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) RestoreRequested?.Invoke(this, hwnd);
            };
            notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, hwnd);

            notifyIcon.Visible = true;
            return true;
        }
        catch
        {
            // 半成品由调用方 DisposeTrayIcon 清理
            return false;
        }
    }

    /// <summary>坐标是否落在任一屏幕范围内（用于判断拿到的光标位置是否可信）。</summary>
    private static bool IsPointOnAnyScreen(Point point)
    {
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                if (screen.Bounds.Contains(point)) return true;
            }
        }
        catch
        {
            // 枚举屏幕失败时按"在屏幕内"处理，不要无谓地跳到角落
            return true;
        }

        return false;
    }

    /// <summary>光标坐标不可信时的兜底菜单位置：主屏工作区右下角（任务栏托盘一侧）。</summary>
    private static Point DefaultTrayMenuPoint()
    {
        var workArea = Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        return new Point(workArea.Right - 12, workArea.Bottom - 12);
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
                // 菜单可能正开着（用户点了菜单项 → 恢复窗口 → 这里销毁）：
                // 先关掉再释放，避免在 ToolStrip 自己的消息处理过程中销毁可见窗口。
                if (record.TrayMenu.Visible) record.TrayMenu.Close();
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
