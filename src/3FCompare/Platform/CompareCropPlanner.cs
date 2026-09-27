using System;
using Avalonia;
using _3FCompare.Core.Display;

namespace _3FCompare.Platform;

/// <summary>
/// 对比模式下把「格表 + 放大参数」换算成各路子 HWND 的裁剪矩形
/// （即 <see cref="WindowRegionClipper.ApplyRect"/> 的入参）。
///
/// <para><b>需求与内核限制</b>：目标是"各路同步放大 → 裁剪出对应区域 → 再做分割对比"。
/// 内核 <c>SetViewTransform</c> 只能放大、不能任意平移（<c>VideoRenderer.cpp:4509-4514</c> 的
/// <c>max(0,·)</c> 钳制），于是改用 Win32 窗口层决定每路露出哪一块。</para>
///
/// <para><b>换算（唯一正确的形式）</b>：内核把放大后的画面锚在<b>窗口左上角</b>，
/// 所以"看到源画面的哪一块"完全由<b>窗口相对格的位置</b>决定：
/// 窗口相对格左上移 <c>d</c>、同时向右下放大 <c>d</c>（保证仍盖住格），
/// 则格内露出的是源画面 <c>(d … d+格)</c> 区域 —— 这正是内核做不到的平移。
/// 换算的最后一步就是：<c>区域 = 格 - 窗口</c>（窗口相对坐标）。
/// 放大/平移参数<b>不直接进入本类</b>，它们通过"子窗口矩形"进入：窗口被放大/偏移多少，就露出哪一块。</para>
///
/// <para><b>今天 <c>d</c> 有多大</b>：<c>CompareGridView.ArrangeOverride</c> 把窗口钉成恰好等于格，
/// 但 Avalonia 会把排布矩形<b>向外取整到物理像素</b>，于是实测窗口比格表算出的矩形大 ≤1px
/// （<c>--comparemodetest</c> 打印：格 996x1122px、窗口 1396x391px 等，差值即取整外扩）。
/// 即 <c>d ∈ {0, 1px}</c>：裁剪会真的下发（把这 ≤1px 的外扩裁掉，区域与格在容器坐标下完全重合，
/// 不打洞），但"裁出对应区域"这件事仍然需要窗口被真正放大 —— 见
/// <see cref="MainWindow.ApplyCompareCrop"/> 与 docs/26 §9.5。</para>
///
/// <para><b>为什么不能"平移区域"（本类刻意不提供该能力）</b>：区域是"开孔"语义，不重排内容。
/// 若把区域相对格挪动 <c>e</c>，则格内被挪开的那一条 <c>e</c> 会变成空洞（露出父窗口底色）——
/// 因为"格被完整覆盖"要求 区域 ⊇ 格，而区域与格同尺寸 ⇒ 必须 区域 == 格。
/// 本类从构造上满足这一条：区域恒等于"格在窗口坐标下的矩形"，故<b>永不打洞</b>。</para>
///
/// <para><b>坐标与 DPI</b>：入参是<b>对比区容器坐标的物理像素</b>（<c>SetWindowRgn</c> 的坐标是
/// 窗口相对、物理像素，见 <see cref="WindowRegionClipper"/>）；DIP → 物理一律走
/// <see cref="ToPhysical"/>，与调用方共用同一套取整规则。</para>
/// </summary>
public static class CompareCropPlanner
{
    /// <summary>
    /// 裁剪方案。<paramref name="ShouldApply"/> 为 false 时<b>不得</b>调用 <c>SetWindowRgn</c>
    /// （应 <see cref="WindowRegionClipper.Clear"/>）。
    /// </summary>
    /// <param name="Region">窗口相对坐标（物理像素）下必须保持可见的矩形 = 该路的格。</param>
    /// <param name="ShouldApply">true = 这是"真正的裁剪"（区域 ⊊ 窗口，即窗口比格大）；
    /// false = 窗口 == 格，裁剪无意义（此时 <paramref name="Region"/> 即整窗）。</param>
    public readonly record struct CropPlan(Rect32 Region, bool ShouldApply);

    /// <summary>
    /// 归一化格 → 容器坐标 DIP 矩形。
    ///
    /// <para><b>必须与 <c>CompareGridView.ArrangeOverride</c> 的对比模式分支逐字一致</b>
    /// （每格内缩 1px 作缝隙、尺寸减 2px 并钳到 0）：两处不一致时裁剪矩形会与画面实际位置错位。
    /// <c>--comparemodetest</c> 的 <c>AssertCellsDriveLayout</c> 断言"各路 Bounds 等于本公式"，
    /// 即这条一致性已被自测钉住。</para>
    /// </summary>
    public static Rect CellToContainerDip(CellRect cell, double containerWidthDip, double containerHeightDip)
        => new(cell.X * containerWidthDip + 1,
               cell.Y * containerHeightDip + 1,
               Math.Max(0, cell.Width * containerWidthDip - 2),
               Math.Max(0, cell.Height * containerHeightDip - 2));

    /// <summary>DIP → 物理像素。区域坐标是物理像素（Win32 不做 DPI 虚拟化，见 app.manifest 的 PerMonitorV2），
    /// 故所有进入 <see cref="WindowRegionClipper"/> 的坐标都必须先过这里。
    /// <paramref name="scaling"/> ≤ 0（尚未确定，如窗口刚创建）按 1.0 处理。</summary>
    public static int ToPhysical(double dip, double scaling)
    {
        var s = scaling > 0 ? scaling : 1.0;
        return (int)Math.Round(dip * s, MidpointRounding.AwayFromZero);
    }

    /// <summary>整块 DIP 矩形 → 物理像素矩形。四条边都经 <see cref="ToPhysical"/>，
    /// 宽度取两边之差（而不是对宽度单独取整），保证 <c>X + Width == Right</c> 不被取整误差破坏。</summary>
    public static Rect32 ToPhysicalRect(Rect dip, double scaling)
    {
        var left = ToPhysical(dip.X, scaling);
        var top = ToPhysical(dip.Y, scaling);
        var right = ToPhysical(dip.X + dip.Width, scaling);
        var bottom = ToPhysical(dip.Y + dip.Height, scaling);
        return new Rect32(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// 计算某一路的裁剪矩形：区域 = 格在窗口坐标下的矩形。
    /// </summary>
    /// <param name="windowPx">该路子 HWND 在容器坐标中的物理像素矩形（今天 == <c>PlayerSurface.Bounds</c> 换算而来）。</param>
    /// <param name="cellPx">该路格在容器坐标中的物理像素矩形（格表经 <see cref="CellToContainerDip"/> 换算而来）。</param>
    /// <returns>窗口未盖住格（说明布局与格表不一致）或尺寸非法时返回 <c>ShouldApply = false</c>：
    /// 此时裁剪要么无效要么会留洞，宁可不做。</returns>
    public static CropPlan Plan(Rect32 windowPx, Rect32 cellPx)
    {
        if (!windowPx.IsValid || !cellPx.IsValid) return NoCrop(windowPx);
        // 窗口没盖住格：布局与格表已经不一致，此时设区域会让格缺一块。什么都不做最安全。
        if (!windowPx.Contains(cellPx)) return NoCrop(windowPx);

        // 由 Contains 保证 0 ≤ region.X 且 region.Right ≤ window.Width ⇒ 区域恒在窗口内，
        // 且与格同尺寸同位置 ⇒ 格被完整覆盖、窗口越界部分被裁掉。这就是"不打洞"的构造性保证。
        var region = new Rect32(cellPx.X - windowPx.X, cellPx.Y - windowPx.Y, cellPx.Width, cellPx.Height);
        var shouldApply = region.X != 0 || region.Y != 0 ||
                          region.Width != windowPx.Width || region.Height != windowPx.Height;
        return new CropPlan(region, shouldApply);
    }

    /// <summary>"不裁剪"方案：区域 == 窗口自身（窗口相对坐标下就是 (0,0,w,h)）。
    /// 调用方在 <c>ShouldApply == false</c> 时不使用它；返回自洽值只是为了让契约没有歧义。</summary>
    private static CropPlan NoCrop(Rect32 windowPx)
        => new(windowPx.IsValid ? new Rect32(0, 0, windowPx.Width, windowPx.Height) : default, false);

    // ══════════════════════════════════════════════════════════════════════
    // 无缝放大：把子窗口放大到格的 z 倍并偏移，再用区域裁出目标画面块
    // ══════════════════════════════════════════════════════════════════════
    //
    // 为什么不是"平移区域"（见类型注释）：区域是开孔语义，不重排内容。要"裁出画面局部"，
    // 必须让子窗口比自己的格更大 —— 内核把画面 fit 到窗口，窗口 z 倍大 ⇒ 画面真的按 z 倍
    // 重新渲染（内核 PrepareScaledVideo 的目标尺寸就是 destination 尺寸，不是插值放大），
    // 再由区域把格以外的部分裁掉。平移则完全由"窗口相对格的位置"表达，
    // 不受内核 SetViewTransform 的 max(0,·) 钳制影响。

    /// <summary>放大后的几何：<paramref name="WindowDip"/> 是子窗口应占的矩形（容器坐标 DIP），
    /// <paramref name="RegionDip"/> 是必须保持可见的矩形（<b>窗口相对坐标</b> DIP，即
    /// <see cref="WindowRegionClipper"/> 的入参经 DPI 换算前的形态）。</summary>
    /// <param name="WindowDip">子窗口矩形（容器坐标 DIP）。位置可为负（窗口伸到容器左上角之外）。</param>
    /// <param name="RegionDip">窗口相对坐标下的可见矩形，恒等于"格在窗口坐标系中的位置"。</param>
    /// <param name="FitDip">画面在窗口中的适配矩形（窗口相对坐标 DIP）。仅用于诊断与断言。</param>
    public readonly record struct MagnifyGeometry(Rect WindowDip, Rect RegionDip, Rect FitDip);

    /// <summary>
    /// 画面在输出矩形中的适配矩形（aspect-preserving contain + 居中）。
    ///
    /// <para><b>必须与内核 <c>VideoRenderer.cpp</c> 的 <c>CalculateVideoDestination</c> 同构</b>
    /// （<c>limitToNativeSize=false</c>，即 C# 侧从不开启原生尺寸限制时的分支）：
    /// 该函数用整数运算决定宽或高先被填满，再把结果居中。这里的取整规则逐字对齐
    /// （<c>(a*sw + sh/2)/sh</c> 后向远离零取整、<c>(ow-w)/2</c> 居中），否则我们的"画面块"
    /// 会与内核实际画出来的差一个像素级偏移。交叉验证见 <c>CompareCropPlannerMagnifyTests</c>：
    /// 内核源码里那三条 <c>static_assert</c>（1920×1080→1280×1024、1080×1920→1920×1080、
    /// 640×360→1920×1080 限制原生）被原样搬成用例。</para>
    ///
    /// <para><b>与内核的残差</b>：内核的输入是<b>物理像素</b>（整数 swapchain 尺寸），本方法在
    /// <b>DIP</b> 里算（窗口尺寸是 z 倍的小数）⇒ 除法结果未必是整数，取整后与内核可能差
    /// &lt;1px。这个残差被 Avalonia 自身的"排布矩形向外取整到物理像素"（≤1px）同量级覆盖，
    /// 不会成为误差主项；要彻底消除只能改成物理像素整数域计算，那会让 DIP 侧的排版与区域换算
    /// 分处两个坐标系，得不偿失。</para>
    ///
    /// <para><b>为什么需要它</b>：内核是 letterbox 适配（不是拉伸铺满）。格与源画面宽高比不同时
    /// 窗口内会有黑边，于是"窗口坐标的 x 比例" ≠ "源画面的 x 比例"。若忽略黑边直接按窗口比例裁剪，
    /// 宽高比不同的两路（例如 ABC 模式里通高的 A 与半高的 B/C）会露出<b>不同的源画面区间</b>，
    /// "无缝放大"就名不副实。见 <see cref="Magnify"/>。</para>
    /// </summary>
    /// <param name="sourceWidth">源画面宽（像素）。≤0 表示未知（演示模式）——此时返回整个输出矩形，
    /// 等价于"画面铺满窗口"，这是唯一安全的假设。</param>
    /// <param name="sourceHeight">源画面高（像素）。</param>
    /// <param name="outputWidth">输出（窗口）宽。</param>
    /// <param name="outputHeight">输出（窗口）高。</param>
    /// <returns>窗口相对坐标下的画面矩形；入参非正时返回 <c>(0,0,outputWidth,outputHeight)</c>。</returns>
    public static Rect FitDestination(int sourceWidth, int sourceHeight, double outputWidth, double outputHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0) return default;
        if (sourceWidth <= 0 || sourceHeight <= 0) return new Rect(0, 0, outputWidth, outputHeight);

        double w = outputWidth, h = outputHeight;
        if ((double)outputWidth * sourceHeight <= (double)outputHeight * sourceWidth)
        {
            // 宽先被填满 ⇒ 高度按源宽高比收窄。
            // 用 Floor 而不是四舍五入：内核是整数除法（截断），二者在 .5 处会差 1px
            // （例如 1920x1080 放进 1280x1024：内核得 720，四舍五入得 721）。
            h = Math.Max(1, Math.Floor((outputWidth * sourceHeight + sourceWidth / 2.0) / sourceWidth));
        }
        else
        {
            w = Math.Max(1, Math.Floor((outputHeight * sourceWidth + sourceHeight / 2.0) / sourceHeight));
        }
        w = Math.Min(w, outputWidth);
        h = Math.Min(h, outputHeight);
        // 居中偏移同样截断（内核返回值是 uint32，隐式截断）
        return new Rect(Math.Floor((outputWidth - w) / 2), Math.Floor((outputHeight - h) / 2), w, h);
    }

    /// <summary>各路源尺寸的<b>最小宽高</b> —— 像素级对齐的基准尺寸。
    /// 未知路（(0,0)：演示模式 / 纯音频 / 会话已释放）不参与；全部未知时返回 (0,0)。
    ///
    /// <para><b>为什么取最小值而不是某一路</b>：基准若取高分辨率的那一路，
    /// 低分辨率路要露出的像素数会<b>超过它自己的整幅画面</b> ⇒ 无法表达（只能退回整幅，
    /// 对齐失效）。取最小值则每一路都容得下，对齐对所有路同时成立。</para></summary>
    public static PixelSize MinSourceSize(IReadOnlyList<PixelSize> sources)
    {
        var minW = 0;
        var minH = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            if (s.Width <= 0 || s.Height <= 0) continue;
            if (minW == 0 || s.Width < minW) minW = s.Width;
            if (minH == 0 || s.Height < minH) minH = s.Height;
        }
        return new PixelSize(minW, minH);
    }

    /// <summary>像素级对齐下某一路的<b>有效放大倍数</b>。
    ///
    /// <para><b>推导（关键：只改裁剪位置做不到像素级对齐）</b>：<see cref="Magnify"/> 的构造决定
    /// "格恒好露出源画面的 1/z"，即露出的源像素尺寸恒为 <c>(W/z, H/z)</c> —— 只挪 crop
    /// 只能改变位置，改变不了尺寸。要让各路露出<b>相同像素尺寸</b>，必须让各路的<b>有效 z 不同</b>：
    /// <c>z_i = z·W_i/baseW</c> ⇒ 露出 <c>(W_i/z_i) = baseW/z</c>，与路号无关。</para>
    ///
    /// <para><b>以宽度为基准</b>：宽高比不同的两路无法同时对齐宽与高（只能对齐一个维度），
    /// 取横向分辨率作基准（它是"分辨率"通常所指的维度）。宽高比一致时纵向自动也对齐。</para>
    ///
    /// <para><b>副作用必须知晓</b>：<c>z_i ≥ z</c>，高分辨率路的窗口会<b>比共用倍率大得多</b>
    /// （4K 与 1080p 同场、z=2 ⇒ 4K 路的有效倍率为 8）。像素预算闸门必须按 <c>z_i</c> 估算，
    /// 否则会低估到 1/16 —— 见 <c>EstimateCompareMagnifyPixels</c>。</para></summary>
    /// <param name="zoom">共用倍率（≤1 按 1）。</param>
    /// <param name="sourceWidth">该路源宽（像素）。</param>
    /// <param name="baseWidth">基准源宽（各路最小值）。</param>
    /// <returns>该路的有效倍率；源尺寸或基准非法时返回 <paramref name="zoom"/>（退化）。</returns>
    public static double AlignedZoom(double zoom, int sourceWidth, int baseWidth)
    {
        if (sourceWidth <= 0 || baseWidth <= 0) return zoom > 1.0 ? zoom : 1.0;
        var z = zoom > 1.0 ? zoom : 1.0;
        // 基准取最小值 ⇒ 比值 ≥ 1 ⇒ z_i ≥ z，不会出现"比共用倍率还小"的窗口
        return z * ((double)sourceWidth / baseWidth);
    }

    /// <summary>像素级对齐下某一路的裁剪分量（源画面归一化坐标）。
    ///
    /// <para><b>推导</b>：共用参数 <paramref name="viewFraction"/>（0~1，0=贴左/上、1=贴右/下）
    /// 表示"视口在可平移范围内的位置"。可平移的<b>源像素</b>范围为
    /// <c>baseSize·(1-1/z)</c>（基准路整个可平移区间），故露出区间的<b>像素起点</b>
    /// <c>p = view·baseSize·(1-1/z)</c>；各路起点必须相同 ⇒ 归一化分量
    /// <c>crop = p / sourceSize</c>。</para>
    ///
    /// <para><b>恒在画面内</b>：<c>crop + 1/z_i = (baseSize/sourceSize)·(view·(1-1/z) + 1/z) ≤ 1</c>
    /// （<c>baseSize ≤ sourceSize</c>、<c>view ≤ 1</c>）⇒ 区域不会越出画面、也不会打洞。</para>
    ///
    /// <para><b>与相对对齐的分界</b>：相对对齐下 crop 直接就是归一化位置、各路相同；
    /// 这里 crop 逐路不同、且 <paramref name="viewFraction"/> 的含义从"归一化位置"变成了
    /// "可平移范围内的相对位置" —— 语义变了，故调用方必须经本方法取值。</para></summary>
    public static double AlignedCrop(double viewFraction, double zoom, int sourceSize, int baseSize)
    {
        if (sourceSize <= 0 || baseSize <= 0) return ClampUnit(viewFraction);

        var z = zoom > 1.0 ? zoom : 1.0;
        var p = ClampUnit(viewFraction) * baseSize * (1.0 - 1.0 / z);
        var crop = p / sourceSize;

        // 防御：钳到 [0, 1-1/z_i]，保证区域右边界不出画面（正常路径本就满足）
        var zi = AlignedZoom(zoom, sourceSize, baseSize);
        var maxCrop = MaxCropFraction(zi);
        return crop < 0 ? 0 : crop > maxCrop ? maxCrop : crop;
    }

    /// <summary>把视口位置钳到 [0,1]；NaN → 0（NaN 矩形会让 <c>SetWindowRgn</c> 收到翻转矩形）。</summary>
    private static double ClampUnit(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        return v > 1 ? 1 : v;
    }

    /// <summary>以画面上的某点为锚点缩放：求新倍率下应当露出的区间左上角（源画面归一化坐标）。
    ///
    /// <para><b>推导</b>：设当前倍率 <c>z0</c>、露出区间左上角 <c>u0</c>、锚点的源坐标 <c>u</c>
    /// （通常是光标下的那一点）。缩放前后锚点在屏幕上的相对位置必须不变：
    /// <c>(u-u0)·z0 == (u-u1)·z1</c> ⇒ <c>u1 = u - (u-u0)·z0/z1</c>。</para>
    ///
    /// <para><b>为什么抽成纯函数</b>：它是"滚轮锚点缩放"的唯一算术，留在 UI 侧就只能靠
    /// 真机目视验证（光标位置无法在自测里精确控制）。抽出来后 <c>CompareCropPlannerAnchorTests</c>
    /// 能直接钉住"锚点不动"这条性质 —— 摘掉 <c>z0/z1</c> 这个比例因子（写成 <c>1/z1</c>）
    /// 时用例会判红，而那正是"从 2× 继续放大时画面会跳"的成因。</para>
    ///
    /// <para>返回值<b>未</b>钳到 <c>[0, MaxCropFraction(z1)]</c>：钳位是调用方的责任
    /// （它需要同时对 X / Y 做，并在此后还可能因超预算整体放弃）。</para></summary>
    /// <param name="anchorFraction">锚点的源画面归一化坐标（0~1）。</param>
    /// <param name="currentCrop">当前露出区间左上角的源画面归一化坐标。</param>
    /// <param name="zoomFrom">当前倍率（≤1 视为 1）。</param>
    /// <param name="zoomTo">目标倍率（≤1 时原样返回 <paramref name="currentCrop"/>）。</param>
    public static double AnchorCrop(double anchorFraction, double currentCrop, double zoomFrom, double zoomTo)
    {
        if (!(zoomTo > 0)) return currentCrop;
        var z0 = zoomFrom > 1.0 ? zoomFrom : 1.0;
        if (double.IsNaN(anchorFraction) || double.IsNaN(currentCrop)) return 0;
        return anchorFraction - (anchorFraction - currentCrop) * (z0 / zoomTo);
    }

    /// <summary>裁剪参数的合法上界：露出 1/z 的画面 ⇒ 左上角最多到 <c>1 - 1/z</c>。
    /// 超过它区域就会越出窗口，而 <c>SetWindowRgn</c> 会静默裁掉越界部分 ⇒ 格内出现空洞。</summary>
    public static double MaxCropFraction(double zoom)
        => zoom > 1 ? 1.0 - 1.0 / zoom : 0.0;

    /// <summary>
    /// 放大换算（唯一正确形式，已独立验算，见 <c>.review_pr/verify_magnify.py</c> 与
    /// <c>CompareCropPlannerMagnifyTests</c>）：
    ///
    /// <code>
    /// 设格 = (cx, cy, cw, ch)（容器坐标 DIP），z = 放大倍数，u/v = 要露出的画面块左上角
    /// （<b>源画面归一化坐标</b>，0~1，钳到 [0, 1-1/z]）
    ///
    /// 窗口尺寸 = 格尺寸 × z                       W.size = (cw·z, ch·z)
    /// 画面适配 = letterbox(W)                      fit = FitDestination(src, W.size)
    /// 区域     = fit 内的 u/v 处，尺寸 fit/z        R = (fit.x + u·fit.w, fit.y + v·fit.h, fit.w/z, fit.h/z)
    /// 窗口位置 = 格左上 − 区域左上                  W.pos = (cx − R.x, cy − R.y)
    /// </code>
    ///
    /// <para><b>为什么这样就对</b>：窗口被放大到 z 倍，内核把整幅画面 fit 进窗口 ⇒ 画面在窗口中
    /// 是 <c>fit</c>；区域取 <c>fit</c> 内 u 处、尺寸 <c>fit/z</c> ⇒ 格内露出的正是源画面
    /// <c>[u, u+1/z]</c>；而窗口位置取"格左上 − 区域左上"⇒ 区域在容器坐标下<b>恒等于格</b>
    /// （<c>W.pos + R = 格左上</c>），格被完整覆盖、窗口其余部分被裁掉，<b>不打洞</b>。</para>
    ///
    /// <para><b>区域恒在窗口内的构造性证明</b>：<c>R.x ≥ 0</c>（fit.x ≥ 0 且 u ≥ 0）；
    /// <c>R.x + R.w = fit.x + u·fit.w + fit.w/z ≤ fit.x + (1−1/z)·fit.w + fit.w/z = fit.x + fit.w ≤ W.w</c>
    /// （末步用 fit ⊂ 窗口）。故 u/v 一旦钳到 <c>1−1/z</c>，区域必不出界。</para>
    ///
    /// <para><b>z ≤ 1 是恒等退化</b>：窗口 == 格、区域 == 整个窗口（<c>ShouldApply</c> 意义上的"不裁剪"），
    /// 调用方应当直接走既有的 <see cref="Plan"/> 路径 —— 本方法给出自洽值只为让契约无歧义。</para>
    /// </summary>
    /// <param name="cellDip">格矩形（容器坐标 DIP）。应与 <see cref="CellToContainerDip"/> 的结果一致。</param>
    /// <param name="zoom">放大倍数。≤1 视为 1（恒等）。</param>
    /// <param name="cropX">要露出的画面块左上角，<b>源画面归一化 x</b>（0~1）。自动钳到 <c>[0, 1-1/z]</c>。</param>
    /// <param name="cropY">同上，y。</param>
    /// <param name="sourceWidth">该路源画面宽（像素）。≤0 = 未知 ⇒ 按"画面铺满窗口"处理。</param>
    /// <param name="sourceHeight">该路源画面高（像素）。</param>
    public static MagnifyGeometry Magnify(Rect cellDip, double zoom, double cropX, double cropY,
        int sourceWidth, int sourceHeight)
    {
        if (!(zoom > 1.0))
        {
            // 恒等：窗口 == 格；区域 == 整个窗口（"不裁剪"）。
            var id = new Rect(0, 0, Math.Max(0, cellDip.Width), Math.Max(0, cellDip.Height));
            return new MagnifyGeometry(new Rect(cellDip.X, cellDip.Y, id.Width, id.Height), id, id);
        }

        var maxCrop = MaxCropFraction(zoom);
        cropX = ClampCrop(cropX, maxCrop);
        cropY = ClampCrop(cropY, maxCrop);

        var wW = Math.Max(0, cellDip.Width) * zoom;
        var wH = Math.Max(0, cellDip.Height) * zoom;

        var fit = FitDestination(sourceWidth, sourceHeight, wW, wH);
        var rX = fit.X + cropX * fit.Width;
        var rY = fit.Y + cropY * fit.Height;
        var rW = fit.Width / zoom;
        var rH = fit.Height / zoom;

        // 内容框**居中**到格里（原先钉在格的左上角）。居中不改变区域尺寸、也不改变区域在
        // 窗口内的位置 ⇒ 回推到源画面的区间一动不动，"各路恒露出整幅的 1/zoom"这条构造性质
        // （见 Magnify_WithLetterbox_RegionSizeIsFitDividedByZoom 与自测判据⑤）原样保持。
        // 改的只是窗口偏移，而 Arrange 与裁剪下发同源于本函数 ⇒ 两侧自动一致。
        // rW ≤ cell.Width 恒成立（fit.Width ≤ wW = cell.Width·zoom），故偏移非负、无需钳。
        var offX = (cellDip.Width - rW) / 2;
        var offY = (cellDip.Height - rH) / 2;

        return new MagnifyGeometry(
            new Rect(cellDip.X + offX - rX, cellDip.Y + offY - rY, wW, wH),
            new Rect(rX, rY, rW, rH),
            fit);
    }

    /// <summary>把裁剪分量钳到 <c>[0, maxCrop]</c>；NaN（未初始化 / 除零）按 0 处理 ——
    /// 与 <c>CompareLayout.ClampFraction</c> 同样的理由：<c>Math.Clamp</c> 会原样放行 NaN，
    /// 而 NaN 一旦进入矩形会让 <c>SetWindowRgn</c> 收到翻转的矩形（表现为窗口整体不可见）。</summary>
    private static double ClampCrop(double v, double maxCrop)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        return v > maxCrop ? maxCrop : v;
    }

    /// <summary>放大方案的物理像素裁剪计划（区域为<b>窗口相对坐标</b>，即
    /// <see cref="WindowRegionClipper.ApplyRect"/> 的入参）。</summary>
    /// <param name="cellDip">格矩形（容器坐标 DIP），应与 <see cref="CellToContainerDip"/> 一致。</param>
    /// <param name="scaling">DIP → 物理像素的缩放（见 <see cref="ToPhysical"/>）。</param>
    /// <returns><c>zoom ≤ 1</c> 或区域取整后退化（宽/高 ≤ 0）时 <c>ShouldApply = false</c>；
    /// 其余情况恒为 true —— 放大后窗口必是区域之外的更大矩形，不下发区域就会露出窗口越界部分。</returns>
    public static CropPlan MagnifyPlan(Rect cellDip, double zoom, double cropX, double cropY,
        int sourceWidth, int sourceHeight, double scaling)
    {
        var region = ToPhysicalRect(Magnify(cellDip, zoom, cropX, cropY, sourceWidth, sourceHeight).RegionDip, scaling);
        return new CropPlan(region, zoom > 1.0 && region.IsValid);
    }
}
