using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ElcBattery.App.Interop;

/// <summary>
/// 窗口磨砂玻璃效果。
/// Win11 22H2+ 用 DWM 的 SYSTEMBACKDROP_TYPE（Acrylic），
/// 老系统回退到 SetWindowCompositionAttribute 的 ACCENT_ENABLE_ACRYLICBLURBEHIND。
/// 两者都失败时不强行透明，保持普通背景，避免"窗口看不见"。
/// </summary>
public static class AcrylicHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>系统是否为深色主题。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 只设置非客户区外观（DWM 圆角 + 深色标记），不启用磨砂背景。
    ///
    /// 为什么不用磨砂：backdrop 的色调由系统决定、并会盖住窗口自身的背景色，
    /// 导致「主题色 → 实际显示色」不可控，卡片与底色的对比度也就无法保证。
    /// 电量数值靠颜色承载语义，对比度必须可控，所以背景一律用主题实色。
    /// </summary>
    public static void ApplyWindowChrome(Window window, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // DWM 属性不可用不影响功能，圆角退化为直角
        }
    }

    /// <summary>应用磨砂效果。返回是否成功。</summary>
    public static bool Apply(Window window, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            bool win11 = Environment.OSVersion.Version.Build >= 22621;

            if (win11)
            {
                int backdrop = DWMSBT_TRANSIENTWINDOW;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                if (hr == 0) return true;
            }

            return ApplyLegacyAcrylic(hwnd, dark);
        }
        catch
        {
            return false;
        }
    }

    private static bool ApplyLegacyAcrylic(IntPtr hwnd, bool dark)
    {
        try
        {
            // GradientColor 是 ABGR
            int gradient = dark ? unchecked((int)0x99201A18) : unchecked((int)0x99F2F2F2);

            var policy = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(policy, ptr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = ptr,
                    SizeOfData = size
                };

                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }
}
