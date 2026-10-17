// 音频管线精度核查：长时间播放的 A/V 漂移与时钟稳定性。
//
// 检查项：
//   ① 音频位置与播放位置是否长期保持一致（漂移 => 两者分离）
//   ② 时钟是否单调（QPC 外推不应回退）
//   ③ 缓冲是否稳定（不稳定说明生产/消费速率不匹配）
//   ④ 欠载/不连续是否随时间增长
//
// 用法：audio_drift_probe <媒体> --seconds N [--interval 秒]

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
    if (argc < 2) { wprintf(L"用法: audio_drift_probe <媒体> --seconds N [--interval 秒]\n"); return 2; }
    int seconds = 30, interval = 5;
    for (int i = 2; i < argc; ++i) {
        const std::wstring a = argv[i];
        if (a == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (a == L"--interval" && i + 1 < argc) interval = _wtoi(argv[++i]);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFAudioDriftWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFAudioDriftWnd", L"drift",
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

    FFF3FPSnapshot s{};
    s.size = sizeof(s); s.version = 20;
    FFF3FP_GetSnapshot(player, &s);
    const auto durationSeconds = static_cast<double>(s.duration100ns) / 1e7;
    printf("时长 %.2fs   运行 %d 秒\n\n", durationSeconds, seconds);

    FFF3FP_Play(player);
    printf("%6s %11s %11s %9s %9s %9s %7s %7s\n",
        "墙钟s", "播放位置", "音频位置", "位置-音频", "缓冲", "欠载", "抖动", "不连续");

    double firstGap = 0.0;
    double maxAbsGap = 0.0;
    double previousPosition = -1.0;
    int monotonicViolations = 0;
    std::uint64_t underruns0 = 0;
    for (int t = interval; t <= seconds; t += interval) {
        Pump(interval * 1000);
        FFF3FP_GetSnapshot(player, &s);
        const auto pos = static_cast<double>(s.position100ns) / 1e7;
        const auto audio = static_cast<double>(s.audioPosition100ns) / 1e7;
        const auto buf = static_cast<double>(s.bufferedAudio100ns) / 1e7;
        const auto gap = pos - audio;
        if (t == interval) { firstGap = gap; underruns0 = s.audioUnderruns; }
        if (std::abs(gap) > maxAbsGap) maxAbsGap = std::abs(gap);
        if (previousPosition >= 0 && pos < previousPosition - 0.001) ++monotonicViolations;
        previousPosition = pos;
        printf("%6d %11.3f %11.3f %9.4f %9.3f %9llu %7llu %7llu\n", t, pos, audio, gap, buf,
            static_cast<unsigned long long>(s.audioUnderruns),
            static_cast<unsigned long long>(s.audioTimestampJitterFrames),
            static_cast<unsigned long long>(s.audioDiscontinuities));
    }

    FFF3FP_GetSnapshot(player, &s);
    printf("\n判定:\n");
    printf("  位置-音频 首发 = %.4f s，最大 |差| = %.4f s  => %s\n",
        firstGap, maxAbsGap, maxAbsGap < 0.100 ? "同步稳定" : "**漂移明显**");
    printf("  时钟单调性: %s（回退 %d 次）\n",
        monotonicViolations == 0 ? "单调 ✓" : "**有回退**", monotonicViolations);
    printf("  欠载增量: %llu\n",
        static_cast<unsigned long long>(s.audioUnderruns - underruns0));

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
