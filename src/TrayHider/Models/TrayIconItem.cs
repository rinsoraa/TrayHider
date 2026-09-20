using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TrayHider.Models;

/// <summary>
/// 托盘中的一个图标条目，直接绑定到主窗口列表。
/// 数据来自 HKCU\Control Panel\NotifyIconSettings。
/// </summary>
public sealed class TrayIconItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isHidden;
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

    /// <summary>当前是否已处于隐藏状态。</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden != value) { _isHidden = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateText)); }
        }
    }

    public string StateText => IsHidden ? "已隐藏" : "显示中";

    public string SubTitle
    {
        get
        {
            var proc = string.IsNullOrEmpty(ProcessName) ? "(进程名未知)" : ProcessName;
            var run = IsRunning ? "运行中" : "未运行";
            return $"{proc}  ·  {run}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>供外部在进程状态变化后触发 SubTitle 重算。</summary>
    internal void RaiseSubTitleChanged() => OnPropertyChanged(nameof(SubTitle));

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
