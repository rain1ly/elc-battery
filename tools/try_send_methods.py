"""
对比两种发送方式，找出能让 ROG 接收器回 0x12 0x07 的正确姿势。

已确认：
  - 0xFFC1 事件接口的报告首字节是 report ID = 5
  - 周期性上报的是 0x12 0x01（状态事件），不是电量
  - 电量应答应在 index1=0x12, index2=0x07, index5=percent

GearLink 用 WebHID 的 sendReport(reportId, data)，data 只有 20 字节；
Windows 的 HidD_SetOutputReport 强制要求整条报告长度（64），
所以改用 WriteFile 发「reportId + 20 字节」= 21 字节试试。
"""

import ctypes
import threading
import time
from ctypes import wintypes

import probe_rog as m


def send_via_writefile(handle, data):
    buf = ctypes.create_string_buffer(bytes(data), len(data))
    written = wintypes.DWORD()
    ok = m.kernel32.WriteFile(
        handle, ctypes.cast(buf, ctypes.c_void_p), len(data),
        ctypes.byref(written), None
    )
    return bool(ok), written.value


def send_via_setoutput(handle, data):
    buf = ctypes.create_string_buffer(bytes(data), len(data))
    return bool(m.hid.HidD_SetOutputReport(handle, buf, len(data))), len(data)


def listen(handles, seconds):
    """同时监听所有接口，收集报文。"""
    collected = []
    stop = threading.Event()

    def worker(handle, length, page):
        while not stop.is_set():
            box = {}

            def reader():
                b = ctypes.create_string_buffer(max(length, 64))
                n = wintypes.DWORD()
                r = m.kernel32.ReadFile(
                    handle, ctypes.cast(b, ctypes.c_void_p), max(length, 64),
                    ctypes.byref(n), None
                )
                if r and n.value:
                    box["d"] = b.raw[:n.value]

            t = threading.Thread(target=reader, daemon=True)
            t.start()
            t.join(0.35)

            if t.is_alive():
                m.kernel32.CancelIoEx(handle, None)
                continue

            if box.get("d"):
                collected.append((page, box["d"]))

    threads = []
    for h, length, page in handles:
        t = threading.Thread(target=worker, args=(h, length, page), daemon=True)
        t.start()
        threads.append(t)

    time.sleep(seconds)
    stop.set()
    time.sleep(0.4)
    return collected


def main():
    m._setup_prototypes()
    m.kernel32.WriteFile.argtypes = [
        wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p
    ]
    m.kernel32.WriteFile.restype = wintypes.BOOL

    interfaces = {}
    handles_for_listen = []

    for path in m.enumerate_hid_paths():
        h = m.open_device(path)
        if h is None:
            continue

        vid, pid = m.get_attributes(h)
        if vid != 0x0B05 or pid != 0x1AD0:
            m.kernel32.CloseHandle(h)
            continue

        caps = m.get_caps(h)
        interfaces.setdefault(caps.UsagePage, h)

        if caps.InputReportByteLength > 0:
            handles_for_listen.append((h, caps.InputReportByteLength, caps.UsagePage))

    print("接口：", {hex(k): f"in={m.get_caps(v).InputReportByteLength} out={m.get_caps(v).OutputReportByteLength}" for k, v in interfaces.items()})

    payload = bytes([0x12, 0x07, 0x00, 0x00]) + bytes(16)  # 20 字节，与 GearLink 一致

    trials = []

    # 方案 A：WriteFile，21 字节（reportId + 20），发到 0xFF01
    if 0xFF01 in interfaces:
        rid = m.find_id_from_valuecaps(interfaces[0xFF01], 1, 0xFF01) or 3
        trials.append(("WriteFile 0xFF01 rid=%d 21B" % rid, interfaces[0xFF01],
                       bytes([rid]) + payload, send_via_writefile))

    # 方案 B：WriteFile，64 字节（补零到整条报告）
    if 0xFF01 in interfaces:
        rid = m.find_id_from_valuecaps(interfaces[0xFF01], 1, 0xFF01) or 3
        full = bytes([rid]) + payload + bytes(64 - 1 - len(payload))
        trials.append(("WriteFile 0xFF01 rid=%d 64B" % rid, interfaces[0xFF01],
                       full, send_via_writefile))

    # 方案 C：改发到 0xFF00
    if 0xFF00 in interfaces:
        rid = m.find_id_from_valuecaps(interfaces[0xFF00], 1, 0xFF00) or 2
        trials.append(("WriteFile 0xFF00 rid=%d 21B" % rid, interfaces[0xFF00],
                       bytes([rid]) + payload, send_via_writefile))

    # 方案 D：改发到 0xFF02
    if 0xFF02 in interfaces:
        rid = m.find_id_from_valuecaps(interfaces[0xFF02], 1, 0xFF02) or 1
        trials.append(("WriteFile 0xFF02 rid=%d 21B" % rid, interfaces[0xFF02],
                       bytes([rid]) + payload, send_via_writefile))

    for name, handle, data, sender in trials:
        print(f"\n{'=' * 62}\n{name}  长度={len(data)}")
        print(f"  bytes: {data[:24].hex(' ').upper()}")

        sender_result = sender(handle, data)
        print(f"  发送: {sender_result}")

        got = listen(handles_for_listen, 1.6)

        hits = []
        for page, d in got:
            if len(d) >= 6 and d[1] == 0x12 and d[2] == 0x07:
                hits.append((page, d))

        if hits:
            for page, d in hits:
                print(f"  >>> 电量应答 page=0x{page:04X}: {d.hex(' ').upper()}")
                print(f"  >>> 电量 = {d[5]}%")
            return
        else:
            print(f"  无 0x12 0x07 应答（收到 {len(got)} 条其他报文）")
            for page, d in got[:2]:
                print(f"      page=0x{page:04X} {d.hex(' ').upper()}")

    print("\n所有方式都没拿到电量应答。")


if __name__ == "__main__":
    main()
