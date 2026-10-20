"""
盲扫 Report ID：依次用 1..12 作为 Report ID 发同一条读电量命令，
看接收器对哪个 ID 有应答。用于确认 Report ID 是否解析正确。

如果全部无应答，基本可以判定鼠标处于关机/休眠状态（接收器在线也没用）。
"""

import ctypes
import threading
from ctypes import wintypes

import probe_rog as m


def read_once(handle, length, timeout):
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
    t.join(timeout)

    if t.is_alive():
        m.kernel32.CancelIoEx(handle, None)
        return None

    return result.get("data")


def main():
    m._setup_prototypes()

    control = None
    event = None

    for path in m.enumerate_hid_paths():
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
        elif caps.UsagePage == 0xFFC1 and event is None:
            event = h
        else:
            m.kernel32.CloseHandle(h)

    if control is None:
        print("没找到 control 接口")
        return

    print("开始盲扫 Report ID 1..12\n")
    print(f"{'RID':>4}  {'发送':<4}  {'应答'}")
    print("-" * 70)

    hits = []

    for rid in range(1, 13):
        buf = bytearray(64)
        buf[0] = rid
        buf[1] = 0x12
        buf[2] = 0x07

        raw = bytes(buf)
        cbuf = ctypes.create_string_buffer(raw, len(raw))
        ok = m.hid.HidD_SetOutputReport(control, cbuf, len(raw))

        if not ok:
            print(f"{rid:>4}  失败")
            continue

        data = read_once(event if event is not None else control, 64, 0.6)

        if data:
            text = data.hex(" ").upper()
            print(f"{rid:>4}  OK    {text}")
            hits.append((rid, data))
        else:
            print(f"{rid:>4}  OK    -")

    print()
    if not hits:
        print("全部无应答。")
        print("最可能的原因：鼠标已关机或处于休眠，接收器在线也不会回电量。")
        print("请把鼠标开机（动一下或按一下），然后重新运行本脚本。")
        return

    print("有应答的 Report ID：")
    for rid, data in hits:
        for offset in (1, 0):
            if len(data) >= offset + 11 and data[offset] == 0x12 and data[offset + 1] == 0x07:
                print(f"  RID={rid} offset={offset} -> 电量 {data[offset + 4]}%")
                break


if __name__ == "__main__":
    main()
