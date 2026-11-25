namespace TrayHider.Services;

/// <summary>
/// 快捷键动作的界面文案，以及快捷键卡片上共用的提示语。
/// 文案统一走 <see cref="Loc"/>（ui 区键前缀 <c>Hotkey.*</c>），避免各处重复写死中文，也能随语言切换。
/// </summary>
public static class HotkeyCatalog
{
    /// <summary>没有绑定快捷键时显示的文字（与 <see cref="HotkeyBinding.ToString"/> 的 0/0 哨兵一致）。</summary>
    public static string UnassignedText => Loc.T("Common.Unassigned");

    /// <summary>每行「×」按钮的悬停提示。</summary>
    public static string ClearButtonTip(string action) => Loc.T("Hotkey.Clear.Tip", Title(action));

    /// <summary>动作的简短名称。</summary>
    public static string Title(string action) => Loc.T("Hotkey." + Key(action) + ".Title");

    /// <summary>动作说明。</summary>
    public static string Description(string action) => Loc.T("Hotkey." + Key(action) + ".Description");

    /// <summary>动作名 → 文案键里的动作段（新增动作时在这里补一行即可）。</summary>
    private static string Key(string action) => action switch
    {
        HotkeyActions.ShowMain => "ShowMain",
        HotkeyActions.ToggleHideWindow => "ToggleHideWindow",
        HotkeyActions.HideAllWindows => "HideAllWindows",
        HotkeyActions.RestoreAllWindows => "RestoreAllWindows",
        HotkeyActions.ToggleTrayIcons => "ToggleTrayIcons",
        _ => action
    };
}
