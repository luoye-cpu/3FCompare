/* hdr10plus_identity_probe.cpp -- the decisive domain test.
 *
 * The dynamic path composes two stages:
 *   1. the ST 2094 per-window curve, in the normalised signal domain
 *   2. BT.2390 compressing the content's targeted range onto this display
 *
 * If the curve is set to EXACTLY the identity, stage 1 is a no-op and the whole
 * expression reduces to
 *     Bt2390HdrToSdrPq(originalIntensity, targetedDisplay, SdrPeak)
 * which is the SAME function the static path calls, with the stream peak in
 * place of the targeted display. So with targetedDisplay == the stream's own
 * peak the two paths must agree closely.
 *
 * That makes this a precise oracle: it separates "the curve stage is wrong"
 * from "the composition/compression stage is wrong", which reasoning about the
 * formulas alone did not resolve.
 *
 * Usage: hdr10plus_identity_probe <media path>
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

static float ReadLuma(unsigned x, unsigned y) {
    FFF3FPVideoPixelProbe probe;
    memset(&probe, 0, sizeof(probe));
    probe.size = sizeof(probe);
    probe.version = 20;
    probe.x = x;
    probe.y = y;
    if (FFF3FP_ReadVideoPixel(g_player, &probe) != FFFResult::Success) return -1.0f;
    return 0.2126f * probe.red + 0.7152f * probe.green + 0.0722f * probe.blue;
}

/* Identity curve: f(x) = x.
 *
 * kneePointX == kneePointY puts the knee exactly on the identity line, and the
 * anchors below continue that line across [knee, 1], so the reconstructed curve
 * is the identity everywhere. Anchor x positions are knee + (1-knee)*(i+1)/(n+1),
 * so the identity requires curveAnchors[i] to equal exactly those x values. */
static void ApplyIdentityCurve(float targetedNits, float declaredPeakNits) {
    FFF3FPHdrDynamicMetadataEntry entry;
    const float knee = 0.40f;
    const int anchors = 5;
    int i;
    memset(&entry, 0, sizeof(entry));
    entry.size = sizeof(entry);
    entry.version = 1;
    entry.frameIndex = 0;
    entry.numWindows = 1;
    entry.windows[0].upperLeftX = 0.0f;
    entry.windows[0].upperLeftY = 0.0f;
    entry.windows[0].lowerRightX = 1.0f;
    entry.windows[0].lowerRightY = 1.0f;
    entry.windows[0].maxScl[0] = entry.windows[0].maxScl[1] =
        entry.windows[0].maxScl[2] = declaredPeakNits / 10000.0f;
    entry.windows[0].averageMaxRgb = declaredPeakNits / 40000.0f;
    entry.windows[0].toneMappingFlag = 1;
    entry.windows[0].kneePointX = knee;
    entry.windows[0].kneePointY = knee;
    entry.windows[0].numCurveAnchors = (unsigned)anchors;
    for (i = 0; i < anchors; ++i)
        entry.windows[0].curveAnchors[i] = knee + (1.0f - knee) * (float)(i + 1) / (float)(anchors + 1);
    entry.targetedDisplayNits = targetedNits;

    if (FFF3FP_SetHdrDynamicMetadata(g_player, &entry, 1) != FFFResult::Success) {
        printf("identity entry REJECTED (targeted=%.0f declaredPeak=%.0f)\n",
            targetedNits, declaredPeakNits);
        return;
    }
    Pump(1100);
    printf("identity curve, declaredPeak=%5.0f targeted=%5.0f : "
           "x=160/480/800 -> %.4f %.4f %.4f\n",
        declaredPeakNits, targetedNits,
        ReadLuma(160, 170), ReadLuma(480, 170), ReadLuma(800, 170));
}

int main(int argc, char** argv) {
    FFF3FPConfiguration configuration;
    FFF3FPSnapshot snapshot;
    WNDCLASSW windowClass;
    HWND window;
    float staticLuma, streamPeak;

    if (argc < 2) { printf("usage: hdr10plus_identity_probe <media>\n"); return 2; }

    memset(&windowClass, 0, sizeof(windowClass));
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.lpszClassName = L"hdr10plusIdentity";
    RegisterClassW(&windowClass);
    window = CreateWindowExW(0, L"hdr10plusIdentity", L"identity",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 960, 540,
        NULL, NULL, windowClass.hInstance, NULL);
    if (window == NULL) return 1;
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
    if (FFF3FP_Open(g_player, argv[1]) != FFFResult::Success) return 7;
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);
    FFF3FP_Play(g_player);
    Pump(1500);

    printf("=== identity curve: isolates stage 1 from stage 2 ===\n\n");

    memset(&snapshot, 0, sizeof(snapshot));
    snapshot.size = sizeof(snapshot);
    snapshot.version = 20;
    FFF3FP_GetSnapshot(g_player, &snapshot);
    streamPeak = (float)snapshot.sourcePeakNits;
    staticLuma = ReadLuma(160, 170);
    printf("static path (no injection)        : x=160/480/800 -> %.4f %.4f %.4f\n",
        staticLuma, ReadLuma(480, 170), ReadLuma(800, 170));
    printf("stream sourcePeak = %.0f nits\n\n", streamPeak);

    /* I1: identity curve, targeted display == the stream's own peak.
     * Stage 2 then reduces to the static mapping, so the output must match. */
    ApplyIdentityCurve(streamPeak > 0.0f ? streamPeak : 1000.0f, streamPeak > 0.0f ? streamPeak : 1000.0f);

    /* I2: identity curve, a much larger declared peak. Stage 1 still a no-op,
     * so any change is stage 2 reacting to the declared peak. */
    ApplyIdentityCurve(500.0f, 6000.0f);

    /* I3: identity curve, small peak. */
    ApplyIdentityCurve(500.0f, 500.0f);

    FFF3FP_ClearHdrDynamicMetadata(g_player);
    FFF3FP_Close(g_player);
    FFF3FP_Destroy(g_player);
    return 0;
}
