// 遮挡场景验证：截图在窗口被完全覆盖时是否仍正确。
//
// 这是本次修复的**核心价值**：旧实现走 Graphics.CopyFromScreen，
// 窗口被遮挡时会抓到遮挡物的像素；新实现离屏渲染，与窗口可见性无关。
//
// 做法：同一个会话跑两次 CopyFrame —— 一次窗口正常，一次用一个置顶窗口
// 完全盖住它。若两次的源分辨率像素完全一致，说明离屏路径不受遮挡影响。
//
// 用法：occlusion_probe <媒体>

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
using CopyFrameFn = FFFResult(__cdecl*)(FFF3FPHandle, void*, std::uint32_t,
    std::uint32_t*, std::uint32_t*, FFF3FPCopyFrameLayout, FFF3FPCopyFrameFormat);

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
bool Grab(CopyFrameFn copyFrame, FFF3FPHandle player, std::vector<std::uint8_t>& out,
    std::uint32_t& w, std::uint32_t& h) {
    printf("    Grab: query...\n");
    const auto query = copyFrame(player, nullptr, 0, &w, &h,
        FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Bgra8);
    printf("    Grab: query=%d size=%ux%u\n", static_cast<int>(query),
        static_cast<unsigned>(w), static_cast<unsigned>(h));
    // Only "buffer too small" means a frame is available; anything else (InvalidState
    // etc.) must be reported rather than forced through.
    if (query != FFFResult::BufferTooSmall) return false;
    out.assign(static_cast<std::size_t>(w) * h * 4u, 0);
    printf("    Grab: fetching %zu bytes...\n", out.size());
    const auto copied = copyFrame(player, out.data(),
        static_cast<std::uint32_t>(out.size()), &w, &h,
        FFF3FPCopyFrameLayout::Source, FFF3FPCopyFrameFormat::Bgra8);
    printf("    Grab: copy=%d\n", static_cast<int>(copied));
    return copied == FFFResult::Success;
}
std::size_t DifferingBytes(const std::vector<std::uint8_t>& a,
    const std::vector<std::uint8_t>& b) {
    if (a.size() != b.size()) return a.size() > b.size() ? a.size() : b.size();
    std::size_t n = 0;
    for (std::size_t i = 0; i < a.size(); ++i) if (a[i] != b[i]) ++n;
    return n;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    // stdout is block-buffered when redirected; a crash would lose every earlier line.
    // Flush after each step so the last printed marker localises the fault.
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: occlusion_probe <媒体>\n"); return 2; }
    printf("[1] 参数 OK\n");

    const HMODULE kernel = GetModuleHandleW(L"FFF.Native.dll");
    const auto copyFrame = reinterpret_cast<CopyFrameFn>(
        kernel ? GetProcAddress(kernel, "FFF3FP_CopyFrame") : nullptr);
    if (copyFrame == nullptr) { printf("内核不含 CopyFrame\n"); return 4; }
    printf("[2] CopyFrame 解析 OK\n");

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFOcclusionProbe";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFOcclusionProbe", L"occlusion probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 100, 100, 800, 480, nullptr, nullptr,
        wc.hInstance, nullptr);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;

printf("[3] 建窗口 OK\n");
FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); return 7;
    }
printf("[4] Open OK\n");
for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(player);
    Pump(1200);
    // Pause before comparing: a playing video advances between the two captures, so a
    // pixel diff would measure elapsed frames rather than the occlusion effect. The
    // comparison is only meaningful on a frozen frame.
    FFF3FP_Pause(player);
    Pump(700);

    // ① 正常可见
    SetWindowPos(window, HWND_TOP, 100, 100, 800, 480, SWP_SHOWWINDOW);
    Pump(600);
printf("[5] 开始可见态截图\n");
std::vector<std::uint8_t> visible;
    std::uint32_t w1 = 0, h1 = 0;
    if (!Grab(copyFrame, player, visible, w1, h1)) { printf("可见态截图失败\n"); return 8; }
    printf("可见态: %ux%u bytes=%zu\n", static_cast<unsigned>(w1), static_cast<unsigned>(h1),
        visible.size());

    // ② 用一个覆盖窗口完全盖住播放器（置顶且更大）
printf("[6] 创建遮挡窗口\n");
const HWND cover = CreateWindowExW(WS_EX_TOPMOST, L"FFFOcclusionProbe", L"cover",
        WS_POPUP | WS_VISIBLE, 0, 0, 1920, 1080, nullptr, nullptr, wc.hInstance, nullptr);
    SetWindowPos(cover, HWND_TOPMOST, 0, 0, 1920, 1080, SWP_SHOWWINDOW);
    Pump(900);
printf("[7] 开始遮挡态截图\n");
std::vector<std::uint8_t> occluded;
    std::uint32_t w2 = 0, h2 = 0;
    if (!Grab(copyFrame, player, occluded, w2, h2)) { printf("遮挡态截图失败\n"); return 9; }
    printf("遮挡态: %ux%u bytes=%zu\n", static_cast<unsigned>(w2), static_cast<unsigned>(h2),
        occluded.size());

    DestroyWindow(cover);
    Pump(300);

    const auto diff = DifferingBytes(visible, occluded);
    printf("\n尺寸一致: %s\n", (w1 == w2 && h1 == h2) ? "PASS" : "FAIL");
    printf("像素差异: %zu / %zu 字节\n", diff, visible.size());
    printf("遮挡下截图内容一致: %s\n", diff == 0 ? "PASS" : "FAIL");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return diff == 0 ? 0 : 1;
}