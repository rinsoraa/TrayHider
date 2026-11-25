using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static TrayHider.Interop.NativeMethods;

namespace TrayHider.Interop;

/// <summary>
/// 跨进程控制托盘图标 —— 通过 Shell_NotifyIcon 的 NIM_DELETE / NIM_MODIFY / NIM_ADD。
///
/// 这是"彻底隐藏"的正确机制（与 gohide、以及本软件"隐藏自身托盘图标"
/// 选项使用的 NotifyIcon.Visible=false 是同一个底层 API）：
///   NIM_DELETE 让 Shell 立即把图标从托盘与溢出面板中移除，进程照常运行。
///
/// 恢复（显示）策略 —— 关键点，也是 issue#1 的根因：
///   NOTIFYICONDATA 是"注册方"的契约：Shell 把这个图标的鼠标消息投给注册时给出的
///   hWnd + uCallbackMessage。本进程无法伪造出目标程序自己的回调消息号，
///   一旦由本进程用 NIM_ADD 重建图标，图标看得见但鼠标消息会被投到目标程序
///   根本不处理的回调上 —— 右键菜单/双击全部失效（只有重启 explorer，
///   让各程序收到 TaskbarCreated 用自己的参数重新注册，才会恢复正常）。
///   因此恢复时优先"让目标程序自己重新注册"：向目标进程投递 TaskbarCreated，
///   再用 NIM_MODIFY 短轮询探测图标是否回归；只有在目标程序不响应时才降级
///   用本进程 NIM_ADD 兜底（见 <see cref="RecreateIconFallback"/>）。
/// </summary>
internal static class TrayIconController
{
    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_GUID = 0x20;

    /// <summary>
    /// 降级重建（<see cref="RecreateIconFallback"/>）使用的回调消息号。
    /// Shell 会把该图标的鼠标消息投到目标窗口的这条消息上，而目标程序并不处理它，
    /// 所以这条路径下的图标"看得见但点不动" —— 这正是 issue#1 的来源。
    /// </summary>
    private const uint FallbackCallbackMessage = 0x8001;

    /// <summary>
    /// "TaskbarCreated" 注册消息。explorer 重启后会用 HWND_BROADCAST 广播它，
    /// 各程序收到后用自己的 hWnd/uID/uCallbackMessage 重新 NIM_ADD，托盘随之恢复。
    /// 本软件恢复图标时也投递这条消息（点对点 PostMessage），效果等同"只重启这一个小程序"，
    /// 是让右键菜单保持有效的唯一可靠办法。
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATAW lpData);

    // ---------------------------------------------------------------- 隐藏

    /// <summary>彻底隐藏：让 Shell 删除目标图标（NIM_DELETE）。不终止进程。</summary>
    public static bool HideIcon(IntPtr hWnd, TrayIconIdentity identity)
    {
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hWnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault()
            };
            if (identity.Guid.HasValue)
            {
                nid.uFlags = NIF_GUID;
                nid.guidItem = identity.Guid.Value;
            }
            return Shell_NotifyIcon(NIM_DELETE, ref nid);
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

    /// <summary>
    /// 【兜底路径，仅在目标程序不响应 TaskbarCreated 时使用】
    /// 由本进程伪造 NOTIFYICONDATA 用 NIM_ADD 把图标加回来 —— 只能保证"图标可见"：
    ///   hWnd 借用目标程序的托盘窗口，uCallbackMessage 固定为 0x8001，
    ///   目标程序并不处理这条回调消息，因此右键菜单/双击可能失效，
    ///   需要重启该程序（它自己会用正确的回调重新注册）才能彻底恢复。
    /// 调用方必须在界面上把这一状态明确告知用户（见 TrayIconItem.CallbackDegraded）。
    /// </summary>
    public static bool RecreateIconFallback(IntPtr hWnd, TrayIconIdentity identity, byte[]? iconSnapshot, string tooltip)
    {
        IconHandle icon = default;
        try
        {
            icon = LoadIconFromSnapshot(iconSnapshot);
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hWnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault(),
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | (identity.Guid.HasValue ? NIF_GUID : 0),
                uCallbackMessage = FallbackCallbackMessage,
                hIcon = icon.Handle,
                szTip = string.IsNullOrEmpty(tooltip) ? "TrayHider" : tooltip
            };
            if (identity.Guid.HasValue) nid.guidItem = identity.Guid.Value;
            return Shell_NotifyIcon(NIM_ADD, ref nid);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (icon.OwnsHandle) DestroyIcon(icon.Handle);
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

    // ---------------------------------------------------------------- 图标重建

    /// <summary>从注册表 IconSnapshot 字节重建图标句柄，失败回退到系统共享图标。</summary>
    private static IconHandle LoadIconFromSnapshot(byte[]? snapshot)
    {
        try
        {
            if (snapshot is { Length: > 8 })
            {
                using var ms = new MemoryStream(snapshot);
                using var bmp = new Bitmap(ms);
                return new IconHandle(bmp.GetHicon(), OwnsHandle: true);
            }
        }
        catch
        {
            // 解码失败（如裸 DIB）则回退
        }

        return new IconHandle(LoadIcon(IntPtr.Zero, (IntPtr)32512), OwnsHandle: false); // IDI_APPLICATION
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

public readonly record struct TrayIconIdentity(uint? Uid, Guid? Guid)
{
    public bool IsValid => Guid.HasValue || Uid.HasValue;
}

internal readonly record struct IconHandle(IntPtr Handle, bool OwnsHandle);
