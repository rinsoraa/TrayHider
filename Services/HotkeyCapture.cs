using System.Runtime.InteropServices;

namespace TrayHider.Services;

/// <summary>
/// 快捷键录入的纯逻辑规则（不依赖 WPF 控件，便于单独测试）。
/// 背景：WPF 的 <c>Keyboard.Modifiers</c> 拿不到 Win 键 —— 系统会先把 Win 组合消费掉，
/// 主窗口的 PreviewKeyDown 根本收不到；因此界面提供显式「附加 Win」勾选，
/// 同时把"已被本软件占用、因而收得到 WM_HOTKEY"的组合路由进录入（见 SettingsWindow.TryCaptureRegisteredHotkey）。
/// </summary>
public static class HotkeyCapture
{
    public const int ModAlt = 0x0001;
    public const int ModCtrl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>四种修饰键的位掩码并集。</summary>
    public const int AllModifiers = ModAlt | ModCtrl | ModShift | ModWin;

    /// <summary>只按下了修饰键本身（Ctrl / Alt / Shift / Win 及其左右键）：此时应继续等主键。</summary>
    public static bool IsModifierOnlyKey(int virtualKey)
        => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>
    /// 已知被 Windows 自己占用、本软件既录不到也注册不上的组合（示例而非穷举）。
    /// 用途：给出"系统保留"的明确提示，而不是让用户对着没反应的按键发懵。
    /// </summary>
    public static bool IsSystemReserved(int modifiers, int virtualKey)
    {
        if ((modifiers & ModWin) == 0) return false;

        var onlyWin = (modifiers & AllModifiers) == ModWin;
        var winShift = (modifiers & AllModifiers) == (ModWin | ModShift);

        // 系统只独占「恰好是 Win + 某键」的组合；多按一个 Ctrl/Alt 就是另一套组合，可以注册。
        // Win + L 锁屏、Win + D 显示桌面、Win + E 资源管理器、Win + R 运行、
        // Win + Tab 任务视图、Win + S 搜索、Win + M 最小化、Win + A 操作中心、
        // Win + G 录屏、Win + V 剪贴板、Win + P 投影
        if (onlyWin && virtualKey is 0x4C or 0x44 or 0x45 or 0x52 or 0x09 or 0x53 or 0x4D or 0x41 or 0x47 or 0x56 or 0x50)
        {
            return true;
        }

        // Win + Shift + S 截图
        if (winShift && virtualKey == 0x53) return true;

        if (onlyWin && virtualKey is >= 0x25 and <= 0x28) return true;   // Win + 方向键（分屏）
        if (onlyWin && virtualKey is >= 0x30 and <= 0x39) return true;   // Win + 数字（任务栏）
        return false;
    }

    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    /// <summary>
    /// Win 键此刻是否物理按下。WPF 的 Keyboard.Modifiers 经常报不到 Win（系统先消费），
    /// 且 Keyboard.IsKeyDown(Key.LWin) 在部分系统上也不更新，所以以 GetKeyState 为准。
    /// </summary>
    public static bool IsWinKeyDown()
    {
        try
        {
            return (GetKeyState(VkLWin) & 0x8000) != 0 || (GetKeyState(VkRWin) & 0x8000) != 0;
        }
        catch
        {
            // 取不到就当没按（录入仍可通过「附加 Win」勾选完成）
            return false;
        }
    }

    /// <summary>
    /// 组合是否可用。可用返回 null，不可用返回给用户看的中文原因。
    /// </summary>
    public static string? Problem(int modifiers, int virtualKey)
    {
        if (virtualKey <= 0)
        {
            return Loc.T("Hotkey.Error.NoKey");
        }

        if ((modifiers & AllModifiers) == 0)
        {
            return Loc.T("Hotkey.Error.NoModifier");
        }

        if (IsModifierOnlyKey(virtualKey))
        {
            return Loc.T("Hotkey.Error.ModifierOnly");
        }

        if (IsSystemReserved(modifiers, virtualKey))
        {
            return Loc.T("Hotkey.Error.SystemReserved");
        }

        return null;
    }
}