// ============================================================================
// loop_stutter_probe.cpp —— 动图循环播放「卡顿」诊断探针（2026-09-26）
//
// 目的：量化动图在**反复循环**时的卡顿。核心量测：
//   ① 每帧到达的**墙钟间隔**（4ms 精度轮询 presentedVideoFrames 的增量）
//   ② **回绕点**（position100ns 从尾部跳回 ~0）前后的帧间隔 vs 稳态帧间隔
//   ③ 整个观察窗口的间隔分布（min/中位/p90/max）与「长停」次数
//
// 判据（卡顿）：
//   · 若回绕点的间隔显著大于稳态 ⇒ 循环接缝处掉帧（真卡顿）
//   · 若整体间隔都远大于源帧时长 ⇒ 帧率整体偏低（不是循环特有）
//   · 帧序完整性（2026-10-23 补）：按位置回绕切圈后，**封口圈**（非尾圈、非首圈）内 fIdx 必须逐 +1，
//     且各圈起点/尾号一致 ⇒ 重复 fIdx、跳号、起点漂移一律红。循环预灌把同一帧灌两次、
//     或接缝处整帧蒸发，只有这条能量出来（丢帧计数会漏报，因为它只看解码侧）。
//     不可辨（不算过）的三种情形：可用圈 ≤1（尾圈被窗截断、只有首圈）、快照 fIdx 为负、
//     稳态中位 < 2×轮询（一次轮询能跨过整帧，"跳号"就可能是采样而不是丢帧）。
//     首圈必须排除：Seek(0) 后的起步追赶本来就会合法跳号（4K 基线实测首圈 fIdx 0,3,6,9），
//     把它算进来判据就永远红 —— 那等于没牙。
//   · 队列深度越界（2026-10-23 补）：**只警告、永不计红** —— 内核的队列上限（PlayerSession.cpp
//     VideoFrameQueueLimit = clamp(128MB / 每帧字节, 2, 8)）**不在快照里**（已逐字段核对
//     FFF.Native/3FP/Api/FFF.Player.Api.h 的 FFF3FPSnapshot：只有 queuedVideoFrames，没有任何
//     上限/预算余量字段），API 头也不是探针能改的 ⇒ 只能按同一套常数自己推算并**原样打印算式**
//     （观测队列峰=N，按预算每帧 B 字节推算上限=M），算出来的数只作观察，不参与退出码。
//
// 用法：loop_stutter_probe <媒体路径> [观察秒数，默认 6]
//       loop_stutter_probe --replay <探针日志路径>      ← 把已录的「逐帧记录」表读回来复判
//   （2026-10-23 补 --replay：判据要能对着**已经录下来的坏跑**自证有牙；回放沿用日志头部记录的
//     轮询/接缝阈/期望 fps，日志里没逐行打印的量（dropped 的每行值）只按端点净增还原 ⇒ 判据
//     只吃读得回的量，读不回的记「未判」而不是猜。）
//   环境变量 LSP_POLL_MS：轮询间隔 ms（默认 4，可设 1 提高分辨率）
//   环境变量 LSP_MAX_SEAM_MS：接缝超额上限 ms（默认 25，沿用 docs/14 §2.2 的判据）
//   环境变量 LSP_EXPECT_FPS：源标称帧率；不设则「节奏」这条记**未判**（不算过）
//   环境变量 LSP_DEC_THREADS=N：把软解线程钉到 N（配置层旋钮；验"低起点会不会掉节奏"用）
//   ⚠⚠ 传 Windows 路径必须用**单引号**或 `cygpath -w`：
//       ./loop_stutter_probe.exe 'C:\PLAN\...\x.gif' 12      ← ✅
//       ./loop_stutter_probe.exe "C:\\PLAN\\...\\x.gif" 12    ← ❌ Git Bash 双引号内
//         `\\` 被折叠成单个反斜杠，后续 `\P`/`\3` 被路径解析吞掉 ⇒ frameCount=0（打开失败）
//   ⚠ 相对路径会被内核拒（"The path must identify an existing regular local file."）
//       ⇒ 一律先 `P=$(cygpath -a -w 相对/路径)` 再传。
// 依赖：同目录 FFF.Native.dll（与 verify_image_mode.exe 相同）
//   ⚠ --replay 也要能加载到 FFF.Native.dll（导入表要求），但它**一个 API 都不调**，
//     只读日志 ⇒ 在没有内核件的目录跑回放时，把含 DLL 的目录放进 PATH 即可。
// 编译：python tools/build_probe.py loop_stutter_probe
//
// 判决（2026-10-10 补牙）：末行打印
//   `SEAM RESULT: PASS|FAIL|NOT-JUDGED（判 N / 红 M / 不可辨 A / 未判 K / 警告 W）`
//   退出码 0=全判且绿 · 1=有红 · 2=用法/建窗/Create/Open/--replay 读不出表 失败 · 3=没有任何一条判据成立（这批**不可判**，不是通过）
//   判据逐条打印自己量到的数：接缝超额 / 长停(>1.5×稳态中位) / 解码侧丢帧 / 渲染侧合并帧 /
//   未提前结束(PlaybackEnded) / 节奏(需 LSP_EXPECT_FPS) / 帧序完整性(重复·跳号·起点漂移) /
//   队列深度越界（`[警告]` 位，不进「判 / 红」计数，因此**永不改变退出码**）
//   ⚠ 三条**时间型**判据带量化带 ±2×轮询：观测量与阈值之差落在带内一律记 `[不可辨]`（不判方向、也不算过）。
//     poll=20ms 时 25ms 的接缝阈差、62ms 级阈差都在带内 ⇒ 那是**测不准**而不是产品坏了；要判小阈差请把
//     LSP_POLL_MS 调到 1（带 ±2ms）。丢帧/合并/PlaybackEnded 是精确计数，不受此限制。
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>
#include <algorithm>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

char g_eventLog[8192] = {};
volatile bool g_openCompleted = false;

// 事件行落进固定缓冲（溢出就丢弃，不覆盖已记的行）。--replay 也走这里，
// 于是「未提前结束」这条判据在活测/回放两条路上读的是同一个缓冲。
void AppendEventLine(const char* line) {
    const auto used = std::strlen(g_eventLog);
    const auto len = std::strlen(line);
    if (used + len < sizeof(g_eventLog))
        std::memcpy(g_eventLog + used, line, len + 1);
}

void __cdecl EventCallback(void* /*context*/, FFF3FPEvent e, const char* json) {
    if (json == nullptr) json = "";
    const char* name = "?";
    switch (e) {
        case FFF3FPEvent::OpenCompleted: name = "OpenCompleted"; g_openCompleted = true; break;
        case FFF3FPEvent::PlaybackEnded: name = "PlaybackEnded"; break;
        case FFF3FPEvent::ColorModeChanged: name = "ColorModeChanged"; break;
        case FFF3FPEvent::Error: name = "Error"; break;
        case FFF3FPEvent::StateChanged: name = "StateChanged"; break;
        default: break;
    }
    char line[512];
    std::snprintf(line, sizeof(line), "    [event] %s %s\n", name, json);
    AppendEventLine(line);
}

LRESULT CALLBACK WindowProcedure(HWND h, UINT m, WPARAM w, LPARAM l) {
    return DefWindowProcW(h, m, w, l);
}

// 轮询间隔（ms）：越小量化误差越低，但 CPU 占用越高。可用环境变量 LSP_POLL_MS 覆盖。
static int g_pollMs = 4;
// 判据参数：接缝超额上限（docs/14 §2.2 定为 25ms）与期望源帧率（0=不判节奏）。
static double g_maxSeamMs = 25.0;
static double g_expectFps = 0.0;
// 流信息：活测从 GetImageInfo/快照取，--replay 从日志头部/观察行取；取不到留 0 ⇒ 对应判据记未判。
static int g_frameCount = 0;
static std::uint32_t g_videoWidth = 0, g_videoHeight = 0;

// 解码队列上限的内核常数（PlayerSession.cpp：MaxQueuedVideoFrames=8、
// MinimumMemoryBoundVideoFrames=2、DecodedVideoQueueBudgetBytes=128MB）。
// 上限本身不在快照里，探针只能按同一套常数推算 ⇒ 见「队列深度越界」判据。
constexpr std::uint64_t kDecodedQueueBudgetBytes = 128ull * 1024ull * 1024ull;
constexpr std::uint64_t kMaxQueuedVideoFrames = 8;
constexpr std::uint64_t kMinimumMemoryBoundVideoFrames = 2;

// 一次呈现 = 表里一行。活测从快照填，--replay 从表身填，判据只认这份结构。
struct Sample { double ms; std::uint64_t presented; std::int64_t posUs; std::uint64_t dropped; std::uint64_t decoded;
                std::uint64_t coalesced; std::uint32_t queued; std::int64_t frameIndex; };

// 回绕规则（逐帧表「回绕?」列与帧序切圈共用同一条）：位置回退超过 200ms 才算换了新的一圈。
constexpr std::int64_t kWrapBackUs = 200'000;

// 取锚点之后的第一个数字。日志与源码同为 UTF-8 ⇒ 锚点可以直接用中文字面量；取不到就返回 false，
// 调用方一律「沿用 env/默认」或「记未判」，不许拿默认值冒充量到的数。
bool NumberAfter(const char* line, const char* anchor, double& out) {
    const char* p = std::strstr(line, anchor);
    if (p == nullptr) return false;
    p += std::strlen(anchor);
    char* end = nullptr;
    const double v = std::strtod(p, &end);
    if (end == p) return false;
    out = v;
    return true;
}

// 取锚点之后的前两个整数（日志里 "A → B（净增 C）" 的箭头是多字节，跳过非数字字符即可）。
bool TwoIntsAfter(const char* line, const char* anchor, std::uint64_t& a, std::uint64_t& b) {
    const char* p = std::strstr(line, anchor);
    if (p == nullptr) return false;
    p += std::strlen(anchor);
    std::uint64_t found[2] = { 0, 0 };
    int n = 0;
    while (*p != '\0' && n < 2) {
        if (*p >= '0' && *p <= '9') {
            char* end = nullptr;
            found[n++] = std::strtoull(p, &end, 10);
            p = end;
        } else { ++p; }
    }
    if (n < 2) return false;
    a = found[0]; b = found[1];
    return true;
}

// ── --replay：把探针自己打印的「逐帧记录」表读回来，让判据能对着**已经录下的坏跑**复判 ──
// 只还原三样：表身（# 墙钟 间隔 位置 fIdx queued coalesced）、日志头部记的量测条件、
// dropped/coalesced 的端点净增与事件行。表里没打印的量（dropped 的逐行值）不还原也不编。
bool LoadReplay(const char* logPath, std::vector<Sample>& samples) {
    std::FILE* file = std::fopen(logPath, "rb");
    if (file == nullptr) {
        std::printf("--replay 打不开日志：%s\n", logPath);
        return false;
    }
    char line[1024];
    std::uint64_t dropFrom = 0, dropTo = 0, decFrom = 0, decTo = 0;
    bool haveDropRange = false, haveDecRange = false, sawPoll = false;
    double v = 0.0;
    while (std::fgets(line, sizeof(line), file) != nullptr) {
        unsigned rowNo = 0, queued = 0;
        double wallS = 0.0, gapMs = 0.0, posS = 0.0;
        long long fIdx = 0;
        unsigned long long coalesced = 0;
        // 表身行：前 7 列是数值且行号必须严格等于已读行数 ⇒ 不是这张表就打不进
        if (std::sscanf(line, " %u %lf %lf %lf %lld %u %llu", &rowNo, &wallS, &gapMs, &posS,
                        &fIdx, &queued, &coalesced) == 7 && rowNo == samples.size()) {
            Sample s{};
            s.ms = wallS * 1000.0;
            s.presented = samples.size() + 1;   // 回放只知"每次呈现 +1"
            s.posUs = static_cast<std::int64_t>(std::llround(posS * 1e6));
            s.dropped = 0;                      // 逐行值日志没打 ⇒ 循环后按端点补
            s.decoded = 0;
            s.coalesced = coalesced;
            s.queued = static_cast<std::uint32_t>(queued);
            s.frameIndex = static_cast<std::int64_t>(fIdx);
            samples.push_back(s);
            continue;
        }
        if (NumberAfter(line, "轮询 ", v)) { g_pollMs = static_cast<int>(v); sawPoll = true; }
        if (NumberAfter(line, "接缝≤", v)) g_maxSeamMs = v;
        if (NumberAfter(line, "期望fps=", v)) g_expectFps = v;   // 「(未设⇒节奏不判)」打不出数字 ⇒ 保持原值
        if (NumberAfter(line, "frameCount=", v)) g_frameCount = static_cast<int>(v);
        if (const char* dim = std::strstr(line, "尺寸 ")) {      // 新构建的观察行才带，旧日志取不到
            unsigned w = 0, h = 0;
            if (std::sscanf(dim + std::strlen("尺寸 "), "%ux%u", &w, &h) == 2) {
                g_videoWidth = static_cast<std::uint32_t>(w);
                g_videoHeight = static_cast<std::uint32_t>(h);
            }
        }
        if (TwoIntsAfter(line, "droppedVideoFrames:", dropFrom, dropTo)) haveDropRange = true;
        if (TwoIntsAfter(line, "decodedVideoFrames:", decFrom, decTo)) haveDecRange = true;
        if (const char* ev = std::strstr(line, "[event]")) {
            char evLine[1024];
            std::snprintf(evLine, sizeof(evLine), "    %s", ev);   // 补回活测的四空格缩进
            AppendEventLine(evLine);
        }
    }
    std::fclose(file);
    if (samples.empty()) {
        std::printf("--replay 日志里没有可解析的逐帧表：%s\n", logPath);
        return false;
    }
    if (haveDropRange) {   // dropped/decoded 日志只打了端点 ⇒ 按端点还原，净增与活测算的是同一个数
        samples.front().dropped = dropFrom;
        samples.back().dropped = dropTo;
    }
    if (haveDecRange) {
        samples.front().decoded = decFrom;
        samples.back().decoded = decTo;
    }
    if (!sawPoll) std::printf("--replay 未从日志头取到轮询，沿用 LSP_POLL_MS/默认值\n");
    return true;
}

// ── 帧序完整性 ────────────────────────────────────────────────────────────
// 按位置回绕切圈后，只判**封口圈**（后面真发生了一次回绕），并且排除：
//   · 尾圈 —— 被观察窗截断，本来就不满一圈；
//   · 首圈 —— Seek(0) 后的起步追赶段会合法跳号（4K 基线实测首圈 fIdx 0,3,6,9），
//             把它算进来判据就永远红，那等于没牙；
//   · 含 fIdx<0（快照当时没有帧号）或不足 2 行的圈。
// 圈内规矩：fIdx 必须逐 +1 ⇒ 重复(步进 0)、跳号(步进 >1)、倒退(步进 <0) 都算违反。
// 圈间规矩：各圈起点必须相同、尾号必须相同（起点偏高=接缝处整帧蒸发，尾号偏低=提前回绕丢尾帧）。
// 参照取各圈极值（min 起点 / max 尾号），可用圈 ≤1  ⇒  不可辨（不算过）。
struct SequenceVerdict {
    bool judged = false;      // false ⇒ 不可辨
    bool failed = false;
    std::string reading;
};

SequenceVerdict CheckFrameSequence(const std::vector<Sample>& samples, double steadyMedianMs) {
    SequenceVerdict out;
    struct Pass { std::size_t begin, end; };   // 闭区间，行号就是逐帧表的 # 列
    std::vector<Pass> sealed;
    {
        std::size_t begin = 0;
        for (std::size_t i = 1; i < samples.size(); ++i) {
            if (samples[i].posUs - samples[i - 1].posUs < -kWrapBackUs) {
                sealed.push_back({ begin, i - 1 });
                begin = i;
            }
        }
    }
    std::vector<Pass> usable;
    std::size_t skippedTiny = 0, skippedNeg = 0;
    for (std::size_t p = 1; p < sealed.size(); ++p) {   // p==0 是首圈 ⇒ 排除
        const Pass pass = sealed[p];
        if (pass.end <= pass.begin) { ++skippedTiny; continue; }
        bool negative = false;
        for (std::size_t i = pass.begin; i <= pass.end; ++i) {
            if (samples[i].frameIndex < 0) { negative = true; break; }
        }
        if (negative) { ++skippedNeg; continue; }
        usable.push_back(pass);
    }
    char buf[512];
    if (usable.size() <= 1) {
        std::snprintf(buf, sizeof(buf),
            "封口圈 %zu（首圈外 %zu，可用 %zu：不足 2 行 %zu、无帧号 %zu）⇒ 不足两圈，不比帧序",
            sealed.size(), sealed.empty() ? std::size_t{0} : sealed.size() - 1,
            usable.size(), skippedTiny, skippedNeg);
        out.reading = buf;
        return out;
    }
    // 采样密度不够时"跳号"是轮询跨过了整帧，不是内核丢了帧 ⇒ 这种跑一律不可辨。
    if (steadyMedianMs > 0.0 && steadyMedianMs < 2.0 * static_cast<double>(g_pollMs)) {
        std::snprintf(buf, sizeof(buf),
            "可用圈 %zu，但稳态中位 %.1fms < 2×轮询 %dms ⇒ 一次轮询可跨过整帧，帧序不可辨",
            usable.size(), steadyMedianMs, g_pollMs);
        out.reading = buf;
        return out;
    }
    std::int64_t refStart = samples[usable[0].begin].frameIndex;
    std::int64_t refEnd = refStart;
    for (const Pass& pass : usable) {
        refStart = std::min(refStart, samples[pass.begin].frameIndex);
        refEnd = std::max(refEnd, samples[pass.end].frameIndex);
    }
    std::uint64_t dup = 0, gap = 0, back = 0, drift = 0, shortTail = 0;
    std::string detail;
    std::uint64_t listed = 0;     // 真进了明细条目的条数（明细有长度帽，必须承认截断）
    const auto note = [&detail, &listed](const char* text) {
        if (detail.size() < 260) {
            if (!detail.empty()) detail += "；";
            detail += text;
            ++listed;
        }
    };
    for (const Pass& pass : usable) {
        for (std::size_t i = pass.begin + 1; i <= pass.end; ++i) {
            const std::int64_t prev = samples[i - 1].frameIndex;
            const std::int64_t now = samples[i].frameIndex;
            const std::int64_t step = now - prev;
            char item[128];
            if (step == 0) {
                ++dup;
                std::snprintf(item, sizeof(item), "fIdx %lld,%lld @ 行 %zu/%zu",
                    static_cast<long long>(prev), static_cast<long long>(now), i - 1, i);
            } else if (step < 0) {
                ++back;
                std::snprintf(item, sizeof(item), "fIdx %lld→%lld @ 行 %zu/%zu 倒退",
                    static_cast<long long>(prev), static_cast<long long>(now), i - 1, i);
            } else if (step > 1) {
                ++gap;
                std::snprintf(item, sizeof(item), "fIdx %lld→%lld @ 行 %zu/%zu 跳号（漏 %lld 帧）",
                    static_cast<long long>(prev), static_cast<long long>(now), i - 1, i,
                    static_cast<long long>(step - 1));
            } else { continue; }
            note(item);
        }
        char item[128];
        if (samples[pass.begin].frameIndex != refStart) {
            ++drift;
            std::snprintf(item, sizeof(item), "起点 fIdx=%lld（参照 %lld）@ 行 %zu",
                static_cast<long long>(samples[pass.begin].frameIndex),
                static_cast<long long>(refStart), pass.begin);
            note(item);
        }
        if (samples[pass.end].frameIndex != refEnd) {
            ++shortTail;
            std::snprintf(item, sizeof(item), "尾号 fIdx=%lld（参照 %lld）@ 行 %zu",
                static_cast<long long>(samples[pass.end].frameIndex),
                static_cast<long long>(refEnd), pass.end);
            note(item);
        }
    }
    const std::uint64_t violations = dup + gap + back + drift + shortTail;
    out.judged = true;
    out.failed = violations != 0;
    std::snprintf(buf, sizeof(buf),
        "可用圈 %zu 起点 %lld 尾号 %lld 每圈应有 %lld 帧 ⇒ 重复 %llu 跳号 %llu 倒退 %llu 起点漂移 %llu 尾号不齐 %llu"
        "（排除：首圈 1、尾圈 %zu、不足 2 行 %zu、无帧号 %zu）",
        usable.size(), static_cast<long long>(refStart), static_cast<long long>(refEnd),
        static_cast<long long>(refEnd - refStart + 1),
        static_cast<unsigned long long>(dup), static_cast<unsigned long long>(gap),
        static_cast<unsigned long long>(back), static_cast<unsigned long long>(drift),
        static_cast<unsigned long long>(shortTail),
        samples.empty() ? std::size_t{0} : std::size_t{1}, skippedTiny, skippedNeg);
    out.reading = buf;
    if (!detail.empty()) {
        out.reading += "；违反项: ";
        out.reading += detail;
        if (listed < violations) {   // 汇总数与点名条数必须对得上，截断了就要承认
            out.reading += "（明细截断，只列了 " + std::to_string(listed) + "/" +
                std::to_string(static_cast<unsigned long long>(violations)) + " 条）";
        }
    }
    return out;
}

void PumpFor(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message{};
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(g_pollMs));
    }
}

FFF3FPSnapshot ReadSnapshot(FFF3FPHandle player) {
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = 20;
    FFF3FP_GetSnapshot(player, &s);
    return s;
}

// ── 活测：建窗 → Create → Open → Seek(0)/Play → 轮询采样 ─────────────────
// player/hwnd 交回调用方，判决打完才销毁 —— 销毁本身可能再打事件，提前销毁会污染
// 「未提前结束」那条读数（这是探针自己的读数，不是内核的）。
// 返回 0=采到样（samples 可能为空，由判据自己记未判），2=对象没起来（用法/建窗/Create/Open）。
int RunLive(const char* path, double seconds, std::vector<Sample>& samples,
            FFF3FPHandle& player, HWND& hwnd) {
    std::printf("=== 动图循环卡顿诊断 ===\n");
    std::printf("file: %s  观察 %.1fs  轮询 %dms  API=%u  判据: 接缝≤%.1fms 期望fps=%s\n",
        path, seconds, g_pollMs, FFF3FP_GetApiVersion(), g_maxSeamMs,
        g_expectFps > 0.0 ? std::to_string(g_expectFps).c_str() : "(未设⇒节奏不判)");

    WNDCLASSW wc{};
    wc.lpfnWndProc = WindowProcedure;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"loopStutterWindow";
    RegisterClassW(&wc);

    hwnd = CreateWindowExW(0, wc.lpszClassName, L"loop-stutter", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
    if (hwnd == nullptr) { std::printf("创建窗口失败\n"); return 2; }   // 2=对象没起来，不是判据红

    FFF3FPConfiguration config{};
    config.size = sizeof(config);
    config.version = FFF3FP_GetApiVersion();   // ⚠ 必须是 API 版本（内核严格相等校验）
    config.outputWindow = hwnd;
    config.decodeMode = FFF3FPDecodeMode::Cpu;
    config.colorMode = FFF3FPColorMode::MapToSdr;
    config.sdrPeakNits = 100.0f;
    config.hdrPeakNits = 0.0f;
    config.sdrPaperWhiteNits = 203.0f;
    config.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    config.forceHdrOutput = 0u;
    config.preferredAdapterIndex = -1;   // ⚠ 必须显式 -1（0 是合法索引）
    config.eventCallback = &EventCallback;
    // LSP_DEC_THREADS=N：把软解线程钉到 N（配置层旋钮，用于验"低起点会不会掉节奏"）。
    if (const char* env = std::getenv("LSP_DEC_THREADS")) {
        const int n = std::atoi(env);
        if (n >= 1 && n <= 64) {
            config.adaptiveDecoderThreads = 1;
            config.minSoftwareDecoderThreads = static_cast<std::uint32_t>(n);
            config.maxSoftwareDecoderThreads = static_cast<std::uint32_t>(n);
            std::printf("钉死软解线程数=%d\n", n);
        }
    }
    config.eventCallbackContext = nullptr;

    const auto createResult = FFF3FP_Create(&config, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        std::printf("Create 失败: %d\n", static_cast<int>(createResult));
        return 2;
    }

    const auto openResult = FFF3FP_Open(player, path);
    std::printf("Open: %d\n", static_cast<int>(openResult));
    if (openResult != FFFResult::Success) {   // 打开失败 ⇒ 一个判据都判不了，退 2 而不是退 0
        std::printf("SEAM RESULT: NOT-JUDGED（Open 失败 %d，路径必须是绝对路径且文件存在）\n",
            static_cast<int>(openResult));
        return 2;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) PumpFor(50);
    std::printf("OpenCompleted: %s\n", g_openCompleted ? "yes" : "no");
    PumpFor(300);   // 让首帧落地

    FFF3FPImageInfo info{};
    info.size = sizeof(info);
    info.version = 20;
    HMODULE mod = GetModuleHandleW(L"FFF.Native.dll");
    if (mod == nullptr) mod = LoadLibraryW(L"FFF.Native.dll");
    using GetInfoFn = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPImageInfo*);
    auto getInfo = mod ? reinterpret_cast<GetInfoFn>(
        reinterpret_cast<void*>(GetProcAddress(mod, "FFF3FP_GetImageInfo"))) : nullptr;
    if (getInfo) {
        getInfo(player, &info);
        g_frameCount = info.frameCount;   // <=0 = 未知（4K APNG 实测就是 -1），帧序判据不依赖它
        std::printf("frameCount=%d flags=0x%X ANIMATED=%d STATIC=%d\n",
            info.frameCount, info.flags,
            (info.flags & FFF3FP_IMAGE_FLAG_ANIMATED) ? 1 : 0,
            (info.flags & FFF3FP_IMAGE_FLAG_STATIC) ? 1 : 0);
    }

    // ── 循环观察：4ms 轮询，记录每次 presentedVideoFrames 变化的时刻 ──
    FFF3FP_Seek(player, 0);
    FFF3FP_Play(player);

    const auto t0 = std::chrono::steady_clock::now();
    const auto deadline = t0 + std::chrono::milliseconds(static_cast<long long>(seconds * 1000));
    std::uint64_t lastPresented = ReadSnapshot(player).presentedVideoFrames;

    while (std::chrono::steady_clock::now() < deadline) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        const auto snap = ReadSnapshot(player);
        if (snap.presentedVideoFrames != lastPresented) {
            const auto ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - t0).count();
            samples.push_back({ ms,
                static_cast<std::uint64_t>(snap.presentedVideoFrames),
                static_cast<std::int64_t>(snap.position100ns / 10),
                static_cast<std::uint64_t>(snap.droppedVideoFrames),
                static_cast<std::uint64_t>(snap.decodedVideoFrames),
                static_cast<std::uint64_t>(snap.coalescedVideoFrames),
                static_cast<std::uint32_t>(snap.queuedVideoFrames),
                static_cast<std::int64_t>(snap.frameIndex) });
            // 解码尺寸留给「队列深度越界」推算每帧字节（快照里就是这两个字段）
            if (snap.videoWidth != 0 && snap.videoHeight != 0) {
                g_videoWidth = snap.videoWidth;
                g_videoHeight = snap.videoHeight;
            }
            lastPresented = snap.presentedVideoFrames;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(g_pollMs));
    }
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    // 两种用法：活测 `loop_stutter_probe <媒体路径> [秒数]`；复判 `loop_stutter_probe --replay <探针日志>`
    if (argc < 2) {
        std::printf("usage: loop_stutter_probe <media path> [seconds] | loop_stutter_probe --replay <probe log>\n");
        return 2;
    }
    const bool replayMode = std::strcmp(argv[1], "--replay") == 0;
    if (replayMode && argc < 3) {
        std::printf("usage: loop_stutter_probe --replay <probe log>\n");
        return 2;
    }
    const char* path = argv[1];
    const double seconds = replayMode ? 6.0 : (argc >= 3 ? std::atof(argv[2]) : 6.0);
    if (const char* env = std::getenv("LSP_POLL_MS")) {
        const int v = std::atoi(env);
        if (v >= 1 && v <= 100) g_pollMs = v;   // 上限 100ms 防呆
    }
    // 判据阈值（可覆盖，默认沿用 docs/14 §2.2 的 25ms 接缝判据）
    if (const char* env = std::getenv("LSP_MAX_SEAM_MS")) {
        const double v = std::atof(env);
        if (v > 0.0 && v <= 10000.0) g_maxSeamMs = v;
    }
    if (const char* env = std::getenv("LSP_EXPECT_FPS")) {
        const double v = std::atof(env);
        if (v > 0.0 && v < 1000.0) g_expectFps = v;   // 0 ⇒ 节奏这一项记「未判」
    }

    std::vector<Sample> samples;
    FFF3FPHandle player = nullptr;
    HWND hwnd = nullptr;
    if (replayMode) {
        // 判据只吃 samples ⇒ 回放与活测走**同一套**判决代码，牙是真牙
        std::printf("=== 动图循环卡顿诊断（--replay：对已录日志复判）===\n");
        if (!LoadReplay(argv[2], samples)) {
            std::printf("SEAM RESULT: NOT-JUDGED（--replay 读不出逐帧表：%s）\n", argv[2]);
            return 2;   // 2=没有可判的对象，不是判据红
        }
        if (g_expectFps > 0.0) {
            std::printf("log: %s  读回 %zu 次呈现  轮询 %dms  frameCount=%d  判据: 接缝≤%.1fms 期望fps=%.6f\n",
                argv[2], samples.size(), g_pollMs, g_frameCount, g_maxSeamMs, g_expectFps);
        } else {
            std::printf("log: %s  读回 %zu 次呈现  轮询 %dms  frameCount=%d  判据: 接缝≤%.1fms 期望fps=(未设⇒节奏不判)\n",
                argv[2], samples.size(), g_pollMs, g_frameCount, g_maxSeamMs);
        }
        std::printf("回放口径：位置/fIdx/queued/coalesced 是逐行原值；dropped 与 decoded 只有日志端点"
            "（末行的 ★DROP 属端点还原）\n");
    } else {
        const int liveResult = RunLive(path, seconds, samples, player, hwnd);
        if (liveResult != 0) {
            if (player != nullptr) FFF3FP_Destroy(player);
            if (hwnd != nullptr) DestroyWindow(hwnd);
            return liveResult;
        }
    }

    std::printf("\n--- 逐帧记录（共 %zu 次呈现）---\n", samples.size());
    std::printf("  #   墙钟(s)   间隔(ms)   位置(s)  fIdx  queued  coalesced  回绕?\n");
    std::vector<double> gaps;
    int wraps = 0, longStops = 0;
    // 判据要用的量（在此声明，供末尾的 SEAM RESULT 判决行取用）
    double seamDelta = 0.0, steadyMed = 0.0, wrapMed = 0.0, meanGap = 0.0;
    bool seamJudged = false;
    std::uint64_t dropNet = 0, coalNet = 0;
    // 「长停」阈值：相对源帧间隔而言，>1.5 倍源间隔才算异常停顿。
    // 固定 200ms 对 5fps 源（间隔 200ms）会每帧误判 ⇒ 必须先算出稳态中位再定阈值。
    double steadyMedian = 100.0;
    {
        std::vector<double> tmp;
        for (std::size_t i = 1; i < samples.size(); ++i) {
            const auto d = samples[i].posUs - samples[i - 1].posUs;
            if (d >= -kWrapBackUs) tmp.push_back(samples[i].ms - samples[i - 1].ms);   // 切圈用同一条规则
        }
        if (!tmp.empty()) { std::sort(tmp.begin(), tmp.end()); steadyMedian = tmp[tmp.size() / 2]; }
    }
    const double longStopThreshold = std::max(50.0, steadyMedian * 1.5);
    for (std::size_t i = 0; i < samples.size(); ++i) {
        double gap = 0; bool wrap = false;
        if (i > 0) {
            gap = samples[i].ms - samples[i - 1].ms;
            gaps.push_back(gap);
            // 回绕判据：位置比上一帧小了 > 200ms（正常帧间隔不可能这么大）——与帧序切圈同一条规则
            const auto deltaUs = samples[i].posUs - samples[i - 1].posUs;
            if (deltaUs < -kWrapBackUs) { wrap = true; ++wraps; }
        }
        const bool longStop = gap > longStopThreshold;   // >1.5× 稳态中位 ⇒ 异常停顿
        if (longStop) ++longStops;
        const bool droppedNow = i > 0 && samples[i].dropped > samples[i - 1].dropped;
        const bool coalescedNow = i > 0 && samples[i].coalesced > samples[i - 1].coalesced;
        // ⚠ 不要把 decoded 与 presented 相减：presented 由 GetSnapshot 实时从 renderer 取，
        //    而 decoded 是 marshal 到 snapshot_ 的旧值 ⇒ 新鲜度不同，相减会下溢。
        std::printf("  %3zu  %8.3f  %9.2f  %8.3f  %5lld  %6u  %9llu  %s%s%s\n",
            i, samples[i].ms / 1000.0, gap, samples[i].posUs / 1e6,
            static_cast<long long>(samples[i].frameIndex),
            samples[i].queued,
            static_cast<unsigned long long>(samples[i].coalesced),
            droppedNow ? "★DROP " : "", coalescedNow ? "★COAL " : "", wrap ? "★WRAP" : "");
    }

    // ── 统计 ──
    if (!gaps.empty()) {
        auto sorted = gaps;
        std::sort(sorted.begin(), sorted.end());
        const auto pct = [&](double p) { return sorted[std::min(sorted.size() - 1,
            static_cast<std::size_t>(p * (sorted.size() - 1)))]; };
        double sum = 0; for (double g : gaps) sum += g;
        meanGap = sum / static_cast<double>(gaps.size());
        std::printf("\n--- 帧间隔统计（全部 %zu 个）---\n", gaps.size());
        std::printf("  均值=%.2fms  最小=%.2fms  中位=%.2fms  p90=%.2fms  最大=%.2fms\n",
            sum / gaps.size(), sorted.front(), pct(0.5), pct(0.9), sorted.back());
        std::printf("  等效帧率：均值 %.1f fps / 中位 %.1f fps\n",
            1000.0 / (sum / gaps.size()), 1000.0 / pct(0.5));

        // ★ 分组：把「回绕帧」（位置回退）与「稳态帧」分开 —— 循环接缝是否掉帧只有这里能看出来
        std::vector<double> wrapGaps, steadyGaps;
        for (std::size_t i = 1; i < samples.size(); ++i) {
            const double g = samples[i].ms - samples[i - 1].ms;
            const auto d = samples[i].posUs - samples[i - 1].posUs;
            if (d < -kWrapBackUs) wrapGaps.push_back(g); else steadyGaps.push_back(g);
        }
        const auto stat = [](std::vector<double>& v) {
            if (v.empty()) return std::string("(无)");
            auto s = v; std::sort(s.begin(), s.end());
            double t = 0; for (double x : s) t += x;
            char buf[160];
            std::snprintf(buf, sizeof(buf), "n=%zu 均值=%.2fms 中位=%.2fms 最大=%.2fms",
                v.size(), t / v.size(), s[s.size() / 2], s.back());
            return std::string(buf);
        };
        std::printf("\n--- ★ 分组统计（循环接缝是否掉帧的关键判据）---\n");
        std::printf("  稳态帧(位置递增): %s\n", stat(steadyGaps).c_str());
        std::printf("  回绕帧(位置回退): %s\n", stat(wrapGaps).c_str());
        if (!wrapGaps.empty() && !steadyGaps.empty()) {
            auto ws = wrapGaps; std::sort(ws.begin(), ws.end());
            auto ss = steadyGaps; std::sort(ss.begin(), ss.end());
            wrapMed = ws[ws.size() / 2]; steadyMed = ss[ss.size() / 2];
            seamDelta = wrapMed - steadyMed;
            seamJudged = true;
            std::printf("  ⇒ 回绕帧比稳态帧 %+.2fms（%.0f%%）",
                seamDelta, (wrapMed / steadyMed - 1.0) * 100.0);
            std::printf("%s\n", seamDelta > g_maxSeamMs ? "  ⚠ 接缝处明显掉帧" : "  （接缝处未明显掉帧）");
        }
    }
    std::printf("  回绕次数=%d   长停(>%.0fms)次数=%d\n", wraps, longStopThreshold, longStops);
    // 队列深度观察（「队列深度越界」那条判据的数就来自这里）。内核的上上限是
    // PlayerSession.cpp VideoFrameQueueLimit = clamp(128MB / 每帧字节, 2, 8)，**这个值不在快照里**
    // （FFF3FPSnapshot 只有 queuedVideoFrames，没有 QueueLimit/预算余量字段，已逐字段核对），
    // 所以只能按解码尺寸反推每帧字节（RGBA 近似，内核实算是 av_image_get_buffer_size）⇒ 只作观察。
    std::uint32_t queuePeak = 0;
    for (const Sample& s : samples) { queuePeak = std::max(queuePeak, s.queued); }
    const std::uint64_t bytesPerFrame = (g_videoWidth != 0u && g_videoHeight != 0u)
        ? static_cast<std::uint64_t>(g_videoWidth) * static_cast<std::uint64_t>(g_videoHeight) * 4ull
        : 0;
    const std::uint64_t queueLimit = bytesPerFrame != 0
        ? std::min<std::uint64_t>(kMaxQueuedVideoFrames, std::max<std::uint64_t>(
            kMinimumMemoryBoundVideoFrames, kDecodedQueueBudgetBytes / bytesPerFrame))
        : 0;
    if (bytesPerFrame != 0) {
        std::printf("  观测队列峰=%u 尺寸 %ux%u 每帧≈%llu B(RGBA) 按预算 %llu B 推算上限=%llu（内核硬顶 %llu）\n",
            queuePeak, g_videoWidth, g_videoHeight,
            static_cast<unsigned long long>(bytesPerFrame),
            static_cast<unsigned long long>(kDecodedQueueBudgetBytes),
            static_cast<unsigned long long>(queueLimit),
            static_cast<unsigned long long>(kMaxQueuedVideoFrames));
    } else {
        std::printf("  观测队列峰=%u 尺寸未知 ⇒ 不推算上限（快照未暴露队列上限，日志里也没有尺寸）\n", queuePeak);
    }
    if (!samples.empty()) {
        dropNet = samples.back().dropped - samples.front().dropped;
        coalNet = samples.back().coalesced - samples.front().coalesced;
        std::printf("  droppedVideoFrames: %llu → %llu（净增 %llu）\n",
            static_cast<unsigned long long>(samples.front().dropped),
            static_cast<unsigned long long>(samples.back().dropped),
            static_cast<unsigned long long>(samples.back().dropped - samples.front().dropped));
        std::printf("  decodedVideoFrames: %llu → %llu（净增 %llu）\n",
            static_cast<unsigned long long>(samples.front().decoded),
            static_cast<unsigned long long>(samples.back().decoded),
            static_cast<unsigned long long>(samples.back().decoded - samples.front().decoded));
        std::printf("  coalescedVideoFrames: %llu → %llu（净增 %llu）★渲染侧跳帧\n",
            static_cast<unsigned long long>(samples.front().coalesced),
            static_cast<unsigned long long>(samples.back().coalesced),
            static_cast<unsigned long long>(samples.back().coalesced - samples.front().coalesced));
    }
    std::printf("  最终 PlaybackEnded=%s\n",
        std::strstr(g_eventLog, "PlaybackEnded") ? "yes (BAD)" : "no (good)");
    if (std::strlen(g_eventLog) > 0) std::printf("--- events ---\n%s", g_eventLog);

    // ── 判决（2026-10-10 补牙，2026-10-23 加帧序完整性/队列越界）────────────
    // 规矩：① 每条判据都把自己量到的数打进判决行，缺席不许算过；
    //       ② 判不出来的记「未判」而不是 PASS（枚举为空必须拦）；
    //       ③ 退出码可区分：0=全判且绿，1=有红，3=没有任何一条判据成立（这批不可判）。
    //       ④ `warn` 行（队列深度越界）只把数打出来，既不进「判」也不进「红」计数 ⇒ 永不改变退出码：
    //          它报的是**推算值**（内核上限不在快照里，每帧字节还是 RGBA 近似），拿推算值判红不合格。
    struct Criterion { const char* name; bool judged; bool failed; std::string reading;
                       bool abstain = false; bool warn = false; };
    std::vector<Criterion> criteria;
    const bool measurable = samples.size() >= 3;   // 少于 3 次呈现 ⇒ 间隔/长停/回绕都不成立
    // 量化带：一次间隔最多被轮询拉宽约 2 个轮询周期。判决只允许在**带外**下结论；
    // 落在带内一律记「不可辨」而不是猜一个方向 —— 这不是放宽阈值，是拒绝在测不准的地方判词。
    const double quant = 2.0 * static_cast<double>(g_pollMs);
    enum class Call { Green, Red, Abstain };
    const auto decide = [quant](double observed, double limit, bool biggerIsWorse) {
        const double d = biggerIsWorse ? observed - limit : limit - observed;   // >0 ⇒ 朝违反方向越界
        if (d > quant) return Call::Red;
        if (-d > quant) return Call::Green;
        return Call::Abstain;
    };

    if (seamJudged) {
        const Call c = decide(seamDelta, g_maxSeamMs, true);
        Criterion cr{ "接缝超额", c != Call::Abstain, c == Call::Red,
            std::to_string(seamDelta) + "ms vs 上限 " + std::to_string(g_maxSeamMs) +
            "ms（量化带 ±" + std::to_string(quant) + "ms）" };
        cr.abstain = (c == Call::Abstain);
        criteria.push_back(std::move(cr));
    } else {
        criteria.push_back({ "接缝超额", false, false, "无回绕帧（demuxer 自回绕或未循环）" });
    }
    if (measurable && !gaps.empty()) {
        double worst = 0.0;
        for (double g : gaps) { if (g > worst) worst = g; }
        const Call c = decide(worst, longStopThreshold, true);
        Criterion cr{ "长停", c != Call::Abstain, c == Call::Red,
            "长停=" + std::to_string(longStops) + " 阈值=" + std::to_string(longStopThreshold) +
            "ms 最大间隔=" + std::to_string(worst) + "ms（量化带 ±" + std::to_string(quant) + "ms）" };
        cr.abstain = (c == Call::Abstain);
        criteria.push_back(std::move(cr));
    } else {
        criteria.push_back({ "长停", false, false, "无可测间隔" });
    }
    criteria.push_back({ "解码侧丢帧", measurable, dropNet != 0,
        "净增 " + std::to_string(static_cast<unsigned long long>(dropNet)) });
    criteria.push_back({ "渲染侧合并帧", measurable, coalNet != 0,
        "净增 " + std::to_string(static_cast<unsigned long long>(coalNet)) });
    criteria.push_back({ "未提前结束", measurable,
        std::strstr(g_eventLog, "PlaybackEnded") != nullptr,
        std::string("presents=") + std::to_string(samples.size()) +
        (std::strstr(g_eventLog, "PlaybackEnded") ? " 但收到 PlaybackEnded" : " 无 PlaybackEnded") });
    // 节奏用**整窗均值**而不是稳态中位：回绕后的追赶帧（4K 实测 41/83ms）会把中位拉到源周期以下，
    // 中位判节奏会把"补帧"误读成"跑得比源快"（6K 那轮的中位 63ms vs 源 100ms 就是这么来的）。
    if (g_expectFps > 0.0 && measurable && meanGap > 0.0) {
        const double expectMs = 1000.0 / g_expectFps;
        const double allowed = expectMs * 0.1;
        const Call c = decide(std::abs(meanGap - expectMs), allowed, true);
        Criterion cr{ "节奏", c != Call::Abstain, c == Call::Red,
            "整窗均值 " + std::to_string(meanGap) + "ms vs 期望 " + std::to_string(expectMs) +
            "ms（容差 " + std::to_string(allowed) + "ms，量化带 ±" + std::to_string(quant) + "ms）" };
        cr.abstain = (c == Call::Abstain);
        criteria.push_back(std::move(cr));
    } else {
        criteria.push_back({ "节奏", false, false, g_expectFps <= 0.0 ? "未设 LSP_EXPECT_FPS" : "无可测间隔" });
    }
    // 帧序完整性（2026-10-23）：切圈 + 圈内 fIdx 逐 +1 + 各圈起点/尾号一致，规则与注释在
    // CheckFrameSequence（文件头部）。可用圈 ≤1 ⇒ 记「不可辨」而不是绿。
    {
        const SequenceVerdict seq = CheckFrameSequence(samples, steadyMedian);
        Criterion cr{ "帧序完整性", seq.judged, seq.failed, seq.reading };
        cr.abstain = !seq.judged;
        criteria.push_back(std::move(cr));
    }
    // 队列深度越界（2026-10-23）：**仅警告**。上限是按内核常数推算的（内核没把上限写进快照，
    // API 头也不是探针能改的），所以这条不参与退出码 —— 但观测峰一定打出来。
    {
        char buf[320];
        if (queueLimit != 0) {
            std::snprintf(buf, sizeof(buf),
                "观测队列峰=%u，按预算每帧 %llu B 推算上限=%llu（内核硬顶 %llu，尺寸 %ux%u）⇒ %s",
                queuePeak, static_cast<unsigned long long>(bytesPerFrame),
                static_cast<unsigned long long>(queueLimit),
                static_cast<unsigned long long>(kMaxQueuedVideoFrames), g_videoWidth, g_videoHeight,
                static_cast<std::uint64_t>(queuePeak) > queueLimit
                    ? "超出上限（每帧字节按 RGBA 近似，仍只警告不计红）" : "未超上限");
        } else {
            std::snprintf(buf, sizeof(buf),
                "观测队列峰=%u，无解码尺寸 ⇒ 不推算上限（快照未暴露队列上限字段）", queuePeak);
        }
        Criterion cr{ "队列深度越界（仅警告）", false, false, buf };
        cr.warn = true;
        criteria.push_back(std::move(cr));
    }

    int judged = 0, red = 0, unknown = 0, abstain = 0, warned = 0;
    std::string reasons;
    std::printf("\n--- 判据逐条 ---\n");
    for (const auto& c : criteria) {
        const char* mark = c.warn ? "警告"
            : (c.abstain ? "不可辨" : (!c.judged ? "未判" : (c.failed ? "红" : "绿")));
        std::printf("  [%s] %s：%s\n", mark, c.name, c.reading.c_str());
        if (c.warn) { ++warned; continue; }   // 警告行不进判/红 ⇒ 不改退出码
        if (c.abstain) { ++abstain; continue; }
        if (!c.judged) { ++unknown; if (c.failed) reasons += std::string(c.name) + "(未判却置红) "; }
        else { ++judged; if (c.failed) { ++red; reasons += std::string(c.name) + " "; } }
    }
    const char* verdict = judged == 0 ? "NOT-JUDGED" : (red > 0 ? "FAIL" : "PASS");
    std::printf("SEAM RESULT: %s（判 %d / 红 %d / 不可辨 %d / 未判 %d / 警告 %d）%s\n", verdict, judged, red,
        abstain, unknown, warned, reasons.empty() ? "" : ("红因: " + reasons).c_str());
    std::printf("=== done ===\n");

    if (player != nullptr) FFF3FP_Destroy(player);   // --replay 下两者都是空 ⇒ 不调用
    if (hwnd != nullptr) DestroyWindow(hwnd);
    if (judged == 0) return 3;
    return red > 0 ? 1 : 0;
}
