using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TrayHider.Services;

namespace TrayHider.ViewModels;

/// <summary>
/// 设置界面里的一行快捷键：动作名 + 说明 + 当前组合 + “修改”“清空”按钮状态。
/// </summary>
public sealed class HotkeyRowViewModel : INotifyPropertyChanged
{
    private string _text;
    private bool _isRecording;
    private bool _hasHotkey = true;

    public HotkeyRowViewModel(string action, string text)
    {
        Action = action;
        Title = HotkeyCatalog.Title(action);
        Description = HotkeyCatalog.Description(action);
        ClearTip = HotkeyCatalog.ClearButtonTip(action);
        _text = text;
        _hasHotkey = !IsUnassignedText(text);
    }

    /// <summary>动作名，见 <see cref="HotkeyActions"/>。</summary>
    public string Action { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>「×」按钮的悬停提示。</summary>
    public string ClearTip { get; }

    /// <summary>当前快捷键的可读文本，如 “Ctrl + Win + T”；清空后为 “未设置”。</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    /// <summary>这一行当前是否有快捷键（清空后为 false，「×」按钮随之隐藏）。</summary>
    public bool HasHotkey
    {
        get => _hasHotkey;
        private set
        {
            if (Set(ref _hasHotkey, value)) OnPropertyChanged(nameof(ClearButtonVisibility));
        }
    }

    /// <summary>「×」按钮的可见性：没有快捷键时隐藏，避免重复清空。</summary>
    public Visibility ClearButtonVisibility => _hasHotkey ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>用新的可读文本刷新一行（同时结束录制状态）。</summary>
    public void UpdateText(string text)
    {
        Text = text;
        HasHotkey = !IsUnassignedText(text);
        IsRecording = false;
    }

    private static bool IsUnassignedText(string? text)
        => string.Equals(text, HotkeyCatalog.UnassignedText, StringComparison.Ordinal);

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
