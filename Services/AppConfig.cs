using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using TrayHider.Interop;

namespace TrayHider.Services;

/// <summary>
/// 全局快捷键的动作名。配置、App 注册、设置界面三处共用同一套字符串，
/// 避免各写各的导致键名不一致。
/// </summary>
public static class HotkeyActions
{
    /// <summary>唤出主窗口。</summary>
    public const string ShowMain = "ShowMain";

    /// <summary>隐藏 / 恢复当前（前台）窗口到托盘。</summary>
    public const string ToggleHideWindow = "ToggleHideWindow";

    /// <summary>隐藏全部窗口。</summary>
    public const string HideAllWindows = "HideAllWindows";

    /// <summary>恢复全部被隐藏的窗口。</summary>
    public const string RestoreAllWindows = "RestoreAllWindows";

    /// <summary>全部隐藏 / 全部恢复托盘图标（切换）。</summary>
    public const string ToggleTrayIcons = "ToggleTrayIcons";

    /// <summary>全部动作，顺序即设置界面的显示顺序。</summary>
    public static readonly string[] All =
    {
        ShowMain, ToggleHideWindow, HideAllWindows, RestoreAllWindows, ToggleTrayIcons
    };
}

/// <summary>
/// 单个全局快捷键绑定。Modifiers 使用 Win32 的 MOD_* 位掩码（Ctrl=0x2、Alt=0x1、Shift=0x4、Win=0x8）。
/// </summary>
public sealed class HotkeyBinding
{
    public int Modifiers { get; set; }
    public int VirtualKey { get; set; }

    /// <summary>是否有意义的绑定（至少一个修饰键且键码非 0）。</summary>
    [JsonIgnore]
    public bool IsValid => (Modifiers & 0x000F) != 0 && VirtualKey != 0;

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, VirtualKey = VirtualKey };

    /// <summary>形如 “Ctrl + Win + T” 的可读文本。</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((Modifiers & 0x0008) != 0) parts.Add("Win");
        if ((Modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((Modifiers & 0x0004) != 0) parts.Add("Shift");

        var key = VirtualKey == 0 ? Key.None : KeyInterop.KeyFromVirtualKey(VirtualKey);
        parts.Add(key == Key.None ? "未设置" : key.ToString());
        return string.Join(" + ", parts);
    }

    /// <summary>各动作的默认快捷键。</summary>
    public static Dictionary<string, HotkeyBinding> CreateDefaults() => new(StringComparer.Ordinal)
    {
        [HotkeyActions.ShowMain] = new() { Modifiers = 0x0002 | 0x0008, VirtualKey = 0x54 },          // Ctrl + Win + T
        [HotkeyActions.ToggleHideWindow] = new() { Modifiers = 0x0002 | 0x0008, VirtualKey = 0x48 },  // Ctrl + Win + H
        [HotkeyActions.HideAllWindows] = new() { Modifiers = 0x0002 | 0x0008, VirtualKey = 0x57 },    // Ctrl + Win + W
        [HotkeyActions.RestoreAllWindows] = new() { Modifiers = 0x0002 | 0x0008, VirtualKey = 0x55 }, // Ctrl + Win + U
        [HotkeyActions.ToggleTrayIcons] = new() { Modifiers = 0x0002 | 0x0008, VirtualKey = 0x49 }    // Ctrl + Win + I
    };
}

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
    /// 用于在“显示”时重建子键、在列表里恢复展示“已隐藏”条目。
    /// </summary>
    public Dictionary<string, TrayIconEntry> HiddenBackups { get; set; } = new();

    /// <summary>隐藏本软件自身托盘图标。</summary>
    public bool HideSelfTrayIcon { get; set; }

    /// <summary>开机自动启动。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>
    /// 旧版字段：唤出主窗口快捷键的修饰键。
    /// v1.2.0 起快捷键改为多动作表（<see cref="Hotkeys"/>），此字段仅用于读取旧配置并迁移，
    /// 保存时始终与 <see cref="HotkeyActions.ShowMain"/> 保持同步。
    /// </summary>
    public int HotkeyModifiers { get; set; } = 0x0002 | 0x0008; // Ctrl + Win

    /// <summary>旧版字段：唤出主窗口快捷键的虚拟键码（同上，仅用于兼容旧配置）。</summary>
    public int HotkeyVirtualKey { get; set; } = 0x54; // 'T'

    /// <summary>
    /// 全部全局快捷键，键为 <see cref="HotkeyActions"/> 中的动作名。
    /// 缺失的动作会在 <see cref="Normalize"/> 中补默认值。
    /// </summary>
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = HotkeyBinding.CreateDefaults();

    /// <summary>关闭窗口时最小化到托盘而非退出。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>隐藏窗口到托盘时，是否同时隐藏该程序原本的托盘图标。</summary>
    public bool HideWindowHidesAppTrayIcon { get; set; }

    /// <summary>退出本软件时，是否把被隐藏的窗口全部恢复。</summary>
    public bool RestoreWindowsOnExit { get; set; } = true;

    /// <summary>主窗口显示时的完整刷新间隔（毫秒）。</summary>
    public int RefreshIntervalMs { get; set; } = 8000;

    /// <summary>主窗口隐藏时，仅守护已隐藏图标的低频间隔（毫秒）。</summary>
    public int BackgroundGuardIntervalMs { get; set; } = 20000;

    /// <summary>窗口列表的刷新间隔（毫秒）。</summary>
    public int WindowRefreshIntervalMs { get; set; } = 3000;

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

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            // 旧字段与多动作表保持同步，保证回退到旧版本时快捷键不丢
            SyncLegacyHotkeyFields();

            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 写入失败不应导致崩溃
        }
    }

    /// <summary>取指定动作的快捷键（缺失时返回默认值，不会返回 null）。</summary>
    public HotkeyBinding GetHotkey(string action)
    {
        if (!Hotkeys.TryGetValue(action, out var binding) || binding == null)
        {
            binding = HotkeyBinding.CreateDefaults().TryGetValue(action, out var fallback)
                ? fallback
                : new HotkeyBinding();
            Hotkeys[action] = binding;
        }
        return binding;
    }

    private void SyncLegacyHotkeyFields()
    {
        var showMain = GetHotkey(HotkeyActions.ShowMain);
        HotkeyModifiers = showMain.Modifiers;
        HotkeyVirtualKey = showMain.VirtualKey;
    }

    private void Normalize()
    {
        HiddenKeys ??= new List<string>();
        HiddenBackups ??= new Dictionary<string, TrayIconEntry>();

        // ---- 快捷键：旧版单快捷键 → 多动作表（向后兼容，旧配置的自定义快捷键不会丢）----
        var legacyModifiers = HotkeyModifiers & ~0x4000;
        var legacyVirtualKey = HotkeyVirtualKey;
        Hotkeys ??= new Dictionary<string, HotkeyBinding>(StringComparer.Ordinal);

        var defaults = HotkeyBinding.CreateDefaults();
        var hasLegacyCustom = (legacyModifiers, legacyVirtualKey) != (defaults[HotkeyActions.ShowMain].Modifiers,
            defaults[HotkeyActions.ShowMain].VirtualKey);

        if (!Hotkeys.ContainsKey(HotkeyActions.ShowMain))
        {
            if (hasLegacyCustom && legacyModifiers != 0 && legacyVirtualKey != 0)
            {
                Hotkeys[HotkeyActions.ShowMain] = new HotkeyBinding
                {
                    Modifiers = legacyModifiers,
                    VirtualKey = legacyVirtualKey
                };
            }
        }

        foreach (var action in HotkeyActions.All)
        {
            if (!Hotkeys.TryGetValue(action, out var binding) || binding == null)
            {
                Hotkeys[action] = defaults[action];
                continue;
            }

            // 清理非法值：去掉 MOD_NOREPEAT（由注册时统一追加）、无修饰键、键码为 0
            binding.Modifiers &= ~0x4000;
            if (!binding.IsValid) Hotkeys[action] = defaults[action];
        }

        SyncLegacyHotkeyFields();

        // ---- 其他数值项 ----
        if (RefreshIntervalMs == 3000) RefreshIntervalMs = 8000;
        if (BackgroundGuardIntervalMs < 15000) BackgroundGuardIntervalMs = 15000;
        if (BackgroundGuardIntervalMs > 30000) BackgroundGuardIntervalMs = 30000;
        if (RefreshIntervalMs < 1000) RefreshIntervalMs = 1000;
        if (RefreshIntervalMs > 60000) RefreshIntervalMs = 60000;
        if (WindowRefreshIntervalMs < 1500) WindowRefreshIntervalMs = 1500;
        if (WindowRefreshIntervalMs > 10000) WindowRefreshIntervalMs = 10000;
    }
}
