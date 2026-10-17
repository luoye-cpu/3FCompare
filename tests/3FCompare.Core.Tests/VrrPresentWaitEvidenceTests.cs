using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// VRR 证据链的托管侧映射契约（内核 <c>presentWait100ns</c> → <see cref="EngineSnapshot"/>）。
///
/// <para><b>为什么只测映射层</b>：唯一的真实接缝是快照构造器
/// <see cref="Fff3FpEngine.Fff3FpSession.MapSnapshot"/>——测试工程里的 FakeEngine/SimulatedEngine
/// 根本不走 interop（无 P/Invoke、无 FFF.Native.dll），而 <c>ReadSnapshot</c> 必须先有活的原生句柄
/// 才能走到映射那几行 ⇒ 把翻译逻辑抽成纯函数是唯一能被单测够到的接缝，本测试据此钉住它。</para>
///
/// <para><b>背景</b>：<c>presentWait100ns</c> 由内核在 <c>PlayerVideoRenderer::PresentCurrentFrame</c>
/// （VideoRenderer.cpp:5342）围绕 <c>chain-&gt;Present()</c> 实测累加，
/// 经 <c>PlayerSession::GetSnapshot</c>（PlayerSession.cpp:1733）回填到
/// <c>FFF3FPSnapshot.presentWait100ns</c>（FFF.Player.Api.h:174）。
/// interop 侧的 <see cref="Fff3FpSnapshot"/> 早已声明该字段（Fff3FpNative.cs:119），
/// 但托管 DTO 一直没透传——于是 VRR 判据只剩 <c>SetPresentConfig</c> 的返回值
/// （它只说明交换链<b>允许</b>撕裂，不说明 Present 真的没等）。</para>
/// </summary>
public class VrrPresentWaitEvidenceTests
{
    /// <summary>原生快照的每一个被映射字段都必须原样出现在 <see cref="EngineSnapshot"/> 上，
    /// 其中 <c>PresentWait100ns</c> 是本用例新增的透传点。
    /// 各字段取<b>互不相同</b>的值：漏一行映射 → 该字段读到 0；
    /// 两行写反（例如把 SwapChainPresents 赋给 PresentWait100ns）→ 两条断言同时失配。</summary>
    [Fact]
    public void 原生快照的每次呈现等待时长被透传到托管快照()
    {
        var native = new Fff3FpSnapshot
        {
            Size = (uint)Marshal.SizeOf<Fff3FpSnapshot>(),
            Version = 8,
            State = (uint)PlayerState.Paused,
            Decoder = 2,
            ActualColorMode = 1,
            Position100ns = 111,
            Duration100ns = 222,
            FrameIndex = 333,
            FramePts = 444,
            FrameTimeBaseNumerator = 1,
            FrameTimeBaseDenominator = 15360,
            PresentedVideoFrames = 555,
            SwapChainPresents = 1200,
            // 1200 次 Present 累计阻塞 2.004s ⇒ 单次 ≈ 16.7ms（60Hz VSync 锁定的特征值）；
            // 撕裂/VRR 真正生效时这个总量应趋近 0。数值本身不参与断言逻辑，只要非零且唯一。
            PresentWait100ns = 20_040_000,
            TimelineGeneration = 777,
        };

        var mapped = Fff3FpEngine.Fff3FpSession.MapSnapshot(in native, frameRate: 23.976);

        // ---- 本用例的主张：VRR 的等待计数器跨过了 DTO 边界 ----
        Assert.Equal(20_040_000L, mapped.PresentWait100ns);
        Assert.Equal(1200L, mapped.SwapChainPresents);

        // ---- 其余映射一并钉住（抽函数时不能丢字段）----
        Assert.Equal(111L, mapped.Position100ns);
        Assert.Equal(222L, mapped.Duration100ns);
        Assert.Equal(333L, mapped.FrameIndex);
        Assert.Equal(444L, mapped.RawFramePts);
        Assert.Equal(1, mapped.FrameTimeBaseNum);
        Assert.Equal(15360, mapped.FrameTimeBaseDen);
        Assert.Equal(23.976, mapped.FrameRate, 9);
        Assert.Equal(2, mapped.Decoder);
        Assert.Equal(1u, mapped.ActualColorMode);
        Assert.Equal(PlayerState.Paused, mapped.State);
        Assert.Equal(555L, mapped.PresentedVideoFrames);
        Assert.Equal(777ul, mapped.TimelineGeneration);
    }
}
