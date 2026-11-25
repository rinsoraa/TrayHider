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

public partial class SettingsWindow : Window, System.ComponentModel.INotifyPropertyChanged
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

        // 语言必须先按配置生效，快捷键行的标题/说明才不会是"构造那一刻"的旧语言；
        // 订阅放在建行之前，保证之后每次切换都能整体重建。
        Loc.ApplyLanguage(Config.Language);
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;

        BuildHotkeyRows();
        RebuildLanguageOptions();

        Loaded += (_, _) =>
        {
            WindowChrome.Apply(this);

            // 窗口隐藏相关开关由 WindowListViewModel 持有，这里给该卡片单独换数据源
            WindowOptionsCard.DataContext = app.WindowViewModel;
        };
    }

    public string ConfigPathText => AppConfig.ConfigPath;

    /// <summary>页脚版本号：取程序集版本，避免每次发版漏改文案。</summary>
    public string FooterText
    {
        get
        {
            var version = typeof(SettingsWindow).Assembly.GetName().Version;
            var text = version == null ? "v?" : $"v{version.Major}.{version.Minor}.{version.Build}";
            return (Loc.CurrentLanguage == Loc.Chinese ? "托盘图标管家 " : "TrayHider ") + text;
        }
    }

    /// <summary>当前权限说明（设置页"权限与运行方式"里显示）。</summary>
    public string ElevationHint
    {
        get
        {
            var app = (App)System.Windows.Application.Current;
            if (app.IsElevated)
            {
                return Loc.T("Settings.Permission.ElevatedHint");
            }

            return Loc.T("Settings.Permission.NormalHint");
        }
    }

    private void RelaunchElevated_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.IsElevated)
        {
            MessageBox.Show(this, Loc.T("Settings.Permission.AlreadyElevated"), AppTitleText(),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            Loc.T("Settings.Permission.RelaunchConfirm"),
            Loc.T("Settings.Permission.RelaunchTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        Close();
        if (!app.RelaunchAsAdministrator())
        {
            MessageBox.Show(this, Loc.T("Settings.Permission.RelaunchFailed"), AppTitleText(),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RepairDegraded_Click(object sender, RoutedEventArgs e)
    {
        // 只如实转述视图模型的执行结果，不在这里另写"已修复/请重启资源管理器"之类的结论
        var repaired = _viewModel.RetryDegradedIcons();
        var status = _viewModel.StatusText;
        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(status) ? Loc.T("Settings.Repair.ResultFallback") : status,
            AppTitleText(),
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
            MessageBox.Show(this, problem, AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            Loc.T("Hotkey.Register.Failed"),
            AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                Loc.T("Hotkey.Clear.Failed"),
                AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    Loc.T("Hotkey.Reset.Note", message),
                    AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                Loc.T("Hotkey.Reset.Success"),
                AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(message) ? Loc.T("Hotkey.Reset.Failed") : message,
            AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            string.IsNullOrWhiteSpace(status) ? Loc.T("Settings.Restore.ResultFallback") : status,
            AppTitleText(),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- 界面语言 / 运行方式

    /// <summary>语言下拉项：<c>Value</c> 写进配置，<c>Label</c> 随当前语言刷新。</summary>
    public sealed record LanguageOption(string Value, string Label)
    {
        // 下拉项在某些模板路径下直接取 ToString()，这里保证显示的是本地化标签而不是 record 语法
        public override string ToString() => Label;
    }

    private List<LanguageOption> _languageOptions = new();
    private bool _rebuildingLanguage;

    /// <summary>下拉数据源。切换语言后 <c>Label</c> 需要重新求值，所以整表重建。</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions
    {
        get => _languageOptions;
        private set
        {
            _languageOptions = new List<LanguageOption>(value);
            OnPropertyChanged(nameof(LanguageOptions));
        }
    }

    /// <summary>当前选中的语言项（对外只读语义；写入等价于切换语言）。</summary>
    public LanguageOption? SelectedLanguage
    {
        get => _languageOptions.FirstOrDefault(o => string.Equals(o.Value, Config.Language, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null) SelectedLanguageValue = value.Value;
        }
    }

    /// <summary>
    /// ComboBox 的二路绑定目标（SelectedValue）。<see cref="LanguageOption"/> 是 record，
    /// 它的 <c>Value</c> 是 init-only 只读属性，WPF 二路写不进去（会报"属性只读"且选择不落盘），
    /// 所以这里用字符串属性承接选中值，写入时统一走一次"生效 + 保存 + 重建选项"。
    /// </summary>
    public string SelectedLanguageValue
    {
        get => Config.Language;
        set
        {
            if (_rebuildingLanguage) return;

            var pref = string.IsNullOrWhiteSpace(value) ? Loc.Auto : value.Trim().ToLowerInvariant();
            if (string.Equals(Config.Language, pref, StringComparison.OrdinalIgnoreCase)) return;

            ApplyLanguagePreference(pref);
            RebuildLanguageOptions();
            OnPropertyChanged(nameof(SelectedLanguage));
        }
    }

    /// <summary>轮询子选项的可见性：只有启用不常驻模式后才需要选择。</summary>
    public System.Windows.Visibility PollingRowVisibility
        => Config.RunOnceAndExit ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>周期轮询开关：开启则每 10 分钟补应用一次，关闭则等统计图标全部隐藏完就静默退出。</summary>
    public bool RunOncePolling
    {
        get => Config.RunOncePolling;
        set
        {
            if (Config.RunOncePolling == value) return;

            Config.RunOncePolling = value;
            Config.Save();
            var message = RunOnceService.SyncSchedule(Config);
            OnPropertyChanged();
            MessageBox.Show(this, message, AppTitleText(), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>「隐藏图标后退出（不常驻）」开关：交给 RunOnceService 落盘/建计划任务，并把结果提示给用户。</summary>
    public bool RunOnceEnabled
    {
        get => Config.RunOnceAndExit;
        set
        {
            if (Config.RunOnceAndExit == value) return;

            // 该功能只在"开机自启"下才有意义：未开启时先征得同意，再一起打开。
            if (value && !_viewModel.RunAtStartup)
            {
                var answer = MessageBox.Show(this, Loc.T("App.RunOnce.NeedAutostart"), AppTitleText(),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
            OnPropertyChanged(nameof(PollingRowVisibility));
                    return;
                }

                _viewModel.RunAtStartup = true;
            }

            var message = string.Empty;
            try
            {
                message = RunOnceService.SetEnabled(Config, value);
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(PollingRowVisibility));

            try
            {
                MessageBox.Show(this, message, AppTitleText(), MessageBoxButton.OK,
                    value ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[设置] 显示运行方式提示失败: " + ex.Message);
            }
        }
    }

    /// <summary>窗口标题文案：app 区没提供时退回通用应用名，避免界面出现裸键名。</summary>
    private static string AppTitleText()
    {
        var title = Loc.T("App.Title");
        return string.Equals(title, "App.Title", StringComparison.Ordinal) ? Loc.T("Common.AppName") : title;
    }

    /// <summary>切换语言：立即刷新界面 + 保存到配置（失败不影响使用）。</summary>
    private void ApplyLanguagePreference(string pref)
    {
        try
        {
            Loc.ApplyLanguage(pref);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[设置] 切换语言失败: " + ex.Message);
        }

        try
        {
            Config.Language = pref;
            Config.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[设置] 保存语言偏好失败: " + ex.Message);
        }
    }

    /// <summary>按当前语言重建三个选项的文字，并保持选中项不变。</summary>
    private void RebuildLanguageOptions()
    {
        _rebuildingLanguage = true;
        try
        {
            var selected = SelectedLanguageValue;
            LanguageOptions = new List<LanguageOption>
            {
                new(Loc.Auto, Loc.T("Settings.Language.Auto")),
                new(Loc.Chinese, Loc.T("Settings.Language.Chinese")),
                new(Loc.English, Loc.T("Settings.Language.English"))
            };

            // 选中项变了（或首次为空）时通知一次，保证 ComboBox 显示正确
            OnPropertyChanged(nameof(SelectedLanguageValue));
            OnPropertyChanged(nameof(SelectedLanguage));
            System.Diagnostics.Debug.WriteLine($"[设置] 语言选项已重建，当前 {selected}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[设置] 重建语言选项失败: " + ex.Message);
        }
        finally
        {
            _rebuildingLanguage = false;
        }
    }

    /// <summary>
    /// 语言切换：下拉项文字、快捷键行的标题/说明/按钮文字、以及窗口自带的权限说明都要重算
    /// （它们不是逐条绑定到 Loc 的，必须在这里主动刷新，否则会留着旧语言的文字）。
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildLanguageOptions();
        BuildHotkeyRows();
        OnPropertyChanged(nameof(ElevationHint));
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
            MessageBox.Show(this, Loc.T("Settings.ConfigPath.OpenFailed", ex.Message), AppTitleText(),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            Loc.T("Settings.Exit.ConfirmText"),
            Loc.T("Settings.Exit.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Close();
            _owner.RequestShutdown();
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}