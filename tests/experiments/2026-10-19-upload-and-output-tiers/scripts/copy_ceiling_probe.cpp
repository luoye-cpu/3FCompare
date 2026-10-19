// What single thread, on which core, can actually copy -- the number the 3FP upload
// path has to be judged against.
//
// Why this exists: the snapshot carries uploadCopy100ns but no byte count, so every
// bandwidth figure quoted so far came from a theoretical frame size. Worse, an
// earlier un-pinned pass of this same probe measured the identical 8K config at
// 12.5 ms and then at 26.9 ms -- a 2.2x swing. This box is a 13650HX (6 P + 8 E
// cores, 20 logical), so an unpinned thread lands on either core type and the two
// answers are both "correct". Nothing may be concluded about the renderer's copy
// rate until the ceiling is measured per core type and shown to repeat.
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <thread>
#include <vector>
#include <algorithm>

static const std::size_t MB1024 = 1024u * 1024u;

static double NowMs() {
    static LARGE_INTEGER freq{};
    static bool init = false;
    if (!init) { QueryPerformanceFrequency(&freq); init = true; }
    LARGE_INTEGER t{};
    QueryPerformanceCounter(&t);
    return static_cast<double>(t.QuadPart) * 1000.0 / static_cast<double>(freq.QuadPart);
}

static void Pin(int logical) {
    if (logical < 0) return;
    DWORD mask = 1u;
    mask <<= logical;
    if (SetThreadAffinityMask(GetCurrentThread(), mask) == 0)
        std::printf("  [pin %d FAILED %lu]\n", logical, GetLastError());
}

static void* AllocBig(std::size_t bytes) {
    auto* p = VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (p == nullptr) { std::printf("VirtualAlloc failed for %zu bytes\n", bytes); ExitProcess(1); }
    return p;
}

struct Plane { unsigned rows; std::size_t rowBytes; };

// The three shapes the fullscreen arms upload, sized the way the code computes
// rowBytes: (width >> xShift) * samplesPerTexel * (bitDepth > 8 ? 2 : 1) per row.
static const Plane k720p8[3] = {{720, 1280}, {360, 640}, {360, 640}};
static const Plane k4kP010[3] = {{2160, 3840 * 2}, {1080, 1920 * 2}, {1080, 1920 * 2}};
static const Plane k8kP010[3] = {{4320, 7680 * 2}, {2160, 3840 * 2}, {2160, 3840 * 2}};
static const Plane k4k444p12[3] = {{2160, 3840 * 2}, {2160, 3840 * 2}, {2160, 3840 * 2}};

static std::size_t PlaneBytes(const Plane* planes, unsigned count) {
    std::size_t t = 0;
    for (unsigned i = 0; i < count; ++i) t += planes[i].rows * planes[i].rowBytes;
    return t;
}

// Rows [lo, hi) of the luma plane own each worker's slice; chroma rows are split
// by the same fraction so every thread gets a proportional share.
static void CopySlice(const std::uint8_t* src, std::uint8_t* dst, const Plane* planes,
    unsigned count, std::size_t dstPitchPad, unsigned rowLo, unsigned rowHi,
    unsigned rowsTotal, std::size_t* bytesOut) {
    std::size_t srcOff = 0, dstOff = 0, moved = 0;
    for (unsigned p = 0; p < count; ++p) {
        const std::size_t rowBytes = planes[p].rowBytes;
        const std::size_t dstPitch = rowBytes + dstPitchPad;
        const unsigned rows = planes[p].rows;
        const unsigned lo = std::min(rowLo * rows / rowsTotal, rows);
        const unsigned hi = std::min(rowHi * rows / rowsTotal, rows);
        for (unsigned r = lo; r < hi; ++r) {
            std::memcpy(dst + dstOff + static_cast<std::size_t>(r) * dstPitch,
                src + srcOff + static_cast<std::size_t>(r) * rowBytes, rowBytes);
            moved += rowBytes;
        }
        srcOff += rows * rowBytes;
        dstOff += rows * dstPitch;
    }
    if (bytesOut != nullptr) *bytesOut = moved;
}

struct Buffers { std::uint8_t* src; std::uint8_t* dst; std::size_t bytes; std::size_t dstBytes; };

static Buffers Make(const Plane* planes, unsigned count, std::size_t pitchPad) {
    Buffers b{};
    b.bytes = PlaneBytes(planes, count);
    std::size_t dstTotal = 0;
    for (unsigned i = 0; i < count; ++i) dstTotal += planes[i].rows * (planes[i].rowBytes + pitchPad);
    b.dstBytes = dstTotal;
    b.src = static_cast<std::uint8_t*>(AllocBig(b.bytes));
    b.dst = static_cast<std::uint8_t*>(AllocBig(dstTotal));
    std::memset(b.src, 0x5A, b.bytes);
    std::memset(b.dst, 0x00, dstTotal);
    return b;
}

// One config: a shape, a thread count, a pinned core set.
static void Run(const char* label, const Plane* planes, unsigned count,
    unsigned threads, std::size_t pitchPad, const int* affinity, unsigned iterations) {
    const Buffers b = Make(planes, count, pitchPad);
    std::vector<double> samples;
    samples.reserve(iterations);
    std::size_t moved = 0;
    for (unsigned it = 0; it < iterations; ++it) {
        const auto t0 = NowMs();
        if (threads <= 1) {
            Pin(affinity[0]);
            CopySlice(b.src, b.dst, planes, count, pitchPad, 0, planes[0].rows,
                planes[0].rows, &moved);
        } else {
            std::vector<std::thread> pool;
            pool.reserve(threads);
            const unsigned rowsTotal = planes[0].rows;
            std::vector<std::size_t> per(threads, 0);
            for (unsigned t = 0; t < threads; ++t) {
                const unsigned lo = static_cast<unsigned>(
                    (static_cast<std::uint64_t>(rowsTotal) * t) / threads);
                const unsigned hi = static_cast<unsigned>(
                    (static_cast<std::uint64_t>(rowsTotal) * (t + 1)) / threads);
                    const int core = affinity[t % 8];
                pool.emplace_back([&, lo, hi, t, core] {
                    Pin(core);
                    CopySlice(b.src, b.dst, planes, count, pitchPad, lo, hi, rowsTotal, &per[t]);
                });
            }
            for (auto& th : pool) th.join();
            moved = 0;
            for (auto v : per) moved += v;
        }
        samples.push_back(NowMs() - t0);
    }
    std::sort(samples.begin(), samples.end());
    const double med = samples[samples.size() / 2];
    const double min = samples.front(), max = samples.back();
    const double gbps = (static_cast<double>(moved) / 1e9) / (med / 1000.0);
    std::printf("%-26s %7.2f MB copied  thr=%u  median=%7.3f ms  min=%7.3f  max=%7.3f  %6.2f GB/s\n",
        label, static_cast<double>(moved) / 1e6, threads, med, min, max, gbps);
    VirtualFree(b.src, 0, MEM_RELEASE);
    VirtualFree(b.dst, 0, MEM_RELEASE);
}

int main() {
    SYSTEM_INFO si{};
    GetNativeSystemInfo(&si);
    std::printf("logical processors = %u (13650HX: 0-11 are the 6 P cores with HT, 12-19 are the 8 E cores)\n\n",
        si.dwNumberOfProcessors);

    // P-core vs E-core, single thread. This is the discriminator the unpinned run
    // could not settle: 12.5 ms and 26.9 ms for one config.
    const int p0[1] = {0}, p2[1] = {2}, e12[1] = {12}, e19[1] = {19};
    std::printf("== single thread, pinned core ==\n");
    Run("8K P010 @P0", k8kP010, 3, 1, 0, p0, 12);
    Run("8K P010 @P2", k8kP010, 3, 1, 0, p2, 12);
    Run("8K P010 @E12", k8kP010, 3, 1, 0, e12, 12);
    Run("8K P010 @E19", k8kP010, 3, 1, 0, e19, 12);
    Run("4K P010 @P0", k4kP010, 3, 1, 0, p0, 12);
    Run("4K P010 @E12", k4kP010, 3, 1, 0, e12, 12);
    Run("4K 444p12 @P0", k4k444p12, 3, 1, 0, p0, 12);
    Run("720p 8bit @P0", k720p8, 3, 1, 0, p0, 12);

    std::printf("\n== A/B/A stability: same config three times ==\n");
    Run("8K P010 @P0 (pass 1)", k8kP010, 3, 1, 0, p0, 12);
    Run("8K P010 @E12 (intervene)", k8kP010, 3, 1, 0, e12, 12);
    Run("8K P010 @P0 (pass 2)", k8kP010, 3, 1, 0, p0, 12);

    std::printf("\n== pitched destination, the way a staging texture aligns rows ==\n");
    Run("8K P010 dstPitch+256 @P0", k8kP010, 3, 1, 256, p0, 12);

    std::printf("\n== P-core threads only ==\n");
    const int pSet[8] = {0, 2, 4, 6, 8, 10, 1, 3};
    Run("8K P010 2xP", k8kP010, 3, 2, 0, pSet, 12);
    Run("8K P010 4xP", k8kP010, 3, 4, 0, pSet, 12);
    Run("8K P010 6xP", k8kP010, 3, 6, 0, pSet, 12);
    Run("4K P010 4xP", k4kP010, 3, 4, 0, pSet, 12);

    std::printf("\n== mixed P+E threads ==\n");
    const int mix[8] = {0, 2, 4, 12, 14, 16, 6, 18};
    Run("8K P010 6x mixed", k8kP010, 3, 6, 0, mix, 12);
    return 0;
}
