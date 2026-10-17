// HdrProcessor Vivid/HLG 峰值核查：确认补丁是否修正了 HLG 基线的峰值误判。
//
// 背景：HDR Vivid 素材若基线是 HLG，`frame->color_trc` 为 UNSPECIFIED，
// 而补丁前的代码无条件按 PQ 的绝对域解读 `maximum_maxrgb * 10000`，
// 对 HLG 基线会放大约 10 倍。历史记录（build/hdr10plus-baseline/vivid_hlg.txt）
// 显示同一素材曾报 peak=10000。
//
// 本探针把"打开后"与"解码若干帧后"的峰值都打出来，并与流的色彩属性对照。
//
// 用法：vivid_peak_probe <媒体> [--seconds N]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
volatile bool g_open = false;
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
void Show(const FFF3FPHandle player, const char* when) {
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = 20;
    FFF3FP_GetSnapshot(player, &s);
    printf("  %-10s hdrFormat=%u hdrPath=%u 源峰值=%u 呈现=%llu 丢帧=%llu outputBits=%u\n",
        when, s.hdrFormat, s.hdrProcessingPath, s.sourcePeakNits,
        static_cast<unsigned long long>(s.presentedVideoFrames),
        static_cast<unsigned long long>(s.droppedVideoFrames), s.videoOutputBitDepth);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: vivid_peak_probe <媒体> [--seconds N]\n"); return 2; }
    int seconds = 4;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFVividPeakWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFVividPeakWnd", L"vivid",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    printf("文件: %s\n", path.c_str());
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);
    Show(player, "open后");

    FFF3FP_Play(player);
    Pump(seconds * 1000);
    FFF3FP_Pause(player);
    Pump(200);
    Show(player, "播放后");

    printf("\n判读：\n");
    printf("  HLG 基线的 Vivid 素材**不应**报 ~10000 nits（那是 PQ 绝对域的解读）。\n");
    printf("  历史记录见 build/hdr10plus-baseline/vivid_hlg.txt（曾报 peak=10000）。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
