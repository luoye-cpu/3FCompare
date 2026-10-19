// read_pixel_probe.cpp —— 读取播放中呈现帧的像素（线性 RGB），用于 HDR 传递函数 A/B 对拍。
// 用法：read_pixel_probe <media path> [--hdr]
// 复用 verify_image_mode.cpp 的窗口/会话框架（CPU 解码、真实窗口、真实 FFmpeg DLL）。
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <vector>
#include <cstdlib>
#include "3FP/Api/FFF.Player.Api.h"

namespace {
FFF3FPHandle g_player = nullptr;
std::atomic<bool> g_openCompleted{false};

void __cdecl OnEvent(void* context, FFF3FPEvent type, const char* detail) {
    (void)context; (void)detail;
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
    MSG message;
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: read_pixel_probe <media path> [--hdr]\n");
        return 2;
    }
    const char* path = argv[1];
    bool requestHdr = false;
    bool forceHdr = false;
    int atFrame = -1;
    int gridSize = 0;
    bool scanLines = false;
    bool requestVsr = false;
    bool requestGpu = false;
    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--hdr") == 0) requestHdr = true;
        // Capability control arm: forceHdrOutput outranks the display probe inside the
        // kernel, so a run that still cannot reach the 16-bit chain is telling us the
        // OS or the swap chain will not carry scRGB -- not that the source is 8-bit.
        if (std::strcmp(argv[i], "--force-hdr") == 0) forceHdr = true;
        // Two arms are only comparable on the SAME picture: seeking by frame index and
        // printing the published position makes that a witness instead of an assumption.
        // Measured without this: the same file, two passes, same coordinate read
        // 0.00000 and 0.39697.
        if (std::strcmp(argv[i], "--at-frame") == 0 && i + 1 < argc)
            atFrame = std::atoi(argv[++i]);
        if (std::strcmp(argv[i], "--grid") == 0 && i + 1 < argc)
            gridSize = std::atoi(argv[++i]);
        // 周期量法：沿一条水平线、一条垂直线各数"跨过本线均值的次数"。
        // 色度位移读错一档 ⇒ 图案周期差一倍，这是格均值看不到的那一维。
        if (std::strcmp(argv[i], "--scan") == 0) scanLines = true;
        // VSR 请求默认 Off 且是会话内的，所以不带这个旗标的历史臂从未走过 VP。
        if (std::strcmp(argv[i], "--vsr") == 0) requestVsr = true;
        // VSR 的前置里有"源面必须带 D3D11_BIND_DECODER"，软解上传面没有 ⇒ 想真正走到 VP
        // 必须换硬解码这一臂；不带这个旗标时 reason 只会是 UnsupportedSourceFormat(5)。
        if (std::strcmp(argv[i], "--gpu") == 0) requestGpu = true;
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpReadPixelWindow";
    if (RegisterClassW(&windowClass) == 0) { std::printf("RegisterClassW failed\n"); return 1; }
    HWND window = CreateWindowExW(0, L"3fpReadPixelWindow", L"3fp read pixel",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, 960, 540, nullptr, nullptr,
        GetModuleHandleW(nullptr), nullptr);
    if (window == nullptr) { std::printf("CreateWindowExW failed\n"); return 1; }
    Pump(200);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = requestGpu ? FFF3FPDecodeMode::Gpu : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = requestHdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.forceHdrOutput = forceHdr ? 1u : 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    const auto created = FFF3FP_Create(&configuration, &g_player);
    if (created != FFFResult::Success || g_player == nullptr) { std::printf("Create: %d\n", static_cast<int>(created)); return 1; }
    const auto opened = FFF3FP_Open(g_player, path);
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (!g_openCompleted) { std::printf("Open timed out\n"); return 1; }
    Pump(300);

    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(g_player, &snapshot);
    std::printf("open: hdrFormat=%u hdrPath=%u dyn=%u peak=%u outBits=%u %ux%u\n",
        static_cast<unsigned>(snapshot.hdrFormat), static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive, snapshot.sourcePeakNits,
        snapshot.videoOutputBitDepth, snapshot.videoWidth, snapshot.videoHeight);

    if (requestVsr) {
        // 请求要发在 Play 之前：暂停态下改旗标不会重绘，状态读数会停在上一帧。
        const auto setVsr = FFF3FP_SetVideoSuperResolution(g_player,
            FFF3FPVideoSuperResolution::Auto);
        std::printf("vsr_set: rc=%d\n", static_cast<int>(setVsr));
    }
    FFF3FP_Play(g_player);
    // 等到呈现若干帧后再读，保证像素/元数据状态稳定。
    Pump(1500);
    FFF3FP_GetSnapshot(g_player, &snapshot);
    std::printf("play: hdrFormat=%u hdrPath=%u dyn=%u peak=%u outBits=%u presented=%llu\n",
        static_cast<unsigned>(snapshot.hdrFormat), static_cast<unsigned>(snapshot.hdrProcessingPath),
        snapshot.dynamicHdrMetadataActive, snapshot.sourcePeakNits, snapshot.videoOutputBitDepth,
        static_cast<unsigned long long>(snapshot.presentedVideoFrames));

    // 钉到同一张画面（两臂可比的前提，打印出来当见证）。
    // 必须先暂停再读：只 Seek 不暂停的话，Pump 期间播放会继续走，
    // 实测两趟的 pos100ns 差到 0.5 s 与 100 ms 不等、presented 48~125 —— 那种类比是假的。
    if (atFrame >= 0) {
        const auto seeked = FFF3FP_SeekFrame(g_player, static_cast<std::uint32_t>(atFrame));
        Pump(300);
        const auto paused = FFF3FP_Pause(g_player);
        Pump(400);
        FFF3FP_GetSnapshot(g_player, &snapshot);
        std::printf("seek: rc=%d frame=%d pauseRc=%d pos100ns=%lld presented=%llu state=%d frameIndex=%lld\n",
            static_cast<int>(seeked), atFrame, static_cast<int>(paused),
            static_cast<long long>(snapshot.position100ns),
            static_cast<unsigned long long>(snapshot.presentedVideoFrames),
            static_cast<int>(snapshot.state), static_cast<long long>(snapshot.frameIndex));
    }

    // ⚠ ReadVideoPixel 的坐标域是后台缓冲/交换链尺寸（不是片源分辨率）——
    //   见 v0.2.5 的 PixelReadback 教训。窗口客户区 ≈ 960×540 内取固定点最稳。
    const std::uint32_t points[][2] = {
        { 480, 250 },
        { 240, 250 },
        { 720, 250 },
        { 480, 120 },
        { 480, 400 },
    };
    for (const auto& point : points) {
        FFF3FPVideoPixelProbe probe{};
        probe.size = sizeof(probe);
        probe.version = 20;
        probe.x = point[0];
        probe.y = point[1];
        const auto result = FFF3FP_ReadVideoPixel(g_player, &probe);
        if (result != FFFResult::Success) {
            std::printf("  (%u,%u) read failed: %d\n", point[0], point[1], static_cast<int>(result));
            continue;
        }
        std::printf("  (%4u,%4u) outBits=%u mode=%u scl=%u R=%.5f G=%.5f B=%.5f A=%.3f\n",
            point[0], point[1], probe.outputBitDepth, static_cast<unsigned>(probe.colorMode),
            static_cast<unsigned>(probe.scalingMode),
            probe.red, probe.green, probe.blue, probe.alpha);
    }
    // 网格：一次 ReadVideoPixelRegion 拿整块，按格算 R/G/B 均值。
    // 几何/定点错会表现为"格的相位对不上"，而两臂的均值分布仍然相近 —— 所以取格均值
    // 而不是逐像素相等：产品着色器是 Lanczos 上采样，参考件是 FFmpeg 的 neighbor 扩展，
    // 块边界本就不同，只有相位/周期能拿来判。
    if (gridSize > 1) {
        RECT client{};
        if (!GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0) {
            std::printf("grid: 客户区不可用\n");
        } else {
            const auto x0 = static_cast<std::uint32_t>(client.right / 10);
            const auto y0 = static_cast<std::uint32_t>(client.bottom / 10);
            const auto w = (static_cast<std::uint32_t>(client.right) * 8u / 10u) & ~1u;
            const auto h = (static_cast<std::uint32_t>(client.bottom) * 8u / 10u) & ~1u;
            std::vector<float> pixels(static_cast<std::size_t>(w) * h * 4u, 0.0f);
            std::uint32_t outBits = 0;
            const auto rc = FFF3FP_ReadVideoPixelRegion(g_player, x0, y0, w, h, pixels.data(),
                static_cast<std::uint32_t>(pixels.size()), &outBits);
            if (rc != FFFResult::Success) {
                std::printf("grid: read failed rc=%d rect=%ux%u@%u,%u\n",
                    static_cast<int>(rc), w, h, x0, y0);
            } else {
                std::printf("grid: outBits=%u rect=%ux%u@%u,%u cells=%d\n",
                    outBits, w, h, x0, y0, gridSize);
                const auto cw = w / static_cast<std::uint32_t>(gridSize);
                const auto ch = h / static_cast<std::uint32_t>(gridSize);
                for (int cy = 0; cy < gridSize; ++cy) {
                    std::printf("G%d", cy);
                    for (int cx = 0; cx < gridSize; ++cx) {
                        double sum[3] = {0.0, 0.0, 0.0};
                        std::uint64_t n = 0;
                        const auto yBegin = static_cast<std::uint32_t>(cy) * ch;
                        const auto yEnd = static_cast<std::uint32_t>(cy + 1) * ch;
                        const auto xBegin = static_cast<std::uint32_t>(cx) * cw;
                        const auto xEnd = static_cast<std::uint32_t>(cx + 1) * cw;
                        for (auto y = yBegin; y < yEnd; ++y) {
                            for (auto x = xBegin; x < xEnd; ++x) {
                                const auto base = (static_cast<std::size_t>(y) * w + x) * 4u;
                                for (int c = 0; c < 3; ++c) sum[c] += pixels[base + c];
                                ++n;
                            }
                        }
                        if (n == 0) { std::printf(" |"); continue; }
                        std::printf(" |%.4f,%.4f,%.4f", sum[0] / n, sum[1] / n, sum[2] / n);
                    }
                    std::printf("\n");
                }
            }
        }
    }
    if (requestVsr) {
        // 读数放在所有读点之后：此时 DrawCachedVideo 已经按 Auto 跑过，reason/active
        // 才是"这一帧到底走了哪条路"的见证，而不是上一次请求的残留。
        FFF3FPVideoSuperResolutionStatus status{};
        status.size = sizeof(status);
        status.version = 20;
        const auto gotVsr = FFF3FP_GetVideoSuperResolutionStatus(g_player, &status);
        std::printf("vsr: getRc=%d requested=%u nvidia=%u active=%u rejected=%u reason=%u "
            "src=%ux%u tgt=%ux%u\n", static_cast<int>(gotVsr),
            static_cast<unsigned>(status.requested), status.nvidiaAdapter, status.active,
            status.driverRejected, static_cast<unsigned>(status.reason),
            status.sourceWidth, status.sourceHeight, status.targetWidth, status.targetHeight);
    }

    if (scanLines) {
        RECT client{};
        if (!GetClientRect(window, &client) || client.right <= 8 || client.bottom <= 8) {
            std::printf("scan: 客户区不可用\n");
        } else {
            const auto x0 = static_cast<std::uint32_t>(client.right / 10);
            const auto y0 = static_cast<std::uint32_t>(client.bottom / 10);
            const auto w = static_cast<std::uint32_t>(client.right) * 8u / 10u;
            const auto h = static_cast<std::uint32_t>(client.bottom) * 8u / 10u;
            std::vector<float> row(static_cast<std::size_t>(w) * 4u, 0.0f);
            std::vector<float> col(static_cast<std::size_t>(h) * 4u, 0.0f);
            std::uint32_t outBits = 0;
            const auto rcH = FFF3FP_ReadVideoPixelRegion(g_player, x0, y0 + h / 2u, w, 1u,
                row.data(), static_cast<std::uint32_t>(row.size()), &outBits);
            const auto rcV = FFF3FP_ReadVideoPixelRegion(g_player, x0 + w / 2u, y0, 1u, h,
                col.data(), static_cast<std::uint32_t>(col.size()), &outBits);
            std::printf("scan: rcH=%d rcV=%d outBits=%u len=%ux%u\n",
                static_cast<int>(rcH), static_cast<int>(rcV), outBits, w, h);
            auto transitions = [](const std::vector<float>& px, int channel) -> int {
                const auto count = px.size() / 4u;
                if (count < 3) return -1;
                double mean = 0.0;
                for (std::size_t i = 0; i < count; ++i) mean += px[i * 4u + channel];
                mean /= static_cast<double>(count);
                int crossings = 0;
                int previous = -1;
                for (std::size_t i = 0; i < count; ++i) {
                    const int now = px[i * 4u + channel] >= mean ? 1 : 0;
                    if (previous >= 0 && now != previous) ++crossings;
                    previous = now;
                }
                return crossings;
            };
            if (rcH == FFFResult::Success)
                std::printf("scan: H B=%d R=%d G=%d\n", transitions(row, 2), transitions(row, 0),
                    transitions(row, 1));
            if (rcV == FFFResult::Success)
                std::printf("scan: V B=%d R=%d G=%d\n", transitions(col, 2), transitions(col, 0),
                    transitions(col, 1));
        }
    }

    Pump(200);
    FFF3FP_Close(g_player);
    return 0;
}
