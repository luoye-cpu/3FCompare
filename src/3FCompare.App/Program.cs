namespace _3FCompare.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    ///  `--autodemo <文件...>`：启动后自动打开指定文件并播放。
    ///  `--selftest <文件>`：真实模式自检（打开→等就绪→双步进断言→退出码）。
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        var autodemoFiles = Array.Empty<string>();
        if (args.Length >= 2 && args[0] == "--autodemo")
        {
            autodemoFiles = args[1..];
        }
        else if (args.Length >= 2 && args[0] == "--selftest")
        {
            var form = new MainForm();
            return form.RunSelfTest(args[1]);
        }        else if (args.Length >= 3 && args[0] == "--screentest")
        {
            var form = new MainForm();
            return form.RunScreenshotTest(args[1], args[2]);
        }
        var form0 = new MainForm();
        if (autodemoFiles.Length > 0)
        {
            form0.AutoOpenFiles(autodemoFiles);
        }
        Application.Run(form0);
        return 0;
    }
}