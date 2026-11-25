using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrayHider.Services;

/// <summary>
/// 运行期文案本地化单例（接口由 Lead 定死，勿改签名）。
///
/// 用法：
///   · XAML：<c>Text="{Binding Source={x:Static services:Loc.Instance}, Path=[设置.语言]}"</c>
///   · 代码：<c>Loc.T("Core.Status.Ready")</c> 或 <c>Loc.T("Core.State.HiddenN", count)</c>
///   · 切换：<c>Loc.ApplyLanguage("auto" | "zh" | "en")</c>
///
/// 文案按「语言 + 区域」分文件：<c>Services/Localization/Strings.&lt;lang&gt;.&lt;area&gt;.json</c>
/// （area 目前有 ui / core / app，由不同人分头维护），同一语言的全部区域文件会被合并；
/// 缺键回退成键名并 Debug 输出，任何加载/解析失败都不抛异常（界面宁可显示键名也不能崩）。
/// 切换语言时对本单例触发 <c>PropertyChanged(null)</c>（让所有 <c>{Binding [键]}</c> 刷新）并 raise <see cref="LanguageChanged"/>。
/// </summary>
internal sealed class Loc : INotifyPropertyChanged
{
    private const string ResourceMarker = "Strings.";
    private const string JsonSuffix = ".json";

    /// <summary>简体中文的语言码。</summary>
    public const string Chinese = "zh";

    /// <summary>英文的语言码。</summary>
    public const string English = "en";

    /// <summary>"自动检测"的偏好值。</summary>
    public const string Auto = "auto";

    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);

    private Dictionary<string, string> _map = EmptyMap;
    private string _language = Chinese;

    private Loc()
    {
    }

    /// <summary>全局唯一实例，供 XAML 绑定使用。</summary>
    public static Loc Instance { get; } = new();

    /// <summary>语言切换后触发（sender 为 <see cref="Loc"/> 实例）。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>解析后的当前语言：<c>"zh"</c> 或 <c>"en"</c>。</summary>
    public static string CurrentLanguage => Instance._language;

    /// <summary>按键取值（XAML 绑定入口）；缺失时回退成键名。</summary>
    public string this[string key] => Get(key);

    /// <summary>取文案（无占位符）。</summary>
    internal static string T(string key) => Instance.Get(key);

    /// <summary>取文案并按 <see cref="string.Format(string, object[])"/> 填充 <c>{0}</c> 之类占位符。</summary>
    internal static string T(string key, params object?[] args)
    {
        var text = Instance.Get(key);
        if (args is null || args.Length == 0)
        {
            return text;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException ex)
        {
            // 文案里的占位符和实参对不上时不要崩，原样返回并留下痕迹
            Debug.WriteLine($"[Loc] 文案格式化失败 key={key}: {ex.Message}");
            return text;
        }
    }

    /// <summary>
    /// 应用语言偏好：<c>"auto"</c> / <c>"zh"</c> / <c>"en"</c>（大小写不敏感）。
    /// <c>"auto"</c>、空值或无法识别的值都按系统界面语言判定：zh 开头 → 中文，其余 → 英文。
    /// 语言未变化时不重复触发通知。
    /// </summary>
    public static void ApplyLanguage(string preference)
    {
        var target = Resolve(preference);
        var instance = Instance;
        if (string.Equals(instance._language, target, StringComparison.Ordinal) && instance._map.Count > 0)
        {
            return;
        }

        instance._language = target;
        instance._map = LoadLanguage(target);

        // 绑定 [键] 的控件靠 null 属性名整体刷新
        instance.OnPropertyChanged(null);

        try
        {
            LanguageChanged?.Invoke(instance, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loc] LanguageChanged 处理异常: {ex.Message}");
        }
    }

    /// <summary>把偏好值解析成实际语言码（zh / en）。</summary>
    internal static string Resolve(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference) ||
            string.Equals(preference, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return DetectFromCulture();
        }

        var value = preference.Trim().ToLowerInvariant();
        if (value.StartsWith("zh", StringComparison.Ordinal)) return Chinese;
        if (value.StartsWith("en", StringComparison.Ordinal)) return English;

        // 认不出来的偏好（例如以后新增的语言被旧版本读到）按系统语言处理
        return DetectFromCulture();
    }

    /// <summary>按系统界面语言判定：zh 开头 → 中文，其余 → 英文。</summary>
    internal static string DetectFromCulture()
    {
        try
        {
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? Chinese
                : English;
        }
        catch
        {
            return English;
        }
    }

    private string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_map.Count == 0)
        {
            // 首次访问发生在任何 ApplyLanguage 之前（例如单元检查、模型静态构造）：
            // 先把默认语言装上，否则会返回键名。
            ApplyLanguage(Auto);
        }

        if (_map.TryGetValue(key, out var value))
        {
            return value;
        }

        Debug.WriteLine($"[Loc] 缺少文案键: {key}（语言 {_language}）");
        return key;
    }

    /// <summary>
    /// 加载指定语言的全部区域文件并合并。区域文件由多人分头维护，
    /// 所以这里按文件名排序后逐个覆盖（后读到的同名键胜出），保证结果稳定；
    /// 任何单个文件坏掉都只影响它自己。
    /// </summary>
    private static Dictionary<string, string> LoadLanguage(string language)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, json) in ReadSources())
        {
            if (!TryParseName(name, out var lang, out _)) continue;
            if (!string.Equals(lang, language, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var text = prop.Value.GetString();
                    if (text is null) continue;
                    merged[prop.Name] = text;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loc] 解析文案文件失败 {name}: {ex.Message}");
            }
        }

        if (merged.Count == 0)
        {
            Debug.WriteLine($"[Loc] 语言 {language} 没有任何可用文案，界面会显示键名");
        }
        return merged;
    }

    /// <summary>
    /// 文案来源：优先内嵌资源（发布形态），其次程序目录下的同名文件（开发/自检用）。
    /// 两种来源都缺失时不抛异常，只是界面显示键名。
    /// </summary>
    private static IEnumerable<(string Name, string Json)> ReadSources()
    {
        var assembly = typeof(Loc).Assembly;
        string[] names;
        try
        {
            names = assembly.GetManifestResourceNames();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loc] 读取内嵌资源清单失败: {ex.Message}");
            names = Array.Empty<string>();
        }

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (name.IndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!name.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            string? json = null;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loc] 读取内嵌资源失败 {name}: {ex.Message}");
            }

            if (json is not null) yield return (name, json);
        }

        var dir = GetDiskFallbackDirectory();
        if (dir is null) yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, ResourceMarker + "*" + JsonSuffix);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loc] 枚举文案目录失败: {ex.Message}");
            yield break;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string? json = null;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loc] 读取文案文件失败 {file}: {ex.Message}");
            }

            if (json is not null) yield return (file, json);
        }
    }

    private static string? GetDiskFallbackDirectory()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) return null;

            var direct = Path.Combine(baseDir, "Services", "Localization");
            if (Directory.Exists(direct)) return direct;

            var flat = Path.Combine(baseDir, "Localization");
            if (Directory.Exists(flat)) return flat;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loc] 定位文案目录失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>从 <c>Strings.&lt;lang&gt;.&lt;area&gt;.json</c> 里解析语言码与区域名。</summary>
    private static bool TryParseName(string name, out string language, out string area)
    {
        language = string.Empty;
        area = string.Empty;

        // 内嵌资源名会带命名空间前缀（TrayHider.Services.Localization.Strings.zh.ui.json），
        // 文件系统路径则可能是绝对路径，两者都从最后一个 "Strings." 开始截取。
        var marker = name.LastIndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        var file = name[marker..];
        if (!file.StartsWith(ResourceMarker, StringComparison.OrdinalIgnoreCase)) return false;
        if (!file.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase)) return false;

        var body = file.AsSpan(ResourceMarker.Length, file.Length - ResourceMarker.Length - JsonSuffix.Length);
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot >= body.Length - 1) return false;

        language = body[..dot].ToString();
        area = body[(dot + 1)..].ToString();
        return language.Length > 0 && area.Length > 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}