using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests.Infrastructure;

/// <summary>
/// 可脚本化的 <see cref="IPlayerEngine"/> / <see cref="IPlayerSession"/> 替身（docs/41 §4.5 配套基建）。
/// </summary>
///
/// <para><b>为什么需要它，而不是继续用 <c>SimulatedEngine</c></b>：
/// <c>SimulatedEngine</c> 是**产品代码**里的演示模式实现，它有三条与真实内核相反的行为
/// （到片尾会回绕、永远不会进入 Ended、Seek 内部 clamp），拿它当替身会得到与实机相反的结论
/// （docs/41 §十三 里 <c>StubSession</c> 就是为此手写的）。本类是**测试专用**替身，
/// 行为由用例显式脚本化，不继承任何产品语义。</para>
///
/// <para><b>设计取向</b>：所有可变状态都是可写的普通属性，所有调用都记数，
/// 让用例能断言"调用了几次/以什么参数调用"，而不是断言某个内部实现细节。</para>
public sealed class FakeEngine : IPlayerEngine
{
    public List<FakeSession> Sessions { get; } = new();
    public List<EngineSessionOptions> CreatedOptions { get; } = new();

    /// <summary>替换 <see cref="EnumerateAdapters"/> 的返回值。</summary>
    public Func<IReadOnlyList<AdapterInfo>>? AdapterProvider { get; set; }

    /// <summary>非空时 <see cref="CreateSession"/> 抛该异常（测"创建失败"的编排路径）。</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>新会话的默认时长（100ns）。默认 1 秒。</summary>
    public long DefaultDuration100ns { get; set; } = 10_000_000;

    /// <summary>新会话的默认帧率。</summary>
    public double DefaultFrameRate { get; set; } = 30.0;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters() =>
        AdapterProvider?.Invoke() ?? Array.Empty<AdapterInfo>();

    public IPlayerSession CreateSession(EngineSessionOptions options)
    {
        CreatedOptions.Add(options);
        if (ThrowOnCreate is not null) throw ThrowOnCreate;
        var s = new FakeSession(options)
        {
            Duration100ns = DefaultDuration100ns,
            MediaInfo = new EngineMediaInfo
            {
                Path = string.Empty,
                Codec = "fake",
                FrameRate = DefaultFrameRate,
                Duration100ns = DefaultDuration100ns,
            },
        };
        Sessions.Add(s);
        return s;
    }
}

/// <summary><see cref="IPlayerSession"/> 的可脚本化替身。
/// 同时实现 <see cref="IFrameSideDataRing"/>：环是脚本化的（<see cref="SideDataRing"/>），
/// 默认空环 ⇒ 每次答 NotFound，与"盘上内核没有这个导出"在调用方看来等价。</summary>
public sealed class FakeSession : IPlayerSession, IFrameSideDataRing
{
    private bool _disposed;

    public FakeSession(EngineSessionOptions options) => Options = options;

    public EngineSessionOptions Options { get; }

    // ---- 可脚本化状态 ----
    public long Position100ns { get; set; }
    public long Duration100ns { get; set; }
    public long FrameIndex { get; set; }
    public ulong TimelineGeneration { get; set; }
    public long PresentedVideoFrames { get; set; }
    public long SwapChainPresents { get; set; }
    /// <summary>对应真实内核 <c>FFF3FPSnapshot.presentWait100ns</c>（Present 内部累计阻塞时长）。
    /// 此前替身没有这个字段、<c>ReadSnapshot</c> 也就恒填 0 ⇒ 任何读这一列的单测都只能读到 0，
    /// "列根本没被算"与"算出来就是 0"在测试里分不开（#42 的 Present 归因读数要用的就是它）。</summary>
    public long PresentWait100ns { get; set; }
    public PlayerState State { get; set; } = PlayerState.Idle;
    public EngineMediaInfo? MediaInfo { get; set; }
    public bool Disposed => _disposed;

    // ---- 行为开关 ----
    /// <summary>Seek 是否钳到 [0, Duration100ns]。默认 true（与真实内核一致）。</summary>
    public bool ClampSeek { get; set; } = true;

    /// <summary>true ⇒ <see cref="Seek"/> 被接受（计数、记目标）但<b>什么都不改</b>：位置与
    /// <see cref="TimelineGeneration"/> 原地不动。
    ///
    /// <para>这是在建模真实内核的那条<b>异步失败</b>路径（#42 的负臂夹具）：
    /// <c>PlayerSession::Seek</c> 只做 <c>Enqueue(DoSeek)</c> 并立刻返回 <c>Success</c>，
    /// 真正的 <c>av_seek_frame</c> 在工作线程里跑；它失败时
    /// <c>ReportError(FfmpegFailure, "…playback remained active.", "seek")</c> 后<b>直接 return</b>
    /// ——不改 <c>snapshot_.position100ns</c>、不 <c>++timelineGeneration</c>、也不把状态置 Failed。
    /// 于是调用侧一切正常，位置却永远回不来。</para>
    /// <para>默认 false ⇒ 其余 600+ 条测试的行为一字不变。</para></summary>
    public bool SeekIsNoOp { get; set; }

    /// <summary>非空时 <see cref="OpenAsync"/> 抛该异常。</summary>
    public Exception? ThrowOnOpen { get; set; }

    /// <summary>按真实内核的**状态守卫**拒绝：调用那一刻状态不允许就抛 <c>EngineException</c>。
    ///
    /// <para>默认 false —— 旧的 631 条测试跑在"不允许就静默什么都不做"的宽松假引擎上，
    /// 那个形态**根本表达不出** #42 与本轮抓到的"组播放扇出被拒后永久丢失"。
    /// 守卫集合按仓内已抄录的内核契约（<c>SyncController</c> 里 <c>Play(){ if (state ∉
    /// {Ready,Paused,Ended}) return InvalidState; Enqueue(...); }</c>）与本机实测的两条正文
    /// （<c>Play 失败: InvalidState</c> / <c>Pause 失败: InvalidState</c>）：</para>
    /// <list type="bullet">
    /// <item><description><see cref="Play"/>：仅 <c>Ready/Paused/Ended</c> 可发起播；</description></item>
    /// <item><description><see cref="Pause"/>：仅 <c>Playing</c> 可暂停。</description></item>
    /// </list>
    /// <para>两者都不允许时 ⇒ 同一毫秒内背靠背的"组暂停 + 组播放"会把处于 <c>Opening</c> 的那一路
    /// <b>两边都拒</b>，这正是 2026-10-02 03:52:59.442 的实测形态。</para>
    /// <para><b>只建模"拒绝"这一件事</b>：<c>EngineException.Result</c> 填 0 —— 内核那个错误码
    /// 仓内没有权威来源，编一个数字出来会让后面的断言把假常数当依据。</para></summary>
    public bool GuardThrows { get; set; }

    /// <summary>非空时 <see cref="ReadRenderTargetInfo"/> 返回 false。</summary>
    public bool RenderTargetInfoUnavailable { get; set; }

    public RenderTargetInfo RenderTargetInfoValue { get; set; }

    // ---- 调用计数（供断言）----
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int SeekCount { get; private set; }
    public int SeekFrameCount { get; private set; }
    public int StepFrameCount { get; private set; }
    public int RedrawCount { get; private set; }
    public int DisposeCount { get; private set; }
    public List<long> SeekTargets { get; } = new();
    public List<int> StepDirections { get; } = new();
    public List<(float Zoom, float PanX, float PanY)> ViewTransforms { get; } = new();

    public event EventHandler<EngineEvent>? EngineEvent;

    /// <summary>触发一次引擎事件（测事件传播路径）。</summary>
    public void RaiseEngineEvent(EngineEvent e) => EngineEvent?.Invoke(this, e);

    public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (ThrowOnOpen is not null) throw ThrowOnOpen;
        State = PlayerState.Ready;
        if (MediaInfo is not null)
            MediaInfo = MediaInfo with { Path = localPath };
        return Task.CompletedTask;
    }

    public void Play()
    {
        ThrowIfDisposed();
        PlayCount++;
        if (GuardThrows && State is not (PlayerState.Ready or PlayerState.Paused or PlayerState.Ended))
            throw new EngineException(0, "Play 失败: InvalidState");
        if (State is PlayerState.Ready or PlayerState.Paused or PlayerState.Ended) State = PlayerState.Playing;
    }

    public void Pause()
    {
        ThrowIfDisposed();
        PauseCount++;
        if (GuardThrows && State != PlayerState.Playing)
            throw new EngineException(0, "Pause 失败: InvalidState");
        if (State == PlayerState.Playing) State = PlayerState.Paused;
    }

    public void Stop() { ThrowIfDisposed(); StopCount++; State = PlayerState.Paused; Position100ns = 0; }

    public void Seek(long position100ns)
    {
        ThrowIfDisposed();
        SeekCount++;
        var target = position100ns;
        if (ClampSeek) target = Math.Clamp(target, 0, Duration100ns);
        SeekTargets.Add(target);
        if (SeekIsNoOp) return;      // 异步失败臂：调用被接受，但位置与代数都不动
        Position100ns = target;
        TimelineGeneration++;   // 真实内核：demuxer seek 成功后才递增
    }

    public void SeekFrame(long frameIndex)
    {
        ThrowIfDisposed();
        SeekFrameCount++;
        FrameIndex = Math.Max(0, frameIndex);
        TimelineGeneration++;
    }

    public void StepFrame(int direction)
    {
        ThrowIfDisposed();
        StepFrameCount++;
        StepDirections.Add(direction);
        FrameIndex = Math.Max(0, FrameIndex + Math.Sign(direction));

        // 内核 StepFrame 前进到"下一帧的 PTS"——位置必须跟着帧号走。
        // 少了这一行，SyncController.StepFrames 读到的 master 新位置恒等于旧位置，
        // 从路会被对齐到**步进前**的位置，与实机行为相反（测试要么恒绿要么误报）。
        var fps = MediaInfo?.FrameRate ?? 0;
        if (fps > 0)
            Position100ns = (long)(FrameIndex * (TimeSpan.TicksPerSecond / fps));
    }

    public void SelectAudioStream(int streamIndex) { ThrowIfDisposed(); }
    public void SelectVideoStream(int streamIndex) { ThrowIfDisposed(); }
    public void SetVolume(float volume, bool muted) { ThrowIfDisposed(); }
    public void SetColorMode(ColorMode mode, bool? contentIsHdr = null) { ThrowIfDisposed(); LastColorMode = mode; LastContentIsHdr = contentIsHdr; }

    public ColorMode? LastColorMode { get; private set; }
    public bool? LastContentIsHdr { get; private set; }

    public bool SetPresentConfig(bool tearing) { ThrowIfDisposed(); LastTearing = tearing; return true; }

    /// <summary>最后一次 VSR 请求；null = 从未调用过（用于断言"没接线的路径不会被误调"）。</summary>
    public bool? LastVideoSuperResolutionRequest { get; private set; }

    /// <summary>非空时 <see cref="ReadVideoSuperResolutionStatus"/> 返回它并给出 true。</summary>
    public VideoSuperResolutionStatus? VideoSuperResolutionStatusToReport { get; set; }

    public bool SetVideoSuperResolution(bool enable)
    {
        ThrowIfDisposed();
        LastVideoSuperResolutionRequest = enable;
        return true;
    }

    public bool ReadVideoSuperResolutionStatus(out VideoSuperResolutionStatus status)
    {
        ThrowIfDisposed();
        status = VideoSuperResolutionStatusToReport ?? default;
        return VideoSuperResolutionStatusToReport.HasValue;
    }
    public bool? LastTearing { get; private set; }

    // 动态元数据注入族（2026-10-10 通用通道 + HDR10+ 旧入口）：记录调用供编排层断言。
    public List<DynamicMetadataPacket> DynamicMetadataPackets { get; } = new();
    public List<DynamicMetadataKind> ClearedDynamicMetadataKinds { get; } = new();
    public List<HdrDynamicMetadataEntry> HdrDynamicMetadataEntries { get; } = new();
    public int ClearHdrDynamicMetadataCalls { get; private set; }
    public DynamicMetadataSource? HdrMetadataSource { get; set; } = DynamicMetadataSource.Bitstream;

    /// <summary><see cref="SetDynamicMetadata"/> 的返回值（默认 true）。
    /// 置 false 用来打"内核整批拒绝、旧表续任"那条分支——kind=3 泵必须把它当失败留痕而不是当成功。</summary>
    public bool SetDynamicMetadataResult { get; set; } = true;

    /// <summary>脚本化的中性帧边数据环：帧号 → 原始字节（<see cref="CopyFrameSideDataAfter"/> 按内核契约应答）。</summary>
    public SortedList<long, byte[]> SideDataRing { get; } = new();

    /// <summary>环调用日志：(fromFrameIndex, 是否空缓冲探路)。</summary>
    public List<(long From, bool Probe)> SideDataCalls { get; } = new();

    public bool SetDynamicMetadata(IReadOnlyList<DynamicMetadataPacket> packets)
    {
        ThrowIfDisposed();
        DynamicMetadataPackets.AddRange(packets);
        return SetDynamicMetadataResult;
    }

    /// <summary>与内核 <c>PlayerSession::CopyFrameSideDataAfter</c> 同契约：
    /// 空缓冲只报尺寸不写数据；容量不足报所需尺寸且<b>不消费条目</b>；没匹配答 NotFound。</summary>
    public SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
        byte[]? buffer, out uint requiredSize, out long foundFrameIndex)
    {
        ThrowIfDisposed();
        var probe = buffer is null or { Length: 0 };
        SideDataCalls.Add((fromFrameIndex, probe));
        requiredSize = 0;
        foundFrameIndex = -1;
        foreach (var pair in SideDataRing)
        {
            if (pair.Key < fromFrameIndex) continue;
            foundFrameIndex = pair.Key;
            requiredSize = (uint)pair.Value.Length;
            break;
        }
        if (foundFrameIndex < 0) return SideDataReadOutcome.NotFound;
        if (probe) return SideDataReadOutcome.Found;
        if (buffer!.Length < requiredSize) return SideDataReadOutcome.BufferTooSmall;
        SideDataRing[foundFrameIndex].CopyTo(buffer);
        return SideDataReadOutcome.Found;
    }

    public bool ClearDynamicMetadata(DynamicMetadataKind kind)
    {
        ThrowIfDisposed();
        ClearedDynamicMetadataKinds.Add(kind);
        return true;
    }

    public bool SetHdrDynamicMetadata(IReadOnlyList<HdrDynamicMetadataEntry> entries)
    {
        ThrowIfDisposed();
        HdrDynamicMetadataEntries.AddRange(entries);
        return true;
    }

    public bool ClearHdrDynamicMetadata() { ThrowIfDisposed(); ClearHdrDynamicMetadataCalls++; return true; }
    public DynamicMetadataSource? GetHdrMetadataSource() => HdrMetadataSource;

    public void SetViewTransform(float zoom, float panX, float panY)
    {
        ThrowIfDisposed();
        ViewTransforms.Add((zoom, panX, panY));
    }

    public void SetFitLimitToNative(bool enable)
    {
        ThrowIfDisposed();
        FitLimitToNativeCalls.Add(enable);
    }

    public List<bool> FitLimitToNativeCalls { get; } = new();

    public EngineSnapshot ReadSnapshot() => new()
    {
        Position100ns = Position100ns,
        Duration100ns = Duration100ns,
        FrameIndex = FrameIndex,
        FrameRate = MediaInfo?.FrameRate ?? 0,
        State = State,
        PresentedVideoFrames = PresentedVideoFrames,
        SwapChainPresents = SwapChainPresents,
        PresentWait100ns = PresentWait100ns,
        TimelineGeneration = TimelineGeneration,
    };

    public EngineMediaInfo? ReadMediaInfo() => MediaInfo;

    public bool TryReadPixel(int x, int y, out PixelSample sample)
    {
        sample = new PixelSample(0, 0, 0, 1, 8);
        return false;   // 默认不支持：替身没有真实像素
    }

    public bool TryReadPixelRegion(int x, int y, int width, int height,
        float[] buffer, out uint outputBitDepth)
    {
        outputBitDepth = 8;
        return false;
    }

    public void Redraw() { ThrowIfDisposed(); RedrawCount++; }

    public bool ReadRenderTargetInfo(out RenderTargetInfo info)
    {
        info = RenderTargetInfoValue;
        return !RenderTargetInfoUnavailable;
    }

    public void Dispose() { DisposeCount++; _disposed = true; }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FakeSession));
    }
}

/// <summary>手动推进的 <see cref="IClock"/>（替换测试里的 <c>Thread.Sleep</c>）。</summary>
public sealed class ManualClock : IClock
{
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta), "不能倒退时间");
        _now += delta;
    }
}
