using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TrayHider.Interop;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace TrayHider.Services;

/// <summary>
/// Windows 11 原生外观助手：圆角窗口 + 云母（Mica）材质 + 跟随系统深浅色。
/// 在不支持的系统上静默降级，不抛异常。
/// </summary>
internal static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;

    private const int DWMWCP_ROUND = 2;

    // 云母类型：2 = DWMSBT_MAINWINDOW (Mica)，3 = DWMSBT_TRANSIENTWINDOW (Acrylic)
    private const int DWMSBT_MAINWINDOW = 2;

    /// <summary>应用 Windows 11 圆角 + 云母 + 跟随系统主题。</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var isDark = IsSystemDarkMode();

        TrySetRoundedCorners(handle);
        TrySetBackdrop(handle, DWMSBT_MAINWINDOW);
        TrySetDarkMode(handle, isDark);
        TryExtendFrame(handle);

        ApplyThemeResources(window, isDark);
    }

    /// <summary>单独切换深浅色（用于运行时跟随系统主题变化）。</summary>
    public static void ApplyTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var isDark = IsSystemDarkMode();
        TrySetDarkMode(handle, isDark);
        ApplyThemeResources(window, isDark);
    }

    private static void TrySetRoundedCorners(IntPtr handle)
    {
        try
        {
            var preference = DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, sizeof(int));
        }
        catch
        {
            // 旧系统不支持，降级为默认直角
        }
    }

    private static void TrySetBackdrop(IntPtr handle, int backdropType)
    {
        try
        {
            var type = backdropType;
            var hr = NativeMethods.DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE,
                ref type, sizeof(int));

            if (hr != 0)
            {
                // Win11 21H2 早期版本用 1029 号属性
                const int DWMWA_MICA_EFFECT = 1029;
                var enabled = 1;
                NativeMethods.DwmSetWindowAttribute(handle, DWMWA_MICA_EFFECT,
                    ref enabled, sizeof(int));
            }
        }
        catch
        {
            // 不支持则保持纯色背景
        }
    }

    private static void TrySetDarkMode(IntPtr handle, bool dark)
    {
        try
        {
            var value = dark ? 1 : 0;
            var hr = NativeMethods.DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref value, sizeof(int));
            if (hr != 0)
            {
                NativeMethods.DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD,
                    ref value, sizeof(int));
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 让 Mica 材质透到客户区。需要 DWM 扩展边框，否则只有标题栏是云母。
    /// </summary>
    private static void TryExtendFrame(IntPtr handle)
    {
        try
        {
            var margins = new NativeMethods.MARGINS
            {
                leftWidth = -1,
                rightWidth = -1,
                topHeight = -1,
                bottomHeight = -1
            };
            NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- 主题探测

    /// <summary>读取注册表判断系统是否处于深色模式。</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch
        {
            // 忽略
        }
        return false;
    }

    /// <summary>把系统主题色板写进 WPF 资源字典，供 XAML 用 DynamicResource 引用。</summary>
    public static void ApplyThemeResources(Window window, bool dark)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        // 说明：窗口背景必须用 DWM 扩展边框让云母透出来。
        // 若把 Background 设成 alpha=0，WPF 在非 AllowsTransparency 窗口上
        // 会把它合成为不透明黑色，导致界面发黑。
        // 因此这里给一个接近云母基色的极浅色/极深色作为底，再由 DWM 叠加材质。
        if (dark)
        {
            resources["App.Background"] = Brush(0xFF, 0x20, 0x20, 0x20);
            resources["App.CardBackground"] = Brush(0x30, 0xFF, 0xFF, 0xFF);
            resources["App.CardBackgroundHover"] = Brush(0x45, 0xFF, 0xFF, 0xFF);
            resources["App.TextPrimary"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            resources["App.TextSecondary"] = Brush(0xFF, 0xC5, 0xC5, 0xC5);
            resources["App.TextTertiary"] = Brush(0xFF, 0x9A, 0x9A, 0x9A);
            resources["App.Border"] = Brush(0x28, 0xFF, 0xFF, 0xFF);
            resources["App.Divider"] = Brush(0x1A, 0xFF, 0xFF, 0xFF);
            resources["App.ControlFill"] = Brush(0x2E, 0xFF, 0xFF, 0xFF);
            resources["App.ControlFillHover"] = Brush(0x3D, 0xFF, 0xFF, 0xFF);
            resources["App.ControlFillPressed"] = Brush(0x1F, 0xFF, 0xFF, 0xFF);
            resources["App.Accent"] = Brush(0xFF, 0x60, 0xCD, 0xFF);
            resources["App.AccentText"] = Brush(0xFF, 0x00, 0x00, 0x00);
        }
        else
        {
            resources["App.Background"] = Brush(0xFF, 0xF3, 0xF3, 0xF3);
            resources["App.CardBackground"] = Brush(0xB8, 0xFF, 0xFF, 0xFF);
            resources["App.CardBackgroundHover"] = Brush(0xD8, 0xFF, 0xFF, 0xFF);
            resources["App.TextPrimary"] = Brush(0xFF, 0x1A, 0x1A, 0x1A);
            resources["App.TextSecondary"] = Brush(0xFF, 0x5A, 0x5A, 0x5A);
            resources["App.TextTertiary"] = Brush(0xFF, 0x76, 0x76, 0x76);
            resources["App.Border"] = Brush(0x20, 0x00, 0x00, 0x00);
            resources["App.Divider"] = Brush(0x14, 0x00, 0x00, 0x00);
            resources["App.ControlFill"] = Brush(0xFF, 0xFD, 0xFD, 0xFD);
            resources["App.ControlFillHover"] = Brush(0xFF, 0xF5, 0xF5, 0xF5);
            resources["App.ControlFillPressed"] = Brush(0xFF, 0xEB, 0xEB, 0xEB);
            resources["App.Accent"] = Brush(0xFF, 0x00, 0x5F, 0xB8);
            resources["App.AccentText"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
        }
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
