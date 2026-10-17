// VSR 像素级验证：对比 VSR 开/关的实际输出差异，确认它真的改变了画面而不是只置了标志。
//
// 判据：
//   1. VSR 开时像素应与关时**不同**（否则说明只是标了 active 却没生效）
//   2. 差异应集中在细节边缘（VSR 是 AI 上采样），而非全图偏移/变色
//   3. 平均亮度应接近（VSR 不应改变曝光）
//
// 用法：vsr_pixel_probe <媒体> [--sr-window WxH] [--samples N]

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
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

struct Capture {
    bool ok = false;
    std::uint32_t width = 0, height = 0;
    std::vector<float> pixels;   // RGBA
};

Capture Grab(FFF3FPHandle player, const std::uint32_t width, const std::uint32_t height) {
    Capture out;
    if (width == 0 || height == 0) return out;
    out.width = width;
    out.height = height;
    out.pixels.assign(static_cast<std::size_t>(width) * height * 4, 0.0f);
    std::uint32_t bitDepth = 0;
    out.ok = FFF3FP_ReadVideoPixelRegion(player, 0, 0, width, height, out.pixels.data(),
        static_cast<std::uint32_t>(out.pixels.size()), &bitDepth) == FFFResult::Success;
    return out;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: vsr_pixel_probe <媒体> [--sr-window WxH]\n"); return 2; }
    unsigned windowWidth = 2560, windowHeight = 1440;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--sr-window" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%ux%u", &windowWidth, &windowHeight);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFVSRPixelWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFVSRPixelWnd", L"vsr", WS_OVERLAPPEDWINDOW,
        20, 20, static_cast<int>(windowWidth), static_cast<int>(windowHeight),
        nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    // VSR requires a source texture carrying D3D11_BIND_DECODER, which only hardware
    // decoding produces (CanUseVideoProcessorForUpscale tests exactly that). Running
    // this probe on software decode measured "VSR changed nothing", when in fact VSR
    // had correctly declined the source. Use hardware decode, as the media needs.
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
    const auto width = snapshot.videoWidth;
    const auto height = snapshot.videoHeight;
    printf("源 %ux%u  窗口 %ux%u\n", width, height, windowWidth, windowHeight);
    if (width == 0 || height == 0) { printf("无尺寸信息\n"); FFF3FP_Destroy(player); return 1; }

    // 同一位置取两帧：VSR 关 -> 开。位置固定，差异只能来自 VSR。
    FFF3FP_SetVideoSuperResolution(player, FFF3FPVideoSuperResolution::Off);
    FFF3FP_Play(player);
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(600);
    const auto off = Grab(player, width, height);

    FFF3FP_SetVideoSuperResolution(player, FFF3FPVideoSuperResolution::Auto);
    FFF3FP_Redraw(player);
    Pump(1500);
    const auto on = Grab(player, width, height);

    FFF3FPVideoSuperResolutionStatus status{};
    status.size = sizeof(status);
    status.version = 20;
    FFF3FP_GetVideoSuperResolutionStatus(player, &status);

    printf("\nVSR 状态: requested=%d active=%d rejected=%d src=%ux%u target=%ux%u\n",
        static_cast<int>(status.requested), status.active ? 1 : 0, status.driverRejected ? 1 : 0,
        status.sourceWidth, status.sourceHeight, status.targetWidth, status.targetHeight);

    if (!off.ok || !on.ok) {
        printf("像素回读失败 (off=%d on=%d)\n", off.ok ? 1 : 0, on.ok ? 1 : 0);
    } else {
        double sumOff = 0, sumOn = 0, maxDiff = 0, sumDiff = 0;
        std::size_t differing = 0;
        const auto count = static_cast<std::size_t>(width) * height;
        for (std::size_t i = 0; i < count; ++i) {
            const auto base = i * 4;
            for (int c = 0; c < 3; ++c) {
                const auto a = off.pixels[base + c];
                const auto b = on.pixels[base + c];
                sumOff += a; sumOn += b;
                const auto d = std::fabs(a - b);
                sumDiff += d;
                if (d > maxDiff) maxDiff = d;
                if (d > 0.004f) ++differing;
            }
        }
        const auto samples = static_cast<double>(count) * 3.0;
        printf("\n平均亮度: 关=%.4f  开=%.4f  (差 %.4f)\n",
            sumOff / samples, sumOn / samples, (sumOn - sumOff) / samples);
        printf("像素差异: 平均=%.4f  最大=%.4f  超过阈值的样本=%.2f%%\n",
            sumDiff / samples, maxDiff, 100.0 * differing / samples);
        printf("判定: %s\n",
            (sumDiff / samples) > 0.0005 ? "VSR **确实改变了输出**（非仅置标志）"
                                         : "VSR 未改变输出 —— 需排查");
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
