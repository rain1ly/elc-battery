using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElcBattery.Core.Hid;

namespace ElcBattery.Core.Devices;

/// <summary>
/// 未知设备的诊断工具：把 HID 结构全盘打印出来，并能嗅探原始输入报文。
/// 给"觅声天蝎座"、"狼蛛"、"迈从"这类没有公开协议的设备找电量字节用。
/// </summary>
public static class VendorProbe
{
    /// <summary>列出本机所有 HID 设备及其集合 / 字段结构。</summary>
    public static string DumpAll()
    {
        var sb = new StringBuilder();
        var groups = DeviceGroup.From(HidEnumerator.Enumerate());

        sb.AppendLine($"共 {groups.Count} 个物理设备（按 VID/PID 归组）");
        sb.AppendLine(new string('-', 78));

        foreach (var g in groups)
        {
            sb.AppendLine($"=== {g.DeviceKey}   {g.DisplayName}   接口数={g.Interfaces.Count}");

            foreach (var i in g.Interfaces)
            {
                sb.AppendLine(
                    $"  [MI {i.InterfaceNumber,2}] usage=0x{i.UsagePage:X4}/0x{i.Usage:X4}  " +
                    $"in={i.InputReportByteLength} out={i.OutputReportByteLength} feat={i.FeatureReportByteLength}  " +
                    $"open={i.CanOpen}");

                if (!string.IsNullOrWhiteSpace(i.Manufacturer) || !string.IsNullOrWhiteSpace(i.ProductName))
                    sb.AppendLine($"        mfr={i.Manufacturer} | product={i.ProductName} | sn={i.SerialNumber}");

                if (i.Descriptor != null)
                {
                    var lines = i.Descriptor.Dump().Split('\n');
                    // 只打印前 40 行，避免刷屏
                    for (int k = 0; k < Math.Min(lines.Length, 40); k++)
                        sb.AppendLine("        " + lines[k].TrimEnd());
                    if (lines.Length > 40)
                        sb.AppendLine($"        ... 省略 {lines.Length - 40} 行");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>嗅探某设备的输入报文（去重），用于对照电量变化找字节。</summary>
    public static async Task<List<string>> SniffAsync(
        DeviceGroup group, int seconds, CancellationToken ct = default)
    {
        var seen = new List<string>();

        foreach (var iface in group.Interfaces)
        {
            if (!iface.CanOpen) continue;

            using var dev = HidDevice.Open(iface, write: false);
            if (dev == null) continue;

            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            while (DateTime.UtcNow < deadline && seen.Count < 300)
            {
                var remain = (int)Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var report = await dev.ReadInputReportAsync(Math.Min(300, remain), ct).ConfigureAwait(false);
                if (report == null) continue;

                string hex = $"MI{iface.InterfaceNumber}: {Convert.ToHexString(report)}";
                if (!seen.Contains(hex)) seen.Add(hex);
            }
        }

        return seen;
    }
}
