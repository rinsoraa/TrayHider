using System.Diagnostics;
using System.IO;
using TrayHider.Interop;

namespace TrayHider.Services;

/// <summary>
/// "隐藏图标后不常驻"模式。
///
/// 原理：v1.2.2 起隐藏只改 Shell 里该图标的 NIS_HIDDEN 状态位，注册仍然属于**目标程序**，
/// 所以本软件退出进程不会影响隐藏效果。于是可以：登录时应用一次隐藏 → 退出 → 由计划任务
/// 定期再应用一次，把之后才启动的程序也隐藏掉。
///
/// 代价（如实告知用户，也写进 README）：
///   · 两次应用之间启动的程序，图标会先显示出来，最多间隔一个周期才被隐藏；
///   · 程序自己重新注册图标（例如收到 TaskbarCreated）会把状态位复位，同样要等下一次应用；
///   · 隐藏的窗口（窗口隐藏功能）必须由常驻进程维持，因此本模式不隐藏窗口。
/// </summary>
internal static class RunOnceService
{
    /// <summary>计划任务名（当前用户，登录时可用）。</summary>
    public const string TaskName = "TrayHider-ApplyHiddenState";

    /// <summary>命令行参数：只应用一次隐藏状态然后退出，不建窗口、不建托盘图标、不注册快捷键。</summary>
    public const string ApplyOnceArg = "--apply-once";

    /// <summary>把配置里记录的隐藏图标重新置为隐藏状态。返回成功隐藏的数量。</summary>
    public static int ApplyRecordedHiddenState(AppConfig config, Action<string>? log = null)
    {
        // 计划任务里没有界面可看，出错时只能留痕：设置该环境变量即可把过程写到 %TEMP%TrayHider-runonce.log
        if (log == null && Environment.GetEnvironmentVariable("TRAYHIDER_RUNONCE_LOG") is { Length: > 0 })
        {
            var path = Path.Combine(Path.GetTempPath(), "TrayHider-runonce.log");
            log = text => { try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff ") + text + Environment.NewLine); } catch { } };
        }

        var hidden = 0;
        log?.Invoke("记录数=" + config.HiddenBackups.Count);

        foreach (var record in config.HiddenBackups.Values)
        {
            // 用户点过"显示"、正在等原程序重启的记录不能再盖回去
            if (record.RestorePending) continue;
            if (!TrayIconController.TryCreateIdentity(record.Uid, record.IconGuid, out var identity)) continue;

            var processName = ProcessNameOf(record.ExecutablePath);
            if (processName.Length == 0) continue;

            var hwnd = TrayIconController.FindTrayWindow(processName, identity);
            if (hwnd == IntPtr.Zero)
            {
                // 兜底：直接在该进程的窗口里找"当前真的显示着这个图标"的那个。
                // FindTrayWindow 依赖按进程名定位窗口，个别程序（多进程/自定义类名）会漏，
                // 而 IsIconDisplayed 是文档化的精确判断，用它来挑窗口最可靠。
                hwnd = FindDisplayedWindow(processName, identity, log);
                if (hwnd == IntPtr.Zero)
                {
                    log?.Invoke(processName + ": 未找到显示中的托盘图标（程序可能未运行）");
                    continue;
                }
            }

            var wasDisplayed = TrayIconController.IsIconDisplayed(hwnd, identity);
            if (!TrayIconController.TryHideByState(hwnd, identity))
            {
                log?.Invoke(processName + ": 不支持状态隐藏，保持显示");
                continue;
            }

            // 置位后要确认真的从通知区域消失，否则清回去，避免留下半吊子状态
            if (wasDisplayed && TrayIconController.IsIconDisplayed(hwnd, identity))
            {
                TrayIconController.TryShowByState(hwnd, identity);
                log?.Invoke(processName + ": 状态位已置但图标仍在显示，已回滚");
                continue;
            }

            hidden++;
        }

        log?.Invoke("完成：重新隐藏 " + hidden + " 个图标");
        return hidden;
    }

    /// <summary>设置界面开关：写入配置、同步自启动参数与计划任务。返回给用户看的说明。</summary>
    public static string SetEnabled(AppConfig config, bool enabled)
    {
        config.RunOnceAndExit = enabled;

        // 开机自启项要跟着改命令行参数（--apply-once / --minimized）
        if (config.RunAtStartup) StartupManager.SetEnabled(true);
        config.Save();

        var message = SyncSchedule(config);
        return enabled
            ? Loc.T("App.RunOnce.Enabled") + message + Loc.T("App.RunOnce.Caveat")
            : Loc.T("App.RunOnce.Disabled") + message;
    }

    /// <summary>按当前设置同步计划任务（开启则创建/刷新，关闭则删除）。返回给用户看的说明。</summary>
    public static string SyncSchedule(AppConfig config)
    {
        var exe = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("App.RunOnce.NotExe");
        }

        if (!config.RunOnceAndExit)
        {
            return RemoveSchedule() ? Loc.T("App.RunOnce.ScheduleRemoved") : Loc.T("App.RunOnce.ScheduleAbsent");
        }

        var quotedExe = Quote(exe + " " + ApplyOnceArg);
        var arguments = "/Create /TN " + Quote(TaskName) + " /TR " + Quote(quotedExe)
                        + " /SC MINUTE /MO " + config.RunOnceIntervalMinutes + " /F";

        if (!Run("schtasks.exe", arguments, out var error))
        {
            return Loc.T("App.RunOnce.ScheduleFail", error);
        }

        return Loc.T("App.RunOnce.ScheduleCreated", TaskName, config.RunOnceIntervalMinutes);
    }

    public static bool IsScheduleInstalled()
        => Run("schtasks.exe", "/Query /TN " + Quote(TaskName), out _);

    public static bool RemoveSchedule()
        => Run("schtasks.exe", "/Delete /TN " + Quote(TaskName) + " /F", out _);

    /// <summary>开机自启动项：不常驻模式下传入 --apply-once，否则维持 --minimized。</summary>
    public static string StartupArguments(AppConfig config)
        => config.RunOnceAndExit ? ApplyOnceArg : "--minimized";

    /// <summary>给命令行参数加引号（schtasks 的 /TR 里还要再套一层）。</summary>
    private static string Quote(string text) => "\\" + text + "\\";

    /// <summary>在该进程的全部窗口里找"这个图标正在显示"的那一个（GetRect 精确判定）。</summary>
    private static IntPtr FindDisplayedWindow(string processName, TrayIconIdentity identity, Action<string>? log)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    var pid = process.Id;
                    var found = IntPtr.Zero;
                    foreach (var hwnd in EnumerateProcessWindows(pid))
                    {
                        if (!TrayIconController.IsIconDisplayed(hwnd, identity)) continue;
                        found = hwnd;
                        break;
                    }

                    if (found != IntPtr.Zero)
                    {
                        log?.Invoke(processName + ": 按显示状态定位到窗口 0x" + found.ToInt64().ToString("X"));
                        return found;
                    }
                }
            }
        }
        catch
        {
            // 找不到就让调用方记录"未找到"
        }

        return IntPtr.Zero;
    }

    private static List<IntPtr> EnumerateProcessWindows(int pid)
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string ProcessNameOf(string executablePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(executablePath);
            return string.IsNullOrEmpty(name) ? string.Empty : name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Run(string file, string arguments, out string error)
    {
        error = string.Empty;

        try
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start);
            if (process == null)
            {
                error = "无法启动 schtasks.exe";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            if (process.ExitCode == 0) return true;

            error = output.Trim();
            if (error.Length == 0) error = "schtasks 退出码 " + process.ExitCode;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
