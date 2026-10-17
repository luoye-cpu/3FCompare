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
    public uint iamfActive;
    public uint iamfChannels;
    public int iamfSoundSystem;
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
    public uint iamfContentChannels;
    public uint iamfBinaural;
    public uint audioBinaural;
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

    static IntPtr CreateDummyWindow() {
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

        IntPtr hwnd = Win32.CreateWindowEx(0, "PerfTestWindowClass", "PerfTest", 
            Win32.WS_OVERLAPPEDWINDOW | 0x04000000 | 0x08000000, // WS_CLIPCHILDREN | WS_CLIPSIBLINGS
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, 640, 480,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
        Win32.ShowWindow(hwnd, 1); // SW_SHOWNORMAL
        Win32.UpdateWindow(hwnd);
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
        Console.WriteLine($"3FP 软解性能测试");
        Console.WriteLine($"文件: {path}");
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
        IntPtr hwnd = CreateDummyWindow();
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
            sdrScRgbMode = 0
        };

        FFFResult r = Native.FFF3FP_Create(ref cfg, out var player);
        if (r != 0) { PrintError(player, "Create"); return; }
        Console.WriteLine("Create 成功");

        r = Native.FFF3FP_Open(player, path);
        if (r != 0) { PrintError(player, "Open"); return; }
        Console.WriteLine("Open 成功");

        uint snapSize = (uint)Marshal.SizeOf<FFF3FPSnapshot>();
        Console.WriteLine($"Snapshot size: {snapSize}");
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
        ulong prevSwConvert = 0, prevHwTransfer = 0, prevDevLock = 0, prevPresentWait = 0;

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
            lastDecoded = snap.decodedVideoFrames;
            lastPresented = snap.presentedVideoFrames;
            lastSwap = snap.swapChainPresents;

            long elapsed = start.ElapsedMilliseconds / 1000;

            Console.WriteLine($"\n--- T+{elapsed}s ---");
            Console.WriteLine($"Decoded: {snap.decodedVideoFrames} (+{decodedDelta}/s) | Presented: {snap.presentedVideoFrames} (+{presentedDelta}/s) | Swap: {snap.swapChainPresents} (+{swapDelta}/s)");
            Console.WriteLine($"Queued: {snap.queuedVideoFrames} | Pos: {snap.position100ns / 1e7:F3}s");

            ulong dSw = snap.softwareConvert100ns - prevSwConvert;
            ulong dHw = snap.hardwareTransfer100ns - prevHwTransfer;
            ulong dDl = snap.deviceLockWait100ns - prevDevLock;
            ulong dPw = snap.presentWait100ns - prevPresentWait;
            prevSwConvert = snap.softwareConvert100ns;
            prevHwTransfer = snap.hardwareTransfer100ns;
            prevDevLock = snap.deviceLockWait100ns;
            prevPresentWait = snap.presentWait100ns;

            if (presentedDelta > 0) {
                Console.WriteLine("Per-frame (last 1s):");
                Console.WriteLine($"  SW convert:  {dSw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  HW transfer: {dHw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Device lock: {dDl / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  Present wait: {dPw / 1e4 / presentedDelta:F3} ms");
                Console.WriteLine($"  TOTAL/frame: {(dSw + dHw + dDl + dPw) / 1e4 / presentedDelta:F3} ms");
            }

            if (snap.state == FFF3FPState.Ended || elapsed >= 30) break;
        }

        Native.FFF3FP_GetSnapshot(player, ref snap);
        PrintSnapshot(snap, "最终快照");

        Native.FFF3FP_Stop(player);
        Native.FFF3FP_Close(player);
        Native.FFF3FP_Destroy(player);
        Console.WriteLine("\n测试完成");
    }

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