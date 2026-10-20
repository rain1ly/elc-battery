using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>一次电量读取的结果。</summary>
public sealed class BatteryInfo
{
    /// <summary>0-100；-1 表示读不到。</summary>
    public int Percent { get; init; } = -1;

    public bool IsCharging { get; init; }

    /// <summary>设备是否在线（接收器在，但鼠标可能已关机）。</summary>
    public bool IsConnected { get; init; }

    public int Millivolts { get; init; }

    public string? Error { get; init; }

    /// <summary>原始应答（十六进制），便于排查协议。</summary>
    public string? Raw { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public bool HasValue => Percent >= 0;

    public static BatteryInfo Unknown(string error) => new() { Error = error, IsConnected = false };
}

/// <summary>同一个物理设备（VID/PID 相同）下的所有 HID 接口。</summary>
public sealed class DeviceGroup
{
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public required List<HidDeviceInfo> Interfaces { get; init; }

    public string DeviceKey => $"{VendorId:X4}:{ProductId:X4}";

    public string DisplayName
    {
        get
        {
            var named = Interfaces.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ProductName));
            if (named != null) return named.ProductName!;

            var mfr = Interfaces.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Manufacturer));
            if (mfr != null) return $"{mfr.Manufacturer} 设备";

            return $"HID 设备 {DeviceKey}";
        }
    }

    public static List<DeviceGroup> From(IEnumerable<HidDeviceInfo> devices) =>
        devices
            .Where(d => d.VendorId != 0)
            .GroupBy(d => (d.VendorId, d.ProductId))
            .Select(g => new DeviceGroup
            {
                VendorId = g.Key.VendorId,
                ProductId = g.Key.ProductId,
                Interfaces = g.ToList()
            })
            .ToList();
}

/// <summary>电量读取的统一抽象。新增品牌 = 新增一个实现 + 一条规则。</summary>
public interface IBatteryProvider
{
    /// <summary>规则 ID，写进 devices.json。</summary>
    string Id { get; }

    /// <summary>人类可读的名称，用于日志与界面。</summary>
    string DisplayName { get; }

    /// <summary>驱动页 / 官网地址。</summary>
    string? DriverUrl { get; }

    /// <summary>判断这个设备组是否归我管。</summary>
    bool Matches(DeviceGroup group);

    Task<BatteryInfo> ReadAsync(DeviceGroup group, CancellationToken ct = default);
}
