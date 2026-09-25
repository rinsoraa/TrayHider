using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayHider.Interop;

/// <summary>
/// 跨进程控制托盘图标 —— 通过 Shell_NotifyIcon 的 NIM_DELETE / NIM_ADD。
///
/// 这是"彻底隐藏"的正确机制（与 gohide、以及本软件"隐藏自身托盘图标"
/// 选项使用的 NotifyIcon.Visible=false 是同一个底层 API）：
///   NIM_DELETE 让 Shell 立即把图标从托盘与溢出面板中移除，进程照常运行；
///   NIM_ADD   则把图标重新加回。
///
/// 早期实现误用了"删除注册表子键"（只删偏好记录、不删运行时图标），已废弃。
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

    private static readonly IntPtr HWND_MESSAGE = new(-3);

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

    // ---------------------------------------------------------------- 隐藏 / 显示

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

    /// <summary>
    /// 恢复图标：用 NIM_ADD 重新添加（图标字节从 IconSnapshot 重建）。
    /// 目标程序后续的 NIM_MODIFY 会自行完善图标与回调。
    /// </summary>
    public static bool ShowIcon(IntPtr hWnd, TrayIconIdentity identity, byte[]? iconSnapshot, string tooltip)
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
                uCallbackMessage = 0x8001,
                hIcon = icon.Handle,
                szTip = string.IsNullOrEmpty(tooltip) ? "TrayHider" : tooltip
            };
            if (identity.Guid.HasValue) nid.guidItem = identity.Guid.Value;
            var ok = Shell_NotifyIcon(NIM_ADD, ref nid);
            return ok;
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
    /// 托盘图标所用的窗口句柄：对每个候选窗口用 NIM_MODIFY（保持原 tooltip，无副作用）
    /// 探测，返回第一个命中的窗口。
    ///
    /// 探测顺序：
    ///   1. 托盘特征窗口（类名含 tray / NotifyIcon / TRAYICON 等，命中率高）；
    ///   2. 其余窗口（含普通 ATL 窗口、消息专用窗口——QQMusic 的托盘窗口就是
    ///      一个无特征的 ATL 窗口，因此必须遍历探测）。
    /// 覆盖顶级窗口（EnumWindows）与消息专用窗口（HWND_MESSAGE）。
    /// </summary>
    public static IntPtr FindTrayWindow(string processName, TrayIconIdentity identity, string tooltip)
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
        }
        catch
        {
            // 忽略
        }

        if (pids.Count == 0) return IntPtr.Zero;

        var trayCandidates = new List<IntPtr>();
        var otherCandidates = new List<IntPtr>();

        void Consider(IntPtr hwnd)
        {
            var cls = GetClassNameOf(hwnd);
            if (IsSystemInjectedClass(cls)) return;   // 排除 IME 等系统注入窗口
            if (IsTrayIconClass(cls)) trayCandidates.Add(hwnd);
            else otherCandidates.Add(hwnd);
        }

        // 1) 顶级窗口
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pids.Contains(pid)) Consider(hwnd);
            return true;
        }, IntPtr.Zero);

        // 2) 消息专用窗口（.NET NotifyIcon 等用这类窗口注册图标）
        IntPtr msg = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, null);
        while (msg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(msg, out int pid);
            if (pids.Contains(pid)) Consider(msg);
            msg = FindWindowEx(HWND_MESSAGE, msg, null, null);
        }

        // 先探测托盘特征窗口，再遍历其余窗口
        foreach (var hwnd in trayCandidates)
        {
            if (ProbeMatch(hwnd, identity, tooltip)) return hwnd;
        }
        foreach (var hwnd in otherCandidates)
        {
            if (ProbeMatch(hwnd, identity, tooltip)) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>用 NIM_MODIFY 探测某个窗口是否持有指定标识的托盘图标（无副作用）。</summary>
    private static bool ProbeMatch(IntPtr hwnd, TrayIconIdentity identity, string tooltip)
    {
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hwnd,
                uID = identity.Guid.HasValue ? 0 : identity.Uid.GetValueOrDefault(),
                uFlags = NIF_TIP | (identity.Guid.HasValue ? NIF_GUID : 0),
                szTip = string.IsNullOrEmpty(tooltip) ? " " : tooltip
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

        IntPtr found = IntPtr.Zero;

        void Check(IntPtr hwnd)
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(GetClassNameOf(hwnd), className, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
            }
        }

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pids.Contains(pid)) Check(hwnd);
            return true;
        }, IntPtr.Zero);

        IntPtr msg = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, null);
        while (msg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(msg, out int pid);
            if (pids.Contains(pid)) Check(msg);
            msg = FindWindowEx(HWND_MESSAGE, msg, null, null);
        }

        return found;
    }

    /// <summary>
    /// 托盘窗口类名可能是动态值（如 ATL:0000...），应用重启后原类名会变化。
    /// 找不到原类名时，退回到同进程内的消息专用窗口。
    /// </summary>
    public static IntPtr FindMessageWindowInProcess(string processName)
    {
        var pids = GetProcessIds(processName);
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;

        void Consider(IntPtr hwnd)
        {
            if (found != IntPtr.Zero) return;
            var cls = GetClassNameOf(hwnd);
            if (IsSystemInjectedClass(cls)) return;
            if (IsTrayIconClass(cls)) found = hwnd;
        }

        IntPtr msg = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, null);
        while (msg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(msg, out int pid);
            if (pids.Contains(pid)) Consider(msg);
            if (found != IntPtr.Zero) return found;
            msg = FindWindowEx(HWND_MESSAGE, msg, null, null);
        }

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pids.Contains(pid)) Consider(hwnd);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);

        return found;
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var pids = new HashSet<int>();
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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
