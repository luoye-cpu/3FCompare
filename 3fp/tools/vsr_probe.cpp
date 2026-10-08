// 3FP RTX Video Super Resolution —— 实机验证程序
//
// 目的：在真实窗口 + 真实 GPU 下确认那些"只读代码无法确认"的行为：
//   · 渲染设备的适配器是不是 NVIDIA（本机是 5080 + 4060 双 GPU，必须确认选对）
//   · 打开 VSR 开关后，videoScalingMode 是否真的变成 D3D11VideoProcessor
//   · 驱动是否接受 PPE 扩展（driverRejected 是否被置位）
//   · 不满足条件时（源分辨率越界 / 未在放大）reason 是否给出正确归因
//   · 关闭开关时，缩放模式是否回到 Shader（零改动路径）
//
// 用法：
//   vsr_probe <媒体路径> [--auto] [--sr-window WxH] [--seconds N]
//     --auto        进入播放后调用 FFF3FP_SetVideoSuperResolution(Auto)
//     --sr-window   把窗口设成指定尺寸以强制"放大"（如 2560x1440）
//     --seconds     播放秒数，默认 6
//
// ⚠ 新 API 一律用 GetProcAddress 动态解析：静态导入会让"尚未含 VSR 的内核"
//   直接加载失败（ab_baseline_vs_patched.sh 已踩过这个坑）。
//
// 构建（VS x64 环境）：
//   cl /EHsc /std:c++20 /I <内核根> vsr_probe.cpp <路径>\FFF.Native.lib

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

using SetVideoSuperResolutionFn = FFFResult(__cdecl*)(FFF3FPHandle,
    FFF3FPVideoSuperResolution);
using GetVideoSuperResolutionStatusFn = FFFResult(__cdecl*)(FFF3FPHandle,
    FFF3FPVideoSuperResolutionStatus*);

bool g_openCompleted = false;
volatile LONG g_stop = 0;

BOOL WINAPI ConsoleHandler(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_CLOSE_EVENT) {
        InterlockedExchange(&g_stop, 1);
        return TRUE;
    }
    return FALSE;
}

void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

// Must be a plain function: the log sink takes a C function pointer, and a
// captureless lambda is not convertible to the API's calling convention here.
// Argument order is (context, line) -- see FFF3FPLogCallback.
void __cdecl LogCallback(void*, const char* line) noexcept {
    if (line != nullptr && std::strstr(line, "Super Resolution") != nullptr)
        printf("[kernel-log] %s\n", line);
}

const char* ScalingModeName(const FFF3FPVideoScalingMode mode) {
    switch (mode) {
    case FFF3FPVideoScalingMode::Shader: return "Shader";
    case FFF3FPVideoScalingMode::D3D11VideoProcessor: return "D3D11VideoProcessor";
    default: return "?";
    }
}

const char* ReasonName(const FFF3FPVideoSuperResolutionReason reason) {
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
    default: return "?";
    }
}

struct Options {
    std::wstring media;
    bool autoEnable = false;
    bool comparisonMode = false;
    std::uint32_t windowWidth = 0;
    std::uint32_t windowHeight = 0;
    int seconds = 6;
};

bool ParseOptions(const int argc, wchar_t** argv, Options& options) {
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--auto") options.autoEnable = true;
        else if (arg == L"--comparison") options.comparisonMode = true;
        else if (arg == L"--sr-window" && i + 1 < argc) {
            unsigned w = 0, h = 0;
            if (swscanf_s(argv[++i], L"%ux%u", &w, &h) != 2 || w == 0 || h == 0) {
                wprintf(L"--sr-window 需要 WxH，例如 2560x1440\n");
                return false;
            }
            options.windowWidth = w;
            options.windowHeight = h;
        } else if (arg == L"--seconds" && i + 1 < argc) {
            options.seconds = _wtoi(argv[++i]);
            if (options.seconds < 1 || options.seconds > 600) options.seconds = 6;
        } else if (!arg.empty() && arg[0] != L'-') {
            options.media = arg;
        }
    }
    if (options.media.empty()) {
        wprintf(L"用法: vsr_probe <媒体路径> [--auto] [--comparison] "
                L"[--sr-window WxH] [--seconds N]\n");
        return false;
    }
    return true;
}

void PrintStatus(const char* tag, const FFF3FPHandle player,
    const GetVideoSuperResolutionStatusFn getStatus) {
    FFF3FPVideoSuperResolutionStatus status{};
    status.size = sizeof(status);
    status.version = 1;
    const auto result = getStatus(player, &status);
    if (result != FFFResult::Success) {
        printf("[%s] 状态查询失败 result=%d\n", tag, static_cast<int>(result));
        return;
    }
    printf("[%s] requested=%d nvidiaAdapter=%u active=%u driverRejected=%u reason=%s "
           "src=%ux%u target=%ux%u\n",
        tag, static_cast<int>(status.requested), status.nvidiaAdapter, status.active,
        status.driverRejected, ReasonName(status.reason), status.sourceWidth,
        status.sourceHeight, status.targetWidth, status.targetHeight);
}

} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    Options options;
    if (!ParseOptions(argc, argv, options)) return 2;

    // Dynamic resolution: never statically import the VSR entry points, so the
    // same probe binary also runs against a kernel that predates them.
    const HMODULE kernel = GetModuleHandleW(L"FFF.Native.dll");
    if (kernel == nullptr) {
        printf("找不到已加载的 FFF.Native.dll（探针需链接内核 import lib）\n");
        return 3;
    }
    const auto setVideoSuperResolution = reinterpret_cast<SetVideoSuperResolutionFn>(
        GetProcAddress(kernel, "FFF3FP_SetVideoSuperResolution"));
    const auto getVideoSuperResolutionStatus =
        reinterpret_cast<GetVideoSuperResolutionStatusFn>(
            GetProcAddress(kernel, "FFF3FP_GetVideoSuperResolutionStatus"));
    if (setVideoSuperResolution == nullptr || getVideoSuperResolutionStatus == nullptr) {
        printf("内核不含 VSR 入口（这是旧内核，符合预期）\n");
        return 4;
    }
    printf("内核含 VSR 入口，PlayerApiVersion=%u\n", FFF3FP_GetApiVersion());

    const HINSTANCE instance = GetModuleHandleW(nullptr);
    constexpr wchar_t kClassName[] = L"FFF3FPVsrProbeWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = kClassName;
    RegisterClassW(&windowClass);

    const auto width = options.windowWidth != 0 ? options.windowWidth : 1280u;
    const auto height = options.windowHeight != 0 ? options.windowHeight : 720u;
    const HWND window = CreateWindowExW(0, kClassName, L"3FP VSR probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, static_cast<int>(width),
        static_cast<int>(height), nullptr, nullptr, instance, nullptr);
    if (window == nullptr) {
        printf("创建窗口失败\n");
        return 5;
    }

    const std::string mediaUtf8 = [&options] {
        const int size = WideCharToMultiByte(CP_UTF8, 0, options.media.c_str(), -1,
            nullptr, 0, nullptr, nullptr);
        std::string text(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
        if (size > 1)
            WideCharToMultiByte(CP_UTF8, 0, options.media.c_str(), -1, text.data(),
                size, nullptr, nullptr);
        return text;
    }();

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 1000.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    const auto created = FFF3FP_Create(&configuration, &player);
    if (created != FFFResult::Success || player == nullptr) {
        printf("FFF3FP_Create 失败 result=%d\n", static_cast<int>(created));
        return 6;
    }

    // Registered before playback: the renderer reports the exact failing VSR step
    // through this sink, which is the only way to tell an unsupported driver
    // apart from a bug in our own view setup (both show as DriverRejected).
    FFF3FP_SetLogCallback(&LogCallback, nullptr);

    // Establish the switch state before opening so the first frame already
    // exercises the intended path.
    if (options.autoEnable) {
        const auto set = setVideoSuperResolution(player, FFF3FPVideoSuperResolution::Auto);
        printf("启用 VSR(Auto) result=%d\n", static_cast<int>(set));
    }
    if (options.comparisonMode) {
        printf("（探针未导出 SetComparisonMode，跳过）\n");
    }

    if (FFF3FP_Open(player, mediaUtf8.c_str()) != FFFResult::Success) {
        char error[512]{};
        FFF3FP_GetLastError(player, error, sizeof(error), nullptr);
        printf("打开失败: %s\n", error);
        FFF3FP_Destroy(player);
        return 7;
    }
    // Open is asynchronous: wait for OpenCompleted before Play, otherwise Play
    // is rejected and no frame is ever presented (and VSR is never reached).
    const auto openDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(20);
    while (!g_openCompleted && std::chrono::steady_clock::now() < openDeadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(50);
    }
    if (!g_openCompleted) {
        char error[512]{};
        FFF3FP_GetLastError(player, error, sizeof(error), nullptr);
        printf("打开未完成（超时）: %s\n", error);
    }
    const auto played = FFF3FP_Play(player);
    printf("Play result=%d openCompleted=%d\n", static_cast<int>(played),
        g_openCompleted ? 1 : 0);

    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::seconds(options.seconds);
    while (std::chrono::steady_clock::now() < deadline &&
           InterlockedCompareExchange(&g_stop, 0, 0) == 0) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(100);
    }

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    // FFF3FPSnapshot carries its own revision, independent of PlayerApiVersion.
    snapshot.version = 8;
    if (FFF3FP_GetSnapshot(player, &snapshot) == FFFResult::Success) {
        printf("[snapshot] opening=%s videoScalingMode=%s video=%ux%u outBits=%u\n",
            g_openCompleted ? "done" : "pending",
            ScalingModeName(snapshot.videoScalingMode), snapshot.videoWidth,
            snapshot.videoHeight, snapshot.videoOutputBitDepth);
    }
    PrintStatus("vsr", player, getVideoSuperResolutionStatus);
    if (options.autoEnable) {
        setVideoSuperResolution(player, FFF3FPVideoSuperResolution::Off);
        Sleep(400);
        printf("（已关闭 VSR）\n");
        PrintStatus("vsr-off", player, getVideoSuperResolutionStatus);
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
