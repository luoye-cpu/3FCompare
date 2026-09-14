namespace _3FCompare.Core.Sync;

/// <summary>
/// 伪变速（周期性 Seek 模拟变速播放）的推进量计算（纯逻辑，可单测）。
/// <para><b>历史缺陷（C2）</b>：倍速切换时若不复位基准点，<c>mediaElapsed</c> 会等于
/// "整个已播放时长"而不是"近 1s 的增量"——30s 处切 2× 会直接跳到 60s，4× 则跳到片尾。
/// 除了在事件里复位基准，这里再兜一层 2s 上限：超过就宁可跳过本次跳变，也不把位置推走。</para>
/// </summary>
public static class PlaybackSpeed
{
    /// <summary>单 tick 允许的媒体推进上限（2 秒）。
    /// 正常每 tick 只增长 ≤1s；超出说明基准点异常（首次进入 / 会话刚重建 / 被外部 Seek）。</summary>
    public const long MaxElapsedTicks = 2 * TimeSpan.TicksPerSecond;

    /// <summary>本次轮询应额外 Seek 的推进量（ticks）。返回 0 表示本次不跳变。</summary>
    /// <param name="mediaElapsedTicks">距上次跳变的媒体推进量（当前位置 − 基准位置）。</param>
    /// <param name="speed">目标倍速（&gt;1 才加速；≤1 表示原生不支持减速，恒返回 0）。</param>
    public static long SeekAdvanceTicks(long mediaElapsedTicks, double speed)
    {
        if (speed <= 1.0) return 0;
        if (mediaElapsedTicks <= 0 || mediaElapsedTicks > MaxElapsedTicks) return 0;
        return (long)(mediaElapsedTicks * (speed - 1.0));
    }
}
