namespace _3FCompare.Platform.Tests;

/// <summary>
/// 探针读数的分岔点：<see cref="MainWindow.ProbePointOrSentinel"/>。
///
/// <para><b>这条判据拦的是什么</b>：历史缺陷（P1-5）是"路还没开 / 会话已释放"时探针把表面内的
/// DIP 坐标当<b>源像素下标</b>显示 —— 数字看着完全合法、随窗口尺寸变而与片源无关，用户会照着
/// 一个不存在的行列去比对。原来的写法有两条分支各自决定"报什么"，所以要伪造 Avalonia 指针事件
/// 才能跑到可疑的那条，等于没法判（§11.15 挂的 #15）。合成一条路径后，非哨兵值只能来自
/// <c>MapPointerToVideoPixel</c>（它本身要求有会话），这里要钉的就是：
/// <b>没有映射结果时，唯一可能的输出是哨兵 (-1,-1)</b>。</para>
/// </summary>
public class ProbePointSentinelTests
{
    [Fact]
    public void NoMapping_YieldsSentinel_NotAPlausibleCoordinate()
    {
        Assert.Equal((-1, -1), MainWindow.ProbePointOrSentinel(null));
    }

    [Fact]
    public void MappedPoint_PassesThroughUntouched()
    {
        Assert.Equal((640, 360), MainWindow.ProbePointOrSentinel((640, 360)));
    }

    /// <summary>源坐标 (0,0) 是<b>合法像素</b>，绝不能被当成"没取到"。
    /// 这一档存在是因为同类判断常被写成真值测试（<c>if (x != 0)</c>）——
    /// 只测上面两条的话，把 0 当缺席的那种写法照样全绿。</summary>
    [Fact]
    public void SourceOrigin_IsLegitimateCoordinate_NotTreatedAsAbsent()
    {
        Assert.Equal((0, 0), MainWindow.ProbePointOrSentinel((0, 0)));
        Assert.Equal((0, -1), MainWindow.ProbePointOrSentinel((0, -1)));
        Assert.Equal((-1, 0), MainWindow.ProbePointOrSentinel((-1, 0)));
    }

    /// <summary>哨兵口径只有一条：负一。若哪天改成 <c>(0,0)</c> 当哨兵，
    /// 与"源画面左上角那个真实像素"就再也分不开了 —— 这条把口径本身钉住。</summary>
    [Fact]
    public void SentinelIsNegativeOne_BecauseZeroIsARealPixel()
    {
        var (x, y) = MainWindow.ProbePointOrSentinel(null);
        Assert.Equal(-1, x);
        Assert.Equal(-1, y);
        Assert.NotEqual((0, 0), (x, y));
    }
}
