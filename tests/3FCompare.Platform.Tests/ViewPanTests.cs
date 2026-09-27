namespace _3FCompare.Platform.Tests;

/// <summary>
/// 单路放大拖动的平移累加（<see cref="ViewPan"/>）。
///
/// <para><b>钉住的是三条已经错过一次的方向/量纲性质</b>，不是"看起来合理"：
/// ① 拖动方向必须与画面位移同向（跟手）。内核把 <c>pan</c> 当<b>视口位置</b>：
/// <c>VideoRenderer.cpp</c> 里画盒原点 <c>= 原点 − pan·量程</c>，所以 <c>pan</c> 越大画面越往左上跑 ——
/// 托管侧漏掉那个负号，症状就是"鼠标往下拖、画面向上跑"（2026-09-26 用户实测到竖直反向、水平不对）。
/// ② 位移的物理像素/DIP 量纲必须除掉 <c>RenderScaling</c>，否则 150% 屏上同样一段拖动会多走 1.5 倍量程。
/// ③ 归一化分母是<b>短边</b>，取长边会让宽屏横向灵敏度凭空减半。</para>
///
/// <para>⚠ 本类只测方向与量纲，<b>测不到量程</b>：内核的 <c>VideoDestination.x/y</c> 是
/// <c>std::uint32_t</c> 且用 <c>max(0, ·)</c> 兜负 ⇒ "往右下露出更多源画面"那一半方向在真机上可能被夹死
/// （拟合盒左沿本就贴 0 时横向平移恒 0）。那条属内核侧限制，真机读数见 <c>--selftest</c> 的平移量程一行。</para>
/// </summary>
public class ViewPanTests
{
    private const double Tol = 1e-4;

    /// <summary>①核心：向右拖 ⇒ panX 必须<b>变小</b>（内核再取负 ⇒ 画面右移 = 跟手）。向下同理。</summary>
    [Fact]
    public void Accumulate_RightAndDownDrag_DecreasesPanSoPictureFollowsCursor()
    {
        var (x, y) = ViewPan.Accumulate(0f, 0f, dxPx: 100, dyPx: 100, scaling: 1.0, minSideDip: 1000);

        Assert.Equal(-0.2, x, Tol);
        Assert.Equal(-0.2, y, Tol);

        // 反向拖必须对称地加回去
        var (rx, ry) = ViewPan.Accumulate(0f, 0f, -100, -100, 1.0, 1000);
        Assert.Equal(0.2, rx, Tol);
        Assert.Equal(0.2, ry, Tol);
    }

    /// <summary>②量纲：DPI 150% 下拖 150 物理像素 == 100% 下拖 100 物理像素。
    /// 漏除 scaling 的话这一条会偏 1.5 倍（表现为"轻轻一拖就顶到头"）。</summary>
    [Fact]
    public void Accumulate_DividesOutRenderScaling_SameDipDragSamePan()
    {
        var at150 = ViewPan.Accumulate(0f, 0f, 150, 0, scaling: 1.5, minSideDip: 1000);
        var at100 = ViewPan.Accumulate(0f, 0f, 100, 0, scaling: 1.0, minSideDip: 1000);

        Assert.Equal(at100.PanX, at150.PanX, Tol);
    }

    /// <summary>③分母是短边：拖满"短边的一半"恰好到量程端点 ±1；
    /// 若误用长边（本仓旧实现），宽屏下这里只会走到 ±(短边/长边)。</summary>
    [Fact]
    public void Accumulate_NormalizesByShortEdge_HalfShortSideReachesUnit()
    {
        var (x, _) = ViewPan.Accumulate(0f, 0f, 500, 0, scaling: 1.0, minSideDip: 1000);
        Assert.Equal(-1.0, x, Tol);
    }

    /// <summary>钳位：任何超量拖动都停在 [-1,1]，且不得溢出成 NaN/±∞
    /// （内核 <c>SetViewTransform</c> 对非有限值直接返回 InvalidArgument）。</summary>
    [Fact]
    public void Accumulate_ClampsToUnitRange_NeverOverflows()
    {
        var (x, y) = ViewPan.Accumulate(0.5f, -0.5f, 1e9, -1e9, 1.0, 1000);

        Assert.Equal(ViewPan.Min, x, Tol);
        Assert.Equal(ViewPan.Max, y, Tol);
    }

    /// <summary>退化输入：短边未知（刚创建的 0 尺寸窗口）必须原样返回，而不是除零出 Infinity；
    /// scaling ≤ 0 按 1.0 处理（与 <c>CompareCropPlanner.ToPhysical</c> 同一约定）。</summary>
    [Fact]
    public void Accumulate_DegenerateInputs_AreNoOpOrAssumeUnity()
    {
        var (x, y) = ViewPan.Accumulate(0.3f, -0.4f, 100, 100, scaling: 1.0, minSideDip: 0);
        Assert.Equal(0.3f, x, Tol);
        Assert.Equal(-0.4f, y, Tol);

        var unknownScale = ViewPan.Accumulate(0f, 0f, 100, 0, scaling: 0, minSideDip: 1000);
        Assert.Equal(-0.2, unknownScale.PanX, Tol);
    }

    /// <summary>连续性：小步累计必须等价于一步到位 —— 节流丢帧（16ms 直接丢弃那一支）
    /// 只允许丢"次数"，不允许丢"位移量"，否则拖到中途会永久少一截。</summary>
    [Fact]
    public void Accumulate_StepwiseEqualsSingleStep()
    {
        var single = ViewPan.Accumulate(0f, 0f, 240, 0, 1.25, 900);
        var (a, _) = ViewPan.Accumulate(0f, 0f, 100, 0, 1.25, 900);
        var (b, _) = ViewPan.Accumulate(a, 0f, 60, 0, 1.25, 900);
        var (c, _) = ViewPan.Accumulate(b, 0f, 80, 0, 1.25, 900);

        Assert.Equal(single.PanX, c, Tol);
    }
}
