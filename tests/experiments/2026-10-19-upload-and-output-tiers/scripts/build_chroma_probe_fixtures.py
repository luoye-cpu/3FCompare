# -*- coding: utf-8 -*-
"""B1 色度定点判据的夹具：**静止**的色度棋盘，按"色度样本"交替，不是按亮度像素交替。

为什么静止：上一轮（par1/par2）栽在"两臂不是同一张画"上 —— 移动素材 + 只靠 wall-clock
位置当见证，配对差全部不可解释。静止图案让"同一张画"由构造保证，帧号漂移不影响像素。

为什么"按色度样本交替"：这样图案在亮度域里的周期是 2^(shift+1) 个亮度像素，
**位移读错一档，周期就差一倍**，而格均值那种量法对周期不敏感（par2 的分叉对照
a410↔a440 只有 0.0074，比噪声底还小，已经证明它没牙）。

两族图案：col = 沿色度列交替（测水平位移），row = 沿色度行交替（测垂直位移）。
每族再配一个 FFmpeg 自己按描述符 neighbor 扩成 4:4:4 的同画面兄弟 —— 那是第二个实现
画的格子，不是我这张表画的格子。
"""
import os
import subprocess
import sys

FFMPEG = r"C:\PLAN\FFmpeg\ffmpeg.exe"
FFPROBE = r"C:\PLAN\FFmpeg\ffprobe.exe"
OUT = r"C:\PLAN\3FCompare\_3fc_tmp\fixtures"
W, H, FPS, SECONDS = 640, 360, 60, 1
Y_VALUE = 128
NEAR, FAR = 64, 192          # 交替的两个色度值
FLAT = 128

# 每枚格式的色度位移 (w,h) —— 只用于**造格子**；判据本身拿两臂的过零次数对比，
# 不拿这张表当尺子（否则又是自证）。真值另由 avutil 描述符探针核过（14/14）。
FORMATS = {"yuv410p": (2, 2), "yuv440p": (0, 1), "yuv420p": (1, 1), "yuv444p": (0, 0)}


def planes(fmt, pattern):
    """返回 (Y, U, V) 三个平面字节串（stride 等于该平面的宽度，无填充）。"""
    wshift, hshift = FORMATS[fmt]
    cw, ch = (W + (1 << wshift) - 1) >> wshift, (H + (1 << hshift) - 1) >> hshift
    y = bytes([Y_VALUE]) * (W * H)
    c = bytearray(cw * ch)
    for cy in range(ch):
        for cx in range(cw):
            if pattern == "col":
                c[cy * cw + cx] = NEAR if (cx % 2 == 0) else FAR
            else:
                c[cy * cw + cx] = NEAR if (cy % 2 == 0) else FAR
    flat = bytes([FLAT]) * (cw * ch)
    return y, bytes(c), flat


def raw_path(fmt, pattern):
    return os.path.join(OUT, "pat_%s_%s.yuv" % (fmt, pattern))


def encode(fmt, pattern):
    dst = os.path.join(OUT, "pat_%s_%s_%dx%d.mkv" % (fmt, pattern, W, H))
    y, u, v = planes(fmt, pattern)
    # 把同一帧复制满整段，而不是靠 -stream_loop：静止素材由**字节本身**保证每帧相同，
    # 上一轮就是栽在"两臂读到的不是同一张画"，这里把这个变量从源头消掉。
    frames = FPS * SECONDS
    with open(raw_path(fmt, pattern), "wb") as f:
        f.write((y + u + v) * frames)
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
           "-f", "rawvideo", "-pix_fmt", fmt, "-s", "%dx%d" % (W, H), "-r", str(FPS),
           "-i", raw_path(fmt, pattern),
           "-frames:v", str(frames), "-c:v", "ffv1", "-level", "3", "-an", dst]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    if r.returncode != 0:
        print("FAILED", dst, (r.stderr or "").strip()[:220])
        return None
    return dst


def up444(src, dst):
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-i", src,
           "-vf", "scale=flags=neighbor,format=yuv444p", "-c:v", "ffv1", "-level", "3", "-an", dst]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    if r.returncode != 0:
        print("FAILED up444", dst, (r.stderr or "").strip()[:220])
        return False
    return True


def probe_fmt(path):
    r = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0",
                        "-show_entries", "stream=pix_fmt,width,height", "-of", "default=nw=1:nk=1", path],
                       capture_output=True, text=True, timeout=120)
    return "|".join((r.stdout or "").split())


if __name__ == "__main__":
    want = sys.argv[1:] or ["yuv410p", "yuv440p", "yuv420p"]
    made = []
    for fmt in want:
        for pattern in ("col", "row"):
            src = encode(fmt, pattern)
            if not src:
                continue
            got = probe_fmt(src)
            ok = fmt in got.split("|")   # ffprobe 的字段顺序是 width|height|pix_fmt
            print("%-34s 回读=%-28s %s" % (os.path.basename(src), got, "OK" if ok else "*** 不是我要的格式 ***"))
            if not ok:
                continue
            made.append(src)
            sib = src.replace(".mkv", "_up444.mkv")
            if up444(src, sib) and "yuv444p" in probe_fmt(sib).split("|"):
                made.append(sib)
                print("%-34s 回读=%-28s OK" % (os.path.basename(sib), probe_fmt(sib)))
            else:
                print("*** 兄弟件失败或格式不对:", sib)
    print("\n可用夹具 %d 个" % len(made))
