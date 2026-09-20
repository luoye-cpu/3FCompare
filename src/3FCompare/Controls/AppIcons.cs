using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
// 本工程开了 ImplicitUsings，System.IO.Path 在作用域内 ⇒ 必须起别名，否则 Path 二义
using Path = Avalonia.Controls.Shapes.Path;

namespace _3FCompare.Controls;

/// <summary>应用矢量图标表（docs/31 阶段 4：第一批按钮类 + 第二批本地化字符串内嵌字形）。
/// <para><b>为什么需要它</b>：此前按钮图标全是 Unicode 字形（<c>▶</c>/<c>■</c>/<c>🔁</c>/<c>⧉</c>…），
/// 字形覆盖依赖具体字体，缺字就渲染成豆腐块；矢量路径与字体无关。</para>
/// <para><b>AOT 安全</b>：路径数据是源码字面量，查表是普通 <see cref="Dictionary{TKey,TValue}"/> 命中；
/// 不用反射、不做 XAML 资源查找、不按字符串拼资源键（<c>PublishAot=true</c> 下这三样都不可靠）。</para>
/// <para><b>为什么用 <see cref="Path"/> 而不是 <c>PathIcon</c></b>：Avalonia 12.1.1 里 <c>PathIcon</c>
/// 确实存在（已实测可编译），但它靠"主题模板把 Fill 绑到 Foreground"这一层间接约定取色；
/// <see cref="Path"/> 是纯 <c>Shape</c>，<c>Fill</c> 由调用点显式绑到主题令牌（见 <see cref="Create"/>），
/// 换主题/换控件模板都不会让图标失色，行为最直白、可追。</para>
/// <para><b>路径规格</b>：统一 24×24 视口、纯填充（不描边）、只用 M/L/H/V/A/Z。
/// <see cref="Create"/> 用 <c>Stretch=Uniform</c> 缩放 ⇒ 每个图标的<b>最大边</b>都等于目标尺寸，
/// 视觉大小基本一致（细长图标如"减号"例外，这符合预期）。</para>
/// <para><b>子路径一律互不重叠</b>：Avalonia 12 的 <c>StreamGeometry</c> 已没有可写的填充规则
/// （只有 <c>PathGeometry</c> 有），拿不到就<b>不去依赖</b>它 —— 图标里没有任何"靠挖洞实现"的形状
/// （放大镜的镜圈、循环的圆环、音频的声波弧带都用"外弧 + 径向切口 + 内弧 + 切口"围成的
/// <b>C 形/带缺口闭合带</b>，影片框用四条独立边条，折叠箭头用开放折线带），
/// 因此 EvenOdd 与 NonZero 下渲染结果一致。</para>
/// <para><b>渲染尺寸的两个坑</b>（都已实测，别按直觉改）：
/// <list type="number">
/// <item><c>Stretch=Uniform</c> 缩放的是几何的<b>紧包围盒</b>，所以"最大边 = 目标尺寸"是按 bbox 算的，
/// 不是按 24 视口算的：三角（bbox 细长）实际渲染宽只有 ~12.5px，正方块则铺满 16px。
/// 想让某个图标渲染得小一点，<b>改路径坐标无效</b>（bbox 比例不变），只能用 <see cref="OpticalScale"/>。</item>
/// <item>需要"空心"效果时，只能靠上面那种 C 形/带缺口画法，不能外圈减内圈。</item>
/// </list></para>
/// <para><b>解析时机</b>：<c>StreamGeometry.Parse</c> 需要平台渲染接口，故路径<b>按需解析并缓存</b>，
/// 不在静态初始化器里一次性解析全部（避免"平台尚未起来就被触碰"而抛
/// <c>Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'</c>）。</para></summary>
public static class AppIcons
{
    // ---------------- 24×24 视口的路径数据 ----------------

    // 传输栏
    public const string Play = "M7 4 L19.5 12 L7 20 Z";
    public const string Pause = "M7 4.5 H10.5 V19.5 H7 Z M13.5 4.5 H17 V19.5 H13.5 Z";
    /// <summary>停止：正方块。<b>注意</b>：方块的紧包围盒恒为正方，Uniform 拉伸下渲染尺寸就恒等于目标
    /// 尺寸（改这里的坐标没用），故它在传输栏里的视觉重量靠 <see cref="OpticalScale"/> 压下来。</summary>
    public const string Stop = "M6 6 H18 V18 H6 Z";
    public const string PrevFrame = "M16.5 4 L6 12 L16.5 20 Z";
    public const string NextFrame = "M7.5 4 L18 12 L7.5 20 Z";
    /// <summary>后退一秒：两个左三角，共用 x=12 的竖直边（<b>只共边不重叠</b>）。</summary>
    public const string PrevSecond = "M22.5 4 L12 12 L22.5 20 Z M12 4 L1.5 12 L12 20 Z";
    /// <summary>前进一秒：两个右三角，同样只共边。</summary>
    public const string NextSecond = "M1.5 4 L12 12 L1.5 20 Z M12 4 L22.5 12 L12 20 Z";
    /// <summary>循环：带缺口的圆环 + 箭头头（缺口在正上方约 60°，缺口本身就是"不闭合"）。
    /// <para>环带是"外弧 → 径向切口 → 内弧 → 径向切口"围成的<b>一条简单闭合折线</b>
    /// （<b>不是</b>"外圆挖内圆"）；箭头头三角与环带只在切口那条边上相接、不重叠。
    /// 两条子路径因此都不依赖填充规则。</para>
    /// <para>原先画的是 ⇄（上下两条反向箭头），语义更接近"重复/交换"，不是直观的"循环"。</para></summary>
    public const string Loop =
        "M16.3 4.55 A8.6 8.6 0 1 1 7.7 4.55 L8.95 6.72 A6.1 6.1 0 1 0 15.05 6.72 Z " +
        "M7.08 3.47 L11.1 4.03 L9.58 7.8 Z";
    public const string Add = "M10.5 4 H13.5 V10.5 H20 V13.5 H13.5 V20 H10.5 V13.5 H4 V10.5 H10.5 Z";
    public const string Remove = "M4 10.5 H20 V13.5 H4 Z";

    // 侧栏 / 面板
    /// <summary>折叠箭头（左）：<b>开放式折线</b>带 —— 两条 45° 平行边条在左端汇成尖角，右端是平切口，
    /// 整条带是一条简单闭合折线（不靠挖洞）。
    /// <para><b>为什么不用实心三角</b>：实心三角与 <see cref="PrevFrame"/> 只差宽高比（0.5 vs 0.66），
    /// 16px 下逐像素差异度只有 30.9%（右侧那对更糟，只有 16.0%），基本等于同一枚图标；
    /// 改成折线带后升到 39.8% / 57.8%（见 .review_pr/iconrender 的前后对照）。</para></summary>
    public const string ChevronLeft = "M14.5 4.5 L7 12 L14.5 19.5 L18.74 19.5 L11.24 12 L18.74 4.5 Z";
    /// <summary>折叠箭头（右）：<see cref="ChevronLeft"/> 的镜像。</summary>
    public const string ChevronRight = "M9.5 4.5 L17 12 L9.5 19.5 L5.26 19.5 L12.76 12 L5.26 4.5 Z";
    /// <summary>探针：滴管/取样管（"像素探针"= 取样某个像素，滴管是它的通用隐喻）。
    /// <para><b>为什么不沿用十字准星</b>：离屏看，十字准星 + 中心方块更容易被读成"靶心/瞄准"，
    /// 而本图标所在的 Tab 是"像素探针"（<c>Menu_Probe = 像素探针</c>）—— 语义是取样不是瞄准。
    /// 形状 = 尖嘴 + 细管 + 粗头，一条简单闭合折线（8 个顶点，45° 轴）。</para></summary>
    public const string Probe =
        "M4.6 19.4 L7.08 19.19 L10.96 15.3 L12.38 16.71 L16.27 12.82 L11.18 7.73 L7.29 11.62 L8.7 13.04 Z";
    public const string Bookmarks = "M6 3 H18 V21 L12 16.2 L6 21 Z";
    /// <summary>偏移：一根水平双向箭头（↔）。刻意<b>不</b>画成"上下两条反向箭头"——
    /// 那种构图与 <see cref="Loop"/>（循环）语义重叠，在 rail 上挨着看会分不清。</summary>
    public const string Offset = "M2 12 L7 8 V10.5 H17 V8 L22 12 L17 16 V13.5 H7 V16 Z";
    /// <summary>媒体：四条独立边条拼成的画框 + 中间播放三角（边条互不重叠，故不需要挖洞）。</summary>
    public const string Media =
        "M2 3 H22 V5.5 H2 Z M2 18.5 H22 V21 H2 Z " +
        "M2 5.5 H4.5 V18.5 H2 Z M19.5 5.5 H22 V18.5 H19.5 Z " +
        "M9.5 8.5 L15.5 12 L9.5 15.5 Z";
    /// <summary>音频：喇叭 + <b>两条独立声波弧带</b>。
    /// <para>弧带 = 外弧 + 径向切口 + 内弧 + 径向切口，各是一条简单闭合折线；
    /// 两条弧带的半径区间（2.7–4.6 与 6.3–8.2）不相交，故互不重叠。</para>
    /// <para>原先"声波"是一枚实心小三角，看起来像喇叭旁又放了个播放键；改成弧带后不再与
    /// <see cref="Play"/> 混淆。</para></summary>
    public const string Audio =
        "M3 9 H7.5 L12.5 4 V20 L7.5 15 H3 Z " +
        "M13.91 10.09 L15.25 8.75 A4.6 4.6 0 0 1 15.25 15.25 L13.91 13.91 A2.7 2.7 0 0 0 13.91 10.09 Z " +
        "M16.46 7.55 L17.8 6.2 A8.2 8.2 0 0 1 17.8 17.8 L16.46 16.46 A6.3 6.3 0 0 0 16.46 7.55 Z";
    /// <summary>放大镜：C 形镜圈（在 45° 方向留 44° 缺口，缺口正好被手柄占据）+ 手柄。
    /// 镜圈是一条连续闭合带（外弧 + 径向线 + 内弧 + 闭合），<b>不是</b>"外圆挖内圆"。</summary>
    public const string Magnifier =
        "M16.94 13.23 A7 7 0 1 0 13.23 16.94 L12.26 14.64 A4.5 4.5 0 1 1 14.64 12.26 Z " +
        "M13.9 16.02 L16.02 13.9 L21.86 19.74 L19.74 21.86 Z";
    /// <summary>复制：后页（只画上边与右边，L 形）+ 前页（实心矩形），两块不重叠。</summary>
    public const string Copy = "M8 3 H21 V16 H18 V6 H8 Z M3 8 H16 V21 H3 Z";

    // ---------------- 第二批：本地化字符串内嵌字形的替代 ----------------
    // 来源是 LanguageManager 里 "◀ 100ms" / "1帧 ▶" / "◎ 对齐于此帧" / "↺ 归零" / "⇩ 导出…"
    // 这类"字形 + 文案"的 value。这些字形同样受字体覆盖影响，故改为与字体无关的矢量几何。

    /// <summary>步进（左）：横向箭头（杆 + 三角头）。替代 <c>Offset_MsMinus</c> / <c>Offset_FrameMinus</c> 里的 ◀。
    /// <para>单条简单闭合折线（杆与头连成一体），<b>不是</b>"杆 + 独立三角"两块拼接 —— 后者在 12~13px 下
    /// 接缝处的抗锯齿会露出一条浅线。</para></summary>
    public const string NudgeBack = "M22 10.5 H9.5 V5.5 L2 12 L9.5 18.5 V13.5 H22 Z";
    /// <summary>步进（右）：<see cref="NudgeBack"/> 的镜像。</summary>
    public const string NudgeForward = "M2 10.5 H14.5 V5.5 L22 12 L14.5 18.5 V13.5 H2 Z";
    /// <summary>导出：向下箭头 + 托盘。替代 <c>Bookmark_Export</c> 里的 ⇩。
    /// <para>⇩ 是"空心向下箭头"，而空心靠挖洞实现 —— 本工程不能用填充规则，故改画托盘式"导出/另存"
    /// （也是更通用的隐喻）。5 条子路径互不重叠：箭头杆底边与三角头只<b>共边</b>（y=10.5），
    /// 托盘三条边条之间只共边（y=19），三角头（x 7~17）与托盘左右边条（x 3~6 / 18~21）在 x 上不相交。</para></summary>
    public const string Export =
        "M10.5 3 H13.5 V10.5 H10.5 Z M7 10.5 L12 16 L17 10.5 Z " +
        "M3 14 H6 V19 H3 Z M18 14 H21 V19 H18 Z M3 19 H21 V22 H3 Z";
    /// <summary>对齐：靶心（C 形环 + 中心点）。替代 <c>Offset_Align</c> 里的 ◎。
    /// <para>环是"外弧 → 径向切口 → 内弧 → 径向切口"围成的<b>一条简单闭合折线</b>（<b>不是</b>外圆挖内圆），
    /// 缺口 50° 开在正上方；中心点半径 2.5 &lt; 环内半径 6，两块不重叠。</para></summary>
    public const string Align =
        "M8.197 3.843 A9 9 0 1 0 15.803 3.843 L14.536 6.562 A6 6 0 1 1 9.464 6.562 Z " +
        "M9.5 12 A2.5 2.5 0 1 1 14.5 12 A2.5 2.5 0 1 1 9.5 12 Z";
    /// <summary>归零：左向箭头 + 左侧竖杠（"回到零点"）。替代 <c>Offset_Reset</c> 里的 ↺。
    /// <para><b>为什么不画成 ↺ 那样的圆环箭头</b>：<see cref="Loop"/> 已经占了"带箭头的圆环"这个形状，
    /// 再画一枚同构的圆环只能靠箭头朝向区分；"竖杠 + 左箭头"（回到起点）既贴合"归零"的语义，
    /// 又拉开了距离 —— 离屏实测 16px 下与 <see cref="Loop"/> 差异度 66.4%、与 <see cref="NudgeBack"/> 50.0%
    /// （同尺寸下 PrevFrame vs NextFrame 这类镜像对是 46.9%，可作参照）。
    /// 竖杠（x ≤ 6.5）与箭头（尖点 x=9）之间留 2.5 的空隙，两块不重叠。</para></summary>
    public const string Reset = "M3.5 4.5 H6.5 V19.5 H3.5 Z M21 10.5 H15.5 V5.5 L9 12 L15.5 18.5 V13.5 H21 Z";

    /// <summary>key → 路径数据。<b>只存字符串</b>，不在这里解析几何（见类注释"解析时机"）。</summary>
    private static readonly Dictionary<string, string> Data = new(StringComparer.Ordinal)
    {
        ["Play"] = Play,
        ["Pause"] = Pause,
        ["Stop"] = Stop,
        ["PrevFrame"] = PrevFrame,
        ["NextFrame"] = NextFrame,
        ["PrevSecond"] = PrevSecond,
        ["NextSecond"] = NextSecond,
        ["Loop"] = Loop,
        ["Add"] = Add,
        ["Remove"] = Remove,
        ["ChevronLeft"] = ChevronLeft,
        ["ChevronRight"] = ChevronRight,
        ["Probe"] = Probe,
        ["Bookmarks"] = Bookmarks,
        ["Offset"] = Offset,
        ["Media"] = Media,
        ["Audio"] = Audio,
        ["Magnifier"] = Magnifier,
        ["Copy"] = Copy,
        ["NudgeBack"] = NudgeBack,
        ["NudgeForward"] = NudgeForward,
        ["Export"] = Export,
        ["Align"] = Align,
        ["Reset"] = Reset,
    };

    /// <summary>已解析几何的缓存。所有调用点都在 UI 线程（控件构造 / 语言切换回调），故不加锁。</summary>
    private static readonly Dictionary<string, StreamGeometry> Cache = new(StringComparer.Ordinal);

    /// <summary><b>光学缩放</b>：给"在 <c>Stretch=Uniform</c> 下会铺满目标框"的图标一个 &lt; 1 的
    /// <b>渲染</b>缩放，把它们压到与同类图标同级的视觉重量。
    /// <para><b>为什么需要它</b>：Uniform 缩放的是几何的<b>紧包围盒</b>，最大边恒等于目标尺寸 ——
    /// 方形几何的 bbox 是正方，渲染尺寸就恒为 <c>size × size</c>，<b>把方块坐标改小完全没用</b>
    /// （bbox 比例不变，缩放系数会跟着变大）。实测 16px 下：<see cref="Stop"/> 的方块渲染 16×16、
    /// 墨迹 256px²；而 <see cref="Play"/> 三角 bbox 细长（12.5×16），渲染 13×16、墨迹只有 116px²。
    /// 两者相差 2.2 倍，传输栏里 Stop 明显偏重。</para>
    /// <para><b>代价可控</b>：这里只改 <c>RenderTransform</c>（绕控件中心缩放"画出来的像"），
    /// <c>Width/Height</c> 仍是 <c>size</c> ⇒ <b>布局框不变</b>，任何调用点的布局都不受影响。
    /// 表里没有的 key 取 1.0，其余 18 个图标逐像素不变（见 .review_pr/iconrender 的前后对照）。</para>
    /// <para><b>0.78 怎么来的</b>：目标是让方块渲染宽度对齐 <see cref="Play"/> 的 12.5px
    /// ⇒ 16 × 0.78 = 12.48 ≈ 12.5。</para></summary>
    private static readonly Dictionary<string, double> OpticalScale = new(StringComparer.Ordinal)
    {
        ["Stop"] = 0.78,
    };

    /// <summary>按 key 取几何（首次调用时解析并缓存，之后共用同一实例）。
    /// <para><b>未命中直接抛</b>：键写错属编程错误，静默返回空几何会变成"按钮上什么都没有"，
    /// 比字形豆腐块更难排查。</para></summary>
    /// <exception cref="KeyNotFoundException">该键未在 <see cref="Data"/> 中登记。</exception>
    public static StreamGeometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (!Data.TryGetValue(key, out var data))
            throw new KeyNotFoundException(
                $"未定义的图标：\"{key}\"。请在 Controls/AppIcons.cs 的 AppIcons.Data 里登记。");
        var geometry = StreamGeometry.Parse(data);
        Cache[key] = geometry;
        return geometry;
    }

    /// <summary>建一个图标控件：几何取自 <see cref="Get"/>，填充色绑到主题令牌
    /// （<see cref="ThemePalette.SetBrush"/> 等价于 XAML 的 <c>{DynamicResource XxxBrush}</c>，
    /// 切主题自动更新；<b>不出现任何硬编码颜色</b>）。
    /// <para>调用方若需要"随状态换色"（如侧栏 rail 的激活态），对同一控件再调一次
    /// <see cref="ThemePalette.SetBrush"/> 改绑令牌即可 —— 同优先级绑定互相替换，
    /// 且必须在主题切换时重跑一次（DynamicResource 绑定本身会自动跟随主题）。</para></summary>
    /// <param name="key">图标 key（见 <see cref="Data"/>）。</param>
    /// <param name="size">目标边长（24 视口按 <c>Stretch=Uniform</c> 缩放到此尺寸）。</param>
    /// <param name="brushKey">主题令牌键，如 <c>TextPrimaryBrush</c> / <c>TextSecondaryBrush</c> / <c>AccentBrush</c>。</param>
    public static Path Create(string key, double size = 16, string brushKey = "TextPrimaryBrush")
    {
        var icon = new Path
        {
            Data = Get(key),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
        };
        // 光学缩放（只缩渲染结果，不动布局框）；表里没有的 key 取 1.0，行为与不加完全一致。
        if (OpticalScale.TryGetValue(key, out var scale) && Math.Abs(scale - 1.0) > 1e-9)
        {
            icon.RenderTransform = new ScaleTransform(scale, scale);
            icon.RenderTransformOrigin = RelativePoint.Center;
        }
        ThemePalette.SetBrush(icon, Path.FillProperty, brushKey);
        return icon;
    }
}
