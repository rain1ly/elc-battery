using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>一个被识别（或未被识别）的设备，以及它最近一次的电量。</summary>
public sealed class DeviceEntry
{
    public required DeviceGroup Group { get; init; }
    public IBatteryProvider? Provider { get; set; }
    public BatteryInfo Battery { get; set; } = new();

    /// <summary>上一次成功读数的时间。失败时用它说明「数据是多久以前的」。</summary>
    public DateTimeOffset? LastGoodAt { get; set; }

    /// <summary>
    /// 本次刷新没读到，当前显示的是上一次的值。
    /// 保留旧值而不是显示 "--"：设备休眠时托盘变空白，用户会以为程序坏了。
    /// </summary>
    public bool IsStale { get; set; }

    public bool Supported => Provider != null;
    public string Key => Group.DeviceKey;
    public string Name => Group.DisplayName;
    public string? DriverUrl => Provider?.DriverUrl;

    public int Percent => Battery.Percent;
    public bool IsCharging => Battery.IsCharging;
    public bool IsOnline => Battery.IsConnected && Battery.HasValue;
}

/// <summary>设备识别与批量刷新。所有 HID 读取在这里串行执行 —— 同一个接收器的命令队列不能并发。</summary>
public sealed class DeviceRegistry
{
    private readonly List<IBatteryProvider> _providers;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeviceRegistry(IEnumerable<IBatteryProvider>? extra = null)
    {
        _providers = new List<IBatteryProvider>
        {
            new RogSpeedNova(),
            new MchoseMouse(),
            new StandardHidBattery()
        };

        if (extra != null) _providers.AddRange(extra);
    }

    public IReadOnlyList<IBatteryProvider> Providers => _providers;

    public List<DeviceEntry> Scan(bool includeUnsupported = false)
    {
        var groups = DeviceGroup.From(HidEnumerator.Enumerate());
        var entries = new List<DeviceEntry>();

        foreach (var g in groups)
        {
            var provider = _providers.FirstOrDefault(p => p.Matches(g));
            if (provider == null && !includeUnsupported) continue;

            entries.Add(new DeviceEntry { Group = g, Provider = provider });
        }

        return entries;
    }

    /// <summary>刷新所有设备的电量（串行）。</summary>
    public async Task RefreshAsync(IEnumerable<DeviceEntry> entries, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Provider == null) continue;

                try
                {
                    entry.Battery = await entry.Provider.ReadAsync(entry.Group, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    entry.Battery = BatteryInfo.Unknown(ex.Message);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>只读锁，给探测工具用，避免和定时刷新抢设备。</summary>
    public async Task<T> WithDeviceLockAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
