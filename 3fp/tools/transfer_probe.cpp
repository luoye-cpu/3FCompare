// HDR **分类**探针：只读会话报告的 HDR 分类，并把打印与判读统一到同一个域
// （FFF3FPHdrFormat + FFF3FPHdrProcessingPath + ST 2094 诊断字段）。
//
// 文件名沿用 transfer_probe.cpp（未入库、历史同名 exe），但它**不读传递函数**，
// 原名是个错位。三条已核实的事实决定了它能读什么：
//   · `FFF3FPSnapshot` 里没有任何 transfer 字段。与"这是哪种 HDR / 走了哪条处理路径"
//     相关的只有 `hdrFormat`（FFF3FPHdrFormat）与 `hdrProcessingPath`
//     （FFF3FPHdrProcessingPath），外加 `compatibleHdrFormats` 位掩码和 v17 的
//     `dynamicMetadata*` 诊断。
//   · 旧版用法串自称调用 `FFF3FP_GetColorTransform`：**两棵树的 API 头里都没有这个
//     导出**（只有 `FFF3FP_EvaluateColorTransform` 与 `FFF3FP_EvaluateHdrProcessing`）。
//     而这两个结构里的 `FFF3FPColorTransfer transfer` 是**调用方喂进去的输入**
//     （校验只到 `transfer > Hlg`），不是从媒体里读出来的原值。
//   · "这一帧实际按哪条 EOTF 解码"的决定在渲染器内部（ResolveDecodeTransfer(hdrState)），
//     ABI 不导出 ⇒ 判 EOTF 得用像素件（hdr10plus_inject_pixel_probe 那一类）。
//
// 旧版还有一处更硬的错位：它按 transfer 码表判读、却打印 hdrFormat 的码值。在
// FFF3FPHdrFormat 里 **2 = Hdr10Plus**、3 才是 Hlg、1 是 Hdr10；而 transfer 码表里
// 2 才是 Hlg。⇒ 同一句"输出 2"在两个域里指向完全不同的东西，判读自然对不上。
// 现在两张名表各自独立、行内标满域前缀，判据只按 hdrFormat/path 说话。
//
// 用法：transfer_probe <媒体> [--seconds N] [--frames M]
// 判据边界：**本探针不是任何修复的验收件**（未入库）。它能区分"分类落在哪条路径标签
// 上、动态元数据是否驱动"，不能区分"样本按哪条 EOTF 解出"。快照 HDR 字段描述的是
// **最近一个已渲染帧** ⇒ 需要窗口与呈现路径；无窗口/从未出帧的会话读数不成立。
//
// 编译面自适应：可选字段用 C++20 requires 探测，缺字段时当场说明而不是猜。
// 历史动机（2026-10-14 之前）：两棵树的头宽度不同——出货树那份快照尾部带 ST 2094
// 诊断字段（dynamicMetadata*），而当时可链接的 3fp/build/FFF.Native.dll 来自实验树、
// 没有这些字段。PR #31 合并后两树同一份检出（API 20、快照 464 B），探测仍然留着：
// 它拦的是"包含树与所链 DLL 不是同一份构建"，这个风险与树是否分叉无关。
//
// 背景（原审计冲突，本探针只在分类域内回答它）：
//   · 一路读数称"transfer=2 ⇒ HLG 未被按 PQ 解码"——那是在 transfer 域念 hdrFormat 的
//     码值，2 其实指 Hdr10Plus，结论不成立。
//   · 另一路读代码称"format 被翻成 HdrVivid 后 `format==Hlg` 为假 ⇒ 落到 PQ(1)"——这里的
//     `1` 若指 hdrFormat 就是 Hdr10，若指 hdrProcessingPath 就是 StaticHdr10，两个域也不能混。
//   · 本探针给出的是：真素材在会话里报到的 format/path 逐帧序列，加上离线喂给分类器的
//     四行对照（transfer 由我声明，不是读到）。

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
volatile bool g_open = false;
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

// ---- 域一：FFF3FPHdrFormat（快照里的 hdrFormat 用这张表念） ----
const char* FormatName(const std::uint32_t f) {
    switch (f) {
        case 0: return "Sdr";
        case 1: return "Hdr10";
        case 2: return "Hdr10Plus";   // 注意：不是 HLG
        case 3: return "Hlg";
        case 4: return "HDRdp";
        case 5: return "HdrVivid";
        default: return "?";
    }
}
// ---- 域二：FFF3FPHdrProcessingPath（hdrProcessingPath 用这张表念） ----
const char* PathName(const std::uint32_t p) {
    switch (p) {
        case 0: return "None";
        case 1: return "StaticHdr10";
        case 2: return "Hdr10PlusDynamic";
        case 3: return "HlgDisplayMapped";
        case 4: return "HDRdpHdr10Fallback";
        case 5: return "HDRdpFelFallback";
        case 6: return "HdrVividDynamic";
        case 7: return "ExternalDynamic";
        default: return "?";
    }
}
// FFF3FPHdrCompatibility 位掩码。
std::string CompatName(const std::uint32_t mask) {
    std::string out;
    const auto add = [&out](const char* name) {
        if (!out.empty()) out += "+";
        out += name;
    };
    if ((mask & 1u) != 0) add("Hdr10");
    if ((mask & 2u) != 0) add("Hlg");
    if ((mask & 4u) != 0) add("HDRdp");
    if ((mask & 8u) != 0) add("HdrVivid");
    if (out.empty()) out = "none";
    return out;
}
// 可选字段探测：用 requires 探测 ST 2094 诊断字段在不在，缺就明说而不是猜。
// 两棵树现在同水（都有这些字段），探测保留是为了拦"头与所链 DLL 不同一份构建"。
template <typename T>
concept HasSt2094Diagnostics = requires(const T& s) {
    s.dynamicMetadataWindows;
    s.dynamicMetadataDegrade;
    s.dynamicMetadataSerial;
    s.dynamicMetadataHeldFrames;
    s.dynamicMetadataTargetedNits;
};

// 读一份快照。门控有两道，取值都实测过，所以这里**不猜**：
//   · size：声明的 size 小于内核自己的 sizeof(FFF3FPSnapshot) ⇒ InvalidArgument；
//   · version：PlayerSession::GetSnapshot 要求 `output.version` 等于内核写死的那一个
//     （两棵树现在都是 20，见 PlayerSession.cpp 的 `output.version != 20`），
//     它是**快照自己的**版本号，与 PlayerApiVersion 是两条独立的水位线。填错 ⇒ InvalidArgument。
// ⇒ PickSnapshotVersion 在候选 {8, GetApiVersion()} 里试出第一个能读通的，之后全程用它，
//   并把选中的值打出来：读不到时先看这一行，别把"字段全 0"当成"内核没分类"。
bool ReadSnapshot(const FFF3FPHandle player, FFF3FPSnapshot& s, const std::uint32_t version,
    FFFResult& result) {
    s = {};
    s.size = sizeof(s);
    s.version = version;
    result = FFF3FP_GetSnapshot(player, &s);
    return result == FFFResult::Success;
}

std::uint32_t g_snapshotVersion = 0;  // 0 = 尚未试出

// 内核侧最后一次失败原因（Open/创建失败时打出来，免得只看到一个裸码）。
std::string LastError(const FFF3FPHandle player) {
    if (player == nullptr) return {};
    char buffer[1024]{};
    std::uint32_t needed = 0;
    FFF3FP_GetLastError(player, buffer, sizeof(buffer) - 1, &needed);
    return std::string(buffer);
}

bool PickSnapshotVersion(const FFF3FPHandle player, FFF3FPSnapshot& s, FFFResult& result) {
    if (g_snapshotVersion != 0) return ReadSnapshot(player, s, g_snapshotVersion, result);
    const std::uint32_t candidates[] = { 8u, FFF3FP_GetApiVersion() };
    for (const auto candidate : candidates) {
        FFFResult probe{};
        if (ReadSnapshot(player, s, candidate, probe)) {
            g_snapshotVersion = candidate;
            printf("快照 version 门控：%u 可读（sizeof=%llu）\n",
                candidate, static_cast<unsigned long long>(sizeof(FFF3FPSnapshot)));
            result = probe;
            return true;
        }
        printf("  (version=%u 读回 %d，继续试下一个候选)\n", candidate, static_cast<int>(probe));
    }
    result = FFFResult::InvalidArgument;
    return false;
}

// 必须是函数模板：只有当 `if constexpr` 的两个分支都依赖模板参数时，未被选中的那支
// 才不做成员名查找 —— 否则在 3fp/kernel 那份头上会直接编不过（这正是"字段只在 TP
// 树有"的实测证据）。
template <typename Snapshot>
void ShowRow(const char* when, const Snapshot& s) {
    printf("  %-9s frame=%-6lld pts=%-8lld hdrFormat=%-12s(%u) path=%-23s(%u) "
        "compat=%-18s(%u) peak=%-5u dynActive=%u fallback=%u effTarget=%-5u presented=%llu",
        when,
        static_cast<long long>(s.frameIndex), static_cast<long long>(s.framePts),
        FormatName(static_cast<std::uint32_t>(s.hdrFormat)),
        static_cast<std::uint32_t>(s.hdrFormat),
        PathName(static_cast<std::uint32_t>(s.hdrProcessingPath)),
        static_cast<std::uint32_t>(s.hdrProcessingPath),
        CompatName(s.compatibleHdrFormats).c_str(), s.compatibleHdrFormats,
        s.sourcePeakNits, s.dynamicHdrMetadataActive, s.hdrFallbackActive,
        s.effectiveTargetPeakNits,
        static_cast<unsigned long long>(s.presentedVideoFrames));
    if constexpr (HasSt2094Diagnostics<Snapshot>) {
        printf(" win=%u degrade=%u serial=%llu held=%llu dynTargeted=%u",
            s.dynamicMetadataWindows, s.dynamicMetadataDegrade,
            static_cast<unsigned long long>(s.dynamicMetadataSerial),
            static_cast<unsigned long long>(s.dynamicMetadataHeldFrames),
            s.dynamicMetadataTargetedNits);
    } else {
        printf(" (此内核快照无 ST 2094 诊断字段)");
    }
    printf("\n");
}

// 离线对照：transfer 是**喂进分类器的输入**，不是读到的媒体属性。
// 目的是把"HLG 基线 + Vivid SEI"会翻成哪个 format/path 摆清楚（不需要窗口）。
void OfflineClassifier() {
    printf("\n[离线] FFF3FP_EvaluateHdrProcessing：transfer 由本探针声明（非读数）\n");
    struct Case { const char* label; FFF3FPColorTransfer transfer; std::uint32_t vivid; std::uint32_t plus; };
    const Case cases[] = {
        { "transfer=Hlg + Vivid SEI ", FFF3FPColorTransfer::Hlg, 1u, 0u },
        { "transfer=Hlg 无 SEI      ", FFF3FPColorTransfer::Hlg, 0u, 0u },
        { "transfer=Pq  + Vivid SEI ", FFF3FPColorTransfer::Pq, 1u, 0u },
        { "transfer=Pq  无 SEI      ", FFF3FPColorTransfer::Pq, 0u, 0u },
    };
    for (const auto& c : cases) {
        FFF3FPHdrProcessingProbe probe{};
        probe.size = sizeof(probe);
        probe.version = 20;
        probe.transfer = c.transfer;
        probe.hdrVividMetadata = c.vivid;
        probe.hdr10PlusMetadata = c.plus;
        probe.displayPeakNits = 1000.0f;
        probe.displayFullFramePeakNits = 1000.0f;
        probe.targetPeakOverrideNits = 0.0f;
        const auto r = FFF3FP_EvaluateHdrProcessing(&probe);
        if (r != FFFResult::Success) {
            printf("  %s -> EvaluateHdrProcessing 返回 %d（内核或不含该导出）\n",
                c.label, static_cast<int>(r));
            continue;
        }
        printf("  %s -> hdrFormat=%-12s(%u) path=%-18s(%u) dyn=%u targetPeak=%u\n",
            c.label,
            FormatName(static_cast<std::uint32_t>(probe.outputFormat)),
            static_cast<std::uint32_t>(probe.outputFormat),
            PathName(static_cast<std::uint32_t>(probe.outputProcessingPath)),
            static_cast<std::uint32_t>(probe.outputProcessingPath),
            probe.outputDynamicMetadata, probe.outputTargetPeakNits);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: transfer_probe <媒体> [--seconds N] [--frames M]\n"
            L"  读 hdrFormat / hdrProcessingPath / ST 2094 诊断（分类域）。\n"
            L"  不读 AVColorTransferCharacteristic 原值：快照无 transfer 字段，\n"
            L"  FFF3FP_GetColorTransform 不存在，Evaluate* 的 transfer 是输入而非读数。\n");
        return 2;
    }
    int seconds = 3;
    int frames = 8;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (std::wstring(argv[i]) == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFTransferProbeWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFTransferProbeWnd", L"hdr class probe",
        WS_OVERLAPPEDWINDOW, 20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

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
    const auto createResult = FFF3FP_Create(&configuration, &player);
    if (createResult != FFFResult::Success || player == nullptr) {
        // 多半是"包含树与所链 DLL 不是同一份构建"：FFF3FP_Create 的 size/version 门控
        // 直接拒（头的 FFF3FPConfiguration 比 DLL 那份窄，或 version 不等）。
        printf("创建失败 result=%d —— 检查 /I 的树与所链 FFF.Native.lib 是否同一份构建\n",
            static_cast<int>(createResult));
        return 1;
    }
    printf("文件: %s\n", path.c_str());
    printf("API version=%u sizeof(FFF3FPSnapshot)=%llu sizeof(FFF3FPConfiguration)=%llu\n",
        FFF3FP_GetApiVersion(), static_cast<unsigned long long>(sizeof(FFF3FPSnapshot)),
        static_cast<unsigned long long>(sizeof(FFF3FPConfiguration)));
    const auto openResult = FFF3FP_Open(player, path.c_str());
    if (openResult != FFFResult::Success) {
        printf("Open 失败 result=%d last=%s\n", static_cast<int>(openResult),
            LastError(player).c_str());
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    FFF3FPSnapshot s{};
    FFFResult r{};
    if (!PickSnapshotVersion(player, s, r)) {
        printf("快照读不通：改用 Close/Destroy 退出（先看上面的 version 候选行）\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 1;
    }
    ShowRow("open后", s);

    FFF3FP_Play(player);
    Pump(300);
    printf("\n[逐帧] 分类域读数（每行 = 最近一个已渲染帧的快照）：\n");
    for (int i = 0; i < frames; ++i) {
        FFF3FPSnapshot row{};
        FFFResult rr{};
        if (!ReadSnapshot(player, row, g_snapshotVersion, rr)) {
            printf("  读快照失败 version=%u 返回 %d\n", g_snapshotVersion, static_cast<int>(rr));
            break;
        }
        ShowRow("播放中", row);
        Pump(static_cast<int>(seconds * 1000 / (frames > 0 ? frames : 1)));
    }
    FFF3FP_Pause(player);
    Pump(200);
    FFF3FPSnapshot tail{};
    FFFResult tr{};
    if (ReadSnapshot(player, tail, g_snapshotVersion, tr)) ShowRow("暂停后", tail);

    OfflineClassifier();

    printf("\n判读（只在 hdrFormat / hdrProcessingPath 这个域内；两张名表互不通用）：\n");
    printf("  · 逐帧行的看点：hdrFormat/path 停在哪儿，以及 peak 是否随帧摆。\n");
    printf("    peak 随帧摆 = 该素材的比特流动态峰值在驱动 sourcePeakNits；\n");
    printf("    peak 恒定 = 源里没有可用的动态峰值，读的是流级静态基线。\n");
    printf("  · hdrFormat=2 是 **Hdr10Plus**，不是 HLG —— 旧版按 transfer 码表念这个 2，\n");
    printf("    于是同一句“输出 2”在两个域里指向完全不同的东西，判读自然错位。\n");
    printf("  · 离线四行是**喂给分类器的输入**对照（不是读数）：SEI 位在 transfer 之前判，\n");
    printf("    所以 Vivid SEI=1 时 format 一律 HdrVivid(5)、path HdrVividDynamic(6)，\n");
    printf("    与喂 Hlg 还是 Pq 无关；无 SEI 时才是 Hlg->HlgDisplayMapped(3)、\n");
    printf("    Pq->StaticHdr10(1)。⇒ “format 翻成 HdrVivid 后落到 PQ(1)”这种说法，\n");
    printf("    在 hdrFormat 域（1=Hdr10）和 path 域（1=StaticHdr10）里都指不到同一件事，\n");
    printf("    要靠这份探针证伪/证实，必须先说清是哪个域。\n");
    printf("  · 边界：本探针**不能**证明“样本按哪条 EOTF 解码”——那要像素件\n");
    printf("    （hdr10plus_inject_pixel_probe 那一类）；它也不是任何修复的验收件。\n");
    printf("    peak 一列只是顺带：峰值/EOTF 的口径归 vivid_peak_probe / hdrpeak_probe，\n");
    printf("    本文件保证的只有一件事——format 与 path 各按自己的名表念，不跨域混读。\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
