using System.Reflection;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="AppLog"/> 的落盘契约（docs/41 §4.5 第 13 项，前四条）+ 复位路径的
/// worker 世代号契约（第五条，防止孤儿线程与新 worker 抢队列）。
///
/// <para><b>为什么要用 <see cref="AppLog.ResetForTests"/></b>：<c>AppLog</c> 是进程级单例，
/// 且 <c>Shutdown()</c> 之后 <c>_worker</c> 仍非 null ⇒ <c>Initialize()</c> 会永久变成 no-op。
/// 没有复位入口时，一个测试进程只能初始化一次，这四条用例既无法各自从干净状态开始、
/// 也无法重跑。复位同时把日志目录改写到 <see cref="TempDir"/>，避免污染测试输出目录。</para>
///
/// <para><b>为什么整类串行化</b>：<c>AppLog</c> 的 writer/队列/溢出标志、以及后台写线程
/// 都是<b>进程级</b>状态，与任何并行测试类都会互相踩。<c>GlobalStateCollection</c>
/// （<c>DisableParallelization = true</c>）保证本类不与其它集合并行；每条用例自身再用
/// <c>try/finally</c> 无条件还原 —— 两者缺一不可（集合只挡并发，挡不住"失败后留下脏状态"）。</para>
///
/// <para><b>期望值来源（独立推算）</b>：① 溢出提示条数用<b>字面量 1</b>（契约："只发一次"）；
/// ② 清理判据用<b>自己设定的 mtime</b>（now−48h / now−1h）与保留时长 24h 手比；
/// ③ 分片只断言"序号 1 的分片文件存在且装了新内容、旧片不再增长"，不引用实现的命名函数；
/// ④ "Shutdown 后不写"用自己写的两个探针字符串。</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class AppLogTests
{
    /// <summary>队列溢出时，提示行<b>只允许出现一次</b> —— 否则"提示自己"也会把队列撑爆，
    /// 形成自我放大的死循环（代码注释里的 B5）。
    ///
    /// <para><b>怎么做到确定性</b>：溢出的前提是"生产快于消费"，直接灌 2 万行会与后台
    /// 写线程赛跑（谁快谁慢取决于调度）。这里先<b>占住 writer 锁</b>，让 worker 在取走
    /// 至多一行后阻塞，生产侧于是必然堆到上限之上；随后释放锁让它正常排空。</para>
    ///
    /// <para><b>期望值</b>：本用例共生产 2 万行，而上限是 1 万行 ⇒ 若"只发一次"的闸门失效，
    /// 溢出提示会出现约 1 万次；断言恰好 <b>1</b> 次，就是这条闸门的直接判据。</para></summary>
    [Fact]
    public void 队列溢出提示只发一次()
    {
        using var dir = new TempDir("applog_overflow");
        AppLog.ResetForTests(dir.Path);
        try
        {
            AppLog.Initialize();
            var logFile = AppLog.CurrentLogFile;
            Assert.NotNull(logFile);

            lock (WriterLockOf())
            {
                for (var i = 0; i < 2 * AppLog.MaxQueuedLines; i++)
                    AppLog.Info("OverflowProbe", "line");
            }

            WaitUntil(() =>
            {
                try { return File.Exists(logFile) && ReadLog(logFile!).Contains("日志队列溢出"); }
                catch (IOException) { return false; }
            }, "溢出提示应最终落盘");

            var text = ReadLog(logFile!);
            Assert.Equal(1, CountOccurrences(text, "日志队列溢出"));
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>超过保留时长（24h）的历史日志必须在初始化时清掉；判据是<b>文件 mtime</b>，
    /// 不是文件名里的日期。
    ///
    /// <para><b>期望值</b>：保留时长 24h（<c>RetentionHours</c> 的公开语义）⇒
    /// mtime = now−48h 的 <c>app-*.log</c> 必须消失；mtime = now−1h 的必须留下；
    /// 非 <c>app-*.log</c> 的文件（哪怕很旧）必须原样不动。
    /// 第二个文件刻意起名 <c>app-2000-01-01.log</c>（名字很旧、mtime 很新）——
    /// 若实现退化成"按文件名日期判断"，它会误删，本断言即判红。</para></summary>
    [Fact]
    public void 初始化时清理超期日志且不误删()
    {
        using var dir = new TempDir("applog_purge");
        AppLog.ResetForTests(dir.Path);
        try
        {
            var logsDir = AppLog.LogsDirectory;
            Directory.CreateDirectory(logsDir);

            var stale = Path.Combine(logsDir, "app-2020-01-01.log");
            File.WriteAllText(stale, "stale");
            File.SetLastWriteTime(stale, DateTime.Now.AddHours(-48));

            var fresh = Path.Combine(logsDir, "app-2000-01-01.log");
            File.WriteAllText(fresh, "fresh");
            File.SetLastWriteTime(fresh, DateTime.Now.AddHours(-1));

            var unrelated = Path.Combine(logsDir, "other.txt");
            File.WriteAllText(unrelated, "keep");
            File.SetLastWriteTime(unrelated, DateTime.Now.AddHours(-48));

            AppLog.Initialize();

            Assert.False(File.Exists(stale), "超过 24h 的 app-*.log 必须被清掉");
            Assert.True(File.Exists(fresh), "未超期的 app-*.log 必须保留（判据是 mtime 而非文件名）");
            Assert.True(File.Exists(unrelated), "只清 app-*.log，其它文件不得误删");
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>当前分片达到大小上限后必须<b>换片续写</b>，而不是让当天的文件无限增长
    /// （代码注释里的 B6：24h 连播 + 内核逐帧日志能把当天文件涨到 GB 级）。
    ///
    /// <para><b>期望值</b>：分片上限是 32MB（<c>MaxLogFileBytes</c> 的公开语义）⇒
    /// 一次写入 33MB（&gt; 上限）后，必须出现"序号 1"的分片文件，且
    /// ① 新内容落在分片里；② 上一片不再增长（保持只有会话头这种 KB 级内容）。</para>
    ///
    /// <para>分片文件名用目录枚举 <c>app-*_1.log</c> 取，不引用实现的命名函数，
    /// 也避免在"恰好跨零点"时因日期串不同而 flake。</para></summary>
    [Fact]
    public void 超过大小上限时换片续写()
    {
        using var dir = new TempDir("applog_rollover");
        AppLog.ResetForTests(dir.Path);
        try
        {
            AppLog.Initialize();
            var firstShard = AppLog.CurrentLogFile;
            Assert.NotNull(firstShard);

            // 33MB 的 ASCII 行：UTF-8 字节数 = 字符数 > 32MB 上限。
            AppLog.Raw(new string('a', 33 * 1024 * 1024));

            string? rolloverFile = null;
            WaitUntil(() =>
            {
                var files = Directory.GetFiles(AppLog.LogsDirectory, "app-*_1.log");
                if (files.Length != 1) return false;
                rolloverFile = files[0];
                return new FileInfo(rolloverFile).Length > 1024;
            }, "超上限后应出现序号 1 的分片并已落盘");

            Assert.NotNull(rolloverFile);
            var head = ReadLogHead(rolloverFile!, 512);
            Assert.Contains("日志分片续写", head);

            Assert.True(new FileInfo(firstShard!).Length < 1024 * 1024,
                "换片后新内容不得继续写进上一片");
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary><c>Shutdown()</c> 之后不得再落盘，也<b>不得再入队</b>：writer 已置 null，
    /// 任何新日志必须被直接丢弃（而不是"复活"日志、或让队列只进不出 —— 后者是 B5：
    /// 内核一秒几百行，几分钟就把播放器 OOM 掉）。
    ///
    /// <para><b>期望值</b>：① Shutdown 之前写的探针串必须在文件里（Shutdown 会等 worker 结束并
    /// flush）；② Shutdown 之后写的探针串必须<b>不</b>在文件里；③ Shutdown 之后连写 200 条，
    /// 私有队列长度必须仍为 <b>0</b>（"文件里没有"是弱判据 —— 后台线程已死时，
    /// 即使队列照收也写不出文件；只有"队列也拒收"才是那条 null 守卫的直接判据）。</para></summary>
    [Fact]
    public void 停机后不再写入也不入队()
    {
        using var dir = new TempDir("applog_shutdown");
        AppLog.ResetForTests(dir.Path);
        try
        {
            AppLog.Initialize();
            var logFile = AppLog.CurrentLogFile;
            Assert.NotNull(logFile);

            AppLog.Info("ShutdownProbe", "before-shutdown-3fc");
            AppLog.Shutdown();

            AppLog.Info("ShutdownProbe", "after-shutdown-3fc");
            for (var i = 0; i < 200; i++) AppLog.Raw($"after-raw-{i}-3fc");

            var text = ReadLog(logFile!);
            Assert.Contains("before-shutdown-3fc", text);
            Assert.DoesNotContain("after-shutdown-3fc", text);
            Assert.Equal(0, QueuedLineCount());
        }
        finally
        {
            Teardown();
        }
    }

    /// <summary>复位后<b>旧世代的 worker 必须自行退出</b>，而不是变成孤儿继续与新 worker
    /// 共用同一个静态队列 / writer —— 后者是两个消费者抢同一条队列，表现为偶发的重复日志行。
    ///
    /// <para><b>为什么这里断言的是"世代号机制"而不是端到端</b>：真实的孤儿只出现在
    /// "旧 worker 卡住 &gt; 5 秒"这条路径上（磁盘满 / 杀软独占 / 网络盘断线），单测里无法
    /// 稳定制造；即便能制造，也是 5 秒起步且依赖调度的 flaky 用例。而世代号机制本身是
    /// <b>可确定性观测</b>的：一个"出生在旧世代、如今才苏醒"的循环线程，应当在第一轮
    /// 自检时返回。因此本用例直接起一个带<b>已过期世代号</b>的同款 <c>WorkerLoop</c> 线程
    /// 来代表那个孤儿，断言它能在 3 秒内自行结束。</para>
    ///
    /// <para><b>期望值（独立推算）</b>：① <c>ResetForTests</c> 后世代号必然与之前不同 ⇒
    /// 旧世代线程的 <c>Join(3000)</c> 必须成功（第一轮循环即 return，不依赖调度运气）；
    /// ② 复位后新写的探针串必须落在<b>新目录</b>的日志里，且不出现在旧目录的日志里。</para>
    /// </summary>
    [Fact]
    public void 复位后旧世代worker自行退出且不污染新writer()
    {
        using var oldDir = new TempDir("applog_gen_old");
        using var newDir = new TempDir("applog_gen_new");

        AppLog.ResetForTests(oldDir.Path);
        try
        {
            AppLog.Initialize();
            var oldLog = AppLog.CurrentLogFile;
            Assert.NotNull(oldLog);
            AppLog.Info("GenProbe", "old-gen-3fc");
            WaitUntil(() => ReadLog(oldLog!).Contains("old-gen-3fc"), "旧世代基线应落盘");

            var staleGeneration = CurrentGeneration();
            var workerLoop = typeof(AppLog).GetMethod("WorkerLoop",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(workerLoop);
            var parameters = workerLoop!.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(int), parameters[0].ParameterType);

            // 切世代：新目录 + 新 writer + 新 worker
            AppLog.ResetForTests(newDir.Path);
            AppLog.Initialize();
            var newLog = AppLog.CurrentLogFile;
            Assert.NotNull(newLog);
            Assert.NotEqual(oldLog, newLog);

            // 模拟"卡住后苏醒的旧世代 worker"：它拿的世代号已经过期
            var orphan = new Thread(() => workerLoop.Invoke(null, new object[] { staleGeneration }))
            {
                IsBackground = true,
                Name = "AppLog.OrphanProbe",
            };
            orphan.Start();

            Assert.True(orphan.Join(3000),
                "旧世代的 worker 必须在世代切换后自行退出，否则会与新 worker 抢同一个队列 / writer");

            AppLog.Info("GenProbe", "new-gen-3fc");
            WaitUntil(() => ReadLog(newLog!).Contains("new-gen-3fc"), "新世代探针应落盘");

            Assert.DoesNotContain("new-gen-3fc", ReadLog(oldLog!));
        }
        finally
        {
            Teardown();
        }
    }

    // ──────── 夹具 ────────

    /// <summary>无条件还原：先 Shutdown（冲队列、关 writer、停后台线程），
    /// 再把单例复位成"未初始化 + 无目录覆盖"。任何一条用例失败都要走到这里。</summary>
    private static void Teardown()
    {
        try { AppLog.Shutdown(); } catch { }
        AppLog.ResetForTests(null);
    }

    /// <summary>反射取私有的 writer 锁对象：用它把后台写线程挡在队列外，
    /// 从而让"生产快于消费"成为<b>确定性</b>事实而不是调度运气。</summary>
    private static object WriterLockOf()
    {
        var field = typeof(AppLog).GetField("WriterLock",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(null)!;
    }

    /// <summary>反射读私有队列长度：这是"停机后是否还在收日志"的直接观测点。</summary>
    private static int QueuedLineCount()
    {
        var field = typeof(AppLog).GetField("Queue",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var queue = (System.Collections.ICollection)field!.GetValue(null)!;
        return queue.Count;
    }

    /// <summary>反射读私有的 worker 世代号：用它给"旧世代的孤儿线程"钉一个已过期的身份。</summary>
    private static int CurrentGeneration()
    {
        var field = typeof(AppLog).GetField("_generation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field!.GetValue(null)!;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>读日志文件。
    ///
    /// <para><b>为什么必须显式 <c>FileShare.ReadWrite</c></b>：<c>AppLog</c> 的 writer 以
    /// "写 + FileShare.Read" 持有文件，而 <c>StreamReader</c>/<c>File.ReadAllText</c> 默认以
    /// <c>FileShare.Read</c> 打开 —— 新句柄的共享模式不允许"已存在的写句柄"，
    /// 于是运行期间读日志会抛 <c>IOException</c>（"being used by another process"）。
    /// 这既让"等日志落盘"的轮询永远为假，也说明该文件在程序运行期间确实不可被普通方式读取。</para></summary>
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

    /// <summary>只读文件开头若干字符（分片可能已有数十 MB，不整读）。</summary>
    private static string ReadLogHead(string path, int charCount)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new char[charCount];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
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
