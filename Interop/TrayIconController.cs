using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static TrayHider.Interop.NativeMethods;

namespace TrayHider.Interop;

/// <summary>
/// 使用 NIM_MODIFY + NIF_STATE 变更 NIS_HIDDEN，保留所有者的原注册、版本与点击回调。
/// 新隐藏操作不会删除注册。旧版已删除的注册无法从注册表推导回调，只能请原程序自己重建。
/// </summary>
internal static class TrayIconController
{
    private const int NIM_MODIFY = 1;

    private const uint NIF_STATE = 0x8;
    private const uint NIF_GUID = 0x20;

    /// <summary>图标状态位：隐藏（不在通知区域显示），但注册仍然存在。</summary>
    private const uint NIS_HIDDEN = 0x1;

    /// <summary>
    /// "TaskbarCreated" 注册消息。explorer 重启后会用 HWND_BROADCAST 广播它，
    /// 各程序收到后用自己的 hWnd/uID/uCallbackMessage 重新 NIM_ADD，托盘随之恢复。
    /// 仅当前代备份的原注册已经不在时，请对应进程自己重建。成功投递不等于图标已经恢复。
    /// </summary>
    private static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    /// <summary>"TaskbarCreated" 是否可用（注册失败时为 0，此时只能走降级路径）。</summary>
    public static bool IsTaskbarCreatedAvailable => WmTaskbarCreated != 0;

    public static bool TryCreateIdentity(uint? uid, string? iconGuid, out TrayIconIdentity identity)
    {
        if (Guid.TryParse(iconGuid, out var guid))
        {
            identity = new TrayIconIdentity(uid, guid);
            return true;
        }

        identity = new TrayIconIdentity(uid, null);
        return uid.HasValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>文档化只读 API：查询图标在通知区域的屏幕矩形（不在显示状态时返回 HRESULT 错误）。</summary>
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);

    // ---------------------------------------------------------------- 非破坏性隐藏（首选）

    /// <summary>
    /// 非破坏性隐藏：NIM_MODIFY + NIF_STATE，把 dwState 的 NIS_HIDDEN 置位。
    /// 图标从通知区域消失，但 Shell 里的注册、目标程序的 hWnd/uID/uCallbackMessage 全都保留，
    /// 因此恢复只要清除状态位即可，右键菜单/双击不会失效。
    /// </summary>
    /// <returns>Shell 是否接受。FALSE 表示该图标不支持状态隐藏，调用方应如实报"未隐藏"。</returns>
    public static bool TryHideByState(IntPtr hWnd, TrayIconIdentity identity)
        => SetHiddenState(hWnd, identity, hidden: true);

    /// <summary>
    /// 非破坏性恢复：清除 NIS_HIDDEN 状态位。
    /// 返回 FALSE 说明这个图标已经不在 Shell 里（例如程序重启过），
    /// 调用方需要走"让程序自己重新注册"的旧路径。
    /// </summary>
    public static bool TryShowByState(IntPtr hWnd, TrayIconIdentity identity)
        => SetHiddenState(hWnd, identity, hidden: false);

    private static bool SetHiddenState(IntPtr hWnd, TrayIconIdentity identity, bool hidden)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hWnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault(),
                uFlags = NIF_STATE | (identity.Guid.HasValue ? NIF_GUID : 0),
                dwState = hidden ? NIS_HIDDEN : 0,
                dwStateMask = NIS_HIDDEN,
                szTip = string.Empty
            };
            if (identity.Guid.HasValue) nid.guidItem = identity.Guid.Value;
            return Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 该图标当前是否正显示在通知区域（文档化只读 API Shell_NotifyIconGetRect）。
    /// 判定依据：Shell 返回 S_OK 且矩形非空 = 正在显示；返回任何错误（含图标在溢出面板里、
    /// 或已被隐藏/删除）= 未显示。实测：置位 NIS_HIDDEN 后本调用立即变为失败、清除后立即恢复。
    /// </summary>
    public static bool IsIconDisplayed(IntPtr hWnd, TrayIconIdentity identity)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = hWnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault()
            };
            if (identity.Guid.HasValue) id.guidItem = identity.Guid.Value;

            if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return false;
            return rect.right > rect.left && rect.bottom > rect.top;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 恢复

    /// <summary>
    /// 让目标程序"自己"重新注册托盘图标：向该进程名对应的**全部实例进程**的窗口
    /// PostMessage(TaskbarCreated, 0, 0)。
    ///
    /// 为什么不用 HWND_BROADCAST：广播只覆盖顶级窗口，而消息专用窗口（HWND_MESSAGE 的子窗口）
    /// 收不到广播；.NET 的 NotifyIcon、大量 Electron 应用、QQMusic 这类程序的托盘窗口
    /// 恰恰就是消息专用窗口。因此这里逐个窗口点对点投递，覆盖：
    ///   1. 顶级窗口；2. 消息专用窗口；3. 顶级窗口的子窗口（少数程序把托盘窗口挂在窗口树里）；
    ///   4. <paramref name="knownWindow"/> —— 之前探测到的托盘窗口（最可能命中的那个）。
    /// 同一窗口只投递一次，避免重复重建。
    /// </summary>
    /// <returns>成功投递的窗口数；0 表示没找到可投递的窗口（如进程已退出或权限被拦）。</returns>
    public static int RequestTrayRebuild(string processName, IntPtr knownWindow = default)
    {
        if (WmTaskbarCreated == 0) return 0;

        var pids = GetProcessIds(processName);
        if (pids.Count == 0) return 0;

        var posted = 0;
        var visited = new HashSet<IntPtr>();

        void Post(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !visited.Add(hwnd)) return;
            // 跨完整性级别（目标提权运行）会被 UIPI 拦下并返回 false，此时只能走降级路径
            if (PostMessage(hwnd, WmTaskbarCreated, IntPtr.Zero, IntPtr.Zero)) posted++;
        }

        if (knownWindow != IntPtr.Zero) Post(knownWindow);
        EnumerateProcessWindows(pids, Post, includeChildren: true);
        return posted;
    }

    /// <summary>
    /// 探测某个窗口是否已经持有该标识的托盘图标（uFlags=0 的 NIM_MODIFY，无副作用）。
    /// 恢复流程用它判断"目标程序是否已经自己注册回来了"。
    /// </summary>
    public static bool IsIconRegistered(IntPtr hwnd, TrayIconIdentity identity)
        => hwnd != IntPtr.Zero && ProbeMatch(hwnd, identity);

    /// <summary>窗口句柄是否仍然有效（进程重启后旧句柄会失效）。</summary>
    public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>窗口所属进程号（失败/无效窗口返回 0）。用于确认缓存的窗口句柄仍属于目标进程。</summary>
    public static int GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>进程名（读不到返回空串）。用于兜住"进程号被系统复用"的情况。</summary>
    public static string GetProcessName(int processId)
    {
        if (processId <= 0) return string.Empty;

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------- 窗口查找

    /// <summary>
    /// 在指定进程名（可执行文件名，不含扩展名）的所有实例中，用"探测法"查找注册
    /// 托盘图标所用的窗口句柄：对每个候选窗口用 NIM_MODIFY（uFlags=0，纯存在性探测，
    /// 不改 tooltip、无副作用）探测，返回第一个命中的窗口。
    ///
    /// 探测顺序：
    ///   1. 托盘特征窗口（类名含 tray / NotifyIcon 等，命中率高）；
    ///   2. 其余窗口（含普通 ATL 窗口、消息专用窗口——QQMusic 的托盘窗口就是
    ///      一个无特征的 ATL 窗口，因此必须遍历探测）。
    /// 覆盖顶级窗口（EnumWindows）与消息专用窗口（HWND_MESSAGE）。
    /// </summary>
    public static IntPtr FindTrayWindow(string processName, TrayIconIdentity identity)
    {
        var pids = GetProcessIds(processName);
        if (pids.Count == 0) return IntPtr.Zero;

        var trayCandidates = new List<IntPtr>();
        var otherCandidates = new List<IntPtr>();

        EnumerateProcessWindows(pids, hwnd =>
        {
            var cls = GetClassNameOf(hwnd);
            if (IsSystemInjectedClass(cls)) return;   // 排除 IME 等系统注入窗口
            if (IsTrayIconClass(cls)) trayCandidates.Add(hwnd);
            else otherCandidates.Add(hwnd);
        });

        // 先探测托盘特征窗口，再遍历其余窗口
        foreach (var hwnd in trayCandidates)
        {
            if (ProbeMatch(hwnd, identity)) return hwnd;
        }
        foreach (var hwnd in otherCandidates)
        {
            if (ProbeMatch(hwnd, identity)) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 纯存在性探测：NIM_MODIFY + uFlags=0（不申请改任何字段），返回 Shell 是否持有该标识的图标。
    ///
    /// 为什么不带 NIF_TIP：带 tooltip 的 NIM_MODIFY 有**可能**覆盖目标程序运行时的 tooltip
    /// （注册表里的 InitialTooltip 实测不变，运行时 tooltip 需 hover 目视才能确认，
    ///   探针未实测这一项），无论如何它都是没必要的写入，属于潜在隐形副作用。
    /// 实测（build/TrayProbe 双进程探针）：uFlags=0 与 NIF_TIP 两种探测结果完全一致
    /// （删除后 4ms 内都变 FALSE，恢复后都变 TRUE），所以只用无副作用的这一种。
    /// </summary>
    private static bool ProbeMatch(IntPtr hwnd, TrayIconIdentity identity)
    {
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hwnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault(),
                uFlags = identity.Guid.HasValue ? NIF_GUID : 0,
                szTip = string.Empty
            };
            if (identity.Guid.HasValue) nid.guidItem = identity.Guid.Value;
            return Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断窗口类名是否为"托盘图标窗口"的特征类名。</summary>
    private static bool IsTrayIconClass(string cls)
    {
        return cls.IndexOf("tray", StringComparison.OrdinalIgnoreCase) >= 0
            || cls.IndexOf("notifyicon", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsSystemInjectedClass(string cls)
    {
        return cls is "IME" or "MSCTFIME UI" or "OleMainThreadWndClass";
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>获取指定窗口的类名。</summary>
    public static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        return GetClassNameOf(hwnd);
    }

    /// <summary>
    /// 按窗口类名在指定进程名（无扩展名）的所有实例中查找窗口。
    /// 用于"恢复已隐藏图标"时，图标已被 NIM_DELETE 删除（探测法失效），
    /// 只能靠隐藏前记录的窗口类名重新定位窗口。
    /// </summary>
    public static IntPtr FindWindowByClass(string processName, string className)
    {
        if (string.IsNullOrEmpty(className)) return IntPtr.Zero;

        var pids = GetProcessIds(processName);
        if (pids.Count == 0) return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumerateProcessWindows(pids, hwnd =>
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(GetClassNameOf(hwnd), className, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
            }
        });

        return found;
    }

    /// <summary>
    /// 托盘窗口类名可能是动态值（如 ATL:0000...），应用重启后原类名会变化。
    /// 找不到原类名时，退回到同进程内的托盘特征窗口。
    /// </summary>
    public static IntPtr FindMessageWindowInProcess(string processName)
    {
        var pids = GetProcessIds(processName);
        if (pids.Count == 0) return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumerateProcessWindows(pids, hwnd =>
        {
            if (found != IntPtr.Zero) return;
            var cls = GetClassNameOf(hwnd);
            if (IsSystemInjectedClass(cls)) return;
            if (IsTrayIconClass(cls)) found = hwnd;
        });
        return found;
    }

    /// <summary>
    /// 枚举指定进程集合的全部窗口并回调，覆盖三类窗口：
    ///   1. 顶级窗口（EnumWindows）；
    ///   2. 消息专用窗口（HWND_MESSAGE 的直接子窗口）；
    ///   3. 子窗口（<paramref name="includeChildren"/> 为 true 时，遍历每个顶级窗口的整棵子树）。
    /// 第 3 类只在"投递 TaskbarCreated"时打开：投递是异步的、代价极低，
    /// 多投一次不会有副作用；而探测图标时遍历子窗口会显著放大 NIM_MODIFY 次数，故默认关闭。
    /// </summary>
    private static void EnumerateProcessWindows(HashSet<int> pids, Action<IntPtr> visit, bool includeChildren = false)
    {
        var topLevel = includeChildren ? new List<IntPtr>() : null;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid))
            {
                visit(hwnd);
                topLevel?.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);

        // 消息专用窗口是 HWND_MESSAGE 的子窗口，不在 EnumWindows 的遍历范围内
        var messageWindow = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, null);
        while (messageWindow != IntPtr.Zero)
        {
            GetWindowThreadProcessId(messageWindow, out var pid);
            if (pids.Contains(pid)) visit(messageWindow);
            messageWindow = FindWindowEx(HWND_MESSAGE, messageWindow, null, null);
        }

        if (topLevel == null) return;

        foreach (var parent in topLevel)
        {
            EnumChildWindows(parent, (child, _) =>
            {
                visit(child);
                return true;
            }, IntPtr.Zero);
        }
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var pids = new HashSet<int>();
        if (string.IsNullOrEmpty(processName)) return pids;

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                pids.Add(process.Id);
                process.Dispose();
            }
        }
        catch
        {
            // 忽略
        }
        return pids;
    }

}

public readonly record struct TrayIconIdentity(uint? Uid, Guid? Guid)
{
    public bool IsValid => Guid.HasValue || Uid.HasValue;
}
