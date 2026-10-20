// 托盘图标预览生成器。
// 绘制逻辑与 src/ElcBattery.App/Tray/IconRenderer.cs 保持一致 ——
// 改参数时先在这里看效果，确认后再同步回 IconRenderer。
//
// 两种样式：
//   ring  纯色空心圆环（用户参考图样式）：颜色编码电量
//   arc   进度弧：轨道 + 从顶部顺时针的电量弧，颜色 + 弧长双重编码

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ElcBattery.Tools;

public static class Program
{
    private const int TraySize = 32;

    // -1 表示离线（显示 –）
    private static readonly int[] Samples = { 8, 25, 50, 75, 100, -1 };

    public static void Main(string[] args)
    {
        string root = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "design", "icons"));

        foreach (var mode in new[] { "arc", "ring", "battery" })
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                string dir = Path.Combine(root, mode, theme);
                Directory.CreateDirectory(dir);

                foreach (int value in Samples)
                {
                    string name = value < 0 ? "offline" : value.ToString();

                    using var bmp = Draw(mode, theme == "dark", value, TraySize);
                    bmp.Save(Path.Combine(dir, $"tray-{name}.png"), ImageFormat.Png);
                }
            }

            // 每种样式取 25% 与 75% 做自检：低电量与高电量的覆盖率应该不同（弧长/填充变化）
            foreach (int value in new[] { 25, 75 })
            {
                using var bmp = Draw(mode, dark: true, value, TraySize);
                Report(mode, value, "dark", bmp);
            }
        }

        // 一个可直接用的 .ico（75% 进度环）
        using (var bmp = Draw("arc", dark: true, 75, TraySize))
        using (var icon = Icon.FromHandle(bmp.GetHicon()))
        using (var fs = File.Create(Path.Combine(root, "tray-75.ico")))
            icon.Save(fs);

        // 程序主图标（品牌蓝 + 白弧），多尺寸 ICO 供 exe 使用
        WriteIco(Path.Combine(root, "app.ico"), new[] { 16, 24, 32, 48, 64 }, DrawAppIcon);

        // 旋转动画的 10 帧，逐帧比对可确认真的在转，而不是静止帧
        string spinDir = Path.Combine(root, "spinner");
        Directory.CreateDirectory(spinDir);
        for (int i = 0; i < 10; i++)
        {
            using var bmp = DrawSpinner(75, offline: false, i / 10f, TraySize);
            bmp.Save(Path.Combine(spinDir, $"frame-{i:00}.png"), ImageFormat.Png);
        }

        using (var preview = BuildPreview())
            preview.Save(Path.Combine(root, "preview.png"), ImageFormat.Png);

        Console.WriteLine("图标已生成: " + root);
    }

    /// <summary>与 IconRenderer.CreateAppIcon 相同的绘制流程。</summary>
    public static Bitmap DrawAppIcon(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var bg = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB)))
            g.FillEllipse(bg, 0, 0, size, size);

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

        return bmp;
    }

    /// <summary>把多张 PNG 打包成一个多尺寸 ICO（Vista+ 支持 PNG 内嵌条目）。</summary>
    private static void WriteIco(string path, int[] sizes, Func<int, Bitmap> draw)
    {
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var bmp = draw(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((short)0);            // reserved
        bw.Write((short)1);            // type: icon
        bw.Write((short)pngs.Count);

        int offset = 6 + 16 * pngs.Count;
        for (int i = 0; i < pngs.Count; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));   // width
            bw.Write((byte)(s >= 256 ? 0 : s));   // height
            bw.Write((byte)0);                    // palette
            bw.Write((byte)0);                    // reserved
            bw.Write((short)1);                   // planes
            bw.Write((short)32);                  // bpp
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (byte[] png in pngs)
            bw.Write(png);
    }

    public static Bitmap Draw(string mode, bool dark, int percent, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        bool offline = percent < 0;
        Color color = offline ? Color.FromArgb(0x8A, 0x92, 0x9E) : PercentToColor(percent);

        // 轨道色：深色托盘要亮一档才看得见，浅色托盘要暗一档
        Color track = dark ? Color.FromArgb(0x4A, 0x50, 0x5A) : Color.FromArgb(0xE2, 0xE5, 0xEA);

        switch (mode)
        {
            case "battery":
                DrawBattery(g, size, percent, offline, color, track);
                break;

            case "arc":
            {
                float penWidth = size * 0.10f;
                var rect = RingRect(size, penWidth);

                using (var trackPen = new Pen(track, penWidth))
                    g.DrawEllipse(trackPen, rect);

                if (!offline)
                {
                    using var pen = new Pen(color, penWidth)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    float sweep = Math.Clamp(percent, 0, 100) * 3.6f;
                    if (sweep > 0.5f) g.DrawArc(pen, rect, -90, sweep);
                }
                break;
            }

            default:
            {
                float penWidth = size * 0.10f;
                using var pen = new Pen(color, penWidth);
                g.DrawEllipse(pen, RingRect(size, penWidth));
                break;
            }
        }

        return bmp;
    }

    private static RectangleF RingRect(int size, float penWidth) =>
        new(penWidth / 2 + 0.6f, penWidth / 2 + 0.6f,
            size - penWidth - 1.2f, size - penWidth - 1.2f);

    /// <summary>与 IconRenderer.CreateSpinner 相同的绘制流程。</summary>
    public static Bitmap DrawSpinner(int percent, bool offline, float phase, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float penWidth = size * 0.10f;
        Color color = offline ? Color.FromArgb(0x8A, 0x92, 0x9E) : PercentToColor(percent);

        DrawArc(g, RingRect(size, penWidth), -90 + phase * 360f, 79f, color, penWidth, 1.0f);
        return bmp;
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

    /// <summary>电池胶囊：外框 + 右侧小头 + 按电量填充内部。</summary>
    private static void DrawBattery(Graphics g, int size, int percent, bool offline,
                                    Color color, Color track)
    {
        float penWidth = Math.Max(size * 0.075f, 1.6f);

        float bx = size * 0.20f;
        float by = size * 0.33f;
        float bw = size * 0.58f;
        float bh = size * 0.34f;

        using var outline = new Pen(offline ? track : color, penWidth)
        {
            LineJoin = LineJoin.Round
        };

        using (var body = RoundedRect(new RectangleF(bx, by, bw, bh), size * 0.08f))
        {
            // 先铺一层底，让内部填充不会透出背景
            using (var shell = new SolidBrush(Color.FromArgb(60, track)))
                g.FillPath(shell, body);

            g.DrawPath(outline, body);
        }

        // 右侧小头
        float hx = bx + bw + penWidth * 0.55f;
        float hy = by + bh * 0.26f;
        using (var head = new SolidBrush(offline ? track : color))
            g.FillRectangle(head, hx, hy, size * 0.05f, bh * 0.48f);

        if (offline) return;

        // 内部填充：左右各留一个笔宽的内边距
        float inset = penWidth * 0.9f;
        float innerW = bw - inset * 2;
        float fillW = innerW * Math.Clamp(percent, 0, 100) / 100f;
        if (fillW <= 0.5f) return;

        using var fillRect = RoundedRect(
            new RectangleF(bx + inset, by + inset, fillW, bh - inset * 2), size * 0.05f);
        using var fill = new SolidBrush(color);
        g.FillPath(fill, fillRect);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>自检：空白透明图是最容易踩的坑，覆盖率与主色能立刻暴露问题。</summary>
    private static void Report(string mode, int percent, string variant, Bitmap bmp)
    {
        int opaque = 0;
        var counts = new Dictionary<Color, int>();

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 128) continue;

                opaque++;
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
            }
        }

        double coverage = 100.0 * opaque / (bmp.Width * bmp.Height);
        Color dominant = counts.Count > 0
            ? counts.OrderByDescending(kv => kv.Value).First().Key
            : Color.Empty;

        string label = percent < 0 ? "离线" : percent + "%";
        Console.WriteLine($"  {mode,-8} {label,-5} {variant}  覆盖 {coverage:0.0}%  " +
                          $"主色 #{dominant.R:X2}{dominant.G:X2}{dominant.B:X2}");
    }

    private static Bitmap BuildPreview()
    {
        const int cell = 92;
        const int icon = 52;
        const int labelCol = 150;
        const int segW = cell * 6;

        int w = labelCol + segW * 2;
        int h = 52 + 118 * 4 + 46;

        var canvas = new Bitmap(w, h, PixelFormat.Format24bppRgb);

        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        using var titleFont = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var modeFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var hintFont = new Font("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var segFont = new Font("Microsoft YaHei UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        using var pctFont = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);

        g.DrawString("托盘电量图标 · 三个方向 · 32×32 透明底", titleFont, Brushes.Black, 14, 14);

        (string key, string name, string hint)[] rows =
        {
            ("arc", "进度环 · 已实装", "暗轨道 + 电量弧：弧长和颜色双重编码"),
            ("ring", "纯色环", "只有颜色，看不出电量多少"),
            ("battery", "电池胶囊", "一眼认出是电量，细节最多"),
            ("app", "程序主图标 · 已实装", "品牌蓝 + 白弧，与电量环同一个符号"),
        };

        for (int row = 0; row < rows.Length; row++)
        {
            int y = 52 + row * 118;
            var (key, name, hint) = rows[row];

            g.DrawString(name, modeFont, Brushes.Black, 14, y + 34);
            using (var hintBrush = new SolidBrush(ColorTranslator.FromHtml("#5F6875")))
                g.DrawString(hint, hintFont, hintBrush, 14, y + 56);

            for (int seg = 0; seg < 2; seg++)
            {
                bool dark = seg == 0;
                int x0 = labelCol + seg * segW;

                using (var bg = new SolidBrush(dark
                           ? ColorTranslator.FromHtml("#16181C")
                           : ColorTranslator.FromHtml("#F7F8FA")))
                    g.FillRectangle(bg, x0, y, segW, 104);

                using (var tag = new SolidBrush(ColorTranslator.FromHtml("#8A929E")))
                    g.DrawString(dark ? "深色托盘" : "浅色托盘", segFont, tag, x0 + 8, y + 6);

                for (int i = 0; i < Samples.Length; i++)
                {
                    int cx = x0 + i * cell + (cell - icon) / 2;
                    int cy = y + (104 - icon) / 2 + 6;

                    using var bmp = key == "app"
                        ? DrawAppIcon(icon)
                        : Draw(key, dark, Samples[i], icon);
                    g.DrawImage(bmp, cx, cy);

                    string label = Samples[i] < 0 ? "离线" : Samples[i] + "%";
                    var size = g.MeasureString(label, pctFont);
                    using var lb = new SolidBrush(dark
                        ? ColorTranslator.FromHtml("#A2AAB8")
                        : ColorTranslator.FromHtml("#5F6875"));
                    g.DrawString(label, pctFont, lb, x0 + i * cell + (cell - size.Width) / 2, y + 84);
                }
            }
        }

        using var note = new SolidBrush(ColorTranslator.FromHtml("#5F6875"));
        g.DrawString("当前程序实装「进度环」+「品牌蓝主图标」。要换成纯色环或电池胶囊，说一声即可。",
            hintFont, note, 14, h - 26);

        return canvas;
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
