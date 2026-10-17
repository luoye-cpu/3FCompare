// 量"平滑缩放的中间态到底有没有真的呈现到屏幕上"（docs/79 §11.9 残留 ①、docs/81 §四第 5 条）。
//
// 为什么需要它：同进程体感档已经证明宿主一格滚轮会发 14~15 次视图更新、约 220ms 静止；
// 但它挂的是假内核，**证明不了 GPU 真的逐帧呈现** —— 若呈现被合并，用户看到的仍是一步跳变，
// 那"平滑"就只存在于宿主状态里。进程外抓屏在这台共享桌面上不可归因（见 docs/81 §11.10），
// 所以这里用真内核 + 真交换链，直接问内核自己的计数器。
//
// 做法：完全照宿主动画的节拍走 —— 一串按临界阻尼算出来的中间缩放值，每个值下发一次并等
// `FFF3FPSnapshot.swapChainPresents` 推进（实测静帧重呈只推这个计数，不推 presentedVideoFrames），
// 同时回读一个**偏离锚点**的小块，看内容是不是逐帧都不同。
//
// 三条读数：① 呈现推进次数 / 下发次数；② 相邻帧内容确实不同的次数；③ 有没有"合并后只剩几帧"。
//
// 用法：zoom_frames_probe <媒体> [--steps 15]
// 退出码：0 中间态逐帧到达屏幕 / 3 未判定（计数器不推进或内容全同，量具/环境不支持）/
//         1 建立失败 / 2 用法

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

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
constexpr int kPatch = 96;   // 必须大到必然跨过色块边界：8x8 会整块落在同色里，Δ恒 0 是假象

double PatchSum(FFF3FPHandle player, const std::uint32_t x, const std::uint32_t y,
    std::vector<float>& buf, bool& ok) {
    std::uint32_t bitDepth = 0;
    if (FFF3FP_ReadVideoPixelRegion(player, x, y, kPatch, kPatch, buf.data(),
            static_cast<std::uint32_t>(buf.size()), &bitDepth) != FFFResult::Success) {
        ok = false;
        return 0.0;
    }
    double total = 0.0;
    for (const float v : buf) total += v;
    ok = true;
    return total;
}

std::uint64_t SwapPresents(FFF3FPHandle player, FFF3FPSnapshot& snap) {
    snap.size = sizeof(snap);
    snap.version = 20;                 // 本树唯一被接受的快照版本（实测 19/0/巨大 一律 rc=-1）
    return FFF3FP_GetSnapshot(player, &snap) == FFFResult::Success ? snap.swapChainPresents : 0;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: zoom_frames_probe <媒体> [--steps N]\n"); return 2; }
    const auto path = Utf8(argv[1]);
    int steps = 15;
    for (int i = 2; i < argc; ++i)
        if (std::wstring(argv[i]) == L"--steps" && i + 1 < argc) steps = _wtoi(argv[++i]);
    if (steps < 4 || steps > 60) { printf("--steps 要在 4~60 之间。\n"); return 2; }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFZoomFramesWnd";
    RegisterClassW(&wc);
    RECT want{ 0, 0, 1264, 681 };
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
    const auto window = CreateWindowExW(0, L"FFFZoomFramesWnd", L"frames", WS_OVERLAPPEDWINDOW,
        20, 20, want.right - want.left, want.bottom - want.top, nullptr, nullptr, wc.hInstance, nullptr);

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
    for (int i = 0; i < 400 && !g_open; ++i) Pump(50);
    if (!g_open) { printf("没等到 OpenCompleted\n"); FFF3FP_Destroy(player); return 1; }
    FFF3FP_Play(player);
    Pump(2000);
    FFF3FP_Pause(player);
    Pump(600);

    RECT client{};
    GetClientRect(window, &client);
    const auto w = static_cast<std::uint32_t>(client.right - client.left);
    const auto h = static_cast<std::uint32_t>(client.bottom - client.top);
    // 采样块**偏离锚点**（锚在中心）：锚定缩放要求锚点下不动，把块放那儿等于等一个不该发生的现象。
    const auto px = static_cast<std::uint32_t>(w * 0.35);
    const auto py = static_cast<std::uint32_t>(h * 0.65);
    printf("客户区 %ux%u  采样块 (%u,%u)  下发 %d 个中间态\n\n", w, h, px, py, steps);

    std::vector<float> buf(kPatch * kPatch * 4, 0.0f);
    FFF3FPSnapshot snap{};
    double prev = 0.0;
    bool firstOk = false;
    prev = PatchSum(player, px, py, buf, firstOk);
    if (!firstOk) { printf("起始回读失败：量具读不到呈现帧。\n"); FFF3FP_Destroy(player); return 1; }

    int 呈现推进 = 0, 内容变化 = 0, 下发 = 0;
    printf("%5s %9s %10s %10s  %s\n", "步", "缩放", "呈现Δ", "内容Δ", "判定");
    for (int i = 1; i <= steps; ++i) {
        // 中间态用等比走：1.0 → 3.0（宿主那一格是 1.0→1.25，这里放大步长让内容差异可分辨）
        // 量程要落在采样块还有分辨力的区间：09g 的色块在 z>1.9 后整块包住 96px 采样窗，
        // 内容差归零不是"没呈现"而是"那里本来就同色"。要拉大量程就得更细的夹具。
        const float zoom = 1.0f + 0.8f * static_cast<float>(i) / static_cast<float>(steps);
        const std::uint64_t before = SwapPresents(player, snap);
        if (FFF3FP_SetViewTransform(player, zoom, 0.0f, 0.0f) != FFFResult::Success) {
            printf("SetViewTransform 失败于步 %d\n", i); break;
        }
        (void)FFF3FP_Redraw(player);
        ++下发;

        std::uint64_t after = before;
        for (int poll = 0; poll < 60 && after <= before; ++poll) {
            Pump(8);
            after = SwapPresents(player, snap);
        }
        const bool advanced = after > before;
        if (advanced) ++呈现推进;

        bool ok = false;
        const double now = PatchSum(player, px, py, buf, ok);
        const double delta = ok ? std::fabs(now - prev) / static_cast<double>(buf.size()) : 0.0;
        if (ok && delta > 1e-6) ++内容变化;
        if (ok) prev = now;
        printf("%5d %9.4f %10s %10.6f  %s\n", i, zoom,
            advanced ? "+1" : "0", delta, (!ok || delta <= 1e-6) ? "未见变化" : "变");
    }

    printf("\n下发 %d ⇒ 呈现推进 %d、内容逐帧不同 %d\n", 下发, 呈现推进, 内容变化);
    const bool presented = 呈现推进 >= static_cast<int>(下发 * 0.8);
    const bool distinct = 内容变化 >= static_cast<int>(下发 * 0.6);
    printf("结论：%s\n",
        (presented && distinct)
            ? "PASS 中间态逐帧真的到达屏幕（不是被合并成一次跳变）"
            : (presented ? "呈现推进了但内容多数相同 ⇒ 帧到得晚于节拍或被合并，未定"
                         : "未判定：呈现代数几乎不推进，这条路在本机不可判"));

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    if (presented && distinct) return 0;
    return 3;
}
