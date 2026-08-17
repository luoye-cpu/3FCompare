namespace _3FCompare.Core.Sync;

/// <summary>帧时间换算 / 双步进目标计算（04 ∅3.2, F12）。纯逻辑，可单测。</summary>
public static class FrameTimeline
{
    public const long TicksPerSecond = 10_000_000; // 100ns 单位

    /// <summary>由帧率得出单帧时长（100ns）。fps≤0 时返回 0。</summary>
    public static long FrameDuration100ns(double fps)
        => fps > 0 ? (long)Math.Round(TicksPerSecond / fps) : 0;

    /// <summary>按帧步进目标时间（clamp 到 [0, duration]）。</summary>
    public static long StepByFrames(long current100ns, long duration100ns, int frames, double fps)
    {
        var delta = frames * FrameDuration100ns(fps);
        return Clamp(current100ns + delta, duration100ns);
    }

    /// <summary>按秒步进目标时间（clamp 到 [0, duration]）。</summary>
    public static long StepBySeconds(long current100ns, long duration100ns, double seconds)
    {
        var delta = (long)(seconds * TicksPerSecond);
        return Clamp(current100ns + delta, duration100ns);
    }

    private static long Clamp(long value, long duration100ns)
        => Math.Clamp(value, 0L, Math.Max(0L, duration100ns));
}