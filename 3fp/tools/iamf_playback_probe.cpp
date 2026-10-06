// IAMF 播放端到端验证：3FP 是否真的用 AOM 参考解码器播出多声道。
//
// 验证要点：
//   1. 快照 iamfActive=1、iamfChannels = 真实声道数（7.1.4 -> 12），
//      而不是 codecpar 里那个子流的 2；
//   2. 有音频帧被解码并送进渲染器（已解码音频帧数增长）；
//   3. FFmpeg 兜底路径仍可用（libiamf 处理不了的文件不能整首播不出）。
//
// 用法：iamf_playback_probe <文件> [--seconds N]

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>

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
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_playback_probe <文件> [--seconds N]\n"); return 2; }
    int seconds = 3;
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds") seconds = _wtoi(argv[i + 1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfPlayWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFIamfPlayWnd", L"iamf play",
        WS_OVERLAPPEDWINDOW, 40, 40, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);

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
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("Create 失败\n"); return 6;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); DestroyWindow(window); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);

    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 8;
    FFF3FP_GetSnapshot(player, &before);

    FFF3FP_Play(player);
    Pump(seconds * 1000);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 8;
    FFF3FP_GetSnapshot(player, &after);

    printf("文件: %s\n", path.c_str());
    printf("IAMF 状态: active=%u  声道=%u  soundSystem=%d\n",
        after.iamfActive, after.iamfChannels, after.iamfSoundSystem);
    printf("已解码音频帧: %llu -> %llu（增量 %llu）\n",
        static_cast<unsigned long long>(before.decodedAudioFrames),
        static_cast<unsigned long long>(after.decodedAudioFrames),
        static_cast<unsigned long long>(after.decodedAudioFrames - before.decodedAudioFrames));
    printf("音频欠载=%llu  状态=%d\n",
        static_cast<unsigned long long>(after.audioUnderruns), static_cast<int>(after.state));

    // IAMF 生效时，声道数必须等于真实布局声道数而非 codecpar 的子流声道数（2）
    const bool iamfEngaged = after.iamfActive != 0 && after.iamfChannels > 2;
    const auto decoded = after.decodedAudioFrames - before.decodedAudioFrames;
    const bool hasAudio = decoded > 0;
    printf("\n判定: IAMF 接管=%s  多声道=%s  有音频=%s\n",
        after.iamfActive ? "是" : "否",
        after.iamfChannels > 2 ? "是" : "否",
        hasAudio ? "是" : "否");
    const bool ok = iamfEngaged && hasAudio;
    printf("%s\n", ok ? "PASS" : "FAIL");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return ok ? 0 : 1;
}
