"""
扫描迈从鼠标（VID_5253）所有 Feature Report ID，找出哪个带电量。

用法：
    python mchose_sweep.py
"""

import ctypes
import time
from ctypes import wintypes

setupapi = ctypes.WinDLL("setupapi.dll")
hid = ctypes.WinDLL("hid.dll")
k32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
INVALID = wintypes.HANDLE(-1).value


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


def protos():
    hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
    hid.HidD_GetAttributes.argtypes = [wintypes.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
    hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
    hid.HidD_GetPreparsedData.restype = wintypes.BOOLEAN
    hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
    hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
    hid.HidP_GetCaps.restype = wintypes.LONG
    hid.HidD_GetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_GetFeature.restype = wintypes.BOOLEAN
    hid.HidD_SetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_SetFeature.restype = wintypes.BOOLEAN
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
    setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CloseHandle.argtypes = [wintypes.HANDLE]


def find():
    g = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(g))
    dev = setupapi.SetupDiGetClassDevsW(ctypes.byref(g), None, None,
                                        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    did = SP_IFACE()
    did.cbSize = ctypes.sizeof(SP_IFACE)
    idx = 0
    best = None
    while True:
        if not setupapi.SetupDiEnumDeviceInterfaces(dev, None, ctypes.byref(g), idx, ctypes.byref(did)):
            break
        idx += 1
        req = wintypes.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(dev, ctypes.byref(did), None, 0, ctypes.byref(req), None)
        if req.value <= 0:
            continue
        buf = ctypes.create_string_buffer(req.value)
        ctypes.cast(buf, ctypes.POINTER(wintypes.DWORD))[0] = 8
        if not setupapi.SetupDiGetDeviceInterfaceDetailW(dev, ctypes.byref(did), buf, req.value,
                                                         ctypes.byref(req), None):
            continue
        path = ctypes.wstring_at(ctypes.addressof(buf) + 4)
        h = k32.CreateFileW(path, 0xC0000000, 0x03, None, 3, 0, None)
        if h == INVALID:
            continue
        attrs = HIDD_ATTRIBUTES()
        attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
        caps = HIDP_CAPS()
        hid.HidD_GetAttributes(h, ctypes.byref(attrs))
        pp = ctypes.c_void_p()
        if hid.HidD_GetPreparsedData(h, ctypes.byref(pp)) and pp:
            hid.HidP_GetCaps(pp, ctypes.byref(caps))
            hid.HidD_FreePreparsedData(pp)
        if attrs.VendorID == 0x5253 and caps.UsagePage == 0xFF01 and caps.FeatureReportByteLength >= 60:
            best = (h, caps)
        else:
            k32.CloseHandle(h)
    setupapi.SetupDiDestroyDeviceInfoList(dev)
    return best


def main():
    protos()
    found = find()
    if not found:
        print("没找到设备")
        return
    h, caps = found
    length = max(caps.FeatureReportByteLength, 65)
    print(f"FeatureReportByteLength = {caps.FeatureReportByteLength}\n")

    print("=== 扫描 Feature Report ID 0x01..0x30 ===")
    for rid in range(1, 0x31):
        buf = ctypes.create_string_buffer(length)
        buf[0] = rid
        ok = hid.HidD_GetFeature(h, ctypes.cast(buf, ctypes.c_void_p), length)
        if not ok:
            continue
        data = bytes(buf.raw[:length])
        body = data[1:]
        if all(b == 0 for b in body):
            continue
        print(f"  0x{rid:02X}  {data[:20].hex(' ').upper()}")

    print("\n=== 连续读 0x14 五次 ===")
    for i in range(5):
        buf = ctypes.create_string_buffer(length)
        buf[0] = 0x14
        ok = hid.HidD_GetFeature(h, ctypes.cast(buf, ctypes.c_void_p), length)
        data = bytes(buf.raw[:length])
        print(f"  #{i+1} ok={bool(ok)}  {data[:10].hex(' ').upper()}"
              f"   byte1={data[1]} byte2={data[2]}")
        time.sleep(0.4)

    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
