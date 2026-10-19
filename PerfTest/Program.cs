using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Threading;

namespace PerfTest;

using FFF3FPHandle = IntPtr;
using FFFResult = int;

static class Win32 {
    public const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int WS_VISIBLE = 0x10000000;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const int WM_DESTROY = 0x0002;
    public const int WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    public delegate IntPtr WNDPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}

enum FFF3FPDecodeMode : uint { Unspecified = 0, Cpu = 1, Gpu = 2, D3D11 = Gpu }
enum FFF3FPColorMode : uint { MapToSdr = 0, RawHdrAsSdr = 1, MapToHdr = 2 }
enum FFF3FPState : uint { Idle = 0, Opening = 1, Ready = 2, Playing = 3, Paused = 4, Ended = 5, Failed = 6, Closed = 7 }
enum FFF3FPEvent : uint { StateChanged = 1, OpenCompleted = 2, OperationCompleted = 3, PlaybackEnded = 4, Error = 5, ColorModeChanged = 6, DeviceChanged = 7 }

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void FFF3FPEventCallback(IntPtr context, FFF3FPEvent eventType, [MarshalAs(UnmanagedType.LPStr)] string detailJsonUtf8);

[StructLayout(LayoutKind.Sequential)]
struct FFF3FPConfiguration {
    public uint size;
    public uint version;
    public IntPtr outputWindow;
    public FFF3FPDecodeMode decodeMode;
    public FFF3FPColorMode colorMode;
    public float sdrPeakNits;
    public float hdrPeakNits;
    public float sdrPaperWhiteNits;
    [MarshalAs(UnmanagedType.LPStr)] public string audioEndpointIdUtf8;
    public FFF3FPEventCallback eventCallback;
    public IntPtr eventCallbackContext;
    public uint videoScalingQuality;
    public uint forceHdrOutput;
    public int preferredAdapterIndex;
    public uint sdrScRgbMode;
    // ⛔ 尾追加段（内核 22 字段 / 104 B）。FFF3FP_Create 门是
    // `configuration->size < sizeof(FFF3FPConfiguration)`，托管镜像少这一段 ⇒
    // 80 < 104 ⇒ Create 直接 InvalidArgument（2026-10-18 实测：本文件停在 15 字段）。
    // 默认值必须与内核头的成员初始化式一致，否则测的是另一套策略。
    public uint adaptiveDownscaleBeforeUpload;   // 0 = 关闭（默认）
    public uint adaptiveDownscaleDropPercent;    // 20
    public uint adaptiveDecoderThreads;          // 0 = 固定线程数（默认）
    public uint minSoftwareDecoderThreads;       // 4
    public uint maxSoftwareDecoderThreads;       // 32
    public uint decoderGrowDropPercent;          // 10
    public uint decoderShrinkDropPercent;        // 2
}

[StructLayout(LayoutKind.Sequential)]
struct FFF3FPSnapshot {
    public uint size;
    public uint version;
    public FFF3FPState state;
    public FFF3FPDecodeMode decodeMode;
    public FFF3FPColorMode requestedColorMode;
    public FFF3FPColorMode actualColorMode;
    public long position100ns;
    public long duration100ns;
    public long frameIndex;
    public long framePts;
    public int frameTimeBaseNumerator;
    public int frameTimeBaseDenominator;
    public int selectedVideoStream;
    public int selectedAudioStream;
    public uint videoWidth;
    public uint videoHeight;
    public uint isHdrSource;
    public uint isExternalAudio;
    public long externalAudioOffset100ns;
    public ulong decodedVideoFrames;
    public ulong presentedVideoFrames;
    public ulong droppedVideoFrames;
    public uint queuedVideoFrames;
    public uint sourcePeakNits;
    public ulong decodedAudioFrames;
    public long audioPosition100ns;
    public long bufferedAudio100ns;
    public ulong audioUnderruns;
    public ulong audioTimestampJitterFrames;
    public ulong audioDiscontinuities;
    public ulong audioInsertedSilenceFrames;
    public ulong audioDroppedOverlapFrames;
    public ulong coalescedVideoFrames;
    public ulong audioRejectedFrames;
    public ulong swapChainPresents;
    public ulong presentWait100ns;
    public ulong deviceLockWait100ns;
    public ulong hardwareTransfer100ns;
    public ulong softwareConvert100ns;
    public ulong videoBitRate;
    public ulong audioBitRate;
    public uint videoOutputBitDepth;
    public uint videoScalingMode;
    public ulong timelineGeneration;
    public uint hdrFormat;
    public uint compatibleHdrFormats;
    public uint hdrProcessingPath;
    // ⛔ DEPRECATED ABI 占位槽（5 × uint = 20 B）——**禁止删除，禁止改名，禁止改序**。
    // 出货树内核头把这五个槽位标为 "DEPRECATED ABI (slots retained, always zero)"：
    // 中性化的正确动作是「改名 + 恒零 + DEPRECATED 注释」，不是删字段。
    // 2026-10-14 托管镜像删槽 ⇒ GetSnapshot 整体 InvalidArgument；本文件在
    // 2026-10-15 全面审查时被发现重犯了同一错误（少 20 B ⇒ 376 B < 内核 392 B，
    // 每次 GetSnapshot 都被 PlayerSession.cpp 的尺寸门拒掉，而调用侧又不检查
    // 返回值 ⇒ 全零快照静默产出，性能数字全部失真）。
    // 守护：tools/check_snapshot_pairing_matrix.py（perftest ↔ 出货树 designated pair）。
    public uint hdrDpProfile;
    public uint hdrDpLevel;
    public uint hasHDRdpRpu;
    public uint hasHDRdpEnhancementLayer;
    public uint hdrDpEnhancementLayer;
    public uint dynamicHdrMetadataActive;
    public uint hdrFallbackActive;
    public uint displayMinLuminanceMilliNits;
    public uint displayPeakNits;
    public uint displayFullFramePeakNits;
    public uint effectiveTargetPeakNits;
    public uint dynamicMetadataWindows;
    public uint dynamicMetadataDegrade;
    public ulong dynamicMetadataSerial;
    public ulong dynamicMetadataHeldFrames;
    public uint dynamicMetadataTargetedNits;
    // ⛔ IAMF 三字段在内核里位于**尾部**（364/368/372），不在 decodedAudioFrames 之后。
    // 本文件曾把它们放在中段：sizeof 同样是 408，但 iamfActive 落在 136 而非 364，
    // 后半段全部错位（2026-10-18 由 OffsetOf 自检抓出）。顺序必须照抄内核头。
    public uint iamfActive;
    public uint iamfChannels;
    public int iamfSoundSystem;
    // 内核 offset 376：紧跟 iamfSoundSystem（Upload 计时，软解大帧的主要开销之一）。
    public ulong videoUpload100ns;
    public uint iamfContentChannels;
    public uint iamfBinaural;
    public uint audioBinaural;
    public long audioDelay100ns;
    // ⛔ 2026-10-18 尾追加：上传路径拆分（内核 440 B）。videoUpload100ns 一个数里混了
    // 「Map 等 GPU」「真拷贝」「驱动提交」三件事，靠它无法定位开销在哪，所以拆开：
    //   uploadMapWait100ns      - Map() 调用本身（不带 DO_NOT_WAIT 时会阻塞等 GPU）
    //   uploadCopy100ns         - memcpy 循环 / UpdateSubresource（真正在搬字节）
    //   downscaleResample100ns  - 降档策略的 sws_scale（原本完全无计量）
    //   scalePassCount          - 缩放链 pass 累计执行次数（÷ 呈现帧数 = 每帧 pass 数）
    // 顺序必须照抄内核头；sizeof 会从 408 变 440。
    public ulong uploadMapWait100ns;
    public ulong uploadCopy100ns;
    public ulong downscaleResample100ns;
    public ulong scalePassCount;
}

class Native {
    const string DllName = "FFF.Native.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FFF3FP_GetApiVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Create(ref FFF3FPConfiguration configuration, out FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Open(FFF3FPHandle player, [MarshalAs(UnmanagedType.LPStr)] string localPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Play(FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Pause(FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Stop(FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_Close(FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_GetSnapshot(FFF3FPHandle player, ref FFF3FPSnapshot snapshot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FFF3FP_Destroy(FFF3FPHandle player);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FFFResult FFF3FP_GetLastError(FFF3FPHandle player, StringBuilder outputUtf8, uint outputSize, out uint requiredSize);
}

class Program {
    static string lastEvent = "";
    static readonly FFF3FPEventCallback eventCb = (ctx, type, json) => {
        lastEvent = $"Event {type}: {json}";
    };

    // 窗口客户区尺寸是要被测量的一等参数：缩放链按「源 → 客户区」的缩放比逐级减半，
    // 640×480 的窗口会让 8K 素材做 1:12 的极端降采样（16 个 pass），测出来的"上传"
    // 里混着大量 GPU 绘制。默认 640×480 只是为了和旧口径对照。
    static IntPtr CreateDummyWindow(int clientWidth, int clientHeight) {
        var wc = new Win32.WNDCLASSEX {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<Win32.WNDPROC>(DefWindowProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = Win32.GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "PerfTestWindowClass",
            hIconSm = IntPtr.Zero
        };
        Win32.RegisterClassEx(ref wc);

        const uint style = Win32.WS_OVERLAPPEDWINDOW | 0x04000000 | 0x08000000; // | WS_CLIPCHILDREN | WS_CLIPSIBLINGS
        // CreateWindowEx 收的是外层尺寸，缩放链用的是客户区尺寸，两者差标题栏+边框。
        // 不反算的话 640×480 的外层窗口客户区只有约 624×441，尺寸对不上就谈不上口径。
        var rect = new Win32.RECT { left = 0, top = 0, right = clientWidth, bottom = clientHeight };
        Win32.AdjustWindowRectEx(ref rect, style, false, 0);

        IntPtr hwnd = Win32.CreateWindowEx(0, "PerfTestWindowClass", "PerfTest",
            style, Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT,
            rect.right - rect.left, rect.bottom - rect.top,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        Win32.ShowWindow(hwnd, 1); // SW_SHOWNORMAL
        Win32.UpdateWindow(hwnd);
        if (Win32.GetClientRect(hwnd, out var got))
            Console.WriteLine($"窗口: 请求客户区 {clientWidth}x{clientHeight}, 实测客户区 {got.right - got.left}x{got.bottom - got.top}");
        return hwnd;
    }

    static Win32.WNDPROC DefWindowProc = (hWnd, uMsg, wParam, lParam) => {
        if (uMsg == Win32.WM_DESTROY) {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hWnd, uMsg, wParam, lParam);
    };

    static void Main(string[] args) {
        if (args.Length < 1) {
            Console.WriteLine("Usage: PerfTest.exe <video_file>");
            return;
        }

        string path = args[0];
        int limitSeconds = 30;
        if (args.Length > 1 && int.TryParse(args[1], out var parsed) && parsed > 0) limitSeconds = parsed;
        // 第三个参数：adaptiveDecoderThreads（0 = 固定线程数基线，1 = 开自适应线程梯）。
        uint adaptiveThreads = 0;
        if (args.Length > 2 && uint.TryParse(args[2], out var at) && at <= 1) adaptiveThreads = at;
        // 第四个参数：adaptiveDownscaleBeforeUpload（0 = 默认关闭，1 = 开启降档再上传）。
        uint downscale = 0;
        if (args.Length > 3 && uint.TryParse(args[3], out var ds) && ds <= 1) downscale = ds;
        // 第五个参数：窗口客户区尺寸 "WxH"。测量口径的一等参数——缩放链按
        // 「源尺寸 → 客户区」的缩放比逐级减半，窗口越小 pass 越多、GPU 绘制越贵，
        // 而这份成本会被 Map() 的同步反射进 upload100ns。默认 640x480 = 旧口径。
        int clientW = 640, clientH = 480;
        if (args.Length > 4 && !string.IsNullOrEmpty(args[4])) {
            var parts = args[4].Split('x', 'X', '*', ',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph)
                && pw > 0 && ph > 0) { clientW = pw; clientH = ph; }
        }
        // 窗口不能超过屏幕，否则客户区会被系统裁到屏幕大小，报出来的尺寸是假的。
        int screenW = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int screenH = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        if (screenW > 0 && screenH > 0 && (clientW > screenW || clientH > screenH)) {
            Console.WriteLine($"⚠ 请求窗口 {clientW}x{clientH} 超过屏幕 {screenW}x{screenH}，按屏幕裁切");
            clientW = Math.Min(clientW, screenW);
            clientH = Math.Min(clientH, screenH);
        }
        Console.WriteLine($"3FP 软解性能测试 (采样 {limitSeconds}s)");
        Console.WriteLine($"文件: {path}");
        Console.WriteLine($"窗口客户区: {clientW}x{clientH} (屏幕 {screenW}x{screenH})");
        Console.WriteLine($"策略: adaptiveDecoderThreads={adaptiveThreads} adaptiveDownscaleBeforeUpload={downscale}");
        Console.WriteLine($"API 版本: {Native.FFF3FP_GetApiVersion()}");
        Console.WriteLine();

        // 设置 FFmpeg DLL 搜索目录
        string ffmpegDir = Path.GetFullPath("ffmpeg-full");
        if (Directory.Exists(ffmpegDir)) {
            Native.SetDllDirectory(ffmpegDir);
            Console.WriteLine($"SetDllDirectory: {ffmpegDir}");
        }

        // 复制 DLL 到输出目录
        string dllSrc = @"C:\PLAN\3FCompare\third_party\fff_project\FFF.Native\x64\Release\FFF.Native.dll";
        string dllDst = "FFF.Native.dll";
        if (!File.Exists(dllDst) || new FileInfo(dllSrc).Length != new FileInfo(dllDst).Length) {
            File.Copy(dllSrc, dllDst, true);
        }

        // 创建一个最小窗口用于渲染
        IntPtr hwnd = CreateDummyWindow(clientW, clientH);
        Console.WriteLine($"Created window: {hwnd}");

        uint configSize = (uint)Marshal.SizeOf<FFF3FPConfiguration>();
        Console.WriteLine($"Config size: {configSize}");
        var cfg = new FFF3FPConfiguration {
            size = configSize,
            version = 20,
            outputWindow = hwnd,
            decodeMode = FFF3FPDecodeMode.Cpu,
            colorMode = FFF3FPColorMode.MapToSdr,
            sdrPeakNits = 203f,
            hdrPeakNits = 1000f,
            sdrPaperWhiteNits = 80f,
            audioEndpointIdUtf8 = null,
            eventCallback = eventCb,
            eventCallbackContext = IntPtr.Zero,
            videoScalingQuality = 0,
            forceHdrOutput = 0,
            preferredAdapterIndex = -1,
            sdrScRgbMode = 0,
            // 与内核头成员初始化式逐一对齐
            adaptiveDownscaleBeforeUpload = downscale,
            adaptiveDownscaleDropPercent = 20,
            adaptiveDecoderThreads = adaptiveThreads,
            minSoftwareDecoderThreads = 4,
            maxSoftwareDecoderThreads = 32,
            decoderGrowDropPercent = 10,
            decoderShrinkDropPercent = 2
        };

        FFFResult r = Native.FFF3FP_Create(ref cfg, out var player);
        if (r != 0) { PrintError(player, "Create"); return; }
        Console.WriteLine("Create 成功");

        r = Native.FFF3FP_Open(player, path);
        if (r != 0) { PrintError(player, "Open"); return; }
        Console.WriteLine("Open 成功");

        uint snapSize = (uint)Marshal.SizeOf<FFF3FPSnapshot>();
        Console.WriteLine($"Snapshot size: {snapSize}");
        // 偏移对撞自检：版本号相等证明不了布局一致，"字段都在但位置错了"会静默读错字段
        // （2026-10-18 实测 sizeof 408 == 408 而 iamfActive 差 228 B）。内核头用
        // static_assert 钉住的锚点必须逐个对上，对不上就退出，不能只是打印了事。
        var anchors = new (string name, int expected)[] {
            ("externalAudioOffset100ns", 88),
            ("iamfActive", 364),
            ("videoUpload100ns", 376),
            ("iamfContentChannels", 384),
            ("audioBinaural", 392),
            ("audioDelay100ns", 400),
            ("uploadMapWait100ns", 408),
            ("uploadCopy100ns", 416),
            ("downscaleResample100ns", 424),
            ("scalePassCount", 432),
        };
        bool layoutOk = snapSize == 440;
        foreach (var (name, expected) in anchors) {
            int actual = (int)Marshal.OffsetOf<FFF3FPSnapshot>(name);
            if (actual != expected) {
                Console.Error.WriteLine($"✗ 布局错位: {name} 托管 {actual} != 内核 {expected}");
                layoutOk = false;
            }
        }
        Console.WriteLine($"OffsetOf: {string.Join(" ", Array.ConvertAll(anchors, a => $"{a.name}={Marshal.OffsetOf<FFF3FPSnapshot>(a.name)}"))}");
        if (!layoutOk) {
            Console.Error.WriteLine("托管 snapshot 镜像与内核布局不一致，测出来的数字不可信，退出。");
            return;
        }
        Console.WriteLine("✓ snapshot 布局对撞通过（sizeof + 10 个锚点）");
        var snap = new FFF3FPSnapshot { size = snapSize, version = 20 };
        for (int i = 0; i < 200; i++) {
            var gs = Native.FFF3FP_GetSnapshot(player, ref snap);
            if (gs != 0) {
                // 调用方结构体比内核小会被尺寸门直接 InvalidArgument；旧代码在这里
                // 忽略返回值，于是 snap 保持全零继续往下算——性能数字全是对着空快照
                // 得出的（2026-10-15 审查实测：镜像少 5 个 DEPRECATED 槽即触发）。
                PrintError(player, $"GetSnapshot 返回 {gs}");
                return;
            }
            if (snap.state == FFF3FPState.Ready) break;
            Thread.Sleep(50);
        }
        PrintSnapshot(snap, "Open 完成");

        r = Native.FFF3FP_Play(player);
        if (r != 0) { PrintError(player, "Play"); return; }
        Console.WriteLine("Play 成功");

        // 等待进入 Playing 状态
        for (int i = 0; i < 50; i++) {
            Native.FFF3FP_GetSnapshot(player, ref snap);
            Console.WriteLine($"  等待状态: State={snap.state}, Decoded={snap.decodedVideoFrames}, Presented={snap.presentedVideoFrames}");
            if (snap.state == FFF3FPState.Playing) break;
            Thread.Sleep(100);
        }

        var start = Stopwatch.StartNew();
        ulong lastPresented = 0, lastDecoded = 0, lastSwap = 0;
        ulong lastDropped = 0, lastCoalesced = 0;
        ulong prevSwConvert = 0, prevHwTransfer = 0, prevDevLock = 0, prevPresentWait = 0;
        ulong prevUpload = 0;
        // 上传拆分（2026-10-18 新增）：map = Map() 里等 GPU；copy = 真搬字节；
        // resample = 降档策略的 sws_scale，它在 upload 之外，旧口径完全看不见。
        ulong prevUploadMap = 0, prevUploadCopy = 0, prevResample = 0, prevPasses = 0;
        // 每个采样窗口一行 CSV，便于事后聚合各阶段耗时占比（开销聚集分析）。
        // total_ms 保持旧口径（五项），total2_ms 追加 resample，保证与旧数据可比。
        var csv = new StringBuilder();
        csv.AppendLine("t_s,decoded_s,presented_s,swap_s,dropped_s,coalesced_s,queued,"
            + "sw_ms,hw_ms,upload_ms,devlock_ms,presentwait_ms,total_ms,fps,"
            + "upload_map_ms,upload_copy_ms,resample_ms,passes_per_frame,total2_ms");

        // 消息泵线程
        var msgThread = new Thread(() => {
            Win32.MSG msg;
            while (Win32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }) { IsBackground = true };
        msgThread.Start();

        while (true) {
            Thread.Sleep(1000);
            Native.FFF3FP_GetSnapshot(player, ref snap);

            ulong decodedDelta = snap.decodedVideoFrames - lastDecoded;
            ulong presentedDelta = snap.presentedVideoFrames - lastPresented;
            ulong swapDelta = snap.swapChainPresents - lastSwap;
            ulong droppedDelta = snap.droppedVideoFrames - lastDropped;
            ulong coalescedDelta = snap.coalescedVideoFrames - lastCoalesced;
            lastDecoded = snap.decodedVideoFrames;
            lastPresented = snap.presentedVideoFrames;
            lastSwap = snap.swapChainPresents;
            lastDropped = snap.droppedVideoFrames;
            lastCoalesced = snap.coalescedVideoFrames;

            long elapsed = start.ElapsedMilliseconds / 1000;

            Console.WriteLine($"\n--- T+{elapsed}s ---");
            Console.WriteLine($"Decoded: {snap.decodedVideoFrames} (+{decodedDelta}/s) | Presented: {snap.presentedVideoFrames} (+{presentedDelta}/s) | Swap: {snap.swapChainPresents} (+{swapDelta}/s)");
            Console.WriteLine($"Dropped: +{droppedDelta}/s | Coalesced: +{coalescedDelta}/s | Queued: {snap.queuedVideoFrames} | Pos: {snap.position100ns / 1e7:F3}s");

            ulong dSw = Delta(snap.softwareConvert100ns, prevSwConvert);
            ulong dHw = Delta(snap.hardwareTransfer100ns, prevHwTransfer);
            ulong dUp = Delta(snap.videoUpload100ns, prevUpload);
            ulong dDl = Delta(snap.deviceLockWait100ns, prevDevLock);
            ulong dPw = Delta(snap.presentWait100ns, prevPresentWait);
            ulong dUpMap = Delta(snap.uploadMapWait100ns, prevUploadMap);
            ulong dUpCopy = Delta(snap.uploadCopy100ns, prevUploadCopy);
            ulong dResample = Delta(snap.downscaleResample100ns, prevResample);
            ulong dPasses = Delta(snap.scalePassCount, prevPasses);
            prevSwConvert = snap.softwareConvert100ns;
            prevHwTransfer = snap.hardwareTransfer100ns;
            prevUpload = snap.videoUpload100ns;
            prevDevLock = snap.deviceLockWait100ns;
            prevPresentWait = snap.presentWait100ns;
            prevUploadMap = snap.uploadMapWait100ns;
            prevUploadCopy = snap.uploadCopy100ns;
            prevResample = snap.downscaleResample100ns;
            prevPasses = snap.scalePassCount;

            if (presentedDelta > 0) {
                Console.WriteLine("Per-frame (last 1s):");
                Console.WriteLine($"  SW convert:  {dSw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  HW transfer: {dHw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Upload:      {dUp / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Device lock: {dDl / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Present wait:{dPw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  TOTAL/frame: {(dSw + dHw + dUp + dDl + dPw) / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Upload split: map(等GPU)={dUpMap / 1e4 / presentedDelta:F3} ms"
                    + $" copy(搬字节)={dUpCopy / 1e4 / presentedDelta:F3} ms"
                    + $" | 降档 resample={dResample / 1e4 / presentedDelta:F3} ms"
                    + $" | 缩放 pass/帧={dPasses / (double)presentedDelta:F2}");
                csv.AppendLine(string.Join(",", new string[] {
                    elapsed.ToString(),
                    decodedDelta.ToString(), presentedDelta.ToString(), swapDelta.ToString(),
                    droppedDelta.ToString(), coalescedDelta.ToString(),
                    snap.queuedVideoFrames.ToString(),
                    (dSw / 1e4 / presentedDelta).ToString("F3"),
                    (dHw / 1e4 / presentedDelta).ToString("F3"),
                    (dUp / 1e4 / presentedDelta).ToString("F3"),
                    (dDl / 1e4 / presentedDelta).ToString("F3"),
                    (dPw / 1e4 / presentedDelta).ToString("F3"),
                    ((dSw + dHw + dUp + dDl + dPw) / 1e4 / presentedDelta).ToString("F3"),
                    presentedDelta.ToString(),
                    (dUpMap / 1e4 / presentedDelta).ToString("F3"),
                    (dUpCopy / 1e4 / presentedDelta).ToString("F3"),
                    (dResample / 1e4 / presentedDelta).ToString("F3"),
                    (dPasses / (double)presentedDelta).ToString("F2"),
                    ((dSw + dHw + dUp + dDl + dPw + dResample) / 1e4 / presentedDelta).ToString("F3")
                }));
            }

            if (snap.state == FFF3FPState.Ended || elapsed >= limitSeconds) break;
        }

        Native.FFF3FP_GetSnapshot(player, ref snap);
        PrintSnapshot(snap, "最终快照");

        // 逐秒样本落盘：聚合各阶段耗时占比靠它，控制台只是给人看的。
        var csvPath = Environment.GetEnvironmentVariable("PERF_CSV_OUT");
        if (string.IsNullOrEmpty(csvPath))
            csvPath = Path.Combine(AppContext.BaseDirectory,
                "perftest_" + Path.GetFileNameWithoutExtension(path) + ".csv");
        File.WriteAllText(csvPath, csv.ToString());
        Console.WriteLine($"CSV: {csvPath}");

        Native.FFF3FP_Stop(player);
        Native.FFF3FP_Close(player);
        Native.FFF3FP_Destroy(player);
        Console.WriteLine("\n测试完成");
    }

    // 内核计数器在 ResetMedia / 设备重建时会被清零，直接相减会把 ulong 减出天文数字
    // （实测出现过 3e13 ms/帧）。差值一律饱和。
    static ulong Delta(ulong cur, ulong prev) { return cur >= prev ? cur - prev : 0ul; }

    static void PrintError(FFF3FPHandle h, string prefix) {
        var sb = new StringBuilder(1024);
        uint required = 0;
        Native.FFF3FP_GetLastError(h, sb, (uint)sb.Capacity, out required);
        string err = sb.ToString();
        if (string.IsNullOrEmpty(err) && required > 0) {
            sb.EnsureCapacity((int)required + 1);
            Native.FFF3FP_GetLastError(h, sb, (uint)sb.Capacity, out _);
            err = sb.ToString();
        }
        Console.Error.WriteLine($"{prefix}: {(string.IsNullOrEmpty(err) ? "(no error message)" : err)}");
    }

    static void PrintSnapshot(FFF3FPSnapshot s, string label) {
        Console.WriteLine($"\n=== {label} ===");
        Console.WriteLine($"State: {s.state} | DecodeMode: {s.decodeMode} | ColorMode: {s.actualColorMode}");
        Console.WriteLine($"Position: {s.position100ns / 1e7:F3}s / {s.duration100ns / 1e7:F3}s");
        Console.WriteLine($"Frame: {s.frameIndex} ({s.framePts}) | Queued: {s.queuedVideoFrames}");
        Console.WriteLine($"Decoded: {s.decodedVideoFrames} | Presented: {s.presentedVideoFrames} | Dropped: {s.droppedVideoFrames}");
        Console.WriteLine($"swapChainPresents: {s.swapChainPresents}");
        Console.WriteLine("--- Timing (100ns units) ---");
        Console.WriteLine($"  presentWait100ns:       {s.presentWait100ns} ({s.presentWait100ns / 1e4:F1} ms)");
        Console.WriteLine($"  deviceLockWait100ns:    {s.deviceLockWait100ns} ({s.deviceLockWait100ns / 1e4:F1} ms)");
        Console.WriteLine($"  hardwareTransfer100ns:  {s.hardwareTransfer100ns} ({s.hardwareTransfer100ns / 1e4:F1} ms)");
        Console.WriteLine($"  softwareConvert100ns:   {s.softwareConvert100ns} ({s.softwareConvert100ns / 1e4:F1} ms)");
        Console.WriteLine($"  videoUpload100ns:       {s.videoUpload100ns} ({s.videoUpload100ns / 1e4:F1} ms)");
        Console.WriteLine($"    ├ uploadMapWait100ns: {s.uploadMapWait100ns} ({s.uploadMapWait100ns / 1e4:F1} ms)  ← 等 GPU");
        Console.WriteLine($"    └ uploadCopy100ns:    {s.uploadCopy100ns} ({s.uploadCopy100ns / 1e4:F1} ms)  ← 搬字节");
        Console.WriteLine($"  downscaleResample100ns: {s.downscaleResample100ns} ({s.downscaleResample100ns / 1e4:F1} ms)");
        Console.WriteLine($"  scalePassCount:         {s.scalePassCount}"
            + (s.presentedVideoFrames > 0 ? $" ({(double)s.scalePassCount / s.presentedVideoFrames:F2}/帧)" : ""));
        if (s.swapChainPresents > 0) {
            double avgPw = (s.presentWait100ns / 1e4) / s.swapChainPresents;
            double avgDl = (s.deviceLockWait100ns / 1e4) / s.swapChainPresents;
            double avgHt = (s.hardwareTransfer100ns / 1e4) / s.swapChainPresents;
            double avgSc = (s.softwareConvert100ns / 1e4) / s.swapChainPresents;
            Console.WriteLine("--- Per-frame averages (ms) ---");
            Console.WriteLine($"  Present wait/frame:     {avgPw:F3} ms");
            Console.WriteLine($"  Device lock/frame:      {avgDl:F3} ms");
            Console.WriteLine($"  HW transfer/frame:      {avgHt:F3} ms");
            Console.WriteLine($"  SW convert/frame:       {avgSc:F3} ms");
            Console.WriteLine($"  Sum/frame:              {avgPw + avgDl + avgHt + avgSc:F3} ms");
        }
        Console.WriteLine($"Video: {s.videoWidth}x{s.videoHeight} | Bitrate: {s.videoBitRate / 1e6:F1} Mbps");
        Console.WriteLine($"HDR: format={s.hdrFormat} path={s.hdrProcessingPath} peak={s.sourcePeakNits} nits");
        if (!string.IsNullOrEmpty(lastEvent)) { Console.WriteLine(lastEvent); lastEvent = ""; }
    }
}