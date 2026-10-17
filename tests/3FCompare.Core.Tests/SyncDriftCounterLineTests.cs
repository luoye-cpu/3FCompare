using _3FCompare.Core.Backend;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Sync;
using _3FCompare.Core.Tests.Infrastructure;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><c>漂移校正核对</c> 行尾那串<b>计数器差</b>的落盘判据（#42 形状 A 的归因读数）。
///
/// <para><b>为什么这一条必须测到"字真的落进日志里"</b>：这个读数的全部价值是把
/// "落地后一拍掉了 ~90 ms" 拆成两种完全不同的病——卡在 <c>Present</c> 里等，还是根本没解出帧。
/// 而如果 <see cref="SyncController"/> 里那次"存上一拍计数器"的循环没被跑到、或者差值串拼出来
/// 却是空的，现场日志就会<b>稳定地少一整列</b>，而单测只断言计数器的话照样全绿
/// ——这是本仓记过的"写好但没被调用的读数＝稳定产出全绿 + 功能坏"的同一种坏法。
/// 所以这里读回真实落盘的文本，逐段断言数值。</para>
///
/// <para><b>为什么整类串行</b>：<c>AppLog</c> 是进程级单例（队列 + 后台写线程），
/// 与任何并行类都会互相踩；<c>GlobalStateCollection</c> 保证本类不与其它集合并行，
/// 每条用例自己再 <c>try/finally</c> 还原。</para>
///
/// <para><b>期望值全部手算</b>，不从实现里引常数：见用例内注释。</para></summary>
[Collection(GlobalStateCollection.Name)]
public class SyncDriftCounterLineTests
{
    private const long Duration = 10 * TimeSpan.TicksPerSecond;
    private const double Fps = 24.0;

    private static void Teardown()
    {
        try { AppLog.Shutdown(); } catch { }
        AppLog.ResetForTests(null);
    }

    /// <summary>等后台写线程把这一行刷到磁盘（最多 5 s）；超时就把当前文本写进失败消息。
    ///
    /// <para><b>为什么不能用 <c>File.ReadAllText</c></b>：日志文件此刻正被 writer 持有，
    /// 必须按它的共享模式（<c>ReadWrite | Delete</c>）开流才读得到；用 ReadAllText 会抛
    /// <c>IOException</c> 被吞掉 ⇒ 测试读到"空文件"，把**读取方式错**误报成**日志没落盘**
    /// （本用例第一版就这么红过一次，靠失败消息里的目录清单才分清是两层里的哪一层）。</para></summary>
    private static string WaitFor(Func<string, bool> ready, string logFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            text = ReadLog(logFile);
            if (ready(text)) return text;
            Thread.Sleep(20);
        }
        return text;
    }

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

    [Fact]
    public void 核对行尾的计数器差_按手算值落进日志()
    {
        using var dir = new TempDir("drift_counters");
        AppLog.ResetForTests(dir.Path);
        var clock = new ManualClock();
        var engine = new FakeEngine { DefaultDuration100ns = Duration, DefaultFrameRate = Fps };
        var sync = new SyncController(clock);
        for (var i = 0; i < 2; i++)
        {
            var session = (FakeSession)engine.CreateSession(
                new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
            session.OpenAsync($"t{i}.mp4").GetAwaiter().GetResult();
            sync.AddSlot(session, $"t{i}.mp4");
        }
        try
        {
            AppLog.Initialize();
            var logFile = AppLog.CurrentLogFile;
            Assert.NotNull(logFile);

            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            var follower = (FakeSession)sync.Slots.ElementAt(1).Session;

            // 上一拍的基准：位置 2.5s（这一拍的快照会读到它），呈现 100/100 次，Present 内累计 100ms。
            follower.Position100ns = 25 * TimeSpan.TicksPerSecond / 10;      // 2.5 s（> 半帧阈值 ⇒ 触发校正）
            follower.PresentedVideoFrames = 100;
            follower.SwapChainPresents = 100;
            follower.PresentWait100ns = 100 * 10_000;                        // 100 ms

            sync.TickDrift();                                                // 发出 Seek，并记下"上一拍"计数器
            // 反向前置：发 Seek 的这一拍**还不该有核对读数**（核对发生在下一拍）。
            // 没有这一句，实现退化成"每次 TickDrift 都加一次 变"也能通过后面的断言。
            Assert.Contains("变=0 未变=0", sync.DriftSkipDiag());

            // 本拍：时钟 +1050ms；位置走到 2.910s；呈现 +24/+24；Present 内累计再多 400ms。
            clock.Advance(TimeSpan.FromMilliseconds(1050));
            follower.Position100ns = 291 * TimeSpan.TicksPerSecond / 100;    // 2.91 s
            follower.PresentedVideoFrames = 124;
            follower.SwapChainPresents = 124;
            follower.PresentWait100ns = 500 * 10_000;                        // 500 ms（差 = 400 ms）

            sync.TickDrift();

            // 手算的期望（全部由上面设定的字面量推出，不引用实现里的常数）：
            //   间隔        = 1050 ms
            //   位置前进    = 2.910 − 2.500 = 0.410 s = 410 ms
            //   呈现帧      = 124 − 100 = +24        Present 调用 = +24
            //   Present阻塞 = 500 − 100 = 400 ms
            //   代数 1 → 2 ⇒（已变）：第一次 Seek 是替身做的真 demuxer seek
            var text = WaitFor(t => t.Contains("漂移校正核对 路1"), logFile!);
            // 失败消息要能直接分辨"日志器没落盘"与"这一行没被生成"两种坏法，否则下一次排查
            // 又得重跑一遍（异步 writer 的测试如果没有这个信息，红了也看不出红在哪一层）。
            var listing = string.Join(";",
                Directory.GetFiles(dir.Path).Select(System.IO.Path.GetFileName));
            Assert.True(text.Contains("漂移校正核对 路1"),
                $"没读到核对行：目录=[{listing}] 期望文件={System.IO.Path.GetFileName(logFile!)} " +
                $"文本长度={text.Length} 尾部=[{text[Math.Min(400, text.Length)..]}]");
            Assert.Contains("漂移校正核对 路1", text);
            Assert.Contains("（已变）", text);
            Assert.Contains("间隔=1050ms", text);
            Assert.Contains("位置前进=410ms", text);
            Assert.Contains("呈现帧=+24", text);
            Assert.Contains("Present调用=+24", text);
            Assert.Contains("Present阻塞=400ms", text);

            // 计数器侧的同一条读数：核对只发生一次，且判成"已变"。
            Assert.Contains("变=1 未变=0", sync.DriftSkipDiag());
        }
        finally
        {
            sync.Clear();
            Teardown();
        }
    }
}
