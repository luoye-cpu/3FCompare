// IAMF 默认声场系统核查：DefaultSoundSystemIndex 硬编码为 5（7.1.4），
// 但素材可能声明别的布局。本探针列出各素材实际解出的声道数与声场系统，
// 判断"固定 7.1.4"是否会与内容不符。
//
// 判据：
//   · 若内容声明 7.1.4，内核应报 声道=12
//   · 若内容声明 7.1（8ch），强行按 7.1.4 渲染可能上混/报错/丢内容
//
// 用法：iamf_layout_audit <目录或文件...>

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
volatile bool g_openCompleted = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(1);
    }
}
std::string Utf8(const std::wstring& wide) {
    if (wide.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, out.data(), size, nullptr, nullptr);
    return out;
}
const char* SystemName(int index) {
    switch (index) {
        case 0: return "2.0";      case 1: return "5.1";    case 2: return "7.1";
        case 3: return "5.1.4";    case 4: return "7.1.1";  case 5: return "7.1.4";
        case 6: return "9.1.4";    case 7: return "9.1.6";  case 8: return "5.1.2";
        case 9: return "7.1.2";    case 10: return "7.1.2'"; case 11: return "3.1.2";
        case 12: return "1.0";     case 13: return "9.1.6'"; case 14: return "7.1.5.4";
        default: return "?";
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: iamf_layout_audit <文件或目录...>\n"); return 2; }

    // 收集 .iamf 文件
    std::vector<std::wstring> files;
    for (int i = 1; i < argc; ++i) {
        std::error_code ec;
        if (std::filesystem::is_directory(argv[i], ec)) {
            for (auto& entry : std::filesystem::recursive_directory_iterator(argv[i], ec))
                if (entry.is_regular_file() && entry.path().extension() == L".iamf")
                    files.push_back(entry.path().wstring());
        } else {
            files.push_back(argv[i]);
        }
    }
    if (files.empty()) { printf("没有找到 .iamf 文件\n"); return 1; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfAuditWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFIamfAuditWnd", L"audit",
        WS_OVERLAPPEDWINDOW, 20, 20, 320, 240, nullptr, nullptr, wc.hInstance, nullptr);

    printf("%-34s %6s %6s %8s %10s  %s\n", "文件", "接管", "目标", "内容声道", "声场系统", "说明");
    int mismatch = 0, fellBack = 0, ok = 0;

    for (const auto& wide : files) {
        const auto path = Utf8(wide);
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
        if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) continue;
        g_openCompleted = false;
        const auto opened = FFF3FP_Open(player, path.c_str()) == FFFResult::Success;
        if (opened) for (int spin = 0; spin < 2000 && !g_openCompleted; ++spin) Pump(1);
        Pump(150);

        FFF3FPSnapshot s{};
        s.size = sizeof(s); s.version = 8;
        FFF3FP_GetSnapshot(player, &s);

        const auto name = std::filesystem::path(wide).filename().string();
        std::string note;
        if (!opened) { note = "**打开失败**"; ++mismatch; }
        else if (s.iamfActive == 0) { note = "回退 FFmpeg（未接管）"; ++fellBack; }
        else if (s.iamfContentChannels > 0 && s.iamfContentChannels < s.iamfChannels) {
            // 内容比渲染目标小：libiamf 会把多出的声道填成数字静音（实测验证），
            // 所以不是假内容，但用户应知道有几路其实是空的。
            note = "内容小于渲染目标（缺的声道为数字静音）";
            ++ok;
        } else { note = "正常"; ++ok; }

        printf("%-34s %6u %6u %8u %10s  %s\n",
            name.substr(0, 34).c_str(), s.iamfActive, s.iamfChannels,
            s.iamfContentChannels, SystemName(s.iamfSoundSystem), note.c_str());

        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
    }

    printf("\n合计：正常 %d，回退 %d，**不符 %d**\n", ok, fellBack, mismatch);
    printf("注：渲染目标固定 7.1.4（12 声道）。内容声道数 < 12 时，\n");
    printf("    libiamf 会把内容没有的声道填成**数字静音**（实测峰值 -inf），\n");
    printf("    不是上混出的假内容。UI 显示内容声道数以免误导。\n");

    if (window != nullptr) DestroyWindow(window);
    return mismatch > 0 ? 1 : 0;
}
