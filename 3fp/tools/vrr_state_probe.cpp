// ============================================================================
// vrr_state_probe.cpp —— 面板 VRR 状态探针（2026-11-03，v3 2026-11-04）
//
// v3 修订（全量修正轮，依据 2026-11-03 实现审查）：
//   ① VRR_DEVICE 选屏真正落地——v1/v2 头注声称可用但 main() 里 deviceName 是
//      死变量，E 段永远主屏；v3 起目标监视器统一解析（N 段优先、E 段建窗、
//      D3D 适配器选择三处共用），不可用时明确告警并退回主屏。
//   ② D3D 设备显式建在窗口屏所在适配器上——混合 GPU 下默认适配器可能与窗口屏
//      不同，swap chain 绕合成路径概率增大、帧统计失真（§4 拓扑教训落实）。
//   ③ 判决矩阵与文档 §2 统一（此前两处口径偏差）：
//        - NVAPI requested 并入 vrr-idle 证据（v1/v2 单独 requested 会落 fixed）；
//        - W1 声明单独即可 vrr-idle——"能力在、行为未确证"语义，声明面本身就是
//          能力证据，无需请求态陪伴；请求态/系统开关只进 NOTE 行增强解释；
//        - inconclusive 收敛为"零面板级证据"（E 失能且 W/N 无声明无读数）；
//        - 消除 v1/v2 不可达的尾部 fixed 分支。
//   ④ W2 解析按键精确匹配（v1/v2 裸 strstr 可被键名子串误匹配），
//      DirectXUserGlobalSettings 原始串整串落盘取证（v1/v2 只打印判断结果）。
//   ⑤ N 段"窗口屏优先"提前到遍历期真实生效（v1/v2 的覆盖条件在遍历时恒假：
//      windowDevice 要到 E 段建窗后才有值，靠 E 段二次查询补救）。
//   ⑥ 轮内稳定检验区分"漂移"与"无数据"（v1/v2 下 C2/V2 断流会显示成漂移，
//      2026-11-03 轮 4 实测误导）。
//   ⑦ v3.1（同日续）：聚合率改分块聚合取中位 + 新增 ⓪统计自洽前置门——
//      DISPLAY1 内屏轮实测首尾单对被 SyncQPCTime 毛刺污染（C 相中位 164.6 正常、
//      首尾对 ΔQPC≈0.4ms、聚合率 623626Hz 荒谬），跌幅判据被假阳性穿过；
//      分块聚合吸收单点毛刺，⓪ 门拦截"聚合与中位背离 >20%"的不可信统计。
//   ⑧ v3.2（同日再续）：HF-VSDB 解码落地——v1-v3 把 HF-VSDB 当"et12"是错的
//      （HDMI Forum 的扩展块是 0x78/0x79=HF-EEODB/HF-SCDB，et12 另有用途），
//      其唯一形式是 tag3+OUI 0xC45DD8；VRR 范围（HDMI 2.1a 起）就在块内
//      （p[5]&0x3F = min，((p[5]&0xC0)<<2)|p[6] = max，10 位），偏移经 Linux
//      drm_edid.c + 本机注册表遗留 SAC2765（SANC DM72u）样本逐字段核验
//      （HF-VSDB 48-160 == FreeSync VSDB 48-160 == FD 48-160 三重互证）。
//      支持判据同内核 drm_parse_hdmi_gaming_info；v1-v3 的 et12 检查已删。
//   ⑨ v3.3（同日三续）：DisplayID 1.2 扩展（0x70）的 Vendor Specific 解码（块头与
//      CTA 同构，AMD FreeSync 与 CTA 版同构）。DisplayID 2.0（DISPLAY1 内屏即是）
//      不解码：块头 2B/11 位 size、无规范文本核验、离线多候选不自洽——但其 FreeSync
//      字节证据（`1A 00 00 03 01 30 A5`，48-165）在案，待核验后补。
// 要回答的问题：**当前连接的这块面板**，VRR（G-SYNC / FreeSync / 自适应同步）
// 到底可不可用、请求了没有、现在有没有真的在跑。三段证据各自独立、互为对账：
//
//   S 静态段  DXGI 能力位（IDXGIFactory5::CheckFeatureSupport
//             DXGI_FEATURE_PRESENT_ALLOW_TEARING）——只证明驱动/OS 支持这个
//             present 模式，不证明面板在 VRR（探测盲区的源头，审查结论①）。
//   N NVAPI 段 动态 nvapi64.dll（全机唯一导出 nvapi_QueryInterface，函数 ID 取
//             官方 NVIDIA/nvapi 仓 nvapi_interface.h，nvapi_official.h 同源）：
//             NvAPI_Disp_GetVRRInfo(displayId) 直接给出
//               bIsVRRPossible        面板/链路支持 VRR
//               bIsVRRRequested       驱动设置里请求了 VRR（控制面板的开关）
//               bIsVRREnabled         VRR 当前在给定显示上激活
//               bIsDisplayInVRRMode   面板此刻真的工作在 VRR 模式
//             这是 NVIDIA 官方面板 VRR 状态查询（since R525，Win10+）；
//             没装 NVIDIA 驱动时整段跳过、不影响 E 段判决。
//   W 原生段  Windows 自己的两处 VRR 回报（2026-11-03 重设计，覆盖 FreeSync）：
//             W1 EDID 声明面——设置→高级显示那行「可变刷新频率：受支持
//             (48-240)」没有公开 DisplayConfig 查询（DISPLAYCONFIG_DEVICE_INFO
//             _TYPE 到 SDK 28000 也没有 VRR 类型；内核侧数据源是
//             DXGK_MONITORLINKINFO_CAPABILITIES.VariableRefresh，WDDM 2.6+），
//             但可还原到同一公开源头：注册表 EDID（Enum\DISPLAY\…\Device
//             Parameters\EDID）。解析基础块范围描述符(FD，只证扫描范围) +
//             CTA-861 数据块：AMD FreeSync VSDB(OUI 0x00001A，min/max 在 OUI 后
//             第 5/6 字节——本机对照设置页 48-240 逐字节验证)、HDMI VSDB
//             (0x000C03)、HDMI Forum VSDB(tag3+OUI 0xC45DD8，VRR 范围 HDMI 2.1a
//             起在块内，v3.2 已解码——经 drm_edid.c+SAC2765 样本核验；et12 不是
//             HF-VSDB，v1-v3 的 et12 检查已删)、DisplayID 扩展(0x70，本机存在
//             未解码)。厂商无关：
//             G-SYNC Compatible / FreeSync / Adaptive-Sync 的声明都落在这。
//             W2 系统级 VRR 开关——HKCU\Software\Microsoft\DirectX\
//             UserGpuPreferences 的 DirectXUserGlobalSettings 里
//             VRROptimizeEnable（设置→图形→默认图形设置→可变刷新率）。与驱动
//             层 G-SYNC/FreeSync 相互独立：本机实测开关=关 而 NVAPI
//             inVrrMode=1，面板 VRR 照常工作。
//   E 经验段  端到端裁决（厂商无关）：自建 borderless FLIP_DISCARD 链 +
//             ALLOW_TEARING 能力位（同产品 VideoRenderer.cpp:3569-3575），
//             用 IDXGISwapChain::GetFrameStatistics 的
//             ΔSyncRefreshCount / ΔSyncQPCTime 反解**面板当时的真实刷新率**：
//               C 相  Present(1,0) 全速翻 ~1.5s ⇒ 面板最高档基线
//               V 相  Present(1,0) 按 24fps 节奏翻 ~3s ⇒ vblank 钳制路径
//               T 相  Present(0, ALLOW_TEARING) 同节奏 ~3s ⇒ tearing 路径
//             面板是 VRR 且激活：V/T 相刷新率显著跌离 C 相（跟随内容节奏，
//             LFC 下是内容率的最小整倍数如 48Hz）；固定刷新屏：V/T 相与 C 相
//             同高（ tearing 只是撕裂、不改变扫描节律）。
//             语义对照 display_grid_probe.cpp 头注：PresentRefreshCount=显示
//             时刻的 v 空白计数、SyncRefreshCount/SyncQPCTime=调度器 QPC 采样对、
//             DISJOINT=序列重置只重置基线、bitblt 链统计全零。
//             判据链（⓪统计自洽前置门 + 五重守门，2026-11-03 三轮实证后定稿，
//             v3.1 增 ⓪）：
//             ⓪ 统计自洽：基线/慢相的块聚合率与中位率背离 >max(20%,5Hz) 时帧统计
//                不可信，任何跌落都不归因面板——记 ambiguous；
//             ① 原始跌落：慢相聚合率相对基线 >25% 且 >10Hz；
//             ② 调用率守门：慢相聚合率 >1.15×Present() 调用率（合成路径驱动会
//                模拟帧统计，SyncRefreshCount 只跟随 present——DISPLAY5@60Hz 轮
//                实测 19.2Hz ≈ 调用率而 LFC 底应为 48Hz；参照物不能用
//                PresentRefreshCount，它与 SyncRefreshCount 同源同速）。
//                callRate 不可得（整相 Present 失败等）时退回纯跌落判据——
//                该回退放宽了守门，靠轮内稳定+节奏两环兜底；
//             ③ LFC 相关：VRR+LFC 下面板率必为内容率整数倍（61.7/24=2.5 会被
//                拒）；贴基线（>0.9×）不算跌；内容率恰好顶格时本来就无法与
//                固定区分，如实放弃；
//             ④ 轮内稳定：加 C2/V2 重复相，链路轮内切换（合成↔独立翻转/模式
//                漂移，本机同机三轮三个样）会现形；
//             ⑤ 节奏自检：慢相调用率须贴近内容率（±30%）；callRate 不可得时
//                视为过（无参照可判，交轮内稳定与 W/N 段兜底）。
//             任一不过只记 ambiguous，交 W/N 段裁决。
//
// 判决矩阵（VRR-VERDICT 行，v3 与文档 §2 统一口径）：
//   vrr-active   E 段五重守门全过（跌落+调用率+LFC 相关+轮内稳定+节奏，决定性）
//   vrr-idle     面板级 VRR 证据在、E 未实测确证：NVAPI 窗口屏读数 possible/
//                enabled/requested 任一，或 W1 EDID 声明了 VRR（FreeSync/HF-VSDB）
//                ——声明本身就是能力证据，无需请求态陪伴（v3 定稿；请求态与
//                系统开关只进 NOTE 行增强解释，不参与判决分支）
//   fixed        E 有数据且无跌落、非 ambiguous，且 NVAPI 窗口屏三位全 0、W1 无声明
//   inconclusive 零面板级证据：E 判别力受限/样本不足/链建不起来，且 W/N 均无
//                声明可依（v3 前此态还吞掉了"有声明但链建不起来"——现在归 vrr-idle）
//
// 用法：vrr_state_probe（环境变量 VRR_SECONDS_CAL=1.5 VRR_SECONDS_SLOW=3.0
//        VRR_FPS=24 VRR_DEVICE=\\.\DISPLAY1 选目标监视器，默认主屏）
// 编译：python 3fp/tools/build_probe.py vrr_state_probe
// 退出码：0=已判（绿红都算判）· 2=基础设施失败 · 3=不可判
// ============================================================================
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi1_5.h>
#include <d3d11.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

namespace {

std::string EnvString(const char* name, const char* fallback) {
    const auto raw = std::getenv(name);
    return (raw != nullptr && *raw != '\0') ? std::string(raw) : std::string(fallback);
}
double EnvDouble(const char* name, double fallback) {
    const auto raw = std::getenv(name);
    if (raw == nullptr) return fallback;
    const auto value = std::atof(raw);
    return value > 0 ? value : fallback;
}

std::int64_t QpcFrequency() {
    static std::int64_t frequency = [] { LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return f.QuadPart; }();
    return frequency;
}
std::int64_t QpcNow() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }

// 重定向场景下 wprintf 不落盘（本轮实测 [S] 段全空），统一窄字符输出。
std::string Narrow(const wchar_t* wide) {
    char buffer[512] = {};
    ::WideCharToMultiByte(CP_UTF8, 0, wide, -1, buffer, sizeof(buffer), nullptr, nullptr);
    return std::string(buffer);
}

// ---- NVAPI 最小面（ID 摘自官方 NVIDIA/nvapi 仓 nvapi_interface.h；结构布局
//      与 nvapi.h 的 NV_GET_VRR_INFO_V1 一致，MAKE_NVAPI_VERSION 同官方定义）----
using NvU32 = unsigned int;
constexpr NvU32 kIdInitialize = 0x0150e828u;              // NvAPI_Initialize
constexpr NvU32 kIdGetDisplayIdByDisplayName = 0xae457190u; // NvAPI_DISP_GetDisplayIdByDisplayName
constexpr NvU32 kIdGetVRRInfo = 0xdf8fda57u;              // NvAPI_Disp_GetVRRInfo

using NvAPI_Status = int;   // NVAPI_OK = 0
constexpr NvAPI_Status kNvapiOk = 0;

#pragma pack(push, 8)
struct NvVrrInfoV1 {
    NvU32 version;          // [in] sizeof | (1 << 16)
    NvU32 bIsVRREnabled : 1;
    NvU32 bIsVRRPossible : 1;
    NvU32 bIsVRRRequested : 1;
    NvU32 bIsVRRIndicatorEnabled : 1;
    NvU32 bIsDisplayInVRRMode : 1;
    NvU32 reserved : 27;
    NvU32 reservedEx[4];
};
#pragma pack(pop)
static_assert(sizeof(NvVrrInfoV1) == 24, "NV_GET_VRR_INFO_V1 layout drift");

struct Nvapi {
    HMODULE dll = nullptr;
    void* (*queryInterface)(NvU32 id) = nullptr;
    NvAPI_Status (*initialize)() = nullptr;
    NvAPI_Status (*getDisplayIdByDisplayName)(const char* name, NvU32* displayId) = nullptr;
    NvAPI_Status (*getVRRInfo)(NvU32 displayId, NvVrrInfoV1* info) = nullptr;
    bool available = false;

    bool Load() {
        dll = ::LoadLibraryW(L"nvapi64.dll");
        if (dll == nullptr) return false;
        queryInterface = reinterpret_cast<void* (*)(NvU32)>(
            reinterpret_cast<void*>(::GetProcAddress(dll, "nvapi_QueryInterface")));
        if (queryInterface == nullptr) return false;
        initialize = reinterpret_cast<NvAPI_Status (*)()>(queryInterface(kIdInitialize));
        getDisplayIdByDisplayName =
            reinterpret_cast<NvAPI_Status (*)(const char*, NvU32*)>(queryInterface(kIdGetDisplayIdByDisplayName));
        getVRRInfo = reinterpret_cast<NvAPI_Status (*)(NvU32, NvVrrInfoV1*)>(queryInterface(kIdGetVRRInfo));
        if (initialize == nullptr || getDisplayIdByDisplayName == nullptr || getVRRInfo == nullptr) return false;
        if (initialize() != kNvapiOk) return false;
        available = true;
        return true;
    }
};

// ---- W 原生段工具：注册表 EDID 声明面 + Windows 系统级 VRR 开关 ----
struct EdidVrrInfo {
    bool edidPresent = false;
    std::size_t bytes = 0;
    int baseMin = 0, baseMax = 0;      // 基础块 FD 范围描述符（只证扫描范围，不作 VRR 声明）
    bool amdFreeSync = false;          // CTA VSDB OUI 0x00001A（AMD FreeSync / FreeSync 2）
    std::uint8_t fsByte3 = 0, fsByte4 = 0;
    int fsMin = 0, fsMax = 0;          // 负载 0 基第 5/6 字节（本机对照设置页 48-240 验证）
    bool hdmiForum = false;            // HF-VSDB（tag3 + OUI 0xC45DD8；v3.2 核验更正：不在 et12）
    int hdmiVrrMin = 0, hdmiVrrMax = 0; // HF-VSDB VRR 范围（HDMI 2.1a 起字段；0=无范围声明）
    bool displayId = false;            // 0x70 扩展
    bool fsFromDisplayId = false;      // FreeSync 声明来自 DisplayID 扩展（v3.3；本机
                                       // DISPLAY1 的声明只落在这里，CTA 块没有）
    std::string fsHex, hfHex;          // 两种 VSDB 原文 hex（取证；v3.2 起分开不再互覆）
    // HDMI VRR 支持判据同 Linux drm_parse_hdmi_gaming_info：min 有效且 ≤48，
    // max 未声明（0）或 ≥100——2.1 原版块无 VRR 字段时 min=0 自然判否。
    bool hdmiVrrDeclared() const {
        return hdmiVrrMin > 0 && hdmiVrrMin <= 48 && (hdmiVrrMax == 0 || hdmiVrrMax >= 100);
    }
    bool Declared() const { return amdFreeSync || hdmiVrrDeclared(); }
};

EdidVrrInfo ParseEdidVrr(const std::vector<BYTE>& e) {
    EdidVrrInfo out;
    if (e.size() < 128) return out;
    out.edidPresent = true;
    out.bytes = e.size();
    // 基础块 4 个 18B 描述符：0x36/0x48/0x5A/0x6C
    for (std::size_t o = 0x36; o <= 0x6C && o + 18 <= e.size(); o += 0x12) {
        if (e[o] == 0 && e[o + 1] == 0 && e[o + 2] == 0 && e[o + 3] == 0xFD) {
            out.baseMin = e[o + 5];
            out.baseMax = e[o + 6];
        }
    }
    const int extCount = e[126];
    auto hex = [](const BYTE* p, int n) {
        std::string s;
        char b[4] = {};
        for (int j = 0; j < n; ++j) { sprintf_s(b, "%02X", p[j]); s += b; }
        return s;
    };
    for (int k = 0; k < extCount; ++k) {
        const std::size_t off = 128u + 128u * static_cast<unsigned>(k);
        if (off + 128 > e.size()) break;
        const BYTE type = e[off];
        if (type == 0x70) {
            out.displayId = true;
            // v3.3：DisplayID 1.2（ext[1]==0x12）数据块里的 Vendor Specific——块头与
            // CTA 同构（高 3 位 type、低 5 位 len），AMD FreeSync VSDB 与 CTA 版同构
            // （OUI+byte3/byte4+min/max）。
            // 🔴 DisplayID 2.0（ext[1]==0x20，本机 DISPLAY1 内屏即是）**不解码**：
            //    其块头为 2B/11 位 size 编码、块类型表不同，无规范文本核验，离线多
            //    候选全不自洽（纪律：不写验不了的代码）。但字节证据在案：DISPLAY1 的
            //    AMD FreeSync 声明 `1A 00 00 03 01 30 A5`（min48/max165）位于其 2.0
            //    section 内（CTA 块没有）——将来核验 2.0 后补解码。
            if (!out.amdFreeSync && e[off + 1] == 0x12) {
                const std::size_t secLen = e[off + 2];
                std::size_t j = off + 3;
                const std::size_t secEnd = off + 3 + secLen;
                while (j < secEnd && j + 1 < off + 128) {
                    const BYTE bh = e[j];
                    const BYTE btype = bh >> 5;
                    const std::size_t blen = bh & 0x1F;
                    if (blen == 0x1F) break;   // 扩展长编码：本机样本不涉及，遇到即停
                    if (j + 1 + blen > off + 128) break;
                    if (btype == 7 && blen >= 7) {   // Vendor Specific 块
                        const BYTE* q = &e[j + 1];
                        const unsigned oui2 = q[0] | (q[1] << 8) | (q[2] << 16);
                        if (oui2 == 0x00001A) {      // AMD FreeSync（CTA 同构解码）
                            out.amdFreeSync = true;
                            out.fsByte3 = q[3]; out.fsByte4 = q[4];
                            out.fsMin = q[5]; out.fsMax = q[6];
                            out.fsHex = hex(q, static_cast<int>(blen < 16 ? blen : 16));
                            out.fsFromDisplayId = true;
                        }
                    }
                    j += 1 + blen;
                }
            }
            continue;
        }
        if (type != 0x02) continue;                             // 只认 CTA-861
        const std::size_t dtdOffset = e[off + 2];
        std::size_t i = off + 4;
        const std::size_t end = off + dtdOffset;
        while (i + 1 < end && end <= off + 128) {
            const BYTE h = e[i];
            const int tag = h >> 5, len = h & 0x1F;
            if (len == 0 || i + 1 + static_cast<std::size_t>(len) > end) break;
            const BYTE* p = &e[i + 1];
            if (tag == 3 && len >= 3) {          // Vendor Specific Data Block
                const unsigned oui = p[0] | (p[1] << 8) | (p[2] << 16);
                if (oui == 0x00001A && len >= 7) {   // AMD FreeSync VSDB
                    out.amdFreeSync = true;
                    out.fsByte3 = p[3]; out.fsByte4 = p[4];
                    out.fsMin = p[5]; out.fsMax = p[6];
                    out.fsHex = hex(p, len < 16 ? len : 16);
                } else if (oui == 0xC45DD8) {        // HDMI Forum VSDB（v3.2 核验：tag3+OUI
                    // 是其唯一形式——et12 不是 HF-VSDB，HDMI Forum 的扩展块是 0x78/0x79
                    // （HF-EEODB/HF-SCDB，另有用途）。VRR 范围就在本块内（HDMI 2.1a 起，
                    // 10 位编码），偏移经 Linux drm_edid.c + 本机 SAC2765 样本逐字段核验。
                    // 注意口径：p 是含 OUI 的整块负载，VRR 字段在 p[8]/p[9]（=OUI 后
                    // 第 5/6 字节）；离线验证曾抓到按"OUI 后索引"直写 p[5]/p[6] 的
                    // off-by-3——与 FreeSync 的 p[5]/p[6]（含 OUI 口径恰好对）不同构。
                    out.hdmiForum = true;
                    out.hfHex = hex(p, len < 16 ? len : 16);
                    if (len >= 10) {   // VRR 字段护栏：负载 ≥10B 才读 p[8]/p[9]
                        out.hdmiVrrMin = p[8] & 0x3F;                     // VRR_min = bits5:0
                        out.hdmiVrrMax = ((p[8] & 0xC0) << 2) | p[9];     // VRR_max = 2:8 拼接
                    }
                }
                // OUI 0x000C03 = HDMI 1.4 VSDB：不含 VRR，忽略
            }
            i += 1 + static_cast<std::size_t>(len);
        }
    }
    return out;
}

// DeviceID = DISPLAY\<型号>\<实例>；EDID 在 \Device Parameters。实例串可能与
// 注册表枚举实例不一致（UID 后缀），兜底按型号枚举全部实例。
EdidVrrInfo ReadEdidForMonitor(const std::wstring& deviceName) {
    DISPLAY_DEVICEW mon{};
    mon.cb = sizeof(mon);
    if (!EnumDisplayDevicesW(deviceName.c_str(), 0, &mon, 0)) return {};
    std::vector<BYTE> edid;
    auto readEdid = [](const std::wstring& path, std::vector<BYTE>& out) {
        DWORD size = 0;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, path.c_str(), L"EDID", RRF_RT_REG_BINARY,
                nullptr, nullptr, &size) != ERROR_SUCCESS ||
            size == 0 || size > 8192) return false;
        out.assign(size, 0);
        DWORD got = size;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, path.c_str(), L"EDID", RRF_RT_REG_BINARY,
                nullptr, out.data(), &got) != ERROR_SUCCESS) return false;
        out.resize(got);
        return true;
    };
    const std::wstring id = mon.DeviceID;
    readEdid(L"SYSTEM\\CurrentControlSet\\Enum\\" + id + L"\\Device Parameters", edid);
    if (edid.empty()) {
        const std::size_t p1 = id.find(L'\\');
        const std::size_t p2 = p1 == std::wstring::npos ? p1 : id.find(L'\\', p1 + 1);
        if (p1 != std::wstring::npos && p2 != std::wstring::npos) {
            const std::wstring model = id.substr(p1 + 1, p2 - p1 - 1);
            const std::wstring base = L"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\" + model;
            HKEY hBase = nullptr;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, base.c_str(), 0, KEY_READ, &hBase) == ERROR_SUCCESS) {
                for (DWORD k = 0; edid.empty(); ++k) {
                    WCHAR inst[128] = {};
                    DWORD len = 128;
                    if (RegEnumKeyExW(hBase, k, inst, &len, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) break;
                    readEdid(base + L"\\" + inst + L"\\Device Parameters", edid);
                }
                RegCloseKey(hBase);
            }
        }
    }
    return ParseEdidVrr(edid);
}

// W2：设置→图形→默认图形设置→可变刷新率。返回 1=开 / 0=显式关 / -1=值不存在（默认关）。
// v3：按 ';' 切 token、'=' 切键值精确匹配键名（v1/v2 裸 strstr 可被键名子串误匹配）；
//     rawOut 带回原始串（v1/v2 只打印判断结果，实测串无留痕）。
int ReadWindowsVrrToggle(std::string& rawOut) {
    char buf[1024] = {};
    DWORD size = sizeof(buf);
    if (RegGetValueA(HKEY_CURRENT_USER, "Software\\Microsoft\\DirectX\\UserGpuPreferences",
            "DirectXUserGlobalSettings", RRF_RT_REG_SZ, nullptr, buf, &size) != ERROR_SUCCESS) return -1;
    rawOut = buf;
    const std::string s = rawOut;
    std::size_t pos = 0;
    for (;;) {
        const std::size_t semi = s.find(';', pos);
        const std::size_t end = semi == std::string::npos ? s.size() : semi;
        const std::size_t eq = s.find('=', pos);
        if (eq != std::string::npos && eq < end) {
            std::size_t kL = pos, kR = eq;
            while (kL < kR && (s[kL] == ' ' || s[kL] == '\t')) ++kL;
            while (kR > kL && (s[kR - 1] == ' ' || s[kR - 1] == '\t')) --kR;
            std::size_t vL = eq + 1, vR = end;
            while (vL < vR && (s[vL] == ' ' || s[vL] == '\t')) ++vL;
            while (vR > vL && (s[vR - 1] == ' ' || s[vR - 1] == '\t')) --vR;
            if (kR > kL && s.compare(kL, kR - kL, "VRROptimizeEnable") == 0)
                return (vR - vL == 1 && s[vL] == '1') ? 1 : 0;
        }
        if (semi == std::string::npos) break;
        pos = semi + 1;
    }
    return -1;
}

// ---- 帧统计采样：一次"相"跑完，把 (SyncRefreshCount, SyncQPCTime) 对全收回来 ----
struct PhaseSample {
    std::int64_t syncRefresh;
    std::int64_t syncQpc;
    std::int64_t presentRefresh;   // PresentRefreshCount：显示时刻的 v 空白计数
};
struct PhaseResult {
    std::vector<PhaseSample> samples;
    std::size_t disjoint = 0;      // DISJOINT 次数（序列重置）
    std::size_t allZero = 0;       // 全零统计（bitblt 链/未合成征兆）
    std::size_t noAdvance = 0;     // 连续样本 SyncRefreshCount 不动
    std::vector<double> rateHz;    // 相邻样本反解的瞬时面板刷新率
    double meanHz = 0, minHz = 0, maxHz = 0;
    double aggregateHz = 0;        // 整相聚合率 = 总推进 / 总时长（判据用它：无离群问题）
    double callRateHz = 0;         // Present() 调用速率（判别力守门参照；不能用
                                   // PresentRefreshCount——它也是 vblank 计数，与
                                   // SyncRefreshCount 同源同速，2026-11-03 实测）
    double medianHz = 0;
    bool Usable() const { return aggregateHz > 0; }
};

LRESULT CALLBACK WndProc(HWND w, UINT m, WPARAM a, LPARAM b) {
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(w, m, a, b);
}

// 产品同形建链（VideoRenderer.cpp:3569-3575）：FLIP_DISCARD / 3 缓冲 /
// BGRA8 / 能力位允许就带 ALLOW_TEARING。窗口铺满目标监视器（borderless），
// 尽量走独立翻转路径让帧统计说真话。
IDXGISwapChain4* CreateChain(ID3D11Device* device, HWND window, UINT width, UINT height,
    bool tearingCapability, std::uint32_t& flagsOut) {
    IDXGIDevice* dxgiDevice = nullptr;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))) return nullptr;
    IDXGIAdapter* adapter = nullptr;
    if (FAILED(dxgiDevice->GetAdapter(&adapter))) { dxgiDevice->Release(); return nullptr; }
    IDXGIFactory5* factory = nullptr;
    const bool ok = SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory)));
    dxgiDevice->Release(); adapter->Release();
    if (!ok) return nullptr;

    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = width; description.Height = height;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 3;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    description.Scaling = DXGI_SCALING_NONE;
    description.Flags = tearingCapability ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
    flagsOut = description.Flags;
    IDXGISwapChain1* chain1 = nullptr;
    const HRESULT created = factory->CreateSwapChainForHwnd(device, window, &description, nullptr, nullptr, &chain1);
    factory->Release();
    if (FAILED(created) || chain1 == nullptr) return nullptr;
    IDXGISwapChain4* chain = nullptr;
    if (FAILED(chain1->QueryInterface(IID_PPV_ARGS(&chain)))) { chain1->Release(); return nullptr; }
    chain1->Release();
    IDXGIDevice1* latencyDevice = nullptr;
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&latencyDevice)))) {
        latencyDevice->SetMaximumFrameLatency(2);   // 同产品 :2955
        latencyDevice->Release();
    }
    return chain;
}

// 一相：presentFlags 全零=Present(1,0)；tearing=Present(0, ALLOW_TEARING)。
// paceHz<=0 = 全速（C 相）。每个 Present 后采一次帧统计。
PhaseResult RunPhase(IDXGISwapChain4* chain, ID3D11DeviceContext* context,
    ID3D11RenderTargetView* target, double seconds, double paceHz, bool tearing, UINT tearingFlag) {
    PhaseResult result;
    const std::int64_t frequency = QpcFrequency();
    const auto startQpc = QpcNow();
    const double periodTicks = paceHz > 0 ? QpcFrequency() / paceHz : 0.0;
    std::int64_t nextDueQpc = startQpc;
    std::uint64_t frame = 0;
    std::uint64_t presents = 0;    // 成功 Present 次数（守门参照）
    for (;;) {
        const auto nowQpc = QpcNow();
        if (static_cast<double>(nowQpc - startQpc) / frequency >= seconds) break;
        if (periodTicks > 0 && nowQpc < nextDueQpc) {
            const auto waitMs = static_cast<std::int64_t>((nextDueQpc - nowQpc) * 1000.0 / frequency);
            std::this_thread::sleep_for(std::chrono::milliseconds(std::clamp<std::int64_t>(waitMs, 1, 20)));
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            continue;
        }
        const float hue = static_cast<float>(frame % 60u) / 60.0f;
        const float color[4] = { hue, 1.0f - hue, 0.5f, 1.0f };
        context->OMSetRenderTargets(1, &target, nullptr);
        context->ClearRenderTargetView(target, color);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->Flush();

        const HRESULT present = tearing
            ? chain->Present(0, tearingFlag ? DXGI_PRESENT_ALLOW_TEARING : 0)
            : chain->Present(1, 0);
        if (present == DXGI_ERROR_INVALID_CALL) break;
        if (FAILED(present) && present != DXGI_ERROR_WAS_STILL_DRAWING) break;
        ++presents;
        if (periodTicks > 0) nextDueQpc += static_cast<std::int64_t>(periodTicks);

        DXGI_FRAME_STATISTICS stats{};
        const HRESULT statsResult = chain->GetFrameStatistics(&stats);
        if (statsResult == DXGI_ERROR_FRAME_STATISTICS_DISJOINT) { ++result.disjoint; continue; }
        if (FAILED(statsResult)) continue;
        const auto syncQpc = stats.SyncQPCTime.QuadPart;
        if (stats.PresentRefreshCount == 0 && stats.SyncRefreshCount == 0 && syncQpc == 0) { ++result.allZero; continue; }
        result.samples.push_back({ static_cast<std::int64_t>(stats.SyncRefreshCount),
            syncQpc, static_cast<std::int64_t>(stats.PresentRefreshCount) });
        ++frame;
    }
    // 相邻样本反解瞬时刷新率；Δt<=0（同 tick）跳过。
    for (std::size_t i = 1; i < result.samples.size(); ++i) {
        const auto dc = result.samples[i].syncRefresh - result.samples[i - 1].syncRefresh;
        const auto dt = result.samples[i].syncQpc - result.samples[i - 1].syncQpc;
        if (dc <= 0) { ++result.noAdvance; continue; }
        if (dt <= 0) continue;
        result.rateHz.push_back(static_cast<double>(dc) * frequency / static_cast<double>(dt));
    }
    if (!result.rateHz.empty()) {
        double sum = 0;
        result.minHz = result.maxHz = result.rateHz.front();
        for (const auto r : result.rateHz) {
            sum += r;
            result.minHz = std::min(result.minHz, r);
            result.maxHz = std::max(result.maxHz, r);
        }
        result.meanHz = sum / static_cast<double>(result.rateHz.size());
        auto sorted = result.rateHz;
        std::sort(sorted.begin(), sorted.end());
        result.medianHz = sorted[sorted.size() / 2];
    }
    // 整相聚合率：v3.1 改分块聚合取中位——v1/v2/v3 用首尾单对，对 SyncQPCTime
    // 毛刺无免疫（2026-11-04 DISPLAY1 内屏轮实测：C 相中位 164.6 正常而首尾对
    // ΔQPC≈0.4ms，聚合率得出 623626Hz 荒谬值，跌幅判据被假阳性穿过）。序列均分
    // 5 块、每块取块首尾对算块率、取中位："大尺度推进/时长"的设计意图不变，
    // 单点毛刺被块中位数吸收，DISJOINT 跨块重置的影响也被限制在单块内。
    if (result.samples.size() >= 2) {
        const std::size_t n = result.samples.size();
        const std::size_t blocks = std::min<std::size_t>(5, n / 2);
        std::vector<double> blockRates;
        for (std::size_t b = 0; b < blocks; ++b) {
            const std::size_t i0 = n * b / blocks;
            const std::size_t i1 = n * (b + 1) / blocks - 1;
            if (i1 <= i0) continue;
            const auto dc = result.samples[i1].syncRefresh - result.samples[i0].syncRefresh;
            const auto dt = result.samples[i1].syncQpc - result.samples[i0].syncQpc;
            if (dc > 0 && dt > 0)
                blockRates.push_back(static_cast<double>(dc) * frequency / static_cast<double>(dt));
        }
        if (!blockRates.empty()) {
            std::sort(blockRates.begin(), blockRates.end());
            result.aggregateHz = blockRates[blockRates.size() / 2];
        }
    }
    const double elapsed = static_cast<double>(QpcNow() - startQpc) / frequency;
    if (elapsed > 0) result.callRateHz = static_cast<double>(presents) / elapsed;
    return result;
}

}   // namespace

int main() {
    // 本机已知怪癖（present_pacing_probe.cpp:195 同款）：图形探针会间歇性地在一条
    // stderr（"can't open file for write !"）之后一声不响地结束——stdout 是块缓冲
    // 时连开头几行都留不下，无法区分"没建起来"和"建起来后死了"。无缓冲 + 每段
    // 尽早落盘是既定缓解。
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    const auto secondsCal = EnvDouble("VRR_SECONDS_CAL", 1.5);
    const auto secondsSlow = EnvDouble("VRR_SECONDS_SLOW", 3.0);
    const auto contentFps = EnvDouble("VRR_FPS", 24.0);
    const auto deviceName = EnvString("VRR_DEVICE", "");

    // 目标监视器统一解析：VRR_DEVICE 显式指定（校验存在且附着、取当前模式），
    // 否则主屏。v1/v2 头注声称 VRR_DEVICE 可用但 deviceName 是死变量，E 段永远
    // 主屏；v3 起此结果供 N 段目标屏优先、E 段建窗矩形共用。
    std::wstring targetDeviceW;
    {
        DEVMODEW mode{}; mode.dmSize = sizeof(mode);
        bool positioned = false;
        if (!deviceName.empty()) {
            std::wstring deviceNameW;
            const int n = MultiByteToWideChar(CP_UTF8, 0, deviceName.c_str(), -1, nullptr, 0);
            if (n > 1) {
                deviceNameW.resize(static_cast<std::size_t>(n - 1));
                MultiByteToWideChar(CP_UTF8, 0, deviceName.c_str(), -1, &deviceNameW[0], n);
            }
            DISPLAY_DEVICEW dd{};
            dd.cb = sizeof(dd);
            for (DWORD i = 0; EnumDisplayDevicesW(nullptr, i, &dd, 0); ++i) {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) &&
                    _wcsicmp(dd.DeviceName, deviceNameW.c_str()) == 0) {
                    if (EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, &mode) &&
                        mode.dmPelsWidth > 0) {
                        targetDeviceW = dd.DeviceName;
                        positioned = true;
                    }
                    break;
                }
            }
            if (!positioned)
                std::printf("[E] VRR_DEVICE=%s 不可用（不存在/未附着/取模式失败）——退回主屏\n",
                    deviceName.c_str());
        }
        if (!positioned) {
            MONITORINFOEXW mi{};
            mi.cbSize = sizeof(mi);
            const HMONITOR monitor = MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (GetMonitorInfoW(monitor, &mi)) targetDeviceW = mi.szDevice;
        }
    }

    std::printf("=== 面板 VRR 状态探针 ===\n");
    std::printf("条件：内容=%.0ffps · C相=%.1fs 全速 · V相=%.1fs 慢速vsync · T相=%.1fs 慢速tearing\n\n",
        contentFps, secondsCal, secondsSlow, secondsSlow);

    // ---- S 静态段：DXGI 能力位 ----
    bool tearingCapability = false;
    {
        IDXGIFactory5* factory = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
            IDXGIAdapter1* adapter = nullptr;
            for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
                DXGI_ADAPTER_DESC1 desc{};
                adapter->GetDesc1(&desc);
                BOOL allow = FALSE;
                const HRESULT hr = factory->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING,
                    &allow, sizeof(allow));
                std::printf("[S] 适配器 %u: %s → PRESENT_ALLOW_TEARING=%s\n",
                    i, Narrow(desc.Description).c_str(), SUCCEEDED(hr) && allow ? "支持" : "不支持");
                if (i == 0) tearingCapability = SUCCEEDED(hr) && allow;
                adapter->Release();
            }
            factory->Release();
        } else {
            std::printf("[S] CreateDXGIFactory1 失败\n");
        }
        // 目标监视器与声明刷新率
        DISPLAY_DEVICEW device{};
        device.cb = sizeof(device);
        std::wstring primary;
        for (DWORD i = 0; EnumDisplayDevicesW(nullptr, i, &device, 0); ++i) {
            if (!(device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
            DEVMODEW mode{}; mode.dmSize = sizeof(mode);
            const double hz = EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, &mode) &&
                mode.dmDisplayFrequency > 1 ? static_cast<double>(mode.dmDisplayFrequency) : 0.0;
            std::printf("[S] 监视器 %s: 声明 %.0f Hz%s\n", Narrow(device.DeviceName).c_str(), hz,
                device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE ? " （主屏）" : "");
            if ((device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) && primary.empty())
                primary = device.DeviceName;
        }
        std::printf("[S] DXGI 能力位=%s（只证驱动/OS 支持 present 模式，不证面板在 VRR）\n\n",
            tearingCapability ? "有" : "无");
    }

    // ---- W 原生段：EDID 声明面（W1）+ Windows 系统级 VRR 开关（W2）----
    // 设置→高级显示的「可变刷新频率：受支持(48-240)」没有公开 DisplayConfig 查询，
    // 这里还原到公开源头：注册表 EDID。厂商无关——AMD FreeSync / G-SYNC
    // Compatible / VESA Adaptive-Sync 的声明都落在 CTA-861 VSDB；基础块范围
    // 描述符只是面板扫描范围，不作 VRR 声明。
    std::vector<std::pair<std::wstring, EdidVrrInfo>> edidByMonitor;
    std::string vrrToggleRaw;   // W2 原始串（取证落盘）
    const int windowsVrrToggle = ReadWindowsVrrToggle(vrrToggleRaw);
    {
        DISPLAY_DEVICEW device{};
        device.cb = sizeof(device);
        for (DWORD i = 0; EnumDisplayDevicesW(nullptr, i, &device, 0); ++i) {
            if (!(device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
            const std::wstring name = device.DeviceName;
            const EdidVrrInfo ed = ReadEdidForMonitor(name);
            edidByMonitor.emplace_back(name, ed);
            const std::string label = Narrow(name.c_str());
            if (!ed.edidPresent) {
                std::printf("[W] %s: EDID 不可读（注册表无 EDID 值）——声明面缺席，不影响 E 段\n", label.c_str());
                continue;
            }
            std::string tags;
            if (ed.amdFreeSync) tags += " AMD-FreeSync-VSDB";
            if (ed.hdmiForum) tags += " HDMI-Forum-VSDB";
            if (ed.displayId) tags += " DisplayID扩展(未解码)";
            if (tags.empty()) tags = " 无VRR声明";
            std::printf("[W] %s: EDID %zu B，声明=[%s ] 范围描述符=%d-%d Hz（只证扫描范围）\n",
                label.c_str(), ed.bytes, tags.c_str(), ed.baseMin, ed.baseMax);
            if (ed.amdFreeSync)
                std::printf("[W]   AMD FreeSync VSDB%s: byte3=0x%02X byte4=0x%02X min=%d max=%d 原文=%s\n",
                    ed.fsFromDisplayId ? "（DisplayID 1.2 扩展内，v3.3 解码）" : "",
                    ed.fsByte3, ed.fsByte4, ed.fsMin, ed.fsMax, ed.fsHex.c_str());
            if (ed.hdmiForum) {
                std::printf("[W]   HDMI Forum VSDB 原文=%s", ed.hfHex.c_str());
                if (ed.hdmiVrrMin > 0)
                    std::printf(" VRR=%d-%d Hz%s", ed.hdmiVrrMin, ed.hdmiVrrMax,
                        ed.hdmiVrrDeclared() ? "（声明 HDMI VRR）" : "（范围声明但未达支持判据）");
                else
                    std::printf("（无 VRR 范围字段——HDMI 2.1 原版块）");
                std::printf("，偏移经 Linux drm_edid.c + SAC2765 样本核验\n");
            }
        }
        std::printf("[W] Windows 系统级 VRR 开关(VRROptimizeEnable)：%s\n",
            windowsVrrToggle == 1 ? "开" : windowsVrrToggle == 0 ? "关" : "未设置（默认关）");
        if (!vrrToggleRaw.empty())
            std::printf("[W]   DirectXUserGlobalSettings 原文=\"%s\"\n", vrrToggleRaw.c_str());
        std::printf("[W] 该开关（设置→图形→默认图形设置→可变刷新率）只管 OS 对 flip 呈现的"
            " VRR 接管，与驱动层 G-SYNC/FreeSync 相互独立\n\n");
    }

    // ---- N NVAPI 段：遍历所有桌面显示逐个查（混合 GPU 下 NVAPI 只认自己托管
    //      的路径，逐个查顺带把“哪块屏不在 NVIDIA 管辖”也摸清）----
    Nvapi nvapi;
    NvVrrInfoV1 vrrIdle{};   // 播放前（空闲）读数
    NvU32 displayId = 0;
    bool nvapiValid = false;
    std::string windowDevice;   // 探针窗口实际所在的监视器设备名（E 段填）
    if (nvapi.Load()) {
        DISPLAY_DEVICEW device{};
        device.cb = sizeof(device);
        for (DWORD i = 0; EnumDisplayDevicesW(nullptr, i, &device, 0); ++i) {
            if (!(device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
            const std::string name = Narrow(device.DeviceName);
            NvU32 id = 0;
            if (nvapi.getDisplayIdByDisplayName(name.c_str(), &id) != kNvapiOk) {
                std::printf("[N] %s: GetDisplayIdByDisplayName 失败\n", name.c_str());
                continue;
            }
            NvVrrInfoV1 info{};
            info.version = static_cast<NvU32>(sizeof(NvVrrInfoV1)) | (1u << 16);
            const NvAPI_Status status = nvapi.getVRRInfo(id, &info);
            if (status == kNvapiOk) {
                std::printf("[N] %s (displayId=0x%X)：possible=%u requested=%u enabled=%u indicator=%u inVrrMode=%u\n",
                    name.c_str(), id, info.bIsVRRPossible, info.bIsVRRRequested,
                    info.bIsVRREnabled, info.bIsVRRIndicatorEnabled, info.bIsDisplayInVRRMode);
                // 留下第一个可判的显示当“空闲/慢相后”对照读数；目标屏（E 段
                // 窗口将所在屏，main 开头统一解析）读数优先——v1/v2 在这里用
                // windowDevice==name 判优先，但 windowDevice 要到 E 段建窗后才有
                // 值（条件恒假），实际靠 E 段二次查询补救；v3 改用 targetDeviceW
                // 在遍历期真实生效，E 段二次查询保留作最终校正。
                const bool isTarget = !targetDeviceW.empty() &&
                    _wcsicmp(device.DeviceName, targetDeviceW.c_str()) == 0;
                if (!nvapiValid || isTarget) { vrrIdle = info; displayId = id; nvapiValid = true; }
            } else {
                std::printf("[N] %s (displayId=0x%X)：GetVRRInfo status=%d（非 NVIDIA 托管路径或不支持）\n",
                    name.c_str(), id, status);
            }
        }
    } else {
        std::printf("[N] nvapi64.dll 不可用（非 NVIDIA 环境），跳过——E 段照常判决\n");
    }
    std::printf("\n");

    // ---- E 经验段 ----
    // 目标监视器的矩形（铺满，最大化独立翻转概率）：VRR_DEVICE 指定时建在其上，
    // 否则主屏（targetDeviceW 已在 main 开头统一解析）。按设备名取当前模式，
    // 避免“MonitorFromPoint 恒取主屏”掩盖选屏失效。
    DEVMODEW targetMode{}; targetMode.dmSize = sizeof(targetMode);
    if (targetDeviceW.empty() ||
        !EnumDisplaySettingsW(targetDeviceW.c_str(), ENUM_CURRENT_SETTINGS, &targetMode) ||
        targetMode.dmPelsWidth == 0) {
        std::printf("目标监视器 %s 当前模式获取失败\n", Narrow(targetDeviceW.c_str()).c_str());
        return 2;
    }
    const UINT width = static_cast<UINT>(targetMode.dmPelsWidth);
    const UINT height = static_cast<UINT>(targetMode.dmPelsHeight);

    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc; wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"vrr-state"; wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    RegisterClassW(&wc);
    // 置顶 + 铺满目标屏：display_grid_probe 实测被遮挡时帧统计不推进（421 次零差分教训）
    HWND window = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, L"vrr-state",
        WS_POPUP, targetMode.dmPosition.x, targetMode.dmPosition.y,
        static_cast<int>(width), static_cast<int>(height),
        nullptr, nullptr, wc.hInstance, nullptr);
    if (window == nullptr) { std::printf("建窗失败\n"); return 2; }
    ShowWindow(window, SW_SHOWNORMAL);
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    SetForegroundWindow(window);

    // 窗口所在监视器的设备名与声明刷新率（混合 GPU 下 EnumDisplaySettings 按
    // 设备名取，避免“主屏≠窗口所在屏”的错配）。
    const HMONITOR windowMonitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW windowMonitorInfo{};
    windowMonitorInfo.cbSize = sizeof(windowMonitorInfo);
    double windowDeclaredHz = 0;
    if (GetMonitorInfoW(windowMonitor, &windowMonitorInfo)) {
        windowDevice = Narrow(windowMonitorInfo.szDevice);
        DEVMODEW mode{}; mode.dmSize = sizeof(mode);
        if (EnumDisplaySettingsW(windowMonitorInfo.szDevice, ENUM_CURRENT_SETTINGS, &mode) &&
            mode.dmDisplayFrequency > 1)
            windowDeclaredHz = static_cast<double>(mode.dmDisplayFrequency);
        // 窗口实际落点与目标屏不符（拓扑错配，混合 GPU 老坑）——N/W/E 判据以
        // 窗口屏为准，但要让读者知道选屏没生效。
        if (!targetDeviceW.empty() &&
            _wcsicmp(windowMonitorInfo.szDevice, targetDeviceW.c_str()) != 0)
            std::printf("[E] ⚠ 窗口实际落在 %s（目标 %s）——拓扑错配，判据以窗口屏为准\n",
                windowDevice.c_str(), Narrow(targetDeviceW.c_str()).c_str());
    }

    ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr; D3D_FEATURE_LEVEL level{};
    // D3D 设备显式建在窗口屏所在适配器上：混合 GPU 下默认适配器可能与窗口屏
    // 不同，swap chain 绕合成路径概率增大、帧统计失真（§4 拓扑教训落实）。
    // 找不到匹配输出时退回默认适配器（D3D_DRIVER_TYPE_HARDWARE）。
    {
        IDXGIFactory1* factory = nullptr;
        IDXGIAdapter1* chosen = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
            for (UINT a = 0; chosen == nullptr; ++a) {
                IDXGIAdapter1* adapter = nullptr;
                if (factory->EnumAdapters1(a, &adapter) == DXGI_ERROR_NOT_FOUND) break;
                for (UINT o = 0; ; ++o) {
                    IDXGIOutput* output = nullptr;
                    if (adapter->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;
                    DXGI_OUTPUT_DESC od{};
                    if (SUCCEEDED(output->GetDesc(&od)) &&
                        _wcsicmp(od.DeviceName, windowMonitorInfo.szDevice) == 0) {
                        chosen = adapter;   // 命中：持有引用，adapter 不在此处释放
                        break;
                    }
                    output->Release();
                }
                if (chosen == nullptr) adapter->Release();
            }
            factory->Release();
        }
        const HRESULT created = chosen != nullptr
            ? D3D11CreateDevice(chosen, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                  D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                  &device, &level, &context)
            : D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                  D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                  &device, &level, &context);
        if (chosen != nullptr) {
            DXGI_ADAPTER_DESC1 desc{};
            chosen->GetDesc1(&desc);
            std::printf("[E] D3D 设备建在窗口屏所在适配器：%s\n", Narrow(desc.Description).c_str());
            chosen->Release();
        }
        if (FAILED(created)) { std::printf("D3D11CreateDevice 失败\n"); return 2; }
    }
    std::uint32_t chainFlags = 0;
    IDXGISwapChain4* chain = CreateChain(device, window, width, height, tearingCapability, chainFlags);
    if (chain == nullptr) { std::printf("建链失败\n"); return 2; }
    ID3D11Texture2D* backBuffer = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) ||
        FAILED(device->CreateRenderTargetView(backBuffer, nullptr, &target))) {
        std::printf("RTV 失败\n"); return 2;
    }
    backBuffer->Release();

    const UINT tearingFlag = (chainFlags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) ? DXGI_PRESENT_ALLOW_TEARING : 0;
    std::printf("[E] 链：%ux%u FLIP_DISCARD/3缓冲/BGRA8/MaxFrameLatency=2，链上 ALLOW_TEARING=%s\n",
        width, height, tearingFlag ? "带" : "无（能力位缺，T 相退化为 Present(0,0)）");
    std::printf("[E] 窗口所在监视器：%s 声明 %.0f Hz\n\n",
        windowDevice.empty() ? "?" : windowDevice.c_str(), windowDeclaredHz);

    // 窗口所在屏的 NVAPI 结果优先参与判决（上面遍历时留下的是“第一个可判”的）。
    // 🔴 只有查到窗口屏**本人**的读数才允许进判决——混合 GPU 下 nvapiValid 可能是
    //    另一块屏的（本轮 DISPLAY1 possible=1 而 DISPLAY5 查询直接失败）。
    bool nvapiWindowValid = false;
    if (nvapi.available && !windowDevice.empty()) {
        NvU32 id = 0;
        NvVrrInfoV1 info{};
        info.version = static_cast<NvU32>(sizeof(NvVrrInfoV1)) | (1u << 16);
        if (nvapi.getDisplayIdByDisplayName(windowDevice.c_str(), &id) == kNvapiOk &&
            nvapi.getVRRInfo(id, &info) == kNvapiOk) {
            vrrIdle = info; displayId = id; nvapiValid = true; nvapiWindowValid = true;
        }
    }

    const auto calib1 = RunPhase(chain, context, target, secondsCal, 0.0, false, 0);
    const auto slowV1 = RunPhase(chain, context, target, secondsSlow, contentFps, false, 0);
    const auto slowT = RunPhase(chain, context, target, secondsSlow, contentFps, true, tearingFlag);
    const auto slowV2 = RunPhase(chain, context, target, secondsSlow, contentFps, false, 0);
    const auto calib2 = RunPhase(chain, context, target, secondsCal, 0.0, false, 0);

    auto dump = [](const char* tag, const PhaseResult& p) {
        std::printf("  %s：样本=%zu 瞬时率样本=%zu DISJOINT=%zu 全零=%zu 不推进=%zu\n",
            tag, p.samples.size(), p.rateHz.size(), p.disjoint, p.allZero, p.noAdvance);
        if (p.aggregateHz > 0)
            std::printf("      面板刷新率：聚合=%.1f Hz 中位=%.1f 区间=[%.1f, %.1f]  present调用率=%.1f Hz\n",
                p.aggregateHz, p.medianHz, p.minHz, p.maxHz, p.callRateHz);
    };
    std::printf("[E] C1 相（全速基线）\n");  dump("C1", calib1);
    std::printf("[E] V1 相（慢速 Present(1,0)）\n"); dump("V1", slowV1);
    std::printf("[E] T 相（慢速 tearing）\n");       dump("T", slowT);
    std::printf("[E] V2 相（慢速重复，轮内稳定性检验）\n"); dump("V2", slowV2);
    std::printf("[E] C2 相（全速重复，轮内稳定性检验）\n"); dump("C2", calib2);

    // 判决链（五重守门 + ⓪统计自洽前置门，详见头注）：
    // ⓪ 统计自洽 → 跌落 → 调用率 → LFC 相关 → 轮内稳定 → 节奏。
    bool empiricalVrr = false;
    bool empiricalAmbiguous = false;   // 有跌落但守门链未全过，判别力受限
    std::string empiricalNote;
    // ⓪ 块聚合率与中位率严重背离 = 帧统计本身不可信（驱动模拟/采样毛刺），
    //    任何跌落都不能归因面板变刷新（v3.1：内屏轮 623626Hz 教训）。
    auto sane = [](const PhaseResult& p) {
        return std::fabs(p.aggregateHz - p.medianHz) <= std::max(0.2 * p.aggregateHz, 5.0);
    };
    if (calib1.Usable() && (slowV1.Usable() || slowT.Usable())) {
        const bool calibSane = sane(calib1);
        const bool slowSane = (!slowV1.Usable() || sane(slowV1)) && (!slowT.Usable() || sane(slowT));
        const double dropV = calib1.aggregateHz - slowV1.aggregateHz;
        const double dropT = calib1.aggregateHz - slowT.aggregateHz;
        const bool rawV = slowV1.Usable() && dropV > 0.25 * calib1.aggregateHz && dropV > 10.0;
        const bool rawT = slowT.Usable() && dropT > 0.25 * calib1.aggregateHz && dropT > 10.0;
        // ② 调用率守门：计数推进须快于 Present() 调用率（模拟统计只跟 present）
        auto guardOk = [](const PhaseResult& p, bool rawDrop) {
            if (!rawDrop) return false;
            if (p.callRateHz <= 0) return true;      // 无调用率参照，退回纯跌落判据
            return p.aggregateHz > 1.15 * p.callRateHz;
        };
        // ③ LFC 相关：VRR+LFC 下面板率=内容率整数倍；贴基线不算跌
        auto lfcOk = [&](const PhaseResult& p) {
            if (!p.Usable()) return false;
            if (p.medianHz > 0.9 * calib1.aggregateHz) return false;
            const double r = p.medianHz / contentFps;
            const double k = std::round(r);
            return k >= 1 && k <= 8 && std::fabs(r - k) <= 0.15;
        };
        // ④ 轮内稳定：路径切换（合成↔独立翻转/模式漂移）会让中位漂移
        const double cDrift = std::fabs(calib1.medianHz - calib2.medianHz);
        const double vDrift = std::fabs(slowV1.medianHz - slowV2.medianHz);
        const bool cStable = calib2.Usable() && cDrift <= std::max(0.05 * calib1.medianHz, 2.0);
        const bool vStable = slowV2.Usable() && vDrift <= std::max(0.05 * slowV1.medianHz, 2.0);
        // ⑤ 节奏自检：慢相调用率须贴近内容率
        const bool pacingOk = slowV1.callRateHz <= 0 ||
            std::fabs(slowV1.callRateHz - contentFps) <= 0.3 * contentFps;
        const bool guardAny = guardOk(slowV1, rawV) || guardOk(slowT, rawT);
        const bool lfc = lfcOk(slowV1) || lfcOk(slowT);
        empiricalVrr = guardAny && lfc && cStable && vStable && pacingOk && calibSane && slowSane;
        empiricalAmbiguous = !empiricalVrr && (rawV || rawT);
        std::printf("[E] 判据：V相跌幅=%.1fHz T相跌幅=%.1fHz（门槛=0.25×%.1f 且 >10Hz）→ %s\n",
            dropV, dropT, calib1.aggregateHz,
            empiricalVrr ? "实测变刷新" : empiricalAmbiguous ? "跌落但判别力受限" : "未见变刷新");
        // v3：重复相无数据 ≠ 漂移（2026-11-03 轮 4 后三相断流曾显示成"漂移"）。
        // cStable/vStable 的保守语义不变（无数据 → 不过 → empiricalVrr 不成立）。
        auto repeatLabel = [](const PhaseResult& repeat, bool stable) {
            if (!repeat.Usable()) return std::string("无数据");
            return stable ? std::string("稳") : std::string("漂移");
        };
        std::printf("[E] 守门链：统计自洽(基线/慢相)=%s/%s 调用率=%s LFC相关=%s "
            "基线稳定(C1=%.1f/C2=%s)=%s 慢相稳定(V1=%.1f/V2=%s)=%s 节奏=%s\n",
            calibSane ? "过" : "不过", slowSane ? "过" : "不过",
            guardAny ? "过" : "不过", lfc ? "过" : "不过",
            calib1.medianHz, repeatLabel(calib2, cStable).c_str(), cStable ? "过" : "不过",
            slowV1.medianHz, repeatLabel(slowV2, vStable).c_str(), vStable ? "过" : "不过",
            pacingOk ? "过" : "不过");
        if (empiricalVrr) {
            double vrrFloor = 1e9;
            if (slowV1.Usable()) vrrFloor = std::min(vrrFloor, slowV1.minHz);
            if (slowT.Usable()) vrrFloor = std::min(vrrFloor, slowT.minHz);
            std::printf("[E] VRR 观测下界≈%.1f Hz（瞬时率最小值）\n", vrrFloor);
        }
        if (empiricalAmbiguous)
            std::printf("[E] 🔴 守门链未全过（见上行各环）——帧统计疑似模拟值或路径轮内切换，"
                "跌落不能归因面板变刷新；E 段让位，以 W/N 段裁决\n");
    } else {
        empiricalNote = "聚合率不可算（";
        if (!calib1.Usable()) empiricalNote += "C1相";
        if (!slowV1.Usable()) empiricalNote += " V1相";
        if (!slowT.Usable()) empiricalNote += " T相";
        empiricalNote += " 样本<2）";
        std::printf("[E] 判据：%s\n", empiricalNote.c_str());
    }

    // ---- 汇总判决 ----
    // 面板在 VRR 模式的后验读数（慢相翻完再取一次，对照空闲读数）
    NvVrrInfoV1 vrrAfter{};
    bool nvapiAfterValid = false;
    if (nvapiValid) {
        vrrAfter.version = static_cast<NvU32>(sizeof(NvVrrInfoV1)) | (1u << 16);
        if (nvapi.getVRRInfo(displayId, &vrrAfter) == kNvapiOk) {
            nvapiAfterValid = true;
            std::printf("[N] 慢相后 (displayId=0x%X%s)：possible=%u requested=%u enabled=%u inVrrMode=%u\n",
                displayId, nvapiWindowValid ? "，窗口屏" : "，非窗口屏读数",
                vrrAfter.bIsVRRPossible, vrrAfter.bIsVRRRequested,
                vrrAfter.bIsVRREnabled, vrrAfter.bIsDisplayInVRRMode);
        }
    }

    // W1：窗口所在屏的 EDID 声明证据（判 VRR-DECLARED 机器行 + 进判决矩阵）
    EdidVrrInfo windowEdid;
    bool windowEdidValid = false;
    for (const auto& entry : edidByMonitor) {
        if (Narrow(entry.first.c_str()) == windowDevice) {
            windowEdid = entry.second;
            windowEdidValid = true;
            break;
        }
    }
    if (!windowEdidValid && !windowDevice.empty()) {
        wchar_t wdev[64] = {};
        if (MultiByteToWideChar(CP_UTF8, 0, windowDevice.c_str(), -1, wdev, 64) > 0) {
            windowEdid = ReadEdidForMonitor(wdev);
            windowEdidValid = windowEdid.edidPresent;
        }
    }
    const bool declaredVrr = windowEdidValid && windowEdid.Declared();
    if (windowEdidValid)
        std::printf("VRR-DECLARED: %s amdFreeSync=%d hdmiForum=%d baseRange=%d-%d windowsToggle=%d\n",
            windowDevice.c_str(), windowEdid.amdFreeSync ? 1 : 0, windowEdid.hdmiForum ? 1 : 0,
            windowEdid.baseMin, windowEdid.baseMax, windowsVrrToggle);
    else if (!windowDevice.empty())
        std::printf("VRR-DECLARED: %s edid=unreadable windowsToggle=%d\n", windowDevice.c_str(), windowsVrrToggle);

    const bool nvWinPossible  = nvapiWindowValid && vrrIdle.bIsVRRPossible != 0;
    const bool nvWinEnabled   = nvapiWindowValid && vrrIdle.bIsVRREnabled != 0;
    const bool nvWinRequested = nvapiWindowValid && vrrIdle.bIsVRRRequested != 0;
    const bool nvWinAny       = nvWinPossible || nvWinEnabled || nvWinRequested;
    const bool eDataOk = calib1.Usable() && (slowV1.Usable() || slowT.Usable());

    // 判决矩阵 v3（与文档 §2 统一口径，详见头注）：
    //   vrr-active  = E 五重守门全过
    //   fixed       = E 有数据且无跌落、非 ambiguous，且 NVAPI 窗口屏三位全 0、无声明
    //   vrr-idle    = 任一面板级证据（NVAPI possible/enabled/requested 或 W1 声明），
    //                 行为态未实测确证——声明无需请求态陪伴（v3 定稿）
    //   inconclusive= 零面板级证据（E 失能/判别力受限且 W/N 无声明无读数）
    const char* verdict;
    if (empiricalVrr)
        verdict = "vrr-active";
    else if (eDataOk && !empiricalAmbiguous && !nvWinAny && !declaredVrr)
        verdict = "fixed";
    else if (nvWinAny || declaredVrr)
        verdict = "vrr-idle";
    else
        verdict = "inconclusive";
    std::printf("\nVRR-VERDICT: %s\n", verdict);
    if (!empiricalNote.empty()) std::printf("VRR-NOTE: %s\n", empiricalNote.c_str());
    if (empiricalAmbiguous)
        std::printf("VRR-NOTE: E 段判别力受限（帧统计疑似模拟值），判决以 W/N 段为主\n");
    if (declaredVrr && nvapiWindowValid && vrrIdle.bIsVRREnabled && windowsVrrToggle != 1)
        std::printf("VRR-NOTE: EDID 声明 VRR（含 FreeSync 系）+ 驱动层 VRR 激活，Windows 系统级开关=关"
            "——两者独立：开关只管 OS 对 flip 呈现的 VRR 接管，不影响驱动层 G-SYNC/FreeSync\n");
    if (declaredVrr && nvapiWindowValid && !vrrIdle.bIsVRRPossible && !vrrIdle.bIsVRREnabled)
        std::printf("VRR-NOTE: EDID 声明了 VRR 但 NVIDIA 驱动报 possible=0——通常是驱动里 G-SYNC"
            "（兼容）未启用（控制面板→设置 G-SYNC）；行为以驱动为准\n");
    if (nvapiWindowValid && !empiricalVrr && (vrrIdle.bIsVRRPossible || vrrIdle.bIsVRREnabled))
        std::printf("VRR-NOTE: NVAPI 说面板能力在/已激活，但实测未见变刷新——窗口大概率走了合成路径，"
            "帧统计不代表面板真值；以 NVAPI 为准\n");

    target->Release(); chain->Release();
    context->Release(); device->Release();
    return verdict == std::string("inconclusive") ? 3 : 0;
}
