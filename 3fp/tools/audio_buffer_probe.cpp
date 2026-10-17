// 音频缓冲池核查：把 Buffered100ns 拆成"应用侧 PCM"与"已提交给 WASAPI 的样本"两部分，
// 找出对 IAMF 报出荒谬数值（1.02s 素材报 365s）的原因。
//
// Buffered100ns 的公式（WasapiRenderer.cpp:439）：
//   frames = queuedBytes_ / outputBlockAlign_ + pendingMediaFrames_
//   return frames * 10^7 / outputSampleRate_
//
// 若 pendingMediaFrames_ 未随播放推进而回落（设备时钟未前进时 playedFrames 不增长），
// 该项会**单调累积**，而它被当作"队列领先量"使用 —— 这正是要验证的假设。
//
// 用法：audio_buffer_probe <媒体> [--seconds N]

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
    if (argc < 2) { wprintf(L"用法: audio_buffer_probe <媒体> [--seconds N]\n"); return 2; }
    int seconds = 6;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFAudioBufferWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFAudioBufferWnd", L"audiobuf",
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
    printf("媒体时长: %.2f 秒   iamfActive=%u  声道=%u\n",
        static_cast<double>(s.duration100ns) / 1e7, s.iamfActive, s.iamfChannels);

    FFF3FP_Play(player);
    printf("\n%6s %12s %12s %12s %12s\n", "秒", "播放位置", "音频位置", "缓冲(s)", "缓冲/位置");
    for (int t = 1; t <= seconds; ++t) {
        Pump(1000);
        FFF3FP_GetSnapshot(player, &s);
        const auto pos = static_cast<double>(s.position100ns) / 1e7;
        const auto audio = static_cast<double>(s.audioPosition100ns) / 1e7;
        const auto buffered = static_cast<double>(s.bufferedAudio100ns) / 1e7;
        const auto ratio = pos > 0.01 ? buffered / pos : 0.0;
        printf("%6d %12.2f %12.2f %12.2f %12.1f\n", t, pos, audio, buffered, ratio);
    }

    // 关键判断：缓冲是否随播放**单调增长**（而队列本该稳定）
    printf("\n判定标准：正常队列的缓冲应稳定在一个小值（约 1~2 秒）。\n");
    printf("若持续增长 => Buffered100ns 把已提交样本累计了，而非反映队列领先量。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
