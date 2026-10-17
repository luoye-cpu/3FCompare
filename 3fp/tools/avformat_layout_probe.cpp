// 布局辨认探针：用内核实际加载的 avformat-63.dll 打开文件，打印音频流声道
// 布局。解决"ffprobe(publish 构建) 说 7.1(wide)，内核(2026-08-15 构建) 眼里
// 是什么布局"的争议——双耳映射的选择以内核所见为准。
#include <cstdio>
#include <string>

extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
}

int main(int argc, char** argv) {
    if (argc < 2) { std::printf("用法: %s <file>\n", argv[0]); return 2; }
    const AVInputFormat* fmt = nullptr;
    AVFormatContext* ctx = nullptr;
    if (avformat_open_input(&ctx, argv[1], fmt, nullptr) != 0) {
        std::printf("打不开 %s\n", argv[1]);
        return 1;
    }
    for (unsigned i = 0; i < ctx->nb_streams; ++i) {
        const auto* st = ctx->streams[i];
        if (st->codecpar->codec_type != AVMEDIA_TYPE_AUDIO) continue;
        char desc[256]{};
        av_channel_layout_describe(&st->codecpar->ch_layout, desc, sizeof(desc));
        std::printf("stream %u: channels=%d order=%d mask=0x%llx desc=%s\n",
            i, st->codecpar->ch_layout.nb_channels,
            static_cast<int>(st->codecpar->ch_layout.order),
            static_cast<unsigned long long>(
                st->codecpar->ch_layout.order == AV_CHANNEL_ORDER_NATIVE
                    ? st->codecpar->ch_layout.u.mask : 0),
            desc);
    }
    avformat_close_input(&ctx);
    return 0;
}
