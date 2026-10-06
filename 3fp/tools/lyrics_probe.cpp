// 歌词承载探测：把内核 MediaInfo JSON 里的歌词相关字段全部打出来。
//
// 目的：先看清"内核现在到底暴露了什么"，再决定歌词支持要补在哪一层。
// 测试包见 C:\PLAN\kit\kit（MANIFEST.txt 定义了每件的预期）。
//
// 用法：lyrics_probe <媒体路径>
//   --raw  额外 dump 完整 metadata 段（截断到 4000 字符）

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

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
/// 从 JSON 里取某个 key 的字符串值（只处理顶层字符串，够本探针用）。
std::string JsonString(const std::string& json, const std::string& key) {
    const std::string needle = "\"" + key + "\":\"";
    const auto at = json.find(needle);
    if (at == std::string::npos) return {};
    const auto start = at + needle.size();
    const auto end = json.find('"', start);
    if (end == std::string::npos) return {};
    return json.substr(start, end - start);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: lyrics_probe <媒体路径> [--raw]\n"); return 2; }
    bool raw = false;
    for (int i = 2; i < argc; ++i) if (std::wstring(argv[i]) == L"--raw") raw = true;

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFLyricsProbeWnd";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, L"FFFLyricsProbeWnd", L"lyrics probe",
        WS_OVERLAPPEDWINDOW, 60, 60, 480, 300, nullptr, nullptr, wc.hInstance, nullptr);

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
        printf("Open 失败: %s\n", e); FFF3FP_Destroy(player); return 7;
    }
    for (int i = 0; i < 200 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(player);
    Pump(500);

    std::vector<char> buffer(1 << 20);
    std::uint32_t required = 0;
    const auto info = FFF3FP_GetMediaInfo(player, buffer.data(),
        static_cast<std::uint32_t>(buffer.size()), &required);
    if (info != FFFResult::Success) {
        printf("GetMediaInfo 失败 result=%d required=%u\n", static_cast<int>(info), required);
        FFF3FP_Close(player); FFF3FP_Destroy(player); DestroyWindow(window); return 8;
    }
    const std::string json(buffer.data());

    printf("媒体: %s\n", path.c_str());
    printf("JSON 长度: %zu\n", json.size());

    // 内核直接给出的歌词字段（若有）
    for (const char* key : { "lyrics", "hasLyrics", "attachedLyrics" }) {
        const auto value = JsonString(json, key);
        if (!value.empty()) printf("  [内核字段] %s = %.80s\n", key, value.c_str());
    }

    // 在 metadata 段里找歌词键（大小写/位置都要看）
    printf("\n=== 歌词候选键（全 JSON 扫描）===\n");
    const char* candidates[] = {
        "LYRICS", "lyrics", "SYNCEDLYRICS", "syncedlyrics", "UNSYNCEDLYRICS", "unsyncedlyrics",
        "USLT", "\xC2\xA9lyr", "clyr", "COMMENT", "comment", "TXXX", "syncedLyrics",
    };
    bool foundAny = false;
    for (const auto* key : candidates) {
        const auto value = JsonString(json, key);
        if (!value.empty()) {
            printf("  \"%s\" -> %zu 字节: %.70s%s\n", key, value.size(), value.c_str(),
                value.size() > 70 ? "..." : "");
            foundAny = true;
        }
    }
    if (!foundAny) printf("  （JSON 里没有任何歌词键）\n");

    // 流级 metadata（.opus 的歌词挂在流级）
    printf("\n=== 流级 metadata 出现次数 ===\n");
    std::size_t at = 0;
    int streamSections = 0;
    while ((at = json.find("\"metadata\":", at)) != std::string::npos) {
        ++streamSections;
        at += 11;
    }
    printf("  \"metadata\" 出现 %d 次（1 个容器级 + N 个流级）\n", streamSections);

    if (raw) {
        printf("\n=== 原始 JSON（前 4000 字符）===\n%.4000s\n", json.c_str());
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return 0;
}
