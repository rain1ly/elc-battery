using System;
using System.Windows;
using System.Windows.Media;
using ElcBattery.Core.Devices;

namespace ElcBattery.App.Models;

/// <summary>电量语义档位。视觉编码见 design/DESIGN.md。</summary>
public enum BatteryLevel
{
    /// <summary>读不到 / 设备离线。</summary>
    Unknown,
    /// <summary>充足（&gt; 50%），刻意不显示任何状态标记。</summary>
    Good,
    /// <summary>偏低。</summary>
    Caution,
    /// <summary>危险（低于预警线）。</summary>
    Danger,
    /// <summary>充电中（覆盖档位色）。</summary>
    Charging,
    /// <summary>已充满。</summary>
    Full
}

/// <summary>给界面绑定用的设备视图模型（把 Brush / Geometry 之类的表现层东西隔离在这里）。</summary>
public sealed class DeviceVm
{
    // ---- 图标：16×16 viewBox 内定义，Path 通过 Stretch 缩放 ----
    private static readonly Geometry WarnIcon =
        Freeze(Geometry.Parse("M8,1.6 L14.4,13 L1.6,13 Z"));

    private static readonly Geometry BoltIcon =
        Freeze(Geometry.Parse("M7.4,0.5 L1.9,8 L5.3,8 L4.4,13.5 L10.3,5.7 L6.9,5.7 Z"));

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }

    public required string Name { get; init; }
    public string DeviceKey { get; init; } = "";

    /// <summary>数值部分，不含百分号。读不到时为 "--"。</summary>
    public required string PercentText { get; init; }

    /// <summary>单位，读不到时为空串（"--" 后面不该跟 %）。</summary>
    public required string UnitText { get; init; }

    public required double PercentValue { get; init; }
    public required string MetaText { get; init; }

    public required BatteryLevel Level { get; init; }
    public required Brush PercentBrush { get; init; }
    public required Brush BarBrush { get; init; }

    /// <summary>状态标记文案，Good 时为空。</summary>
    public string StateText { get; init; } = "";

    public Brush? StateBrush { get; init; }

    /// <summary>状态图标；无标记时为 null。</summary>
    public Geometry? StateIcon { get; init; }

    public bool HasState => StateText.Length > 0;
    public bool HasValue => Level != BatteryLevel.Unknown;

    // ---- 进度条：用 GridLength(Star) 让填充与预警刻度都按比例自适应，无需测量像素 ----
    public GridLength PercentStar => new(Math.Clamp(PercentValue, 0, 100), GridUnitType.Star);
    public GridLength RestStar => new(Math.Max(100 - Math.Clamp(PercentValue, 0, 100), 0.001), GridUnitType.Star);
    public GridLength TickStar => new(Math.Clamp(TickPercent, 0, 100), GridUnitType.Star);
    public GridLength TickRestStar => new(Math.Max(100 - Math.Clamp(TickPercent, 0, 100), 0.001), GridUnitType.Star);

    /// <summary>预警刻度位置（= 一级阈值）。</summary>
    public double TickPercent { get; init; } = 20;
    public bool ShowTick { get; init; }

    public string ErrorText { get; init; } = "";
    public bool HasError { get; init; }

    public bool HasDriverLink { get; init; }
    public string? DriverUrl { get; init; }

    /// <summary>读不到电量的卡片用虚线弱化处理。</summary>
    public bool IsDim => Level == BatteryLevel.Unknown;

    internal static DeviceVm From(DeviceEntry entry, int threshold1, int threshold2)
    {
        bool online = entry.IsOnline && entry.Battery.HasValue;
        int percent = online ? Math.Clamp(entry.Percent, 0, 100) : 0;

        // ---------------- 语义档位 ----------------
        BatteryLevel level;
        if (!online)
            level = BatteryLevel.Unknown;
        else if (entry.IsCharging)
            level = percent >= 100 ? BatteryLevel.Full : BatteryLevel.Charging;
        else if (percent <= threshold1)
            level = BatteryLevel.Danger;
        else if (percent <= 50)
            level = BatteryLevel.Caution;
        else
            level = BatteryLevel.Good;

        // ---------------- 状态标记文案 ----------------
        string stateText = "";
        Geometry? icon = null;
        bool useWarnIcon = false, useBoltIcon = false;

        if (online)
        {
            switch (level)
            {
                case BatteryLevel.Charging:
                    stateText = "充电中";
                    useBoltIcon = true;
                    break;
                case BatteryLevel.Full:
                    stateText = "已充满";
                    break;
                case BatteryLevel.Caution:
                    stateText = "偏低";
                    break;
                case BatteryLevel.Danger:
                    // 文案跟着用户设的阈值走，阈值改了文字才说得对
                    stateText = percent <= threshold2
                        ? "电量紧张"
                        : $"低于预警线 {threshold1}%";
                    useWarnIcon = true;
                    break;
                default:
                    stateText = "";   // 充足时刻意留白，比写「正常」更安静
                    break;
            }
        }

        if (useWarnIcon) icon = WarnIcon;
        else if (useBoltIcon) icon = BoltIcon;

        // ---------------- 元信息：协议 · 新鲜度 · 电压 ----------------
        string provider = entry.Provider?.DisplayName ?? "未知协议";
        string fresh = DescribeAge(entry.Battery.Timestamp);

        string meta = online
            ? $"{provider} · {fresh}"
            : "未读取到电量";

        if (online && entry.Battery.Millivolts > 0)
            meta += $" · {entry.Battery.Millivolts / 1000.0:0.00} V";

        string error = entry.Battery.Error ?? "";
        bool hasError = !online && error.Length > 0;

        return new DeviceVm
        {
            Name = entry.Name,
            DeviceKey = entry.Key,
            PercentText = online ? percent.ToString() : "--",
            UnitText = online ? "%" : "",
            PercentValue = online ? percent : 0,
            MetaText = meta,
            Level = level,
            PercentBrush = LevelBrush(level),
            BarBrush = LevelBrush(level),
            StateText = stateText,
            StateBrush = stateText.Length > 0 ? LevelBrush(level) : null,
            StateIcon = icon,
            TickPercent = Math.Clamp(threshold1, 1, 99),
            ShowTick = online,
            ErrorText = error,
            HasError = hasError,
            HasDriverLink = !string.IsNullOrWhiteSpace(entry.DriverUrl),
            DriverUrl = entry.DriverUrl
        };
    }

    /// <summary>档位 → 画笔。取全局资源，因此换主题后再次刷新即可跟着变。</summary>
    private static Brush LevelBrush(BatteryLevel level)
    {
        string key = level switch
        {
            BatteryLevel.Good => "Success",
            BatteryLevel.Caution => "Caution",
            BatteryLevel.Danger => "Danger",
            BatteryLevel.Charging => "Accent",
            BatteryLevel.Full => "Success",
            _ => "Muted"
        };

        if (Application.Current?.TryFindResource(key) is Brush b)
            return b;

        return Brushes.Gray;
    }

    private static string DescribeAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.Now - timestamp;

        if (age.TotalSeconds < 5) return "刚刚更新";
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds} 秒前更新";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前更新";

        return timestamp.ToString("HH:mm") + " 更新";
    }
}
