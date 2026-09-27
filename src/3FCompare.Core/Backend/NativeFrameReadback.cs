namespace _3FCompare.Core.Backend;

/// <summary>一次原生帧回读的目标矩形（后台缓冲坐标域）。</summary>
public readonly record struct FrameReadbackRect(int X, int Y, int Width, int Height);

/// <summary>
/// 原生帧回读的<b>目标矩形推算</b>（纯算术，无原生调用、无状态）。
///
/// <para><b>为什么在 Core 而不在 UI</b>：这段算术原本内联在
/// <c>MainWindow.CaptureNativeFrame</c> 里，而该方法挂在一个 <c>Window</c> 子类上
/// （必须构造 Avalonia 窗口才跑得起来）⇒ 于是"按 swap 裁剪 / 无 RTInfo 退回整面"
/// 这两条最容易被写错的边界，此前没有任何自动断言（docs/41 §4.4/§4.5 第 12 项）。
/// 算术本身不依赖任何 UI 类型，下沉到 Core 后即可用纯托管输入覆盖全部分支。</para>
///
/// <para><b>语义（与下沉前的 UI 调用点逐行等价，仅溢出分支收紧）</b>：内核回读的坐标域是
/// <b>后台缓冲</b>而非视频源，视频内容只占据 destination 矩形（letterbox 之外是背景），
/// 所以必须先取 <c>RenderTargetInfo</c> 定位目标矩形；交换链尺寸是回读的硬性上界
/// （放大/平移时 Dest 矩形可能超出），必须裁剪，否则会请求越界区域。
/// 唯一与原内联版本不同的一处：原版只兜住坐标溢出、尺寸溢出会落到 <c>null</c>，
/// 两者判据不一致（详见 <see cref="ResolveRect"/> 内的溢出注释）。</para>
/// </summary>
public static class NativeFrameReadback
{
    /// <summary>
    /// 把一次 RTInfo 读取结果折算成"可安全回读的矩形"。
    /// </summary>
    /// <param name="renderTargetInfoAvailable"><c>ReadRenderTargetInfo</c> 是否成功。</param>
    /// <param name="rt">读到的 RTInfo（<paramref name="renderTargetInfoAvailable"/> 为 false 时被忽略）。</param>
    /// <param name="videoWidth">媒体视频宽（无 RTInfo 时"整面即视频"的假定值）。</param>
    /// <param name="videoHeight">媒体视频高。</param>
    /// <returns>可回读矩形；<c>null</c> 表示本次回读不可行（调用方应回退抓屏）。</returns>
    public static FrameReadbackRect? ResolveRect(
        bool renderTargetInfoAvailable, RenderTargetInfo rt, int videoWidth, int videoHeight)
    {
        // 无 RTInfo（演示模式 / 旧内核）时退回"整面即视频"的假定：
        // 此时把交换链与目标矩形都当作视频尺寸，裁剪退化为恒等。
        if (!renderTargetInfoAvailable || rt.DestWidth == 0 || rt.DestHeight == 0)
        {
            rt = new RenderTargetInfo((uint)videoWidth, (uint)videoHeight,
                (uint)videoWidth, (uint)videoHeight,
                0, 0, (uint)videoWidth, (uint)videoHeight, 8, false);
        }

        // ── 溢出：尺寸两个字段判据 —— 不可信 ⇒ 本次回读整体不可行 ──
        // DestWidth/DestHeight 仍是 uint（内核 FFF3FPRenderTargetInfo 如此），≥ 2^31 时 `(int)`
        // 强转会变负。原先四个字段都用 `> int.MaxValue` 兜 —— 那是因为它们当时都是无符号。
        // 现在 DestX/DestY 是**有符号**的（放大平移会把绘制盒推到后台缓冲左/上侧之外，负原点合法），
        // 那条判断对它们既恒假又无意义，删掉；负原点的处理改到下面的求交里去。
        //
        // 选"一律返回 null"而不是"一律钳制"，理由有三：
        //   ① 坐标钳到 0 后回读出来的是 letterbox 区域，等于**静默交出一张错内容的图**
        //      （调用方无法分辨"裁过"与"没裁"），比明确失败更危险；
        //   ② 尺寸没有可钳的合法值：钳到 0 就是空矩形（照样得返回 null），钳到"交换链剩余"
        //      则会在 SwapWidth 未知（0 ⇒ 上界取 int.MaxValue）时凭空编出约 2^31 宽的矩形，
        //      调用方据此分配位图会直接 OOM —— 拿不可信数据去构造"看起来合法"的请求，
        //      正是这条分支最该避免的事；
        //   ③ null 的语义早已定义好且有现成退路：调用方回退抓屏（见本方法 <returns>）。
        //      fail-closed 且不新增任何调用方分支。
        if (rt.DestWidth > int.MaxValue || rt.DestHeight > int.MaxValue)
        {
            return null;
        }

        // 交换链尺寸未知（0）时视为无上界：宁可不裁，也不要凭 0 裁成空矩形。
        var swapW = rt.SwapWidth > 0 ? (int)rt.SwapWidth : int.MaxValue;
        var swapH = rt.SwapHeight > 0 ? (int)rt.SwapHeight : int.MaxValue;

        // 可回读矩形 = 绘制盒 ∩ 后台缓冲。
        // 左/上侧：**必须按有符号原点求交** —— 负原点意味着盒子的左/上侧已在屏幕外，
        // 可见宽度是 `DestX + DestWidth`，不是 `DestWidth`。旧式 `w = min(DestWidth, swapW − x0)`
        // 在 DestX<0 时会**多数出 |DestX| 个像素**，把屏幕外的内容当成可见区回读回来。
        // 右/下侧：保持既有契约不变（`x0` 钳进 [0, swapW−1] ⇒ 盒体整体越过右界时仍留 1 列可读），
        // 那条行为有测试钉着，且内核加了原点钳制后该形状已不可达 —— 不在这里顺手改语义。
        var x0 = Math.Clamp(rt.DestX, 0, Math.Max(0, swapW - 1));
        var y0 = Math.Clamp(rt.DestY, 0, Math.Max(0, swapH - 1));
        var w = (int)(Math.Min((long)rt.DestX + rt.DestWidth, swapW) - x0);
        var h = (int)(Math.Min((long)rt.DestY + rt.DestHeight, swapH) - y0);
        if (w <= 0 || h <= 0) return null;

        return new FrameReadbackRect(x0, y0, w, h);
    }
}
