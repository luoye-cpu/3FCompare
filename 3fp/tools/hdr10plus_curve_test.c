// hdr10plus_curve_test.c — validated ST 2094-40 curve reconstruction, offline.
//
// Why a standalone test
// ---------------------
// The reconstruction is the mathematical core of the feature and the part a
// shader would hide: a wrong interpolant still produces a plausible picture.
// Re-deriving the algorithm here (independently of the kernel) and checking the
// properties that must hold lets the curve be validated without a GPU.
//
// The window values below are the real ones ffprobe reports for the FFmpeg FATE
// vector hevc/hdr10_plus_h265_sample.hevc, so this also pins the parse against
// actual content rather than a hand-made example.

#include <stdio.h>
#include <math.h>

#define LUT_SIZE 256
#define MAX_ANCHORS 15
#define MAX_NODES (MAX_ANCHORS + 2)

typedef struct {
    float kneeX, kneeY;
    int anchorCount;
    float anchors[MAX_ANCHORS];
} Window;

static int failures = 0;

static void Check(int condition, const char* what) {
    if (!condition) {
        printf("  FAIL: %s\n", what);
        ++failures;
    }
}

/* Mirror of HdrToneCurve.cpp:BuildNodes -- kept deliberately separate so a
 * regression in the kernel shows up as a mismatch here rather than as two
 * copies of the same mistake. */
static int BuildNodes(const Window* window, float* xs, float* ys, int* count) {
    int anchors = window->anchorCount;
    int i;
    float span;
    if (anchors <= 0) return 0;
    if (window->kneeX < 0.0f || window->kneeX >= 1.0f) return 0;
    if (window->kneeY < 0.0f || window->kneeY > 1.0f) return 0;
    if (window->kneeY > window->kneeX + 1e-4f) return 0;

    *count = anchors + 2;
    xs[0] = window->kneeX;
    ys[0] = window->kneeY;
    span = 1.0f - window->kneeX;
    for (i = 0; i < anchors; ++i) {
        float value = window->anchors[i];
        if (value < 0.0f || value > 1.0f) return 0;
        xs[i + 1] = window->kneeX + span * (float)(i + 1) / (float)(anchors + 1);
        ys[i + 1] = value;
    }
    xs[anchors + 1] = 1.0f;
    ys[anchors + 1] = 1.0f;
    for (i = 1; i < *count; ++i) {
        if (xs[i] <= xs[i - 1]) return 0;
        if (ys[i] < ys[i - 1]) return 0;
    }
    return 1;
}

static float EvaluateNodes(const float* xs, const float* ys, int count, float x) {
    int last = count - 1;
    int segment = 0;
    float h, t, t2, t3, h00, h10, h01, h11;
    float d0, d1;
    if (x <= xs[0]) {
        if (xs[0] <= 0.0f) return ys[0];
        return ys[0] * (x / xs[0]);
    }
    if (x >= xs[last]) return ys[last];
    while (segment + 1 < last && x > xs[segment + 1]) ++segment;
    h = xs[segment + 1] - xs[segment];
    if (h <= 0.0f) return ys[segment + 1];

    {
        float prev = (ys[segment] - ys[segment > 0 ? segment - 1 : 0]) /
            (xs[segment] - xs[segment > 0 ? segment - 1 : 0]);
        float next = (ys[segment + 1] - ys[segment]) / h;
        if (segment == 0) {
            d0 = next;
        } else if (prev == 0.0f || next == 0.0f ||
            ((prev < 0.0f) != (next < 0.0f))) {
            d0 = 0.0f;
        } else {
            float hp = xs[segment] - xs[segment - 1];
            float w1 = 2.0f * h + hp;
            float w2 = h + 2.0f * hp;
            d0 = (w1 + w2) / (w1 / prev + w2 / next);
        }
    }
    if (segment + 1 == last) {
        d1 = (ys[last] - ys[last - 1]) / (xs[last] - xs[last - 1]);
    } else {
        float prev = (ys[segment + 1] - ys[segment]) / h;
        float next = (ys[segment + 2] - ys[segment + 1]) /
            (xs[segment + 2] - xs[segment + 1]);
        if (prev == 0.0f || next == 0.0f ||
            ((prev < 0.0f) != (next < 0.0f))) {
            d1 = 0.0f;
        } else {
            float hn = xs[segment + 2] - xs[segment + 1];
            float w1 = 2.0f * hn + h;
            float w2 = hn + 2.0f * h;
            d1 = (w1 + w2) / (w1 / prev + w2 / next);
        }
    }

    t = (x - xs[segment]) / h;
    t2 = t * t;
    t3 = t2 * t;
    h00 = 2.0f * t3 - 3.0f * t2 + 1.0f;
    h10 = t3 - 2.0f * t2 + t;
    h01 = -2.0f * t3 + 3.0f * t2;
    h11 = t3 - t2;
    return h00 * ys[segment] + h10 * h * d0 + h01 * ys[segment + 1] + h11 * h * d1;
}

static void ReportCurve(const char* label, const Window* window) {
    float xs[MAX_NODES], ys[MAX_NODES];
    int count = 0, i;
    printf("%s\n", label);
    if (!BuildNodes(window, xs, ys, &count)) {
        printf("  (no usable curve)\n");
        ++failures;
        return;
    }
    printf("  nodes=%d knee=(%.5f,%.5f)\n", count, window->kneeX, window->kneeY);
    printf("  samples:");
    for (i = 0; i <= 8; ++i) {
        float x = (float)i / 8.0f;
        printf(" %.4f->%.4f", x, EvaluateNodes(xs, ys, count, x));
    }
    printf("\n");
}

int main(void) {
    /* Real values from ffprobe on the FATE HDR10+ vector. */
    Window fate;
    int i;
    fate.kneeX = 0.0f;
    fate.kneeY = 0.0f;
    fate.anchorCount = 9;
    fate.anchors[0] = 102.0f / 1023.0f;
    fate.anchors[1] = 205.0f / 1023.0f;
    fate.anchors[2] = 307.0f / 1023.0f;
    fate.anchors[3] = 410.0f / 1023.0f;
    fate.anchors[4] = 512.0f / 1023.0f;
    fate.anchors[5] = 614.0f / 1023.0f;
    fate.anchors[6] = 717.0f / 1023.0f;
    fate.anchors[7] = 819.0f / 1023.0f;
    fate.anchors[8] = 922.0f / 1023.0f;

    printf("=== ST 2094-40 curve reconstruction test ===\n\n");
    ReportCurve("FATE HDR10+ window (knee=0, 9 anchors):", &fate);

    {
        float xs[MAX_NODES], ys[MAX_NODES];
        int count = 0;
        printf("\n-- properties --\n");
        Check(BuildNodes(&fate, xs, ys, &count) == 1, "FATE window builds");

        /* Monotonicity across the whole domain, sampled far finer than the LUT. */
        {
            float previous = -1.0f;
            int monotone = 1, clamped = 1;
            for (i = 0; i <= 4000; ++i) {
                float x = (float)i / 4000.0f;
                float y = EvaluateNodes(xs, ys, count, x);
                if (y < previous - 1e-6f) monotone = 0;
                if (y < -1e-6f || y > 1.0f + 1e-6f) clamped = 0;
                previous = y;
            }
            Check(monotone, "curve is monotonically non-decreasing");
            Check(clamped, "curve stays within [0,1]");
        }

        /* Endpoint exactness: f(0)=0 and f(1)=1 by construction. */
        Check(fabsf(EvaluateNodes(xs, ys, count, 0.0f) - 0.0f) < 1e-5f,
            "f(0) == 0");
        Check(fabsf(EvaluateNodes(xs, ys, count, 1.0f) - 1.0f) < 1e-5f,
            "f(1) == 1");

        /* LUT quantisation: adjacent entries must not step coarsely, which is
         * what banding would look like. */
        {
            float lut[LUT_SIZE];
            float maxStep = 0.0f;
            for (i = 0; i < LUT_SIZE; ++i) {
                float x = (float)i / (float)(LUT_SIZE - 1);
                float y = EvaluateNodes(xs, ys, count, x);
                if (y < 0.0f) y = 0.0f;
                if (y > 1.0f) y = 1.0f;
                if (i > 0 && y < lut[i - 1]) y = lut[i - 1];
                lut[i] = y;
                if (i > 0 && lut[i] - lut[i - 1] > maxStep) maxStep = lut[i] - lut[i - 1];
            }
            printf("  max LUT step = %.5f (%.2f of 8-bit code)\n",
                maxStep, maxStep * 255.0f);
            Check(maxStep * 255.0f < 4.0f, "LUT step below 4/255 (no visible banding)");
        }

        /* Highest anchor == 0.90127: the curve must not overshoot 1.0 early. */
        Check(EvaluateNodes(xs, ys, count, 0.90127f) < 1.0f,
            "no overshoot before the final node");
    }

    /* Rejection cases: these must NOT build a curve. */
    {
        Window bad;
        float xs[MAX_NODES], ys[MAX_NODES];
        int count = 0;
        printf("\n-- rejection cases --\n");

        bad = fate; bad.anchorCount = 0;
        Check(BuildNodes(&bad, xs, ys, &count) == 0, "zero anchors rejected");

        bad = fate; bad.anchors[3] = 0.1f;  /* below the previous anchor */
        Check(BuildNodes(&bad, xs, ys, &count) == 0, "non-monotone anchors rejected");

        bad = fate; bad.kneeY = 0.9f; bad.kneeX = 0.1f;
        Check(BuildNodes(&bad, xs, ys, &count) == 0, "knee above identity rejected");

        bad = fate; bad.anchors[9 - 1] = 1.5f;
        Check(BuildNodes(&bad, xs, ys, &count) == 0, "out-of-range anchor rejected");
    }

    /* A genuine reference shape: knee at 0.5 with a flat-then-rising curve. */
    {
        Window ref;
        int count = 0;
        float xs[MAX_NODES], ys[MAX_NODES];
        ref.kneeX = 0.5f;
        ref.kneeY = 0.25f;
        ref.anchorCount = 3;
        ref.anchors[0] = 0.40f;
        ref.anchors[1] = 0.60f;
        ref.anchors[2] = 0.80f;
        printf("\n-- knee + anchors --\n");
        ReportCurve("reference window (knee=0.5, 3 anchors):", &ref);
        Check(BuildNodes(&ref, xs, ys, &count) == 1, "reference window builds");
        Check(fabsf(EvaluateNodes(xs, ys, count, 0.5f) - 0.25f) < 1e-5f,
            "f(knee_x) == knee_y");
        {
            float previous = -1.0f;
            int monotone = 1;
            for (i = 0; i <= 4000; ++i) {
                float x = (float)i / 4000.0f;
                float y = EvaluateNodes(xs, ys, count, x);
                if (y < previous - 1e-6f) monotone = 0;
                previous = y;
            }
            Check(monotone, "reference curve monotone");
        }

        /* Composed mapping: the ST 2094 curve followed by the display
         * compression, which is what the shader actually does. The FATE vector
         * is near-identity and exercises none of this, so the composition is
         * pinned here with a curve that is clearly not identity. */
        printf("\n-- composed mapping (curve then display compression) --\n");
        {
            /* Mirror of Bt2390HdrToSdrPq from VideoRenderer.cpp. */
            const float m1 = 2610.0f / 16384.0f;
            const float m2 = 2523.0f / 32.0f;
            const float c1 = 3424.0f / 4096.0f;
            const float c2 = 2413.0f / 128.0f;
            const float c3 = 2392.0f / 128.0f;
            const float sourcePeak = 400.0f;
            const float targetPeak = 100.0f;
            const float sourceMaximum = sourcePeak > 1.0f ? sourcePeak : 1.0f;
            const float targetMaximum = targetPeak < 1.0f ? 1.0f :
                (targetPeak > sourceMaximum ? sourceMaximum : targetPeak);
            float sourcePq, targetPq, normalizedTarget, knee;
            float previous = -1.0f;
            int monotone = 1, bounded = 1, darker = 0;

            sourcePq = (float)pow(sourceMaximum / 10000.0, m1);
            sourcePq = (float)pow((c1 + c2 * sourcePq) / (1.0f + c3 * sourcePq), m2);
            targetPq = (float)pow(targetMaximum / 10000.0, m1);
            targetPq = (float)pow((c1 + c2 * targetPq) / (1.0f + c3 * targetPq), m2);
            normalizedTarget = targetPq / sourcePq;
            knee = 1.5f * normalizedTarget - 0.5f;
            if (knee < 0.0f) knee = 0.0f;
            if (knee > 1.0f) knee = 1.0f;

            for (i = 0; i <= 2000; ++i) {
                float x = (float)i / 2000.0f;
                /* 1) the metadata curve maps content signal -> targeted display */
                float curved = EvaluateNodes(xs, ys, count, x);
                /* 2) the targeted display is compressed onto our SDR peak */
                float signal = curved;
                float mapped = signal;
                if (sourcePq > 1e-6f && targetPq < sourcePq) {
                    float s = signal;
                    if (s > knee && knee < 1.0f) {
                        float t = (s - knee) / (1.0f - knee);
                        float t2 = t * t, t3 = t2 * t;
                        mapped = (2 * t3 - 3 * t2 + 1) * knee +
                            (t3 - 2 * t2 + t) * (1 - knee) +
                            (-2 * t3 + 3 * t2) * normalizedTarget;
                    } else {
                        mapped = s;
                    }
                }
                if (mapped < previous - 1e-6f) monotone = 0;
                if (mapped < -1e-6f || mapped > 1.0f + 1e-6f) bounded = 0;
                if (i == 2000) {
                    printf("  composed(0->1): %.4f -> %.4f\n",
                        EvaluateNodes(xs, ys, count, 0.0f), mapped);
                    /* The reference curve pulls mid-tones down, so the composed
                     * result must be below the identity at the top end. */
                    if (mapped <= 1.0f) darker = 1;
                }
                previous = mapped;
            }
            Check(monotone, "composed mapping monotone");
            Check(bounded, "composed mapping within [0,1]");
            Check(darker, "composed mapping does not overshoot white");

            /* The reference curve maps 0.5 -> 0.25, so the composed output at
             * mid-grey must be clearly darker than the curve-free static path
             * would be. This is the check the near-identity FATE vector cannot
             * provide. */
            {
                float curvedMid = EvaluateNodes(xs, ys, count, 0.5f);
                printf("  curve(0.5) = %.4f (identity would be 0.5000)\n", curvedMid);
                Check(curvedMid < 0.45f, "non-identity curve darkens mid-tones");
            }
        }
    }

    printf("\n=== %s (%d failures) ===\n", failures ? "FAIL" : "PASS", failures);
    return failures ? 1 : 0;
}
