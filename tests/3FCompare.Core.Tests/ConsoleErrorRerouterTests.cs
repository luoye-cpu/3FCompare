using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="ConsoleErrorRerouter"/> 的<b>双写与切行契约</b>（docs/41 §4.5 第 15 项）。
///
/// <para><b>为什么走真实链路（<c>Console.Error</c> → AppLog → 磁盘）而不是给 DualWriter 开测试钩子</b>：
/// 本类的全部价值就在于"55 处既有的 <c>Console.Error.WriteLine</c> 无需改动即可落盘"，
/// 所以必须验证 <c>Console.SetError</c> 装上去之后<b>真的</b>能穿过 <c>TextWriter</c> 的各条
/// 重载（<c>Write(string)</c> / <c>Write(char[])</c> / <c>Flush</c>）到达日志文件。
/// 只测一个被注入的 sink 会漏掉"重载没被覆写"这一类缺陷（代码注释里的 B11）。</para>
///
/// <para><b>期望值来源（独立推算）</b>：全部用本文件自己写的探针串（<c>…-3fc</c>）与
/// 手算的切行结果；单行上限用<b>字面量</b> <c>64 * 1024</c>（对应实现里公开声明的
/// "单行上限"契约），而不是从被测类型读常量。CR 那条用"若 CR 未被剥掉，文件里会出现
/// 连续两个 CR"这一独立推演来构造判据。</para>
///
/// <para><b>与 <see cref="AppLogTests"/> 同集合</b>：两者都改 <c>Console.Error</c> 与
/// <c>AppLog</c> 这两个进程级单例，必须串行。</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class ConsoleErrorRerouterTests
{
    /// <summary><c>'\n'</c> 是唯一的切行点：一次 <c>Write</c> 里的多行必须拆成多条日志
    /// （整块当一条会让日志文件里挤成一行，无法按行检索 —— 代码注释里的 B11）。
    ///
    /// <para><b>期望值（独立推演）</b>：拆成两条日志后，两条之间是<b>行结束符</b>（CRLF）；
    /// 若不拆而把整块当一条，则两条之间是内容里的那个裸 <c>'\n'</c>（此时整块由
    /// <c>WriteLine</c> 追加行结束符，故只有末尾有 CRLF）。所以判据是文件里含
    /// <c>"alpha-3fc" + NewLine + "beta-3fc"</c> —— 它同时钉住"拆开了"与"顺序不变"。
    /// ⚠ 只断言"文件里能分别搜到两个串"是<b>不够</b>的：内嵌换行同样会落成两行（实测过）。</para></summary>
    [Fact]
    public void 换行符切分成多条日志()
    {
        using var dir = new TempDir("rerouter_lines");
        var logFile = StartLog(dir);
        try
        {
            ConsoleErrorRerouter.Install();
            Console.Error.Write("alpha-3fc\nbeta-3fc\n");
            ConsoleErrorRerouter.Uninstall();

            WaitUntil(() => ReadLog(logFile).Contains("beta-3fc"), "两行都应落盘");

            var text = ReadLog(logFile);
            Assert.Contains("alpha-3fc" + Environment.NewLine + "beta-3fc", text);
            Assert.Equal(1, CountOccurrences(text, "alpha-3fc"));
            Assert.Equal(1, CountOccurrences(text, "beta-3fc"));
        }
        finally
        {
            StopLog();
        }
    }

    /// <summary><c>"\r\n"</c> 的行尾 CR 必须被丢掉，否则日志每行末尾多一个不可见字符
    /// （按行检索、diff、导入表格时都会出问题）。
    ///
    /// <para><b>期望值（独立推演）</b>：日志文件的行结束符本来就是 CRLF ⇒
    /// 若 CR 未被剥掉，两行之间会出现<b>连续两个 CR</b>（内容里的 CR + 行结束的 CR）。
    /// 所以判据是"两行之间恰好只有一个 CR"，即文件里含
    /// <c>"gamma-3fc" + NewLine + "delta-3fc"</c>。</para></summary>
    [Fact]
    public void 回车换行不留回车()
    {
        using var dir = new TempDir("rerouter_crlf");
        var logFile = StartLog(dir);
        try
        {
            ConsoleErrorRerouter.Install();
            Console.Error.Write("gamma-3fc\r\ndelta-3fc\r\n");
            ConsoleErrorRerouter.Uninstall();

            WaitUntil(() => ReadLog(logFile).Contains("delta-3fc"), "两行都应落盘");

            var text = ReadLog(logFile);
            Assert.Contains("gamma-3fc" + Environment.NewLine + "delta-3fc", text);
            Assert.DoesNotContain("\r\r\n", text);
            Assert.Contains("gamma-3fc", ReadLogLines(logFile));
        }
        finally
        {
            StopLog();
        }
    }

    /// <summary>调用方一直不换行时（进度条式的连续 <c>Write</c>）缓冲不能无限增长：
    /// 一旦达到单行上限就<b>强制成一条日志</b>。
    ///
    /// <para><b>期望值</b>：上限是 64×1024 = 65536 字符（实现声明的"单行上限"契约）⇒
    /// 写满 65536 个 <c>z</c> 之后，日志里必须出现一条<b>恰好</b> 65536 字符的 z 串；
    /// 之后再写的 <c>tail-3fc</c> 必须是<b>另一条</b>（说明强出真的把缓冲清空了，
    /// 而不是把尾巴并进同一条）。</para></summary>
    [Fact]
    public void 达到单行上限时强制成条()
    {
        const int ExpectedLimit = 64 * 1024;

        using var dir = new TempDir("rerouter_limit");
        var logFile = StartLog(dir);
        try
        {
            ConsoleErrorRerouter.Install();
            Console.Error.Write(new string('z', ExpectedLimit));
            Console.Error.Write("tail-3fc");
            ConsoleErrorRerouter.Uninstall();

            WaitUntil(() => ReadLog(logFile).Contains("tail-3fc"), "强出的整行与尾巴都应落盘");

            var lines = ReadLogLines(logFile);
            Assert.Contains(new string('z', ExpectedLimit), lines);
            Assert.Contains("tail-3fc", lines);
        }
        finally
        {
            StopLog();
        }
    }

    /// <summary>不足一行的残留文本在 <c>Flush</c> 时必须被送进日志 —— 否则程序崩溃/退出前
    /// 最后半行（往往正是最有价值的那句）会丢。
    ///
    /// <para><b>期望值</b>：<c>Write("partial-3fc")</c> 不含换行 ⇒ 在 Flush 之前
    /// 日志里<b>不得</b>出现它（这条是确定性的：唯一入队路径就是 <c>FlushPending</c>）；
    /// <c>Flush()</c> 之后必须出现，且内容恰为探针串本身（无行尾残留）。</para></summary>
    [Fact]
    public void 冲刷时送出未成行的残留()
    {
        using var dir = new TempDir("rerouter_flush");
        var logFile = StartLog(dir);
        try
        {
            ConsoleErrorRerouter.Install();
            Console.Error.Write("partial-3fc");

            Assert.DoesNotContain("partial-3fc", ReadLog(logFile));

            Console.Error.Flush();

            WaitUntil(() => ReadLog(logFile).Contains("partial-3fc"), "Flush 应把残留送出");
            Assert.Contains("partial-3fc", ReadLogLines(logFile));
        }
        finally
        {
            StopLog();
        }
    }

    // ──────── 夹具 ────────

    /// <summary>起一套干净的日志环境（临时目录 + 已初始化的 AppLog），返回当前日志文件路径。</summary>
    private static string StartLog(TempDir dir)
    {
        AppLog.ResetForTests(dir.Path);
        AppLog.Initialize();
        var logFile = AppLog.CurrentLogFile;
        Assert.NotNull(logFile);
        return logFile!;
    }

    /// <summary>无条件还原：先卸载双写器（恢复原始 stderr），再冲队列并复位单例。</summary>
    private static void StopLog()
    {
        try { ConsoleErrorRerouter.Uninstall(); } catch { }
        try { AppLog.Shutdown(); } catch { }
        AppLog.ResetForTests(null);
    }

    /// <summary>读日志文件。必须显式 <c>FileShare.ReadWrite</c>：<c>AppLog</c> 的 writer 以
    /// "写 + FileShare.Read" 持有文件，而 <c>File.ReadAllText</c> 默认以 <c>FileShare.Read</c>
    /// 打开 —— 新句柄的共享模式不允许已存在的写句柄 ⇒ 运行期间读日志会抛 <c>IOException</c>
    /// （"being used by another process"），表现为"等日志落盘"永远超时。</summary>
    private static string ReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>按行切分日志（去掉行尾 CR）。断言"某一行<b>恰好</b>是探针串"时用它 ——
    /// 若 CR 未被剥掉，该行会变成 <c>"gamma-3fc\r"</c>，<c>Contains</c> 立刻判红。</summary>
    private static string[] ReadLogLines(string path)
        => ReadLog(path).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        Assert.Fail($"等待超时：{what}");
    }
}
