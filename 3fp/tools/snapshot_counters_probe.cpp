// 把 FFF3FPSnapshot 里几个"呈现/解码"计数器在**一次视图变更前后**摊开对比，
// 用来回答两件 docs/79 挂着没闭的事：
//
//   Q1（C0 该用哪个信号）：静帧被 Redraw 强制重呈时，推进的是
//      presentedVideoFrames、swapChainPresents，还是两者都不动？
//      zoom_cost_probe 用 presentedVideoFrames 量到 16/16 不推进，可像素读数确实变了
//      ⇒ 呈现发生了、代数没走 —— 要么这条路径不计这个数，要么我读错了字段。
//
//   Q2（U3 快照 ABI）：探针把 FFF3FPSnapshot.version 硬钉成 8（image_zoom_probe.cpp:85、
//      image_load_probe.cpp:114 的同源写法），而 PlayerApiVersion=19。内核既不填也不校验
//      该字段的调用点我 grep 不到 ⇒ 会不会 GetSnapshot 按 8 接受、却按别的布局写，
//      让我**静默读到错位的字段**（docs/78 §1 记过同族缺陷）。
//
// 取证办法：把 version 依次设成 8 / 19 / 0 / 一个巨大值，各自 GetSnapshot 一次，
// 打印返回码 + 回写的 size/version + 全部计数器；再在同一档位做一次 ZoomViewAt+Redraw，
// 500ms 轮询后打印差值。**若某个 version 让读数和另一个 version 差出量级，ABI 就是错位的。**
//
// 用法：snapshot_counters_probe <媒体> [--waits N]
// 退出码：0 至少有一个计数器随重呈推进 / 3 全都纹丝不动（那 C0 得换量法）/
//         1 建立失败 / 2 用法 / 4 回读失败

#include <windows.h>

#include <cstdint>
#include <cstdio>
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
void Dump(const char* label, const FFF3FPSnapshot& s, const FFFResult r) {
    printf("%-14s rc=%d size=%u ver=%u state=%d | 解码%llu 呈现%llu 丢%llu 合并%llu "
        "交换链呈%llu 帧号%lld 队列%u\n",
        label, static_cast<int>(r), s.size, s.version, static_cast<int>(s.state),
        static_cast<unsigned long long>(s.decodedVideoFrames),
        static_cast<unsigned long long>(s.presentedVideoFrames),
        static_cast<unsigned long long>(s.droppedVideoFrames),
        static_cast<unsigned long long>(s.coalescedVideoFrames),
        static_cast<unsigned long long>(s.swapChainPresents),
        static_cast<long long>(s.frameIndex), s.queuedVideoFrames);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: snapshot_counters_probe <媒体>\n"); return 2; }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFSnapCountersWnd";
    RegisterClassW(&wc);
    RECT want{ 0, 0, 1264, 681 };
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
    const auto window = CreateWindowExW(0, L"FFFSnapCountersWnd", L"counters",
        WS_OVERLAPPEDWINDOW, 20, 20, want.right - want.left, want.bottom - want.top,
        nullptr, nullptr, wc.hInstance, nullptr);

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
        printf("Open 失败（路径要全反斜杠）\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 400 && !g_open; ++i) Pump(50);
    if (!g_open) { printf("没等到 OpenCompleted\n"); FFF3FP_Destroy(player); return 1; }
    FFF3FP_Play(player);
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(600);

    printf("结构体 sizeof(FFF3FPSnapshot) = %u，FFF3FP_GetApiVersion() = %u\n\n",
        static_cast<unsigned>(sizeof(FFF3FPSnapshot)), FFF3FP_GetApiVersion());

    // ── Q2：换 version 值读同一次快照，看读数会不会跳量级 ────────────────────
    const std::uint32_t probes[] = { 8u, 19u, 0u, 4000000000u };
    for (const std::uint32_t version : probes) {
        FFF3FPSnapshot s{};
        s.size = sizeof(s);
        s.version = version;
        const auto r = FFF3FP_GetSnapshot(player, &s);
        Dump(version == 8u ? "version=8" : (version == 19u ? "version=19" :
            (version == 0u ? "version=0" : "version=巨大")), s, r);
    }
    std::vector<float> px(4, 0.0f);
    std::uint32_t bitDepth = 0;
    const auto readOk = FFF3FP_ReadVideoPixelRegion(player, 400, 300, 1u, 1u, px.data(),
        static_cast<std::uint32_t>(px.size()), &bitDepth);
    printf("\n像素回读 rc=%d 值=(%.4f,%.4f,%.4f)\n\n", static_cast<int>(readOk),
        px[0], px[1], px[2]);
    if (readOk != FFFResult::Success) {
        printf("量具回读失败。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    // ── Q1：一次重呈前后，哪个计数器动了 ────────────────────────────────────
    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 20;
    if (FFF3FP_GetSnapshot(player, &before) != FFFResult::Success) {
        printf("前置快照失败\n"); FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }
    Dump("变更前", before, FFFResult::Success);
    float resulting = 0.0f;
    const auto zoomRc = FFF3FP_ZoomViewAt(player, 1.25f, 0.75f, 0.30f, &resulting);
    const auto redrawRc = FFF3FP_Redraw(player);
    Pump(600);
    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    const auto afterRc = FFF3FP_GetSnapshot(player, &after);
    Dump("变更后", after, afterRc);
    printf("ZoomViewAt rc=%d zoom=%.3f Redraw rc=%d\n", static_cast<int>(zoomRc),
        resulting, static_cast<int>(redrawRc));

    const bool moved = after.presentedVideoFrames != before.presentedVideoFrames ||
        after.swapChainPresents != before.swapChainPresents ||
        after.coalescedVideoFrames != before.coalescedVideoFrames ||
        after.droppedVideoFrames != before.droppedVideoFrames ||
        after.frameIndex != before.frameIndex;
    printf("\nΔ呈现=%lld Δ交换链=%lld Δ合并=%lld Δ丢帧=%lld Δ帧号=%lld\n",
        static_cast<long long>(after.presentedVideoFrames - before.presentedVideoFrames),
        static_cast<long long>(after.swapChainPresents - before.swapChainPresents),
        static_cast<long long>(after.coalescedVideoFrames - before.coalescedVideoFrames),
        static_cast<long long>(after.droppedVideoFrames - before.droppedVideoFrames),
        static_cast<long long>(after.frameIndex - before.frameIndex));
    printf("结论：%s\n", moved ? "有计数器随重呈推进 ⇒ 用它当 C0 的主信号"
        : "全部计数器纹丝不动 ⇒ 静帧重呈不计数，C0 必须换量法（不能用快照代数）");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return moved ? 0 : 3;
}
