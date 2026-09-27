using _3FCompare.App.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 抓屏线的释放路径。
///
/// <para>两条线持有的资源不同：<see cref="WgcFrameCapture"/> 持有一个原生 handle（内含 D3D11 设备与会话），
/// <see cref="GdiFrameCapture"/> 每次抓帧成对申请/释放 HDC、不持有跨调用的非托管资源。
/// 这里钉的是两类最容易翻车的性质：<b>重复释放必须安全</b>、<b>释放后不得再触碰原生资源</b>
/// （use-after-free 防线）。</para>
///
/// <para><b>为什么只走这些分支</b>：<c>3FC.WgcCapture.dll</c> 是 UI 工程的<b>内嵌资源</b>
/// （<c>&lt;EmbeddedResource LogicalName="3FC.WgcCapture.dll"&gt;</c>，见
/// <c>src/3FCompare/3FCompare.csproj</c>），只在程序启动时解压，<b>从不</b>出现在测试宿主的
/// 探测路径上——所以真正调用原生库必然抛 <see cref="DllNotFoundException"/>，
/// 那样测出来的就不是释放逻辑了。故所有用例都停在"P/Invoke 之前就返回"的分支上 ——
/// 这也恰好是正确性最要紧的地方。</para>
///
/// <para><b>这不是"依赖机器环境"</b>（docs/41 §4.3 曾如此归类，已复核为不成立）：
/// "原生库不可加载"是<b>构建期固定属性</b>，不是某台机器的偶然状态。而且用例的判红
/// 与环境无关——把 <c>_disposed</c> 判断挪到 <c>EnsureCreated()</c> 之后（本类要守的回归），
/// 缺库时留下 <c>CREATE_FAILED(2)</c>、有库时留下一次真实抓帧失败，两种环境都会判红。</para>
/// </summary>
public class CaptureLaneResourceTests
{
    private const nint Hwnd = 0x1234;

    /// <summary>
    /// 从未创建过原生 handle 的实例被释放两次：必须安全。
    /// 若 Dispose 不做 <c>handle == 0</c> 短路而直接调 <c>Wgc_Destroy</c>，
    /// 在缺库环境下就会抛异常、在正常环境下则是把空指针交给原生层。
    /// </summary>
    [Fact]
    public void WGC线_重复释放是安全的()
    {
        var lane = new WgcFrameCapture();

        lane.Dispose();
        lane.Dispose();

        // 释放后仍必须处于"拒绝抓帧"状态
        Assert.Null(lane.Capture(Hwnd));
    }

    /// <summary>
    /// 释放后拒绝抓帧，且**在尝试创建原生会话之前**就拒绝。
    /// 若实现把 <c>_disposed</c> 判断挪到 <c>EnsureCreated()</c> 之后，
    /// 释放过的实例会重新建出一个原生会话（句柄泄漏），并在这里留下 CREATE_FAILED(2)。
    /// </summary>
    [Fact]
    public void WGC线_释放后不再触碰原生资源()
    {
        var lane = new WgcFrameCapture();
        lane.Dispose();

        Assert.Null(lane.Capture(Hwnd));

        Assert.Equal(WgcNative.Ok, lane.LastErrorCode);
        Assert.Equal(string.Empty, lane.LastErrorMessage);
    }

    /// <summary>
    /// 零句柄：必须在任何原生调用之前返回 null，且**不得记录成一次失败**。
    /// 否则调用方（门面）会为一次"根本没试"的抓帧累计失败，把 WGC 白白降级。
    /// </summary>
    [Fact]
    public void WGC线_零句柄不触碰原生库也不记录失败()
    {
        using var lane = new WgcFrameCapture();

        Assert.Null(lane.Capture(0));

        Assert.Equal(WgcNative.Ok, lane.LastErrorCode);
        Assert.Equal(string.Empty, lane.LastErrorMessage);
    }

    /// <summary>
    /// GDI 线按设计不持有非托管资源（每次抓帧内部成对申请/释放 HDC），
    /// 因此 Dispose 是空操作、可重复调用；零句柄同样在 GetAncestor/GetWindowRect 之前返回。
    /// </summary>
    [Fact]
    public void GDI线_不持有非托管资源且零句柄直接返回null()
    {
        var lane = new GdiFrameCapture();

        Assert.Null(lane.Capture(0));

        lane.Dispose();
        lane.Dispose();
    }
}
