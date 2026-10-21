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

    /// <summary>
    /// 各设备最近一次成功读数。Scan() 每次都造新的 DeviceEntry，
    /// 所以跨刷新的"上一次的值"只能存在这里 —— 设备休眠时用它兜底显示。
    /// </summary>
    private readonly Dictionary<string, (BatteryInfo Info, DateTimeOffset At)> _lastGood =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读不到电量时的快速补刷定时器（用户设的间隔可能长达十几分钟）。</summary>
    private DispatcherTimer? _quickRetry;
    private int _quickRetryCount;

    /// <summary>连续补刷上限，超过就交给常规定时器，避免对着关机设备空转。</summary>
    private const int MaxQuickRetries = 6;

    /// <summary>刷新重入保护。</summary>
    private bool _refreshing;
    private bool _refreshPending;

    /// <summary>上一轮枚举到的设备条目，用于枚举抖动时兜底。</summary>
    private readonly Dictionary<string, DeviceEntry> _lastEntries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>各设备连续几轮没被枚举到。</summary>
    private readonly Dictionary<string, int> _missingRounds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 连续几轮枚举不到就认定设备已拔掉。
    /// 不用太小：接口被厂商软件短暂占用、USB 供电抖动都会让枚举临时失败，
    /// 图标突然消失比显示旧值更让人困惑。6 轮在默认间隔下约半小时。
    /// </summary>
    private const int MissingRoundsBeforeDrop = 6;

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
        // 防重入：定时器、托盘双击、IPC、保存设置都可能触发刷新。
        // 并发跑会让同一台设备的 HID 命令交错，也会让日志时间点乱掉。
        // 运行中再来请求只记一个标记，本轮结束后补跑一次。
        if (_refreshing)
        {
            _refreshPending = true;
            if (manual) _tray?.StartRefreshAnimation();
            return;
        }

        _refreshing = true;

        if (manual)
            _tray?.StartRefreshAnimation();

        try
        {
            var scanned = _registry.Scan(Config.ShowUnsupportedDevices);
            await _registry.RefreshAsync(scanned).ConfigureAwait(true);

            CarryOverStaleReadings(scanned);

            // 本轮枚举不到的设备补回来（接口被厂商软件占用、描述符偶发读不到等），
            // 否则它会从界面凭空消失 —— 这是比读到旧值更难理解的故障。
            RestoreMissingDevices(scanned);

            _entries = scanned;

            AppLog.WriteRefresh(_entries.Select(e =>
                $"{e.Key} | {e.Name} | provider={e.Provider?.Id ?? "未识别"} | " +
                $"{(e.IsOnline ? e.Percent + "%" : "--")}{(e.IsStale ? "（过期）" : "")} | " +
                $"charging={e.IsCharging} | {e.Battery.Error}"));

            _tray?.Sync(_entries);
            _mainWindow?.UpdateDevices(_entries);

            if (_entries.Any(e => e.IsStale))
                ScheduleQuickRetry();
            else
            {
                // 全部读到 = 设备已唤醒，补刷计数归零
                _quickRetryCount = 0;
                _quickRetry?.Stop();
                _quickRetry = null;
            }

            CheckLowBattery();
        }
        catch (Exception ex)
        {
            // 一次刷新失败不应该让程序崩掉，下一轮会重试
            AppLog.Write("刷新异常: " + ex.Message);
        }
        finally
        {
            _refreshing = false;

            // 必须收尾，否则托盘会一直转下去
            if (manual)
                _tray?.StopRefreshAnimation();

            if (_refreshPending)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>
    /// 把本轮没枚举到、但上一轮还在的设备补回列表，并标记为过期。
    /// 连续 MissingRoundsBeforeDrop 轮都看不到才认定真的拔掉了。
    /// </summary>
    private void RestoreMissingDevices(List<DeviceEntry> entries)
    {
        var present = new HashSet<string>(entries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _lastEntries.ToList())
        {
            if (present.Contains(pair.Key))
            {
                _missingRounds.Remove(pair.Key);
                continue;
            }

            int rounds = (_missingRounds.TryGetValue(pair.Key, out int n) ? n : 0) + 1;
            _missingRounds[pair.Key] = rounds;

            if (rounds > MissingRoundsBeforeDrop)
            {
                // 连续几轮都看不到，当它拔掉了
                _lastEntries.Remove(pair.Key);
                _missingRounds.Remove(pair.Key);
                _lastGood.Remove(pair.Key);
                continue;
            }

            pair.Value.IsStale = true;
            entries.Add(pair.Value);
        }

        foreach (var entry in entries)
            _lastEntries[entry.Key] = entry;
    }

    /// <summary>
    /// 本次没读到电量的设备，沿用上一次的有效值并标记 IsStale。
    ///
    /// 为什么这么做：鼠标静止一段时间会进深度休眠，接收器不再应答。
    /// 这时若把界面清成 "--"，托盘图标就变成灰环、主窗口变成空白 ——
    /// 用户以为程序坏了。显示"旧值 + 数据过期"信息量更大，也不会误报。
    /// </summary>
    private void CarryOverStaleReadings(List<DeviceEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Battery.HasValue)
            {
                entry.LastGoodAt = entry.Battery.Timestamp;
                entry.IsStale = false;
                _lastGood[entry.Key] = (entry.Battery, entry.Battery.Timestamp);
                continue;
            }

            // 从来没读到过 —— 保持 "--"，没有可信的旧值可用
            if (!_lastGood.TryGetValue(entry.Key, out var last)) continue;

            string? error = entry.Battery.Error;

            // IsConnected 置 true：设备本身还在，只是这次没答上来。
            // 否则界面会按「离线」处理，又退回到不显示数值。
            entry.Battery = new BatteryInfo
            {
                Percent = last.Info.Percent,
                IsCharging = last.Info.IsCharging,
                IsConnected = true,
                Millivolts = last.Info.Millivolts,
                Timestamp = last.At,
                Error = error
            };

            entry.LastGoodAt = last.At;
            entry.IsStale = true;
        }

        // 这里不清 _lastGood：本轮枚举不到的设备会被 RestoreMissingDevices 补回来，
        // 清掉缓存会让它恢复后读不到旧值。真正的清理在设备被判定拔掉时做。
    }

    /// <summary>
    /// 有设备没读到时，25 秒后补刷一次。
    /// 用户把间隔设成 15 分钟时，让一台休眠设备等 15 分钟才重试毫无意义。
    /// </summary>
    private void ScheduleQuickRetry()
    {
        if (_quickRetry != null) return;
        if (_quickRetryCount >= MaxQuickRetries) return;

        _quickRetryCount++;

        _quickRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        _quickRetry.Tick += (_, _) =>
        {
            _quickRetry?.Stop();
            _quickRetry = null;
            _ = RefreshAsync();
        };
        _quickRetry.Start();
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
        _quickRetry?.Stop();
        _tray?.Dispose();
        _ipcWait?.Unregister(null);
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
