using System;
using _3FCompare.Platform;
using Xunit;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="CompareCropPlanner.AnchorCrop"/> 的单测 —— 滚轮"以光标为锚点缩放"的算术核心。
///
/// <para><b>为什么单独一个文件</b>：锚点公式决定"放大时光标下的那一点会不会跑掉"，
/// 是滚轮放大体验里唯一能被离线钉住的性质（光标真实位置在自测里无法精确控制，
/// 真机目视又验不出 1px 级的漂移）。把它抽成纯函数就是为了能在这里穷举。</para>
///
/// <para><b>被测性质（每条都独立手算）</b>：缩放前后，锚点在"格内"的相对位置不变：
/// <c>(u-u0)·z0 == (u-u1)·z1</c>。用例一律按这个等式<b>反解</b>出期望值，
/// 不引用被测实现的写法 —— 实现若把比例因子写成 <c>1/z1</c>（漏了 z0），
/// 从 2× 继续放大时锚点会漂，<c>锚点保持不动_连续放大</c> 立刻判红。</para>
///
/// <para><b>反向验证</b>：实现阶段曾把 <c>z0 / zoomTo</c> 改成 <c>1.0 / zoomTo</c>，
/// <c>锚点保持不动_连续放大</c> 与 <c>从已放大状态继续放大_锚点不动</c> 两条判红
/// （前者 0.375 ≠ 0.25、后者 0.4375 ≠ 0.375）；恢复后转绿。</para>
/// </summary>
public class CompareCropPlannerAnchorTests
{
    private const double Eps = 1e-9;

    /// <summary>从未放大（z=1、crop=0）起放大到 z：锚点 u 处的画面应留在锚点上。
    /// 手算：u1 = u - u/z。u=0.5、z=2 ⇒ 0.25（露出画面正中的一半）。</summary>
    [Theory]
    [InlineData(0.5, 2.0, 0.25)]
    [InlineData(0.5, 4.0, 0.375)]
    [InlineData(0.25, 2.0, 0.125)]
    [InlineData(0.75, 2.0, 0.375)]   // 锚点在右侧：露出 [0.375, 0.875]
    [InlineData(0.0, 2.0, 0.0)]      // 锚点贴左：露出左半
    [InlineData(1.0, 2.0, 0.5)]      // 锚点贴右：露出右半（0.5 = MaxCropFraction(2)）
    public void 锚点保持不动_从一倍起放大(double anchor, double zoomTo, double expected)
    {
        var u1 = CompareCropPlanner.AnchorCrop(anchor, 0.0, 1.0, zoomTo);

        Assert.Equal(expected, u1, precision: 9);
        // 性质形式：缩放前后锚点在露出区间里的相对位置相同
        AssertEqual((anchor - 0.0) * 1.0, (anchor - u1) * zoomTo);
    }

    /// <summary>从<b>已放大</b>状态继续放大（z0 &gt; 1、crop ≠ 0）—— 这一条专门盯着
    /// "漏掉 z0 比例因子"这个错误：只在 z0 == 1 时两种写法结果相同，
    /// 连续滚两格才会暴露。手算：z0=2、u0=0.25、u=0.5、z1=4 ⇒ 0.5-(0.5-0.25)·(2/4)=0.375。</summary>
    [Fact]
    public void 从已放大状态继续放大_锚点不动()
    {
        const double z0 = 2.0, u0 = 0.25, anchor = 0.5, z1 = 4.0;

        var u1 = CompareCropPlanner.AnchorCrop(anchor, u0, z0, z1);

        Assert.Equal(0.375, u1, precision: 9);
        AssertEqual((anchor - u0) * z0, (anchor - u1) * z1);
    }

    /// <summary>连续放大：1 → 2 → 4，锚点必须始终不动（等价于"分两次滚"与"一次滚到位"结果一致
    /// 只在锚点恒定时才成立 —— 这正是要钉的性质）。
    /// 手算：u=0.5：第一次 0.25；第二次 0.5-(0.5-0.25)·(2/4)=0.375。</summary>
    [Fact]
    public void 锚点保持不动_连续放大()
    {
        const double anchor = 0.5;

        var u1 = CompareCropPlanner.AnchorCrop(anchor, 0.0, 1.0, 2.0);
        var u2 = CompareCropPlanner.AnchorCrop(anchor, u1, 2.0, 4.0);

        Assert.Equal(0.25, u1, precision: 9);
        Assert.Equal(0.375, u2, precision: 9);
        // 锚点在屏幕上的相对位置三次都一样
        AssertEqual((anchor - 0.0) * 1.0, (anchor - u1) * 2.0);
        AssertEqual((anchor - 0.0) * 1.0, (anchor - u2) * 4.0);
    }

    /// <summary>缩小同样以锚点为不动点（z0 &gt; z1）。手算：z0=4、u0=0.375、u=0.5、z1=2
    /// ⇒ 0.5-(0.5-0.375)·(4/2)=0.25，正好回到"1→2"那一档的 crop（可逆）。</summary>
    [Fact]
    public void 缩小是放大的逆运算()
    {
        const double anchor = 0.5;

        var up = CompareCropPlanner.AnchorCrop(anchor, 0.0, 1.0, 2.0);
        var up2 = CompareCropPlanner.AnchorCrop(anchor, up, 2.0, 4.0);
        var down = CompareCropPlanner.AnchorCrop(anchor, up2, 4.0, 2.0);

        Assert.Equal(up, down, precision: 9);          // 4→2 回到 1→2 的位置
        Assert.Equal(0.25, down, precision: 9);
    }

    /// <summary>锚点恰在露出区间之外（例如光标落在黑边上被映射到了区间外）时，
    /// 结果会超出合法区间 —— 这里<b>不</b>钳位（钳位是调用方的责任，见方法注释），
    /// 但必须给出<b>有限</b>值：NaN / ∞ 传进 SetWindowRgn 会让窗口整体不可见。</summary>
    [Theory]
    [InlineData(0.9, 0.0, 1.0, 1.05)]   // 放大小于 1.05 倍：crop 略小于 0
    [InlineData(-0.2, 0.0, 1.0, 2.0)]
    [InlineData(1.5, 0.0, 1.0, 2.0)]
    public void 锚点越界时结果仍是有限值(double anchor, double crop, double z0, double z1)
    {
        var u1 = CompareCropPlanner.AnchorCrop(anchor, crop, z0, z1);

        Assert.False(double.IsNaN(u1));
        Assert.False(double.IsInfinity(u1));
    }

    /// <summary>退化入参不得产出 NaN：z0 ≤ 1 一律按 1 处理（未放大时露出整幅，crop 恒为 0），
    /// zoomTo ≤ 0 视为非法 ⇒ 原样返回当前 crop（调用方随后不会拿它去开窗口）。</summary>
    [Theory]
    [InlineData(0.0, 0.25, 1.0, 2.0)]
    [InlineData(-3.0, 0.25, 1.0, 2.0)]
    [InlineData(0.5, 0.25, 1.0, 0.0)]    // zoomTo 非法
    [InlineData(0.5, 0.25, 1.0, -1.0)]
    public void 退化入参不产出NaN(double anchor, double crop, double z0, double z1)
    {
        var u1 = CompareCropPlanner.AnchorCrop(anchor, crop, z0, z1);

        Assert.False(double.IsNaN(u1));
        if (!(z1 > 0)) Assert.Equal(crop, u1, precision: 12);
    }

    /// <summary>NaN 锚点（MapPointerToVideoPixel 取不到源坐标时的兜底路径）必须落回 0，
    /// 而不是把 NaN 传下去 —— NaN 矩形会让 SetWindowRgn 收到翻转矩形（窗口整体不可见）。</summary>
    [Fact]
    public void NaN锚点返回零()
    {
        Assert.Equal(0.0, CompareCropPlanner.AnchorCrop(double.NaN, 0.25, 2.0, 4.0), precision: 12);
        Assert.Equal(0.0, CompareCropPlanner.AnchorCrop(0.5, double.NaN, 2.0, 4.0), precision: 12);
    }

    private static void AssertEqual(double a, double b) =>
        Assert.True(Math.Abs(a - b) <= 1e-9, $"锚点相对位置不等：{a} vs {b}");
}
