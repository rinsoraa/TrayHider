using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayHider.Interop;

namespace TrayHider.Services;

/// <summary>
/// 图标提取。两种来源：
///  1. 目标进程主模块内的图标资源（最稳定，跨会话有效）
///  2. 目标窗口的 WM_GETICON（部分应用才提供）
/// 两者都拿不到时回退到默认图标。
///
/// 内存相关的三个约束（列表里最多显示到 32px，没必要为图标常驻大位图）：
///  * 一律按 <see cref="IconPixelSize"/> 解码，避免把 48/64/256 的源图标整张留在内存；
///  * exe 路径 → 已冻结 ImageSource 做弱引用缓存，重复刷新不再重复解码；
///  * 备份用的 IconSnapshot 规范化成 ≤32×32 的 PNG（config.json 里的 base64 也随之变小）。
/// </summary>
internal static class IconExtractor
{
    /// <summary>托盘图标的标准边长（像素）。列表显示尺寸不会超过它。</summary>
    private const int IconPixelSize = 32;

    /// <summary>
    /// 图标缓存上限。注意：这里存的是 <see cref="WeakReference{T}"/>，**不会**让位图一直活着
    /// （位图由 TrayIconItem.Icon 持有），所以上限只影响字典自身的体积与重复解码次数。
    ///
    /// 实测结论（HeapBench，见 build/memprobe）：把上限从 128 降到 64 之后，
    ///   · 内存：托管堆只省下几十 KB（字典项本身），而且位图不受影响 —— 基本没有收益；
    ///   · CPU：120 个 exe 的场景下 5 轮取图标就出现 341 次淘汰、命中率掉到 43.8%，
    ///     每多一次未命中就要重新 ExtractAssociatedIcon + PNG 解码（本机单次约 16ms）。
    /// 因此**保留 128**，不再往下调：缓存是弱引用，不构成内存压力，调小只有坏处。
    /// 真正的省内存点在 MemoryTrimmer（清缓存 + GC + 工作集裁剪）。
    /// </summary>
    private const int MaxCachedIcons = 128;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, WeakReference<ImageSource>> IconCache =
        new(StringComparer.OrdinalIgnoreCase);

    // ---- 命中率统计（只为诊断/实测服务，不参与业务逻辑）----
    private static long _cacheHits;
    private static long _cacheMisses;
    private static long _evictions;
    private static long _clears;

    /// <summary>
    /// 从可执行文件提取图标（带缓存）。
    /// 缓存命中直接返回同一份已 Freeze 的 ImageSource —— 多个条目共用一份位图，
    /// 既省内存也省掉重复的 ExtractAssociatedIcon + PNG 编解码。
    /// </summary>
    public static ImageSource? FromExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        lock (CacheLock)
        {
            if (IconCache.TryGetValue(executablePath, out var weak))
            {
                if (weak.TryGetTarget(out var cached))
                {
                    _cacheHits++;
                    return cached;
                }

                IconCache.Remove(executablePath);   // 已被回收，去掉空壳
            }

            _cacheMisses++;
        }

        if (!File.Exists(executablePath)) return null;

        var image = LoadFromExecutable(executablePath);
        if (image != null)
        {
            lock (CacheLock) Remember(executablePath, image);
        }
        return image;
    }

    private static ImageSource? LoadFromExecutable(string executablePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            return icon == null ? null : ToImageSource(icon);
        }
        catch
        {
            return null;
        }
    }

    private static void Remember(string executablePath, ImageSource image)
    {
        if (IconCache.Count >= MaxCachedIcons)
        {
            TrimDeadEntries();
            if (IconCache.Count >= MaxCachedIcons) EvictOne();
        }

        IconCache[executablePath] = new WeakReference<ImageSource>(image);
    }

    /// <summary>
    /// 清空图标缓存（MemoryTrimmer 在空闲裁剪时调用）。
    /// 缓存本身是弱引用，不会"保住"位图；清空的意义是：
    ///   · 丢掉已经被回收的空壳条目，让字典立刻变小；
    ///   · 对于已经从列表里移除的图标，下一次 GC 能把位图真正回收（不再有 WeakReference 包装残留）。
    /// 清空后下一次取图标会重新解码一次（几十微秒级），代价可接受。
    /// </summary>
    public static void Clear()
    {
        lock (CacheLock)
        {
            IconCache.Clear();
            _clears++;
        }
    }

    /// <summary>当前缓存条目数（诊断用）。</summary>
    internal static int CachedIconCount
    {
        get
        {
            lock (CacheLock)
            {
                return IconCache.Count;
            }
        }
    }

    /// <summary>取一份缓存统计快照（命中/未命中/淘汰/清空次数，诊断与实测用）。</summary>
    internal static IconCacheStats GetCacheStats()
    {
        lock (CacheLock)
        {
            return new IconCacheStats(
                IconCache.Count,
                _cacheHits,
                _cacheMisses,
                _evictions,
                _clears);
        }
    }

    /// <summary>摘掉已经被 GC 回收的缓存条目。</summary>
    private static void TrimDeadEntries()
    {
        List<string>? dead = null;
        foreach (var pair in IconCache)
        {
            if (!pair.Value.TryGetTarget(out _)) (dead ??= new List<string>()).Add(pair.Key);
        }

        if (dead == null) return;
        foreach (var key in dead) IconCache.Remove(key);
    }

    /// <summary>
    /// 缓存满时只淘汰一个条目（取字典里最早的那个），不做整体清空 ——
    /// 整体清空在"图标数略多于上限"时会让缓存每次都被清掉、反复重建，反而更慢。
    /// </summary>
    private static void EvictOne()
    {
        string? victim = null;
        foreach (var key in IconCache.Keys)
        {
            victim = key;
            break;
        }

        if (victim != null)
        {
            IconCache.Remove(victim);
            _evictions++;
        }
    }

    /// <summary>
    /// 从注册表中的 IconSnapshot 读取图标。
    /// 这是 Shell 在图标注册时留下的位图快照，
    /// 对于主模块读不到图标的应用（如部分商店应用）是有效的兜底来源。
    /// </summary>
    public static ImageSource? FromSnapshot(string subKeyName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"{TrayIconReader.RegistryPath}\{subKeyName}");

            if (key?.GetValue("IconSnapshot") is not byte[] data || data.Length < 8)
            {
                return null;
            }

            return DecodeSnapshot(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 直接从 IconSnapshot 字节解码图标。
    /// 用于"已彻底隐藏"（子键已删除）的条目，此时无法再从注册表读取快照，
    /// 只能使用隐藏前备份的原始字节。
    /// </summary>
    public static ImageSource? FromSnapshotBytes(byte[] data)
    {
        if (data is not { Length: >= 8 }) return null;

        try
        {
            return DecodeSnapshot(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Shell 的原始快照规范化为 ≤32×32 的 PNG。两个用途：
    ///   1. 旧配置迁移（旧版备份里是 DIB 或大尺寸 PNG）；
    ///   2. 隐藏图标时把注册表快照收进 config.json —— 缩到 32px 后 base64 长度也一并变小。
    /// 本来就是 ≤32×32 的 PNG 时原样返回（不复制，避免多留一份大数组）。
    /// </summary>
    public static byte[]? NormalizeSnapshotToPng(byte[] data)
    {
        if (data is not { Length: >= 8 }) return null;

        if (IsPng(data) &&
            TryReadPngSize(data, out var width, out var height) &&
            width <= IconPixelSize && height <= IconPixelSize)
        {
            return data;
        }

        if (DecodeSnapshot(data) is not ImageSource decoded) return null;

        return EncodePng(ScaleToTraySize(decoded));
    }

    /// <summary>
    /// 解析图标快照。Shell 保存的格式并不固定：
    ///   1. 直接就是 PNG 文件字节
    ///   2. DIB / BMP 数据
    ///   3. 带 4 字节长度前缀的 PNG
    /// 依次尝试。
    /// </summary>
    private static ImageSource? DecodeSnapshot(byte[] data)
    {
        // 1) 整块即 PNG
        var png = TryDecodePng(data);
        if (png != null) return png;

        // 2) 带长度前缀的 PNG
        if (data.Length > 8)
        {
            var declared = BitConverter.ToInt32(data, 0);
            if (declared > 0 && declared <= data.Length - 4)
            {
                var slice = new byte[declared];
                Buffer.BlockCopy(data, 4, slice, 0, declared);
                png = TryDecodePng(slice);
                if (png != null) return png;
            }
        }

        // 3) BMP / DIB
        var bmp = TryDecodeBitmap(data);
        if (bmp != null) return bmp;

        return null;
    }

    private static ImageSource? TryDecodePng(byte[] data)
    {
        if (!IsPng(data)) return null;

        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = IconPixelSize;   // 大尺寸快照直接按托盘尺寸解码
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryDecodeBitmap(byte[] data)
    {
        try
        {
            // BMP 文件头 "BM"
            if (data.Length > 2 && data[0] == 0x42 && data[1] == 0x4D)
            {
                using var stream = new MemoryStream(data);
                using var bmp = new Bitmap(stream);
                return ToImageSource(bmp);
            }

            // 裸 DIB：前面补一个 BMP 文件头
            if (data.Length > 40)
            {
                var headerSize = BitConverter.ToInt32(data, 0);
                if (headerSize is 40 or 108 or 124)
                {
                    var offset = 14 + data.Length;
                    using var ms = new MemoryStream();
                    using (var bw = new BinaryWriter(ms, System.Text.Encoding.Default, leaveOpen: true))
                    {
                        bw.Write((byte)'B');
                        bw.Write((byte)'M');
                        bw.Write(offset);
                        bw.Write(0);
                        bw.Write(14 + headerSize);
                        bw.Write(data);
                    }
                    ms.Position = 0;

                    using var bmp = new Bitmap(ms);
                    return ToImageSource(bmp);
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>把位图转成按 <see cref="IconPixelSize"/> 解码、已 Freeze 的 BitmapImage。</summary>
    private static ImageSource? ToImageSource(Bitmap bitmap)
    {
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = IconPixelSize;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ToImageSource(Icon icon)
    {
        try
        {
            using var bitmap = icon.ToBitmap();
            return ToImageSource(bitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>保证位图长边不超过托盘尺寸（解码路径已缩放，这里只是兜底）。</summary>
    private static ImageSource ScaleToTraySize(ImageSource source)
    {
        if (source is not BitmapSource bitmap) return source;
        if (bitmap.PixelWidth <= IconPixelSize && bitmap.PixelHeight <= IconPixelSize) return bitmap;

        var scale = IconPixelSize / (double)Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[]? EncodePng(ImageSource source)
    {
        if (source is not BitmapSource bitmap) return null;

        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPng(byte[] data)
        => data.Length >= 8 &&
           data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    /// <summary>从 PNG 的 IHDR 里读出像素宽高（不需要解码整张图）。</summary>
    private static bool TryReadPngSize(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 8 字节签名 + 4 字节块长度 + 4 字节块类型 "IHDR"，之后是宽高各 4 字节（大端）
        if (data.Length < 24) return false;
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return false;
        }

        width = (int)ReadBigEndianUInt32(data, 16);
        height = (int)ReadBigEndianUInt32(data, 20);
        return width > 0 && height > 0;
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24)
         | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8)
         | data[offset + 3];
}

/// <summary>图标缓存统计快照（诊断/实测用）。</summary>
internal readonly struct IconCacheStats
{
    public IconCacheStats(int count, long hits, long misses, long evictions, long clears)
    {
        Count = count;
        Hits = hits;
        Misses = misses;
        Evictions = evictions;
        Clears = clears;
    }

    /// <summary>当前缓存条目数。</summary>
    public int Count { get; }

    /// <summary>命中次数。</summary>
    public long Hits { get; }

    /// <summary>未命中次数（需要重新解码）。</summary>
    public long Misses { get; }

    /// <summary>因超过上限被淘汰的次数。</summary>
    public long Evictions { get; }

    /// <summary>整体清空次数（MemoryTrimmer 触发）。</summary>
    public long Clears { get; }

    /// <summary>命中率（0~1；没有任何请求时为 0）。</summary>
    public double HitRate
    {
        get
        {
            var total = Hits + Misses;
            return total == 0 ? 0 : Hits / (double)total;
        }
    }
}
