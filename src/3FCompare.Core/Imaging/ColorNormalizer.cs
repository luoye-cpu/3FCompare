using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Imaging;

/// <summary>
/// 回读像素的色彩/位深归一化：把内核 <see cref="PixelSample"/> 统一到
/// <b>线性光域（linear light），1.0 = SDR 参考白（paper white）</b>。
///
/// <para><b>为什么需要</b>：内核回读的是<b>已呈现帧</b>的像素，不同输出位深下数值语义
/// 并不一致（内核交换链契约见 <c>VideoRenderer.cpp</c>，实测见 docs/19 §3）：</para>
/// <list type="bullet">
/// <item><description>SDR 输出（8-bit <c>BGRA8</c> / 10-bit <c>RGB10A2</c>）：
/// <b>gamma 编码</b>的归一化值，范围 0..1；</description></item>
/// <item><description>HDR 输出（16-bit <c>R16G16B16A16_FLOAT</c>）：<b>线性 scRGB</b>，
/// <c>1.0 = 80 nits</c>，实测可 &gt;1（docs/19 记录到 <c>B=2.6641</c>）。</description></item>
/// </list>
/// <para>域由内核 <see cref="RenderTargetInfo.Hdr"/> 标志（权威）+ 位深（兜底）共同判定，
/// 见 <see cref="IsHdrDomain(uint,bool)"/>：只认位深会在 10-bit HDR 之类的未来契约下走错曲线。</para>
/// <para>两种值直接相减没有意义；而 HDR 的 &gt;1 值若按 <c>v*255</c> 显示会被截断成 255
/// （<c>ProbePanel.To8Bit</c> / <c>MagnifierOverlay.To8</c> 的现状缺陷）。本类把两者搬到
/// 同一域后再比较，是"多路视频差异对比"的前置条件。</para>
///
/// <para><b>统一域</b>：线性光，<c>1.0</c> = SDR 参考白；HDR 高光<b>允许 &gt;1，绝不截断</b>。</para>
///
/// <para><b>无 NaN 契约</b>：入参 NaN 一律归 <c>0</c>（见 <see cref="SanitizeNaN"/>），
/// 与 <see cref="PixelFidelity"/> / <see cref="Display.CompareLayout"/> 一致。</para>
///
/// <para><b>纯函数 / 无状态 / 无分配</b>：全部是 <c>static</c> 值运算，输入输出均为
/// <c>readonly struct</c>，逐像素调用不产生堆分配。</para>
/// </summary>
public static class ColorNormalizer
{
    /// <summary>scRGB 的单位亮度：内核契约 <c>1.0 = 80 nits</c>（docs/19 §3）。</summary>
    public const float ScRgbUnitNits = 80f;

    /// <summary>SDR 参考白（paper white）的默认亮度（nits）。
    /// 203 nits 取自 ITU-R BT.2408 / HLG 的参考白约定，也是 Windows HDR 组合管线的常见纸白。
    /// <para>⚠ 与 <c>ToneMappingParameters.DefaultPaperWhite</c>（200 nits，色调映射的旋钮）
    /// 不是同一个量：那是"把 SDR 内容放进 HDR 容器时抬多高"，这里是"1.0 的物理亮度基准"。
    /// 二者语义不同，故不互相引用。</para></summary>
    public const float DefaultPaperWhiteNits = 203f;

    /// <summary>位深 + HDR 标志是否属于 HDR（线性 scRGB）域。
    ///
    /// <para><b><paramref name="hdr"/> 是权威判据</b>：它来自 <see cref="RenderTargetInfo.Hdr"/>，
    /// 是内核对自己输出链路的直接陈述；<c>bitDepth == 16</c> 只是格式层面的<b>兜底推断</b>
    /// （当前内核规则 <c>hdr → 16</c>、<c>sourceBitDepth &gt; 8 → 10</c>，故 16 位只可能来自
    /// <c>R16G16B16A16_FLOAT</c>）。</para>
    ///
    /// <para><b>为什么不能只看位深</b>：一旦输出契约变化（例如出现 10-bit HDR 链，
    /// hdr=true 但 bitDepth=10），只看位深就会把线性 scRGB 值当成 sRGB gamma 值再解一次
    /// gamma——那正是本类要消除的"走错曲线"。两者取或：标志置位即按 HDR 处理，
    /// 标志缺失时仍按 16 位格式推断（不改变现有行为）。</para>
    ///
    /// <para>只有位深、拿不到 <see cref="RenderTargetInfo"/> 的场合（如批量回读只回传
    /// <c>outputBitDepth</c>），请显式传 <c>hdr: false</c>，由这里的 16 位兜底生效；
    /// 若另有依据（如同一会话已读到的 rt），应把真实标志传进来。</para></summary>
    public static bool IsHdrDomain(uint bitDepth, bool hdr) => hdr || bitDepth == 16;

    /// <summary>sRGB EOTF：gamma 编码值 → 线性光。
    /// <para>采用 IEC 61966-2-1 的<b>精确分段式</b>而非 2.2 幂近似：</para>
    /// <list type="bullet">
    /// <item><description>它是 SDR 交换链（BGRA8 / RGB10A2）实际遵循的标准，
    /// 用 2.2 近似会在暗部引入最大约 0.003 的偏差，而差异对比恰恰最关心暗部；</description></item>
    /// <item><description>分段式对<b>负值</b>走线性段，能保持符号（超色域线性值不被折叠），
    /// 纯幂函数对负底数无定义；</description></item>
    /// <item><description>&gt;1 的输入继续单调外推、<b>不截断</b>，与 HDR 分支的约定一致。</description></item>
    /// </list>
    /// <para>代价只是一个分支 + 一次 <c>Pow</c>，相对回读/比较本身可忽略。</para>
    /// <para>NaN 入参返回 0（无 NaN 契约），见 <see cref="SanitizeNaN"/>。</para></summary>
    public static float SrgbToLinear(float encoded)
    {
        if (float.IsNaN(encoded)) return 0f;
        return encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary><see cref="SrgbToLinear"/> 的逆（sRGB OETF），供往返一致性验证与"把结果写回原域"使用。
    /// <para>NaN 入参返回 0（无 NaN 契约），见 <see cref="SanitizeNaN"/>。</para></summary>
    public static float LinearToSrgb(float linear)
    {
        if (float.IsNaN(linear)) return 0f;
        return linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>NaN → 0。
    /// <para>为什么必须处理：<c>NaN</c> 与任何值比较恒为 <c>false</c>，一旦流进下游的阈值判断
    /// 就会把"有差异"静默变成"无差异"——这比抛异常危险得多。<see cref="PixelFidelity"/> 与
    /// <see cref="Display.CompareLayout"/> 都有显式的"无 NaN 契约"，本类保持一致。</para>
    /// <para>只处理 NaN：负值（超色域）、<c>&gt;1</c> 的 HDR 高光与 ±Inf 一律原样保留，
    /// 因为本类的契约是"<b>绝不截断</b>"。</para></summary>
    private static float SanitizeNaN(float value) => float.IsNaN(value) ? 0f : value;

    /// <summary>把回读采样统一到线性光域（<c>out</c> 版；逐像素热路径首选，避免返回值搬运）。
    /// A 通道<b>不做</b> gamma/线性转换，原样传递。</summary>
    /// <param name="sample">内核回读的采样（编码值 + 位深）。</param>
    /// <param name="hdr">内核 <see cref="RenderTargetInfo.Hdr"/>：HDR 输出链路的权威标志。</param>
    /// <param name="result">线性光域结果，<c>1.0</c> = SDR 参考白。</param>
    /// <param name="paperWhiteNits">SDR 参考白亮度（nits），必须为有限正数；仅 HDR 分支使用与校验。</param>
    public static void ToLinear(
        in PixelSample sample, bool hdr, out LinearRgba result,
        float paperWhiteNits = DefaultPaperWhiteNits)
        => result = ToLinear(sample.R, sample.G, sample.B, sample.A, sample.BitDepth, hdr, paperWhiteNits);

    /// <summary>把回读采样统一到线性光域（返回值版，等价于 <see cref="ToLinear(in PixelSample, bool, out LinearRgba, float)"/>）。</summary>
    public static LinearRgba ToLinear(
        in PixelSample sample, bool hdr,
        float paperWhiteNits = DefaultPaperWhiteNits)
        => ToLinear(sample.R, sample.G, sample.B, sample.A, sample.BitDepth, hdr, paperWhiteNits);

    /// <summary>按原始通道值 + 位深归一化（批量回读缓冲 <c>float[]</c> 逐像素取用时可免于构造 <see cref="PixelSample"/>）。</summary>
    /// <param name="r">R 通道（回读域编码值）。</param>
    /// <param name="g">G 通道。</param>
    /// <param name="b">B 通道。</param>
    /// <param name="a">A 通道（原样传递，不做转换）。</param>
    /// <param name="bitDepth">回读位深：8/10 = SDR gamma 编码；16 = HDR 线性 scRGB。</param>
    /// <param name="hdr">内核 <see cref="RenderTargetInfo.Hdr"/>：HDR 输出链路的权威标志
    /// （见 <see cref="IsHdrDomain"/>）。</param>
    /// <param name="paperWhiteNits">SDR 参考白亮度（nits），必须为有限正数；仅 HDR 分支使用与校验。</param>
    public static LinearRgba ToLinear(
        float r, float g, float b, float a, uint bitDepth, bool hdr,
        float paperWhiteNits = DefaultPaperWhiteNits)
    {
        if (IsHdrDomain(bitDepth, hdr))
        {
            // 参考白只有这一条分支在用，校验就放在这里：SDR 是逐像素热路径，
            // 不该为一个用不到的旋钮付出"每像素都可能抛异常"的语义负担
            //（fail-fast 的意图保留在真正依赖该值的地方）。
            ValidatePaperWhite(paperWhiteNits);

            // scRGB 线性值 → nits → 相对 SDR 参考白。
            // 刻意不 Clamp：HDR 高光可远超参考白，截断会把高光差异抹平。
            var scale = ScRgbUnitNits / paperWhiteNits;
            return new LinearRgba(
                SanitizeNaN(r * scale), SanitizeNaN(g * scale), SanitizeNaN(b * scale), a);
        }

        // SDR（8/10）与**未知位深**：gamma 编码 → 线性。
        //
        // 未知位深（0 / 其他值）走 SDR 分支是**有意的兜底策略**：
        //  1) 回读绝大多数发生在 SDR 路径，按 SDR 解释最可能正确；
        //  2) sRGB EOTF 单调且不截断，即使源其实是线性值，结果也只是整体压暗，
        //     通道间大小关系与"两路之差"的符号仍然成立，差异对比不至于失效；
        //  3) "直接透传"会把 gamma 值与线性值混进同一个域——那正是本类要消除的问题。
        return new LinearRgba(
            SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), a);
    }

    /// <summary><see cref="ToLinear(in PixelSample, bool, float)"/> 的逆：线性光 → 回读域编码值。
    /// 用于往返一致性验证，或把差异结果写回原域。A 通道原样传递。</summary>
    /// <param name="linear">线性光域的 RGBA。</param>
    /// <param name="bitDepth">目标回读位深。</param>
    /// <param name="hdr">内核 <see cref="RenderTargetInfo.Hdr"/>：HDR 输出链路的权威标志。</param>
    /// <param name="paperWhiteNits">SDR 参考白亮度（nits），必须为有限正数；仅 HDR 分支使用与校验。</param>
    public static PixelSample FromLinear(
        in LinearRgba linear, uint bitDepth, bool hdr,
        float paperWhiteNits = DefaultPaperWhiteNits)
    {
        if (IsHdrDomain(bitDepth, hdr))
        {
            ValidatePaperWhite(paperWhiteNits);

            var scale = paperWhiteNits / ScRgbUnitNits;
            return new PixelSample(
                SanitizeNaN(linear.R * scale), SanitizeNaN(linear.G * scale),
                SanitizeNaN(linear.B * scale), linear.A, bitDepth);
        }

        return new PixelSample(
            LinearToSrgb(linear.R), LinearToSrgb(linear.G), LinearToSrgb(linear.B),
            linear.A, bitDepth);
    }

    /// <summary>参考白必须是有限正数：0 / 负数会让 HDR 缩放因子变成 Inf 或反号，
    /// NaN 则一路污染到差异值——静默产出无意义的对比结果，比抛异常危险得多。
    /// <para><b>只在 HDR 分支调用</b>：SDR 路径不使用参考白，也就没有理由在校验它时
    /// 于逐像素热路径上抛异常（见 <see cref="ToLinear(float,float,float,float,uint,bool,float)"/>）。</para></summary>
    private static void ValidatePaperWhite(float paperWhiteNits)
    {
        // !(x > 0) 同时拦下 NaN；IsFinite 再拦下 +Inf
        if (!(paperWhiteNits > 0f) || !float.IsFinite(paperWhiteNits))
        {
            throw new ArgumentOutOfRangeException(
                nameof(paperWhiteNits), paperWhiteNits,
                "SDR 参考白必须是有限正数（nits）");
        }
    }
}

/// <summary>线性光域的 RGBA：<c>1.0</c> = SDR 参考白，HDR 高光可 &gt;1。
///
/// <para>与 <see cref="PixelSample"/> 的区别：<see cref="PixelSample"/> 是<b>回读域</b>的值，
/// 语义随 <c>BitDepth</c> 变化（gamma 编码 / 线性 scRGB）；本结构是<b>编码无关</b>的线性值，
/// 因此可以跨路（HDR 路 vs SDR 路）直接相减。</para></summary>
/// <param name="R">线性 R（1.0 = SDR 参考白）。</param>
/// <param name="G">线性 G。</param>
/// <param name="B">线性 B。</param>
/// <param name="A">A（原样传递，未做转换）。</param>
public readonly record struct LinearRgba(float R, float G, float B, float A);
