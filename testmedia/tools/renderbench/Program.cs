using System.Diagnostics;
using SkiaSharp;

namespace RenderBench;

/// <summary>
/// 小幅渲染基准：模拟 3FCompare 时间轴/缩略图浮层的典型绘制负载，
/// 对比 GDI+ 与 SkiaSharp（Avalonia 同款引擎）的每帧耗时。
/// 输出结果到 testmedia/tmp/renderbench.txt。
/// </summary>
internal static class Program
{
    private const int WarmupFrames = 30;
    private const int BenchFrames = 300;
    private const int W = 1200, H = 160; // 时间轴尺寸量级

    [STAThread]
    private static void Main()
    {
        var lines = new List<string>
        {
            $"渲染基准: {W}x{H} @ {BenchFrames} 帧 (预热 {WarmupFrames})",
            $"OS: {Environment.OSVersion.VersionString}  .NET: {Environment.Version}",
            "",
        };

        // ---- 场景1: 时间轴帧（刻度线 + 播放头 + 半透明区间）----
        lines.Add($"[场景1] 时间轴帧: 刻度线60根 + 渐变背景 + 播放头 + AB区间");
        lines.Add($"  GDI+ : {BenchGdi(TimelineGdi):F3} ms/帧");
        lines.Add($"  Skia : {BenchSkia(TimelineSkia):F3} ms/帧");

        // ---- 场景2: 缩略图合成（位图缩放 + 圆角 + 阴影边框）----
        using var src = MakeTestBitmap(320, 180);
        using var srcSkia = MakeTestBitmapSkia(320, 180);
        lines.Add($"[场景2] 缩略图合成: 位图缩放到220x124 + 圆角裁剪 + 描边");
        lines.Add($"  GDI+ : {BenchGdi(g => ThumbGdi(g, src)):F3} ms/帧");
        lines.Add($"  Skia : {BenchSkia(s => ThumbSkia(s, srcSkia)):F3} ms/帧");

        // ---- 场景3: 文本密集（时间码 x20，模拟多路标签）----
        lines.Add($"[场景3] 文本密集: 时间码文本 x20 (抗锯齿)");
        lines.Add($"  GDI+ : {BenchGdi(TextGdi):F3} ms/帧");
        lines.Add($"  Skia : {BenchSkia(TextSkia):F3} ms/帧");

        File.WriteAllLines(OutputPath(), lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }

    private static string OutputPath()
    {
        // 输出到 testmedia/tmp/（目录规范）
        // 实测：BaseDirectory 上溯 6 级 = testmedia/（bin\<cfg>\<tfm>\renderbench\tools 各占一级）
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "tmp", "renderbench.txt");
    }

    // ================= 基准框架 =================

    private static double BenchGdi(Action<Graphics> draw)
    {
        using var bmp = new Bitmap(W, H);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        for (var i = 0; i < WarmupFrames; i++) draw(g);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < BenchFrames; i++) draw(g);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / BenchFrames;
    }

    private static double BenchSkia(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        for (var i = 0; i < WarmupFrames; i++) { canvas.Clear(); draw(canvas); }
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < BenchFrames; i++) { canvas.Clear(); draw(canvas); }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / BenchFrames;
    }

    private static Bitmap MakeTestBitmap(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(0, 0, w, h), Color.DarkSlateBlue, Color.Black, 45f);
        g.FillRectangle(grad, 0, 0, w, h);
        return bmp;
    }

    private static SKBitmap MakeTestBitmapSkia(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(w, h),
                new[] { new SKColor(72, 61, 139), SKColors.Black },
                null, SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, w, h, paint);
        return bmp;
    }

    // ================= 场景1: 时间轴 =================

    private static readonly Font GdiFont = new("Segoe UI", 9f);

    private static void TimelineGdi(Graphics g)
    {
        using var bgGrad = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(0, 0, W, H), Color.FromArgb(30, 30, 36), Color.FromArgb(18, 18, 20), 90f);
        g.FillRectangle(bgGrad, 0, 0, W, H);

        using var tickPen = new Pen(Color.FromArgb(90, 90, 100));
        for (var i = 0; i <= 60; i++)
        {
            var x = i * W / 60f;
            var len = i % 5 == 0 ? 24f : 12f;
            g.DrawLine(tickPen, x, H - len, x, H);
        }

        // AB 循环区间（半透明）
        using var abBrush = new SolidBrush(Color.FromArgb(70, 255, 200, 64));
        g.FillRectangle(abBrush, W * 0.25f, 0, W * 0.4f, H);

        // 播放头
        using var headPen = new Pen(Color.OrangeRed, 2f);
        var hx = W * 0.62f;
        g.DrawLine(headPen, hx, 0, hx, H);
        g.FillRectangle(Brushes.OrangeRed, hx - 5, 0, 10, 14);
    }

    private static void TimelineSkia(SKCanvas c)
    {
        using var bgPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, H),
                new[] { new SKColor(30, 30, 36), new SKColor(18, 18, 20) },
                null, SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, 0, W, H, bgPaint);

        using var tick = new SKPaint { Color = new SKColor(90, 90, 100), StrokeWidth = 1 };
        for (var i = 0; i <= 60; i++)
        {
            var x = i * W / 60f;
            var len = i % 5 == 0 ? 24f : 12f;
            c.DrawLine(x, H - len, x, H, tick);
        }

        using var ab = new SKPaint { Color = new SKColor(255, 200, 64, 70) };
        c.DrawRect(W * 0.25f, 0, W * 0.4f, H, ab);

        using var head = new SKPaint { Color = SKColors.OrangeRed, StrokeWidth = 2 };
        var hx = W * 0.62f;
        c.DrawLine(hx, 0, hx, H, head);
        c.DrawRect(hx - 5, 0, 10, 14, head);
    }

    // ================= 场景2: 缩略图合成 =================

    private static void ThumbGdi(Graphics g, Bitmap src)
    {
        var dst = new Rectangle(10, 10, 220, 124);
        using var path = RoundedRect(dst, 8);
        g.SetClip(path);
        g.DrawImage(src, dst);
        g.ResetClip();
        using var pen = new Pen(Color.FromArgb(80, 80, 90));
        g.DrawPath(pen, path);
    }

    private static void ThumbSkia(SKCanvas c, SKBitmap src)
    {
        var dst = new SKRect(10, 10, 230, 134);
        using var clip = new SKPath();
        clip.AddRoundRect(new SKRoundRect(dst, 8));
        c.Save(); c.ClipPath(clip);
        c.DrawBitmap(src, dst, new SKPaint { IsAntialias = true });
        c.Restore();
        using var pen = new SKPaint { Color = new SKColor(80, 80, 90), IsStroke = true };
        c.DrawPath(clip, pen);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ================= 场景3: 文本 =================

    private static void TextGdi(Graphics g)
    {
        using var brush = new SolidBrush(Color.White);
        for (var i = 0; i < 20; i++)
            g.DrawString($"00:00:{i:D2}:12", GdiFont, brush, 10 + i * 58f, 60);
    }

    private static void TextSkia(SKCanvas c)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 12f);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < 20; i++)
            c.DrawText($"00:00:{i:D2}:12", 10 + i * 58f, 72, font, paint);
    }
}
