namespace _3FCompare.Core.Display;

/// <summary>
/// 指针 / 光标坐标 → 源视频像素坐标的映射（纯逻辑，可单测）。
/// <para><b>历史缺陷（C4）</b>：无 <c>RenderTargetInfo</c> 的兜底分支曾把 physX 自己当分母，
/// 退化成 <c>physX / physX == 1</c>，于是任何位置都被映射到右下角的越界像素，探针必然读失败。
/// 兜底分母<b>必须</b>是"表面物理尺寸"——这里把该约束固化成可单测的纯函数。</para>
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
        if (videoW <= 0 || videoH <= 0 || destW <= 0 || destH <= 0) return null;
        if (physX < destX || physX >= destX + destW ||
            physY < destY || physY >= destY + destH) return null;

        var nx = (int)((physX - destX) / destW * videoW);
        var ny = (int)((physY - destY) / destH * videoH);
        return (Clamp(nx, videoW), Clamp(ny, videoH));
    }

    /// <summary>无 <c>RenderTargetInfo</c>（演示模式 / 旧内核）时的兜底：整面等比映射。
    /// 会漏掉 letterbox，但绝不越界、也绝不会退化成常量。
    /// 表面尚未布局（尺寸 ≤ 0）时返回 <c>null</c>，而不是产出一个假坐标。</summary>
    public static (int X, int Y)? MapFallback(
        double physX, double physY,
        double surfaceW, double surfaceH,
        int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0 || surfaceW <= 0 || surfaceH <= 0) return null;
        // 注意：这里刻意不做"落在表面之外 → null"的判定，而是钳到边界内。
        // 整面等比映射本就是退化路径，指针贴在右/下边缘时给出最后一个像素比给 null 更符合直觉。
        var nx = (int)(physX / surfaceW * videoW);
        var ny = (int)(physY / surfaceH * videoH);
        return (Clamp(nx, videoW), Clamp(ny, videoH));
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : (v > max - 1 ? max - 1 : v);
}
