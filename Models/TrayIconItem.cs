using System.ComponentModel;
using TrayHider.Services;

using System.Runtime.CompilerServices;
using System.Windows.Media;
using TrayHider.Interop;

namespace TrayHider.Models;

/// <summary>
/// 托盘中的一个图标条目，直接绑定到主窗口列表。
/// 数据来自 HKCU\Control Panel\NotifyIconSettings。
/// </summary>
public sealed class TrayIconItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isHidden;
    private bool _callbackDegraded;
    private string _displayName = string.Empty;
    private string _processName = string.Empty;
    private ImageSource? _icon;

    /// <summary>注册表子键名。这是本条目在系统中的唯一身份。</summary>
    public string SubKeyName { get; set; } = string.Empty;

    /// <summary>图标所属可执行文件的完整路径。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>图标在其进程内的标识（NOTIFYICONDATA 的 uID）。</summary>
    public uint? Uid { get; set; }

    /// <summary>图标的 GUID 标识（NOTIFYICONDATA 的 guidItem，部分图标才有）。</summary>
    public string IconGuid { get; set; } = string.Empty;

    /// <summary>图标位图快照字节，用于"显示"时重建图标。</summary>
    public byte[]? IconSnapshot { get; set; }

    /// <summary>发布者信息，用于在界面上补充说明。</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>该图标对应的进程当前是否正在运行。</summary>
    public bool IsRunning { get; set; }

    /// <summary>当前进程 ID（若正在运行）。</summary>
    public int ProcessId { get; set; }

    /// <summary>注册图标所用的窗口句柄（运行时获取，不持久化）。</summary>
    public IntPtr Hwnd { get; set; }

    /// <summary>注册图标所用窗口的类名（隐藏时记录，供重启后按类名重新定位窗口）。</summary>
    public string WindowClassName { get; set; } = string.Empty;

    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public string ProcessName
    {
        get => _processName;
        set { if (_processName != value) { _processName = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubTitle)); } }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set { if (!ReferenceEquals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
    }

    /// <summary>是否被勾选（勾选即代表"我要隐藏它"）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// 隐藏该图标时用的方式（非破坏性状态隐藏 / 破坏性删除）。
    /// 决定恢复走哪条路径：State → 清除状态位即可，回调不受影响；Delete → 需要程序自己重新注册。
    /// 由 MainViewModel 在隐藏/恢复时维护，随隐藏备份一起持久化。
    /// </summary>
    public TrayHideMode HideMode { get; set; } = TrayHideMode.Delete;

    /// <summary>
    /// 已经确认"这个程序不响应托盘重建、恢复不了"。
    /// 置位后守护逻辑不再反复重隐它 —— 否则程序自己重启后图标又被藏掉，
    /// 用户永远等不到它回来。仅运行时状态，不持久化。
    /// </summary>
    public bool RestoreBlocked { get; set; }

    /// <summary>当前是否已处于隐藏状态。</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden != value) { _isHidden = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateText)); }
        }
    }

    /// <summary>
    /// 恢复（显示）时目标程序未响应 TaskbarCreated，只能由本进程伪造 NIM_ADD 重建。
    /// 这种状态下图标看得见，但 Shell 会把鼠标消息投给目标程序并不处理的回调消息，
    /// 所以右键菜单/双击可能失效；重启该程序即可恢复（它会用自己的回调重新注册）。
    /// 程序重启后（进程号变化）或图标重新被隐藏时自动清除。
    /// </summary>
    public bool CallbackDegraded
    {
        get => _callbackDegraded;
        set
        {
            if (_callbackDegraded != value)
            {
                _callbackDegraded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string StateText => IsHidden
        ? CoreText.T("Core.State.Hidden")
        : _callbackDegraded ? CoreText.T("Core.State.Degraded") : CoreText.T("Core.State.Visible");

    public string SubTitle
    {
        get
        {
            var proc = string.IsNullOrEmpty(ProcessName) ? CoreText.T("Core.SubTitle.UnknownProcess") : ProcessName;
            var run = IsRunning ? CoreText.T("Core.SubTitle.Running") : CoreText.T("Core.SubTitle.NotRunning");
            return CoreText.T("Core.SubTitle.Format", proc, run);
        }
    }

    /// <summary>语言切换后刷新所有本地化文本（状态与副标题）。</summary>
    internal void RaiseLocalizedChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SubTitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>供外部在进程状态变化后触发 SubTitle 重算。</summary>
    internal void RaiseSubTitleChanged() => OnPropertyChanged(nameof(SubTitle));

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 核心层文案入口（MainViewModel / Models / WindowListViewModel / WindowHiderService 共用）。
///
/// 为什么还要这一层：模型可能在视图模型之外被直接构造（例如自检），
/// 若此时 <see cref="Loc"/> 还没 ApplyLanguage 过，取文案会退化成键名。
/// 这里做一次**幂等**的初始化兜底；视图模型随后用配置里的语言偏好覆盖即可。
/// </summary>
internal static class CoreText
{
    private static bool _initialized;

    /// <summary>确保 Loc 至少应用过一次（按系统界面语言判定）；重复调用无副作用。</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        Loc.ApplyLanguage("auto");
    }

    /// <summary>按键取文案。</summary>
    public static string T(string key)
    {
        EnsureInitialized();
        return Loc.T(key);
    }

    /// <summary>按键取文案并填充占位符。</summary>
    public static string T(string key, params object?[] args)
    {
        EnsureInitialized();
        return Loc.T(key, args);
    }
}
