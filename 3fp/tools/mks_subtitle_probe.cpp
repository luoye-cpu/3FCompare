// 3FP MKS 外挂字幕 —— 可行性探针（2026-09-26，上游 issue #2）
//
// 要回答的问题：上游作者说「mka 做完了，帮我找一个 mks 字幕和对应的视频来，不然做不了」。
// 本探针不解任何产品代码，只回答一件事：
//   **内核 FFF.Native 现在（零改动）能不能直接吃 .mks？如果能，条件是什么？**
//
// 判据（必须具特异性，不只是「返回 Success」）：
//   · Open 成功 且 渲染出的位图里真的有非透明像素 ⇒ 真的解出并画出了字幕。
//   · 时间戳起止落在预期区间 ⇒ 时间轴没被容器偏移带歪。
//   · 反向对照：流索引 -1（宿主今天对外部字幕的用法）必须失败 ⇒ 断言有牙齿。
//
// 用法：
//   mks_subtitle_probe <文件> [更多文件…]       对每个文件遍历流索引 -1..2
//   （渲染画布固定 640x360；采样时刻 1s / 4s / 7s / 11s）
//   mks_subtitle_probe --probe-streams <文件>… 只调 FFF3FP_ProbeSubtitleStreams
//   并打印 JSON（MKS PR-1 的探测 API 验证；缓冲不够时按 required 重量）。
//
// 构建：python tools/build_probe.py mks_subtitle_probe
// 运行：把 FFF.Native.dll + FFmpeg DLL 放在 exe 同目录，用 Bash 跑（真实终端）。

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {

constexpr std::int64_t kSecond100ns = 10'000'000LL;
constexpr int kCanvasWidth = 640;
constexpr int kCanvasHeight = 360;

// 采样时刻（秒）。默认兼顾自制样本（0~10s 有字）与真实影视样本（开头大片空档）；
// --times 可覆盖。真实 .mks 常有容器时间轴偏移，必须用 --times 对齐到同一条事件。
std::vector<std::int64_t> gMoments = {0LL, 1LL, 4LL, 7LL, 11LL, 12LL, 40LL, 120LL, 600LL};

const char* ResultName(int r) {
    switch (r) {
    case 0: return "Success";
    case -1: return "InvalidArgument";
    case -2: return "InvalidState";
    case -3: return "BufferTooSmall";
    case -4: return "NativeFailure";
    case -5: return "FfmpegFailure";
    case -6: return "DeviceFailure";
    case -7: return "NotSupported";
    default: return "?";
    }
}

void PrintLastError(FFF3FPAssSubtitleHandle handle) {
    char buffer[1024]{};
    std::uint32_t required = 0;
    if (FFF3FP_GetAssSubtitleLastError(handle, buffer, sizeof(buffer), &required) == FFFResult::Success)
        printf("        lastError: %s\n", buffer);
}

int CountOpaquePixels(FFF3FPAssSubtitleHandle handle, const FFF3FPBitmapSubtitleFrame& frame) {
    if (frame.width <= 0 || frame.height <= 0 || frame.stride <= 0) return -1;
    const auto bytes = static_cast<std::size_t>(frame.stride) * static_cast<std::size_t>(frame.height);
    std::vector<std::uint8_t> pixels(bytes, 0);
    const std::uint32_t capacity = static_cast<std::uint32_t>(bytes);
    if (FFF3FP_CopyAssSubtitlePixels(handle, pixels.data(), capacity) != FFFResult::Success)
        return -1;
    int opaque = 0;
    for (std::int32_t y = 0; y < frame.height; ++y) {
        const auto* row = pixels.data() + static_cast<std::size_t>(y) * frame.stride;
        for (std::int32_t x = 0; x < frame.width; ++x) {
            if (row[static_cast<std::size_t>(x) * 4 + 3] > 8) ++opaque;
        }
    }
    return opaque;
}

// 返回 true = 该流索引可用（且至少在某一时刻画出了字）
bool TryAss(const char* path, std::int32_t streamIndex, const char* fontDirectory) {
    FFF3FPAssSubtitleHandle handle = nullptr;
    const auto open = FFF3FP_OpenAssSubtitle(path, fontDirectory, streamIndex, &handle);
    const int openResult = static_cast<int>(open);
    printf("    [ASS] streamIndex=%d -> %s (%d)\n", streamIndex, ResultName(openResult), openResult);
    if (openResult != 0) {
        if (handle != nullptr) {
            PrintLastError(handle);
            FFF3FP_DestroyAssSubtitle(handle);
        }
        return false;
    }

    bool drewAnything = false;
    for (const std::int64_t moment : gMoments) {
        FFF3FPBitmapSubtitleFrame frame{};
        frame.size = sizeof(frame);
        frame.version = 1;
        const auto render = FFF3FP_RenderAssSubtitle(handle, moment * kSecond100ns,
            kCanvasWidth, kCanvasHeight, &frame);
        const int renderResult = static_cast<int>(render);
        if (renderResult != 0) {
            printf("      t=%llds render -> %s (%d)\n", moment, ResultName(renderResult), renderResult);
            continue;
        }
        const auto flags = static_cast<std::uint32_t>(frame.flags);
        const int opaque = (flags & 1u) ? 0 : CountOpaquePixels(handle, frame); // flags&1 == Clear
        const double start = frame.start100ns / 1e7;
        const double end = frame.end100ns / 1e7;
        printf("      t=%llds  flags=0x%x  box=%dx%d @(%d,%d)  start=%.3fs end=%.3fs  opaque=%d\n",
            moment, flags, frame.width, frame.height, frame.x, frame.y, start, end, opaque);
        if (opaque > 0) drewAnything = true;
    }
    FFF3FP_DestroyAssSubtitle(handle);
    return drewAnything;
}

int CountOpaqueBitmapPixels(FFF3FPBitmapSubtitleHandle handle, const FFF3FPBitmapSubtitleFrame& frame) {
    if (frame.width <= 0 || frame.height <= 0 || frame.stride <= 0) return -1;
    const auto bytes = static_cast<std::size_t>(frame.stride) * static_cast<std::size_t>(frame.height);
    std::vector<std::uint8_t> pixels(bytes, 0);
    if (FFF3FP_CopyBitmapSubtitlePixels(handle, pixels.data(),
            static_cast<std::uint32_t>(bytes)) != FFFResult::Success)
        return -1;
    int opaque = 0;
    for (std::int32_t y = 0; y < frame.height; ++y) {
        const auto* row = pixels.data() + static_cast<std::size_t>(y) * frame.stride;
        for (std::int32_t x = 0; x < frame.width; ++x) {
            if (row[static_cast<std::size_t>(x) * 4 + 3] > 8) ++opaque;
        }
    }
    return opaque;
}

// 位图字幕（PGS/DVB/XSUB）路径：Seek 到采样时刻 -> 顺序 Read 一帧 -> 拷像素计数。
// 返回 true = 该流是位图字幕且至少在某一时刻真的画出了图形。
bool TryBitmap(const char* path, std::int32_t streamIndex) {
    FFF3FPBitmapSubtitleHandle handle = nullptr;
    const int openResult = static_cast<int>(
        FFF3FP_OpenBitmapSubtitle(path, streamIndex, &handle));
    if (openResult != 0 || handle == nullptr) {
        printf("    [BITMAP] streamIndex=%d -> %s (%d)\n", streamIndex, ResultName(openResult), openResult);
        return false;
    }
    printf("    [BITMAP] streamIndex=%d -> Success (0)\n", streamIndex);

    bool drewAnything = false;
    for (const std::int64_t moment : gMoments) {
        const int seekResult = static_cast<int>(
            FFF3FP_SeekBitmapSubtitle(handle, moment * kSecond100ns));
        if (seekResult != 0) {
            printf("      t=%llds seek -> %s (%d)\n", moment, ResultName(seekResult), seekResult);
            continue;
        }
        FFF3FPBitmapSubtitleFrame frame{};
        frame.size = sizeof(frame);
        frame.version = 1;
        const int readResult = static_cast<int>(FFF3FP_ReadBitmapSubtitle(handle, &frame));
        if (readResult != 0) {
            printf("      t=%llds read -> %s (%d)\n", moment, ResultName(readResult), readResult);
            continue;
        }
        const auto flags = static_cast<std::uint32_t>(frame.flags);
        if (flags & 2u) {  // EndOfStream
            printf("      t=%llds  flags=0x%x (EndOfStream)\n", moment, flags);
            continue;
        }
        const int opaque = CountOpaqueBitmapPixels(handle, frame);
        printf("      t=%llds  flags=0x%x  box=%dx%d @(%d,%d) stride=%d  start=%.3fs end=%.3fs  opaque=%d\n",
            moment, flags, frame.width, frame.height, frame.x, frame.y, frame.stride,
            frame.start100ns / 1e7, frame.end100ns / 1e7, opaque);
        if (opaque > 0) drewAnything = true;
    }
    FFF3FP_DestroyBitmapSubtitle(handle);
    return drewAnything;
}

// --probe-streams：只调 FFF3FP_ProbeSubtitleStreams（MKS PR-1 探测 API）。
void ProbeStreams(const char* path) {
    std::uint32_t required = 0;
    auto result = static_cast<int>(FFF3FP_ProbeSubtitleStreams(path, nullptr, 0, &required));
    printf("  probe -> %s (%d) required=%u\n", ResultName(result), result, required);
    if (result != 0 && result != static_cast<int>(FFFResult::BufferTooSmall)) return;
    std::vector<char> buffer(required > 0 ? required : 1, '\0');
    result = static_cast<int>(FFF3FP_ProbeSubtitleStreams(path, buffer.data(),
        static_cast<std::uint32_t>(buffer.size()), &required));
    printf("  probe(retry) -> %s (%d)\n", ResultName(result), result);
    if (result == 0) printf("  %s\n", buffer.data());
}

}  // namespace

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    const char* fontDirectory = nullptr;
    std::int32_t only = -100;
    bool probeOnly = false;
    std::vector<const char*> paths;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--fontdir") == 0 && i + 1 < argc) { fontDirectory = argv[++i]; continue; }
        if (std::strcmp(argv[i], "--stream") == 0 && i + 1 < argc) { only = std::atoi(argv[++i]); continue; }
        if (std::strcmp(argv[i], "--probe-streams") == 0) { probeOnly = true; continue; }
        // --times 142,153,181 ：自定义采样秒数（真实素材常有容器时间轴偏移，需对齐同一事件）
        if (std::strcmp(argv[i], "--times") == 0 && i + 1 < argc) {
            gMoments.clear();
            const char* p = argv[++i];
            while (*p != '\0') {
                if (*p >= '0' && *p <= '9') {
                    gMoments.push_back(static_cast<std::int64_t>(std::strtoll(p, const_cast<char**>(&p), 10)));
                } else {
                    ++p;
                }
            }
            if (gMoments.empty()) { printf("--times 解析失败\n"); return 2; }
            continue;
        }
        paths.push_back(argv[i]);
    }
    if (paths.empty()) {
        printf("usage: mks_subtitle_probe [--probe-streams] [--fontdir <dir>] [--stream N] [--times a,b,c] <file> [more...]\n");
        return 2;
    }
    if (fontDirectory != nullptr)
        printf("(fontDirectory = %s%s)\n", fontDirectory,
            probeOnly ? ", ignored in --probe-streams" : "");
    for (const char* path : paths) {
        printf("=== %s ===\n", path);
        if (probeOnly) { ProbeStreams(path); continue; }
        // 流索引上界取 9：官方多字幕样本（multiple_sub_sample）有 7 条字幕轨。
        const std::int32_t first = (only == -100) ? -1 : only;
        const std::int32_t last = (only == -100) ? 9 : only;
        for (std::int32_t streamIndex = first; streamIndex <= last; ++streamIndex) {
            TryAss(path, streamIndex, fontDirectory);
            TryBitmap(path, streamIndex);
        }
        printf("\n");
    }
    return 0;
}
