using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>
/// ROG SpeedNova 无线外设（龙鳞 2 Ace / 龙鳞 Ace / 战刃 3 等）。
///
/// 协议来源：华硕官方 GearLink 网页驱动（gearlink.asus.com.cn）的前端 bundle，
/// 关键函数 fetchPowerInfo / getSmartBatteryInfo。不是猜测。
///
///   ┌ 发送：HID Output Report（不是 Feature Report）
///   │   命令帧 = [0x12, 0x07, 0x00, 0x00] + 16 × 0x00，无线模式总长补零到 20 字节
///   │   Report ID 必须从报告描述符里动态取：
///   │     control collection = usagePage 0xFF01 / usage 1（取它的 Output reportId）
///   └ 接收：Interrupt IN，长度 ≥ 11，且前两字节为 0x12 0x07
///       [4]    电量百分比（0-100，直接取值）
///       [5]    自动休眠档位
///       [6]    低电量阈值
///       [7..8] 电压，小端 u16
///       [9]    ==1 表示正在充电
///       [10]   电池类型
///
/// 超时策略与官方一致：500 ms，失败重试一次。
/// </summary>
public sealed class RogSpeedNova : IBatteryProvider
{
    public const ushort AsusVendorId = 0x0B05;
    public const ushort ControlUsagePage = 0xFF01;
    public const ushort EventUsagePage = 0xFFC1;
    public const ushort VendorUsage = 1;

    /// <summary>读电量的命令头。</summary>
    private static readonly byte[] PowerCommand = { 0x12, 0x07, 0x00, 0x00 };

    private const int WirelessPayloadLength = 20;

    /// <summary>单次尝试的等待预算。鼠标休眠时接收器响应更慢，700ms 实测不够稳。</summary>
    private const int SingleAttemptBudgetMs = 900;

    /// <summary>尝试次数。休眠设备常常第一次没反应，第二次就通了。</summary>
    private const int ReadAttempts = 3;

    private const int RetryDelayMs = 220;

    /// <summary>发送前清空积压事件的上限，防止把预算耗在旧报文上。</summary>
    private const int DrainLimit = 12;
    private const int DrainTimeoutMs = 25;

    public string Id => "rog-speednova";

    public string DisplayName => "ROG SpeedNova 无线";

    public string? DriverUrl => "https://gearlink.asus.com.cn/zh-cn";

    /// <summary>
    /// 华硕 VID 下，只要某个接口的描述符里存在
    /// (0xFF01/1 的 Output) 或 (0xFFC1/1 的 Input)，就认定它说这套协议。
    /// 不写死 PID —— 不同型号 / 固件版本 PID 不一样。
    /// </summary>
    public bool Matches(DeviceGroup group)
    {
        if (group.VendorId != AsusVendorId) return false;

        return group.Interfaces.Any(i =>
        {
            var d = i.Descriptor;
            if (d == null) return false;
            return d.FindReportId(ControlUsagePage, VendorUsage, ReportKind.Output).HasValue
                || d.FindReportId(EventUsagePage, VendorUsage, ReportKind.Input).HasValue;
        });
    }

    public async Task<BatteryInfo> ReadAsync(DeviceGroup group, CancellationToken ct = default)
    {
        var (control, outId, eventIf, inId) = Locate(group);

        if (control == null && eventIf == null)
            return BatteryInfo.Unknown("未找到 0xFF01/0xFFC1 厂商集合（可能不是 SpeedNova 设备）");

        // control 与 event 常常是同一个接口，避免重复打开
        HidDeviceInfo controlSi = control ?? eventIf!;
        HidDeviceInfo eventSi = eventIf ?? control!;
        bool sameDevice = ReferenceEquals(controlSi, eventSi);

        using var ctrlDev = HidDevice.Open(controlSi, write: true);
        if (ctrlDev == null)
            return BatteryInfo.Unknown("接口被占用，打不开（可能是其他外设软件独占）");

        HidDevice? eventDev = null;
        if (sameDevice) eventDev = ctrlDev;
        else eventDev = HidDevice.Open(eventSi!, write: false);

        try
        {
            if (eventDev == null)
                return BatteryInfo.Unknown("事件接口打不开");

            // 鼠标休眠后接收器不是每次都能立刻回答：实测单次尝试约 1/3 成功率，
            // 所以必须有重试。3 次尝试 × 800ms，最坏 2.8s —— 只在失败时才会走满。
            BatteryInfo? last = null;

            for (int attempt = 0; attempt < ReadAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);

                var result = await TryReadOnceAsync(ctrlDev, eventDev, outId, inId, ct)
                    .ConfigureAwait(false);

                if (result.HasValue) return result;

                last = result;

                // 接口层面的错误重试无意义，直接返回
                if (result.Error != null && result.Error.StartsWith("发送命令失败", StringComparison.Ordinal))
                    return result;
            }

            return last ?? BatteryInfo.Unknown("设备无应答");
        }
        finally
        {
            if (!sameDevice) eventDev?.Dispose();
        }
    }

    /// <summary>发一次命令并等待应答。失败返回 IsConnected=false。</summary>
    private static async Task<BatteryInfo> TryReadOnceAsync(
        HidDevice ctrlDev, HidDevice eventDev, byte outId, byte inId, CancellationToken ct)
    {
        // 鼠标休眠期间会积压一堆 0x12 0x01 周期状态事件，不清掉的话
        // 新应答会排在它们后面，把时间预算耗在旧数据上。
        for (int i = 0; i < DrainLimit; i++)
        {
            var junk = await eventDev.ReadInputReportAsync(DrainTimeoutMs, ct).ConfigureAwait(false);
            if (junk == null) break;
        }

        var payload = new byte[WirelessPayloadLength];
        Array.Copy(PowerCommand, payload, PowerCommand.Length);

        if (!ctrlDev.SendOutputReport(outId, payload))
            return BatteryInfo.Unknown("发送命令失败（设备可能已断开）");

        // 事件接口上可能有历史事件，循环读到匹配的 0x12 0x07 为止
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < SingleAttemptBudgetMs)
        {
            int remain = SingleAttemptBudgetMs - (int)sw.ElapsedMilliseconds;
            if (remain <= 0) break;

            var resp = await eventDev.ReadInputReportAsync(Math.Min(250, remain), ct)
                                   .ConfigureAwait(false);
            if (resp == null) continue;

            var parsed = TryParsePower(resp, inId);
            if (parsed != null) return parsed;
        }

        return new BatteryInfo
        {
            IsConnected = false,
            Error = "设备无应答（接收器在线，但鼠标可能已关机）"
        };
    }

    /// <summary>从应答报文里提取电量；不是电量应答则返回 null。</summary>
    private static BatteryInfo? TryParsePower(byte[] resp, byte inId)
    {
        // 部分固件会在报文首字节带 Report ID
        int off = (inId != 0 && resp.Length > 0 && resp[0] == inId) ? 1 : 0;

        if (resp.Length < off + 11) return null;
        if (resp[off] != 0x12 || resp[off + 1] != 0x07) return null;

        int percent = resp[off + 4];
        int millivolts = resp[off + 7] | (resp[off + 8] << 8);

        return new BatteryInfo
        {
            Percent = Math.Clamp(percent, 0, 100),
            IsCharging = resp[off + 9] == 1,
            IsConnected = true,
            Millivolts = millivolts,
            Raw = Convert.ToHexString(resp)
        };
    }

    /// <summary>在设备组里定位 control（可写）与 event（可读）接口，并取出 Report ID。</summary>
    private static (HidDeviceInfo? Control, byte OutId, HidDeviceInfo? Event, byte InId)
        Locate(DeviceGroup group)
    {
        HidDeviceInfo? control = null;
        HidDeviceInfo? eventIf = null;
        byte outId = 0;
        byte inId = 0;

        foreach (var iface in group.Interfaces)
        {
            var d = iface.Descriptor;

            // 优先按 usagePage/usage 精确匹配
            var outReportId = d?.FindReportId(ControlUsagePage, VendorUsage, ReportKind.Output);
            if (outReportId.HasValue)
            {
                control = iface;
                outId = outReportId.Value;
            }
            else if (control == null && iface.UsagePage == ControlUsagePage)
            {
                // 顶层 collection 本身就是 0xFF01，但描述符没能解析出具体 usage：
                // 退而求其次，取这个接口上任意一个 Output Report ID。
                control = iface;
                outId = FirstReportId(d, ReportKind.Output);
            }

            var inReportId = d?.FindReportId(EventUsagePage, VendorUsage, ReportKind.Input);
            if (inReportId.HasValue)
            {
                eventIf = iface;
                inId = inReportId.Value;
            }
            else if (eventIf == null && iface.UsagePage == EventUsagePage)
            {
                // 事件接口通常只有 Input report，且系统不一定给得出 Report ID，
                // 读 Interrupt IN 时会把 ID 放在报文首字节，这里允许它为 0。
                eventIf = iface;
                inId = 0;
            }
        }

        // 实测（2026-10-20）：电量应答应从 0xFF01 接口自身的 Input 回来，
        // 首字节 Report ID 与发送时相同（=3）；0xFFC1 上只有 0x12 0x01 的周期状态事件。
        if (control != null && control.InputReportByteLength > 0)
        {
            eventIf = control;
            inId = outId;
        }

        return (control, outId, eventIf, inId);
    }

    private static byte FirstReportId(ReportDescriptor? descriptor, ReportKind kind)
    {
        if (descriptor == null) return 0;

        foreach (var kv in descriptor.FallbackReportIds)
            if (kv.Key.Kind == kind && kv.Value != 0)
                return kv.Value;

        foreach (var r in descriptor.Reports)
            if (r.Kind == kind && r.ReportId != 0)
                return r.ReportId;

        return 0;
    }
}
