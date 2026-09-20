namespace _3FCompare.Core.Sync;

/// <summary>帧时间换算 / 双步进目标计算（04 ∅3.2, F12）。纯逻辑，可单测。</summary>
public static class FrameTimeline
{
    public const long TicksPerSecond = 10_000_000; // 100ns 单位

    /// <summary>由帧率得出单帧时长（100ns）。fps 非法（≤0 或非有限值）时返回 0。
    ///
    /// <para><b>为什么必须挡非有限值</b>：只挡 <c>fps &lt;= 0</c> 是不够的——
    /// <c>fps = 1e-300</c> 时 <c>TicksPerSecond / fps</c> 得到 <c>+Infinity</c>，
    /// 而 .NET Core 3.0 起浮点→整型的转换在溢出时是<b>饱和</b>的（不再产生
    /// <c>long.MinValue</c>），于是得到 <c>long.MaxValue</c>；后续
    /// <c>frames * long.MaxValue</c> 静默回绕成负数 ⇒ "前进一帧"反而跳回片头。</para>
    /// </summary>
    public static long FrameDuration100ns(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0) return 0;
        var ticks = TicksPerSecond / fps;
        // 商本身也必须验范围：fps 极小时 1e7/fps 会得到一个"有限但巨大"的值
        //（例如 fps=1e-300 ⇒ 1e307），而 .NET Core 3.0 起浮点→整型是**饱和**转换，
        // 结果就是 long.MaxValue —— 后续再乘步长就静默回绕成负数。
        // 所以必须在转换前卡住，而不是指望转换本身报错。
        if (!double.IsFinite(ticks) || ticks > long.MaxValue) return 0;
        return (long)Math.Round(ticks);
    }

    /// <summary>按帧步进目标时间（clamp 到 [0, duration]）。
    /// fps 非法（含 NaN / ±∞ 或导致单帧时长溢出）时步长为 0 ⇒ 原地不动，
    /// 而不是跳到片头/片尾。</summary>
    public static long StepByFrames(long current100ns, long duration100ns, int frames, double fps)
    {
        // 中间量走 double：frames × 单帧时长 可能超出 long 的范围，
        // 直接 long 相乘会静默回绕 ⇒ "前进 N 帧"反而跳回片头。
        var delta = (double)frames * FrameDuration100ns(fps);
        if (delta >= long.MaxValue) return Clamp(duration100ns, duration100ns);
        if (delta <= long.MinValue) return Clamp(0, duration100ns);
        return Clamp(current100ns + (long)delta, duration100ns);
    }

    /// <summary>按秒步进目标时间（clamp 到 [0, duration]）。
    /// seconds 为 NaN 时返回 <paramref name="current100ns"/>（不步进）——
    /// NaN 的三个比较（&gt;、&lt;、==）全为 false，若不显式处理会静默走到
    /// <c>(long)NaN</c>（同样饱和为 0），表现为"按了快进却毫无反应"且没有任何报错。</summary>
    public static long StepBySeconds(long current100ns, long duration100ns, double seconds)
    {
        if (!double.IsFinite(seconds)) return Clamp(current100ns, duration100ns);
        var delta = seconds * TicksPerSecond;
        // 防止极大值在 (long) 转换时溢出为负值（UI 输入受限但 API 应健壮）
        if (delta > long.MaxValue) return Clamp(duration100ns, duration100ns);
        if (delta < long.MinValue) return Clamp(0, duration100ns);
        return Clamp(current100ns + (long)delta, duration100ns);
    }

    private static long Clamp(long value, long duration100ns)
        => Math.Clamp(value, 0L, Math.Max(0L, duration100ns));
}