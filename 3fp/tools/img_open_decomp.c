/* ============================================================================
 * img_open_decomp.c —— 静图「打开」耗时的分段归因基准（纯 FFmpeg SDK）
 *
 * 为什么需要：E1 探针只量到 FFF3FP_Open -> OpenCompleted 的一个总墙钟数
 *   （108 MP JPEG: base 975.3 ms / 命中 410.7 ms）。总差值只能说明「快了多少」，
 *   不能说明「省的是哪一段」。而 P0-a 的设计前提正是「缓存帧只替代首帧解码，
 *   其余（demux 探测、解码器打开、色彩转换、呈现、seek 回起点）两臂共有」。
 *   归因若错，收益解释就是编的。
 *
 * 本基准在同一台机器、同一文件上把那段路拆成 5 段，供交叉验证：
 *   tOpen     avformat_open_input            —— 容器/图像探测（读文件头）
 *   tInfo     avformat_find_stream_info      —— 填 codecpar（对 still 可能试解）
 *   tDecOpen  avcodec_open2                  —— 解码器初始化（含线程池）
 *   tDecode   av_read_frame + send/receive    —— 首帧解码（P0-a 声称省掉的项）
 *   tClone    av_frame_clone                 —— 命中时的帧深拷贝（P0-a 新增的成本项）
 *
 * 判据口径：只看同一进程内各段的相对占比与重复稳定性；不把本基准的绝对值
 *   直接等同于播放器里的墙钟，因为播放器另有呈现链、快照锁与 worker 排队。
 *
 * 编译（必须在 3fp/ 下）：python tools/build_probe.py img_open_decomp
 * 运行：img_open_decomp.exe --rounds 8 --out <csv> <file> [more files...]
 * ========================================================================== */
#include <windows.h>

#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/frame.h>
#include <libavutil/imgutils.h>

static double NowMs(void) {
    LARGE_INTEGER freq, counter;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * 1000.0 / (double)freq.QuadPart;
}

/* One measurement of "open this still and decode its first frame". */
typedef struct Sample {
    double openMs;
    double infoMs;
    double decOpenMs;
    double decodeMs;
    double cloneMs;
    int packets;
    long long packetBytes;
    long long frameBytes;
    int width;
    int height;
    int pixFmt;
    int requestedThreads;
    int activeThreads;
    int activeThreadType;
    int ok;
} Sample;

/* threads: 0 = let FFmpeg decide (av_cpu_count), >0 = request that many, -1 = force 1.
 * The player does not let FFmpeg decide: OpenDecoder sets thread_count from a
 * resolution tier and thread_type = FRAME|SLICE (PlayerSession.cpp:3616-3619), so
 * the decoder's parallelism is a first-class input to "how long does the first
 * frame take" and must be measured, not assumed. */
static Sample Measure(const char* path, int threads) {
    Sample s;
    memset(&s, 0, sizeof(s));
    s.pixFmt = -1;
    s.requestedThreads = threads;
    s.activeThreads = -1;
    s.activeThreadType = -1;

    AVFormatContext* format = NULL;
    AVCodecContext* decoder = NULL;
    AVFrame* frame = NULL;
    AVPacket* packet = NULL;

    double t0 = NowMs();
    if (avformat_open_input(&format, path, NULL, NULL) < 0) goto done;
    double t1 = NowMs();
    if (avformat_find_stream_info(format, NULL) < 0) goto done;
    double t2 = NowMs();

    int videoIndex = -1;
    for (unsigned i = 0; i < format->nb_streams; ++i) {
        if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
            videoIndex = (int)i;
            break;
        }
    }
    if (videoIndex < 0) goto done;

    {
        const AVCodec* codec = avcodec_find_decoder(format->streams[videoIndex]->codecpar->codec_id);
        if (codec == NULL) goto done;
        decoder = avcodec_alloc_context3(codec);
        if (decoder == NULL) goto done;
        if (avcodec_parameters_to_context(decoder, format->streams[videoIndex]->codecpar) < 0) goto done;
        if (threads > 0) {
            decoder->thread_count = threads;
            decoder->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
        }
        /* The player opens the CPU decoder on the same terms: default threads. */
        if (avcodec_open2(decoder, codec, NULL) < 0) goto done;
        s.activeThreads = decoder->thread_count;
        s.activeThreadType = (int)decoder->active_thread_type;
    }
    double t3 = NowMs();

    frame = av_frame_alloc();
    packet = av_packet_alloc();
    if (frame == NULL || packet == NULL) goto done;

    /* Read until the first decoded video frame. A still is one packet, but the
     * loop is generic so the same tool can be pointed at a short clip. */
    while (av_read_frame(format, packet) >= 0) {
        s.packets += 1;
        s.packetBytes += packet->size;
        if (packet->stream_index == videoIndex) {
            const int sent = avcodec_send_packet(decoder, packet);
            av_packet_unref(packet);
            if (sent < 0) continue;
            if (avcodec_receive_frame(decoder, frame) >= 0) break;
        } else {
            av_packet_unref(packet);
        }
    }
    /* Flush what the decoder is still holding (JPEG may need the EOF signal). */
    if (frame->data[0] == NULL) {
        avcodec_send_packet(decoder, NULL);
        (void)avcodec_receive_frame(decoder, frame);
    }
    double t4 = NowMs();

    if (frame->data[0] == NULL) goto done;
    s.width = frame->width;
    s.height = frame->height;
    s.pixFmt = (int)frame->format;
    s.frameBytes = (long long)av_image_get_buffer_size(
        (enum AVPixelFormat)frame->format, frame->width, frame->height, 1);
    if (s.frameBytes <= 0) s.frameBytes = 0;

    /* P0-a hit path pays exactly one deep copy of this frame (TakeStill ->
     * av_frame_clone). Quantify it: it is a real, non-zero cost of hitting. */
    {
        AVFrame* clone = av_frame_clone(frame);
        double t5 = NowMs();
        s.cloneMs = t5 - t4;
        av_frame_free(&clone);
        s.decodeMs = t4 - t3;
    }
    s.openMs = t1 - t0;
    s.infoMs = t2 - t1;
    s.decOpenMs = t3 - t2;
    s.ok = 1;

done:
    if (packet != NULL) av_packet_free(&packet);
    if (frame != NULL) av_frame_free(&frame);
    if (decoder != NULL) avcodec_free_context(&decoder);
    if (format != NULL) avformat_close_input(&format);
    return s;
}

int main(int argc, char** argv) {
    int rounds = 8;
    int threads = 0;
    const char* outPath = NULL;
    const char* files[16];
    int fileCount = 0;

    for (int i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "--rounds") == 0 && i + 1 < argc) rounds = atoi(argv[++i]);
        else if (strcmp(argv[i], "--threads") == 0 && i + 1 < argc) threads = atoi(argv[++i]);
        else if (strcmp(argv[i], "--out") == 0 && i + 1 < argc) outPath = argv[++i];
        else if (fileCount < 16) files[fileCount++] = argv[i];
    }
    if (fileCount == 0 || rounds < 1) {
        fprintf(stderr, "usage: img_open_decomp --rounds N [--threads N] --out CSV <file> [more...]\n");
        return 2;
    }

    FILE* csv = outPath != NULL ? fopen(outPath, "w") : stdout;
    if (csv == NULL) {
        fprintf(stderr, "cannot open %s\n", outPath);
        return 2;
    }
    fprintf(csv, "file,round,ok,width,height,pixFmt,reqThreads,activeThreads,activeThreadType,"
                 "packets,packetBytes,frameBytes,"
                 "openMs,infoMs,decOpenMs,decodeMs,cloneMs,pipelineMs\n");

    int failures = 0;
    for (int f = 0; f < fileCount; ++f) {
        const char* base = strrchr(files[f], '\\');
        base = base != NULL ? base + 1 : files[f];
        for (int round = 0; round < rounds; ++round) {
            const Sample s = Measure(files[f], threads);
            const double pipeline = s.openMs + s.infoMs + s.decOpenMs + s.decodeMs;
            fprintf(csv, "%s,%d,%d,%d,%d,%d,%d,%d,%d,%d,%lld,%lld,"
                         "%.3f,%.3f,%.3f,%.3f,%.3f,%.3f\n",
                base, round, s.ok, s.width, s.height, s.pixFmt, s.requestedThreads,
                s.activeThreads, s.activeThreadType, s.packets, s.packetBytes,
                s.frameBytes, s.openMs, s.infoMs, s.decOpenMs, s.decodeMs, s.cloneMs, pipeline);
            fflush(csv);
            fprintf(stderr, "[%s] r%d/%d ok=%d thr=%d/%d type=%d open=%.2f info=%.2f "
                            "decOpen=%.2f decode=%.2f clone=%.2f total=%.2f\n",
                base, round + 1, rounds, s.ok, s.requestedThreads, s.activeThreads,
                s.activeThreadType, s.openMs, s.infoMs, s.decOpenMs,
                s.decodeMs, s.cloneMs, pipeline);
            if (!s.ok) ++failures;
        }
    }
    if (csv != stdout) fclose(csv);
    fprintf(stderr, "DONE files=%d rounds=%d threads=%d failures=%d\n",
        fileCount, rounds, threads, failures);
    return 0;
}
