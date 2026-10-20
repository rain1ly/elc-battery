using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ElcBattery.App.Services;

/// <summary>
/// 极简日志：只记刷新结果，方便出问题时排查。
/// 位置 %APPDATA%\ELC\elc.log，超过 256KB 自动截断保留后半段。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 256 * 1024;

    public static string LogPath => Path.Combine(ConfigService.ConfigDirectory, "elc.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigService.ConfigDirectory);

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
                }

                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    public static void WriteRefresh(IEnumerable<string> deviceLines)
    {
        var lines = deviceLines.ToList();
        Write($"--- 刷新，设备 {lines.Count} 个 ---");
        foreach (var line in lines) Write("  " + line);
    }
}
