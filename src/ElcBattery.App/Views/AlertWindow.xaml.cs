using System;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ElcBattery.App.Views;

/// <summary>右下角滑入的低电量提醒卡片，8 秒后自动淡出。</summary>
public partial class AlertWindow : Window
{
    private DispatcherTimer? _autoCloseTimer;
    private bool _closing;

    public AlertWindow()
    {
        InitializeComponent();
    }

    /// <summary>UI 线程上弹一个提醒。</summary>
    public static void ShowAlert(string deviceName, int percent, int level, bool sound, string? soundPath)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        app.Dispatcher.Invoke(() =>
        {
            var window = new AlertWindow();
            window.Setup(deviceName, percent, level);
            window.Show();

            if (sound) PlayAlertSound(soundPath);
        });
    }

    private void Setup(string deviceName, int percent, int level)
    {
        PercentText.Text = $"{percent}%";

        // 用语义色，不用连续色阶：和主界面卡片的红/琥珀保持一致
        string key = level >= 2 ? "Danger" : "Caution";
        PercentBadge.Background = TryFindResource(key) as Brush ?? Brushes.IndianRed;

        TitleText.Text = level >= 2 ? "电量严重不足" : "电量偏低";
        DeviceText.Text = deviceName;
        HintText.Text = level >= 2
            ? "请立即充电，避免设备突然断连"
            : "剩余电量已低于设定阈值，建议尽快充电";

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 18);

        Opacity = 0;

        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, _) => CloseSmooth();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoCloseTimer.Tick += (_, _) => CloseSmooth();
        _autoCloseTimer.Start();
    }

    private void CloseSmooth()
    {
        if (_closing) return;
        _closing = true;

        _autoCloseTimer?.Stop();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseSmooth();

    private static void PlayAlertSound(string? soundPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(soundPath) && File.Exists(soundPath))
            {
                var player = new SoundPlayer(soundPath);
                player.Play(); // 异步，不阻塞 UI
            }
            else
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch
        {
            // 自定义音频损坏时静默
        }
    }
}
