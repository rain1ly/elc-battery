using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ElcBattery.Core.Hid;

/// <summary>枚举系统里所有 HID 接口并解析其能力与报告描述符。</summary>
public static class HidEnumerator
{
    private static readonly Regex VidPidRegex =
        new(@"vid_([0-9a-fA-F]{4})&pid_([0-9a-fA-F]{4})", RegexOptions.Compiled);

    private static readonly Regex MiRegex =
        new(@"&mi_([0-9a-fA-F]{2})", RegexOptions.Compiled);

    public static List<HidDeviceInfo> Enumerate()
    {
        var list = new List<HidDeviceInfo>();
        HidNative.HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfoSet = HidNative.SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            return list;

        try
        {
            var did = new HidNative.SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf<HidNative.SP_DEVICE_INTERFACE_DATA>();

            for (int index = 0; ; index++)
            {
                if (!HidNative.SetupDiEnumDeviceInterfaces(
                        devInfoSet, IntPtr.Zero, ref hidGuid, index, ref did))
                {
                    if (Marshal.GetLastWin32Error() == HidNative.ERROR_NO_MORE_ITEMS)
                        break;
                    break;
                }

                string? path = GetDevicePath(devInfoSet, ref did);
                if (path == null) continue;

                var info = Inspect(path);
                if (info != null) list.Add(info);
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return list;
    }

    private static string? GetDevicePath(IntPtr devInfoSet, ref HidNative.SP_DEVICE_INTERFACE_DATA did)
    {
        HidNative.SetupDiGetDeviceInterfaceDetail(
            devInfoSet, ref did, IntPtr.Zero, 0, out int required, IntPtr.Zero);

        if (required <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA 的 cbSize 在 64 位下是 8
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4);

            if (!HidNative.SetupDiGetDeviceInterfaceDetail(
                    devInfoSet, ref did, buffer, required, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidDeviceInfo? Inspect(string path)
    {
        ushort vid = 0, pid = 0;
        int mi = -1;

        var m = VidPidRegex.Match(path);
        if (m.Success)
        {
            vid = Convert.ToUInt16(m.Groups[1].Value, 16);
            pid = Convert.ToUInt16(m.Groups[2].Value, 16);
        }

        var mm = MiRegex.Match(path);
        if (mm.Success) mi = Convert.ToInt32(mm.Groups[1].Value, 16);

        SafeFileHandle handle = HidNative.CreateFile(
            path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        bool canOpen = !handle.IsInvalid;

        if (!canOpen)
        {
            // 只读再试一次（部分接口不允许写）
            handle = HidNative.CreateFile(
                path, HidNative.GENERIC_READ,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return new HidDeviceInfo
            {
                DevicePath = path,
                VendorId = vid,
                ProductId = pid,
                InterfaceNumber = mi,
                CanOpen = false
            };
        }

        try
        {
            var attrs = new HidNative.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
            if (HidNative.HidD_GetAttributes(handle, ref attrs))
            {
                vid = attrs.VendorID;
                pid = attrs.ProductID;
            }

            var caps = GetCaps(handle);
            var descriptor = ReadDescriptor(handle);

            // 描述符 IOCTL 被系统拒绝时（ERROR_INCORRECT_FUNCTION），
            // 改用 HidP_GetValueCaps 直接问每个 usage page 的 Report ID。
            if (descriptor == null)
                descriptor = BuildFromValueCaps(handle, caps);
            else
                descriptor.TopLevelUsagePage = caps.UsagePage;

            return new HidDeviceInfo
            {
                DevicePath = path,
                VendorId = vid,
                ProductId = pid,
                VersionNumber = attrs.VersionNumber,
                Manufacturer = ReadString(handle, 1),
                ProductName = ReadString(handle, 2),
                SerialNumber = ReadString(handle, 3),
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                InputReportByteLength = caps.InputReportByteLength,
                OutputReportByteLength = caps.OutputReportByteLength,
                FeatureReportByteLength = caps.FeatureReportByteLength,
                CanOpen = true,
                Descriptor = descriptor,
                InterfaceNumber = mi
            };
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static HidNative.HIDP_CAPS GetCaps(SafeFileHandle handle)
    {
        var caps = new HidNative.HIDP_CAPS();
        if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero)
            return caps;

        try
        {
            HidNative.HidP_GetCaps(pp, ref caps);
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(pp);
        }
        return caps;
    }

    /// <summary>用 HidP_GetValueCaps 建立 usagePage -> ReportID 的兜底映射。</summary>
    private static ReportDescriptor? BuildFromValueCaps(SafeFileHandle handle, HidNative.HIDP_CAPS caps)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero)
            return null;

        try
        {
            var desc = ReportDescriptor.Empty();
            desc.TopLevelUsagePage = caps.UsagePage;

            CollectValueCaps(pp, caps.NumberOutputValueCaps, HidNative.HIDP_OUTPUT, ReportKind.Output, desc);
            CollectValueCaps(pp, caps.NumberInputValueCaps, HidNative.HIDP_INPUT, ReportKind.Input, desc);
            CollectValueCaps(pp, caps.NumberFeatureValueCaps, HidNative.HIDP_FEATURE, ReportKind.Feature, desc);

            return desc;
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(pp);
        }
    }

    private static void CollectValueCaps(
        IntPtr preparsed, int count, int reportType, ReportKind kind, ReportDescriptor desc)
    {
        if (count <= 0) return;

        var array = new HidNative.HIDP_VALUE_CAPS[count];
        ushort length = (ushort)count;

        if (HidNative.HidP_GetValueCaps(reportType, array, ref length, preparsed) != HidNative.HIDP_STATUS_SUCCESS)
            return;

        for (int i = 0; i < length; i++)
        {
            ushort page = array[i].UsagePage;
            if (page == 0) continue;

            var key = (page, kind);
            if (!desc.FallbackReportIds.ContainsKey(key))
                desc.FallbackReportIds[key] = array[i].ReportID;
        }
    }

    private static ReportDescriptor? ReadDescriptor(SafeFileHandle handle)
    {
        var buffer = new byte[4096];
        if (!HidNative.DeviceIoControl(
                handle, HidNative.IOCTL_HID_GET_REPORT_DESCRIPTOR,
                IntPtr.Zero, 0, buffer, buffer.Length, out int returned, IntPtr.Zero)
            || returned <= 0)
            return null;

        var data = new byte[returned];
        Array.Copy(buffer, data, returned);

        try
        {
            return ReportDescriptor.Parse(data);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(SafeFileHandle handle, int which)
    {
        var buf = new byte[512];
        bool ok = which switch
        {
            1 => HidNative.HidD_GetManufacturerString(handle, buf, buf.Length),
            2 => HidNative.HidD_GetProductString(handle, buf, buf.Length),
            3 => HidNative.HidD_GetSerialNumberString(handle, buf, buf.Length),
            _ => false
        };

        if (!ok) return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
    }
}
