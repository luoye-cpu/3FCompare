using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Imaging;

/// <summary>
/// 当前画面"能不能宣称像素级"的档位（<see cref="PixelFidelity"/> 的判定结果）。
///
/// <para>划分依据是内核的取样事实（已核实）：内核放大恒为<b>双线性</b>
/// （<c>VideoRenderer.cpp</c> sampler <c>MIN_MAG_LINEAR_MIP_POINT</c>），
/// Lanczos/双三次只出现在缩小链路。因此：</para>
/// <list type="bullet">
/// <item><description>1:1 时呈现缓冲里的值就是源像素原值；</description></item>
/// <item><description>任何缩放（含内核自动 fit、用户 zoom）都会引入插值，
/// 位置也许仍可对齐，但取样值已非原值。</description></item>
/// </list>
/// </summary>
public enum PixelFidelityTier
{
    /// <summary>尺寸不可用（源或 dest ≤ 0、NaN/Inf）——无法判定，不应据此宣称任何结论。</summary>
    Unknown = 0,

    /// <summary>横纵均为 1:1（相差不超过判定容差，见 <see cref="PixelFidelity.RelativeTolerance"/>）——
    /// 像素为源原值，<b>可以宣称像素级比对</b>。</summary>
    PixelExact = 1,

    /// <summary>横纵均为同一个整数倍（≥2）放大——像素位置仍可对齐，但取样已插值，
    /// <b>只能用于视觉差异</b>，不能宣称像素级。
    /// <para>"整数倍"须在判定容差内成立；容差同时受绝对与相对上限约束，
    /// 以免极小源上把 2.6× 这类真·非整数倍算成 3×。</para></summary>
    IntegerScaled = 2,

    /// <summary>其余情况：非整数倍放大、横纵比不一致、缩小、fit 产生的任意比例——
    /// 一律经过重采样，仅供视觉参考。</summary>
    Interpolated = 3,
}

/// <summary>
/// <see cref="PixelFidelity.Evaluate(double, double, double, double)"/> 的结果。
///
/// <para><b>无 NaN 契约</b>：尺寸不可用时 <see cref="RatioX"/>/<see cref="RatioY"/> 为 <c>0</c>
/// 而非 <c>NaN</c>——NaN 一旦流入下游的阈值比较会静默地把所有判断变成 <c>false</c>。</para>
/// </summary>
/// <param name="Tier">可信度档位。</param>
/// <param name="RatioX">横向缩放比 <c>destWidth / sourceWidth</c>；不可用时为 0。</param>
/// <param name="RatioY">纵向缩放比 <c>destHeight / sourceHeight</c>；不可用时为 0。</param>
/// <param name="Description">面向 UI 的中文文案（非空）。</param>
public readonly record struct PixelFidelityResult(
    PixelFidelityTier Tier, double RatioX, double RatioY, string Description)
{
    /// <summary>是否可对外宣称"像素级比对"（仅 <see cref="PixelFidelityTier.PixelExact"/> 为真）。</summary>
    public bool CanClaimPixelExact => Tier == PixelFidelityTier.PixelExact;

    /// <summary>尺寸是否可用于给出任何结论（<see cref="PixelFidelityTier.Unknown"/> 为假）。</summary>
    public bool IsUsable => Tier != PixelFidelityTier.Unknown;
}

/// <summary>
/// 判定"当前渲染画面能否宣称像素级"：比较视频<b>源分辨率</b>与内核上报的
/// <b>实际绘制矩形</b>（<c>RenderTargetInfo.DestWidth/DestHeight</c>，zoom/pan 之后的 dest，非 fit 框）。
///
/// <para><b>为什么需要</b>：回读 API（<c>FFF3FP_ReadVideoPixelRegion</c>）读的是<b>已呈现帧</b>，
/// 分辨率等于呈现分辨率而非源分辨率。只有 dest 与源分辨率 1:1 时，呈现缓冲里的像素才是源原值；
/// 一旦发生缩放，像素已被插值，任何"像素级比对"的结论都不成立。本类把这一前提变成可计算、可展示的量。</para>
///
/// <para><b>纯函数 / 无状态</b>：不调用任何内核 API，全部是 <c>static</c> 值运算，便于单测与复用。
/// 入参类型直接复用 Core 的 <see cref="RenderTargetInfo"/>，因此 Core 可独立编译。</para>
/// </summary>
public static class PixelFidelity
{
    /// <summary>1:1 判定的绝对容差（单位：像素）。dest 与 source 每轴相差不超过此值即视为 1:1。
    /// <para>取 0.5 而非严格 <c>==</c>：<c>dest</c> 是内核从浮点布局取整上报的整数，
    /// 布局舍入可能带来 ±0.5 像素的偏移，严格相等会把合法的 1:1 误判为缩放。
    /// 而超过 0.5 像素的偏差已意味着必然发生过一次采样，不能再宣称原值。</para>
    /// <para>它是<b>绝对</b>容差，源尺寸很小时会与倍数同级，因此实际判定还要再受
    /// <see cref="RelativeTolerance"/> 收紧，见该常量的说明。</para></summary>
    public const double PixelTolerance = 0.5;

    /// <summary>整数倍 / 1:1 判定的<b>相对</b>容差上限。
    ///
    /// <para><b>为什么需要</b>：<see cref="PixelTolerance"/> 是绝对像素容差，当"倍数 × 源尺寸"
    /// 本身很小时（极小源，或低倍数 + 小源），0.5px 会与倍数同级甚至更大。例：源宽 1px、
    /// dest 2.6px 时 <c>|2.6 − 3×1| = 0.4 ≤ 0.5</c> 会被判成"3.0× 整数倍"，而真实倍率是 2.6×。</para>
    ///
    /// <para><b>规则</b>：容差取「<see cref="PixelTolerance"/> 像素」与「倍数 × 源尺寸 × 本值」中的
    /// <b>较小者</b>。绝对项负责吸收布局取整（源 ≥ 50px 时它更小，行为与引入本常量前逐位一致），
    /// 相对项负责在极小源上收紧，避免把真·非整数倍误判为整数倍。</para>
    ///
    /// <para><b>为什么是 1%</b>：它远大于布局取整带来的相对误差（源 ≥ 50px 时取整不足 1%，
    /// 而那时绝对项本来就更小），又远小于"2.6 对 3"这种 13% 的真实偏差，两侧都有充分余量。
    /// 真实视频源（≥ 320px）永远落在绝对项一侧，因此本常量只影响退化尺寸。</para></summary>
    public const double RelativeTolerance = 0.01;

    /// <summary>从内核渲染目标信息 + 源分辨率判定。
    /// <para>注意 <see cref="RenderTargetInfo.DestWidth"/>/<c>DestHeight</c> 是 <c>uint</c>，
    /// 天然非负，但仍可能为 0（未布局 / 未渲染）。</para></summary>
    /// <param name="info">内核上报的渲染目标信息。</param>
    /// <param name="sourceWidth">视频源宽度（像素）。</param>
    /// <param name="sourceHeight">视频源高度（像素）。</param>
    public static PixelFidelityResult FromRenderTarget(
        in RenderTargetInfo info, int sourceWidth, int sourceHeight)
        => Evaluate((double)info.DestWidth, (double)info.DestHeight, sourceWidth, sourceHeight);

    /// <summary>从 dest 尺寸 + 源尺寸判定（整数便捷重载）。</summary>
    /// <param name="destWidth">内核上报的实际绘制矩形宽度（像素）。</param>
    /// <param name="destHeight">内核上报的实际绘制矩形高度（像素）。</param>
    /// <param name="sourceWidth">视频源宽度（像素）。</param>
    /// <param name="sourceHeight">视频源高度（像素）。</param>
    public static PixelFidelityResult Evaluate(
        int destWidth, int destHeight, int sourceWidth, int sourceHeight)
        => Evaluate((double)destWidth, (double)destHeight, sourceWidth, sourceHeight);

    /// <summary>判定核心：给定 dest 矩形与源分辨率，返回档位、缩放比与展示文案。
    ///
    /// <para><b>档位规则</b>（各档容差 = min(<see cref="PixelTolerance"/>,
    /// 倍数 × 源尺寸 × <see cref="RelativeTolerance"/>)，见后者说明）：</para>
    /// <list type="bullet">
    /// <item><description><see cref="PixelFidelityTier.Unknown"/>：任一入参非有限正数
    /// （≤0 / 负数 / NaN / ±Inf）；<see cref="PixelFidelityResult.RatioX"/>/<c>RatioY</c> 记 0。</description></item>
    /// <item><description><see cref="PixelFidelityTier.PixelExact"/>：横纵各自
    /// <c>|dest − source|</c> 不超过容差（倍数 = 1）。</description></item>
    /// <item><description><see cref="PixelFidelityTier.IntegerScaled"/>：存在整数
    /// <c>n ≥ 2</c> 使横纵各自 <c>|dest − n·source|</c> 不超过容差（即同一个整数倍、各向同性）。</description></item>
    /// <item><description><see cref="PixelFidelityTier.Interpolated"/>：其余（含非整数倍、非等比、缩小）。</description></item>
    /// </list>
    /// </summary>
    /// <param name="destWidth">内核上报的实际绘制矩形宽度（像素）。</param>
    /// <param name="destHeight">实际绘制矩形高度。</param>
    /// <param name="sourceWidth">视频源宽度。</param>
    /// <param name="sourceHeight">视频源高度。</param>
    public static PixelFidelityResult Evaluate(
        double destWidth, double destHeight, double sourceWidth, double sourceHeight)
    {
        // 无效尺寸统一归 Unknown，且绝不把 NaN/Inf 写进 RatioX/RatioY。
        if (!IsPositiveFinite(destWidth) || !IsPositiveFinite(destHeight) ||
            !IsPositiveFinite(sourceWidth) || !IsPositiveFinite(sourceHeight))
        {
            return new PixelFidelityResult(
                PixelFidelityTier.Unknown, 0d, 0d,
                "渲染尺寸或源分辨率不可用，无法判定像素可信度");
        }

        var ratioX = destWidth / sourceWidth;
        var ratioY = destHeight / sourceHeight;

        // 1:1 —— 按容差判定，避免布局取整把合法 1:1 误判为缩放。
        if (WithinScaleTolerance(destWidth, sourceWidth, 1d) &&
            WithinScaleTolerance(destHeight, sourceHeight, 1d))
        {
            return new PixelFidelityResult(
                PixelFidelityTier.PixelExact, ratioX, ratioY,
                "1:1 原生分辨率，可像素级比对");
        }

        // 整数倍放大：横纵必须落在同一个整数倍上（各向同性），否则视为非等比缩放。
        // 倍数取"离 ratioX 最近的整数"：用 Floor(x+0.5) 而不是 Math.Round(x)——
        // 后者默认银行家舍入（2.5→2、3.5→4），会让判据依赖于舍入模式而非几何距离。
        var factor = Math.Floor(ratioX + 0.5);
        if (factor >= 2d &&
            WithinScaleTolerance(destWidth, sourceWidth, factor) &&
            WithinScaleTolerance(destHeight, sourceHeight, factor))
        {
            return new PixelFidelityResult(
                PixelFidelityTier.IntegerScaled, ratioX, ratioY,
                $"已放大 {factor:0.0}×（整数倍插值），非像素级");
        }

        return new PixelFidelityResult(
            PixelFidelityTier.Interpolated, ratioX, ratioY,
            DescribeInterpolated(ratioX, ratioY));
    }

    /// <summary>生成 <see cref="PixelFidelityTier.Interpolated"/> 的展示文案：区分放大/缩小/非等比。</summary>
    private static string DescribeInterpolated(double ratioX, double ratioY)
    {
        if (Math.Abs(ratioX - ratioY) <= 1e-6)
        {
            return ratioX < 1d
                ? $"已缩小 {ratioX:0.00}×（重采样），非像素级"
                : $"已放大 {ratioX:0.00}×（插值），非像素级";
        }

        return $"非等比缩放 X {ratioX:0.00}× / Y {ratioY:0.00}×（插值），非像素级";
    }

    /// <summary>有限正数判定：<c>v &gt; 0</c> 同时拦下 NaN（比较恒为 false）；IsFinite 拦下 ±Inf。</summary>
    private static bool IsPositiveFinite(double v) => v > 0d && double.IsFinite(v);

    /// <summary>判断 <paramref name="dest"/> 是否等于 <paramref name="factor"/> × <paramref name="source"/>，
    /// 容差取「绝对 <see cref="PixelTolerance"/> 像素」与「<see cref="RelativeTolerance"/> 相对上限」的
    /// <b>较小者</b>——理由见 <see cref="RelativeTolerance"/>。</summary>
    private static bool WithinScaleTolerance(double dest, double source, double factor)
    {
        var ideal = factor * source;
        var tolerance = Math.Min(PixelTolerance, ideal * RelativeTolerance);
        return Math.Abs(dest - ideal) <= tolerance;
    }
}
