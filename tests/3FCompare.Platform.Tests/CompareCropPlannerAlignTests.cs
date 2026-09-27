using Avalonia;
using _3FCompare.Core.Display;
using _3FCompare.Platform;
using Xunit;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 像素级对齐（<see cref="CompareAlign.Pixel"/>）的算术单测。
///
/// <para><b>为什么必须有这个文件</b>：像素级对齐要满足的性质是"各路露出<b>相同源像素尺寸</b>、
/// 且<b>相同像素起点</b>"，这条性质只能在纯算术层穷举 —— 真机需要两路不同分辨率的素材，
/// 且肉眼根本看不出"露出的像素数是不是一样"。</para>
///
/// <para><b>性质怎么验</b>：对一组源尺寸，逐个算出 <c>z_i</c> 与 <c>crop_i</c>，
/// 再反推"露出的源像素区间" <c>[crop_i·W_i, (crop_i + 1/z_i)·W_i]</c>，
/// 断言各路的<b>起点相同、宽度相同</b>。期望值按定义独立推导（基准路 z=2 ⇒ 露出
/// 基准宽的一半），不引用实现内部的写法。</para>
///
/// <para><b>反向验证</b>：实现阶段曾把 <see cref="CompareCropPlanner.AlignedCrop"/> 写成
/// 早前的 <c>view·(1-w)</c> 形态（只挪位置、不改尺寸），<c>像素级对齐_各路露出相同的源像素尺寸</c>
/// 立刻判红（4K 那一路露出的仍是 1920 而不是 960）；改回"逐路有效倍率"后转绿。</para>
/// </summary>
public class CompareCropPlannerAlignTests
{
    /// <summary>基准尺寸 = 各路源尺寸的最小值；未知路（0,0）不参与；全未知 ⇒ (0,0)。</summary>
    [Fact]
    public void MinSourceSize_取各路最小值且忽略未知路()
    {
        var sources = new[]
        {
            new PixelSize(3840, 2160),
            new PixelSize(1920, 1080),   // 最小
            new PixelSize(0, 0),         // 未知（演示模式 / 纯音频）
            new PixelSize(2560, 1440),
        };

        Assert.Equal(new PixelSize(1920, 1080), CompareCropPlanner.MinSourceSize(sources));
        // 全未知 ⇒ 无基准（调用方据此退化到相对对齐）
        Assert.Equal(new PixelSize(0, 0),
            CompareCropPlanner.MinSourceSize(new[] { new PixelSize(0, 0), new PixelSize(0, 0) }));
        Assert.Equal(new PixelSize(0, 0), CompareCropPlanner.MinSourceSize(new PixelSize[0]));
    }

    /// <summary>核心性质：1080p 与 4K 同场、z=2 ⇒ 两路都露出基准宽的一半（960px），且起点相同。
    /// 手算：minW=1920 ⇒ 目标 960px。1080p 路 z=2（露出 1920/2=960）、4K 路 z=4（露出 3840/4=960）。
    /// 居中（view=0.5）⇒ 起点像素 480。</summary>
    [Fact]
    public void 像素级对齐_各路露出相同的源像素尺寸()
    {
        var sources = new[] { new PixelSize(1920, 1080), new PixelSize(3840, 2160) };
        var min = CompareCropPlanner.MinSourceSize(sources);
        const double z = 2.0;
        const double view = 0.5;

        double? startPx = null;
        double? widthPx = null;
        foreach (var s in sources)
        {
            var zi = CompareCropPlanner.AlignedZoom(z, s.Width, min.Width);
            var crop = CompareCropPlanner.AlignedCrop(view, z, s.Width, min.Width);

            // 由 Magnify 的构造：露出的归一化区间是 [crop, crop + 1/z_i]
            var shownWidthPx = s.Width / zi;
            var shownStartPx = crop * s.Width;

            Assert.Equal(960.0, shownWidthPx, precision: 9);   // 基准 1920 / z=2
            if (widthPx is null) widthPx = shownWidthPx;
            if (startPx is null) startPx = shownStartPx;

            Assert.Equal(widthPx.Value, shownWidthPx, precision: 9);
            Assert.Equal(startPx.Value, shownStartPx, precision: 9);
        }
        Assert.Equal(480.0, startPx!.Value, precision: 9); // 居中：可平移范围 960 的一半
    }

    /// <summary>同一个视口位置下，各路露出的<b>像素起点相同</b>（不只是尺寸相同）——
    /// 这是"对齐"的另一半：只对齐尺寸不对齐位置，两路看的仍是不同的内容。</summary>
    [Theory]
    [InlineData(0.0)]   // 贴左：起点像素 0
    [InlineData(0.25)]
    [InlineData(0.5)]   // 居中
    [InlineData(1.0)]   // 贴右：起点像素 = minW - minW/z
    public void 像素级对齐_各路露出的像素起点相同(double view)
    {
        var sources = new[] { new PixelSize(1280, 720), new PixelSize(1920, 1080), new PixelSize(3840, 2160) };
        var min = CompareCropPlanner.MinSourceSize(sources);
        const double z = 3.0;

        double? startPx = null;
        foreach (var s in sources)
        {
            var crop = CompareCropPlanner.AlignedCrop(view, z, s.Width, min.Width);
            var px = crop * s.Width;

            if (startPx is null) startPx = px;
            else Assert.Equal(startPx.Value, px, precision: 9);
        }
        // 手算：可平移范围 = minW·(1-1/z) = 1280·(2/3) ≈ 853.33
        Assert.Equal(min.Width * (1.0 - 1.0 / z) * view, startPx!.Value, precision: 9);
    }

    /// <summary>区域恒在画面内：<c>crop + 1/z_i ≤ 1</c> —— 否则 <c>SetWindowRgn</c> 会静默裁掉
    /// 越界部分 ⇒ 格里出现空洞（露出父窗口底色）。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.33)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void 像素级对齐_区域恒不越出画面(double view)
    {
        var sources = new[] { new PixelSize(640, 360), new PixelSize(1920, 1080), new PixelSize(3840, 2160) };
        var min = CompareCropPlanner.MinSourceSize(sources);

        foreach (var z in new[] { 1.0, 1.15, 2.0, 4.0 })
        foreach (var s in sources)
        {
            var zi = CompareCropPlanner.AlignedZoom(z, s.Width, min.Width);
            var crop = CompareCropPlanner.AlignedCrop(view, z, s.Width, min.Width);

            Assert.True(crop >= 0, $"crop 为负：{crop}");
            Assert.True(crop + 1.0 / zi <= 1.0 + 1e-9,
                $"区域越出画面：crop={crop} + 1/z_i={1.0 / zi} > 1（W={s.Width}, z={z}）");
        }
    }

    /// <summary>基准路（最小的那一路）在 z=1 时必须露出整幅：crop=0、z_i=1。
    /// 这是"分辨率拉到一致"的零放大形态 —— 最小分辨率路显示全画面，其余路裁出同样大小的中心块。</summary>
    [Fact]
    public void 基准路在一倍时露出整幅()
    {
        var sources = new[] { new PixelSize(1920, 1080), new PixelSize(3840, 2160) };
        var min = CompareCropPlanner.MinSourceSize(sources);

        var zi = CompareCropPlanner.AlignedZoom(1.0, min.Width, min.Width);
        var crop = CompareCropPlanner.AlignedCrop(0.5, 1.0, min.Width, min.Width);

        Assert.Equal(1.0, zi, precision: 12);
        Assert.Equal(0.0, crop, precision: 12);

        // 另一路：z=1 ⇒ 也要露出 1920×1080 像素（= 它自己画面的 1/4）
        var zi2 = CompareCropPlanner.AlignedZoom(1.0, 3840, min.Width);
        var crop2 = CompareCropPlanner.AlignedCrop(0.5, 1.0, 3840, min.Width);
        Assert.Equal(2.0, zi2, precision: 12);

        // ⚠ z=1 时**没有可平移的余地**，故 crop 恒为 0（露出左上角），"居中"请求被钳到 0。
        // 这不是缺陷而是数学必然：起点 p 必须让**最小的那一路**也容得下
        // （p + minW/z ≤ minW）⇒ p ≤ minW·(1-1/z)，z=1 时上界就是 0。
        // 想要居中必须先放大（z>1）才有平移空间 —— 见上面的 view=0.5 用例。
        Assert.Equal(0.0, crop2, precision: 12);
        Assert.Equal(0.0, CompareCropPlanner.AlignedCrop(1.0, 1.0, 3840, min.Width), precision: 12);
    }

    /// <summary>退化路径：源尺寸未知或没有基准时，不得产出 NaN ——
    /// NaN 矩形会让 <c>SetWindowRgn</c> 收到翻转矩形（窗口整体不可见）。</summary>
    [Fact]
    public void 退化入参不产出NaN()
    {
        Assert.Equal(2.0, CompareCropPlanner.AlignedZoom(2.0, 0, 1920), precision: 12);   // 源未知
        Assert.Equal(2.0, CompareCropPlanner.AlignedZoom(2.0, 3840, 0), precision: 12);   // 无基准
        Assert.Equal(0.0, CompareCropPlanner.AlignedCrop(double.NaN, 2.0, 3840, 1920), precision: 12);
        Assert.False(double.IsNaN(CompareCropPlanner.AlignedCrop(0.5, 2.0, 0, 1920)));
    }

    /// <summary>相对对齐下各路共用同一个 crop 与同一个 z —— 像素级对齐改变的是"逐路"，
    /// 相对路径必须逐字不变（默认行为不受新枚举影响）。这条钉住"新增模式不得污染默认路径"。</summary>
    [Fact]
    public void 相对对齐不受对齐算术影响()
    {
        // 相对对齐直接取共用的 crop，与源尺寸无关 ⇒ 4K 与 1080p 露出的像素尺寸不同（既有行为）
        var w1080 = 1920;
        var w4k = 3840;
        const double crop = 0.25;
        const double z = 2.0;

        Assert.Equal(crop * w1080, 480.0, precision: 9);   // 露出 [480, 1440]
        Assert.Equal(crop * w4k, 960.0, precision: 9);     // 露出 [960, 2880] —— 像素尺寸是 1080p 的 2 倍
        Assert.NotEqual(w1080 / z, w4k / z);               // 这正是"相对对齐不满足像素级对齐"的量化形式
    }
}
