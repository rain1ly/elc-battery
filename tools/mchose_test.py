"""
迈从鼠标电量协议实测（只读查询，等价于官方网页驱动的行为）。

协议来源：开源插件 dsh-mchose-battery（针对 VID_3837 家族）
  发送  FeatureReport reportId=0x11，64 字节全 0xFF，其中
        byte[0] = 0x0B ^ 0xFF = 0xF4
        byte[1] = 0xAA ^ 0xFF = 0x55
  接收  InputReport reportId=0x13，byte[0] == 0xE2，电量 = byte[4]

本脚本用于验证这套协议在你的设备（VID_5253 A7 Pro）上是否同样成立。

用法：
    python mchose_test.py
"""

import ctypes
import time
from ctypes import wintypes
from collections import defaultdict

setupapi = ctypes.WinDLL("setupapi.dll")
hid = ctypes.WinDLL("hid.dll")
k32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
INVALID = wintypes.HANDLE(-1).value
CFG_PAGE = 0xFF01


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
    hid.HidD_GetAttributes.restype = wintypes.BOOLEAN
    hid.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
    hid.HidD_GetPreparsedData.restype = wintypes.BOOLEAN
    hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
    hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
    hid.HidP_GetCaps.restype = wintypes.LONG
    hid.HidD_SetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_SetFeature.restype = wintypes.BOOLEAN
    hid.HidD_GetFeature.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.ULONG]
    hid.HidD_GetFeature.restype = wintypes.BOOLEAN
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
    k32.ReadFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                             ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]


def hexs(b):
    return b.hex(" ").upper()


def main():
    protos()
    g = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(g))
    dev = setupapi.SetupDiGetClassDevsW(ctypes.byref(g), None, None,
                                        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)

    did = SP_IFACE()
    did.cbSize = ctypes.sizeof(SP_IFACE)
    targets = []
    idx = 0

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
            h = k32.CreateFileW(path, 0x80000000, 0x03, None, 3, 0, None)
        if h == INVALID:
            continue

        attrs = HIDD_ATTRIBUTES()
        attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
        caps = HIDP_CAPS()
        ok = hid.HidD_GetAttributes(h, ctypes.byref(attrs))
        pp = ctypes.c_void_p()
        if hid.HidD_GetPreparsedData(h, ctypes.byref(pp)) and pp:
            hid.HidP_GetCaps(pp, ctypes.byref(caps))
            hid.HidD_FreePreparsedData(pp)

        if ok and attrs.VendorID == 0x5253 and caps.UsagePage == CFG_PAGE:
            targets.append((path, h, caps))
            print(f"配置接口: {path[-70:]}")
            print(f"  in={caps.InputReportByteLength} out={caps.OutputReportByteLength} "
                  f"feat={caps.FeatureReportByteLength}")
        else:
            k32.CloseHandle(h)

    setupapi.SetupDiDestroyDeviceInfoList(dev)

    if not targets:
        print("没找到 VID_5253 的 0xFF01 配置接口")
        return

    path, h, caps = targets[0]
    length = max(caps.FeatureReportByteLength, 65)

    def send(cmd_bytes, label):
        buf = ctypes.create_string_buffer(length)
        buf[0] = 0x11
        for i, b in enumerate(cmd_bytes):
            buf[1 + i] = b
        ok = hid.HidD_SetFeature(h, ctypes.cast(buf, ctypes.c_void_p), length)
        print(f"\n[发送 {label}] SetFeature(0x11) -> {bool(ok)}")
        print("   " + hexs(bytes(buf.raw[:min(length, 24)])))

    # 方案 1：开源插件里的命令（0x0B/0xAA，整体按 0xFF 取反）
    payload1 = bytearray([0xFF] * 64)
    payload1[0] = 0x0B ^ 0xFF
    payload1[1] = 0xAA ^ 0xFF
    send(bytes(payload1), "插件方案 F4 55 FF..")

    time.sleep(0.3)

    # 读应答：先试 GetFeature 0x12/0x13/0x14，再试 Input
    for rid in (0x12, 0x13, 0x14):
        rb = ctypes.create_string_buffer(length)
        rb[0] = rid
        ok = hid.HidD_GetFeature(h, ctypes.cast(rb, ctypes.c_void_p), length)
        data = bytes(rb.raw[:length])
        print(f"\n[GetFeature 0x{rid:02X}] ok={bool(ok)}")
        print("   " + hexs(data[:24]))
        if data[0] == 0xE2 or (len(data) > 1 and data[1] == 0xE2):
            off = 0 if data[0] == 0xE2 else 1
            print(f"   >>> 命中 E2 前缀，电量 = {data[off + 4]}%")

    # 读一条输入报文
    print("\n[读 InputReport]")
    for _ in range(6):
        ib = ctypes.create_string_buffer(max(caps.InputReportByteLength, 65))
        n = wintypes.DWORD()
        if k32.ReadFile(h, ctypes.cast(ib, ctypes.c_void_p), len(ib), ctypes.byref(n), None):
            d = bytes(ib.raw[:n.value])
            print(f"   rid=0x{d[0]:02X}  {hexs(d[:24])}")
            if len(d) > 1 and (d[0] == 0x13 or d[1] == 0xE2):
                print("   >>> 可能是电量应答")
        else:
            break
        time.sleep(0.05)

    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
