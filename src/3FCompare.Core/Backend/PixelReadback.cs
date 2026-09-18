using System;

namespace _3FCompare.Core.Backend;

/// <summary>
/// 像素回读的坐标域换算（UI 侧唯一入口）。
/// <para><b>背景（实测，2026-09-16）</b>：内核 <c>FFF3FP_ReadVideoPixel</c> 的坐标域是
/// <b>后台缓冲 / 交换链</b>，<b>不是</b>片源分辨率。判据：4K 片源（client 1995×1122）下
/// x=1990 可读、x≥2500 失败；换成 1280×720 片源后 x=1500/1920/1990 全部可读
/// —— 越界边界恒等于后台缓冲尺寸，与片源分辨率无关。</para>
/// <para>而 UI 的探针/放大镜坐标一直按<b>片源像素</b>语义使用（"源第几行第几列"，
/// 对用户才有意义）。直接把片源坐标丢给内核 ⇒ 窗口小于片源时（常态）读到的
/// 是偏右下的错误位置，甚至越界返回黑或失败。这里做唯一一次换算。</para>
/// <para><b>坐标换算的唯一出处（A11）</b>：片源 ⇄ 缓冲两个方向的换算都收敛到
/// <see cref="TryMapSourceToBackBuffer"/> / <see cref="BackBufferToSource"/>，
/// 入口（<see cref="TryReadPixelAtSource"/>）只负责取元数据与派发，不再各写一份公式
/// ——过去两套口径各算各的，是"放大镜和探针取到不同像素"这类问题的温床。</para>
/// </summary>
public static class PixelReadback
{
    /// <summary>片源像素坐标 → 后台缓冲坐标。
    /// <para>视频内容只占据 destination 矩形（letterbox 之外是背景），
    /// 因此必须经 DestX/DestY + 缩放，不能直接按片源尺寸取。</para>
    /// <returns>null 表示无法换算（片源尺寸非法 / 无诊断信息）。</returns></summary>
    public static (int X, int Y)? SourceToBackBuffer(
        int srcX, int srcY, int videoWidth, int videoHeight, RenderTargetInfo rt)
        // 数值行为与历史实现完全一致，只是把计算搬到公共内核里，
        // 使"源→缓冲"与"缓冲→源"共用同一组常量与钳制规则。
        => TryMapSourceToBackBuffer(srcX, srcY, videoWidth, videoHeight, rt, out var bx, out var by)
            ? (bx, by)
            : null;

    /// <summary>片源坐标 → 后台缓冲坐标的唯一实现（含 letterbox 偏移 + 交换链钳制）。
    /// <returns>false = 无法换算（片源尺寸非法）。</returns></summary>
    private static bool TryMapSourceToBackBuffer(
        int srcX, int srcY, int videoWidth, int videoHeight, RenderTargetInfo rt,
        out int bufferX, out int bufferY)
    {
        bufferX = 0;
        bufferY = 0;
        if (videoWidth <= 0 || videoHeight <= 0) return false;

        // 无诊断信息（演示模式 / 旧内核）：退回"片源坐标即缓冲坐标"的旧行为。
        // 演示模式下后台缓冲本就等于片源尺寸，不会读错。
        if (rt.DestWidth == 0 || rt.DestHeight == 0)
        {
            bufferX = srcX;
            bufferY = srcY;
            return true;
        }

        // +0.5 取像素中心，避免整除把首行/首列压成 0
        var bx = (int)Math.Floor(rt.DestX + (srcX + 0.5) * rt.DestWidth / (double)videoWidth);
        var by = (int)Math.Floor(rt.DestY + (srcY + 0.5) * rt.DestHeight / (double)videoHeight);

        // 交换链是回读的硬性上界：放大/平移时 Dest 可能超出，必须钳制，
        // 否则请求越界区域（内核返回失败或垃圾数据）。
        bufferX = ClampToBuffer(bx, rt.SwapWidth);
        bufferY = ClampToBuffer(by, rt.SwapHeight);
        return true;
    }

    /// <summary>后台缓冲坐标 → 片源像素坐标（<see cref="TryMapSourceToBackBuffer"/> 的逆变换，
    /// 含 letterbox 偏移扣除 + 片源边界钳制）。
    /// <para>用途：把内核按缓冲坐标系回读的结果，或鼠标在画面上的命中位置，
    /// 还原成对用户有意义的"第几行第几列"。</para>
    /// <returns>null 表示无法换算（片源尺寸非法）。</returns>
    /// <para>⚠ <b>zoom/pan 的逆变换尚未纳入</b>：内核的 zoom/pan（<c>FFF3FP_SetViewTransform</c>）
    /// 如何影响 Dest 矩形与回读坐标系，需要实机取多组样本验证后再接入；此处按
    /// "Dest 矩形即最终成像区域"处理，在 zoom=1/pan=0 下与现有行为一致。</para>
    /// </summary>
    public static (int X, int Y)? BackBufferToSource(
        int bufferX, int bufferY, int videoWidth, int videoHeight, RenderTargetInfo rt)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return null;

        // 无诊断信息：与正向换算对称，退回"缓冲坐标即片源坐标"
        if (rt.DestWidth == 0 || rt.DestHeight == 0)
            return (bufferX, bufferY);

        // 与正向换算互逆：先扣掉 letterbox 偏移，再按 Dest/片源的比例缩回片源尺度。
        // 不加 +0.5：正变换已在片源侧取过像素中心，这里取整即可落回同一个像素格。
        var sx = (int)Math.Floor((bufferX - (int)rt.DestX) * (double)videoWidth / rt.DestWidth);
        var sy = (int)Math.Floor((bufferY - (int)rt.DestY) * (double)videoHeight / rt.DestHeight);

        // 落回片源边界内：光标可能停在黑边（letterbox）上，此时算出来是负数或超界，
        // 不钳制会得到"第 -37 列"这种无意义的下标。
        return (Math.Clamp(sx, 0, videoWidth - 1), Math.Clamp(sy, 0, videoHeight - 1));
    }

    /// <summary>把缓冲坐标钳制到交换链尺寸内。
    /// swapSize == 0 表示内核没给尺寸（旧内核）⇒ 只能用 0 做下界（与改造前一致：
    /// 原实现用 int.MaxValue 作上界，等价于只保留下界 0），不引入新的行为差异。</summary>
    private static int ClampToBuffer(int value, uint swapSize)
        => swapSize > 0 ? Math.Clamp(value, 0, (int)swapSize - 1) : Math.Max(value, 0);

    /// <summary>按<b>片源像素坐标</b>读取像素（内部换算到内核的后台缓冲坐标域）。
    /// <returns>false 表示引擎不支持本次回读。</returns></summary>
    public static bool TryReadPixelAtSource(
        this IPlayerSession session, int srcX, int srcY, out PixelSample sample)
    {
        var media = session.ReadMediaInfo();
        if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0 ||
            !session.ReadRenderTargetInfo(out var rt))
        {
            // 拿不到诊断信息时退回原坐标（旧行为，至少不比修复前差）
            return session.TryReadPixel(srcX, srcY, out sample);
        }
        // 走统一内核，不在入口重复公式（A11）
        if (!TryMapSourceToBackBuffer(srcX, srcY, media.VideoWidth, media.VideoHeight, rt,
                out var bx, out var by))
        {
            sample = default;
            return false;
        }
        return session.TryReadPixel(bx, by, out sample);
    }
}
