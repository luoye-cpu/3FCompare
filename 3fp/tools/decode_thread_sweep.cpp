// 测量软解线程数与吞吐的关系，为动态线程分级提供依据。
//
// 为什么需要：当前策略是"固定 min(硬件线程, 8)"，AV1 8K 特例 16。
// 但线程并非越多越好 —— 大帧解码受内存带宽限制，超配反而变慢。
// 本探针扫多组线程数，输出每组的 ms/帧 与相对最优值，用于确定分级表。
//
// 用法：decode_thread_sweep <媒体> [--frames N] [--threads 1,2,4,8,12,16,24,32]

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/imgutils.h>
}

namespace {
struct Result { int threads = 0; double msPerFrame = 0; int frames = 0; bool ok = false; };

Result Measure(const std::string& path, const int threadCount, const int targetFrames) {
    Result result;
    result.threads = threadCount;
    AVFormatContext* format = nullptr;
    if (avformat_open_input(&format, path.c_str(), nullptr, nullptr) < 0) return result;
    if (avformat_find_stream_info(format, nullptr) < 0) { avformat_close_input(&format); return result; }
    const auto streamIndex = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (streamIndex < 0) { avformat_close_input(&format); return result; }

    const auto* stream = format->streams[streamIndex];
    // Match the kernel's preference: libdav1d for AV1, otherwise the default decoder.
    const AVCodec* codec = stream->codecpar->codec_id == AV_CODEC_ID_AV1
        ? avcodec_find_decoder_by_name("libdav1d") : nullptr;
    if (codec == nullptr) codec = avcodec_find_decoder(stream->codecpar->codec_id);
    if (codec == nullptr) { avformat_close_input(&format); return result; }

    auto* context = avcodec_alloc_context3(codec);
    if (context == nullptr) { avformat_close_input(&format); return result; }
    if (avcodec_parameters_to_context(context, stream->codecpar) < 0) {
        avcodec_free_context(&context); avformat_close_input(&format); return result;
    }
    context->thread_count = threadCount;
    context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (avcodec_open2(context, codec, nullptr) < 0) {
        avcodec_free_context(&context); avformat_close_input(&format); return result;
    }
    // A thread_count above the codec's own maximum is silently clamped, so read back
    // what was actually applied rather than assuming the request took effect.
    result.threads = context->thread_count;

    AVPacket* packet = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    const auto started = std::chrono::steady_clock::now();
    int decoded = 0;
    while (decoded < targetFrames && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index != streamIndex) { av_packet_unref(packet); continue; }
        if (avcodec_send_packet(context, packet) < 0) { av_packet_unref(packet); break; }
        av_packet_unref(packet);
        while (avcodec_receive_frame(context, frame) >= 0) {
            ++decoded;
            av_frame_unref(frame);
            if (decoded >= targetFrames) break;
        }
    }
    const auto elapsed = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count();
    result.frames = decoded;
    if (decoded > 0) { result.msPerFrame = elapsed / decoded; result.ok = true; }

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&context);
    avformat_close_input(&format);
    return result;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: decode_thread_sweep <媒体> [--frames N] [--threads a,b,c]\n");
        return 2;
    }
    int frames = 40;
    std::vector<int> counts = {1, 2, 4, 6, 8, 12, 16, 24, 32};
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (key == L"--threads" && i + 1 < argc) {
            counts.clear();
            std::wstring list = argv[++i];
            std::size_t start = 0;
            while (start <= list.size()) {
                const auto comma = list.find(L',', start);
                const auto piece = list.substr(start, comma == std::wstring::npos ? std::wstring::npos : comma - start);
                if (!piece.empty()) counts.push_back(_wtoi(piece.c_str()));
                if (comma == std::wstring::npos) break;
                start = comma + 1;
            }
        }
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    const auto cores = std::thread::hardware_concurrency();
    printf("媒体: %s\n逻辑核: %u  目标帧数: %d\n\n", path.c_str(), cores, frames);
    printf("%8s %12s %10s %10s\n", "请求线程", "实际线程", "ms/帧", "相对最优");

    std::vector<Result> results;
    double best = 1e18;
    for (const auto count : counts) {
        // 每档测两次取较快者：机器上有其他负载，单次易被抢占
        auto a = Measure(path, count, frames);
        auto b = Measure(path, count, frames);
        auto r = (a.ok && b.ok) ? (a.msPerFrame <= b.msPerFrame ? a : b) : (a.ok ? a : b);
        if (r.ok) best = (std::min)(best, r.msPerFrame);
        results.push_back(r);
    }
    for (const auto& r : results) {
        if (!r.ok) { printf("%8d %12s %10s %10s\n", r.threads, "-", "失败", "-"); continue; }
        printf("%8d %12d %10.2f %9.2fx\n", r.threads, r.threads, r.msPerFrame, r.msPerFrame / best);
    }
    printf("\n（相对最优 < 1.00x 为更快；> 1.00x 为更慢）\n");
    return 0;
}
