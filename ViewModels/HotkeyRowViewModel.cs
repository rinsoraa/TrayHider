using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrayHider.Services;

namespace TrayHider.ViewModels;

/// <summary>
/// 设置界面里的一行快捷键：动作名 + 说明 + 当前组合 + “修改”按钮状态。
/// </summary>
public sealed class HotkeyRowViewModel : INotifyPropertyChanged
{
    private string _text;
    private bool _isRecording;

    public HotkeyRowViewModel(string action, string text)
    {
        Action = action;
        Title = HotkeyCatalog.Title(action);
        Description = HotkeyCatalog.Description(action);
        _text = text;
    }

    /// <summary>动作名，见 <see cref="HotkeyActions"/>。</summary>
    public string Action { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>当前快捷键的可读文本，如 “Ctrl + Win + T”。</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    /// <summary>是否正在录制新组合键。</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(ButtonText));
        }
    }

    public string ButtonText => IsRecording ? "请按下新快捷键…" : "修改";

    public void UpdateText(string text)
    {
        Text = text;
        IsRecording = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
