using _3FCompare.Platform;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="WindowRegionClipper"/> 的纯逻辑用例。
///
/// 边界说明：Win32 调用（CreateRectRgn / SetWindowRgn）本身无法在单测里验证真伪，
/// 本文件刻意<b>只</b>覆盖两类可验证行为：
///   1) 纯几何/校验逻辑（<see cref="Rect32"/>、<see cref="WindowRegionClipper.NormalizeRects"/>）；
///   2) 在触碰 Win32 <b>之前</b>就应短路返回 false 的守卫分支（句柄为零、宽高 ≤ 0、坐标溢出）——
///      这些分支不产生任何 P/Invoke 副作用，因此可以安全断言。
/// 真实的裁剪效果（尤其对 D3D11 flip-model 子窗口是否生效）必须走应用内实测。
///
/// <para><b>守卫分支的句柄必须非零</b>（<see cref="FakeHandle"/>）：零句柄会被第一道守卫拦下，
/// 用例就永远执行不到 <c>TryGetEdges</c> / <c>NormalizeRects</c>，注释声称的覆盖便是假的。</para>
/// </summary>
public class WindowRegionClipperTests
{
    // ---- Rect32.IsValid：宽或高 ≤ 0 ----

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(640, 360, true)]
    [InlineData(0, 360, false)]   // 宽为 0 ⇒ CreateRectRgn 得到空区域
    [InlineData(640, 0, false)]   // 高为 0
    [InlineData(-1, 360, false)]  // 负宽
    [InlineData(640, -1, false)]  // 负高
    [InlineData(0, 0, false)]
    [InlineData(-5, -5, false)]
    public void IsValid_OnlyTrueForPositiveSize(int width, int height, bool expected)
    {
        var rect = new Rect32(0, 0, width, height);
        Assert.Equal(expected, rect.IsValid);
    }

    // ---- Rect32.TryGetEdges：尺寸校验 + 溢出拒绝 ----

    [Fact]
    public void TryGetEdges_NormalRect_ReturnsEdges()
    {
        var rect = new Rect32(10, 20, 30, 40);
        Assert.True(rect.TryGetEdges(out var left, out var top, out var right, out var bottom));
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(40, right);
        Assert.Equal(60, bottom);
    }

    [Fact]
    public void TryGetEdges_NegativeOrigin_IsAllowed()
    {
        // 负坐标表示矩形落在窗口左上角之外，是合法区域坐标，不应被拒绝
        var rect = new Rect32(-100, -50, 30, 40);
        Assert.True(rect.TryGetEdges(out var left, out var top, out var right, out var bottom));
        Assert.Equal(-100, left);
        Assert.Equal(-50, top);
        Assert.Equal(-70, right);
        Assert.Equal(-10, bottom);
    }

    [Fact]
    public void TryGetEdges_DegenerateSize_IsRejected()
    {
        Assert.False(new Rect32(0, 0, 0, 10).TryGetEdges(out _, out _, out _, out _));
        Assert.False(new Rect32(0, 0, 10, 0).TryGetEdges(out _, out _, out _, out _));
        Assert.False(new Rect32(0, 0, -10, -10).TryGetEdges(out _, out _, out _, out _));
    }

    [Fact]
    public void TryGetEdges_OverflowingRightEdge_IsRejected()
    {
        // X + Width 溢出 int 会让 CreateRectRgn 收到翻转的矩形（被当成空区域），
        // 现象是"调用成功但窗口不可见"，因此必须在换算阶段就拒绝。
        var rect = new Rect32(int.MaxValue, 0, 10, 10);
        Assert.False(rect.TryGetEdges(out _, out _, out _, out _));
    }

    [Fact]
    public void TryGetEdges_OverflowingBottomEdge_IsRejected()
    {
        var rect = new Rect32(0, int.MaxValue, 10, 10);
        Assert.False(rect.TryGetEdges(out _, out _, out _, out _));
    }

    [Fact]
    public void TryGetEdges_MaxNonOverflowingRect_IsAccepted()
    {
        var rect = new Rect32(int.MaxValue - 1, int.MaxValue - 1, 1, 1);
        Assert.True(rect.TryGetEdges(out var left, out var top, out var right, out var bottom));
        Assert.Equal(int.MaxValue - 1, left);
        Assert.Equal(int.MaxValue - 1, top);
        Assert.Equal(int.MaxValue, right);
        Assert.Equal(int.MaxValue, bottom);
    }

    // ---- Rect32.Contains ----

    [Fact]
    public void Contains_TrueForInnerAndEqualRect_FalseOtherwise()
    {
        var outer = new Rect32(0, 0, 100, 100);
        Assert.True(outer.Contains(new Rect32(10, 10, 50, 50)));  // 完全在内
        Assert.True(outer.Contains(new Rect32(0, 0, 100, 100)));  // 完全重合
        Assert.False(outer.Contains(new Rect32(50, 50, 100, 100))); // 右下越界
        Assert.False(outer.Contains(new Rect32(-10, 0, 50, 50)));   // 左上越界
        Assert.False(outer.Contains(new Rect32(0, 0, 101, 100)));   // 宽超一点
    }

    [Fact]
    public void Contains_DegenerateRects_AreNeverContained()
    {
        var outer = new Rect32(0, 0, 100, 100);
        Assert.False(outer.Contains(new Rect32(10, 10, 0, 50)));
        Assert.False(new Rect32(0, 0, 0, 0).Contains(new Rect32(0, 0, 0, 0)));
    }

    [Fact]
    public void Contains_DoesNotOverflowOnExtremeCoordinates()
    {
        // 若用 int 相加，int.MaxValue + 10 会回绕成负数，把"越界"误判成"被包含"
        var outer = new Rect32(0, 0, int.MaxValue, int.MaxValue);
        Assert.False(outer.Contains(new Rect32(10, 10, int.MaxValue, 10)));
    }

    // ---- NormalizeRects：过滤 + 去覆盖 ----

    [Fact]
    public void NormalizeRects_NullOrEmpty_ReturnsEmptyArray()
    {
        Assert.Empty(WindowRegionClipper.NormalizeRects(null));
        Assert.Empty(WindowRegionClipper.NormalizeRects(Array.Empty<Rect32>()));
    }

    [Fact]
    public void NormalizeRects_DropsDegenerateRects()
    {
        var result = WindowRegionClipper.NormalizeRects(new[]
        {
            new Rect32(0, 0, 100, 100),
            new Rect32(5, 5, 0, 50),    // 宽 0
            new Rect32(5, 5, 50, 0),    // 高 0
            new Rect32(5, 5, -10, 50),  // 负宽
        });

        Assert.Single(result);
        Assert.Equal(new Rect32(0, 0, 100, 100), result[0]);
    }

    [Fact]
    public void NormalizeRects_DropsRectCoveredByEarlierRect()
    {
        // 被分割线切成的"小块落在大块里"是常见的调用方失误，留着只会让区域多一个冗余环
        var result = WindowRegionClipper.NormalizeRects(new[]
        {
            new Rect32(0, 0, 100, 100),
            new Rect32(10, 10, 20, 20),   // 被上一个覆盖
            new Rect32(0, 0, 100, 100),   // 完全重复
        });

        Assert.Single(result);
        Assert.Equal(new Rect32(0, 0, 100, 100), result[0]);
    }

    [Fact]
    public void NormalizeRects_LaterLargerRectRemovesEarlierSmallerOne()
    {
        var result = WindowRegionClipper.NormalizeRects(new[]
        {
            new Rect32(10, 10, 20, 20),
            new Rect32(0, 0, 100, 100),
        });

        Assert.Single(result);
        Assert.Equal(new Rect32(0, 0, 100, 100), result[0]);
    }

    [Fact]
    public void NormalizeRects_KeepsDisjointRectsInInputOrder()
    {
        // 被竖直分割线切成左右两块：互不包含，两块都要留
        var left = new Rect32(0, 0, 40, 100);
        var right = new Rect32(60, 0, 40, 100);

        var result = WindowRegionClipper.NormalizeRects(new[] { left, right });

        Assert.Equal(2, result.Length);
        Assert.Equal(left, result[0]);
        Assert.Equal(right, result[1]);
    }

    [Fact]
    public void NormalizeRects_KeepsPartiallyOverlappingRects()
    {
        // 部分重叠不做布尔并集（那会把区域变成非矩形），两者都保留
        var result = WindowRegionClipper.NormalizeRects(new[]
        {
            new Rect32(0, 0, 60, 60),
            new Rect32(40, 40, 60, 60),
        });

        Assert.Equal(2, result.Length);
    }

    // ---- 守卫分支：句柄为零 / 尺寸非法时必须在触碰 Win32 之前短路返回 false ----
    // （这些断言不会产生任何 P/Invoke 副作用，可安全在单测中运行）

    /// <summary>非零"假"句柄，用于验证"在触碰 Win32 <b>之前</b>就短路"的守卫分支。
    ///
    /// <para><b>为什么不能用 <see cref="nint.Zero"/></b>：<c>ApplyRect</c>/<c>ApplyRects</c> 的第一道
    /// 守卫就是零句柄，传零会让用例在<b>第一个 if</b> 处返回，根本执行不到
    /// <c>TryGetEdges</c> / <c>NormalizeRects</c> —— 注释声称的覆盖是假的。
    /// 而被测的这几条分支（尺寸非正、坐标溢出、归一化后为空）在调用任何 P/Invoke 之前就返回，
    /// 所以传一个非零假值不会真的去操作窗口，是安全且真正覆盖到目标代码的。</para></summary>
    private static readonly nint FakeHandle = (nint)1;

    [Fact]
    public void ApplyRect_ZeroHandle_ReturnsFalse()
    {
        Assert.False(WindowRegionClipper.ApplyRect(nint.Zero, 0, 0, 100, 100));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void ApplyRect_NonPositiveSize_ReturnsFalse(int width, int height)
    {
        // 尺寸非法时同样在 CreateRectRgn 之前返回，不留悬空句柄。
        // 用非零句柄，确保真的走到 TryGetEdges 的尺寸校验分支。
        Assert.False(WindowRegionClipper.ApplyRect(FakeHandle, 0, 0, width, height));
    }

    [Fact]
    public void ApplyRect_OverflowingEdges_ReturnsFalse()
    {
        // 非零句柄 + 溢出坐标：必须在 TryGetEdges 的溢出校验处被拒绝，而不是落到 CreateRectRgn
        Assert.False(WindowRegionClipper.ApplyRect(FakeHandle, int.MaxValue, 0, 10, 10));
        Assert.False(WindowRegionClipper.ApplyRect(FakeHandle, 0, int.MaxValue, 10, 10));
    }

    [Fact]
    public void ApplyRects_ZeroHandle_ReturnsFalse()
    {
        Assert.False(WindowRegionClipper.ApplyRects(nint.Zero, new[] { new Rect32(0, 0, 10, 10) }));
    }

    [Fact]
    public void ApplyRects_NoUsableRects_ReturnsFalse()
    {
        // 归一化后为空 ⇒ 返回 false 且不改变当前区域（要恢复整窗请显式调 Clear）。
        // 用非零句柄，确保真的走到 NormalizeRects 的空结果分支，而不是被零句柄守卫拦下。
        Assert.False(WindowRegionClipper.ApplyRects(FakeHandle, null));
        Assert.False(WindowRegionClipper.ApplyRects(FakeHandle, Array.Empty<Rect32>()));
        Assert.False(WindowRegionClipper.ApplyRects(FakeHandle, new[] { new Rect32(0, 0, 0, 0) }));
        Assert.False(WindowRegionClipper.ApplyRects(FakeHandle, new[] { new Rect32(0, 0, -1, 5) }));
    }

    [Fact]
    public void Clear_ZeroHandle_ReturnsFalse()
    {
        Assert.False(WindowRegionClipper.Clear(nint.Zero));
    }
}
