// 验证 dav1d 线程数是否真的生效：读 FFmpeg 解码器的实际线程配置。
//
// 背景：PR #15 把 4K+ AV1(libdav1d) 的 thread_count 从 8 提到 16。
// 实测内核吞吐**无变化**（PRE 与 POST 都是 23 帧/6 秒），需查明原因：
//   (a) thread_count 没被 dav1d 采纳（capability 是 "other"，非 frame/slice）
//   (b) 改了但 8 线程已够，16 无增益
//   (c) 瓶颈根本不在解码（在别处）
//
// 做法：用与 3FP 相同的 API 序列（avcodec_find_decoder_by_name("libdav1d")
// + avcodec_parameters_to_context + thread_count/thread_type + avcodec_open2），
// 然后回读 context->thread_count / thread_type，并**实测解码耗时**对比 8/16。
//
// 用法：dav1d_thread_probe <av1 文件> [--frames N]

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/opt.h>
}

namespace {
/// 按 3FP 的方式打开 dav1d 解码器，指定线程数，返回实测解码耗时（毫秒）。
/// 同时回读 FFmpeg 记录的 thread_count，看是否被采纳。
struct Result {
    bool ok = false;
    int appliedThreads = -1;      // avcodec_open2 之后 context->thread_count
    int appliedThreadType = -1;
    double decodeMs = 0;
    int decodedFrames = 0;
    std::string codecName;
    int capabilities = 0;
};

Result Run(const std::string& path, const int threads, const int framesToDecode) {
    Result out;
    AVFormatContext* format = nullptr;
    if (avformat_open_input(&format, path.c_str(), nullptr, nullptr) < 0) return out;
    if (avformat_find_stream_info(format, nullptr) < 0) { avformat_close_input(&format); return out; }

    int streamIndex = -1;
    for (unsigned i = 0; i < format->nb_streams; ++i)
        if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) { streamIndex = static_cast<int>(i); break; }
    if (streamIndex < 0) { avformat_close_input(&format); return out; }
    auto* stream = format->streams[streamIndex];

    // 与 3FP 一致：按名字找 libdav1d
    const auto* codec = avcodec_find_decoder_by_name("libdav1d");
    if (codec == nullptr) { avformat_close_input(&format); return out; }
    out.codecName = codec->name != nullptr ? codec->name : "";
    out.capabilities = codec->capabilities;

    auto* context = avcodec_alloc_context3(codec);
    if (context == nullptr) { avformat_close_input(&format); return out; }
    if (avcodec_parameters_to_context(context, stream->codecpar) < 0) {
        avcodec_free_context(&context); avformat_close_input(&format); return out;
    }
    // 复刻 3FP / PR#15 的设定
    context->thread_count = threads;
    context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (avcodec_open2(context, codec, nullptr) < 0) {
        avcodec_free_context(&context); avformat_close_input(&format); return out;
    }
    out.appliedThreads = context->thread_count;
    out.appliedThreadType = context->thread_type;

    AVPacket* packet = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    const auto started = std::chrono::steady_clock::now();
    while (out.decodedFrames < framesToDecode && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index == streamIndex) {
            if (avcodec_send_packet(context, packet) >= 0) {
                while (avcodec_receive_frame(context, frame) >= 0) {
                    ++out.decodedFrames;
                    av_frame_unref(frame);
                    if (out.decodedFrames >= framesToDecode) break;
                }
            }
        }
        av_packet_unref(packet);
    }
    // 排空
    avcodec_send_packet(context, nullptr);
    while (avcodec_receive_frame(context, frame) >= 0) { ++out.decodedFrames; av_frame_unref(frame); }
    out.decodeMs = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count();
    out.ok = out.decodedFrames > 0;

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&context);
    avformat_close_input(&format);
    return out;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: dav1d_thread_probe <av1 文件> [--frames N]\n"); return 2; }
    int frames = 40;
    for (int i = 2; i + 1 < argc; ++i)
        if (std::wstring(argv[i]) == L"--frames") frames = _wtoi(argv[i + 1]);

    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    printf("文件: %s\n目标帧数: %d\n\n", path.c_str(), frames);
    // 用同一份代码跑不同线程数，直接对比
    for (const int threads : {1, 4, 8, 16}) {
        const auto r = Run(path, threads, frames);
        if (!r.ok) { printf("threads=%-3d 失败\n", threads); continue; }
        printf("请求 %-3d 线程 -> FFmpeg 记录 thread_count=%-3d thread_type=0x%X  "
               "解出 %-3d 帧 耗时 %8.1f ms (%.2f ms/帧)  codec=%s  caps=0x%X\n",
            threads, r.appliedThreads, r.appliedThreadType, r.decodedFrames,
            r.decodeMs, r.decodedFrames > 0 ? r.decodeMs / r.decodedFrames : 0.0,
            r.codecName.c_str(), r.capabilities);
    }
    return 0;
}
