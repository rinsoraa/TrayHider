using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TrayHider.Services;

/// <summary>
/// 托盘弹出菜单的一项。分隔符用 <see cref="Separator"/>；<see cref="CommandId"/> 由调用方定义，
/// 菜单返回时原样带回（0 保留表示"取消"）。
/// </summary>
internal sealed class TrayPopupMenuItem
{
    /// <summary>菜单项文字。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>命令标识；菜单关闭后由 <see cref="TrayPopupMenu.Show(System.Collections.Generic.IReadOnlyList{TrayPopupMenuItem})"/> 返回。</summary>
    public int CommandId { get; init; }

    /// <summary>是否为分隔线。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>是否可用（false 时显示为灰色且不可选）。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>是否为默认项（加粗显示）。</summary>
    public bool IsDefault { get; init; }

    /// <summary>创建一条分隔线。</summary>
    public static TrayPopupMenuItem Separator => new() { IsSeparator = true };
}

/// <summary>
/// 托盘右键菜单 —— 原生 Win32 弹出菜单（TrackPopupMenuEx）。
///
/// 为什么不用 WinForms ContextMenuStrip：它的弹出位置由 WinForms 按"当时的光标位置"自行换算，
/// 进程在 PerMonitorV2 下还要经过 WinForms 的 DPI 缩放逻辑；跨不同缩放比例的显示器、
/// 或 shell 回调被延迟时光标已经移走，菜单就会落在错误位置。
///
/// 本实现：
///   1) 坐标全部是**物理像素**：进程是 PerMonitorV2（app.manifest 已声明），user32 的菜单同样按物理像素摆放，
///      全程不做任何 DPI 换算；
///   2) 锚点优先取 GetMessagePos（当前正在处理的那条 shell 回调消息对应的屏幕位置），
///      不会被"消息延迟 + 之后的光标移动"带偏；取不到才退回 GetCursorPos，最后退到主屏工作区右下角。
///      **任何情况下都不移动用户光标**；
///   3) 弹出前把菜单宿主窗口挪到锚点所在显示器（SetWindowPos + SWP_NOACTIVATE），
///      让 TrackPopupMenuEx 按该显示器的 DPI 渲染菜单（跨缩放时字号/行高才正确），且不激活、不抢焦点；
///   4) 菜单宿主是一个隐藏窗口（WinForms NativeWindow，预定义 "STATIC" 类，DefWindowProc 处理消息），
///      只创建一次，退出时 <see cref="ReleaseOwnerWindow"/> 释放；
///   5) 弹出前 SetForegroundWindow(owner)、关闭后 PostMessage(owner, WM_NULL) —— 官方托盘菜单做法：
///      菜单能收键盘（方向键/回车/ESC），点别处能自动关闭。不使用 AttachThreadInput 去挂接用户前台线程；
///   6) TPM_RIGHTALIGN | TPM_BOTTOMALIGN：菜单右下角贴在锚点上（和系统托盘菜单一致），越界由系统收回屏幕内；
///   7) 每次弹出新建 HMENU、finally 里 DestroyMenu，不复用、不泄漏。
/// </summary>
internal static class TrayPopupMenu
{
    // ---------------------------------------------------------------- 常量

    /// <summary>预定义窗口类：无需自注册窗口类/自定义 WndProc，DefWindowProc 足够。</summary>
    private const string OwnerWindowClass = "STATIC";

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_DEFAULT = 0x1000;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const uint TPM_TOPALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint MONITOR_DEFAULTTONULL = 0x00000000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    private const int WM_NULL = 0x0000;

    /// <summary>兜底锚点离主屏工作区右下角的距离（像素）。</summary>
    private const int FallbackMargin = 12;

    // ---------------------------------------------------------------- 状态

    private static Forms.NativeWindow? _ownerWindow;

    // ---------------------------------------------------------------- 对外接口

    /// <summary>
    /// 在托盘图标被右击的位置弹出菜单，返回所选菜单项的 <see cref="TrayPopupMenuItem.CommandId"/>；
    /// 用户取消（ESC / 点空白）或菜单无法显示时返回 0。必须在创建 NotifyIcon 的同一个 UI 线程上调用。
    /// </summary>
    public static int Show(IReadOnlyList<TrayPopupMenuItem> items) => Show(items, null);

    /// <summary>便利重载：直接列菜单项。</summary>
    public static int Show(params TrayPopupMenuItem[] items) => Show(items, null);

    /// <summary>用显式物理像素坐标弹出菜单（测试 / 特殊场景用；正常调用不要传）。</summary>
    internal static int ShowAt(IReadOnlyList<TrayPopupMenuItem> items, Point screenPoint, bool anchorBottomRight = true)
        => Show(items, screenPoint, anchorBottomRight);

    private static int Show(IReadOnlyList<TrayPopupMenuItem> items, Point? overridePoint, bool anchorBottomRight = true)
    {
        if (items == null || items.Count == 0) return 0;

        var anchor = overridePoint ?? ResolveAnchorPoint(out _);
        if (!EnsureOwnerWindow()) return 0;

        // 先把宿主窗口挪到锚点所在显示器：菜单按 owner 所在显示器的 DPI 渲染，
        // SWP_NOACTIVATE 只挪位置，不激活、不抢焦点，用户无感知。
        PositionOwnerOnMonitor(anchor);

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;

        try
        {
            var commands = new List<int>(items.Count);
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                var itemFlags = MF_STRING;
                if (!item.IsEnabled) itemFlags |= MF_GRAYED;
                if (item.IsDefault) itemFlags |= MF_DEFAULT;

                // 菜单 id 用 1 开始的自增序号，返回后再映射回调用方的 CommandId
                commands.Add(item.CommandId);
                AppendMenu(menu, itemFlags, (IntPtr)commands.Count, item.Text);
            }

            // 官方托盘菜单做法：先让 owner 成为前台窗口，键盘才能操作菜单、点别处才会自动关闭
            TryBringOwnerToForeground();

            var trackFlags = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY;
            trackFlags |= anchorBottomRight ? (TPM_RIGHTALIGN | TPM_BOTTOMALIGN) : (TPM_LEFTALIGN | TPM_TOPALIGN);

            var selected = TrackPopupMenuEx(menu, trackFlags, anchor.X, anchor.Y, OwnerWindowHandle, IntPtr.Zero);

            // 收尾：给 owner 补一条 WM_NULL，清掉菜单遗留的鼠标捕获/前台状态
            PostMessage(OwnerWindowHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            return selected > 0 && selected <= commands.Count ? commands[selected - 1] : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ---------------------------------------------------------------- 锚点

    /// <summary>
    /// 计算菜单锚点（物理像素），并把来源写入 <paramref name="source"/> 便于诊断。
    /// 优先级：当前消息的屏幕位置（GetMessagePos）→ 当前光标（GetCursorPos）→ 主屏工作区右下角。
    /// 全程只读，不移动光标。
    /// </summary>
    internal static Point ResolveAnchorPoint(out string source)
    {
        // 1) 正在处理的那条消息的位置。对托盘右键来说，那就是 shell 在点击瞬间投递过来的回调，
        //    UI 线程卡顿导致消息延迟时，这个值仍然是"右击发生的位置"。
        var messagePoint = ParseScreenPoint(GetMessagePos());
        if (IsPointOnAnyMonitor(messagePoint))
        {
            source = "GetMessagePos";
            return messagePoint;
        }

        // 2) 消息位置不可用时退回当前光标（仍是物理像素，且不移动它）
        if (GetCursorPos(out var cursor) && IsPointOnAnyMonitor(new Point(cursor.X, cursor.Y)))
        {
            source = "GetCursorPos";
            return new Point(cursor.X, cursor.Y);
        }

        // 3) 都不可用：主屏工作区右下角（任务栏托盘那一侧）
        source = "FallbackPrimaryCorner";
        return GetPrimaryWorkAreaCorner();
    }

    /// <summary>把一个 DWORD 形式的屏幕位置解析成坐标；高位按有符号处理（负数副屏不会变成大正数）。</summary>
    internal static Point ParseScreenPoint(uint packedPosition)
    {
        // LOWORD 是 X、HIWORD 是 Y；两者都是有符号 16 位（副屏在主屏左侧/上方时为负数）
        var x = unchecked((short)(packedPosition & 0xFFFF));
        var y = unchecked((short)((packedPosition >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    /// <summary>坐标是否落在某个真实存在的显示器上（用 MonitorFromPoint 判断，兼容负坐标）。</summary>
    internal static bool IsPointOnAnyMonitor(Point point)
    {
        var native = new POINT { X = point.X, Y = point.Y };
        return MonitorFromPoint(native, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>主屏（默认显示器）工作区右下角，用作最后兜底锚点。</summary>
    internal static Point GetPrimaryWorkAreaCorner()
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return new Point(info.rcWork.Right - FallbackMargin, info.rcWork.Bottom - FallbackMargin);
                }
            }
        }
        catch
        {
            // 忽略，走下面的兜底
        }

        return new Point(1024 - FallbackMargin, 768 - FallbackMargin);
    }

    // ---------------------------------------------------------------- 菜单宿主窗口

    /// <summary>菜单宿主窗口句柄（测试 / 诊断用；未创建时为 0）。</summary>
    internal static IntPtr OwnerWindowHandle => _ownerWindow?.Handle ?? IntPtr.Zero;

    /// <summary>菜单宿主窗口当前所在的显示器 DPI（测试 / 诊断用；未创建时为 0）。</summary>
    internal static uint OwnerDpi
    {
        get
        {
            var handle = OwnerWindowHandle;
            if (handle == IntPtr.Zero) return 0;

            try
            {
                return GetDpiForWindow(handle);
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 确保承载菜单的隐藏宿主窗口已创建（UI 线程，惰性创建一次）。
    /// 用 WinForms NativeWindow + 预定义 "STATIC" 类：不需要自注册窗口类、不需要自己写 WndProc，
    /// 消息由 DefWindowProc 处理即可。
    /// </summary>
    internal static bool EnsureOwnerWindow()
    {
        var existing = _ownerWindow;
        if (existing != null && existing.Handle != IntPtr.Zero && IsWindow(existing.Handle)) return true;

        try
        {
            var window = new Forms.NativeWindow();
            window.CreateHandle(new Forms.CreateParams
            {
                Caption = "TrayHider.TrayPopupMenuOwner",
                ClassName = OwnerWindowClass,
                Style = WS_POPUP,
                ExStyle = WS_EX_TOOLWINDOW,
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1
            });

            _ownerWindow = window;
            return window.Handle != IntPtr.Zero;
        }
        catch
        {
            _ownerWindow = null;
            return false;
        }
    }

    /// <summary>把宿主窗口挪到锚点所在显示器（只改位置，不激活、不改尺寸、不改 Z 序）。</summary>
    internal static void PositionOwnerOnMonitor(Point anchor)
    {
        var handle = OwnerWindowHandle;
        if (handle == IntPtr.Zero) return;

        try
        {
            SetWindowPos(handle, IntPtr.Zero, anchor.X, anchor.Y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch
        {
            // 挪不动也不影响菜单显示，只是菜单可能按旧显示器 DPI 渲染
        }
    }

    /// <summary>
    /// 把宿主窗口设为前台窗口，让菜单能接收键盘输入、并能在点击别处时关闭。
    /// 官方托盘菜单做法只用 SetForegroundWindow + 关闭后的 WM_NULL；
    /// 不去 AttachThreadInput 挂接用户的前台线程（那会干扰其它程序的消息队列）。
    /// </summary>
    internal static bool TryBringOwnerToForeground()
    {
        var handle = OwnerWindowHandle;
        if (handle == IntPtr.Zero) return false;
        if (GetForegroundWindow() == handle) return true;

        try
        {
            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>释放宿主窗口（程序退出时调用；之后再 Show 会重新创建）。</summary>
    public static void ReleaseOwnerWindow()
    {
        var window = _ownerWindow;
        _ownerWindow = null;
        if (window == null) return;

        try
        {
            window.DestroyHandle();
        }
        catch
        {
            // 退出路径不抛异常
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetMessagePos();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr itemId, string? itemText);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr ownerWindow, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
