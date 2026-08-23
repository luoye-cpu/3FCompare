namespace _3FCompare.Core.Sync;

/// <summary>步进配置（F12：双步进按钮的步长，来自二级设置窗口）。</summary>
public sealed record StepProfile
{
    /// <summary>按帧步进步长（默认 ±1 帧）。</summary>
    public int FrameStep { get; init; } = 1;

    /// <summary>按秒步进步长（默认 ±1 秒）。</summary>
    public double SecondsStep { get; init; } = 1.0;
}