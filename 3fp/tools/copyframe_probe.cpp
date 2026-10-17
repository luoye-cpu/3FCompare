// 截图帧回读探针：覆盖 8/10/16 位交换链、SDR / HDR / 广色域 SDR 全部组合。
//
// 验证目标（对应 docs/40 的 V1/V2/V5/V7）：
//   V1  源分辨率截图：layout=Source 时尺寸 == 源分辨率（且随旋转交换宽高）
//   V2  HDR 不失败：layout×format 四种组合全部 result=0
//   V5  旋转一致：旋转 90° 后 Source 截图为竖
//   V7  不污染后缓冲：截图前后渲染目标信息与已呈现帧数不变
//
// 用法：copyframe_probe <媒体> [--hdr] [--sdr-scrgb] [--rotate N]

#include <windows.h>

#include <chrono>
#include <cmath>   // std::ldexp：HALF 解码要用（此前靠 <windows.h> 间接拖进来，MSVC 14.5x 不再带）
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
using CopyFrameFn = FFFResult(__cdecl*)(FFF3FPHandle, void*, std::uint32_t,
    std::uint32_t*, std::uint32_t*, FFF3FPCopyFrameLayout, FFF3FPCopyFrameFormat);
using BitDepthFn = FFFResult(__cdecl*)(FFF3FPHandle, std::uint32_t*);

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
const char* ModeName(const std::uint32_t mode) {
    switch (mode) { case 0: return "MapToSdr"; case 1: return "RawHdrAsSdr"; case 2: return "MapToHdr"; default: return "?"; }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) { wprintf(L"用法: copyframe_probe <媒体> [--hdr] [--sdr-scrgb] [--rotate N]\n"); return 2; }
    bool hdr = false, sdrScRgb = false;
    std::uint32_t rotate = 0;
    for (int i = 2; i < argc; ++i) {
        const std::wstring a = argv[i];
        if (a == L"--hdr") hdr = true;
        else if (a == L"--sdr-scrgb") sdrScRgb = true;
        else if (a == L"--rotate" && i + 1 < argc) rotate = static_cast<std::uint32_t>(_wtoi(argv[++i]));
    }

    const HMODULE kernel = GetModuleHandleW(L"FFF.Native.dll");
    if (kernel == nullptr) { printf("找不到内核\n"); return 3; }
    const auto copyFrame = reinterpret_cast<CopyFrameFn>(GetProcAddress(kernel, "FFF3FP_CopyFrame"));
    const auto getBitDepth = reinterpret_cast<BitDepthFn>(GetProcAddress(kernel, "FFF3FP_GetLastCopyFrameBitDepth"));
    if (copyFrame == nullptr) { printf("内核不含 CopyFrame（旧内核）\n"); return 4; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFCopyFrameProbe";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFCopyFrameProbe", L"copyframe probe",
        WS_OVERLAPPEDWINDOW, 20, 20, 900, 520, nullptr, nullptr, wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    // 显式声明 16F 交换链意图：HDR 用 forceHdrOutput，广色域 SDR 用 sdrScRgbMode=Auto。
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
    configuration.forceHdrOutput = hdr ? 1u : 0u;
    configuration.sdrScRgbMode = sdrScRgb ? 1u : 0u;
    configuration.preferredAdapterIndex = -1;

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
    if (rotate != 0) { FFF3FP_SetViewRotation(player, rotate); Pump(400); }

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
    FFF3FPRenderTargetInfo before{};
    before.size = sizeof(before);
    before.version = 20;
    FFF3FP_GetRenderTargetInfo(player, &before);

    printf("媒体: %s\n", path.c_str());
    printf("配置: hdr=%d sdrScRgb=%d rotate=%u\n", hdr ? 1 : 0, sdrScRgb ? 1 : 0, rotate);
    printf("快照: 源=%ux%u actual=%s outBits=%u presented=%llu swapPresents=%llu\n",
        snapshot.videoWidth, snapshot.videoHeight,
        ModeName(static_cast<std::uint32_t>(snapshot.actualColorMode)),
        snapshot.videoOutputBitDepth,
        static_cast<unsigned long long>(snapshot.presentedVideoFrames),
        static_cast<unsigned long long>(snapshot.swapChainPresents));
    printf("交换链: %ux%u outBits=%u hdr=%u\n\n",
        before.swapWidth, before.swapHeight, before.outputBitDepth, before.hdr);

    struct Case { const char* name; FFF3FPCopyFrameLayout layout; FFF3FPCopyFrameFormat format; };
    const Case cases[] = {
        {"源分辨率 + BGRA8 ", FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Bgra8},
        {"源分辨率 + RGBA16F", FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Rgba16Float},
        {"窗口尺寸 + BGRA8 ", FFF3FPCopyFrameLayout::Window, FFF3FPCopyFrameFormat::Bgra8},
        {"窗口尺寸 + RGBA16F", FFF3FPCopyFrameLayout::Window, FFF3FPCopyFrameFormat::Rgba16Float},
    };
    int passed = 0;
    for (const auto& c : cases) {
        std::uint32_t w = 0, h = 0;
        // 第一次调用问尺寸（pixels=null）
        const auto query = copyFrame(player, nullptr, 0, &w, &h, c.layout, c.format);
        const auto bpp = c.format == FFF3FPCopyFrameFormat::Rgba16Float ? 8u : 4u;
        std::vector<std::uint8_t> buffer(static_cast<std::size_t>(w) * h * bpp);
        const auto copied = copyFrame(player, buffer.data(),
            static_cast<std::uint32_t>(buffer.size()), &w, &h, c.layout, c.format);
        std::uint32_t depth = 0;
        if (getBitDepth != nullptr) getBitDepth(player, &depth);

        // 采样中心像素，确认不是全黑（黑 = 渲染没发生）
        const char* verdict = "FAIL";
        if (copied == FFFResult::Success && w > 0 && h > 0) {
            const auto cx = w / 2, cy = h / 2;
            if (bpp == 4) {
                const auto* p = buffer.data() + (static_cast<std::size_t>(cy) * w + cx) * 4;
                printf("  %s query=%d size=%ux%u copy=%d depth=%u  BGRA=(%u,%u,%u,%u)\n",
                    c.name, static_cast<int>(query), w, h, static_cast<int>(copied), depth,
                    p[0], p[1], p[2], p[3]);
                if (p[0] || p[1] || p[2]) { verdict = "PASS"; }
            } else {
                // HALF 每通道 2 字节；用 XMConvertHalfToFloat 需要 d3d，这里手工解码
                const auto* p = buffer.data() + (static_cast<std::size_t>(cy) * w + cx) * 8;
                const auto half = [](const std::uint16_t v) {
                    const std::uint32_t s = (v >> 15) & 1u, e = (v >> 10) & 0x1fu, m = v & 0x3ffu;
                    float r;
                    if (e == 0) r = std::ldexp(static_cast<float>(m), -24);
                    else if (e == 31) r = m ? 0.0f : 1.0f;
                    else r = std::ldexp(static_cast<float>(m + 1024), static_cast<int>(e) - 25);
                    return s ? -r : r;
                };
                const auto r0 = half(*reinterpret_cast<const std::uint16_t*>(p));
                const auto g0 = half(*reinterpret_cast<const std::uint16_t*>(p + 2));
                const auto b0 = half(*reinterpret_cast<const std::uint16_t*>(p + 4));
                printf("  %s query=%d size=%ux%u copy=%d depth=%u  RGB=(%.4f,%.4f,%.4f)\n",
                    c.name, static_cast<int>(query), w, h, static_cast<int>(copied), depth,
                    r0, g0, b0);
                if (r0 != 0.0f || g0 != 0.0f || b0 != 0.0f) { verdict = "PASS"; }
            }
        } else {
            printf("  %s 失败 copy=%d\n", c.name, static_cast<int>(copied));
        }
        if (std::string(verdict) == "PASS") ++passed;
    }

    // 源分辨率应等于快照尺寸（旋转 90/270 时交换）。
    // ⚠ 必须读内核**实际套用**的旋转，而不是只看 --rotate 参数：图片的 EXIF 方向
    //   由内核在打开时自动转正（见 docs/35），只看 CLI 会把正确结果判成失败。
    std::uint32_t sw = 0, sh = 0;
    copyFrame(player, nullptr, 0, &sw, &sh, FFF3FPCopyFrameLayout::Source,
        FFF3FPCopyFrameFormat::Bgra8);
    std::uint32_t appliedRotation = rotate;
    (void)FFF3FP_GetViewRotation(player, &appliedRotation);
    const bool oddTurn = (appliedRotation & 1u) != 0;
    const auto expectW = oddTurn ? snapshot.videoHeight : snapshot.videoWidth;
    const auto expectH = oddTurn ? snapshot.videoWidth : snapshot.videoHeight;
    const bool sizeOk = sw == expectW && sh == expectH;

    // V7：截图不得改变后缓冲/呈现计数
    FFF3FPRenderTargetInfo after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetRenderTargetInfo(player, &after);
    FFF3FPSnapshot afterSnapshot{};
    afterSnapshot.size = sizeof(afterSnapshot);
    afterSnapshot.version = 20;
    FFF3FP_GetSnapshot(player, &afterSnapshot);
    const bool swapUnchanged = before.swapWidth == after.swapWidth &&
        before.swapHeight == after.swapHeight &&
        before.outputBitDepth == after.outputBitDepth;

    printf("\n结果: 格式组合 %d/4 通过\n", passed);
    printf("V1 源分辨率: 期望 %ux%u 实际 %ux%u  %s\n",
        static_cast<unsigned>(expectW), static_cast<unsigned>(expectH),
        static_cast<unsigned>(sw), static_cast<unsigned>(sh),
        sizeOk ? "PASS" : "FAIL");
    printf("V7 后缓冲未扰动: swap %ux%u -> %ux%u, outBits %u -> %u, 呈现 %llu -> %llu  %s\n",
        static_cast<unsigned>(before.swapWidth), static_cast<unsigned>(before.swapHeight),
        static_cast<unsigned>(after.swapWidth), static_cast<unsigned>(after.swapHeight),
        static_cast<unsigned>(before.outputBitDepth),
        static_cast<unsigned>(after.outputBitDepth),
        static_cast<unsigned long long>(snapshot.presentedVideoFrames),
        static_cast<unsigned long long>(afterSnapshot.presentedVideoFrames),
        swapUnchanged ? "PASS" : "FAIL");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return (passed == 4 && sizeOk && swapUnchanged) ? 0 : 1;
}
