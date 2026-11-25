using System.Drawing;
using System.IO;
using TrayHider.Interop;
using TrayHider.Models;

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

    /// <summary>本窗口专属的托盘图标（左键单击恢复该窗口，右键弹原生菜单）。</summary>
    public Forms.NotifyIcon? TrayIcon { get; set; }

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
///   · 右键菜单用原生 TrackPopupMenuEx（TrayPopupMenu），按右击瞬间的**物理像素**坐标定位，
///     不经过 WinForms 的 DPI 换算、也不移动用户光标，多显示器/混合缩放时不会漂；
///   · 托盘图标的点击与右键菜单只抛事件（RestoreRequested / CloseRequested / RestoreAllRequested），
///     真正的恢复动作（含"把该程序的托盘图标还回去"）由视图模型统一编排。
/// </summary>
internal sealed class WindowHiderService : IDisposable
{
    /// <summary>右键菜单命令：显示（恢复）该窗口。</summary>
    private const int MenuCommandShowWindow = 1;

    /// <summary>右键菜单命令：关闭该窗口。</summary>
    private const int MenuCommandCloseWindow = 2;

    /// <summary>右键菜单命令：显示全部窗口。</summary>
    private const int MenuCommandShowAllWindows = 3;

    private readonly Dictionary<IntPtr, HiddenWindow> _hidden = new();

    public WindowHiderService()
    {
        // 右键菜单是每次右键**新建**的，文本在弹出时现取即可；
        // 只有常驻的托盘图标 tooltip 需要在语言切换时刷一遍。
        Loc.LanguageChanged += OnLanguageChanged;
    }

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
    /// 这样记录里已经挂上的半成品（图标 / Icon）不会泄漏。
    ///
    /// 右键菜单不用 NotifyIcon.ContextMenuStrip（位置会被 WinForms 按当时光标做一次 DPI 换算，
    /// 跨缩放显示器时容易漂），改为在右键 MouseUp 里调用 <see cref="TrayPopupMenu"/> 原生弹出，
    /// 见 <see cref="ShowTrayPopupMenu"/>。
    /// </summary>
    private bool CreateTrayIcon(HiddenWindow record)
    {
        try
        {
            var (icon, ownsIcon) = LoadTrayIcon(record.Hwnd, record.ExecutablePath);
            if (ownsIcon) record.OwnedIcon = icon;

            var tooltip = BuildTooltip(record);

            var hwnd = record.Hwnd;
            var notifyIcon = new Forms.NotifyIcon
            {
                Text = tooltip,
                Icon = icon,
                // 不使用 WinForms 自带的自动弹菜单：位置与 DPI 换算都不可控
                ContextMenuStrip = null
            };

            record.TrayIcon = notifyIcon;

            // 左键单击 / 双击恢复窗口；右键弹原生菜单（不触发恢复）
            notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) RestoreRequested?.Invoke(this, hwnd);
            };
            notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, hwnd);

            // 右键：必须在 MouseUp 里同步弹菜单 —— 此刻"当前消息"就是 shell 投递的右键回调，
            // TrayPopupMenu 用 GetMessagePos 才能取到右击瞬间的物理像素坐标。
            notifyIcon.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) ShowTrayPopupMenu(hwnd);
            };

            notifyIcon.Visible = true;
            return true;
        }
        catch
        {
            // 半成品由调用方 DisposeTrayIcon 清理
            return false;
        }
    }

    /// <summary>生成托盘图标提示文本（标题优先，退化到进程名，最后是本地化兜底文案）。</summary>
    private static string BuildTooltip(HiddenWindow record)
    {
        var tooltip = string.IsNullOrWhiteSpace(record.Title) ? record.ProcessName : record.Title;
        if (string.IsNullOrWhiteSpace(tooltip)) tooltip = CoreText.T("Core.Tray.TooltipFallback");

        // NotifyIcon.Text 有长度上限，超出会被 WinForms 直接拒绝
        return tooltip.Length > 60 ? tooltip.Substring(0, 60) + "..." : tooltip;
    }

    /// <summary>语言切换：把已有托盘图标上的提示文本换成新语言。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshTooltips();

    private void RefreshTooltips()
    {
        foreach (var record in _hidden.Values)
        {
            var icon = record.TrayIcon;
            if (icon == null) continue;

            try
            {
                var text = BuildTooltip(record);
                if (!string.Equals(icon.Text, text, StringComparison.Ordinal))
                {
                    icon.Text = text;
                }
            }
            catch
            {
                // 图标正在释放时改 Text 会抛；这里不补救
            }
        }
    }

    /// <summary>
    /// 在该隐藏窗口的托盘图标位置弹出原生右键菜单，并按选择分派命令。
    /// 菜单项与行为保持不变：显示窗口 / 关闭窗口 / 显示全部窗口。
    /// </summary>
    private void ShowTrayPopupMenu(IntPtr hwnd)
    {
        var command = TrayPopupMenu.Show(
            new TrayPopupMenuItem { Text = CoreText.T("Core.Menu.ShowWindow"), CommandId = MenuCommandShowWindow },
            new TrayPopupMenuItem { Text = CoreText.T("Core.Menu.CloseWindow"), CommandId = MenuCommandCloseWindow },
            TrayPopupMenuItem.Separator,
            new TrayPopupMenuItem { Text = CoreText.T("Core.Menu.ShowAllWindows"), CommandId = MenuCommandShowAllWindows });

        switch (command)
        {
            case MenuCommandShowWindow:
                RestoreRequested?.Invoke(this, hwnd);
                break;

            case MenuCommandCloseWindow:
                CloseRequested?.Invoke(this, hwnd);
                break;

            case MenuCommandShowAllWindows:
                RestoreAllRequested?.Invoke(this, EventArgs.Empty);
                break;
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
        Loc.LanguageChanged -= OnLanguageChanged;

        foreach (var record in _hidden.Values.ToList())
        {
            DisposeTrayIcon(record);
        }

        _hidden.Clear();
    }
}
