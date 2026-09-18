using _3FCompare.Core.Backend;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// PixelReadback（源像素坐标 → 内核后台缓冲坐标）测试。
/// <para>背景：内核 FFF3FP_ReadVideoPixel 的坐标域是后台缓冲，不是片源分辨率。
/// 期望值一律**手工独立推导**，不照抄被测实现（docs/15 教训）。</para>
/// </summary>
public sealed class PixelReadbackTests
{
    /// <summary>4K 片源铺满 1995×1122 缓冲（实测场景：dest == 整个 swap）。</summary>
    private static RenderTargetInfo Full4K() => new(
        SwapWidth: 1995, SwapHeight: 1122,
        ClientWidth: 1995, ClientHeight: 1122,
        DestX: 0, DestY: 0, DestWidth: 1995, DestHeight: 1122,
        OutputBitDepth: 8, Hdr: false);

    [Fact]
    public void SourceCenter_MapsTo_BufferCenter()
    {
        // 3840×2160 的中心 → 1995×1122 的中心：(997.5, 561) → floor → (997, 561)
        var r = PixelReadback.SourceToBackBuffer(1920, 1080, 3840, 2160, Full4K());
        Assert.NotNull(r);
        Assert.Equal(997, r!.Value.X);
        Assert.Equal(561, r!.Value.Y);
    }

    [Fact]
    public void QuarterSource_MapsTo_QuarterBuffer()
    {
        // 取像素中心(+0.5)：(960.5)×1995/3840 = 499.01 → floor 499
        //                (540.5)×1122/2160 = 280.76 → floor 280
        var r = PixelReadback.SourceToBackBuffer(960, 540, 3840, 2160, Full4K());
        Assert.NotNull(r);
        Assert.Equal(499, r!.Value.X);
        Assert.Equal(280, r!.Value.Y);
    }

    [Fact]
    public void Origin_StaysAtOrigin()
    {
        var r = PixelReadback.SourceToBackBuffer(0, 0, 3840, 2160, Full4K());
        Assert.NotNull(r);
        Assert.Equal(0, r!.Value.X);
        Assert.Equal(0, r!.Value.Y);
    }

    [Fact]
    public void Letterbox_OffsetsByDestOrigin()
    {
        // 上下黑边各 61px：dest 高 1000，起点 y=61
        var rt = new RenderTargetInfo(
            SwapWidth: 1000, SwapHeight: 1122,
            ClientWidth: 1000, ClientHeight: 1122,
            DestX: 0, DestY: 61, DestWidth: 1000, DestHeight: 1000,
            OutputBitDepth: 8, Hdr: false);
        // 片源 1080p 垂直中心 (960,540) → 61 + (540.5)*1000/1080 = 61 + 500.46 = 561
        var r = PixelReadback.SourceToBackBuffer(960, 540, 1920, 1080, rt);
        Assert.NotNull(r);
        Assert.Equal(561, r!.Value.Y);
    }

    [Fact]
    public void Clamped_ToSwapChainBounds()
    {
        // 放大/平移时 dest 会超出交换链；片源右下角必须被钳到缓冲内，不能越界
        var rt = new RenderTargetInfo(
            SwapWidth: 800, SwapHeight: 600,
            ClientWidth: 800, ClientHeight: 600,
            DestX: 700, DestY: 500, DestWidth: 2000, DestHeight: 1500,
            OutputBitDepth: 8, Hdr: false);
        var r = PixelReadback.SourceToBackBuffer(1919, 1079, 1920, 1080, rt);
        Assert.NotNull(r);
        Assert.Equal(799, r!.Value.X); // SwapWidth - 1
        Assert.Equal(599, r!.Value.Y); // SwapHeight - 1
    }

    [Fact]
    public void NoRenderTargetInfo_FallsBackToSourceCoords()
    {
        // 演示模式 / 旧内核：DestWidth == 0 ⇒ 退回原坐标（旧行为，此时缓冲即片源尺寸）
        var rt = new RenderTargetInfo(0, 0, 0, 0, 0, 0, 0, 0, 8, false);
        var r = PixelReadback.SourceToBackBuffer(123, 456, 1280, 720, rt);
        Assert.NotNull(r);
        Assert.Equal(123, r!.Value.X);
        Assert.Equal(456, r!.Value.Y);
    }

    [Theory]
    [InlineData(0, 1920)]
    [InlineData(-1, 1920)]
    [InlineData(1920, 0)]
    public void InvalidSourceSize_ReturnsNull(int w, int h)
    {
        Assert.Null(PixelReadback.SourceToBackBuffer(0, 0, w, h, Full4K()));
    }
}
