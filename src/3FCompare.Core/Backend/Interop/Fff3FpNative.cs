using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend.Interop;

/// <summary>事件回调（FFF3FPEventCallback：__cdecl，来自原生工作线程）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Fff3FpEventCallback(nint context, uint eventType, nint detailJsonUtf8);

/// <summary>3FP 原生播放器结果（对齐 FFFResult，FFF.Player.Api.h）。</summary>
internal enum FffResult : int
{
    Success = 0,
    InvalidArgument = -1,
    InvalidState = -2,
    BufferTooSmall = -3,
    NativeFailure = -4,
    FfmpegFailure = -5,
    DeviceFailure = -6,
    NotSupported = -7,
    // 有界存储里没有可返回的匹配项（FFF3FP_CopyFrameSideDataAfter 在环里
    // 找不到该类型/该帧号的边数据）。内核 2026-10-13 追加，属新增枚举值：
    // 不认识它的宿主仍把非零当失败处理，ABI 不变。
    NotFound = -8,
}

/// <summary>解码模式（FFF3FPDecodeMode）。</summary>
internal enum FffDecodeMode : uint
{
    Unspecified = 0,
    Cpu = 1,
    Gpu = 2,
}

/// <summary>色彩模式（FFF3FPColorMode）。</summary>
internal enum FffColorMode : uint
{
    MapToSdr = 0,
    RawHdrAsSdr = 1,
    MapToHdr = 2,
}

/// <summary>播放器状态（FFF3FPState）。</summary>
internal enum FffPlayerState : uint
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

/// <summary>FFF3FPConfiguration 序列表，逐字段对照内核头 FFF.Native/3FP/Api/FFF.Player.Api.h。
/// 内核对 version 严格相等、对 size 按下界拒收（PlayerApi.cpp:96），所以这里的字段清单就是
/// 会话能否建立的先决条件；tools/check_kernel_config_abi.py 与 SnapshotLayoutTests.cs 看守它。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpConfiguration
{
    public uint Size;
    public uint Version;
    public nint OutputWindow;
    public uint DecodeMode;
    public uint ColorMode;
    public float SdrPeakNits;
    public float HdrPeakNits;
    public float SdrPaperWhiteNits;
    public nint AudioEndpointIdUtf8;
    public nint EventCallback;
    public nint EventCallbackContext;
    public uint VideoScalingQuality; // FFF3FPVideoScalingQuality: 0=Balanced, 1=HighQuality
    public uint ForceHdrOutput;      // 新增 v12: 非 0 = 强制尝试 scRGB HDR 链（绕过显示器能力门控）
    /// <summary>3FCompare 扩展（A11，api 15）：偏好的 DXGI 适配器索引。
    /// -1 = 沿用内核默认策略（窗口所在显示器所属的适配器）；
    /// ≥0 作为 <c>IDXGIFactory1::EnumAdapters1</c> 的下标。
    /// ⚠ 索引空间必须与内核一致 ⇒ 托管侧枚举必须走 DXGI（<see cref="Display.GpuEnumeration"/>），
    /// 不能用 Win32 <c>EnumDisplayDevices</c>（那是 DISPLAY 设备索引，两者不对应）。
    /// 越界或枚举失败时内核静默回落到默认策略。</summary>
    public int PreferredAdapterIndex;
    /// <summary>v16：SDR 源的 scRGB 呈现策略。0=从不（历史行为），
    /// 1=自动（显示器开启 Advanced Color 时，位深 &gt;8 或广色域的 SDR 源走 16bit scRGB 链）。</summary>
    public uint SdrScRgbMode;
    // 上游 578f80b 追加的自适应档（结构体 80→112 B）。内核 FFF3FP_Create 按
    // `size < sizeof(FFF3FPConfiguration)` 拒收，所以这几个字段必须声明，否则每次建会话都失败。
    // 两个开关留 0 = 关闭（与追加前行为一致），阈值取内核头自己的默认值。
    public uint AdaptiveDownscaleBeforeUpload;
    public uint AdaptiveDownscaleDropPercent;
    public uint AdaptiveDecoderThreads;
    public uint MinSoftwareDecoderThreads;
    public uint MaxSoftwareDecoderThreads;
    public uint DecoderGrowDropPercent;
    public uint DecoderShrinkDropPercent;
}

/// <summary>FFF3FPSnapshot 完整布局，逐字段对照内核头 FFF.Native/3FP/Api/FFF.Player.Api.h。
/// 顺序与宽度由 tests/3FCompare.Core.Tests/SnapshotLayoutTests.cs 现场解析内核头来对撞，
/// 所以这里不写尺寸常数——内核 GetSnapshot 按 <c>size &lt; sizeof(FFF3FPSnapshot) || version !=
/// PlayerApiVersion</c> 直接拒收（PlayerSession.cpp:2469），少一个字段或错一位就让整个快照读数失效。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpSnapshot
{
    public uint Size;
    public uint Version;            // == 内核 PlayerApiVersion，见 Fff3FpEngine.SnapshotVersion
    public uint State;
    public uint Decoder;
    public uint RequestedColorMode;
    public uint ActualColorMode;
    public long Position100ns;
    public long Duration100ns;
    public long FrameIndex;
    public long FramePts;
    public int FrameTimeBaseNumerator;
    public int FrameTimeBaseDenominator;
    public int SelectedVideoStream;
    public int SelectedAudioStream;
    public uint VideoWidth;
    public uint VideoHeight;
    public uint IsHdrSource;
    public uint IsExternalAudio;
    public long ExternalAudioOffset100ns;
    public ulong DecodedVideoFrames;
    public ulong PresentedVideoFrames;
    public ulong DroppedVideoFrames;
    public uint QueuedVideoFrames;
    public uint SourcePeakNits;
    public ulong DecodedAudioFrames;
    public long AudioPosition100ns;
    public long BufferedAudio100ns;
    public ulong AudioUnderruns;
    public ulong AudioTimestampJitterFrames;
    public ulong AudioDiscontinuities;
    public ulong AudioInsertedSilenceFrames;
    public ulong AudioDroppedOverlapFrames;
    public ulong CoalescedVideoFrames;
    public ulong AudioRejectedFrames;
    public ulong SwapChainPresents;
    public ulong PresentWait100ns;
    public ulong DeviceLockWait100ns;
    public ulong HardwareTransfer100ns;
    public ulong SoftwareConvert100ns;
    public ulong VideoBitRate;
    public ulong AudioBitRate;
    public uint VideoOutputBitDepth;
    public uint VideoScalingMode;
    public ulong TimelineGeneration;
    public uint HdrFormat;
    public uint CompatibleHdrFormats;
    public uint HdrProcessingPath;
    // ⛔ DEPRECATED ABI 保留槽（5 × uint32 = 20 B）——**禁止删除，禁止改名，禁止改序**。
    // 内核把这几个槽标为 "DEPRECATED ABI (slots retained, always zero)"：中性化的正确动作是
    // 「改名 + 恒零」，不是删字段。2026-10-14 工作区整块删过它们 ⇒ 其后每个字段偏移整体前移 20 B，
    // 并被内核的 size 闸门直接拒收。守护：SnapshotLayoutTests.cs 逐字段对撞，删一行即判红。
    public uint HdrDpProfile;
    public uint HdrDpLevel;
    // 按内核头的原始大小写照拼（hasHDRdpRpu / hasHDRdpEnhancementLayer）：镜像与内核头逐字段
    // 对撞靠"首字母大写"这一套唯一映射，写成 HdrDp 会让映射失配。
    public uint HasHDRdpRpu;
    public uint HasHDRdpEnhancementLayer;
    public uint HdrDpEnhancementLayer;
    public uint DynamicHdrMetadataActive;
    public uint HdrFallbackActive;
    public uint DisplayMinLuminanceMilliNits;
    public uint DisplayPeakNits;
    public uint DisplayFullFramePeakNits;
    public uint EffectiveTargetPeakNits;
    // ST 2094 动态元数据诊断（逐帧 HDR10+ / HDR Vivid 解析结果）。
    public uint DynamicMetadataWindows;
    public uint DynamicMetadataDegrade;
    public ulong DynamicMetadataSerial;
    public ulong DynamicMetadataHeldFrames;
    public uint DynamicMetadataTargetedNits;
    // IAMF 空间音频组。位置由内核头裁决，不要按"追加式 ABI"推断它们就在结构体最尾部：
    // videoUpload100ns 就插在 iamfSoundSystem 与 iamfContentChannels 之间。
    public uint IamfActive;
    public uint IamfChannels;
    public int IamfSoundSystem;      // 内核是 std::int32_t：OAR sound system 号允许取负/扩展值
    public ulong VideoUpload100ns;
    public uint IamfContentChannels; // content 声明的声道数（≠ iamfChannels 的渲染目标声道数）
    public uint IamfBinaural;        // 1=libiamf 双耳(HRTF)直接出立体声，0=布局由 swr 折叠
    public uint AudioBinaural;       // 1=非 IAMF 多声道床由 OBR HRTF 双耳渲染，0=swr 矩阵折叠
    // 上传/降采样通路记账（内核 578f80b 追加）。
    public long AudioDelay100ns;
    public ulong UploadMapWait100ns;
    public ulong UploadCopy100ns;
    public ulong DownscaleResample100ns;
    public ulong ScalePassCount;
    public ulong UploadCount;
    public ulong UploadCopyBytes;
    public uint UploadPath;
}

/// <summary>FFF3FPVideoPixelProbe 前置关键字段（ReadPixel 用）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpVideoPixelProbe
{
    public uint Size;
    public uint Version;
    public uint X;
    public uint Y;
    public float Red;
    public float Green;
    public float Blue;
    public float Alpha;
    public uint VideoScalingMode;
    public uint OutputBitDepth;
    public uint ColorMode;
    public uint Reserved;
}

/// <summary>FFF3FPHdrSignalDiagnostics（HDR 呈现诊断，API 独立新结构）。
/// 字段顺序与宽度必须与 FFF.Player.Api.h 逐一对应：8 字节头 + 4×u64 计数 + 4×u32 信令值
/// + 2×u32 状态，共 64 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpHdrSignalDiagnostics
{
    public uint Size;
    public uint Version;
    public ulong DynamicCurveFramesApplied;
    public ulong DynamicCurveFramesRefused;
    public ulong SetHdrMetaDataCalls;
    public ulong SetHdrMetaDataSuppressed;
    /// <summary>DXGI 原值：max/min mastering 亮度单位 1/10000 cd/m²，两个光级单位 cd/m²。</summary>
    public uint LastSignaledMaxMasteringLuminance;
    public uint LastSignaledMinMasteringLuminance;
    public uint LastSignaledMaxContentLightLevel;
    public uint LastSignaledMaxFrameAverageLightLevel;
    public uint SwapChainHdrEnabled;
    public uint Reserved;
}

/// <summary>RTX VSR 状态（对应内核 <c>FFF3FPVideoSuperResolutionStatus</c>，
/// FFF.Player.Api.h:649）。11 个 u32 = 44 字节；字段顺序与宽度必须逐一对应，
/// 内核 <c>PlayerSession.cpp:2002</c> 按 <c>size &lt; sizeof || version != 20</c> 拒。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpVideoSuperResolutionStatus
{
    public uint Size;
    public uint Version;
    /// <summary>0 = Off，1 = Auto（<c>FFF3FPVideoSuperResolution</c>）。</summary>
    public uint Requested;
    public uint NvidiaAdapter;
    public uint Active;
    public uint DriverRejected;
    /// <summary><c>FFF3FPVideoSuperResolutionReason</c>；内核在 Active=1 时把它强制写成 None。</summary>
    public uint Reason;
    public uint SourceWidth;
    public uint SourceHeight;
    public uint TargetWidth;
    public uint TargetHeight;
}

/// <summary>动态元数据种类（对齐 FFF3FPDynamicMetadataKind，FFF.Player.Api.h:906）。</summary>
internal enum Fff3FpDynamicMetadataKind : uint
{
    None = 0,
    Hdr10Plus = 1,
    HdrVivid = 2,
    // 调用方 supplied 的色彩变换：一张 33³ 采样 LUT + 一个线性光 3x3 矩阵。
    // 载荷是 Fff3FpColorTransformPayload；内核只按数值消费，不认识生产者是谁。
    ExternalColorTransform = 3,
}

/// <summary>FFF3FPHdrMetadataWindow（ST 2094-40 处理窗口，对齐 FFF.Player.Api.h:729）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpHdrMetadataWindow
{
    public float UpperLeftX;
    public float UpperLeftY;
    public float LowerRightX;
    public float LowerRightY;
    public float MaxSclR;
    public float MaxSclG;
    public float MaxSclB;
    public float AverageMaxRgb;
    public float FractionBrightPixels;
    public uint ToneMappingFlag;
    public float KneePointX;
    public float KneePointY;
    public uint NumCurveAnchors;
    // 固定数组 15 个 float
    public float CurveAnchors0;
    public float CurveAnchors1;
    public float CurveAnchors2;
    public float CurveAnchors3;
    public float CurveAnchors4;
    public float CurveAnchors5;
    public float CurveAnchors6;
    public float CurveAnchors7;
    public float CurveAnchors8;
    public float CurveAnchors9;
    public float CurveAnchors10;
    public float CurveAnchors11;
    public float CurveAnchors12;
    public float CurveAnchors13;
    public float CurveAnchors14;
}

/// <summary>FFF3FPHdrDynamicMetadataEntry（HDR10+ 注入条目，对齐 FFF.Player.Api.h:748）。
/// 旧版专用入口 FFF3FP_SetHdrDynamicMetadata 使用的结构；通用通道 kind=Hdr10Plus 时 payload 为本结构。版本=1。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpHdrDynamicMetadataEntry
{
    public uint Size;
    public uint Version;
    public long FrameIndex;
    public uint NumWindows;
    public uint Reserved;
    public Fff3FpHdrMetadataWindow Window0;
    public Fff3FpHdrMetadataWindow Window1;
    public Fff3FpHdrMetadataWindow Window2;
    public float TargetedDisplayNits;
}

/// <summary>FFF3FPHdrVividMetadataPayload（通用动态元数据通道，HDR Vivid 峰值语义载荷）。
/// Vivid 按定义全幅（内核 FromVivid 同口径），仅承载归一化峰值；max(maxRGB)×10000 = nits。
/// 扁平镜像 3 个窗口（每窗 4 float），4×u32 头 + 48B + float = 68 字节（C++ static_assert ==68）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpHdrVividMetadataPayload
{
    public uint Size;
    public uint Version;       // == 1
    public uint NumWindows;    // 1..3
    public uint Reserved;
    // windows[0..2]，各 4 float：maxRGB[3] + averageMaxRGB。
    public float W0MaxRGB0; public float W0MaxRGB1; public float W0MaxRGB2; public float W0AverageMaxRGB;
    public float W1MaxRGB0; public float W1MaxRGB1; public float W1MaxRGB2; public float W1AverageMaxRGB;
    public float W2MaxRGB0; public float W2MaxRGB1; public float W2MaxRGB2; public float W2AverageMaxRGB;
    public float TargetedDisplayNits;   // cd/m²，0=由窗口峰值推导
}

/// <summary>FFF3FPColorTransformPayload（kind=3 外部色彩变换载荷）。真源是内核头
/// <c>3fp/kernel/FFF.Native/3FP/Api/FFF.Player.Api.h</c>，那里用
/// <c>static_assert(sizeof == 431296)</c> 钉死；托管镜像由
/// <c>ColorTransformLayoutTests</c> 逐字段偏移比对（同 DynamicMetadataLayoutTests 的做法）。</summary>
/// <remarks>
/// 布局：16B 头（Size/Version/LutResolution/LutDomain）+ 9×float 矩阵（36B）
/// + 33³×3 float 立体 LUT（431244B）= 431296B，全程 4 字节对齐、无填充。
/// 数组取 ByValArray 镜像而非逐字段扁平化：107811 个 float 摊成字段不现实，
/// 而这个结构<b>从不作为 P/Invoke 形参传递</b>（包里的 Payload 是 nint），
/// 源生成器不会为它生成封送代码，Marshal.SizeOf/OffsetOf 照常可用。
/// 宿主用法：填好实例后 StructureToPtr 到一块 431296B 非托管内存，把指针放进包。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpColorTransformPayload
{
    public uint Size;           // == 431296，内核按精确尺寸校验（不是前缀放行）
    public uint Version;        // == 1，即包的 KindVersion
    public uint LutResolution;  // == 33，内核 kColorTransformLutResolution
    public uint LutDomain;      // 0=替代源分量→RGB；1=在内核自己的转换之后作用于编码 RGB

    /// <summary>行主序 3x3（out = M * rgb），作用于传函解码后的线性光信号；全零=单位阵。</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public float[] OutputMatrix;

    /// <summary>33³ 个 texel，每 texel 一个 float3，平坦索引 (z*res + y)*res + x；采样为三线性。</summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 33 * 33 * 33 * 3)]
    public float[] CurveLut;
}

/// <summary>FFF3FPDynamicMetadataPacket（通用动态元数据注入通道的包，共 40 字节，
/// C++ 侧 static_assert ==40 钉死）。Payload 按 Kind 解释：Hdr10Plus=FFF3FPHdrDynamicMetadataEntry
/// 结构指针，HdrVivid=Fff3FpHdrVividMetadataPayload；未知 Kind 内核返回 NotSupported 且旧表续任。
/// Kind 常量：Hdr10Plus=1、HdrVivid=2、0 仅用于 Clear 表示全部。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpDynamicMetadataPacket
{
    public uint Size;
    public uint Version;       // == 1
    public uint Kind;          // FFF3FPDynamicMetadataKind
    public uint KindVersion;   // 目前 1
    public long FrameIndex;    // 与 HDR10+ 注入同一匹配键（snapshot.frameIndex）
    public uint PayloadSize;
    public uint Reserved;
    public nint Payload;       // 内核成功即拷贝，调用方保有自己的内存
}

/// <summary>FFF3FPRenderTargetInfo（3FCompare K4，API 独立新结构）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpRenderTargetInfo
{
    public uint Size;
    public uint Version;  // == 2（v2 起 destX/destY 为有符号）
    public uint SwapWidth;
    public uint SwapHeight;
    public uint ClientWidth;
    public uint ClientHeight;
    // 有符号：放大平移会把绘制盒的左/上沿推到后台缓冲之外（负原点）。
    // 与内核 FFF3FPRenderTargetInfo 逐字段对齐；字节宽度与 uint 相同，靠 Version 区分语义。
    public int DestX;
    public int DestY;
    public uint DestWidth;
    public uint DestHeight;
    public uint OutputBitDepth;
    public uint Hdr;
}

/// <summary>FFF3FPPresentPacingStatus（2026-10-15 呈现节律锁的回读面）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpPresentPacingStatus
{
    public uint Size;
    public uint Version;  // == 20，与内核 FFF.Native 的 PlayerApiVersion 同一个数
    // EnumDisplaySettings 报给内核的面板刷新率。内核过去先把它 clamp 到 120，
    // 于是 144Hz 面板被报成 120——这里是不再修正后的原值。
    public float DeclaredRefreshHz;
    // 从交换链自己的帧统计（SyncRefreshCount 对 SyncQPCTime）反解出的面板周期。
    // 0 = 还没标定，也就是"锁还没上岗"。
    public float MeasuredRefreshHz;
    // 生效中的窗口翻帧上限，Hz。自动取 min(面板率, 视频率)，不是可调项。
    public float FrameRateBoundHz;
    public uint GridLocked;       // 1 = 每次翻帧都对齐到刷新网格
    // 0 = 已锁或本就没有可对齐的节律；1 = 走了可变刷新路（G-SYNC/FreeSync 自己配速）；
    // 2 = 网格还没标定出来；3 = 这次翻帧由原生窗口移动循环接管。
    public uint PacingBypass;
    // 上一次翻帧相对它瞄准的那一格的时间差，单位 100ns。锁生效时应为 0。
    public long LastSlotError100ns;
    // 结果那半边，由内核从 PresentRefreshCount 数出来：可测停留数 / 落在 ⌊r⌋ 或 ⌈r⌉ 带内的
    // 次数（60fps 配 144Hz 就是 2 或 3 格）/ 带外最大偏差。带内就是"看着稳"，带外（1 格，
    // 或 4 格以上）才是眼睛说的卡顿。提交准时只算一半，这三条才说明结果。
    public ulong HoldSampled;
    public ulong HoldWithinRounding;
    public ulong HoldWorstDeviationRefreshes;
    // 因为"片源比面板快"而被丢弃的帧（如 240fps 片配 144Hz 面板）。刻意**没有**并进
    // 快照的 DroppedVideoFrames：那个读数的含义是"管线跟不上媒体时钟"，还要驱动自适应降采样，
    // 而面板限死的丢弃两者都不是——把画面缩小也不会让 144Hz 面板播出 240 帧。
    // 两个数要并排列出，总和才是丢弃总量。
    public ulong DisplayRateDiscardedFrames;
    // 交给了 Present() 但图像从未到达显示器的翻帧数（被更晚一帧在采样前取代）。停留判带没有
    // 这个数就没法解释：PresentRefreshCount 只在真上屏那场 vblank 前进，从未上屏的翻帧不推进
    // 它，于是两三帧会被并成一次"跨 5~7 格"的停留，看起来像卡顿而不是合并。
    // ⚠ 内核按 size >= sizeof 校验这个结构：加了尾字段就必须与内核**成对重编**，
    //    否则这一次读取会返回 InvalidArgument（不写越界，但读数整个失效）。
    public ulong NeverCompositedPresents;
}

/// <summary>对 FFF.Native.dll（3FP）的 P/Invoke 互操作层。
/// 全部使用 <see cref="LibraryImportAttribute"/> 以兼容 NativeAOT 静态解析（06-R15）。</summary>
internal static partial class Fff3FpNative
{
    private const string DllName = "FFF.Native";

    // ---- 生命周期 ----

    [LibraryImport(DllName)]
    internal static partial uint FFF3FP_GetApiVersion();

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Create(
        in Fff3FpConfiguration configuration, out nint player);

    /// <summary>销毁播放器句柄。
    /// ⚠ 原生声明是 <c>void FFF3FP_Destroy(FFF3FPHandle) noexcept</c>
    /// （FFF.Player.Api.h:571），<b>不返回</b> FFFResult。
    /// 按 FffResult 声明时，x64 下托管侧会把 RAX 里的残留值当成返回值
    /// （被调用者不保证在 RAX 中留下有效值）⇒ 读到的是一个无意义的结果码。
    /// 当前调用点不使用返回值所以没暴露成 bug，但声明必须与真实 ABI 一致。</summary>
    [LibraryImport(DllName)]
    internal static partial void FFF3FP_Destroy(nint player);

    // ---- 控制 ----

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Open(nint player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string localPathUtf8);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Play(nint player);

    /// <summary>转交外部床渲染函数表（表由 <see cref="ColorTransformModule.TryGetAudioRenderTable"/>
    /// 取得）。内核拷贝整张表，因此调用返回后调用方即可释放这块缓冲；传 0 表示撤下、
    /// 回到矩阵折叠。附加导出：不抬 API 版本、不动快照布局。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetAudioRenderPlug(nint player, nint table);

    /// <summary>插槽回读：owner=1 表示最后一帧床由外部渲染器产出，0 表示矩阵折叠。
    /// 名称缓冲与两个计数按契约可为 0（可选输出）。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetAudioRenderPlugState(nint player,
        out uint owner, nint nameBuffer, uint nameCapacity,
        out ulong framesRendered, out ulong framesDeclined);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Pause(nint player);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Stop(nint player);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Seek(nint player, long position100ns);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SeekFrame(nint player, long frameIndex);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_StepFrame(nint player, int direction);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SelectVideoStream(nint player, int streamIndex);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SelectAudioStream(nint player, int streamIndex);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetVolume(nint player, float volume, uint muted);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetOutputWindow(nint player, nint outputWindow);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetViewTransform(nint player,
        float zoom, float panX, float panY);

    /// <summary>3FCompare 内核扩展：0=上游行为（zoom 相对"适配窗口后的视频框"）；
    /// 1=把适配盒封顶到源原生尺寸，于是 zoom 直接就是 屏幕像素/视频像素 之比
    /// （zoom=1 是逐像素 1:1，窗口比源大时四周留黑边）。光盘在播时返回 NotSupported。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetFitLimitToNative(nint player, uint enable);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetColorMode(nint player,
        uint colorMode, float sdrPeakNits, float hdrPeakNits, float sdrPaperWhiteNits,
        uint forceHdrOutput);

    // 3FCompare 扩展（VRR 低延迟呈现）：1 = Present(0, ALLOW_TEARING)，0 = VSync 锁定
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetPresentConfig(nint player, uint enableTearing);

    // 呈现器实际按什么在配速。窗口翻帧上限是自动的 min(面板率, 视频率)，**没有 setter**——
    // 装了 setter 就等于给一个恒开的行为配了开关，与既定口径冲突。
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetPresentPacingStatus(nint player,
        ref Fff3FpPresentPacingStatus status);

    // ---- 读取 ----

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetSnapshot(nint player, ref Fff3FpSnapshot snapshot);

    /// <summary>HDR 呈现诊断：ST 2094 曲线到底有没有作用于已绘帧、逐帧信令实际发没发
    /// （内核 4925 的闸门与 2% 变更限流此前只能读源码，现在是可读数）。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetHdrSignalDiagnostics(nint player,
        ref Fff3FpHdrSignalDiagnostics diagnostics);

    /// <summary>RTX VSR：请求 Auto(1) 或 Off(0)。内核在 <c>ApplySuperResolution</c> 里逐帧判定
    /// 七道门（适配器/格式/是否真放大/源高度 360..1440/失败闩），任何一道不过都只改变
    /// <c>reason</c>，不影响本次调用的返回值。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetVideoSuperResolution(nint player, uint mode);

    /// <summary>读取 VSR 状态。返回 false 的两种含义要分清：入口缺失（旧盘上 DLL，
    /// <c>EntryPointNotFoundException</c>）由上层捕获；结构尺寸/版本不符才是这里的
    /// <c>InvalidArgument</c>。<para>注意 <c>Active</c> 只表示"驱动没有拒绝"——
    /// 实测该路径的 HRESULT 不携带任何能力信息，见内核 NvidiaVideoExtensions.h 的说明。</para></summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetVideoSuperResolutionStatus(nint player,
        ref Fff3FpVideoSuperResolutionStatus status);

    /// <summary>通用动态元数据注入通道（3FCompare 扩展，2026-10-10）：kind 标签 + 版本化载荷，
    /// 与 ST 2094-40 注入共用一张表、同一匹配/拒绝语义；未知 Kind → NotSupported 且旧表续任。
    /// 原生形参是 <c>const FFF3FPDynamicMetadataPacket*</c>：数组形态声明让源生成器整体钉住
    /// managed 数组（元素连续），比 "传首元素引用 + count" 少一处"staging 拷贝"的猜测空间。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetDynamicMetadata(nint player,
        Fff3FpDynamicMetadataPacket[] packets, uint count);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_ClearDynamicMetadata(nint player, uint kind);

    /// <summary>中性帧边数据环（2026-10-13，追加式入口，PlayerApiVersion 不动，
    /// 宿主按 GetProcAddress 语义解析；旧盘上 DLL 会抛 <c>EntryPointNotFoundException</c>）。
    /// 取"帧号 ≥ fromFrameIndex 且带该类型边数据"的最小帧的<b>原始字节</b>，交给外部分析
    /// 模块生成 kind=3 载荷；内核不解释内容，类型只是一个数。</summary>
    /// <remarks>
    /// sideDataType 用 FFmpeg <c>AVFrameSideDataType</c> 的数值（本机对 bundled 头实测：
    /// DYNAMIC_HDR_PLUS=17、DOVI_RPU_BUFFER=23、DOVI_METADATA=24、DYNAMIC_HDR_VIVID=25，
    /// 见 <c>3fp/tools/sidedata_size_probe.cpp</c>）。
    /// buffer 传 0 只问尺寸（两趟式契约，同 CopyBitmapSubtitlePixels）；
    /// 容量不足返回 <see cref="FffResult.BufferTooSmall"/> 且 writtenSize 报所需字节数，
    /// 条目留在环里可换大缓冲重试；环里没有匹配返回 <see cref="FffResult.NotFound"/>
    /// （writtenSize=0、foundFrameIndex=-1）。
    /// 环上界：120 帧 / 1 MiB，单块超过 16 KiB 的边数据不缓存（内核常量，按实测结构尺寸定）。
    /// 迭代方式：下次把 fromFrameIndex 设为 foundFrameIndex + 1。
    /// </remarks>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_CopyFrameSideDataAfter(nint player,
        uint sideDataType, long fromFrameIndex, nint buffer, uint capacity,
        out uint writtenSize, out long foundFrameIndex);

    // ---- HDR10+ 专用注入（旧版入口，与通用通道 kind=Hdr10Plus 共用内核实现）----

    /// <summary>HDR10+ 动态元数据来源（对齐 FFF3FPHdrMetadataSource，FFF.Player.Api.h:715）。</summary>
    internal enum Fff3FpHdrMetadataSource : uint
    {
        Bitstream = 0,
        Injected = 1,
    }

    /// <summary>设置 HDR10+ 动态元数据注入表（旧版专用入口）。
    /// 原生形参是 <c>const FFF3FPHdrDynamicMetadataEntry*</c>（数组 + count）：数组形态声明的
    /// 理由同 <see cref="FFF3FP_SetDynamicMetadata"/>。条目被内核拷贝，调用方保有自己的数组。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetHdrDynamicMetadata(nint player,
        Fff3FpHdrDynamicMetadataEntry[] entries, uint count);

    /// <summary>清除 HDR10+ 动态元数据注入表（旧版专用入口）。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_ClearHdrDynamicMetadata(nint player);

    /// <summary>获取当前 HDR 动态元数据来源（旧版专用入口）。</summary>
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetHdrMetadataSource(nint player,
        out uint source);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetMediaInfo(nint player,
        nint outputUtf8, uint outputSize, out uint requiredSize);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetLastError(nint player,
        nint outputUtf8, uint outputSize, out uint requiredSize);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_ReadVideoPixel(nint player,
        ref Fff3FpVideoPixelProbe probe);

    // 3FCompare patch (0004): batch pixel readback (single GPU staging copy).
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_ReadVideoPixelRegion(nint player,
        uint x, uint y, uint width, uint height,
        float[] dst, uint dstFloatCount, out uint outputBitDepth);

    // 3FCompare K4/K5: render-target diagnostics + Redraw
    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetRenderTargetInfo(nint player,
        ref Fff3FpRenderTargetInfo info);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Redraw(nint player);
}