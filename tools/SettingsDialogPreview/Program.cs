// 诊断 + 截图：显示控件树，并用 DrawToBitmap 渲染对话框供布局分析
// 可选的 DPI 模拟：参数 2 传 144/192 时，在 Show 前触发 AutoScale 模拟缩放
using System.Drawing.Imaging;
using _3FCompare.App;
using _3FCompare.Core.Settings;

namespace SettingsDialogPreview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var settings = new AppSettings
        {
            HardwareDecode = true,
            PreferredAdapterIndex = 0,
            FrameStep = 1,
            SecondsStep = 1.0,
            StartFullscreen = false,
            HideChromeInFullscreen = true,
            ColorMode = ColorModeSetting.MapToHdr,
            DefaultGridCols = 2,
            DefaultGridRows = 1,
            FfmpegDirectory = null,
            Language = 0,
        };

        // 默认输出到统一的测试产物目录 testmedia/tmp/（无参数时避免散落仓库根）
        string outDir = args.Length > 0 ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testmedia", "tmp");
        outDir = Path.GetFullPath(outDir);
        int simDpi = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 0;
        Directory.CreateDirectory(outDir);

        using var dlg = new SettingsDialog(settings);
        if (simDpi > 0 && simDpi != 96)
        {
            // 模拟高 DPI：把基准 AutoScaleDimensions 设为目标 DPI 再执行缩放
            var factor = simDpi / 96f;
            dlg.AutoScaleDimensions = new SizeF(96f / factor, 96f / factor);
        }
        dlg.Show();
        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }

        Console.WriteLine($"Dialog ClientSize={dlg.ClientSize} Min={dlg.MinimumSize} DPI={dlg.DeviceDpi}");

        // 用更大的画布（含滚动内容全高）渲染，便于分析分区边界
        var w = dlg.ClientSize.Width;
        var h = dlg.ClientSize.Height;
        using var bmp = new Bitmap(w, h);
        dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.ClientSize));
        var path = Path.Combine(outDir, simDpi > 0 ? $"settings-sim{simDpi}.png" : "settings-fixed.png");
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {path} ({w}x{h})");

        dlg.Close();
        return 0;
    }
}