using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ElcBattery.Core.Hid;

/// <summary>
/// 一个已打开的 HID 接口。共享打开（FILE_SHARE_READ|FILE_SHARE_WRITE），
/// 只发 Output Report / 读 Interrupt IN，绝不独占设备。
/// </summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public HidDeviceInfo Info { get; }

    private HidDevice(SafeFileHandle handle, HidDeviceInfo info)
    {
        _handle = handle;
        Info = info;
    }

    public static HidDevice? Open(HidDeviceInfo info, bool write)
    {
        uint access = HidNative.GENERIC_READ | (write ? HidNative.GENERIC_WRITE : 0);

        SafeFileHandle handle = HidNative.CreateFile(
            info.DevicePath, access,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid && write)
        {
            handle = HidNative.CreateFile(
                info.DevicePath, HidNative.GENERIC_READ,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        // 加大输入缓冲，减少事件报告被丢弃的概率
        try { HidNative.HidD_SetNumInputBuffers(handle, 64); } catch { /* 忽略 */ }

        return new HidDevice(handle, info);
    }

    /// <summary>
    /// 发送 Output Report。buffer 首字节为 Report ID（reportId 为 0 时省略）。
    /// </summary>
    public bool SendOutputReport(byte reportId, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return false;

        int ridLen = reportId != 0 ? 1 : 0;
        int declared = Info.OutputReportByteLength;
        int len = Math.Max(declared > 0 ? declared : 0, ridLen + payload.Length);
        if (len <= 0) len = ridLen + payload.Length;

        var buf = new byte[len];
        if (reportId != 0) buf[0] = reportId;
        payload.CopyTo(buf.AsSpan(ridLen));

        try
        {
            // 必须用 WriteFile：HidD_SetOutputReport 对这条华硕命令返回成功但设备不响应
            return HidNative.WriteFile(_handle, buf, len, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读一条 Interrupt IN 报告，超时返回 null。</summary>
    public async Task<byte[]?> ReadInputReportAsync(int timeoutMs, CancellationToken ct = default)
    {
        if (_disposed) return null;

        int len = Math.Max((int)Info.InputReportByteLength, 64);
        var handle = _handle;

        var readTask = Task.Run(() =>
        {
            var buf = new byte[len];
            if (!HidNative.ReadFile(handle, buf, len, out int read, IntPtr.Zero) || read <= 0)
                return (byte[]?)null;

            var result = new byte[read];
            Array.Copy(buf, result, read);
            return result;
        }, ct);

        var finished = await Task.WhenAny(readTask, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);

        if (finished != readTask)
        {
            try { HidNative.CancelIoEx(handle, IntPtr.Zero); } catch { /* 忽略 */ }
            // 让被取消的读自然结束，避免线程泄漏
            _ = readTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return null;
        }

        try { return await readTask.ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>读取 Feature Report（某些设备用它上报电量）。</summary>
    public byte[]? GetFeatureReport(byte reportId, int length)
    {
        if (_disposed) return null;

        int len = Math.Max(length, (int)Info.FeatureReportByteLength);
        if (len <= 0) return null;

        var buf = new byte[len];
        if (reportId != 0) buf[0] = reportId;

        return HidNative.HidD_GetFeature(_handle, buf, len) ? buf : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}
