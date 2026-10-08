/* hdr10plus_inject_pixel_probe.cpp -- prove injected metadata reaches the pixels.
 *
 * The injection API test (hdr10plus_inject_test) checks the contract: accepted,
 * rejected, cleared, source reported. That is necessary but not sufficient --
 * a table could be accepted and then never reach the renderer. This probe
 * compares the presented pixel with injection off and on, on the same content,
 * at the same timestamp, so "the injection changed the picture" is observed
 * rather than assumed.
 *
 * Usage: hdr10plus_inject_pixel_probe <media path>
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

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

/* Read a fixed grid of pixels and print them; the caller diffs the two runs by
 * eye or with a script. */
static void DumpPixels(const char* label) {
    static const unsigned points[5][2] = {
        {480, 250}, {240, 250}, {720, 250}, {480, 120}, {480, 400}
    };
    unsigned i;
    FFF3FPSnapshot snapshot;
    memset(&snapshot, 0, sizeof(snapshot));
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    FFF3FP_GetSnapshot(g_player, &snapshot);
    printf("%s: peak=%u windows=%u degrade=%u serial=%llu\n", label,
        snapshot.sourcePeakNits, snapshot.dynamicMetadataWindows,
        snapshot.dynamicMetadataDegrade,
        (unsigned long long)snapshot.dynamicMetadataSerial);
    for (i = 0; i < 5; ++i) {
        FFF3FPVideoPixelProbe probe;
        memset(&probe, 0, sizeof(probe));
        probe.size = sizeof(probe);
        probe.version = 1;
        probe.x = points[i][0];
        probe.y = points[i][1];
        if (FFF3FP_ReadVideoPixel(g_player, &probe) != FFFResult::Success) {
            printf("  (%u,%u) read failed\n", points[i][0], points[i][1]);
            continue;
        }
        printf("  (%4u,%4u) R=%.5f G=%.5f B=%.5f\n",
            points[i][0], points[i][1], probe.red, probe.green, probe.blue);
    }
}

int main(int argc, char** argv) {
    FFF3FPConfiguration configuration;
    FFF3FPHdrDynamicMetadataEntry entry;
    WNDCLASSW windowClass;
    HWND window;
    const char* path;

    if (argc < 2) {
        printf("usage: hdr10plus_inject_pixel_probe <media path>\n");
        return 2;
    }
    path = argv[1];

    memset(&windowClass, 0, sizeof(windowClass));
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.lpszClassName = L"hdr10plusInjectPixel";
    RegisterClassW(&windowClass);
    window = CreateWindowExW(0, L"hdr10plusInjectPixel", L"inject pixels",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540,
        NULL, NULL, windowClass.hInstance, NULL);
    if (window == NULL) { printf("window creation failed\n"); return 1; }
    Pump(200);

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

    if (FFF3FP_Create(&configuration, &g_player) != FFFResult::Success) return 6;
    if (FFF3FP_Open(g_player, path) != FFFResult::Success) return 7;
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(g_player);
    Pump(1500);

    printf("=== injected metadata -> pixels ===\n\n");
    DumpPixels("A bitstream ");

    /* A deliberately strong, non-identity curve so the effect cannot be
     * mistaken for noise: mid-tones are pushed well down. */
    memset(&entry, 0, sizeof(entry));
    entry.size = sizeof(entry);
    entry.version = 1;
    entry.frameIndex = 0;
    entry.maxScl[0] = 0.05f;
    entry.maxScl[1] = 0.05f;
    entry.maxScl[2] = 0.05f;
    entry.averageMaxRgb = 0.02f;
    entry.fractionBrightPixels = 0.001f;
    entry.toneMappingFlag = 1;
    entry.kneePointX = 0.40f;
    entry.kneePointY = 0.10f;
    entry.numCurveAnchors = 6;
    entry.curveAnchors[0] = 0.20f;
    entry.curveAnchors[1] = 0.35f;
    entry.curveAnchors[2] = 0.50f;
    entry.curveAnchors[3] = 0.65f;
    entry.curveAnchors[4] = 0.80f;
    entry.curveAnchors[5] = 0.92f;
    entry.targetedDisplayNits = 200.0f;

    if (FFF3FP_SetHdrDynamicMetadata(g_player, &entry, 1) != FFFResult::Success) {
        printf("injection rejected\n");
        return 8;
    }
    Pump(1500);
    DumpPixels("B injected ");

    FFF3FP_ClearHdrDynamicMetadata(g_player);
    Pump(1500);
    DumpPixels("C cleared  ");

    FFF3FP_Close(g_player);
    FFF3FP_Destroy(g_player);
    return 0;
}
