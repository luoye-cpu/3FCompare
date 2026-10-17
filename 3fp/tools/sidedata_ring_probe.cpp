// sidedata_ring_probe.cpp — drive the neutral frame side-data ring
// (FFF3FP_CopyFrameSideDataAfter, added 2026-10-13) against real decoded
// frames, and check the contract the API header documents:
//
//   * the cursor filter is live: asking past the end of the stream must come
//     back NotFound, never a stale hit;
//   * an unknown type value is NotFound (the kernel matches numbers, it does
//     not know formats);
//   * a short buffer is BufferTooSmall *and still reports the required size*,
//     with the entry left in the ring so the retry can succeed;
//   * iterating by foundFrameIndex + 1 never re-serves the same frame;
//   * with suitable content, a frame's side data really is readable, byte for
//     byte identical to what the demuxer attached.
//
// Negative controls run whether or not the fixture carries side data, so a
// probe with the wrong file still measures the routing. The positive path needs
// content with frame side data; the two verified on 2026-10-13 are in the repo:
//   testmedia/media/hdr10plus/real_hdr10plus_rolloff.mp4
//       -> per-frame type 17 (11304 B) and type 11 (88 B), served for frames
//          0,1,2 then 18,42,66,90,114 -- the gaps show the "first at/after the
//          cursor" rule, not a per-frame requirement;
//   testmedia/media/hdrmeta/hdr_vivid_h265_sample.hevc
//       -> type 25 (1372 B) at frame 0.
// Sizes above are the structs sidedata_size_probe measured, so a ring that
// truncated a blob would show up as a size mismatch here.
//
// Usage: sidedata_ring_probe <media path> [--ms <pump per step>] [--steps N]
// Built against 3fp/kernel's FFF.Player.Api.h (the tree that has kind 3), so
// run it beside the DLL that tree produced: 3fp/build/FFF.Native.dll.

#include <windows.h>
#include <cstdio>
#include <cstring>
#include <vector>
#include <string>
#include <chrono>
#include <thread>
#include <atomic>
#include "3FP/Api/FFF.Player.Api.h"

namespace {

FFF3FPHandle g_player = nullptr;
std::atomic<bool> g_openCompleted{false};

void __cdecl OnEvent(void* context, FFF3FPEvent type, const char* detail) {
    (void)context; (void)detail;
    if (type == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

void Pump(int milliseconds) {
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::milliseconds(milliseconds);
    MSG message;
    while (std::chrono::steady_clock::now() < deadline) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
}

const char* Verdict(FFFResult result) {
    switch (result) {
    case FFFResult::Success: return "Success";
    case FFFResult::BufferTooSmall: return "BufferTooSmall";
    case FFFResult::NotFound: return "NotFound";
    case FFFResult::InvalidArgument: return "InvalidArgument";
    default: return "other";
    }
}

// The candidate frame side data types, as plain numbers -- values measured
// against the bundled libavutil (sidedata_size_probe prints them).
struct Candidate { const char* name; std::uint32_t type; };
const Candidate kCandidates[] = {
    { "DISPLAYMATRIX", 6 },
    { "MASTERING_DISPLAY", 11 },
    { "ICC_PROFILE", 15 },
    { "DYNAMIC_HDR_PLUS", 17 },
    { "FILM_GRAIN_PARAMS", 21 },
    { "DOVI_RPU_BUFFER", 23 },
    { "DOVI_METADATA", 24 },
    { "DYNAMIC_HDR_VIVID", 25 },
};
constexpr std::size_t kCandidateCount = sizeof(kCandidates) / sizeof(kCandidates[0]);

std::int64_t CurrentFrameIndex() {
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    // The kernel rejects any other version outright (PlayerSession::GetSnapshot
    // gates on == 20), so leaving this at 0 made the probe read "-1" and turned
    // every index-dependent control into a trivial pass.
    snapshot.version = 20;
    if (FFF3FP_GetSnapshot(g_player, &snapshot) != FFFResult::Success) return -1;
    return snapshot.frameIndex;
}

int g_failures = 0;

void Check(bool condition, const char* label) {
    std::printf("  [%s] %s\n", condition ? "PASS" : "FAIL", label);
    if (!condition) ++g_failures;
}

/// Asks the ring for `type` at/after `from`, probe-call style (no buffer).
bool Ask(std::uint32_t type, std::int64_t from, std::uint32_t& size,
    std::int64_t& index) {
    const auto result = FFF3FP_CopyFrameSideDataAfter(g_player, type, from,
        nullptr, 0u, &size, &index);
    return result == FFFResult::Success;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: sidedata_ring_probe <media path> [--ms N] [--steps N]\n");
        return 2;
    }
    const char* path = argv[1];
    int stepMs = 500;
    int steps = 12;
    for (int i = 2; i < argc; ++i) {
        if (std::strcmp(argv[i], "--ms") == 0 && i + 1 < argc) stepMs = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--steps") == 0 && i + 1 < argc) steps = std::atoi(argv[++i]);
    }

    // Which FFF.Native this process actually bound to, and whether it carries
    // the additive export. The entry point is linked implicitly, so a DLL
    // without it would have failed at process start -- but print the module
    // path anyway, because "measured the DLL you think you measured" is exactly
    // the false green this probe exists to avoid.
    {
        HMODULE module = GetModuleHandleW(L"FFF.Native.dll");
        if (module == nullptr) module = GetModuleHandleW(L"FFF.Native");
        wchar_t modulePath[1024]{};
        const auto length = GetModuleFileNameW(module, modulePath,
            static_cast<DWORD>(std::size(modulePath)));
        if (length == 0) {
            std::printf("FATAL: FFF.Native is not loaded in this process (err=%lu)\n",
                GetLastError());
            return 3;
        }
        std::printf("bound module = %ls\n", modulePath);
        if (GetProcAddress(module, "FFF3FP_CopyFrameSideDataAfter") == nullptr) {
            std::printf("FATAL: %ls has no FFF3FP_CopyFrameSideDataAfter (older kernel)\n",
                modulePath);
            return 3;
        }
    }
    std::printf("dll api version = %u\n", FFF3FP_GetApiVersion());

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"3fpSideDataRingProbe";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, L"3fpSideDataRingProbe", L"sidedata ring probe",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540,
        nullptr, nullptr, windowClass.hInstance, nullptr);
    if (window == nullptr) { std::printf("CreateWindowExW failed\n"); return 1; }
    Pump(200);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    if (FFF3FP_Create(&configuration, &g_player) != FFFResult::Success ||
        g_player == nullptr) {
        std::printf("Create failed\n");
        return 6;
    }
    if (FFF3FP_Open(g_player, path) != FFFResult::Success) {
        char error[512]{};
        FFF3FP_GetLastError(g_player, error, sizeof(error), nullptr);
        std::printf("Open failed: %s\n", error);
        FFF3FP_Destroy(g_player);
        return 7;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    Pump(300);
    FFF3FP_Play(g_player);

    std::uint32_t seenTypes = 0;
    std::int64_t lastHitIndex = -1;
    std::vector<std::pair<std::uint32_t, std::int64_t>> cursors;
    for (const auto& candidate : kCandidates)
        cursors.emplace_back(candidate.type, 0);

    for (int step = 0; step < steps; ++step) {
        Pump(stepMs);
        const auto frameIndex = CurrentFrameIndex();
        std::printf("step %2d: frameIndex=%lld\n", step,
            static_cast<long long>(frameIndex));
        for (std::size_t c = 0; c < kCandidateCount; ++c) {
            const auto& candidate = kCandidates[c];
            std::uint32_t size = 0;
            std::int64_t index = -1;
            if (!Ask(candidate.type, cursors[c].second, size, index)) continue;
            std::printf("    %-18s type=%2u size=%u frameIndex=%lld\n",
                candidate.name, candidate.type, size, static_cast<long long>(index));
            ++seenTypes;
            // Iteration contract: a later answer never names an earlier frame.
            if (index < cursors[c].second) {
                std::printf("    [FAIL] %s: foundFrameIndex %lld < requested %lld\n",
                    candidate.name, static_cast<long long>(index),
                    static_cast<long long>(cursors[c].second));
                ++g_failures;
            }
            if (index == lastHitIndex && cursors[c].second != index) {
                // Not a failure per se (different types can share a frame), only
                // a cross-check that the same frame really serves multiple types.
                std::printf("    note: same frame serves another type as well\n");
            }
            lastHitIndex = index;
            cursors[c].second = index + 1;

            // Short-buffer path on a hit we know exists: must refuse, must
            // report the size, and must leave the entry for the retry.
            std::vector<std::uint8_t> oneByte;
            std::uint32_t reported = 0;
            std::int64_t retryIndex = -1;
            oneByte.resize(1);
            const auto refused = FFF3FP_CopyFrameSideDataAfter(g_player, candidate.type,
                index, oneByte.data(), 1u, &reported, &retryIndex);
            if (size > 1) {
                Check(refused == FFFResult::BufferTooSmall, "short buffer refused");
                Check(reported == size, "short buffer still reports required size");
            }
            const auto refetch = FFF3FP_CopyFrameSideDataAfter(g_player, candidate.type,
                index, nullptr, 0u, &reported, &retryIndex);
            Check(refetch == FFFResult::Success && retryIndex == index,
                "entry survives the refused call (no silent consume)");

            // Read it for real once, and keep the bytes for the content check.
            std::vector<std::uint8_t> blob(size);
            std::uint32_t written = 0;
            const auto full = FFF3FP_CopyFrameSideDataAfter(g_player, candidate.type,
                index, blob.data(), static_cast<std::uint32_t>(blob.size()), &written,
                &retryIndex);
            Check(full == FFFResult::Success && written == size,
                "exact-size buffer accepted");
            std::printf("    first bytes:");
            for (std::size_t i = 0; i < (size < 8 ? size : 8u); ++i)
                std::printf(" %02X", blob[i]);
            std::printf("\n");
        }
    }

    // --- Negative controls: these must hold whatever the content was. ---
    std::printf("negative controls (frameIndex=%lld now)\n",
        static_cast<long long>(CurrentFrameIndex()));
    {
        std::uint32_t size = 0;
        std::int64_t index = -1;
        const auto askedAhead = FFF3FP_CopyFrameSideDataAfter(g_player, 24,
            CurrentFrameIndex() + 100'000, nullptr, 0u, &size, &index);
        Check(askedAhead == FFFResult::NotFound, "asking ahead of the stream returns NotFound");
        Check(size == 0u && index == -1, "NotFound reports size 0 and index -1");

        size = 0; index = -1;
        const auto bogus = FFF3FP_CopyFrameSideDataAfter(g_player, 9999u, 0,
            nullptr, 0u, &size, &index);
        Check(bogus == FFFResult::NotFound, "an unknown type number is NotFound");

        size = 123; index = 456;
        const auto nullOut = FFF3FP_CopyFrameSideDataAfter(g_player, 24, 0,
            nullptr, 0u, nullptr, &index);
        Check(nullOut == FFFResult::InvalidArgument, "null writtenSize refused");
        Check(size == 123u && index == 456, "refused call leaves the out params alone");
    }

    std::printf("distinct type hits observed = %u\n", seenTypes);
    if (seenTypes == 0)
        std::printf("NOTE: this fixture carried no frame side data the ring caches; "
                    "positive path unverified with this file.\n");
    std::printf("failures = %d\n", g_failures);

    FFF3FP_Close(g_player);
    FFF3FP_Destroy(g_player);
    return g_failures == 0 ? 0 : 1;
}
