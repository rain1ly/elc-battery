using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>
/// 标准 HID 电池：实现了 Usage Page 0x06 (Generic Device Controls) /
/// Usage 0x20 (Battery Strength) 的设备，直接读 Input Report 里那个字段。
///
/// 很多国产 2.4G 外设、蓝牙耳机走这一套，属于"零配置可用"。
/// </summary>
public sealed class StandardHidBattery : IBatteryProvider
{
    public const uint BatteryUsagePage = 0x06;
    public const uint BatteryStrengthUsage = 0x20;

    public string Id => "hid-battery-strength";
    public string DisplayName => "标准 HID 电池";
    public string? DriverUrl => null;

    public bool Matches(DeviceGroup group) =>
        group.Interfaces.Any(i =>
            i.Descriptor?.FindField(BatteryUsagePage, BatteryStrengthUsage, ReportKind.Input) != null);

    public async Task<BatteryInfo> ReadAsync(DeviceGroup group, CancellationToken ct = default)
    {
        foreach (var iface in group.Interfaces)
        {
            var desc = iface.Descriptor;
            if (desc == null) continue;

            var field = desc.FindField(BatteryUsagePage, BatteryStrengthUsage, ReportKind.Input);
            if (field == null) continue;

            using var dev = HidDevice.Open(iface, write: false);
            if (dev == null) continue;

            // 这类设备通常只在电量变化时上报，等 1.5 秒给它机会
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline)
            {
                int remain = (int)Math.Max(150, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var report = await dev.ReadInputReportAsync(remain, ct).ConfigureAwait(false);
                if (report == null) continue;

                // 跳过 Report ID 前缀
                int dataStart = (field.ReportId != 0 && report.Length > 0 && report[0] == field.ReportId) ? 1 : 0;
                int needed = dataStart + (field.BitOffset + field.BitSize + 7) / 8;
                if (report.Length < needed) continue;

                var payload = new byte[report.Length - dataStart];
                Array.Copy(report, dataStart, payload, 0, payload.Length);

                int raw = ExtractBits(payload, field.BitOffset, field.BitSize);

                int percent = field.LogicalMax > 0 && field.LogicalMax != 100
                    ? (int)Math.Round(raw * 100.0 / field.LogicalMax)
                    : raw;

                return new BatteryInfo
                {
                    Percent = Math.Clamp(percent, 0, 100),
                    IsConnected = true,
                    IsCharging = false,
                    Raw = Convert.ToHexString(report)
                };
            }

            return new BatteryInfo
            {
                IsConnected = false,
                Error = "该设备支持标准电池用法，但当前没有上报数据"
            };
        }

        return BatteryInfo.Unknown("未找到标准 Battery Strength 字段");
    }

    /// <summary>按 HID 的位序（小端、LSB 起）从报文里取字段。</summary>
    internal static int ExtractBits(byte[] data, int bitOffset, int bitSize)
    {
        if (bitSize <= 0) return 0;

        int byteOffset = bitOffset / 8;
        int bitInByte = bitOffset % 8;
        int bytesNeeded = (bitInByte + bitSize + 7) / 8;

        uint raw = 0;
        for (int i = 0; i < bytesNeeded; i++)
        {
            int idx = byteOffset + i;
            if (idx >= data.Length) break;
            raw |= (uint)data[idx] << (8 * i);
        }

        raw >>= bitInByte;
        uint mask = bitSize >= 32 ? 0xFFFFFFFFu : (1u << bitSize) - 1;
        return (int)(raw & mask);
    }
}
