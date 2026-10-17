// CPU cost of the HDR10+ per-frame work, measured on the shipping source files
// themselves (HdrMetadataParse.cpp / HdrToneCurve.cpp are compiled into this
// bench, not re-implemented).
//
// Why a separate instrument: the play-back matrix paces to the media clock and its
// fps is capped at the source frame rate, so a per-frame cost of a few microseconds
// cannot show up there. This measures the two functions the dynamic-metadata path
// adds, against real AVDynamicHDRPlus side data decoded from a real HDR10+ stream,
// repeated to clear the timer floor.
//
// Usage:
//   hdr10plus_cost_microbench <media> [--frames N] [--iters K]
//
//   --frames N   decoded frames to sample (default 120)
//   --iters K    repetitions per timed block (default 200)
//
// Static side: the kernel also runs av_frame_get_side_data lookups per frame on the
// way to those functions; those are timed too, so "what HDR10+ adds per frame" is
// the sum of the printed parts rather than a guess about one of them.

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hdr_dynamic_metadata.h>
#include <libavutil/hdr_dynamic_vivid_metadata.h>
#include <libavutil/frame.h>
}

#include "3FP/Hdr/HdrMetadataParse.h"
#include "3FP/Hdr/HdrToneCurve.h"

namespace {

double NowMs() {
    static LARGE_INTEGER freq{ [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }() };
    LARGE_INTEGER v{};
    QueryPerformanceCounter(&v);
    return static_cast<double>(v.QuadPart) * 1000.0 / (freq.QuadPart > 0 ? freq.QuadPart : 1);
}

double Median(std::vector<double> values) {
    if (values.empty()) return 0.0;
    std::sort(values.begin(), values.end());
    const auto mid = values.size() / 2;
    return values.size() % 2 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
}

int Run(const wchar_t* path, const int maxFrames, const int iters) {
    std::string utf8Path;
    {
        const int size = WideCharToMultiByte(CP_UTF8, 0, path, -1, nullptr, 0, nullptr, nullptr);
        if (size <= 1) { std::printf("bad path\n"); return 2; }
        utf8Path.resize(static_cast<std::size_t>(size) - 1);
        WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8Path.data(), size, nullptr, nullptr);
    }

    AVFormatContext* fmt = nullptr;
    if (avformat_open_input(&fmt, utf8Path.c_str(), nullptr, nullptr) < 0) {
        std::printf("OPEN failed: %s\n", utf8Path.c_str());
        return 1;
    }
    if (avformat_find_stream_info(fmt, nullptr) < 0) {
        avformat_close_input(&fmt);
        std::printf("PROBE failed\n");
        return 1;
    }
    int streamIndex = -1;
    for (unsigned i = 0; i < fmt->nb_streams; ++i) {
        if (fmt->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) { streamIndex = static_cast<int>(i); break; }
    }
    if (streamIndex < 0) { avformat_close_input(&fmt); std::printf("no video stream\n"); return 1; }

    AVCodecParameters* par = fmt->streams[streamIndex]->codecpar;
    const AVCodec* codec = avcodec_find_decoder(par->codec_id);
    AVCodecContext* ctx = avcodec_alloc_context3(codec);
    avcodec_parameters_to_context(ctx, par);
    ctx->thread_count = 1;                       // decode threads are not what is timed here
    if (avcodec_open2(ctx, codec, nullptr) < 0) {
        avcodec_free_context(&ctx);
        avformat_close_input(&fmt);
        std::printf("decoder open failed\n");
        return 1;
    }

    std::vector<double> sideLookups, masteringLookups, parseUs, curveUs, identityUs, windowCounts;
    AVPacket* packet = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    HdrDynamicMetadata sink{};
    HdrToneCurve curve{};
    int used = 0;

    while (used < maxFrames && av_read_frame(fmt, packet) >= 0) {
        if (packet->stream_index == streamIndex) {
            if (avcodec_send_packet(ctx, packet) >= 0) {
                while (used < maxFrames && avcodec_receive_frame(ctx, frame) == 0) {
                    const auto* plusData = av_frame_get_side_data(frame, AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
                    if (plusData != nullptr && plusData->size >= (int)sizeof(AVDynamicHDRPlus)) {
                        const auto* plus = reinterpret_cast<const AVDynamicHDRPlus*>(plusData->data);

                        // 1. the per-frame side-data scan the kernel performs on the way in
                        //    (HdrProcessor: vivid, hdr10+, light, mastering).
                        double t0 = NowMs();
                        for (int i = 0; i < iters; ++i) {
                            volatile const AVFrameSideData* a = av_frame_get_side_data(frame, AV_FRAME_DATA_DYNAMIC_HDR_VIVID);
                            volatile const AVFrameSideData* b = av_frame_get_side_data(frame, AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
                            (void)a; (void)b;
                        }
                        double t1 = NowMs();
                        for (int i = 0; i < iters; ++i) {
                            volatile const AVFrameSideData* a = av_frame_get_side_data(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
                            volatile const AVFrameSideData* b = av_frame_get_side_data(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
                            (void)a; (void)b;
                        }
                        double t2 = NowMs();

                        // 2. ST 2094-40 -> snapshot conversion
                        for (int i = 0; i < iters; ++i) sink = HdrMetadataParse::FromHdr10Plus(*plus);
                        double t3 = NowMs();

                        // 3. per-window LUT bake, the count the render path actually uses
                        const auto windows = std::max<std::uint32_t>(1u, sink.windowCount);
                        for (int i = 0; i < iters; ++i) {
                            for (std::uint32_t w = 0; w < sink.windowCount; ++w)
                                HdrToneCurveBuild::BuildFromWindow(sink.windows[w], curve);
                        }
                        double t4 = NowMs();
                        for (int i = 0; i < iters; ++i) {
                            for (std::uint32_t w = 0; w < windows; ++w) HdrToneCurveBuild::BuildIdentity(curve);
                        }
                        double t5 = NowMs();

                        const double scale = 1000.0 / iters;   // ms -> us per single call
                        sideLookups.push_back((t1 - t0) * scale);
                        masteringLookups.push_back((t2 - t1) * scale);
                        parseUs.push_back((t3 - t2) * scale);
                        curveUs.push_back((t4 - t3) * scale);
                        identityUs.push_back((t5 - t4) * scale);
                        windowCounts.push_back(static_cast<double>(sink.windowCount));
                        ++used;
                    }
                }
            }
        }
        av_packet_unref(packet);
    }

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&ctx);
    avformat_close_input(&fmt);

    if (used == 0) { std::printf("NO FRAMES WITH HDR10+ SIDE DATA in %s\n", utf8Path.c_str()); return 3; }
    const double medianWindows = Median(windowCounts);
    const double total = Median(sideLookups) + Median(masteringLookups) + Median(parseUs) + Median(curveUs);
    std::printf("microbench media=%s frames=%d iters=%d\n", utf8Path.c_str(), used, iters);
    std::printf("side_data_scan_3lookup_us=%.3f side_data_scan_2lookup_us=%.3f\n",
        Median(sideLookups), Median(masteringLookups));
    std::printf("from_hdr10_plus_us=%.3f build_from_window_us=%.3f (windows=%.0f) build_identity_us=%.3f\n",
        Median(parseUs), Median(curveUs), medianWindows, Median(identityUs));
    std::printf("total_per_frame_us=%.3f  (side scans + parse + curve bake)\n", total);
    std::printf("reference: 60 fps budget = 16667 us/frame; 8K BGRA upload at 4 GB/s = %.0f us/frame\n",
        7680.0 * 4320.0 * 4.0 / 4.0e9 * 1e6);
    return 0;
}

}  // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        std::wprintf(L"用法: hdr10plus_cost_microbench <媒体> [--frames N] [--iters K]\n");
        return 2;
    }
    int frames = 120, iters = 200;
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (key == L"--iters" && i + 1 < argc) iters = _wtoi(argv[++i]);
    }
    if (frames < 1) frames = 1;
    if (iters < 1) iters = 1;
    return Run(argv[1], frames, iters);
}
