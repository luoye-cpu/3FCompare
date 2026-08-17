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
}

/// <summary>显示器HDR能力（对应3FP的HdrDisplayCapabilities）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HdrDisplayCapabilities
{
    public bool supported;              // 是否支持HDR
    public float minimumNits;           // 最小亮度（nits）
    public float maximumNits;           // 峰值亮度（nits）
    public float maximumFullFrameNits;  // 全帧最大亮度（nits）
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

/// <summary>FFF3FPConfiguration 序列表（字段布局需在拿到 fork 工程后对照 FFF.Player.Api.h 校准）。</summary>
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
    public uint VideoScalingQuality; // 新增 v11: 0=Balanced, 1=HighQuality
}

/// <summary>FFF3FPSnapshot 完整布局（对照 FFF.Player.Api.h v8，逐字段对齐）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fff3FpSnapshot
{
    public uint Size;
    public uint Version;            // GetSnapshot 要求 == 8
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
    public uint DolbyVisionProfile;
    public uint DolbyVisionLevel;
    public uint HasDolbyVisionRpu;
    public uint HasDolbyVisionEnhancementLayer;
    public uint DolbyVisionEnhancementLayer;
    public uint DynamicHdrMetadataActive;
    public uint HdrFallbackActive;
    public uint DisplayMinLuminanceMilliNits;
    public uint DisplayPeakNits;
    public uint DisplayFullFramePeakNits;
    public uint EffectiveTargetPeakNits;
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

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Destroy(nint player);

    // ---- 控制 ----

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Open(nint player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string localPathUtf8);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_Play(nint player);

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

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_SetColorMode(nint player,
        uint colorMode, float sdrPeakNits, float hdrPeakNits, float sdrPaperWhiteNits);

    // ---- 读取 ----

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetSnapshot(nint player, ref Fff3FpSnapshot snapshot);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetMediaInfo(nint player,
        nint outputUtf8, uint outputSize, out uint requiredSize);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_GetLastError(nint player,
        nint outputUtf8, uint outputSize, out uint requiredSize);

    [LibraryImport(DllName)]
    internal static partial FffResult FFF3FP_ReadVideoPixel(nint player,
        ref Fff3FpVideoPixelProbe probe);
}