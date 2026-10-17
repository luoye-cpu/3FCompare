#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
gen_dirty_rect_anim.py —— 脏矩形动图 + 位深对照动图 夹具生成器（W1d）

为什么不用 lavfi：本机 ffmpeg 的 lavfi 源（testsrc2/gradients）默认几何不可复现
（seed 只钉颜色），而这两组夹具的判据本身就是"逐像素改了哪些"。⇒ 帧内容全部由本脚本
用纯 stdlib 整数运算合成，直接以 rawvideo 管道喂给 ffmpeg；一帧也不经过 lavfi 源。

产出两组：

  组 1  脏矩形动画（问题："局部上传路径值不值？"）
        APNG + GIF，640x480 / 1920x1080 / 3840x2160，10 帧 @10fps，
        大面积静态背景 + 一个小移动方块 ⇒ 相邻帧变化像素占比必须 <= 2%。
        脚本会**回读编码后的产物**（ffmpeg 解码成 raw）实测变化占比与变化并集包围盒，
        超过 2% 就自动缩小方块重生成（最多 4 轮），并把实测值打进 --report 文件。

  组 2  8bit / 10bit 同内容对照（问题："动图 HEIC/AVIF 20.6ms/帧 是不是 10bit 路径的锅？"）
        同一批 16bit 源帧（含平滑渐变，真正吃到位深），只改 -pix_fmt：
        yuv420p（8bit）vs yuv420p10le（10bit），其余分辨率/帧数/帧时序/内容全同。

用法：
    py samples/tools/gen_dirty_rect_anim.py --anim-dir samples/media/anim \
        --scratch build/anim-perf/lane-W1d --ffmpeg C:/PLAN/FFmpeg/ffmpeg.exe \
        --ffprobe C:/PLAN/FFmpeg/ffprobe.exe

依赖：仅 stdlib + 同目录的 ffmpeg/ffprobe。所有数值判据由本脚本实测，不靠"应该"。
"""

import argparse
import hashlib
import json
import os
import struct
import subprocess
import sys

N_FRAMES = 10
FPS = 10

# ── 组 1：脏矩形规格 ──────────────────────────────────────────────────────────
# s=方块边长 dx/dy=每帧位移（dx==s ⇒ 相邻帧方块不相交 ⇒ 变化像素 == 2*s*s）
DIRTY_SPECS = [
    {"tag": "10a", "ext": "png", "w": 640,  "h": 480,  "s": 40,  "dx": 40,  "dy": 20},
    {"tag": "10b", "ext": "png", "w": 1920, "h": 1080, "s": 110, "dx": 110, "dy": 55},
    {"tag": "10c", "ext": "png", "w": 3840, "h": 2160, "s": 220, "dx": 220, "dy": 110},
    {"tag": "10d", "ext": "gif", "w": 640,  "h": 480,  "s": 40,  "dx": 40,  "dy": 20},
    {"tag": "10e", "ext": "gif", "w": 1920, "h": 1080, "s": 110, "dx": 110, "dy": 55},
    {"tag": "10f", "ext": "gif", "w": 3840, "h": 2160, "s": 220, "dx": 220, "dy": 110},
]
DIRTY_TARGET_FRAC = 0.02      # 判据：<= 2%
DIRTY_FAIL_FRAC = 0.05        # > 5% ⇒ 夹具失去意义（必须重做）

# 调色板：总不同颜色数远小于 256 ⇒ GIF 量化按色 1:1 映射，背景不会逐帧漂移
C_A = (36, 40, 48)
C_B = (46, 52, 62)
C_GRID = (74, 84, 100)
C_SWATCH = [
    (120, 60, 60), (60, 120, 90), (90, 90, 140), (150, 130, 70),
    (70, 140, 150), (140, 100, 150), (100, 150, 110), (150, 110, 90),
    (80, 100, 160), (160, 90, 120), (110, 150, 80), (95, 95, 95),
]
C_BLK = [(232, 236, 244), (180, 190, 208), (120, 170, 232), (232, 160, 96)]

CHECKER = 16      # 棋盘格边长
GRID = 64         # 静态网格线间距
SWATCH_FRAC = [   # 静态色块（按分辨率比例放，保证三种分辨率内容统计一致）
    (0.04, 0.05, 0.10, 0.10), (0.20, 0.05, 0.10, 0.10), (0.36, 0.05, 0.10, 0.10),
    (0.52, 0.05, 0.10, 0.10), (0.68, 0.05, 0.10, 0.10), (0.04, 0.80, 0.10, 0.10),
    (0.20, 0.80, 0.10, 0.10), (0.36, 0.80, 0.10, 0.10), (0.52, 0.80, 0.10, 0.10),
    (0.68, 0.80, 0.10, 0.10), (0.84, 0.05, 0.10, 0.30), (0.84, 0.55, 0.10, 0.30),
]


def _px(color, n):
    b = bytes(color)
    return b * n


def build_static_rows(w, h):
    """静态背景：每行一个 bytearray（行内用切片填充 ⇒ 4K 也只花几十毫秒）。"""
    half = CHECKER // 2
    row_tpl = bytearray()
    for x in range(GRID):
        band = (x // half) % 2
        c = C_GRID if x % GRID == 0 else (C_A if band == 0 else C_B)
        row_tpl += bytes(c)
    rows = []
    for y in range(h):
        if y % GRID == 0:
            row = bytearray(_px(C_GRID, GRID)) * (w // GRID)
        else:
            band = (y // half) % 2
            tpl = bytearray()
            for x in range(GRID):
                c = C_GRID if x % GRID == 0 else (C_A if band == 0 else C_B)
                tpl += bytes(c)
            row = bytearray(tpl * (w // GRID))
        rows.append(row[: w * 3] if len(row) > w * 3 else row)
    # 静态色块
    for i, (fx, fy, fw, fh) in enumerate(SWATCH_FRAC):
        x0, y0 = int(round(fx * w)), int(round(fy * h))
        x1, y1 = min(w, int(round((fx + fw) * w))), min(h, int(round((fy + fh) * h)))
        fill = _px(C_SWATCH[i % len(C_SWATCH)], x1 - x0)
        for y in range(y0, y1):
            rows[y][x0 * 3 : x1 * 3] = fill
    return rows


def draw_block(rows, x0, y0, s):
    """在方块区域内画 2x2 子棋盘（图案锚定方块原点 ⇒ 平移时方块内部像素不变，
    只有新旧位置的对称差发生变化）。"""
    half = max(1, s // 2)
    for dy in range(s):
        y = y0 + dy
        if y >= len(rows):
            break
        band = (dy // half) % 2
        seg = bytearray()
        for k in range(s):
            c = C_BLK[0 if (k // half) % 2 == band else 1]
            seg += bytes(c)
        x1 = min(len(rows[y]) // 3, x0 + s)
        if x1 > x0:
            rows[y][x0 * 3 : x1 * 3] = seg[: (x1 - x0) * 3]


def dirty_frame(w, h, s, dx, dy, idx, static_rows):
    rows = [bytearray(r) for r in static_rows]          # 每帧从同一静态底版复制
    bx = 20 + idx * dx
    by = 20 + idx * dy
    draw_block(rows, bx, by, s)
    return b"".join(bytes(r) for r in rows)


# ── 组 2：同内容 8/10bit 源帧（16bit 平滑渐变 + 同一条方块轨迹）──────────────────
def gradient_frame_16(w, h, s, dx, dy, idx):
    """rgb48be：渐变在 10bit 精度上定义；8bit 臂由转换量化得到，两臂源逐字节相同。"""
    out = bytearray(w * h * 6)
    for y in range(h):
        gy = y * 700 // max(1, h - 1)
        base = y * 3 * w
        p = base
        for x in range(w):
            gx = x * 700 // max(1, w - 1)
            r = 96 + gx + (gy // 4)
            g = 96 + gy + (gx // 4)
            b = 96 + ((gx + gy) // 2)
            r = min(1023, r) << 4
            g = min(1023, g) << 4
            b = min(1023, b) << 4
            out[p] = r >> 8; out[p + 1] = r & 255
            out[p + 2] = g >> 8; out[p + 3] = g & 255
            out[p + 4] = b >> 8; out[p + 5] = b & 255
            p += 6
    # 方块也画在 16bit 上（与组 1 同一轨迹、同一尺寸）
    bx, by = 20 + idx * dx, 20 + idx * dy
    for j in range(s):
        yy = by + j
        if yy >= h:
            break
        for i in range(s):
            xx = bx + i
            if xx >= w:
                break
            v = (512 + ((i // max(1, s // 2)) * 256 + (j // max(1, s // 2)) * 128)) << 4
            p = (yy * w + xx) * 6
            out[p] = v >> 8; out[p + 1] = v & 255
            out[p + 2] = 0; out[p + 3] = 0
            out[p + 4] = v >> 8; out[p + 5] = v & 255
    return bytes(out)


# ── ffmpeg 封装 ───────────────────────────────────────────────────────────────
def run(cmd, inp=None):
    proc = subprocess.run(cmd, input=inp, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return proc.returncode, proc.stdout, proc.stderr


def encode_ffmpeg(src, w, h, in_pix, out_path, ffmpeg, mux, extra_before_out):
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
           "-f", "rawvideo", "-pix_fmt", in_pix, "-video_size", "%dx%d" % (w, h),
           "-framerate", str(FPS), "-i", "pipe:0"]
    cmd += extra_before_out
    cmd += [out_path]
    rc, so, se = run(cmd, src)
    if rc != 0:
        raise RuntimeError("encode failed rc=%d :: %s :: %s" % (
            rc, os.path.basename(out_path), se.decode("utf-8", "replace").strip()))
    return so.decode("utf-8", "replace")


def apng_cmd(w, h, in_pix="rgb24"):
    return ["-frames:v", str(N_FRAMES), "-f", "apng", "-plays", "0"]


# ffmpeg 会把 rgb24 自动降到 gif 的第一个受支持格式（rgb8/pal8）并做误差扩散抖色
# ⇒ 背景每帧都在"轻微变"，脏矩形夹具直接废掉。显式钉一个 <=32 色的全局调色板 +
#   dither=none 后，源色 1:1 映射到调色板项，背景逐帧逐字节相等。
GIF_PALETTE_CHAIN = (
    "[0:v]palettegen=max_colors=32:stats_mode=full[p];"
    "[0:v][p]paletteuse=dither=none:alpha_threshold=128"
)


def gif_cmd_palette(w, h):
    return ["-frames:v", str(N_FRAMES), "-filter_complex", GIF_PALETTE_CHAIN, "-loop", "0"]


def avif_cmd(pix):
    return ["-frames:v", str(N_FRAMES), "-loop", "0", "-c:v", "libaom-av1",
            "-crf", "30", "-cpu-used", "8", "-pix_fmt", pix, "-f", "avif"]


PIX_BYTES_PER_PIXEL = {"rgb24": 3, "bgr24": 3, "rgb8": 3, "rgb48be": 6, "rgb48le": 6,
                       "yuv420p": 1.5, "yuv420p10le": 3.0, "yuv420p12le": 3.0,
                       "gray": 1, "gray16le": 2}


def frame_stride(pix, w, h):
    bpp = PIX_BYTES_PER_PIXEL.get(pix)
    if bpp is None:
        raise ValueError("unknown bytes/pixel for %s" % pix)
    return int(w * h * bpp)


def decode_raw(path, ffmpeg, pix, w, h, vmap=None):
    """vmap 是**输出侧**选项（-map 在 -i 之后），用于挑 AVIF 的动画轨。"""
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-i", path]
    if vmap:
        cmd += ["-map", vmap]
    cmd += ["-f", "rawvideo", "-pix_fmt", pix, "pipe:1"]
    rc, so, se = run(cmd)
    if rc != 0:
        raise RuntimeError("decode failed %s :: %s" % (path, se.decode("utf-8", "replace")[-400:]))
    if not (w and h):
        raise ValueError("w/h required")
    stride = frame_stride(pix, w, h)
    frames = [so[i : i + stride] for i in range(0, len(so) - stride + 1, stride)]
    return frames, se.decode("utf-8", "replace")


def decode_best(path, ffmpeg, pix, w, h, want=N_FRAMES):
    """AVIF avis 容器有两条流（流0=primary 1 帧，流1=动画轨 N 帧）；依次试
    默认 / 0:v:1 / 0:v:0，取解出帧数最多的一路，并记下用的是哪一路。"""
    best = None
    for vmap in (None, "0:v:1", "0:v:0"):
        try:
            frames, err = decode_raw(path, ffmpeg, pix, w, h, vmap=vmap)
        except RuntimeError as exc:
            if best is None:
                best = ([], vmap, str(exc))
            continue
        if best is None or len(frames) > len(best[0]):
            best = (frames, vmap, err)
        if len(frames) >= want:
            break
    return best[0], best[1]


def ffprobe_streams(path, ffprobe):
    cmd = [ffprobe, "-hide_banner", "-loglevel", "error", "-print_format", "json",
           "-show_streams", "-show_format", path]
    rc, so, se = run(cmd)
    if rc != 0:
        raise RuntimeError("ffprobe failed %s :: %s" % (path, se.decode("utf-8", "replace")[-300:]))
    return json.loads(so.decode("utf-8", "replace"))


# ── 容器级帧数/时序解析（不依赖 ffprobe 对图像流的 fps 报告）────────────────────
def parse_apng(path):
    with open(path, "rb") as fh:
        data = fh.read()
    out = {"frames": 0, "delays_ms": [], "num_plays": None, "has_acTL": False}
    i = 8  # PNG 签名占 8 字节，不能从 0 开始走 chunk
    while i + 8 <= len(data):
        ln = struct.unpack(">I", data[i : i + 4])[0]
        typ = data[i + 4 : i + 8]
        body = data[i + 8 : i + 8 + ln]
        if typ == b"acTL":
            out["has_acTL"] = True
            out["frames"], out["num_plays"] = struct.unpack(">II", body[:8])
        elif typ == b"fcTL":
            # 延时读数与 ffprobe 包时长、解码帧数三个口径一致 ⇒ 采信；
            # 偏移/尺寸元组这里不解析（避免给出解码未对齐的读数）
            num, den = struct.unpack(">HH", body[20:24])
            out["delays_ms"].append(round(num * 1000.0 / den, 3) if den else None)
        elif typ == b"IEND":
            break
        i += 12 + ln
    return out


def parse_gif(path):
    """手写 GIF 走查器：图像描述符 0x2C(10B)+局部调色板+LZW 子块，GCE 0x21/F9 取延迟
    （单位 1/100 秒），NETSCAPE2.0 取循环次数。ffmpeg 用 transdiff 时可能少写 GCE，
    因此 frames 与 delays 条数可以不等 —— 两个数都记。"""
    with open(path, "rb") as fh:
        data = fh.read()
    out = {"frames": 0, "delays_ms": [], "loop": None, "gce_count": 0}
    if data[:3] != b"GIF":
        return out
    flags = data[10]
    i = 13
    if flags & 0x80:
        i += 3 * (1 << ((flags & 0x07) + 1))
    while i < len(data):
        b = data[i]
        if b == 0x21:
            label = data[i + 1]
            j = i + 2
            payload = bytearray()
            while j < len(data) and data[j] != 0:
                sz = data[j]
                payload += data[j + 1 : j + 1 + sz]
                j += sz + 1
            j += 1
            if label == 0xF9:
                # payload = [flags, delay_lo, delay_hi, transparent_index]
                delay = payload[1] | (payload[2] << 8)
                out["delays_ms"].append(delay * 10)
                out["gce_count"] += 1
            elif label == 0xFF and payload[:11] == b"NETSCAPE2.0":
                loop = int.from_bytes(payload[12:14], "little")
                out["loop"] = "infinite(0)" if loop == 0 else loop
            i = j
        elif b == 0x2C:
            out["frames"] += 1
            packed = data[i + 9]
            i += 10
            if packed & 0x80:
                i += 3 * (1 << ((packed & 0x07) + 1))
            i += 1  # LZW min code size
            while i < len(data) and data[i] != 0:
                i += data[i] + 1
            i += 1
        elif b == 0x3B:
            break
        else:
            break
    return out


def ffprobe_packets(path, ffprobe):
    """帧时序的第二读数：容器级 packet pts_time/duration_time。"""
    cmd = [ffprobe, "-hide_banner", "-loglevel", "error", "-select_streams", "v",
           "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "csv=p=0", path]
    rc, so, se = run(cmd)
    txt = so.decode("utf-8", "replace").strip()
    rows = [ln.split(",") for ln in txt.splitlines() if ln.strip()]
    durs = []
    for r in rows:
        try:
            durs.append(float(r[2]))
        except (ValueError, IndexError):
            pass
    return {"packets": len(rows), "duration_sum_s": round(sum(durs), 4) if durs else None,
            "raw": rows[:12]}



# ── 逐像素实测 ────────────────────────────────────────────────────────────────
try:                      # 有 numpy 就走向量路径（4K 对照也能整帧逐像素精确量）
    import numpy as _np
except Exception:         # noqa: BLE001 - 没有就退回纯 stdlib 扫描
    _np = None


def measure_pairs(frames, w, h, channels=3):
    """相邻帧比较：变化像素占比（任一分量不等）+ 变化并集包围盒 + 每帧对包围盒。
    背景相同的行用整行相等快速跳过，只有脏行做逐像素扫描。"""
    total = w * h
    n = len(frames)
    if _np is not None:
        arrs = [_np.frombuffer(f, dtype=_np.uint8).reshape(h, w, channels) for f in frames]
        union = _np.zeros((h, w), dtype=bool)
        per_pair = []
        for k in range(n - 1):
            d = (arrs[k] != arrs[k + 1]).any(axis=2)
            big = (_np.abs(arrs[k].astype(_np.int16) - arrs[k + 1].astype(_np.int16))
                   .max(axis=2) >= 8)
            changed = int(d.sum())
            ys, xs = _np.nonzero(d)
            bbox = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())] if changed else None
            per_pair.append({
                "pair": "%02d->%02d" % (k, k + 1), "changed_px": changed,
                "changed_frac": changed / total,
                "changed_frac_ge8": int(big.sum()) / total,
                "bbox": bbox,
                "bbox_w_h": None if bbox is None else [bbox[2] - bbox[0] + 1, bbox[3] - bbox[1] + 1],
            })
            union |= d
        uarea = int(union.sum())
        uys, uxs = _np.nonzero(union)
        ub = [int(uxs.min()), int(uys.min()), int(uxs.max()), int(uys.max())] if uarea else None
        return _finish_measure(per_pair, union_count=uarea, union_bbox=ub, total=total, n=n)
    per_pair = []
    changed_total = 0
    gx0, gy0, gx1, gy1 = w, h, -1, -1
    union = bytearray(total)
    for k in range(n - 1):
        a, b = frames[k], frames[k + 1]
        changed = 0
        changed_ge8 = 0
        x0, y0, x1, y1 = w, h, -1, -1
        for y in range(h):
            s = y * w * channels
            e = s + w * channels
            if a[s:e] == b[s:e]:
                continue
            row_a = a[s:e]
            row_b = b[s:e]
            for x in range(w):
                p = x * channels
                pa = row_a[p : p + channels]
                pb = row_b[p : p + channels]
                if pa != pb:
                    changed += 1
                    union[y * w + x] = 1
                    if x < x0: x0 = x
                    if x > x1: x1 = x
                    if y < y0: y0 = y
                    if y > y1: y1 = y
                    if any(abs(pa[c] - pb[c]) >= 8 for c in range(channels)):
                        changed_ge8 += 1
        per_pair.append({
            "pair": "%02d->%02d" % (k, k + 1),
            "changed_px": changed,
            "changed_frac": changed / total,
            "changed_frac_ge8": changed_ge8 / total,
            "bbox": None if changed == 0 else [x0, y0, x1, y1],
            "bbox_w_h": None if changed == 0 else [x1 - x0 + 1, y1 - y0 + 1],
        })
        changed_total += changed
        if changed:
            gx0, gy0, gx1, gy1 = min(gx0, x0), min(gy0, y0), max(gx1, x1), max(gy1, y1)
    return _finish_measure(per_pair, union_count=sum(union),
                           union_bbox=[gx0, gy0, gx1, gy1] if gx1 >= 0 else None,
                           total=total, n=n)


def _finish_measure(per_pair, union_count, union_bbox, total, n):
    if n < 2:
        raise ValueError("need >=2 decoded frames to measure dirty rects, got %d" % n)
    fr = [p["changed_frac"] for p in per_pair]
    ge8 = [p["changed_frac_ge8"] for p in per_pair]
    return {
        "frame_pairs": n - 1,
        "method": "numpy-exact" if _np is not None else "stdlib-exact",
        "mean_changed_frac": sum(fr) / len(fr),
        "max_changed_frac": max(fr),
        "min_changed_frac": min(fr),
        "mean_changed_frac_ge8": sum(ge8) / len(ge8),
        "union_bbox": union_bbox,
        "union_bbox_w_h": None if union_bbox is None else
                          [union_bbox[2] - union_bbox[0] + 1, union_bbox[3] - union_bbox[1] + 1],
        "union_changed_px": union_count,
        "union_changed_frac": union_count / total,
        "per_pair": per_pair,
    }


def sha256(path):
    hh = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            hh.update(chunk)
    return hh.hexdigest()


# ── 主流程 ────────────────────────────────────────────────────────────────────
def gen_dirty(spec, anim_dir, ffmpeg, ffprobe, retries=4):
    w, h = spec["w"], spec["h"]
    s, dx, dy = spec["s"], spec["dx"], spec["dy"]
    name = "%s_dirtyrect_%dx%d.%s" % (spec["tag"], w, h, spec["ext"])
    path = os.path.join(anim_dir, name)
    static_rows = build_static_rows(w, h)
    result = {"name": name, "path": path, "size_bytes": None, "sha256": None,
              "w": w, "h": h, "block_s": s, "dx": dx, "dy": dy,
              "predicted_frac": 2 * s * s / (w * h), "attempts": [], "final": None,
              "container": None, "probe": None}
    for attempt in range(retries + 1):
        src = b"".join(dirty_frame(w, h, s, dx, dy, i, static_rows) for i in range(N_FRAMES))
        extra = apng_cmd(w, h) if spec["ext"] == "png" else gif_cmd_palette(w, h)
        encode_ffmpeg(src, w, h, "rgb24", path, ffmpeg, spec["ext"], extra)
        frames, _ = decode_raw(path, ffmpeg, "rgb24", w, h)
        meas = measure_pairs(frames, w, h)
        meas["decoded_frames"] = len(frames)
        result["attempts"].append({"s": s, "dx": dx, "dy": dy, "measure": meas})
        if meas["max_changed_frac"] <= DIRTY_TARGET_FRAC:
            result["final"] = meas
            break
        # 大面积但幅度极小的变化 = 调色板抖色，不是脏矩形太大 ⇒ 缩小方块救不回来
        if meas["max_changed_frac"] > DIRTY_FAIL_FRAC and meas["mean_changed_frac_ge8"] < 0.2 * meas["max_changed_frac"]:
            result["warning"] = ("small-amplitude change spread over the whole frame "
                                 "=> palette dithering, not block size; shrinking will not help")
            result["final"] = meas
            break
        s = max(8, int(s * 0.75)); dx = s; dy = max(1, s // 2)
        result["block_s"], result["dx"], result["dy"] = s, dx, dy
        result["predicted_frac"] = 2 * s * s / (w * h)
    if result["final"] is None:
        result["final"] = result["attempts"][-1]["measure"]
        result["warning"] = "still above target after retries"
    if result["final"]["max_changed_frac"] > DIRTY_FAIL_FRAC:
        result["verdict"] = "FAIL(>5%)"
    elif result["final"]["max_changed_frac"] > DIRTY_TARGET_FRAC:
        result["verdict"] = "WARN(>2%)"
    else:
        result["verdict"] = "OK(<=2%)"
    result["size_bytes"] = os.path.getsize(path)
    result["sha256"] = sha256(path)
    result["container"] = parse_apng(path) if spec["ext"] == "png" else parse_gif(path)
    result["packets"] = ffprobe_packets(path, ffprobe)
    result["probe"] = ffprobe_streams(path, ffprobe)
    result["encode_args"] = (apng_cmd(w, h) if spec["ext"] == "png" else gif_cmd_palette(w, h))
    return result


def bit_depth_fingerprint(path, ffmpeg, w, h):
    """位深实证——**不读 ffprobe 的 pix_fmt 旗标，只看像素**。
    把两臂都用同一条管道解到 yuv420p10le（8bit 臂 ⇒ swscale 左移 2 位补齐，
    所有 Y 值必为 4 的倍数；真 10bit 编码 ⇒ 低位有内容）：
      · distinct_y_first_frame : 首帧 Y 平面不同取值个数（8bit 上转换最多 256）
      · frac_y_not_multiple_of_4 : 首帧 Y 里不是 4 的倍数的占比 ← 判据
      · frac_rgb_not_multiple_of_4 : 同一判据走 rgb48be 路的读数（对照组：
        YUV→RGB 矩阵会把 8bit 也打成非 4 倍数，实测 0.36 ⇒ 该口径**不可用**，
        留在这里当反面记录，别拿它下结论）
    """
    frames, vmap = decode_best(path, ffmpeg, "yuv420p10le", w, h)
    if not frames:
        raise RuntimeError("no frame decoded from %s" % path)
    mv = memoryview(frames[0])
    vals = set()
    non4 = 0
    for i in range(0, w * h * 2, 2):
        v = mv[i] | (mv[i + 1] << 8)
        vals.add(v)
        if v & 3:
            non4 += 1
    res = {"decoded_frames": len(frames), "vmap": vmap,
           "decode_pix_fmt": "yuv420p10le",
           "distinct_y_first_frame": len(vals),
           "frac_y_not_multiple_of_4": non4 / (w * h),
           "y_min": min(vals), "y_max": max(vals)}
    try:
        rgb, _ = decode_raw(path, ffmpeg, "rgb48be", w, h)
        rmv = memoryview(rgb[0])
        res["frac_rgb_not_multiple_of_4"] = sum(1 for b in rmv[1::2] if b & 3) / len(rmv[1::2])
    except RuntimeError:
        pass
    return res


def gen_bitdepth_pair(anim_dir, ffmpeg, ffprobe, scratch):
    w, h, s, dx, dy = 640, 480, 40, 40, 20
    src = b"".join(gradient_frame_16(w, h, s, dx, dy, i) for i in range(N_FRAMES))
    out = {"source_frames": N_FRAMES, "source_w_h": [w, h],
           "source_sha256_rgb48be": hashlib.sha256(src).hexdigest(),
           "source_fingerprint": "16bit 平滑渐变(r/g/b 各含 x、y 两个方向的斜坡) + 与组1同轨迹的 40px 移动方块",
           "arms": [], "notes": []}
    for tag, label, pix in (("10g", "8bit", "yuv420p"), ("10h", "10bit", "yuv420p10le")):
        name = "%s_bitdepth_%s_640x480.avif" % (tag, label)
        path = os.path.join(anim_dir, name)
        arm = {"label": label, "name": name, "path": path, "pix_fmt_requested": pix,
               "encode_args": avif_cmd(pix)}
        try:
            encode_ffmpeg(src, w, h, "rgb48be", path, ffmpeg, "avif", avif_cmd(pix))
        except RuntimeError as exc:
            arm["error"] = str(exc)
            out["arms"].append(arm)
            continue
        try:
            frames, vmap = decode_best(path, ffmpeg, "rgb24", w, h)
            arm["rgb24_decode"] = {"frames": len(frames), "vmap": vmap}
            meas = measure_pairs(frames, w, h)
            arm["changed_measure_rgb24"] = {k: meas[k] for k in (
                "frame_pairs", "mean_changed_frac", "max_changed_frac",
                "mean_changed_frac_ge8", "union_changed_frac", "union_bbox")}
            arm["changed_measure_note"] = "rgb24 下测 ⇒ 8bit 臂与 10bit 臂都先被截到 8bit，只用于对照时序"
            arm["bit_depth_fingerprint"] = bit_depth_fingerprint(path, ffmpeg, w, h)
            arm["probe"] = ffprobe_streams(path, ffprobe)
            arm["packets"] = ffprobe_packets(path, ffprobe)
            arm["size_bytes"] = os.path.getsize(path)
            arm["sha256"] = sha256(path)
        except RuntimeError as exc:
            arm["error"] = str(exc)
        out["arms"].append(arm)
    return out


def try_unsupported(ffmpeg, scratch):
    """组 2 的容器可用性：动图 HEIC / JXL 在本机的真实报错，逐条留原话。"""
    tests = [
        ("muxer=heif 探测", [ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                             "-i", "nullsrc=s=64x48:r=1", "-frames:v", "1", "-f", "heif",
                             os.path.join(scratch, "_probe.heic")]),
        ("muxer=jxl 探测", [ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                            "-i", "nullsrc=s=64x48:r=1", "-frames:v", "1", "-f", "jxl",
                            os.path.join(scratch, "_probe.jxl")]),
        ("mov 容器写 .heic", [ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                              "-i", "nullsrc=s=64x48:r=1", "-frames:v", "1", "-c:v", "libaom-av1",
                              "-y", os.path.join(scratch, "_probe_mov.heic")]),
        ("libjxl_anim + image2 -update 1", [ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                                             "-i", "nullsrc=s=64x48:r=10", "-frames:v", "10",
                                             "-c:v", "libjxl_anim", "-f", "image2", "-update", "1",
                                             "-y", os.path.join(scratch, "_probe_jxlanim.jxl")]),
    ]
    res = []
    for label, cmd in tests:
        rc, so, se = run(cmd)
        res.append({"label": label, "rc": rc,
                    "stderr": se.decode("utf-8", "replace").strip()[-600:]})
    return res


def stream_hw(probe):
    """从 ffprobe json 里挑帧数最多的那条视频流，返回 (w, h, codec, pix_fmt, nb, r_frame_rate)。"""
    best = None
    for st in probe.get("streams", []):
        if st.get("codec_type") != "video":
            continue
        try:
            nb = int(st.get("nb_frames") or 0)
        except ValueError:
            nb = 0
        if best is None or nb > best[5]:
            best = (int(st["width"]), int(st["height"]), st.get("codec_name"),
                    st.get("pix_fmt"), st.get("r_frame_rate"), nb)
    return best


def measure_existing(path, ffmpeg, ffprobe, want_bitdepth=False):
    """对照组：既有的 07*/07e 样张用同一把尺子量（改动占比 + 位深指纹）。"""
    probe = ffprobe_streams(path, ffprobe)
    hw = stream_hw(probe)
    if not hw:
        return {"path": path, "error": "no video stream"}
    w, h, codec, pix, frate, nb = hw
    res = {"path": path, "width": w, "height": h, "codec": codec,
           "probe_pix_fmt": pix, "r_frame_rate": frate, "nb_frames_probe": nb,
           "size_bytes": os.path.getsize(path), "sha256": sha256(path),
           "streams": [(s.get("index"), s.get("codec_name"), s.get("pix_fmt"), s.get("width"),
                        s.get("height"), s.get("r_frame_rate"), s.get("nb_frames"),
                        s.get("profile"))
                       for s in probe.get("streams", []) if s.get("codec_type") == "video"]}
    low = path.lower()
    if low.endswith(".png"):
        res["container"] = parse_apng(path)
    elif low.endswith(".gif"):
        res["container"] = parse_gif(path)
    res["packets"] = ffprobe_packets(path, ffprobe)
    frames, vmap = decode_best(path, ffmpeg, "rgb24", w, h, want=max(2, nb or 2))
    res["decoded_frames"] = len(frames)
    res["vmap"] = vmap
    if len(frames) >= 2:
        meas = measure_pairs(frames, w, h)
        res["changed"] = meas            # 含 per_pair，逐对可查
        res["changed_summary"] = {k: meas[k] for k in (
            "frame_pairs", "mean_changed_frac", "max_changed_frac",
            "mean_changed_frac_ge8", "union_changed_frac", "union_bbox", "method")}
    if want_bitdepth:
        try:
            res["bit_depth_fingerprint"] = bit_depth_fingerprint(path, ffmpeg, w, h)
        except RuntimeError as exc:
            res["bit_depth_fingerprint_error"] = str(exc)[:300]
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--anim-dir", required=True)
    ap.add_argument("--scratch", required=True)
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--ffprobe", default="ffprobe")
    ap.add_argument("--only", default="all",
                    choices=["all", "dirty", "bitdepth", "probe", "control"])
    ap.add_argument("--controls", default="",
                   help="对照组文件，逗号分隔（既有样张，用同一把尺子量）")
    ap.add_argument("--controls-bitdepth", default="",
                   help="对照组里还要做位深指纹的文件，逗号分隔")
    ap.add_argument("--report", default=None)
    args = ap.parse_args()
    os.makedirs(args.anim_dir, exist_ok=True)
    os.makedirs(args.scratch, exist_ok=True)
    report = {"n_frames": N_FRAMES, "fps": FPS, "dirty": [], "bitdepth": None,
              "unsupported": None, "controls": [], "controls_bitdepth": []}
    if args.only in ("all", "dirty"):
        for spec in DIRTY_SPECS:
            r = gen_dirty(spec, args.anim_dir, args.ffmpeg, args.ffprobe)
            print("%-34s %s  frac(max)=%.4f%%  mean=%.4f%%  union=%.4f%%  bbox=%s" % (
                r["name"], r["verdict"], 100 * r["final"]["max_changed_frac"],
                100 * r["final"]["mean_changed_frac"], 100 * r["final"]["union_changed_frac"],
                r["final"]["union_bbox"]), flush=True)
            report["dirty"].append(r)
    if args.only in ("all", "bitdepth"):
        report["bitdepth"] = gen_bitdepth_pair(args.anim_dir, args.ffmpeg, args.ffprobe, args.scratch)
        for arm in report["bitdepth"]["arms"]:
            if "error" in arm:
                print("bitdepth %-6s: ERROR %s" % (arm["label"], arm["error"][:400]), flush=True)
            else:
                fp = arm.get("bit_depth_fingerprint", {})
                print("bitdepth %-6s %-34s frames=%s Ydistinct=%s Y_nonX4=%.4f rgb_nonX4=%.4f" % (
                    arm["label"], arm["name"], fp.get("decoded_frames"),
                    fp.get("distinct_y_first_frame"),
                    fp.get("frac_y_not_multiple_of_4", float("nan")),
                    fp.get("frac_rgb_not_multiple_of_4", float("nan"))), flush=True)
    if args.only in ("all", "probe"):
        report["unsupported"] = try_unsupported(args.ffmpeg, args.scratch)
        for t in report["unsupported"]:
            print("%-32s rc=%d  %s" % (t["label"], t["rc"], t["stderr"].replace("\n", " ")[:180]), flush=True)
    ctl = [p for p in args.controls.split(",") if p.strip()]
    if ctl:
        for p in ctl:
            r = measure_existing(p, args.ffmpeg, args.ffprobe)
            c = r.get("changed", {})
            print("control %-58s frames=%s mean_changed=%s max_changed=%s %s" % (
                os.path.basename(p), r.get("decoded_frames"),
                "%.4f%%" % (100 * c["mean_changed_frac"]) if "mean_changed_frac" in c else "n/a",
                "%.4f%%" % (100 * c["max_changed_frac"]) if "max_changed_frac" in c else "n/a",
                r.get("error", "")), flush=True)
            report["controls"].append(r)
    for p in [x for x in args.controls_bitdepth.split(",") if x.strip()]:
        r = measure_existing(p, args.ffmpeg, args.ffprobe, want_bitdepth=True)
        report["controls_bitdepth"].append(r)
        print("control-bitdepth %-40s %s" % (
            os.path.basename(p), json.dumps(r.get("bit_depth_fingerprint",
                                                   r.get("bit_depth_fingerprint_error", "n/a")))),
              flush=True)
    out = args.report or os.path.join(args.scratch, "gen_report.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=1, ensure_ascii=False)
    print("report ->", out)


if __name__ == "__main__":
    sys.exit(main())
