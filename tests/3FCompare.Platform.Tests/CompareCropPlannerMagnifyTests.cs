using Avalonia;
using _3FCompare.Platform;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="CompareCropPlanner"/> 的"无缝放大"换算用例（<see cref="CompareCropPlanner.Magnify"/> /
/// <see cref="CompareCropPlanner.FitDestination"/> / <see cref="CompareCropPlanner.MagnifyPlan"/>）。
///
/// <para><b>这一组用例是本次改动的核心正确性依据</b>：换算全在 DIP 里做，纯函数、可离线验算，
/// 而它的错误后果（画面裁错块 / 格内留洞 / 各路露出的不是同一区域）在真机上只表现为
/// "看起来有点不对"，很难归因。故这里把三条性质钉死：</para>
/// <list type="number">
/// <item><description><b>对齐</b>：窗口位置 + 区域 = 格左上 ⇒ 区域在容器坐标下恒等于格（不打洞）；</description></item>
/// <item><description><b>不出界</b>：区域恒在窗口内（否则 <c>SetWindowRgn</c> 会静默裁掉越界部分 ⇒ 格内留洞）；</description></item>
/// <item><description><b>露出的是源画面的 [crop, crop+1/z]</b>：按 letterbox 适配矩形反算 ——
/// 这正是"各路同步放大后显示相同相对位置"的判据。</description></item>
/// </list>
///
/// <para>换算的独立推导与数值验算另见仓库根 <c>.review_pr/verify_magnify.py</c>。</para>
/// </summary>
public class CompareCropPlannerMagnifyTests
{
    private const double Tol = 1e-9;

    // ══════════ FitDestination：与内核 CalculateVideoDestination 同构 ══════════
    //
    // 下面三条直接来自内核源码 VideoRenderer.cpp 的 static_assert（第 872-878 行），
    // 是"我们算出的 letterbox 适配矩形 == 内核实际画出来的矩形"的交叉验证。
    // 若哪天内核改了适配规则，这三条会先失败，而不是等用户看到画面裁错。

    [Fact]
    public void FitDestination_MatchesKernelStaticAssert_LandscapeIntoTallerBox()
    {
        // static_assert(CalculateVideoDestination(1920,1080,1280,1024).width == 1280 &&
        //               ...height == 720 && ...y == 152)
        var fit = CompareCropPlanner.FitDestination(1920, 1080, 1280, 1024);
        Assert.Equal(1280, fit.Width, Tol);
        Assert.Equal(720, fit.Height, Tol);
        Assert.Equal(0, fit.X, Tol);
        Assert.Equal(152, fit.Y, Tol);
    }

    [Fact]
    public void FitDestination_MatchesKernelStaticAssert_PortraitIntoLandscapeBox()
    {
        // static_assert(CalculateVideoDestination(1080,1920,1920,1080).width == 608 && ...x == 656)
        var fit = CompareCropPlanner.FitDestination(1080, 1920, 1920, 1080);
        Assert.Equal(608, fit.Width, Tol);
        Assert.Equal(1080, fit.Height, Tol);
        Assert.Equal(656, fit.X, Tol);
        Assert.Equal(0, fit.Y, Tol);
    }

    [Fact]
    public void FitDestination_SourceSmallerThanWindow_IsUpscaledToFill()
    {
        // 内核的 limitToNativeSize=false（C# 侧从不开启）⇒ 小源也要放大铺满，不做原生尺寸居中。
        // 这一条正是"窗口放大 z 倍 ⇒ 内核真的按更高分辨率重渲染"的前提。
        var fit = CompareCropPlanner.FitDestination(640, 360, 1920, 1080);
        Assert.Equal(1920, fit.Width, Tol);
        Assert.Equal(1080, fit.Height, Tol);
        Assert.Equal(0, fit.X, Tol);
        Assert.Equal(0, fit.Y, Tol);
    }

    [Fact]
    public void FitDestination_SameAspect_FillsWholeWindow()
    {
        var fit = CompareCropPlanner.FitDestination(1920, 1080, 800, 450);
        Assert.Equal(0, fit.X, Tol);
        Assert.Equal(0, fit.Y, Tol);
        Assert.Equal(800, fit.Width, Tol);
        Assert.Equal(450, fit.Height, Tol);
    }

    [Theory]
    [InlineData(0, 0)]     // 演示模式：源尺寸未知
    [InlineData(-1, 1080)] // 脏数据
    public void FitDestination_UnknownSource_FallsBackToWholeWindow(int sw, int sh)
    {
        // 源尺寸未知时唯一安全的假设是"画面铺满窗口"（无黑边），否则会算出一个虚假的偏移
        var fit = CompareCropPlanner.FitDestination(sw, sh, 800, 600);
        Assert.Equal(new Rect(0, 0, 800, 600), fit);
    }

    [Fact]
    public void FitDestination_ZeroOutput_ReturnsEmpty()
    {
        Assert.Equal(default, CompareCropPlanner.FitDestination(1920, 1080, 0, 600));
    }

    // ══════════ Magnify：手算实例（任务书里的公式） ══════════

    [Fact]
    public void Magnify_NoLetterbox_MatchesHandDerivation()
    {
        // 格 (100,50,400,300)，z=2，crop=(0.25,0)，源 8:6=4:3 与格同宽高比 ⇒ 无黑边
        // 手算：窗口 = 格 x2 = (-100, 50, 800, 600)
        //       区域 = (0.25*400*2, 0, 400, 300) = (200, 0, 400, 300)
        // 验算：窗口左上 + 区域左上 = (-100+200, 50+0) = (100, 50) == 格左上 ✓
        var geom = CompareCropPlanner.Magnify(new Rect(100, 50, 400, 300), 2, 0.25, 0, 800, 600);

        Assert.Equal(new Rect(-100, 50, 800, 600), geom.WindowDip);
        Assert.Equal(new Rect(200, 0, 400, 300), geom.RegionDip);
        Assert.Equal(new Rect(0, 0, 800, 600), geom.FitDip);
    }

    [Fact]
    public void Magnify_CropAtMaxCorner_RegionStillInsideWindow()
    {
        // crop = 1 - 1/z = 0.5（z=2）是合法上界：区域右下角恰好贴到窗口右下角
        var geom = CompareCropPlanner.Magnify(new Rect(100, 50, 400, 300), 2, 0.5, 0.5, 800, 600);
        Assert.Equal(new Rect(-300, -250, 800, 600), geom.WindowDip);
        Assert.Equal(new Rect(400, 300, 400, 300), geom.RegionDip);
        AssertRegionFitsWindow(geom);
    }

    [Fact]
    public void Magnify_WithLetterbox_RegionSizeIsFitDividedByZoom()
    {
        // 格 400x300(4:3)、源 16:9、z=2 ⇒ 窗口 800x600，画面 letterbox 成 800x450 @y=75
        // 区域尺寸 = 画面尺寸 / z = 400x225（**不是**格的 400x300）—— 这正是必须做 letterbox 修正的理由：
        // 若用格的尺寸当区域，格内会露出上下黑边之外的多余内容，且各路露出的源区间会不一致。
        var geom = CompareCropPlanner.Magnify(new Rect(0, 0, 400, 300), 2, 0.25, 0, 1920, 1080);

        Assert.Equal(new Rect(0, 75, 800, 450), geom.FitDip);
        Assert.Equal(new Rect(200, 75, 400, 225), geom.RegionDip);
        Assert.Equal(new Rect(-200, -75, 800, 600), geom.WindowDip);
        AssertRegionFitsWindow(geom);
    }

    // ══════════ 三条核心性质（参数化，覆盖各种格/倍数/裁剪/源宽高比） ══════════

    public static TheoryData<Rect, double, double, double, int, int> Cases()
    {
        var data = new TheoryData<Rect, double, double, double, int, int>();
        var cells = new[]
        {
            new Rect(0, 0, 400, 300),
            new Rect(100, 50, 400, 300), // 非零原点：验证"窗口位置 = 格左上 − 区域左上"
            new Rect(0, 0, 500, 600),    // ABC 的通高格
            new Rect(500, 0, 500, 300),  // ABC 的半高格
            new Rect(0, 0, 800, 300),    // 极扁：letterbox 由高度受限
            new Rect(0, 0, 300, 800),    // 极瘦：letterbox 由宽度受限
        };
        var zooms = new[] { 1.5, 2.0, 4.0 };
        var crops = new[] { 0.0, 0.25, 0.75, double.NaN }; // 含越界与 NaN（须被钳制/归零）
        var sources = new[] { (1920, 1080), (0, 0) };                  // 含"源尺寸未知"（演示模式）
        foreach (var cell in cells)
            foreach (var z in zooms)
                foreach (var crop in crops)
                    foreach (var (sw, sh) in sources)
                        data.Add(cell, z, crop, crop, sw, sh);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Magnify_Invariants_HoldForAllCombinations(
        Rect cell, double zoom, double cropX, double cropY, int sw, int sh)
    {
        var geom = CompareCropPlanner.Magnify(cell, zoom, cropX, cropY, sw, sh);

        // 1) 窗口尺寸 == 格尺寸 x z
        Assert.Equal(cell.Width * zoom, geom.WindowDip.Width, 1e-6);
        Assert.Equal(cell.Height * zoom, geom.WindowDip.Height, 1e-6);

        // 2) 区域在容器坐标下恒等于格 ⇒ 格被完整覆盖，不打洞
        Assert.Equal(cell.X, geom.WindowDip.X + geom.RegionDip.X, 1e-6);
        Assert.Equal(cell.Y, geom.WindowDip.Y + geom.RegionDip.Y, 1e-6);

        // 3) 区域恒在窗口内 ⇒ SetWindowRgn 不会静默裁掉越界部分
        AssertRegionFitsWindow(geom);

        // 4) 露出的源画面区间恒为 [crop钳制, crop钳制 + 1/z]
        var maxCrop = CompareCropPlanner.MaxCropFraction(zoom);
        var ex = Math.Clamp(double.IsNaN(cropX) ? 0 : cropX, 0, maxCrop);
        var ey = Math.Clamp(double.IsNaN(cropY) ? 0 : cropY, 0, maxCrop);
        var span = 1.0 / zoom;
        Assert.Equal(ex, (geom.RegionDip.X - geom.FitDip.X) / geom.FitDip.Width, 1e-6);
        Assert.Equal(ex + span, (geom.RegionDip.X + geom.RegionDip.Width - geom.FitDip.X) / geom.FitDip.Width, 1e-6);
        Assert.Equal(ey, (geom.RegionDip.Y - geom.FitDip.Y) / geom.FitDip.Height, 1e-6);
        Assert.Equal(ey + span, (geom.RegionDip.Y + geom.RegionDip.Height - geom.FitDip.Y) / geom.FitDip.Height, 1e-6);

        // 5) 放大倍数：区域里的画面尺寸 = 该路 fit 到窗口时的画面尺寸 / z
        //    （z 倍是相对"格里的画面"而言，不是相对格本身 —— 后者在 letterbox 下不成立）
        Assert.Equal(geom.FitDip.Width / zoom, geom.RegionDip.Width, 1e-6);
        Assert.Equal(geom.FitDip.Height / zoom, geom.RegionDip.Height, 1e-6);
    }

    // ══════════ 裁剪参数钳制 ══════════

    [Theory]
    [InlineData(-0.5, 0.0)]     // 负数 ⇒ 0
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]      // z=2 的合法上界 = 1 - 1/2
    [InlineData(1.5, 0.5)]      // 越界 ⇒ 钳到上界（否则区域会越出窗口 ⇒ 格内留洞）
    [InlineData(double.NaN, 0.0)] // NaN ⇒ 0（Math.Clamp 会原样放行 NaN，必须显式处理）
    public void Magnify_CropIsClampedToValidRange(double crop, double expected)
    {
        // 源与格同宽高比 ⇒ 无黑边，fit == 整个窗口，便于直接读回钳制结果
        var geom = CompareCropPlanner.Magnify(new Rect(0, 0, 400, 300), 2, crop, crop, 400, 300);
        Assert.Equal(expected, (geom.RegionDip.X - geom.FitDip.X) / geom.FitDip.Width, 1e-9);
        Assert.Equal(expected, (geom.RegionDip.Y - geom.FitDip.Y) / geom.FitDip.Height, 1e-9);
        AssertRegionFitsWindow(geom);
    }

    // ══════════ 跨路一致：不同格、同一 (z,crop) ⇒ 同一源画面区间 ══════════

    [Fact]
    public void Magnify_DifferentCellAspects_ExposeSameSourceRegion()
    {
        // ABC 模式：格 A 通高（500x600），格 B 半高（500x300），源 16:9
        // 两者的 letterbox 不同（A 的画面更靠下），但按源画面比例裁剪后必须露出同一区间。
        const double z = 2, crop = 0.25;
        var a = CompareCropPlanner.Magnify(new Rect(0, 0, 500, 600), z, crop, crop, 1920, 1080);
        var b = CompareCropPlanner.Magnify(new Rect(500, 0, 500, 300), z, crop, crop, 1920, 1080);

        Assert.Equal(SourceSpanX(a, crop, z), SourceSpanX(b, crop, z), 1e-9);
        Assert.Equal(SourceSpanY(a, crop, z), SourceSpanY(b, crop, z), 1e-9);
    }

    [Fact]
    public void Magnify_ExtremeCellAspects_WouldDifferWithoutLetterboxCorrection()
    {
        // 反例（证明修正不是可选项）：8:3 的格高度受限、3:8 的格宽度受限，
        // 此时"把格当画面"的朴素换算会让两路露出**不同**的源区间。
        const double z = 2, crop = 0.25;
        var wide = CompareCropPlanner.Magnify(new Rect(0, 0, 800, 300), z, crop, crop, 1920, 1080);
        var tall = CompareCropPlanner.Magnify(new Rect(0, 0, 300, 800), z, crop, crop, 1920, 1080);

        // 修正后仍然一致
        Assert.Equal(SourceSpanX(wide, crop, z), SourceSpanX(tall, crop, z), 1e-9);

        // 朴素换算（区域尺寸取格尺寸、偏移取 crop*格*z）露出的源区间：
        static double NaiveStart(Rect cell, CompareCropPlanner.MagnifyGeometry g, double c, double zz)
            => (c * cell.Width * zz - g.FitDip.X) / g.FitDip.Width;
        var naiveWide = NaiveStart(new Rect(0, 0, 800, 300), wide, crop, z);
        var naiveTall = NaiveStart(new Rect(0, 0, 300, 800), tall, crop, z);
        Assert.NotEqual(naiveWide, naiveTall, 1e-3); // 差异 > 1e-3 ⇒ 朴素换算确实会错
    }

    // ══════════ z ≤ 1：恒等退化（"默认关闭 == 现状"的契约） ══════════

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void Magnify_ZoomNotAboveOne_IsIdentity(double zoom)
    {
        var cell = new Rect(100, 50, 400, 300);
        var geom = CompareCropPlanner.Magnify(cell, zoom, 0.4, 0.4, 1920, 1080);

        Assert.Equal(cell, geom.WindowDip);                     // 窗口 == 格
        Assert.Equal(new Rect(0, 0, 400, 300), geom.RegionDip); // 区域 == 整个窗口（不裁剪）
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.99, false)]
    [InlineData(2.0, true)]
    [InlineData(4.0, true)]
    public void MagnifyPlan_ShouldApplyOnlyWhenZoomed(double zoom, bool expected)
    {
        var plan = CompareCropPlanner.MagnifyPlan(new Rect(100, 50, 400, 300), zoom, 0.25, 0.25, 1920, 1080, 1.0);
        Assert.Equal(expected, plan.ShouldApply);
    }

    [Fact]
    public void MagnifyPlan_ConvertsRegionToPhysicalPixels()
    {
        // 缩放 1.5：格 400x300DIP、z=2、源 16:9 ⇒ 区域 (200,75,400,225) DIP
        // 物理：左 round(300)=300、上 round(112.5)=113、右 round(900)=900、下 round(450)=450
        // ⇒ (300, 113, 600, 337)。注意右/下由端点差得出（而不是对宽度单独取整），
        // 保证 X+Width == Right 不被取整误差破坏。
        var plan = CompareCropPlanner.MagnifyPlan(new Rect(0, 0, 400, 300), 2, 0.25, 0, 1920, 1080, 1.5);
        Assert.True(plan.ShouldApply);
        Assert.Equal(new Rect32(300, 113, 600, 337), plan.Region);
    }

    // ══════════ 工具 ══════════

    /// <summary>性质 3：区域恒在窗口内（含边界贴合），否则 SetWindowRgn 会静默裁掉越界部分。</summary>
    private static void AssertRegionFitsWindow(CompareCropPlanner.MagnifyGeometry g)
    {
        Assert.True(g.RegionDip.X >= -1e-6, $"区域左边越出窗口：{g.RegionDip}");
        Assert.True(g.RegionDip.Y >= -1e-6, $"区域上边越出窗口：{g.RegionDip}");
        Assert.True(g.RegionDip.X + g.RegionDip.Width <= g.WindowDip.Width + 1e-6,
            $"区域右边越出窗口：区域 {g.RegionDip} 窗口宽 {g.WindowDip.Width}");
        Assert.True(g.RegionDip.Y + g.RegionDip.Height <= g.WindowDip.Height + 1e-6,
            $"区域下边越出窗口：区域 {g.RegionDip} 窗口高 {g.WindowDip.Height}");
    }

    private static double SourceSpanX(CompareCropPlanner.MagnifyGeometry g, double crop, double zoom)
        => Math.Round((g.RegionDip.X - g.FitDip.X) / g.FitDip.Width, 9);

    private static double SourceSpanY(CompareCropPlanner.MagnifyGeometry g, double crop, double zoom)
        => Math.Round((g.RegionDip.Y - g.FitDip.Y) / g.FitDip.Height, 9);
}
