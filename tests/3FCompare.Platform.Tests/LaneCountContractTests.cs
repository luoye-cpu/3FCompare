namespace _3FCompare.Platform.Tests;

using LaneRemoveAction = global::_3FCompare.MainWindow.LaneRemoveAction;

/// <summary>
/// 传输栏左下角「加路 / 减路」与 D1~D9 的<b>计数规则</b>守门测试。
///
/// <para><b>为什么可以直接调用</b>：被测的 <c>MainWindow.LaneCountAfterAdd</c> /
/// <c>MainWindow.PlanLaneRemove</c> 是 <c>internal static</c> 纯函数（只做整数比较，不碰 Avalonia、
/// 不碰内核），而 UI 工程已对 <c>3FCompare.Platform.Tests</c> 开 <c>InternalsVisibleTo</c>
/// （见 <c>src/3FCompare/3FCompare.csproj</c>）⇒ 这里调用的是<b>生产代码本身</b>，
/// 不是测试里重写的一份等价逻辑。</para>
///
/// <para><b>期望值来源（独立推算，不读实现）</b>：规则本身是两条用户能陈述的承诺 ——
/// ① 加一条 lane 让<b>可见 lane 数</b> +1，封顶 9，任何情况下都不许让 lane 变少；
/// ② 减路撤<b>最后一条 lane</b>，只有当它上面真压着素材时才动素材（素材恒占前 <c>route</c> 条
/// lane，故"最后是空 lane"等价于 <c>lane &gt; route</c>）。下面的期望值全部由这两条手算得出。</para>
///
/// <para><b>本文件的独立价值</b>：真机契约 <c>AssertLaneAddRemoveContractAsync</c>（挂在
/// <c>--selftest</c> 与 <c>--comparemodetest</c> 上）跑到时网格与会话数恰好相等（1 路 1 格 /
/// 2 路 2 格），而 <c>lane == route</c> 时新旧两套锚点<b>算出同一个数</b> ⇒ 那一刻真机路径
/// 抓不到"锚点取错"。分叉只出现在冷启动（默认 2 条空 lane、0 路素材）与 D9 之后，
/// 前者在带着素材的自测流程里回不去 ⇒ 这张真值表是唯一能钉住它们的判据。</para>
/// </summary>
public class LaneCountContractTests
{
    // ─────────────────────────── 生产代码入口 ───────────────────────────

    private const int MaxLane = global::_3FCompare.MainWindow.MaxLaneCount;

    private static int AfterAdd(int lane) => global::_3FCompare.MainWindow.LaneCountAfterAdd(lane);

    private static LaneRemoveAction Plan(int lane, int route) =>
        global::_3FCompare.MainWindow.PlanLaneRemove(lane, route);

    // ─────────────── ① 加路 = lane +1，封顶 9，永不缩 ───────────────

    [Theory]
    [InlineData(0, 1)]   // 全收起时加路要恢复出 1 条 lane
    [InlineData(1, 2)]
    [InlineData(2, 3)]   // 冷启动默认就是 2 条空 lane：必须 3；旧实现按会话数算成 1
    [InlineData(8, 9)]
    public void 加路让可见lane数加一(int lane, int expected)
    {
        Assert.Equal(expected, AfterAdd(lane));
    }

    [Fact]
    public void 加路触顶时停在原地()
    {
        // 返回原值 = 已在 9 路上限，调用方据此给"已达上限"反馈而不是静默 no-op
        Assert.Equal(MaxLane, AfterAdd(MaxLane));
    }

    /// <summary>加路的结果永远 ≥ 入参 —— 这条是「0 素材时加路反而撤 lane」缺陷的负样本：
    /// 旧实现在 lane=2、route=0 时算出 SetCount(1)，本断言当场判红。</summary>
    [Fact]
    public void 加路永不让lane变少()
    {
        for (var lane = 0; lane <= MaxLane; lane++)
            Assert.True(AfterAdd(lane) >= lane, $"加路把 lane {lane} 变成了 {AfterAdd(lane)}");
    }

    // ─────────────── ② 减路：尾部空 lane 优先，素材不动 ───────────────

    [Theory]
    // lane, route, 期望
    [InlineData(0, 0, LaneRemoveAction.None)]            // 什么都没有：不动作
    [InlineData(1, 1, LaneRemoveAction.DropLastRoute)]   // 一格一素材：减路就是撤最后一路
    [InlineData(2, 2, LaneRemoveAction.DropLastRoute)]
    [InlineData(2, 0, LaneRemoveAction.DropEmptyLane)]   // 冷启动的 2 条空 lane
    [InlineData(3, 2, LaneRemoveAction.DropEmptyLane)]   // A/B 下加过一条 lane：撤的必须是那条空的
    [InlineData(9, 2, LaneRemoveAction.DropEmptyLane)]   // D9 之后：7 条空 lane 垫底
    [InlineData(9, 9, LaneRemoveAction.DropLastRoute)]
    public void 减路按尾部是否空lane决策(int lane, int route, LaneRemoveAction expected)
    {
        Assert.Equal(expected, Plan(lane, route));
    }

    /// <summary>加一次再减一次必须回到原样，且减的那一步判为「撤空 lane」——
    /// 两个方向互为逆运算。对全部合法 (lane, route) 组合成立（route ≤ lane）。</summary>
    [Fact]
    public void 加一次再减一次只动空lane()
    {
        for (var lane = 0; lane < MaxLane; lane++)
        {
            for (var route = 0; route <= lane; route++)
            {
                var after = AfterAdd(lane);
                Assert.Equal(lane + 1, after);
                Assert.Equal(LaneRemoveAction.DropEmptyLane, Plan(after, route));
                Assert.Equal(lane, after - 1);   // 撤掉的就是刚加的那一条
            }
        }
    }

    /// <summary>负样本：<c>lane == route</c> 时不允许判成「撤空 lane」—— 那种情况下并没有空 lane，
    /// 判成撤 lane 就等于让最后一路素材失去显示位而会话仍留在 <c>_sync</c> 里。</summary>
    [Fact]
    public void 没有空lane时不得判成撤lane()
    {
        for (var lane = 1; lane <= MaxLane; lane++)
            Assert.Equal(LaneRemoveAction.DropLastRoute, Plan(lane, lane));
    }
}
