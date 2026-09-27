using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="NativeFrameReadback.ResolveRect"/> 的<b>边界契约</b>（docs/41 §4.5 第 12 项）。
///
/// <para><b>被测的是什么</b>：从 <c>MainWindow.CaptureNativeFrame</c> 下沉到 Core 的那段纯算术
/// —— "无 RTInfo 退回整面 / Dest 超 swap 按 swap 裁剪 / 退化输入返回 null"。
/// 下沉前它挂在 <c>Window</c> 子类上，必须构造 Avalonia 窗口才跑得起来，故此前零覆盖。</para>
///
/// <para><b>期望值来源（独立推算）</b>：全部由 <c>RenderTargetInfo</c> 的定义手算，不调用被测实现
/// 也不调用它的兄弟函数。算术是"先判四字段溢出（<c>&gt; int.MaxValue</c> ⇒ <c>null</c>）"，
/// 再 <c>x0=clamp(DestX,0,swapW−1)</c>、<c>w=min(DestWidth, max(0, swapW−x0))</c>（y 同理），
/// 每条用例的注释里写出了代入过程。</para>
///
/// <para><b>不覆盖的部分（诚实边界）</b>：位图分配/<c>UnlockBits</c>/失败释放那一段仍在
/// <c>MainWindow.CaptureNativeFrame</c> 里（需要 <c>System.Drawing.Bitmap</c> 与真实会话），
/// 属 docs/41 的 C 类（只能冒烟）；本文件只钉住"算出哪个矩形、算不出来时必须 null"。</para>
/// </summary>
public class NativeFrameReadbackTests
{
    /// <summary>负原点（放大后往右下平移 ⇒ 画盒左/上沿推到屏幕外）时，可见尺寸必须按<b>求交</b>收缩。
    ///
    /// <para><b>期望值手算</b>：<c>swap=(1000,500)</c>、<c>dest=(-200,-100,1000,800)</c> ⇒
    /// 画盒横占 <c>[-200,800)</c>、纵占 <c>[-100,700)</c>，与缓冲 <c>[0,1000)×[0,500)</c> 求交
    /// ⇒ <c>(0,0,800,500)</c>。旧式 <c>w=min(DestWidth, swapW−x0)</c> 会算出 <c>w=1000</c> ——
    /// 多出的 200 列在屏幕外，回读等于把错内容当可见区。这条只在内核把 <c>destX/destY</c>
    /// 从无符号改成有符号之后才会被走到（<c>RenderTargetInfo</c> v2）。</para></summary>
    [Fact]
    public void 负原点时可见尺寸按求交收缩()
    {
        var rt = new RenderTargetInfo(1000, 500, 1000, 500, -200, -100, 1000, 800, 8, false);

        Assert.Equal(R(0, 0, 800, 500),
            NativeFrameReadback.ResolveRect(true, rt, 1920, 1080));
    }

    /// <summary>Dest 矩形超出交换链 ⇒ 必须按 swap 裁剪（否则会请求越界区域，内核返回失败或垃圾数据）。
    ///
    /// <para><b>期望值</b>：① <c>swap=(1000,500)</c>、<c>dest=(900,400,800,400)</c> ⇒
    /// <c>x0=900</c>（未越界）、<c>w=min(800, 1000−900)=100</c>、<c>h=min(400, 500−400)=100</c>
    /// ⇒ <c>(900,400,100,100)</c>；② <c>destX=1200</c> 已超出可钳区间 ⇒
    /// <c>x0=clamp(1200,0,999)=999</c>、<c>w=min(800, 1000−999)=1</c> ⇒ <c>(999,400,1,100)</c>
    /// （宽度被压到 1 而不是 0：钳制上界是 <c>swapW−1</c>，故恒有 1 列可读）。</para></summary>
    [Fact]
    public void Dest超出交换链时按交换链裁剪()
    {
        var rt = new RenderTargetInfo(1000, 500, 1000, 500, 900, 400, 800, 400, 8, false);
        Assert.Equal(R(900, 400, 100, 100),
            NativeFrameReadback.ResolveRect(true, rt, 1920, 1080));

        var overflowing = new RenderTargetInfo(1000, 500, 1000, 500, 1200, 400, 800, 400, 8, false);
        Assert.Equal(R(999, 400, 1, 100),
            NativeFrameReadback.ResolveRect(true, overflowing, 1920, 1080));
    }

    /// <summary>拿不到 RTInfo（演示模式 / 旧内核），或 RTInfo 的 Dest 尺寸为 0 ⇒ 退回"整面即视频"。
    ///
    /// <para><b>期望值</b>：退回后 <c>swap=dest=(视频宽,视频高)</c> 且原点为 0 ⇒
    /// 裁剪退化为恒等，结果为 <c>(0,0,视频宽,视频高)</c>。
    /// 用两个不同的视频尺寸（1920×1080 / 640×360）分别走"探针返回 false"与
    /// "探针成功但 Dest 为 0"两条触发路径，避免把"恒返回视频尺寸"这种错误实现测成绿。</para></summary>
    [Fact]
    public void 无渲染目标信息时退回整面()
    {
        Assert.Equal(R(0, 0, 1920, 1080),
            NativeFrameReadback.ResolveRect(false, default, 1920, 1080));

        var zeroDest = new RenderTargetInfo(1280, 720, 1280, 720, 0, 0, 0, 0, 8, false);
        Assert.Equal(R(0, 0, 640, 360),
            NativeFrameReadback.ResolveRect(true, zeroDest, 640, 360));
    }

    /// <summary>交换链尺寸为 0 = 未知 ⇒ 视为<b>无上界</b>（宁可不裁，也不要凭 0 裁成空矩形）；
    /// 且尺寸字段 <c>uint</c> 溢出（值 ≥ 2^31，强转成 <c>int</c> 为负）时按"字段不可信"拒绝整次回读。
    ///
    /// <para><b>期望值</b>：① <c>swap=0</c> ⇒ <c>swapW=swapH=int.MaxValue</c>，
    /// <c>dest=(10,20,300,200)</c> 原样保留 ⇒ <c>(10,20,300,200)</c>；
    /// ② <c>destWidth=uint.MaxValue</c> ⇒ 尺寸不可信 ⇒ <c>null</c>。</para>
    ///
    /// <para><b>本条原本是用 <c>destX=uint.MaxValue</c> 来判 <c>null</c> 的</b>：内核把
    /// <c>destX/destY</c> 从无符号改成<b>有符号</b>（RTInfo v2，理由见 <see cref="RenderTargetInfo"/> 的注释）
    /// 之后，"坐标 ≥ 2^31"这个形状在类型上已不可表示，那条断言失去对象 ⇒ 改拿<b>仍然无符号的尺寸字段</b>
    /// 表达同一件事。坐标侧"不可信就拒绝"改由下面那条"画盒被完全推出屏幕"的用例接替。
    /// 改前那版②的判红理由（坐标被钳到第 0 列后照常回读）依然成立，只是触发它的字段换了。</para></summary>
    [Fact]
    public void 交换链未知时视为无上界且溢出字段一律拒绝()
    {
        var unknownSwap = new RenderTargetInfo(0, 0, 0, 0, 10, 20, 300, 200, 8, false);
        Assert.Equal(R(10, 20, 300, 200),
            NativeFrameReadback.ResolveRect(true, unknownSwap, 1920, 1080));

        var overflowed = new RenderTargetInfo(0, 0, 0, 0, 10, 20, uint.MaxValue, 200, 8, false);
        Assert.Null(NativeFrameReadback.ResolveRect(true, overflowed, 1920, 1080));
    }

    /// <summary>尺寸两个字段溢出 ⇒ <c>null</c>；画盒被<b>完全推出</b>缓冲（含负原点推到左外侧）同样
    /// 求交出空矩形 ⇒ <c>null</c>。
    ///
    /// <para><b>本条原来钉的是"Dest 四个字段同一判据"</b>：<c>destX/destY/destWidth/destHeight</c>
    /// 当时都是 <c>uint</c>，谁 ≥ 2^31 就整体拒绝。内核 v2 把两个原点改成<b>有符号</b>之后
    /// "坐标 ≥ 2^31"已不可表示，那两例失去对象（改前它们靠 <c>uint.MaxValue</c> 触发，现在连编译都不过），
    /// 于是换成两件<b>现在真的会发生</b>的不可信形状：尺寸溢出、以及求交后为空。
    /// ⚠ 有意保留的不对称：盒体整体越过<b>右/下</b>界时仍留 1 列可读（原点钳进 [0, swap−1] 的旧契约，
    /// 有上面的用例钉着）；而整体越过<b>左/上</b>界时给 <c>null</c> —— 后者意味着内核报回的盒子和
    /// 缓冲根本没有交集，那已经不是"裁一点"而是数据不可信。</para>
    ///
    /// <para><b>期望值（手算，不调用被测实现）</b>：交换链 <c>1000×500</c>，
    /// ① <c>dest=(-5000,-100,1000,800)</c> ⇒ 横占 <c>[-5000,-4000)</c> 与 <c>[0,1000)</c> 无交集
    /// ⇒ <c>w=−4000−0&lt;0</c> ⇒ <c>null</c>；② <c>DestWidth=uint.MaxValue</c> ⇒ 尺寸溢出 ⇒ <c>null</c>；
    /// ③ <c>DestHeight=uint.MaxValue</c> ⇒ 尺寸溢出 ⇒ <c>null</c>。</para></summary>
    [Fact]
    public void 尺寸溢出或求交为空_一律返回空()
    {
        const uint max = uint.MaxValue;

        var fullyOffLeft = new RenderTargetInfo(1000, 500, 1000, 500, -5000, -100, 1000, 800, 8, false);
        Assert.Null(NativeFrameReadback.ResolveRect(true, fullyOffLeft, 1920, 1080));

        var wOverflow = new RenderTargetInfo(1000, 500, 1000, 500, 100, 100, max, 400, 8, false);
        Assert.Null(NativeFrameReadback.ResolveRect(true, wOverflow, 1920, 1080));

        var hOverflow = new RenderTargetInfo(1000, 500, 1000, 500, 100, 100, 800, max, 8, false);
        Assert.Null(NativeFrameReadback.ResolveRect(true, hOverflow, 1920, 1080));

        // 对照组：同样的交换链、只把溢出字段换成合法值 ⇒ 必须照常给出矩形，
        // 否则"统一判据"会被写成"凡可疑就 null"而把正常回读一起打死。
        var sane = new RenderTargetInfo(1000, 500, 1000, 500, 100, 100, 800, 400, 8, false);
        Assert.Equal(R(100, 100, 800, 400),
            NativeFrameReadback.ResolveRect(true, sane, 1920, 1080));
    }

    /// <summary>退化输入 ⇒ <c>null</c>（调用方据此回退抓屏），不得产出 0 宽/0 高的矩形。
    ///
    /// <para><b>期望值</b>：无 RTInfo 且视频尺寸为 0 ⇒ 退回的矩形宽或高为 0 ⇒
    /// <c>w=0</c>（或 <c>h=0</c>）⇒ 返回 <c>null</c>。
    /// 正常路径上调用方已先挡掉 <c>VideoWidth/Height ≤ 0</c>，这里是该函数的自保边界
    /// —— 少一层守卫就会 <c>new Bitmap(0,0)</c> 抛 <c>ArgumentException</c>。</para></summary>
    [Fact]
    public void 退化输入返回空()
    {
        Assert.Null(NativeFrameReadback.ResolveRect(false, default, 0, 0));
        Assert.Null(NativeFrameReadback.ResolveRect(false, default, 0, 1080));
        Assert.Null(NativeFrameReadback.ResolveRect(true, default, 0, 0));
    }

    /// <summary>期望矩形用构造器显式写出（<c>FrameReadbackRect?</c>），
    /// 使 <c>Assert.Equal</c> 的泛型推断落在可空值类型上。</summary>
    private static FrameReadbackRect? R(int x, int y, int width, int height)
        => new FrameReadbackRect(x, y, width, height);
}
