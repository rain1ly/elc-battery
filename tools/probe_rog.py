"""
ROG SpeedNova 电量协议探针（独立验证工具，不依赖 .NET）

用途：
    在编译主程序之前，先直接对接收器发一次读电量命令，确认
    Report ID 解析和电量字节位置是否正确。

用法：
    python tools/probe_rog.py

输出：
    会列出本机所有 HID 接口，然后对 VID_0B05 的 SpeedNova 设备发送
    [0x12, 0x07, 0x00, 0x00] + 补零，打印应答的十六进制和解析出的电量。
"""

import ctypes
import threading
from ctypes import wintypes

hid = ctypes.WinDLL("hid.dll")
setupapi = ctypes.WinDLL("setupapi.dll")
kernel32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 0x01
FILE_SHARE_WRITE = 0x02
OPEN_EXISTING = 3
ERROR_NO_MORE_ITEMS = 259
IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0007

CONTROL_USAGE_PAGE = 0xFF01
EVENT_USAGE_PAGE = 0xFFC1
VENDOR_USAGE = 1
DEBUG = False


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wintypes.DWORD),
        ("Data2", wintypes.WORD),
        ("Data3", wintypes.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("InterfaceClassGuid", GUID),
        ("Flags", wintypes.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [
        ("Size", wintypes.ULONG),
        ("VendorID", wintypes.USHORT),
        ("ProductID", wintypes.USHORT),
        ("VersionNumber", wintypes.USHORT),
    ]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wintypes.USHORT),
        ("UsagePage", wintypes.USHORT),
        ("InputReportByteLength", wintypes.USHORT),
        ("OutputReportByteLength", wintypes.USHORT),
        ("FeatureReportByteLength", wintypes.USHORT),
        ("Reserved", wintypes.USHORT * 17),
        ("NumberLinkCollectionNodes", wintypes.USHORT),
        ("NumberInputButtonCaps", wintypes.USHORT),
        ("NumberInputValueCaps", wintypes.USHORT),
        ("NumberInputDataIndices", wintypes.USHORT),
        ("NumberOutputButtonCaps", wintypes.USHORT),
        ("NumberOutputValueCaps", wintypes.USHORT),
        ("NumberOutputDataIndices", wintypes.USHORT),
        ("NumberFeatureButtonCaps", wintypes.USHORT),
        ("NumberFeatureValueCaps", wintypes.USHORT),
        ("NumberFeatureDataIndices", wintypes.USHORT),
    ]


class HIDP_VALUE_CAPS(ctypes.Structure):
    _fields_ = [
        ("UsagePage", wintypes.USHORT),
        ("ReportID", ctypes.c_ubyte),
        ("IsAlias", ctypes.c_ubyte),
        ("BitField", wintypes.USHORT),
        ("LinkCollection", wintypes.USHORT),
        ("LinkUsagePage", wintypes.USHORT),
        ("LinkUsage", wintypes.USHORT),
        ("IsRange", ctypes.c_ubyte),
        ("IsStringRange", ctypes.c_ubyte),
        ("IsDesignatorRange", ctypes.c_ubyte),
        ("IsAbsolute", ctypes.c_ubyte),
        ("HasNull", ctypes.c_ubyte),
        ("Reserved", ctypes.c_ubyte),
        ("BitSize", wintypes.USHORT),
        ("ReportCount", wintypes.USHORT),
        ("Reserved2", wintypes.USHORT * 5),
        ("UnitsExp", wintypes.ULONG),
        ("Units", wintypes.ULONG),
        ("LogicalMin", wintypes.LONG),
        ("LogicalMax", wintypes.LONG),
        ("PhysicalMin", wintypes.LONG),
        ("PhysicalMax", wintypes.LONG),
        # 联合体：Range / NotRange 都是 8 个 USHORT = 16 字节。
        # 之前写成 3 个 ULONG（12 字节），sizeof 少 4，数组步长错位会直接段错误。
        ("Data", wintypes.ULONG * 4),
    ]


HIDP_STATUS_SUCCESS = 0x00110000


def _setup_prototypes():
    """
    必须显式声明 restype/argtypes。
    否则 64 位下 ctypes 默认按 32 位 int 处理返回值，
    HANDLE 会被截断，SetupDiGetClassDevs 返回的句柄直接失效，
    结果就是"枚举到 0 个设备"。
    """
    hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
    hid.HidD_GetHidGuid.restype = None

    hid.HidD_GetAttributes.argtypes = [wintypes.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
    hid.HidD_GetAttributes.restype = wintypes.BOOLEAN

    hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
    hid.HidD_GetPreparsedData.restype = wintypes.BOOLEAN

    hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
    hid.HidP_GetCaps.restype = wintypes.LONG

    hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
    hid.HidD_FreePreparsedData.restype = wintypes.BOOLEAN

    hid.HidP_GetValueCaps.argtypes = [
        wintypes.INT, ctypes.POINTER(HIDP_VALUE_CAPS),
        ctypes.POINTER(wintypes.USHORT), ctypes.c_void_p
    ]
    hid.HidP_GetValueCaps.restype = wintypes.LONG

    hid.HidD_SetOutputReport.argtypes = [wintypes.HANDLE, ctypes.c_char_p, wintypes.ULONG]
    hid.HidD_SetOutputReport.restype = wintypes.BOOLEAN

    setupapi.SetupDiGetClassDevsW.argtypes = [
        ctypes.POINTER(GUID), wintypes.LPCWSTR, wintypes.HWND, wintypes.DWORD
    ]
    setupapi.SetupDiGetClassDevsW.restype = wintypes.HANDLE

    setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(GUID),
        wintypes.DWORD, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)
    ]
    setupapi.SetupDiEnumDeviceInterfaces.restype = wintypes.BOOL

    setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
        wintypes.HANDLE, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA),
        ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p
    ]
    setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wintypes.BOOL

    setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]
    setupapi.SetupDiDestroyDeviceInfoList.restype = wintypes.BOOL

    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
        wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE
    ]
    kernel32.CreateFileW.restype = wintypes.HANDLE

    kernel32.DeviceIoControl.argtypes = [
        wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
        ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p
    ]
    kernel32.DeviceIoControl.restype = wintypes.BOOL

    kernel32.ReadFile.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p
    ]
    kernel32.ReadFile.restype = wintypes.BOOL

    kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    kernel32.CancelIoEx.restype = wintypes.BOOL

    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL


INVALID_HANDLE = wintypes.HANDLE(-1).value


def enumerate_hid_paths():
    hid_guid = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(hid_guid))

    flags = DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
    dev_info = setupapi.SetupDiGetClassDevsW(
        ctypes.byref(hid_guid), None, None, flags
    )
    if dev_info in (INVALID_HANDLE, 0, None):
        print(f"  SetupDiGetClassDevs 失败，错误码 {ctypes.get_last_error()}")
        return []

    paths = []
    did = SP_DEVICE_INTERFACE_DATA()
    did.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)

    index = 0
    while True:
        ok = setupapi.SetupDiEnumDeviceInterfaces(
            dev_info, None, ctypes.byref(hid_guid), index, ctypes.byref(did)
        )
        if not ok:
            break

        required = wintypes.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(
            dev_info, ctypes.byref(did), None, 0, ctypes.byref(required), None
        )

        if required.value > 0:
            buf = ctypes.create_string_buffer(required.value)
            ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[0] = 8
            ok = setupapi.SetupDiGetDeviceInterfaceDetailW(
                dev_info, ctypes.byref(did), buf, required.value,
                ctypes.byref(required), None
            )
            if ok:
                paths.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))

        index += 1

    setupapi.SetupDiDestroyDeviceInfoList(dev_info)
    return paths


def open_device(path):
    handle = kernel32.CreateFileW(
        path,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        None, OPEN_EXISTING, 0, None
    )
    if handle in (INVALID_HANDLE, 0, None):
        handle = kernel32.CreateFileW(
            path, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None, OPEN_EXISTING, 0, None
        )
    if handle in (INVALID_HANDLE, 0, None):
        return None
    return handle


def get_attributes(handle):
    attrs = HIDD_ATTRIBUTES()
    attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
    if hid.HidD_GetAttributes(handle, ctypes.byref(attrs)):
        return attrs.VendorID, attrs.ProductID
    return None, None


def get_caps(handle):
    caps = HIDP_CAPS()
    pp = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(handle, ctypes.byref(pp)) or not pp:
        return caps
    try:
        hid.HidP_GetCaps(pp, ctypes.byref(caps))
    finally:
        hid.HidD_FreePreparsedData(pp)
    return caps


def get_value_caps(handle, report_type):
    """report_type: 0=Input 1=Output 2=Feature -> [(usagePage, reportId), ...]"""
    pp = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(handle, ctypes.byref(pp)) or not pp:
        return []

    try:
        caps = HIDP_CAPS()
        hid.HidP_GetCaps(pp, ctypes.byref(caps))

        total = {
            0: caps.NumberInputValueCaps,
            1: caps.NumberOutputValueCaps,
            2: caps.NumberFeatureValueCaps,
        }.get(report_type, 0)

        if total <= 0:
            return []

        arr = (HIDP_VALUE_CAPS * total)()
        length = wintypes.USHORT(total)
        status = hid.HidP_GetValueCaps(report_type, arr, ctypes.byref(length), pp)

        if status != HIDP_STATUS_SUCCESS:
            return []

        return [(arr[i].UsagePage, arr[i].ReportID) for i in range(length.value)]
    finally:
        hid.HidD_FreePreparsedData(pp)


def find_id_from_valuecaps(handle, report_type, usage_page):
    caps = get_value_caps(handle, report_type)
    for page, rid in caps:
        if page == usage_page and rid:
            return rid
    for page, rid in caps:
        if rid:
            return rid
    return None


def read_descriptor(handle):
    buf = ctypes.create_string_buffer(4096)
    returned = wintypes.DWORD()
    ok = kernel32.DeviceIoControl(
        handle, IOCTL_HID_GET_REPORT_DESCRIPTOR,
        None, 0, ctypes.cast(buf, ctypes.c_void_p), 4096,
        ctypes.byref(returned), None
    )
    if not ok or returned.value == 0:
        if DEBUG:
            print(f"    [debug] DeviceIoControl 失败 ok={ok} err={ctypes.get_last_error()}")
        return None
    return buf.raw[:returned.value]


def parse_reports(desc, want_page):
    """极简解析：返回 (report_id, kind) 列表，kind: 'in' / 'out'。只跟踪 collection usage。"""
    reports = []
    stack = []
    usage_page = 0
    report_id = 0
    pos = 0
    local_usages = []

    while pos < len(desc):
        prefix = desc[pos]
        pos += 1
        if prefix == 0xFE:
            if pos + 2 > len(desc):
                break
            size = desc[pos]
            pos += 2 + size
            continue

        size_code = prefix & 0x03
        size = 4 if size_code == 3 else size_code
        typ = (prefix >> 2) & 0x03
        tag = (prefix >> 4) & 0x0F

        if pos + size > len(desc):
            break

        value = int.from_bytes(desc[pos:pos + size], "little", signed=False) if size else 0
        pos += size

        if typ == 0:  # Main
            if tag == 0x0A:  # Collection
                page, usage = 0, 0
                if local_usages:
                    first = local_usages[0]
                    if first > 0xFFFF:
                        page, usage = first >> 16, first & 0xFFFF
                    else:
                        page, usage = usage_page, first & 0xFFFF
                else:
                    page = usage_page
                stack.append((page, usage))
                local_usages = []
            elif tag == 0xC0:  # EndCollection
                if stack:
                    stack.pop()
                local_usages = []
            elif tag in (0x08, 0x09, 0xB1):  # Input / Output / Feature
                kind = {0x08: "in", 0x09: "out", 0xB1: "feat"}[tag]
                page, usage = stack[-1] if stack else (0, 0)
                reports.append((report_id, kind, page, usage))
                local_usages = []
        elif typ == 1:  # Global
            if tag == 0x00:
                usage_page = value
            elif tag == 0x08:
                report_id = value & 0xFF
        elif typ == 2:  # Local
            if tag == 0x00:
                local_usages.append(value)

    return [r for r in reports if r[2] == want_page and r[3] == VENDOR_USAGE]


def find_report_id(desc, page, kind):
    for report_id, k, _, _ in parse_reports(desc, page):
        if k == kind:
            return report_id
    return None


def read_with_timeout(handle, length, timeout=1.0):
    result = {}

    def reader():
        buf = ctypes.create_string_buffer(length)
        read = wintypes.DWORD()
        ok = kernel32.ReadFile(handle, buf, length, ctypes.byref(read), None)
        result["data"] = buf.raw[:read.value] if ok and read.value else None

    thread = threading.Thread(target=reader, daemon=True)
    thread.start()
    thread.join(timeout)

    if thread.is_alive():
        kernel32.CancelIoEx(handle, None)
        return None

    return result.get("data")


def main():
    _setup_prototypes()

    paths = enumerate_hid_paths()
    print(f"枚举到 {len(paths)} 个 HID 接口\n")

    candidates = []

    for path in paths:
        handle = open_device(path)
        if handle is None:
            continue

        vid, pid = get_attributes(handle)
        if vid is None:
            kernel32.CloseHandle(handle)
            continue

        desc = read_descriptor(handle)
        out_id = find_report_id(desc, CONTROL_USAGE_PAGE, "out") if desc else None
        in_id = find_report_id(desc, EVENT_USAGE_PAGE, "in") if desc else None

        if out_id is None:
            out_id = find_id_from_valuecaps(handle, 1, CONTROL_USAGE_PAGE)
        if in_id is None:
            in_id = find_id_from_valuecaps(handle, 0, EVENT_USAGE_PAGE)

        is_asus = vid == 0x0B05
        tag = "  <== SpeedNova 候选" if (out_id is not None or in_id is not None) else ""

        if is_asus:
            caps = get_caps(handle)
            print(f"VID_{vid:04X} PID_{pid:04X}  usagePage=0x{caps.UsagePage:04X} "
                  f"inLen={caps.InputReportByteLength} outLen={caps.OutputReportByteLength} "
                  f"outId={out_id} inId={in_id}{tag}")

            out_caps = get_value_caps(handle, 1)
            in_caps = get_value_caps(handle, 0)
            print(f"    output value caps: {out_caps[:8]}  (共 {len(out_caps)})")
            print(f"    input  value caps: {in_caps[:8]}  (共 {len(in_caps)})")
        else:
            print(f"VID_{vid:04X} PID_{pid:04X}  outId={out_id} inId={in_id}{tag}")

        if is_asus and (out_id is not None or in_id is not None):
            candidates.append((path, handle, out_id, in_id, pid))
        else:
            kernel32.CloseHandle(handle)

    if not candidates:
        print("\n没有找到 0xFF01/0xFFC1 厂商集合的华硕设备。")
        print("请确认接收器已插好，且鼠标处于 2.4G 无线模式。")
        return

    for path, handle, out_id, in_id, pid in candidates:
        print(f"\n{'=' * 60}")
        print(f"测试 PID_{pid:04X}")
        print(f"{'=' * 60}")

        payload = bytearray(20)
        payload[0] = 0x12
        payload[1] = 0x07

        if out_id is not None:
            report = bytes([out_id]) + bytes(payload)
        else:
            report = bytes(payload)

        buf = ctypes.create_string_buffer(report, len(report))
        ok = hid.HidD_SetOutputReport(handle, buf, len(report))
        print(f"发送命令 0x12 0x07 -> {'成功' if ok else '失败'}")

        if not ok:
            kernel32.CloseHandle(handle)
            continue

        data = read_with_timeout(handle, 64, timeout=1.2)

        if data is None:
            print("没有收到应答（鼠标可能已关机，或该接口不负责回传）")
            kernel32.CloseHandle(handle)
            continue

        print(f"应答 {len(data)} 字节: {data.hex(' ').upper()}")

        offset = 1 if (in_id is not None and data and data[0] == in_id) else 0

        if len(data) >= offset + 11 and data[offset] == 0x12 and data[offset + 1] == 0x07:
            percent = data[offset + 4]
            charging = data[offset + 9] == 1
            mv = data[offset + 7] | (data[offset + 8] << 8)
            print(f"\n  >>> 电量: {percent}%")
            print(f"  >>> 充电中: {charging}")
            print(f"  >>> 电压: {mv} mV")
        else:
            print("应答前缀不匹配 0x12 0x07，可能读到了别的事件报文")

        kernel32.CloseHandle(handle)


if __name__ == "__main__":
    main()
