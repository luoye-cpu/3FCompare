// read_pixel_probe.cpp —— 读取播放中呈现帧的像素（线性 RGB），用于 HDR 传递函数 A/B 对拍。
// 用法：read_pixel_probe <media path> [--hdr]
// 复用 verify_image_mode.cpp 的窗口/会话框架（CPU 解码、真实窗口、真实 FFmpeg DLL）。
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include "3FP/Api/FFF.Player.Api.h"

namespace {
FFF3FPHandle g_player = nullptr;
std::atomic<bool> g_openCompleted{false};

void __cdecl OnEvent(void* context, FFF3FPEvent type, const char* detail) {
    (void)context; (void)detail;
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message;
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: read_pixel_probe <media path> [--hdr]\n");
        return 2;
    }
    const char* path = argv[1];
    bool requestHdr = false;
    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--hdr") == 0) requestHdr = true;
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpReadPixelWindow";
    if (RegisterClassW(&windowClass) == 0) { std::printf("RegisterClassW failed\n"); return 1; }
    HWND window = CreateWindowExW(0, L"3fpReadPixelWindow", L"3fp read pixel",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, 960, 540, nullptr, nullptr,
        GetModuleHandleW(nullptr), nullptr);
    if (window == nullptr) { std::printf("CreateWindowExW failed\n"); return 1; }
    Pump(200);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = requestHdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.forceHdrOutput = 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    const auto created = FFF3FP_Create(&configuration, &g_player);
    if (created != FFFResult::Success || g_player == nullptr) { std::printf("Create: %d\n", static_cast<int>(created)); return 1; }
    const auto opened = FFF3FP_Open(g_player, path);
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (!g_openCompleted) { std::printf("Open timed out\n"); return 1; }
    Pump(300);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(g_player, &snapshot);
    std::printf("open: hdrFormat=%u hdrPath=%u dyn=%u peak=%u outBits=%u %ux%u\n",
        static_cast<unsigned>(snapshot.hdrFormat), static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive, snapshot.sourcePeakNits,
        snapshot.videoOutputBitDepth, snapshot.videoWidth, snapshot.videoHeight);

    FFF3FP_Play(g_player);
    // 等到呈现若干帧后再读，保证像素/元数据状态稳定。
    Pump(1500);
    FFF3FP_GetSnapshot(g_player, &snapshot);
    std::printf("play: hdrFormat=%u hdrPath=%u dyn=%u peak=%u outBits=%u presented=%llu\n",
        static_cast<unsigned>(snapshot.hdrFormat), static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive, snapshot.sourcePeakNits, snapshot.videoOutputBitDepth,
        static_cast<unsigned long long>(snapshot.presentedVideoFrames));

    // ⚠ ReadVideoPixel 的坐标域是后台缓冲/交换链尺寸（不是片源分辨率）——
    //   见 v0.2.5 的 PixelReadback 教训。窗口客户区 ≈ 960×540 内取固定点最稳。
    const std::uint32_t points[][2] = {
        { 480, 250 },
        { 240, 250 },
        { 720, 250 },
        { 480, 120 },
        { 480, 400 },
    };
    for (const auto& point : points) {
        FFF3FPVideoPixelProbe probe{};
        probe.size = sizeof(probe);
        probe.version = 20;
        probe.x = point[0];
        probe.y = point[1];
        const auto result = FFF3FP_ReadVideoPixel(g_player, &probe);
        if (result != FFFResult::Success) {
            std::printf("  (%u,%u) read failed: %d\n", point[0], point[1], static_cast<int>(result));
            continue;
        }
        std::printf("  (%4u,%4u) outBits=%u R=%.5f G=%.5f B=%.5f A=%.3f\n",
            point[0], point[1], probe.outputBitDepth, probe.red, probe.green, probe.blue, probe.alpha);
    }
    Pump(200);
    FFF3FP_Close(g_player);
    return 0;
}
