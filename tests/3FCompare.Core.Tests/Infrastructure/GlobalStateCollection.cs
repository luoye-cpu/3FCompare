namespace _3FCompare.Core.Tests.Infrastructure;

/// <summary>
/// 「进程级全局状态」测试集合（docs/41 §4.5 第 6~10 项配套）。
///
/// <para><b>为什么必须有它</b>：<c>NativeRuntime.FfmpegDirectory</c>、
/// <c>EngineFactory</c> 的探测缓存、<c>FFMPEG_DIR</c> 环境变量都是**进程级**状态，
/// 而 xunit 默认按「测试类 = 集合」并行。两个类同时改同一份静态量时，
/// 会得到"单跑绿、全跑红"的随机结果——docs/41 §4.3 正是拿
/// <c>NativeRuntimeFfmpegDirTests</c> 改静态量当作反面样例点名的。</para>
///
/// <para><b>做法</b>：把这些类放进同一个 <c>DisableParallelization = true</c> 的集合。
/// 效果是①集合内用例彼此串行；②该集合不与其它集合并行 ⇒ 不会再有人在我们
/// 读写静态量/环境变量的中间插一脚。代价是这几个类失去并行度，它们总共不到 20 个用例，可忽略。</para>
///
/// <para><b>与"每条用例自己 try/finally 还原"的关系</b>：两者都要有，缺一不可。
/// 集合只解决"并发互踩"，不解决"用例失败后静态量留在脏值上影响**后续**用例"
/// ——那必须靠 <c>try/finally</c>（或 <c>IDisposable</c> 夹具）。</para>
/// </summary>
[CollectionDefinition(GlobalStateCollection.Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    /// <summary>集合名。用 ASCII 是刻意的：它会出现在测试运行器的集合标识里，
    /// 中文名在部分终端/日志管线下会被转义成 <c>?</c>，排障时反而认不出来。</summary>
    public const string Name = "GlobalProcessState";
}
