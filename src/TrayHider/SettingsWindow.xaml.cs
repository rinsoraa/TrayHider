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

    public SettingsWindow(MainViewModel viewModel, MainWindow owner)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _owner = owner;
        DataContext = viewModel;

        Loaded += (_, _) => WindowChrome.Apply(this);
    }

    public string ConfigPathText => AppConfig.ConfigPath;

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
