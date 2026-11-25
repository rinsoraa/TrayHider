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
    private readonly MainViewModel _trayViewModel;
    private readonly WindowListViewModel _windowViewModel;

    /// <summary>托盘图标分区的单击延时定时器（用于区分单击勾选与双击隐藏/显示）。</summary>
    private readonly Dictionary<TrayIconItem, DispatcherTimer> _clickTimers = new();

    /// <summary>窗口分区的单击延时定时器（同上）。</summary>
    private readonly Dictionary<WindowItem, DispatcherTimer> _windowClickTimers = new();

    private SettingsWindow? _settingsWindow;
    private bool _reallyClosing;
    private bool _showingWindowSection;

    public MainWindow(MainViewModel trayViewModel, WindowListViewModel windowViewModel)
    {
        InitializeComponent();

        _trayViewModel = trayViewModel;
        _windowViewModel = windowViewModel;

        DataContext = trayViewModel;
        WindowPanel.DataContext = windowViewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        IsVisibleChanged += MainWindow_IsVisibleChanged;

        // 快捷键打到本软件自己的窗口上时，把主窗口收到托盘
        _windowViewModel.HideSelfRequested += (_, _) => MinimizeToTray();

        // 列表条目被移除时清理残留的单击延时定时器，避免字典无限增长
        _trayViewModel.Icons.CollectionChanged += (_, _) => PruneClickTimers();
        _windowViewModel.Windows.CollectionChanged += (_, _) => PruneWindowClickTimers();

        ApplySection(showWindows: false);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowChrome.Apply(this);
        _trayViewModel.Start();
        _windowViewModel.Start();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _trayViewModel.SetWindowVisible(IsVisible);
        _windowViewModel.SetWindowVisible(IsVisible);
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

        if (_trayViewModel.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            _trayViewModel.SetWindowVisible(false);
            _windowViewModel.SetWindowVisible(false);
            _trayViewModel.StatusText = "已最小化到托盘，隐藏守护继续运行";
            return;
        }

        RequestShutdown();
    }

    /// <summary>真正退出程序。</summary>
    public void RequestShutdown()
    {
        _reallyClosing = true;

        ClearClickTimers();
        ClearWindowClickTimers();

        // 退出前把被隐藏的窗口恢复回去，绝不把用户的窗口留在隐藏态
        _windowViewModel.RestoreAllOnExit();
        _windowViewModel.Dispose();

        _trayViewModel.Dispose();
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

        if (_showingWindowSection)
        {
            _windowViewModel.Refresh();
        }
    }

    /// <summary>把主窗口收到系统托盘（程序与快捷键继续可用）。</summary>
    private void MinimizeToTray()
    {
        Hide();
        _trayViewModel.SetWindowVisible(false);
        _windowViewModel.SetWindowVisible(false);
        _trayViewModel.StatusText = "已最小化到托盘，隐藏守护继续运行";
    }

    // ---------------------------------------------------------------- 分区切换

    private void TraySection_Click(object sender, RoutedEventArgs e) => ApplySection(showWindows: false);

    private void WindowSection_Click(object sender, RoutedEventArgs e) => ApplySection(showWindows: true);

    /// <summary>
    /// 用两个按钮拼出分段切换效果：选中项用 AccentButton，未选中项用 StandardButton，
    /// 同时切换两个分区容器的 Visibility（不引入 TabControl，避免改动主题样式）。
    /// </summary>
    private void ApplySection(bool showWindows)
    {
        _showingWindowSection = showWindows;

        TrayPanel.Visibility = showWindows ? Visibility.Collapsed : Visibility.Visible;
        WindowPanel.Visibility = showWindows ? Visibility.Visible : Visibility.Collapsed;

        TraySectionButton.Style = (Style)FindResource(showWindows ? "StandardButton" : "AccentButton");
        WindowSectionButton.Style = (Style)FindResource(showWindows ? "AccentButton" : "StandardButton");

        if (showWindows)
        {
            _windowViewModel.Refresh();
        }
    }

    // ---------------------------------------------------------------- 托盘图标列表交互

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

            _trayViewModel.ToggleItem(item);
            return;
        }

        // 单击：延迟执行，等待判断是否为双击
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _clickTimers.Remove(item);
            item.IsSelected = !item.IsSelected;
            _trayViewModel.UpdateCounters();
        };
        _clickTimers[item] = timer;
        timer.Start();
    }

    /// <summary>清理已经不在列表里的条目的延时定时器，防止定时器与字典无限累积。</summary>
    private void PruneClickTimers()
    {
        if (_clickTimers.Count == 0) return;

        foreach (var (item, timer) in _clickTimers.ToList())
        {
            if (_trayViewModel.Icons.Contains(item)) continue;

            timer.Stop();
            _clickTimers.Remove(item);
        }
    }

    private void ClearClickTimers()
    {
        foreach (var timer in _clickTimers.Values)
        {
            timer.Stop();
        }

        _clickTimers.Clear();
    }

    // ---------------------------------------------------------------- 窗口列表交互

    private void WindowRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WindowItem item }) return;

        if (e.ClickCount == 2)
        {
            if (_windowClickTimers.Remove(item, out var pending))
            {
                pending.Stop();
            }

            _windowViewModel.ToggleItem(item);
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _windowClickTimers.Remove(item);
            item.IsSelected = !item.IsSelected;
            _windowViewModel.UpdateCounters();
        };
        _windowClickTimers[item] = timer;
        timer.Start();
    }

    private void PruneWindowClickTimers()
    {
        if (_windowClickTimers.Count == 0) return;

        foreach (var (item, timer) in _windowClickTimers.ToList())
        {
            if (_windowViewModel.Windows.Contains(item)) continue;

            timer.Stop();
            _windowClickTimers.Remove(item);
        }
    }

    private void ClearWindowClickTimers()
    {
        foreach (var timer in _windowClickTimers.Values)
        {
            timer.Stop();
        }

        _windowClickTimers.Clear();
    }

    // ---------------------------------------------------------------- 托盘图标工具条动作

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _trayViewModel.SelectAll();
        _trayViewModel.StatusText = "已勾选全部图标";
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        _trayViewModel.InvertSelection();
        _trayViewModel.StatusText = "已反选";
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _trayViewModel.ClearSelection();
        _trayViewModel.StatusText = "已取消所有勾选";
    }

    private void HideSelected_Click(object sender, RoutedEventArgs e)
        => _trayViewModel.ApplySelected();

    private void ShowSelected_Click(object sender, RoutedEventArgs e)
        => _trayViewModel.RestoreSelected();

    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(this,
            "确定要隐藏全部托盘图标吗？",
            "确认全部隐藏",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _trayViewModel.HideAll();
        }
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
        => _trayViewModel.RestoreAll();

    // ---------------------------------------------------------------- 窗口工具条动作

    private void WindowSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _windowViewModel.SelectAll();
        _windowViewModel.StatusText = "已勾选全部窗口";
    }

    private void WindowInvert_Click(object sender, RoutedEventArgs e)
    {
        _windowViewModel.InvertSelection();
        _windowViewModel.StatusText = "已反选";
    }

    private void WindowClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _windowViewModel.ClearSelection();
        _windowViewModel.StatusText = "已取消所有勾选";
    }

    private void WindowHideSelected_Click(object sender, RoutedEventArgs e)
        => _windowViewModel.ApplySelected();

    private void WindowRestoreSelected_Click(object sender, RoutedEventArgs e)
        => _windowViewModel.RestoreSelected();

    private void WindowHideAll_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(this,
            "确定要把全部窗口隐藏到托盘吗？\n被隐藏的程序仍在后台运行，可按快捷键或托盘图标恢复。",
            "确认全部隐藏",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _windowViewModel.HideAllWindows();
        }
    }

    private void WindowRestoreAll_Click(object sender, RoutedEventArgs e)
        => _windowViewModel.RestoreAllWindows();

    // ---------------------------------------------------------------- 设置

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_trayViewModel, this)
        {
            Owner = this
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
}
