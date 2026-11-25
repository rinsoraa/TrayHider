using System.ComponentModel;
using TrayHider.Services;

using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TrayHider.Models;

/// <summary>
/// 一个可以被隐藏到托盘的顶层窗口，直接绑定到主窗口的『窗口』列表。
/// 数据来自 EnumWindows 的实时枚举；被隐藏后依然保留在列表里（以内部隐藏集合为准）。
/// </summary>
public sealed class WindowItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _processName = string.Empty;
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _isHidden;
    private bool _isRunning = true;

    /// <summary>顶层窗口句柄，这是本条目在系统中的唯一身份。</summary>
    public IntPtr Hwnd { get; set; }

    /// <summary>窗口标题。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>所属进程名（不含扩展名）。</summary>
    public string ProcessName
    {
        get => _processName;
        set
        {
            if (_processName == value) return;
            _processName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubTitle));
        }
    }

    /// <summary>所属进程可执行文件完整路径（权限不足时为空）。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>所属进程 ID。</summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// 窗口图标是否已经探测过。
    /// 探测失败的窗口（例如以管理员权限运行、读不到图标）不再每轮重试，避免反复跨进程发消息。
    /// </summary>
    public bool IconChecked { get; set; }

    /// <summary>窗口图标。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否被勾选（勾选即代表"我要隐藏它"）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前是否已被本软件隐藏到托盘。</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>窗口所属进程是否仍在运行。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubTitle));
        }
    }

    /// <summary>状态徽标文案。</summary>
    public string StateText => IsHidden ? CoreText.T("Core.State.Hidden") : CoreText.T("Core.State.Visible");

    /// <summary>次级文案：进程名 · 运行中/未运行。</summary>
    public string SubTitle
    {
        get
        {
            var process = string.IsNullOrEmpty(ProcessName) ? CoreText.T("Core.SubTitle.UnknownProcess") : ProcessName;
            var running = IsRunning ? CoreText.T("Core.SubTitle.Running") : CoreText.T("Core.SubTitle.NotRunning");
            return CoreText.T("Core.SubTitle.Format", process, running);
        }
    }

    /// <summary>语言切换后刷新所有本地化文本（状态与副标题）。</summary>
    internal void RaiseLocalizedChanged()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SubTitle));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
