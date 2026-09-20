using _3FCompare.Core.Backend;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// PixelReadback 的 zoom/pan 语义测试（4 参重载）。
///
/// <para><b>内核语义（已核实）</b>：<c>VideoRenderer.cpp:4517-4532</c> 把 fit 框就地改成
/// 缩放/平移后的矩形，<c>:4561-4564</c> 再把该矩形存进 <c>lastDest*</c> ⇒ 内核上报的
/// Dest 是 zoom/pan <b>之后</b>的实际绘制矩形。因此本组测试传入的 Dest 一律当
/// <b>post-zoom dest</b> 使用：换算必须原样采用它，<b>不得再叠加任何变换</b>。</para>
///
/// <para>这一契约此前由一对同名重载承载（7 参版把同一个 <c>rt</c> 当 fit 框 + 自算 zoom/pan），
/// 两者入参语义完全相反、极易重复施加变换，而 7 参版全仓零调用，已整体删除。
/// 本文件即删除后的契约钉：Dest 就是绘制矩形本身。</para>
///
/// <para>期望值一律**手工独立推导**（几何 + 内核公式），不照抄被测实现。</para>
/// </summary>
public sealed class PixelReadbackZoomPanTests
{
    /// <summary>1000×1000 片源，fit 框恰好落在 4000×4000 缓冲中央：Dest=(1000,1000,1000,1000)。
    /// 选正方形 + 整值，便于手算（1 源像素 == 1 缓冲像素）。</summary>
    private static RenderTargetInfo SquareFit() => new(
        SwapWidth: 4000, SwapHeight: 4000,
        ClientWidth: 4000, ClientHeight: 4000,
        DestX: 1000, DestY: 1000, DestWidth: 1000, DestHeight: 1000,
        OutputBitDepth: 8, Hdr: false);

    /// <summary>同一会话 zoom=2/pan=0 时内核会上报的 Dest：fit 框以中心为锚放大 2×
    /// ⇒ (1000+500, 1000+500, 2000, 2000) = (1500,1500,2000,2000)。</summary>
    private static RenderTargetInfo SquareZoom2() => new(
        SwapWidth: 4000, SwapHeight: 4000,
        ClientWidth: 4000, ClientHeight: 4000,
        DestX: 1500, DestY: 1500, DestWidth: 2000, DestHeight: 2000,
        OutputBitDepth: 8, Hdr: false);

    private const int VW = 1000;
    private const int VH = 1000;

    private static (int X, int Y) Fwd(int sx, int sy, RenderTargetInfo rt)
    {
        var r = PixelReadback.SourceToBackBuffer(sx, sy, VW, VH, rt);
        Assert.NotNull(r);
        return r!.Value;
    }

    private static (int X, int Y) Inv(int bx, int by, RenderTargetInfo rt)
    {
        var r = PixelReadback.BackBufferToSource(bx, by, VW, VH, rt);
        Assert.NotNull(r);
        return r!.Value;
    }

    // ───────────────────────── zoom=1（fit 框即绘制矩形） ─────────────────────────

    [Fact]
    public void ZoomOne_Forward_HandDerived()
    {
        var rt = SquareFit();
        // src(0,0)     → 1000 + (0.5)*1000/1000   = 1000.5 → floor 1000
        // src(999,999) → 1000 + (999.5)           = 1999.5 → floor 1999
        Assert.Equal((1000, 1000), Fwd(0, 0, rt));
        Assert.Equal((1999, 1999), Fwd(999, 999, rt));
    }

    [Fact]
    public void ZoomOne_Inverse_HandDerived()
    {
        var rt = SquareFit();
        // buffer(1000,1000) → (1000-1000)*1000/1000 = 0
        // buffer(1999,1999) → (1999-1000)*1000/1000 = 999
        Assert.Equal((0, 0), Inv(1000, 1000, rt));
        Assert.Equal((999, 999), Inv(1999, 1999, rt));
    }

    // ───────────── zoom=2 的 post-zoom dest：必须原样采用，不得二次变换 ─────────────

    [Fact]
    public void PostZoomDest_Forward_IsUsedAsIs()
    {
        // Dest=(1500,1500,2000,2000) 已是绘制矩形：
        //   bx = floor(1500 + (sx+0.5)*2000/1000) = floor(1500 + 2*sx + 1) = 1501 + 2*sx
        // 若把同一份 rt 误当 fit 框再放大 2×（重复施加变换），结果会整体偏移并放大一倍。
        var rt = SquareZoom2();
        Assert.Equal((1501, 1501), Fwd(0, 0, rt));
        Assert.Equal((1999, 1999), Fwd(249, 249, rt));
        Assert.Equal((2501, 2501), Fwd(500, 500, rt));
        Assert.Equal((3499, 3499), Fwd(999, 999, rt));
    }

    [Fact]
    public void PostZoomDest_Inverse_IsUsedAsIs()
    {
        // sx = floor((bx-1500)*1000/2000) = floor((bx-1500)/2)
        var rt = SquareZoom2();
        Assert.Equal((0, 0), Inv(1501, 1501, rt));
        Assert.Equal((249, 249), Inv(1999, 1999, rt));
        Assert.Equal((500, 500), Inv(2501, 2501, rt));
        Assert.Equal((999, 999), Inv(3499, 3499, rt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(750)]
    [InlineData(999)]
    public void PostZoomDest_RoundTrips(int source)
    {
        // 缩放因子 = dw/vw = 2 ≥ 1 ⇒ 像素中心量化可逆，往返应精确回到原像素。
        var rt = SquareZoom2();
        var buf = Fwd(source, source, rt);
        Assert.Equal((source, source), Inv(buf.X, buf.Y, rt));
    }

    [Fact]
    public void PannedDest_Forward_HandDerived()
    {
        // 内核 zoom=2、pan=+1 时 Dest 被钳到 (0,0,2000,2000)：
        //   bx = floor(0 + (sx+0.5)*2) = floor(2*sx + 1) = 1 + 2*sx
        var rt = new RenderTargetInfo(
            SwapWidth: 4000, SwapHeight: 4000,
            ClientWidth: 4000, ClientHeight: 4000,
            DestX: 0, DestY: 0, DestWidth: 2000, DestHeight: 2000,
            OutputBitDepth: 8, Hdr: false);

        Assert.Equal((1, 1), Fwd(0, 0, rt));
        Assert.Equal((999, 999), Fwd(499, 499, rt));
        Assert.Equal((1999, 1999), Fwd(999, 999, rt));
    }

    [Fact]
    public void PannedDest_RoundTrips()
    {
        var rt = new RenderTargetInfo(
            SwapWidth: 4000, SwapHeight: 4000,
            ClientWidth: 4000, ClientHeight: 4000,
            DestX: 0, DestY: 0, DestWidth: 2000, DestHeight: 2000,
            OutputBitDepth: 8, Hdr: false);

        foreach (var s in new[] { 0, 1, 250, 499, 500, 501, 750, 998, 999 })
        {
            var buf = Fwd(s, s, rt);
            Assert.Equal((s, s), Inv(buf.X, buf.Y, rt));
        }
    }

    // ───────────────────────── letterbox 偏移 ─────────────────────────

    [Fact]
    public void Letterbox_Inverse_SubtractsDestOrigin()
    {
        // 与 PixelReadbackTests.Letterbox_OffsetsByDestOrigin 同一场景（上下黑边各 61px）
        var rt = new RenderTargetInfo(
            SwapWidth: 1000, SwapHeight: 1122,
            ClientWidth: 1000, ClientHeight: 1122,
            DestX: 0, DestY: 61, DestWidth: 1000, DestHeight: 1000,
            OutputBitDepth: 8, Hdr: false);

        // 片源 1920×1080，故这里不能用类内的 1000×1000 便捷包装
        // buffer(500,561) → x = (500-0)*1920/1000 = 960；y = (561-61)*1080/1000 = 540
        Assert.Equal((960, 540), PixelReadback.BackBufferToSource(500, 561, 1920, 1080, rt));
        // buffer(999,1060) → x = 999*1.92 = 1918.08 → 1918；y = 999*1.08 = 1078.92 → 1078
        Assert.Equal((1918, 1078), PixelReadback.BackBufferToSource(999, 1060, 1920, 1080, rt));
    }

    // ───────────────────────── 边界钳制 ─────────────────────────

    [Fact]
    public void OutOfRangeBuffer_ClampedToSourceBounds()
    {
        var rt = SquareZoom2();
        // buffer(0,0)       → floor((0-1500)/2)    = -750 → clamp 0
        // buffer(3999,3999) → floor((3999-1500)/2) = 1249 → clamp 999
        Assert.Equal((0, 0), Inv(0, 0, rt));
        Assert.Equal((999, 999), Inv(3999, 3999, rt));
    }

    [Fact]
    public void ZeroSwapSize_ClampsOnlyLowerBound()
    {
        // 旧内核没给交换链尺寸（swap=0）：只能用 0 做下界，不引入新的上界。
        var rt = new RenderTargetInfo(
            SwapWidth: 0, SwapHeight: 0,
            ClientWidth: 0, ClientHeight: 0,
            DestX: 10, DestY: 10, DestWidth: 2000, DestHeight: 2000,
            OutputBitDepth: 8, Hdr: false);

        // src(0,0)   → floor(10 + 0.5*2000/1000) = floor(11)   = 11
        // src(999,999) → floor(10 + 999.5*2)     = floor(2009) = 2009（远超 swap，未被钳掉）
        Assert.Equal((11, 11), Fwd(0, 0, rt));
        Assert.Equal((2009, 2009), Fwd(999, 999, rt));
    }

    [Fact]
    public void ZeroDest_FallsBackToIdentity_BothDirections()
    {
        // 演示模式 / 旧内核：Dest 全 0 ⇒ 退回"缓冲坐标即片源坐标"
        var rt = new RenderTargetInfo(0, 0, 0, 0, 0, 0, 0, 0, 8, false);
        Assert.Equal((123, 456), Fwd(123, 456, rt));
        Assert.Equal((321, 654), Inv(321, 654, rt));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    [InlineData(-1, 1000)]
    public void InvalidSourceSize_ReturnsNull(int w, int h)
    {
        var rt = SquareFit();
        Assert.Null(PixelReadback.SourceToBackBuffer(0, 0, w, h, rt));
        Assert.Null(PixelReadback.BackBufferToSource(0, 0, w, h, rt));
    }
}
