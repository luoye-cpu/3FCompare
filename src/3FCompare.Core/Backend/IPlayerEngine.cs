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
    /// <param name="mode">目标色彩模式。</param>
    /// <param name="contentIsHdr">内容是否为 HDR。传 null 由会话按自身媒体信息判定；
    /// <b>多路对比时必须由调用方传入全会话统一的值</b>，否则同一素材的两个编码版本
    /// 只要有一路 HDR 元数据缺失，两路就会走不同的色调映射曲线，对比失去意义。</param>
    void SetColorMode(ColorMode mode, bool? contentIsHdr = null);

    /// <summary>设置呈现节奏（内核扩展：VRR/G-SYNC 低延迟路径）。
    /// tearing=true 选择 Present(0, ALLOW_TEARING)（显示器按自身节奏扫描输出）；
    /// false 保持 VSync 锁定。显示器/驱动不支持时静默保持 VSync（返回值仅提示）。</summary>
    bool SetPresentConfig(bool tearing);

    /// <summary>RTX 视频超分辨率（内核扩展 <c>FFF3FP_SetVideoSuperResolution</c>）。
    /// enable=true 请求 Auto：内核<b>每帧</b>重新判定七道门（NVIDIA 适配器、源格式、
    /// 是否真在放大、源高度 360..1440、失败闩…），本调用成功只代表"请求已登记"。
    /// <para>返回 false 表示当前盘上的 <c>FFF.Native.dll</c> 没有这个入口（旧内核，
    /// <c>NativeRuntime</c> 不覆盖已存在的同名文件）——这是告警级状态，不是会话失败，
    /// 也不该弹窗；真实内核缺导出是常态而非异常。</para></summary>
    bool SetVideoSuperResolution(bool enable);

    /// <summary>读取 VSR 实际状态与内核给出的原因码。<para>返回 false 同样意味着
    /// 内核不提供该入口，此时 <paramref name="status"/> 为 default。</para>
    /// <para><see cref="VideoSuperResolutionStatus.Active"/> 只表示"驱动没有拒绝这次请求"：
    /// 实测该路径的 HRESULT 不携带任何能力信息（对伪造的 GUID 也返回 S_OK），
    /// 所以它<b>不能</b>当作"画面被增强"的证据；画质档位（1-4/Auto）归 NVIDIA App
    /// 或控制面板所有，本 API 既不能设也不能读。</para></summary>
    bool ReadVideoSuperResolutionStatus(out VideoSuperResolutionStatus status);

    // ⚠ 这里曾有一段"媒体率呈现节奏（pacing）"的 /// 注释，但内核**没有** SetPacingConfig
    // （已导出 82 个 API 中不存在）。悬空 /// 会被挂到下一个成员上 ⇒ 它实际成了
    // SetViewTransform 的文档，是错的。已删除。要实现需先在内核侧加导出。

    /// <summary>设置视口变换（缩放 + 平移）。zoom=1.0 表示适应窗口；
    /// panX/panY 为相对未缩放视频框的归一化偏移 [-1,1]。</summary>
    void SetViewTransform(float zoom, float panX, float panY);

    /// <summary>3FCompare 内核扩展：把适配盒封顶到源原生尺寸。开启后 <see cref="SetViewTransform"/>
    /// 的 zoom 就是 屏幕像素/视频像素 之比——zoom=1 为逐像素 1:1（窗口比源大时留黑边），
    /// 而不是"放大到窗口"。内核不支持时抛 <see cref="EngineException"/>。</summary>
    void SetFitLimitToNative(bool enable);

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

    /// <summary>请求重绘当前帧（3FCompare K5）：子 HWND resize 后调用，
    /// 让 presenter 线程感知尺寸变化并执行换成链 resize + 一帧重绘。</summary>
    void Redraw();

    /// <summary>读取渲染目标诊断信息（swap/client/dest 尺寸）。返回 false 表示不可用。</summary>
    bool ReadRenderTargetInfo(out RenderTargetInfo info);

    /// <summary>引擎事件（原生工作线程回调；消费者须自行调度到 UI 线程）。</summary>
    event EventHandler<EngineEvent>? EngineEvent;

    /// <summary>设置通用动态元数据注入表（kind 化通道：HDR10+ / HDR Vivid 等）。
    /// 成功会整表替换（含旧版入口装载的表）；任一包校验失败（形状错/未知 kind）整批拒绝且旧表续任。
    /// 返回 false 表示内核不支持该导出（旧版本）或整批被拒，不应抛异常。</summary>
    bool SetDynamicMetadata(IReadOnlyList<DynamicMetadataPacket> packets);

    /// <summary>清除动态元数据注入表。
    /// kind=0 (None) 清除全部 kind；其他值仅清除该 kind 的条目并重新评估剩余表。
    /// 返回 false 表示内核不支持该导出（旧版本）。</summary>
    bool ClearDynamicMetadata(DynamicMetadataKind kind);

    /// <summary>设置 HDR10+ 动态元数据注入表（旧版专用入口，与通用通道 kind=Hdr10Plus 共用内核实现）。
    /// 传空列表即清除。返回 false 表示内核不支持该导出（旧版本）。</summary>
    bool SetHdrDynamicMetadata(IReadOnlyList<HdrDynamicMetadataEntry> entries);

    /// <summary>清除 HDR10+ 动态元数据注入表（旧版专用入口）。
    /// 返回 false 表示内核不支持该导出（旧版本）。</summary>
    bool ClearHdrDynamicMetadata();

    /// <summary>获取当前 HDR 动态元数据来源（旧版专用入口）。
    /// 返回 null 表示内核不支持该导出（旧版本）。</summary>
    DynamicMetadataSource? GetHdrMetadataSource();
}

/// <summary>动态元数据种类（对应内核 FFF3FPDynamicMetadataKind，FFF.Player.Api.h:906）。</summary>
public enum DynamicMetadataKind : uint
{
    /// <summary>仅对 Clear 有意义：清除全部 kind。</summary>
    None = 0,
    /// <summary>ST 2094-40（HDR10+）：载荷为 <see cref="HdrDynamicMetadataEntry"/>。</summary>
    Hdr10Plus = 1,
    /// <summary>ST 2094-30 / CUVA 005（HDR Vivid）：载荷为 <see cref="HdrVividMetadataPayload"/>。</summary>
    HdrVivid = 2,
    /// <summary>调用方 supplied 的色彩变换：载荷为 <see cref="ColorTransformTable"/>
    /// （一张 33³ 采样立体 LUT + 一个线性光 3x3 矩阵，内核侧结构
    /// <c>FFF3FPColorTransformPayload</c>，431296 字节）。</summary>
    /// <remarks>值必须与内核同水：它是内核 <c>HdrDynamicMetadata.h</c> 的
    /// <c>HdrMetadataKind::ExternalColorTransform</c> 与 <c>FFF.Player.Api.h</c> 的
    /// <c>FFF3FPDynamicMetadataKind</c> 里的同一个 3，两侧任何一侧改动都要由
    /// <c>ColorTransformLayoutTests</c> 判红。内核把这一 kind 当独立分支处理：它整体替换
    /// 帧的动态元数据状态、走 <c>BuildColorTransformSettings</c> 的另一条渲染路径，
    /// 与 ST 2094 那张窗口表无关。</remarks>
    ExternalColorTransform = 3,
}

/// <summary>HDR 动态元数据来源（对应内核 FFF3FPHdrMetadataSource，FFF.Player.Api.h:715）。</summary>
public enum DynamicMetadataSource : uint
{
    Bitstream = 0,
    Injected = 1,
}

/// <summary>HDR10+ 动态元数据处理窗口（对应 FFF3FPHdrMetadataWindow）。
/// 坐标归一化到画面（0,0=左上，1,1=右下）；maxScl 为线性化 RGB 最大值 0..1（nits = value*10000）。</summary>
public sealed record HdrDynamicMetadataWindow
{
    public required float UpperLeftX { get; init; }
    public required float UpperLeftY { get; init; }
    public required float LowerRightX { get; init; }
    public required float LowerRightY { get; init; }
    public required float MaxSclR { get; init; }
    public required float MaxSclG { get; init; }
    public required float MaxSclB { get; init; }
    public required float AverageMaxRgb { get; init; }
    public required float FractionBrightPixels { get; init; }
    /// <summary>toneMappingFlag=0 时该窗口只约束峰值、不提供曲线。</summary>
    public required uint ToneMappingFlag { get; init; }
    /// <summary>kneePointX=0 表示无线性段。</summary>
    public required float KneePointX { get; init; }
    public required float KneePointY { get; init; }
    /// <summary>曲线锚点，升序 0..1，最多 15 个。</summary>
    public required IReadOnlyList<float> CurveAnchors { get; init; }
}

/// <summary>HDR10+ 动态元数据条目（对应 FFF3FPHdrDynamicMetadataEntry）。
/// 最多 3 个窗口（ST 2094-40 范围）；numWindows==1 时窗口必须覆盖全幅。</summary>
public sealed record HdrDynamicMetadataEntry
{
    /// <summary>该条目生效的首帧（含），匹配键为 snapshot.frameIndex。</summary>
    public required long FrameIndex { get; init; }
    public required IReadOnlyList<HdrDynamicMetadataWindow> Windows { get; init; }
    /// <summary>目标显示峰值 cd/m²（0 = 由窗口峰值推导）。</summary>
    public required float TargetedDisplayNits { get; init; }
}

/// <summary>HDR Vivid 动态元数据载荷（峰值语义，对应 FFF3FPHdrVividMetadataPayload）。
/// 按 CUVA 005.1 非公开曲线的前提，内核对 Vivid 用峰值语义（无曲线锚点解释）。</summary>
public sealed record HdrVividMetadataPayload
{
    /// <summary>按定义全幅的窗口（Vivid 逐窗读数不带几何）；max 值 0..1，内核按 max(maxRGB)*10000 推算峰值。</summary>
    public required IReadOnlyList<(float MaxR, float MaxG, float MaxB, float AverageMaxRgb)> Windows { get; init; }
    /// <summary>目标显示峰值 cd/m²（0 = 由窗口峰值推导）。</summary>
    public required float TargetedDisplayNits { get; init; }
}

/// <summary>通用动态元数据包（对应 FFF3FPDynamicMetadataPacket）。
/// <see cref="Payload"/> 按 <see cref="Kind"/> 解释：
/// Hdr10Plus = <see cref="HdrDynamicMetadataEntry"/>，HdrVivid = <see cref="HdrVividMetadataPayload"/>，
/// ExternalColorTransform = <see cref="ColorTransformTable"/>。</summary>
public sealed record DynamicMetadataPacket
{
    public required DynamicMetadataKind Kind { get; init; }
    public required long FrameIndex { get; init; }
    public required object Payload { get; init; }
}

/// <summary>渲染目标尺寸诊断（3FCompare K4）。
/// <para><paramref name="DestX"/>/<paramref name="DestY"/> 是<b>有符号</b>的绘制盒原点：
/// 放大后平移会把盒子的左/上沿推到后台缓冲之外（露出源画面的右/下半段），负值合法。
/// 内核 <c>FFF3FPRenderTargetInfo</c> v1 曾把它声明成无符号并配 <c>max(0,·)</c>，
/// 结果是"往右下平移"整半段量程被裁掉（实测 pan 扫全程、上报原点恒 (0,0)）。</para></summary>
public readonly record struct RenderTargetInfo(
    uint SwapWidth, uint SwapHeight,
    uint ClientWidth, uint ClientHeight,
    int DestX, int DestY, uint DestWidth, uint DestHeight,
    uint OutputBitDepth, bool Hdr);

/// <summary>HDR 呈现诊断（对应内核 <c>FFF3FPHdrSignalDiagnostics</c>）。
/// <para>存在的理由：<c>hdrProcessingPath</c> 在两条臂上都报 <c>*Dynamic</c>，而
/// <c>MapToHdr</c> 那条臂按设计（3fp/docs/58 §4.5 / T3）不套曲线、改为逐帧更新
/// <c>DXGI_HDR_METADATA_HDR10</c>。此前"到底哪一个机制在跑"只能读着色器闸门；
/// 这几个计数把它变成可读数，也用于回归——曲线 applied 恒 0 而 dyn=1 就是 RE-03 那一类故障。</para>
/// <para><paramref name="MaxMasteringLuminance"/>/<paramref name="MinMasteringLuminance"/>
/// 是 DXGI 原值，单位 1/10000 cd/m²；两个光级单位 cd/m²。</para></summary>
public readonly record struct HdrSignalDiagnostics(
    ulong DynamicCurveFramesApplied, ulong DynamicCurveFramesRefused,
    ulong SetHdrMetaDataCalls, ulong SetHdrMetaDataSuppressed,
    uint MaxMasteringLuminance, uint MinMasteringLuminance,
    uint MaxContentLightLevel, uint MaxFrameAverageLightLevel,
    bool SwapChainHdrEnabled);

/// <summary>内核 <c>FFF3FPVideoSuperResolutionReason</c>：上一帧为什么用/没用 VSR。
/// 逐值对应内核 3501..3525 的判定顺序，0 只在 Active=1 时出现（内核会把 reason 覆写成 None）。</summary>
public enum VideoSuperResolutionReason : uint
{
    /// <summary>已使用（仅在 Active=true 时出现）。</summary>
    None = 0,
    /// <summary>宿主没请求 Auto（默认状态）。</summary>
    NotRequested = 1,
    /// <summary>渲染设备背后的适配器不是 NVIDIA；混合显卡机型上取决于窗口落在哪块卡。</summary>
    NotNvidiaAdapter = 2,
    /// <summary>目标不大于源——VSR 只做放大。</summary>
    NotUpscaling = 3,
    /// <summary>源高度不在 360..1440 内（4K 源就在这一档被拒）。</summary>
    SourceResolutionOutOfRange = 4,
    /// <summary>源纹理不满足视频处理器输入条件（软解上传路径没有 D3D11_BIND_DECODER 等）。</summary>
    UnsupportedSourceFormat = 5,
    /// <summary>驱动拒绝过，已闩住；只有重新发一次 Auto 才会再探。</summary>
    DriverRejected = 6,
}

/// <summary>RTX VSR 状态（对应内核 <c>FFF3FPVideoSuperResolutionStatus</c>）。
/// <para>用途是把"为什么没生效"变成可读数，而不是"是否生效"的证据——见
/// <see cref="IPlayerEngine.ReadVideoSuperResolutionStatus"/> 的限定。</para></summary>
public readonly record struct VideoSuperResolutionStatus(
    bool Requested, bool NvidiaAdapter, bool Active, bool DriverRejected,
    VideoSuperResolutionReason Reason,
    uint SourceWidth, uint SourceHeight, uint TargetWidth, uint TargetHeight);

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

    /// <summary>SDR 源的 scRGB 呈现策略（内核 v16）。
    /// 0 = 从不：SDR 源始终走 8/10bit sRGB 交换链（历史行为）；
    /// 1 = 自动：显示器开启 Advanced Color 时，位深 &gt; 8 或广色域 primaries 的 SDR 源
    /// 改走 16bit scRGB 链，避免在 gamma R10G10B10A2 链上被 DWM 二次处理、
    /// 以及广色域被着色器折叠回 Rec.709。
    /// 显示器未开启 Advanced Color（或交换链拒绝 scRGB）时仍回退 SDR 链。默认 0。</summary>
    public int SdrScRgbMode { get; init; }

    /// <summary>VRR 低延迟呈现（内核扩展）： tearing=true 选择 Present(0, ALLOW_TEARING)，
    /// 让 G-SYNC/FreeSync 显示器按自身节奏扫描输出；false 保持 VSync 锁定（无撕裂）。
    /// 显示器链不支持时静默保持 VSync。盯帧对比场景建议保持 false。</summary>
    public bool TearingPresent { get; init; }

    /// <summary>RTX 视频超分辨率（内核扩展 <c>FFF3FP_SetVideoSuperResolution</c>）。
    /// true = 请求 Auto：内核每帧自行判定（必须是真放大、源高度 360..1440、渲染适配器为
    /// NVIDIA、源纹理可作视频处理器输入等），任一不满足只是这次不用，不影响会话。
    /// <para>默认 false。三个诚实前提：① 出货内核（third_party 那棵）目前没有这个导出，
    /// 请求会软失败并记一条 warn；② 画质档位（1-4/Auto）与总开关归 NVIDIA App/控制面板，
    /// 本应用设不了也读不到；③ 内核的 Active 位只表示"驱动没拒绝"，<b>不表示画面被增强</b>
    /// ——实测该路径的 HRESULT 不携带能力信息。所以这个开关的验收判据只能是
    /// <see cref="IPlayerSession.ReadVideoSuperResolutionStatus"/> 的 Reason 分布，
    /// 不能是 Active。</para></summary>
    public bool RtxVideoSuperResolution { get; init; }

    /// <summary>媒体率呈现节奏（内核扩展 A9）：pacing=true 抑制叠加层固定周期的重翻转，
    /// 使呈现节奏跟随源视频帧率。需 TearingPresent=true 发挥完整效果。</summary>
}

/// <summary>解码适配器信息（用于多显卡指定，F26/A11）。</summary>
public sealed record AdapterInfo
{
    public int Index { get; init; }
    public required string Description { get; init; }
    public ulong DedicatedMemoryBytes { get; init; }

    /// <summary>PCI 厂商 ID（0x10DE=NVIDIA、0x8086=Intel、0x1002=AMD、0x1414=Microsoft 软件渲染器）。
    /// 与内核诊断日志比对用。</summary>
    public uint VendorId { get; init; }
    /// <summary>PCI 设备 ID。与 <see cref="VendorId"/> 一起可唯一定位一张卡的型号。</summary>
    public uint DeviceId { get; init; }
    /// <summary>DXGI 适配器 LUID。
    /// ⚠ **同型号多卡的唯一区分依据**：<c>DXGI_ADAPTER_DESC.Description</c> 对同型号两张卡
    /// 返回**完全相同**的字符串（实测双 RTX 4060 Laptop），此时只有 LUID 能分辨它们。</summary>
    public long AdapterLuid { get; init; }
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
    /// <summary>所有 DXGI Present 调用<b>内部</b>累计的阻塞时长（100ns 单位）。
    /// 对应内核 <c>FFF3FPSnapshot.presentWait100ns</c>（FFF.Player.Api.h:174），
    /// 由 <c>PlayerVideoRenderer::PresentCurrentFrame</c>（VideoRenderer.cpp:5342）在
    /// <c>chain-&gt;Present(...)</c> 前后取 steady_clock 差值累加，
    /// 并在 <c>PlayerSession::GetSnapshot</c>（PlayerSession.cpp:1733）里实时刷新到快照。
    /// <para>这是 VRR/撕裂是否<b>真的生效</b>的唯一可观测证据：与
    /// <see cref="SwapChainPresents"/> 相除即得"单次 Present 平均等待"——
    /// VSync 锁定时应约等于一个刷新周期（60Hz ≈ 16.7ms），撕裂/VRR 生效时趋近 0。
    /// <see cref="IPlayerSession.SetPresentConfig"/> 的返回值只说明交换链<b>允许</b>撕裂，
    /// 不代表 Present 实际没等，故不能用作判据。</para></summary>
    public long PresentWait100ns { get; init; }
    /// <summary>时间轴代数：仅在真实 demuxer seek 成功后递增。
    /// 用于判定 Seek/StepFrame 是否真正生效（3FCompare 优化项⑤）。</summary>
    public ulong TimelineGeneration { get; init; }
    // ---- IAMF 空间音频（内核 FFF3FPSnapshot 中部 trio + 尾部 iamfContentChannels/iamfBinaural）----
    /// <summary>IAMF 沉浸音频是否接管（AOM 参考解码器渲染该轨时为 true）。</summary>
    public bool IamfActive { get; init; }
    /// <summary>IAMF <b>渲染目标</b>输出声道数（如 7.1.4 = 12）。
    /// 内容比目标小时 libiamf 用数字静音补齐而不是上混，故它回答"我们在往几个喇叭渲染"。</summary>
    public uint IamfChannels { get; init; }
    /// <summary>IAMF 声音系统标识（OAR sound system；int32）。</summary>
    public int IamfSoundSystem { get; init; }
    /// <summary>IAMF <b>内容声明</b>声道数（≠ <see cref="IamfChannels"/>；0 = libiamf
    /// 未上报 per-element 细节）。它回答"文件里实际有几声道"——只显示前者会把
    /// 7.1 轨误标成 7.1.4（其中四个喇叭全程静音）。</summary>
    public uint IamfContentChannels { get; init; }
    /// <summary>IAMF 渲染模式（内核 FFF.Player.Api.h:258，API v19 尾追加）：
    /// 1=libiamf 双耳(HRTF)渲染直接出立体声，0=扬声器布局(7.1.4)由 swr 折叠。
    /// 双耳保留耳机上的空间<b>成像</b>，矩阵折叠保留能量但丢掉方位——两者不是
    /// 音量差别，故 UI 必须显示用户实际听到的那一种。</summary>
    public uint IamfBinaural { get; init; }
    /// <summary>API v20 尾追加（内核 FFF.Player.Api.h:268，结构体最尾部）：非 IAMF 多声道床
    /// （AC-3/E-AC-3/Atmos 床、TrueHD、DTS/DTS-HD/DTS:X 床、普通多声道 PCM/FLAC/WAV）
    /// 在立体声设备上的双耳渲染。1=OBR HRTF 双耳渲染（空间定位保留），0=swr 矩阵折叠
    /// （保留能量、丢失方位）。IAMF 轨由各自的 <see cref="IamfBinaural"/> 双耳路径上报，
    /// 不走此字段。</summary>
    public uint AudioBinaural { get; init; }
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
