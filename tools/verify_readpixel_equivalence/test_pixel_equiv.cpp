// test_pixel_equiv.cpp - equivalence check between the PR-added batch readback
// API FFF3FP_ReadVideoPixelRegion and the pre-existing single-point API
// FFF3FP_ReadVideoPixel.
//
// For the SAME coordinate, both APIs must report the same normalized RGBA.
// Playback is PAUSED first: while playing, the cached frame advances and the
// two calls would sample different frames, producing a false mismatch.
//
// Derived from test_pr.cpp (helpers kept verbatim, DBWIN capture dropped).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>
#include <string>
#include <cstdlib>
#include "3FP/Api/FFF.Player.Api.h"

// ---------------------------------------------------------------------------
static HWND g_hwnd = nullptr;
static const wchar_t* kWndClass = L"FFFPRPixelEquivWnd";

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    return DefWindowProcW(h, m, w, l);
}

static bool CreateTestWindow(const int clientW, const int clientH)
{
    HINSTANCE inst = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = kWndClass;
    if (RegisterClassExW(&wc) == 0) return false;
    RECT r{ 0, 0, clientW, clientH };
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    g_hwnd = CreateWindowExW(0, kWndClass, L"FFF Pixel Equivalence", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left, r.bottom - r.top,
        nullptr, nullptr, inst, nullptr);
    if (g_hwnd == nullptr) return false;
    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(g_hwnd);
    return true;
}

static void Pump()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

static void PumpFor(const DWORD ms)
{
    const DWORD end = GetTickCount() + ms;
    while (GetTickCount() < end) { Pump(); Sleep(20); }
}

static const char* R(const FFFResult r)
{
    switch (r) {
    case FFFResult::Success:        return "Success";
    case FFFResult::InvalidArgument:return "InvalidArgument";
    case FFFResult::InvalidState:   return "InvalidState";
    case FFFResult::BufferTooSmall: return "BufferTooSmall";
    case FFFResult::NativeFailure:  return "NativeFailure";
    case FFFResult::FfmpegFailure:  return "FfmpegFailure";
    case FFFResult::DeviceFailure:  return "DeviceFailure";
    case FFFResult::NotSupported:   return "NotSupported";
    default:                        return "?";
    }
}

static const char* StateName(const FFF3FPState s)
{
    switch (s) {
    case FFF3FPState::Idle:    return "Idle";
    case FFF3FPState::Opening: return "Opening";
    case FFF3FPState::Ready:   return "Ready";
    case FFF3FPState::Playing: return "Playing";
    case FFF3FPState::Paused:  return "Paused";
    case FFF3FPState::Ended:   return "Ended";
    case FFF3FPState::Failed:  return "Failed";
    case FFF3FPState::Closed:  return "Closed";
    default:                   return "Other";
    }
}

static FFF3FPConfiguration MakeConfig(const std::int32_t adapterIndex)
{
    FFF3FPConfiguration c{};
    c.size = sizeof(FFF3FPConfiguration);
    c.version = 15;                       // PR bump: 14 -> 15
    c.outputWindow = g_hwnd;
    c.decodeMode = FFF3FPDecodeMode::D3D11;
    // Default run is SDR (8-bit BGRA swap chain). Setting FFF_TEST_HDR=1 forces
    // HDR output, which selects an R16G16B16A16_FLOAT swap chain and therefore
    // exercises the 16F branch where the two readback paths are implemented
    // differently.
    const bool hdr = (std::getenv("FFF_TEST_HDR") != nullptr);
    c.colorMode = hdr ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    c.sdrPeakNits = 100.0f;
    c.hdrPeakNits = 1000.0f;
    c.sdrPaperWhiteNits = 200.0f;
    c.audioEndpointIdUtf8 = nullptr;
    c.eventCallback = nullptr;
    c.eventCallbackContext = nullptr;
    c.videoScalingQuality = FFF3FPVideoScalingQuality::Balanced;
    c.forceHdrOutput = hdr ? 1u : 0u;
    c.preferredAdapterIndex = adapterIndex;
    return c;
}

static bool GetSnapshot(FFF3FPHandle h, FFF3FPSnapshot& s)
{
    s = {};
    s.size = sizeof(FFF3FPSnapshot);
    s.version = 8;
    return FFF3FP_GetSnapshot(h, &s) == FFFResult::Success;
}

static bool GetRti(FFF3FPHandle h, FFF3FPRenderTargetInfo& info)
{
    info = {};
    info.size = sizeof(FFF3FPRenderTargetInfo);
    info.version = 1;
    return FFF3FP_GetRenderTargetInfo(h, &info) == FFFResult::Success;
}

// Media under test. Overridable via RPE_MEDIA so the harness is not welded to
// one file. Default is HDR10: only an HDR source can produce an HDR (16F)
// swap chain, which is the path this harness exists to cover.
static std::string MediaPath()
{
    const char* m = std::getenv("RPE_MEDIA");
    return (m != nullptr && *m != '\0')
        ? std::string(m)
        : std::string("C:\\PLAN\\3FCompare\\testmedia\\media\\real\\real_4k_hevc_hdr10_60m.mp4");
}
static const std::string kMedia = MediaPath();

struct PlaySession {
    FFF3FPHandle h = nullptr;
    FFFResult create = FFFResult::InvalidState;
    FFFResult open = FFFResult::InvalidState;
    FFFResult play = FFFResult::InvalidState;
    FFF3FPRenderTargetInfo info{};
    bool rtiOk = false;
    std::uint64_t presented = 0;
    std::uint64_t decoded = 0;
    std::uint64_t presents = 0;
    FFF3FPState lastState = FFF3FPState::Idle;
};

// Poll until Open leaves Opening (Open is asynchronous: it only enqueues DoOpen
// and returns Success). Play() would otherwise return InvalidState.
static PlaySession StartPlayback(const std::int32_t adapterIndex, const DWORD timeoutMs)
{
    PlaySession s;
    FFF3FPConfiguration c = MakeConfig(adapterIndex);
    s.create = FFF3FP_Create(&c, &s.h);
    if (s.create != FFFResult::Success) return s;
    s.open = FFF3FP_Open(s.h, kMedia.c_str());
    if (s.open != FFFResult::Success) return s;

    std::printf("    open->ready wait: ");
    for (int i = 0; i < 600; ++i) {          // up to ~30 s
        FFF3FPSnapshot w{};
        if (GetSnapshot(s.h, w)) {
            if (w.state != s.lastState) {
                std::printf("[%lums %s] ", static_cast<unsigned long>(i * 50), StateName(w.state));
                s.lastState = w.state;
            }
            if (w.state != FFF3FPState::Opening) break;
        }
        PumpFor(50);
    }
    std::printf("\n");

    s.play = FFF3FP_Play(s.h);
    if (s.play != FFFResult::Success) return s;

    const DWORD end = GetTickCount() + timeoutMs;
    while (GetTickCount() < end) {
        PumpFor(150);
        FFF3FPSnapshot snap{};
        if (GetSnapshot(s.h, snap)) {
            s.presented = snap.presentedVideoFrames;
            s.decoded = snap.decodedVideoFrames;
            s.presents = snap.swapChainPresents;
            s.lastState = snap.state;
        }
        FFF3FPRenderTargetInfo info{};
        if (GetRti(s.h, info)) {
            s.info = info;
            s.rtiOk = true;
            if (info.swapWidth > 0 && info.swapHeight > 0 && s.presented > 0) break;
        }
    }
    return s;
}

static void StopPlayback(PlaySession& s)
{
    if (s.h == nullptr) return;
    FFF3FP_Close(s.h);
    FFF3FP_Destroy(s.h);
    s.h = nullptr;
}

// ---------------------------------------------------------------------------
static const float kTol = 1e-3f;

struct Sample {
    std::uint32_t x = 0, y = 0;
    float reg[4]{};
    float pix[4]{};
    float maxDiff = 0.0f;
    FFFResult regRes = FFFResult::InvalidState;
    FFFResult pixRes = FFFResult::InvalidState;
    std::uint32_t regBits = 0, pixBits = 0;
};

static float MaxAbsDiff(const float a[4], const float b[4])
{
    float m = 0.0f;
    for (int i = 0; i < 4; ++i) { const float d = std::fabs(a[i] - b[i]); if (d > m) m = d; }
    return m;
}

int main()
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    std::printf("=== FFF_Project: ReadVideoPixelRegion vs ReadVideoPixel equivalence ===\n");
    std::printf("kernel api version : %u\n", FFF3FP_GetApiVersion());
    std::printf("probe struct size  : %u bytes\n", static_cast<unsigned>(sizeof(FFF3FPVideoPixelProbe)));

    if (!CreateTestWindow(1280, 720)) {
        std::printf("FATAL: could not create test window (err %lu)\n", GetLastError());
        return 2;
    }
    std::printf("test window        : hwnd=%p client=1280x720\n\n", static_cast<void*>(g_hwnd));

    std::printf("--- open + play (adapter 0) ---\n");
    PlaySession s = StartPlayback(0, 25000);
    std::printf("create=%s open=%s play=%s\n", R(s.create), R(s.open), R(s.play));
    if (s.h == nullptr) { std::printf("FATAL: could not create player\n"); return 3; }
    if (s.open != FFFResult::Success || s.play != FFFResult::Success) {
        char err[1024] = { 0 };
        std::uint32_t need = 0;
        FFF3FP_GetLastError(s.h, err, sizeof(err), &need);
        std::printf("FATAL: open/play failed (state=%s): %s\n", StateName(s.lastState), err);
        StopPlayback(s);
        return 3;
    }
    if (s.lastState != FFF3FPState::Ready && s.lastState != FFF3FPState::Playing) {
        std::printf("FATAL: state=%s, expected Ready/Playing\n", StateName(s.lastState));
        StopPlayback(s);
        return 3;
    }

    FFF3FPSnapshot snap{};
    GetSnapshot(s.h, snap);
    std::printf("state=%s video=%ux%u decoded=%llu presented=%llu presents=%llu\n",
        StateName(snap.state), snap.videoWidth, snap.videoHeight,
        static_cast<unsigned long long>(snap.decodedVideoFrames),
        static_cast<unsigned long long>(snap.presentedVideoFrames),
        static_cast<unsigned long long>(snap.swapChainPresents));

    // Let the picture settle, then freeze the cached frame so that the two
    // APIs provably sample the SAME frame.
    std::printf("stabilizing 3 s ...\n");
    PumpFor(3000);

    FFF3FPRenderTargetInfo rti{};
    const FFFResult rtiRes = FFF3FP_GetRenderTargetInfo(s.h, &rti);
    std::printf("rti result=%s swap=%ux%u dest=(%u,%u) %ux%u outputBitDepth=%u hdr=%u\n",
        R(rtiRes), rti.swapWidth, rti.swapHeight, rti.destX, rti.destY,
        rti.destWidth, rti.destHeight, rti.outputBitDepth, rti.hdr);

    const FFFResult pauseRes = FFF3FP_Pause(s.h);
    PumpFor(500);
    FFF3FPSnapshot paused{};
    GetSnapshot(s.h, paused);
    std::printf("pause result=%s state=%s  (frame frozen for sampling)\n\n",
        R(pauseRes), StateName(paused.state));

    const std::uint32_t W = rti.swapWidth;
    const std::uint32_t H = rti.swapHeight;
    if (W < 16 || H < 16) {
        std::printf("FATAL: swap chain %ux%u too small\n", W, H);
        StopPlayback(s);
        return 4;
    }

    // ---- single-pixel equivalence over several coordinates ----------------
    const std::uint32_t coords[][2] = {
        { W / 8,      H / 8      },
        { W / 2,      H / 2      },
        { W * 7 / 8,  H * 7 / 8  },
        { W / 4,      H * 3 / 4  },
        { W * 3 / 4,  H / 4      },
        { W - 8,      H - 8      },
    };
    const int coordCount = static_cast<int>(sizeof(coords) / sizeof(coords[0]));

    // ---- control: prove the cached frame is actually frozen ----------------
    // If playback were still advancing, two identical single-pixel reads would
    // differ and the whole comparison below would be meaningless.
    {
        FFF3FPVideoPixelProbe a{}, b{};
        a.size = b.size = sizeof(FFF3FPVideoPixelProbe);
        a.version = b.version = 1;
        a.x = b.x = coords[0][0];
        a.y = b.y = coords[0][1];
        const FFFResult ra = FFF3FP_ReadVideoPixel(s.h, &a);
        PumpFor(300);
        const FFFResult rb = FFF3FP_ReadVideoPixel(s.h, &b);
        const float av[4] = { a.red, a.green, a.blue, a.alpha };
        const float bv[4] = { b.red, b.green, b.blue, b.alpha };
        const float d = (ra == FFFResult::Success && rb == FFFResult::Success)
            ? MaxAbsDiff(av, bv) : -1.0f;
        std::printf("frame-frozen control: two reads 300 ms apart at (%u,%u) diff=%.6f -> %s\n",
            coords[0][0], coords[0][1], d, (d == 0.0f) ? "FROZEN (valid)" : "FRAME MOVED");
        if (d != 0.0f) {
            std::printf("FATAL: frame is not stable while paused; comparison would be invalid.\n");
            StopPlayback(s);
            return 5;
        }
    }

    std::printf("\n--- (1) same-coordinate: Region 1x1 vs ReadVideoPixel ---\n");
    std::printf("%-5s %-14s %-38s %-38s %-10s %-6s\n",
        "#", "coord", "Region  R    G    B    A", "Pixel   R    G    B    A", "maxDiff", "verdict");

    int agree = 0, disagree = 0, failed = 0;
    float worst = 0.0f;
    for (int i = 0; i < coordCount; ++i) {
        Sample sp;
        sp.x = coords[i][0];
        sp.y = coords[i][1];

        float rbuf[4] = { -1.0f, -1.0f, -1.0f, -1.0f };
        sp.regRes = FFF3FP_ReadVideoPixelRegion(s.h, sp.x, sp.y, 1, 1, rbuf, 4, &sp.regBits);
        std::memcpy(sp.reg, rbuf, sizeof(rbuf));

        FFF3FPVideoPixelProbe p{};
        p.size = sizeof(FFF3FPVideoPixelProbe);
        p.version = 1;
        p.x = sp.x;
        p.y = sp.y;
        sp.pixRes = FFF3FP_ReadVideoPixel(s.h, &p);
        sp.pix[0] = p.red; sp.pix[1] = p.green; sp.pix[2] = p.blue; sp.pix[3] = p.alpha;
        sp.pixBits = p.outputBitDepth;

        const bool ok = sp.regRes == FFFResult::Success && sp.pixRes == FFFResult::Success;
        if (ok) {
            sp.maxDiff = MaxAbsDiff(sp.reg, sp.pix);
            if (sp.maxDiff > worst) worst = sp.maxDiff;
            if (sp.maxDiff <= kTol) ++agree; else ++disagree;
        } else {
            ++failed;
        }

        char coord[32];
        std::snprintf(coord, sizeof(coord), "(%u,%u)", sp.x, sp.y);
        std::printf("%-5d %-14s R=%.4f G=%.4f B=%.4f A=%.4f  R=%.4f G=%.4f B=%.4f A=%.4f  %-10.6f %s\n",
            i, coord,
            sp.reg[0], sp.reg[1], sp.reg[2], sp.reg[3],
            sp.pix[0], sp.pix[1], sp.pix[2], sp.pix[3],
            sp.maxDiff,
            !ok ? "ERROR" : (sp.maxDiff <= kTol ? "OK" : "MISMATCH"));
        std::printf("      regRes=%-13s pixRes=%-13s regBits=%u pixBits=%u\n",
            R(sp.regRes), R(sp.pixRes), sp.regBits, sp.pixBits);
    }

    // ---- layout check: 4x4 regions at CONTENT anchors vs per-pixel reads ---
    // Anchors are the coordinates that showed non-zero colour in (1). A black
    // block would pass even with a broken row/column mapping, so it is useless
    // as evidence; these anchors make a wrong stride detectable.
    std::printf("\n--- (2) layout check: 4x4 region vs per-pixel reads (content anchors) ---\n");
    const std::uint32_t anchors[][2] = {
        { W / 8,     H / 8 },
        { W * 3 / 4, H / 4 },
    };
    int layoutAgree = 0, layoutFail = 0, layoutPixels = 0, layoutColored = 0;
    float layoutWorst = 0.0f;
    bool layoutRegionOk = true;
    for (const auto& a : anchors) {
        const std::uint32_t bx = a[0], by = a[1];
        std::vector<float> region(4u * 4u * 4u, -1.0f);
        std::uint32_t regionBits = 0;
        const FFFResult rr = FFF3FP_ReadVideoPixelRegion(
            s.h, bx, by, 4, 4, region.data(), static_cast<std::uint32_t>(region.size()), &regionBits);
        std::printf("region (%u,%u) 4x4 -> %s bitDepth=%u\n", bx, by, R(rr), regionBits);
        if (rr != FFFResult::Success) {
            layoutRegionOk = false; layoutFail += 16; layoutPixels += 16;
            continue;
        }
        for (std::uint32_t row = 0; row < 4; ++row) {
            for (std::uint32_t col = 0; col < 4; ++col) {
                FFF3FPVideoPixelProbe p{};
                p.size = sizeof(FFF3FPVideoPixelProbe);
                p.version = 1;
                p.x = bx + col;
                p.y = by + row;
                const FFFResult pr = FFF3FP_ReadVideoPixel(s.h, &p);
                const float* rp = &region[(static_cast<std::size_t>(row) * 4u + col) * 4u];
                const float pv[4] = { p.red, p.green, p.blue, p.alpha };
                const float d = (pr == FFFResult::Success) ? MaxAbsDiff(rp, pv) : -1.0f;
                ++layoutPixels;
                if (rp[0] > 0.01f || rp[1] > 0.01f || rp[2] > 0.01f) ++layoutColored;
                if (d >= 0.0f && d <= kTol) ++layoutAgree; else ++layoutFail;
                if (d >= 0.0f && d > layoutWorst) layoutWorst = d;
                std::printf("  (%u,%u) region R=%.4f G=%.4f B=%.4f A=%.4f | pixel R=%.4f G=%.4f B=%.4f A=%.4f | diff=%.6f %s\n",
                    bx + col, by + row,
                    rp[0], rp[1], rp[2], rp[3], pv[0], pv[1], pv[2], pv[3], d,
                    (d >= 0.0f && d <= kTol) ? "OK" : "MISMATCH");
            }
        }
    }

    // ---- summary ----------------------------------------------------------
    std::printf("\n=== SUMMARY ===\n");
    std::printf("single-pixel coords compared : %d  (agree=%d disagree=%d error=%d)\n",
        coordCount, agree, disagree, failed);
    std::printf("worst single-pixel maxDiff   : %.6f (tolerance %.1e)\n", worst, kTol);
    std::printf("4x4 layout pixels compared   : %d (agree=%d mismatch=%d) worst=%.6f\n",
        layoutPixels, layoutAgree, layoutFail, layoutWorst);
    std::printf("layout pixels with signal    : %d / %d (must be >0 for a meaningful test)\n",
        layoutColored, layoutPixels);
    const bool pass = (failed == 0 && disagree == 0 && layoutFail == 0 &&
                       coordCount > 0 && layoutRegionOk && layoutColored > 0);
    std::printf("VERDICT: %s\n", pass ? "CONSISTENT (batch API matches single-point API)"
                                    : "NOT CONSISTENT - see rows above");

    StopPlayback(s);
    if (g_hwnd) DestroyWindow(g_hwnd);
    return pass ? 0 : 1;
}
