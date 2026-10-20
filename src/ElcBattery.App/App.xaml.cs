using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ElcBattery.App.Models;
using ElcBattery.App.Services;
using ElcBattery.App.Tray;
using ElcBattery.App.Views;
using ElcBattery.Core.Devices;

namespace ElcBattery.App;

public partial class App
{
    private Mutex? _singleInstanceMutex;

    /// <summary>第二个实例用它通知已运行的实例"把窗口显示出来"。</summary>
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _ipcWait;
    private volatile bool _exiting;

    private readonly ConfigService _configService = new();
    private readonly DeviceRegistry _registry = new();

    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _timer;

    private List<DeviceEntry> _entries = new();

    /// <summary>已经弹过预警的 key：{设备Key}:{级别}，充电或回升后清除。</summary>
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    private bool _silentStart;

    public AppConfig Config => _configService.Current;

    public IReadOnlyList<DeviceEntry> Entries => _entries;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _silentStart = e.Args.Any(a =>
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-s", StringComparison.OrdinalIgnoreCase));

        // 单实例：第二个实例不再静默消失，而是叫醒已有实例的窗口。
        // 否则用户双击 exe 会觉得"程序打不开"（其实它一直在托盘里）。
        _singleInstanceMutex = new Mutex(true, @"Global\ELCBattery_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Global\ELCBattery_ShowWindow", out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch
            {
                // 叫不醒就算了，别让第二个实例报错
            }

            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, @"Global\ELCBattery_ShowWindow");

        StartShowWindowListener();

        _configService.Load();

        // 主题必须在任何窗口创建之前应用，否则首帧会用默认浅色闪一下
        ThemeService.Apply(Config.Theme);
        ThemeService.WatchSystem(_ =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Config.Theme != ThemeMode.System) return;
                ThemeService.Apply(ThemeMode.System);
                _mainWindow?.RefreshTheme();
            }));
        });

        // 让配置里的自启开关与实际注册表状态一致（用户可能手动改过）
        Config.AutoStart = AutoStartService.IsEnabled();

        _tray = new TrayIconManager(
            onShowWindow: ShowMainWindow,
            onRefresh: () => _ = RefreshAsync(manual: true),
            onExit: ExitApplication);

        _tray.EnsureAppIcon();

        if (!_silentStart)
            ShowMainWindow();

        RestartTimer();
        _ = RefreshAsync();
    }

    private void RestartTimer()
    {
        _timer?.Stop();

        int seconds = Math.Clamp(Config.RefreshSeconds, 30, 86400);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>
    /// 重新枚举设备并刷新电量。这是所有 UI 状态的唯一入口。
    /// manual=true 表示用户主动触发（托盘双击、托盘菜单、主窗口按钮）—— 这时播放托盘动画；
    /// 后台定时刷新不播动画，否则每 5 分钟闪一次纯属噪音。
    /// </summary>
    public async Task RefreshAsync(bool manual = false)
    {
        if (manual)
            _tray?.StartRefreshAnimation();

        try
        {
            var scanned = _registry.Scan(Config.ShowUnsupportedDevices);
            await _registry.RefreshAsync(scanned).ConfigureAwait(true);

            _entries = scanned;

            AppLog.WriteRefresh(_entries.Select(e =>
                $"{e.Key} | {e.Name} | provider={e.Provider?.Id ?? "未识别"} | " +
                $"{(e.IsOnline ? e.Percent + "%" : "--")} | charging={e.IsCharging} | {e.Battery.Error}"));

            _tray?.Sync(_entries);
            _mainWindow?.UpdateDevices(_entries);

            CheckLowBattery();
        }
        catch (Exception ex)
        {
            // 一次刷新失败不应该让程序崩掉，下一轮会重试
            AppLog.Write("刷新异常: " + ex.Message);
        }
        finally
        {
            // 必须收尾，否则托盘会一直转下去
            if (manual)
                _tray?.StopRefreshAnimation();
        }
    }

    private void CheckLowBattery()
    {
        int t1 = Config.LowThreshold1;
        int t2 = Config.LowThreshold2;

        foreach (var entry in _entries)
        {
            if (!entry.IsOnline) continue;

            // 充电中不打扰；电量回升后清掉标记，下次低电量还能再提醒
            if (entry.IsCharging)
            {
                _alerted.Remove($"{entry.Key}:1");
                _alerted.Remove($"{entry.Key}:2");
                continue;
            }

            int level = 0;
            if (entry.Percent <= t2) level = 2;
            else if (entry.Percent <= t1) level = 1;

            if (level == 0)
            {
                _alerted.Remove($"{entry.Key}:1");
                _alerted.Remove($"{entry.Key}:2");
                continue;
            }

            // 已经在更高优先级提醒过就不再打扰
            if (_alerted.Contains($"{entry.Key}:{level}")) continue;
            if (level == 1 && _alerted.Contains($"{entry.Key}:2")) continue;

            _alerted.Add($"{entry.Key}:{level}");

            AlertWindow.ShowAlert(
                entry.Name,
                entry.Percent,
                level,
                Config.AlertSoundEnabled,
                Config.AlertSoundPath);
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += OnMainWindowClosed;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
        _mainWindow.UpdateDevices(_entries);
    }

    /// <summary>
    /// 等"显示窗口"信号（第二个实例发来的）。
    ///
    /// 用线程池的等待句柄而不是自己开一条常驻线程：省掉一个线程（1MB 栈 + 内核对象），
    /// 而且信号不来时它不占任何资源。
    /// </summary>
    private void StartShowWindowListener()
    {
        if (_showWindowEvent == null) return;

        _ipcWait = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, timedOut) =>
            {
                if (_exiting || timedOut) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_exiting) return;
                    ShowMainWindow();
                    _ = RefreshAsync();
                }));
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void ShowTrayHint(string title, string text) => _tray?.ShowBalloon(title, text);

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _mainWindow = null;
    }

    /// <summary>
    /// 窗口藏进托盘后真正销毁它，释放视觉树与相关缓存。
    /// 托盘程序的窗口可能一整天都用不上一次，没必要让它常驻内存。
    /// 下次 ShowMainWindow 会重建。
    /// </summary>
    public void ReleaseMainWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.ForceClose();
        _mainWindow = null;
    }

    /// <summary>设置保存后调用：重排定时器、同步自启、立即刷一次。</summary>
    public void ApplySettings()
    {
        _configService.Save();
        AutoStartService.SetEnabled(Config.AutoStart);
        RestartTimer();
        _ = RefreshAsync();
    }

    public void SaveConfig() => _configService.Save();

    /// <summary>只重画托盘图标，不重新扫描设备（切主题时用，避免每次都全量枚举 HID）。</summary>
    public void SyncTrayIcons() => _tray?.Sync(_entries);

    public void ExitApplication()
    {
        _timer?.Stop();
        _tray?.Dispose();
        _mainWindow?.ForceClose();

        AutoStartService.SetEnabled(Config.AutoStart);
        _configService.Save();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        _ipcWait?.Unregister(null);
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
