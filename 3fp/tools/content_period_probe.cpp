// ============================================================================
// content_period_probe.cpp —— 内容周期的离线验收（2026-10-16）
//
// 要判的事只有一件：内核喂给翻帧节律锁的"内容率"，在**可变帧率**源上是不是逐帧走的。
//
// 为什么另开一支、不塞进 present_pacing_probe：那支判的是网格/停留/丢弃分账，前提
//   是窗口真的在被合成 —— 它实测会因为遮挡或前台归属而整轮只推进 0.76s 媒体时间、
//   只呈现 19 帧，那种红不是内核的账。本支**不建输出窗口**，因此没有合成、没有焦点、
//   没有显示侧计时前提，读数只由"内容周期"这一条决定，同一个文件反复跑应当同判。
//
// 无窗口带来的两个约束，都是这台机器上量出来的事实（docs/03 §A9 ⑥）：
//   · 没有交换链 ⇒ 网格永不标定，①②③④ 那组显示判据在这支里没有操作数，本支不碰。
//   · VideoRenderer::DisplayCadenceHz() 退回构造默认 **60.0f** ⇒ 上限读数是
//     min(60, 内容率)。所以本支用的夹具**两段速率都必须低于 60**（12b = 20 与 40），
//     否则晚段会读成 60，分不清是内容率赢还是面板默认值赢。
//
// 用法：content_period_probe '<绝对媒体路径>' [秒数]
//   CPCP_EARLY_HZ=N   前段速率（必填）
//   CPCP_LATE_HZ=N    后段速率（必填）
//   CPCP_AVG_HZ=N     整片平均速率（选填：给了才判"不许停在平均上"）
//   CPCP_POLL_MS=10   轮询间隔
//   ⚠ Windows 路径必须单引号；相对路径内核会拒。
// 编译：py 3fp/tools/build_probe.py content_period_probe
// 退出码：0=判且绿 · 1=有红 · 2=建会话/Open 失败 · 3=不可判
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

std::atomic<bool> g_openCompleted{ false };

void __cdecl OnEvent(void*, FFF3FPEvent event, const char*) {
    if (event == FFF3FPEvent::OpenCompleted) g_openCompleted.store(true);
}

std::int64_t Now100ns() {
    LARGE_INTEGER v{}; QueryPerformanceCounter(&v);
    static const LARGE_INTEGER f = [] { LARGE_INTEGER x{}; QueryPerformanceFrequency(&x); return x; }();
    return v.QuadPart * 10'000'000 / f.QuadPart;
}

void Pump(int ms) {
    const auto end = Now100ns() + ms * 10'000;
    while (Now100ns() < end) std::this_thread::sleep_for(std::chrono::milliseconds(1));
}

struct Sample { double tS; double hz; };
struct Plateau { double startS; double durationS; double meanHz; std::size_t count; };

// 连续读数落在同一条位（相对上次均值 <5%）算一段。判"换档"必须按段，不能按整片均值：
// 均值会把 20/40 两段糊成 30，而 30 恰好就是"走容器平均数"那条路应当给出的数。
std::vector<Plateau> FindPlateaus(const std::vector<Sample>& samples) {
    std::vector<Plateau> out;
    for (const auto& s : samples) {
        if (!out.empty()) {
            auto& last = out.back();
            if (std::fabs(s.hz - last.meanHz) <= last.meanHz * 0.05) {
                last.meanHz = (last.meanHz * last.count + s.hz) / (last.count + 1);
                ++last.count;
                last.durationS = s.tS - last.startS;
                continue;
            }
        }
        out.push_back(Plateau{s.tS, 0.0, s.hz, 1});
    }
    return out;
}

const Plateau* FindPlateau(const std::vector<Plateau>& plateaus, double wantHz, double minS) {
    if (wantHz <= 0.0) return nullptr;
    for (const auto& p : plateaus)
        if (p.durationS >= minS && std::fabs(p.meanHz - wantHz) <= wantHz * 0.05) return &p;
    return nullptr;
}

}   // namespace

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);   // 崩在第几步必须看得见，别让块缓冲吞掉
    if (argc < 2) {
        std::printf("用法：content_period_probe '<绝对媒体路径>' [秒数]\n");
        return 2;
    }
    const std::string path = argv[1];
    const double seconds = argc > 2 ? std::atof(argv[2]) : 4.5;
    auto EnvInt = [](const char* name) { const auto raw = std::getenv(name); return raw ? std::atoi(raw) : 0; };
    const auto earlyHz = EnvInt("CPCP_EARLY_HZ");
    const auto lateHz = EnvInt("CPCP_LATE_HZ");
    const auto avgHz = EnvInt("CPCP_AVG_HZ");
    int pollMs = 10;
    if (const auto raw = std::getenv("CPCP_POLL_MS")) pollMs = std::clamp(std::atoi(raw), 1, 100);

    // 无窗口会话：config.outputWindow = nullptr，内核走 headless 分支，不建交换链。
    FFF3FPConfiguration config{};
    config.size = sizeof(config);
    config.version = FFF3FP_GetApiVersion();
    config.outputWindow = nullptr;
    config.decodeMode = FFF3FPDecodeMode::Cpu;
    config.colorMode = FFF3FPColorMode::MapToSdr;
    config.sdrPeakNits = 100.0f;
    config.sdrPaperWhiteNits = 203.0f;
    config.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    config.preferredAdapterIndex = -1;
    config.eventCallback = &OnEvent;

    FFF3FPHandle player = nullptr;
    const auto createResult = FFF3FP_Create(&config, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败（result=%d）⇒ 无窗口会话在这台机器上建不起来，本支不适用\n",
            static_cast<int>(createResult));
        return 2;
    }
    const auto openResult = FFF3FP_Open(player, path.c_str());
    if (openResult != FFFResult::Success) {
        char why[512]{}; std::uint32_t needed = 0;
        FFF3FP_GetLastError(player, why, static_cast<std::uint32_t>(sizeof(why) - 1), &needed);
        std::printf("Open 失败：result=%d 内核错误=\"%s\" 路径=\"%s\"\n",
            static_cast<int>(openResult), why, path.c_str());
        FFF3FP_Destroy(player); return 2;
    }
    for (int i = 0; i < 400 && !g_openCompleted.load(); ++i) Pump(25);
    std::printf("=== 内容周期（无窗口）验收 ===\n");
    std::printf("file=%s 观察=%.1fs 轮询=%dms 段率=%d/%d 平均预期=%d\n",
        path.c_str(), seconds, pollMs, earlyHz, lateHz, avgHz);

    // 内核自己看到的容器率与"恒定/可变"判定。这四个数是解释本支读数的关键：退回容器
    // 那一档时上限会读成什么，全看 av_guess_frame_rate()（它不是裸的 avg_frame_rate）。
    std::string lastMode;   // open 后先存一份，播完后再读一份并判（实测判词那时才成立）
    {
        std::string json(64u * 1024u, '\0');
        std::uint32_t written = 0;
        if (FFF3FP_GetMediaInfo(player, json.data(), static_cast<std::uint32_t>(json.size()), &written)
            == FFFResult::Success && written > 0) {
            json.resize(written);
            auto JsonNumber = [&json](const char* key) -> long long {
                const auto at = json.find(key);
                if (at == std::string::npos) return -1;
                const auto colon = json.find(':', at);
                return colon == std::string::npos ? -1 : std::atoll(json.c_str() + colon + 1);
            };
            auto JsonText = [&json](const char* key) -> std::string {
                const auto at = json.find(key);
                if (at == std::string::npos) return "?";
                const auto q1 = json.find('"', json.find(':', at) + 1);
                const auto q2 = json.find('"', q1 + 1);
                return q1 == std::string::npos || q2 == std::string::npos ? "?" : json.substr(q1 + 1, q2 - q1 - 1);
            };
            const auto avgN = JsonNumber("averageFrameRateNumerator");
            const auto avgD = JsonNumber("averageFrameRateDenominator");
            const auto nomN = JsonNumber("nominalFrameRateNumerator");
            const auto nomD = JsonNumber("nominalFrameRateDenominator");
            lastMode = JsonText("frameRateMode");
            std::printf("  内核 media info：av_guess=%lld/%lld=%.2f Hz｜nominal(r_frame_rate)=%lld/%lld=%.2f Hz"
                "｜frameRateMode=\"%s\"\n",
                avgN, avgD, avgD > 0 ? static_cast<double>(avgN) / avgD : 0.0,
                nomN, nomD, nomD > 0 ? static_cast<double>(nomN) / nomD : 0.0,
                JsonText("frameRateMode").c_str());
        }
    }
    if (earlyHz <= 0 || lateHz <= 0) {
        std::printf("\nCPCP RESULT: NOT-JUDGED（没给 CPCP_EARLY_HZ / CPCP_LATE_HZ，无法预测两段上限）\n");
        FFF3FP_Destroy(player); return 3;
    }

    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    Pump(400);   // 让首帧落地

    FFF3FPSnapshot baseline{}; baseline.size = sizeof(baseline); baseline.version = 20;
    FFF3FP_GetSnapshot(player, &baseline);
    FFF3FPPresentPacingStatus basePacing{};
    basePacing.size = sizeof(basePacing); basePacing.version = 20;
    FFF3FP_GetPresentPacingStatus(player, &basePacing);

    const auto start = Now100ns();
    std::vector<Sample> series;
    while (Now100ns() - start < static_cast<std::int64_t>(seconds * 10'000'000)) {
        FFF3FPPresentPacingStatus p{}; p.size = sizeof(p); p.version = 20;
        FFF3FP_GetPresentPacingStatus(player, &p);
        series.push_back(Sample{(Now100ns() - start) / 10'000'000.0,
                                static_cast<double>(p.frameRateBoundHz)});
        std::this_thread::sleep_for(std::chrono::milliseconds(pollMs));
    }
    FFF3FPSnapshot after{}; after.size = sizeof(after); after.version = 20;
    FFF3FP_GetSnapshot(player, &after);
    // 播完之后再读一次：A12 那个实测判词要呈现够 12 帧才成立，只读 open 后那一版就会
    // 永远读到容器值，把"已经修好"读成"没修"。
    {
        std::string json(64u * 1024u, '\0');
        std::uint32_t written = 0;
        if (FFF3FP_GetMediaInfo(player, json.data(), static_cast<std::uint32_t>(json.size()), &written)
            == FFFResult::Success && written > 0) {
            json.resize(written);
            const auto at = json.find("\"frameRateMode\"");
            const auto q1 = at == std::string::npos ? std::string::npos
                : json.find('"', json.find(':', at) + 1);
            const auto q2 = q1 == std::string::npos ? std::string::npos : json.find('"', q1 + 1);
            lastMode = q1 == std::string::npos || q2 == std::string::npos
                ? std::string("?") : json.substr(q1 + 1, q2 - q1 - 1);
        }
    }
    std::printf("  内核 media info（播完后）：frameRateMode=\"%s\"\n", lastMode.c_str());

    const auto wall100ns = Now100ns() - start;
    const double presented = static_cast<double>(after.presentedVideoFrames - baseline.presentedVideoFrames);
    const double drops = static_cast<double>(after.droppedVideoFrames - baseline.droppedVideoFrames);
    const double mediaS = std::max(0.0,
        static_cast<double>(after.position100ns - baseline.position100ns) / 10'000'000.0);
    const auto plateaus = FindPlateaus(series);

    std::printf("\n--- 读数 ---\n");
    std::printf("  媒体时钟推进=%.2fs（观察窗 %.2fs）｜呈现帧=%.0f｜传统口径丢弃=%.0f\n",
        mediaS, seconds, presented, drops);
    std::printf("  采样 %zu 个｜平台段 %zu 条：", series.size(), plateaus.size());
    for (const auto& p : plateaus)
        std::printf("[起 %.2fs 长 %.2fs @ %.1fHz] ", p.startS, p.durationS, p.meanHz);
    std::printf("\n");

    int judged = 0, red = 0, undecided = 0;
    auto Judge = [&](bool evaluable, bool ok, const char* label) {
        if (!evaluable) { ++undecided; std::printf("  [未判] %s\n", label); return; }
        ++judged;
        if (ok) std::printf("  [绿] %s\n", label);
        else { ++red; std::printf("  [红] %s\n", label); }
    };

    // 前置一：无窗口 ⇒ 上限是 min(60, 内容率)，所以后段速率必须严格低于 60，
    //   否则"读到一个 60 的平台段"既可能是内容率也可能是那个默认值，判据没有操作数。
    Judge(true, lateHz < 60, "前置：晚段速率低于无窗口默认面板值 60（否则 min() 分不清哪半赢）");
    // 前置二：会话没被饿住。媒体时钟不推进就等于没跑到后段，"晚段没换档"不是内核的账。
    Judge(mediaS > 0.0, mediaS >= seconds * 0.6, "前置：媒体时钟推进 ≥ 观察窗 60%");
    if (mediaS < seconds * 0.6) {
        std::printf("\nCPCP RESULT: NOT-JUDGED（会话只推进 %.2fs，段判据没有操作数）\n", mediaS);
        FFF3FP_Destroy(player); return 3;
    }
    // 帧率模式判据：这是"媒体信息页那句 恒定/可变 在可变帧率上是不是假话"的直接口。
    // 期望值由调用方按夹具声明（CPCP_EXPECT_MODE）——探针不许自己猜，否则是自己给自己出题。
    if (const auto* want = std::getenv("CPCP_EXPECT_MODE")) {
        const std::string expected = want;
        const std::string label = "⑥ media info 的 frameRateMode == \"" + expected +
            "\"（播够 12 帧之后）";
        Judge(presented > 20 && !expected.empty(), lastMode == expected, label.c_str());
    } else {
        std::printf("  [未判] ⑥ frameRateMode：没给 CPCP_EXPECT_MODE，不猜期望值\n");
        ++undecided;
    }
    // CFR 对照模式（两段给同一个率）：这时问的不是"会不会换档"，而是
    // "把内容周期的取数来源从 frame->duration 换成队列里的 PTS 差之后，恒定帧率的
    // 读数动没动"。要求整窗只有一条平台段、落在该率上、且不丢帧。
    const bool cfr = earlyHz == lateHz;
    if (cfr) {
        const auto* only = FindPlateau(plateaus, static_cast<double>(earlyHz), seconds * 0.8);
        std::printf("  ── CFR 对照模式（两段同率 %d）──\n", earlyHz);
        Judge(true, plateaus.size() == 1 && only != nullptr,
            "Ⓒ 整窗只有一条平台段且落在该率上（取数来源换了、读数没换）");
        Judge(presented > 20, drops <= 2,
            "Ⓓ 没因为取数来源变化而丢帧（传统口径丢弃 ≤2）");
    }
    const auto* early = FindPlateau(plateaus, static_cast<double>(earlyHz), 0.4);
    const auto* late = FindPlateau(plateaus, static_cast<double>(lateHz), 0.4);
    Judge(!cfr, early != nullptr, "① 前段有 ≥0.4s 平台段落在 min(默认面板 60, 前段率)");
    Judge(!cfr, late != nullptr, "② 后段有 ≥0.4s 平台段落在 min(默认面板 60, 后段率)");
    // 方向：先 20 后 40。只问"有没有 40 的平台段"会被片尾那条退回容器档的尾巴蒙过去
    //   —— 实测 12a 的 .ts 臂就在 EOF 之后多出一条 [起 3.41s 长 0.59s @ 25.0Hz]。
    Judge(!cfr && early != nullptr && late != nullptr,
        early != nullptr && late != nullptr && early->startS < late->startS,
        "③ 换档方向不能反（前段平台段先出现）");
    Judge(!cfr && avgHz > 0 && avgHz != earlyHz && avgHz != lateHz,
        FindPlateau(plateaus, static_cast<double>(avgHz), 0.4) == nullptr,
        "④ 上限没停在整片平均速率上（容器 avg_frame_rate 那条路的签名）");
    // 后果这一栏才是本支真正在意的东西：上限掐错段 ⇒ 队列积压 ⇒ 会话按迟到丢帧。
    // 未修复时 12a.ts 实测 4 秒丢 68 帧 ≈ (60−25)×2s，而修复后应当近乎 0。
    Judge(!cfr && presented > 20, drops <= 2,
        "⑤ 没有因为上限掐错段而丢帧（传统口径丢弃 ≤2）");

    std::printf("\nCPCP RESULT: %s（判 %d / 红 %d / 未判 %d）\n",
        red > 0 ? "FAIL" : (judged > 0 ? "PASS" : "NOT-JUDGED"), judged, red, undecided);
    FFF3FP_Stop(player);
    FFF3FP_Destroy(player);
    return red > 0 ? 1 : (judged > 0 ? 0 : 3);
}
