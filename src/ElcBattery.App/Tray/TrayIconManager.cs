using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Threading;
using ElcBattery.Core.Devices;

namespace ElcBattery.App.Tray;

/// <summary>
/// 托盘图标管理：一个主图标（程序入口）+ 每个设备一个电量图标。
///
/// 交互约定：
///   左键单击设备图标  打开主窗口
///   左键双击设备图标  立即刷新（并播放托盘动画）
///   主图标            单击/双击都只打开主窗口
///
/// 动画说明：Windows 没有给 NotifyIcon 提供任何动画 API，唯一可行的做法是
/// **定时替换 Icon 属性**播放帧序列。两个硬约束：
///   1. 每帧换下来的旧 Icon 必须 Dispose，否则 GDI 句柄泄漏（托盘程序最常见的坑）
///   2. 替换不能太快 —— 低于约 60ms 一帧系统会丢弃更新，且白白占 CPU
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    /// <summary>动画收尾时额外多留的时间，避免最后一帧一闪而过。</summary>
    private const int AnimationMinTailMs = 60;

    /// <summary>
    /// 帧数与每帧间隔：10 帧 × 58ms ≈ 580ms 一圈。
    /// 原来是 12 × 75ms = 900ms，明显偏慢；再快会接近系统对托盘图标的更新节流
    ///（约 60ms），更新会被丢弃，白占 CPU。
    /// </summary>
    private const int AnimationFrames = 10;
    private const int AnimationIntervalMs = 58;

    private readonly Dictionary<string, NotifyIcon> _deviceIcons = new();

    /// <summary>每台设备当前的读数，动画期间渲染需要用到。</summary>
    private readonly Dictionary<string, (int Percent, bool Offline)> _state = new();

    private NotifyIcon? _appIcon;
    private bool _disposed;
    private long _lastDoubleClickTick;

    private readonly Action _onShowWindow;
    private readonly Action _onRefresh;
    private readonly Action _onExit;

    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _clickTimer;
    private int _animFrame;
    private bool _animating;
    private DateTime _animStartedAt;

    public TrayIconManager(Action onShowWindow, Action onRefresh, Action onExit)
    {
        _onShowWindow = onShowWindow;
        _onRefresh = onRefresh;
        _onExit = onExit;
    }

    public void EnsureAppIcon(string tooltip = "ELC 电量监视")
    {
        if (_appIcon != null) return;

        _appIcon = new NotifyIcon
        {
            Icon = IconRenderer.CreateAppIcon(),
            Text = Truncate(tooltip),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _appIcon.MouseUp += OnIconMouseUp;
        _appIcon.DoubleClick += (_, _) => _onShowWindow();
    }

    /// <summary>把最新的设备状态同步到托盘。多出来的图标会被移除。</summary>
    public void Sync(IReadOnlyList<DeviceEntry> entries)
    {
        if (_disposed) return;

        EnsureAppIcon();

        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            alive.Add(entry.Key);

            int percent = entry.IsOnline ? entry.Percent : 0;
            bool offline = !entry.IsOnline;
            _state[entry.Key] = (percent, offline);

            if (!_deviceIcons.TryGetValue(entry.Key, out var notify))
            {
                notify = new NotifyIcon
                {
                    Visible = true,
                    ContextMenuStrip = BuildMenu(entry.Key)
                };
                notify.MouseUp += OnIconMouseUp;
                notify.DoubleClick += OnIconDoubleClick;
                _deviceIcons[entry.Key] = notify;
            }

            // 动画进行中时不重画：动画的下一帧马上会覆盖，省一次 GDI 往返
            if (!_animating)
                SwapIcon(notify, IconRenderer.Create(percent, offline));

            notify.Text = Truncate(BuildTooltip(entry));
        }

        foreach (string key in _deviceIcons.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            var notify = _deviceIcons[key];
            notify.Visible = false;
            notify.Icon?.Dispose();
            notify.ContextMenuStrip?.Dispose();
            notify.Dispose();
            _deviceIcons.Remove(key);
            _state.Remove(key);
        }
    }

    /// <summary>换图标，并把旧图标释放掉。</summary>
    private static void SwapIcon(NotifyIcon notify, System.Drawing.Icon next)
    {
        var old = notify.Icon;
        notify.Icon = next;
        old?.Dispose();
    }

    // ==================== 刷新动画 ====================

    /// <summary>开始播放刷新动画。已在播放时不会重置进度（避免重复触发导致卡顿感）。</summary>
    public void StartRefreshAnimation()
    {
        if (_disposed || _deviceIcons.Count == 0) return;
        if (_animating) return;

        _animFrame = 0;
        _animating = true;
        _animStartedAt = DateTime.UtcNow;

        _animTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AnimationIntervalMs)
        };
        _animTimer.Tick -= OnAnimTick;
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
    }

    /// <summary>
    /// 结束动画。若播放时间太短（刷新太快），补足到至少一圈再停 ——
    /// 一闪而过的动画看起来更像故障而不是反馈。
    /// </summary>
    public void StopRefreshAnimation()
    {
        if (!_animating) return;

        double elapsed = (DateTime.UtcNow - _animStartedAt).TotalMilliseconds;
        double minMs = AnimationFrames * AnimationIntervalMs + AnimationMinTailMs;

        if (elapsed < minMs)
        {
            var wait = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(minMs - elapsed)
            };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                FinishAnimation();
            };
            wait.Start();
            return;
        }

        FinishAnimation();
    }

    private void FinishAnimation()
    {
        _animating = false;
        _animTimer?.Stop();
        RedrawStaticIcons();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (!_animating) return;

        _animFrame = (_animFrame + 1) % AnimationFrames;
        float phase = _animFrame / (float)AnimationFrames;

        foreach (var pair in _deviceIcons)
        {
            if (!_state.TryGetValue(pair.Key, out var st)) continue;
            SwapIcon(pair.Value, IconRenderer.CreateSpinner(st.Percent, st.Offline, phase));
        }
    }

    private void RedrawStaticIcons()
    {
        foreach (var pair in _deviceIcons)
        {
            if (!_state.TryGetValue(pair.Key, out var st)) continue;
            SwapIcon(pair.Value, IconRenderer.Create(st.Percent, st.Offline));
        }
    }

    // ==================== 交互 ====================

    /// <summary>
    /// 单击：延迟一点再开窗 —— 双击会先触发一次 MouseUp，
    /// 不延迟的话双击会既开窗又刷新。240ms 是"够快"与"能等到双击"的折中。
    /// </summary>
    private void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // NotifyIcon 双击的时序是 MouseUp(1) → DoubleClick → MouseUp(2)：
        // DoubleClick 里取消的开窗，会被紧跟着的第二下 MouseUp 重新排回去，
        // 结果就是「双击 = 刷新 + 弹窗」。刚发生过双击的短窗口内，忽略 MouseUp。
        if (Environment.TickCount64 - _lastDoubleClickTick < 600) return;

        _clickTimer?.Stop();
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer?.Stop();
            _onShowWindow();
        };
        _clickTimer.Start();
    }

    private void OnIconDoubleClick(object? sender, EventArgs e)
    {
        _lastDoubleClickTick = Environment.TickCount64;
        _clickTimer?.Stop();   // 取消这次的开窗
        _onRefresh();
    }

    /// <summary>弹一个托盘气泡（用于首次最小化提示等）。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_appIcon == null) return;

        _appIcon.BalloonTipTitle = Truncate(title);
        _appIcon.BalloonTipText = text;
        _appIcon.BalloonTipIcon = ToolTipIcon.Info;
        _appIcon.ShowBalloonTip(6000);
    }

    private static string BuildTooltip(DeviceEntry entry)
    {
        string name = entry.Name;

        if (!entry.IsOnline)
            return $"{name} — 未连接";

        // 设备休眠时显示的是上一次的有效值，必须说清楚它不是实时的
        if (entry.IsStale)
            return $"{name} — {entry.Percent}%（数据过期，{entry.Battery.Timestamp:HH:mm} 读取）";

        string charging = entry.IsCharging ? "（充电中）" : "";
        return $"{name} — {entry.Percent}%{charging} · 双击立即刷新";
    }

    private ContextMenuStrip BuildMenu(string? deviceKey = null)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("打开主界面", null, (_, _) => _onShowWindow());
        menu.Items.Add("立即刷新", null, (_, _) => _onRefresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _onExit());

        return menu;
    }

    /// <summary>Win32 限制 NotifyIcon.Text 最长 63 个字符。</summary>
    private static string Truncate(string text) =>
        text.Length <= 60 ? text : text[..57] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _animTimer?.Stop();
        _clickTimer?.Stop();

        if (_appIcon != null)
        {
            _appIcon.Visible = false;
            _appIcon.Icon?.Dispose();
            _appIcon.ContextMenuStrip?.Dispose();
            _appIcon.Dispose();
            _appIcon = null;
        }

        foreach (var notify in _deviceIcons.Values)
        {
            notify.Visible = false;
            notify.Icon?.Dispose();
            notify.ContextMenuStrip?.Dispose();
            notify.Dispose();
        }

        _deviceIcons.Clear();
        _state.Clear();
    }
}
