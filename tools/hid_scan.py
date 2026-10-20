"""
通用 HID 设备扫描：列出每个设备的接口、usage page、报告长度和字段能力。

用途：
  - 判断某设备是否实现了标准 HID 电池（usagePage 0x0006 / usage 0x0020）
  - 看它有没有厂商自定义集合（usagePage 0xFF00~0xFFFF），即是否需要逆向私有协议

用法：
    python hid_scan.py            # 全部设备
    python hid_scan.py 3837       # 只看某个 VID（十六进制）
"""

import ctypes
import sys
from ctypes import wintypes
from collections import defaultdict

setupapi = ctypes.WinDLL("setupapi.dll")
hid = ctypes.WinDLL("hid.dll")
k32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
GENERIC_RW = 0xC0000000
FILE_SHARE_RW = 0x03
OPEN_EXISTING = 3
HIDP_STATUS_SUCCESS = 0x00110000

BATTERY_PAGE = 0x0006
BATTERY_USAGE = 0x0020


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wintypes.DWORD), ("Data2", wintypes.WORD),
                ("Data3", wintypes.WORD), ("Data4", ctypes.c_ubyte * 8)]


class SP_IFACE(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wintypes.DWORD), ("Reserved", ctypes.c_void_p)]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wintypes.ULONG), ("VendorID", wintypes.USHORT),
                ("ProductID", wintypes.USHORT), ("VersionNumber", wintypes.USHORT)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [("Usage", wintypes.USHORT), ("UsagePage", wintypes.USHORT),
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
                ("NumberFeatureDataIndices", wintypes.USHORT)]


class VALUE_CAPS(ctypes.Structure):
    _pack_ = 1
    _fields_ = [("UsagePage", wintypes.USHORT), ("ReportID", ctypes.c_ubyte),
                ("IsAlias", ctypes.c_ubyte), ("BitField", wintypes.USHORT),
                ("LinkCollection", wintypes.USHORT), ("LinkUsagePage", wintypes.USHORT),
                ("LinkUsage", wintypes.USHORT), ("IsRange", ctypes.c_ubyte),
                ("IsStringRange", ctypes.c_ubyte), ("IsDesignatorRange", ctypes.c_ubyte),
                ("IsAbsolute", ctypes.c_ubyte), ("HasNull", ctypes.c_ubyte),
                ("Reserved", ctypes.c_ubyte), ("BitSize", wintypes.USHORT),
                ("ReportCount", wintypes.USHORT), ("R2", wintypes.USHORT * 5),
                ("UnitsExp", wintypes.ULONG), ("Units", wintypes.ULONG),
                ("LogicalMin", wintypes.LONG), ("LogicalMax", wintypes.LONG),
                ("PhysicalMin", wintypes.LONG), ("PhysicalMax", wintypes.LONG),
                ("Union", ctypes.c_ubyte * 16)]


def setup_protos():
    hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
    hid.HidD_GetAttributes.argtypes = [wintypes.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
    hid.HidD_GetAttributes.restype = wintypes.BOOLEAN
    hid.HidD_GetProductString.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_GetProductString.restype = wintypes.BOOLEAN
    hid.HidD_GetManufacturerString.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_GetManufacturerString.restype = wintypes.BOOLEAN
    hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
    hid.HidD_GetPreparsedData.restype = wintypes.BOOLEAN
    hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
    hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
    hid.HidP_GetCaps.restype = wintypes.LONG
    hid.HidP_GetValueCaps.argtypes = [wintypes.INT, ctypes.POINTER(VALUE_CAPS),
                                      ctypes.POINTER(wintypes.USHORT), ctypes.c_void_p]
    hid.HidP_GetValueCaps.restype = wintypes.LONG

    setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), ctypes.c_void_p,
                                              ctypes.c_void_p, wintypes.DWORD]
    setupapi.SetupDiGetClassDevsW.restype = wintypes.HANDLE
    setupapi.SetupDiEnumDeviceInterfaces.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                                     ctypes.POINTER(GUID), wintypes.DWORD,
                                                     ctypes.POINTER(SP_IFACE)]
    setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [wintypes.HANDLE, ctypes.POINTER(SP_IFACE),
                                                          ctypes.c_void_p, wintypes.DWORD,
                                                          ctypes.POINTER(wintypes.DWORD),
                                                          ctypes.c_void_p]
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    k32.CloseHandle.restype = wintypes.BOOL
    setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]
    setupapi.SetupDiDestroyDeviceInfoList.restype = wintypes.BOOL


def read_str(handle, which):
    buf = ctypes.create_string_buffer(512)
    fn = hid.HidD_GetProductString if which == 2 else hid.HidD_GetManufacturerString
    if not fn(handle, ctypes.cast(buf, ctypes.c_void_p), 512):
        return ""
    return buf.raw.decode("utf-16-le", "ignore").split("\x00")[0]


def get_value_caps(pp, report_type, count):
    if count <= 0:
        return []
    arr = (VALUE_CAPS * count)()
    length = wintypes.USHORT(count)
    if hid.HidP_GetValueCaps(report_type, arr, ctypes.byref(length), pp) != HIDP_STATUS_SUCCESS:
        return []
    out = []
    for i in range(length.value):
        c = arr[i]
        usage = int.from_bytes(bytes(c.Union[0:2]), "little")
        out.append({
            "page": c.UsagePage, "usage": usage, "reportId": c.ReportID,
            "bitSize": c.BitSize, "count": c.ReportCount,
            "logMin": c.LogicalMin, "logMax": c.LogicalMax,
        })
    return out


def main():
    setup_protos()
    only_vid = int(sys.argv[1], 16) if len(sys.argv) > 1 else None

    g = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(g))
    dev = setupapi.SetupDiGetClassDevsW(ctypes.byref(g), None, None,
                                        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if dev in (None, wintypes.HANDLE(-1).value):
        print("枚举失败")
        return

    did = SP_IFACE()
    did.cbSize = ctypes.sizeof(SP_IFACE)
    idx = 0
    groups = defaultdict(list)
    INVALID = wintypes.HANDLE(-1).value

    while True:
        if not setupapi.SetupDiEnumDeviceInterfaces(dev, None, ctypes.byref(g), idx,
                                                    ctypes.byref(did)):
            break
        idx += 1

        req = wintypes.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(dev, ctypes.byref(did), None, 0,
                                                  ctypes.byref(req), None)
        if req.value <= 0:
            continue
        buf = ctypes.create_string_buffer(req.value)
        ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[0] = 8
        if not setupapi.SetupDiGetDeviceInterfaceDetailW(dev, ctypes.byref(did), buf, req.value,
                                                         ctypes.byref(req), None):
            continue
        path = ctypes.wstring_at(ctypes.addressof(buf) + 4)

        h = k32.CreateFileW(path, GENERIC_RW, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None)
        if h == INVALID:
            h = k32.CreateFileW(path, 0x80000000, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None)
        if h == INVALID:
            continue

        try:
            attrs = HIDD_ATTRIBUTES()
            attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
            if not hid.HidD_GetAttributes(h, ctypes.byref(attrs)):
                continue
            vid, pid = attrs.VendorID, attrs.ProductID
            if only_vid is not None and vid != only_vid:
                continue

            caps = HIDP_CAPS()
            pp = ctypes.c_void_p()
            values = {}
            if hid.HidD_GetPreparsedData(h, ctypes.byref(pp)) and pp:
                try:
                    hid.HidP_GetCaps(pp, ctypes.byref(caps))
                    values = {
                        0: get_value_caps(pp, 0, caps.NumberInputValueCaps),
                        1: get_value_caps(pp, 1, caps.NumberOutputValueCaps),
                        2: get_value_caps(pp, 2, caps.NumberFeatureValueCaps),
                    }
                finally:
                    hid.HidD_FreePreparsedData(pp)

            groups[(vid, pid)].append({
                "product": read_str(h, 2), "mfr": read_str(h, 1),
                "usagePage": caps.UsagePage, "usage": caps.Usage,
                "inLen": caps.InputReportByteLength,
                "outLen": caps.OutputReportByteLength,
                "featLen": caps.FeatureReportByteLength,
                "values": values,
            })
        finally:
            k32.CloseHandle(h)

    setupapi.SetupDiDestroyDeviceInfoList(dev)

    for (vid, pid), ifaces in sorted(groups.items()):
        name = next((i["product"] for i in ifaces if i["product"]), "")
        mfr = next((i["mfr"] for i in ifaces if i["mfr"]), "")
        print(f"\n=== VID_{vid:04X} PID_{pid:04X}  {name}  [{mfr}]  接口数={len(ifaces)}")

        battery = False
        vendor_pages = set()

        for i in ifaces:
            print(f"    usage=0x{i['usagePage']:04X}/0x{i['usage']:04X}"
                  f"  in={i['inLen']:<3} out={i['outLen']:<3} feat={i['featLen']:<3}")

            for rt, label in ((0, "IN"), (1, "OUT"), (2, "FEAT")):
                for v in i["values"].get(rt, []):
                    print(f"        {label} page=0x{v['page']:04X} usage=0x{v['usage']:04X}"
                          f" rid=0x{v['reportId']:02X} bit={v['bitSize']}"
                          f" log={v['logMin']}..{v['logMax']}")
                    if v["page"] == BATTERY_PAGE and v["usage"] == BATTERY_USAGE:
                        battery = True

            if 0xFF00 <= i["usagePage"] <= 0xFFFF:
                vendor_pages.add(i["usagePage"])

        verdict = []
        if battery:
            verdict.append("✅ 标准 HID 电池（程序已支持，零配置）")
        if vendor_pages:
            verdict.append("⚠ 有厂商集合 " + ", ".join(f"0x{p:04X}" for p in sorted(vendor_pages))
                           + " → 需要私有协议")
        if not verdict:
            verdict.append("❌ 无电池字段、无厂商集合 → 读不到电量")
        for v in verdict:
            print("    " + v)


if __name__ == "__main__":
    main()
