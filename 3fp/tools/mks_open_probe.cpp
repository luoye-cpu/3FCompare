// 3FP MKS 外挂字幕 —— 「播放器能否直接打开 MKS」探针（2026-09-26，上游 issue #2）
//
// 要回答的问题（决定宿主改造要不要动内核）：
//   宿主今天枚举字幕流靠的是已打开媒体的 GetMediaInfo。而 .mks 是**纯字幕容器**（无视频轨）。
//   如果播放器连 .mks 都 Open 不了 ⇒ 宿主没有任何途径拿到「流索引 + 编码名」
//   ⇒ 必须给内核加一个按路径探测字幕流的 API；反之若 Open 得通，宿主可复用现成路径。
//
// 用法：mks_open_probe <文件> [更多文件…]
// 构建：python tools/build_probe.py mks_open_probe
// 运行：exe 同目录需 FFF.Native.dll + FFmpeg DLL（跑 ASS 还要 ass-9.dll 及 freetype/harfbuzz/z.dll）

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <thread>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

bool g_openCompleted = false;
bool g_failed = false;

void __cdecl OnEvent(void*, FFF3FPEvent type, const char* detail) {
    const char* name = "?";
    switch (type) {
    case FFF3FPEvent::StateChanged: name = "StateChanged"; break;
    case FFF3FPEvent::OpenCompleted: name = "OpenCompleted"; break;
    case FFF3FPEvent::OperationCompleted: name = "OperationCompleted"; break;
    case FFF3FPEvent::PlaybackEnded: name = "PlaybackEnded"; break;
    case FFF3FPEvent::Error: name = "Error"; break;
    case FFF3FPEvent::ColorModeChanged: name = "ColorModeChanged"; break;
    case FFF3FPEvent::DeviceChanged: name = "DeviceChanged"; break;
    default: break;
    }
    printf("    event=%s %s\n", name, detail != nullptr ? detail : "");
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
    if (type == FFF3FPEvent::Error) g_failed = true;
}

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM w, LPARAM l) {
    return DefWindowProcW(window, message, w, l);
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message{};
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessage(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

}  // namespace

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        printf("usage: mks_open_probe <file> [more...]\n");
        return 2;
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpMksProbeWindow";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, L"3fpMksProbeWindow", L"mks probe",
        WS_OVERLAPPED, 0, 0, 320, 240, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);

    FFF3FPHandle player = nullptr;
    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.forceHdrOutput = 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    const int created = static_cast<int>(FFF3FP_Create(&configuration, &player));
    printf("Create: %d (player=%p)\n", created, player);
    if (created != 0 || player == nullptr) return 1;

    for (int i = 1; i < argc; ++i) {
        g_openCompleted = false;
        g_failed = false;
        printf("=== %s ===\n", argv[i]);
        const int opened = static_cast<int>(FFF3FP_Open(player, argv[i]));
        printf("  Open -> %d\n", opened);
        for (int tick = 0; tick < 120 && !g_openCompleted && !g_failed; ++tick) Pump(50);
        printf("  OpenCompleted=%s Error=%s\n", g_openCompleted ? "yes" : "no", g_failed ? "yes" : "no");

        char info[8192]{};
        std::uint32_t required = 0;
        const int infoResult = static_cast<int>(
            FFF3FP_GetMediaInfo(player, info, sizeof(info), &required));
        printf("  GetMediaInfo -> %d (required=%u)\n", infoResult, required);
        if (infoResult == 0) {
            // 只打印到流列表为止，避免刷屏
            char copy[8192];
            std::memcpy(copy, info, sizeof(copy));
            char* cut = std::strstr(copy, "\"chapters\"");
            if (cut != nullptr) *cut = '\0';
            printf("  mediaInfo: %s\n", copy);
        }
        printf("\n");
    }

    FFF3FP_Destroy(player);
    return 0;
}
