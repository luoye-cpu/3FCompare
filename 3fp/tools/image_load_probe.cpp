// 图片模式启动耗时分解。
//
// 目标：回答"启动慢"慢在哪一步。3FP 的图片路径是
//   FFF3FP_Open() -> [worker] OpenInternal -> DecodeInitialFrame() -> OpenCompleted
// 也就是说 **Open 返回时图片已经被解码并渲染**，不是"先开窗再出图"。
// 因此要分清三段：
//   ① API 往返（Open 调用本身）
//   ② 到 OpenCompleted 事件（含解码 + 首次渲染 + 交换链建立）
//   ③ 到首个像素真正可读（解码完成）
//
// 用法：image_load_probe <图片...>

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
volatile bool g_openCompleted = false;
volatile long long g_openCompletedTick = 0;

void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) {
        g_openCompletedTick = static_cast<long long>(GetTickCount64());
        g_openCompleted = true;
    }
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
    if (argc < 2) { wprintf(L"用法: image_load_probe <图片...>\n"); return 2; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFImageLoadWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFImageLoadWnd", L"imgload",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    printf("%-20s %10s %14s %14s %10s\n", "图片", "大小MB", "Open(ms)", "到Open事件(ms)", "首像素(ms)");

    for (int i = 1; i < argc; ++i) {
        const auto path = Utf8(argv[i]);
        FFF3FPConfiguration configuration{};
        configuration.size = sizeof(configuration);
        configuration.version = FFF3FP_GetApiVersion();
        configuration.outputWindow = window;
        configuration.decodeMode = FFF3FPDecodeMode::D3D11;
        configuration.colorMode = FFF3FPColorMode::MapToSdr;
        configuration.sdrPeakNits = 100.0f;
        configuration.sdrPaperWhiteNits = 203.0f;
        configuration.eventCallback = EventCallback;
        configuration.preferredAdapterIndex = -1;

        FFF3FPHandle player = nullptr;
        if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
            printf("%-20s 创建失败\n", path.c_str()); continue;
        }
        g_openCompleted = false;
        g_openCompletedTick = 0;

        // 文件大小
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        double sizeMb = 0.0;
        if (GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &attributes))
            sizeMb = (static_cast<double>(attributes.nFileSizeHigh) * 4294967296.0 +
                attributes.nFileSizeLow) / (1024.0 * 1024.0);

        // ① API 往返
        const auto openStart = std::chrono::steady_clock::now();
        const auto openResult = FFF3FP_Open(player, path.c_str());
        const auto openEnd = std::chrono::steady_clock::now();
        const auto openMs = std::chrono::duration<double, std::milli>(openEnd - openStart).count();

        // ② 等到 OpenCompleted 事件
        const auto eventStart = std::chrono::steady_clock::now();
        for (int spin = 0; spin < 30000 && !g_openCompleted; ++spin) Pump(1);
        const auto eventEnd = std::chrono::steady_clock::now();
        const auto eventMs = std::chrono::duration<double, std::milli>(eventEnd - eventStart).count();

        // ③ 首个像素可读（此时应已解码完成）
        std::vector<float> pixels(4 * 4 * 4, 0.0f);
        std::uint32_t bitDepth = 0;
        const auto pixelStart = std::chrono::steady_clock::now();
        const auto pixelOk = FFF3FP_ReadVideoPixelRegion(player, 0, 0, 4, 4, pixels.data(),
            static_cast<std::uint32_t>(pixels.size()), &bitDepth) == FFFResult::Success;
        const auto pixelEnd = std::chrono::steady_clock::now();
        const auto pixelMs = std::chrono::duration<double, std::milli>(pixelEnd - pixelStart).count();

        FFF3FPSnapshot s{};
        s.size = sizeof(s); s.version = 20;
        FFF3FP_GetSnapshot(player, &s);

        printf("%-20s %10.2f %14.1f %14.1f %10s  open=%d %ux%u\n",
            path.substr(path.find_last_of("/\\") + 1).c_str(), sizeMb, openMs, eventMs,
            pixelOk ? std::to_string(static_cast<int>(pixelMs)).c_str() : "失败",
            static_cast<int>(openResult), s.videoWidth, s.videoHeight);

        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
    }

    if (window != nullptr) DestroyWindow(window);
    printf("\n注：Open(ms) 是 API 往返；到Open事件(ms) 含解码+首次渲染+交换链。\n");
    return 0;
}
