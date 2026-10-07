// IAMF seek 验证：seek 后音频是否真的跳到新位置。
//
// 背景：IAMF 走自己的 libiamf 解码器（不经过 audioDecoder_），所以 DoSeek 里
// avcodec_flush_buffers(audioDecoder_) 对它无效。此前 RestartFile 虽已实现却
// 没有调用点，seek 被静默忽略。
//
// 判据：seek 到接近末尾的位置后，
//   ① 位置确实前进到目标附近（不是从头继续）
//   ② 仍有音频产出（没被 seek 打断成静音）
//   ③ 音频位置跟上视频/时钟位置
//
// 用法：iamf_seek_probe <iamf> [--seek 秒]

#include <windows.h>

#include <cmath>
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
void Snapshot(FFF3FPHandle player, FFF3FPSnapshot& s) {
    s = {};
    s.size = sizeof(s);
    s.version = 8;
    FFF3FP_GetSnapshot(player, &s);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_seek_probe <iamf> [--seek 秒]\n"); return 2; }
    double seekSeconds = 0.0;   // 0 = 自动取时长的 75%
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--seek" && i + 1 < argc) seekSeconds = _wtof(argv[++i]);
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfSeekWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFIamfSeekWnd", L"iamf-seek",
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
    Snapshot(player, s);
    const auto durationSeconds = static_cast<double>(s.duration100ns) / 1e7;
    printf("时长: %.2f 秒   iamfActive=%u  声道=%u\n",
        durationSeconds, s.iamfActive, s.iamfChannels);
    if (durationSeconds <= 0.5) { printf("素材太短，无法测 seek\n"); FFF3FP_Destroy(player); return 1; }
    if (seekSeconds <= 0.0) seekSeconds = durationSeconds * 0.75;

    // 先播到开头附近，确认音频在动
    FFF3FP_Play(player);
    Pump(600);
    FFF3FP_Pause(player);
    Pump(300);
    Snapshot(player, s);
    const auto beforePosition = static_cast<double>(s.position100ns) / 1e7;
    const auto beforeFrames = s.decodedAudioFrames;
    const auto beforeAudio = static_cast<double>(s.audioPosition100ns) / 1e7;
    printf("\nseek 前: 位置=%.2f 秒  音频位置=%.2f 秒  已解码音频帧=%llu\n",
        beforePosition, beforeAudio, static_cast<unsigned long long>(beforeFrames));

    // 执行 seek
    const auto target100ns = static_cast<std::int64_t>(seekSeconds * 1e7);
    const auto seekResult = FFF3FP_Seek(player, target100ns);
    printf("Seek(%.2f 秒) -> result=%d\n", seekSeconds, static_cast<int>(seekResult));
    Pump(1500);

    Snapshot(player, s);
    const auto afterPosition = static_cast<double>(s.position100ns) / 1e7;
    const auto afterFrames = s.decodedAudioFrames;
    const auto afterAudio = static_cast<double>(s.audioPosition100ns) / 1e7;
    printf("seek 后: 位置=%.2f 秒  音频位置=%.2f 秒  已解码音频帧=%llu（增量 %lld）\n",
        afterPosition, afterAudio, static_cast<unsigned long long>(afterFrames),
        static_cast<long long>(afterFrames - beforeFrames));

    // 继续播一会，确认音频还在推进（不是静音卡住）
    FFF3FP_Play(player);
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(200);
    Snapshot(player, s);
    const auto playPosition = static_cast<double>(s.position100ns) / 1e7;
    const auto playedFrames = s.decodedAudioFrames;
    const auto playedAudio = static_cast<double>(s.audioPosition100ns) / 1e7;
    const auto buffered = static_cast<double>(s.bufferedAudio100ns) / 1e7;
    printf("续播后: 位置=%.2f 秒  音频位置=%.2f 秒  缓冲=%.2f 秒  已解码音频帧=%llu（增量 %lld）\n",
        playPosition, playedAudio, buffered,
        static_cast<unsigned long long>(playedFrames),
        static_cast<long long>(playedFrames - afterFrames));

    printf("\n判定:\n");
    const auto seekedForward = afterPosition > beforePosition + 0.5;
    // IAMF has no random access: RestartFile rebuilds the decoder from the start and
    // the timestamp filter discards up to the target, so the tolerance scales with how
    // far in the seek went.
    const auto tolerance = (std::max)(1.5, seekSeconds * 0.05);
    const auto nearTarget = std::abs(afterPosition - seekSeconds) < tolerance;
    const auto audioContinued = playedFrames > afterFrames;
    // 真正的判据：音频渲染器的位置也必须跳到目标附近。position100ns 是 seek 自己
    // 写进去的，单独看它无法证明音频跟上了；audioPosition 来自渲染器，才是证据。
    const auto audioFollowed = afterAudio > beforeAudio + 0.5 && afterAudio > seekSeconds * 0.5;
    printf("  位置前进到目标附近: %s（目标 %.2f，实际 %.2f）\n",
        (seekedForward && nearTarget) ? "PASS" : "**FAIL**", seekSeconds, afterPosition);
    printf("  seek 后仍有音频产出: %s（续播增量 %lld 帧）\n",
        audioContinued ? "PASS" : "**FAIL**", static_cast<long long>(playedFrames - afterFrames));
    printf("  音频位置跳到目标附近: %s（前 %.2f -> 后 %.2f，目标 %.2f）\n",
        audioFollowed ? "PASS" : "**FAIL**", beforeAudio, afterAudio, seekSeconds);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return (seekedForward && nearTarget && audioContinued && audioFollowed) ? 0 : 1;
}
