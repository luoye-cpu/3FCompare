// One-shot probe: dump the raw side-data bytes a kind-3 host would feed the
// external colour-transform module, from a real dual-layer Dolby-Vision file.
//
//   argv[1] = media path
//   argv[2] = output prefix (writes <prefix>_conf.bin, <prefix>_rpu.bin)
//
// Prints the byte sizes it wrote; nonzero exit on any failure. The blob bytes
// are exactly what AV_FRAME_DATA_DOVI_METADATA / AV_PKT_DATA_DOVI_CONF carry,
// so a ctypes caller can hand them to the module unmodified.

extern "C" {
#include "libavformat/avformat.h"
#include "libavcodec/avcodec.h"
#include "libavutil/dovi_meta.h"
#include "libavutil/frame.h"
}

#include <cstdio>
#include <cstring>

static int WriteWhole(const char* path, const void* bytes, std::size_t size) {
    FILE* f = std::fopen(path, "wb");
    if (f == nullptr) return -1;
    const std::size_t written = std::fwrite(bytes, 1, size, f);
    std::fclose(f);
    return written == size ? 0 : -1;
}

int main(int argc, char** argv)
{
    if (argc < 3) {
        std::printf("usage: dovi_blob_dump <media> <out-prefix>\n");
        return 2;
    }
    const char* media = argv[1];
    const char* prefix = argv[2];

    AVFormatContext* format = nullptr;
    if (avformat_open_input(&format, media, nullptr, nullptr) < 0) {
        std::printf("open failed\n");
        return 1;
    }
    if (avformat_find_stream_info(format, nullptr) < 0) {
        std::printf("find_stream_info failed\n");
        return 1;
    }
    const AVStream* stream = format->streams[0];
    std::printf("stream 0: %s %dx%d\n", avcodec_get_name(stream->codecpar->codec_id),
        stream->codecpar->width, stream->codecpar->height);

    char path[1024];
    const AVPacketSideData* conf = av_packet_side_data_get(
        stream->codecpar->coded_side_data, stream->codecpar->nb_coded_side_data,
        AV_PKT_DATA_DOVI_CONF);
    if (conf != nullptr) {
        std::snprintf(path, sizeof path, "%s_conf.bin", prefix);
        if (WriteWhole(path, conf->data, conf->size) == 0)
            std::printf("conf record: %u bytes -> %s\n", conf->size, path);
    } else {
        std::printf("conf record: none\n");
    }

    const AVCodec* codec = avcodec_find_decoder(stream->codecpar->codec_id);
    AVCodecContext* context = avcodec_alloc_context3(codec);
    avcodec_parameters_to_context(context, stream->codecpar);
    context->thread_count = 1;
    if (avcodec_open2(context, codec, nullptr) < 0) {
        std::printf("decoder open failed\n");
        return 1;
    }
    AVPacket* packet = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    int dumped = 0;
    while (!dumped && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index != 0) { av_packet_unref(packet); continue; }
        if (avcodec_send_packet(context, packet) >= 0) {
            while (!dumped && avcodec_receive_frame(context, frame) >= 0) {
                const AVFrameSideData* rpu = av_frame_get_side_data(
                    frame, AV_FRAME_DATA_DOVI_METADATA);
                if (rpu != nullptr) {
                    std::snprintf(path, sizeof path, "%s_rpu.bin", prefix);
                    if (WriteWhole(path, rpu->data, rpu->size) == 0) {
                        std::printf("rpu blob: %u bytes -> %s (frame pts=%lld)\n",
                            rpu->size, path, static_cast<long long>(frame->pts));
                        dumped = 1;
                    }
                }
                av_frame_unref(frame);
            }
        }
        av_packet_unref(packet);
    }
    if (!dumped) std::printf("no DOVI_METADATA frame found\n");

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&context);
    avformat_close_input(&format);
    return dumped ? 0 : 1;
}
