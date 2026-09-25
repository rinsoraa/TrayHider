using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TrayHider.Services;
using TrayHider.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace TrayHider;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _owner;
    private bool _recordingHotkey;

    public SettingsWindow(MainViewModel viewModel, MainWindow owner)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _owner = owner;
        DataContext = viewModel;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;

        Loaded += (_, _) => WindowChrome.Apply(this);
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        ChangeHotkeyButton.Content = "按下新的快捷键";
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Escape)
        {
            StopRecording();
            return;
        }

        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
            System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
            System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
            System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            return;
        }

        var modifierKeys = System.Windows.Input.Keyboard.Modifiers;
        var modifiers = 0;
        if (modifierKeys.HasFlag(System.Windows.Input.ModifierKeys.Control)) modifiers |= 0x0002;
        if (modifierKeys.HasFlag(System.Windows.Input.ModifierKeys.Alt)) modifiers |= 0x0001;
        if (modifierKeys.HasFlag(System.Windows.Input.ModifierKeys.Shift)) modifiers |= 0x0004;
        if (modifierKeys.HasFlag(System.Windows.Input.ModifierKeys.Windows)) modifiers |= 0x0008;

        if (modifiers == 0)
        {
            System.Windows.MessageBox.Show(this,
                "快捷键至少需要一个修饰键，例如 Ctrl、Alt、Shift 或 Win。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return;
        }

        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        var app = (App)System.Windows.Application.Current;
        if (!app.TryUpdateHotkey(modifiers, virtualKey))
        {
            System.Windows.MessageBox.Show(this,
                "快捷键注册失败，可能已被其他软件占用，请换一个组合。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StopRecording();
    }

    private void StopRecording()
    {
        _recordingHotkey = false;
        ChangeHotkeyButton.Content = "修改快捷键";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch
        {
            // 忽略
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RestoreAll();
        MessageBox.Show(this,
            "已清空所有隐藏记录。\n\n图标将在对应程序重新注册后回到托盘；若希望立即恢复，可重启对应的程序。",
            "托盘图标管家",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppConfig.ConfigDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开目录：{ex.Message}", "托盘图标管家",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "确定要退出托盘图标管家吗？\n\n退出后，已被隐藏的图标将在对应程序重新注册后回到托盘，本软件也不再提供隐藏守护。",
            "确认退出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Close();
            _owner.RequestShutdown();
        }
    }
}
