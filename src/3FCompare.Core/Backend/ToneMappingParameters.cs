using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;

namespace _3FCompare.Core.Backend;

/// <summary>3FP 色调映射参数计算器。
/// 基于3FP原生API（FFF3FP_SetColorMode）的语义：
///   - SdrPeakNits：HDR→SDR 降级的目标峰值（BT.2390 targetPeak）
///   - PaperWhiteNits：SDR→HDR 升级和叠加层（字幕/UI）的"纸白"参考
///   - HdrPeakNits：0 = 自动从显示器 EDID 读取（3FP ResolveTargetPeak）
///
/// 关键设计：
///   - SdrPeakNits 不再固定 100 nits，否则 BT.2390 的 knee 点会退化为 0，
///     导致三次 Hermite 平滑分支失效、高光被线性硬切到 target 区间。
///   - HdrPeakNits 始终传 0（让 3FP 内部 ResolveTargetPeak 决定显示器峰值）。
///   - PaperWhiteNits 与 SdrPeakNits 同向，避免 SDR→HDR 升级时叠加层 199→20300 nits 的过曝。</summary>
public static class ToneMappingParameters
{
    /// <summary>保守的 SDR 显示器默认峰值（nits）。
    /// 对应 Windows "HDR/SDR 亮度平衡" 滑块约 50% 的常见位置，
    /// 也符合参考 SDR 电视的标称值。</summary>
    public const float DefaultSdrTargetPeak = 200f;

    /// <summary>SDR→HDR 升级的默认纸白参考（nits）。
    /// 与 SDR 目标峰值对齐，避免叠加层在 HDR 模式下被过度提升。</summary>
    public const float DefaultPaperWhite = 200f;

    /// <summary>SDR 兜底峰值：当 EDID 缺失时使用 1000 nits 作为回退值，
    /// 与 3FP 内部 ResolveTargetPeak 的最终回退一致。</summary>
    public const float FallbackHdrTargetPeak = 1000f;

    /// <summary>显示器能读取到的最小可用峰值（nits）。</summary>
    public const float MinimumDisplayPeak = 80f;

    /// <summary>显示器能读取到的最大可用峰值（nits）。</summary>
    public const float MaximumDisplayPeak = 10000f;

    /// <summary>计算当前配置下的 3FP 色调映射参数。
    /// 调用时机：会话创建后（已知 ColorMode）与每次 SetColorMode 调用前。</summary>
    /// <param name="colorMode">目标色彩模式（MapToSdr / RawHdrAsSdr / MapToHdr）。</param>
    /// <param name="displayCapabilities">显示器能力（可为 null：触发默认值路径）。</param>
    /// <param name="contentIsHdr">源内容是否为 HDR（用于判断色调映射是否激活）。</param>
    public static ToneMappingConfig Calculate(
        ColorMode colorMode,
        DisplayLuminanceCapabilities? displayCapabilities,
        bool contentIsHdr)
    {
        // HDR 输出路径：3FP 内部 ResolveTargetPeak 会优先取 EDID，无数据时回退 1000 nits。
        // 我们不要传具体数值（保持 0 = 自动），让 3FP 处理显示器细节。
        // 但 SDR 路径仍需要 SdrPeakNits 来做 BT.2390 降级。

        float sdrPeakNits;
        float hdrPeakNits = 0f; // 始终自动：3FP 用 SetTargetPeakOverride(0) → ResolveTargetPeak
        float paperWhiteNits;

        // SDR 目标峰值：根据显示器能力或参数化默认值。
        var displayPeak = ResolveDisplayPeakNits(displayCapabilities, colorMode);

        switch (colorMode)
        {
            case ColorMode.MapToHdr:
                // HDR 输出：用户/显示器语义。3FP 的 ResolveTargetPeak 会自动选目标峰值。
                // PaperWhite 不应过高，避免 UI/字幕异常发白；通常用 203 nits（HLG 参考纸白）。
                // 但若显示器不支持 HDR，3FP 会 fallback 到 SDR 路径，仍需合理的 SdrPeakNits。
                paperWhiteNits = DefaultPaperWhite;
                if (displayCapabilities?.Supported == true && displayPeak >= MinimumDisplayPeak)
                {
                    // 有真实 HDR 显示器：SdrPeakNits 给 Windows DWM 的实际 SDR 白点参考值。
                    // Windows 的"S DR 白点亮度"通常 100-200 nits，取显示器峰值的 20%，封顶 300。
                    sdrPeakNits = Math.Clamp(displayPeak * 0.2f, 100f, 300f);
                }
                else
                {
                    // 显示器无 HDR 或无法检测：使用保守 200 nits 默认。
                    sdrPeakNits = DefaultSdrTargetPeak;
                }
                break;

            case ColorMode.RawHdrAsSdr:
                // 原始 HDR 作为 SDR：不做色调映射，直接显示 PQ/HLG 原始编码。
                // SdrPeakNits 在该路径不起色调映射作用，使用默认值即可。
                sdrPeakNits = DefaultSdrTargetPeak;
                paperWhiteNits = DefaultPaperWhite;
                break;

            case ColorMode.MapToSdr:
            default:
                // HDR→SDR 降级：这里是最关键的路径。
                // SdrPeakNits 作为 BT.2390 targetPeak，须保证 knee 点非 0：
                //   knee = clamp(1.5 * targetPq/sourcePq - 0.5, 0, 1)
                //   为使 knee > 0，需 targetPq/sourcePq > 1/3。
                //   sourcePeak=1000 nits 时，对应 targetPeak ≈ 250 nits。
                //   sourcePeak=4000 nits 时，对应 targetPeak ≈ 700 nits。
                // 但 SDR 显示器实际峰值通常 ≤400 nits，无法达到理论值，故取可用显示器峰值的 80%。
                if (displayPeak >= MinimumDisplayPeak && displayPeak <= MaximumDisplayPeak)
                {
                    sdrPeakNits = Math.Clamp(displayPeak * 0.8f, 200f, 400f);
                }
                else
                {
                    sdrPeakNits = DefaultSdrTargetPeak;
                }
                paperWhiteNits = sdrPeakNits; // SDR 路径无纸白概念，与目标峰值对齐
                break;
        }

        return new ToneMappingConfig
        {
            SdrPeakNits = sdrPeakNits,
            HdrPeakNits = hdrPeakNits,
            PaperWhiteNits = paperWhiteNits,
            ColorMode = colorMode,
            ContentIsHdr = contentIsHdr,
            DetectedDisplayPeakNits = displayPeak,
        };
    }

    /// <summary>解析显示器峰值（nits）。优先 EDID；缺失时返回 0 让调用方使用默认。</summary>
    private static float ResolveDisplayPeakNits(
        DisplayLuminanceCapabilities? capabilities,
        ColorMode colorMode)
    {
        if (capabilities is null) return 0f;

        // 统一解析：HDR 显示器优先取 maxFullFrame（更稳定），其次 max；
        // SDR 显示器（MaxLuminance>0，如标称 250-400 nits）也应参与 SDR 目标峰值计算。
        var peak = capabilities.FullFrameNits > 0
            ? capabilities.FullFrameNits
            : capabilities.MaximumNits;

        // HDR 输出模式：只有 Supported 时才采用显示器数据（否则 HDR 输出无意义）。
        if (colorMode == ColorMode.MapToHdr && !capabilities.Supported)
        {
            return 0f;
        }

        return peak >= MinimumDisplayPeak ? Math.Min(peak, MaximumDisplayPeak) : 0f;
    }
}

/// <summary>计算后的 3FP 色调映射参数。</summary>
public sealed record ToneMappingConfig
{
    /// <summary>SDR 峰值亮度（nits）。HDR→SDR 降级时作为 BT.2390 targetPeak。</summary>
    public float SdrPeakNits { get; init; }

    /// <summary>HDR 峰值亮度（nits）。0 = 自动（让 3FP 用 EDID）。</summary>
    public float HdrPeakNits { get; init; }

    /// <summary>纸白亮度（nits）。SDR→HDR 升级和叠加层参考。</summary>
    public float PaperWhiteNits { get; init; }

    /// <summary>色彩模式。</summary>
    public ColorMode ColorMode { get; init; }

    /// <summary>源内容是否为 HDR。</summary>
    public bool ContentIsHdr { get; init; }

    /// <summary>检测到的显示器峰值（nits）。0 = 未检测到。</summary>
    public float DetectedDisplayPeakNits { get; init; }
}
