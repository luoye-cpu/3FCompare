using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// ToneMappingParameters 显示器感知分支测试。
/// 验证报告改进项：DisplayCapabilities 提供真实数据时，
/// SDR 目标峰值（BT.2390 targetPeak）与 HDR 纸白应随显示器调整。
/// </summary>
public sealed class ToneMappingParametersTests
{
    // ---- 显示器能力样例 ----

    /// <summary>SDR 显示器（非 HDR，但标称 300 nits 峰值）。</summary>
    private static DisplayLuminanceCapabilities Sdr300Nits() => new()
    {
        Supported = false,
        MaximumNits = 300f,
        MinimumNits = 0.05f,
        FullFrameNits = 300f,
    };

    /// <summary>HDR 显示器（HDR10，1000 nits）。</summary>
    private static DisplayLuminanceCapabilities Hdr1000Nits() => new()
    {
        Supported = true,
        MaximumNits = 1000f,
        MinimumNits = 0.003f,
        FullFrameNits = 400f,
    };

    // ---- 场景 1：SDR 输出 + 已知 SDR 显示器（改进点） ----

    [Fact]
    public void MapToSdr_WithKnownSdrDisplay_ScalesTargetPeak()
    {
        // 改进前：displayCapabilities 恒为 Supported=false + 0 峰值 → 固定 200 nits
        // 改进后：SDR 显示器 300 nits → targetPeak = clamp(300*0.8, 200, 400) = 240
        var config = ToneMappingParameters.Calculate(ColorMode.MapToSdr, Sdr300Nits(), contentIsHdr: true);

        Assert.Equal(240f, config.SdrPeakNits);
        Assert.Equal(240f, config.PaperWhiteNits);
        // HDR 峰值仍自动（0 = 交给 3FP ResolveTargetPeak）
        Assert.Equal(0f, config.HdrPeakNits);
    }

    [Fact]
    public void MapToSdr_WithUnknownDisplay_FallsBackToDefault()
    {
        // 未知显示器：保持旧行为（固定 200 nits），确保无回归
        var config = ToneMappingParameters.Calculate(ColorMode.MapToSdr, null, contentIsHdr: false);

        Assert.Equal(ToneMappingParameters.DefaultSdrTargetPeak, config.SdrPeakNits);
        Assert.Equal(ToneMappingParameters.DefaultSdrTargetPeak, config.PaperWhiteNits);
    }

    [Fact]
    public void MapToSdr_WithLowPeakSdrDisplay_ClampsToFloor()
    {
        // 100 nits 显示器：clamp(80, 200, 400) = 200（不低于下限）
        var low = new DisplayLuminanceCapabilities { Supported = false, MaximumNits = 100f, FullFrameNits = 100f };
        var config = ToneMappingParameters.Calculate(ColorMode.MapToSdr, low, contentIsHdr: true);

        Assert.Equal(200f, config.SdrPeakNits);
    }

    // ---- 场景 2：HDR 输出 + 已知 HDR 显示器 ----

    [Fact]
    public void MapToHdr_WithKnownHdrDisplay_UsesDisplayPeakForSdrReference()
    {
        // HDR1000 显示器（FullFrame=400）：SdrPeak = clamp(400*0.2, 100, 300) = 100
        // → 用于 DWM SDR 白点参考（全屏亮度 400 的 20%）。
        var config = ToneMappingParameters.Calculate(ColorMode.MapToHdr, Hdr1000Nits(), contentIsHdr: true);

        Assert.Equal(100f, config.SdrPeakNits);
        // 纸白与 HDR 显示器语义一致地固定（203 = HLG 参考纸白语义）
        Assert.True(config.PaperWhiteNits > 0);
        // HDR 输出：峰值永远自动
        Assert.Equal(0f, config.HdrPeakNits);
    }

    [Fact]
    public void MapToHdr_WithUnknownDisplay_UsesDefaults()
    {
        var config = ToneMappingParameters.Calculate(ColorMode.MapToHdr, null, contentIsHdr: true);

        Assert.Equal(ToneMappingParameters.DefaultSdrTargetPeak, config.SdrPeakNits);
    }

    // ---- 场景 3：HDR 显示器但用户选 SDR 输出（内容仍按 SDR 降级）----

    [Fact]
    public void MapToSdr_WithHdrCapableDisplay_UsesFullFramePeak()
    {
        // HDR 显示器但用户选 SDR：displayPeak = FullFrame（400）→ clamp(400*0.8,200,400) = 320 nits
        var config = ToneMappingParameters.Calculate(ColorMode.MapToSdr, Hdr1000Nits(), contentIsHdr: true);

        Assert.Equal(320f, config.SdrPeakNits);
    }

    // ---- 场景 4：RawHdrAsSdr ----

    [Fact]
    public void RawHdrAsSdr_IgnoresDisplayAndUsesDefaults()
    {
        var config = ToneMappingParameters.Calculate(ColorMode.RawHdrAsSdr, Hdr1000Nits(), contentIsHdr: true);

        Assert.Equal(ToneMappingParameters.DefaultSdrTargetPeak, config.SdrPeakNits);
        Assert.Equal(ToneMappingParameters.DefaultPaperWhite, config.PaperWhiteNits);
    }
}