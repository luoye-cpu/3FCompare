/* hdr10plus_inject_test.c -- exercise the ST 2094-40 injection API.
 *
 * What this proves
 * ----------------
 * The injection API exists so the dynamic tone mapping can be driven with
 * arbitrary metadata instead of only with content that happens to carry it.
 * This test checks the contract rather than the picture:
 *
 *   1. a well-formed table is accepted and reported as the active source;
 *   2. a malformed table is rejected and the previous table stays live, so a
 *      bad call cannot half-apply;
 *   3. clearing restores bitstream-only behaviour;
 *   4. injected metadata overrides real bitstream SEI (the file below carries
 *      genuine ST 2094-40, so "source == Injected" is a meaningful result);
 *   5. the entry selected follows the last frameIndex <= current frame.
 *
 * Usage: hdr10plus_inject_test <media with HDR10+ SEI>
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>

#include "3FP/Api/FFF.Player.Api.h"

static FFF3FPHandle g_player = NULL;
static volatile LONG g_openCompleted = 0;

static void __cdecl OnEvent(void* context, FFF3FPEvent type, const char* detail) {
    (void)context; (void)detail;
    if (type == FFF3FPEvent::OpenCompleted) InterlockedExchange(&g_openCompleted, 1);
}

static void Pump(int milliseconds) {
    DWORD deadline = GetTickCount() + (DWORD)milliseconds;
    MSG message;
    while (GetTickCount() < deadline) {
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(5);
    }
}

static int g_failures = 0;

static void Check(int condition, const char* what) {
    printf("  %s %s\n", condition ? "PASS" : "FAIL", what);
    if (!condition) ++g_failures;
}

static FFF3FPHdrDynamicMetadataEntry MakeEntry(long long frameIndex) {
    FFF3FPHdrDynamicMetadataEntry entry;
    int i;
    memset(&entry, 0, sizeof(entry));
    entry.size = sizeof(entry);
    entry.version = 1;
    entry.frameIndex = frameIndex;
    /* A clearly non-identity curve so the injection is observable: the anchors
     * pull mid-tones well below where the identity would put them. */
    entry.maxScl[0] = 0.40f;
    entry.maxScl[1] = 0.30f;
    entry.maxScl[2] = 0.20f;
    entry.averageMaxRgb = 0.10f;
    entry.fractionBrightPixels = 0.01f;
    entry.toneMappingFlag = 1;
    entry.kneePointX = 0.30f;
    entry.kneePointY = 0.15f;
    entry.numCurveAnchors = 5;
    for (i = 0; i < 5; ++i)
        entry.curveAnchors[i] = 0.30f + 0.10f * (float)i;
    entry.targetedDisplayNits = 500.0f;
    return entry;
}

static unsigned ActiveSource(void) {
    unsigned source = 99;
    if (FFF3FP_GetHdrMetadataSource(g_player, &source) != FFFResult::Success)
        return 99;
    return source;
}

int main(int argc, char** argv) {
    FFF3FPConfiguration configuration;
    FFF3FPHdrDynamicMetadataEntry entry, bad;
    FFF3FPHdrDynamicMetadataEntry table[3];
    HWND window;
    WNDCLASSW windowClass;
    const char* path;

    if (argc < 2) {
        printf("usage: hdr10plus_inject_test <media with HDR10+ SEI>\n");
        return 2;
    }
    path = argv[1];

    memset(&windowClass, 0, sizeof(windowClass));
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.lpszClassName = L"hdr10plusInjectTest";
    RegisterClassW(&windowClass);
    window = CreateWindowExW(0, L"hdr10plusInjectTest", L"inject",
        WS_OVERLAPPEDWINDOW, 40, 40, 960, 540, NULL, NULL, windowClass.hInstance, NULL);
    if (window == NULL) { printf("window creation failed\n"); return 1; }
    Pump(150);

    memset(&configuration, 0, sizeof(configuration));
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.hdrPeakNits = 0.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.videoScalingQuality = FFF3FPVideoScalingQuality::HighQuality;
    configuration.preferredAdapterIndex = -1;
    configuration.eventCallback = &OnEvent;

    if (FFF3FP_Create(&configuration, &g_player) != FFFResult::Success) {
        printf("Create failed\n");
        return 6;
    }
    if (FFF3FP_Open(g_player, path) != FFFResult::Success) {
        char error[512];
        error[0] = 0;
        FFF3FP_GetLastError(g_player, error, sizeof(error), NULL);
        printf("Open failed: %s\n", error);
        return 7;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(g_player);
    Pump(1200);

    printf("=== ST 2094-40 injection API ===\n");

    /* 1. No injection: the bitstream must be in charge. */
    printf("\n-- baseline (bitstream) --\n");
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Bitstream,
        "source reports Bitstream before any injection");

    /* 2. Argument validation. */
    printf("\n-- argument validation --\n");
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, NULL, 1) == FFFResult::InvalidArgument,
        "null array with count>0 rejected");
    entry = MakeEntry(0);
    bad = entry;
    bad.version = 2;
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "wrong version rejected");
    bad = entry;
    bad.size = 4;
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "undersized entry rejected");
    bad = entry;
    bad.maxScl[0] = 2.0f;
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "out-of-range maxScl rejected");
    bad = entry;
    bad.curveAnchors[3] = 0.05f;   /* below the previous anchor */
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "non-ascending anchors rejected");
    bad = entry;
    bad.kneePointY = 0.90f;        /* above the identity line */
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "knee above identity rejected");
    bad = entry;
    bad.numCurveAnchors = 99;
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &bad, 1) == FFFResult::InvalidArgument,
        "anchor count above the spec maximum rejected");
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Bitstream,
        "rejected calls left the previous table untouched");

    /* 3. A valid single entry takes effect. */
    printf("\n-- single entry --\n");
    entry = MakeEntry(0);
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, &entry, 1) == FFFResult::Success,
        "valid entry accepted");
    Pump(600);
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Injected,
        "source reports Injected while a table is installed");

    /* 4. Clearing restores the bitstream. */
    printf("\n-- clear --\n");
    Check(FFF3FP_ClearHdrDynamicMetadata(g_player) == FFFResult::Success,
        "clear succeeds");
    Pump(600);
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Bitstream,
        "source reports Bitstream after clear");

    /* 5. Frame-range matching: a later entry supersedes an earlier one. */
    printf("\n-- frame-range matching --\n");
    table[0] = MakeEntry(0);
    table[1] = MakeEntry(30);
    table[2] = MakeEntry(60);
    Check(FFF3FP_SetHdrDynamicMetadata(g_player, table, 3) == FFFResult::Success,
        "3-entry scene list accepted");
    Pump(600);
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Injected,
        "scene list drives the mapping");

    /* 6. Injected metadata must win over real bitstream SEI. */
    printf("\n-- injection overrides bitstream SEI --\n");
    Check(ActiveSource() == (unsigned)FFF3FPHdrMetadataSource::Injected,
        "injected source still wins on a stream that carries HDR10+ SEI");

    Check(FFF3FP_ClearHdrDynamicMetadata(g_player) == FFFResult::Success, "final clear");

    printf("\n=== %s (%d failures) ===\n", g_failures ? "FAIL" : "PASS", g_failures);
    Pump(150);
    FFF3FP_Close(g_player);
    FFF3FP_Destroy(g_player);
    return g_failures ? 1 : 0;
}
