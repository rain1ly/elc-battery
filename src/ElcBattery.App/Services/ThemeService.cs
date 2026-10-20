using System;
using System.Windows;
using ElcBattery.App.Interop;
using ElcBattery.App.Models;
using Microsoft.Win32;

namespace ElcBattery.App.Services;

/// <summary>
/// 主题切换。做法是替换 Application.Resources.MergedDictionaries[0]
/// （即 Themes/Light.xaml 或 Themes/Dark.xaml 这一份 token 字典）。
///
/// 前提：所有控件样式都用 DynamicResource 引用 token。若改成 StaticResource，
/// 替换字典后界面不会跟着变 —— 这是 WPF 主题切换最常踩的坑。
/// </summary>
public static class ThemeService
{
    private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

    private static bool _watching;

    public static bool CurrentIsDark { get; private set; }

    public static bool Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => IsSystemDark()
    };

    public static bool IsSystemDark() => AcrylicHelper.IsSystemDark();

    public static void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        if (app == null) return;

        bool dark = Resolve(mode);
        CurrentIsDark = dark;

        var dict = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = app.Resources.MergedDictionaries;

        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Add(dict);
    }

    /// <summary>跟随系统时，系统主题一变就重新应用。</summary>
    public static void WatchSystem(Action<string> onChanged)
    {
        if (_watching) return;
        _watching = true;

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;

            try
            {
                onChanged(IsSystemDark() ? "dark" : "light");
            }
            catch
            {
                // 系统事件里抛异常会带崩进程，一律吞掉
            }
        };
    }
}
