using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TrayHider.Services;
using TrayHider.ViewModels;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyInterop = System.Windows.Input.KeyInterop;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
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
                return "已以管理员身份运行：可以管理以管理员权限运行的程序（含它们的托盘图标恢复与窗口隐藏）。"
                     + "个别程序的右键菜单失效与权限无关——例如自带替代图标、或不响应系统托盘重建的程序，"
                     + "这类情况需要重启该程序本身才能恢复。";
            }

            return "当前为普通权限。部分以管理员权限运行的程序（例如一些安全软件、驱动服务）"
                 + "在提权后才能恢复或隐藏它们的托盘图标与窗口，需要时可用右侧按钮以管理员身份重启。"
                 + "右键菜单失效并不都由权限引起：自带替代图标或不响应托盘重建的程序，重启该程序即可恢复。";
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
        // 只如实转述视图模型的执行结果，不在这里另写"已修复/请重启资源管理器"之类的结论
        var repaired = _viewModel.RetryDegradedIcons();
        var status = _viewModel.StatusText;
        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(status) ? "修复操作已完成。" : status,
            "托盘图标管家",
            MessageBoxButton.OK,
            repaired > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ---------------------------------------------------------------- 快捷键

    /// <summary>设置界面只读配置快照：清空/重置后产品侧可能重建对象，统一从这里取最新的。</summary>
    private AppConfig Config => ((App)System.Windows.Application.Current).Config;

    private void BuildHotkeyRows()
    {
        _hotkeyRows.Clear();
        foreach (var action in HotkeyActions.All)
        {
            _hotkeyRows.Add(new HotkeyRowViewModel(action, Config.GetHotkey(action).ToString()));
        }
        HotkeyList.ItemsSource = _hotkeyRows;
    }

    /// <summary>按配置里的最新值刷新所有行（清空、重置之后调用）。</summary>
    private void RefreshHotkeyRows()
    {
        foreach (var row in _hotkeyRows)
        {
            row.UpdateText(Config.GetHotkey(row.Action).ToString());
        }
    }

    private void HotkeyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotkeyRowViewModel row }) return;

        foreach (var item in _hotkeyRows) item.IsRecording = false;
        row.IsRecording = true;
        _recordingRow = row;
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingRow == null) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        var modifiers = CurrentModifiers();

        // Delete / Backspace 只有"没有任何修饰键、也没勾附加 Win"时才当清空；
        // Ctrl+Delete、Alt+Backspace 这类是合法组合，要照常录进去。
        if (key is Key.Delete or Key.Back && modifiers == 0)
        {
            ClearHotkey(_recordingRow, showError: true);
            return;
        }

        // 只按下修饰键时继续等主键
        if (HotkeyCapture.IsModifierOnlyKey(KeyInterop.VirtualKeyFromKey(key)))
        {
            return;
        }

        TryApplyCandidate(_recordingRow, modifiers, KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>当前生效的修饰键：键盘实时状态 + 「附加 Win」显式勾选。</summary>
    private int CurrentModifiers()
    {
        var modifiers = 0;
        var live = Keyboard.Modifiers;
        if (live.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyCapture.ModCtrl;
        if (live.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyCapture.ModAlt;
        if (live.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyCapture.ModShift;
        if (live.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyCapture.ModWin;

        // WPF 常常报不到 Win：ModifierKeys 不含 Win，Keyboard.IsKeyDown(Key.LWin) 也可能不更新，
        // 因此再查一次物理键状态（GetKeyState）。
        if (HotkeyCapture.IsWinKeyDown()) modifiers |= HotkeyCapture.ModWin;

        // 显式勾选是录入 Win 组合的可靠入口（系统会吃掉 Win 组合，主键都可能收不到）
        if (WinModifierToggle?.IsChecked == true) modifiers |= HotkeyCapture.ModWin;
        return modifiers;
    }

    /// <summary>
    /// 把候选组合交给产品侧注册。键盘录入与 WM_HOTKEY 路由两条入口共用这一份逻辑，
    /// 保证"能录到"的组合与"能注册上"的组合判定完全一致。
    /// </summary>
    /// <returns>true 表示本次按键已被录入流程消费（调用方不要再执行快捷键动作）。</returns>
    private bool TryApplyCandidate(HotkeyRowViewModel row, int modifiers, int virtualKey)
    {
        var problem = HotkeyCapture.Problem(modifiers, virtualKey);
        if (problem != null)
        {
            MessageBox.Show(this, problem, "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
            StopRecording();
            return true;
        }

        var app = (App)System.Windows.Application.Current;
        if (app.TryUpdateHotkey(row.Action, modifiers, virtualKey))
        {
            row.UpdateText(Config.GetHotkey(row.Action).ToString());
            StopRecording();
            return true;
        }

        MessageBox.Show(this,
            "这个组合没能注册成功：可能已被其它软件占用，或与本软件其它动作重复。原快捷键保持不变，请换一个组合。",
            "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
        StopRecording();
        return true;
    }

    /// <summary>
    /// 供 <see cref="App"/> 的窗口过程调用：正在录入时，把这次 WM_HOTKEY 当成"用户按下的组合"，
    /// 走与键盘录入完全相同的候选逻辑，避免它去执行隐藏 / 恢复等动作。
    /// </summary>
    /// <returns>false 表示当前没在录入（或窗口不在前台），调用方按原有逻辑处理该快捷键。</returns>
    internal bool TryCaptureRegisteredHotkey(int modifiers, int virtualKey)
    {
        var row = _recordingRow;
        if (row == null || !IsActive) return false;

        // 走 WM_HOTKEY 时 Win 修饰位是可靠的；顺手把显式勾选也算上，避免两条入口行为不一致
        if (WinModifierToggle?.IsChecked == true) modifiers |= HotkeyCapture.ModWin;
        return TryApplyCandidate(row, modifiers, virtualKey);
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotkeyRowViewModel row }) return;
        ClearHotkey(row, showError: true);
    }

    /// <summary>清空某一行的快捷键（0/0 哨兵由产品侧落盘，重启不会被默认值补回）。</summary>
    private void ClearHotkey(HotkeyRowViewModel row, bool showError)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.TryClearHotkey(row.Action))
        {
            row.UpdateText(Config.GetHotkey(row.Action).ToString());
            StopRecording();
            return;
        }

        if (showError)
        {
            MessageBox.Show(this,
                "没有清空成功，请稍后重试。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StopRecording();
    }

    /// <summary>仅重置快捷键：隐藏记录、开关等其它设置保持不变；失败时产品侧会说明哪些动作冲突。</summary>
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        StopRecording();   // 成功或失败都不要留一行卡在"请按下新快捷键…"状态

        if (app.TryResetHotkeys(out var message))
        {
            RefreshHotkeyRows();

            // 5 组默认值都已保存；若其中某些组合注册不上（被系统保留或被别的程序占用），
            // 产品侧会用 message 说明是哪几项，这里用警告图标如实转述。
            if (!string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show(this,
                    "已把 5 个全局快捷键恢复为默认值，其它设置保持不变。\n\n" + message,
                    "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                "已把 5 个全局快捷键恢复为默认值，其它设置保持不变。",
                "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(message) ? "重置失败，请稍后重试。" : message,
            "托盘图标管家", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        // 同上：用视图模型的真实结果，避免界面写出与实际不符的"已清空所有"
        _viewModel.RestoreAll();
        var status = _viewModel.StatusText;
        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(status) ? "恢复操作已完成。" : status,
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