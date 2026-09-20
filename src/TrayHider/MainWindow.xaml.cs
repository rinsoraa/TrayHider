using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TrayHider.Models;
using TrayHider.Services;
using TrayHider.ViewModels;
using Application = System.Windows.Application;

namespace TrayHider;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<TrayIconItem, DispatcherTimer> _clickTimers = new();
    private SettingsWindow? _settingsWindow;
    private bool _reallyClosing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowChrome.Apply(this);
        _viewModel.Start();
    }

    // ---------------------------------------------------------------- 标题栏

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 快速双击时 DragMove 可能抛异常，忽略
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyClosing) return;

        // 关闭窗口默认最小化到托盘，保证后台守护持续生效
        e.Cancel = true;
        Hide();
        _viewModel.StatusText = "已最小化到托盘，隐藏守护继续运行";
    }

    /// <summary>真正退出程序。</summary>
    public void RequestShutdown()
    {
        _reallyClosing = true;
        _viewModel.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>从托盘或快捷键唤出主窗口。</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ---------------------------------------------------------------- 列表交互

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrayIconItem item }) return;

        if (e.ClickCount == 2)
        {
            // 双击：取消待处理的单击，直接切换隐藏/显示（不影响勾选）
            if (_clickTimers.Remove(item, out var pending))
            {
                pending.Stop();
            }
            _viewModel.ToggleItem(item);
            return;
        }

        // 单击：延迟执行，等待判断是否为双击
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _clickTimers.Remove(item);
            item.IsSelected = !item.IsSelected;
            _viewModel.UpdateCounters();
        };
        _clickTimers[item] = timer;
        timer.Start();
    }

    // ---------------------------------------------------------------- 工具条动作

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAll();
        _viewModel.StatusText = "已勾选全部图标";
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InvertSelection();
        _viewModel.StatusText = "已反选";
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelection();
        _viewModel.StatusText = "已取消所有勾选";
    }

    private void HideSelected_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplySelected();

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
        => _viewModel.RestoreSelected();

    private void HideAll_Click(object sender, RoutedEventArgs e)
        => _viewModel.HideAll();

    private void ShowAll_Click(object sender, RoutedEventArgs e)
        => _viewModel.RestoreAll();

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_viewModel, this)
        {
            Owner = this
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
}
