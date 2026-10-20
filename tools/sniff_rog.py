"""
全接口嗅探：同时监听 ROG 接收器的每一个 vendor 接口，
然后发送读电量命令，收集所有返回的数据。

目的：确定应答到底从哪个 usage page 的接口回来
     （GearLink 说 0xFFC1，但实测可能不是）。
"""

import ctypes
import threading
import time
from ctypes import wintypes

import probe_rog as m


def main():
    m._setup_prototypes()

    interfaces = []

    for path in m.enumerate_hid_paths():
        h = m.open_device(path)
        if h is None:
            continue

        vid, pid = m.get_attributes(h)
        if vid != 0x0B05 or pid != 0x1AD0:
            m.kernel32.CloseHandle(h)
            continue

        caps = m.get_caps(h)
        interfaces.append((h, caps))

    print(f"ROG 接口 {len(interfaces)} 个：")
    for h, caps in interfaces:
        print(f"  page=0x{caps.UsagePage:04X} in={caps.InputReportByteLength} "
              f"out={caps.OutputReportByteLength} feat={caps.FeatureReportByteLength}")

    collected = []
    stop = threading.Event()

    def listener(handle, length, page):
        while not stop.is_set():
            result = {}

            def reader():
                b = ctypes.create_string_buffer(max(length, 64))
                n = wintypes.DWORD()
                r = m.kernel32.ReadFile(
                    handle, ctypes.cast(b, ctypes.c_void_p), max(length, 64),
                    ctypes.byref(n), None
                )
                if r and n.value:
                    result["data"] = b.raw[:n.value]

            t = threading.Thread(target=reader, daemon=True)
            t.start()
            t.join(0.4)

            if t.is_alive():
                m.kernel32.CancelIoEx(handle, None)
                continue

            data = result.get("data")
            if data:
                collected.append((page, data))

    threads = []
    for h, caps in interfaces:
        if caps.InputReportByteLength > 0:
            t = threading.Thread(target=listener, args=(h, caps.InputReportByteLength, caps.UsagePage), daemon=True)
            t.start()
            threads.append(t)

    print("\n监听已启动，发送命令…")

    control = None
    report_id = None
    for h, caps in interfaces:
        if caps.UsagePage == 0xFF01 and caps.OutputReportByteLength == 64:
            control = h
            report_id = m.find_id_from_valuecaps(h, 1, 0xFF01)
            break

    if control is None:
        print("没有 0xFF01 输出接口")
        return

    print(f"control=0xFF01 reportId={report_id}")

    buf = bytearray(64)
    buf[0] = report_id or 3
    buf[1] = 0x12
    buf[2] = 0x07

    raw = bytes(buf)
    cbuf = ctypes.create_string_buffer(raw, len(raw))
    print(f"发送: {'成功' if m.hid.HidD_SetOutputReport(control, cbuf, len(raw)) else '失败'}")

    time.sleep(2.5)
    stop.set()
    time.sleep(0.6)

    print(f"\n收到 {len(collected)} 条报文")
    seen = set()
    for page, data in collected:
        key = (page, data.hex())
        if key in seen:
            continue
        seen.add(key)
        print(f"  page=0x{page:04X} len={len(data):2}  {data.hex(' ').upper()}")

    if not collected:
        print("  一条都没有。设备侧完全没有回传 —— 鼠标很可能处于关机/休眠。")


if __name__ == "__main__":
    main()
