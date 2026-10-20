using System;
using System.Collections.Generic;
using System.Linq;

namespace ElcBattery.Core.Hid;

public enum ReportKind
{
    Input,
    Output,
    Feature
}

/// <summary>报告里的一个数据字段（main item 的一个槽位）。</summary>
public sealed class HidFieldInfo
{
    public uint UsagePage { get; init; }
    public uint Usage { get; init; }
    public byte ReportId { get; init; }
    public ReportKind Kind { get; init; }
    public int BitOffset { get; init; }
    public int BitSize { get; init; }
    public int LogicalMin { get; init; }
    public int LogicalMax { get; init; }

    public override string ToString() =>
        $"{Kind} id=0x{ReportId:X2} 0x{UsagePage:X4}/0x{Usage:X4} bit={BitOffset}+{BitSize} log={LogicalMin}..{LogicalMax}";
}

/// <summary>某一条 Input/Output/Feature 报告项（collection 视角）。</summary>
public sealed class HidReportInfo
{
    public byte ReportId { get; init; }
    public ReportKind Kind { get; init; }
    public ushort CollectionUsagePage { get; init; }
    public ushort CollectionUsage { get; init; }
    public int BitLength { get; init; }

    public override string ToString() =>
        $"{Kind} id=0x{ReportId:X2} collection(0x{CollectionUsagePage:X4}/{CollectionUsage}) bits={BitLength}";
}

/// <summary>HID collection 树节点。</summary>
public sealed class HidCollectionNode
{
    public ushort UsagePage { get; set; }
    public ushort Usage { get; set; }
    public byte Type { get; set; }
    public List<HidCollectionNode> Children { get; } = new();
    public List<HidReportInfo> Reports { get; } = new();

    public IEnumerable<HidCollectionNode> Descendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Descendants())
                yield return d;
    }

    public override string ToString() =>
        $"Collection(0x{UsagePage:X4}/{Usage}) type=0x{Type:X2} children={Children.Count} reports={Reports.Count}";
}

/// <summary>
/// 轻量 HID 报告描述符解析器（零第三方依赖）。
///
/// 为什么必须解析描述符而不是写死 Report ID：
/// 华硕 GearLink 前端就是遍历 collections 动态取
/// usagePage==0xFF01 &amp;&amp; usage==1 那个 collection 的 outputReports[0].reportId，
/// 不同型号 / 固件的 Report ID 并不相同。
/// </summary>
public sealed class ReportDescriptor
{
    public HidCollectionNode Root { get; } = new();
    public List<HidReportInfo> Reports { get; } = new();
    public List<HidFieldInfo> Fields { get; } = new();

    /// <summary>(reportId, kind) -> 总字节长度（含 Report ID 前缀字节）。</summary>
    public Dictionary<(byte ReportId, ReportKind Kind), int> ReportByteLengths { get; } = new();

    /// <summary>
    /// 兜底来源：通过 HidP_GetValueCaps 直接问系统得到的 (usagePage, kind) -> ReportID。
    /// 当原始描述符读取失败时（部分系统 IOCTL 被拒），靠这张表定位 Report ID。
    /// </summary>
    public Dictionary<(ushort UsagePage, ReportKind Kind), byte> FallbackReportIds { get; } = new();

    /// <summary>创建一个不含描述符解析结果的空壳，只用来装 fallback 数据。</summary>
    public static ReportDescriptor Empty() => new();

    /// <summary>顶层 collection 的 usage page（来自 HidP_GetCaps）。</summary>
    public ushort TopLevelUsagePage { get; set; }

    public static ReportDescriptor Parse(byte[] data)
    {
        var result = new ReportDescriptor();
        var stack = new Stack<HidCollectionNode>();
        stack.Push(result.Root);

        ushort globalUsagePage = 0;
        int reportSize = 0;
        int reportCount = 0;
        byte reportId = 0;
        int logicalMin = 0;
        int logicalMax = 0;

        var localUsages = new List<uint>();

        var bitSums = new Dictionary<(byte, ReportKind), int>();
        var bitOffsets = new Dictionary<(byte, ReportKind), int>();
        var globalStack = new Stack<(ushort usagePage, int reportSize, int reportCount, byte reportId, int logicalMin, int logicalMax)>();

        int pos = 0;
        while (pos < data.Length)
        {
            byte prefix = data[pos++];

            if (prefix == 0xFE) // long item
            {
                if (pos + 2 > data.Length) break;
                int dsize = data[pos];
                pos += 2 + dsize;
                continue;
            }

            int sizeCode = prefix & 0x03;
            int size = sizeCode == 3 ? 4 : sizeCode;
            int type = (prefix >> 2) & 0x03;
            int tag = (prefix >> 4) & 0x0F;

            if (pos + size > data.Length) break;

            uint u = 0;
            for (int i = 0; i < size; i++)
                u |= (uint)data[pos + i] << (8 * i);
            pos += size;

            int s = size switch
            {
                1 => (sbyte)u,
                2 => (short)u,
                4 => (int)u,
                _ => 0
            };

            switch (type)
            {
                case 0: // Main
                    switch (tag)
                    {
                        case 0x8:
                            Record(result, stack, ReportKind.Input, reportId, reportSize, reportCount,
                                   globalUsagePage, localUsages, logicalMin, logicalMax, bitSums, bitOffsets);
                            localUsages.Clear();
                            break;
                        case 0x9:
                            Record(result, stack, ReportKind.Output, reportId, reportSize, reportCount,
                                   globalUsagePage, localUsages, logicalMin, logicalMax, bitSums, bitOffsets);
                            localUsages.Clear();
                            break;
                        case 0xB:
                            Record(result, stack, ReportKind.Feature, reportId, reportSize, reportCount,
                                   globalUsagePage, localUsages, logicalMin, logicalMax, bitSums, bitOffsets);
                            localUsages.Clear();
                            break;
                        case 0xA:
                        {
                            var node = new HidCollectionNode { Type = (byte)u };
                            if (localUsages.Count > 0)
                            {
                                uint first = localUsages[0];
                                if (first > 0xFFFF)
                                {
                                    node.UsagePage = (ushort)(first >> 16);
                                    node.Usage = (ushort)(first & 0xFFFF);
                                }
                                else
                                {
                                    node.UsagePage = globalUsagePage;
                                    node.Usage = (ushort)first;
                                }
                            }
                            else
                            {
                                node.UsagePage = globalUsagePage;
                            }

                            stack.Peek().Children.Add(node);
                            stack.Push(node);
                            localUsages.Clear();
                            break;
                        }
                        case 0xC:
                            if (stack.Count > 1) stack.Pop();
                            localUsages.Clear();
                            break;
                    }
                    break;

                case 1: // Global
                    switch (tag)
                    {
                        case 0x0: globalUsagePage = (ushort)u; break;
                        case 0x1: logicalMin = s; break;
                        case 0x2: logicalMax = s; break;
                        case 0x7: reportSize = (int)u; break;
                        case 0x8: reportId = (byte)u; break;
                        case 0x9: reportCount = (int)u; break;
                        case 0xA: // Push
                            globalStack.Push((globalUsagePage, reportSize, reportCount, reportId, logicalMin, logicalMax));
                            break;
                        case 0xB: // Pop
                            if (globalStack.Count > 0)
                            {
                                var g = globalStack.Pop();
                                globalUsagePage = g.usagePage;
                                reportSize = g.reportSize;
                                reportCount = g.reportCount;
                                reportId = g.reportId;
                                logicalMin = g.logicalMin;
                                logicalMax = g.logicalMax;
                            }
                            break;
                    }
                    break;

                case 2: // Local
                    if (tag == 0x0) localUsages.Add(u);
                    break;
            }
        }

        foreach (var kv in bitSums)
        {
            int bytes = (kv.Value + 7) / 8;
            if (kv.Key.Item1 != 0) bytes += 1; // Report ID 前缀
            result.ReportByteLengths[kv.Key] = bytes;
        }

        return result;
    }

    private static void Record(
        ReportDescriptor result,
        Stack<HidCollectionNode> stack,
        ReportKind kind,
        byte reportId,
        int reportSize,
        int reportCount,
        ushort globalUsagePage,
        List<uint> localUsages,
        int logicalMin,
        int logicalMax,
        Dictionary<(byte, ReportKind), int> bitSums,
        Dictionary<(byte, ReportKind), int> bitOffsets)
    {
        var top = stack.Peek();
        var key = (reportId, kind);

        int totalBits = reportSize * reportCount;
        int startOffset = bitOffsets.TryGetValue(key, out int o) ? o : 0;

        // collection 级记录
        top.Reports.Add(new HidReportInfo
        {
            ReportId = reportId,
            Kind = kind,
            CollectionUsagePage = top.UsagePage,
            CollectionUsage = top.Usage,
            BitLength = totalBits
        });
        result.Reports.Add(top.Reports[^1]);

        // 字段级记录（用于按 bit 偏移取值）
        for (int i = 0; i < reportCount; i++)
        {
            uint usagePage = globalUsagePage;
            uint usage = 0;

            if (localUsages.Count > 0)
            {
                uint raw = localUsages[Math.Min(i, localUsages.Count - 1)];
                if (raw > 0xFFFF)
                {
                    usagePage = raw >> 16;
                    usage = raw & 0xFFFF;
                }
                else
                {
                    usage = raw;
                }
            }

            result.Fields.Add(new HidFieldInfo
            {
                UsagePage = usagePage,
                Usage = usage,
                ReportId = reportId,
                Kind = kind,
                BitOffset = startOffset + i * reportSize,
                BitSize = reportSize,
                LogicalMin = logicalMin,
                LogicalMax = logicalMax
            });
        }

        bitSums[key] = (bitSums.TryGetValue(key, out int cur) ? cur : 0) + totalBits;
        bitOffsets[key] = startOffset + totalBits;
    }

    /// <summary>在指定 usagePage/usage 的 collection 中查找某一方向的 Report ID。</summary>
    public byte? FindReportId(ushort usagePage, ushort usage, ReportKind kind)
    {
        foreach (var node in Root.Descendants())
        {
            if (node.UsagePage != usagePage || node.Usage != usage) continue;
            foreach (var r in node.Reports)
                if (r.Kind == kind) return r.ReportId;
        }

        foreach (var r in Reports)
            if (r.Kind == kind && r.CollectionUsagePage == usagePage && r.CollectionUsage == usage)
                return r.ReportId;

        // 描述符解析不可用时，退回 HidP_GetValueCaps 得到的结果
        if (FallbackReportIds.TryGetValue((usagePage, kind), out byte fallback))
            return fallback;

        return null;
    }

    public HidFieldInfo? FindField(uint usagePage, uint usage, ReportKind kind) =>
        Fields.FirstOrDefault(f => f.UsagePage == usagePage && f.Usage == usage && f.Kind == kind);

    public bool HasUsagePage(ushort usagePage) =>
        Root.Descendants().Any(n => n.UsagePage == usagePage);

    public string Dump()
    {
        var lines = new List<string>();
        void Walk(HidCollectionNode n, int depth)
        {
            lines.Add(new string(' ', depth * 2) + n);
            foreach (var r in n.Reports)
                lines.Add(new string(' ', depth * 2 + 2) + "- " + r);
            foreach (var c in n.Children)
                Walk(c, depth + 1);
        }
        Walk(Root, 0);
        return string.Join(Environment.NewLine, lines);
    }
}
