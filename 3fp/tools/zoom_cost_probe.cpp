// 量每格滚轮缩放的"下发 → 画面真的变了"延迟，用来判定降采样链缓存键是否在付费（docs/79 C0）。
//
// 背景（全部读自源码，见 docs/79 §1/§5）：
//   降采样链按 (源宽,源高,目标宽,目标高,格式) 全等才复用（VideoRenderer.cpp:3778-3782），
//   否则整链释放重建（:3784-3793）。而它的目标尺寸就是**缩放后的盒**
//   （:5938 destination.width = zoomedWidth → :5962 PrepareScaledVideo → :4037 targetWidth）。
//   ⇒ 只要"缩放盒 < 源尺寸"，每格滚轮都换 target ⇒ 每格整链重建 + 全图重降采样。
//   越过 1:1 后 targetWidth==planeWidth，链塌缩成 0 pass，才不付费。
//
// ⚠ 为什么不是计 API 调用时长（v1 的错法，实测已证伪）：`ZoomViewAt` 只 bump 呈现代数并
//   notify（PlayerSession.cpp:1400 → VideoRenderer::Redraw），绘制在 presenter 线程上做，
//   所以 API 调用与紧随的一次回读都是 ~0 ms —— v1 就是这么读出**全零**，却因为守卫写成
//   `activeAvg < collapsedAvg*1.3`，0.0 < 0.0 为假 ⇒ 全零被放行还打印了"贵 85.9 倍"这种
//   算不出却敢印的数。现在改成等**画面变化**，并把"量不到"变成显式结局。
//
// 测法：每步下发 ×1.25 后，轮询 `FFF3FPSnapshot.swapChainPresents`，直到它推进 ——
// 那才是"这一格真的画出来了"。这个延迟就是用户手上的"卡不卡"，链的耗时必然在它的关键路径上。
// 为什么不用像素读数当主判据：像素在有些档位本来就"不变"（细条纹被重缩略自平均、
// 粗块在高倍下邻域全平），拿"没变"当"没呈现"会把量具自己判死 —— v2/v3 就分别栽在这两种上。
// 像素读数只留作第二参照（代数刚推进时内容是否已换）。
//
// 自检（先证明量具活着再量被测件）：
//   S1 QPC 过 Sleep(50) 必须落在 40~80ms，否则整套计时作废；
//   S2 每步都必须观测到变化，超时步数 > 0 时判"读数无效"；
//   S3 两个区间（活动/塌缩）必须各取到样本，且活动段均值必须是**可分辨的正数**。
//
// 用法：zoom_cost_probe <媒体> --src WxH [--steps 18] [--client 1264x681]
//   --src 必须由 ffprobe 从产物读出后传入，不要手抄：
//     ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 <媒体>
// 退出码：0 量到且成本随交叉变化 / 3 读数无效（没跟踪到链，不能据此判定）/
//         1 建立失败 / 2 用法 / 4 量具自检失败

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
double MsSince(const LARGE_INTEGER start, const LARGE_INTEGER freq) {
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    return static_cast<double>(now.QuadPart - start.QuadPart) * 1000.0 /
        static_cast<double>(freq.QuadPart);
}

constexpr int kPatch = 8;                        // 回读小块边长（8x8：一次回读 ≈ 本量具的分辨率下限）
constexpr double kChangeEps = 0.0005;            // 小块均值和的变化超过它才算"画面变了"
constexpr int kWaitPerStepMs = 4000;             // 单步等变化的上限
constexpr double kResolvableMs = 0.5;            // 低于它就是量不到，不许拿来比大小

struct Sample { double sum = 0.0; bool ok = false; };

Sample ReadPatch(FFF3FPHandle player, const std::uint32_t x, const std::uint32_t y,
    std::vector<float>& buffer) {
    std::uint32_t bitDepth = 0;
    Sample out;
    if (FFF3FP_ReadVideoPixelRegion(player, x, y, kPatch, kPatch, buffer.data(),
            static_cast<std::uint32_t>(buffer.size()), &bitDepth) != FFFResult::Success)
        return out;
    double total = 0.0;
    for (const float value : buffer) total += value;
    out.sum = total;
    out.ok = true;
    return out;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: zoom_cost_probe <媒体> --src WxH [--steps N] [--client WxH]\n");
        return 2;
    }
    const auto path = Utf8(argv[1]);
    int srcW = 0, srcH = 0, steps = 18, clientW = 1264, clientH = 681;
    for (int i = 2; i < argc; ++i) {
        if (i + 1 >= argc) return 2;
        const std::wstring key = argv[i];
        if (key == L"--src") (void)swscanf_s(argv[++i], L"%dx%d", &srcW, &srcH);
        else if (key == L"--steps") (void)swscanf_s(argv[++i], L"%d", &steps);
        else if (key == L"--client") (void)swscanf_s(argv[++i], L"%dx%d", &clientW, &clientH);
        else return 2;
    }
    if (srcW <= 0 || srcH <= 0 || steps <= 0) {
        printf("--src/--steps 必须为正；--src 要由 ffprobe 从产物读出。\n");
        return 2;
    }

    LARGE_INTEGER freq{};
    // 这里必须是 QueryPerformanceFrequency。写成 QueryPerformanceCounter 会把"当前计数"
    // 当成分母，于是任何时长都被除成 ~0 ms —— 本轮 v1 的全零读数就是这个错，
    // 靠上面的 S1 自检（Sleep(50) 必须落在 40~80ms）才当场暴露。
    QueryPerformanceFrequency(&freq);
    {
        // S1 量具自检：QPC 必须真的在走。
        LARGE_INTEGER t0{};
        QueryPerformanceCounter(&t0);
        Sleep(50);
        const double probe = MsSince(t0, freq);
        printf("QPC 自检 Sleep(50) 读到 %.1f ms\n", probe);
        if (!(probe > 40.0 && probe < 80.0)) {
            printf("量具自检失败：计时器不可信，本轮不产出任何结论。\n");
            return 4;
        }
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFZoomCostWnd";
    RegisterClassW(&wc);
    // 用 AdjustWindowRect 把**客户区**当自变量：内核的目标盒由客户区算，
    // 拿窗口外框当客户区会让预测交叉点整体错位。
    RECT want{ 0, 0, clientW, clientH };
    AdjustWindowRect(&want, WS_OVERLAPPEDWINDOW, FALSE);
    const auto window = CreateWindowExW(0, L"FFFZoomCostWnd", L"cost", WS_OVERLAPPEDWINDOW,
        20, 20, want.right - want.left, want.bottom - want.top, nullptr, nullptr,
        wc.hInstance, nullptr);

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
        printf("Open 失败（路径必须是全反斜杠的 Windows 路径；bash 里用 cygpath -w）\n");
        FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 400 && !g_open; ++i) Pump(50);
    if (!g_open) { printf("没等到 OpenCompleted\n"); FFF3FP_Destroy(player); return 1; }
    FFF3FP_Play(player);
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(600);

    RECT client{};
    GetClientRect(window, &client);
    const double cw = client.right - client.left, ch = client.bottom - client.top;
    const double fitScale = (cw / srcW < ch / srcH) ? cw / srcW : ch / srcH;
    const double crossing = 1.0 / fitScale;   // 缩放盒 == 源尺寸的那一档，链在此塌缩
    printf("源 %dx%d  客户区 %.0fx%.0f  fit=%.4f  预测交叉 zoom=%.2f（此前链活动，此后塌缩）\n\n",
        srcW, srcH, cw, ch, fitScale, crossing);

    std::vector<float> buffer(kPatch * kPatch * 4, 0.0f);
    // 采样块故意**离开锚点**：锚定缩放要求锚点下的内容不变，把块放在锚点上
    // 等于等一个正确实现里不该发生的现象（v2 就栽在这儿，16 步里 6~14 步"超时"）。
    const auto patchX = static_cast<std::uint32_t>(cw * 0.40);
    const auto patchY = static_cast<std::uint32_t>(ch * 0.65);
    Sample previous = ReadPatch(player, patchX, patchY, buffer);
    if (!previous.ok) {
        printf("起始回读失败：量具读不到产物。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    {
        // 分辨率下限：把同一次回读再计一遍时，它决定"变化延迟"能分辨到多少毫秒。
        LARGE_INTEGER f0{};
        QueryPerformanceCounter(&f0);
        (void)ReadPatch(player, patchX, patchY, buffer);
        const double floorMs = MsSince(f0, freq);
        previous = ReadPatch(player, patchX, patchY, buffer);
        printf("单次回读成本 = %.2f ms ⇒ 延迟分辨率下限约 %.2f ms（低于它的差别不要当结论）\n\n",
            floorMs, floorMs);
    }

    // 主判据换成**内容无关**的呈现代数：FFF3FPSnapshot.swapChainPresents
    // （实测：静帧一次重呈 ΔpresentedVideoFrames=0、ΔswapChainPresents=+1，
    //   取证见 snapshot_counters_probe.cpp / 3fp/build 同名产物）。
    // 像素读数随缩放档位可能不变（平块/重缩略自平均都会），
    // 拿"像没变"当"没呈现"会把量具自己判死 —— 实测：细条纹在 16:1 下头两步超时，
    // 粗块在高倍下 16 步全超时。代数推进才是"这一格真的画出来了"的凭据。
    // 版本/尺寸照 image_load_probe.cpp:114 的同源写法；GetSnapshot 被拒就显式失败，
    // 不静默出零（docs/78 §1 记过这类 ABI 静默错位）。
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) {
        printf("GetSnapshot 失败：呈现代数读不到，本探针不产出结论。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    {
        // **自由推进对照**：不发任何命令，等 600ms，看 swapChainPresents 会不会自己走。
        // 若它自由推进，那"等到代数推进"就只测到 vsync 节拍，与单次缩放的开销无关 ——
        // 三个档位都落在 ~15ms（60Hz 的间隔）正是这个怀疑的形状，必须当场否证或坐实。
        const std::uint64_t idleBefore = snapshot.swapChainPresents;
        Pump(600);
        if (FFF3FP_GetSnapshot(player, &snapshot) == FFFResult::Success) {
            const std::uint64_t d = snapshot.swapChainPresents - idleBefore;
            printf("自由推进对照（不发任何命令等 600ms）：交换链计数 +%llu\n",
                static_cast<unsigned long long>(d));
            if (d > 1) {
                printf("读数无效：呈现代数在空转时也推进 ⇒ 本量法测到的是节拍而不是缩放开销；"
                    "C0 记**未判定**，要换包内部时或链计数器那条路。\n");
                FFF3FP_Close(player); FFF3FP_Destroy(player);
                if (window != nullptr) DestroyWindow(window);
                return 3;
            }
        }
    }

    printf("%5s %8s %12s %7s %10s  %s\n", "步", "zoom", "呈现延迟ms", "轮询", "像素已变", "链");
    double activeTotal = 0.0, collapsedTotal = 0.0;
    int activeCount = 0, collapsedCount = 0, timeouts = 0, pixelConfirmed = 0;
    float zoom = 1.0f;
    for (int step = 1; step <= steps; ++step) {
        // 主信号 = swapChainPresents。实测（snapshot_counters_probe，静帧重呈一次）：
        // ΔpresentedVideoFrames=0 而 ΔswapChainPresents=+1 ⇒ 静帧强制重呈只推交换链计数。
        const std::uint64_t before = snapshot.swapChainPresents;
        LARGE_INTEGER t0{};
        QueryPerformanceCounter(&t0);
        float resulting = zoom;
        (void)FFF3FP_ZoomViewAt(player, 1.25f, 0.75f, 0.30f, &resulting);
        (void)FFF3FP_Redraw(player);

        double latency = -1.0;
        int polls = 0;
        while (MsSince(t0, freq) < kWaitPerStepMs) {
            Pump(1);
            ++polls;
            if (FFF3FP_GetSnapshot(player, &snapshot) != FFFResult::Success) break;
            if (snapshot.swapChainPresents > before) {
                latency = MsSince(t0, freq);
                break;
            }
        }

        // 第二参照（不独立判定，只佐证）：代数刚推进的那一刻，采样块内容是否已经变了。
        zoom = resulting > 0.0f ? resulting : zoom * 1.25f;
        const bool active = static_cast<double>(zoom) < crossing;
        const Sample current = ReadPatch(player, patchX, patchY, buffer);
        bool pixelChanged = false;
        if (current.ok && previous.ok) {
            pixelChanged = std::fabs(current.sum - previous.sum) >
                kChangeEps * static_cast<double>(buffer.size());
            if (pixelChanged) ++pixelConfirmed;
            previous = current;
        }
        if (latency < 0.0) {
            ++timeouts;
            printf("%5d %8.3f %12s %7d %10s  %s 代数未推进\n", step, zoom, "超时", polls,
                pixelChanged ? "是" : "否", active ? "活动" : "塌缩");
            continue;
        }
        (active ? activeTotal : collapsedTotal) += latency;
        (active ? activeCount : collapsedCount) += 1;
        printf("%5d %8.3f %12.2f %7d %10s  %s\n", step, zoom, latency, polls,
            pixelChanged ? "是" : "否", active ? "活动" : "塌缩");
        Pump(60);
    }
    printf("\n第二参照：代数刚推进时像素已变 = %d/%d 步（低于 100%% 说明有的档位内容本来就平，"
        "不影响主判据）\n", pixelConfirmed, steps);

    const double activeAvg = activeCount ? activeTotal / activeCount : 0.0;
    const double collapsedAvg = collapsedCount ? collapsedTotal / collapsedCount : 0.0;
    printf("\n活动段均值 = %.2f ms（%d 步）；塌缩段均值 = %.2f ms（%d 步）；超时 %d 步\n",
        activeAvg, activeCount, collapsedAvg, collapsedCount, timeouts);

    // S2/S3 守卫：任何一条不过就记"未判定"，绝不打印算不出却敢印的比值。
    if (timeouts > 0) {
        printf("读数无效：有 %d 步呈现代数没推进，均值被剔样本污染。\n", timeouts);
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 3;
    }
    if (activeCount == 0 || collapsedCount == 0) {
        printf("读数无效：没跨过预测交叉点，两个区间没各取到样本。加 --steps 或换媒体重跑。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 3;
    }
    if (!(collapsedAvg > kResolvableMs) || !(activeAvg > kResolvableMs)) {
        printf("读数无效：至少一段均值 %.2f/%.2f ms 低于可分辨下限 %.1f ms，"
            "这套量具没量到关键路径上的工作，**不能**据此判定缓存键成本。\n",
            activeAvg, collapsedAvg, kResolvableMs);
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 3;
    }
    if (activeAvg < collapsedAvg * 1.3) {
        printf("读数支持另一种解释：活动段没有显著贵过塌缩段（%.2f vs %.2f）"
            "⇒ 每格滚轮的整链重建成本在本机不可见，轨道 C 降级为"
            "「只在 >16k 档顺手做」，不进平滑缩放的前置。\n", activeAvg, collapsedAvg);
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 0;
    }
    printf("结论：链成本成立 —— 活动段单格 %.2f ms 比塌缩段 %.2f ms 贵 %.1f 倍；"
        "滚轮每格都在重降采样整幅，轨道 C 是平滑缩放的前置。\n",
        activeAvg, collapsedAvg, activeAvg / collapsedAvg);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
