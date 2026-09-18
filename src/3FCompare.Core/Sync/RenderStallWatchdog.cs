namespace _3FCompare.Core.Sync;

/// <summary>停滞恢复应采取的动作。</summary>
public enum StallAction
{
    /// <summary>无需动作。</summary>
    None,

    /// <summary>第一级：Pause → Play → Redraw（重启呈现管线，代价小）。</summary>
    LightRecovery,

    /// <summary>第二级：轻量恢复无效，升级为完整会话重建。</summary>
    FullRebuild,
}

/// <summary>
/// 呈现停滞看门狗（纯计算，可单测）。
/// <para>从 <c>MainWindow.Playback</c> 的轮询逻辑下沉而来：那里混着 P/Invoke、UI 状态与
/// 计时器，无法单测；而历史上两个真实缺陷（P1-5 暂停时空转累加、P1-6 用轮询次数当时间
/// 导致平移期间 415ms 就误判）都出在**判定**上，不是出在恢复动作上。</para>
/// </summary>
public sealed class RenderStallWatchdog
{
    /// <summary>连续完整重建的次数上限。
    ///
    /// <para><b>为什么要有上限</b>：FullRebuild 由调用方走 <c>RecoverFromFailedAsync</c>
    /// 重建整个会话（重新打开媒体、重建 D3D 设备）。若底层是"换了显示器/驱动就必然停滞"
    /// 这类不可恢复的故障，重建后 presented 依旧不增长，而 <c>_lightTried</c> 只在
    /// presented 增长时才复位 ⇒ 每 2×阈值（约 1250ms）就再吐一次 FullRebuild，
    /// 形成周期性**会话重建风暴**：画面反复黑屏重载，CPU/IO 持续打满，
    /// 用户连"暂停"都来不及点。</para>
    ///
    /// <para>达到上限后封顶，只保留"不再升级"这一档，直到 <see cref="Reset(long)"/>
    ///（换媒体 / 显式重建会话）才恢复完整能力。</para></summary>
    private const int MaxFullRebuilds = 3;

    private readonly long _thresholdMs;
    private long _lastPresented = -1;
    private long? _stallSinceMs;
    private bool _lightTried;
    private int _fullRebuildCount;

    /// <param name="threshold">presented 持续无增长多久判定为停滞。</param>
    public RenderStallWatchdog(System.TimeSpan threshold)
    {
        _thresholdMs = (long)threshold.TotalMilliseconds;
        if (_thresholdMs <= 0) _thresholdMs = 1;
    }

    /// <summary>每个轮询周期调用一次。
    /// <param name="uiPlaying">UI 是否认为在播放。只有为真时才检测 —— 暂停时 presented
    /// 本就不该增长，检测会导致空转累加（P1-5）。</param>
    /// <param name="engineActive">引擎处于 Playing/Ready/Paused（兼容刚点播放的过渡期）。</param>
    /// <param name="presented">本次读到的 presented 计数。</param>
    /// <param name="nowMs">单调递增的毫秒时钟（用真实时长判定，与轮询频率解耦 —— P1-6）。</param>
    /// </summary>
    public StallAction Update(bool uiPlaying, bool engineActive, long presented, long nowMs)
    {
        // 不该呈现的场景一律不检测，并复位（避免恢复播放时立刻误触发）
        if (!uiPlaying || !engineActive)
        {
            Reset(presented);
            return StallAction.None;
        }

        if (_lastPresented < 0)
        {
            _lastPresented = presented;
            return StallAction.None;
        }

        if (presented != _lastPresented)
        {
            // 恢复增长：复位升级标志，下次真实停滞仍从第一级开始
            _lastPresented = presented;
            _stallSinceMs = null;
            _lightTried = false;
            return StallAction.None;
        }

        _stallSinceMs ??= nowMs;
        if (nowMs - _stallSinceMs.Value < _thresholdMs) return StallAction.None;

        if (!_lightTried)
        {
            _lightTried = true;
            // 复位计时：再给轻量恢复一个完整阈值窗口，不达标不升级
            _stallSinceMs = null;
            return StallAction.LightRecovery;
        }

        _stallSinceMs = null;

        // 完整重建次数封顶：presented 不增长时 _lightTried 永远不会复位，
        // 于是每个阈值周期都会走到这里再吐一次 FullRebuild —— 有上限才有出口。
        if (_fullRebuildCount >= MaxFullRebuilds) return StallAction.None;

        _fullRebuildCount++;
        return StallAction.FullRebuild;
    }

    /// <summary>换媒体 / 重建会话后必须复位，否则上一次的升级痕迹会跨媒体残留
    /// （轻量恢复失败过一次 ⇒ 打开新片后第一次真实停滞被直接跳级成完整重建）。
    ///
    /// <para>完整重建配额同样在这里清零：换片 / 重建本身就是一次"完整重建"，
    /// 新会话理应重新拿到全套恢复能力，否则新片的第一次停滞就直接无药可救。</para></summary>
    public void Reset(long presented = -1)
    {
        _lastPresented = presented;
        _stallSinceMs = null;
        _lightTried = false;
        _fullRebuildCount = 0;
    }
}
