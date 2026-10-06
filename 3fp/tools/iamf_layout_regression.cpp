// IAMF 布局报告回归：验证内核如实报出 mix presentation 的真实布局。
//
// 背景（见 docs/43）：IAMF 把一个音频元素的声道拆到多个 dependent 子流上，
// codecpar 只描述其中**一个**子流（7.1.4 的一个 stereo 对）。若播放器只报
// codecpar，就会把 7.1.4 说成 stereo —— 静默降级。本用例断言内核现在报的是
// mix presentation 的真实布局。
//
// 判据（V1）：iamf.presentationLayout 与 ffmpeg 报的布局一致；
//             iamf.substreams > 1 时必须给出布局，而不是空。
//
// 用法：iamf_layout_regression [样本根目录]

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
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

struct 期望值 {
    const wchar_t* 相对路径;
    const char* 期望布局;      // mix presentation 的目标布局
    int 期望声道;              // 0 = 不校验
};

struct 结果 {
    bool 打开成功 = false;
    std::string 布局;
    int 声道 = 0;
    int 子流数 = 0;
    std::string 原始键;
};

/// 从 JSON 里抽 "iamf":{...} 段并解析三个字段。
void 解析Iamf段(const std::string& json, 结果& out) {
    const auto at = json.find("\"iamf\":{");
    if (at == std::string::npos) return;
    const auto end = json.find('}', at);
    if (end == std::string::npos) return;
    out.原始键 = json.substr(at, end - at + 1);
    auto 取数 = [&](const char* key) -> int {
        const std::string needle = std::string("\"") + key + "\":";
        const auto p = out.原始键.find(needle);
        if (p == std::string::npos) return 0;
        return std::atoi(out.原始键.c_str() + p + needle.size());
    };
    auto 取串 = [&](const char* key) -> std::string {
        const std::string needle = std::string("\"") + key + "\":\"";
        const auto p = out.原始键.find(needle);
        if (p == std::string::npos) return {};
        const auto s = p + needle.size();
        const auto e = out.原始键.find('"', s);
        return e == std::string::npos ? std::string{} : out.原始键.substr(s, e - s);
    };
    out.子流数 = 取数("substreams");
    out.声道 = 取数("presentationChannels");
    out.布局 = 取串("presentationLayout");
}

结果 打开一件(const std::wstring& 全路径) {
    结果 out;
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfLayoutWnd";
    static bool registered = false;
    if (!registered) { RegisterClassW(&wc); registered = true; }
    const HWND window = CreateWindowExW(0, L"FFFIamfLayoutWnd", L"iamf layout",
        0, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);

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
        DestroyWindow(window);
        return out;
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, 全路径.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, 全路径.c_str(), -1, path.data(), size, nullptr, nullptr);

    g_openCompleted = false;
    if (FFF3FP_Open(player, path.c_str()) == FFFResult::Success) {
        for (int i = 0; i < 100 && !g_openCompleted; ++i) Pump(30);
        out.打开成功 = true;
        std::vector<char> buffer(1 << 20);
        std::uint32_t required = 0;
        if (FFF3FP_GetMediaInfo(player, buffer.data(),
                static_cast<std::uint32_t>(buffer.size()), &required) == FFFResult::Success) {
            解析Iamf段(std::string(buffer.data()), out);
        }
    }
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    DestroyWindow(window);
    return out;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::wstring root = argc > 1 ? argv[1]
        : L"C:\\PLAN\\3FUI\\fixtures\\decode-research\\atmos2iamf";
    if (!root.empty() && root.back() == L'\\') root.pop_back();

    // 覆盖多种布局与承载。期望值 = 该文件**最大**的 mix presentation 布局，
    // 由 `ffmpeg -i <file>` 的 "Layout #N:" 实测得到。
    // ⚠ 一个文件可以有**多个** submix 布局（实测 conv_bedonly 同时有 stereo 与
    //   5.1(side)）；内核按"取最大"报告，因为那是内容真正能给出的最好渲染。
    //   `ffmpeg -i` 默认只显示第一个（stereo），不能直接当期望值用。
    const 期望值 用例[] = {
        {L"bed_order_out\\live_7_1_4.iamf", "7.1.4", 12},
        {L"bed_order_out\\live_7_1_2.iamf", "7.1.2", 10},
        {L"bed_order_out\\live_7_1.iamf",   "7.1",    8},
        {L"convert_out\\conv_bedonly.iamf", "5.1",    6},
        {L"convert_out\\conv_bedonly_libopus.iamf", "7.1.4", 12},
        {L"convert_out\\conv_bedonly_flac.iamf",    "7.1.4", 12},
        {L"convert_out\\conv_hoaonly.iamf", "5.1",    6},
        {L"flac_ctl_out\\单元素_flac.iamf",  "5.1",    6},
    };

    int 通过 = 0, 失败 = 0, 跳过 = 0;
    printf("=== IAMF 布局报告回归 ===\n根目录: %ls\n\n", root.c_str());

    for (const auto& c : 用例) {
        const std::wstring full = root + L"\\" + c.相对路径;
        if (GetFileAttributesW(full.c_str()) == INVALID_FILE_ATTRIBUTES) {
            printf("SKIP  %-42ls（文件不存在）\n", c.相对路径);
            ++跳过;
            continue;
        }
        const auto r = 打开一件(full);
        const bool 布局对 = !r.布局.empty() &&
            r.布局.find(c.期望布局) != std::string::npos;
        const bool 声道对 = c.期望声道 == 0 || r.声道 == c.期望声道;
        const bool 好 = r.打开成功 && 布局对 && 声道对;
        if (好) ++通过; else ++失败;

        // 只显示文件名，避免长路径刷屏
        std::wstring name(c.相对路径);
        const auto slash = name.find_last_of(L'\\');
        if (slash != std::wstring::npos) name = name.substr(slash + 1);

        char 名[512]{};
        WideCharToMultiByte(CP_UTF8, 0, name.c_str(), -1, 名, sizeof(名), nullptr, nullptr);
        printf("%s  %-34s 布局=%-8s 声道=%-3d 子流=%d  期望=%s/%d\n",
            (好 ? "PASS" : "FAIL"), 名, r.布局.empty() ? "(无)" : r.布局.c_str(),
            r.声道, r.子流数, c.期望布局, c.期望声道);
        if (!好) {
            printf("      打开=%s  原始: %s\n", r.打开成功 ? "ok" : "失败",
                r.原始键.empty() ? "(无 iamf 段)" : r.原始键.c_str());
        }
    }

    printf("\n通过 %d / 失败 %d / 跳过 %d\n", 通过, 失败, 跳过);
    return 失败 == 0 ? 0 : 1;
}
