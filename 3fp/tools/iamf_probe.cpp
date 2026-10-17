// IAMF 空间音频探测：3FP 能否正确打开/播放 IAMF？
//
// IAMF（Immersive Audio Model and Formats，AOM）与普通音频容器的关键差别：
//   · 它是**多 stream group** 结构：至少一个 Audio Element（可含多层/多通道 bed 或
//     对象/高阶 ambisonics）+ 一个 Mix Presentation（描述如何混成可播放声道布局）。
//   · 承载编解码器是 Opus / FLAC / PCM —— 没有独立的 "iamf 解码器"，
//     demuxer 负责解析 IAMF 结构并把它展开成底层编码流。
//   · 因此播放器需要：(a) iamf demuxer，(b) 底层解码器，(c) 能处理 >2 声道的输出。
//
// 本探针只回答"打开与解码链路是否通"，不判断空间感（那需要多声道回放设备）。
//
// 用法：iamf_probe <文件> [--seconds N]

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
/// 从 MediaInfo JSON 里找一小段便于判读的信息。
void PrintInteresting(const std::string& json) {
    for (const char* key : { "\"formatName\"", "\"codec\"", "\"channels\"",
                             "\"channelLayout\"", "\"sampleRate\"", "\"duration100ns\"" }) {
        const auto at = json.find(key);
        if (at == std::string::npos) continue;
        auto end = json.find(',', at);
        if (end == std::string::npos || end - at > 90) end = at + 90;
        printf("  %s\n", json.substr(at, end - at).c_str());
    }
    // The IAMF block is the whole point of this probe: codecpar describes only ONE
    // substream, so `channels` above understates the real layout. Print it verbatim.
    const auto iamf = json.find("\"iamf\":{");
    if (iamf != std::string::npos) {
        const auto end = json.find('}', iamf);
        if (end != std::string::npos)
            printf("  %s   <-- 真实布局（codecpar 只是其中一个子流）\n",
                json.substr(iamf, end - iamf + 1).c_str());
    } else {
        printf("  (无 iamf 段)\n");
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_probe <文件> [--seconds N]\n"); return 2; }
    int seconds = 3;
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--seconds") seconds = _wtoi(argv[i + 1]);
    if (seconds < 1 || seconds > 60) seconds = 3;

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFIamfProbeWnd", L"iamf probe",
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
    const auto created = FFF3FP_Create(&configuration, &player);
    if (created != FFFResult::Success || player == nullptr) {
        printf("Create 失败 result=%d\n", static_cast<int>(created)); return 6;
    }

    printf("文件: %s\n", path.c_str());
    const auto opened = FFF3FP_Open(player, path.c_str());
    printf("Open result=%d\n", static_cast<int>(opened));
    if (opened != FFFResult::Success) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("  lastError: %s\n", e);
        FFF3FP_Destroy(player); DestroyWindow(window);
        return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    printf("OpenCompleted=%s\n", g_openCompleted ? "yes" : "no");

    // 媒体信息：IAMF 的声道/布局会体现在这里
    static char mediaInfo[262144]{};
    std::uint32_t required = 0;
    if (FFF3FP_GetMediaInfo(player, mediaInfo, sizeof(mediaInfo), &required) == FFFResult::Success) {
        printf("MediaInfo:\n");
        PrintInteresting(std::string(mediaInfo));
    } else {
        printf("GetMediaInfo 失败 required=%u\n", required);
    }

    // 播放并数已解码音频帧
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

    printf("播放 %d s：已解码音频帧 %llu -> %llu（增量 %llu）\n", seconds,
        static_cast<unsigned long long>(before.decodedAudioFrames),
        static_cast<unsigned long long>(after.decodedAudioFrames),
        static_cast<unsigned long long>(after.decodedAudioFrames - before.decodedAudioFrames));
    printf("状态: %d -> %d（3=播放中）\n", static_cast<int>(before.state), static_cast<int>(after.state));
    if (after.state == FFF3FPState::Failed) {
        char e[512]{}; FFF3FP_GetLastError(player, e, sizeof(e), nullptr);
        printf("  lastError: %s\n", e);
    }

    const auto decoded = after.decodedAudioFrames - before.decodedAudioFrames;
    const bool ok = decoded > 0;
    printf("\n判定: %s\n", ok ? "PASS（有音频帧被解码）" : "FAIL（没有音频帧被解码）");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return ok ? 0 : 1;
}
