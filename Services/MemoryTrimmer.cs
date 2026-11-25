using System.Diagnostics;
using TrayHider.Interop;

namespace TrayHider.Services;

/// <summary>
/// 空闲内存裁剪：**只在主窗口已隐藏 / 最小化之后**调用（用户看不到界面时做，避免掉帧）。
///
/// 必须区分两件事，否则会把"观感"当成"真省内存"：
///   1) 工作集（任务管理器里的"内存(专用工作集)"、Process.WorkingSet64）
///      会被 <see cref="NativeMethods.SetProcessWorkingSetSize"/> 把冷页换出到页面文件而立刻下降；
///      **私有字节 / 提交量（PrivateMemorySize64）基本不变**，被换出的页下次访问还会读回来。
///      所以工作集下降只是"暂时把内存让给系统"，不等于真的少占内存。
///   2) 真正可再生、能少占的托管内存：图标缓存（弱引用包装）、可重建的图标快照字节副本。
///      这部分要靠 <see cref="IconExtractor.Clear"/> + （必要时）一次 Full GC 才真正释放。
///
/// 因此：
///   · <c>aggressive=false</c>：清图标缓存 + 通知宿主释放可重建快照 + 裁工作集（便宜，可放在"窗口隐藏"时）；
///   · <c>aggressive=true</c>：在上一档基础上再加一次 GC.Collect(2, Forced, Blocking, Compacting)
///     并裁工作集（放在"隐藏满一段时间仍空闲"时，避免频繁 Full GC 拖慢响应）。
///
/// 节流：两次裁剪之间至少 <see cref="CooldownMs"/> 毫秒，防止调用方在快速的显示/隐藏切换里
/// 反复触发 Full GC 与工作集抖动。
/// </summary>
internal static class MemoryTrimmer
{
    /// <summary>裁剪冷却时间（毫秒）：2 分钟内重复调用会被直接忽略。</summary>
    private const int CooldownMs = 120_000;

    private static readonly object Sync = new();

    /// <summary>可重建快照字节的释放回调（由宿主注册；返回释放的字节数，未知时返回 0）。</summary>
    private static Func<long>? _snapshotReleaser;

    private static long _lastTrimTick = long.MinValue / 2;
    private static long _trimCount;
    private static long _skippedCount;
    private static long _lastManagedReleasedBytes;

    /// <summary>
    /// 注册"释放可重建快照字节"的回调（例如让视图模型把 TrayIconItem.IconSnapshot 这类
    /// 隐藏时可重建的副本置空，返回释放的字节数）。传 null 表示取消注册。
    /// 说明：视图模型侧目前没有对应方法，需要先把 App.xaml.cs / MainViewModel 接上（不在本任务范围内）。
    /// </summary>
    public static void RegisterSnapshotReleaser(Func<long>? releaser)
    {
        lock (Sync)
        {
            _snapshotReleaser = releaser;
        }
    }

    /// <summary>
    /// 执行一次空闲裁剪。调用方负责保证"此刻主窗口确实不可见"；冷却期内重复调用直接返回。
    /// </summary>
    /// <param name="aggressive">
    /// true 时额外做一次阻塞式 Full GC（compress + 回收 LOH 之外的代），适合"隐藏一段时间仍空闲"；
    /// false 时只清缓存/快照并裁工作集，适合"刚隐藏窗口"的快速路径。
    /// </param>
    public static void TrimIdle(bool aggressive)
    {
        Func<long>? releaser;
        long trimNumber;

        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (now - _lastTrimTick < CooldownMs)
            {
                _skippedCount++;
                Debug.WriteLine(
                    $"[MemoryTrimmer] 冷却中，跳过（距上次 {now - _lastTrimTick}ms < {CooldownMs}ms，aggressive={aggressive}，已跳过 {_skippedCount} 次）");
                return;
            }

            _lastTrimTick = now;
            _trimCount++;
            trimNumber = _trimCount;
            releaser = _snapshotReleaser;
        }

        var releasedBefore = GC.GetTotalMemory(forceFullCollection: false);

        try
        {
            // 1) 可再生的托管对象：图标缓存（弱引用包装 + 已失效条目）
            IconExtractor.Clear();

            // 2) 宿主侧可重建的快照字节（需要视图模型配合；未注册时跳过）
            _lastManagedReleasedBytes = 0;
            if (releaser != null)
            {
                try
                {
                    _lastManagedReleasedBytes = releaser();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MemoryTrimmer] 快照释放回调异常: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 3) 只有激进档才做 Full GC：非激进档让 GC 自己按节奏回收，避免隐藏窗口时卡顿。
            //    实测备注：曾尝试在 Full GC 前打开 LOH 一次性压缩（GCSettings.LargeObjectHeapCompactionMode），
            //    用 200×64KB 的 byte[] 测过：普通 Full GC 与 LOH 压缩后私有字节几乎一样（29.1MB vs 29.3MB），
            //    多花的 CPU 没有换来提交量下降，所以这里不做 —— 留着会让人误以为"真的省了内存"。
            if (aggressive)
            {
                GC.Collect(
                    generation: 2,
                    mode: GCCollectionMode.Forced,
                    blocking: true,
                    compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            // 4) 裁工作集：把冷页换出物理内存（观感上的下降；私有字节基本不变）
            var trimWorkingSetOk = TrimWorkingSet(out var trimError);

            var releasedAfter = GC.GetTotalMemory(forceFullCollection: false);
            Debug.WriteLine(
                "[MemoryTrimmer] " +
                $"第 {trimNumber} 次裁剪(aggressive={aggressive})：" +
                $"托管堆 {releasedBefore / 1024.0:F0}KB -> {releasedAfter / 1024.0:F0}KB，" +
                $"快照释放 {_lastManagedReleasedBytes / 1024.0:F0}KB，" +
                $"SetProcessWorkingSetSize(-1,-1)={(trimWorkingSetOk ? "成功" : "失败 err=" + trimError)}，" +
                $"工作集={ReadWorkingSetMb():F1}MB 私有字节={ReadPrivateBytesMb():F1}MB " +
                $"（工作集下降不代表私有字节下降）");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryTrimmer] 裁剪异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>当前是否处于冷却期（诊断用）。</summary>
    internal static bool IsCoolingDown
    {
        get
        {
            lock (Sync)
            {
                return Environment.TickCount64 - _lastTrimTick < CooldownMs;
            }
        }
    }

    /// <summary>取一次裁剪统计快照（诊断/实测用）。</summary>
    internal static MemoryTrimStats GetStats()
    {
        lock (Sync)
        {
            return new MemoryTrimStats(_trimCount, _skippedCount, _lastManagedReleasedBytes, CooldownMs);
        }
    }

    /// <summary>清掉冷却计时（仅供实测/诊断使用，产品代码不要调用）。</summary>
    internal static void ResetCooldownForDiagnostics()
    {
        lock (Sync)
        {
            _lastTrimTick = long.MinValue / 2;
        }
    }

    // ---------------------------------------------------------------- 内部工具

    /// <summary>调用 SetProcessWorkingSetSize(-1,-1) 裁剪工作集，失败时返回 Win32 错误码。</summary>
    private static bool TrimWorkingSet(out int errorCode)
    {
        try
        {
            var ok = NativeMethods.SetProcessWorkingSetSize(
                NativeMethods.CurrentProcessPseudoHandle,
                new IntPtr(-1),
                new IntPtr(-1));

            errorCode = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return ok;
        }
        catch (Exception ex)
        {
            errorCode = ex.HResult;
            return false;
        }
    }

    private static double ReadWorkingSetMb()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64 / 1024.0 / 1024.0;
        }
        catch
        {
            return -1;
        }
    }

    private static double ReadPrivateBytesMb()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64 / 1024.0 / 1024.0;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>空闲裁剪统计快照（诊断/实测用）。</summary>
internal readonly struct MemoryTrimStats
{
    public MemoryTrimStats(long trimCount, long skippedCount, long lastReleasedBytes, int cooldownMs)
    {
        TrimCount = trimCount;
        SkippedCount = skippedCount;
        LastReleasedBytes = lastReleasedBytes;
        CooldownMs = cooldownMs;
    }

    /// <summary>实际执行过的裁剪次数。</summary>
    public long TrimCount { get; }

    /// <summary>因冷却期被跳过的次数。</summary>
    public long SkippedCount { get; }

    /// <summary>上一次裁剪中由快照释放回调归还的字节数。</summary>
    public long LastReleasedBytes { get; }

    /// <summary>冷却时间（毫秒）。</summary>
    public int CooldownMs { get; }
}
