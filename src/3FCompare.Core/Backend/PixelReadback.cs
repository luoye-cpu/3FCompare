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
/// <see cref="SourceToBackBuffer(int,int,int,int,RenderTargetInfo)"/> /
/// <see cref="BackBufferToSource(int,int,int,int,RenderTargetInfo)"/>，
/// 入口（<see cref="TryReadPixelAtSource"/>）只负责取元数据与派发，不再各写一份公式
/// ——过去两套口径各算各的，是"放大镜和探针取到不同像素"这类问题的温床。</para>
///
/// <para><b>为什么没有 zoom/pan 参数</b>：内核 <c>GetRenderTargetInfo</c> 上报的 Dest 矩形
/// 已经是 zoom/pan <b>之后</b>的实际绘制矩形（<c>VideoRenderer.cpp:4517-4532</c> 就地改写
/// <c>destination</c>，<c>:4561-4564</c> 存进 <c>lastDest*</c>，<c>:4451-4454</c> 原样回传）。
/// 因此本类只要拿实机 <c>rt</c> 就是 zoom-aware 的；再叠加一次 zoom/pan 会重复施加变换。
/// 曾经存在过一套"按 fit 框 + zoom/pan 自算"的重载，因全仓零调用且语义与实机 rt 相反
/// （同名重载、入参含义互斥，极易误用）已整体删除，不保留未走通的生产路径。</para>
/// </summary>
public static class PixelReadback
{
    /// <summary>片源像素坐标 → 后台缓冲坐标。
    /// <para>视频内容只占据 destination 矩形（letterbox 之外是背景），
    /// 因此必须经 DestX/DestY + 缩放，不能直接按片源尺寸取。</para>
    /// <returns>null 表示无法换算（片源尺寸非法）。</returns></summary>
    public static (int X, int Y)? SourceToBackBuffer(
        int srcX, int srcY, int videoWidth, int videoHeight, RenderTargetInfo rt)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return null;

        // 无诊断信息（演示模式 / 旧内核）：退回"片源坐标即缓冲坐标"的旧行为。
        // 演示模式下后台缓冲本就等于片源尺寸，不会读错。
        if (rt.DestWidth == 0 || rt.DestHeight == 0) return (srcX, srcY);

        // +0.5 取像素中心，避免整除把首行/首列压成 0
        var bx = FloorToIntClamped(rt.DestX + (srcX + 0.5) * rt.DestWidth / videoWidth);
        var by = FloorToIntClamped(rt.DestY + (srcY + 0.5) * rt.DestHeight / videoHeight);

        // 交换链是回读的硬性上界：放大/平移时 Dest 可能超出，必须钳制，
        // 否则请求越界区域（内核返回失败或垃圾数据）。
        return (ClampToBuffer(bx, rt.SwapWidth), ClampToBuffer(by, rt.SwapHeight));
    }

    /// <summary>后台缓冲坐标 → 片源像素坐标（含 letterbox 偏移扣除 + 片源边界钳制）。
    /// <para>用途：把内核按缓冲坐标系回读的结果，或鼠标在画面上的命中位置，
    /// 还原成对用户有意义的"第几行第几列"。</para>
    /// <para><b>已核实（2026-09-18）</b>：内核 <c>GetRenderTargetInfo</c> 上报的 Dest 矩形是
    /// zoom/pan <b>之后</b>的实际绘制矩形（<c>VideoRenderer.cpp:4561-4564</c> 存的是变换后的
    /// <c>destination</c>）⇒ 直接拿实机 rt 走本重载<b>本就已经是 zoom-aware 的</b>。</para>
    /// <para>与正向换算互逆：先扣 letterbox 偏移，再按比例缩回片源尺度。
    /// 不加 +0.5：正变换已在片源侧取过像素中心，这里取整即可落回同一个像素格
    /// （下采样时因量化仍可能差 1 像素，与历史实现一致）。</para>
    /// <returns>null 表示无法换算（片源尺寸非法）。</returns></summary>
    public static (int X, int Y)? BackBufferToSource(
        int bufferX, int bufferY, int videoWidth, int videoHeight, RenderTargetInfo rt)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return null;

        // 无诊断信息：与正向换算对称，退回"缓冲坐标即片源坐标"
        if (rt.DestWidth == 0 || rt.DestHeight == 0) return (bufferX, bufferY);

        // 与正向换算互逆：先扣掉 letterbox 偏移，再按绘制矩形/片源的比例缩回片源尺度。
        var sx = (int)Math.Floor((bufferX - (double)rt.DestX) * videoWidth / rt.DestWidth);
        var sy = (int)Math.Floor((bufferY - (double)rt.DestY) * videoHeight / rt.DestHeight);

        // 落回片源边界内：光标可能停在黑边（letterbox）上，此时算出来是负数或超界，
        // 不钳制会得到"第 -37 列"这种无意义的下标。
        return (Math.Clamp(sx, 0, videoWidth - 1), Math.Clamp(sy, 0, videoHeight - 1));
    }

    /// <summary>把缓冲坐标钳制到交换链尺寸内。
    /// swapSize == 0 表示内核没给尺寸（旧内核）⇒ 只能用 0 做下界（与改造前一致：
    /// 原实现用 int.MaxValue 作上界，等价于只保留下界 0），不引入新的行为差异。</summary>
    private static int ClampToBuffer(int value, uint swapSize)
        => swapSize > 0 ? Math.Clamp(value, 0, (int)swapSize - 1) : Math.Max(value, 0);

    /// <summary>向下取整并拒绝 NaN/负值/溢出：极端 zoom 会让中间量远超 int 值域，
    /// 直接 <c>(int)</c> 强转结果未定义，这里先钳进 int 范围，保证不产生脏坐标。</summary>
    private static int FloorToIntClamped(double value)
    {
        if (!(value > 0)) return 0;              // 含 NaN
        return value >= int.MaxValue ? int.MaxValue : (int)Math.Floor(value);
    }

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
        // 走统一内核，不在入口重复公式（A11）。
        // 内核上报的 Dest 已是 zoom/pan 之后的实际绘制矩形 ⇒ 换算里不再叠加变换。
        var mapped = SourceToBackBuffer(srcX, srcY, media.VideoWidth, media.VideoHeight, rt);
        if (mapped is null)
        {
            sample = default;
            return false;
        }
        return session.TryReadPixel(mapped.Value.X, mapped.Value.Y, out sample);
    }
}
