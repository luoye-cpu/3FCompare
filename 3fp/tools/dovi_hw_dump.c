// dovi_hw_dump.c —— 核查「杜比 P 系列 × 软解/硬解」RPU 介入证据的探针。
//
//   dovi_hw_dump <media> <out-prefix> <soft|d3d11|cuda> [decoder-name]
//
// 对给定媒体：
//   1. 流级：codecpar coded_side_data 里的 AV_PKT_DATA_DOVI_CONF（dvcC/dvvC），
//      打字节数与前 8 字节十六进制（AVDOVIDecoderConfigurationRecord 布局：
//      偏移 0/1 = 版本，2 = dv_profile，3 = dv_level，4 = rpu/el/bl 标志位打包）。
//   2. 帧级：解码至多 240 帧，逐帧报像素格式与 DOVI side data
//      （AV_FRAME_DATA_DOVI_METADATA / DOVI_RPU_BUFFER / HDR10+）尺寸，
//      第一个 DOVI_METADATA blob 写 <prefix>_rpu.bin。
// 硬解臂与内核同配：软解器 + hw_device_ctx + get_format 回调
// （PlayerSession.cpp 的 SelectHardwareFormat 同构）。
// 退出码：抓到 DOVI_METADATA = 0，否则 1；用法错误 = 2。

#include "libavformat/avformat.h"
#include "libavcodec/avcodec.h"
#include "libavutil/hwcontext.h"
#include "libavutil/pixdesc.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static enum AVPixelFormat g_hwPixFmt = AV_PIX_FMT_NONE;

static enum AVPixelFormat GetHwFormat(AVCodecContext* ctx,
                                      const enum AVPixelFormat* fmts)
{
    const enum AVPixelFormat* p;
    printf("  offered formats:");
    for (p = fmts; *p != AV_PIX_FMT_NONE; p++)
        printf(" %s", av_get_pix_fmt_name(*p));
    printf("\n");
    for (p = fmts; *p != AV_PIX_FMT_NONE; p++)
        if (*p == g_hwPixFmt) return *p;
    printf("  hw format %s NOT offered\n", av_get_pix_fmt_name(g_hwPixFmt));
    return AV_PIX_FMT_NONE;
}

static int WriteWhole(const char* path, const unsigned char* bytes, int size)
{
    FILE* f = fopen(path, "wb");
    if (!f) return -1;
    size_t n = fwrite(bytes, 1, (size_t)size, f);
    fclose(f);
    return n == (size_t)size ? 0 : -1;
}

static void PrintSideData(const AVFrame* frame)
{
    for (int i = 0; i < frame->nb_side_data; i++) {
        const AVFrameSideData* sd = frame->side_data[i];
        const char* name = av_frame_side_data_name(sd->type);
        printf("    side[%d] type=%d (%s) %d bytes\n",
               i, (int)sd->type, name ? name : "?", sd->size);
    }
}

int main(int argc, char** argv)
{
    if (argc < 4) {
        printf("usage: dovi_hw_dump <media> <prefix> <soft|d3d11|cuda> [decoder]\n");
        return 2;
    }
    const char* media = argv[1];
    const char* prefix = argv[2];
    const char* mode = argv[3];
    const char* decoderName = argc > 4 ? argv[4] : NULL;
    int streamOverride = argc > 5 ? atoi(argv[5]) : -1;

    unsigned libVer = avformat_version();
    printf("avformat %u.%u.%u\n", AV_VERSION_MAJOR(libVer),
           AV_VERSION_MINOR(libVer), AV_VERSION_MICRO(libVer));

    AVFormatContext* format = NULL;
    if (avformat_open_input(&format, media, NULL, NULL) < 0) {
        printf("open failed\n");
        return 1;
    }
    if (avformat_find_stream_info(format, NULL) < 0) {
        printf("find_stream_info failed\n");
        return 1;
    }
    int vs = streamOverride >= 0 ? streamOverride
        : av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    if (vs < 0) { printf("no video stream\n"); return 1; }
    // 流清单：双轨（BL+EL 两条 video track）在这里现形。
    for (unsigned i = 0; i < format->nb_streams; i++) {
        const AVStream* s = format->streams[i];
        if (s->codecpar->codec_type != AVMEDIA_TYPE_VIDEO) continue;
        printf("video stream %u: %s %dx%d profile=%d coded_side_data=%u\n",
               i, avcodec_get_name(s->codecpar->codec_id),
               s->codecpar->width, s->codecpar->height,
               s->codecpar->profile, s->codecpar->nb_coded_side_data);
        for (unsigned k = 0; k < s->codecpar->nb_coded_side_data; k++) {
            const AVPacketSideData* sd = &s->codecpar->coded_side_data[k];
            printf("  stream %u side[%u]: type=%d (%s) %d bytes", i, k,
                   (int)sd->type, av_packet_side_data_name(sd->type), sd->size);
            if (sd->type == AV_PKT_DATA_DOVI_CONF && sd->size > 2)
                printf("  (dv_profile=%u)", sd->data[2]);
            printf("\n");
        }
    }
    const AVStream* stream = format->streams[vs];
    printf("stream %d: %s %dx%d profile=%d\n", vs,
           avcodec_get_name(stream->codecpar->codec_id),
           stream->codecpar->width, stream->codecpar->height,
           stream->codecpar->profile);

    // 流级 dvcC/dvvC：容器声明，与解码器无关。
    const AVPacketSideData* conf = av_packet_side_data_get(
        stream->codecpar->coded_side_data,
        stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    if (conf != NULL) {
        printf("conf record: %d bytes, first bytes:", conf->size);
        for (int i = 0; i < conf->size && i < 8; i++)
            printf(" %02X", conf->data[i]);
        printf("  (dv_profile=%u)\n", conf->size > 2 ? conf->data[2] : 0);
    } else {
        printf("conf record: none\n");
    }

    // 解码器：显式名字优先（libdav1d 等），否则该流的默认软解器。
    const AVCodec* codec = decoderName
        ? avcodec_find_decoder_by_name(decoderName)
        : avcodec_find_decoder(stream->codecpar->codec_id);
    if (!codec) { printf("decoder not found\n"); return 1; }
    AVCodecContext* ctx = avcodec_alloc_context3(codec);
    if (!ctx) return 1;
    avcodec_parameters_to_context(ctx, stream->codecpar);
    ctx->thread_count = 1;

    AVBufferRef* hwDev = NULL;
    if (strcmp(mode, "d3d11") == 0) {
        g_hwPixFmt = AV_PIX_FMT_D3D11;
        if (av_hwdevice_ctx_create(&hwDev, AV_HWDEVICE_TYPE_D3D11VA,
                                   NULL, NULL, 0) < 0) {
            printf("d3d11va device create FAILED\n");
            return 1;
        }
    } else if (strcmp(mode, "cuda") == 0) {
        g_hwPixFmt = AV_PIX_FMT_CUDA;
        if (av_hwdevice_ctx_create(&hwDev, AV_HWDEVICE_TYPE_CUDA,
                                   NULL, NULL, 0) < 0) {
            printf("cuda device create FAILED\n");
            return 1;
        }
    } else if (strcmp(mode, "soft") != 0) {
        printf("unknown mode %s\n", mode);
        return 2;
    }
    if (hwDev) {
        ctx->hw_device_ctx = av_buffer_ref(hwDev);
        ctx->get_format = GetHwFormat;
        printf("hw mode: %s (pix fmt %s)\n", mode,
               av_get_pix_fmt_name(g_hwPixFmt));
    } else {
        printf("soft mode\n");
    }

    if (avcodec_open2(ctx, codec, NULL) < 0) {
        printf("decoder open failed (%s)\n", codec->name);
        return 1;
    }
    printf("decoder: %s\n", codec->name);

    AVPacket* pkt = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    int scanned = 0, metaHits = 0, rpuBufHits = 0, dumped = 0, eof = 0;
    int pktDumped = 0;
    char path[1024];

    while (scanned < 240 && !dumped && !eof) {
        int r = av_read_frame(format, pkt);
        if (r < 0) {
            eof = 1;
            avcodec_send_packet(ctx, NULL);  // flush
            pkt = NULL;
        } else if (pkt->stream_index != vs) {
            av_packet_unref(pkt);
            continue;
        }
        // 前 5 个视频包的包级 side data：MKV 的 RPU BlockAdditional 在这里现形。
        if (!pktDumped && pkt->side_data_elems > 0) {
            for (int i = 0; i < pkt->side_data_elems; i++) {
                const AVPacketSideData* sd = &pkt->side_data[i];
                printf("pkt side data: type=%d (%s) %d bytes\n",
                       (int)sd->type, av_packet_side_data_name(sd->type), sd->size);
            }
            pktDumped = 1;
        }
        r = avcodec_send_packet(ctx, pkt);
        if (pkt) av_packet_unref(pkt);
        if (r < 0 && r != AVERROR(EAGAIN)) break;
        while (scanned < 240) {
            r = avcodec_receive_frame(ctx, frame);
            if (r < 0) break;
            scanned++;
            int hasMeta = av_frame_get_side_data(frame, AV_FRAME_DATA_DOVI_METADATA) != NULL;
            int hasRpuBuf = av_frame_get_side_data(frame, AV_FRAME_DATA_DOVI_RPU_BUFFER) != NULL;
            if (hasMeta) metaHits++;
            if (hasRpuBuf) rpuBufHits++;
            if (scanned <= 3 || (hasMeta && metaHits == 1)) {
                printf("frame %d: fmt=%s dovi_meta=%d rpu_buf=%d\n",
                       scanned, av_get_pix_fmt_name((enum AVPixelFormat)frame->format),
                       hasMeta, hasRpuBuf);
                PrintSideData(frame);
            }
            if (hasMeta && !dumped) {
                const AVFrameSideData* sd =
                    av_frame_get_side_data(frame, AV_FRAME_DATA_DOVI_METADATA);
                snprintf(path, sizeof path, "%s_rpu.bin", prefix);
                if (WriteWhole(path, sd->data, sd->size) == 0) {
                    printf("rpu blob: %d bytes -> %s (pts=%lld)\n",
                           sd->size, path, (long long)frame->pts);
                    dumped = 1;
                }
            }
            av_frame_unref(frame);
        }
    }

    printf("summary: frames=%d dovi_meta_hits=%d rpu_buf_hits=%d dumped=%d\n",
           scanned, metaHits, rpuBufHits, dumped);

    av_frame_free(&frame);
    av_packet_free(&pkt);
    avcodec_free_context(&ctx);
    if (hwDev) av_buffer_unref(&hwDev);
    avformat_close_input(&format);
    return dumped ? 0 : 1;
}
