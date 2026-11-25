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

    /// <summary>显式清空的绑定。缺少动作表示沿用默认值，(0,0) 则表示停用，重启不能补回。</summary>
    [JsonIgnore]
    public bool IsUnassigned => Modifiers == 0 && VirtualKey == 0;

    /// <summary>有效组合：四种修饰键中的至少一种，加一个非修饰主键。</summary>
    [JsonIgnore]
    public bool IsValid => Modifiers is > 0 and <= HotkeyCapture.AllModifiers && VirtualKey is > 0 and < 0xFF
        && !HotkeyCapture.IsModifierOnlyKey(VirtualKey);

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
        parts.Add(key == Key.None ? HotkeyCatalog.UnassignedText : key.ToString());
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
    // 只有 Load() 载入的真实配置或显式传入路径的实例才能落盘。
    // 普通 new / FromJson 只表示内存数据，绝不能自动指向用户的 Roaming 目录。
    private string? _savePath;

    public AppConfig() { }

    // 同一套持久化逻辑也能用于隔离配置；测试中的回归绝不能写入真实用户目录。
    internal AppConfig(string savePath) => _savePath = Path.GetFullPath(savePath);

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

    /// <summary>界面语言：auto（跟随系统）/ zh / en。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// 隐藏图标后不常驻：登录时只应用一次隐藏状态就退出进程，之后由计划任务定期补应用。
    /// 隐藏状态本身由 Shell 保存，所以进程退出后图标仍然是隐藏的。
    /// </summary>
    public bool RunOnceAndExit { get; set; }

    /// <summary>
    /// 不常驻模式下是否启用"周期轮询"（计划任务每 N 分钟补应用一次）。
    /// 关闭时只在登录后应用一遍，等统计中的图标全部隐藏完就静默退出，不创建计划任务。
    /// </summary>
    public bool RunOncePolling { get; set; }

    /// <summary>不常驻模式下补应用隐藏状态的间隔（分钟，1~15）。</summary>
    public int RunOnceIntervalMinutes { get; set; } = 10;

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
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>旧版记录中已知不响应 TaskbarCreated 的程序（进程名，不含 .exe）。
    /// 新隐藏不依赖该消息；名单只记录旧注册的恢复情况，不用于判定状态位操作是否成功。</summary>
    public List<string> RebuildUnsupported { get; set; } = new();

    /// <summary>兼容名单的迁移版本，与应用版本号无关。</summary>
    public int RebuildSeedRevision { get; set; }

    /// <summary>已报告不响应托盘重建的进程。用于旧备份的处理，不是删除原图标的许可。</summary>
    internal static readonly string[] KnownUnsupportedSeed =
    {
        "HipsTray", "nvcontainer", "NVDisplay.Container", "ACE-Tray", "WitchDrawer"
    };

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

    public static AppConfig Load() => Load(ConfigPath);

    internal static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = FromJson(File.ReadAllText(path));
                config._savePath = Path.GetFullPath(path);
                return config;
            }
        }
        catch
        {
            // 配置损坏时回退到默认值，不影响启动
        }

        var fresh = new AppConfig(path);
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        if (_savePath is not { } path) return;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, ToJson());
            // 在同一目录完成替换。保存失败不截断原配置；保存成功仍能找回上一版。
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        catch
        {
            // 文件不可写时保持原配置，不影响当前进程运行。
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static AppConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new();
        config.Normalize();
        return config;
    }

    internal string ToJson()
    {
        SyncLegacyHotkeyFields();
        return JsonSerializer.Serialize(this, JsonOptions);
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
        RebuildUnsupported ??= new List<string>();

        Language = Language?.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "en" or "en-us" => "en",
            _ => "auto"
        };

        RunOnceIntervalMinutes = Math.Clamp(RunOnceIntervalMinutes, 1, 15);

        // 进程名归一化（去 .exe、去空白），保证学习记录能命中
        for (var i = 0; i < RebuildUnsupported.Count; i++)
        {
            var name = (RebuildUnsupported[i] ?? string.Empty).Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            RebuildUnsupported[i] = name;
        }
        RebuildUnsupported.RemoveAll(string.IsNullOrEmpty);

        // v1.2.1 没有名单版本号。升级时保留已学习名单、补入新样本；不在每次启动时重加已撤销的条目。
        if (RebuildSeedRevision < 1)
        {
            if (RebuildUnsupported.Count == 0) RebuildUnsupported.AddRange(KnownUnsupportedSeed);
            else if (!RebuildUnsupported.Contains("WitchDrawer", StringComparer.OrdinalIgnoreCase))
                RebuildUnsupported.Add("WitchDrawer");
            RebuildSeedRevision = 1;
        }

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
            if (!binding.IsUnassigned && !binding.IsValid) Hotkeys[action] = defaults[action];
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
