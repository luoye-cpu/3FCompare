using _3FCompare.Core.Backend;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// SimulatedEngine 像素回读边界测试。
/// 验证修复项：TryReadPixelRegion 必须拒绝容量不足的缓冲区（与真实引擎一致），
/// 且对非 4 倍长度缓冲区不越界写。
/// </summary>
public sealed class SimulatedEnginePixelRegionTests
{
    private static IPlayerSession CreateSession() =>
        new SimulatedEngine().CreateSession(new EngineSessionOptions());

    [Fact]
    public void TryReadPixelRegion_RejectsUndersizedBuffer()
    {
        using var session = CreateSession();
        var buffer = new float[3 * 3 * 4 - 1]; // 少 1 个 float

        var ok = session.TryReadPixelRegion(0, 0, 3, 3, buffer, out var bitDepth);

        Assert.False(ok);
        Assert.Equal(8u, bitDepth);
    }

    [Fact]
    public void TryReadPixelRegion_FillsExactSizedBuffer()
    {
        using var session = CreateSession();
        var buffer = new float[2 * 2 * 4];

        var ok = session.TryReadPixelRegion(0, 0, 2, 2, buffer, out _);

        Assert.True(ok);
        Assert.Equal(0.5f, buffer[0]);
        Assert.Equal(1f, buffer[3]);
        Assert.Equal(0.5f, buffer[^4]);
        Assert.Equal(1f, buffer[^1]);
    }

    [Fact]
    public void TryReadPixelRegion_ToleratesNonMultipleOfFourLength()
    {
        using var session = CreateSession();
        // 容量足够但长度非 4 倍：只允许写完整像素，不得越界
        var buffer = new float[4 * 4 + 2];

        var ok = session.TryReadPixelRegion(0, 0, 2, 2, buffer, out _);

        Assert.True(ok);
        Assert.Equal(0f, buffer[16]); // 尾部多余槽位不被触碰
        Assert.Equal(0f, buffer[17]);
    }
}
