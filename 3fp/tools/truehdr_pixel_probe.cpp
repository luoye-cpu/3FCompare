// TrueHDR（NVIDIA 视频处理器私有流扩展自动 HDR）像素级验证。
//
// 判据（双臂：关 -> 开，同一进程内完成；合成条夹具内容恒定，跨臂差图成立）：
//   1. 噪声地板：同一臂暂停后连读两遍必须逐字节相同。不成立时后面所有差值都不可归因。
//   2. 路径判据：开臂像素应与关臂**不同**（否则说明只置了 active 标志，扩展没真干活）。
//   3. 平色区生纹：关臂平色块内部取值，在开臂应散成多个值（驱动做 SDR->HDR 转换时引入
//      纹理/抖动；这是"换了条路"最强的单项证据——纯通道替换不会在平色区内部生纹）。
//   4. 亮度方向：开臂平色区均值应**高于**关臂（自动 HDR 的意义就是把 SDR 白推到更高尼特）。
//      方向反了要报警——那意味着映射被套了两次。
//   5. 超 SDR 白比例：开臂应出现超过关臂最大值的样本（同样以"超过多少"报告，不设硬门——
//      驱动的映射曲线是它自己的，本程序只报告方向与量级）。
//
// 前置条件（内核门，逐帧判定，任一不满足静默不生效）：
//   · NVIDIA 适配器；驱动对 TrueHDR GUID 的能力探测读回 1
//   · SDR 源（cachedVideoSettings_.transfer == 0）
//   · 输出路径已是 HDR 契约（actualMode == MapToHdr 且 swapHdr_，即 10 位 HDR/scRGB 链）
//   · 硬解纹理（CanUseVideoProcessorForUpscale：D3D11_BIND_DECODER）
// 所以本探针固定 MapToHdr + D3D11 硬解 + 10bit SDR 夹具（默认 env 下走 16bit scRGB 链）。
//
// 边界（照抄 10-19 的边界，不算新结论）：读回只覆盖到合成器/交换链为止，屏上最终
// tone map 与显示器自身处理不在本臂内。
//
// 用法：truehdr_pixel_probe <媒体> [--sr-window WxH] [--dump DIR]

#include <windows.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

using Microsoft::WRL::ComPtr;

namespace {
bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
}
// 阶段文本的唯一出口：rejectAt 在 draw 路径上跑（ApplyTrueHdr 失败后 draw 照常
// 回落 VSR/shader 路径、整体返 Success），写的是 renderer 私有 lastError_，
// 会话的 FFF3FP_GetLastError 看不到。内核日志行（含失败阶段）经这个 sink 出来。
// 与 vsr_probe 同一模式：DriverRejected 分不清"驱动不支持"还是"我们视图搭错"，
// 只有这行能分辨。参数序 (context, line)，见 FFF3FPLogCallback。
void __cdecl LogCallback(void*, const char* line) noexcept {
    if (line != nullptr && std::strstr(line, "automatic HDR") != nullptr)
        printf("[kernel-log] %s\n", line);
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

struct Capture {
    bool ok = false;
    std::uint32_t width = 0, height = 0, bitDepth = 0;
    std::vector<float> pixels;   // RGBA float32
};

Capture Grab(FFF3FPHandle player, const std::uint32_t width, const std::uint32_t height) {
    Capture out;
    if (width == 0 || height == 0) return out;
    out.width = width;
    out.height = height;
    out.pixels.assign(static_cast<std::size_t>(width) * height * 4, 0.0f);
    out.ok = FFF3FP_ReadVideoPixelRegion(player, 0, 0, width, height, out.pixels.data(),
        static_cast<std::uint32_t>(out.pixels.size()), &out.bitDepth) == FFFResult::Success;
    return out;
}

/// 直接问 Windows：这个显示器现在是否处于 HDR 模式。TrueHDR 的输出契约要求呈现链是 HDR，
/// 显示器没开 HDR 时 swapHdr_ 不成立，内核会静默跳过——这一行让日志自证环境。
void ReportDisplayHdr(const HWND window) {
    ComPtr<IDXGIFactory6> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) { printf("  (无 DXGI 工厂)\n"); return; }
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW info{}; info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) { printf("  (无监视器信息)\n"); return; }
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        for (UINT output = 0;; ++output) {
            ComPtr<IDXGIOutput> candidate;
            if (adapter->EnumOutputs(output, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{};
            if (FAILED(candidate->GetDesc(&desc))) continue;
            if (wcscmp(desc.DeviceName, info.szDevice) != 0) continue;
            ComPtr<IDXGIOutput6> output6;
            if (SUCCEEDED(candidate.As(&output6))) {
                DXGI_OUTPUT_DESC1 desc1{};
                if (SUCCEEDED(output6->GetDesc1(&desc1))) {
                    printf("  显示器 %ls: HDR=%s  maxLum=%.0f nits  bits=%u\n",
                        desc1.DeviceName,
                        desc1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? "开" : "关",
                        desc1.MaxLuminance, desc1.BitsPerColor);
                }
            }
            return;
        }
    }
}

void DumpBlock(const std::wstring& dir, const wchar_t* name, const Capture& capture) {
    if (dir.empty() || !capture.ok) return;
    const auto bin = std::filesystem::path(dir) / name;
    std::ofstream ofs(bin, std::ios::binary | std::ios::trunc);
    ofs.write(reinterpret_cast<const char*>(capture.pixels.data()),
        static_cast<std::streamsize>(capture.pixels.size() * sizeof(float)));
    ofs.close();
    auto meta = std::wstring(bin.wstring());
    meta += L".meta";
    std::ofstream mf(meta);
    mf << "width=" << capture.width << "\nheight=" << capture.height
       << "\nbitDepth=" << capture.bitDepth << "\nlayout=rgba_float32_rowmajor_topdown\n";
}

const char* ReasonText(const FFF3FPVideoAutoHdrReason reason) {
    switch (reason) {
        case FFF3FPVideoAutoHdrReason::None: return "None(已用)";
        case FFF3FPVideoAutoHdrReason::NotRequested: return "NotRequested(未请求)";
        case FFF3FPVideoAutoHdrReason::NotNvidiaAdapter: return "NotNvidiaAdapter(非 NVIDIA)";
        case FFF3FPVideoAutoHdrReason::NotSupportedByDriver: return "NotSupportedByDriver(驱动探测未过)";
        case FFF3FPVideoAutoHdrReason::NotSdrSource: return "NotSdrSource(源不是 SDR)";
        case FFF3FPVideoAutoHdrReason::OutputPathNotHdr: return "OutputPathNotHdr(输出链不是 HDR)";
        case FFF3FPVideoAutoHdrReason::UnsupportedSourceFormat: return "UnsupportedSourceFormat(源格式不进 VP)";
        case FFF3FPVideoAutoHdrReason::DriverRejected: return "DriverRejected(驱动拒绝/Blt 失败,已闩)";
    }
    return "(未知)";
}

// 时点状态打印。抓取（ReadVideoPixelRegion → DrawCachedVideo）本身会重走一遍完整
// 绘制路径、重跑一遍 ApplyTrueHdr 门，所以"最终状态"只反映最后一次评估，不能代表
// 抓到的像素来自哪条路。三个时点对照才能归因：
//   Redraw 后 / on1 抓取后 / on2 抓取后。
void PrintTrueHdrStatus(FFF3FPHandle player, const char* label) {
    FFF3FPVideoAutoHdrStatus status{};
    status.size = sizeof(status);
    status.version = 20;
    if (FFF3FP_GetVideoAutoHdrStatus(player, &status) != FFFResult::Success) {
        printf("  [%s] 状态读取失败\n", label);
        return;
    }
    printf("  [%s] requested=%d active=%d rejected=%d reason=%s | nvidia=%d src=%ux%u out=%ux%u\n",
        label, static_cast<int>(status.requested), status.active ? 1 : 0,
        status.driverRejected ? 1 : 0, ReasonText(status.reason),
        status.nvidiaAdapter ? 1 : 0, status.sourceWidth, status.sourceHeight,
        status.outputWidth, status.outputHeight);
}

// 两个像素的三通道最大差是否在容差内。平色掩码的邻居检查用；
// 刻意做成自由函数而不是嵌套 lambda——MSVC 对"lambda 内再定义 const auto lambda"
// 直接报 C2513（本轮实测），而平色判据本身不需要任何捕获。
bool PixelNear(const Capture& capture, const std::size_t a, const std::size_t b, const float tolerance) {
    if (a >= capture.pixels.size() / 4 || b >= capture.pixels.size() / 4) return false;
    for (auto c = 0u; c < 3u; ++c) {
        if (std::fabs(capture.pixels[a * 4 + c] - capture.pixels[b * 4 + c]) > tolerance)
            return false;
    }
    return true;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    // 只用 printf，不混 wprintf：stdout 流向被窄字符占用后 wprintf 会整段静默丢弃（10-19 实测丢过 15 行诊断）。
    if (argc < 2) { printf("用法: truehdr_pixel_probe <媒体> [--sr-window WxH] [--dump DIR]\n"); return 2; }
    unsigned windowWidth = 1024, windowHeight = 600;
    std::wstring dumpDir;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--sr-window" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%ux%u", &windowWidth, &windowHeight);
        else if (std::wstring(argv[i]) == L"--dump" && i + 1 < argc)
            dumpDir = argv[++i];
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFTrueHdrPixelWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFTrueHdrPixelWnd", L"truehdr", WS_OVERLAPPEDWINDOW,
        20, 20, static_cast<int>(windowWidth), static_cast<int>(windowHeight),
        nullptr, nullptr, wc.hInstance, nullptr);

    printf("== 显示器 HDR 状态（Windows 报告）==\n");
    ReportDisplayHdr(window);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    // TrueHDR 与 VSR 同门槛：源纹理必须带 D3D11_BIND_DECODER，只有硬解产出
    // （CanUseVideoProcessorForUpscale 测的就是这个）。软解臂实测"什么都没变"其实是正确拒绝。
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;
    // 请求 MapToHdr：内核只在"输出契约已经是 HDR"时才允许 TrueHDR 落地
    // （actualMode_ == MapToHdr && swapHdr_）。默认 env 下 10bit SDR 源升 16bit scRGB 链。
    configuration.colorMode = FFF3FPColorMode::MapToHdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    // 播放在此之前注册：开头几帧就可能 rejectAt，阶段行要全程在场。
    FFF3FP_SetLogCallback(&LogCallback, nullptr);
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    // ── 关臂 ────────────────────────────────────────────────────────────────
    FFF3FP_SetVideoAutoHdr(player, FFF3FPVideoAutoHdr::Off);
    FFF3FP_Play(player);
    Pump(1800);
    // 会话状态在播放中读：Open 之后、首帧之前的快照还是初始值（实际色彩/outBits
    // 会显示成回退态），那正是 10-19 读数矛盾的来源。播放起来后才是链路的真实选择。
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(player, &snapshot);
    const auto width = snapshot.videoWidth;
    const auto height = snapshot.videoHeight;
    printf("\n== 会话状态（播放中，TrueHDR=Off）==\n");
    printf("  源=%ux%u  窗口=%ux%u  请求色彩=%d  实际色彩=%d  outBits=%u\n",
        width, height, windowWidth, windowHeight,
        static_cast<int>(snapshot.requestedColorMode),
        static_cast<int>(snapshot.actualColorMode), snapshot.videoOutputBitDepth);
    if (width == 0 || height == 0) { printf("无尺寸信息\n"); FFF3FP_Destroy(player); return 1; }

    FFF3FP_Pause(player);
    Pump(700);
    const auto off1 = Grab(player, width, height);
    Pump(400);
    const auto off2 = Grab(player, width, height);

    // ── 开臂 ────────────────────────────────────────────────────────────────
    FFF3FP_SetVideoAutoHdr(player, FFF3FPVideoAutoHdr::Auto);
    FFF3FP_Redraw(player);
    PrintTrueHdrStatus(player, "Redraw 后");
    Pump(1800);
    const auto on1 = Grab(player, width, height);
    PrintTrueHdrStatus(player, "on1 抓取后");
    Pump(400);
    const auto on2 = Grab(player, width, height);

    printf("\n== TrueHDR 状态（on2 抓取后，本次抓取各自重跑过门）==\n");
    PrintTrueHdrStatus(player, "on2 抓取后");
    // 失败阶段不在会话错误里读：rejectAt 写 renderer 私有 lastError_，draw 路径
    // 整体返 Success，会话 Fail 不会被触发。阶段行由上面注册的 log sink 直接打出
    // （[kernel-log] NVIDIA automatic HDR step failed: <阶段>）。reason 只告诉你
    // "驱动拒绝"，阶段才告诉你拒绝在哪一步。

    printf("\n== 像素回读 ==\n");
    printf("  off: ok=%d bits=%u   on: ok=%d bits=%u\n",
        off1.ok ? 1 : 0, off1.bitDepth, on1.ok ? 1 : 0, on1.bitDepth);
    DumpBlock(dumpDir, L"truehdr_off1.f32", off1);
    DumpBlock(dumpDir, L"truehdr_off2.f32", off2);
    DumpBlock(dumpDir, L"truehdr_on1.f32", on1);
    DumpBlock(dumpDir, L"truehdr_on2.f32", on2);

    if (!off1.ok || !on1.ok) {
        printf("\n像素回读失败 (off=%d on=%d) —— 不判\n", off1.ok ? 1 : 0, on1.ok ? 1 : 0);
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 6;
    }

    // 1. 噪声地板：同一臂暂停后两遍必须逐字节相同。这是所有跨臂差值的归因前提。
    auto byteIdentical = [](const Capture& a, const Capture& b) {
        return a.ok && b.ok && a.pixels.size() == b.pixels.size() &&
            std::memcmp(a.pixels.data(), b.pixels.data(),
                        a.pixels.size() * sizeof(float)) == 0;
    };
    const bool offFloor = byteIdentical(off1, off2);
    const bool onFloor = byteIdentical(on1, on2);
    printf("\n== 判据 1：噪声地板（同臂两遍逐字节相同）==\n");
    printf("  关臂=%s  开臂=%s\n", offFloor ? "相同" : "不同（地板不成立!）", onFloor ? "相同" : "不同（地板不成立!）");
    if (!offFloor || !onFloor)
        printf("  ⚠ 地板不成立：下面所有跨臂差值不可归因给 TrueHDR，只能当噪声读\n");

    const auto count = static_cast<std::size_t>(width) * height;
    const auto channels = 3u;

    // 2/3/4/5 都基于 off1 与 on1 的全图通道统计。
    double sumOff = 0, sumOn = 0, maxDiff = 0, sumDiff = 0;
    std::size_t differing = 0;
    for (std::size_t i = 0; i < count; ++i) {
        const auto base = i * 4;
        for (auto c = 0u; c < channels; ++c) {
            const auto a = off1.pixels[base + c];
            const auto b = on1.pixels[base + c];
            sumOff += a; sumOn += b;
            const auto d = std::fabs(a - b);
            sumDiff += d;
            if (d > maxDiff) maxDiff = d;
            if (d > 0.004f) ++differing;
        }
    }
    const auto samples = static_cast<double>(count) * channels;
    printf("\n== 判据 2：路径（全图差值 off1 vs on1）==\n");
    printf("  平均亮度: 关=%.4f  开=%.4f  (差 %+.4f)\n",
        sumOff / samples, sumOn / samples, (sumOn - sumOff) / samples);
    printf("  像素差异: 平均=%.5f  最大=%.4f  超 0.004 阈值=%.2f%%\n",
        sumDiff / samples, maxDiff, 100.0 * differing / samples);
    const bool pathChanged = (sumDiff / samples) > 0.0005;
    printf("  判定: %s\n", pathChanged ? "TrueHDR **确实改变了输出**（非仅置标志）"
                                       : "输出未变 —— 结合上面的 reason 判断是拒绝还是没请求");

    // 3. 平色区生纹：从 off1 提平色掩码（"像素与其右/下邻居三通道最大差 < 1/1024"的宽松版）。
    //    合成条夹具的平色块在关臂是数学常量；开臂若散成多个值，只能来自驱动转换。
    auto flatMaskIndex = [&](const Capture& capture, const std::size_t index) {
        const auto row = index / width;
        const auto col = index % width;
        if (col + 1 < width && !PixelNear(capture, index, index + 1, 0.001f)) return false;
        if (row + 1 < height && !PixelNear(capture, index, index + width, 0.001f)) return false;
        return true;
    };
    std::size_t flatCount = 0;
    double flatSumOff = 0, flatSumOn = 0, flatMaxDiff = 0, flatSumSqOn = 0;
    for (std::size_t i = 0; i < count; ++i) {
        if (!flatMaskIndex(off1, i)) continue;
        ++flatCount;
        const auto base = i * 4;
        for (auto c = 0u; c < channels; ++c) {
            const auto a = off1.pixels[base + c];
            const auto b = on1.pixels[base + c];
            flatSumOff += a; flatSumOn += b;
            flatSumSqOn += static_cast<double>(b) * b;
            const auto d = std::fabs(a - b);
            if (d > flatMaxDiff) flatMaxDiff = d;
        }
    }
    printf("\n== 判据 3/4/5：平色区 ==\n");
    if (flatCount == 0) {
        printf("  掩码为空（夹具没有平色区）—— 判据 3/4/5 不判\n");
    } else {
        const auto flatSamples = static_cast<double>(flatCount) * channels;
        const auto flatMeanOff = flatSumOff / flatSamples;
        const auto flatMeanOn = flatSumOn / flatSamples;
        const auto flatVar = flatSumSqOn / flatSamples - flatMeanOn * flatMeanOn;
        printf("  平色像素=%zu (%.1f%%)  掩码内平均差=%.5f  掩码内最大差=%.4f\n",
            flatCount, 100.0 * flatCount / count, flatMaxDiff > 0 ? flatMaxDiff : 0.0, flatMaxDiff);
        printf("  平色区均值: 关=%.4f  开=%.4f  (比 %+.4f, %+.2f%%)\n",
            flatMeanOff, flatMeanOn, flatMeanOn - flatMeanOff,
            flatMeanOff > 0 ? 100.0 * (flatMeanOn - flatMeanOff) / flatMeanOff : 0.0);
        printf("  开臂平色区内部方差=%.6f（std=%.5f）\n", flatVar, std::sqrt(flatVar > 0 ? flatVar : 0.0));
        const bool textured = flatMaxDiff > 0.004f;
        printf("  判据3 判定: %s\n", textured
            ? "平色区**生了纹**（驱动转换在常量区引入纹理/抖动）"
            : "平色区没生纹 —— 要么常量映射（亮度整体等比抬升也算改变，见判据2），要么没生效");
        const bool brighter = flatMeanOn > flatMeanOff;
        printf("  判据4 判定: %s\n", brighter
            ? "开臂更亮（SDR 白被推到更高尼特，方向符合自动 HDR 语义）"
            : "开臂不变或更暗 —— 方向可疑，需排查双重映射");
    }

    // 5. 超 SDR 白比例：on1 中超过 off1 全局最大值的样本占比。
    float offMax = 0.0f;
    for (std::size_t i = 0; i < count; ++i)
        for (auto c = 0u; c < channels; ++c)
            offMax = (std::max)(offMax, off1.pixels[i * 4 + c]);
    std::size_t aboveWhite = 0;
    for (std::size_t i = 0; i < count; ++i)
        for (auto c = 0u; c < channels; ++c)
            if (on1.pixels[i * 4 + c] > offMax) ++aboveWhite;
    printf("\n== 判据 5：超过关臂最大亮度（%.4f）的样本 ==\n", offMax);
    printf("  %zu / %zu 通道样本 (%.2f%%)\n", aboveWhite, count * channels,
        100.0 * aboveWhite / (count * channels));
    printf("  注: 读回单位按 10-19 标定 v×80nits；offMax 对应 SDR 峰值(100nits 档)\n");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    // 退出码照旧只表示"这一臂跑没跑成"；判定看日志，不看退出码（到货验收的老规矩）。
    return 0;
}
