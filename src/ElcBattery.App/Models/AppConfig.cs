using System;
using System.Collections.Generic;

namespace ElcBattery.App.Models;

public enum CloseAction
{
    /// <summary>关闭窗口时最小化到托盘（默认）。</summary>
    MinimizeToTray = 0,
    /// <summary>关闭窗口即退出程序。</summary>
    ExitApp = 1
}

/// <summary>界面主题。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统深浅色。</summary>
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>用户配置。持久化为 %APPDATA%\ELC\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>刷新间隔（秒），默认 300 秒。</summary>
    public int RefreshSeconds { get; set; } = 300;

    /// <summary>开机自启。</summary>
    public bool AutoStart { get; set; } = true;

    public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;

    /// <summary>一级低电量阈值（%）。</summary>
    public int LowThreshold1 { get; set; } = 20;

    /// <summary>二级低电量阈值（%）。</summary>
    public int LowThreshold2 { get; set; } = 10;

    public bool AlertSoundEnabled { get; set; } = true;

    /// <summary>自定义提示音 wav 路径，空则用系统提示音。</summary>
    public string? AlertSoundPath { get; set; }

    /// <summary>跟随系统深浅色；false 时固定浅色。</summary>
    public bool FollowSystemTheme { get; set; } = true;

    /// <summary>主题：跟随系统 / 固定浅色 / 固定深色。优先于 FollowSystemTheme。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;


    /// <summary>唤醒后立即补一次读取。</summary>
    public bool RefreshOnWake { get; set; } = true;

    /// <summary>是否在列表里显示不支持电量读取的设备（调试用）。</summary>
    public bool ShowUnsupportedDevices { get; set; }

    /// <summary>"已最小化到托盘"的提示只弹一次。</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>每个设备的自定义显示名 / 驱动地址覆盖。</summary>
    public List<DeviceOverride> DeviceOverrides { get; set; } = new();

    public AppConfig Clone() => new()
    {
        RefreshSeconds = RefreshSeconds,
        AutoStart = AutoStart,
        CloseAction = CloseAction,
        LowThreshold1 = LowThreshold1,
        LowThreshold2 = LowThreshold2,
        AlertSoundEnabled = AlertSoundEnabled,
        AlertSoundPath = AlertSoundPath,
        FollowSystemTheme = FollowSystemTheme,
        Theme = Theme,
        RefreshOnWake = RefreshOnWake,
        ShowUnsupportedDevices = ShowUnsupportedDevices,
        TrayHintShown = TrayHintShown,
        DeviceOverrides = new List<DeviceOverride>(DeviceOverrides)
    };
}

public sealed class DeviceOverride
{
    /// <summary>"0B05:1AD0" 形式。</summary>
    public string DeviceKey { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? DriverUrl { get; set; }
}
