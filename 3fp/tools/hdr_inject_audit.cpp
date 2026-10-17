// Behavioural audit of the HDR10+ external injection interface. (rev 2)
//
// Checks it against its own contract (FFF.Player.Api.h "Contract details") rather than
// against opinions: matching key, rejection leaving the previous table in force, clear
// restoring bitstream behaviour, the headless/paused caveats, and -- the part the
// contract never states -- WHICH presentation arm the injection can actually influence.
//
// The carrier is the SEI-stripped clip (same pixels and residuals as the HDR10+ arm with
// the ST 2094-40 SEI removed), so the bitstream contributes no dynamic metadata at all:
// every picture change observed here can only come from this interface.
//
// rev 2 fixes the three defects of rev 1:
//   * --control runs the same sampling cadence WITHOUT any injection, so the playing
//     arms' digest drift can be judged against a baseline instead of being trusted;
//     playing-mode verdicts now ride the applied/refused counters, which do not move
//     unless the injection interface itself did something.
//   * the malformed-table probe now runs while a good table is in force, per contract
//     ("a malformed entry returns InvalidArgument and leaves the previous table in
//     charge") -- rev 1 ran it after Clear and so never exercised that clause.
//   * a dedicated matching-key arm posts a sparse 3-entry table (frameIndex 10/30/60,
//     maxScl 0.05/0.10/0.40 == 500/1000/4000 nits, targeted 700/1400/2800) and walks
//     frames 0/5/9/10/29/30/59/60/75 with seek+pause, checking snapshot.sourcePeakNits
//     and dynamicMetadataTargetedNits against the contract's "last entry whose
//     frameIndex <= current frame wins; frames below the first entry get the first
//     entry". Also checks the lazy-install expectation (table posted after Open and
//     before Play must stay inert until a frame is indexed).
//
// usage: hdr_inject_audit <strippedMedia> [--frame N] [--row Y] [--width N]
//                         [--playing] [--control]

#include <windows.h>

#include <sddl.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

bool g_openCompleted = false;

void OnEvent(void*, const FFF3FPEvent event, const char*) {
    if (event == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}

// Pre-load every kernel delay dependency up front and report any failure with its
// Win32 error, so a delay-load abort deep inside the kernel names its DLL here instead.
void PreloadDelayDependencies() {
    // Token diagnostics: is this process running inside an AppContainer or a
    // restricted token? That would explain err=5 on every workspace file while
    // system DLLs (which carry ALL APPLICATION PACKAGES ACEs) still load.
    {
        HANDLE token = nullptr;
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
            DWORD isAppContainer = 0, returned = 0;
            if (GetTokenInformation(token, TokenIsAppContainer, &isAppContainer,
                    sizeof(isAppContainer), &returned))
                std::printf("    TOKEN isAppContainer=%lu\n",
                    static_cast<unsigned long>(isAppContainer));
            DWORD integrityLevel = 0;
            if (GetTokenInformation(token, TokenIntegrityLevel, &integrityLevel,
                    sizeof(integrityLevel), &returned))
                std::printf("    TOKEN integrityLevel=0x%04lX\n",
                    static_cast<unsigned long>(integrityLevel));
            DWORD restricted = 0;
            if (GetTokenInformation(token, TokenRestrictedSids, nullptr, 0, &returned) ||
                GetLastError() == ERROR_INSUFFICIENT_BUFFER)
                restricted = returned;
            std::printf("    TOKEN restrictedSidsBytes=%lu\n",
                static_cast<unsigned long>(restricted));
            if (restricted > 0) {
                std::vector<char> blob(restricted, 0);
                if (GetTokenInformation(token, TokenRestrictedSids, blob.data(), restricted,
                        &returned)) {
                    const auto* groups = reinterpret_cast<const TOKEN_GROUPS*>(blob.data());
                    std::printf("    TOKEN restrictedSids=%u\n", groups->GroupCount);
                    for (DWORD i = 0; i < groups->GroupCount; ++i) {
                        LPSTR sidText = nullptr;
                        if (ConvertSidToStringSidA(groups->Groups[i].Sid, &sidText) &&
                            sidText != nullptr) {
                            std::printf("      sid[%lu]=%s attr=0x%08lX\n",
                                static_cast<unsigned long>(i), sidText,
                                static_cast<unsigned long>(groups->Groups[i].Attributes));
                            LocalFree(sidText);
                        }
                    }
                }
            }
            CloseHandle(token);
        } else {
            std::printf("    TOKEN open failed err=%lu\n", GetLastError());
        }
    }
    // Policy probe: is the denial extension-based (.dll) or file-based?
    {
        wchar_t exeDir[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exeDir, MAX_PATH);
        const auto slash = std::wstring(exeDir).find_last_of(L"\\/");
        if (slash != std::wstring::npos) exeDir[slash] = L'\0';
        const wchar_t* probes[] = {L"\\dll-manifest.txt", L"\\FFF.Native.dll",
            L"\\__nonexistent.dll"};
        for (const wchar_t* suffix : probes) {
            std::wstring full = std::wstring(exeDir) + suffix;
            SetLastError(0);
            const HANDLE h = CreateFileW(full.c_str(), GENERIC_READ, FILE_SHARE_READ,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            const DWORD err = GetLastError();
            std::printf("    POLICY open %ls -> %p err=%lu\n", suffix,
                static_cast<void*>(h), static_cast<unsigned long>(err));
            if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
        }
    }
    const wchar_t* names[] = {L"bluray-3.dll", L"ass-9.dll", L"avcodec-63.dll",
        L"avformat-63.dll", L"avutil-61.dll", L"swresample-7.dll", L"swscale-10.dll",
        L"avfilter-12.dll"};
    wchar_t exeDir[MAX_PATH]{};
    GetModuleFileNameW(nullptr, exeDir, MAX_PATH);
    const auto slash = std::wstring(exeDir).find_last_of(L"\\/");
    if (slash != std::wstring::npos) exeDir[slash] = L'\0';
    for (const wchar_t* name : names) {
        std::wstring full = std::wstring(exeDir) + L"\\" + name;
        SetLastError(0);
        const HMODULE loaded = LoadLibraryW(full.c_str());
        if (loaded == nullptr) {
            const DWORD plainErr = GetLastError();
            SetLastError(0);
            const HMODULE dataOnly = LoadLibraryExW(full.c_str(), nullptr,
                LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            const DWORD dataErr = GetLastError();
            SetLastError(0);
            const HMODULE sys = LoadLibraryW(L"dwrite.dll");
            const DWORD sysErr = GetLastError();
            std::printf("    PRELOAD %ls FAILED plain err=%lu ; datafile=%p err=%lu ; "
                        "system-dll=%p err=%lu\n", name,
                static_cast<unsigned long>(plainErr), static_cast<void*>(dataOnly),
                static_cast<unsigned long>(dataErr), static_cast<void*>(sys),
                static_cast<unsigned long>(sysErr));
            // Discriminator 1: can we open the file for plain read at all?
            SetLastError(0);
            const HANDLE read = CreateFileW(full.c_str(), GENERIC_READ, FILE_SHARE_READ,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            const DWORD readErr = GetLastError();
            if (read != INVALID_HANDLE_VALUE) CloseHandle(read);
            // Discriminator 2: does a fresh COPY of the file load?
            wchar_t tmpPath[MAX_PATH]{};
            std::wcscpy(tmpPath, exeDir);
            std::wcscat(tmpPath, L"\\__preload_copy.dll");
            const BOOL copied = CopyFileW(full.c_str(), tmpPath, FALSE);
            const DWORD copyErr = GetLastError();
            SetLastError(0);
            const HMODULE copyLoaded = copied ? LoadLibraryW(tmpPath) : nullptr;
            const DWORD copyLoadErr = GetLastError();
            if (copyLoaded != nullptr) FreeLibrary(copyLoaded);
            DeleteFileW(tmpPath);
            std::printf("        read=%p err=%lu ; copy=%d err=%lu ; copy-load=%p err=%lu\n",
                static_cast<void*>(read), static_cast<unsigned long>(readErr),
                static_cast<int>(copied), static_cast<unsigned long>(copyErr),
                static_cast<void*>(copyLoaded), static_cast<unsigned long>(copyLoadErr));
        } else {
            std::printf("    PRELOAD %ls ok\n", name);
        }
    }
}

void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }
        Sleep(1);
    }
}

unsigned long long Digest(const std::vector<float>& pixels) {
    auto h = 0xcbf29ce484222325ULL;
    for (const float value : pixels) {
        h ^= static_cast<unsigned long long>(std::lround(value * 4096.0f) + 0x100000000LL);
        h *= 0x1000193ULL;
    }
    return h;
}

bool ReadRow(FFF3FPHandle player, int row, int width, std::vector<float>& out, unsigned* bits) {
    out.assign(static_cast<std::size_t>(width) * 4, 0.0f);
    std::uint32_t readbackBits = 0;
    const auto result = FFF3FP_ReadVideoPixelRegion(player, 0, static_cast<std::uint32_t>(row),
        static_cast<std::uint32_t>(width), 1, out.data(),
        static_cast<std::uint32_t>(out.size()), &readbackBits);
    if (bits != nullptr) *bits = readbackBits;
    return result == FFFResult::Success;
}

void FullFrameWindow(FFF3FPHdrMetadataWindow& w) {
    memset(&w, 0, sizeof(w));
    w.upperLeftX = 0.0f;
    w.upperLeftY = 0.0f;
    w.lowerRightX = 1.0f;
    w.lowerRightY = 1.0f;
    w.toneMappingFlag = 1;
    w.kneePointX = 0.30f;
    w.kneePointY = 0.12f;      // strongly non-identity: pulls mids down
    w.numCurveAnchors = 5;
    for (int i = 0; i < 5; ++i) w.curveAnchors[i] = 0.12f + 0.02f * i;
}

void StrongTable(FFF3FPHdrDynamicMetadataEntry& entry, std::int64_t frameIndex, float targetNits) {
    memset(&entry, 0, sizeof(entry));
    entry.size = sizeof(entry);
    entry.version = 1;
    entry.frameIndex = frameIndex;
    entry.numWindows = 1;
    FullFrameWindow(entry.windows[0]);
    entry.windows[0].maxScl[0] = entry.windows[0].maxScl[1] = entry.windows[0].maxScl[2] = 0.05f;
    entry.windows[0].averageMaxRgb = 0.01f;
    entry.targetedDisplayNits = targetNits;
}

// Sparse table for the matching-key arm. maxScl -> nits is value * 10000, so the
// three entries carry peaks 500 / 1000 / 4000 nits. Targeted values are chosen
// distinct from every peak so a peak/targeted swap cannot fake a pass.
struct MkEntry {
    std::int64_t frameIndex;
    float maxScl;
    float targetedNits;
};
constexpr MkEntry kMk[]{{10, 0.05f, 700.0f}, {30, 0.10f, 1400.0f}, {60, 0.40f, 2800.0f}};

void MkTable(FFF3FPHdrDynamicMetadataEntry& entry, const MkEntry& e) {
    memset(&entry, 0, sizeof(entry));
    entry.size = sizeof(entry);
    entry.version = 1;
    entry.frameIndex = e.frameIndex;
    entry.numWindows = 1;
    FullFrameWindow(entry.windows[0]);
    entry.windows[0].maxScl[0] = entry.windows[0].maxScl[1] = entry.windows[0].maxScl[2] = e.maxScl;
    entry.windows[0].averageMaxRgb = e.maxScl * 0.25f;
    entry.targetedDisplayNits = e.targetedNits;
}

struct State {
    std::uint32_t source = 9, windows = 0, format = 0, path = 0, outBits = 0, dyn = 0;
    std::uint32_t peak = 0, targeted = 0;
    std::int64_t frameIdx = -1;
    unsigned long long applied = 0, refused = 0, calls = 0, suppressed = 0;
    unsigned long long digest = 0;
    bool pixelsOk = false;
};

void ReadState(FFF3FPHandle player, int row, int width, State& out) {
    std::vector<float> pixels;
    out.pixelsOk = ReadRow(player, row, width, pixels, &out.outBits);
    out.digest = out.pixelsOk ? Digest(pixels) : 0ULL;

    FFF3FPHdrSignalDiagnostics d{};
    d.size = sizeof(d);
    d.version = 20;
    if (FFF3FP_GetHdrSignalDiagnostics(player, &d) == FFFResult::Success) {
        out.applied = d.dynamicCurveFramesApplied;
        out.refused = d.dynamicCurveFramesRefused;
        out.calls = d.setHdrMetaDataCalls;
        out.suppressed = d.setHdrMetaDataSuppressed;
    }
    FFF3FPSnapshot s{};
    s.size = sizeof(s);
    s.version = 20;
    if (FFF3FP_GetSnapshot(player, &s) == FFFResult::Success) {
        out.windows = s.dynamicMetadataWindows;
        out.format = static_cast<unsigned>(s.hdrFormat);
        out.path = static_cast<unsigned>(s.hdrProcessingPath);
        out.dyn = s.dynamicHdrMetadataActive;
        out.peak = s.sourcePeakNits;
        out.targeted = s.dynamicMetadataTargetedNits;
        out.frameIdx = s.frameIndex;
    }
    FFF3FP_GetHdrMetadataSource(player, &out.source);
}

void Dump(const char* label, const State& s) {
    std::printf("    %-14s pixels=%016llx src=%u wins=%u fmt=%u path=%u outbits=%u "
                "peak=%u tgt=%u f=%lld applied=%llu refused=%llu\n", label, s.digest, s.source,
        s.windows, s.format, s.path, s.outBits, s.peak, s.targeted, s.frameIdx, s.applied,
        s.refused);
}

FFFResult Configure(FFF3FPConfiguration& configuration, bool hdrArm, HWND window) {
    memset(&configuration, 0, sizeof(configuration));
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = hdrArm ? FFF3FPColorMode::MapToHdr : FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.forceHdrOutput = hdrArm ? 1u : 0u;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;
    return FFFResult::Success;
}

std::string ToUtf8(const wchar_t* wide) {
    std::string path;
    const int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    path.resize(n > 1 ? n - 1 : 0);
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, path.data(), n, nullptr, nullptr);
    return path;
}

int RunArm(const wchar_t* media, bool hdrArm, bool keepPlaying, bool control, int frame, int row,
           int width, HWND window) {
    FFF3FPConfiguration configuration{};
    Configure(configuration, hdrArm, window);
    g_openCompleted = false;
    FFF3FPHandle player = nullptr;
    const auto created = FFF3FP_Create(&configuration, &player);
    std::printf("    [trace] create -> %d handle=%p\n", static_cast<int>(created),
        static_cast<void*>(player));
    if (created != FFFResult::Success || player == nullptr) return 4;
    const std::string path = ToUtf8(media);
    const auto opened = FFF3FP_Open(player, path.c_str());
    std::printf("    [trace] open -> %d\n", static_cast<int>(opened));
    if (opened != FFFResult::Success) { FFF3FP_Destroy(player); return 5; }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    std::printf("    [trace] openCompleted=%d\n", g_openCompleted ? 1 : 0);
    if (FFF3FP_Play(player) != FFFResult::Success) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 6; }
    Pump(1500);
    if (!keepPlaying) {
        FFF3FP_SeekFrame(player, frame);
        Pump(700);
        FFF3FP_Pause(player);
        Pump(600);
    } else {
        Pump(600);
    }

    std::printf("  arm=%s state=%s%s\n", hdrArm ? "MapToHdr(16bit)" : "MapToSdr",
        keepPlaying ? "playing" : "paused", control ? " [CONTROL: no injection]" : "");

    if (control) {
        // Same sampling rhythm as the injection arm, zero Set/Clear calls: whatever the
        // digests do here is the baseline the playing arms' "CHANGED" must beat.
        State a{}, b{}, c{};
        ReadState(player, row, width, a);
        Dump("sample-a", a);
        Pump(1200);
        ReadState(player, row, width, b);
        Dump("sample-b", b);
        Pump(1200);
        ReadState(player, row, width, c);
        Dump("sample-c", c);
        const bool drift = a.pixelsOk && b.pixelsOk && c.pixelsOk &&
            (a.digest != b.digest || b.digest != c.digest);
        std::printf("    CONTROL digest drift without any injection: %s\n",
            drift ? "yes (expected while frames advance)" : "no");
        FFF3FP_Close(player);
        FFF3FP_Destroy(player);
        return 0;
    }

    State before{}, injected{}, cleared{};
    ReadState(player, row, width, before);
    Dump("before", before);

    FFF3FPHdrDynamicMetadataEntry entry{};
    StrongTable(entry, 0, 1000.0f);
    const auto install = FFF3FP_SetHdrDynamicMetadata(player, &entry, 1);
    Pump(1200);
    ReadState(player, row, width, injected);
    std::printf("    SetHdrDynamicMetadata -> %d\n", static_cast<int>(install));
    Dump("injected", injected);

    FFF3FP_ClearHdrDynamicMetadata(player);
    Pump(1200);
    ReadState(player, row, width, cleared);
    Dump("cleared", cleared);

    const long long appliedDelta =
        static_cast<long long>(injected.applied) - static_cast<long long>(before.applied);
    const long long refusedDelta =
        static_cast<long long>(injected.refused) - static_cast<long long>(before.refused);
    // On MapToHdr the renderer deliberately refuses the curve (docs/58 §4.5 T3);
    // refused+1 there is the DESIGN-correct outcome, not a failure.
    const bool curveRan = appliedDelta > 0;
    const bool curveRefusedByDesign = hdrArm && refusedDelta > 0 && appliedDelta == 0;
    if (keepPlaying) {
        // Frames advance between any two samples, so the digest proves nothing here;
        // the counters only move when the injection interface itself acted.
        const bool reached = appliedDelta > 0 || refusedDelta > 0;
        std::printf("    VERDICT counters applied+%lld refused+%lld -> injection %s\n",
            appliedDelta, refusedDelta,
            reached ? "REACHED the pipeline (digest judge void while playing)" :
                      "did NOT reach the pipeline");
    } else if (curveRefusedByDesign) {
        std::printf("    VERDICT curve refused by design (docs/58 §4.5 T3): path 1->%u, "
                    "src=%u, counters applied+%lld refused+%lld, digest untouched "
                    "(%016llx)\n", injected.path, injected.source, appliedDelta, refusedDelta,
            injected.digest);
    } else {
        const bool moved = before.pixelsOk && injected.pixelsOk && before.digest != injected.digest;
        const bool restored = cleared.pixelsOk && cleared.digest == before.digest;
        std::printf("    VERDICT injection %s picture (digest %016llx -> %016llx); clear %s; "
                    "counters applied+%lld refused+%lld\n",
            moved ? "CHANGED" : "did NOT change", before.digest, injected.digest,
            restored ? "restored it" : "did NOT restore", appliedDelta, refusedDelta);
    }

    // Contract: a malformed table must be rejected AND leave the previous table in
    // charge. The good table is re-installed first so this actually exercises that
    // clause (rev 1 ran the bad call after Clear and measured nothing).
    FFF3FPHdrDynamicMetadataEntry good{};
    StrongTable(good, 0, 1000.0f);
    const auto reinstall = FFF3FP_SetHdrDynamicMetadata(player, &good, 1);
    Pump(1000);
    State withGood{};
    ReadState(player, row, width, withGood);

    FFF3FPHdrDynamicMetadataEntry bad{};
    StrongTable(bad, 0, 1000.0f);
    bad.numWindows = 4;                       // out of ST 2094-40's [1,3]
    const auto rejected = FFF3FP_SetHdrDynamicMetadata(player, &bad, 1);
    Pump(400);
    State afterBad{};
    ReadState(player, row, width, afterBad);
    const bool stillInjected = afterBad.source == 1u;   // FFF3FPHdrMetadataSource::Injected
    const bool pixelsHeld = keepPlaying || !withGood.pixelsOk || !afterBad.pixelsOk ||
        afterBad.digest == withGood.digest;
    std::printf("    malformed(numWindows=4) -> %d (expect non-zero) ; good-table reinstall -> %d ; "
                "src=%u (expect 1=Injected) ; pixels %s\n",
        static_cast<int>(rejected), static_cast<int>(reinstall), afterBad.source,
        pixelsHeld ? "held (good table still in force)" : "CHANGED (contract violation)");
    FFF3FP_ClearHdrDynamicMetadata(player);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    const bool ok = keepPlaying ? (appliedDelta > 0 || refusedDelta > 0)
                                : (curveRefusedByDesign ||
                                   (before.pixelsOk && injected.pixelsOk &&
                                    before.digest != injected.digest)) &&
                                  pixelsHeld;
    return ok ? 0 : 1;
}

// Sparse-table matching key: "last entry whose frameIndex <= current frame wins;
// frames below the first entry get the first entry." Peak ownership: an entry with a
// valid maxScl owns snapshot.sourcePeakNits == maxScl * 10000 and its targeted value
// rides along in dynamicMetadataTargetedNits.
int RunMatchingKeyArm(const wchar_t* media, int row, int width, HWND window) {
    std::printf("  arm=matching-key (MapToSdr, seek+pause over frames 0/5/9/10/29/30/59/60/75)\n");
    FFF3FPConfiguration configuration{};
    Configure(configuration, false, window);
    g_openCompleted = false;
    FFF3FPHandle player = nullptr;
    const auto created = FFF3FP_Create(&configuration, &player);
    std::printf("    [trace] create -> %d handle=%p\n", static_cast<int>(created),
        static_cast<void*>(player));
    if (created != FFFResult::Success || player == nullptr) return 4;
    const std::string path = ToUtf8(media);
    const auto opened = FFF3FP_Open(player, path.c_str());
    std::printf("    [trace] open -> %d\n", static_cast<int>(opened));
    if (opened != FFFResult::Success) { FFF3FP_Destroy(player); return 5; }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    std::printf("    [trace] openCompleted=%d\n", g_openCompleted ? 1 : 0);
    if (!g_openCompleted) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 5; }

    // Lazy install: the table posted before any frame is indexed must stay inert
    // (HdrProcessor.cpp: "only an indexed frame may move the key").
    FFF3FPHdrDynamicMetadataEntry table[3];
    for (int i = 0; i < 3; ++i) MkTable(table[i], kMk[i]);
    const auto early = FFF3FP_SetHdrDynamicMetadata(player, table, 3);
    std::printf("    [trace] early set -> %d\n", static_cast<int>(early));
    Pump(500);
    State lazy{};
    ReadState(player, row, width, lazy);
    const bool lazyOk = lazy.source == 0u && lazy.applied == 0ULL;   // still Bitstream, curve idle
    std::printf("    lazy-install(before Play) -> %d ; src=%u (expect 0=Bitstream) applied=%llu -> %s\n",
        static_cast<int>(early), lazy.source, lazy.applied, lazyOk ? "lazy as documented" : "NOT lazy");

    if (FFF3FP_Play(player) != FFFResult::Success) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 6; }
    Pump(1200);
    FFF3FP_Pause(player);
    Pump(400);

    int fails = 0, points = 0;
    for (const int f : {0, 5, 9, 10, 29, 30, 59, 60, 75}) {
        FFF3FP_SeekFrame(player, f);
        Pump(800);
        State st{};
        ReadState(player, row, width, st);
        // "Last entry whose frameIndex <= current frame wins": entry@10 covers
        // frames 10..29 (and, via the below-first fallback, 0..9 too), entry@30
        // covers 30..59, entry@60 covers 60 and beyond.
        const std::uint32_t expectPeak = f < 30 ? 500u : f < 60 ? 1000u : 4000u;
        const std::uint32_t expectTargeted = f < 30 ? 700u : f < 60 ? 1400u : 2800u;
        const bool peakOk = st.source == 1u && st.peak == expectPeak;
        const bool targetedOk = st.targeted == expectTargeted;
        ++points;
        if (!peakOk || !targetedOk) ++fails;
        std::printf("    frame %2d: snap.frame=%lld src=%u peak=%u (exp %u) targeted=%u (exp %u) "
                    "wins=%u -> %s\n", f, st.frameIdx, st.source, st.peak, expectPeak, st.targeted,
            expectTargeted, st.windows, peakOk && targetedOk ? "ok" : "MISMATCH");
    }
    FFF3FP_ClearHdrDynamicMetadata(player);
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    std::printf("    MATCHING-KEY %s (%d/%d points)%s\n", fails == 0 ? "PASS" : "FAIL",
        points - fails, points, lazyOk ? "" : " ; lazy-install expectation violated");
    return (fails == 0 && lazyOk) ? 0 : 1;
}

// Generic carrier arm (FFF3FP_SetDynamicMetadata / ClearDynamicMetadata): the
// kind-tagged channel must behave exactly like the dedicated ST 2094-40 entry
// point, carry HdrVivid with peak-only semantics, refuse unknown kinds with the
// previous table in force, support selective clear, and share one table with
// the legacy channel (wholesale replace).
int RunGenericArm(const wchar_t* media, int frame, int row, int width, HWND window) {
    std::printf("  arm=generic-carrier (MapToSdr, paused)\n");
    FFF3FPConfiguration configuration{};
    Configure(configuration, false, window);
    g_openCompleted = false;
    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) return 4;
    const std::string path = ToUtf8(media);
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) { FFF3FP_Destroy(player); return 5; }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    if (FFF3FP_Play(player) != FFFResult::Success) { FFF3FP_Close(player); FFF3FP_Destroy(player); return 6; }
    Pump(1500);
    FFF3FP_SeekFrame(player, frame);
    Pump(700);
    FFF3FP_Pause(player);
    Pump(600);

    int fails = 0;
    State before{};
    ReadState(player, row, width, before);
    if (!before.pixelsOk) {
        // In --playing runs this arm's pause can land at end-of-stream, where
        // pixel readback fails; settle on a mid-clip frame instead.
        FFF3FP_SeekFrame(player, 60);
        Pump(1200);
        ReadState(player, row, width, before);
    }
    Dump("baseline", before);

    // (1) kind=Hdr10Plus through the carrier == the legacy path's behaviour.
    FFF3FPHdrDynamicMetadataEntry h10p{};
    StrongTable(h10p, 0, 1000.0f);
    FFF3FPDynamicMetadataPacket p1{};
    p1.size = sizeof(p1);
    p1.version = 1;
    p1.kind = 1;                       // FFF3FPDynamicMetadataKind::Hdr10Plus
    p1.kindVersion = 1;
    p1.frameIndex = 0;
    p1.payloadSize = sizeof(h10p);
    p1.payload = &h10p;
    const auto r1 = FFF3FP_SetDynamicMetadata(player, &p1, 1);
    Pump(1000);
    State s1{};
    ReadState(player, row, width, s1);
    const bool ok1 = r1 == FFFResult::Success && s1.source == 1u && s1.path == 2u &&
        s1.applied > before.applied && s1.digest != before.digest;
    if (!ok1) ++fails;
    std::printf("    carrier/Hdr10Plus -> %d src=%u path=%u (exp 2) applied+%llu digest %s\n",
        static_cast<int>(r1), s1.source, s1.path, s1.applied - before.applied,
        s1.digest != before.digest ? "moved" : "UNCHANGED");

    // (2) kind=HdrVivid replaces the table: peak-only reading, path 6.
    FFF3FPHdrVividMetadataPayload viv{};
    viv.size = sizeof(viv);
    viv.version = 1;
    viv.numWindows = 1;
    viv.reserved = 0;
    viv.windows[0].maxRGB[0] = viv.windows[0].maxRGB[1] = viv.windows[0].maxRGB[2] = 0.30f;
    viv.windows[0].averageMaxRGB = 0.10f;
    viv.targetedDisplayNits = 2000.0f;
    FFF3FPDynamicMetadataPacket p2{};
    p2.size = sizeof(p2);
    p2.version = 1;
    p2.kind = 2;                       // FFF3FPDynamicMetadataKind::HdrVivid
    p2.kindVersion = 1;
    p2.frameIndex = 0;
    p2.payloadSize = sizeof(viv);
    p2.payload = &viv;
    const auto r2 = FFF3FP_SetDynamicMetadata(player, &p2, 1);
    Pump(1000);
    State s2{};
    ReadState(player, row, width, s2);
    const bool ok2 = r2 == FFFResult::Success && s2.source == 1u && s2.path == 6u &&
        s2.windows == 1u && s2.targeted == 2000u && s2.digest != s1.digest;
    if (!ok2) ++fails;
    // Note: snapshot.peak is renderer-frame-owned (PlayerSession reads
    // videoRenderer_.SourcePeakNits(), refreshed when a frame is RENDERED), so a
    // paused install moves src/path/targeted immediately but the peak follows on
    // the next rendered frame -- exactly the snapshot-lag the contract documents.
    std::printf("    carrier/HdrVivid -> %d src=%u path=%u (exp 6) wins=%u tgt=%u (exp 2000) "
                "peak=%u (renderer-frame-owned) digest %s\n", static_cast<int>(r2), s2.source,
        s2.path, s2.windows, s2.targeted, s2.peak, s2.digest != s1.digest ? "moved" : "UNCHANGED");

    // (3) unknown kind: NotSupported, previous (Vivid) table stays in force.
    FFF3FPDynamicMetadataPacket p3 = p2;
    p3.kind = 99;
    const auto r3 = FFF3FP_SetDynamicMetadata(player, &p3, 1);
    State s3{};
    ReadState(player, row, width, s3);
    const bool ok3 = r3 != FFFResult::Success && s3.source == 1u && s3.path == 6u &&
        s3.digest == s2.digest;
    if (!ok3) ++fails;
    std::printf("    carrier/kind=99 -> %d (expect non-zero) ; table held: src=%u path=%u "
                "digest %s\n", static_cast<int>(r3), s3.source, s3.path,
        s3.digest == s2.digest ? "held" : "CHANGED");

    // (4) selective clear of the Vivid kind returns the picture to bitstream-only.
    const auto r4 = FFF3FP_ClearDynamicMetadata(player, 2);
    Pump(800);
    State s4{};
    ReadState(player, row, width, s4);
    const bool ok4 = r4 == FFFResult::Success && s4.source == 0u && s4.path == 1u &&
        s4.digest == before.digest;
    if (!ok4) ++fails;
    std::printf("    clear(kind=HdrVivid) -> %d src=%u path=%u digest %s\n",
        static_cast<int>(r4), s4.source, s4.path,
        s4.digest == before.digest ? "restored" : "NOT restored");

    // (5) the two channels share one table: a legacy call replaces wholesale.
    (void)FFF3FP_SetDynamicMetadata(player, &p2, 1);
    Pump(600);
    FFF3FPHdrDynamicMetadataEntry legacy{};
    StrongTable(legacy, 0, 1000.0f);
    const auto r5 = FFF3FP_SetHdrDynamicMetadata(player, &legacy, 1);
    Pump(1000);
    State s5{};
    ReadState(player, row, width, s5);
    const bool ok5 = r5 == FFFResult::Success && s5.source == 1u && s5.path == 2u &&
        s5.targeted == 1000u;
    if (!ok5) ++fails;
    std::printf("    legacy-after-carrier -> %d src=%u path=%u (exp 2, wholesale replace) "
                "tgt=%u peak=%u (renderer-frame-owned)\n", static_cast<int>(r5), s5.source,
        s5.path, s5.targeted, s5.peak);

    // (6) matching key through the carrier: same sparse-table rule.
    FFF3FPHdrDynamicMetadataEntry mk[3];
    for (int i = 0; i < 3; ++i) MkTable(mk[i], kMk[i]);
    FFF3FPDynamicMetadataPacket mp[3]{};   // zero-init: reserved must be 0
    for (int i = 0; i < 3; ++i) {
        mp[i].size = sizeof(mp[i]);
        mp[i].version = 1;
        mp[i].kind = 1;
        mp[i].kindVersion = 1;
        mp[i].frameIndex = kMk[i].frameIndex;
        mp[i].payloadSize = sizeof(mk[i]);
        mp[i].payload = &mk[i];
    }
    const auto r6 = FFF3FP_SetDynamicMetadata(player, mp, 3);
    FFF3FP_SeekFrame(player, 30);
    Pump(800);
    State s6{};
    ReadState(player, row, width, s6);
    if (s6.frameIdx != 30) {
        // A backward seek while paused can land on a keyframe; re-issue and let
        // the session settle before judging the matching rule.
        FFF3FP_SeekFrame(player, 30);
        Pump(1200);
        ReadState(player, row, width, s6);
    }
    // A rendered frame carries the entry's own peak (maxScl * 10000), so after a
    // seek the peak must follow the matching entry too.
    const bool ok6 = r6 == FFFResult::Success && s6.frameIdx == 30 && s6.source == 1u &&
        s6.path == 2u && s6.peak == 1000u && s6.targeted == 1400u;
    if (!ok6) ++fails;
    std::printf("    carrier matching @frame30: install=%d snap.frame=%lld src=%u path=%u "
                "peak=%u (exp 1000) tgt=%u (exp 1400)\n", static_cast<int>(r6), s6.frameIdx,
        s6.source, s6.path, s6.peak, s6.targeted);

    FFF3FP_ClearDynamicMetadata(player, 0);
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    std::printf("    GENERIC-CARRIER %s (%d checks failed)\n", fails == 0 ? "PASS" : "FAIL", fails);
    return fails == 0 ? 0 : 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    PreloadDelayDependencies();
    SetUnhandledExceptionFilter([](EXCEPTION_POINTERS* info) -> LONG {
        HMODULE module = nullptr;
        char modulePath[MAX_PATH] = "?";
        wchar_t wpath[MAX_PATH]{};
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(info->ExceptionRecord->ExceptionAddress), &module) &&
            GetModuleFileNameW(module, wpath, MAX_PATH) > 0) {
            WideCharToMultiByte(CP_UTF8, 0, wpath, -1, modulePath, MAX_PATH, nullptr, nullptr);
        }
        std::printf("FATAL exception code=0x%08lX addr=%p module=%s\n",
            static_cast<unsigned long>(info->ExceptionRecord->ExceptionCode),
            info->ExceptionRecord->ExceptionAddress, modulePath);
        // Delay-load failures carry the requested DLL/function name in the parameter
        // block as ANSI char* (VcppException(ERROR_MOD_NOT_FOUND) layout).
        if (info->ExceptionRecord->NumberParameters >= 1) {
            for (unsigned i = 0; i < info->ExceptionRecord->NumberParameters && i < 4; ++i) {
                const auto p = info->ExceptionRecord->ExceptionInformation[i];
                std::printf("    param[%u]=0x%p", i, reinterpret_cast<void*>(p));
                if (i < 2 && p != 0) {
                    const char* s = reinterpret_cast<const char*>(p);
                    __try { std::printf(" \"%s\"", s); }
                    __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                                  ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
                        std::printf(" <unreadable>");
                    }
                }
                std::printf("\n");
            }
        }
        std::fflush(stdout);
        return EXCEPTION_EXECUTE_HANDLER;
    });
    if (argc < 2) {
        std::printf("usage: hdr_inject_audit <strippedMedia> [--frame N] [--row Y] "
                    "[--width N] [--playing] [--control]\n");
        return 2;
    }
    int frame = 120, row = 270, width = 256;
    bool playing = false, control = false;
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        const auto next = [&]() -> int { return i + 1 < argc ? _wtoi(argv[++i]) : 0; };
        if (key == L"--frame") frame = next();
        else if (key == L"--row") row = next();
        else if (key == L"--width") width = next();
        else if (key == L"--playing") playing = true;
        else if (key == L"--control") control = true;
    }

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"hdrInjectAudit";
    RegisterClassW(&wc);
    const HWND window = CreateWindowExW(0, wc.lpszClassName, L"HDR injection audit",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540, nullptr, nullptr, wc.hInstance, nullptr);
    if (window == nullptr) return 1;
    Pump(300);

    std::printf("carrier=%s frame=%d row=%d width=%d mode=%s\n",
        ToUtf8(argv[1]).c_str(), frame, row, width, playing ? "playing" : "paused");
    const int sdr = RunArm(argv[1], false, playing, control, frame, row, width, window);
    const int hdr = RunArm(argv[1], true, playing, control, frame, row, width, window);
    const int mk = RunMatchingKeyArm(argv[1], row, width, window);
    const int gen = control ? 0 : RunGenericArm(argv[1], frame, row, width, window);
    std::printf("SUMMARY matching-key=%s MapToSdr=%s MapToHdr=%s generic-carrier=%s\n",
        mk == 0 ? "PASS" : "FAIL",
        control ? "(control run)" : (sdr == 0 ? "influences" : "NO EFFECT"),
        control ? "(control run)" : (hdr == 0 ? "design-refused/influences" : "NO EFFECT"),
        control ? "(control run)" : (gen == 0 ? "PASS" : "FAIL"));
    DestroyWindow(window);
    return 0;
}
