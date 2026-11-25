using System.Runtime.InteropServices;

namespace TrayHider.Interop;

/// <summary>
/// Win32 互操作声明。
///
/// 说明：本软件在 Windows 11 上通过注册表管理托盘图标可见性，
/// 因此这里只保留窗口外观（圆角/云母/主题）、全局热键与 Shell 通知所需的 API。
/// 早期版本依赖的工具栏消息（TB_GETBUTTON 等）在 Win11 上已不可用，相关声明已移除。
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- Shell 通知

    /// <summary>通知 Shell 配置已变更，促使其刷新任务栏。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;

    // ---------------------------------------------------------------- 窗口查找 / 枚举

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    /// <summary>枚举子窗口（按父窗口过滤，<paramref name="hWndParent"/> 为 null 时枚举全部顶级窗口）。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>按 Z 序枚举所有顶级窗口（不含消息专用窗口）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>枚举指定窗口的全部子窗口（递归）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>窗口枚举回调。<c>return true</c> 表示继续枚举。</summary>
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 消息专用窗口的父句柄。这类窗口不会出现在 EnumWindows 的遍历结果里
    /// （.NET 的 NotifyIcon、大量 Electron 应用的托盘窗口都是这类窗口），
    /// 只能用 FindWindowEx(HWND_MESSAGE, ...) 逐个挂出来。
    /// </summary>
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---------------------------------------------------------------- 消息投递

    /// <summary>
    /// 注册一条系统级窗口消息，返回它在本会话中的唯一 ID（失败返回 0）。
    /// 同一条名字在所有进程中拿到同一个 ID，因此可以跨进程投递。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string lpString);

    /// <summary>
    /// 向指定窗口投递消息（异步入队，不等待目标处理完）。
    /// 注意：跨完整性级别（普通进程 → 提权进程）会被 UIPI 拦掉并返回 false。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 发送窗口消息并等待对端处理结果。用于"请已运行的实例把主窗口唤出来"这类需要回执的跨进程通知：
    /// 对端在 WndProc 里处理并返回一个魔数，发送方据此判断"真的有人接管了"，
    /// 而不是盲目退出（也就不用回退到"已经在运行中"的提示框）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    // ---------------------------------------------------------------- 全局热键

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    // ---------------------------------------------------------------- DWM（圆角 / 云母）

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int leftWidth;
        public int rightWidth;
        public int topHeight;
        public int bottomHeight;
    }

    // ---------------------------------------------------------------- 进程内存

    /// <summary>
    /// 当前进程的伪句柄（kernel32!GetCurrentProcess 的返回值）。
    /// SetProcessWorkingSetSize 用它即可，不需要 OpenProcess / GetCurrentProcess。
    /// </summary>
    internal static readonly IntPtr CurrentProcessPseudoHandle = new(-1);

    /// <summary>
    /// 裁剪进程工作集：把最近没有访问的物理页换出到页面文件，调用后"任务管理器的内存"会立刻下降。
    ///
    /// 重要边界：**它不减少私有字节/提交量**，被换出的页面在下次访问时还会读回来，
    /// 所以这只影响"常驻物理内存"的观感，不等于真的省了内存。
    /// 传 -1/-1 表示由系统决定最小/最大工作集。
    /// </summary>
    /// <param name="process">进程句柄；当前进程用 <see cref="CurrentProcessPseudoHandle"/>。</param>
    /// <param name="minimumWorkingSetSize">最小工作集字节数；传 -1 表示由系统决定。</param>
    /// <param name="maximumWorkingSetSize">最大工作集字节数；传 -1 表示由系统决定。</param>
    /// <returns>成功返回 true；失败返回 false，错误码由 <see cref="System.Runtime.InteropServices.Marshal.GetLastWin32Error"/> 取得。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
