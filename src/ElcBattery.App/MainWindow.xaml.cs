using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ElcBattery.App.Interop;
using ElcBattery.App.Models;
using ElcBattery.App.Services;
using ElcBattery.Core.Devices;

namespace ElcBattery.App;

public partial class MainWindow : Window
{
    private bool _forceClose;

    // 设置页里编辑中的值（保存时才写回 config）
    private int _refreshSeconds = 300;
    private int _threshold1 = 20;
    private int _threshold2 = 10;
    private ThemeMode _draftTheme = ThemeMode.System;

    private int _lastDeviceCount = -1;
    private bool _showingSettings;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            LoadSettingsToUi();
            RefreshCardLayer();
            FitHeight();
        };

        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static App AppHost => (App)System.Windows.Application.Current;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);

        // 让窗口背景真正透明，DWM 的磨砂才能透出来
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        RefreshCardLayer();
    }

    /// <summary>按当前主题重建窗口底色。用主题实色而不是 DWM 磨砂：
    /// 磨砂的色调由系统决定并会盖住主题色，会让数值与卡片的对比度失控。</summary>
    private void RefreshCardLayer()
    {
        bool dark = ThemeService.Resolve(AppHost.Config.Theme);
        AcrylicHelper.ApplyWindowChrome(this, dark);
        CardLayer.Background = (Brush)FindResource("WinBg");
    }

    /// <summary>主题被外部（系统深浅色变化）切换后调用：重建卡片，让数值取到新语义色。</summary>
    public void RefreshTheme()
    {
        RefreshCardLayer();
        UpdateDevices(AppHost.Entries);

        // 托盘数字的描边颜色跟着主题走，必须重画
        AppHost.SyncTrayIcons();
    }

    // ==================== 设备刷新 ====================

    public void UpdateDevices(IReadOnlyList<DeviceEntry> entries)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateDevices(entries));
            return;
        }

        var cfg = AppHost.Config;
        var vms = entries
            .Select(e => DeviceVm.From(e, cfg.LowThreshold1, cfg.LowThreshold2))
            .ToList();

        DeviceList.ItemsSource = vms;
        EmptyHint.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (vms.Count == 0)
        {
            FooterText.Text = $"未发现设备 · {DateTime.Now:HH:mm} 刷新";
        }
        else
        {
            var online = vms.Where(v => v.HasValue).ToList();
            string lowest = online.Count > 0
                ? $"最低 {(int)online.Min(v => v.PercentValue)}%"
                : "无可用读数";

            FooterText.Text = $"{lowest} · {DateTime.Now:HH:mm} 刷新 · 每 {FormatInterval(cfg.RefreshSeconds)}";
        }

        // 只在设备数量变化时重算窗口高度，避免每次自动刷新窗口跳动
        if (vms.Count != _lastDeviceCount)
        {
            _lastDeviceCount = vms.Count;
            FitHeight();
        }
    }

    /// <summary>窗口高度跟着内容走（上限 700）。设置页内容更长时会自动到上限并滚动。</summary>
    private void FitHeight(bool animate = false)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            double available = Shell.ActualWidth > 0 ? Shell.ActualWidth : Width - 32;
            if (available <= 0) return;

            Shell.Measure(new Size(available, double.PositiveInfinity));
            double desired = Shell.DesiredSize.Height;
            if (desired <= 0 || double.IsInfinity(desired)) return;

            double target = Math.Clamp(desired, MinHeight, MaxHeight > 0 ? MaxHeight : 700);
            if (Math.Abs(Height - target) <= 0.5) return;

            if (animate)
            {
                BeginAnimation(HeightProperty, new DoubleAnimation(Height, target,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                BeginAnimation(HeightProperty, null);
                Height = target;
            }
        }), DispatcherPriority.Loaded);
    }

    // ==================== 视图切换 ====================

    private void ShowView(bool settings, bool animate = true)
    {
        _showingSettings = settings;

        var inView = settings ? SettingsView : MainView;
        var outView = settings ? MainView : SettingsView;

        TitleNormal.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        TitleBack.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        RefreshBtn.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsBtn.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        MainFooter.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsFooter.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;

        outView.Visibility = Visibility.Collapsed;

        if (!animate)
        {
            inView.BeginAnimation(OpacityProperty, null);
            inView.RenderTransform = Transform.Identity;
            inView.Opacity = 1;
            inView.Visibility = Visibility.Visible;
            FitHeight(true);
            return;
        }

        // 设置页从右滑入，返回时从左侧滑入
        double from = settings ? 16 : -16;
        var tt = new TranslateTransform(from, 0);
        inView.RenderTransform = tt;
        inView.Opacity = 0;
        inView.Visibility = Visibility.Visible;

        var slide = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        tt.BeginAnimation(TranslateTransform.XProperty, slide);

        inView.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        FitHeight(true);
    }

    // ==================== 快捷键 ====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.R)
        {
            DoRefresh();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            if (!_showingSettings) OpenSettings();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_showingSettings) BackToMain();
            else WindowState = WindowState.Minimized;

            e.Handled = true;
        }
    }

    // ==================== 窗口按钮 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch
        {
            // 拖拽过程中窗口状态变化会抛异常，忽略即可
        }
    }

    private void DoRefresh()
    {
        FooterText.Text = "正在刷新…";

        // 手动刷新：图标转一圈（自动刷新不播动画，否则会变成噪音）
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        RefreshIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        RefreshIcon.RenderTransform = new RotateTransform();
        ((RotateTransform)RefreshIcon.RenderTransform)
            .BeginAnimation(RotateTransform.AngleProperty, spin);

        _ = AppHost.RefreshAsync(manual: true);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => DoRefresh();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_showingSettings) BackToMain();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        LoadSettingsToUi();
        ShowView(true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackToMain();

    private void BackToMain() => ShowView(false);

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;

        if (AppHost.Config.CloseAction == Models.CloseAction.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();

            if (!AppHost.Config.TrayHintShown)
            {
                AppHost.Config.TrayHintShown = true;
                AppHost.SaveConfig();
                AppHost.ShowTrayHint(
                    "ELC 电量监视仍在后台运行",
                    "程序已最小化到托盘，左键点击托盘上的数字图标可以重新打开。要彻底退出请右键托盘图标选择\"退出\"。");
            }

            // 藏起来之后把窗口真正销毁：WPF 窗口连同视觉树、字体缓存要占二三十 MB，
            // 而程序绝大多数时间都在后台。下次打开时重建，代价约一两百毫秒。
            AppHost.ReleaseMainWindow();
        }
        else
        {
            AppHost.ExitApplication();
        }
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    // ==================== 设置读写 ====================

    private void LoadSettingsToUi()
    {
        var c = AppHost.Config;

        _refreshSeconds = Math.Clamp(c.RefreshSeconds, 30, 3600);
        _threshold1 = Math.Clamp(c.LowThreshold1, 5, 50);
        _threshold2 = Math.Clamp(c.LowThreshold2, 1, 45);
        _draftTheme = c.Theme;

        RefreshValue.Text = FormatInterval(_refreshSeconds);
        Threshold1Value.Text = $"{_threshold1}%";
        Threshold2Value.Text = $"{_threshold2}%";

        AutoStartToggle.IsChecked = c.AutoStart;
        WakeToggle.IsChecked = c.RefreshOnWake;
        SoundToggle.IsChecked = c.AlertSoundEnabled;
        SoundPathText.Text = string.IsNullOrWhiteSpace(c.AlertSoundPath)
            ? "系统默认"
            : Path.GetFileName(c.AlertSoundPath);

        CloseMinimizeRadio.IsChecked = c.CloseAction != Models.CloseAction.ExitApp;
        CloseExitRadio.IsChecked = c.CloseAction == Models.CloseAction.ExitApp;

        ThemeLightRadio.IsChecked = c.Theme == ThemeMode.Light;
        ThemeDarkRadio.IsChecked = c.Theme == ThemeMode.Dark;
        ThemeSystemRadio.IsChecked = c.Theme == ThemeMode.System;

        ShowUnsupportedToggle.IsChecked = c.ShowUnsupportedDevices;
    }

    private static string FormatInterval(int seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";

        double minutes = seconds / 60.0;
        return minutes < 10 ? $"{minutes:0.#} 分钟" : $"{minutes:0} 分钟";
    }

    // ---------- 步进器 ----------

    private void RefreshMinus_Click(object sender, RoutedEventArgs e) => SetRefresh(_refreshSeconds - 30);
    private void RefreshPlus_Click(object sender, RoutedEventArgs e) => SetRefresh(_refreshSeconds + 30);

    private void SetRefresh(int seconds)
    {
        _refreshSeconds = Math.Clamp(seconds, 30, 3600);
        RefreshValue.Text = FormatInterval(_refreshSeconds);
    }

    private void Threshold1Minus_Click(object sender, RoutedEventArgs e) => SetThreshold1(_threshold1 - 1);
    private void Threshold1Plus_Click(object sender, RoutedEventArgs e) => SetThreshold1(_threshold1 + 1);
    private void Threshold2Minus_Click(object sender, RoutedEventArgs e) => SetThreshold2(_threshold2 - 1);
    private void Threshold2Plus_Click(object sender, RoutedEventArgs e) => SetThreshold2(_threshold2 + 1);

    private void SetThreshold1(int value)
    {
        _threshold1 = Math.Clamp(value, 5, 50);

        // 一级必须高于二级，否则两级提醒失去次序
        if (_threshold2 >= _threshold1)
            _threshold2 = Math.Max(1, _threshold1 - 5);

        Threshold1Value.Text = $"{_threshold1}%";
        Threshold2Value.Text = $"{_threshold2}%";
    }

    private void SetThreshold2(int value)
    {
        _threshold2 = Math.Clamp(value, 1, _threshold1 - 1);
        Threshold2Value.Text = $"{_threshold2}%";
    }

    // ---------- 主题 ----------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        _draftTheme = tag switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        // 立即预览；是否持久化由「保存」决定
        ThemeService.Apply(_draftTheme);
        RefreshCardLayer();
        UpdateDevices(AppHost.Entries);
        AppHost.SyncTrayIcons();
    }

    // ---------- 保存 / 取消 / 恢复 ----------

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var c = AppHost.Config;

        c.RefreshSeconds = _refreshSeconds;
        c.LowThreshold1 = _threshold1;
        c.LowThreshold2 = _threshold2;
        c.AutoStart = AutoStartToggle.IsChecked == true;
        c.RefreshOnWake = WakeToggle.IsChecked == true;
        c.CloseAction = CloseExitRadio.IsChecked == true
            ? Models.CloseAction.ExitApp
            : Models.CloseAction.MinimizeToTray;
        c.AlertSoundEnabled = SoundToggle.IsChecked == true;
        c.ShowUnsupportedDevices = ShowUnsupportedToggle.IsChecked == true;
        c.Theme = _draftTheme;

        // ApplySettings 内部会调 RefreshAsync，这里先落盘再让它跑
        AppHost.ApplySettings();

        if (sender is Button btn)
        {
            string original = btn.Content as string ?? "保存";
            btn.Content = "已保存";
            btn.IsEnabled = false;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                btn.Content = original;
                btn.IsEnabled = true;
            };
            timer.Start();
        }

        ShowView(false);
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        // 撤掉未保存的主题预览
        ThemeService.Apply(AppHost.Config.Theme);
        RefreshCardLayer();
        UpdateDevices(AppHost.Entries);
        AppHost.SyncTrayIcons();
        LoadSettingsToUi();
        ShowView(false);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var def = new AppConfig();

        _refreshSeconds = def.RefreshSeconds;
        _threshold1 = def.LowThreshold1;
        _threshold2 = def.LowThreshold2;

        RefreshValue.Text = FormatInterval(_refreshSeconds);
        Threshold1Value.Text = $"{_threshold1}%";
        Threshold2Value.Text = $"{_threshold2}%";

        AutoStartToggle.IsChecked = def.AutoStart;
        WakeToggle.IsChecked = def.RefreshOnWake;
        SoundToggle.IsChecked = def.AlertSoundEnabled;
        CloseMinimizeRadio.IsChecked = true;
        CloseExitRadio.IsChecked = false;
        ThemeSystemRadio.IsChecked = true;
        ShowUnsupportedToggle.IsChecked = def.ShowUnsupportedDevices;
    }

    // ---------- 文件选择 ----------

    private void PickSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择提示音",
            Filter = "音频文件 (*.wav)|*.wav",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AppHost.Config.AlertSoundPath = dialog.FileName;
            SoundPathText.Text = Path.GetFileName(dialog.FileName);
            AppHost.SaveConfig();
        }
    }

    private void Driver_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceVm vm &&
            !string.IsNullOrWhiteSpace(vm.DriverUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(vm.DriverUrl!) { UseShellExecute = true });
            }
            catch
            {
                System.Windows.MessageBox.Show("无法打开链接：" + vm.DriverUrl,
                    "ELC 电量监视", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Dump_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = VendorProbe.DumpAll();
            string path = Path.Combine(Path.GetTempPath(), "elc-hid-report.txt");
            File.WriteAllText(path, text);

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("生成报告失败：" + ex.Message,
                "ELC 电量监视", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
