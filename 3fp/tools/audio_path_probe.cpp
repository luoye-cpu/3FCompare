// 音频管线对比：同一音频内容在"带视频轨"与"纯音频"两种形态下，
// 3FP 的输出行为是否不同。
//
// 期望差异（读代码得出，本探针验证）：
//   ① 带视频：audioBlockedUntilVideoFrame_ 会先把音频**闸住**，
//      等首个视频帧呈现后才放行（ShouldGateAudioAtVideoStart）。
//   ② 纯音频：该闸门不生效（videoStream_ < 0），可立即起播。
//   ③ 纯音频且无窗口/无渲染器时走"墙钟推进"分支（PumpPlayback 早退）。
//
// 用法：audio_path_probe <媒体> [--seconds N] [--label 名称]

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
    if (argc < 2) { wprintf(L"用法: audio_path_probe <媒体> [--seconds N] [--label 名]\n"); return 2; }
    int seconds = 4;
    std::string label = "media";
    for (int i = 2; i < argc; ++i) {
        const std::wstring a = argv[i];
        if (a == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (a == L"--label" && i + 1 < argc) label = Utf8(argv[++i]);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFAudioPathWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFAudioPathWnd", L"audiopath",
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
        printf("[%s] 创建失败\n", label.c_str()); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("[%s] Open 失败\n", label.c_str()); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FPSnapshot s{};
    s.size = sizeof(s); s.version = 8;
    FFF3FP_GetSnapshot(player, &s);
    printf("\n===== %s =====\n", label.c_str());
    printf("视频流=%d  音频流=%d  时长=%.2fs  解码器=%d\n",
        s.selectedVideoStream, s.selectedAudioStream,
        static_cast<double>(s.duration100ns) / 1e7, static_cast<int>(s.decodeMode));

    // 起播瞬间：音频何时开始产出（反映闸门是否存在）
    const auto playStart = GetTickCount64();
    FFF3FP_Play(player);
    std::uint64_t firstAudioFrames = 0;
    ULONGLONG firstAudioAt = 0;
    for (int i = 0; i < seconds * 100; ++i) {
        Pump(10);
        FFF3FP_GetSnapshot(player, &s);
        if (s.decodedAudioFrames > 0 && firstAudioAt == 0) {
            firstAudioAt = GetTickCount64() - playStart;
            firstAudioFrames = s.decodedAudioFrames;
        }
    }
    FFF3FP_GetSnapshot(player, &s);
    printf("首个音频帧出现在: %llu ms（播放开始后）\n",
        static_cast<unsigned long long>(firstAudioAt));
    printf("已解码音频帧=%llu  已呈现视频帧=%llu  已丢弃视频帧=%llu\n",
        static_cast<unsigned long long>(s.decodedAudioFrames),
        static_cast<unsigned long long>(s.presentedVideoFrames),
        static_cast<unsigned long long>(s.droppedVideoFrames));
    printf("音频位置=%.2fs  播放位置=%.2fs  缓冲=%.2fs\n",
        static_cast<double>(s.audioPosition100ns) / 1e7,
        static_cast<double>(s.position100ns) / 1e7,
        static_cast<double>(s.bufferedAudio100ns) / 1e7);
    printf("欠载=%llu  抖动=%llu  不连续=%llu  插入静音帧=%llu\n",
        static_cast<unsigned long long>(s.audioUnderruns),
        static_cast<unsigned long long>(s.audioTimestampJitterFrames),
        static_cast<unsigned long long>(s.audioDiscontinuities),
        static_cast<unsigned long long>(s.audioInsertedSilenceFrames));

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
