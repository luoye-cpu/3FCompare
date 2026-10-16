// ============================================================================
// content_period_probe.cpp —— 内容周期的离线验收（2026-10-16，随上限层语义改版）
//
// 要判的事：内核的呈现上限层在**可变帧率**源上有没有把每一段的内容率跟上。
//
// 观测口径改版（与内核同步，不是回归）：上限层现在只产出一个**per-媒体的常数**
//   上限 = min(Windows 声明的显示器刷新率, 该媒体 open 时算出的帧率上界 r_frame_rate)，
//   不再逐帧/逐段重算。所以 "内容率换了档" 这件事在 frameRateBoundHz 上**读不出来了**
//   —— 那里今后必须恒定。换档只能从**实测呈现速率**看：轮询 snapshot 的
//   presentedVideoFrames 对墙上时间做滑窗差分（0.5s 窗 / 0.1s 步），再做平台段检测。
//   上限自身则由 "窗口内恒定 + 不高于面板那一半 + 不低于最快段率" 三条判据钉住。
//
// 为什么另开一支、不塞进 present_pacing_probe：那支判的是停留/丢弃分账与提交节奏，前提
//   是窗口真的在被合成 —— 它实测会因为遮挡或前台归属而整轮只推进 0.76s 媒体时间、
//   只呈现 19 帧，那种红不是内核的账。本支**不建输出窗口**，因此没有合成、没有焦点、
//   没有显示侧计时前提，读数只由"内容周期"这一条决定，同一个文件反复跑应当同判。
//
// 无窗口带来的三个约束，都是这台机器上量出来的事实（docs/03 §A9 ⑥）：
//   · 没有交换链 ⇒ 实测面板周期永远是 0，显示侧那组判据在这支里没有操作数，本支不碰。
//   · 没有输出窗口 ⇒ 上限的面板那一半取 DisplayCadenceHz() 的构造默认 **60.0f**，
//     于是上限读数 = min(60, 媒体帧率上界)。所以本支用的夹具**两段速率都必须低于 60**
//     （12b = 20 与 40），否则晚段会读成 60，分不清是内容率赢还是那个默认值赢
//     （这条写在下面的前置判据里，不满足就记未判）。
//   · 这支看不到"上限有没有真的钳住呈现"：无窗口时内核按内容时钟喂帧，实测速率本来就
//     等于内容率。所以本支的段判据证明的是"内容段被逐段跟随"，"上限钳住了快段"这件事
//     归带窗口的 present_pacing_probe 判；本支只判上限恒定性与它的上下界。
//
// 用法：content_period_probe '<绝对媒体路径>' [秒数]
//   CPCP_EARLY_HZ=N   前段速率（必填）
//   CPCP_LATE_HZ=N    后段速率（必填）
//   CPCP_AVG_HZ=N     整片平均速率（选填：给了才判"实测速率不许停在平均上"）
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
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

std::atomic<bool> g_openCompleted{ false };
// Seek 是排入命令队列的异步调用，FFF3FP_Seek 的返回值只说明"命令收下了"，定位真的失败
// 只会出现在事件里（实测裸流：result=-5，消息自称 "playback remained active"，可紧接着
// state=Ended）。所以这里把那条失败记下来，读数时如实打印，而不是让它的后果冒充内核读数。
std::atomic<bool> g_seekFailed{ false };

void __cdecl OnEvent(void*, FFF3FPEvent event, const char* detail) {
    if (event == FFF3FPEvent::OpenCompleted) g_openCompleted.store(true);
    if (detail != nullptr && std::strstr(detail, "could not seek") != nullptr)
        g_seekFailed.store(true);
    if (std::getenv("CPCP_EVENTS"))
        std::fprintf(stderr, "EVENT %d %s\n", static_cast<int>(event), detail ? detail ? detail : "(空)" : "(null)");
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

struct RateSample { double tS; double hz; };
struct Plateau { double startS; double durationS; double meanHz; std::size_t count; };
struct FrameStamp { double tS; double frames; };

// 连续读数落在同一条位（相对上次均值 <5%，且不小于差分窗的量化步长 tolHz）算一段。
// 判"换档"必须按段，不能按整片均值：均值会把 20/40 两段糊成 30，而 30 恰好就是
// "走容器平均数"那条路应当给出的数。
std::vector<Plateau> FindPlateaus(const std::vector<RateSample>& samples, double tolHz) {
    std::vector<Plateau> out;
    for (const auto& s : samples) {
        if (!out.empty()) {
            auto& last = out.back();
            if (std::fabs(s.hz - last.meanHz) <= std::fmax(last.meanHz * 0.05, tolHz)) {
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

const Plateau* FindPlateau(const std::vector<Plateau>& plateaus, double wantHz, double minS,
                           double tolHz) {
    if (wantHz <= 0.0) return nullptr;
    const auto tol = std::fmax(wantHz * 0.05, tolHz * 1.5);
    for (const auto& p : plateaus)
        if (p.durationS >= minS && std::fabs(p.meanHz - wantHz) <= tol) return &p;
    return nullptr;
}

// ---- 实测呈现速率：presentedVideoFrames 对墙上时间的滑窗差分 ----
// 上限现在是 open 时算出的 per-媒体常数，段的变化只能从翻帧速率上看。
// 窗宽 0.5s、步长 0.1s：窗宽决定量化步长（1/W = 2Hz），取 0.1s 窗的话步长就是 10Hz，
// 比 20/40 段的判据容差还粗，平台段永远聚不出来。步长 0.1s 保证每 ~100ms 出一个读数。
std::vector<RateSample> WindowedRates(const std::vector<FrameStamp>& raw,
                                       double windowS, double strideS) {
    std::vector<RateSample> out;
    std::size_t j = 0;
    for (std::size_t i = 0; i < raw.size(); ++i) {
        const auto t = raw[i].tS;
        if (t < windowS) continue;
        if (!out.empty() && t - out.back().tS < strideS) continue;
        while (j + 1 < raw.size() && raw[j + 1].tS <= t - windowS) ++j;
        const auto dt = t - raw[j].tS;
        if (dt < windowS * 0.5) continue;
        out.push_back(RateSample{t, (raw[i].frames - raw[j].frames) / dt});
    }
    return out;
}

// 中位数：上限现在应当只有一个值，取中位数而不是取峰值——"整窗取 max"会把一次抖动
// 当成上限本身。
double Median(std::vector<double> v) {
    if (v.empty()) return 0.0;
    std::nth_element(v.begin(), v.begin() + v.size() / 2, v.end());
    return v[v.size() / 2];
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

    // CPCP_NO_SEEK=1 跳过那次 Seek(0)。这条开关是被实测逼出来的：裸流上 Seek(0) 会失败
    // （事件里 result=-5，消息自称 "playback remained active"），可紧接着会话就
    // state=Ended／解码帧=0——上一轮我因此把"裸流不出帧"记成播放器缺陷，其实是**仪器自己
    // 按下的那颗钮**。同一件裸流不 seek 时实测 state=Playing、解码 88 帧。
    if (std::getenv("CPCP_NO_SEEK") == nullptr) FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);
    Pump(400);   // 让首帧落地

    FFF3FPSnapshot baseline{}; baseline.size = sizeof(baseline); baseline.version = 20;
    FFF3FP_GetSnapshot(player, &baseline);
    FFF3FPPresentPacingStatus basePacing{};
    basePacing.size = sizeof(basePacing); basePacing.version = 20;
    FFF3FP_GetPresentPacingStatus(player, &basePacing);

    const auto start = Now100ns();
    std::vector<RateSample> boundSeries;    // 上限读数序列：判"窗口内恒定"用
    std::vector<FrameStamp> presentTimeline;   // 呈现帧计数序列：差分成实测呈现速率
    double minBoundHz = 0.0, maxBoundHz = 0.0;
    std::uint32_t zeroBoundSamples = 0;
    std::uint32_t lastBypass = 0;
    std::size_t bypassTwoSamples = 0;
    while (Now100ns() - start < static_cast<std::int64_t>(seconds * 10'000'000)) {
        FFF3FPPresentPacingStatus p{}; p.size = sizeof(p); p.version = 20;
        FFF3FP_GetPresentPacingStatus(player, &p);
        FFF3FPSnapshot s{}; s.size = sizeof(s); s.version = 20;
        FFF3FP_GetSnapshot(player, &s);
        const auto tS = (Now100ns() - start) / 10'000'000.0;
        const auto bound = static_cast<double>(p.frameRateBoundHz);
        lastBypass = p.pacingBypass;
        if (p.pacingBypass == 2u) ++bypassTwoSamples;
        if (bound > 0.0) {
            if (minBoundHz == 0.0) minBoundHz = bound;
            minBoundHz = std::min(minBoundHz, bound);
            maxBoundHz = std::max(maxBoundHz, bound);
            boundSeries.push_back(RateSample{tS, bound});
        } else {
            ++zeroBoundSamples;   // 上限读成 0：这一层没产出（无窗口/取不到声明刷新率 ⇒ bypass=2）
        }
        presentTimeline.push_back(FrameStamp{tS, static_cast<double>(s.presentedVideoFrames)});
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

    // ---- 两条操作数 ----
    // 第一件：上限的**方向**。它现在是 per-媒体 初值（来自容器字段）＋ 只许上抬，
    // 所以判"单调不减"而不是"恒定"：`r_frame_rate` 在 MPEG-TS 上会偏低（实测
    // 12b_vfr_20then40.ts 报 20/1 而后半段真是 40fps），钉死初值就会掐掉快段；
    // 反过来任何一次**下降**都是上限把自己掐小，正是这条修复要防的形状。
    std::vector<double> boundValues;
    boundValues.reserve(boundSeries.size());
    for (const auto& b : boundSeries) boundValues.push_back(b.hz);
    const double boundHz = Median(boundValues);
    const double boundSpread = (boundHz > 0.0) ? (maxBoundHz - minBoundHz) : 0.0;
    const double boundTol = std::fmax(0.5, boundHz * 0.01);
    std::size_t boundDownSteps = 0, boundUpSteps = 0;
    for (std::size_t i = 1; i < boundSeries.size(); ++i) {
        if (boundSeries[i].hz + boundTol < boundSeries[i - 1].hz) ++boundDownSteps;
        else if (boundSeries[i].hz > boundSeries[i - 1].hz + boundTol) ++boundUpSteps;
    }
    const bool boundMonotone = boundHz > 0.0 && zeroBoundSamples == 0 &&
        boundSeries.size() >= 40 && boundDownSteps == 0;
    // 第二件：实测呈现速率。段的变化今后只从这一条上看。
    const double rateWindowS = 0.5;
    const double rateStrideS = 0.1;
    const double rateQuantHz = 1.0 / rateWindowS;
    const auto rateSeries = WindowedRates(presentTimeline, rateWindowS, rateStrideS);
    const auto plateaus = FindPlateaus(rateSeries, rateQuantHz);
    double peakRateHz = 0.0;
    for (const auto& r : rateSeries) peakRateHz = std::max(peakRateHz, r.hz);

    std::printf("\n--- 读数 ---\n");
    std::printf("  媒体时钟推进=%.2fs（观察窗 %.2fs）｜呈现帧=%.0f｜传统口径丢弃=%.0f\n",
        mediaS, seconds, presented, drops);
    std::printf("  上限读数：中位=%.2f Hz｜窗口内范围=[%.2f, %.2f] 跨度=%.2f Hz｜"
        "上抬台阶=%zu 次 下降=%zu 次（阈值 %.2f，判的是单调不减）｜"
        "塌回 0 的采样=%u 次｜末次 bypass=%u（2＝上限不可用：无输出窗口或取不到声明刷新率）\n",
        boundHz, minBoundHz, maxBoundHz, boundSpread, boundUpSteps, boundDownSteps, boundTol,
        zeroBoundSamples, lastBypass);
    // 会话状态与时长：裸流那类"一帧都不出"的形状，只有把 state／duration 打出来才分得清
    // 是"停在预滚"还是"根本没解码"。缺这两个数时，NOT-JUDGED 就是一句没有信息量的话。
    std::printf("  末次快照 state=%d｜position=%.2fs｜duration=%.2fs｜解码帧=%llu｜队列=%u"
        "｜选中流 v=%d a=%d\n",
        static_cast<int>(after.state),
        static_cast<double>(after.position100ns) / 10'000'000.0,
        static_cast<double>(after.duration100ns) / 10'000'000.0,
        static_cast<unsigned long long>(after.decodedVideoFrames),
        static_cast<unsigned>(after.queuedVideoFrames),
        static_cast<int>(after.selectedVideoStream), static_cast<int>(after.selectedAudioStream));
    // 定位失败与"零帧"同时出现时，成因是那句 Seek(0)，不是内容周期。不写这一行，读者就会
    // 把仪器造成的 0 帧当成播放器缺陷——本轮就是这样误判过一次并已当面更正。
    if (g_seekFailed.load())
        std::printf("  ⚠ 内核在这扇窗里报过定位失败（事件 result=-5）。若上面是零帧，成因是\n"
            "    探针自己的 Seek(0)，不是内容周期；用 CPCP_NO_SEEK=1 复跑同一件就能分开。\n");
    std::printf("  实测呈现速率（%.2fs 窗/%.2fs 步差分，样本 %zu 个，峰值 %.1f Hz）｜平台段 %zu 条：",
        rateWindowS, rateStrideS, rateSeries.size(), peakRateHz, plateaus.size());
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

    // 前置一：无窗口 ⇒ 上限的面板那一半取 DisplayCadenceHz() 的构造默认 60.0f，
    //   于是上限读数 = min(60, 媒体帧率上界)。段速率必须严格低于 60，否则"读到一条 60 的
    //   速率平台段"既可能是内容率也可能是那个默认值把快段钳住了，判据没有操作数。
    //   （60.0 这个数是内核构造默认值的独立事实，不是从被检读数反推的——见本支文件头。）
    const double windowlessPanelFallbackHz = 60.0;
    Judge(true, lateHz < windowlessPanelFallbackHz,
        "前置：晚段速率低于无窗口默认面板值 60（否则 min() 分不清哪半赢）");
    // 前置二：会话没被饿住。媒体时钟不推进就等于没跑到后段，"晚段没换档"不是内核的账。
    Judge(mediaS > 0.0, mediaS >= seconds * 0.6, "前置：媒体时钟推进 ≥ 观察窗 60%");
    // 零帧与定位失败同时出现 ⇒ 是探针的 Seek(0) 把会话送进 Ended，不是内核的内容周期。
    // 这条必须排在下面那个提前 return 之前，否则零帧那趟根本走不到判据、只留下一句"未判"。
    Judge(true, !(g_seekFailed.load() && presented == 0.0),
        "前置：零帧不是被探针自己的 Seek(0) 弄出来的（定位失败＋零帧 ⇒ 本臂不适用）");
    if (mediaS < seconds * 0.6) {
        std::printf("\nCPCP RESULT: NOT-JUDGED（会话只推进 %.2fs，段判据没有操作数）\n", mediaS);
        FFF3FP_Destroy(player); return 3;
    }
    // 帧率模式判据：这是"媒体信息页那句 恒定/可变 在可变帧率上是不是假话"的直接口。
    // 期望值由调用方按夹具声明（CPCP_EXPECT_MODE）——探针不许自己猜，否则是自己给自己出题。
    // 这一组用的是散布测量，与上限口径无关，改版后原样保留。
    if (const auto* want = std::getenv("CPCP_EXPECT_MODE")) {
        const std::string expected = want;
        const std::string label = "⑥ media info 的 frameRateMode == \"" + expected +
            "\"（播够 12 帧之后）";
        Judge(presented > 20 && !expected.empty(), lastMode == expected, label.c_str());
    } else {
        std::printf("  [未判] ⑥ frameRateMode：没给 CPCP_EXPECT_MODE，不猜期望值\n");
        ++undecided;
    }

    // ---- 上限自身的三条（新语义：per-媒体常数）----
    // 上限是 open 时算出来的，所以本支现在要求它**恒定**；旧版这里要求它逐段变，
    // 那是有意的语义翻转，不是回归。若上限在无窗口会话里干脆不产出（bypass=2 的
    // 新语义就是"上限不可用"），下面三条记未判而不是绿，换档判据照常按实测速率判。
    if (boundHz <= 0.0) {
        std::printf("  上限全程读 0（末次 bypass=%u，其中 bypass=2 的采样 %zu 次；2＝上限不可用：无输出窗口或取不到声明刷新率）"
            "⇒ 本支的三条上限判据记未判；换档判据不受影响，它们只看实测呈现速率\n",
            lastBypass, bypassTwoSamples);
    }
    Judge(boundSeries.size() >= 40 && boundHz > 0.0, boundMonotone,
        "Ⓑ 上限在观察窗内单调不减（per-媒体 初值＋看到更快一段就上抬，下降步数必须为 0；中途不塌回 0）");
    // 注意这条在**本支现有的五臂上没有区分度**：夹具两段都 <60，min() 的赢方是媒体那一半，
    // 所以只要上限算得出来就必然 ≤60。它的作用是让"面板那一半整个没参与"这种实现在
    // 快于 60 的无窗口件上现形（那种件 bound 会读成媒体上界而不是 60）。要让它有牙，
    // 本支需要一条 >60 的夹具件（例如 120fps 无窗口臂，期望 bound 恒读 60）。
    // 上下界的操作数是**窗口里出现过的最大值**（=该媒体最终稳定下来的上限），不是中位：
    // 上限允许在观察窗内抬一次（12b.ts 的初值 20 抬到 40），中位会把两个时期混成一个数，
    // 于是"实现正确"也会读成红。抬升方向由上面那条"单调不减"判，越界方向由这两条判。
    Judge(maxBoundHz > 0.0, maxBoundHz <= windowlessPanelFallbackHz * 1.005 + 0.5,
        "Ⓑ 上限不高于无窗口会话的面板回退值 60（取窗口内最大值；现有五臂无区分度，>60 件才有）");
    Judge(maxBoundHz > 0.0,
        maxBoundHz >= std::min(windowlessPanelFallbackHz,
                               static_cast<double>(std::max(earlyHz, lateHz))) * 0.95,
        "Ⓑ 上限不低于最快段速率（取窗口内最大值；TS 的 r_frame_rate 实测会偏低，不抬上去报出去的就是假上限）");

    // CFR 对照模式（两段给同一个率）：这时问的不是"会不会换档"，而是
    // "把内容周期的取数来源从 frame->duration 换成队列里的 PTS 差之后，恒定帧率的
    // 读数动没动"。要求整窗只有一条显著速率平台段、落在该率上、且不丢帧。
    const bool cfr = earlyHz == lateHz;
    std::size_t significantPlateaus = 0;
    for (const auto& p : plateaus) if (p.durationS >= 0.4) ++significantPlateaus;
    if (cfr) {
        // 判的是"有没有另一档"，不是"平台段条数/长度"。0.5s 窗的量化是 2Hz，20Hz 的恒定内容
        // 实测在 19.1~21.7 之间飘，测量噪声会把一条真平台切成两三段（实测 12c.ts 与 12d 裸流：
        // 同一次跑切出 [1.73s@19.6][0.43s@19.3][1.40s@20.2]）——那是仪器的分割，不是内容换档。
        // 所以两条一起判：① 没有一条显著平台段落在别处（真出现 40 就红）；② 离该率超过容差的
        // 采样占比 ≤10%（整窗都在这一档上）。容差 max(5%, 1.5×量化) = 20Hz 那档给到 3Hz。
        std::size_t offRatePlateaus = 0, offSamples = 0;
        const auto cfrTol = std::fmax(earlyHz * 0.05, rateQuantHz * 1.5);
        for (const auto& p : plateaus)
            if (p.durationS >= 0.4 && std::fabs(p.meanHz - earlyHz) > cfrTol) ++offRatePlateaus;
        for (const auto& r : rateSeries)
            if (std::fabs(r.hz - earlyHz) > cfrTol) ++offSamples;
        const double offShare = rateSeries.empty() ? 1.0
            : static_cast<double>(offSamples) / static_cast<double>(rateSeries.size());
        std::printf("  ── CFR 对照模式（两段同率 %d）显著平台段=%zu 条、落在别处的=%zu 条｜"
            "速率样本=%zu 离该率>%d 的=%zu（占比 %.1f%%，容差 %.2f）──\n",
            earlyHz, significantPlateaus, offRatePlateaus, rateSeries.size(), 3, offSamples,
            offShare * 100.0, cfrTol);
        Judge(rateSeries.size() >= 10, offRatePlateaus == 0 && offShare <= 0.1,
            "Ⓒ 实测呈现速率整窗都在这一个率上（没有显著平台段落在别处，且离该率超容差的采样 ≤10%）");
        Judge(presented > 20, drops <= 2,
            "Ⓓ 没因为取数来源变化而丢帧（传统口径丢弃 ≤2）");
        // ⓔ 吞吐这一条是**故意放在无窗口支里**的：§A9 那三条 CFR 臂走的是带窗口的
        // present_pacing_probe，而那支读数要窗口真在被合成——本轮这台盒子上一个游戏客户端
        // 长期占 300%+ CPU，同一文件三次跑出 19/132/156 帧，那种吞吐不能当判词。
        // 无窗口支实测媒体时钟 4.50s 观察窗推进 4.51s、呈现计数复跑一致（12c 两容器都是
        // 91 帧），所以这里的期望值可以直接由"率 × 秒"给出，与机器负载基本无关。
        // 口径不动：这条数的仍是整窗总帧数 ÷ 媒体时钟秒数，不是滑窗速率。
        const auto expected = static_cast<double>(earlyHz) * mediaS;
        Judge(earlyHz > 0 && mediaS > 0, expected > 0 && std::fabs(presented - expected) / expected <= 0.10,
            "ⓔ CFR 呈现帧数 == 该率 × 媒体时钟实际推进秒数（±10%，负载无关的吞吐闸）");
    }
    // 段判据：操作数换成实测呈现速率。段期望速率 = min(60 回退值, 该段率)，
    // 由前置一保证这里取到的是该段率本身。
    const auto wantEarly = std::min(windowlessPanelFallbackHz, static_cast<double>(earlyHz));
    const auto wantLate = std::min(windowlessPanelFallbackHz, static_cast<double>(lateHz));
    const auto* early = rateSeries.size() >= 5
        ? FindPlateau(plateaus, wantEarly, 0.4, rateQuantHz) : nullptr;
    const auto* late = rateSeries.size() >= 5
        ? FindPlateau(plateaus, wantLate, 0.4, rateQuantHz) : nullptr;
    const bool rateOperand = !cfr && rateSeries.size() >= 5;
    Judge(rateOperand, early != nullptr,
        "① 前段实测呈现速率有 ≥0.4s 平台段落在该段内容率（无窗口下面板那一半是回退值 60）");
    Judge(rateOperand, late != nullptr,
        "② 后段实测呈现速率有 ≥0.4s 平台段落在该段内容率（换档只能在速率上看，上限是常数）");
    // 方向：先 20 后 40。只问"有没有 40 的平台段"会被片尾那条退回容器档的尾巴蒙过去
    //   —— 实测 12a 的 .ts 臂就在 EOF 之后多出一条 [起 3.41s 长 0.59s @ 25.0Hz]。
    Judge(rateOperand && early != nullptr && late != nullptr,
        early != nullptr && late != nullptr && early->startS < late->startS,
        "③ 换档方向不能反（前段速率平台段先出现）");
    Judge(rateOperand && avgHz > 0 && avgHz != earlyHz && avgHz != lateHz,
        FindPlateau(plateaus, static_cast<double>(avgHz), 0.4, rateQuantHz) == nullptr,
        "④ 实测呈现速率没停在整片平均上（容器 avg_frame_rate 那条路的签名）");
    // 后果这一栏才是本支真正在意的东西：内容段没被跟上 ⇒ 队列积压 ⇒ 会话按迟到丢帧。
    // 未修复时 12a.ts 实测 4 秒丢 68 帧 ≈ (60−25)×2s，而修复后应当近乎 0。
    Judge(!cfr && presented > 20, drops <= 2,
        "⑤ 没有因为内容段没被跟上而丢帧（传统口径丢弃 ≤2）");

    std::printf("\nCPCP RESULT: %s（判 %d / 红 %d / 未判 %d）\n",
        red > 0 ? "FAIL" : (judged > 0 ? "PASS" : "NOT-JUDGED"), judged, red, undecided);
    FFF3FP_Stop(player);
    FFF3FP_Destroy(player);
    return red > 0 ? 1 : (judged > 0 ? 0 : 3);
}
