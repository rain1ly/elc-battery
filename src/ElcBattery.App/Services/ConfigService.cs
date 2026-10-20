using System;
using System.IO;
using System.Text.Json;
using ElcBattery.App.Models;

namespace ElcBattery.App.Services;

/// <summary>配置读写（%APPDATA%\ELC\config.json）。</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ELC");

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    public AppConfig Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                Current = new AppConfig();
                return;
            }

            string json = File.ReadAllText(ConfigFilePath);
            Current = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            // 配置损坏时回退到默认值，不让程序起不来
            Current = new AppConfig();
        }

        Normalize(Current);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            string json = JsonSerializer.Serialize(Current, JsonOptions);

            // 先写临时文件再替换，避免写一半断电导致配置损坏
            string tmp = ConfigFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, ConfigFilePath, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // 保存失败不致命，下次内存里的设置仍然生效
        }
    }

    private static void Normalize(AppConfig c)
    {
        c.RefreshSeconds = Math.Clamp(c.RefreshSeconds, 30, 86400);
        c.LowThreshold1 = Math.Clamp(c.LowThreshold1, 1, 99);
        c.LowThreshold2 = Math.Clamp(c.LowThreshold2, 1, 99);

        if (c.LowThreshold2 >= c.LowThreshold1)
            c.LowThreshold2 = Math.Max(1, c.LowThreshold1 - 5);
    }
}
