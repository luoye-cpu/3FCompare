// ============================================================================
// apng_seek_repro.c —— 独立复现「APNG 在 av_seek_frame 后循环丢帧」
//
// 背景：3FC 内核 PlayerSession::DecodeInitialFrame() 在呈现首帧后会执行
//   av_seek_frame(format, videoStream, start, AVSEEK_FLAG_BACKWARD)
// 以便"打开动图后从头播放"。实测发现 APNG 在此之后进入 ignore_loop=0 的
// 自循环时，每轮丢失一帧的时间片（pts 0.9 -> 1.1，跳过 1.0）。
// 本探针不依赖 3FC，直接用 libavformat 复现，判定这是 FFmpeg 的 demuxer
// 行为还是 3FC 的缺陷。
//
// 用法：apng_seek_repro <媒体路径> [模式]
//   模式: noseek | seek_backward | seek_any | seek_byte
// ============================================================================
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>

int main(int argc, char** argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc < 2) { printf("usage: apng_seek_repro <path> [noseek|seek_backward|seek_any|seek_byte]\n"); return 2; }
    const char* path = argv[1];
    const char* mode = argc >= 3 ? argv[2] : "noseek";

    avformat_network_init();
    AVFormatContext* fmt = NULL;
    AVDictionary* opts = NULL;
    av_dict_set(&opts, "ignore_loop", "0", 0);
    int r = avformat_open_input(&fmt, path, NULL, &opts);
    av_dict_free(&opts);
    if (r < 0) { printf("open failed: %d\n", r); return 1; }
    avformat_find_stream_info(fmt, NULL);

    int vs = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    if (vs < 0) { printf("no video stream\n"); return 1; }
    printf("format=%s stream=%d duration=%lld\n", fmt->iformat->name, vs, (long long)fmt->duration);

    const AVCodec* dec = avcodec_find_decoder(fmt->streams[vs]->codecpar->codec_id);
    AVCodecContext* cc = avcodec_alloc_context3(dec);
    avcodec_parameters_to_context(cc, fmt->streams[vs]->codecpar);
    if (avcodec_open2(cc, dec, NULL) < 0) { printf("decoder open failed\n"); return 1; }

    AVPacket* pkt = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    const AVRational tb = fmt->streams[vs]->time_base;

    // 步骤 1：读若干帧（模拟 DecodeInitialFrame 读到首帧）
    int firstReads = 0;
    printf("--- phase 1: read first 3 packets ---\n");
    while (firstReads < 3 && av_read_frame(fmt, pkt) >= 0) {
        if (pkt->stream_index == vs) {
            printf("  pkt pts=%lld (%.3fs) dur=%lld\n", (long long)pkt->pts,
                pkt->pts * av_q2d(tb), (long long)pkt->duration);
            ++firstReads;
        }
        av_packet_unref(pkt);
    }

    // 步骤 2：按模式执行 seek
    if (strcmp(mode, "noseek") != 0) {
        int64_t start = fmt->streams[vs]->start_time == AV_NOPTS_VALUE ? 0 : fmt->streams[vs]->start_time;
        int flags = AVSEEK_FLAG_BACKWARD;
        if (strcmp(mode, "seek_any") == 0) flags = AVSEEK_FLAG_ANY;
        if (strcmp(mode, "seek_byte") == 0) flags = AVSEEK_FLAG_BYTE;
        int sr = av_seek_frame(fmt, strcmp(mode, "seek_byte") == 0 ? -1 : vs, start, flags);
        printf("--- phase 2: av_seek_frame(%s) -> %d ---\n", mode, sr);
        avcodec_flush_buffers(cc);
    } else {
        printf("--- phase 2: NO seek ---\n");
    }

    // 步骤 3：连续读 40 个包，看 pts 是否在轮次接缝处跳号
    printf("--- phase 3: pts sequence (backend=%s) ---\n", mode);
    int n = 0;
    while (n < 40) {
        r = av_read_frame(fmt, pkt);
        if (r < 0) { printf("  EOF/err %d at n=%d\n", r, n); break; }
        if (pkt->stream_index != vs) { av_packet_unref(pkt); continue; }
        const double t = pkt->pts == AV_NOPTS_VALUE ? -1.0 : pkt->pts * av_q2d(tb);
        printf("  %2d  pkt pts=%8.4fs", n, t);
        // 真正送进解码器，并取出解码帧 —— 区分「包没发」与「发了但 decoder 不出帧」
        int sent = avcodec_send_packet(cc, pkt);
        int got = 0;
        while (avcodec_receive_frame(cc, frame) >= 0) {
            const double ft = frame->pts == AV_NOPTS_VALUE ? -1.0
                : frame->pts * av_q2d(tb);
            printf("   [frame pts=%8.4fs]", ft);
            ++got;
            av_frame_unref(frame);
        }
        if (got == 0) printf("   [NO FRAME OUTPUT]");
        printf("   send=%d\n", sent);
        ++n;
        av_packet_unref(pkt);
    }

    av_frame_free(&frame); av_packet_free(&pkt);
    avcodec_free_context(&cc); avformat_close_input(&fmt);
    return 0;
}
