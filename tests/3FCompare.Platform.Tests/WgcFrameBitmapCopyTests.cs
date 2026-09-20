using System.Drawing;
using System.Drawing.Imaging;
using _3FCompare.App.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// WGC 原生缓冲 → 托管位图的**唯一**转换点（<c>WgcFrameCapture.CopyToBitmap</c>）。
///
/// <para><b>为什么值得单独钉</b>：原生侧契约（<c>native/wgc_capture/wgc_capture.h</c>）与 GDI DIB 恰好相反 ——
/// 原生输出是**自上而下**行序、且 stride 允许大于 <c>width*4</c>。这一跳做错（顺手翻转、或按
/// <c>width*4</c> 取行）不会报错，只会静默产出错位/串行的帧，而调用方拿到的仍是"可信的
/// <see cref="_3FCompare.Core.Capture.CaptureRoute.Wgc"/> 帧"。docs/27 §十 的行序判据此前只能在真机上验证，
/// 托管侧零覆盖。</para>
///
/// <para><b>不加载原生库</b>：源缓冲是测试自己 pin 住的合成内存，因此这些用例稳定、不依赖窗口。</para>
/// </summary>
public class WgcFrameBitmapCopyTests
{
    private const int Width = 2;
    private const int Height = 2;

    /// <summary>行 0 纯红、行 1 纯蓝；行尾填充字节用纯绿做哨兵（正常像素里绝不会出现）。</summary>
    private static byte[] BuildSource(int stride)
    {
        var buffer = new byte[stride * Height];

        // 行 0 = 红（内存序 B,G,R,A = 0,0,255,255），行 1 = 蓝（255,0,0,255）
        FillRow(buffer, stride, row: 0, b: 0, g: 0, r: 255);
        FillRow(buffer, stride, row: 1, b: 255, g: 0, r: 0);

        // 填充区 = 绿哨兵：一旦实现忽略 stride，就会把这些字节当成像素读进来
        for (var y = 0; y < Height; y++)
            for (var offset = y * stride + Width * 4; offset < (y + 1) * stride; offset += 4)
                WritePixel(buffer, offset, b: 0, g: 255, r: 0);

        return buffer;
    }

    private static void FillRow(byte[] buffer, int stride, int row, byte b, byte g, byte r)
    {
        for (var x = 0; x < Width; x++)
            WritePixel(buffer, row * stride + x * 4, b, g, r);
    }

    private static void WritePixel(byte[] buffer, int offset, byte b, byte g, byte r)
    {
        buffer[offset] = b;
        buffer[offset + 1] = g;
        buffer[offset + 2] = r;
        buffer[offset + 3] = 255;   // A
    }

    /// <summary>
    /// 行序必须与原生一致（自上而下，**不做翻转**），且必须按源 stride 取行。
    ///
    /// <para>两种 stride 都测：实测 WGC 给出的是无填充（400 宽 → stride 1600 = 400×4），
    /// 但契约允许有填充，所以两者都得对。</para>
    /// </summary>
    /// <param name="stride">源缓冲每行的字节数。</param>
    [Theory]
    [InlineData(Width * 4)]         // 无填充（实测形态）
    [InlineData(Width * 4 + 4)]     // 有填充（契约允许）
    public unsafe void 保持自上而下行序且按源stride取行(int stride)
    {
        var source = BuildSource(stride);

        fixed (byte* p = source)
        {
            using var bmp = WgcFrameCapture.CopyToBitmap((nint)p, Width, Height, stride);

            Assert.NotNull(bmp);
            Assert.Equal(Width, bmp.Width);
            Assert.Equal(Height, bmp.Height);
            Assert.Equal(PixelFormat.Format32bppArgb, bmp.PixelFormat);

            // 行序：源第 0 行是红 ⇒ 位图第 0 行必须也是红。
            // 若实现照搬 GDI DIB 的习惯做了上下翻转，这两行会整体互换。
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), bmp.GetPixel(0, 0));
            Assert.Equal(Color.FromArgb(255, 0, 0, 255), bmp.GetPixel(0, 1));

            // stride：若实现按 width*4 取行，第 1 行会从错误的偏移读起（读到填充或串行），
            // 颜色就不再是纯红/纯蓝。
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), bmp.GetPixel(1, 0));
            Assert.Equal(Color.FromArgb(255, 0, 0, 255), bmp.GetPixel(1, 1));
        }
    }

    /// <summary>
    /// 交出的位图必须仍然可读 —— 释放责任在调用方（同 <c>CapturedFrame</c> 的契约）。
    /// 若转换函数在返回前把它 Dispose 掉，这里会抛。
    /// </summary>
    [Fact]
    public unsafe void 交出的位图仍可读_释放责任在调用方()
    {
        var source = BuildSource(Width * 4);

        fixed (byte* p = source)
        {
            using var bmp = WgcFrameCapture.CopyToBitmap((nint)p, Width, Height, Width * 4);

            Assert.NotNull(bmp);
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), bmp.GetPixel(0, 0));
        }
    }
}
