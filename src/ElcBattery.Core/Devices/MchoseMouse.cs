using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>
/// 迈从（MCHOSE）鼠标，VID 0x5253 家族（实测型号 A7 Pro / PID 0x1021）。
///
/// 协议（2026-10-20 真机实测，非推测）：
///   配置接口 = 顶层 usagePage 0xFF01，Feature Report 长度 65
///   读取电量 = HidD_GetFeature(0x14)，无需先发命令
///   应答格式 = [0x14, 电量%, 电量%, 0x00, ...]
///               ↑回显  ↑byte[1] 是最可靠的电量位（byte[2] 同值）
///
/// 同一 collection 上还有：
///   0x11 → 状态缓冲（随时间变化，语义未定）
///   0x12 → DPI / 配置回显
///
/// 注意：这是 RealTek 主控的新一代方案（5253）。老一代 VID_3837
/// 用的是"发 0x11 命令、从输入报文 0x13 读 0xE2 前缀"的另一套协议，
/// 这里没有实现 —— 没实物验证过的东西不往里写。
/// </summary>
public sealed class MchoseMouse : IBatteryProvider
{
    public const ushort MchoseVendorId = 0x5253;
    private const ushort ConfigUsagePage = 0xFF01;
    private const byte BatteryReportId = 0x14;

    public string Id => "mchose-hid";

    public string DisplayName => "迈从（MCHOSE）";

    public string? DriverUrl => "https://www.mchose.com.cn";

    public bool Matches(DeviceGroup group) => group.VendorId == MchoseVendorId;

    public Task<BatteryInfo> ReadAsync(DeviceGroup group, CancellationToken ct = default)
    {
        // 找配置接口：顶层 usage page 就是 0xFF01 的那个
        var iface = group.Interfaces.FirstOrDefault(i => i.UsagePage == ConfigUsagePage)
                    ?? group.Interfaces.FirstOrDefault(i => i.FeatureReportByteLength >= 60);

        if (iface == null)
            return Task.FromResult(BatteryInfo.Unknown("没找到 0x0xFF01 配置接口"));

        using var dev = HidDevice.Open(iface, write: false);
        if (dev == null)
            return Task.FromResult(BatteryInfo.Unknown("配置接口被占用，打不开"));

        int length = Math.Max((int)iface.FeatureReportByteLength, 65);
        var report = dev.GetFeatureReport(BatteryReportId, length);

        if (report == null || report.Length < 3)
            return Task.FromResult(BatteryInfo.Unknown("读电量失败（GetFeature 0x14 无应答）"));

        // 正常情况下 byte[0] 会回显 0x14
        int offset = report[0] == BatteryReportId ? 1 : 0;

        if (report.Length <= offset)
            return Task.FromResult(BatteryInfo.Unknown("应答长度异常"));

        int percent = report[offset];

        // 值不在 0-100 说明这不是电量，宁可显示读不到也别给错数字
        if (percent < 0 || percent > 100)
            return Task.FromResult(new BatteryInfo
            {
                IsConnected = false,
                Error = $"电量字段异常（读到 {percent}），该型号可能不支持",
                Raw = Convert.ToHexString(report)
            });

        return Task.FromResult(new BatteryInfo
        {
            Percent = percent,
            IsConnected = true,
            IsCharging = false,
            Raw = Convert.ToHexString(report)
        });
    }
}
