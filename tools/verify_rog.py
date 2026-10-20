"""
ROG 电量读取端到端验证（依赖 probe_rog.py 的底层封装）

流程：
    1. 在 0xFF01 接口（OutputReportByteLength=64）发 [reportId=3, 0x12, 0x07, 0x00, 0x00, ...]
    2. 从 0xFFC1 接口（事件通道）读 Interrupt IN
    3. 解析 [4] = 电量
"""

import ctypes
import threading
from ctypes import wintypes

import probe_rog as m


def main():
    m._setup_prototypes()

    paths = m.enumerate_hid_paths()
    print(f"HID 接口: {len(paths)} 个\n")

    control = None
    event = None
    spares = []

    for path in paths:
        h = m.open_device(path)
        if h is None:
            continue

        vid, pid = m.get_attributes(h)
        if vid != 0x0B05 or pid != 0x1AD0:
            m.kernel32.CloseHandle(h)
            continue

        caps = m.get_caps(h)

        if caps.UsagePage == 0xFF01 and caps.OutputReportByteLength == 64 and control is None:
            control = h
            print(f"[control] usagePage=0xFF01 in={caps.InputReportByteLength} out={caps.OutputReportByteLength}")
        elif caps.UsagePage == 0xFFC1 and event is None:
            event = h
            print(f"[event]   usagePage=0xFFC1 in={caps.InputReportByteLength} out={caps.OutputReportByteLength}")
        else:
            spares.append(h)

    if control is None:
        print("没找到 0xFF01 control 接口")
        return

    report_id = m.find_id_from_valuecaps(control, 1, 0xFF01)
    print(f"\nReport ID = {report_id}")

    buf = bytearray(64)
    buf[0] = report_id or 3
    buf[1] = 0x12
    buf[2] = 0x07

    raw = bytes(buf)
    cbuf = ctypes.create_string_buffer(raw, len(raw))
    ok = m.hid.HidD_SetOutputReport(control, cbuf, len(raw))
    print(f"发送命令: {'成功' if ok else '失败'}")

    if not ok:
        return

    targets = []
    if event is not None:
        targets.append(("event(0xFFC1)", event, 64))
    targets.append(("control-input", control, 64))

    for name, handle, length in targets:
        print(f"\n--- 从 {name} 读应答 ---")

        result = {}

        def reader():
            b = ctypes.create_string_buffer(length)
            n = wintypes.DWORD()
            r = m.kernel32.ReadFile(
                handle, ctypes.cast(b, ctypes.c_void_p), length,
                ctypes.byref(n), None
            )
            if r and n.value:
                result["data"] = b.raw[:n.value]

        t = threading.Thread(target=reader, daemon=True)
        t.start()
        t.join(1.5)

        if t.is_alive():
            m.kernel32.CancelIoEx(handle, None)
            print("  超时，无应答")
            continue

        data = result.get("data")
        if not data:
            print("  没读到数据")
            continue

        print(f"  原始: {data.hex(' ').upper()}")

        for offset in (1, 0):
            if len(data) >= offset + 11 and data[offset] == 0x12 and data[offset + 1] == 0x07:
                percent = data[offset + 4]
                mv = data[offset + 7] | (data[offset + 8] << 8)
                print(f"  >>> 匹配成功 (offset={offset})")
                print(f"  >>> 电量: {percent}%")
                print(f"  >>> 充电中: {data[offset + 9] == 1}")
                print(f"  >>> 电压: {mv} mV")
                break
        else:
            print("  前缀不是 0x12 0x07")

    for h in spares:
        m.kernel32.CloseHandle(h)


if __name__ == "__main__":
    main()
