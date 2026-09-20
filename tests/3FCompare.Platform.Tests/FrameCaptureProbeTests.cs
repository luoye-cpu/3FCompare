using _3FCompare.App.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 探测映射（<c>FrameCapture.ProbeFrom</c>）的守门测试。
///
/// <para>它是"WGC 不可用 ⇒ 走 GDI"这一整类回退的**唯一输入源**，也是整个抓屏链路里唯一会碰到
/// "原生库不存在"这种**预期状态**的地方：<c>3FC.WgcCapture.dll</c> 是内嵌资源、由 Program 启动时解压，
/// 便携部署下解压失败、或用户只拷了 exe 时都会缺库。此时 <c>Wgc_IsSupported()</c> 抛
/// <see cref="DllNotFoundException"/>，若让它冒泡就会掀掉抓帧线程（而不是体面地退回 GDI）。</para>
///
/// <para><b>期望值来源</b>：docs/27 §三 表格第 2 行（"DLL 缺失 / 加载失败 → 走 GDI"）与 §四
/// （"缺库是预期状态，不是故障"）；"库可用但不支持"是同一张表第 1 行的输入，二者必须可区分。</para>
/// </summary>
public class FrameCaptureProbeTests
{
    /// <summary>正常路径：原生报告支持（返回非 0）⇒ 库可用 + 支持。</summary>
    [Fact]
    public void 原生报告支持时_库可用且支持()
    {
        var result = FrameCapture.ProbeFrom(() => 1, out var failure);

        Assert.True(result.DllAvailable);
        Assert.True(result.Supported);
        Assert.Null(failure);
    }

    /// <summary>
    /// 系统不支持（<c>Wgc_IsSupported()==0</c>：远程桌面、企业策略禁用、老系统）。
    /// 此时**库是好的** —— 错报成"库缺失"会把日志引向错误方向（去查打包问题）。
    /// </summary>
    [Fact]
    public void 原生报告不支持时_库可用但不支持()
    {
        var result = FrameCapture.ProbeFrom(() => 0, out var failure);

        Assert.True(result.DllAvailable);
        Assert.False(result.Supported);
        Assert.Null(failure);
    }

    /// <summary>缺库：必须被吃掉并降级为"库不可用"，异常本身留给调用方记 WARN（只记一次）。</summary>
    [Fact]
    public void 原生库缺失时_捕获异常并报库不可用()
    {
        var ex = new DllNotFoundException("3FC.WgcCapture.dll");

        var result = FrameCapture.ProbeFrom(() => throw ex, out var failure);

        Assert.False(result.DllAvailable);
        Assert.False(result.Supported);
        Assert.Same(ex, failure);
    }

    /// <summary>
    /// 位数/架构不符（x64 进程加载了别的架构的 DLL）抛的是 <see cref="BadImageFormatException"/>，
    /// 导出符号对不上抛 <see cref="EntryPointNotFoundException"/> —— 都要一并吃掉，
    /// 不能只 catch 一个具体类型。
    /// </summary>
    [Fact]
    public void 原生库加载失败时_同样被捕获()
    {
        var badImage = new BadImageFormatException("试图加载格式不正确的程序");
        var noEntry = new EntryPointNotFoundException("Wgc_IsSupported");

        var r1 = FrameCapture.ProbeFrom(() => throw badImage, out var f1);
        var r2 = FrameCapture.ProbeFrom(() => throw noEntry, out var f2);

        Assert.False(r1.DllAvailable);
        Assert.False(r1.Supported);
        Assert.Same(badImage, f1);

        Assert.False(r2.DllAvailable);
        Assert.False(r2.Supported);
        Assert.Same(noEntry, f2);
    }

    /// <summary>
    /// "不支持"与"库不可用"必须可区分：两条分支都走 GDI，但原因不同
    /// （前者换机器/关策略即可恢复，后者是包缺文件），日志与排障方向完全不同。
    /// </summary>
    [Fact]
    public void 不支持与库不可用可区分()
    {
        var notSupported = FrameCapture.ProbeFrom(() => 0, out var noFailure);
        var unavailable = FrameCapture.ProbeFrom(() => throw new DllNotFoundException("3FC.WgcCapture.dll"), out var failure);

        Assert.True(notSupported.DllAvailable);
        Assert.Null(noFailure);

        Assert.False(unavailable.DllAvailable);
        Assert.NotNull(failure);
    }
}
