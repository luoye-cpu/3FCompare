namespace _3FCompare.Core.Backend;

/// <summary>播放器统一抽象（未来可替换实现；当前唯一实现为 3FP P/Invoke 层）。</summary>
public interface IPlayerEngine
{
    /// <summary>枚举系统可用的解码适配器（多显卡场景，F26）。</summary>
    IReadOnlyList<AdapterInfo> EnumerateAdapters();

    /// <summary>创建一个新的播放会话（未打开媒体）。</summary>
    IPlayerSession CreateSession(EngineSessionOptions options);
}

/// <summary>单个视频会话抽象（对应 3FP PlayerSession 的一个原生句柄）。</summary>
public interface IPlayerSession : IDisposable
{
    /// <summary>异步打开本地媒体；失败抛 <see cref="EngineException"/>。</summary>
    Task OpenAsync(string localPath, CancellationToken cancellationToken = default);

    void Play();
    void Pause();
    void Stop();

    /// <summary>按时间 Seek（100ns 精度）。</summary>
    void Seek(long position100ns);

    /// <summary>按帧索引跳转。</summary>
    void SeekFrame(long frameIndex);

    /// <summary>帧级步进（±方向）。</summary>
    void StepFrame(int direction);

    /// <summary>选择音轨（流索引；-1 表示无音轨）。</summary>
    void SelectAudioStream(int streamIndex);

    /// <summary>选择视频流（流索引）。</summary>
    void SelectVideoStream(int streamIndex);

    /// <summary>音量设置（0..1；muted=1 静音）。</summary>
    void SetVolume(float volume, bool muted);

    /// <summary>设置色彩模式（MapToSdr/RawHdrAsSdr/MapToHdr）。运行时切换 HDR/SDR。</summary>
    void SetColorMode(ColorMode mode);

    /// <summary>设置呈现节奏（内核扩展：VRR/G-SYNC 低延迟路径）。
    /// tearing=true 选择 Present(0, ALLOW_TEARING)（显示器按自身节奏扫描输出）；
    /// false 保持 VSync 锁定。显示器/驱动不支持时静默保持 VSync（返回值仅提示）。</summary>
    bool SetPresentConfig(bool tearing);

    /// <summary>媒体率呈现节奏（内核扩展 A9）：
    /// pacing=true 抑制叠加层固定周期的重翻转，使呈现节奏跟随源视频帧率而非叠加层帧率。
    /// 对 VRR 显示器消除 3:2 抖动；对 VSync 锁定显示器无害。需配合 SetPresentConfig(true) 发挥完整效果。</summary>
    bool SetPacingConfig(bool pacing);

    /// <summary>设置视口变换（缩放 + 平移）。zoom=1.0 表示适应窗口；
    /// panX/panY 为相对未缩放视频框的归一化偏移 [-1,1]。</summary>
    void SetViewTransform(float zoom, float panX, float panY);

    /// <summary>读取当前快照（高频安全）。</summary>
    EngineSnapshot ReadSnapshot();

    /// <summary>读取媒体信息（打开成功后有效）。</summary>
    EngineMediaInfo? ReadMediaInfo();

    /// <summary>读取某像素（颜色管理前原生缓冲）。</summary>
    bool TryReadPixel(int x, int y, out PixelSample sample);

    /// <summary>批量读取像素区域（3FCompare patch 0004）：单次 GPU staging 拷贝，
    /// buffer 长度须 ≥ width*height*4（RGBA 归一化浮点，行优先）。
    /// 返回 false 表示引擎不支持（演示模式由实现填充默认值则返回 true）。</summary>
    bool TryReadPixelRegion(int x, int y, int width, int height,
        float[] buffer, out uint outputBitDepth);

    /// <summary>引擎事件（原生工作线程回调；消费者须自行调度到 UI 线程）。</summary>
    event EventHandler<EngineEvent>? EngineEvent;
}

/// <summary>会话创建选项。</summary>
public sealed record EngineSessionOptions
{
    /// <summary>输出窗口句柄（3FP 的 outputWindow）。</summary>
    public nint OutputWindow { get; init; }

    /// <summary>解码模式：false=CPU，true=GPU(硬件)。</summary>
    public bool HardwareDecode { get; init; } = true;

    /// <summary>指定的解码 GPU 序号（-1 表示系统默认）。</summary>
    public int PreferredAdapterIndex { get; init; } = -1;

    /// <summary>色彩模式（映射 SDR / 原始 HDR / PQ HDR）。</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.MapToSdr;

    /// <summary>强制尝试 scRGB HDR 输出（内核 v12）。
    /// 绕过显示器 HDR 能力门控（针对亮度字段缺失的电视/虚拟显示器）；
    /// 不影响"SDR 源强制回 SDR"的内容门控。默认 false = 按显示器探测结果自动降级。</summary>
    public bool ForceHdrOutput { get; init; }

    /// <summary>VRR 低延迟呈现（内核扩展）： tearing=true 选择 Present(0, ALLOW_TEARING)，
    /// 让 G-SYNC/FreeSync 显示器按自身节奏扫描输出；false 保持 VSync 锁定（无撕裂）。
    /// 显示器链不支持时静默保持 VSync。盯帧对比场景建议保持 false。</summary>
    public bool TearingPresent { get; init; }

    /// <summary>媒体率呈现节奏（内核扩展 A9）：pacing=true 抑制叠加层固定周期的重翻转，
    /// 使呈现节奏跟随源视频帧率。需 TearingPresent=true 发挥完整效果。</summary>
    public bool PacingEnabled { get; init; }
}

/// <summary>解码适配器信息（用于多显卡指定，F26/A11）。</summary>
public sealed record AdapterInfo
{
    public int Index { get; init; }
    public required string Description { get; init; }
    public ulong DedicatedMemoryBytes { get; init; }
}

public enum ColorMode
{
    MapToSdr = 0,
    RawHdrAsSdr = 1,
    MapToHdr = 2,
}

/// <summary>播放器状态（与 3FP FFF3FPState 对齐）。</summary>
public enum PlayerState
{
    Idle = 0,
    Opening = 1,
    Ready = 2,
    Playing = 3,
    Paused = 4,
    Ended = 5,
    Failed = 6,
    Closed = 7,
}

/// <summary>快照（对齐 3FP FFF3FPSnapshot 的关键字段）。</summary>
public sealed record EngineSnapshot
{
    public required long Position100ns { get; init; }
    public required long Duration100ns { get; init; }
    public required long FrameIndex { get; init; }
    public long RawFramePts { get; init; }
    public int FrameTimeBaseNum { get; init; }
    public int FrameTimeBaseDen { get; init; }
    /// <summary>媒体帧率（fps）。来自媒体信息 nominalFrameRate；0 = 未知。</summary>
    public double FrameRate { get; init; }
    public int Decoder { get; init; }
    public uint ActualColorMode { get; init; }
    public PlayerState State { get; init; } = PlayerState.Ready;
    /// <summary>已呈现视频帧计数（诊断渲染管线是否停滞）。</summary>
    public long PresentedVideoFrames { get; init; }
    /// <summary>SwapChain Present 调用计数（诊断 D3D11 呈现是否正常）。</summary>
    public long SwapChainPresents { get; init; }
    /// <summary>时间轴代数：仅在真实 demuxer seek 成功后递增。
    /// 用于判定 Seek/StepFrame 是否真正生效（3FCompare 优化项⑤）。</summary>
    public ulong TimelineGeneration { get; init; }
}

/// <summary>媒体信息（从 3FP GetMediaInfo JSON 反序列化，F3 媒体信息面板）。</summary>
public sealed record EngineMediaInfo
{
    public required string Path { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public double FrameRate { get; init; }
    public required string Codec { get; init; }
    public bool IsHdr { get; init; }

    // ---- 扩展字段（F3 媒体信息面板） ----
    public string? Format { get; init; }          // mov,mp4,m4a...
    public long Duration100ns { get; init; }
    public long BitRate { get; init; }
    public long FileSize { get; init; }
    public string? ContainerMetadata { get; init; } // 容器 metadata 摘要
    public bool IsLossless { get; init; }
    public int BitDepth { get; init; } = 8;       // 解码位深
    public string? PixelFormat { get; init; }     // yuv420p 等
    public string? ChromaSubsampling { get; init; } // 4:2:0 等
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? HdrFormat { get; init; }       // SDR / HDR10 / HLG...
    public bool Interlaced { get; init; }
    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public int AudioSampleRate { get; init; }
    public long FrameCount { get; init; }
}

/// <summary>单像素采样（颜色管理前 BGRA8/RGB10A2 码值）。</summary>
public readonly record struct PixelSample(float R, float G, float B, float A, uint BitDepth);

/// <summary>引擎事件（3FP FFF3FPEvent 回调，Fff3FpSession.EventReceived）。</summary>
public enum EngineEventType
{
    StateChanged = 1,
    OpenCompleted = 2,
    OperationCompleted = 3,
    PlaybackEnded = 4,
    Error = 5,
    ColorModeChanged = 6,
    DeviceChanged = 7,
}

/// <summary>引擎事件载荷（detailJsonUtf8 原样保留，由消费方解析）。</summary>
public readonly record struct EngineEvent(EngineEventType Type, string DetailJson);

/// <summary>引擎异常（对应 3FP FFFResult 与错误消息）。</summary>
public sealed class EngineException : Exception
{
    public int Result { get; }

    public EngineException(int result, string message) : base(message) => Result = result;
}