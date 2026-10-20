using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ElcBattery.App.Services;

namespace ElcBattery.App.Tray;

/// <summary>
/// 动态生成托盘图标：透明底上的空心圆环（参考用户提供的样式图）。
///
/// 设计规范（design/DESIGN.md 第 3.5 节）：
/// - 画布 32×32，完全透明 —— 没有底色、没有数字
/// - 环的粗细约占画布 10%，在 16px 的实际托盘尺寸下依然清晰
/// - **暗轨道 + 电量弧**：从顶部顺时针，弧长与颜色双重编码电量
///   （纯色环做不到这点：25% 与 75% 覆盖率完全相同，只有颜色差别）
/// - 环色走电量色阶：0% 红 → 50% 黄 → 100% 绿；离线为灰环，不画弧
/// - 轨道色随主题（深色托盘要亮一档），所以换主题必须重画图标
/// - 具体百分比靠 tooltip 和主窗口，图标负责「一眼看出大概」
///
/// 注意：每次换图标都必须 DestroyIcon，否则长时间运行会累积 GDI 句柄，
/// 最终托盘图标变黑甚至程序卡死 —— 这是托盘类程序最常见的坑。
/// </summary>
public static class IconRenderer
{
    private const int CanvasSize = 32;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(int percent, bool offline)
    {
        int size = CanvasSize;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 环外缘留 1.6px（半个笔宽）边距，视觉直径约 26px
            float penWidth = size * 0.10f;
            var rect = new RectangleF(
                penWidth / 2 + 0.6f, penWidth / 2 + 0.6f,
                size - penWidth - 1.2f, size - penWidth - 1.2f);

            Color color = offline ? Color.FromArgb(0x8A, 0x92, 0x9E) : PercentToColor(percent);

            // 轨道色必须跟着主题走：深色托盘上要亮一档才看得见
            bool dark = ThemeService.CurrentIsDark;
            Color track = dark
                ? Color.FromArgb(0x4A, 0x50, 0x5A)
                : Color.FromArgb(0xE2, 0xE5, 0xEA);

            using (var trackPen = new Pen(track, penWidth))
                g.DrawEllipse(trackPen, rect);

            // 从顶部开始顺时针画电量弧：弧长与颜色双重编码电量，
            // 纯色环做不到这点（25% 与 75% 只有颜色差别，覆盖率完全相同）
            if (!offline)
            {
                using var pen = new Pen(color, penWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                float sweep = Math.Clamp(percent, 0, 100) * 3.6f;
                if (sweep > 0.5f)
                    g.DrawArc(pen, rect, -90, sweep);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// 刷新动画的某一帧：短弧绕圈旋转。phase 为 0..1 的循环进度。
    ///
    /// 固定 22% 周长而不是真实电量弧长 —— 电量 94% 时弧几乎闭合根本看不出在转，
    /// 低电量时又只有一小截在转，表现不一致。弧的颜色仍保留电量语义（红=低、绿=高）。
    ///
    /// 曾经另做过脉冲呼吸和拖尾扫过两种，实测在托盘里跟旋转几乎分不出来
    /// （32px 经托盘缩放 + 系统约 60ms 的更新节流，细微差异全被吃掉），已移除。
    /// </summary>
    public static Icon CreateSpinner(int percent, bool offline, float phase)
    {
        const int size = CanvasSize;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float penWidth = size * 0.10f;
            Color color = offline ? Color.FromArgb(0x8A, 0x92, 0x9E) : PercentToColor(percent);

            DrawArc(g, RingRect(size, penWidth), -90 + phase * 360f, 79f, color, penWidth, 1.0f);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawArc(Graphics g, RectangleF rect, float start, float sweep,
                                Color color, float penWidth, float alpha)
    {
        using var pen = new Pen(Color.FromArgb((int)(255 * alpha), color), penWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawArc(pen, rect, start, sweep);
    }

    private static RectangleF RingRect(int size, float penWidth) =>
        new(penWidth / 2 + 0.6f, penWidth / 2 + 0.6f,
            size - penWidth - 1.2f, size - penWidth - 1.2f);

    /// <summary>
    /// 程序主图标：品牌蓝圆底 + 白色电量弧。
    /// 弧是整个产品的核心视觉（托盘电量图标就是进度环），主图标沿用同一个符号形成品牌记忆，
    /// 而不是画一个 16px 下根本看不清的迷你电池。底色用 UI 的 accent 蓝，与主窗口 logo 同色。
    /// </summary>
    public static Icon CreateAppIcon()
    {
        const int size = CanvasSize;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var bg = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB)))
                g.FillEllipse(bg, 0, 0, size, size);

            // 270° 白弧：顶部起点，缺口在左上 —— 与托盘电量环同一个符号
            float penWidth = size * 0.105f;
            float radius = size * 0.30f;
            var rect = new RectangleF(
                size / 2f - radius, size / 2f - radius, radius * 2, radius * 2);

            using (var pen = new Pen(Color.White, penWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
            {
                g.DrawArc(pen, rect, -90, 270);
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>电量 → 颜色：0% 纯红 (H=0°)，100% 纯绿 (H=120°)。</summary>
    public static Color PercentToColor(int percent)
    {
        double hue = Math.Clamp(percent, 0, 100) * 1.2;
        return HsvToRgb(hue, 0.72, 0.86);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }

}
