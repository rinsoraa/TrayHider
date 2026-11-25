using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using TrayHider.Interop;
using TrayHider.Models;
using TrayHider.Services;
using TrayHider.ViewModels;

// 托盘隐藏方式的回归检查（不读写用户配置、不动任何真实应用的托盘图标）。
//
//   A. 非破坏性隐藏：直接调产品里的 MainViewModel.TryHideWithoutDeleting（反射，private static），
//      验证"Shell 里注册仍在（回调不会失效）、图标确实离开通知区域、清除状态后同一矩形回来"。
//      全程只操作本检查进程自己 --host 起来的探针图标。
//   B. 隐藏方式持久化：TrayHideMode 在 TrayIconEntry 上往返；旧 JSON 缺字段 → Delete（旧备份仍能恢复）。
//      RestorePending（"请求显示、等程序重启"）同样能持久化。
//   C. RestoreBlocked：置位后守护不再重隐（不阻止 owner 重启）；清除后恢复守护。
//   D. 防回归：TrayIconController 不再暴露 RecreateIconFallback（伪造图标那条路已删除）。
//
// 运行：dotnet run -c Release --project tests/TrayHider.TrayModeChecks
//      加 --no-icon 只跑 B/D 这类不建图标的检查。

internal static class Program
{
    private const string ProbeText = "THModeProbe";
    private static int _passed;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--host")
        {
            RunHost();
            return;
        }

        var withIcon = args.Length == 0 || args[0] != "--no-icon";

        CheckHideModeSerialization();
        CheckFakeRebuildRemoved();

        if (!withIcon)
        {
            Console.WriteLine($"PASS {_passed} checks（--no-icon：跳过需要真实图标的用例）");
            return;
        }

        Process? host = null;
        try
        {
            // 用"改了名的副本"当 host：本软件的自身条目过滤按 exe 路径判定，
            // 直接跑同一个 exe 会让探针条目被当成"自己人"过滤掉（这正是历史回归里踩到的坑）。
            var hostExe = EnsureHostCopy();
            host = Process.Start(new ProcessStartInfo(hostExe, "--host") { UseShellExecute = false });
            var target = WaitForProbeIcon(host!.Id);
            if (target == null)
            {
                Console.WriteLine("FAIL: 找不到探针托盘图标（host 未注册成功）");
                Environment.ExitCode = 1;
                return;
            }

            CheckNonDestructiveHide(target.Value);
            CheckRestoreBlocked(target.Value);
            CheckHistoricalRowDoesNotReleasePending(target.Value, host!.Id);
        }
        finally
        {
            try { host?.Kill(); } catch { }
        }

        Console.WriteLine($"PASS {_passed} checks");
    }

    /// <summary>复制一份改了名的 host 可执行文件（同目录，避免影响产品与真实托盘）。</summary>
    private static string EnsureHostCopy()
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "THModeProbeHost.exe");
        var source = Environment.ProcessPath!;

        if (!File.Exists(hostExe) ||
            File.GetLastWriteTimeUtc(hostExe) < File.GetLastWriteTimeUtc(source))
        {
            File.Copy(source, hostExe, overwrite: true);
        }

        return hostExe;
    }

    // ---------------------------------------------------------------- A

    private static void CheckNonDestructiveHide(Target target)
    {
        var identity = new TrayIconIdentity(target.Uid, null);

        Check(IsRegistered(target), "探针图标已注册");
        Check(IsDisplayed(target), "探针图标当前显示在通知区域");

        var hidden = (bool)InvokeTryHideWithoutDeleting(target.Hwnd, identity)!;
        Check(hidden, "TryHideWithoutDeleting 返回 true");
        Check(IsRegistered(target), "隐藏后 Shell 里注册仍在（保留目标程序自己的回调）");
        Check(WaitForDisplayState(target, expected: false), "隐藏后图标不再显示在通知区域");

        var shown = (bool)InvokeController("TryShowByState", target.Hwnd, identity)!;
        Check(shown, "TryShowByState 清除状态位成功");
        Check(WaitForDisplayState(target, expected: true), "恢复后图标重新显示");
    }

    // ---------------------------------------------------------------- B

    private static void CheckHideModeSerialization()
    {
        var entry = new TrayIconEntry { SubKeyName = "1", Uid = 7, HideMode = TrayHideMode.State, RestorePending = true };
        var back = JsonSerializer.Deserialize<TrayIconEntry>(JsonSerializer.Serialize(entry))!;
        Check(back.HideMode == TrayHideMode.State, "State 隐藏方式能持久化往返");
        Check(back.RestorePending, "RestorePending（请求显示、等程序重启）能持久化往返");
        Check(new TrayIconItem().HideMode == TrayHideMode.Delete, "列表条目默认按删除式（兼容旧记录）");

        const string legacy = "{\"SubKeyName\":\"2\",\"Uid\":1,\"ToolTip\":\"Old\"}";
        var old = JsonSerializer.Deserialize<TrayIconEntry>(legacy)!;
        Check(old.HideMode == TrayHideMode.Delete, "旧备份缺 HideMode 时按删除式恢复");
        Check(!old.RestorePending, "旧备份默认不是等待重启状态");
    }

    private static void CheckFakeRebuildRemoved()
    {
        var controller = GetControllerType();
        var fake = controller.GetMethod("RecreateIconFallback", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        Check(fake == null, "TrayIconController 已不再提供 RecreateIconFallback（伪造图标回归会被挡住）");

        var deleteButton = typeof(MainViewModel).GetMethod("AddIconFallback", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(deleteButton == null, "MainViewModel 已不再提供伪造重建的 AddIconFallback");
    }

    // ---------------------------------------------------------------- C

    private static void CheckRestoreBlocked(Target target)
    {
        var vm = new MainViewModel(NewIsolatedConfig());   // 落盘只写临时文件，永不碰用户配置

        var hostPid = GetWindowPid(target.Hwnd);
        var hostProcessName = Process.GetProcessById(hostPid).ProcessName;

        var item = new TrayIconItem
        {
            SubKeyName = "checks-fake",
            ExecutablePath = Path.Combine(AppContext.BaseDirectory, "THModeProbeHost.exe"),
            ProcessName = hostProcessName,
            DisplayName = ProbeText,
            IsRunning = true,
            IsHidden = true,
            Hwnd = target.Hwnd,
            ProcessId = hostPid,
            Uid = target.Uid,
            HideMode = TrayHideMode.State,
            RestoreBlocked = true
        };
        vm.Icons.Add(item);

        InvokePrivate(vm, "ReapplyHidden");
        Check(WaitForDisplayState(target, expected: true), "RestoreBlocked 置位时守护不重隐（不阻止 owner 重启）");

        item.RestoreBlocked = false;
        InvokePrivate(vm, "ReapplyHidden");
        Check(WaitForDisplayState(target, expected: false), "清除 RestoreBlocked 后守护重新生效");

        var shown = (bool)InvokeController("TryShowByState", target.Hwnd, new TrayIconIdentity(target.Uid, null))!;
        Check(shown, "收尾：探针图标状态已清除");
    }

    /// <summary>
    /// 本次运行唯一的临时配置文件。这样即使产品代码以后回归、RestoreItems/Refresh 意外触发 Persist，
    /// 也只会写到这个临时路径，绝不可能覆写用户的 %AppData%\TrayHider\config.json。
    /// </summary>
    private static AppConfig NewIsolatedConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TrayHider.TrayModeChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "config.json");
        _ = path;   // 只有 Save() 时才会真正落盘，且只落到这个临时路径
        return new AppConfig(path);
    }

    // ---------------------------------------------------------------- E. 历史注册表行 vs owner 真重建

    /// <summary>
    /// 回归：NotifyIconSettings 里的行是**永存历史**（图标被删掉后行仍然在）。
    /// 只凭"有这一行"绝不能解除"请求显示、等程序重启"的记录 ——
    /// 必须真的探测到该图标的注册（State 方式还要求状态能清除）才解除。
    /// 本用例只用本检查进程自己 --host 起来的探针图标。
    /// </summary>
    private static void CheckHistoricalRowDoesNotReleasePending(Target target, int hostPid)
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "THModeProbeHost.exe");
        var subKey = FindProbeSubKey(hostExe, target.Uid);
        Check(subKey != null, "探针图标在 NotifyIconSettings 里有历史行");

        // 1) 删掉探针图标的注册（模拟"旧版删除式隐藏"后的状态：注册没了，历史行还在）
        var nid = NewNid(target);
        _ = Shell_NotifyIcon(NIM_DELETE, ref nid);
        Check(WaitForRegistered(target, expected: false), "探针图标的注册已删除（模拟旧版删除式隐藏）");
        Check(FindProbeSubKey(hostExe, target.Uid) != null, "删除注册后历史行依然存在（这正是不能当'已重建'的原因）");

        // 2) 配置里放一条"请求显示、等程序重启"的隐藏记录，刷新后不得被解除
        var config = NewIsolatedConfig();
        config.HiddenBackups[subKey!] = new TrayIconEntry
        {
            SubKeyName = subKey!,
            ExecutablePath = hostExe,
            Uid = target.Uid,
            ToolTip = ProbeText,
            HideMode = TrayHideMode.Delete,
            RestorePending = true
        };

        var vm = new MainViewModel(config);

        vm.Refresh();

        var kept = config.HiddenBackups.ContainsKey(subKey!);
        Check(kept, "只有历史行、owner 未重建：隐藏记录不被解除");

        var item = vm.Icons.FirstOrDefault(i => i.SubKeyName == subKey);
        Check(item != null && item.RestoreBlocked, "该条目的 RestoreBlocked 仍为 true（守护不会盖回去）");

        var identity = new TrayIconIdentity(target.Uid, null);
        Check(!(bool)InvokeCanRelease(target.Hwnd, identity, TrayHideMode.Delete)!, "判定函数：历史行 + 未注册 → 不得解除");

        // 3) 让 owner 真的重新注册（TaskbarCreated，WinForms NotifyIcon 会自己加回来）
        PostTaskbarCreated(hostPid);
        Check(WaitForRegistered(target, true), "owner 重新注册成功（模拟程序重启后自注册）");
        Check((bool)InvokeCanRelease(target.Hwnd, identity, TrayHideMode.Delete)!, "判定函数：真正重新注册后才允许解除");
    }

    /// <summary>找出 host 副本那条托盘记录的子键（按 exe 路径 + tooltip + uid 精确匹配）。</summary>
    private static string? FindProbeSubKey(string hostExe, uint uid)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return null;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key == null) continue;

                if (!string.Equals(key.GetValue("ExecutablePath") as string, hostExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(key.GetValue("InitialTooltip") as string, ProbeText, StringComparison.Ordinal)) continue;

                var rowUid = key.GetValue("UID") is { } raw ? Convert.ToUInt32(raw) : 0u;
                if (rowUid != uid) continue;

                return name;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static void PostTaskbarCreated(int hostPid)
    {
        var message = RegisterWindowMessage("TaskbarCreated");

        EnumerateWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == hostPid) PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
            return true;
        });
    }

    private static bool WaitForRegistered(Target target, bool expected, int timeoutMs = 1500)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (IsRegistered(target) == expected) return true;
            Thread.Sleep(20);
        }

        return IsRegistered(target) == expected;
    }

    private static object? InvokeCanRelease(IntPtr hwnd, TrayIconIdentity identity, TrayHideMode mode)
        => typeof(MainViewModel)
            .GetMethod("CanReleaseRestorePending", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { hwnd, identity, mode });

    // ---------------------------------------------------------------- host / 探测

    private static void RunHost()
    {
        using var icon = new NotifyIcon
        {
            Text = ProbeText,
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var stop = new System.Windows.Forms.Timer { Interval = 60_000 };
        stop.Tick += (_, _) => Application.Exit();
        stop.Start();
        Application.Run(new ApplicationContext());
    }

    private readonly record struct Target(IntPtr Hwnd, uint Uid);

    private static Target? WaitForProbeIcon(int hostPid)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(120);

            var windows = new List<IntPtr>();
            EnumerateWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == hostPid) windows.Add(hwnd);
                return true;
            });

            foreach (var hwnd in windows)
            {
                for (uint uid = 1; uid <= 16; uid++)
                {
                    var candidate = new Target(hwnd, uid);
                    if (IsRegistered(candidate)) return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsRegistered(Target target)
    {
        var nid = NewNid(target);
        return Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>等待"是否显示在通知区域"变成期望值（Shell 更新有几十毫秒延迟，不能立刻断言）。</summary>
    private static bool WaitForDisplayState(Target target, bool expected, int timeoutMs = 1000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (IsDisplayed(target) == expected) return true;
            Thread.Sleep(20);
        }

        return IsDisplayed(target) == expected;
    }

    private static bool IsDisplayed(Target target)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = target.Hwnd,
            uID = target.Uid
        };

        return Shell_NotifyIconGetRect(ref id, out _) == 0;
    }

    private static int GetWindowPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static NOTIFYICONDATAW NewNid(Target target) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = target.Hwnd,
        uID = target.Uid,
        szTip = string.Empty
    };

    private static Type GetControllerType()
        => typeof(MainViewModel).Assembly.GetType("TrayHider.Interop.TrayIconController")!;

    private static object? InvokeTryHideWithoutDeleting(IntPtr hwnd, TrayIconIdentity identity)
        => typeof(MainViewModel)
            .GetMethod("TryHideWithoutDeleting", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { hwnd, identity });

    private static object? InvokeController(string method, IntPtr hwnd, TrayIconIdentity identity)
        => GetControllerType()
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { hwnd, identity });

    private static void InvokePrivate(MainViewModel vm, string method)
        => typeof(MainViewModel)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine($"PASS {++_passed}: {name}");
    }

    // ---------------------------------------------------------------- P/Invoke

    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private static void EnumerateWindows(EnumWindowsProc callback)
    {
        EnumWindows((hwnd, l) => callback(hwnd, l), IntPtr.Zero);

        var msg = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, null);
        while (msg != IntPtr.Zero)
        {
            callback(msg, IntPtr.Zero);
            msg = FindWindowEx(HWND_MESSAGE, msg, null, null);
        }
    }
}
