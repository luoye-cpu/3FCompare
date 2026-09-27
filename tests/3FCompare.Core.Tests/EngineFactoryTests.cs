using _3FCompare.Core.Backend;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 引擎工厂的降级决策（docs/41 §4.5 第 8 项）。
///
/// <para>被测的是"没有 FFmpeg 时是否老实退回演示模式、并把原因说清楚"，以及
/// "改了 FFmpeg 目录后探测缓存是否真的失效"。后者对应一个已修复的真实故障：
/// 探测结果此前一旦算出就永久缓存，而全仓库没有重置点 ⇒ 用户配好目录后
/// 仍是演示模式，只能重启（见 <c>EngineFactory.ResetNativeProbe</c> 的注释）。</para>
///
/// <para><b>全局状态隔离</b>（本组是 docs/41 §4.3 点名的高危区）：
/// ① <c>NativeRuntime.FfmpegDirectory</c> 是静态属性；② <c>EngineFactory</c> 的
/// <c>_nativeAvailable</c>/<c>_lastUnavailableReason</c> 是进程级缓存。
/// 两者都在 <see cref="Dispose"/> 里**无条件**还原（<c>SetFfmpegDirectory</c> 会顺带
/// <c>ResetNativeProbe</c>，再显式补一次 Reset 以防未来签名变化）；
/// 同时本类与 <c>NativeRuntimeFfmpegDirTests</c> 同属
/// <see cref="GlobalStateCollection"/>（禁止并行），否则两个类会互相改静态量。</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class EngineFactoryTests : IDisposable
{
    /// <summary>探测在"缺 FFmpeg"分支写出的原因串（写死常量，不调用被测实现取）。
    /// 它与 <c>EngineFactory.ProbeNativeAvailable</c> 里的字面量一一对应；
    /// 若生产代码改了文案，这条会红——这是**有意的**，因为该串会显示在状态栏上。</summary>
    private const string 缺Ffmpeg原因 = "缺少 FFmpeg 核心库（avcodec-*.dll）";

    private readonly string? _savedFfmpegDirectory;

    public EngineFactoryTests()
    {
        _savedFfmpegDirectory = NativeRuntime.FfmpegDirectory;
    }

    public void Dispose()
    {
        NativeRuntime.SetFfmpegDirectory(_savedFfmpegDirectory);   // 内部会 ResetNativeProbe
        EngineFactory.ResetNativeProbe();
        GC.SuppressFinalize(this);
    }

    /// <summary>把探测环境归位到"无 FFmpeg"：清掉手动目录 + 清掉进程级探测缓存。</summary>
    private static void 置为无Ffmpeg环境()
    {
        NativeRuntime.SetFfmpegDirectory(null);
        EngineFactory.ResetNativeProbe();
    }

    /// <summary>前置条件：测试输出目录本身不含 avcodec-*.dll。
    /// 用被测的兄弟函数显式断言，而不是默认它成立——环境一旦不符（例如有人往
    /// 测试输出目录塞了 FFmpeg），本组会**立刻红**并指出前置条件不成立，
    /// 而不是给出一个含义不明的失败。</summary>
    private static void 断言前置条件_测试输出目录无FFmpeg()
        => Assert.False(NativeRuntime.IsFfmpegAvailable(),
            "前置条件不成立：测试输出目录含 avcodec-*.dll，本组用例的前提是'无 FFmpeg'");

    [Fact]
    public void 无FFmpeg时_IsNativeAvailable为false且原因是缺少FFmpeg()
    {
        置为无Ffmpeg环境();
        断言前置条件_测试输出目录无FFmpeg();

        Assert.False(EngineFactory.IsNativeAvailable());
        Assert.Equal(缺Ffmpeg原因, EngineFactory.LastUnavailableReason);
    }

    [Fact]
    public void 无FFmpeg时_Create返回演示引擎()
    {
        置为无Ffmpeg环境();
        断言前置条件_测试输出目录无FFmpeg();

        var engine = EngineFactory.Create();

        Assert.IsType<SimulatedEngine>(engine);
    }

    /// <summary>状态栏/日志里透出的模式名必须**带上原因**，否则用户只看到"演示"，
    /// 无从知道是缺 FFmpeg、缺内核还是别的原因。
    ///
    /// <para><b>独立推算</b>：期望由两个各自独立取得的量拼出——
    /// <c>SimulatedEngine.ModeName</c>（常量）与 <c>LastUnavailableReason</c>（探测结论）。
    /// 断言"模式名同时包含这两段"即可，不复制生产代码的拼接格式。</para></summary>
    [Fact]
    public void 演示模式的CurrentModeName含降级原因()
    {
        置为无Ffmpeg环境();
        断言前置条件_测试输出目录无FFmpeg();

        var reason = EngineFactory.LastUnavailableReason;
        var modeName = EngineFactory.CurrentModeName;

        Assert.NotNull(reason);
        Assert.Contains(SimulatedEngine.ModeName, modeName);
        Assert.Contains(reason!, modeName);
    }

    /// <summary>改了 FFmpeg 目录后，上一次的探测结论必须失效。
    ///
    /// <para><b>独立推算</b>：先记录"无 FFmpeg"下的结论 R1；再把目录指向一个
    /// **用例自己造的、确实含 <c>avcodec-63.dll</c>** 的目录。此时
    /// <c>NativeRuntime.IsFfmpegAvailable()</c> 必然为 true（手动目录优先级最高），
    /// 于是探测的走向必然改变 ⇒ 新结论 R2 不可能再是"缺少 FFmpeg"。
    /// 只断言 <c>R2 != R1</c> 与"R2 不含缺 FFmpeg 字样"，不假设内核是否加载成功
    /// （本机测试输出目录没有 FFF.Native.dll，故 R2 通常落在 DllNotFoundException 分支）。</para></summary>
    [Fact]
    public void 设置含avcodec的目录后_探测缓存被清除并重探()
    {
        置为无Ffmpeg环境();
        断言前置条件_测试输出目录无FFmpeg();

        var before = EngineFactory.LastUnavailableReason;   // 触发并缓存一次探测
        Assert.Equal(缺Ffmpeg原因, before);

        using var dir = new TempDir("enginefactory");
        var ffmpegDir = Path.Combine(dir.Path, "ffmpeg");
        Directory.CreateDirectory(ffmpegDir);
        File.WriteAllText(Path.Combine(ffmpegDir, "avcodec-63.dll"), "");

        NativeRuntime.SetFfmpegDirectory(ffmpegDir);

        Assert.True(NativeRuntime.IsFfmpegAvailable());     // 前置：FFmpeg 已被"找到"
        var after = EngineFactory.LastUnavailableReason;

        Assert.NotEqual(before, after);
        Assert.DoesNotContain("缺少 FFmpeg", after ?? "");
    }
}
