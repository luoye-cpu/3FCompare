# -*- coding: utf-8 -*-
"""造 layout 0 对照夹具：同几何、同帧数、同帧率，只差解码器交出来的像素格式。

为什么要造：软件转换那条路（sws_scale 转成 RGB 再整帧上传 4 B/px）在我今天所有 11 个臂上
sw_ms 都是 0 —— 意思是我的素材集一次都没触发它，所以"RGB/packed 类源在软解下到底多贵"
这个问题**从来没有读数**。这一组就是为了产生第一个读数。

同几何是硬要求：sw_ms 与 uploadCopyBytes 都要能直接跟 yuv420p 那臂对，尺寸一变就没法比。
"""
import os
import subprocess
import sys

FFMPEG = r"C:\PLAN\FFmpeg\ffmpeg.exe"
OUTDIR = r"C:\PLAN\3FCompare\_3fc_tmp\fixtures"
W, H, FPS, SECONDS = 1920, 1080, 60, 6

# 名字 -> 期望解码后交出的像素格式（用 ffprobe 回读校验，不信命令行说成功就算）
FORMATS = {
    "yuv420p": "yuv420p",    # 平面参考臂：已知 layout 1
    "yuv422p": "yuv422p",    # 平面 4:2:2
    "yuyv422": "yuyv422",    # 打包 4:2:2
    "rgb24":   "rgb24",      # 明显不是 YUV 平面
    "bgr0":    "bgr0",
    "gbrp":    "gbrp",       # 平面 RGB
    # B1 新增的两条平面档。ffprobe 回读确认 ffv1 会**保留**这两枚（不像 nv16/nv24 会被
    # 规范化成分离平面兄弟），所以它们是本表唯一能用文件跑出来的新路径。
    "yuv410p": "yuv410p",
    "yuv440p": "yuv440p",
}


def make(tag, pix_fmt):
    dst = os.path.join(OUTDIR, "fx_%s_%dx%d.mkv" % (tag, W, H))
    if os.path.isfile(dst):
        print("exists, skipping", dst)
        return dst
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
           "-f", "lavfi", "-i", "testsrc2=size=%dx%d:rate=%d:duration=%d" % (W, H, FPS, SECONDS),
           "-c:v", "ffv1", "-level", "3", "-pix_fmt", pix_fmt,
           "-g", "30", dst]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    if r.returncode != 0:
        print("FAILED", tag, (r.stderr or "").strip()[:200])
        return None
    print("made", os.path.basename(dst), os.path.getsize(dst), "bytes")
    return dst


def probe(path):
    """回读真实像素格式与尺寸 —— 夹具的像素格式不能信生成命令，只能信文件。"""
    cmd = [FFMPEG.replace("ffmpeg.exe", "ffprobe.exe"), "-v", "error",
           "-select_streams", "v:0",
           "-show_entries", "stream=codec_name,pix_fmt,width,height,r_frame_rate",
           "-of", "default=nw=1", path]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    d = {}
    for line in (r.stdout or "").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            d[k.strip()] = v.strip()
    return d


if __name__ == "__main__":
    os.makedirs(OUTDIR, exist_ok=True)
    want = sys.argv[1:] or list(FORMATS)
    report = []
    for tag in want:
        pix = FORMATS[tag]
        path = make(tag, pix)
        if not path:
            continue
        info = probe(path)
        ok = info.get("pix_fmt") == pix
        print("  %-9s -> codec=%-8s pix_fmt=%-10s %sx%s %s  %s" % (
            tag, info.get("codec_name"), info.get("pix_fmt"),
            info.get("width"), info.get("height"), info.get("r_frame_rate"),
            "OK" if ok else "*** MISMATCH: asked %s ***" % pix))
        report.append((tag, info.get("pix_fmt"), ok))
    bad = [t for t, _p, ok in report if not ok]
    print("\nbuilt=%d mismatched=%d" % (len(report), len(bad)))
    if bad:
        print("MISMATCHED:", bad)
        sys.exit(1)
