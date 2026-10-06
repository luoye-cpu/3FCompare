// 报告实际使用的硬件解码后端与像素格式。
//
// 动机：3FP 的硬件后端优先级是 D3D11VA -> CUDA。D3D11 帧可被着色器**零拷贝**采样，
// 而 CUDA 帧必须先 hwframe_transfer_data 下载到系统内存（GPU->CPU，大帧很贵），
// 且下载目标是**单个** videoTransferFrame_。因此"CUVDA 是否被选用"决定了
// 帧缓冲路径的成本结构，必须查清而不是假定。
//
// 用法：backend_probe <媒体>

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
    if (argc < 2) { wprintf(L"用法: backend_probe <媒体> [--decode cpu|gpu]\n"); return 2; }
    bool gpu = true;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--decode" && i + 1 < argc)
            gpu = std::wstring(argv[++i]) == L"gpu";
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFBackendWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFBackendWnd", L"backend",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = gpu ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
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
    FFF3FP_Play(player);
    Pump(2500);

    // FFF3FP_GetMediaInfo writes UTF-8 JSON; ask with a generous buffer and read back
    // the required size if it does not fit.
    std::uint32_t required = 0;
    (void)FFF3FP_GetMediaInfo(player, nullptr, 0, &required);
    if (required == 0) { printf("无 mediaInfo\n"); }
    else {
        std::string json(required, '\0');
        std::uint32_t written = 0;
        if (FFF3FP_GetMediaInfo(player, json.data(), required, &written) == FFFResult::Success) {
            // Only the fields that answer the question at hand, to keep it readable.
            const char* keys[] = {"hardwareAcceleration", "decoderPixelFormat",
                "decoderSurfaceFormat", "decoderBitDepth", "pixelFormat", "width", "height"};
            for (const auto* key : keys) {
                const auto needle = std::string("\"") + key + "\":";
                const auto at = json.find(needle);
                if (at == std::string::npos) continue;
                auto end = json.find_first_of(",}", at);
                printf("  %s\n", json.substr(at, end - at).c_str());
            }
        }
    }

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
