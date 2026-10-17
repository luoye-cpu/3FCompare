// Upper bound on the cost of what the HDR diagnostics added to the frame loop.
//
// The end-to-end instrument cannot answer "did the counters cost anything": re-running the
// identical clip+arm in a different batch moves cpu_ms/frame by up to 40% (observed:
// 1080p cpu-hdr 8.55 -> 11.98 ms), which is orders of magnitude above anything four
// relaxed atomic increments could cost. So measure the primitive instead of trying to see
// it through that noise.
//
// usage: counter_cost_microbench [--iters N]

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>

namespace {

double NowUs() {
    static const LARGE_INTEGER freq = [] {
        LARGE_INTEGER f{};
        QueryPerformanceFrequency(&f);
        return f;
    }();
    LARGE_INTEGER v{};
    QueryPerformanceCounter(&v);
    return freq.QuadPart > 0
        ? static_cast<double>(v.QuadPart) * 1e6 / static_cast<double>(freq.QuadPart) : 0.0;
}

}  // namespace

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    long long iters = 20000000;
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::string(argv[i]) == "--iters") iters = atoll(argv[i + 1]);
    }
    if (iters < 1000) iters = 1000;

    // The shape the renderer actually uses: one branch of the curve tally per drawn frame,
    // plus the two SetHdrMetadata tallies (they only move when the chain is scRGB HDR).
    // Four relaxed RMWs per loop is therefore an over-estimate of the per-frame addition.
    std::atomic<std::uint64_t> curve{ 0 }, other{ 0 }, calls{ 0 }, suppressed{ 0 };
    const double start = NowUs();
    for (long long i = 0; i < iters; ++i) {
        (i & 1 ? curve : other).fetch_add(1, std::memory_order_relaxed);
        calls.fetch_add(1, std::memory_order_relaxed);
        suppressed.fetch_add(1, std::memory_order_relaxed);
        curve.fetch_add(1, std::memory_order_relaxed);
    }
    const double perLoop = (NowUs() - start) / static_cast<double>(iters);
    std::printf("4 relaxed fetch_add per iteration: %.5f us/loop = %.1f ns per frame\n",
        perLoop, perLoop * 1000.0);
    std::printf("counters observed: %llu %llu %llu %llu\n",
        static_cast<unsigned long long>(curve.load()),
        static_cast<unsigned long long>(other.load()),
        static_cast<unsigned long long>(calls.load()),
        static_cast<unsigned long long>(suppressed.load()));

    // The read side runs only when a host polls, i.e. off the frame path.
    std::atomic<std::uint32_t> a{ 1 }, b{ 2 }, c{ 3 }, d{ 4 };
    const long long polls = iters / 100;
    const double pollStart = NowUs();
    unsigned long long sink = 0;
    for (long long i = 0; i < polls; ++i) {
        sink += a.load(std::memory_order_relaxed) + b.load(std::memory_order_relaxed)
              + c.load(std::memory_order_relaxed) + d.load(std::memory_order_relaxed);
    }
    std::printf("poll-side 4 relaxed loads: %.5f us per poll (sink=%llu)\n",
        (NowUs() - pollStart) / static_cast<double>(polls > 0 ? polls : 1), sink);
    return 0;
}
