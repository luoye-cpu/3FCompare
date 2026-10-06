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
///
/// <para><b>第二类用例：光标格标记（<see cref="MagnifierOverlay.TryCursorCell"/>）</b>。
/// 采样窗口的左上角在 <c>RefreshPixels</c> 里被 <c>Math.Clamp</c> 过，贴边时窗口整体滑离
/// "以光标为中心"的位置 —— 光标像素仍在窗口里，但不再落在中心格 (6,6)。这组用例钉的是
/// "标记格 = 光标物理坐标 − 窗口左上角"，其中贴边两例正是"硬标中心格"的写法会红的地方。</para>
///
/// <para><b>覆盖边界（诚实说明）</b>：<c>TryCursorCell</c> 是纯映射，它钉住了"哪一格该被描边"
/// 的算术，但钉不住 <c>RefreshPixels</c> **何时**把光标坐标写进字段——光标在同一窗口内移动时
/// 不回读（节流 / "网格未移动"两处提前返回），坐标若只在回读路径上记，标记格就钉死在旧格。
/// 该顺序由"写坐标是本方法第一段语句"这一结构保证（见那里的 ⚠ 注释），
/// 要跑通它需要活的会话与 GPU 回读，只能由 <c>--selftest</c> 的实机断言覆盖；
/// <c>同一窗口内光标移动_标记格随光标换格</c> 只把这条依赖的<b>契约</b>写成了用例。</para>
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

    // ────────────────── 光标格标记（TryCursorCell）──────────────────

    private const int Grid = 12;   // = MagnifierOverlay.ZoomGrid（私有常数，这里按出货值取）

    /// <summary>RefreshPixels 里采样窗口左上角的算式，在测试里<b>独立</b>复刻一份当参照。
    ///
    /// <para>那段算式嵌在 GPU 回读与节流判据之间，离线拿不到 (gx,gy)。复刻的代价是"两边都得改"，
    /// 换来的是夹具能同时验算<b>钳制</b>与<b>映射</b>；用例一律按行为断言（贴边时标记格必须
    /// 不是中心格），而不是比对两份代码是否同字——同字比对照抄不出真实缺陷。</para></summary>
    private static (int Left, int Top) SampleWindow(int cursorX, int cursorY, int swapW, int swapH)
    {
        var half = Grid / 2;
        return (Math.Clamp(cursorX - half, 0, Math.Max(0, swapW - Grid)),
                Math.Clamp(cursorY - half, 0, Math.Max(0, swapH - Grid)));
    }

    private static void AssertCell((int Col, int Row)? cell, int col, int row)
    {
        Assert.NotNull(cell);
        Assert.Equal(col, cell!.Value.Col);
        Assert.Equal(row, cell!.Value.Row);
    }

    [Fact]
    public void 光标在窗口正中_标记格是中心格()
    {
        // 没有发生钳制的常规位置：窗口左上角 = 光标 − half ⇒ 光标正好落在 (6,6)。
        AssertCell(MagnifierOverlay.TryCursorCell(100, 100, 100 - Grid / 2, 100 - Grid / 2, Grid), 6, 6);
    }

    [Fact]
    public void 贴左上边界_窗口被钳住_标记格不是中心格()
    {
        // 这一条就是"把标记硬写成几何中心"会红的地方：光标 (3,2) 时 (gx,gy) 被钳到 (0,0)，
        // 窗口虽然贴着缓冲左上角，光标像素仍然在窗口里 —— 只是不再居中。
        var (left, top) = SampleWindow(3, 2, 1920, 1080);
        Assert.Equal((0, 0), (left, top));
        var cell = MagnifierOverlay.TryCursorCell(3, 2, left, top, Grid);
        AssertCell(cell, 3, 2);
        Assert.True(cell!.Value != (6, 6), "贴边时标记被摆在几何中心：放大镜与指针脱节的原缺陷");
    }

    [Fact]
    public void 贴右下边界_窗口被钳住_标记格落在最后一格()
    {
        const int swapW = 1920, swapH = 1080;
        var (left, top) = SampleWindow(swapW - 1, swapH - 1, swapW, swapH);
        // 左上角退到 (缓冲尺寸 − 网格)，末尾像素仍在窗口内、且落在最后一格里。
        Assert.Equal((swapW - Grid, swapH - Grid), (left, top));
        AssertCell(MagnifierOverlay.TryCursorCell(swapW - 1, swapH - 1, left, top, Grid), Grid - 1, Grid - 1);
    }

    [Fact]
    public void 窗口内逐格扫过_标记格等于光标减窗口左上角()
    {
        for (var row = 0; row < Grid; row++)
        {
            for (var col = 0; col < Grid; col++)
            {
                AssertCell(MagnifierOverlay.TryCursorCell(200 + col, 150 + row, 200, 150, Grid), col, row);
            }
        }
    }

    [Fact]
    public void 光标在窗口外一格_四个方向都不画标记()
    {
        // 可信区间是 [left, left+size) 的半开区间：越出一步就没有正确的格，返回 null（不画）。
        Assert.Null(MagnifierOverlay.TryCursorCell(199, 150, 200, 150, Grid));   // 左外一格
        Assert.Null(MagnifierOverlay.TryCursorCell(212, 150, 200, 150, Grid));   // 右外一格
        Assert.Null(MagnifierOverlay.TryCursorCell(200, 149, 200, 150, Grid));   // 上外一格
        Assert.Null(MagnifierOverlay.TryCursorCell(200, 162, 200, 150, Grid));   // 下外一格
        // 区间内侧两端必须仍然可信（防止把半开区间写成开区间或闭区间）
        AssertCell(MagnifierOverlay.TryCursorCell(200, 150, 200, 150, Grid), 0, 0);
        AssertCell(MagnifierOverlay.TryCursorCell(211, 161, 200, 150, Grid), Grid - 1, Grid - 1);
    }

    [Fact]
    public void 回读落后于光标_旧窗口已不含光标时不画标记()
    {
        // 缓冲显示的还是窗口 (100,100)，光标已经跑到 (300,300)：窗口要到下一次回读才跟上。
        // 这时任何格号都是错的 —— 必须 null（Render 据此不画描边），而不是退回中心格。
        Assert.Null(MagnifierOverlay.TryCursorCell(300, 300, 100, 100, Grid));
    }

    [Fact]
    public void 同一窗口内光标移动_标记格随光标换格()
    {
        // 契约：格号只吃"光标坐标 + 窗口左上角"，与有没有发生回读无关。
        // RefreshPixels 的节流与"网格未移动"两处提前返回期间不回读，所以光标坐标必须
        // 在那两处之前就写进字段（否则这两次调用会拿到同一个旧格号 —— 见类注释的覆盖边界）。
        AssertCell(MagnifierOverlay.TryCursorCell(100, 100, 94, 94, Grid), 6, 6);
        AssertCell(MagnifierOverlay.TryCursorCell(97, 104, 94, 94, Grid), 3, 10);
    }

    [Fact]
    public void 从未成功回读_负哨兵不画标记()
    {
        // _gridLeft/_gridTop 的初值与三处失败分支都置 -1，而真实的 gx/gy 被钳在
        // [0, 缓冲尺寸 − 网格] 内永不为负 ⇒ 负原点只可能是"还没有可信窗口"。
        Assert.Null(MagnifierOverlay.TryCursorCell(0, 0, -1, -1, Grid));
        Assert.Null(MagnifierOverlay.TryCursorCell(5, 5, -1, 0, Grid));
        Assert.Null(MagnifierOverlay.TryCursorCell(5, 5, 0, -1, Grid));
        // 光标自己的哨兵值也落在任何可信窗口之外
        Assert.Null(MagnifierOverlay.TryCursorCell(-1, -1, 0, 0, Grid));
    }

    [Fact]
    public void 后台缓冲比网格还小_窗口钳到零而标记格仍指着光标那格()
    {
        // RefreshPixels 对上下界做了 Math.Max(0,·)：缓冲小于网格时上界为负 → 归零。
        var (left, top) = SampleWindow(4, 6, 5, 7);
        Assert.Equal((0, 0), (left, top));
        AssertCell(MagnifierOverlay.TryCursorCell(4, 6, left, top, Grid), 4, 6);
    }
}
