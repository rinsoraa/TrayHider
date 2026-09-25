using System.Diagnostics;
using Microsoft.Win32;

namespace TrayHider.Services;

/// <summary>
/// 开机自启管理。写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 仅影响当前用户，无需管理员权限。
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TrayHider";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = GetExecutablePath();
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前可执行文件路径。开发期由 dotnet 宿主启动时，
    /// 使用主模块路径以确保写入的是真实 exe。
    /// </summary>
    private static string GetExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            using var process = Process.GetCurrentProcess();
            var main = process.MainModule?.FileName;
            return main ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
