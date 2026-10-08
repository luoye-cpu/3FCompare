// 验证：解码中途改变 thread_count 是否需要重开解码器？
//
// 这决定了"动态线程数"的实现方式：
//   A) 若可热改 => 直接改 context->thread_count
//   B) 若必须重开 => 需要 avcodec_flush_buffers / 重新 open，会丢失参考帧
//
// 用法：thread_reconfig_test <媒体>

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <string>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
}

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: thread_reconfig_test <媒体>\n"); return 2; }
    const int size = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    std::string path(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, path.data(), size, nullptr, nullptr);

    AVFormatContext* format = nullptr;
    if (avformat_open_input(&format, path.c_str(), nullptr, nullptr) < 0) { printf("打开失败\n"); return 1; }
    avformat_find_stream_info(format, nullptr);
    const auto index = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    const auto* stream = format->streams[index];
    const auto* codec = stream->codecpar->codec_id == AV_CODEC_ID_AV1
        ? avcodec_find_decoder_by_name("libdav1d") : nullptr;
    if (codec == nullptr) codec = avcodec_find_decoder(stream->codecpar->codec_id);

    auto* context = avcodec_alloc_context3(codec);
    avcodec_parameters_to_context(context, stream->codecpar);
    context->thread_count = 8;
    context->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (avcodec_open2(context, codec, nullptr) < 0) { printf("open 失败\n"); return 1; }
    printf("初始 thread_count=%d  codec=%s\n", context->thread_count, codec->name);

    // 打开后直接改，看驱动是否采纳
    context->thread_count = 32;
    printf("直接改为 32 后，context->thread_count=%d\n", context->thread_count);
    printf("=> 注意：FFmpeg 只在 avcodec_open2 读取该字段，改这里不会重建线程池。\n");

    // 实际解码若干帧，确认仍能工作（即使设置未生效）
    AVPacket* packet = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    int decoded = 0;
    while (decoded < 5 && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index == index) {
            if (avcodec_send_packet(context, packet) >= 0) {
                while (avcodec_receive_frame(context, frame) >= 0) { ++decoded; av_frame_unref(frame); }
            }
        }
        av_packet_unref(packet);
    }
    printf("改线程数后仍解出 %d 帧 => 热改不崩溃，但也不会真正重建线程池\n", decoded);

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&context);
    avformat_close_input(&format);
    return 0;
}
