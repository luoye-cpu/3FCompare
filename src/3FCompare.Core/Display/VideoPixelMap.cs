namespace _3FCompare.Core.Display;

/// <summary>
/// 指针 / 光标坐标 → 源视频像素坐标的映射（纯逻辑，可单测）。
/// <para><b>历史缺陷（C4）</b>：无 <c>RenderTargetInfo</c> 的兜底分支曾把 physX 自己当分母，
/// 退化成 <c>physX / physX == 1</c>，于是任何位置都被映射到右下角的越界像素，探针必然读失败。
/// 兜底分母<b>必须</b>是"表面物理尺寸"——这里把该约束固化成可单测的纯函数。</para>
///
/// <para><b>唯一口径</b>：整数像素（探针读值、放大镜）与小数归一化坐标（放大锚点）必须是
/// <b>同一个点</b>，否则"光标钉在哪个像素上"和"界面报的第几列"会各说一套（<c>PixelReadback</c>
/// 的 A11 教训）。因此本类只有一份边界判定与一份截断规则：
/// <see cref="MapToSource"/> = <see cref="MapToSourceFraction"/> + <see cref="FractionToPixel"/>，
/// <see cref="MapFallback"/> = <see cref="MapFallbackFraction"/> + <see cref="FractionToPixel"/>。</para>
/// </summary>
public static class VideoPixelMap
{
    /// <summary>把后台缓冲物理坐标映射到源视频像素。
    /// 落在 destination 之外（letterbox 黑边）或参数无效时返回 <c>null</c>（无有效像素）。</summary>
    /// <param name="physX">表面物理坐标 X（DIP × RenderScaling）。</param>
    /// <param name="physY">表面物理坐标 Y。</param>
    /// <param name="destX">视频内容区在后台缓冲中的左上角 X。</param>
    /// <param name="destY">视频内容区左上角 Y。</param>
    /// <param name="destW">视频内容区宽度（&gt;0）。</param>
    /// <param name="destH">视频内容区高度（&gt;0）。</param>
    /// <param name="videoW">源视频宽度（&gt;0）。</param>
    /// <param name="videoH">源视频高度（&gt;0）。</param>
    public static (int X, int Y)? MapToSource(
        double physX, double physY,
        double destX, double destY, double destW, double destH,
        int videoW, int videoH)
    {
        // 源尺寸非法必须在**这里**拦：分数换算本身与源尺寸无关，漏下去会被钳成 -1。
        if (videoW <= 0 || videoH <= 0) return null;
        var frac = MapToSourceFraction(physX, physY, destX, destY, destW, destH);
        if (frac is not { } f) return null;
        return (FractionToPixel(f.U, videoW), FractionToPixel(f.V, videoH));
    }

    /// <summary>与 <see cref="MapToSource"/> 同一判定、同一算式，只是<b>不截到整数像素</b>：
    /// 返回源画面上的归一化坐标 <c>U, V ∈ [0,1]</c>。
    ///
    /// <para>用途：放大锚点。锚点条件是"光标下那一点在屏幕上不动"，把光标所在像素截成整数
    /// 等于放弃像素内的亚像素位置 —— 每格滚轮最多偏一个<b>显示后</b>的像素，且同向累加
    /// （不是随机抖动），放大十几格就看得出来了。故锚点必须走小数版。</para>
    /// <para>落在 destination 之外（黑边）或参数非法同样返回 <c>null</c> = "光标下没有源画面"。</para></summary>
    public static (double U, double V)? MapToSourceFraction(
        double physX, double physY,
        double destX, double destY, double destW, double destH)
    {
        if (destW <= 0 || destH <= 0) return null;
        if (physX < destX || physX >= destX + destW ||
            physY < destY || physY >= destY + destH) return null;

        return ((physX - destX) / destW, (physY - destY) / destH);
    }

    /// <summary>无 <c>RenderTargetInfo</c>（演示模式 / 旧内核）时的兜底：整面等比映射。
    /// 会漏掉 letterbox，但绝不越界、也绝不会退化成常量。
    /// 表面尚未布局（尺寸 ≤ 0）时返回 <c>null</c>，而不是产出一个假坐标。</summary>
    public static (int X, int Y)? MapFallback(
        double physX, double physY,
        double surfaceW, double surfaceH,
        int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0) return null;
        var frac = MapFallbackFraction(physX, physY, surfaceW, surfaceH);
        if (frac is not { } f) return null;
        return (FractionToPixel(f.U, videoW), FractionToPixel(f.V, videoH));
    }

    /// <summary><see cref="MapFallback"/> 的小数版（约定与理由同上）。</summary>
    public static (double U, double V)? MapFallbackFraction(
        double physX, double physY,
        double surfaceW, double surfaceH)
    {
        if (surfaceW <= 0 || surfaceH <= 0) return null;
        // 与 MapFallback 一致：兜底路径刻意不做"落在表面之外 → null"，而是钳到边界内——
        // 指针贴在右/下边缘时给出最后一个像素的位置比给 null 更符合直觉。
        return (ClampUnit(physX / surfaceW), ClampUnit(physY / surfaceH));
    }

    /// <summary>归一化坐标 → 整数像素下标：截断 + 钳到 <c>[0, count-1]</c>。
    /// 本类所有"分数 → 像素"的出口共用这一条规则（含 NaN 兜到 0）。</summary>
    public static int FractionToPixel(double fraction, int count)
    {
        if (count <= 0) return 0;
        if (double.IsNaN(fraction)) return 0;
        return Clamp((int)(fraction * count), count);
    }

    private static double ClampUnit(double v) =>
        double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;

    private static int Clamp(int v, int max) => v < 0 ? 0 : (v > max - 1 ? max - 1 : v);
}
