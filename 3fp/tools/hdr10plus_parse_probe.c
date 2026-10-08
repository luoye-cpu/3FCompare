/* hdr10plus_parse_probe.c — print a per-frame ST 2094-40 / ST 2094-30 parse
 * dump straight from the decoder, so it can be diffed against ffprobe.
 *
 * Why this exists
 * ---------------
 * The kernel-side parse is the part of the HDR10+ feature most likely to be
 * subtly wrong, and reading it back through the player would let a bug in the
 * render path hide a bug in the parse path. This probe therefore talks to
 * libavcodec directly and prints the fields *before* they reach HdrProcessor.
 *
 * It deliberately does NOT reuse HdrMetadataParse: printing our own output
 * would be self-certification. The dump below is the raw FFmpeg side data, and
 * the comparison is done outside, against `ffprobe -show_frames`.
 *
 * Usage: hdr10plus_parse_probe <media path> [maxFrames]
 */

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hdr_dynamic_metadata.h>
#include <libavutil/hdr_dynamic_vivid_metadata.h>
#include <libavutil/rational.h>

#include <stdio.h>
#include <stdlib.h>

static double Q(AVRational value) {
    return value.den == 0 ? 0.0 : av_q2d(value);
}

static void DumpHdr10Plus(const AVDynamicHDRPlus* plus, int frameIndex) {
    unsigned windows = plus->num_windows > 3 ? 3u : plus->num_windows;
    unsigned index;
    printf("frame %d hdr10plus windows=%u targeted_display_luminance=%.6f\n",
        frameIndex, (unsigned)plus->num_windows,
        Q(plus->targeted_system_display_maximum_luminance));
    for (index = 0; index < windows; ++index) {
        const AVHDRPlusColorTransformParams* window = &plus->params[index];
        unsigned point;
        printf("  window %u maxscl=%.5f,%.5f,%.5f average_maxrgb=%.5f"
            " fraction_bright_pixels=%.5f\n",
            index,
            Q(window->maxscl[0]), Q(window->maxscl[1]), Q(window->maxscl[2]),
            Q(window->average_maxrgb), Q(window->fraction_bright_pixels));
        printf("  window %u tone_mapping_flag=%u knee=(%.5f,%.5f) anchors=%u\n",
            index, (unsigned)window->tone_mapping_flag,
            Q(window->knee_point_x), Q(window->knee_point_y),
            (unsigned)window->num_bezier_curve_anchors);
        printf("  window %u anchors=[", index);
        for (point = 0;
            point < window->num_bezier_curve_anchors && point < 15; ++point)
            printf("%s%.5f", point ? "," : "", Q(window->bezier_curve_anchors[point]));
        printf("]\n");
        printf("  window %u distribution=%u [", index,
            (unsigned)window->num_distribution_maxrgb_percentiles);
        for (point = 0;
            point < window->num_distribution_maxrgb_percentiles && point < 15; ++point) {
            const AVHDRPlusPercentile* entry = &window->distribution_maxrgb[point];
            /* NOTE the swapped names: `percentage` is the percentage and
             * `percentile` is the linearized maxRGB value. */
            printf("%s%u:%.5f", point ? "," : "",
                (unsigned)entry->percentage, Q(entry->percentile));
        }
        printf("]\n");
    }
}

static void DumpVivid(const AVDynamicHDRVivid* vivid, int frameIndex) {
    unsigned windows = vivid->num_windows > 3 ? 3u : vivid->num_windows;
    unsigned index;
    printf("frame %d vivid windows=%u\n", frameIndex, (unsigned)vivid->num_windows);
    for (index = 0; index < windows; ++index) {
        const AVHDRVividColorTransformParams* window = &vivid->params[index];
        printf("  window %u maximum_maxrgb=%.5f average_maxrgb=%.5f"
            " tone_mapping_mode_flag=%d params=%d\n",
            index, Q(window->maximum_maxrgb), Q(window->average_maxrgb),
            window->tone_mapping_mode_flag, window->tone_mapping_param_num);
        if (window->tone_mapping_param_num > 0) {
            const AVHDRVividColorToneMappingParams* mapping = &window->tm_params[0];
            printf("  window %u targeted_display_luminance=%.6f"
                " three_Spline_enable=%d num=%d\n",
                index, Q(mapping->targeted_system_display_maximum_luminance),
                mapping->three_Spline_enable_flag, mapping->three_Spline_num);
        }
    }
}

int main(int argc, char** argv) {
    const char* path;
    int maxFrames = 30;
    AVFormatContext* format = NULL;
    AVCodecParameters* parameters;
    const AVCodec* codec;
    AVCodecContext* context;
    AVPacket* packet;
    AVFrame* frame;
    int streamIndex;
    int frameIndex = 0, plusFrames = 0, vividFrames = 0, done = 0;

    if (argc < 2) {
        printf("usage: hdr10plus_parse_probe <media path> [maxFrames]\n");
        return 2;
    }
    path = argv[1];
    if (argc > 2) maxFrames = atoi(argv[2]);

    if (avformat_open_input(&format, path, NULL, NULL) < 0) {
        printf("open failed: %s\n", path);
        return 3;
    }
    if (avformat_find_stream_info(format, NULL) < 0) {
        printf("find_stream_info failed\n");
        avformat_close_input(&format);
        return 3;
    }
    streamIndex = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    if (streamIndex < 0) {
        printf("no video stream\n");
        avformat_close_input(&format);
        return 3;
    }
    parameters = format->streams[streamIndex]->codecpar;
    codec = avcodec_find_decoder(parameters->codec_id);
    if (codec == NULL) {
        printf("no decoder for codec id %d\n", (int)parameters->codec_id);
        avformat_close_input(&format);
        return 3;
    }
    context = avcodec_alloc_context3(codec);
    if (context == NULL || avcodec_parameters_to_context(context, parameters) < 0) {
        printf("codec context setup failed\n");
        avcodec_free_context(&context);
        avformat_close_input(&format);
        return 3;
    }
    if (avcodec_open2(context, codec, NULL) < 0) {
        printf("avcodec_open2 failed\n");
        avcodec_free_context(&context);
        avformat_close_input(&format);
        return 3;
    }

    printf("codec=%s trc=%d primaries=%d\n", codec->name,
        (int)parameters->color_trc, (int)parameters->color_primaries);

    packet = av_packet_alloc();
    frame = av_frame_alloc();
    while (!done && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index != streamIndex) {
            av_packet_unref(packet);
            continue;
        }
        if (avcodec_send_packet(context, packet) < 0) {
            av_packet_unref(packet);
            continue;
        }
        av_packet_unref(packet);
        while (avcodec_receive_frame(context, frame) == 0) {
            AVPacketSideData* unused = NULL;
            ++frameIndex;
            (void)unused;
            if (frameIndex <= maxFrames) {
                AVFrameSideData* data = av_frame_get_side_data(
                    frame, AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
                if (data != NULL && data->size >= sizeof(AVDynamicHDRPlus)) {
                    ++plusFrames;
                    DumpHdr10Plus((const AVDynamicHDRPlus*)data->data, frameIndex);
                } else {
                    AVFrameSideData* vividData = av_frame_get_side_data(
                        frame, AV_FRAME_DATA_DYNAMIC_HDR_VIVID);
                    if (vividData != NULL && vividData->size >= sizeof(AVDynamicHDRVivid)) {
                        ++vividFrames;
                        DumpVivid((const AVDynamicHDRVivid*)vividData->data, frameIndex);
                    } else {
                        printf("frame %d (no dynamic metadata side data)\n", frameIndex);
                    }
                }
            }
            av_frame_unref(frame);
            if (frameIndex >= maxFrames) { done = 1; break; }
        }
    }
    /* Flush the decoder so trailing frames are counted too. */
    avcodec_send_packet(context, NULL);
    while (avcodec_receive_frame(context, frame) == 0) {
        ++frameIndex;
        av_frame_unref(frame);
    }

    printf("summary: decoded=%d hdr10plus_frames=%d vivid_frames=%d\n",
        frameIndex, plusFrames, vividFrames);

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&context);
    avformat_close_input(&format);
    return 0;
}
