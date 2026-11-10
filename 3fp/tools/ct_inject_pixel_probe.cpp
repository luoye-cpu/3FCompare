// 色彩变换模块自带注入泵的实机像素级验证（2026-11-02）。
//
// 被测对象不是"模块的数学"，而是**模块自己那条泵**：
//   FFF_GetInjectionApi().start(内核会话句柄) 之后，模块线程是否真的
//   从内核环形缓冲区取到逐帧侧数据、烘出表、按帧索引把表投回内核，
//   以及那张表是否真的落到了像素上。
//
// 判据（顺序即因果顺序，前面不成立时后面的差值不可归因）：
//   1. 噪声地板：同一臂暂停后连读两遍必须逐字节相同。
//   2. 泵活着：running=1、framesInjected 随播放增长、lastFrameIndex 前进、
//      contentText 非空（模块自己的措辞，程序不懂它的含义）。
//   3. 表到像素：B 臂（泵开）与 A 臂（泵关）像素必须不同。
//   4. 可逆：停泵 + ClearDynamicMetadata(kind 3) + 关掉合成请求之后，
//      像素必须回到 A 臂（否则差值来自别的状态残留，不是表）。
//   5. 消费判定：内核自己的 HDR 诊断里 dynamicCurveFramesApplied/Refused
//      必须与"像素变没变"一致。Refused 增长而像素不变 ⇒ 表被内核**有意扣留**
//      （合成请求已生效但这一帧没有配上辅助层），这是"请求到了、画面没合成"，
//      与"泵没在工作"是两件事，必须分开报。
//
// 为什么用两套素材（同目录 P7_FEL_12bit_set2，两份都是时间静止的渐变）：
//   · P8（无增强层）：模块不会请求合成 ⇒ 表应真正采样 ⇒ 判据 3 成立。
//   · P7（有增强层）：模块会请求合成 ⇒ 若内核配不上辅助层，表被扣留 ⇒
//     判据 3 不成立、判据 5 的 Refused 增长。两者一起才说明"表通了，
//     残差那条路还没通"，而不是含糊的"没变化"。
//
// 用法：ct_inject_pixel_probe <媒体> --module <DLL> [--frame N] [--decode cpu|gpu]
//                               [--perf-seconds S] [--dump DIR]
// 退出码只表示"这一轮跑没跑成"，判定看输出（到货验收的老规矩）。

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

// ── 模块 ABI 的镜像 ────────────────────────────────────────────────────────
// 只镜像本探针用到的头部字段；`size` 一律按镜像结构自身的大小填，模块若
// 认为不够/不对会返 TooSmall / InvalidArgument，探针把它原样打出来 ——
// 也就是说镜像错了不会静默通过，而是当场报错。
struct MirrorTransformInput {
    std::uint32_t size;
    std::uint32_t version;
    const void* metadata;
    std::uint64_t metadataSize;
    std::uint64_t frameIndex;
    const void* configRecord;
    std::uint64_t configRecordSize;
    std::uint32_t flags;
    std::uint32_t reserved;
};
struct MirrorTransformOutput {
    std::uint32_t size;
    std::uint32_t payloadKind;
    std::uint32_t payloadKindVersion;
    std::uint32_t payloadSize;
    void* payload;
    std::uint32_t payloadCapacity;
    std::uint32_t reserved;
};
struct MirrorTransformApi {
    std::uint32_t size;
    std::uint32_t version;
    std::uint32_t(__cdecl* transformVersion)();
    int(__cdecl* analyzeFrame)(const MirrorTransformInput*, MirrorTransformOutput*);
    void(__cdecl* reset)();
    void(__cdecl* setDisplayTargetNits)(float);
};
struct MirrorInjectionApi {
    std::uint32_t size;
    std::uint32_t version;
    int(__cdecl* start)(void* kernelHandle);
    void(__cdecl* stop)();
    void(__cdecl* readStatus)(void* status);
};
struct MirrorInjectionStatus {
    std::uint32_t size;
    std::uint32_t running;
    std::uint64_t framesInjected;
    std::uint64_t framesUnchanged;
    std::uint64_t framesSkipped;
    std::uint64_t framesNoMetadata;
    std::int64_t lastFrameIndex;
    float lastFramePeakNits;
    char contentText[64];
    char compositionText[64];
};

constexpr std::uint32_t kTransformVersion = 3;   // 模块面 ABI 版本（表 40 字节）
constexpr std::uint32_t kInputVersion = 3;       // 输入结构版本，模块要求精确相等
constexpr std::uint32_t kInjectionVersion = 1;
constexpr std::uint32_t kKindColourTransform = 3;  // ExternalColorTransform
constexpr std::uint32_t kFrameSideDataContentMetadata = 24;
constexpr std::uint32_t kStreamSideDataConfigRecord = 29;
constexpr std::uint32_t kMetadataCapacity = 1u << 20;
constexpr std::uint32_t kConfigCapacity = 256;
constexpr std::uint32_t kPayloadCapacity = 431296;
constexpr std::uint32_t kStatusesVersion = 21;

bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
}
void __cdecl LogCallback(void*, const char* line) noexcept {
    if (line != nullptr && std::strstr(line, "[el]") != nullptr) printf("[kernel] %s\n", line);
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(1);
    }
}
std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    return out;
}
double CpuSeconds() {
    FILETIME create{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &create, &exit, &kernel, &user)) return 0.0;
    auto toSeconds = [](const FILETIME& time) {
        ULARGE_INTEGER value{};
        value.LowPart = time.dwLowDateTime;
        value.HighPart = time.dwHighDateTime;
        return static_cast<double>(value.QuadPart) / 1e7;
    };
    return toSeconds(kernel) + toSeconds(user);
}

struct Band {
    double mean[15];
    double minimum[15];
    double maximum[15];
    std::vector<float> raw;   // 便于落盘
    std::uint32_t width = 0, height = 0, bitDepth = 0;
    bool ok = false;
};

// 读一条横带（交换链坐标），再按 15 等分取每格均值/极值。
// 素材是横向渐变，所以"每格均值"就是这条带的一维指纹；逐字节比较用 raw。
Band ReadBand(FFF3FPHandle player, const std::uint32_t y, const std::uint32_t height,
    const std::uint32_t width) {
    Band band;
    band.width = width;
    band.height = height;
    band.raw.assign(static_cast<std::size_t>(width) * height * 4u, 0.0f);
    if (FFF3FP_ReadVideoPixelRegion(player, 0, y, width, height, band.raw.data(),
            static_cast<std::uint32_t>(band.raw.size()), &band.bitDepth) != FFFResult::Success)
        return band;
    band.ok = true;
    for (auto cell = 0; cell < 15; ++cell) {
        const auto from = width * static_cast<std::uint32_t>(cell) / 15u;
        const auto to = width * static_cast<std::uint32_t>(cell + 1) / 15u;
        double sum = 0.0, low = 1e9, high = -1e9;
        std::size_t count = 0;
        for (std::uint32_t row = 0; row < height; ++row) {
            for (auto x = from; x < to; ++x) {
                const auto value = band.raw[(static_cast<std::size_t>(row) * width + x) * 4u];
                sum += value;
                low = (std::min)(low, static_cast<double>(value));
                high = (std::max)(high, static_cast<double>(value));
                ++count;
            }
        }
        band.mean[cell] = count ? sum / static_cast<double>(count) : 0.0;
        band.minimum[cell] = count ? low : 0.0;
        band.maximum[cell] = count ? high : 0.0;
    }
    return band;
}

// 回读宽度必须先探一次：ReadVideoPixelRegion 只认交换链坐标，窗口客户区在
// 不同 DPI 下宽度会变，写死 1600 会在读回时被判 InvalidArgument 而不是报错
// 到别处。三档递减，取第一个成功的宽度并在所有臂里复用（换宽度会让指纹不可比）。
std::uint32_t ResolveBandWidth(FFF3FPHandle player) {
    for (const std::uint32_t candidate : {1600u, 1280u, 1024u}) {
        Band probe = ReadBand(player, 8u, 8u, candidate);
        if (probe.ok) return candidate;
    }
    return 0;
}

bool Identical(const Band& a, const Band& b) {    return a.ok && b.ok && a.raw.size() == b.raw.size() &&
        std::memcmp(a.raw.data(), b.raw.data(), a.raw.size() * sizeof(float)) == 0;
}

void PrintBand(const char* label, const Band& band) {
    if (!band.ok) { printf("  %s: 读回失败\n", label); return; }
    printf("  %-14s bits=%u mean =", label, band.bitDepth);
    for (auto cell = 0; cell < 15; ++cell) printf(" %6.4f", band.mean[cell]);
    printf("\n");
}

// 两臂之间的"指纹差"：15 格均值的最大绝对差 + 全带最大样本差。
void CompareBands(const char* label, const Band& a, const Band& b) {
    if (!a.ok || !b.ok) { printf("  %s: 不可比（读回失败）\n", label); return; }
    double meanMax = 0.0;
    for (auto cell = 0; cell < 15; ++cell)
        meanMax = (std::max)(meanMax, std::fabs(a.mean[cell] - b.mean[cell]));
    double sampleMax = 0.0;
    for (std::size_t i = 0; i < a.raw.size(); ++i)
        sampleMax = (std::max)(sampleMax, std::fabs(static_cast<double>(a.raw[i]) - b.raw[i]));
    printf("  %s: 格均值最大差=%.4f  样本最大差=%.4f  → %s\n", label, meanMax, sampleMax,
        sampleMax > 0.0 ? "不同" : "逐字节相同");
}

struct KernelDiag {
    std::uint64_t applied = 0, refused = 0;
    std::uint32_t swapHdr = 0;
};
KernelDiag ReadDiag(FFF3FPHandle player) {
    KernelDiag diag;
    FFF3FPHdrSignalDiagnostics diagnostics{};
    diagnostics.size = sizeof(diagnostics);
    diagnostics.version = 20;
    if (FFF3FP_GetHdrSignalDiagnostics(player, &diagnostics) != FFFResult::Success) return diag;
    diag.applied = diagnostics.dynamicCurveFramesApplied;
    diag.refused = diagnostics.dynamicCurveFramesRefused;
    diag.swapHdr = diagnostics.swapChainHdrEnabled;
    return diag;
}

MirrorInjectionStatus ReadStatus(const MirrorInjectionApi* injection) {
    MirrorInjectionStatus status{};
    status.size = sizeof(status);
    if (injection != nullptr) injection->readStatus(&status);
    return status;
}

// 进程私有字节数。用 kernel32 导出的 K32GetProcessMemoryInfo，避免为 psapi.lib
// 再改一次链接脚本；拿不到就返 0，调用处按"没量到"处理。
std::size_t PrivateBytes() {
    struct Counters {
        std::uint32_t cb;
        std::uint32_t faultCount;
        std::size_t peakWorkingSet, workingSet, peakPagedPool, pagedPool, peakNonPaged, nonPaged,
            pagefile, peakPagefile, privateBytes, peakPrivate, virtualSize;
    };
    using Query = BOOL(WINAPI*)(HANDLE, void*, std::uint32_t);
    static const auto query = []() -> Query {
        const auto kernel = GetModuleHandleW(L"kernel32.dll");
        return kernel ? reinterpret_cast<Query>(
            reinterpret_cast<void*>(GetProcAddress(kernel, "K32GetProcessMemoryInfo"))) : nullptr;
    }();
    if (query == nullptr) return 0;
    Counters counters{};
    counters.cb = sizeof(counters);
    if (!query(GetCurrentProcess(), &counters, sizeof(counters))) return 0;
    return counters.privateBytes;
}

void PrintStatus(const MirrorInjectionApi* injection, const char* label) {
    if (injection == nullptr) { printf("  [%s] 无注入面\n", label); return; }
    MirrorInjectionStatus status{};
    status.size = sizeof(status);
    injection->readStatus(&status);
    printf("  [%s] 模块报 size=%u (镜像 %zu) running=%u injected=%llu unchanged=%llu "
           "skipped=%llu noMeta=%llu lastFrame=%lld peak=%.0f\n",
        label, status.size, sizeof(MirrorInjectionStatus), status.running,
        static_cast<unsigned long long>(status.framesInjected),
        static_cast<unsigned long long>(status.framesUnchanged),
        static_cast<unsigned long long>(status.framesSkipped),
        static_cast<unsigned long long>(status.framesNoMetadata),
        static_cast<long long>(status.lastFrameIndex), status.lastFramePeakNits);
    printf("  [%s] 内容=\"%.63s\"  合成=\"%.63s\"\n", label, status.contentText, status.compositionText);
}

void DumpBand(const std::wstring& dir, const wchar_t* name, const Band& band) {
    if (dir.empty() || !band.ok) return;
    const auto path = std::filesystem::path(dir) / name;
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    out.write(reinterpret_cast<const char*>(band.raw.data()),
        static_cast<std::streamsize>(band.raw.size() * sizeof(float)));
    out.close();
    auto meta = std::wstring(path.wstring()) + L".meta";
    std::ofstream metaOut(meta);
    metaOut << "width=" << band.width << "\nheight=" << band.height
            << "\nbitDepth=" << band.bitDepth << "\nlayout=rgba_float32_rowmajor_topdown\n";
}

struct PerfReading {
    double wallSeconds = 0.0;
    double cpuSeconds = 0.0;
    std::uint64_t decoded = 0, presented = 0;
    std::uint64_t dropped = 0, presents = 0;
};

// 播放固定时长，量这段的墙上时间、进程 CPU 时间与解码/呈现帧数。
// 只用进程 CPU 差值：注入泵是同一进程里的第二个线程，它的开销就落在这里。
PerfReading PlayFor(FFF3FPHandle player, const int seconds) {
    PerfReading reading;
    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = kStatusesVersion;
    FFF3FP_GetSnapshot(player, &before);
    const auto wall0 = GetTickCount64();
    const auto cpu0 = CpuSeconds();
    Pump(seconds * 1000);
    const auto wall1 = GetTickCount64();
    const auto cpu1 = CpuSeconds();
    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = kStatusesVersion;
    FFF3FP_GetSnapshot(player, &after);
    reading.wallSeconds = static_cast<double>(wall1 - wall0) / 1000.0;
    reading.cpuSeconds = cpu1 - cpu0;
    reading.decoded = after.decodedVideoFrames - before.decodedVideoFrames;
    reading.presented = after.presentedVideoFrames - before.presentedVideoFrames;
    reading.dropped = after.droppedVideoFrames - before.droppedVideoFrames;
    reading.presents = after.swapChainPresents - before.swapChainPresents;
    return reading;
}

void PrintPerf(const char* label, const PerfReading& reading) {
    printf("  %-8s 墙上=%.2fs CPU=%.3fs 平均占用=%.3f 核 | 解码=%llu (%.2f fps) "
           "呈现=%llu (%.2f fps) 丢帧=%llu 交换链提交=%llu\n",
        label, reading.wallSeconds, reading.cpuSeconds,
        reading.wallSeconds > 0 ? reading.cpuSeconds / reading.wallSeconds : 0.0,
        static_cast<unsigned long long>(reading.decoded),
        reading.wallSeconds > 0 ? reading.decoded / reading.wallSeconds : 0.0,
        static_cast<unsigned long long>(reading.presented),
        reading.wallSeconds > 0 ? reading.presented / reading.wallSeconds : 0.0,
        static_cast<unsigned long long>(reading.dropped),
        static_cast<unsigned long long>(reading.presents));
}

}  // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        printf("用法: ct_inject_pixel_probe <媒体> --module <DLL> [--frame N] [--decode cpu|gpu]\n"
               "                              [--perf-seconds S] [--dump DIR]\n"
               "                              [--color sdr|hdr|raw] [--scrgb 0|1] [--no-pump-perf]\n"
               "                              [--d-play]  (D 臂投表后播放一段再暂停)\n");
        return 2;
    }
    std::wstring modulePath;
    std::wstring dumpDir;
    long long frameIndex = 48;
    bool hardwareDecode = true;
    int perfSeconds = 8;
    FFF3FPColorMode colorMode = FFF3FPColorMode::MapToHdr;
    std::uint32_t sdrScRgbMode = 1;
    bool runPerf = true;
    bool dPlayAfterPost = false;
    for (int i = 2; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--module" && i + 1 < argc) modulePath = argv[++i];
        else if (arg == L"--dump" && i + 1 < argc) dumpDir = argv[++i];
        else if (arg == L"--frame" && i + 1 < argc) frameIndex = _wtoi64(argv[++i]);
        else if (arg == L"--perf-seconds" && i + 1 < argc) perfSeconds = _wtoi(argv[++i]);
        else if (arg == L"--decode" && i + 1 < argc)
            hardwareDecode = std::wstring(argv[++i]) != L"cpu";
        else if (arg == L"--color" && i + 1 < argc) {
            const std::wstring value = argv[++i];
            colorMode = value == L"sdr" ? FFF3FPColorMode::MapToSdr
                : (value == L"raw" ? FFF3FPColorMode::RawHdrAsSdr : FFF3FPColorMode::MapToHdr);
        } else if (arg == L"--scrgb" && i + 1 < argc) sdrScRgbMode = _wtoi(argv[++i]) ? 1u : 0u;
        else if (arg == L"--no-pump-perf") runPerf = false;
        else if (arg == L"--d-play") dPlayAfterPost = true;
    }
    if (modulePath.empty()) { printf("必须给 --module <FFF.HDRdp.Test.dll>\n"); return 2; }
    const auto media = Utf8(argv[1]);

    // 内核自己的辅助层配对日志（stderr），用来分辨"请求发出但没配上"。
    SetEnvironmentVariableW(L"FFF_EL_TRACE", L"1");

    printf("== 加载模块 ==\n");
    const HMODULE module = ::LoadLibraryExW(modulePath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr) {
        printf("  LoadLibrary 失败 err=%lu\n", GetLastError());
        return 1;
    }
    printf("  已装载 %ls\n", modulePath.c_str());
    const auto authStatus = reinterpret_cast<std::uint32_t(__cdecl*)()>(
        ::GetProcAddress(module, "FFF_ColorTransformAuthorizationStatus"));
    if (authStatus != nullptr) printf("  授权状态=%u (2=已解锁)\n", authStatus());
    MirrorTransformApi transform{};
    transform.size = sizeof(transform);
    const auto getTransform = reinterpret_cast<int(__cdecl*)(std::uint32_t, MirrorTransformApi*)>(
        ::GetProcAddress(module, "FFF_GetColorTransformApi"));
    if (getTransform == nullptr || getTransform(kTransformVersion, &transform) != 0) {
        printf("  模块面不可用（未解锁？）—— 泵会一直空转，本轮无意义\n");
        ::FreeLibrary(module);
        return 1;
    }
    printf("  analyzeFrame=%p  payload 版本=%u\n",
        reinterpret_cast<const void*>(transform.analyzeFrame),
        transform.transformVersion ? transform.transformVersion() : 0u);
    MirrorInjectionApi injection{};
    injection.size = sizeof(injection);
    const auto getInjection = reinterpret_cast<int(__cdecl*)(std::uint32_t, MirrorInjectionApi*)>(
        ::GetProcAddress(module, "FFF_GetInjectionApi"));
    if (getInjection == nullptr || getInjection(kInjectionVersion, &injection) != 0) {
        printf("  注入面不可用 —— 无法测泵\n");
        ::FreeLibrary(module);
        return 1;
    }
    printf("  注入面 version=%u start=%p\n", injection.version,
        reinterpret_cast<const void*>(injection.start));

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFCtInjectPixelWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFCtInjectPixelWnd", L"ct-inject", WS_OVERLAPPEDWINDOW,
        20, 20, 1616, 900, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = hardwareDecode ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = colorMode;
    configuration.sdrScRgbMode = sdrScRgbMode;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); ::FreeLibrary(module); return 1;
    }
    FFF3FP_SetLogCallback(&LogCallback, nullptr);
    if (FFF3FP_Open(player, media.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); ::FreeLibrary(module); return 1;
    }
    for (int i = 0; i < 300 && !g_open; ++i) Pump(50);
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = kStatusesVersion;
    FFF3FP_GetSnapshot(player, &snapshot);
    printf("\n== 会话 ==\n  媒体=%s\n  源=%dx%u  解码=%s  实际色彩=%d\n  请求色彩=%s scRGB=%u\n", media.c_str(),
        snapshot.videoWidth, snapshot.videoHeight, hardwareDecode ? "D3D11" : "CPU",
        static_cast<int>(snapshot.actualColorMode),
        colorMode == FFF3FPColorMode::MapToSdr ? "MapToSdr"
            : (colorMode == FFF3FPColorMode::RawHdrAsSdr ? "RawHdrAsSdr" : "MapToHdr"),
        sdrScRgbMode);
    if (snapshot.videoWidth == 0) { printf("无尺寸信息\n"); FFF3FP_Destroy(player); return 1; }

    const auto targetY = 300u;
    const auto stripHeight = 48u;
    // 播放一帧之后才可能有可抓画面（hasCachedVideo_ 要等首次呈现）。
    FFF3FP_SeekFrame(player, frameIndex);
    FFF3FP_Play(player);
    Pump(1200);
    FFF3FP_Pause(player);
    Pump(400);
    RECT client{};
    GetClientRect(window, &client);
    const auto bandWidth = ResolveBandWidth(player);
    printf("\n== 采样几何 ==\n  客户区=%ldx%ld  回读宽度=%u  带 y=%u 高=%u\n",
        client.right - client.left, client.bottom - client.top, bandWidth, targetY, stripHeight);
    if (bandWidth == 0) {
        printf("像素回读完全失败 —— 不判\n");
        FFF3FP_Destroy(player); ::FreeLibrary(module); return 6;
    }
    // 画面概览：整帧 12x8 格均值，用来证明"读到的确实是那张画面"以及
    // 采样带落在画面的哪一部分。没有这一行，后面的差值无法排除"读错了地方"。
    {
        const auto overviewHeight = static_cast<std::uint32_t>(
            (client.bottom - client.top) > 0 ? (client.bottom - client.top) : 861);
        Band overview = ReadBand(player, 0, overviewHeight, bandWidth);
        printf("\n== 画面概览（整帧 8 行 x 12 列，R 通道均值）==\n");
        if (!overview.ok) printf("  概览读回失败\n");
        else {
            for (int row = 0; row < 8; ++row, printf("\n")) {
                printf("  ");
                for (int column = 0; column < 12; ++column) {
                    const auto x0 = overview.width * static_cast<std::uint32_t>(column) / 12u;
                    const auto x1 = overview.width * static_cast<std::uint32_t>(column + 1) / 12u;
                    const auto y0 = overview.height * static_cast<std::uint32_t>(row) / 8u;
                    const auto y1 = overview.height * static_cast<std::uint32_t>(row + 1) / 8u;
                    double sum = 0.0;
                    std::size_t count = 0;
                    for (auto y = y0; y < y1; ++y)
                        for (auto x = x0; x < x1; ++x) {
                            sum += overview.raw[(static_cast<std::size_t>(y) * overview.width + x) * 4u];
                            ++count;
                        }
                    printf(" %6.4f", count ? sum / static_cast<double>(count) : 0.0);
                }
            }
        }
        DumpBand(dumpDir, L"overview.f32", overview);
    }

    // ── 臂 A：泵未启动（基线）────────────────────────────────────────────
    printf("\n== 臂 A：泵未启动 ==\n");
    FFF3FP_SeekFrame(player, frameIndex);
    FFF3FP_Play(player);
    Pump(1800);
    FFF3FP_Pause(player);
    Pump(700);
    const auto a1 = ReadBand(player, targetY, stripHeight, bandWidth);
    Pump(400);
    const auto a2 = ReadBand(player, targetY, stripHeight, bandWidth);
    PrintBand("A", a1);
    PrintStatus(&injection, "A");
    const auto diagA = ReadDiag(player);
    printf("  内核诊断: applied=%llu refused=%llu swapHdr=%u\n",
        static_cast<unsigned long long>(diagA.applied),
        static_cast<unsigned long long>(diagA.refused), diagA.swapHdr);
    DumpBand(dumpDir, L"arm_a.f32", a1);
    const bool floorA = Identical(a1, a2);
    printf("  噪声地板 A: %s\n", floorA ? "两遍逐字节相同" : "两遍不同（地板不成立!）");

    // ── 臂 B：泵开 ────────────────────────────────────────────────────────
    printf("\n== 臂 B：泵开（模块自己取侧数据/烘表/投表）==\n");
    const int started = injection.start(player);
    printf("  start() = %d (0=已启动)\n", started);
    // 先把一段时间的帧喂给泵：环里的 blob 只有呈现过才可读。
    FFF3FP_SeekFrame(player, frameIndex > 60 ? frameIndex - 60 : 0);
    FFF3FP_Play(player);
    Pump(3000);
    FFF3FP_Pause(player);
    Pump(900);
    const auto b1 = ReadBand(player, targetY, stripHeight, bandWidth);
    Pump(400);
    const auto b2 = ReadBand(player, targetY, stripHeight, bandWidth);
    PrintBand("B", b1);
    PrintStatus(&injection, "B");
    const auto diagB = ReadDiag(player);
    printf("  内核诊断: applied=%llu (+%llu) refused=%llu (+%llu) swapHdr=%u\n",
        static_cast<unsigned long long>(diagB.applied),
        static_cast<unsigned long long>(diagB.applied - diagA.applied),
        static_cast<unsigned long long>(diagB.refused),
        static_cast<unsigned long long>(diagB.refused - diagA.refused), diagB.swapHdr);
    DumpBand(dumpDir, L"arm_b.f32", b1);
    const bool floorB = Identical(b1, b2);
    printf("  噪声地板 B: %s\n", floorB ? "两遍逐字节相同" : "两遍不同（地板不成立!）");

    // ── 臂 C：停泵 + 撤表 + 关合成请求（可逆性）───────────────────────────
    printf("\n== 臂 C：停泵、撤表、关合成请求（可逆性）==\n");
    injection.stop();
    FFF3FP_ClearDynamicMetadata(player, kKindColourTransform);
    {
        FFF3FPAuxiliaryComposition off{};
        off.size = sizeof(off);
        off.version = FFF3FP_AUXILIARY_COMPOSITION_VERSION;
        off.frameIndex = frameIndex;
        off.enabled = 0;
        FFF3FP_SetAuxiliaryComposition(player, &off);
    }
    // 再 seek 一次：内核的请求环是"取索引不晚于当前帧的最后一条，索引早于全部
    // 条目时取第一条"。只投 enabled=0 而不动轨道位置，环里那条 enabled=1 会在
    // 回跳后重新被选中（本轮实测过：不 seek 时 D 臂的 refused 仍在涨）。seek 走
    // ClearVideoQueue → FlushAuxiliaryLayer 把环清空，状态才是真正的"无请求"。
    FFF3FP_SeekFrame(player, frameIndex);
    FFF3FP_Play(player);
    Pump(1000);
    FFF3FP_Pause(player);
    Pump(600);
    const auto c1 = ReadBand(player, targetY, stripHeight, bandWidth);
    PrintBand("C", c1);
    PrintStatus(&injection, "C");
    const auto diagC = ReadDiag(player);
    printf("  内核诊断: applied=%llu (+%llu) refused=%llu (+%llu)\n",
        static_cast<unsigned long long>(diagC.applied),
        static_cast<unsigned long long>(diagC.applied - diagB.applied),
        static_cast<unsigned long long>(diagC.refused),
        static_cast<unsigned long long>(diagC.refused - diagB.refused));
    DumpBand(dumpDir, L"arm_c.f32", c1);

    // ── 单帧烘焙开销 + 载荷捕获（与泵同一调用、同一份侧数据）─────────────
    printf("\n== 烘焙开销（analyzeFrame 单帧）==\n");
    std::vector<std::uint8_t> metadata(kMetadataCapacity);
    std::vector<std::uint8_t> config(kConfigCapacity);
    std::vector<std::uint8_t> payload(kPayloadCapacity);
    std::uint32_t metadataSize = 0, configSize = 0, payloadSize = 0;
    std::uint32_t payloadFlags = 0;
    double bakeAverageMicros = 0.0;
    {
        std::int64_t found = -1;
        FFF3FP_CopyFrameSideDataAfter(player, kFrameSideDataContentMetadata, 0, metadata.data(),
            kMetadataCapacity, &metadataSize, &found);
        FFF3FP_CopyStreamSideData(player, kStreamSideDataConfigRecord, config.data(),
            kConfigCapacity, &configSize);
        printf("  侧数据: 帧内 blob=%u 字节（首帧 index=%lld）  流级记录=%u 字节\n",
            metadataSize, static_cast<long long>(found), configSize);
        if (configSize > 0) {
            printf("  流级记录前 16 字节:");
            for (std::uint32_t i = 0; i < configSize && i < 16; ++i) printf(" %02X", config[i]);
            printf("\n");
        }
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        // 两个旗标各烘一轮：0 = 没有"已预合成"承诺（模块应当拒绝给 FEL 内容烘表），
        // 1 = 承诺已预合成（泵在残差请求生效时用的就是这一档）。两行的差别本身
        // 就是模块安全属性的一条证据。
        for (const std::uint32_t flags : {0u, 1u}) {
            MirrorTransformInput in{};
            in.size = sizeof(in);
            in.version = kInputVersion;
            in.metadata = metadata.data();
            in.metadataSize = metadataSize;
            in.configRecord = configSize ? config.data() : nullptr;
            in.configRecordSize = configSize;
            in.flags = flags;
            constexpr int kIterations = 300;
            double best = 1e9, total = 0.0, worst = 0.0;
            int code = 0;
            std::uint32_t produced = 0;
            for (int i = 0; i < kIterations; ++i) {
                MirrorTransformOutput out{};
                out.size = sizeof(out);
                out.payload = payload.data();
                out.payloadCapacity = kPayloadCapacity;
                LARGE_INTEGER start{}, finish{};
                QueryPerformanceCounter(&start);
                code = transform.analyzeFrame(&in, &out);
                QueryPerformanceCounter(&finish);
                const auto microseconds = (finish.QuadPart - start.QuadPart) * 1e6 / frequency.QuadPart;
                best = (std::min)(best, microseconds);
                worst = (std::max)(worst, microseconds);
                total += microseconds;
                produced = out.payloadSize;
            }
            const auto average = total / kIterations;
            printf("  flags=%u: 返回码=%d 载荷=%u 字节 单帧 最小=%.1f 平均=%.1f 最大=%.1f 微秒"
                   " → 24fps 占 %.3f%% / 60fps 占 %.3f%%\n",
                flags, code, produced, best, average, worst,
                100.0 * average / 41667.0, 100.0 * average / 16667.0);
            if (code == 0 && produced > 0 && payloadSize == 0) {
                payloadSize = produced;
                payloadFlags = flags;
                bakeAverageMicros = average;
            }
        }
        // 泵在真实影片上每帧要做的第二件事：把新表投进内核。内核收到投表后会
        // 立刻 enqueue 一次重绘（SetDynamicMetadata → Redraw，异步），所以这里
        // 量到的是"泵线程上每帧多出来的那段"，不含呈现线程的实际提交。
        if (payloadSize > 0) {
            FFF3FPDynamicMetadataPacket packet{};
            packet.size = sizeof(packet);
            packet.version = 1;
            packet.kind = kKindColourTransform;
            packet.kindVersion = 1;
            packet.frameIndex = 0;
            packet.payloadSize = payloadSize;
            packet.payload = payload.data();
            constexpr int kPosts = 100;
            double worst = 0.0, total = 0.0;
            for (int i = 0; i < kPosts; ++i) {
                const auto start = GetTickCount64();
                LARGE_INTEGER before{}, after{};
                QueryPerformanceCounter(&before);
                FFF3FP_SetDynamicMetadata(player, &packet, 1);
                QueryPerformanceCounter(&after);
                (void)start;
                const auto microseconds = (after.QuadPart - before.QuadPart) * 1e6 / frequency.QuadPart;
                total += microseconds;
                worst = (std::max)(worst, microseconds);
            }
            printf("  投表(SetDynamicMetadata+enqueue 重绘) 每次平均=%.1f 最大=%.0f 微秒 → "
                   "24fps 占 %.3f%%\n", total / kPosts, worst, 100.0 * (total / kPosts) / 41667.0);
            printf("  泵线程每帧自有开销上限 ≈ 烘焙 %.0f + 投表 %.0f = %.0f 微秒 (%.2f%% of 41667)\n",
                bakeAverageMicros, total / kPosts, bakeAverageMicros + total / kPosts,
                100.0 * (bakeAverageMicros + total / kPosts) / 41667.0);
            FFF3FP_ClearDynamicMetadata(player, kKindColourTransform);
        }
    }

    // ── 臂 D：探针按模块烘出的同一份载荷自己投表（绕过泵）─────────────────
    // 目的只有一个：把"泵投的表"和"任何人投这份表"分开。若 D 与 B 像素完全不同，
    // 说明 B 的差异不能由这份载荷解释，前面所有归因都要作废。
    printf("\n== 臂 D：同一份载荷由探针直接投表（绕过泵）==\n");
    Band d1;
    if (payloadSize == 0) {
        printf("  没有可投的载荷（模块对这份侧数据拒绝烘表）—— 本臂跳过\n");
    } else {
        FFF3FPDynamicMetadataPacket packet{};
        packet.size = sizeof(packet);
        packet.version = 1;
        packet.kind = kKindColourTransform;
        packet.kindVersion = 1;
        packet.frameIndex = 0;
        packet.payloadSize = payloadSize;
        packet.payload = payload.data();
        const auto posted = FFF3FP_SetDynamicMetadata(player, &packet, 1);
        printf("  投表(frames=0, flags=%u, 载荷=%u 字节) = %d  FFFResult::Success=%d\n",
            payloadFlags, payloadSize, static_cast<int>(posted), static_cast<int>(FFFResult::Success));
        FFF3FP_Redraw(player);
        if (dPlayAfterPost) {
            // SDR 呈现链的常量缓冲只属于呈现路径（RefreshHdrShaderInputs 的锁
            // 约束）：暂停时投表后若不再呈现一帧，表可能只落了一半。这一段
            // 用来分辨"表没生效"和"生效时机不同"。
            FFF3FP_SeekFrame(player, frameIndex);
            FFF3FP_Play(player);
            Pump(800);
            FFF3FP_Pause(player);
            Pump(500);
            printf("  (--d-play) 投表后已重新播放并暂停在帧 %lld\n", static_cast<long long>(frameIndex));
        }
        Pump(900);
        d1 = ReadBand(player, targetY, stripHeight, bandWidth);
        PrintBand("D", d1);
        const auto diagD = ReadDiag(player);
        printf("  内核诊断: applied=%llu (+%llu) refused=%llu (+%llu)\n",
            static_cast<unsigned long long>(diagD.applied),
            static_cast<unsigned long long>(diagD.applied - diagB.applied),
            static_cast<unsigned long long>(diagD.refused),
            static_cast<unsigned long long>(diagD.refused - diagB.refused));
        DumpBand(dumpDir, L"arm_d.f32", d1);
        FFF3FP_ClearDynamicMetadata(player, kKindColourTransform);
        FFF3FP_Redraw(player);
    }

    printf("\n== 像素对照 ==\n");
    CompareBands("A vs B（泵开是否改变画面）", a1, b1);
    CompareBands("A vs C（撤表后是否回到基线）", a1, c1);
    CompareBands("B vs C（差值的可逆性）", b1, c1);
    if (d1.ok) {
        CompareBands("A vs D（同一份载荷是否改变画面）", a1, d1);
        CompareBands("B vs D（泵投的表 == 探针投的表？）", b1, d1);
    }

    if (!runPerf) {
        printf("\n== 播放开销：--no-pump-perf 跳过 ==\n");
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        ::FreeLibrary(module);
        return 0;
    }

    // ── 播放开销：同一文件、同一时长，关/开交替三轮 ───────────────────────
    // 交替而不是"先关三轮再开三轮"：机器温度、别的进程、解码器预热都会随
    // 时间漂移，成组跑会把这层漂移全部算到后一组头上。取三轮中位数。
    printf("\n== 播放开销（每轮 %d 秒，关/开交替三轮）==\n", perfSeconds);
    PerfReading off[3], on[3];
    // 预热一轮（丢弃）：首次播放要付解码器/页缓存的初始化，那一轮的量级能到
    // 稳定态的两三倍，混进对照会把均值抬到看不出差别。
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    const auto warmup = PlayFor(player, 3);
    FFF3FP_Pause(player);
    Pump(400);
    PrintPerf("预热丢弃", warmup);
    for (int round = 0; round < 3; ++round) {
        injection.stop();
        FFF3FP_Seek(player, 0);
        FFF3FP_Play(player);
        off[round] = PlayFor(player, perfSeconds);
        FFF3FP_Pause(player);
        Pump(400);
        PrintPerf("泵关", off[round]);
        injection.start(player);
        // 计数基线必须在 start() 之后取：Start 会清零模块计数，先取基线会让
        // 本轮增量下溢成天文数字。
        const auto statusBefore = ReadStatus(&injection);
        const auto privateBefore = PrivateBytes();
        FFF3FP_Seek(player, 0);
        FFF3FP_Play(player);
        on[round] = PlayFor(player, perfSeconds);
        FFF3FP_Pause(player);
        Pump(400);
        const auto statusAfter = ReadStatus(&injection);
        const auto privateAfter = PrivateBytes();
        PrintPerf("泵开", on[round]);
        // 这一段同时是"逐帧变表的节奏"测量：素材的每一帧都带不同的 RPU 时，
        // 泵必须每帧烘一张表、每帧投一次（内核的投表路径不去重，每次都会重新
        // 分配载荷并重新应用到屏上帧 + enqueue 重绘）。injected/秒 与呈现帧率
        // 相等，才说明它真跟上了帧节奏；低于帧率说明泵在丢帧或内核背压。
        const auto injectedDelta = statusAfter.framesInjected - statusBefore.framesInjected;
        const auto unchangedDelta = statusAfter.framesUnchanged - statusBefore.framesUnchanged;
        const auto skippedDelta = statusAfter.framesSkipped - statusBefore.framesSkipped;
        const auto noMetaDelta = statusAfter.framesNoMetadata - statusBefore.framesNoMetadata;
        const auto handled = injectedDelta + unchangedDelta + skippedDelta + noMetaDelta;
        printf("  [节奏] 投表=%llu 未变=%llu 跳过=%llu 无据=%llu 合计=%llu  "
               "呈现=%.0f 帧  → 投表/秒=%.2f  处理/秒=%.2f\n",
            static_cast<unsigned long long>(injectedDelta),
            static_cast<unsigned long long>(unchangedDelta),
            static_cast<unsigned long long>(skippedDelta),
            static_cast<unsigned long long>(noMetaDelta),
            static_cast<unsigned long long>(handled),
            static_cast<double>(on[round].presented),
            on[round].wallSeconds > 0 ? injectedDelta / on[round].wallSeconds : 0.0,
            on[round].wallSeconds > 0 ? handled / on[round].wallSeconds : 0.0);
        if (privateBefore > 0 && privateAfter > 0)
            printf("  [内存] 私有字节 %zu → %zu KB  单轮增量 %+.0f KB\n",
                privateBefore / 1024, privateAfter / 1024,
                (static_cast<double>(privateAfter) - static_cast<double>(privateBefore)) / 1024.0);
        injection.stop();
        Pump(60);
        const auto stopped = ReadStatus(&injection);
        printf("  [节奏] stop() 后 running=%u\n", stopped.running);
    }

    const auto middle = [](double a, double b, double c) {
        return (std::max)((std::min)(a, b), (std::min)((std::max)(a, b), c));
    };
    const auto offCores = middle(off[0].cpuSeconds / off[0].wallSeconds,
        off[1].cpuSeconds / off[1].wallSeconds, off[2].cpuSeconds / off[2].wallSeconds);
    const auto onCores = middle(on[0].cpuSeconds / on[0].wallSeconds,
        on[1].cpuSeconds / on[1].wallSeconds, on[2].cpuSeconds / on[2].wallSeconds);
    const auto offFps = middle(off[0].presented / off[0].wallSeconds,
        off[1].presented / off[1].wallSeconds, off[2].presented / off[2].wallSeconds);
    const auto onFps = middle(on[0].presented / on[0].wallSeconds,
        on[1].presented / on[1].wallSeconds, on[2].presented / on[2].wallSeconds);
    const auto spread = [](const char* label, const PerfReading* readings,
                            const double divisor, const char* unit) {
        double value[3] = { readings[0].cpuSeconds / divisor, readings[1].cpuSeconds / divisor,
            readings[2].cpuSeconds / divisor };
        if (std::strcmp(unit, "fps") == 0)
            for (int i = 0; i < 3; ++i) value[i] = readings[i].presented / divisor;
        std::sort(value, value + 3);
        printf("  %-10s 最小=%.3f 中位=%.3f 最大=%.3f %s\n", label, value[0], value[1], value[2], unit);
    };
    printf("\n== 开销汇总 ==\n");
    spread("泵关 核", off, off[0].wallSeconds ? off[0].wallSeconds : 1.0, "核");
    printf("  (泵关三轮: %.3f %.3f %.3f 核)\n",
        off[0].cpuSeconds / off[0].wallSeconds, off[1].cpuSeconds / off[1].wallSeconds,
        off[2].cpuSeconds / off[2].wallSeconds);
    printf("  (泵开三轮: %.3f %.3f %.3f 核)\n",
        on[0].cpuSeconds / on[0].wallSeconds, on[1].cpuSeconds / on[1].wallSeconds,
        on[2].cpuSeconds / on[2].wallSeconds);
    printf("  进程平均占用中位差: 泵开-泵关 = %+.3f 核\n", onCores - offCores);
    printf("  烘焙模型上限: 单帧 %.0f 微秒 x %.2f fps = %.3f 核（泵的全部自有开销都在这里）\n",
        bakeAverageMicros, onFps, bakeAverageMicros * 1e-6 * onFps);
    printf("  呈现帧率: 泵关最小/中位/最大 = %.2f / %.2f / %.2f fps；泵开 = %.2f / %.2f / %.2f fps\n",
        (std::min)(off[0].presented / off[0].wallSeconds, (std::min)(off[1].presented / off[1].wallSeconds,
            off[2].presented / off[2].wallSeconds)),
        offFps,
        (std::max)(off[0].presented / off[0].wallSeconds, (std::max)(off[1].presented / off[1].wallSeconds,
            off[2].presented / off[2].wallSeconds)),
        (std::min)(on[0].presented / on[0].wallSeconds, (std::min)(on[1].presented / on[1].wallSeconds,
            on[2].presented / on[2].wallSeconds)),
        onFps,
        (std::max)(on[0].presented / on[0].wallSeconds, (std::max)(on[1].presented / on[1].wallSeconds,
            on[2].presented / on[2].wallSeconds)));
    std::uint64_t droppedOff = 0, droppedOn = 0;
    for (int i = 0; i < 3; ++i) { droppedOff += off[i].dropped; droppedOn += on[i].dropped; }
    printf("  三轮丢帧合计: 泵关=%llu 泵开=%llu\n",
        static_cast<unsigned long long>(droppedOff), static_cast<unsigned long long>(droppedOn));
    printf("  判读: 进程 CPU 的单轮噪声可达 ±0.05 核（外部负载只增加不减少），\n"
           "        所以泵的开销以'最小轮'与烘焙模型上限为准，中位只用于看趋势。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    // 卸载之前先让泵彻底停下：DLL 的线程不能在下一次 FreeLibrary 之后还活着。
    ::FreeLibrary(module);
    return 0;
}
