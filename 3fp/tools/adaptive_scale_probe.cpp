// 验证"自适应预缩放"：软解丢帧过多时，先把帧缩到显示尺寸再上传。
//
// 用法：adaptive_scale_probe <媒体> --decode cpu|gpu --seconds N [--threshold P]
//
// 观察点：
//  1. 软解 + 丢帧多 => 应打印 [adaptive] engaged，且上传耗时/带宽大幅下降
//  2. 硬解 => 策略**完全不参与**（无 [adaptive] 日志，行为与原来一致）
//  3. 阈值设 99 => 不应触发

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

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
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: adaptive_scale_probe <媒体> [--decode cpu|gpu] [--seconds N] [--threshold P] [--off]\n"); return 2; }
    bool gpu = false, off = false;
    int seconds = 6, threshold = 20;
    for (int i = 2; i < argc; ++i) {
        const std::wstring k = argv[i];
        if (k == L"--decode" && i + 1 < argc) gpu = std::wstring(argv[++i]) == L"gpu";
        else if (k == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (k == L"--threshold" && i + 1 < argc) threshold = _wtoi(argv[++i]);
        else if (k == L"--off") off = true;
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFAdaptiveScaleWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFAdaptiveScaleWnd", L"adaptive",
        WS_OVERLAPPEDWINDOW, 20, 20, 1600, 900, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = gpu ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;
    configuration.adaptiveDownscaleBeforeUpload = off ? 0u : 1u;
    configuration.adaptiveDownscaleDropPercent = static_cast<std::uint32_t>(threshold);

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 20;
    FFF3FP_GetSnapshot(player, &before);

    FFF3FP_Play(player);
    Pump(seconds * 1000);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetSnapshot(player, &after);

    const auto frames = static_cast<double>(after.presentedVideoFrames - before.presentedVideoFrames);
    const auto dropped = static_cast<double>(after.droppedVideoFrames - before.droppedVideoFrames);
    const auto uploadMs = static_cast<double>(after.videoUpload100ns - before.videoUpload100ns) / 10000.0;
    const auto presentMs = static_cast<double>(after.presentWait100ns - before.presentWait100ns) / 10000.0;

    printf("\n配置: 自适应=%s  阈值=%d%%  解码=%s\n", off ? "关" : "开", threshold, gpu ? "硬解" : "软解");
    printf("呈现帧=%.0f (%.1f fps)  丢帧=%.0f\n", frames, frames / seconds, dropped);
    if (frames > 0)
        printf("上传 %.2f ms/帧   Present %.2f ms/帧\n", uploadMs / frames, presentMs / frames);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
