// 截图链路诊断探针
//
// 目的：把"截图有问题"从主观感受变成可测数据。逐项验证：
//   1) CopySdrFrame（内核唯一的 GPU 回读口）返回的是**源分辨率**还是**窗口尺寸**？
//   2) 在 HDR / 10-bit 输出下它是否直接失败（源码只接受 BGRA8）？
//   3) ReadVideoPixelRegion（真正的帧回读）能否给出源分辨率像素？
//   4) 窗口被遮挡时屏幕抓取会抓到什么（用 WGC/GDI 侧无法在此测，改为对比两口径）
//
// 用法：shot_probe <媒体路径> [--hdr]
//
// 构建：cl /EHsc /std:c++20 /I <内核根> shot_probe.cpp <路径>\FFF.Native.lib user32.lib

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_openCompleted = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(10);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) { wprintf(L"用法: shot_probe <媒体路径> [--hdr]\n"); return 2; }
    bool hdr = false;
    for (int i = 2; i < argc; ++i) if (std::wstring(argv[i]) == L"--hdr") hdr = true;

    // Window deliberately sized differently from the video so "which size came back"
    // is unambiguous: any result equal to the window size proves it is not the frame.
    constexpr int kWindowWidth = 900;
    constexpr int kWindowHeight = 520;

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFShotProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFShotProbeWnd", L"shot probe",
        WS_OVERLAPPEDWINDOW, 40, 40, kWindowWidth, kWindowHeight, nullptr, nullptr,
        wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = hdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = hdr ? 1000.0f : 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;
    configuration.forceHdrOutput = hdr ? 1u : 0u;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(player);
    Pump(1000);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    const auto snapResult = FFF3FP_GetSnapshot(player, &snapshot);
    printf("媒体: %s\n", path.c_str());
    printf("请求色彩模式: %s\n", hdr ? "MapToHdr(强制HDR输出)" : "MapToSdr");
    if (snapResult == FFFResult::Success) {
        printf("快照: 源=%ux%u  actual色模=%d  outBits=%u  state=%d\n",
            snapshot.videoWidth, snapshot.videoHeight,
            static_cast<int>(snapshot.actualColorMode), snapshot.videoOutputBitDepth,
            static_cast<int>(snapshot.state));
    } else {
        printf("快照读取失败 result=%d\n", static_cast<int>(snapResult));
    }

    FFF3FPRenderTargetInfo rt{};
    rt.size = sizeof(rt);
    rt.version = 2;
    if (FFF3FP_GetRenderTargetInfo(player, &rt) == FFFResult::Success)
        printf("渲染目标: swap=%ux%u client=%ux%u dest=(%u,%u) %ux%u outBits=%u hdr=%u\n",
            rt.swapWidth, rt.swapHeight, rt.clientWidth, rt.clientHeight,
            rt.destX, rt.destY, rt.destWidth, rt.destHeight, rt.outputBitDepth, rt.hdr);

    // ---- 1) CopySdrFrame：返回什么尺寸？HDR 下能否成功？ ----
    printf("\n=== 1) FFF3FP_CopySdrFrame（宿主截图菜单**没有**用它，但它是内核唯一 GPU 回读口）===\n");
    std::uint32_t w = 0, h = 0;
    auto probe = FFF3FP_CopySdrFrame(player, nullptr, 0, &w, &h, 0u);
    printf("  尺寸查询 result=%d  ->  %ux%u\n", static_cast<int>(probe), w, h);
    if (probe == FFFResult::BufferTooSmall && w > 0 && h > 0) {
        printf("  ⚠ 期望源分辨率 %ux%u；实际返回 %ux%u（= 窗口/交换链尺寸则为缺陷）\n",
            snapshot.videoWidth, snapshot.videoHeight, w, h);
        std::vector<std::uint8_t> buffer(static_cast<std::size_t>(w) * h * 4);
        const auto copied = FFF3FP_CopySdrFrame(player, buffer.data(),
            static_cast<std::uint32_t>(buffer.size()), &w, &h, 0u);
        printf("  拷贝 result=%d", static_cast<int>(copied));
        if (copied == FFFResult::Success) {
            // 采样中心与角点，确认是否有真实像素（而非全黑/全零）
            const auto at = [&](unsigned x, unsigned y, int c) {
                return buffer[(static_cast<std::size_t>(y) * w + x) * 4 + c];
            };
            printf("  中心像素 BGRA=(%u,%u,%u,%u) 左上=(%u,%u,%u,%u)",
                at(w / 2, h / 2, 0), at(w / 2, h / 2, 1), at(w / 2, h / 2, 2), at(w / 2, h / 2, 3),
                at(1, 1, 0), at(1, 1, 1), at(1, 1, 2), at(1, 1, 3));
        } else {
            printf("  ⚠ 拷贝失败（源码只接受 DXGI_FORMAT_B8G8R8A8_UNORM；HDR/scRGB/10bit 会走 NotSupported）");
        }
        printf("\n");
    }

    // ---- 2) ReadVideoPixelRegion：真正的帧回读，能否给出源尺寸？ ----
    printf("\n=== 2) FFF3FP_ReadVideoPixelRegion（从底缓冲回读，非屏幕抓取）===\n");
    const unsigned regionW = snapshot.videoWidth > 8 ? 8u : snapshot.videoWidth;
    const unsigned regionH = snapshot.videoHeight > 8 ? 8u : snapshot.videoHeight;
    std::vector<float> region(static_cast<std::size_t>(regionW) * regionH * 4);
    std::uint32_t outBits = 0;
    const auto regionResult = FFF3FP_ReadVideoPixelRegion(player,
        snapshot.videoWidth / 2, snapshot.videoHeight / 2, regionW, regionH,
        region.data(), static_cast<std::uint32_t>(region.size()), &outBits);
    printf("  回读 %ux%u result=%d outBits=%u", regionW, regionH,
        static_cast<int>(regionResult), outBits);
    if (regionResult == FFFResult::Success)
        printf("  首像素 RGBA=(%.3f,%.3f,%.3f,%.3f)",
            region[0], region[1], region[2], region[3]);
    printf("\n  => 这条路径能给出真实色彩值（HDR 下是线性 nits/80），但坐标域是底缓冲，\n");
    printf("     宿主若要原图像素必须自己做 dest->source 的坐标反解。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
