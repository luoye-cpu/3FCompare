// 3FP CUDA→D3D11 interop 直拷认证探针
//
// 目的：在真实窗口 + 真实 GPU（RTX 5080 + 4060 双卡）下认证 PR55 fallback 修复：
//   V1 默认路径：HEVC 4:4:4 10bit 被 D3D11VA 拒绝（无 hw_config）→ CUDA 解码
//     接管 → interop 直拷（snapshot.uploadPath == 8，无 PCIe 往返）。
//   V2 FFF_CUDA_INTEROP_PROBE_REFUSE=1：内核在 Import 处强制拒绝 → fallback
//     下载 + UploadPlanarFrames（首帧 uploadPath=1/2/6）→ 闩锁 cudaInteropRefused_
//     后 PlayerSession 降级 CPU 下载帧（后续 uploadPath=2）。修复核心断言：
//     帧不缺图、presented 持续推进。
//   V3 FFF3FP_CUDA_D3D11_INTEROP=0：互操作整体关闭，仍 CUDA 解码但上传走
//     下载后 CPU 上传（uploadPath == 2）。
//   像素对比：--shot SEC:PATH 在指定秒把当前呈现帧写成 BMP（FFF3FP_CopyFrame
//     源分辨率 BGRA8），V2/V3 在相同秒各拍一张后做结构一致性比较。
//   性能：V1 vs V3 的 uploadCopy100ns / videoUpload100ns / uploadMapWait100ns
//     每帧均值（上传环节口径，不含解码）。
//
// 用法：
//   cuda_interop_probe <媒体路径> [--seconds N] [--shot SEC:PATH]...
//     --seconds   播放秒数，默认 10
//     --shot      播放到第 SEC 秒时把呈现帧存为 BMP（可重复；秒与路径以第一个
//                 冒号分隔，路径内可再含盘符冒号）
//
// stderr 诊断（FFF_CUDA_INTEROP_TRACE=1 时内核的 "[cuda] import ok/refused"）
// 由内核直接 fprintf(stderr)，由外部 shell 重定向收集；本探针 stdout 只打
// snapshot 行与汇总行。
//
// 全部 FFF3FP 入口用 GetProcAddress 动态解析：静态导入会在 ABI/导出集不完全
// 匹配的内核上直接加载失败（vsr_probe.cpp 的既有教训），也让本探针不依赖
// FFF.Native.lib 的新旧。
//
// 构建（pwsh -NoProfile -File build\cuda_probe_build.ps1）：
//   cl /EHsc /std:c++20 /I <FFF.Native 根> cuda_interop_probe.cpp /link user32.lib

#include <windows.h>
#include <dxgi1_6.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

// ---- Dynamically resolved entry points ------------------------------------
using GetApiVersionFn = std::uint32_t(__cdecl*)();
using CreateFn = FFFResult(__cdecl*)(const FFF3FPConfiguration*, FFF3FPHandle*);
using OpenFn = FFFResult(__cdecl*)(FFF3FPHandle, const char*);
using PlayFn = FFFResult(__cdecl*)(FFF3FPHandle);
using GetSnapshotFn = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPSnapshot*);
using SeekFrameFn = FFFResult(__cdecl*)(FFF3FPHandle, std::int64_t);
using CopyFrameFn = FFFResult(__cdecl*)(FFF3FPHandle, void*, std::uint32_t,
    std::uint32_t*, std::uint32_t*, FFF3FPCopyFrameLayout, FFF3FPCopyFrameFormat);
using GetLastErrorFn = FFFResult(__cdecl*)(FFF3FPHandle, char*, std::uint32_t,
    std::uint32_t*);
using CloseFn = FFFResult(__cdecl*)(FFF3FPHandle);
using DestroyFn = void(__cdecl*)(FFF3FPHandle);
using SetVideoSuperResolutionFn = FFFResult(__cdecl*)(FFF3FPHandle,
    FFF3FPVideoSuperResolution);
using GetVideoSuperResolutionStatusFn = FFFResult(__cdecl*)(FFF3FPHandle,
    FFF3FPVideoSuperResolutionStatus*);

struct KernelEntries {
    GetApiVersionFn getApiVersion = nullptr;
    CreateFn create = nullptr;
    OpenFn open = nullptr;
    PlayFn play = nullptr;
    GetSnapshotFn getSnapshot = nullptr;
    SeekFrameFn seekFrame = nullptr;
    CopyFrameFn copyFrame = nullptr;
    GetLastErrorFn getLastError = nullptr;
    CloseFn close = nullptr;
    DestroyFn destroy = nullptr;
    SetVideoSuperResolutionFn setVsr = nullptr;
    GetVideoSuperResolutionStatusFn getVsrStatus = nullptr;
};

bool g_openCompleted = false;
volatile LONG g_stop = 0;
volatile LONG g_errorEvents = 0;
volatile LONG g_playbackEnded = 0;

BOOL WINAPI ConsoleHandler(const DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_CLOSE_EVENT) {
        InterlockedExchange(&g_stop, 1);
        return TRUE;
    }
    return FALSE;
}

void EventCallback(void*, const FFF3FPEvent eventType, const char* detailJsonUtf8) {
    switch (eventType) {
    case FFF3FPEvent::OpenCompleted:
        g_openCompleted = true;
        break;
    case FFF3FPEvent::Error:
        InterlockedIncrement(&g_errorEvents);
        printf("[event] Error detail=%s\n",
            detailJsonUtf8 != nullptr ? detailJsonUtf8 : "(null)");
        break;
    case FFF3FPEvent::PlaybackEnded:
        InterlockedExchange(&g_playbackEnded, 1);
        break;
    default:
        break;
    }
}

const char* DecodeModeName(const FFF3FPDecodeMode mode) {
    switch (mode) {
    case FFF3FPDecodeMode::Cpu: return "Cpu";
    case FFF3FPDecodeMode::Gpu: return "Gpu";
    default: return "Unspecified";
    }
}

const char* StateName(const FFF3FPState state) {
    switch (state) {
    case FFF3FPState::Idle: return "Idle";
    case FFF3FPState::Opening: return "Opening";
    case FFF3FPState::Ready: return "Ready";
    case FFF3FPState::Playing: return "Playing";
    case FFF3FPState::Paused: return "Paused";
    case FFF3FPState::Ended: return "Ended";
    case FFF3FPState::Failed: return "Failed";
    case FFF3FPState::Closed: return "Closed";
    default: return "?";
    }
}

const char* VsrReasonName(const FFF3FPVideoSuperResolutionReason reason) {
    switch (reason) {
    case FFF3FPVideoSuperResolutionReason::None: return "None(active)";
    case FFF3FPVideoSuperResolutionReason::NotRequested: return "NotRequested";
    case FFF3FPVideoSuperResolutionReason::NotNvidiaAdapter: return "NotNvidiaAdapter";
    case FFF3FPVideoSuperResolutionReason::NotUpscaling: return "NotUpscaling";
    case FFF3FPVideoSuperResolutionReason::SourceResolutionOutOfRange:
        return "SourceResolutionOutOfRange";
    case FFF3FPVideoSuperResolutionReason::UnsupportedSourceFormat:
        return "UnsupportedSourceFormat";
    case FFF3FPVideoSuperResolutionReason::DriverRejected: return "DriverRejected";
    case FFF3FPVideoSuperResolutionReason::YieldedToAutoHdr: return "YieldedToAutoHdr";
    default: return "?";
    }
}

// DXGI 适配器枚举：Factory1（PnP 顺序，解码侧 FindAdapter 用它）与 Factory6
// （硬件优先排序，渲染侧 EnsureDevice 用它）并列打印——两种序号可能不一致，
// --adapter N 的实际落点必须按此对照解读。
void ListAdapters() {
    for (int factoryKind = 0; factoryKind < 2; ++factoryKind) {
        void* factoryRaw = nullptr;
        const IID& factoryIid = factoryKind == 0 ? __uuidof(IDXGIFactory1)
            : __uuidof(IDXGIFactory6);
        if (FAILED(CreateDXGIFactory1(factoryIid, &factoryRaw)) || factoryRaw == nullptr) {
            printf("CreateDXGIFactory1(%s) 失败\n", factoryKind == 0 ? "Factory1" : "Factory6");
            continue;
        }
        auto* factory1 = static_cast<IDXGIFactory1*>(factoryRaw);
        printf("[%s 枚举]\n", factoryKind == 0 ? "Factory1(解码 FindAdapter 序)"
            : "Factory6(渲染 EnsureDevice 序)");
        for (UINT index = 0;; ++index) {
            IDXGIAdapter1* adapter = nullptr;
            if (factory1->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 description{};
            if (adapter->GetDesc1(&description) == S_OK) {
                UINT outputs = 0;
                for (UINT outputIndex = 0;; ++outputIndex) {
                    IDXGIOutput* output = nullptr;
                    if (adapter->EnumOutputs(outputIndex, &output) == DXGI_ERROR_NOT_FOUND) break;
                    ++outputs;
                    output->Release();
                }
                printf("  [%u] %ls vendor=0x%04X dev=0x%04X LUID=%08X:%08X outputs=%u%s\n",
                    index, description.Description, description.VendorId,
                    description.DeviceId, description.AdapterLuid.HighPart,
                    description.AdapterLuid.LowPart, outputs,
                    (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) ? " (software)" : "");
            }
            adapter->Release();
        }
        factory1->Release();
    }
}

// 100 ns -> ms，保留三位由 printf 格式控制。
double Ms(const std::uint64_t hundredNs) {
    return static_cast<double>(hundredNs) / 10000.0;
}

struct Options {
    std::wstring media;
    int seconds = 10;
    int adapterIndex = -1;                    // preferredAdapterIndex；-1=内核默认策略
    bool vsr = false;                         // 启用 RTX VSR(Auto) 并打印 reason
    bool listAdapters = false;                // 枚举 DXGI 适配器后退出
    std::vector<std::wstring> shotSpecs;      // 原始 "SEC:PATH"（墙钟时刻拍）
    std::vector<std::wstring> frameShotSpecs; // 原始 "IDX:PATH"（SeekFrame 帧对齐拍）
};

bool ParseOptions(const int argc, wchar_t** argv, Options& options) {
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--seconds" && i + 1 < argc) {
            options.seconds = _wtoi(argv[++i]);
            if (options.seconds < 1 || options.seconds > 600) options.seconds = 10;
        } else if (arg == L"--adapter" && i + 1 < argc) {
            options.adapterIndex = _wtoi(argv[++i]);
        } else if (arg == L"--vsr") {
            options.vsr = true;
        } else if (arg == L"--list-adapters") {
            options.listAdapters = true;
        } else if (arg == L"--shot" && i + 1 < argc) {
            options.shotSpecs.push_back(argv[++i]);
        } else if (arg == L"--frame-shot" && i + 1 < argc) {
            options.frameShotSpecs.push_back(argv[++i]);
        } else if (!arg.empty() && arg[0] != L'-') {
            options.media = arg;
        }
    }
    if (!options.listAdapters && options.media.empty()) {
        wprintf(L"用法: cuda_interop_probe <媒体路径> [--seconds N] [--adapter N] "
                L"[--vsr] [--list-adapters] [--shot SEC:PATH]...\n");
        return false;
    }
    return true;
}

struct Shot {
    int second = 0;
    std::wstring path;
    bool done = false;
};

// "3:D:\TEST\x.bmp" -> {3, "D:\TEST\x.bmp"}；以第一个冒号分隔。
bool ParseShotSpec(const std::wstring& spec, Shot& shot) {
    const auto colon = spec.find(L':');
    if (colon == std::wstring::npos) return false;
    shot.second = _wtoi(spec.substr(0, colon).c_str());
    shot.path = spec.substr(colon + 1);
    return shot.second >= 0 && !shot.path.empty();
}

std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0,
        nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1)
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr,
            nullptr);
    return out;
}

// FFF3FP_CopyFrame(Bgra8) 返回自顶向下逐行 BGRA8；BITMAPINFOHEADER 用负高度
// 直接表达 top-down，32bpp BI_RGB 是合法 BMP 且天然 4 字节行对齐。
bool WriteBmp(const wchar_t* path, const std::uint32_t width,
    const std::uint32_t height, const std::vector<std::uint8_t>& bgra) {
    BITMAPFILEHEADER fileHeader{};
    BITMAPINFOHEADER infoHeader{};
    const std::uint32_t rowBytes = width * 4u;
    fileHeader.bfType = 0x4D42; // "BM"
    fileHeader.bfOffBits = sizeof(fileHeader) + sizeof(infoHeader);
    fileHeader.bfSize = fileHeader.bfOffBits + rowBytes * height;
    infoHeader.biSize = sizeof(infoHeader);
    infoHeader.biWidth = static_cast<LONG>(width);
    infoHeader.biHeight = -static_cast<LONG>(height);
    infoHeader.biPlanes = 1;
    infoHeader.biBitCount = 32;
    infoHeader.biCompression = BI_RGB;
    FILE* file = nullptr;
    if (_wfopen_s(&file, path, L"wb") != 0 || file == nullptr) return false;
    const bool ok = fwrite(&fileHeader, sizeof fileHeader, 1, file) == 1 &&
        fwrite(&infoHeader, sizeof infoHeader, 1, file) == 1 &&
        fwrite(bgra.data(), 1, bgra.size(), file) == bgra.size();
    fclose(file);
    return ok;
}

// 两段式 CopyFrame：先 null 拿宽高，再取 BGRA8 源分辨率整帧并存 BMP。
bool SaveShot(const KernelEntries& kernel, const FFF3FPHandle player,
    const Shot& shot, const std::int64_t framePts) {
    std::uint32_t width = 0, height = 0;
    const auto sized = kernel.copyFrame(player, nullptr, 0, &width, &height,
        FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Bgra8);
    // 两段式契约：首次 capacity=0 返回 BufferTooSmall 并写回宽高，属预期而非错误。
    if ((sized != FFFResult::Success && sized != FFFResult::BufferTooSmall) ||
        width == 0 || height == 0) {
        printf("[shot t=%ds] CopyFrame 尺寸查询失败 result=%d\n", shot.second,
            static_cast<int>(sized));
        return false;
    }
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(width) * height * 4);
    const auto copied = kernel.copyFrame(player, pixels.data(),
        static_cast<std::uint32_t>(pixels.size()), &width, &height,
        FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Bgra8);
    if (copied != FFFResult::Success) {
        printf("[shot t=%ds] CopyFrame 失败 result=%d\n", shot.second,
            static_cast<int>(copied));
        return false;
    }
    const bool written = WriteBmp(shot.path.c_str(), width, height, pixels);
    printf("[shot t=%ds] %s %ux%u framePts=%lld bytes=%zu\n", shot.second,
        written ? "已写" : "写入失败", width, height,
        static_cast<long long>(framePts), pixels.size());
    return written;
}

// 跨打印点的计数快照；增量全部来自相邻两次 GetSnapshot。
struct Counters {
    std::uint64_t uploadCount = 0;
    std::uint64_t upload100ns = 0;
    std::uint64_t uploadCopy100ns = 0;
    std::uint64_t uploadMapWait100ns = 0;
    std::uint64_t uploadCopyBytes = 0;
    std::uint64_t presented = 0;
    std::uint64_t dropped = 0;
    std::uint64_t decode100ns = 0;
    std::uint64_t hwTransfer100ns = 0;
};

Counters MakeCounters(const FFF3FPSnapshot& snapshot) {
    return Counters{snapshot.uploadCount, snapshot.videoUpload100ns,
        snapshot.uploadCopy100ns, snapshot.uploadMapWait100ns,
        snapshot.uploadCopyBytes, snapshot.presentedVideoFrames,
        snapshot.droppedVideoFrames, snapshot.videoDecode100ns,
        snapshot.hardwareTransfer100ns};
}

void PrintRow(const int second, const FFF3FPSnapshot& snapshot,
    const Counters& previous) {
    const auto uploads = snapshot.uploadCount - previous.uploadCount;
    const auto uploadMs = Ms(snapshot.videoUpload100ns - previous.upload100ns);
    const auto copyMs = Ms(snapshot.uploadCopy100ns - previous.uploadCopy100ns);
    const auto mapMs = Ms(snapshot.uploadMapWait100ns - previous.uploadMapWait100ns);
    const auto copyBytes = snapshot.uploadCopyBytes - previous.uploadCopyBytes;
    const auto presented = snapshot.presentedVideoFrames - previous.presented;
    const auto dropped = snapshot.droppedVideoFrames - previous.dropped;
    const auto hwXferMs = Ms(snapshot.hardwareTransfer100ns -
        previous.hwTransfer100ns);
    long long bytesPerFrame = -1;
    if (uploads > 0) bytesPerFrame = static_cast<long long>(copyBytes / uploads);
    printf("[t=%2ds] state=%-7s pts=%6lld dec=%-4s upath=%u upCnt=%llu "
        "upMs=%8.3f copyMs=%8.3f mapMs=%7.3f hwXferMs=%8.3f copyB/fr=%lld "
        "pres=%llu drop=%llu\n",
        second, StateName(snapshot.state),
        static_cast<long long>(snapshot.framePts), DecodeModeName(snapshot.decodeMode),
        snapshot.uploadPath, static_cast<unsigned long long>(uploads), uploadMs,
        copyMs, mapMs, hwXferMs, bytesPerFrame,
        static_cast<unsigned long long>(presented),
        static_cast<unsigned long long>(dropped));
}

void Accumulate(const FFF3FPSnapshot& snapshot, const Counters& previous,
    std::map<std::uint32_t, std::uint64_t>& pathCounts) {
    // uploadPath 是"最近一帧"的路径；区间内 uploadCount 增量记到当前路径名下。
    // 60fps 下每秒 60 帧，路径切换只发生在开头的 fallback 帧，误差可忽略。
    const auto uploads = snapshot.uploadCount - previous.uploadCount;
    if (uploads > 0) pathCounts[snapshot.uploadPath] += uploads;
}

void PrintSummary(const FFF3FPSnapshot& snapshot, const Counters& first,
    const std::map<std::uint32_t, std::uint64_t>& pathCounts,
    const int errorEvents, const bool openCompleted, const bool playbackEnded) {
    const auto uploads = snapshot.uploadCount - first.uploadCount;
    const auto uploadMs = Ms(snapshot.videoUpload100ns - first.upload100ns);
    const auto copyMs = Ms(snapshot.uploadCopy100ns - first.uploadCopy100ns);
    const auto mapMs = Ms(snapshot.uploadMapWait100ns - first.uploadMapWait100ns);
    const auto copyBytes = snapshot.uploadCopyBytes - first.uploadCopyBytes;
    const auto decodeMs = Ms(snapshot.videoDecode100ns - first.decode100ns);
    const auto hwXferMs = Ms(snapshot.hardwareTransfer100ns - first.hwTransfer100ns);
    const auto presented = snapshot.presentedVideoFrames - first.presented;
    const auto dropped = snapshot.droppedVideoFrames - first.dropped;
    printf("[summary] uploads=%llu paths{", static_cast<unsigned long long>(uploads));
    bool firstEntry = true;
    for (const auto& [path, count] : pathCounts) {
        printf("%s%u=%llu", firstEntry ? "" : " ", path,
            static_cast<unsigned long long>(count));
        firstEntry = false;
    }
    printf("}\n");
    if (uploads > 0) {
        const auto perFrame = static_cast<double>(uploads);
        printf("[summary] upMsTotal=%.3f copyMsTotal=%.3f mapMsTotal=%.3f "
            "upMs/frame=%.3f copyMs/frame=%.3f mapMs/frame=%.3f\n",
            uploadMs, copyMs, mapMs, uploadMs / perFrame, copyMs / perFrame,
            mapMs / perFrame);
        printf("[summary] copyBytesTotal=%llu copyBytes/frame=%llu "
            "decodeMsTotal=%.3f hwXferMsTotal=%.3f hwXferMs/frame=%.3f\n",
            static_cast<unsigned long long>(copyBytes),
            static_cast<unsigned long long>(copyBytes / uploads), decodeMs,
            hwXferMs, hwXferMs / perFrame);
    }
    printf("[summary] presented=%llu dropped=%llu errors=%d openCompleted=%d "
        "playbackEnded=%d dec=%s\n",
        static_cast<unsigned long long>(presented),
        static_cast<unsigned long long>(dropped), errorEvents,
        openCompleted ? 1 : 0, playbackEnded ? 1 : 0,
        DecodeModeName(snapshot.decodeMode));
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    Options options;
    if (!ParseOptions(argc, argv, options)) return 2;
    if (options.listAdapters) {
        ListAdapters();
        return 0;
    }
    std::vector<Shot> shots;
    for (const auto& spec : options.shotSpecs) {
        Shot shot;
        if (!ParseShotSpec(spec, shot)) {
            wprintf(L"--shot 需要 SEC:PATH 格式: %s\n", spec.c_str());
            return 2;
        }
        shots.push_back(shot);
    }
    std::vector<Shot> frameShots;
    for (const auto& spec : options.frameShotSpecs) {
        Shot shot;
        if (!ParseShotSpec(spec, shot)) {
            wprintf(L"--frame-shot 需要 IDX:PATH 格式: %s\n", spec.c_str());
            return 2;
        }
        frameShots.push_back(shot);
    }

    // 显式加载 exe 同目录的内核 DLL（探针全部动态解析，无静态导入触发加载）。
    const HMODULE kernelModule = LoadLibraryW(L"FFF.Native.dll");
    if (kernelModule == nullptr) {
        printf("加载 FFF.Native.dll 失败 GetLastError=%lu\n", GetLastError());
        return 3;
    }
    KernelEntries kernel;
    const auto resolve = [&kernel, kernelModule](const char* name, void** target) {
        *target = reinterpret_cast<void*>(GetProcAddress(kernelModule, name));
        return *target != nullptr;
    };
    bool resolved = true;
    resolved &= resolve("FFF3FP_GetApiVersion",
        reinterpret_cast<void**>(&kernel.getApiVersion));
    resolved &= resolve("FFF3FP_Create", reinterpret_cast<void**>(&kernel.create));
    resolved &= resolve("FFF3FP_Open", reinterpret_cast<void**>(&kernel.open));
    resolved &= resolve("FFF3FP_Play", reinterpret_cast<void**>(&kernel.play));
    resolved &= resolve("FFF3FP_GetSnapshot",
        reinterpret_cast<void**>(&kernel.getSnapshot));
    resolved &= resolve("FFF3FP_SeekFrame",
        reinterpret_cast<void**>(&kernel.seekFrame));
    resolved &= resolve("FFF3FP_CopyFrame",
        reinterpret_cast<void**>(&kernel.copyFrame));
    resolved &= resolve("FFF3FP_GetLastError",
        reinterpret_cast<void**>(&kernel.getLastError));
    resolved &= resolve("FFF3FP_Close", reinterpret_cast<void**>(&kernel.close));
    resolved &= resolve("FFF3FP_Destroy",
        reinterpret_cast<void**>(&kernel.destroy));
    // VSR 入口可选：缺失（旧内核）时 --vsr 降级为不可用，不阻塞其他认证。
    resolve("FFF3FP_SetVideoSuperResolution",
        reinterpret_cast<void**>(&kernel.setVsr));
    resolve("FFF3FP_GetVideoSuperResolutionStatus",
        reinterpret_cast<void**>(&kernel.getVsrStatus));
    if (!resolved) {
        printf("内核缺少必需入口（GetProcAddress 失败）\n");
        return 4;
    }
    printf("内核入口解析完成 PlayerApiVersion=%u\n", kernel.getApiVersion());

    const HINSTANCE instance = GetModuleHandleW(nullptr);
    constexpr wchar_t kClassName[] = L"FFF3FPCudaInteropProbeWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kClassName;
    RegisterClassW(&windowClass);
    const HWND window = CreateWindowExW(0, kClassName, L"3FP CUDA interop probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 1280, 720, nullptr, nullptr,
        instance, nullptr);
    if (window == nullptr) {
        printf("创建窗口失败\n");
        return 5;
    }

    const std::string mediaUtf8 = Utf8(options.media.c_str());

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = kernel.getApiVersion();
    configuration.outputWindow = window;
    // Gpu(=D3D11) 是设备列表的入口：内核按 D3D11VA→CUDA 顺序尝试，4:4:4 10bit
    // 被 D3D11VA 拒绝后由 CUDA 接管——这正是要认证的触发路径。
    configuration.decodeMode = FFF3FPDecodeMode::Gpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    // -1 保留内核默认策略（窗口所在显示器）；--adapter N 覆盖为 DXGI EnumAdapters1
    // 序号（Api.h：渲染侧按 IDXGIFactory6 枚举，见 --list-adapters 的双序列对照）。
    configuration.preferredAdapterIndex = options.adapterIndex;
    if (options.adapterIndex >= 0)
        printf("preferredAdapterIndex=%d\n", options.adapterIndex);

    FFF3FPHandle player = nullptr;
    const auto created = kernel.create(&configuration, &player);
    if (created != FFFResult::Success || player == nullptr) {
        printf("FFF3FP_Create 失败 result=%d\n", static_cast<int>(created));
        return 6;
    }
    if (kernel.open(player, mediaUtf8.c_str()) != FFFResult::Success) {
        char error[512]{};
        kernel.getLastError(player, error, sizeof(error), nullptr);
        printf("打开失败: %s\n", error);
        kernel.destroy(player);
        return 7;
    }
    // Open 异步：等 OpenCompleted 再 Play，否则 Play 被拒且无帧呈现。
    const auto openDeadline = std::chrono::steady_clock::now() +
        std::chrono::seconds(20);
    while (!g_openCompleted && std::chrono::steady_clock::now() < openDeadline &&
        InterlockedCompareExchange(&g_stop, 0, 0) == 0) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(50);
    }
    if (!g_openCompleted) {
        char error[512]{};
        kernel.getLastError(player, error, sizeof(error), nullptr);
        printf("打开未完成（超时）: %s\n", error);
    }
    // 帧对齐拍摄：SeekFrame 落到精确帧索引，V2/V3 各自呈现同一解码帧后像素可比
    // （墙钟 --shot 的拍摄时刻对不齐运动内容，PSNR 会被 1-2 帧的相位差拉低）。
    if (!frameShots.empty()) {
        const auto seeked = kernel.seekFrame(player,
            static_cast<std::int64_t>(frameShots.front().second));
        printf("SeekFrame(%d) result=%d\n", frameShots.front().second,
            static_cast<int>(seeked));
    }
    const auto played = kernel.play(player);
    printf("Play result=%d openCompleted=%d\n", static_cast<int>(played),
        g_openCompleted ? 1 : 0);

    // I4：VSR(Auto) 请求在播放建立后发出，reason 由内核在下一帧给出——
    // Intel 渲染时预期 NotNvidiaAdapter（VendorId 0x8086 非 0x10DE）。
    bool vsrRequested = false;
    if (options.vsr && kernel.setVsr != nullptr) {
        const auto set = kernel.setVsr(player, FFF3FPVideoSuperResolution::Auto);
        vsrRequested = set == FFFResult::Success;
        printf("SetVideoSuperResolution(Auto) result=%d\n", static_cast<int>(set));
    } else if (options.vsr) {
        printf("--vsr：内核缺 VSR 入口，跳过\n");
    }
    const auto PrintVsrStatus = [&kernel, player](const char* tag) {
        if (kernel.getVsrStatus == nullptr) return;
        FFF3FPVideoSuperResolutionStatus status{};
        status.size = sizeof(status);
        status.version = 20;
        if (kernel.getVsrStatus(player, &status) == FFFResult::Success) {
            printf("[vsr %s] requested=%d nvidiaAdapter=%u active=%u "
                "driverRejected=%u reason=%s\n", tag,
                static_cast<int>(status.requested), status.nvidiaAdapter,
                status.active, status.driverRejected,
                VsrReasonName(status.reason));
        } else {
            printf("[vsr %s] 状态查询失败\n", tag);
        }
    };

    Counters first{};
    bool firstSampled = false;
    Counters previous{};
    bool frameShotDone = false;
    bool vsrFirstReported = false;
    std::map<std::uint32_t, std::uint64_t> pathCounts;
    int reportedSecond = 0;
    const auto start = std::chrono::steady_clock::now();
    const auto deadline = start + std::chrono::seconds(options.seconds);

    while (std::chrono::steady_clock::now() < deadline &&
        InterlockedCompareExchange(&g_stop, 0, 0) == 0) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (InterlockedCompareExchange(&g_playbackEnded, 0, 0) != 0) break;

        FFF3FPSnapshot snapshot{};
        snapshot.size = sizeof(snapshot);
        snapshot.version = 22; // PlayerSession.cpp:53 SnapshotVersion（与 PlayerApiVersion 无关）
        if (kernel.getSnapshot(player, &snapshot) == FFFResult::Success) {
            if (!firstSampled) {
                first = MakeCounters(snapshot);
                previous = first;
                firstSampled = true;
            }
            if (vsrRequested && !vsrFirstReported &&
                snapshot.presentedVideoFrames >= 1) {
                vsrFirstReported = true;
                PrintVsrStatus("首帧");
            }
            // 帧对齐模式：第一帧呈现即拍 SeekFrame 落点的帧并结束（V2/V3 各自
            // 呈现同一解码帧，像素逐点可比）。
            if (!frameShots.empty() && !frameShotDone &&
                snapshot.presentedVideoFrames >= 1) {
                frameShotDone = true;
                SaveShot(kernel, player, frameShots.front(),
                    static_cast<std::int64_t>(snapshot.framePts));
                break;
            }
            const auto elapsed = std::chrono::duration_cast<
                std::chrono::duration<double>>(std::chrono::steady_clock::now() -
                start).count();
            const int elapsedSecond = static_cast<int>(elapsed);
            if (elapsedSecond > reportedSecond && elapsedSecond <= options.seconds) {
                ++reportedSecond;
                PrintRow(reportedSecond, snapshot, previous);
                Accumulate(snapshot, previous, pathCounts);
                previous = MakeCounters(snapshot);
            }
            for (auto& shot : shots) {
                if (!shot.done &&
                    static_cast<int>(elapsed) >= shot.second && shot.second > 0) {
                    shot.done = true;
                    SaveShot(kernel, player, shot,
                        static_cast<std::int64_t>(snapshot.framePts));
                }
            }
        }
        Sleep(50);
    }

    // 收尾采样：把最后不足一秒的区间并入汇总。
    FFF3FPSnapshot finalSnapshot{};
    finalSnapshot.size = sizeof(finalSnapshot);
    finalSnapshot.version = 22;
    if (firstSampled &&
        kernel.getSnapshot(player, &finalSnapshot) == FFFResult::Success) {
        if (finalSnapshot.uploadCount > previous.uploadCount) {
            PrintRow(reportedSecond + 1, finalSnapshot, previous);
            Accumulate(finalSnapshot, previous, pathCounts);
        }
        PrintSummary(finalSnapshot, first, pathCounts,
            g_errorEvents, g_openCompleted, g_playbackEnded != 0);
        if (vsrRequested) PrintVsrStatus("收尾");
    } else {
        printf("[summary] 无有效采样（GetSnapshot 从未成功或首次采样缺失）\n");
    }

    kernel.close(player);
    kernel.destroy(player);
    DestroyWindow(window);
    return 0;
}