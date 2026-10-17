// 音频健康度告警的**触发验证**。
//
// 为什么需要：刚接线的 UI 告警行只有在异常时才显示。若从不触发，它就是个死代码，
// 而这正是本轮反复出现的问题类型（"永远非零的抖动计数" / "从不上报的静音队列"）。
// 所以必须证明：正常素材**不显示**，异常素材**显示**。
//
// 复用与 UI 相同的判定逻辑（通道是否非空），并打印各计数值。
//
// 用法：audio_health_probe <媒体> [--seconds N]

#include <windows.h>

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

/// 与 播放器信息图层呈现器.音频健康度 相同的判定
std::string Health(const FFF3FPSnapshot& s, std::vector<std::string>& details) {
    if (s.audioDiscontinuities > 0) details.push_back("不连续 " + std::to_string(s.audioDiscontinuities));
    if (s.audioTimestampJitterFrames > 0) details.push_back("抖动 " + std::to_string(s.audioTimestampJitterFrames));
    if (s.audioInsertedSilenceFrames > 0) details.push_back("补静音 " + std::to_string(s.audioInsertedSilenceFrames));
    if (s.audioDroppedOverlapFrames > 0) details.push_back("丢重叠 " + std::to_string(s.audioDroppedOverlapFrames));
    if (s.audioRejectedFrames > 0) details.push_back("拒绝 " + std::to_string(s.audioRejectedFrames));
    std::string joined;
    for (std::size_t i = 0; i < details.size(); ++i) {
        if (i > 0) joined += "   ";
        joined += details[i];
    }
    return joined;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: audio_health_probe <媒体> [--seconds N]\n"); return 2; }
    int seconds = 6;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFAudioHealthWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFAudioHealthWnd", L"health",
        WS_OVERLAPPEDWINDOW, 20, 20, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);

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
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FP_Play(player);
    Pump(seconds * 1000);

    FFF3FPSnapshot s{};
    s.size = sizeof(s); s.version = 20;
    FFF3FP_GetSnapshot(player, &s);

    std::vector<std::string> details;
    const auto health = Health(s, details);
    const auto shown = !health.empty();

    printf("解码音频帧=%llu  缓冲=%.3fs  欠载=%llu\n",
        static_cast<unsigned long long>(s.decodedAudioFrames),
        static_cast<double>(s.bufferedAudio100ns) / 1e7,
        static_cast<unsigned long long>(s.audioUnderruns));
    printf("不连续=%llu  抖动=%llu  补静音=%llu  丢重叠=%llu  拒绝=%llu\n",
        static_cast<unsigned long long>(s.audioDiscontinuities),
        static_cast<unsigned long long>(s.audioTimestampJitterFrames),
        static_cast<unsigned long long>(s.audioInsertedSilenceFrames),
        static_cast<unsigned long long>(s.audioDroppedOverlapFrames),
        static_cast<unsigned long long>(s.audioRejectedFrames));
    printf("\nUI 告警行: %s\n", shown ? health.c_str() : "（不显示 —— 音频健康）");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return shown ? 1 : 0;   // 退出码 1 = 显示告警（便于脚本断言）
}
