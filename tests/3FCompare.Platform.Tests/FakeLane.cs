using System.Drawing;
using System.Drawing.Imaging;
using _3FCompare.App.Capture;
using _3FCompare.Core.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 抓屏线的测试替身：记录调用次数 / 句柄，并按脚本决定每次调用是否产出位图。
///
/// <para><b>它不碰任何 Win32 或原生库</b> —— 这正是能把门面（<see cref="FrameCapture"/>）的
/// 全部选路分支跑起来的原因。真实抓帧慢（单次超时约 2s）且依赖窗口与会话，
/// 放进单测既慢又不稳（docs/27 §五 把端到端单列为真机验证层）。</para>
/// </summary>
internal sealed class FakeLane : IFrameCapture
{
    private readonly Func<int, bool> _succeedsOnCall;

    /// <param name="route">本线对外声明的路径标识。</param>
    /// <param name="succeeds">第 n 次（从 1 起）调用是否产出位图。</param>
    public FakeLane(CaptureRoute route, Func<int, bool> succeeds)
    {
        Route = route;
        _succeedsOnCall = succeeds;
    }

    /// <param name="succeeds">每次调用是否都产出位图。</param>
    public FakeLane(CaptureRoute route, bool succeeds = true)
        : this(route, _ => succeeds)
    {
    }

    public CaptureRoute Route { get; }

    /// <summary>被调用次数 —— 选路断言的核心：门面到底有没有真的去试这条线。</summary>
    public int CaptureCalls { get; private set; }

    /// <summary>每次调用收到的句柄（验证门面把目标句柄原样传给了选中那条线）。</summary>
    public List<nint> Handles { get; } = new();

    /// <summary>已交出的位图。用来验证"交出的位图没有被门面吞掉"（吞掉即泄漏）。</summary>
    public List<Bitmap> HandedOut { get; } = new();

    public Bitmap? Capture(nint hwnd)
    {
        CaptureCalls++;
        Handles.Add(hwnd);

        if (!_succeedsOnCall(CaptureCalls)) return null;

        var bmp = TestBitmaps.New();
        HandedOut.Add(bmp);
        return bmp;
    }

    public void Dispose() { }

    /// <summary>释放所有已交出的位图（所有权在调用方，替身不代为释放）。</summary>
    public void DisposeHandedOut()
    {
        foreach (var bmp in HandedOut) bmp.Dispose();
    }
}

internal static class TestBitmaps
{
    /// <summary>2×2 的极小位图：只关心"有没有交出一张图"，不关心内容。</summary>
    public static Bitmap New() => new(2, 2, PixelFormat.Format32bppArgb);
}
