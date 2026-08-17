using _3FCompare.Core.Backend;

namespace _3FCompare.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    ///  `--autodemo <文件...>`：启动后自动打开指定文件并播放。
    ///  `--selftest <文件>`：真实模式自检（打开→等就绪→双步进断言→退出码）。
    ///  `--ffmpegdir <目录> --selftest <文件>`：指定 FFmpeg DLL 目录后自检。
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        // 从嵌入资源释放 FFF.Native.dll（NativeAOT 单文件仅一个 exe，首次运行自动展开）
        NativeRuntime.ExtractEmbeddedDll(name =>
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        });

        var ffmpegDir = (string?)null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ffmpegdir" && i + 1 < args.Length)
            {
                ffmpegDir = args[++i];
            }
            else
            {
                remaining.Add(args[i]);
            }
        }
        args = remaining.ToArray();

        var autodemoFiles = Array.Empty<string>();
        if (args.Length >= 2 && args[0] == "--autodemo")
        {
            autodemoFiles = args[1..];
        }
        else if (args.Length >= 2 && args[0] == "--selftest")
        {
            var form = new MainForm(ffmpegDir);
            return form.RunSelfTest(args[1]);
        }
        else if (args.Length >= 3 && args[0] == "--screentest")
        {
            var form = new MainForm(ffmpegDir);
            return form.RunScreenshotTest(args[1], args[2]);
        }
        var form0 = new MainForm(ffmpegDir);
        if (autodemoFiles.Length > 0)
        {
            form0.AutoOpenFiles(autodemoFiles);
        }
        Application.Run(form0);
        return 0;
    }
}