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
/// </summary>
internal static class IconExtractor
{
    public static ImageSource? FromExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

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

    /// <summary>把 Shell 的原始快照标准化为较小的 PNG，仅用于旧配置迁移和注册表缺失时的恢复兜底。</summary>
    public static byte[]? NormalizeSnapshotToPng(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return data.ToArray();
        }

        if (FromSnapshotBytes(data) is not BitmapSource bitmapSource) return null;

        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
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
        // PNG 魔数
        if (data.Length < 8 ||
            data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
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
            using var stream = new MemoryStream();
            using var bitmap = icon.ToBitmap();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
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
}
