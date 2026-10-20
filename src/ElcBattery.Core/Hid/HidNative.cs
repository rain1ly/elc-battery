using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ElcBattery.Core.Hid;

/// <summary>
/// hid.dll / setupapi.dll / kernel32.dll 的原生声明。
/// 只做只读探测与 Output Report 发送，不独占设备、不修改设备配置。
/// </summary>
internal static class HidNative
{
    // ---- kernel32 ----
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    // ---- setupapi ----
    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;
    public const int ERROR_NO_MORE_ITEMS = 259;

    // CTL_CODE(0x0B, 0x01, METHOD_NEITHER, FILE_ANY_ACCESS)
    public const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0007;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsagePage;
        public ushort LinkUsage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        // Reserved2[5] 展开成独立字段，避免 ByValArray 需要预分配数组
        public ushort Reserved2_0, Reserved2_1, Reserved2_2, Reserved2_3, Reserved2_4;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // 联合体 Range / NotRange，共 16 字节
        public uint Data0, Data1, Data2, Data3;
    }

    /// <summary>HIDP_STATUS_SUCCESS</summary>
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    public const int HIDP_INPUT = 0;
    public const int HIDP_OUTPUT = 1;
    public const int HIDP_FEATURE = 2;

    // ---- hid.dll ----
    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    /// <summary>
    /// 直接问系统"某个 usage page 的字段落在哪个 Report ID 里"。
    /// 比解析原始报告描述符可靠：后者要走 IOCTL_HID_GET_REPORT_DESCRIPTOR，
    /// 而部分系统/驱动会返回 ERROR_INCORRECT_FUNCTION。
    /// </summary>
    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(
        int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps,
        ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll")]
    public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int bufferLength);

    [DllImport("hid.dll")]
    public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int bufferLength);

    [DllImport("hid.dll")]
    public static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int numberBuffers);

    // ---- setupapi.dll ----
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
        out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---- kernel32.dll ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle h, byte[] buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);

    /// <summary>
    /// 发送 Output Report。
    /// 必须用 WriteFile，不能用 HidD_SetOutputReport ——
    /// 后者对这条华硕命令虽然返回成功，但设备实际收不到；实测 WriteFile 才有应答。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle h, byte[] buffer, int bytesToWrite, out int bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
