using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrayHider.Interop;

namespace TrayHider.Services;

/// <summary>
/// 应用配置。所有勾选状态与设置项持久化到
/// %AppData%\TrayHider\config.json，重启后自动恢复。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 旧版字段：仅存被隐藏图标的子键名（当时用 IsPromoted=0 隐藏）。
    /// 新版改用删除子键的方式彻底隐藏，此字段仅用于一次性迁移，迁移后清空。
    /// </summary>
    public List<string> HiddenKeys { get; set; } = new();

    /// <summary>
    /// 新版：被彻底隐藏（子键已删除）图标的完整备份，键为子键名。
    /// 用于在"显示"时重建子键、在列表里恢复展示"已隐藏"条目。
    /// </summary>
    public Dictionary<string, TrayIconEntry> HiddenBackups { get; set; } = new();

    /// <summary>隐藏本软件自身托盘图标。</summary>
    public bool HideSelfTrayIcon { get; set; }

    /// <summary>开机自动启动。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>唤出主窗口的全局快捷键（修饰键）。</summary>
    public int HotkeyModifiers { get; set; } = 0x0002 | 0x0008; // Ctrl + Win

    /// <summary>唤出主窗口的全局快捷键（虚拟键码）。</summary>
    public int HotkeyVirtualKey { get; set; } = 0x54; // 'T'

    /// <summary>关闭窗口时最小化到托盘而非退出。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>主窗口显示时的完整刷新间隔（毫秒）。</summary>
    public int RefreshIntervalMs { get; set; } = 8000;

    /// <summary>主窗口隐藏时，仅守护已隐藏图标的低频间隔（毫秒）。</summary>
    public int BackgroundGuardIntervalMs { get; set; } = 20000;

    // ---------------------------------------------------------------- 读写

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayHider");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.Normalize();
                    return config;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值，不影响启动
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 写入失败不应导致崩溃
        }
    }

    private void Normalize()
    {
        HiddenKeys ??= new List<string>();
        HiddenBackups ??= new Dictionary<string, TrayIconEntry>();
        if (RefreshIntervalMs == 3000) RefreshIntervalMs = 8000;
        if (BackgroundGuardIntervalMs < 15000) BackgroundGuardIntervalMs = 15000;
        if (BackgroundGuardIntervalMs > 30000) BackgroundGuardIntervalMs = 30000;
        HotkeyModifiers &= ~0x4000;
        if (RefreshIntervalMs < 1000) RefreshIntervalMs = 1000;
        if (RefreshIntervalMs > 60000) RefreshIntervalMs = 60000;
    }
}
