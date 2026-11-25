namespace TrayHider.Services;

/// <summary>
/// 快捷键动作的界面文案，以及快捷键卡片上共用的提示语。
/// 设置界面与状态提示共用，避免各处重复写死中文。
/// </summary>
public static class HotkeyCatalog
{
    /// <summary>没有绑定快捷键时显示的文字（与 <see cref="HotkeyBinding.ToString"/> 的 0/0 哨兵一致）。</summary>
    public const string UnassignedText = "未设置";

    /// <summary>每行「×」按钮的悬停提示。</summary>
    public static string ClearButtonTip(string action) => "清空「" + Title(action) + "」的快捷键（不会影响其它动作）";

    /// <summary>「仅重置快捷键」按钮文字。</summary>
    public const string ResetButtonText = "仅重置快捷键";

    /// <summary>「附加 Win」勾选框文字。</summary>
    public const string WinCheckText = "附加 Win";

    /// <summary>「附加 Win」勾选框的悬停提示。</summary>
    public const string WinCheckTip =
        "系统会先消费 Win 组合，主窗口收不到 Win 键。需要 Ctrl + Win + T 这类组合时，先勾选此项，再按 Ctrl + T 即可。";

    /// <summary>录入区的操作提示。</summary>
    public const string CaptureHint =
        "点「修改」后按下组合键；按 Delete 清空该行，Esc 取消。只重置快捷键不会改动隐藏记录与其它开关。";

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
