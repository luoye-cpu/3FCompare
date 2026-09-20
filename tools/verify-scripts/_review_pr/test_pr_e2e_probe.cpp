// FFF_Project PR end-to-end verification harness.
// Loads FFF.Native.dll directly (no managed layer), creates a real output
// window, and exercises the API surface touched by the PR:
//   - FFF3FPConfiguration.preferredAdapterIndex (API 15)
//   - FFF3FP_GetRenderTargetInfo
//   - FFF3FP_ReadVideoPixelRegion
//   - FFF3FP_SetViewTransform
// Round 2 adds regression assertions for the review fixes:
//   (f) GetRenderTargetInfo must NOT report an all-zero Success when no
//       swapchain exists yet.
//   (g) ReadPixelRegion must reject out-of-bounds regions instead of
//       silently truncating them.
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <thread>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

using FnGetApiVersion = std::uint32_t(__cdecl*)() noexcept;
using FnCreate = FFFResult(__cdecl*)(const FFF3FPConfiguration*, FFF3FPHandle*) noexcept;
using FnOpen = FFFResult(__cdecl*)(FFF3FPHandle, const char*) noexcept;
using FnPlay = FFFResult(__cdecl*)(FFF3FPHandle) noexcept;
using FnPause = FFFResult(__cdecl*)(FFF3FPHandle) noexcept;
using FnClose = FFFResult(__cdecl*)(FFF3FPHandle) noexcept;
using FnDestroy = void(__cdecl*)(FFF3FPHandle) noexcept;
using FnSetVolume = FFFResult(__cdecl*)(FFF3FPHandle, float, std::uint32_t) noexcept;
using FnSetViewTransform = FFFResult(__cdecl*)(FFF3FPHandle, float, float, float) noexcept;
using FnGetSnapshot = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPSnapshot*) noexcept;
using FnGetRenderTargetInfo = FFFResult(__cdecl*)(FFF3FPHandle, FFF3FPRenderTargetInfo*) noexcept;
using FnReadVideoPixelRegion = FFFResult(__cdecl*)(FFF3FPHandle, std::uint32_t, std::uint32_t,
    std::uint32_t, std::uint32_t, float*, std::uint32_t, std::uint32_t*) noexcept;

struct Api {
    FnGetApiVersion getApiVersion = nullptr;
    FnCreate create = nullptr;
    FnOpen open = nullptr;
    FnPlay play = nullptr;
    FnPause pause = nullptr;
    FnClose close = nullptr;
    FnDestroy destroy = nullptr;
    FnSetVolume setVolume = nullptr;
    FnSetViewTransform setViewTransform = nullptr;
    FnGetSnapshot getSnapshot = nullptr;
    FnGetRenderTargetInfo getRenderTargetInfo = nullptr;
    FnReadVideoPixelRegion readVideoPixelRegion = nullptr;
};

const char* Name(FFFResult r) noexcept {
    switch (r) {
        case FFFResult::Success: return "Success";
        case FFFResult::InvalidArgument: return "InvalidArgument";
        case FFFResult::InvalidState: return "InvalidState";
        case FFFResult::BufferTooSmall: return "BufferTooSmall";
        case FFFResult::NativeFailure: return "NativeFailure";
        case FFFResult::FfmpegFailure: return "FfmpegFailure";
        case FFFResult::DeviceFailure: return "DeviceFailure";
        case FFFResult::NotSupported: return "NotSupported";
        default: return "Unknown";
    }
}

// ---- OutputDebugStringA capture (DBWIN) -------------------------------------
std::atomic<bool> g_dbgStop{false};
std::atomic<int> g_dbgLines{0};

void DbwinThread() noexcept {
    HANDLE ready = CreateEventA(nullptr, FALSE, TRUE, "DBWIN_BUFFER_READY");
    HANDLE dataReady = CreateEventA(nullptr, FALSE, FALSE, "DBWIN_DATA_READY");
    HANDLE buffer = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        0, 4096, "DBWIN_BUFFER");
    if (ready == nullptr || dataReady == nullptr || buffer == nullptr) return;
    auto* view = static_cast<char*>(MapViewOfFile(buffer, FILE_MAP_READ, 0, 0, 0));
    if (view == nullptr) return;
    while (!g_dbgStop.load()) {
        SetEvent(ready);
        if (WaitForSingleObject(dataReady, 200) != WAIT_OBJECT_0) continue;
        const auto pid = *reinterpret_cast<DWORD*>(view);
        if (pid != GetCurrentProcessId()) continue;
        const char* text = view + sizeof(DWORD);
        if (text[0] != '\0') {
            printf("    [dbg] %s", text);
            if (text[0] != '\0' && text[strlen(text) - 1] != '\n') printf("\n");
            fflush(stdout);
            g_dbgLines.fetch_add(1);
        }
    }
    UnmapViewOfFile(view);
}

// ---- output window on its own thread ---------------------------------------
std::atomic<HWND> g_hwnd{nullptr};

LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) noexcept {
    if (m == WM_DESTROY) PostQuitMessage(0);
    return DefWindowProcA(h, m, w, l);
}

void WindowThread() noexcept {
    WNDCLASSA wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleA(nullptr);
    wc.lpszClassName = "FffPrTestWindow";
    RegisterClassA(&wc);
    HWND h = CreateWindowExA(0, "FffPrTestWindow", "PR test", WS_OVERLAPPEDWINDOW,
        60, 60, 1280 + 16, 720 + 39, nullptr, nullptr, wc.hInstance, nullptr);
    g_hwnd.store(h);
    if (h == nullptr) return;
    ShowWindow(h, SW_SHOW);
    MSG msg{};
    while (GetMessageA(&msg, nullptr, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageA(&msg); }
}

int g_failures = 0;

void Check(bool ok, const char* label) noexcept {
    printf("  %-58s : %s\n", label, ok ? "PASS" : "FAIL");
    if (!ok) ++g_failures;
}

FFF3FPConfiguration MakeConfig(HWND hwnd, std::int32_t adapter) noexcept {
    FFF3FPConfiguration c{};
    c.size = sizeof(c);
    c.version = 15;
    c.outputWindow = hwnd;
    c.decodeMode = FFF3FPDecodeMode::Gpu;
    c.colorMode = FFF3FPColorMode::MapToSdr;
    c.sdrPeakNits = 203.0f;
    c.hdrPeakNits = 1000.0f;
    c.sdrPaperWhiteNits = 203.0f;
    c.videoScalingQuality = FFF3FPVideoScalingQuality::Balanced;
    c.forceHdrOutput = 0;
    c.preferredAdapterIndex = adapter;
    return c;
}

// FFF3FP_Open is asynchronous: it returns Success while the session is still
// Opening. FFF3FP_Play rejects anything that is not Ready, so wait for the
// state to settle before issuing Play.
bool WaitReady(const Api& a, FFF3FPHandle h, int maxMs) noexcept {
    const auto deadline = GetTickCount64() + static_cast<DWORD>(maxMs);
    do {
        FFF3FPSnapshot s{};
        s.size = sizeof(s); s.version = 8;
        if (a.getSnapshot(h, &s) == FFFResult::Success &&
            (s.state == FFF3FPState::Ready || s.state == FFF3FPState::Failed)) return true;
        Sleep(30);
    } while (GetTickCount64() < deadline);
    return false;
}

bool WaitPresented(const Api& a, FFF3FPHandle h, FFF3FPSnapshot& out, int maxMs) noexcept {
    const auto deadline = GetTickCount64() + static_cast<DWORD>(maxMs);
    do {
        out.size = sizeof(out);
        out.version = 8;
        if (a.getSnapshot(h, &out) == FFFResult::Success && out.presentedVideoFrames > 0) return true;
        Sleep(30);
    } while (GetTickCount64() < deadline);
    return false;
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: test_pr.exe <video>\n"); return 2; }
    const std::string media = argv[1];
    // Unbuffered: if the process dies abnormally, block-buffered stdout would
    // silently swallow everything since the last flush.
    setvbuf(stdout, nullptr, _IONBF, 0);

    printf("=== FFF_Project PR end-to-end verification (round 2) ===\n");
    HMODULE dll = LoadLibraryA("FFF.Native.dll");
    if (dll == nullptr) { fprintf(stderr, "cannot load FFF.Native.dll\n"); return 2; }
    Api a;
#define BIND(var, name) a.var = reinterpret_cast<Fn##name>(GetProcAddress(dll, "FFF3FP_" #name))
    BIND(getApiVersion, GetApiVersion);
    BIND(create, Create);
    BIND(open, Open);
    BIND(play, Play);
    BIND(pause, Pause);
    BIND(close, Close);
    BIND(destroy, Destroy);
    BIND(setVolume, SetVolume);
    BIND(setViewTransform, SetViewTransform);
    BIND(getSnapshot, GetSnapshot);
    BIND(getRenderTargetInfo, GetRenderTargetInfo);
    BIND(readVideoPixelRegion, ReadVideoPixelRegion);
#undef BIND

    const auto api = a.getApiVersion();
    printf("kernel api version: %u (expected 15)\n", api);
    printf("config struct size : %zu bytes\n", sizeof(FFF3FPConfiguration));
    printf("rti struct size    : %zu bytes\n", sizeof(FFF3FPRenderTargetInfo));
    Check(api == 15, "apiVersion==15");
    Check(sizeof(FFF3FPConfiguration) == 80, "sizeof(FFF3FPConfiguration)==80");

    std::thread dbg(DbwinThread);
    printf("dbwin listener     : ready\n");
    std::thread wt(WindowThread);
    HWND hwnd = nullptr;
    for (int i = 0; i < 200 && hwnd == nullptr; ++i) { hwnd = g_hwnd.load(); Sleep(20); }
    if (hwnd == nullptr) { fprintf(stderr, "window creation failed\n"); return 2; }
    RECT cr{};
    GetClientRect(hwnd, &cr);
    printf("test window        : hwnd=%p client=%ldx%ld\n",
        static_cast<void*>(hwnd), cr.right - cr.left, cr.bottom - cr.top);

    // ---------- (a) preferredAdapterIndex sweep ----------
    printf("\n--- (a) preferredAdapterIndex ---\n");
    for (std::int32_t idx : {0, -1, 1, 99, -2}) {
        auto cfg = MakeConfig(hwnd, idx);
        FFF3FPHandle h = nullptr;
        const auto rc = a.create(&cfg, &h);
        if (rc != FFFResult::Success) {
            printf("adapter=%-3d create=%-16s (expect %s)\n", idx, Name(rc),
                (idx == 99 || idx == -2) ? "InvalidArgument" : "Success");
            Check((idx == 99 || idx == -2) && rc == FFFResult::InvalidArgument,
                idx == 99 ? "(a) adapter=99 rejected" : "(a) adapter=-2 rejected");
            continue;
        }
        const auto ro = a.open(h, media.c_str());
        const auto ready = ro == FFFResult::Success && WaitReady(a, h, 30000);
        const auto rp = ready ? a.play(h) : FFFResult::InvalidState;
        FFF3FPSnapshot snap{};
        const bool played = rp == FFFResult::Success && WaitPresented(a, h, snap, 15000);
        FFF3FPRenderTargetInfo rti{};
        rti.size = sizeof(rti); rti.version = 1;
        const auto rr = a.getRenderTargetInfo(h, &rti);
        printf("adapter=%-3d create=%-16s open=%-16s play=%-16s rti=%-16s swap=%ux%u presented=%llu\n",
            idx, Name(rc), Name(ro), Name(rp), Name(rr), rti.swapWidth, rti.swapHeight,
            played ? static_cast<unsigned long long>(snap.presentedVideoFrames) : 0ULL);
        Check(ro == FFFResult::Success && rp == FFFResult::Success && played,
            idx == 0 ? "(a) adapter=0 plays" : (idx == -1 ? "(a) adapter=-1 plays" : "(a) adapter=1 plays"));
        a.close(h);
        a.destroy(h);
    }

    // ---------- (b)(c)(f)(g) main session ----------
    printf("\n--- (b) open + play (adapter 0) ---\n");
    auto cfg = MakeConfig(hwnd, 0);
    FFF3FPHandle h = nullptr;
    auto rc = a.create(&cfg, &h);
    printf("    [trace] before open\n");
    const auto roMain = rc == FFFResult::Success ? a.open(h, media.c_str()) : FFFResult::InvalidState;
    printf("    [trace] open=%s\n", Name(roMain));
    const auto readyMain = roMain == FFFResult::Success && WaitReady(a, h, 30000);
    printf("    [trace] ready=%d\n", readyMain ? 1 : 0);
    const auto rpMain = readyMain ? a.play(h) : FFFResult::InvalidState;
    printf("create=%s open=%s play=%s\n", Name(rc), Name(roMain), Name(rpMain));
    Check(rc == FFFResult::Success, "(b) create");
    Check(roMain == FFFResult::Success && readyMain && rpMain == FFFResult::Success,
        "(b) open + play");
    if (rc != FFFResult::Success) { g_dbgStop.store(true); return 1; }
    a.setVolume(h, 0.0f, 1);

    // (f) NEW: RTI before any frame is presented must not lie with all zeros.
    printf("\n--- (f) GetRenderTargetInfo before first present ---\n");
    FFF3FPRenderTargetInfo early{};
    early.size = sizeof(early); early.version = 1;
    const auto rEarly = a.getRenderTargetInfo(h, &early);
    printf("result=%s swap=%ux%u client=%ux%u\n", Name(rEarly), early.swapWidth,
        early.swapHeight, early.clientWidth, early.clientHeight);
    const bool earlyOk = (rEarly == FFFResult::InvalidState) ||
        (rEarly == FFFResult::Success && early.swapWidth > 0 && early.swapHeight > 0);
    Check(earlyOk, "(f) RTI never returns all-zero Success");
    if (rEarly == FFFResult::Success) printf("    note: swapchain already up at that point\n");

    FFF3FPSnapshot snap{};
    const bool played = WaitPresented(a, h, snap, 20000);
    printf("state=%u video=%ux%u decoded=%llu presented=%llu presents=%llu\n",
        static_cast<unsigned>(snap.state), snap.videoWidth, snap.videoHeight,
        static_cast<unsigned long long>(snap.decodedVideoFrames),
        static_cast<unsigned long long>(snap.presentedVideoFrames),
        static_cast<unsigned long long>(snap.swapChainPresents));
    Check(played && snap.videoWidth > 0, "(b) playback presents frames");

    // (c) RTI after playback
    printf("\n--- (c) FFF3FP_GetRenderTargetInfo ---\n");
    FFF3FPRenderTargetInfo rti{};
    rti.size = sizeof(rti); rti.version = 1;
    const auto rr = a.getRenderTargetInfo(h, &rti);
    printf("result=%s size=%u version=%u\n", Name(rr), rti.size, rti.version);
    printf("swap   = %u x %u\nclient = %u x %u\ndest   = (%u,%u) %u x %u\n",
        rti.swapWidth, rti.swapHeight, rti.clientWidth, rti.clientHeight,
        rti.destX, rti.destY, rti.destWidth, rti.destHeight);
    printf("outputBitDepth = %u   hdr = %u\n", rti.outputBitDepth, rti.hdr);
    Check(rr == FFFResult::Success && rti.swapWidth > 0 && rti.destWidth > 0,
        "(c) RTI reports a real target");

    // (d) ReadVideoPixelRegion in-bounds
    printf("\n--- (d) FFF3FP_ReadVideoPixelRegion in-bounds ---\n");
    std::vector<float> px(8 * 8 * 4, -1.0f);
    std::uint32_t bits = 0;
    auto rd = a.readVideoPixelRegion(h, 0, 0, 8, 8, px.data(),
        static_cast<std::uint32_t>(px.size()), &bits);
    float maxAbs = 0.0f;
    for (float v : px) { const auto av = v < 0 ? -v : v; if (av > maxAbs) maxAbs = av; }
    printf("top-left (0,0) 8x8 -> %s bitDepth=%u maxAbs=%f\n", Name(rd), bits, maxAbs);
    for (int i = 0; i < 4; ++i)
        printf("    px[%d] = R=%.4f G=%.4f B=%.4f A=%.4f\n", i,
            px[i * 4 + 0], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]);
    Check(rd == FFFResult::Success && bits > 0 && maxAbs > 0.0f,
        "(d) in-bounds readback returns real samples");

    // (g) NEW: out-of-bounds regions must be rejected, not truncated.
    printf("\n--- (g) ReadVideoPixelRegion out-of-bounds rejection ---\n");
    struct Case { std::uint32_t x, y, w, hh; const char* label; };
    const Case cases[] = {
        {0, 0, rti.swapWidth + 1, 8, "width = swapWidth+1"},
        {0, 0, 8, rti.swapHeight + 1, "height = swapHeight+1"},
        {rti.swapWidth - 1, 0, 2, 2, "x+w straddles right edge"},
        {0, rti.swapHeight - 1, 2, 2, "y+h straddles bottom edge"},
    };
    bool allRejected = true;
    for (const auto& c : cases) {
        std::vector<float> buf(static_cast<std::size_t>(c.w) * c.hh * 4, -1.0f);
        std::uint32_t b2 = 0;
        const auto r2 = a.readVideoPixelRegion(h, c.x, c.y, c.w, c.hh, buf.data(),
            static_cast<std::uint32_t>(buf.size()), &b2);
        printf("  %-28s -> %s\n", c.label, Name(r2));
        if (r2 != FFFResult::InvalidArgument && r2 != FFFResult::InvalidState) allRejected = false;
    }
    Check(allRejected, "(g) out-of-bounds regions rejected");

    // (h) undersized destination buffer still rejected (64-bit compare)
    std::vector<float> tinyBuffer(16, 0.0f);
    const auto rSmall = a.readVideoPixelRegion(h, 0, 0, 64, 64, tinyBuffer.data(), 16, &bits);
    printf("\n--- (h) undersized buffer ---\n  64x64 into 16 floats -> %s\n", Name(rSmall));
    Check(rSmall == FFFResult::InvalidArgument, "(h) undersized dst rejected");

    // (e) SetViewTransform
    printf("\n--- (e) FFF3FP_SetViewTransform ---\n");
    bool vtOk = true;
    for (float z : {1.0f, 1.25f, 1.5f, 2.0f, 1.0f}) {
        const auto r3 = a.setViewTransform(h, z, 0.0f, 0.0f);
        printf("zoom=%.2f pan=(0,0) -> %s\n", z, Name(r3));
        if (r3 != FFFResult::Success) vtOk = false;
    }
    const auto rZero = a.setViewTransform(h, 0.0f, 0.0f, 0.0f);
    printf("zoom=0.00 (invalid) -> %s (expect InvalidArgument)\n", Name(rZero));
    float nan = std::numeric_limits<float>::quiet_NaN();
    const auto rNan = a.setViewTransform(h, 1.0f, nan, 0.0f);
    printf("panX=nan (invalid)  -> %s (expect InvalidArgument)\n", Name(rNan));
    Check(vtOk && rZero == FFFResult::InvalidArgument && rNan == FFFResult::InvalidArgument,
        "(e) view transform valid/invalid handling");

    std::vector<float> px2(8 * 8 * 4, -1.0f);
    const auto rAfter = a.readVideoPixelRegion(h, 0, 0, 8, 8, px2.data(),
        static_cast<std::uint32_t>(px2.size()), &bits);
    float maxAbs2 = 0.0f;
    for (float v : px2) { const auto av = v < 0 ? -v : v; if (av > maxAbs2) maxAbs2 = av; }
    printf("post-zoom (0,0) 8x8 -> %s bitDepth=%u maxAbs=%f\n", Name(rAfter), bits, maxAbs2);
    Check(rAfter == FFFResult::Success, "(e) session alive after transform");

    a.pause(h);
    a.close(h);
    a.destroy(h);

    printf("\n--- debug diagnostics captured ---\n");
    printf("OutputDebugString lines from kernel: %d\n", g_dbgLines.load());
    Check(g_dbgLines.load() > 0, "device-adapter diagnostic still emitted");

    g_dbgStop.store(true);
    PostMessageA(hwnd, WM_CLOSE, 0, 0);
    if (dbg.joinable()) dbg.join();
    if (wt.joinable()) wt.join();

    printf("\n=== SUMMARY ===\n");
    printf("round-2 checks failed: %d\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
