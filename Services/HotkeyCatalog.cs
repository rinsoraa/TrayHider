namespace TrayHider.Services;

/// <summary>
/// 快捷键动作的界面文案。设置界面与状态提示共用，避免各处重复写死中文。
/// </summary>
public static class HotkeyCatalog
{
    /// <summary>动作的简短名称。</summary>
    public static string Title(string action) => action switch
    {
        HotkeyActions.ShowMain => "唤出主窗口",
        HotkeyActions.ToggleHideWindow => "隐藏 / 恢复当前窗口",
        HotkeyActions.HideAllWindows => "隐藏全部窗口",
        HotkeyActions.RestoreAllWindows => "显示全部窗口",
        HotkeyActions.ToggleTrayIcons => "全部隐藏 / 恢复托盘图标",
        _ => action
    };

    /// <summary>动作说明。</summary>
    public static string Description(string action) => action switch
    {
        HotkeyActions.ShowMain => "无论本软件图标是否隐藏，都可用该快捷键唤出主窗口。",
        HotkeyActions.ToggleHideWindow => "把当前前台窗口收进托盘；再按一次把它恢复回来。",
        HotkeyActions.HideAllWindows => "把当前所有可隐藏的窗口收进托盘，进程继续运行。",
        HotkeyActions.RestoreAllWindows => "把所有被收进托盘的窗口一次性恢复显示。",
        HotkeyActions.ToggleTrayIcons => "托盘里还有图标就全部隐藏，全部已隐藏则全部恢复。",
        _ => string.Empty
    };
}
