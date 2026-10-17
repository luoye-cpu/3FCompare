using System;

using _3FCompare.Themes;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Display;

namespace _3FCompare.Controls;

/// <summary>
/// 网格对比（"标准网格 + A/B 可拖动"合并后的 N 路分区）的<b>分割线 + 可拖动手柄</b>覆盖窗口。
///
/// <para><b>几何从哪儿来</b>：本窗口只持有宿主给的那一份 <see cref="GridPartition"/> 值对象，
/// 线条、手柄、命中区全部向它要（<see cref="GridPartition.ComputeCells"/> /
/// <see cref="GridPartition.HandlePositions"/>）—— 既不在这里重算一份布局（会与宿主的格表漂移），
/// 也不由宿主把像素矩形塞进来（那会把"哪条线可拖"这件事散成两份实现）。
/// 分割线最多 <c>(cols-1)+(rows-1)</c> 条（3×3 即 4 条），每条都是贯穿容器的整线。</para>
///
/// <para><b>为什么必须是独立窗口</b>：每路视频由 <see cref="PlayerSurface"/> 的
/// <c>NativeControlHost</c> 子 HWND 渲染，子 HWND 永远盖在父窗口之上 —— 任何放进
/// <c>CenterPanel</c> 的普通 Avalonia 控件（例如 <see cref="MagnifierOverlay"/>）
/// 在真机模式下都会被画面遮住（docs/10 §A8）。故这里走 <see cref="ThumbnailPopup"/>
/// 同款方案：独立无边框透明 <see cref="Window"/>。</para>
///
/// <para><b>Z 序：靠 Owner，<u>不</u>用 Topmost</b>。真正的需求是「高于主窗口（及其视频子
/// HWND），但<b>不</b>高于其他应用」。<c>Topmost</c> 做不到后半句 —— 它的语义是"高于<b>所有</b>
/// 非 topmost 窗口"，Win32 置顶带与 owner 关系无关，于是切到别的程序时分割线/手柄仍浮在最上面
/// （真机体验缺陷，本类曾如此）。改用 <see cref="Window.Owner"/> 换来的是：不占任务栏、随 owner
/// 最小化隐藏、且与其他应用同处普通 Z 序带（不在置顶带，判据见 <see cref="HasTopmostStyle"/>）。</para>
///
/// <para>⚠ <b>但 owner 只是"盖得住画面"的必要条件，<u>不保证</u>次序</b>："owned 窗口恒定在
/// owner 之上"这条说法已被真机读数否证 —— 同一个 owner 方案、同一条走链判据，放大镜覆盖窗上线前
/// 可确证的 22 次读数里 <b>3 次（14%）排在主窗之下</b>，其中留了重试链的 2 例在 180ms×6 次采样里
/// 恒为 Below（未观测到自愈），定案见 <c>docs/48 §3.7</c>。本站点是它的同构站点，所以
/// <see cref="IsAboveHostInZOrder"/> 读的是<b>真机现状</b>而不是"结构上必然"；本窗口目前只做
/// 持续观测（<see cref="ZOrderSamples"/> / <see cref="ZOrderBelowCount"/>），<b>未</b>装
/// <c>HWND_TOP</c> 补插 —— 要不要装由这轮量出来的 rate 决定。</para>
///
/// <para><b>Owner 必须显式设</b>（见 <see cref="ShowOverlay"/>）：不能只依赖 <c>Show(host)</c> ——
/// 模式循环会在窗口<b>已可见</b>时再次调用 <see cref="ShowOverlay"/>，那条路径不会走
/// <c>Show(owner)</c>。是否真的挂上 owner 见 <see cref="OwnerHwndAttached"/>，
/// 是否还残留置顶见 <see cref="HasTopmostStyle"/>（两者都是可自动断言的回归闸门）。</para>
///
/// <para><b>透明</b>：<c>TransparencyLevelHint = Transparent</c> + 全透明 <c>Background</c>。
/// Avalonia 12 的 Win32 后端在 DirectComposition 可用时走
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> + DComp 合成，逐像素 alpha 有效；
/// 实际拿到的级别见 <see cref="AchievedTransparency"/>，宿主可据此断言而不是"假设能透明"。</para>
///
/// <para><b>鼠标穿透（关键）</b>：覆盖窗口铺满整个对比区，若不处理会吞掉全部鼠标消息，
/// 使视频表面的选中 / 滚轮缩放 / 拖动平移统统失效。因此本窗口子类化自身 HWND，
/// 在 <c>WM_NCHITTEST</c> 中<b>只在分割线 / 手柄的命中区内</b>返回 <c>HTCLIENT</c>
/// （单轴手柄沿轴放开到整条线，交叉点型只放一个圆点，见 <see cref="HandleIndexAt(double,double,double,double,double)"/>），
/// 其余位置一律 <c>HTTRANSPARENT</c>，把消息让给下层的视频子 HWND
/// （视频 HWND 与本窗口同属 UI 线程，<c>HTTRANSPARENT</c> 的同线程转发语义成立）。
/// 钩子是否安装成功见 <see cref="HitTestHookInstalled"/>。</para>
///
/// <para><b>职责边界</b>：本窗口只做「呈现 + 输入」，<b>不</b>自己改布局。
/// 拖动时先本地乐观更新（跟手），再经 <see cref="PartitionChanged"/> 通知宿主。
/// 通知按 16ms（~60Hz）合并提交、并在松手 / 失去捕获时必定补发最后一次，线条与手柄仍每个
/// pointer move 都跟手；代价是宿主那份分区在拖动期间允许落后一两拍，拖动结束后必然对齐
/// （详见 <see cref="ApplyDrag"/> 与 <see cref="FlushPendingPartitionCommit"/>）。</para>
/// </summary>
public sealed class LayoutOverlayWindow : Window
{
    // ═══════════════════════ 公开 API ═══════════════════════

    /// <summary>手柄被拖动、分区变化时触发（值由 <see cref="GridPartition.WithHandleDragged"/>
    /// 产出，权重与下界已在内部钳好）。宿主在此更新自身布局；本窗口不直接改布局。
    /// <para><b>拖动期间的发射节奏</b>：至多每 16ms 一次（~60Hz 合并提交），并在松手 /
    /// 失去捕获时<b>必定</b>补发一次最后位置（见 <see cref="ApplyDrag"/> 与
    /// <see cref="FlushPendingPartitionCommit"/>）。也就是说宿主手上那份 <c>_partition</c>
    /// 在拖动中允许落后一两个 pointer move，但拖动结束后与 <see cref="Partition"/> 必然相等。</para></summary>
    public event Action<GridPartition>? PartitionChanged;

    /// <summary>当前分区（只读，由 <see cref="ShowOverlay"/> / <see cref="Update"/> 设置）。
    /// 拖动期间是本地乐观值（跟手，故会比宿主那份<b>超前</b>一两个 move）；
    /// 松手时那个超前值会补发给宿主，两边最终一致。</summary>
    public GridPartition Partition => _partition;

    /// <summary>手柄是否正在被拖动。</summary>
    public bool IsDragging => _dragging;

    /// <summary>当前各手柄的归一化位置 —— <b>就是绘制与命中测试所用的那一份</b>。
    ///
    /// <para>自测据此断言"N 路网格里真的画了 / 判了 N 条线"，而不是只查 Core 的
    /// <see cref="GridPartition.HandleCount"/>：后者只能证明规则正确，证明不了覆盖层
    /// 真的按它去画与判命中（漏改覆盖层的表现是"第二条线看不见、也抓不住"）。</para></summary>
    public (double X, double Y)[] HandlePositions() => _partition.HandlePositions();

    /// <summary><b>自由拼接形态</b>：由宿主传入的线段表（null = 未启用，走 <c>_partition</c> 的网格线）。
    ///
    /// <para><b>为什么必须单独一条通道，而不是把拼接塞进 <see cref="GridPartition"/></b>：
    /// 网格分区表达的是"每列/每行一个权重"⇒ 每条线必然<b>贯穿整个容器</b>。
    /// 自由拼接允许 <b>T 形交叉</b>（一条竖线只存在于上半），那是"线段"而不是"整线"，
    /// 权重模型表达不了。故这里给一条只读线段表的旁路：有它时按它画、按它判命中；
    /// 没有时完全走既有路径（<b>逐字不变</b>）。</para>
    ///
    /// <para>线段坐标是归一化的 <c>(轴, 位置, 起, 止)</c>，与
    /// <c>MosaicLayout.LogicalLines()</c> 的输出一一对应。</para></summary>
    public void SetFreeformLines(IReadOnlyList<(SplitAxis Axis, double Position, double From, double To)>? lines)
    {
        _freeformLines = lines;
        InvalidateVisual();
    }

    private IReadOnlyList<(SplitAxis Axis, double Position, double From, double To)>? _freeformLines;

    /// <summary>当前是否处于自由拼接形态（线段由宿主给出）。</summary>
    public bool FreeformActive => _freeformLines is { Count: > 0 };

    /// <summary>自由拼接线段数（自测据此断言"T 形下真的画了两段"）。</summary>
    public int FreeformLineCount => _freeformLines?.Count ?? 0;


    /// <summary><c>WM_NCHITTEST</c> 钩子是否安装成功。
    /// <b>为 false 时本窗口会吞掉对比区内的全部鼠标消息</b>，宿主应据此禁用对比模式或降级。</summary>
    public bool HitTestHookInstalled => _hitTestHookInstalled;

    /// <summary>平台实际给到的透明级别（Windows 上期望 <see cref="WindowTransparencyLevel.Transparent"/>）。</summary>
    public WindowTransparencyLevel AchievedTransparency => ActualTransparencyLevel;

    /// <summary>覆盖窗口当前是否已显示。</summary>
    public bool IsOverlayVisible => IsVisible;

    /// <summary>诊断：平台层是否真的把宿主挂成了 owner（<c>GetWindow(hwnd, GW_OWNER)</c> 等于宿主 HWND）。
    /// <para><see cref="Window.Owner"/> 只是 Avalonia 侧状态。只判"非 0"不够：owner 为空且
    /// <c>ShowInTaskbar=false</c> 时 Avalonia 会把窗口挂到离屏父窗口上，那同样非 0，但 Z 序关系
    /// 完全不是我们要的（覆盖层会盖不住视频）。故必须与宿主 HWND 逐一比对。</para></summary>
    public bool OwnerHwndAttached
    {
        get
        {
            if (_hwnd == nint.Zero) return false;
            var owner = GetWindow(_hwnd, GW_OWNER);
            return owner != nint.Zero && owner == _host?.TryGetPlatformHandle()?.Handle;
        }
    }

    /// <summary>诊断：走一次顶层链，报本覆盖层<b>此刻</b>是否排在主窗口之上（顶层 Z 序里更靠前）。
    /// <para>这是 airspace 不退化（分割线/手柄不被视频画面盖住）的可自动判定形式：视频子 HWND
    /// 属于主窗口，只要本窗口高于 owner 就必然高于它们。</para>
    ///
    /// <para>⚠ <b>它读的是真机现状，不是"结构上必然成立"的事</b>：owner 只是必要条件 —— Win32
    /// <b>不保证</b> owned 窗口恒定在 owner 之上，也<b>不保证</b>两者之间不会插进第三个窗口。
    /// 同一套 owner 方案 + 同一条走链判据在放大镜覆盖窗上实测：上线前可确证的 22 次读数里
    /// <b>3 次（14%）为 Below</b>，其中 2 例在 180ms×6 次采样里恒为 Below（未观测到自愈），
    /// 定案见 <c>docs/48 §3.7</c>。⇒ 本属性为 <c>true</c> 只代表"这一拍在上面"，为 <c>false</c>
    /// 则是真出事了（用户看不见分割线）；长期发生率见 <see cref="ZOrderSamples"/> /
    /// <see cref="ZOrderBelowCount"/>。</para>
    ///
    /// <para>⚠ 取证<b>不能</b>换成 <c>WindowFromPoint</c> 一类命中探针：本窗口对
    /// <c>WM_NCHITTEST</c> 在手柄命中圈之外一律返回 <c>HTTRANSPARENT</c>（见
    /// <see cref="HitTestWndProc"/>），命中测试按设计落到下层窗口 ⇒ 该探针对"在不在上面"
    /// 没有分辨力，反而会在覆盖层正常工作时报"被遮挡"。</para></summary>
    public bool IsAboveHostInZOrder => QueryChainPosition() == ChainPos.Above;

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>。
    /// <para>这是自动化唯一能拿到的 Z 序证据 —— 真机 Z 序（"有没有盖住别的应用"）无法在进程内
    /// 断言，但"还在不在置顶带"可以。修掉"浮在所有窗口之上"后此值必须为 <c>false</c>。</para></summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    // ═══════════════ Z 序持续观测（只读数，不动手） ═══════════════
    //
    // 为什么要这一段：上面的判据原先只在自测进入某一步时被读一次，而放大镜站点实测到的翻转出现在
    // **后续的呈现 / 位置回调**上 ⇒ 一次性采样在统计上等于没测。下面这几个计数只走链、只累加，
    // 不调 SetWindowPos、不写样式、不动位置与焦点（纯观测）；是否给本站点装 HWND_TOP 补插，
    // 由这一轮量出来的 rate 决定。

    /// <summary>顶层走链的累计次数（rate 的分母）。只在窗口<b>已显示</b>且自身与宿主 HWND 都拿得到
    /// 时才增加 —— 隐藏窗口仍在顶层链里，那时读到的 Below 与"用户看不见分割线"无关，是假样本。</summary>
    public int ZOrderSamples { get; private set; }

    /// <summary>其中判为 <b>Below</b>（本窗口排在主窗之下 ⇒ 分割线/手柄被视频画面盖住）的次数。</summary>
    public int ZOrderBelowCount { get; private set; }

    /// <summary>其中走完全链都没见到本窗口的次数。它与 Below 是<b>两种病</b>（Z 序 vs show/hide
    /// 竞态或句柄取错），必须分开数，否则会把后者读成 Z 序缺陷。</summary>
    public int ZOrderNotInChainCount { get; private set; }

    /// <summary>其中<b>进入 Below 的边缘</b>次数（上一次读数不是 Below、这次是）。与
    /// <see cref="ZOrderBelowCount"/> 一比就能把"被压下去一次后长期待着（不自愈 —— 放大镜实测
    /// 正是这种）"和"反复抖动"分开：前者 below 很大而 flips=1，后者两者相近。</summary>
    public int ZOrderFlipsToBelow { get; private set; }

    /// <summary>诊断：<b>宿主</b>是否带 <c>WS_EX_TOPMOST</c>（参照
    /// <see cref="MagnifierOverlayWindow.HostHasTopmostStyle"/> 的同名读数）。置顶带整体高于普通带：
    /// 若主窗在置顶带而本窗口不在，那 <c>HWND_TOP</c> 也补不动 —— 这条读数用来区分"补插无效"与
    /// "补插没被执行"，故一并写进翻转日志。</summary>
    public bool HostHasTopmostStyle
    {
        get
        {
            var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
            return hostHwnd != nint.Zero
                   && (GetWindowLongPtr(hostHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        }
    }

    /// <summary>走链的三态（内部判据）。分开"被压到主窗之下"与"压根不在链里"，理由见
    /// <see cref="ZOrderNotInChainCount"/>。</summary>
    private enum ChainPos { Above, Below, NotInChain }

    /// <summary>上一次观测到的状态，<c>null</c> = 还没采过。只用来把日志压到翻转那一拍。</summary>
    private ChainPos? _lastZOrder;

    /// <summary>采样那一刻的 owner / 置顶状态（<c>null</c> = 还没采过）。
    /// 汇总行必须印这两个而不是现场再读一次 —— 详见 <see cref="ObserveZOrder"/>。</summary>
    private bool? _lastOwnerAttached;
    private bool? _lastTopmost;

    /// <summary>走链取样间隔（ms）。取 200 的理由：一次拖动的 pointer move 可达 125+/s，
    /// 200ms 仍能采到几十次；而放大镜站点实测的翻转是**持续态**（180ms×6 次采样里恒为 Below），
    /// 200ms 的步长足够在同样的窗口里看见它 —— 取样再快只是把遍历成本搬到用户手感上。</summary>
    private const long ZOrderSampleIntervalMs = 200;

    /// <summary>上一次真正走链的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _lastZOrderSampleMs;   // 0 = 还没采过（TickCount64 恒 ≥ 间隔 ⇒ 首帧必采）

    /// <summary>自顶向下走一次顶层链。<b>纯查询</b>：不计数、不写日志、不改任何窗口状态。
    /// <para>链首必须用 <c>GetTopWindow(nint.Zero)</c>：<c>GetWindow(0, GW_HWNDFIRST)</c> 返回 0
    /// ⇒ 整条走链一次都不执行 ⇒ 恒判 NotInChain（放大镜站点实测踩过，<c>docs/48 §3.7 ①</c>）。</para></summary>
    private ChainPos QueryChainPosition()
    {
        if (_hwnd == nint.Zero) return ChainPos.NotInChain;
        var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hostHwnd == nint.Zero) return ChainPos.NotInChain;

        for (var h = GetTopWindow(nint.Zero); h != nint.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h == _hwnd) return ChainPos.Above;
            if (h == hostHwnd) return ChainPos.Below;
        }
        return ChainPos.NotInChain;   // 走完全链都没见到本窗口
    }

    /// <summary>累计一次走链读数，并<b>只在"进入 Below"的那一拍</b>落一条日志
    /// （上一次读数不是 Below 而这次是 —— 连续 Below 不重复写；首个读数即 Below 时没有前置状态，
    /// 也写一条并标 <c>无前置→Below</c>：放大镜实测的失效形态正是"一进去就一直 Below"，
    /// 若只认 Above→Below 这一条边，那种情况反而一条证据都不留）。
    ///
    /// <para><b>为什么日志不能无条件写</b>：采样点挂在产品热路径上（拖动分割线、主窗移动/缩放都会
    /// 走到），日志条数必须与"异常回合数"同阶而不能与"调用次数"同阶 —— 本项目有过"组件日志节流是
    /// 全局共享"把判据读成假红的前车之鉴，无条件写日志只会制造噪声、甚至灌满落盘队列。</para>
    ///
    /// <para><b>副作用清单：无</b>。只有 Win32 只读查询（GetTopWindow / GetWindow /
    /// GetWindowLongPtr）与自身计数，不触碰位置、尺寸、Z 序、焦点、激活状态。</para></summary>
    private void ObserveZOrder()
    {
        if (_closed || !IsVisible || _hwnd == nint.Zero) return;
        if ((_host?.TryGetPlatformHandle()?.Handle ?? nint.Zero) == nint.Zero) return;

        // 节流：<see cref="Update"/> 挂在**拖动热路径**上（每个 pointer move 一次），而走链是
        // 从 <c>GetTopWindow(0)</c> 起把整机顶层链走一遍——观测成本不该压在用户手感上。
        // 2026-09-30 实测合并后每会话 SplitChanged/拖动量翻倍，这一条是全类里唯一"每次移动都跑"
        // 的 Win32 遍历。按时间取样后，一次拖动仍有几十个样本，rate 的分母照样立得住；
        // 而"进入 Below 就落日志"这条边沿检测不受影响（漏采的那几次不产生任何计数，见下方守卫）。
        var now = Environment.TickCount64;
        if (now - _lastZOrderSampleMs < ZOrderSampleIntervalMs) return;
        _lastZOrderSampleMs = now;

        var previous = _lastZOrder;
        var pos = QueryChainPosition();
        _lastZOrder = pos;
        // 把"当时是不是真挂上了 owner / 还带不带置顶"一起缓存：汇总行如果在**退出之后**才读
        // 这两个量，会读到隐藏/关闭链已经改过的状态（实测：HideOverlay 末尾读 owner=False，
        // 而会话进行中自测报的是 owner=True）。留痕里印一个误导性的字段，比不印更坏。
        _lastOwnerAttached = OwnerHwndAttached;
        _lastTopmost = HasTopmostStyle;
        ZOrderSamples++;

        if (pos == ChainPos.Below)
        {
            ZOrderBelowCount++;
            if (previous == ChainPos.Below) return;   // 连续 Below：只在进入的那一拍写
            ZOrderFlipsToBelow++;
            var from = previous?.ToString() ?? "无前置";
            AppLog.Warn("ZOrder",
                $"[LayoutOverlayWindow] {from}→Below" +
                $" owner={OwnerHwndAttached} topmost={HasTopmostStyle}" +
                $" hostTopmost={HostHasTopmostStyle}" +
                $" samples={ZOrderSamples} below={ZOrderBelowCount}");
        }
        else if (pos == ChainPos.NotInChain)
        {
            ZOrderNotInChainCount++;
        }
    }

    /// <summary>落一条累计汇总（只在"这一轮观测到此结束"的位置调用，与热路径无关）。
    /// 一次都没采到就不写 —— 那说明这轮压根没进过对比模式，写出来只是噪声。
    ///
    /// <para><b>为什么要 <c>where</c>：本站点的"窗口关闭"那条多半落不了盘</b> —— 宿主的
    /// <c>OnClosing</c> 先调 <c>AppLog.Shutdown()</c>（MainWindow.axaml.cs:3034），之后才在
    /// <c>OnClosed</c> 里 <c>CloseAndDispose()</c>（同文件 3114），writer 已关 ⇒ 那一条被丢弃。
    /// 故 <see cref="HideOverlay"/>（退出对比模式，writer 仍在）是本站点唯一稳定能落盘的汇总点，
    /// 两处都打、用 <c>where</c> 区分。</para></summary>
    private void LogZOrderSummary(string where)
    {
        if (ZOrderSamples <= 0) return;
        AppLog.Info("ZOrder",
            $"[LayoutOverlayWindow] 汇总({where}) samples={ZOrderSamples} below={ZOrderBelowCount}" +
            $" notInChain={ZOrderNotInChainCount} flips={ZOrderFlipsToBelow} last={_lastZOrder}");
        // 同一份读数再落一条**组件日志**：AppLog 那一条按进程分段落盘、且退出路径上多半被丢弃
        // （见方法注释），而 M5/M3 的测量口径本来就是"按 PID 段读组件日志"。
        // 不双写的话，本站点的 Z 序发生率在测量脚本里永远是空的 —— 而"自动常驻网格对比"（M0）
        // 的判词要的正是这个率：读数取不到 ≠ 率为 0。
        // evt 名固定为 OverlayZOrder，脚本侧用它做 grep（改名要同步改 m3/fault 那两支脚本）。
        // owner/topmost 用的是**观测那一刻**缓存的值（见 ObserveZOrder），不是汇总时再读一次：
        // HideOverlay 之后实时读会得到 owner=False（隐藏链改过状态），那是误导性的读数。
        _3FCompare.Diagnostics.ComponentLog.Log(_3FCompare.Diagnostics.Comp.Overlay, "OverlayZOrder", -1,
            $"where={where} samples={ZOrderSamples} below={ZOrderBelowCount}" +
            $" notInChain={ZOrderNotInChainCount} flips={ZOrderFlipsToBelow} last={_lastZOrder}" +
            $" ownerAtSample={_lastOwnerAttached?.ToString() ?? "未采"} topmostAtSample={_lastTopmost?.ToString() ?? "未采"}");
    }

    // ═══════════════════════ 状态 ═══════════════════════

    private Window? _host;
    private Visual? _anchor;

    private GridPartition _partition = GridPartition.Uniform(2, 2, 1);

    private bool _dragging;
    /// <summary>正在被拖的是第几个手柄（<see cref="GridPartition.HandleCount"/> 之内的索引）。
    /// 1×2 只有一个手柄恒为 0；3×3 有 4 条线 ⇒ 0..3 各管一条。
    /// 拖动结束时<b>不</b>复位到 0：模式切换的那一拍若还拿着旧索引，取轴只会拿到"最后一个手柄"，
    /// 而复位成 0 会让"拖动中切模式"把第一条线拽走 —— 留着旧索引是更保守的选择
    /// （<see cref="GridPartition.WithHandleDragged"/> 对越界索引直接返回自身）。</summary>
    private int _dragHandle;

    // ── 宿主通知的合并提交（~60Hz）────────────────────────────────────────
    // 为什么只合"通知宿主"、不合"画线"：PartitionChanged 在宿主侧是 MainWindow.OnPartitionChanged，
    // 一次要做满 9 路子 HWND 的重排 + 一轮裁切派发 + 组件日志；高回报率鼠标每个 pointer move
    // 都戳一遍（125+/s）。线条与手柄仍每拍跟手（_partition + InvalidateVisual 不动），
    // 只有提交被按时间合并。
    // 门限与判据刻意照抄平移路径那套已经<b>实测过</b>的纪律（MainWindow.axaml.cs ~1788-1802，
    // --magnifybench [平移提交] p95=0.27ms、34 次/秒连续提交、出帧 59.87fps 零卡顿，
    // 日志 tests/output/magnifybench-pancommit-z2-20260929.log）：同一个 16ms 门槛、
    // 同一个 Environment.TickCount64，不再造第三套节奏。

        /// <summary>拖动中提交给宿主的最小间隔（ms）。默认 16 ≈ 60Hz —— 与平移档同值，
    /// 也是 <c>FC_GESTURE_COMMIT_MS</c> 未设时的出货行为（逐字等于本次改动前）。
    ///
    /// <para><b>为什么留一个测量档开关</b>：每笔提交在宿主侧都要重排全部子 HWND ⇒ 触发一轮
    /// DXGI 尺寸变更。§十 已经量过"区域调用次数（4 倍）不是崩溃带的杠杆"，那条否证之后
    /// 剩下的唯一干预点就是这里 —— 少搬窗口。所以 <c>FC_GESTURE_COMMIT_MS</c>
    /// （只认 0..1000，其余一律忽略回默认）是 §十五 那条 A/B 的<b>唯一</b>变量，
    /// 不是产品可调项，也不写进任何界面。</para>
    ///
    /// <para>把它设成 0 会关掉合并，风暴判据应如期判红 —— 那是预期，不是回归。</para></summary>
    internal static readonly long GestureCommitIntervalMs = GestureCommitIntervalFromEnv(16);

    private static long GestureCommitIntervalFromEnv(long fallback)
    {
        var raw = Environment.GetEnvironmentVariable("FC_GESTURE_COMMIT_MS");
        return long.TryParse(raw, out var v) && v >= 0 && v <= 1000 ? v : fallback;
    }

    /// <summary>上一次"拖动中提交"的 TickCount64（0 = 还没提交过 ⇒ 第一次移动就提交，
    /// 抓取那一拍不会有 16ms 死区；镜像 <c>BeginComparePan()</c> 对
    /// <c>_lastCompareCommitTicks</c> 的清零）。</summary>
    private long _lastPartitionCommitTicks;

    /// <summary>被 16ms 门限挡下、还<b>没</b>发给宿主的分区（<c>null</c> = 没有欠账）。
    /// 存的是 <see cref="GridPartition"/> 引用本身（<c>WithHandleDragged</c> 已经分配好了），
    /// 每次被挡下只换一个引用 ⇒ 热路径不新增分配，也不建 timer/list。
    /// 欠账必须在松手 / 失去捕获时结清，见 <see cref="FlushPendingPartitionCommit"/>。</summary>
    private GridPartition? _pendingPartitionCommit;

    // ── 拖动热路径的自测计数（口径 = 最近一次抓取，见 BeginHandleDrag 的归零）──────
    // 为什么放在生产类里而不是夹具自己数：夹具数不出"覆盖层内部本来想提交几次"，而合并发生
    // 在 ApplyDrag 的门限里。两个计数都由被测代码自己 +1（_probeDragMoves 在跟手那一段、
    // _probeHostCommits 在唯一发射口 NotifyHost），比值就是合并的真实效果。

    /// <summary>本轮手势里<b>真的改变了几何</b>的 pointer move 次数（原地抖动不计 —— 那些本来
    /// 就不会产生提交，计进来只会把分母吹大、让比值假绿）。</summary>
    private int _probeDragMoves;

    /// <summary>本轮手势里实际发给宿主的次数（含松手时那一次补发）。</summary>
    private int _probeHostCommits;

    private bool _syncing;
    private bool _closed;

    /// <summary>手柄命中半径（DIP）。比视觉半径大一圈，手指/鼠标更容易抓到。</summary>
    private const double HitRadius = 14.0;

    /// <summary>手柄视觉半径（DIP）。</summary>
    private const double HandleRadius = 9.0;

    private Cursor _cursor = CursorAll;

    // 复用的线段缓冲：Render 每帧调用，避免每帧分配（本项目对热路径分配敏感）。
    private readonly List<(Point A, Point B)> _segments = new(8);

    // U3：Cursor 是 IDisposable，原先 CursorFor 在每次 ApplyState（拖拽移动中宿主会回灌
    // 分割参数 ⇒ 每个 pointer move 一次）都 new 一个且从不释放，与"热路径零分配"自述相悖。
    // 三个轴向各缓存一份，仅轴变化时换引用即可。静态字段在首次实例化（Avalonia 已启动）时才初始化。
    private static readonly Cursor CursorX = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CursorY = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CursorAll = new(StandardCursorType.SizeAll);

    // ═══════════════════════ 构造 ═══════════════════════

    public LayoutOverlayWindow()
    {
        // Avalonia 12：WindowDecorations 是新 API；旧 SystemDecorations 已废弃（CS0618）。
        SetCurrentValue(WindowDecorationsProperty, global::Avalonia.Controls.WindowDecorations.None);
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 逐像素透明：让下方视频透出。命中测试需要非 null 的 Background
        // （Avalonia 侧靠它参与 InputHitTest），而 alpha=0 的纯透明刷在 DComp 合成下不可见。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Colors.Transparent);

        // 刻意**不**置顶（显式写 false 以固化意图）：置顶带高于所有非 topmost 窗口、与 owner 无关，
        // 会让分割线在切到别的程序后仍浮在最上面。Z 序改由 Owner 提供（见类注释与 ShowOverlay）。
        Topmost = false;
        ShowActivated = false;      // 显示/点击都不抢主窗口激活（Space 等快捷键保持在主窗口）
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Focusable = false;
        // 分割线/手柄必须跟随主题（视频区本身不参与，但覆盖层要跟）。
        // 自绘窗口不会自动失效，需显式重绘。
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // ═══════════════════════ 对外方法 ═══════════════════════

    /// <summary>在 <paramref name="anchor"/>（对比区容器，通常是 <c>CompareGridView</c>）
    /// 之上显示覆盖层，并开始跟随宿主窗口的移动 / 缩放 / DPI 变化。
    /// <para>分区是 Core 的值对象，本窗口向它要几何（格表、手柄位置、轴向、拖动结果），
    /// 调用方无需（也不能）传线条矩形 —— 避免"宿主算一份、覆盖层再算一份"的两份几何漂移。</para></summary>
    /// <param name="host">宿主主窗口，用于订阅位置/尺寸变化并作为 owner。</param>
    /// <param name="anchor">对比区容器，覆盖窗口精确覆盖它的屏幕矩形。</param>
    /// <param name="partition">当前分区（含用户已拖好的权重）。</param>
    public void ShowOverlay(Window host, Visual anchor, GridPartition partition)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(partition);
        if (_closed) throw new InvalidOperationException("窗口已关闭，不能再 ShowOverlay；请新建实例。");

        DetachHost();
        _host = host;
        // Owner 是本窗口 Z 序的唯一来源（见类注释）。必须显式设，不能只靠下面的 Show(host)：
        // ShowOverlay 会在窗口已可见时被再次调用（C 键换朝向），那条路径跳过 Show(owner)。
        Owner = host;
        _anchor = anchor;
        ApplyState(partition);
        AttachHost();

        // 先定位再显示，避免在默认位置闪一帧。
        SyncGeometry();
        if (!IsVisible) Show(host);
        SyncGeometry();
        InvalidateVisual();
        // 尾部观测一次：本方法是"进入 / 切换对比模式"的呈现入口（宿主每次 EnterCompareMode 都调，
        // 含 C 键模式循环），语义对齐 MagnifierOverlayWindow 挂在 ShowOverlay 尾部的同款读数。
        ObserveZOrder();
    }

    /// <summary>更新分区并重绘（宿主在自身布局变化后调用）。
    /// 非拖动中是整份接受；<b>拖动中不回灌 <see cref="_partition"/></b>（宿主那份此刻是被
    /// 合并过的滞后值，写回来会把线拽回去，见 <see cref="ApplyHostEcho"/>）。
    /// 顺带做一次 Z 序走链观测（只累加计数，见
    /// <see cref="ZOrderSamples"/>；日志只在"进入 Below"的那一拍写）。</summary>
    public void Update(GridPartition partition)
    {
        ApplyHostEcho(partition);
        InvalidateVisual();
        // 热路径观测点：宿主每次回灌分割参数都走这里（拖动中每个 pointer move 一次）。
        ObserveZOrder();
    }

    /// <summary>隐藏覆盖层并解除对宿主的跟随订阅（可再次 <see cref="ShowOverlay"/> 复用实例）。
    ///
    /// <para><b>B6：刻意不叫 <c>Hide</c></b>。若本方法以 <c>new void Hide()</c> 遮蔽基类
    /// <see cref="Window.Hide"/>，则任何持有 <see cref="Window"/> 引用的路径（框架内部、
    /// 宿主泛型代码、将来新增的调用点）调 <c>Hide()</c> 都会走基类实现而<b>不</b>
    /// <see cref="DetachHost"/> —— 本窗口会继续强订阅主窗口的
    /// Position/Size/Scaling 与 anchor 的 PropertyChanged ⇒ 覆盖层被隐藏却仍被主窗口
    /// 强引用，且每次宿主布局变化都白跑一次 <see cref="SyncGeometry"/>。
    /// 改名让"必须解除订阅"这件事在调用点就显式可见，不再依赖调用者的静态类型。</para>
    ///
    /// <para>末尾落一条 Z 序观测汇总（累计快照）：这是本站点稳定能落盘的汇总点 —— 本窗口的
    /// <c>OnClosed</c> 只发生在应用退出链上，那时宿主的 <c>AppLog.Shutdown()</c> 已经跑过，
    /// 理由见 <see cref="LogZOrderSummary"/>。</para></summary>
    public void HideOverlay()
    {
        DetachHost();
        _dragging = false;
        // 强制结束拖动时把欠账一起丢掉（不补发）：本方法是"退出对比模式"，此刻再回调宿主会在
        // 关闭链上多跑一遍重排/裁剪派发。留着更糟 —— 一个已经结束的拖动的过期值，会在下次
        // 意外的 OnPointerCaptureLost 时被当作"松手"补发给宿主。覆盖层下次 ShowOverlay 会按
        // 宿主给的 ActivePartition() 整份对齐（ApplyState 不经拖动守卫），两边不会长期背离。
        _pendingPartitionCommit = null;
        base.Hide();
        LogZOrderSummary("HideOverlay");
    }

    /// <summary>彻底关闭并释放。宿主窗口关闭时必须调用，否则进程里会残留一个顶层窗口，
    /// 且 Avalonia 会因仍有存活 Window 而不退出消息循环（同 <see cref="ThumbnailPopup.CloseAndDispose"/>）。</summary>
    public void CloseAndDispose()
    {
        if (_closed) return;
        DetachHost();
        _dragging = false;
        _pendingPartitionCommit = null;  // 同 HideOverlay：拖动已不存在，欠账不再有归属
        _segments.Clear();
        if (IsVisible) base.Hide();
        Close();
    }

    // ═══════════════════════ 状态 / 跟随 ═══════════════════════

    private void ApplyState(GridPartition partition)
    {
        _partition = partition;
        SetCursorForHandle(_dragging ? _dragHandle : 0);
    }

    /// <summary>宿主回灌用的记账：与 <see cref="ApplyState"/> 唯一的差别是<b>拖动中不覆盖</b>
    /// <see cref="_partition"/>，其余副作用（光标、由调用方负责的重绘与 Z 序观测）照旧。
    ///
    /// <para><b>为什么必须挡这一层</b>：<see cref="PartitionChanged"/> 现在按 16ms 合并提交，
    /// 而宿主回调 <c>MainWindow.OnPartitionChanged</c>（该文件 1096 行）收到后立刻调回本方法
    /// 把<b>同一份</b>值回灌 —— 那份是上一个已提交的旧位置，落后于指针当前所在的乐观值。
    /// 若照写，线条每提交一次就被回灌往回拽一小格、下一个 move 再弹回去，用户读到的正是拖动抖动。
    /// 本地 <c>_partition</c> 才是这一拍的真值（线与手柄就按它画，<see cref="HandlePositions"/>
    /// 也按它报），欠宿主的那一段由 <see cref="FlushPendingPartitionCommit"/> 在松手时补齐，
    /// 所以"拖动中不一致"只存在于拖动期间。</para>
    ///
    /// <para>不拖动时（换模式 / 路数变化）与改动前逐字相同：整份接受回灌。</para>
    ///
    /// <para>⚠ <b>但"形状变了"这一类回灌不能挡</b>：换形状只可能由宿主的权威事件发起
    /// （加减路、网格布局预设、单屏切换、C 键换朝向）。若一并吞掉，松手时那条 flush 会把
    /// <b>旧形状</b>的分区回灌给已经是新形状的宿主 ⇒ 画面停在错格上、且此后每拍都错（比抖动严重）。
    /// 因此只挡"同一形状、权重滞后"的那种回灌；收下新形状的同时<b>丢掉欠账</b>——
    /// 那笔欠账属于旧几何，结清它只会把新形状拽回去。</para></summary>
    private void ApplyHostEcho(GridPartition partition)
    {
        var reshaped = partition.Cols != _partition.Cols || partition.Rows != _partition.Rows;
        if (!_dragging || reshaped)
        {
            _partition = partition;
            _pendingPartitionCommit = null;
        }
        SetCursorForHandle(_dragging ? _dragHandle : 0);
    }

    /// <summary>把光标设成第 <paramref name="handleIndex"/> 个手柄的轴向光标：竖线给左右箭头、
    /// 横线给上下箭头。拖动中必须用<b>当前手柄</b>的轴 —— 3×3 有 4 条线、两种轴，
    /// 静止时按第一个手柄给光标会给出错误提示。
    /// 只在轴真的变化时换引用（U3：拖拽热路径不得 new Cursor）。</summary>
    private void SetCursorForHandle(int handleIndex)
    {
        var next = CursorFor(_partition.HandleAxisAt(handleIndex));
        if (ReferenceEquals(next, _cursor)) return;
        _cursor = next;
        Cursor = next;
    }

    private void AttachHost()
    {
        if (_host is null) return;
        _host.PositionChanged += OnHostPositionChanged;
        _host.SizeChanged += OnHostSizeChanged;
        _host.ScalingChanged += OnHostScalingChanged;
        // U2：自身被拖到另一块显示器时 RenderScaling 变化，必须重算 DIP 尺寸（见 EffectiveScaling）。
        ScalingChanged += OnSelfScalingChanged;
        if (_anchor is not null) _anchor.PropertyChanged += OnAnchorPropertyChanged;
    }

    private void DetachHost()
    {
        if (_host is not null)
        {
            _host.PositionChanged -= OnHostPositionChanged;
            _host.SizeChanged -= OnHostSizeChanged;
            _host.ScalingChanged -= OnHostScalingChanged;
        }
        ScalingChanged -= OnSelfScalingChanged;
        if (_anchor is not null) _anchor.PropertyChanged -= OnAnchorPropertyChanged;
    }

    private void OnHostPositionChanged(object? sender, PixelPointEventArgs e) => SyncGeometry();

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => SyncGeometry();

    private void OnHostScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnSelfScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnAnchorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 侧栏拖动 / 全屏切换 / 网格预设变化都会改对比区的 Bounds；只认这一条即可覆盖。
        if (e.Property == Visual.BoundsProperty) SyncGeometry();
    }

    /// <summary>把对比区的屏幕矩形同步给本窗口：位置用物理像素，尺寸换算回 DIP。</summary>
    private void SyncGeometry()
    {
        if (_syncing || _closed) return;
        if (_host is null || _anchor is null) return;
        if (TopLevel.GetTopLevel(_anchor) is null) return; // 尚未挂上视觉树 / 已摘除

        var bounds = _anchor.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        PixelPoint topLeft, bottomRight;
        try
        {
            topLeft = _anchor.PointToScreen(new Point(0, 0));
            bottomRight = _anchor.PointToScreen(new Point(bounds.Width, bounds.Height));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LayoutOverlayWindow] PointToScreen 失败: {ex.Message}");
            return;
        }

        var pxW = bottomRight.X - topLeft.X;
        var pxH = bottomRight.Y - topLeft.Y;
        if (pxW <= 0 || pxH <= 0) return;

        // 尺寸按**对比区所在显示器**的缩放换算（Position 已定位到对比区左上角，两者必然同屏）。
        //
        // U2：这里刻意**不先取本窗口的 RenderScaling**。ShowOverlay 在 Show() 之前就先调一次
        // SyncGeometry（避免在默认位置闪一帧），而此时本窗口还没被挂到任何显示器上，
        // RenderScaling 会返回 1.0 —— 它不是"未知"，只是"尚未确定"，且 1.0 本身是合法缩放，
        // 无法靠数值区分。原先的 `!(scaling > 0)` 判断因此拦不住它：150% DPI 下首帧会按 1.0
        // 折算，窗口比对比区大 50%，直到下一次布局事件才纠正。
        // anchor 已在视觉树里，其 TopLevel 的 RenderScaling 从第一次调用起就是可信的，故优先用它。
        var scaling = EffectiveScaling();

        var dipW = pxW / scaling;
        var dipH = pxH / scaling;

        _syncing = true;
        try
        {
            if (Position != topLeft) Position = topLeft;
            // 浮点抖动不触发重排：差 < 0.5 DIP 视为同值。
            if (Math.Abs(Width - dipW) > 0.5) Width = dipW;
            if (Math.Abs(Height - dipH) > 0.5) Height = dipH;
        }
        finally
        {
            _syncing = false;
        }

        // 落位之后再观测一次（前面那些守卫 return 都不计数，采的是"真的重摆了一遍"）：
        // 主窗移动 / 缩放 / DPI 变化 / 侧栏拖动都走这条位置同步路径 ⇒ 覆盖层每次被重新摆一遍都会
        // 留下一个读数 —— 放大镜那次翻转正是出现在这类后续位置/呈现回调上。
        ObserveZOrder();
    }

    /// <summary>对比区所在显示器的缩放（物理像素 / DIP），保证为正。
    ///
    /// <para>优先级：anchor 所在 <see cref="TopLevel"/> 的 <c>RenderScaling</c> → 宿主 →
    /// 本窗口自身 → 1.0。首选 anchor 的理由见 <see cref="SyncGeometry"/> 的 U2 注释：
    /// 本窗口在 <see cref="ShowOverlay"/> 的首次 <see cref="SyncGeometry"/> 时尚未挂到显示器上，
    /// 其 <c>RenderScaling</c> 恒为 1.0（合法的默认值，不是"未知"），据此折算会算错高 DPI 首帧。</para>
    /// <para>自身项保留作兜底：anchor 已被摘出视觉树时（<c>GetTopLevel</c> 为 null）它至少是 1.0。</para></summary>
    private double EffectiveScaling()
    {
        var scaling = TopLevel.GetTopLevel(_anchor)?.RenderScaling ?? 0d;
        if (!(scaling > 0) && _host is not null) scaling = _host.RenderScaling;
        if (!(scaling > 0)) scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        return scaling;
    }

    // ═══════════════════════ 输入（拖动） ═══════════════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // ── 自由拼接形态：命中"逻辑线段"，按线段编号上报（不是按分区整份上报）──
        // 为什么单独一条通道：自由拼接的线是**线段**（T 形交叉时同一位置有多段、覆盖区间不同），
        // 表达不了"每列一个权重"的分区模型。宿主拿到线段编号后调 MosaicLayout.DragLine。
        if (_freeformLines is { Count: > 0 })
        {
            var line = HitFreeformLineAt(e.GetPosition(this));
            if (line < 0) return;
            _dragging = true;
            _dragFreeformLine = line;
            _probeDragMoves = 0;
            _probeHostCommits = 0;
            SetCursorForFreeformLine(line);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var handle = HandleIndexAt(e.GetPosition(this));
        if (handle < 0) return;

        BeginHandleDrag(handle);
        e.Pointer.Capture(this); // 拖出窗口仍要收到 Move/Released
        e.Handled = true;
    }

    /// <summary>自由拼接下的线段命中：返回<b>逻辑线编号</b>（-1 = 没命中）。
    ///
    /// <para>判据与 <c>MosaicLayout.LogicalLines</c> 的输出一一对应：
    /// 竖线比 X、横线比 Y，且点必须落在该线段的<b>覆盖区间</b>内 ——
    /// 只比位置会抓到一条根本不经过该点的线（T 形交叉下同一位置有多段、区间不同）。</para>
    ///
    /// <para>容差按 DIP 取 <see cref="HitRadius"/>，与网格那条线的手感一致。</para></summary>
    internal int HitFreeformLineAt(Point dip)
    {
        if (_freeformLines is not { Count: > 0 } lines) return -1;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return -1;

        var u = dip.X / w;
        var v = dip.Y / h;
        var tolX = HitRadius / w;
        var tolY = HitRadius / h;

        for (var i = 0; i < lines.Count; i++)
        {
            var (axis, pos, from, to) = lines[i];
            if (axis == SplitAxis.X)
            {
                if (Math.Abs(u - pos) > tolX) continue;
                if (v < from - tolY || v > to + tolY) continue;
            }
            else
            {
                if (Math.Abs(v - pos) > tolY) continue;
                if (u < from - tolX || u > to + tolX) continue;
            }
            return i;
        }
        return -1;
    }

    private void SetCursorForFreeformLine(int line)
    {
        var axis = _freeformLines is { Count: > 0 } l && line >= 0 && line < l.Count
            ? l[line].Axis : SplitAxis.X;
        Cursor = new Cursor(axis == SplitAxis.X ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
    }

    /// <summary>抓取第 <paramref name="handle"/> 条线：<b>拖动状态迁移的唯一一处</b>。
    ///
    /// <para>真实按下与自测探针（<see cref="ProbeGrabHandle"/>）都进这里 —— 探针若另写一份状态迁移，
    /// 验的就是副本而不是产品走的那条路径。刻意不含 <c>Pointer.Capture</c>：那是 Avalonia 的输入
    /// 管线动作，探针灌的是归一化坐标序列，捕获与否不改变本类的提交 / 结算逻辑。</para></summary>
    private void BeginHandleDrag(int handle)
    {
        _dragging = true;
        _dragHandle = handle; // 记住抓的是哪一个：三列模式下两条线的拖动目标不同
        // 起手把提交时间戳清零 ⇒ 第一次 pointer move 立刻提交，抓取那一拍不会有 16ms 死区
        // （镜像 MainWindow.CompareCrop.cs :: BeginComparePan() 对 _lastCompareCommitTicks 的清零：
        // 那 16ms 正好落在用户判断"跟不跟手"的那一下）。顺带丢弃上一轮可能残留的欠账。
        _lastPartitionCommitTicks = 0;
        _pendingPartitionCommit = null;
        SetCursorForHandle(handle);
        _probeDragMoves = 0;
        _probeHostCommits = 0;
    }

    /// <summary>结束拖动：<b>结清欠账的唯一一处</b>（真实松手、失去捕获、自测探针共用同一份）。
    ///
    /// <para>松手必须结清欠账：被 16ms 门限挡下的最后那一段只有在这里补发，宿主的格表 / 裁剪区域 /
    /// 自测探针读到的那份 _partition 才和覆盖层画出来的位置对上（滞后只允许发生在拖动期间）。
    /// <see cref="OnPointerReleased"/> 刻意把本方法放在 <c>Capture(null)</c> <b>之前</b>：释放捕获
    /// 可能同步回调 <see cref="OnPointerCaptureLost"/>，那里也走本方法 —— Flush 先把欠账置 null，
    /// 所以第二次进来是空操作（幂等，不重复通知宿主）。</para>
    ///
    /// <para>与之相反，<see cref="HideOverlay"/> / <see cref="CloseAndDispose"/> 是<b>丢弃</b>欠账
    /// 而不是结清（退出对比模式时再回调宿主，会在关闭链上多跑一遍重排与裁剪派发），故不走本方法。</para></summary>
    private void EndHandleDrag()
    {
        _dragging = false;
        _dragFreeformLine = -1;      // 自由拼接那条通道的拖动状态一并清掉
        FlushPendingPartitionCommit();
    }

    /// <summary>自由拼接形态下正在拖的逻辑线编号（-1 = 不在拖自由拼接的线）。
    /// <para>与 <c>_dragHandle</c> 分工：那条走分区模型（网格用的整线），这条走线段模型。
    /// 两者互斥（<see cref="OnPointerPressed"/> 里按形态二选一）。</para></summary>
    private int _dragFreeformLine = -1;

    /// <summary><b>自由拼接的线段被拖动</b>：参数是（逻辑线编号, 该轴归一化位置）。
    ///
    /// <para>宿主据此调 <c>MosaicLayout.DragLine</c> 重建布局，再回灌新的线段表。
    /// 与 <see cref="PartitionChanged"/> 并列：那条只服务"整线贯穿"的网格分区。</para></summary>
    public event Action<int, double>? FreeformLineDragged;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        // 自由拼接：把指针位置按轴向换算成归一化坐标后上报（不经过分区模型）
        if (_dragFreeformLine >= 0 && _freeformLines is { Count: > 0 } lines
            && _dragFreeformLine < lines.Count)
        {
            var p = e.GetPosition(this);
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w > 0 && h > 0)
            {
                var axis = lines[_dragFreeformLine].Axis;
                var norm = axis == SplitAxis.X ? p.X / w : p.Y / h;
                if (double.IsFinite(norm))
                    FreeformLineDragged?.Invoke(_dragFreeformLine, norm);
            }
            e.Handled = true;
            return;
        }

        ApplyDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        EndHandleDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndHandleDrag();   // 失去捕获等同于松手：欠账同样要结清
    }

    /// <summary>指针（窗口内 DIP）落在第几个手柄的命中区内；未命中返回 -1。
    /// 手柄数量与位置都向 <see cref="_partition"/> 要（本窗口不自己推导几何，见类注释）。</summary>
    private int HandleIndexAt(Point p) => HandleIndexAt(p.X, p.Y, Bounds.Width, Bounds.Height, HitRadius);

    /// <summary>把指针位置（窗口内 DIP）换算成归一化坐标，交给 Core 的"拖动第 k 条线"规则，
    /// 本地乐观更新后抛给宿主。
    ///
    /// <para><b>分量身份不再由本窗口判断</b>：旧实现要在 X / Y 两个自由度里挑一个
    /// （<c>CompareLayout.HandleComponentAt</c>），因为两条竖线共用同一份只有两个自由度的
    /// 分割参数。权重模型下每条线只管它两侧那两个相邻权重，"抓住哪条就动哪条"由
    /// <see cref="GridPartition.WithHandleDragged"/> 的索引直接给出，
    /// 于是"拖 A|B 的分隔条却改变了 B|C 的分界"这一类缺陷在结构上不可能出现。</para>
    ///
    /// <para><b>通知宿主按 16ms 合并，画线不合并</b>（见 <see cref="GestureCommitIntervalMs"/>）。
    /// 与之相邻的另一个合并点是平移：<c>MainWindow.axaml.cs</c> 约 1788-1802 行按同一个
    /// <see cref="Environment.TickCount64"/> 门槛合并 crop 提交，那条已经过实测 ——
    /// <c>--magnifybench [平移提交]</c> p95=0.27ms、连续 34 次/秒提交、出帧 59.87fps 零卡顿，
    /// 日志 <c>tests/output/magnifybench-pancommit-z2-20260929.log</c>。本站点成本更高
    /// （宿主一次回调要重排最多 9 路子 HWND + 一轮裁剪派发 + 组件日志），门槛却一样，
    /// 是因为要合并的是"每秒 125+ 次"这件不该由用户手感买单的事。</para>
    ///
    /// <para><b>两条自测路径各有分工</b>：网格拖动的旧验收（<c>MainWindow.SelfTest.cs</c> 的
    /// <c>OnPartitionChanged(dragged)</c>、<c>SplitRelayoutUnitAsync</c> 等）是<b>直接调宿主回调</b>，
    /// 绕开覆盖层的 pointer move 链路 ⇒ 它们验"格表与画面跟不跟"，一次都不会被这里的合并挡住；
    /// 而<b>合并本身</b>由 <c>view.drag-commit</c> 那组探针（<see cref="ProbeGrabHandle"/> /
    /// <see cref="ProbeMoveTo"/> / <see cref="ProbeRelease"/>）驱动 —— 它们转调的就是本类的
    /// <see cref="ApplyDrag"/> 与 <see cref="EndHandleDrag"/>，不复制判断。
    /// 覆盖层侧的 <see cref="HandlePositions"/> / <see cref="HitHandleIndexAt"/> 探针在<b>非拖动</b>态读值
    /// （此时 <see cref="ApplyHostEcho"/> 照旧整份接受回灌），判据不变。</para></summary>
    private void ApplyDrag(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var next = _partition.WithHandleDragged(_dragHandle, p.X / w, p.Y / h);
        if (next.SameShape(_partition)) return; // 权重逐项比较：原地抖动不产生事件风暴

        // ── 跟手的部分：每个 move 都跑，一行不省 ──
        _partition = next; // 乐观本地更新：不必等宿主回灌即可跟手
        _probeDragMoves++;  // 口径：真的改变了几何的 move = "本来可以提交"的次数（见探针注释）
        InvalidateVisual();

        // ── 通知宿主的部分：按 ~60Hz 合并 ──
        // 被挡下的那次不是丢弃，是欠账（_pendingPartitionCommit 只换一个已分配好的引用，
        // 不新增分配、不建 timer）；结清点见 FlushPendingPartitionCommit。
        var nowMs = Environment.TickCount64;
        if (nowMs - _lastPartitionCommitTicks < GestureCommitIntervalMs)
        {
            _pendingPartitionCommit = next;
            return;
        }

        _lastPartitionCommitTicks = nowMs;
        _pendingPartitionCommit = null;
        NotifyHost(next);
    }

    /// <summary>把被门限挡下的最后那个分区补发给宿主 —— <b>拖动收尾的唯一一处</b>。
    ///
    /// <para>为什么必须有它：宿主的 <c>_partition</c> 是它自己的格表真值（<c>ApplyCompareLayout</c>
    /// 摆窗、<c>ScheduleCompareCrop</c> 出裁剪矩形、自测探针比对边界位置都读它）。只合并、
    /// 不结清，松手后宿主就停在最后一个已提交的采样位置上，与覆盖层画出来的线差最多一个拖动增量
    /// —— 表现是"线在这儿、画面却没切到这儿"，退出再进对比模式时线还会整体跳回去。</para>
    ///
    /// <para>幂等：欠账为 <c>null</c> 时什么都不做，所以 OnPointerReleased 与随后可能同步触发的
    /// OnPointerCaptureLost 只会真正通知一次。补发时把时间戳推进到当前，避免紧接着的一次
    /// 非拖动回灌又被算成"刚提交过"。</para></summary>
    private void FlushPendingPartitionCommit()
    {
        var pending = _pendingPartitionCommit;
        if (pending is null) return;
        _pendingPartitionCommit = null;
        _lastPartitionCommitTicks = Environment.TickCount64;
        NotifyHost(pending);
    }

    /// <summary>将当前分区交还宿主 —— <c>PartitionChanged</c> 的<b>唯一发射口</b>。
    ///
    /// <para>两个调用点（<see cref="ApplyDrag"/> 的按 16ms 提交、<see cref="FlushPendingPartitionCommit"/>
    /// 的收尾补发）都走这里，探针计数（<see cref="ProbeHostCommits"/>）因此不可能漏计其中一处：
    /// "事件发射点写两遍、计数只加在一遍上"是这类判据最常见的失效形态（读数恒低、判据恒绿）。</para></summary>
    private void NotifyHost(GridPartition partition)
    {
        _probeHostCommits++;
        PartitionChanged?.Invoke(partition);
    }

    /// <summary>自测探针口：把一次 DIP 坐标按<b>与真实点击完全同一条</b>判据换算成手柄序号。
    /// <para>为什么要暴露它：命中区从"线中间的圆点"改成"整条线"之后，肉眼看不出差别，
    /// 而判据错了的表现是"抓线拖不动"或"哪儿都能抓、误触吞掉画面上的滚轮"。
    /// 这里只读不写状态，与 <see cref="HandlePositions"/> 同性质。</para></summary>
    internal int HitHandleIndexAt(Point dip) => HandleIndexAt(dip);

    // ═══════════════ 自测探针：拖动热路径（合并提交 / 结算 / 回灌）═══════════════
    //
    // 为什么必须是探针而不是"真鼠标"：本仓的验收跑在无头进程内，PostMessage 一条真实拖动序列
    // 要跨越消息泵、还受"覆盖层 owned 窗 Z 序会翻转""焦点不可依赖"这些既有坑影响，读数不可复现。
    // 而**合并逻辑本身是纯状态机**（时间门限 + 欠账 + 回灌守卫），它可以也只该被确定性驱动。
    //
    // 三个入口全部转调生产私有方法（BeginHandleDrag / ApplyDrag / EndHandleDrag），
    // 不复制任何一行判断；因此探针验到的就是真鼠标走的那条路径。
    // 探针刻意不含的部分：命中测试（由 HitHandleIndexAt 那条判据单独管）、Pointer.Capture、
    // 以及 Avalonia 的绘制时序 —— 本组判据不声称验过"渲染成本"，那是 --magnifybench 的账。

    /// <summary>探针：本轮手势内"真的改变了几何"的 move 次数（分母）。</summary>
    internal int ProbeDragMoves => _probeDragMoves;

    /// <summary>探针：本轮手势内 <c>PartitionChanged</c> 的实际发射次数（分子，含松手那次补发）。</summary>
    internal int ProbeHostCommits => _probeHostCommits;

    /// <summary>探针：拖动提交的最小间隔（ms），自测报告口径用；<c>0</c> 表示合并被关闭。
    /// <para>它同时是<b>变异验牙的靶子</b>：把它改成 0 重新编译，下面的风暴判据必须判红；
    /// 若改坏了还绿，说明判据没牙。</para></summary>
    internal static long ProbeCommitIntervalMs => GestureCommitIntervalMs;

    /// <summary>探针：宿主手上是否还欠一笔未提交的分区（拖动中被门限挡下的那一段）。</summary>
    internal bool ProbeHasPendingCommit => _pendingPartitionCommit is not null;

    /// <summary>探针：抓取第 <paramref name="index"/> 条线（与真实按下共用 <see cref="BeginHandleDrag"/>，
    /// 含时间戳清零与欠账丢弃）。越界不在此拦：<see cref="GridPartition.WithHandleDragged"/> 对越界
    /// 索引直接返回自身，那条行为由 Core 的单测钉住，探针不该另立一套边界判断。</summary>
    internal void ProbeGrabHandle(int index) => BeginHandleDrag(index);

    /// <summary>探针：灌一次"移动到归一化坐标 (<paramref name="fx"/>, <paramref name="fy"/>)"，
    /// 走的就是 <see cref="OnPointerMoved"/> 那一行 <see cref="ApplyDrag"/>。
    /// <para>坐标换算成 DIP 用的是 <see cref="Window.Bounds"/>，与真实事件同一份尺寸来源；
    /// 尺寸非法时<b>直接抛</b>而不是静默不动 —— 夹具若在未布局时起拖，必须当场暴露成红，
    /// 不能读出一组"移动了但几何没变"的空数据。</para></summary>
    internal void ProbeMoveTo(double fx, double fy)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException(
                $"探针移动时覆盖层尺寸非法（{w:F1}x{h:F1}），拖动无从换算 —— 夹具须先等一次布局完成");
        ApplyDrag(new Point(fx * w, fy * h));
    }

    /// <summary>探针：松手（与真实 Released 共用 <see cref="EndHandleDrag"/>，即补发欠账）。
    /// 不含 <c>Pointer.Capture(null)</c>，理由见 <see cref="BeginHandleDrag"/>。</summary>
    internal void ProbeRelease() => EndHandleDrag();

    /// <summary>命中判据的<b>唯一实现</b>：DIP 路径（Avalonia 事件）与物理路径
    /// （<c>WM_NCHITTEST</c>）都换调到它，只是传入各自的坐标与半径 —— 同一条规则出现两份实现，
    /// 就是本项目反复踩到的"其中一份是错的"（真机表现：光标显示能拖、点下去却让给视频层）。
    ///
    /// <para><b>单轴手柄沿轴放开到整条线</b>：<see cref="SplitAxis.X"/> 的竖线不论高低都能抓，
    /// <see cref="SplitAxis.Y"/> 的横线不论左右都能抓。合并后的网格里<b>每条线都是单轴</b>
    /// （一条竖线只改它左右两列的宽度），所以"整条线都能抓"这条既有体验可以原样保留，
    /// 用户的说法是"中间的线可以拉动"，而不是"线中间那个圆点可以拉动"。
    /// <see cref="SplitAxis.Both"/>（交叉点型，一次改两个分量）在此刻意不放开：沿整条线抓会把
    /// 另一轴一起拽到指针高度，用户看到的是"没抓的那条线跳了"——网格分区已不再产出这种手柄，
    /// 保留该分支只因为枚举里还有这个取值。</para></summary>
    private int HandleIndexAt(double px, double py, double w, double h, double radius)
    {
        if (w <= 0 || h <= 0) return -1;

        var count = _partition.HandleCount;
        for (var i = 0; i < count; i++)
        {
            var (hx, hy) = _partition.HandlePositionAt(i);
            var dx = px - hx * w;
            var dy = py - hy * h;
            var hit = _partition.HandleAxisAt(i) switch
            {
                SplitAxis.X => System.Math.Abs(dx) <= radius,
                SplitAxis.Y => System.Math.Abs(dy) <= radius,
                _ => dx * dx + dy * dy <= radius * radius, // 交叉点型：只放一个圆点
            };
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>指针（窗口内 DIP）是否落在任一手柄的命中区内。</summary>
    private bool IsOnHandle(Point p) => HandleIndexAt(p) >= 0;

    /// <summary>轴向 → 光标。返回**静态缓存**实例（U3：不得在拖拽热路径上 new，见字段注释）。</summary>
    private static Cursor CursorFor(SplitAxis axis) => axis switch
    {
        SplitAxis.X => CursorX,
        SplitAxis.Y => CursorY,
        _ => CursorAll,
    };

    // ═══════════════════════ 绘制 ═══════════════════════

    // 分割线/手柄配色取主题令牌：强调色随主题（浅色下金黄压暗到琥珀），
    // 光晕与描边环是"压在视频上"的对比度辅助，深浅同值（见 ThemeTokens）。
    // 非 static readonly：主题切换后要能重取。
    // 字段初值取令牌表的深色值：首次 EnsureTheme 会按当前变体覆盖，运行时不生效，
    // 只为消除硬编码副本（单一真源见 ThemeTokens）。
    private Pen _haloPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayHaloBrush").Dark)), 4);
    private Pen _linePen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 1.5);
    private IBrush _handleFill = new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark));
    private Pen _handleRingPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayRingBrush").Dark)), 2);
    private Avalonia.Styling.ThemeVariant? _themeTag;

    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _haloPen = new Pen(ThemePalette.Brush(this, "OverlayHaloBrush"), 4);
        _linePen = new Pen(ThemePalette.Brush(this, "AccentBrush"), 1.5);
        _handleFill = ThemePalette.Brush(this, "AccentBrush");
        _handleRingPen = new Pen(ThemePalette.Brush(this, "OverlayRingBrush"), 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        EnsureTheme();

        _segments.Clear();

        // ── 自由拼接形态：线段由宿主给出（可含 T 形交叉），直接换算成像素即可 ──
        // 与下面的网格路径是**互斥**的两条；有自由拼接时完全不读 _partition，
        // 于是"两套几何各画一半"这种漂移在结构上不可能发生。
        if (_freeformLines is { Count: > 0 } freeform)
        {
            foreach (var (axis, pos, from, to) in freeform)
            {
                if (axis == SplitAxis.X)
                    _segments.Add((new Point(pos * w, from * h), new Point(pos * w, to * h)));
                else
                    _segments.Add((new Point(from * w, pos * h), new Point(to * w, pos * h)));
            }
        }
        else
        foreach (var c in _partition.ComputeCells())
        {
            var x0 = c.X * w;
            var y0 = c.Y * h;
            var x1 = (c.X + c.Width) * w;
            var y1 = (c.Y + c.Height) * h;

            // 只取每条单元格的「右边界」与「下边界」，且仅当它落在 (0,1) 开区间内。
            // 理由：ComputeCells 保证无缝铺满 [0,1]²，于是任意一条内部竖分割线
            // 恰好等于「其左侧所有单元格的右边界」的并集 —— 只发右/下边即可无重无漏地
            // 覆盖全部内部边界，也自然避免了同一条线被画两遍（半透明光晕叠加会变黑）。
            // 贴容器外沿的边不画：视频格自身已有边框，再描一圈只会显脏。
            // 路数少于容量时（5 路用 3×2）空着的那格不在格表里，它与已占格之间的
            // 分界仍由"已占格的右/下边"发出 ⇒ 线条照样完整。
            if (c.X + c.Width < 1.0 - GridPartition.Epsilon)
                _segments.Add((new Point(x1, y0), new Point(x1, y1)));
            if (c.Y + c.Height < 1.0 - GridPartition.Epsilon)
                _segments.Add((new Point(x0, y1), new Point(x1, y1)));
        }

        // 两趟绘制：先铺完所有深色光晕，再画亮线。
        // 若逐段「晕+线」，后一段的光晕会盖住前一段的亮线，交叉处出现断口。
        foreach (var (a, b) in _segments) context.DrawLine(_haloPen, a, b);
        foreach (var (a, b) in _segments) context.DrawLine(_linePen, a, b);

        // 手柄：数量与位置都由 Core 的分区给出（每条线一个，落在该线中点）。
        // 必须逐个画，不能只画第一个 —— 3×3 有 4 条线，漏画的那几条可拖但看不见抓手，用户无从下手。
        if (_freeformLines is { Count: > 0 } freeformHandles)
        {
            // 自由拼接：手柄落在线段的**中点**（T 形下两段各有自己的中点，
            // 用户因此能看出"这条线只覆盖这一段"）。
            foreach (var (axis, pos, from, to) in freeformHandles)
            {
                var mid = (from + to) / 2;
                var cx = axis == SplitAxis.X ? pos : mid;
                var cy = axis == SplitAxis.X ? mid : pos;
                context.DrawEllipse(_handleFill, _handleRingPen, new Point(cx * w, cy * h),
                    HandleRadius, HandleRadius);
            }
        }
        else
        {
            var handles = _partition.HandleCount;
            for (var i = 0; i < handles; i++)
            {
                var (hx, hy) = _partition.HandlePositionAt(i);
                context.DrawEllipse(_handleFill, _handleRingPen, new Point(hx * w, hy * h), HandleRadius, HandleRadius);
            }
        }
    }

    // ═══════════════════════ Win32：命中测试钩子 ═══════════════════════

    private nint _hwnd;
    private nint _origWndProc;
    private bool _hitTestHookInstalled;
    private WndProcDelegate? _hookProc; // 必须字段持有：委托被 GC 后原生回调会踩空指针

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const int GWLP_WNDPROC = -4;
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOPMOST = 0x00000008;
    /// <summary><c>GetWindow</c> 命令：取 owner 窗口（对顶层窗口即 <c>GWL_HWNDPARENT</c> 所指）。</summary>
    private const uint GW_OWNER = 4;
    /// <summary><c>GetWindow</c> 命令：Z 序里下一个（更靠下）的窗口。</summary>
    private const uint GW_HWNDNEXT = 2;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private const nint HTTRANSPARENT = -1;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InstallHitTestHook();

        // 不假装透明一定成功：拿不到逐像素 alpha 时窗口会以不透明底覆盖视频，
        // 这是必须在真机上暴露而不是静默降级的失效模式，故显式记到 stderr。
        if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            Console.Error.WriteLine(
                $"[LayoutOverlayWindow] 未取得逐像素透明（实际 {ActualTransparencyLevel}），" +
                "覆盖层可能遮挡视频画面；宿主应据 AchievedTransparency 降级或改用 Win32 分层窗口。");
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭链最上游落一条汇总（此时计数已定）。⚠ 应用退出路径上宿主已在 OnClosing 里
        // AppLog.Shutdown()，这一条多半被丢弃 ⇒ 稳定的汇总见 HideOverlay（见该方法注释）。
        LogZOrderSummary("OnClosed");
        _closed = true;
        UninstallHitTestHook();
        base.OnClosed(e);
    }

    private void InstallHitTestHook()
    {
        if (_hwnd != nint.Zero) return;

        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == nint.Zero)
        {
            Console.Error.WriteLine("[LayoutOverlayWindow] 取不到平台 HWND，命中测试钩子未安装（对比区鼠标消息将被吞掉）。");
            return;
        }

        _hwnd = handle.Handle;

        // 点击手柄不激活本窗口：主窗口的键盘快捷键（Space/方向键）不被打断。
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);

        _hookProc = HitTestWndProc;
        var prev = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hookProc));
        if (prev == nint.Zero)
        {
            Console.Error.WriteLine($"[LayoutOverlayWindow] 子类化失败（Win32 错误 {Marshal.GetLastWin32Error()}），命中测试钩子未安装。");
            _hookProc = null;
            return;
        }

        _origWndProc = prev;
        _hitTestHookInstalled = true;
    }

    private void UninstallHitTestHook()
    {
        if (_hwnd != nint.Zero && _origWndProc != nint.Zero)
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _origWndProc);
        _origWndProc = nint.Zero;
        _hwnd = nint.Zero;
        _hookProc = null;
        _hitTestHookInstalled = false;
    }

    /// <summary>子类化窗口过程：只截 <c>WM_NCHITTEST</c>，其余原样转发给 Avalonia 的窗口过程。
    /// 手柄命中圈内返回 <c>HTCLIENT</c>（消息正常进来），其余位置返回 <c>HTTRANSPARENT</c>
    /// 把消息让给下层的视频子 HWND —— 这是"覆盖窗口不吞输入"的唯一关键点。</summary>
    private nint HitTestWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_NCHITTEST && _hitTestHookInstalled)
        {
            // lParam 是**屏幕坐标**，低/高 16 位各为有符号 short。
            var sx = (short)(lParam & 0xFFFF);
            var sy = (short)((lParam >> 16) & 0xFFFF);
            var pt = new POINT { X = sx, Y = sy };
            if (ScreenToClient(hwnd, ref pt) && IsOnHandlePhysical(pt.X, pt.Y))
                return HTCLIENT;
            return HTTRANSPARENT;
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>与 <see cref="HandleIndexAt(Point)"/> <b>同一条</b>判据，只是输入是**物理客户区像素**
    /// （<c>WM_NCHITTEST</c> 拿到的是物理坐标，不能直接与 DIP 比较）：半径按 <see cref="RenderScaling"/>
    /// 放大后交给共用实现，两条路径不再各写一份几何。
    /// 必须逐个手柄判：3×3 有 4 条线，漏判的那条不命中也会
    /// 吞掉该处的鼠标消息（表现为"那条线附近无法拖动画面"）。</summary>
    private bool IsOnHandlePhysical(int px, int py)
    {
        if (!GetClientRect(_hwnd, out var rc)) return false;
        var cw = rc.Right - rc.Left;
        var ch = rc.Bottom - rc.Top;
        if (cw <= 0 || ch <= 0) return false;

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        return HandleIndexAt(px, py, cw, ch, HitRadius * scaling) >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint prevProc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);
}
