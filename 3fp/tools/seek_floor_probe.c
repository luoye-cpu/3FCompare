// ============================================================================
// seek_floor_probe.c —— 量出「跳转」在纯 FFmpeg 侧的物理下限
//
// 背景：用户报「来回快进/快退时卡顿」。内核 FFF3FP_Seek → DoSeek(exact=true)
//   把读位置甩到**目标之前的关键帧**，再由 DecodeUntilSeekTarget 逐包解码到目标，
//   期间每一帧都要真解（PresentVideoFrame 里对 position < target 只 return，
//   是"解了不显示"，不是"不解"）。所以单次跳转的固有成本 =
//   关键帧间距 × 解码速度。本探针不依赖 3FC，直接量这条下限：
//     · 关键帧表 / GOP 长度分布
//     · av_seek_frame 本身的耗时（demux 侧）
//     · seek 后解码到目标要解多少帧、多少字节、多少毫秒（decode 侧）
//   与 seek_latency_probe（内核端到端）对比，即可判定卡顿是"物理下限"还是
//   "内核额外开销（排队/呈现/重置）"。
//
// 用法：
//   seek_floor_probe <媒体> [--base 秒] [--step 秒] [--rounds N] [--gop]
//   默认 base = 时长 40%，step = 2 秒，rounds = 12（+ + + - - - 交替）
//   --gop 只打印关键帧表与 GOP 统计，不做跳转测量
// ============================================================================
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>

static int64_t NowNs(void) {
    LARGE_INTEGER f, t;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t);
    return (int64_t)((double)t.QuadPart * 1e9 / (double)f.QuadPart);
}

static double Sec(int64_t ts, AVRational tb) {
    return ts == AV_NOPTS_VALUE ? -1.0 : (double)ts * av_q2d(tb);
}

int main(int argc, char** argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc < 2) {
        printf("usage: seek_floor_probe <media> [--base s] [--step s] [--rounds n] [--gop]\n");
        return 2;
    }
    const char* path = argv[1];
    double baseSec = -1.0, stepSec = 2.0;
    int rounds = 12, gopOnly = 0;
    // --skip nonref : 只解参考帧（AVDISCARD_NONREF），--near S : 距目标 S 秒内恢复全解。
    // 这两项直接对应内核里要试的 seek 跳帧策略，先在纯 FFmpeg 侧量出收益与落点代价。
    const char* skipMode = "none";
    double nearSec = 0.0;
    for (int i = 2; i < argc; ++i) {
        if (!strcmp(argv[i], "--base") && i + 1 < argc) baseSec = atof(argv[++i]);
        else if (!strcmp(argv[i], "--step") && i + 1 < argc) stepSec = atof(argv[++i]);
        else if (!strcmp(argv[i], "--rounds") && i + 1 < argc) rounds = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--gop")) gopOnly = 1;
        else if (!strcmp(argv[i], "--skip") && i + 1 < argc) skipMode = argv[++i];
        else if (!strcmp(argv[i], "--near") && i + 1 < argc) nearSec = atof(argv[++i]);
    }

    avformat_network_init();
    AVFormatContext* fmt = NULL;
    if (avformat_open_input(&fmt, path, NULL, NULL) < 0) { printf("open failed\n"); return 1; }
    if (avformat_find_stream_info(fmt, NULL) < 0) { printf("stream info failed\n"); return 1; }

    const int vs = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    if (vs < 0) { printf("no video stream\n"); return 1; }
    AVStream* st = fmt->streams[vs];
    const AVCodec* dec = avcodec_find_decoder(st->codecpar->codec_id);
    AVCodecContext* cc = dec ? avcodec_alloc_context3(dec) : NULL;
    if (!cc || avcodec_parameters_to_context(cc, st->codecpar) < 0 ||
        avcodec_open2(cc, dec, NULL) < 0) { printf("decoder open failed\n"); return 1; }

    const double duration = fmt->duration > 0 ? (double)fmt->duration / AV_TIME_BASE : -1.0;
    printf("file      : %s\n", path);
    printf("container : %s  codec=%s  %dx%d  tb=%d/%d  duration=%.3fs  nb_frames=%lld\n",
           fmt->iformat->name, dec->name, st->codecpar->width, st->codecpar->height,
           st->time_base.num, st->time_base.den, duration,
           (long long)st->nb_frames);

    // ---- 关键帧表：一趟顺序读，记录每个关键帧的 pts 与包序号 ----
    AVPacket* pkt = av_packet_alloc();
    int64_t kfTs[8192];
    int64_t packetOfKf[8192];
    int kfCount = 0;
    int64_t packetIndex = 0;
    int64_t totalPackets = 0;
    while (av_read_frame(fmt, pkt) >= 0) {
        if (pkt->stream_index == vs) {
            if (pkt->flags & AV_PKT_FLAG_KEY) {
                if (kfCount < 8192) { kfTs[kfCount] = pkt->pts; packetOfKf[kfCount] = packetIndex; ++kfCount; }
            }
            ++packetIndex;
            ++totalPackets;
        }
        av_packet_unref(pkt);
    }
    printf("packets   : %lld (video)  keyframes=%d\n", (long long)totalPackets, kfCount);
    if (kfCount >= 2) {
        double minGop = 1e9, maxGop = 0, sumGop = 0;
        for (int i = 1; i < kfCount; ++i) {
            const double g = Sec(kfTs[i], st->time_base) - Sec(kfTs[i - 1], st->time_base);
            const int64_t frames = packetOfKf[i] - packetOfKf[i - 1];
            if (g < minGop) minGop = g;
            if (g > maxGop) maxGop = g;
            sumGop += g;
            if (i <= 6 || gopOnly)
                printf("  kf[%3d] t=%8.3fs  pkt=%6lld  gop=%6.3fs / %lld 包\n",
                       i, Sec(kfTs[i], st->time_base), (long long)packetOfKf[i], g, (long long)frames);
        }
        printf("GOP       : min=%.3fs mean=%.3fs max=%.3fs  (n=%d)\n",
               minGop, sumGop / (kfCount - 1), maxGop, kfCount - 1);
    }
    if (gopOnly) {
        av_packet_free(&pkt);
        avcodec_free_context(&cc);
        avformat_close_input(&fmt);
        return 0;
    }

    // ---- 跳转测量：av_seek_frame 耗时 + 解码到目标耗时 ----
    const double startSec = baseSec >= 0 ? baseSec
        : (duration > 0 ? duration * 0.40 : 10.0);
    if (duration > 0) {
        // 保证整段脚本留在片内
        const double span = stepSec * rounds;
        if (startSec + span > duration) baseSec = startSec - span * 0.5;
    }
    const int64_t startTs = (int64_t)(startSec / av_q2d(st->time_base));

    AVFrame* frame = av_frame_alloc();
    double pos = startSec;
    printf("\nskip=%s near=%.2fs\n", skipMode, nearSec);
    printf("%-4s %-9s %-9s %-10s %-9s %-8s %-8s %-9s\n",
           "#", "target", "seek_ms", "decode_ms", "frames", "landed", "dist_ms", "hit_err_ms");
    double sumSeek = 0, sumDecode = 0;
    long sumFrames = 0;
    for (int i = 0; i < rounds; ++i) {
        // + + + - - - 的来回脚本：用户按快进三次再按快退三次
        const int phase = (i / 3) % 2;
        pos = pos + (phase == 0 ? stepSec : -stepSec);
        if (pos < 0) pos = 0;
        const int64_t targetTs = (int64_t)(pos / av_q2d(st->time_base));

        const int64_t t0 = NowNs();
        const int r = av_seek_frame(fmt, vs, targetTs, AVSEEK_FLAG_BACKWARD);
        const int64_t t1 = NowNs();
        if (r < 0) { printf("%-4d seek failed rc=%d\n", i, r); continue; }

        // 找到落点（seek 后第一个包的 pts 就是关键帧时间），再解到 target
        double landed = -1.0, hit = -1.0;
        long framesDecoded = 0;
        int64_t firstFramePts = AV_NOPTS_VALUE;
        avcodec_flush_buffers(cc);
        while (av_read_frame(fmt, pkt) >= 0) {
            if (pkt->stream_index != vs) { av_packet_unref(pkt); continue; }
            if (landed < 0) landed = Sec(pkt->pts, st->time_base);
            if (strcmp(skipMode, "none") != 0) {
                // 非参考帧不被任何人引用，跳过它们的重建不影响参考链；
                // 但目标帧本身可能就是非参考帧，所以接近目标必须恢复全解。
                const int64_t pktTs = pkt->pts != AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                const double pp = Sec(pktTs, st->time_base);
                cc->skip_frame = (nearSec > 0.0 && pp >= pos - nearSec)
                    ? AVDISCARD_DEFAULT : AVDISCARD_NONREF;
            }
            avcodec_send_packet(cc, pkt);
            av_packet_unref(pkt);
            int rr;
            while ((rr = avcodec_receive_frame(cc, frame)) >= 0) {
                ++framesDecoded;
                if (firstFramePts == AV_NOPTS_VALUE) firstFramePts = frame->pts;
                const double fp = Sec(frame->pts, st->time_base);
                if (fp + 1e-6 >= pos - 1e-6) { hit = fp; goto reached; }
                av_frame_unref(frame);
            }
        }
    reached:
        av_frame_unref(frame);
        const int64_t t2 = NowNs();
        const double seekMs = (t1 - t0) / 1e6;
        const double decodeMs = (t2 - t1) / 1e6;
        printf("%-4d %8.3fs %8.2f %9.2f %8ld %8.3fs %8.1f %8.1f\n",
               i, pos, seekMs, decodeMs, framesDecoded, landed,
               (pos - landed) * 1000.0, hit >= 0.0 ? (hit - pos) * 1000.0 : -1.0);
        sumSeek += seekMs;
        sumDecode += decodeMs;
        sumFrames += framesDecoded;
    }
    printf("\n平均：seek %.2f ms + decode %.2f ms = %.2f ms；解码 %.1f 帧/次\n",
           sumSeek / rounds, sumDecode / rounds, (sumSeek + sumDecode) / rounds,
           (double)sumFrames / rounds);

    av_frame_free(&frame);
    av_packet_free(&pkt);
    avcodec_free_context(&cc);
    avformat_close_input(&fmt);
    return 0;
}
