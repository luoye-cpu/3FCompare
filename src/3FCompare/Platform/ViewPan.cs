using System;

namespace _3FCompare.Platform;

/// <summary>单路（非对比模式）放大拖动的平移累加 —— 把一段屏幕位移换算成内核
/// <c>FFF3FP_SetViewTransform</c> 的归一化 pan 增量。
///
/// <para>抽成纯函数的唯一理由是这个符号与量纲错过两次，而它在窗口里没法离线验：
/// <list type="bullet">
/// <item><b>取负号才跟手</b>：内核把 <c>pan</c> 当<b>视口位置</b>用
/// （<c>VideoRenderer.cpp</c> 的 <c>destination.x = destination.x + (w − zoomedW)/2 − offsetX</c>，
/// 其中 <c>offsetX = panX · maxPanX · w</c> 与 <c>panX</c> 同号）⇒ <c>pan</c> 增大是画面往<b>左/上</b>跑。
/// 鼠标右移要画面也右移，累加必须做减法。对比模式那一路（<c>CompareCropPlanner.Magnify</c> +
/// <c>CommitComparePan</c>）本来就是负号，两路语义因此一致。</item>
/// <item><b>位移要先除 DPI</b>：<c>GetCursorPos</c> 给的是<b>物理像素</b>，而分母
/// <paramref name="minSideDip"/> 是 <c>Window.Bounds</c> 的<b>DIP</b>。不除的话 150% 缩放下
/// 每格拖动的量程直接超 1.5 倍 —— 表现为"轻轻一拖就顶到头"。（对比模式的 <c>CommitComparePan</c>
/// 一直有这一步，本路此前漏了。）</item>
/// <item><b>分母取短边</b>：<c>2/minSide</c> 让"拖满短边一半"对应 <c>pan = ±1</c>。
/// 取长边会让宽屏下横向灵敏度凭空减半。</item>
/// </list></para>
///
/// <para>⚠ 这里修的只是<b>方向与量纲</b>。<b>量程</b>受内核另一条限制：<c>VideoDestination.x/y</c>
/// 是 <c>std::uint32_t</c>，上面那段平移代码用 <c>max(0, ·)</c> 兜住负值 ⇒ 放大盒的原点不能为负
/// ⇒ "往右/往下露出更多源画面"这一半方向会被夹死（拟合盒左沿本就贴 0 时，横向平移恒等于 0）。
/// 那是内核侧的事，不在本函数职责内。</para></summary>
public static class ViewPan
{
    /// <summary>pan 的合法区间（内核 <c>SetViewTransform</c> 也按 <c>[-1,1]</c> 钳）。</summary>
    public const float Min = -1f;
    public const float Max = 1f;

    /// <param name="dxPx">本次拖动的屏幕水平位移（物理像素，右为正）。累计值由调用方持有。</param>
    /// <param name="scaling">渲染缩放（<c>RenderScaling</c>）。≤0 = 尚未确定，按 1.0 处理。</param>
    /// <param name="minSideDip">窗口短边（DIP）。≤0（刚创建/尺寸为 0）时本次不动，返回原值。</param>
    public static (float PanX, float PanY) Accumulate(float currentPanX, float currentPanY,
        double dxPx, double dyPx, double scaling, double minSideDip)
    {
        if (!(minSideDip > 0)) return (currentPanX, currentPanY);
        var s = scaling > 0 ? scaling : 1.0;
        var k = (float)(2.0 / (minSideDip * s));
        return (Math.Clamp(currentPanX - (float)dxPx * k, Min, Max),
                Math.Clamp(currentPanY - (float)dyPx * k, Min, Max));
    }
}
