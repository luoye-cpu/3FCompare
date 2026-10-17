// IAMF 内容声道数 字段诊断：确认 offsetof 与运行期取值。
//
// 背景：内核已实现 iamfContentChannels（追加在 FFF3FPSnapshot 末尾），
// 但探针读到 0。可能原因：① 字段偏移不对 ② libiamf 未上报 audio_elements
// ③ 赋值路径没走到。本探针把三者分开看。
//
// 用法：iamf_field_probe <iamf文件>

#include <windows.h>

#include <cstddef>
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
    if (argc < 2) { wprintf(L"用法: iamf_field_probe <iamf文件>\n"); return 2; }

    printf("=== 结构体偏移 ===\n");
    printf("  sizeof(FFF3FPSnapshot)          = %zu\n", sizeof(FFF3FPSnapshot));
    printf("  offsetof(iamfActive)            = %zu\n", offsetof(FFF3FPSnapshot, iamfActive));
    printf("  offsetof(iamfChannels)          = %zu\n", offsetof(FFF3FPSnapshot, iamfChannels));
    printf("  offsetof(iamfSoundSystem)       = %zu\n", offsetof(FFF3FPSnapshot, iamfSoundSystem));
    printf("  offsetof(videoUpload100ns)      = %zu\n", offsetof(FFF3FPSnapshot, videoUpload100ns));
    printf("  offsetof(iamfContentChannels)   = %zu\n", offsetof(FFF3FPSnapshot, iamfContentChannels));

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfFieldWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFIamfFieldWnd", L"field",
        WS_OVERLAPPEDWINDOW, 20, 20, 320, 240, nullptr, nullptr, wc.hInstance, nullptr);

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
    const auto path = Utf8(argv[1]);
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);
    // 必须真的播放：内容声道数在 PumpFile 的 configure 分支里读取，
    // 不播放则那条路径根本不会执行（曾因此误判为"字段没生效"）。
    FFF3FP_Play(player);
    Pump(800);
    FFF3FP_Pause(player);
    Pump(200);

    FFF3FPSnapshot s{};
    s.size = sizeof(s); s.version = 20;
    const auto result = FFF3FP_GetSnapshot(player, &s);
    printf("\n=== 运行期 ===\n");
    printf("  内核 API 版本        = %u\n", FFF3FP_GetApiVersion());
    printf("  GetSnapshot result   = %d\n", static_cast<int>(result));
    printf("  iamfActive           = %u\n", s.iamfActive);
    printf("  iamfChannels         = %u\n", s.iamfChannels);
    printf("  iamfSoundSystem      = %d\n", s.iamfSoundSystem);
    printf("  iamfContentChannels  = %u\n", s.iamfContentChannels);
    printf("  videoUpload100ns     = %llu\n", static_cast<unsigned long long>(s.videoUpload100ns));
    printf("  已解码音频帧         = %llu  <-- 0 表示 PumpFile 从未产出，配置分支自然没走到\n",
        static_cast<unsigned long long>(s.decodedAudioFrames));

    printf("\n判读：iamfContentChannels 为 0 有两种可能 ——\n");
    printf("  · libiamf 未上报 audio_element_count（则应为 0，属正常降级）\n");
    printf("  · 赋值路径未走到（缺陷）\n");
    printf("  iamfChannels=%u 说明已接管，故 stream_info 本身可用。\n", s.iamfChannels);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
