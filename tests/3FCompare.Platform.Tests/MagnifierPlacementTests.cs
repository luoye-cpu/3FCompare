using Avalonia;
using _3FCompare.Controls;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// #22 放大镜浮窗摆放的**算术**部分（<see cref="MagnifierOverlay.PlaceOverlay"/>）用例。
///
/// <para><b>覆盖边界（诚实说明）</b>：本文件钉住的是"右下优先 → 越界翻到左上 → 钳到容器内"
/// 这三条规则本身。而 #22 的真实缺陷是**坐标系用错**（把 surface 局部坐标当成容器坐标），
/// 它发生在 <c>UpdateAt</c> 的 <c>TranslatePoint</c> 换算处、依赖视觉树变换链，
/// 离线造不出等价链路 ⇒ 由 <c>--selftest</c> / <c>--comparemodetest</c> 的
/// <c>AssertMagnifierFollowsCursorAsync</c> 覆盖（多格布局下被测路原点与容器原点差数百 DIP）。
/// 本文件的作用是：把 <c>PlaceOverlay</c> 抽成纯函数时**行为与改动前逐字一致**这件事钉死，
/// 避免"顺手改视觉"把浮窗推出可视区。</para>
/// </summary>
public class MagnifierPlacementTests
{
    private const double W = MagnifierOverlay.WidthPx;   // 192
    private const double H = MagnifierOverlay.HeightPx;  // 144

    private static Point Place(double cx, double cy, double cw, double ch)
        => MagnifierOverlay.PlaceOverlay(new Point(cx, cy), new Size(cw, ch));

    [Fact]
    public void 光标在左上_浮窗落在光标右下方()
    {
        var p = Place(100, 100, 1000, 800);
        Assert.Equal(116, p.X, 1e-9);   // +16 偏移
        Assert.Equal(116, p.Y, 1e-9);
        // 不越界：右下角仍在容器内
        Assert.True(p.X + W <= 1000 && p.Y + H <= 800);
    }

    [Fact]
    public void 贴右边界_翻到光标左侧()
    {
        var p = Place(900, 100, 1000, 800);
        Assert.Equal(900 - W - 8, p.X, 1e-9);   // 翻转为 -W-8（既有取值，不改视觉）
        Assert.Equal(116, p.Y, 1e-9);           // Y 不受影响
        Assert.True(p.X + W <= 1000);
    }

    [Fact]
    public void 贴下边界_翻到光标上方()
    {
        var p = Place(100, 700, 1000, 800);
        Assert.Equal(116, p.X, 1e-9);
        Assert.Equal(700 - H - 8, p.Y, 1e-9);
        Assert.True(p.Y + H <= 800);
    }

    [Fact]
    public void 右下角_两个方向同时翻转()
    {
        var p = Place(900, 700, 1000, 800);
        Assert.Equal(700, p.X, 1e-9);
        Assert.Equal(548, p.Y, 1e-9);
    }

    [Fact]
    public void 恰好贴边_不翻转()
    {
        // 判据是 `>` 而不是 `>=`：右边界正好落在容器边上时不翻转（保持右下优先）。
        var p = Place(1000 - W - 16, 800 - H - 16, 1000, 800);
        Assert.Equal(1000 - W, p.X, 1e-9);
        Assert.Equal(800 - H, p.Y, 1e-9);
    }

    [Fact]
    public void 容器比浮窗还小_钳到零而不是负坐标()
    {
        // 负坐标会让浮窗整块滑出可视区（用户看到的仍是"放大镜不显示"）。
        var p = Place(10, 10, 100, 100);
        Assert.Equal(0, p.X, 1e-9);
        Assert.Equal(0, p.Y, 1e-9);
    }

    [Fact]
    public void 容器无穷大_只做偏移不做钳制()
    {
        var p = Place(5000, 5000, double.PositiveInfinity, double.PositiveInfinity);
        Assert.Equal(5016, p.X, 1e-9);
        Assert.Equal(5016, p.Y, 1e-9);
    }

    [Fact]
    public void 任何输入下_浮窗都不越出容器()
    {
        // 属性式抽查：把光标扫过容器（含越界点），浮窗矩形必须始终落在容器内。
        var size = new Size(640, 480);
        for (var cx = -50.0; cx <= 700; cx += 25)
        for (var cy = -50.0; cy <= 520; cy += 25)
        {
            var p = MagnifierOverlay.PlaceOverlay(new Point(cx, cy), size);
            Assert.InRange(p.X, 0.0, size.Width - W);
            Assert.InRange(p.Y, 0.0, size.Height - H);
        }
    }
}
