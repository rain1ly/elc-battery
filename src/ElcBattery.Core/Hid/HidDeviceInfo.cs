using System;

namespace ElcBattery.Core.Hid;

/// <summary>一个 HID 接口（对应系统中一条 device interface path）。</summary>
public sealed class HidDeviceInfo
{
    public required string DevicePath { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }

    public string Manufacturer { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string SerialNumber { get; init; } = "";

    /// <summary>顶层 collection 的 usage page / usage（来自 HidP_GetCaps）。</summary>
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }

    public ushort InputReportByteLength { get; init; }
    public ushort OutputReportByteLength { get; init; }
    public ushort FeatureReportByteLength { get; init; }

    /// <summary>是否可以被当前进程打开（键盘/鼠标的受保护接口可能打不开）。</summary>
    public bool CanOpen { get; init; }

    public ReportDescriptor? Descriptor { get; init; }

    /// <summary>接口号（来自路径中的 mi_XX），用于区分同一接收器的多个集合。</summary>
    public int InterfaceNumber { get; init; }

    /// <summary>VID/PID 相同视为同一物理设备的不同接口。</summary>
    public string DeviceKey => $"{VendorId:X4}:{ProductId:X4}";

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(ProductName) ? ProductName
        : !string.IsNullOrWhiteSpace(Manufacturer) ? $"{Manufacturer} 设备"
        : $"HID {VendorId:X4}:{ProductId:X4}";

    public override string ToString() =>
        $"[{DeviceKey}] {DisplayName} MI={InterfaceNumber} usage=0x{UsagePage:X4}/0x{Usage:X4} " +
        $"in={InputReportByteLength} out={OutputReportByteLength} open={CanOpen}";
}
