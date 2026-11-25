using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayHider.Services;

namespace TrayHider.Interop;

/// <summary>枚举得到的一个"可以隐藏到托盘"的顶层窗口。</summary>
internal sealed class WindowDescriptor
{
    /// <summary>顶层窗口句柄。</summary>
    public IntPtr Hwnd { get; init; }

    /// <summary>窗口标题（GetWindowText）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>窗口类名（ApplicationFrameWindow / CabinetWClass 等）。</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>所属进程名（不含扩展名）。</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>所属进程可执行文件完整路径（权限不足时为空）。</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>所属进程 ID。</summary>
    public int ProcessId { get; init; }
}

/// <summary>
/// 普通窗口的枚举 / 隐藏 / 恢复，全部基于 user32 的窗口消息，
/// **不结束进程、不修改目标程序的任何文件**。
///
/// 隐藏用 ShowWindowAsync(SW_HIDE)：异步投递，即便目标窗口卡死也不会把本软件拖住；
/// 隐藏前用 GetWindowPlacement 记下位置与显示状态，恢复时 SetWindowPlacement 原样还原。
/// </summary>
internal static class WindowController
{
    // ---------------------------------------------------------------- 常量

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private const int DWMWA_CLOAKED = 14;

    private const int SW_HIDE = 0;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMINIMIZED_LOW = 6;   // SW_MINIMIZE
    private const int SW_SHOW = 5;

    private const int WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private const int WM_CLOSE = 0x0010;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// 系统 Shell 自身的窗口类名。这些不是"用户应用窗口"：
    /// 桌面、任务栏、WorkerW、UWP 壳层等一律不出现在窗口列表里。
    /// </summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                                      // 桌面
        "WorkerW",                                      // 桌面（壁纸层）
        "Shell_TrayWnd",                                // 任务栏
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",                     // 溢出面板
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",                   // UWP 壳层（开始菜单 / 搜索等）
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Windows.Internal.Shell.TabProxyWindow",
        "XamlExplorerHostIslandWindow",
        "ApplicationManager_ImmersiveShellWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "Shell_InputSwitchTopLevelWindow",
        "EdgeUiInputTopWndClass",
        "LockScreenBackstopFrame",
        "Dwm"
    };

    // ---------------------------------------------------------------- 枚举

    /// <summary>
    /// 枚举当前所有"可以隐藏到托盘"的顶层窗口。
    ///
    /// 过滤规则：不可见、无标题、工具窗口（WS_EX_TOOLWINDOW）、
    /// DWM 已隐藏（cloaked，UWP 关闭后留下的空壳窗口就属于此类）、本进程窗口、
    /// 以及 Shell 自身的窗口（桌面 / 任务栏 / WorkerW / 开始菜单等）。
    /// UWP 的 ApplicationFrameWindow 只要有标题且没被 cloaked 就保留。
    /// </summary>
    public static List<WindowDescriptor> EnumerateHideableWindows()
    {
        var windows = new List<WindowDescriptor>();
        var selfProcessId = Environment.ProcessId;
        var processCache = new Dictionary<int, (string Name, string Path)>();

        bool Callback(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;

                var title = GetWindowTitle(hwnd);
                if (title.Length == 0) return true;

                if (IsCloaked(hwnd)) return true;

                var extendedStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((extendedStyle & WS_EX_TOOLWINDOW) != 0) return true;

                var className = GetWindowClassName(hwnd);
                if (ShellWindowClasses.Contains(className)) return true;

                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == 0 || processId == selfProcessId) return true;

                if (!processCache.TryGetValue(processId, out var processInfo))
                {
                    processInfo = GetProcessInfo(processId);
                    processCache[processId] = processInfo;
                }

                // 拿不到进程信息（权限不足 / 已退出）的窗口不展示，也无法可靠恢复
                if (processInfo.Name.Length == 0) return true;

                windows.Add(new WindowDescriptor
                {
                    Hwnd = hwnd,
                    Title = title,
                    ClassName = className,
                    ProcessName = processInfo.Name,
                    ExecutablePath = processInfo.Path,
                    ProcessId = processId
                });
            }
            catch
            {
                // 单个窗口探测失败不影响整体枚举
            }

            return true;
        }

        EnumWindowsProc callback = Callback;
        EnumWindows(callback, IntPtr.Zero);
        return windows;
    }

    // ---------------------------------------------------------------- 状态查询

    /// <summary>窗口句柄是否仍然有效（进程没退出、窗口没销毁）。</summary>
    public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>窗口当前是否可见（被我们隐藏后返回 false）。</summary>
    public static bool IsWindowVisibleNow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

    /// <summary>窗口是否属于本进程。</summary>
    public static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    /// <summary>取前台窗口句柄。</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>取窗口标题并去掉首尾空白。</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return string.Empty;

            var buffer = new StringBuilder(length + 1);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>取窗口类名。</summary>
    public static string GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>窗口是否被 DWM 隐藏（cloaked）。UWP 程序关闭后窗口往往只是被 cloak，并未销毁。</summary>
    public static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 隐藏 / 恢复

    /// <summary>
    /// 记录窗口当前的位置与显示状态，用于恢复。
    /// 取不到时返回默认结构，恢复时退化为单纯 ShowWindowAsync(SW_SHOW)。
    /// </summary>
    public static WindowPlacement GetPlacement(IntPtr hwnd)
    {
        var placement = WindowPlacement.Create();
        try
        {
            if (!GetWindowPlacement(hwnd, ref placement))
            {
                return WindowPlacement.Create();
            }
        }
        catch
        {
            return WindowPlacement.Create();
        }
        return placement;
    }

    /// <summary>隐藏窗口（ShowWindowAsync，不阻塞本进程）。</summary>
    public static bool Hide(IntPtr hwnd)
    {
        if (!IsWindowAlive(hwnd)) return false;
        try
        {
            return ShowWindowAsync(hwnd, SW_HIDE);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 恢复窗口：先按隐藏前记录的位置/状态还原，再显示并置前。
    /// 用户点托盘图标是希望"看到窗口"，所以原来最小化的窗口也恢复成普通状态。
    /// </summary>
    public static bool Restore(IntPtr hwnd, WindowPlacement placement)
    {
        if (!IsWindowAlive(hwnd)) return false;

        try
        {
            if (placement.Length > 0)
            {
                var restorePlacement = placement;
                if (restorePlacement.ShowCmd is SW_HIDE or SW_SHOWMINIMIZED or SW_SHOWMINIMIZED_LOW)
                {
                    restorePlacement.ShowCmd = SW_SHOW;
                }
                SetWindowPlacement(hwnd, ref restorePlacement);
            }
        }
        catch
        {
            // 位置还原失败不影响显示
        }

        try
        {
            ShowWindowAsync(hwnd, SW_SHOW);
        }
        catch
        {
            // 忽略
        }

        try
        {
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 忽略
        }

        return true;
    }

    /// <summary>请窗口自行关闭（发送 WM_CLOSE，不强制结束进程）。</summary>
    public static bool CloseWindow(IntPtr hwnd)
    {
        if (!IsWindowAlive(hwnd)) return false;
        try
        {
            return PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 图标

    /// <summary>
    /// 取窗口自身图标的 HICON：WM_GETICON → GetClassLongPtr(GCLP_HICON/GCLP_HICONSM)。
    /// 返回的句柄归目标窗口或窗口类所有，**调用方不得销毁**；拿不到返回 IntPtr.Zero。
    /// </summary>
    public static IntPtr TryGetWindowIconHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        foreach (var iconKind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            var icon = QueryWindowIcon(hwnd, iconKind);
            if (icon != IntPtr.Zero) return icon;
        }

        try
        {
            var classIcon = GetClassLongPtr(hwnd, GCLP_HICON);
            if (classIcon != IntPtr.Zero) return classIcon;

            return GetClassLongPtr(hwnd, GCLP_HICONSM);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 取窗口图标并转换成 WPF ImageSource，供窗口列表展示。
    /// 顺序：窗口自身图标 → 进程可执行文件图标。
    /// </summary>
    public static ImageSource? GetWindowIcon(IntPtr hwnd, string executablePath)
    {
        var iconHandle = TryGetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(
                    iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            catch
            {
                // 图标句柄不可用时回退到可执行文件图标
            }
        }

        return IconExtractor.FromExecutable(executablePath);
    }

    private static IntPtr QueryWindowIcon(IntPtr hwnd, int iconKind)
    {
        try
        {
            var result = SendMessageTimeout(
                hwnd, WM_GETICON, (IntPtr)iconKind, IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out var icon);
            return result == IntPtr.Zero ? IntPtr.Zero : icon;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 进程信息

    /// <summary>
    /// 取进程名与可执行文件路径。
    /// 进程名用 Process 读取；路径用 QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION），
    /// 比读 MainModule 快，也不容易被目标进程的加载状态拖住。
    /// </summary>
    private static (string Name, string Path) GetProcessInfo(int processId)
    {
        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch
        {
            // 受保护 / 已退出的进程读不到名字
        }

        return (name, GetProcessImagePath(processId));
    }

    /// <summary>取进程可执行文件完整路径。权限不足时返回空串（调用方会用默认图标兜底）。</summary>
    private static string GetProcessImagePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return string.Empty;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr hwnd, int index);

    private static IntPtr GetClassLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetClassLongPtr64(hwnd, index) : new IntPtr(GetClassLong32(hwnd, index));
}

/// <summary>
/// WINDOWPLACEMENT 的托管结构。字段顺序与 Win32 严格一致：
/// length / flags / showCmd / ptMinPosition / ptMaxPosition / rcNormalPosition。
/// 这里把嵌套的 POINT、RECT 摊平成连续的 int，避免额外嵌套类型。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int ShowCmd;
    public int MinPositionX;
    public int MinPositionY;
    public int MaxPositionX;
    public int MaxPositionY;
    public int NormalLeft;
    public int NormalTop;
    public int NormalRight;
    public int NormalBottom;

    /// <summary>按 Win32 要求带上结构体大小；取位置前必须先填好这个字段。</summary>
    public static WindowPlacement Create() => new() { Length = Marshal.SizeOf<WindowPlacement>() };
}
