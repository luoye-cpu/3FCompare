using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace _3FCompare.Platform;

/// <summary>
/// 用 Win32 窗口区域（<c>SetWindowRgn</c>）裁剪窗口的可见部分。
///
/// <para><b>用途</b>：多路视频"先放大、再裁剪出对应区域做分割对比"。内核的
/// <c>SetViewTransform(zoom, panX, panY)</c> 无法实现任意平移（<c>zoom &gt; 1.0001f</c> 才生效，
/// 且 destination 有 <c>max(0, …)</c> 下限钳制，视频铺满时 panX 不产生位移），
/// 因此在窗口层裁剪是绕开内核限制的办法。本类只做裁剪，不碰渲染。</para>
///
/// <para><b>坐标语义（已查证）</b>：<c>SetWindowRgn</c> 的矩形坐标是相对
/// <b>窗口左上角</b>（window 矩形，<b>不是</b>客户区左上角）。MSDN 原文：
/// "The coordinates of a window's window region are relative to the upper-left corner
/// of the window, not the client area of the window."
/// 另注：RTL 布局窗口是相对右上角（本应用不涉及）。
/// 对本项目的 <c>WS_CHILD</c> 子窗口：子窗口的 window 矩形与客户区重合
/// （<c>PlayerSurface</c> 创建时只带 WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS，
/// 无 WS_BORDER / WS_CAPTION 等非客户区），故此处"相对窗口左上角"与
/// "相对客户区左上角"数值相同；但本类一律按 <b>相对窗口左上角</b> 实现，
/// 不要依赖两者相等这一巧合。坐标可为负（表示落在窗口左上角之外）。</para>
///
/// <para><b>DPI</b>：区域坐标是<b>物理像素</b>（设备单位），Win32 不对其做 DPI 虚拟化；
/// 本应用为 PerMonitorV2 感知（见 app.manifest），Win32 窗口坐标本身就是物理像素。
/// 因此从 Avalonia 的 DIP 坐标换算时必须乘以 <c>RenderScaling</c>，这一步由<b>调用方负责</b>。</para>
///
/// <para><b>句柄所有权（已查证，MSDN）</b>：<c>SetWindowRgn</c> <b>成功</b>后，
/// 系统拥有该区域句柄，系统不做拷贝，系统在不再需要时自行删除 ——
/// 调用方<b>绝不可</b>再对该句柄做任何调用，尤其不能 <c>DeleteObject</c>。
/// <b>失败</b>时所有权不转移，句柄仍归调用方，必须自行 <c>DeleteObject</c>，否则泄漏 GDI 区域。
/// 传 <c>NULL</c> 表示把窗口区域置空（恢复整窗可见），不涉及句柄转移。</para>
///
/// <para><b>⚠ 未验证（接线前必读）</b>：本类改的是 Win32 窗口区域，而视频内容是 3FP 内核的
/// D3D11（flip-model swapchain）直接呈现的。窗口区域能否同样裁剪 swapchain 呈现的内容，
/// 官方文档<b>没有明确说明</b>，本项目也<b>尚未在目标机器上实测</b>——不要假定它一定生效。
/// 接线时请先用一路视频做一次手工试验确认。若不生效，替代方案见
/// <see cref="ApplyRect"/> 的备注（改子窗口位置 + 父窗口裁剪，语义等价且不依赖 D3D 行为）。</para>
///
/// <para><b>错误信息</b>：失败时返回 false，且不抛异常；调用方可在同线程上紧接着调用
/// <see cref="GetLastErrorCode"/> 取 Win32 错误码。注意 MSDN 并未把 <c>SetWindowRgn</c>
/// 列入会设置 last-error 的函数，该错误码属<b>尽力而为</b>的调试辅助，不可作为可靠契约。</para>
/// </summary>
public static class WindowRegionClipper
{
    // CombineRgn 的组合模式与返回码（wingdi.h）
    private const int RgnOr = 2;
    private const int RgnError = 0;

    /// <summary>
    /// 把窗口的可见区域限制为指定矩形（坐标语义见类型注释：相对<b>窗口左上角</b>，物理像素，可为负）。
    /// </summary>
    /// <param name="hwnd">目标窗口。必须是非零且有效的窗口句柄。</param>
    /// <param name="x">矩形左边（相对窗口左上角）。</param>
    /// <param name="y">矩形上边（相对窗口左上角）。</param>
    /// <param name="width">宽，必须 &gt; 0。</param>
    /// <param name="height">高，必须 &gt; 0。</param>
    /// <returns>成功为 true；<paramref name="hwnd"/> 为零、宽或高 ≤ 0、坐标溢出 int 范围、
    /// 或 Win32 调用失败时返回 false（不抛异常，且不留悬空句柄）。</returns>
    /// <remarks>
    /// <b>替代方案（若窗口区域对 D3D11 内容不生效）</b>：不裁剪区域，改为移动/缩放子 HWND ——
    /// 让子窗口按"放大后"的完整尺寸渲染，再把它相对父窗口偏移 <c>(-cropX, -cropY)</c>，
    /// 由父窗口（<c>WS_CLIPCHILDREN</c>）自然裁掉越界部分。效果等价，
    /// 且完全不依赖窗口区域与 swapchain 的交互行为，只是要求调用方能控制子窗口位置。
    /// </remarks>
    public static bool ApplyRect(nint hwnd, int x, int y, int width, int height)
    {
        if (hwnd == nint.Zero) return false;

        var rect = new Rect32(x, y, width, height);
        if (!rect.TryGetEdges(out var left, out var top, out var right, out var bottom)) return false;

        var hRgn = CreateRectRgn(left, top, right, bottom);
        if (hRgn == nint.Zero) return false;

        if (!SetWindowRgn(hwnd, hRgn, true))
        {
            // 失败 ⇒ 所有权未转移，句柄仍是我们的，必须释放，否则泄漏 GDI 区域。
            DeleteObject(hRgn);
            return false;
        }
        // 成功 ⇒ 系统接管 hRgn，此处刻意不再 DeleteObject（见类型注释的所有权规则）。
        return true;
    }

    /// <summary>
    /// 组合多个矩形（例如被分割线切成两块）后一次性设为窗口区域。
    /// 入参会先经 <see cref="NormalizeRects"/> 归一化；归一化后为空则返回 false 且不改变当前区域。
    /// </summary>
    /// <param name="hwnd">目标窗口。</param>
    /// <param name="rects">矩形集合，坐标语义同 <see cref="ApplyRect"/>。可为 null。</param>
    /// <returns>成功为 true；句柄为零、无有效矩形、或 Win32 调用失败时返回 false。</returns>
    /// <remarks>要恢复整窗可见请调用 <see cref="Clear"/>，不要传空集合。</remarks>
    public static bool ApplyRects(nint hwnd, IReadOnlyList<Rect32>? rects)
    {
        if (hwnd == nint.Zero) return false;

        var normalized = NormalizeRects(rects);
        if (normalized.Length == 0) return false;
        if (normalized.Length == 1)
        {
            var only = normalized[0];
            return ApplyRect(hwnd, only.X, only.Y, only.Width, only.Height);
        }

        nint combined = nint.Zero;
        var handedOff = false;
        try
        {
            foreach (var rect in normalized)
            {
                if (!rect.TryGetEdges(out var left, out var top, out var right, out var bottom)) continue;

                var part = CreateRectRgn(left, top, right, bottom);
                if (part == nint.Zero) continue;

                if (combined == nint.Zero)
                {
                    combined = part; // 首个矩形直接接管，省一次 Combine
                    continue;
                }

                var mode = CombineRgn(combined, combined, part, RgnOr);
                // part 从未交给系统，无论 Combine 成败都由我们释放。
                DeleteObject(part);
                if (mode == RgnError) return false; // finally 负责释放 combined
            }

            if (combined == nint.Zero) return false;

            handedOff = SetWindowRgn(hwnd, combined, true);
            return handedOff;
        }
        finally
        {
            // 只有"没交出去"时才由我们释放（成功路径由系统接管）。
            if (!handedOff && combined != nint.Zero) DeleteObject(combined);
        }
    }

    /// <summary>清除裁剪，恢复窗口完整可见（<c>SetWindowRgn(hwnd, NULL, TRUE)</c>）。</summary>
    /// <returns>成功为 true；<paramref name="hwnd"/> 为零或 Win32 调用失败时返回 false。</returns>
    public static bool Clear(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        // hRgn = NULL ⇒ 窗口区域置空。没有句柄转移，无需（也不该）DeleteObject。
        return SetWindowRgn(hwnd, nint.Zero, true);
    }

    /// <summary>取调用线程最近的 Win32 错误码（尽力而为的调试辅助，见类型注释）。</summary>
    public static int GetLastErrorCode() => Marshal.GetLastWin32Error();

    /// <summary>
    /// 纯逻辑：过滤掉空/负尺寸矩形，并丢弃被其它矩形完全覆盖的矩形。保持输入顺序，便于单测与预测结果。
    /// </summary>
    /// <param name="rects">原始矩形集合，可为 null。</param>
    /// <returns>归一化后的数组；无有效矩形时返回空数组（不返回 null）。</returns>
    public static Rect32[] NormalizeRects(IReadOnlyList<Rect32>? rects)
    {
        if (rects is null || rects.Count == 0) return Array.Empty<Rect32>();

        var kept = new List<Rect32>(rects.Count);
        foreach (var rect in rects)
        {
            if (!rect.IsValid) continue; // 宽或高 ≤ 0：CreateRectRgn 会得到空区域，直接丢弃

            var covered = false;
            foreach (var k in kept)
            {
                if (k.Contains(rect)) { covered = true; break; }
            }
            if (covered) continue;

            // 新矩形可能吞掉先前保留的小矩形，一并移除，避免区域里留下冗余环
            kept.RemoveAll(k => rect.Contains(k));
            kept.Add(rect);
        }
        return kept.ToArray();
    }

    // ---- P/Invoke（Platform 目录下系统 DLL 沿用既有 DllImport + ExactSpelling 风格，
    //      见 Platform/GdiFrameCapture.cs；自定义原生库才用 LibraryImport，见 WgcFrameCapture.cs）----

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int CombineRgn(nint hDestRgn, nint hSrcRgn1, nint hSrcRgn2, int combineMode);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowRgn(nint hwnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
}

/// <summary>
/// 裁剪矩形：<see cref="X"/>/<see cref="Y"/> 是相对<b>窗口左上角</b>的物理像素偏移，
/// <see cref="Width"/>/<see cref="Height"/> 为尺寸。X/Y 可为负（矩形落在窗口左上角之外）。
/// 与 <see cref="WindowRegionClipper.ApplyRect"/> 的入参顺序一致，避免 Left/Top/Right/Bottom 的
/// 开闭区间歧义。
/// </summary>
public readonly record struct Rect32(int X, int Y, int Width, int Height)
{
    /// <summary>右下角 X（独占边界）。可能溢出 int，需精确比较请用 <see cref="TryGetEdges"/>。</summary>
    public int Right => X + Width;

    /// <summary>右下角 Y（独占边界）。可能溢出 int，需精确比较请用 <see cref="TryGetEdges"/>。</summary>
    public int Bottom => Y + Height;

    /// <summary>宽和高是否均为正（空矩形/负尺寸会被 CreateRectRgn 解释为空区域）。</summary>
    public bool IsValid => Width > 0 && Height > 0;

    /// <summary>是否完全覆盖 <paramref name="other"/>（用 long 运算，避免极端坐标下的溢出误判）。</summary>
    public bool Contains(Rect32 other)
        => IsValid && other.IsValid
           && other.X >= X && other.Y >= Y
           && (long)other.X + other.Width <= (long)X + Width
           && (long)other.Y + other.Height <= (long)Y + Height;

    /// <summary>
    /// 纯逻辑：校验尺寸为正、且换算成 right/bottom 后不溢出 int 范围，成功则输出四条边。
    /// 溢出会让 <c>CreateRectRgn</c> 收到翻转的矩形（被当成空区域），表现为"调用成功但窗口不可见"，
    /// 因此宁可在这里明确拒绝。
    /// </summary>
    public bool TryGetEdges(out int left, out int top, out int right, out int bottom)
    {
        left = X;
        top = Y;
        right = 0;
        bottom = 0;

        if (!IsValid) return false;

        var r = (long)X + Width;
        var b = (long)Y + Height;
        if (r > int.MaxValue || r < int.MinValue) return false;
        if (b > int.MaxValue || b < int.MinValue) return false;

        right = (int)r;
        bottom = (int)b;
        return true;
    }
}
