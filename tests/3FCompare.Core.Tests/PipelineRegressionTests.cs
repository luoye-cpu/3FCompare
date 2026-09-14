using _3FCompare.Core.Display;
using _3FCompare.Core.Sync;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 管线审查（docs/13）第 2 批回归：把 C2 / C4 / C6 里原本散在 UI 层的纯计算下沉到 Core，
/// 用确定性单测钉死。这三项的共同特点是"不崩溃、只静默失效"，实机很难复现，必须有单测兜底。
/// </summary>
public class PipelineRegressionTests
{
    // ══════════ C6：会话布局代码的保存 / 还原映射 ══════════

    [Theory]
    [InlineData(false, 1, GridLayout.Code2x2)]
    [InlineData(false, 2, GridLayout.Code2x2)]
    [InlineData(false, 4, GridLayout.Code2x2)]
    [InlineData(false, 5, GridLayout.Code3x3)]
    [InlineData(false, 9, GridLayout.Code3x3)]
    [InlineData(true, 1, GridLayout.CodeSingle)]
    [InlineData(true, 9, GridLayout.CodeSingle)]
    public void CodeFor_按单屏与路数取代码(bool singleView, int count, int expected)
        => Assert.Equal(expected, GridLayout.CodeFor(singleView, count));

    [Theory]
    [InlineData(GridLayout.CodeSingle, true)]
    [InlineData(GridLayout.Code2x2, false)]
    [InlineData(GridLayout.Code3x3, false)]
    [InlineData(GridLayout.CodeAuto, false)]
    public void IsSingleView_仅单屏代码为真(int code, bool expected)
        => Assert.Equal(expected, GridLayout.IsSingleView(code));

    [Theory]
    [InlineData(GridLayout.Code2x2, "2x2")]
    [InlineData(GridLayout.Code3x3, "3x3")]
    [InlineData(GridLayout.CodeSingle, "auto")]
    [InlineData(GridLayout.CodeAuto, "auto")]
    public void PresetOf_代码映射到预设名(int code, string expected)
        => Assert.Equal(expected, GridLayout.PresetOf(code));

    [Fact]
    public void 保存与还原是同一套映射_往返不丢布局()
    {
        foreach (var singleView in new[] { true, false })
        {
            for (var count = 1; count <= 9; count++)
            {
                var code = GridLayout.CodeFor(singleView, count);
                Assert.Equal(singleView, GridLayout.IsSingleView(code));
                // 还原侧用的预设名必须是 SetGridLayout 认得的四个取值之一
                Assert.Contains(GridLayout.PresetOf(code), new[] { "auto", "2x2", "3x3" });
            }
        }
    }

    // ══════════ C4：探针坐标 → 源视频像素 ══════════

    [Fact]
    public void MapFallback_分母是表面尺寸_四分之一处映射到四分之一()
    {
        const int vw = 1920, vh = 1080;
        const double sw = 800, sh = 450; // 表面物理尺寸（DIP × RenderScaling）
        var q = VideoPixelMap.MapFallback(sw * 0.25, sh * 0.25, sw, sh, vw, vh);
        Assert.NotNull(q);
        Assert.Equal(480, q!.Value.X);
        Assert.Equal(270, q!.Value.Y);
    }

    [Fact]
    public void MapFallback_不退化成常量_左右两点结果不同()
    {
        // 缺陷形态：分母退化成 physX 自己 → physX/physX ≡ 1 → 任何位置都映射到右下角。
        // 若回归，左 1/4 与右 3/4 会得到同一个 X（都等于 vw-1）。
        const int vw = 1920, vh = 1080;
        const double sw = 800, sh = 450;
        var left = VideoPixelMap.MapFallback(sw * 0.25, sh * 0.5, sw, sh, vw, vh);
        var right = VideoPixelMap.MapFallback(sw * 0.75, sh * 0.5, sw, sh, vw, vh);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.True(left!.Value.X < right!.Value.X, $"左右映射未区分：{left.Value.X} vs {right.Value.X}");
        Assert.True(left.Value.X < vw / 2, $"左侧点未落在左半区：{left.Value.X}");
    }

    [Fact]
    public void MapFallback_边界与无效输入()
    {
        const int vw = 1920, vh = 1080;
        const double sw = 800, sh = 450;
        // 未布局（尺寸 0）→ null，而不是产出一个假坐标
        Assert.Null(VideoPixelMap.MapFallback(0, 0, 0, 0, vw, vh));
        Assert.Null(VideoPixelMap.MapFallback(0, 0, sw, sh, 0, vh));
        // 贴边/越界 → 钳到最后一个像素，不得越界
        var edge = VideoPixelMap.MapFallback(sw, sh, sw, sh, vw, vh);
        Assert.NotNull(edge);
        Assert.Equal(vw - 1, edge!.Value.X);
        Assert.Equal(vh - 1, edge!.Value.Y);
        var neg = VideoPixelMap.MapFallback(-10, -10, sw, sh, vw, vh);
        Assert.NotNull(neg);
        Assert.Equal(0, neg!.Value.X);
        Assert.Equal(0, neg!.Value.Y);
    }

    [Fact]
    public void MapToSource_letterbox黑边返回null_内容区内线性映射()
    {
        const int vw = 1920, vh = 1080;
        // 画面上下有黑边：内容区 y∈[100, 980)，宽满屏
        const double dx = 0, dy = 100, dw = 1920, dh = 880;
        Assert.Null(VideoPixelMap.MapToSource(960, 50, dx, dy, dw, dh, vw, vh));   // 上黑边
        Assert.Null(VideoPixelMap.MapToSource(960, 1000, dx, dy, dw, dh, vw, vh)); // 下黑边
        var top = VideoPixelMap.MapToSource(960, 100, dx, dy, dw, dh, vw, vh);
        Assert.NotNull(top);
        Assert.Equal(0, top!.Value.Y);
        var mid = VideoPixelMap.MapToSource(960, 540, dx, dy, dw, dh, vw, vh);
        Assert.NotNull(mid);
        Assert.Equal(960, mid!.Value.X);
        Assert.Equal(540, mid!.Value.Y);
    }

    [Fact]
    public void MapToSource_无效尺寸返回null()
    {
        Assert.Null(VideoPixelMap.MapToSource(10, 10, 0, 0, 0, 100, 1920, 1080));
        Assert.Null(VideoPixelMap.MapToSource(10, 10, 0, 0, 100, 0, 1920, 1080));
        Assert.Null(VideoPixelMap.MapToSource(10, 10, 0, 0, 100, 100, 0, 1080));
    }

    // ══════════ C2：伪变速推进量钳制 ══════════

    [Fact]
    public void SeekAdvance_正常一秒增量按倍速放大()
    {
        var oneSec = TimeSpan.TicksPerSecond;
        Assert.Equal(oneSec, PlaybackSpeed.SeekAdvanceTicks(oneSec, 2.0));
        Assert.Equal(oneSec * 3, PlaybackSpeed.SeekAdvanceTicks(oneSec, 4.0));
    }

    [Theory]
    [InlineData(3L * 10_000_000, 2.0)]   // 3s：超过 2s 上限
    [InlineData(30L * 10_000_000, 4.0)]  // 30s：典型的"整个已播放时长"误用
    [InlineData(0L, 2.0)]                // 未推进
    [InlineData(-10_000_000L, 2.0)]      // 回退（Seek 之后）
    public void SeekAdvance_异常增量一律不跳(long elapsed, double speed)
        => Assert.Equal(0, PlaybackSpeed.SeekAdvanceTicks(elapsed, speed));

    [Fact]
    public void SeekAdvance_二秒边界内且非加速档不跳()
    {
        var twoSec = 2 * TimeSpan.TicksPerSecond;
        Assert.Equal(twoSec, PlaybackSpeed.SeekAdvanceTicks(twoSec, 2.0)); // 边界含
        Assert.Equal(0, PlaybackSpeed.SeekAdvanceTicks(twoSec + 1, 2.0));  // 越界不含
        Assert.Equal(0, PlaybackSpeed.SeekAdvanceTicks(TimeSpan.TicksPerSecond, 1.0)); // 1× 不跳
        Assert.Equal(0, PlaybackSpeed.SeekAdvanceTicks(TimeSpan.TicksPerSecond, 0.5)); // 慢速未接线
    }

    // ══════════ C9：时间码「秒内帧号」必须与显示的秒同源 ══════════
    //
    // 旧实现用 FrameIndex % round(fps)（标称帧网格），而时间码的 HH:MM:SS 来自
    // Position100ns（墙上时间）。23.976 / 29.97 下两套网格每秒相对滑移，
    // 约 1000 s 后错开整整一个周期 → "秒刚跳过去，帧号却是 29"。

    private const long Sec = TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData(29.97)]
    [InlineData(23.976)]
    [InlineData(25.0)]
    [InlineData(60.0)]
    [InlineData(30.0)]
    public void FrameInSecond_整秒处一律为零_帧号与秒同源(double fps)
    {
        // 秒边界上（整秒）帧号必须是 0：这是"帧号属于当前这一秒"的判据。
        // 旧实现的反例：500s @29.97 → Floor(500*29.97)=14985，14985 % 30 = 15（应为 0）。
        for (var s = 0; s <= 2000; s += 7)
            Assert.Equal(0, Timecode.FrameInSecond(s * Sec, fps));
    }

    [Theory]
    [InlineData(29.97, 500, 14)]   // 半秒 → Floor(0.5*29.97)=14
    [InlineData(23.976, 500, 11)]  // Floor(0.5*23.976)=11
    [InlineData(25.0, 500, 12)]
    [InlineData(60.0, 500, 30)]
    public void FrameInSecond_半秒处取中间帧(double fps, int second, int expected)
        => Assert.Equal(expected, Timecode.FrameInSecond(second * Sec + Sec / 2, fps));

    [Theory]
    [InlineData(29.97)]
    [InlineData(23.976)]
    public void FrameInSecond_秒内单调不越界(double fps)
    {
        var max = (int)Math.Ceiling(fps) - 1;
        var prev = -1;
        for (var i = 0; i < 200; i++)
        {
            var t = 137 * Sec + i * (Sec / 200);   // 第 137 秒内均匀取样
            var ff = Timecode.FrameInSecond(t, fps);
            Assert.InRange(ff, 0, max);
            Assert.True(ff >= prev, $"帧号在秒内回退了：{prev} → {ff}");
            prev = ff;
        }
    }

    [Fact]
    public void FrameInSecond_秒末取到最后一帧()
    {
        // 一秒的最后一个 tick：Floor(((10^7-1)/10^7) * 29.97) = 29（上限内）
        Assert.Equal(29, Timecode.FrameInSecond(501 * Sec - 1, 29.97));
        Assert.Equal(23, Timecode.FrameInSecond(501 * Sec - 1, 23.976));
    }

    [Theory]
    [InlineData(0, 29.97)]
    [InlineData(-1, 29.97)]
    [InlineData(0, 0)]
    [InlineData(10 * 10_000_000L, 0)]
    [InlineData(10 * 10_000_000L, -30.0)]
    public void FrameInSecond_无效输入返回零(long pos, double fps)
        => Assert.Equal(0, Timecode.FrameInSecond(pos, fps));
}
