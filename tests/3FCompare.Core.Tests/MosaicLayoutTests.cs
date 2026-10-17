using _3FCompare.Core.Display;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="MosaicLayout"/> 的判据：自由拼接布局（含 T 形交叉）。
///
/// <para><b>为什么这些判据是必需的</b>：拼接一旦算错，表现是"画面缺一块"、
/// "两块在同一像素上打架（胜负交给 Z 序）"或"拖一条线把不该动的块也带走" ——
/// 三者都很难从截图反推到根因，而它们全都是<b>纯几何</b>性质，在这里能逐条硬断言。</para>
///
/// <para><b>三条不变式</b>（不重叠 / 并集铺满 / 每块为正）在<b>每一次构造与拖动之后</b>
/// 都必须成立，故下面用一个共用的 <c>AssertInvariants</c> 逐例调用 ——
/// 包括随机化构造（用固定种子，失败可复现）。</para></summary>
public class MosaicLayoutTests
{
    // ────────── 共用：三条不变式 ──────────

    private static void AssertInvariants(MosaicLayout m, string what)
    {
        Assert.True(m.Tiles.Length > 0, $"{what}: 至少要有一块");

        // ③ 每块宽高 > 0（0 宽块会让 CreateRectRgn 得到空区域、SetWindowRgn 失败 ⇒ 窗口整块不可见）
        foreach (var t in m.Tiles)
        {
            Assert.True(t.Rect.Width > 0, $"{what}: 块宽非正 {t.Rect.Width}");
            Assert.True(t.Rect.Height > 0, $"{what}: 块高非正 {t.Rect.Height}");
            Assert.True(t.Rect.X >= -1e-9 && t.Rect.Y >= -1e-9, $"{what}: 块越出左上 {t.Rect}");
            Assert.True(t.Rect.X + t.Rect.Width <= 1 + 1e-9, $"{what}: 块越出右边界 {t.Rect}");
            Assert.True(t.Rect.Y + t.Rect.Height <= 1 + 1e-9, $"{what}: 块越出下边界 {t.Rect}");
        }

        // ① 两两不重叠
        for (var i = 0; i < m.Tiles.Length; i++)
            for (var j = i + 1; j < m.Tiles.Length; j++)
            {
                var a = m.Tiles[i].Rect;
                var b = m.Tiles[j].Rect;
                var overlap = a.X < b.X + b.Width - 1e-9 && b.X < a.X + a.Width - 1e-9 &&
                              a.Y < b.Y + b.Height - 1e-9 && b.Y < a.Y + a.Height - 1e-9;
                Assert.False(overlap, $"{what}: 第 {i} 块 {a} 与第 {j} 块 {b} 重叠");
            }

        // ② 并集恰好铺满 [0,1]²：面积和 == 1（配合"不重叠"⇒ 等价于铺满）
        var area = m.Tiles.Sum(t => t.Rect.Width * t.Rect.Height);
        Assert.True(Math.Abs(area - 1.0) < 1e-6, $"{what}: 面积和 {area:F9} != 1（有缝或越界）");
    }

    // ────────── 构造 ──────────

    [Fact]
    public void 单块铺满整窗()
    {
        var m = MosaicLayout.Single();
        Assert.Single(m.Tiles);
        Assert.Equal(0, m.Tiles[0].Route);
        Assert.Equal(new CellRect(0, 0, 1, 1), m.Tiles[0].Rect);
        Assert.Empty(m.Splits());          // 贴外沿的边不算分割线
        Assert.Equal(1, m.RouteCount);
        AssertInvariants(m, "Single");
    }

    [Fact]
    public void 竖切一刀得左右两块()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        Assert.Equal(2, m.Tiles.Length);
        Assert.Equal(new CellRect(0, 0, 0.5, 1), m.Tiles[0].Rect);
        Assert.Equal(new CellRect(0.5, 0, 0.5, 1), m.Tiles[1].Rect);
        // 切开即得两块显示不同路（否则"切了没反应"）
        Assert.Equal(0, m.Tiles[0].Route);
        Assert.Equal(1, m.Tiles[1].Route);
        AssertInvariants(m, "竖切");

        var s = m.Splits();
        Assert.Single(s);
        Assert.Equal(SplitAxis.X, s[0].Axis);
        Assert.Equal(0.5, s[0].Position, 9);
        Assert.Equal(0.0, s[0].From, 9);      // 竖线贯穿整个高度
        Assert.Equal(1.0, s[0].To, 9);
    }

    [Fact]
    public void 横切一刀得上下两块()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.Y, 0.25);
        Assert.Equal(2, m.Tiles.Length);
        Assert.Equal(new CellRect(0, 0, 1, 0.25), m.Tiles[0].Rect);
        Assert.Equal(new CellRect(0, 0.25, 1, 0.75), m.Tiles[1].Rect);
        AssertInvariants(m, "横切");
    }

    [Fact]
    public void 从网格分区继承保住用户已拖好的分格()
    {
        // 网格那边拖到 0.3 / 0.7，切到自由拼接时不能重置
        var gp = GridPartition.Uniform(2, 2, 1).WithHandleDragged(0, 0.3, 0.5);
        var m = MosaicLayout.FromGridPartition(gp);
        Assert.Equal(gp.ComputeCells().Length, m.Tiles.Length);
        for (var i = 0; i < m.Tiles.Length; i++)
        {
            Assert.Equal(gp.ComputeCells()[i], m.Tiles[i].Rect);
            Assert.Equal(i, m.Tiles[i].Route);   // 恒等槽号
        }
        AssertInvariants(m, "FromGridPartition");
    }

    // ────────── T 形交叉（本模型相对 GridPartition 的核心价值） ──────────

    [Fact]
    public void T形交叉_上半两块下半一整块()
    {
        // 先横切成上下两半，再把上半竖切 ⇒ 经典 T 形
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.Y, 0.5)          // 块0=上, 块1=下
            .Split(0, SplitAxis.X, 0.5);         // 上再竖切 ⇒ 块0=左上 块1=右上 块2=下
        Assert.Equal(3, m.Tiles.Length);
        AssertInvariants(m, "T形");

        var vert = m.Splits().Where(s => s.Axis == SplitAxis.X).ToList();
        Assert.Single(vert);                     // 只有一条竖线
        Assert.Equal(0.5, vert[0].Position, 9);
        // ★ 关键：它只覆盖上半（0~0.5），**没有**贯穿到下半
        Assert.Equal(0.0, vert[0].From, 9);
        Assert.Equal(0.5, vert[0].To, 9);

        var horz = m.Splits().Where(s => s.Axis == SplitAxis.Y).ToList();
        Assert.Single(horz);
        Assert.Equal(0.5, horz[0].Position, 9);
        Assert.Equal(0.0, horz[0].From, 9);
        Assert.Equal(1.0, horz[0].To, 9);        // 横线贯穿整个宽度
    }

    [Fact]
    public void T形下拖竖线不切开下半()
    {
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.Y, 0.5)
            .Split(0, SplitAxis.X, 0.5);

        var line = m.LogicalLines().Single(l => l.Axis == SplitAxis.X);
        var dragged = m.DragLine(SplitAxis.X, line.Position, 0.7, line.From, line.To);
        AssertInvariants(dragged, "T形拖动");

        // 下半（原块2，现在仍是最后一块）必须纹丝不动
        var bottom = dragged.Tiles[2];
        Assert.Equal(new CellRect(0, 0.5, 1, 0.5), bottom.Rect);

        // 上半两块按新位置重新分配
        Assert.Equal(0.7, dragged.Tiles[0].Rect.Width, 9);
        Assert.Equal(0.7, dragged.Tiles[1].Rect.X, 9);
        Assert.Equal(0.3, dragged.Tiles[1].Rect.Width, 9);

        // 竖线现在覆盖 0~0.5，位置 0.7
        var nl = dragged.Splits().Single(s => s.Axis == SplitAxis.X);
        Assert.Equal(0.7, nl.Position, 9);
        Assert.Equal(0.5, nl.To, 9);
    }

    [Fact]
    public void 不对称组合_一横一竖混排()
    {
        // 左侧竖条 + 右侧上下两块 —— 横竖混排的不对称形态
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.X, 0.3)          // 块0=左条, 块1=右
            .Split(1, SplitAxis.Y, 0.4);         // 块1 再横切 ⇒ 块1=右上 块2=右下
        Assert.Equal(3, m.Tiles.Length);
        AssertInvariants(m, "不对称");

        Assert.Equal(new CellRect(0, 0, 0.3, 1), m.Tiles[0].Rect);      // 左侧整条
        Assert.Equal(new CellRect(0.3, 0, 0.7, 0.4), m.Tiles[1].Rect);  // 右上
        Assert.Equal(new CellRect(0.3, 0.4, 0.7, 0.6), m.Tiles[2].Rect);// 右下

        // 竖线只覆盖右侧之外？不：左条的右边在 0.3，右两块也都在 0.3 起 ⇒ 竖线贯穿
        var vert = m.Splits().Where(s => s.Axis == SplitAxis.X).ToList();
        Assert.Single(vert);
        Assert.Equal(0.3, vert[0].Position, 9);
        // 横线只覆盖右侧（0.3~1）
        var horz = m.Splits().Where(s => s.Axis == SplitAxis.Y).ToList();
        Assert.Single(horz);
        Assert.Equal(0.4, horz[0].Position, 9);
        Assert.Equal(0.3, horz[0].From, 9);
        Assert.Equal(1.0, horz[0].To, 9);
    }

    // ────────── 拖动 ──────────

    [Fact]
    public void 拖动位置被钳住_块不会塌成零宽()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        foreach (var target in new[] { 0.0, 1.0, -5.0, 5.0 })
        {
            var d = m.DragLine(SplitAxis.X, 0.5, target, 0, 1);
            AssertInvariants(d, $"拖到 {target}");
            Assert.True(d.Tiles[0].Rect.Width >= MosaicLayout.MinSpanFraction - 1e-9,
                $"左侧块被压到 {d.Tiles[0].Rect.Width}");
            Assert.True(d.Tiles[1].Rect.Width >= MosaicLayout.MinSpanFraction - 1e-9,
                $"右侧块被压到 {d.Tiles[1].Rect.Width}");
        }
    }

    [Fact]
    public void 拖动只影响这条线覆盖范围内的块()
    {
        // 上排两块、下排两块，但下排的分界线在别处 —— 形成两条互不相干的竖线
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.Y, 0.5)          // 块0=上 块1=下
            .Split(0, SplitAxis.X, 0.5)          // 上：块0=左上 块1=右上 块2=下
            .Split(2, SplitAxis.X, 0.25);        // 下：块2=左下 块3=右下（线在 0.25）
        Assert.Equal(4, m.Tiles.Length);
        AssertInvariants(m, "两线");

        var lines = m.LogicalLines();
        var upper = lines.Single(l => l.Axis == SplitAxis.X && Math.Abs(l.Position - 0.5) < 1e-9);
        var lower = lines.Single(l => l.Axis == SplitAxis.X && Math.Abs(l.Position - 0.25) < 1e-9);

        // 拖上排那条线：下排必须纹丝不动
        var d = m.DragLine(SplitAxis.X, upper.Position, 0.8, upper.From, upper.To);
        AssertInvariants(d, "拖上排");
        var bottomLeft = d.Tiles[2].Rect;
        var bottomRight = d.Tiles[3].Rect;
        Assert.Equal(0.25, bottomLeft.Width, 9);
        Assert.Equal(0.25, bottomRight.X, 9);
        // 上排按新位置分配
        Assert.Equal(0.8, d.Tiles[0].Rect.Width, 9);

        // 拖下排那条线：上排必须纹丝不动
        var d2 = m.DragLine(SplitAxis.X, lower.Position, 0.6, lower.From, lower.To);
        AssertInvariants(d2, "拖下排");
        Assert.Equal(0.5, d2.Tiles[0].Rect.Width, 9);
        Assert.Equal(0.5, d2.Tiles[1].Rect.X, 9);
        Assert.Equal(0.6, d2.Tiles[2].Rect.Width, 9);
    }

    [Fact]
    public void 拖动NaN不改变几何()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        var d = m.DragLine(SplitAxis.X, 0.5, double.NaN, 0, 1);
        Assert.True(m.SameAs(d), "NaN 入参必须幂等（NaN 进几何会让 CreateRectRgn 收到翻转矩形）");
        AssertInvariants(d, "NaN");
    }

    // ────────── 合并 ──────────

    [Fact]
    public void 合并不是切分的逆_同槽号才可合并()
    {
        // 显式让两块同槽号 ⇒ 可合并回单块（"删掉这道线"的语义）
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5, newRoute: 0);
        Assert.Equal(2, m.Tiles.Length);
        Assert.Equal(0, m.Tiles[0].Route);
        Assert.Equal(0, m.Tiles[1].Route);

        var merged = m.Merge(0);
        Assert.Single(merged.Tiles);
        Assert.Equal(new CellRect(0, 0, 1, 1), merged.Tiles[0].Rect);
        AssertInvariants(merged, "合并");
    }

    [Fact]
    public void 默认切分给新块分配未占用的槽号()
    {
        // 切一刀就得到两块显示不同路（不是"切了没反应"）
        var m1 = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        Assert.Equal(new[] { 0, 1 }, m1.Tiles.Select(t => t.Route).ToArray());

        // 再切一刀 ⇒ 复用最小未占用槽号 2
        var m2 = m1.Split(1, SplitAxis.Y, 0.5);
        Assert.Equal(new[] { 0, 1, 2 }, m2.Tiles.Select(t => t.Route).ToArray());

        // 槽号 1 被合并掉之后，下一次切分应复用 1（不跳号）
        var m3 = m2.WithRoute(1, 0, 3).WithRoute(2, 0, 3);   // 三块都成了槽号 0
        var m4 = m3.Split(0, SplitAxis.X, 0.5);
        Assert.Contains(1, m4.Tiles.Select(t => t.Route));
        AssertInvariants(m4, "槽号复用");
    }

    [Fact]
    public void 槽号不同则不合并_幂等()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5, newRoute: 1);
        Assert.Equal(0, m.Tiles[0].Route);
        Assert.Equal(1, m.Tiles[1].Route);
        var merged = m.Merge(0);
        Assert.Equal(2, merged.Tiles.Length);   // 异槽 ⇒ 不合并
        AssertInvariants(merged, "异槽不合并");
    }

    // ────────── 槽号与镜像 ──────────

    [Fact]
    public void 同一路可占多块_含不相邻()
    {
        // 三竖条，左右都指定为路 0（中间是路 1）⇒ 路 0 占不相邻的两块
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.X, 1.0 / 3)
            .Split(1, SplitAxis.X, 0.5)
            .WithRoute(0, 0, 2)
            .WithRoute(1, 1, 2)
            .WithRoute(2, 0, 2);
        Assert.Equal(3, m.Tiles.Length);
        AssertInvariants(m, "重复槽");

        var ofRoute0 = m.TilesOfRoute(0);
        Assert.Equal(2, ofRoute0.Length);
        Assert.Equal(new[] { 0, 2 }, ofRoute0.ToArray());   // ★ 不相邻
        Assert.Equal(2, m.RouteCount);
    }

    [Fact]
    public void 槽号被钳到路数范围()
    {
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.X, 0.5)
            .WithRoute(1, 99, routeCount: 3);    // 路数 3 ⇒ 合法槽号 0..2
        Assert.Equal(2, m.Tiles[1].Route);
        AssertInvariants(m, "槽号钳制");
    }

    // ────────── 区域换算 ──────────

    [Fact]
    public void 区域换算_每块一个矩形且铺满该块的份额()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        var win = new Rect32(0, 0, 1000, 800);

        var r0 = m.PlanRegionsFor(win, 0);
        var r1 = m.PlanRegionsFor(win, 1);
        Assert.Single(r0);
        Assert.Single(r1);
        Assert.Equal(new Rect32(0, 0, 500, 800), r0[0]);
        Assert.Equal(new Rect32(500, 0, 500, 800), r1[0]);

        // 两块并集 == 整窗（拼接的性质；这条同时验证"没留缝"）
        Assert.Equal(1000, r0[0].Width + r1[0].Width);
    }

    [Fact]
    public void 区域换算_同一路占不相邻两块时给出两个矩形()
    {
        // 三竖条，路 0 占左与右（不相邻）
        var m = MosaicLayout.Single()
            .Split(0, SplitAxis.X, 1.0 / 3)   // 块0=路0(左), 块1=路1(右)
            .Split(1, SplitAxis.X, 0.5)       // 块1 -> 块1=路1(中), 块2=路2(右)
            .WithRoute(2, 0, 3);              // 最右改回路 0 ⇒ 与左侧的路 0 不相邻

        var win = new Rect32(0, 0, 900, 600);
        var r = m.PlanRegionsFor(win, 0);
        Assert.Equal(2, r.Length);                     // ★ 两个矩形
        Assert.Equal(new Rect32(0, 0, 300, 600), r[0]);
        Assert.Equal(new Rect32(600, 0, 300, 600), r[1]);

        // 中间那块归路 1，且三块横坐标不重叠、铺满
        var rMid = m.PlanRegionsFor(win, 1);
        Assert.Single(rMid);
        Assert.Equal(300, rMid[0].X);
        Assert.Equal(300, rMid[0].Width);
    }

    [Fact]
    public void 区域换算_坐标是窗口相对而不是容器相对()
    {
        // ⚠ 这条判据钉住一个真机抓出来的缺陷：区域是**窗口相对**坐标（SetWindowRgn 语义），
        //   而块是容器相对 ⇒ 换算时**不得**把窗口原点的 X/Y 加进区域里。
        //   第一版写成"加偏移"，实测区域整体偏了 2,2，表现是"露出一条父窗底色/裁错位置"。
        var m = MosaicLayout.Single().Split(0, SplitAxis.Y, 0.25);
        var win = new Rect32(12, 34, 1000, 800);      // 窗口在容器里位于 (12,34)
        var top = m.PlanRegionsFor(win, 0);
        var bottom = m.PlanRegionsFor(win, 1);

        // 区域从窗口原点 (0,0) 起算，与窗口在容器里的位置无关
        Assert.Equal(new Rect32(0, 0, 1000, 200), top[0]);
        Assert.Equal(new Rect32(0, 200, 1000, 600), bottom[0]);

        // 尺寸只由窗口的宽高决定（位置不参与）
        var atOrigin = m.PlanRegionsFor(new Rect32(0, 0, 1000, 800), 0);
        Assert.Equal(atOrigin[0].Width, top[0].Width);
        Assert.Equal(atOrigin[0].Height, top[0].Height);
    }

    [Fact]
    public void 区域换算_太小无法表达时跳过而不是产生零宽()
    {
        // 窗口只有 1px 宽、两块各要一半 ⇒ 取整后必然有一块不足 1px ⇒ 那一块跳过。
        // 判据只要求"绝不产生 <1px 的区域"（0 宽区域会让 SetWindowRgn 失败、窗口整块不可见），
        // 不要求一定为空 —— 取整口径下 1px 窗口的两块可能各得 1px（都合法）。
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        var tiny = new Rect32(0, 0, 1, 10);
        foreach (var r in m.PlanRegionsFor(tiny, 0).Concat(m.PlanRegionsFor(tiny, 1)))
        {
            Assert.True(r.Width >= 1, $"产生了不足 1px 宽的块：{r}");
            Assert.True(r.Height >= 1, $"产生了不足 1px 高的块：{r}");
        }

        // 明确不可表达的情形：窗口 1px 宽但 4 块 ⇒ 必然有块拿不到 1px
        var m4 = m.Split(1, SplitAxis.X, 0.5).Split(2, SplitAxis.X, 0.5, newRoute: 2);
        var total = 0;
        for (var route = 0; route <= 3; route++) total += m4.PlanRegionsFor(tiny, route).Length;
        Assert.True(total < 4, $"1px 窗口下 4 块应无法全部表达，实际给出 {total} 块");

        // 非法窗口返回空
        Assert.Empty(m.PlanRegionsFor(new Rect32(0, 0, 0, 10), 0));
        Assert.Empty(m.PlanRegionsFor(new Rect32(0, 0, 100, 0), 0));
    }

    [Fact]
    public void 区域换算_未占块的槽号返回空()
    {
        var m = MosaicLayout.Single().Split(0, SplitAxis.X, 0.5);
        // 只有槽号 0 与 1 存在；问 7 应该得到空
        Assert.Empty(m.PlanRegionsFor(new Rect32(0, 0, 100, 100), 7));
    }

    // ────────── 校验 ──────────

    [Fact]
    public void 非法块表在构造点失败而不是发给Win32()
    {
        Assert.Throws<ArgumentException>(() => MosaicLayout.FromTiles(Array.Empty<MosaicTile>()));
        Assert.Throws<ArgumentException>(() => MosaicLayout.FromTiles(new[]
        {
            new MosaicTile(new CellRect(0, 0, 0, 1), 0)      // 0 宽
        }));
        Assert.Throws<ArgumentException>(() => MosaicLayout.FromTiles(new[]
        {
            new MosaicTile(new CellRect(0, 0, 1, 1), 0),
            new MosaicTile(new CellRect(0.5, 0, 0.5, 1), 1)  // 与上一块重叠
        }));
        Assert.Throws<ArgumentException>(() => MosaicLayout.FromTiles(new[]
        {
            new MosaicTile(new CellRect(double.NaN, 0, 1, 1), 0)
        }));
    }

    /// <summary><b>区域换算后每个像素必须恰好被一块覆盖</b>（无缝、无叠）。
    ///
    /// <para><b>本条来自对抗式复查（BLOCKER）</b>。原来的 <see cref="AssertInvariants"/> 只验
    /// "归一化矩形"的面积和，而**像素投影**那一层完全没有判据；于是下面这个反例
    /// 在全部既有判据下都是绿的，却在屏幕上漏出整整一行父窗底色：</para>
    ///
    /// <para>相邻块在归一化空间共享一条边，但两侧表达式不同（<c>A+B</c> vs <c>0.95</c>），
    /// 浮点差约 <c>1e-16</c>；当 <c>边 × 窗口尺寸</c> 恰好落在 <c>.5</c> 平局上时，
    /// 这个差会让两侧向<b>相反方向</b>取整 ⇒ 空出一整行像素。
    /// 反例（deterministic）：<c>0.8341561705991691 + 0.11584382940083071 = 0.9499999999999998</c>，
    /// 而下一块起点 <c>0.95</c>；在 h=2450 时分别取整到 <b>2327 / 2328</b> ⇒ 第 2327 行无人覆盖。</para>
    ///
    /// <para><b>为什么面积判据在结构上抓不到它</b>：缝宽 <c>g</c> 只让面积和少 <c>g</c>，
    /// 而面积容差是 <c>1e-6</c> ⇒ <c>1e-16</c> 级的缝对面积判据完全不可见
    /// （实测 <c>1e-6</c> 级能检出、<c>1e-7</c> 级不能）。
    /// 故本判据改成<b>逐像素扫一遍</b>并断言"恰好被一块覆盖"——这是能看见它的形式。</para>
    ///
    /// <para>修法是"共享边界表"（<c>PlanRegionsFor</c> 里 <c>BuildPixelCuts</c>）：
    /// 先把该轴所有边界取整去重排序，再让每块从同一张表取端点 ⇒ 共享边必然取到同一整数，
    /// 缝与叠在构造上不可能出现。</para></summary>
    [Fact]
    public void 区域换算后每个像素恰好被一块覆盖()
    {
        // 复查给出的确定性反例（曾在 h=2450 时漏掉第 2327 行）
        var m = MosaicLayout.Single();
        m = m.Split(0, SplitAxis.Y, 0.4320868367478656);
        m = m.Split(1, SplitAxis.Y, 0.9367893701124886);
        m = m.Split(1, SplitAxis.Y, 0.7763257672899988);
        m = m.Split(0, SplitAxis.Y, 0.4628319323355481);
        m = m.Split(1, SplitAxis.X, 0.6531030650497894);
        AssertInvariants(m, "反例布局");

        // 在多个窗口尺寸上扫（反例在部分高度上才出现 ⇒ 单个尺寸可能漏掉）
        foreach (var (w, h) in new[] { (401, 2450), (401, 2449), (401, 2327), (1, 1), (3, 3), (1920, 1080), (1995, 1122) })
        {
            var win = new Rect32(0, 0, w, h);
            var cover = new int[w * h];
            var routes = new System.Collections.Generic.SortedSet<int>();
            foreach (var t in m.Tiles) routes.Add(t.Route);

            foreach (var route in routes)
                foreach (var rg in m.PlanRegionsFor(win, route))
                {
                    Assert.True(rg.Width >= 1 && rg.Height >= 1,
                        $"{w}x{h} route {route}：区域尺寸非正 {rg.Width}x{rg.Height}（SetWindowRgn 会失败）");
                    for (var y = rg.Y; y < rg.Y + rg.Height; y++)
                        for (var x = rg.X; x < rg.X + rg.Width; x++)
                        {
                            Assert.True(x >= 0 && y >= 0 && x < w && y < h,
                                $"{w}x{h} route {route}：区域越出窗口 [{rg.X},{rg.Y} {rg.Width}x{rg.Height}]");
                            cover[y * w + x]++;
                        }
                }

            var holes = 0; var overlaps = 0; var firstHole = "";
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var c = cover[y * w + x];
                    if (c == 0) { holes++; if (firstHole == "") firstHole = $"({x},{y})"; }
                    else if (c > 1) overlaps++;
                }

            Assert.True(holes == 0,
                $"{w}x{h}：有 {holes} 个像素无人覆盖（首个 {firstHole}）—— 会露出父窗口底色");
            Assert.True(overlaps == 0,
                $"{w}x{h}：有 {overlaps} 个像素被多于一块覆盖 —— 可见性会交给 Z 序（本方案的前提是不依赖 Z 序）");
        }
    }

    [Fact]
    public void 越界块号抛出()
    {
        var m = MosaicLayout.Single();
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Split(-1, SplitAxis.X, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Split(1, SplitAxis.X, 0.5));
        Assert.Throws<ArgumentException>(() => m.Split(0, SplitAxis.Both, 0.5));
    }

    /// <summary>反复切分不得把块压到 <see cref="MosaicLayout.MinSpanFraction"/>（占**整个容器**）以下。
    ///
    /// <para><b>本条来自审查轮</b>：<c>Split</c> 里
    /// <c>minRel = Math.Min(0.5, MinSpanFraction / span)</c> 的 <c>Math.Min(0.5, ·)</c>
    /// 会在 <c>span ≤ 2 × MinSpanFraction</c> 时把下界**封顶到 0.5</c> ——
    /// 于是继续切就退化成"对半劈"，块一路变小、**突破文档承诺的 5% 下界**
    /// （实测连切 12 次可到 <c>0.000024</c>，即容器的 <c>0.0024%</c>）。
    /// 后果：块小到 1px 以下时区域根本表达不出来（<c>PlanRegionsFor</c> 会跳过该块），
    /// 表现为"某一块在屏幕上彻底消失"，而用户只是多点了几下切分。</para>
    ///
    /// <para>本判据钉住**文档承诺**：无论怎么切，每块都 ≥ MinSpanFraction。</para></summary>
    [Fact]
    public void 反复切分不得把块压到最小尺寸以下()
    {
        var m = MosaicLayout.Single();
        for (var depth = 1; depth <= 12; depth++)
        {
            var idx = m.Tiles.Length - 1;          // 每次都切最右那一块（最容易造出小块）
            m = m.Split(idx, SplitAxis.X, 1.0);
            AssertInvariants(m, $"第 {depth} 次切分后");
            foreach (var t in m.Tiles)
            {
                Assert.True(t.Rect.Width >= MosaicLayout.MinSpanFraction - 1e-9,
                    $"第 {depth} 次切分后出现宽度 {t.Rect.Width:F9} 的块，" +
                    $"低于承诺的最小尺寸 {MosaicLayout.MinSpanFraction}（占容器）");
            }
        }
    }

    // ────────── 随机化：任意构造序列后不变式恒成立 ──────────

    [Fact]
    public void 随机构造序列后三条不变式恒成立()
    {
        // 固定种子 ⇒ 失败可复现（不用随机种子，免得"偶发红"无法定位）
        var rng = new Random(20261007);
        for (var trial = 0; trial < 200; trial++)
        {
            var m = MosaicLayout.Single();
            var ops = rng.Next(1, 9);
            for (var k = 0; k < ops; k++)
            {
                var pick = rng.Next(3);
                if (pick == 0)
                {
                    // 随机切一块（fraction 故意包含极端值，考验钳制）
                    var idx = rng.Next(m.Tiles.Length);
                    var axis = rng.Next(2) == 0 ? SplitAxis.X : SplitAxis.Y;
                    var f = rng.Next(5) switch
                    {
                        0 => 0.0,
                        1 => 1.0,
                        2 => -3.0,
                        3 => 4.0,
                        _ => rng.NextDouble(),
                    };
                    m = m.Split(idx, axis, f);
                }
                else if (pick == 1)
                {
                    // 随机拖一条逻辑线
                    var lines = m.LogicalLines();
                    if (lines.Length == 0) continue;
                    var l = lines[rng.Next(lines.Length)];
                    m = m.DragLine(l.Axis, l.Position, rng.NextDouble(), l.From, l.To);
                }
                else
                {
                    // 随机改槽号
                    var idx = rng.Next(m.Tiles.Length);
                    m = m.WithRoute(idx, rng.Next(4), routeCount: 4);
                }
                AssertInvariants(m, $"trial={trial} op={k}");
            }

            // 每个 trial 收尾再全面查一次（含线段覆盖区间合法性）
            AssertInvariants(m, $"trial={trial} 收尾");
            foreach (var s in m.Splits())
            {
                Assert.True(s.From >= -1e-9 && s.To <= 1 + 1e-9, $"trial={trial}: 线段越界 {s}");
                Assert.True(s.To > s.From, $"trial={trial}: 线段区间非正 {s}");
                Assert.True(s.Position > 0 && s.Position < 1, $"trial={trial}: 线位置越界 {s}");
            }
        }
    }
}
