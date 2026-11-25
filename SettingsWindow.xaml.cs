using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TrayHider.Services;
using TrayHider.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace TrayHider;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _owner;
    private readonly AppConfig _config;
    private readonly ObservableCollection<HotkeyRowViewModel> _hotkeyRows = new();
    private HotkeyRowViewModel? _recordingRow;

    public SettingsWindow(MainViewModel viewModel, MainWindow owner)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _owner = owner;

        var app = (App)System.Windows.Application.Current;
        _config = app.Config;

        DataContext = viewModel;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;

        BuildHotkeyRows();

        Loaded += (_, _) =>
        {
            WindowChrome.Apply(this);

            // 窗口隐藏相关开关由 WindowListViewModel 持有，这里给该卡片单独换数据源
            WindowOptionsCard.DataContext = app.WindowViewModel;
        };
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    /// <summary>当前权限说明（设置页"权限与运行方式"里显示）。</summary>
    public string ElevationHint
    {
        get
        {
            var app = (App)System.Windows.Application.Current;
            if (app.IsElevated)
            {
                return "已以管理员身份运行：可以管理以管理员权限运行的程序（含它们的托盘图标恢复与窗口隐藏）。";
            }

            return "当前为普通权限。以管理员权限运行的程序（例如部分安全软件、驱动服务）"
                 + "其托盘图标被隐藏后可能无法恢复正常点击响应，也无法隐藏它的窗口；"
                 + "需要时请用右侧按钮以管理员身份重启。";
        }
    }

    private void RelaunchElevated_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.IsElevated)
        {
            MessageBox.Show(this, "当前已经是管理员权限，无需重复启动。", "托盘图标管家",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            "将以管理员身份重新启动托盘图标管家。\n\n" +
            "当前已隐藏的托盘图标会保持隐藏（配置不变），被隐藏的窗口会在退出时恢复，重启后可用设置里的『恢复所有被隐藏的图标』再次接管。\n\n继续吗？",
            "以管理员身份重启",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        Close();
        if (!app.RelaunchAsAdministrator())
        {
            MessageBox.Show(this, "提权被取消或失败，程序保持普通权限运行。", "托盘图标管家",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RepairDegraded_Click(object sender, RoutedEventArgs e)
    {
        var repaired = _viewModel.RetryDegradedIcons();
        MessageBox.Show(this,
            repaired > 0
                ? $"已修复 {repaired} 个图标的右键菜单。"
                : "没有需要修复的图标。\n\n如果某个图标仍然左右键无反应，说明该程序不响应系统的托盘重建，" +
                  "此时重启该程序（或重启资源管理器）即可恢复。",
            "托盘图标管家",
            MessageBoxButton.OK,
            repaired > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ---------------------------------------------------------------- 快捷键

    private void BuildHotkeyRows()
    {
        _hotkeyRows.Clear();
        foreach (var action in HotkeyActions.All)
        {
            _hotkeyRows.Add(new HotkeyRowViewModel(action, _config.GetHotkey(action).ToString()));
        }
        HotkeyList.ItemsSource = _hotkeyRows;
    }

    private void HotkeyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotkeyRowViewModel row }) return;

        foreach (var item in _hotkeyRows) item.IsRecording = false;
        row.IsRecording = true;
        _recordingRow = row;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingRow == null) return;
        e.Handled = true;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Escape)
        {
            StopRecording();
            return;
        }

        // 单纯按下修饰键时等待用户继续按主键
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
            MessageBox.Show(this,
                "快捷键至少需要一个修饰键，例如 Ctrl、Alt、Shift 或 Win。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return;
        }

        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        var action = _recordingRow.Action;
        var app = (App)System.Windows.Application.Current;

        if (app.TryUpdateHotkey(action, modifiers, virtualKey))
        {
            _recordingRow.UpdateText(_config.GetHotkey(action).ToString());
            _viewModel.NotifyHotkeyChanged();
            app.WindowViewModel?.NotifyHotkeyChanged();
        }
        else
        {
            MessageBox.Show(this,
                "快捷键注册失败，可能已被其他软件（或本软件的其他动作）占用，请换一个组合。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StopRecording();
    }

    private void StopRecording()
    {
        if (_recordingRow != null)
        {
            _recordingRow.IsRecording = false;
            _recordingRow = null;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
            "已清空所有隐藏记录。\n\n图标将回到托盘；若个别程序没有响应托盘重建，重启该程序即可让右键菜单恢复正常。",
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
            "确定要退出托盘图标管家吗？\n\n退出后，已被隐藏的图标将在对应程序重新注册后回到托盘，被隐藏的窗口会被恢复，" +
            "本软件也不再提供隐藏守护。",
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
