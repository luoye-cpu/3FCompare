// scRGB（16bit）路径下的 VSR 验证。
//
// 背景：VSR 会把上采样结果**替换源**，再走正常着色器链。在 SDR 链上已验证正常，
// 但 HDR 输出会切到 R16G16B16A16_FLOAT 的 scRGB 交换链（outBits=16），
// 这条分支此前因显示器未开 HDR 而无法测试。
//
// 判据：
//   1. actualColorMode 应为 MapToHdr，outBits 应为 16（scRGB 生效）
//   2. VSR 应能 active（前提：硬解 + 上采样 + 源高 360..1440）
//   3. VSR 开/关的像素应不同，且亮度无整体偏移
//
// 用法：vsr_scrgb_probe <媒体> [--sr-window WxH]

#include <windows.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

using Microsoft::WRL::ComPtr;

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

/// 直接问 Windows：这个显示器现在是否处于 HDR 模式。
void ReportDisplayHdr(const HWND window) {
    ComPtr<IDXGIFactory6> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) { printf("  (无 DXGI 工厂)\n"); return; }
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW info{}; info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) { printf("  (无监视器信息)\n"); return; }
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        for (UINT output = 0;; ++output) {
            ComPtr<IDXGIOutput> candidate;
            if (adapter->EnumOutputs(output, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{};
            if (FAILED(candidate->GetDesc(&desc))) continue;
            if (wcscmp(desc.DeviceName, info.szDevice) != 0) continue;
            ComPtr<IDXGIOutput6> output6;
            if (SUCCEEDED(candidate.As(&output6))) {
                DXGI_OUTPUT_DESC1 desc1{};
                if (SUCCEEDED(output6->GetDesc1(&desc1))) {
                    printf("  显示器 %ls: HDR=%s  maxLum=%.0f nits  minLum=%.4f  bits=%u\n",
                        desc1.DeviceName,
                        desc1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? "开" : "关",
                        desc1.MaxLuminance, desc1.MinLuminance, desc1.BitsPerColor);
                }
            }
            return;
        }
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: vsr_scrgb_probe <媒体> [--sr-window WxH]\n"); return 2; }
    unsigned windowWidth = 2560, windowHeight = 1440;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--sr-window" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%ux%u", &windowWidth, &windowHeight);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFVSRScRgbWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFVSRScRgbWnd", L"scrgb", WS_OVERLAPPEDWINDOW,
        20, 20, static_cast<int>(windowWidth), static_cast<int>(windowHeight),
        nullptr, nullptr, wc.hInstance, nullptr);

    printf("== 显示器 HDR 状态（Windows 报告）==\n");
    ReportDisplayHdr(window);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;      // VSR 要求硬解纹理
    configuration.colorMode = FFF3FPColorMode::MapToHdr;      // 触发 scRGB 交换链
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

    FFF3FP_SetVideoSuperResolution(player, FFF3FPVideoSuperResolution::Off);
    FFF3FP_Play(player);
    Pump(1800);
    FFF3FP_Pause(player);
    Pump(700);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    FFF3FP_GetSnapshot(player, &snapshot);
    const auto width = snapshot.videoWidth;
    const auto height = snapshot.videoHeight;
    printf("\n== 会话状态 ==\n");
    printf("  源=%ux%u  请求=%d  实际=%d  outBits=%u\n", width, height,
        static_cast<int>(snapshot.requestedColorMode),
        static_cast<int>(snapshot.actualColorMode), snapshot.videoOutputBitDepth);
    if (width == 0 || height == 0) { printf("无尺寸\n"); FFF3FP_Destroy(player); return 1; }

    const auto grab = [&](std::vector<float>& out) {
        out.assign(static_cast<std::size_t>(width) * height * 4, 0.0f);
        std::uint32_t bitDepth = 0;
        return FFF3FP_ReadVideoPixelRegion(player, 0, 0, width, height, out.data(),
            static_cast<std::uint32_t>(out.size()), &bitDepth) == FFFResult::Success;
    };
    std::vector<float> offPixels, onPixels;
    const auto offOk = grab(offPixels);

    FFF3FP_SetVideoSuperResolution(player, FFF3FPVideoSuperResolution::Auto);
    FFF3FP_Redraw(player);
    Pump(1800);
    const auto onOk = grab(onPixels);

    FFF3FPVideoSuperResolutionStatus status{};
    status.size = sizeof(status);
    status.version = 1;
    FFF3FP_GetVideoSuperResolutionStatus(player, &status);
    printf("\n== VSR 状态 ==\n");
    printf("  requested=%d active=%d rejected=%d reason=%d\n",
        static_cast<int>(status.requested), status.active ? 1 : 0,
        status.driverRejected ? 1 : 0, static_cast<int>(status.reason));
    printf("  src=%ux%u  target=%ux%u\n", status.sourceWidth, status.sourceHeight,
        status.targetWidth, status.targetHeight);

    if (!offOk || !onOk) {
        printf("\n像素回读失败 (off=%d on=%d)\n", offOk ? 1 : 0, onOk ? 1 : 0);
    } else {
        double sumOff = 0, sumOn = 0, maxDiff = 0, sumDiff = 0;
        std::size_t differing = 0;
        const auto count = static_cast<std::size_t>(width) * height;
        for (std::size_t i = 0; i < count; ++i) {
            const auto base = i * 4;
            for (int c = 0; c < 3; ++c) {
                const auto a = offPixels[base + c], b = onPixels[base + c];
                sumOff += a; sumOn += b;
                const auto d = std::fabs(a - b);
                sumDiff += d;
                if (d > maxDiff) maxDiff = d;
                if (d > 0.004f) ++differing;
            }
        }
        const auto samples = static_cast<double>(count) * 3.0;
        printf("\n== 像素比对（scRGB 路径）==\n");
        printf("  平均亮度: 关=%.4f  开=%.4f  (差 %+.4f)\n",
            sumOff / samples, sumOn / samples, (sumOn - sumOff) / samples);
        printf("  像素差异: 平均=%.4f  最大=%.4f  超阈值=%.2f%%\n",
            sumDiff / samples, maxDiff, 100.0 * differing / samples);
        printf("  判定: %s\n", (sumDiff / samples) > 0.0005
            ? "VSR 在 scRGB 路径**确实生效**" : "VSR 未改变输出 —— 需排查");
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
