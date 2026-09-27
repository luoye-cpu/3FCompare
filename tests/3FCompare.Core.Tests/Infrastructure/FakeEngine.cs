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

/// <summary><see cref="IPlayerSession"/> 的可脚本化替身。</summary>
public sealed class FakeSession : IPlayerSession
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
    public PlayerState State { get; set; } = PlayerState.Idle;
    public EngineMediaInfo? MediaInfo { get; set; }
    public bool Disposed => _disposed;

    // ---- 行为开关 ----
    /// <summary>Seek 是否钳到 [0, Duration100ns]。默认 true（与真实内核一致）。</summary>
    public bool ClampSeek { get; set; } = true;

    /// <summary>非空时 <see cref="OpenAsync"/> 抛该异常。</summary>
    public Exception? ThrowOnOpen { get; set; }

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

    public void Play() { ThrowIfDisposed(); PlayCount++; if (State is PlayerState.Ready or PlayerState.Paused or PlayerState.Ended) State = PlayerState.Playing; }
    public void Pause() { ThrowIfDisposed(); PauseCount++; if (State == PlayerState.Playing) State = PlayerState.Paused; }
    public void Stop() { ThrowIfDisposed(); StopCount++; State = PlayerState.Paused; Position100ns = 0; }

    public void Seek(long position100ns)
    {
        ThrowIfDisposed();
        SeekCount++;
        var target = position100ns;
        if (ClampSeek) target = Math.Clamp(target, 0, Duration100ns);
        SeekTargets.Add(target);
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
    public bool? LastTearing { get; private set; }

    public void SetViewTransform(float zoom, float panX, float panY)
    {
        ThrowIfDisposed();
        ViewTransforms.Add((zoom, panX, panY));
    }

    public EngineSnapshot ReadSnapshot() => new()
    {
        Position100ns = Position100ns,
        Duration100ns = Duration100ns,
        FrameIndex = FrameIndex,
        FrameRate = MediaInfo?.FrameRate ?? 0,
        State = State,
        PresentedVideoFrames = PresentedVideoFrames,
        SwapChainPresents = SwapChainPresents,
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
