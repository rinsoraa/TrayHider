using System.IO;
using Microsoft.Win32;

namespace TrayHider.Interop;

/// <summary>
/// Windows 11 托盘图标读取器。
///
/// 背景（经本机实测确认）：
///   Windows 11 的任务栏已改为 XAML 实现，承载托盘图标的
///   ToolbarWindow32 窗口被微软移除。传统 TB_GETBUTTON 方案已失效。
///
/// 数据源：
///   HKCU\Control Panel\NotifyIconSettings
///   每个子键代表一个注册过的托盘图标，含 ExecutablePath / UID / IconGuid /
///   InitialTooltip / IconSnapshot / IsPromoted。
///
/// 说明：本类只负责"读取"枚举数据。真正的隐藏/显示由 TrayIconController
/// 通过 Shell_NotifyIcon 的 NIM_DELETE / NIM_ADD 完成（跨进程、立即生效）。
/// </summary>
internal sealed class TrayIconReader
{
    internal const string RegistryPath = @"Control Panel\NotifyIconSettings";

    /// <summary>读取全部注册过的托盘图标条目。</summary>
    public IReadOnlyList<TrayIconEntry> ReadAll()
    {
        var results = new List<TrayIconEntry>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (root == null) return results;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(subKeyName);
                if (key == null) continue;

                var rawPath = key.GetValue("ExecutablePath") as string;
                if (string.IsNullOrWhiteSpace(rawPath)) continue;

                var resolved = ResolveExecutablePath(rawPath);
                if (string.IsNullOrEmpty(resolved)) continue;

                var entry = new TrayIconEntry
                {
                    SubKeyName = subKeyName,
                    ExecutablePath = resolved,
                    Uid = ReadUid(key),
                    ToolTip = key.GetValue("InitialTooltip") as string ?? string.Empty,
                    Publisher = key.GetValue("Publisher") as string ?? string.Empty,
                    IconGuid = key.GetValue("IconGuid") as string ?? string.Empty,
                    IconSnapshot = key.GetValue("IconSnapshot") as byte[]
                };

                var promoted = key.GetValue("IsPromoted");
                entry.IsPromoted = promoted == null ? null : Convert.ToInt32(promoted) != 0;

                results.Add(entry);
            }
        }
        catch
        {
            // 读取失败时返回已收集的部分
        }

        return results;
    }

    /// <summary>读取 UID（NOTIFYICONDATA 的 uID 标识）。</summary>
    private static uint? ReadUid(RegistryKey key)
    {
        try
        {
            var value = key.GetValue("UID");
            if (value == null) return null;
            return Convert.ToUInt32(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析可执行路径。注册表中可能存的是形如
    /// {6D809377-6AF0-444B-8957-A3773F02200E}\Microsoft OneDrive\OneDrive.exe
    /// 的已知文件夹 GUID 形式，需要还原成真实路径。
    /// </summary>
    internal static string ResolveExecutablePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var path = raw.Trim();

        if (path.StartsWith("{"))
        {
            var end = path.IndexOf('}');
            if (end > 0)
            {
                var guid = path[..(end + 1)];
                var rest = path[(end + 1)..].TrimStart('\\');

                var baseDir = KnownFolderMap.Resolve(guid);
                if (baseDir != null)
                {
                    path = Path.Combine(baseDir, rest);
                }
            }
        }

        path = Environment.ExpandEnvironmentVariables(path);

        // 无法解析的 GUID 路径直接放弃，避免产生无效条目
        if (path.StartsWith("{")) return string.Empty;

        return path;
    }
}

/// <summary>托盘图标条目（来自注册表的原始数据）。</summary>
public sealed class TrayIconEntry
{
    public string SubKeyName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public uint? Uid { get; set; }
    public string ToolTip { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string IconGuid { get; set; } = string.Empty;
    public byte[]? IconSnapshot { get; set; }

    /// <summary>注册图标所用窗口的类名（隐藏时记录，供恢复时按类名重新定位窗口）。</summary>
    public string WindowClassName { get; set; } = string.Empty;

    /// <summary>null 表示未设置（跟随系统默认）。仅用于展示图标所在面板。</summary>
    public bool? IsPromoted { get; set; }

    /// <summary>显示名：优先提示文本，其次文件名。</summary>
    public string DisplayName
    {
        get
        {
            var tip = ToolTip.Trim();
            if (!string.IsNullOrEmpty(tip))
            {
                var line = tip.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }

            try
            {
                return Path.GetFileNameWithoutExtension(ExecutablePath);
            }
            catch
            {
                return ExecutablePath;
            }
        }
    }
}

/// <summary>已知文件夹 GUID → 真实路径。</summary>
internal static class KnownFolderMap
{
    private static readonly Dictionary<string, Environment.SpecialFolder> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,      // ProgramFilesX64
        ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = Environment.SpecialFolder.ProgramFiles,      // ProgramFiles
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,  // ProgramFilesX86
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,           // System32
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.SpecialFolder.SystemX86,        // SysWOW64
        ["{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}"] = Environment.SpecialFolder.Programs,         // Programs（开始菜单）
        ["{DE974D24-D9C6-4D3E-BF91-F4455120B917}"] = Environment.SpecialFolder.CommonProgramFilesX86 // ProgramFilesCommonX86
    };

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string guid)
    {
        if (Cache.TryGetValue(guid, out var cached)) return cached;

        string? result = null;
        if (Map.TryGetValue(guid, out var folder))
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path)) result = path;
            }
            catch
            {
                // 忽略
            }
        }

        if (result != null) Cache[guid] = result;
        return result;
    }
}
