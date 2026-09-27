using _3FCompare.Diagnostics;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="HookDetector"/> 的<b>候选名单与筛选语义</b>（docs/41 §4.5 第 14 项）。
///
/// <para><b>为什么要抽 <c>FilterPresent</c> / <c>NativeProbe</c> 两个纯成员</b>：真实的
/// <c>GetModuleHandleW</c> 只认"本进程已加载的模块"，而候选的 RTSS/覆盖层 DLL 在开发机
/// 上并不加载 ⇒ 用真实 API 无法覆盖"命中"分支；反过来，若改用自造替身来模拟加载，
/// 测的又是替身自己的比较语义（"子串/大小写"两条恰恰就是比较语义）。所以拆成：
/// ① 纯筛选（<c>FilterPresent</c>，期望值由字面量表独立给出）；
/// ② 真实探针（<c>NativeProbe</c>，用<b>一定已加载</b>的 kernel32 验证大小写不敏感与不做子串匹配）。</para>
///
/// <para><b>为什么不需要串行化集合</b>：本文件只调纯函数，不写任何静态量，
/// 也不触发 <c>DetectedHooks</c> 的进程级缓存。</para>
/// </summary>
public class HookDetectorTests
{
    /// <summary>三类已知注入模块必须都在名单里，且"在场"时逐个被认出。
    ///
    /// <para><b>期望值</b>：名单是照 <c>docs/33 §八</c> 的定案结论<b>手写</b>的四个字面量
    /// （RTSS 的两个模块名 + NVIDIA 覆盖层），顺序即名单顺序；期望结果 = 这 3 个字面量本身
    /// （<c>FilterPresent</c> 返回候选名单里的原始拼写，不做二次规范化）。</para></summary>
    [Fact]
    public void 三类已知钩子名都会被认出()
    {
        var expected = new[] { "RTSSHooks64.dll", "RTSSHooks.dll", "nvspcap64.dll" };
        Assert.Equal(expected, HookDetector.KnownCandidates);

        // 替身只做"精确、大小写不敏感"的在场判定，模拟 GetModuleHandleW 的语义；
        // 这里的期望值是上面那 3 个字面量，与替身实现无关。
        var loaded = new[] { "rtsshooks64.dll", "RTSSHOOKS.DLL", "NVSPCAP64.DLL" };
        var present = HookDetector.FilterPresent(
            HookDetector.KnownCandidates,
            name => loaded.Contains(name, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(expected, present);
    }

    /// <summary>一个都不在场 ⇒ 空列表 ⇒ <c>IsOverlayHookPresent</c> 为 false
    /// （该属性的定义就是 <c>DetectedHooks.Count &gt; 0</c>，空列表即 false）。
    ///
    /// <para><b>期望值</b>：空集合是契约本身（"扫过、没有"必须能与"根本没跑过检测"区分开 ——
    /// 这正是 <c>ProbeAndLog</c> 会显式记一条 <c>HookScanClear</c> 的原因）。</para></summary>
    [Fact]
    public void 无命中时返回空列表()
    {
        var candidates = new[] { "RTSSHooks64.dll", "RTSSHooks.dll", "nvspcap64.dll" };

        var present = HookDetector.FilterPresent(candidates, _ => false);

        Assert.Empty(present);
        Assert.False(present.Count > 0, "空列表 ⇒ IsOverlayHookPresent 语义为 false");
    }

    /// <summary>筛选依赖的两条<b>真实</b>语义：大小写不敏感、且不做子串匹配。
    /// 用一定已加载的 <c>kernel32.dll</c> 打真实探针，因此这两条不是替身的行为而是
    /// <c>GetModuleHandleW</c> 的行为（生产代码正是靠它）。
    ///
    /// <para><b>期望值（独立推算）</b>：<c>kernel32.dll</c> 是 Windows 进程的基础模块、必然在
    /// 模块表中 ⇒ 两种拼写都命中，故期望结果恰为这两个候选；<c>kernel3</c> 与
    /// <c>kernel32.dll.bak</c> 都不是已加载模块的<b>完整</b>名字 ⇒ 必须缺席（若实现退化成
    /// "包含/前缀匹配"，这两个会假命中，本断言即判红）。</para></summary>
    [Fact]
    public void 真实探针大小写不敏感且不做子串匹配()
    {
        var candidates = new[] { "KERNEL32.DLL", "kernel32.dll", "kernel3", "kernel32.dll.bak",
            "3fc_no_such_module_3fcompare.dll" };

        var present = HookDetector.FilterPresent(candidates, HookDetector.NativeProbe);

        Assert.Equal(new[] { "KERNEL32.DLL", "kernel32.dll" }, present);
    }
}
