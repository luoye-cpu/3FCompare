#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""动图解码格式清单 (lane-W1e)

给"动图解码帧能否原样上传进 D3D11 纹理"这个决策提供**事实底座**：
逐文件报告 demuxer、codec 名与其数值 id、流级声明的 pix_fmt、以及**真正解码出来的帧格式**
（两路独立解码测量必须一致），再按内存字节序 / 位深 / 语义损失分类。

为什么不只读流头：本仓已被"流头与解码帧不一致"绊倒过两次。实测本机样本
anim_4k_webp.webp 声明 `argb`，解码帧却是 `yuva420p`。

失败即非零退出（绝不允许交出一份"悄悄少了几行"的清单）：
  * 列出的文件不存在
  * 任一文件解不出 ≥1 帧
  * 两路解码实测（ffprobe -show_frames / ffmpeg debug pixfmt）不一致
  * codec_id 三方（内核对账头 / 外部 ffprobe 所用 avcodec / 内核 runtime avcodec）对不上或比对条数过少
  * 内存序交叉验证（swscale rawvideo 逐字节 vs libavutil 像素描述符）对不上
  * 工具不可运行 / 超时

用法：
    python 动图解码格式清单.py [采样目录] [--out PATH] [--strict]
决定论：同一输入 ⇒ 同一输出（不写时间戳，所有集合排序后输出）。
"""

from __future__ import annotations

import argparse
import ctypes
import glob
import hashlib
import json
import os
import re
import subprocess
import sys

DEFAULT_SAMPLES = r"C:\PLAN\3FCompare\3fp\samples\media\anim"
DEFAULT_FIXTURES = [
    r"C:\PLAN\3FCompare\3fp\build\anim-perf\anim_4k_apng.png",
    r"C:\PLAN\3FCompare\3fp\build\anim-perf\anim_4k_webp.webp",
    r"C:\PLAN\3FCompare\3fp\build\anim-perf\anim_1080p_apng.png",
    r"C:\PLAN\3FCompare\3fp\build\anim-perf\anim_6k_apng.png",
    r"C:\PLAN\3FCompare\3fp\build\anim-perf\anim_over16k_apng.png",
]
DEFAULT_PROBE = r"C:\PLAN\FFmpeg\ffprobe.exe"
DEFAULT_FFMPEG = r"C:\PLAN\FFmpeg\ffmpeg.exe"
DEFAULT_HEADER = (
    r"C:\PLAN\3FCompare\3fp\kernel\third_party\ffmpeg"
    r"\include\libavcodec\codec_id.h"
)
DEFAULT_DLLS = [r"C:\PLAN\FFmpeg\avcodec-63.dll",
                r"C:\PLAN\3FCompare\3fp\kernel\runtime\avcodec-63.dll"]
DEFAULT_PERF_DIR = r"C:\PLAN\3FCompare\3fp\build\anim-perf"
DEFAULT_OUT = r"C:\PLAN\3FCompare\3fp\build\anim-perf\lane-W1e\decod_formats.md"

MEDIA_EXT = {".png", ".webp", ".gif", ".avif", ".heic", ".jxl", ".mng",
             ".apng", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif"}

# 交叉验证内存序用的 packed 8bit 格式（样本里出现的会并进来）
BYTEORDER_PROBE_SET = ["bgra", "rgba", "argb", "abgr", "rgb0", "bgr0", "0rgb",
                       "0bgr", "rgb24", "bgr24"]

# 探针源颜色：R<G<B 三者距离足够大，字节序靠"大小排位"判定，抗 1LSB 量化误差
PROBE_COLOR = "0x20A0E0"   # R=0x20 G=0xA0 B=0xE0

MIN_PARITY_COMPARED = 200      # 至少比对这么多 codec 名，才算"真的比过"
MIN_HEADER_ENTRIES = 400

FLAG_BE, FLAG_PAL, FLAG_BITSTREAM, FLAG_HWACCEL = 1 << 0, 1 << 1, 1 << 2, 1 << 3
FLAG_PLANAR, FLAG_RGB, FLAG_ALPHA = 1 << 4, 1 << 5, 1 << 7

CLASSES = ["verbatim-BGRA", "verbatim-RGBA", "needs-swizzle",
           "10bit-or-deeper", "YUV-can-use-directYuv", "unknown"]


def shq(p: str) -> str:
    """markdown 里用正斜杠，避免反斜杠被当成转义。"""
    return p.replace("\\", "/")


class Runner:
    def __init__(self, probe: str, ffmpeg: str, timeout: int):
        self.probe = probe
        self.ffmpeg = ffmpeg
        self.timeout = timeout
        self.calls = 0

    def _run(self, cmd, binary=False):
        self.calls += 1
        try:
            p = subprocess.run(cmd, capture_output=True, timeout=self.timeout,
                               shell=False)
        except subprocess.TimeoutExpired:
            return None, None, "TIMEOUT after %ss" % self.timeout
        except OSError as e:
            return None, None, "OSERROR %s" % e
        dec = (lambda b: b) if binary else (lambda b: b.decode("utf-8", "replace"))
        return p.returncode, dec(p.stdout), dec(p.stderr)

    def version_line(self, exe):
        rc, out, err = self._run([exe, "-version"])
        if rc is None:
            return None, err
        text = (out or "") + (err or "")
        first = text.splitlines()[0].strip() if text.strip() else ""
        return first, None

    def json(self, args):
        rc, out, err = self._run([self.probe, "-hide_banner", "-loglevel",
                                  "error"] + args)
        if rc != 0:
            return None, "rc=%s %s" % (rc, (err or "").strip()[:400])
        try:
            return json.loads(out or "{}"), None
        except Exception as e:
            return None, "json parse failed: %s" % e

    def lines(self, args):
        rc, out, err = self._run([self.probe, "-hide_banner", "-loglevel",
                                  "error"] + args)
        if rc is None:
            return None, err
        if rc != 0:
            return None, "rc=%s %s" % (rc, (err or "").strip()[:400])
        rows = [ln.strip() for ln in (out or "").splitlines() if ln.strip()]
        return rows, None

    def decoded_fmts(self, path, vidx):
        """路 A：ffprobe 逐帧解码，返回 (帧格式序列, 错误)。"""
        rows, err = self.lines(["-select_streams", "v:%d" % vidx, "-show_entries",
                                "frame=pix_fmt", "-of", "csv=p=0", path])
        if err:
            return None, err
        fmts = []
        for ln in rows:
            first = ln.split(",")[0].strip()
            if first:
                fmts.append(first)
        return fmts, None

    def counted(self, path, vidx):
        """独立的第三条解码计数：-count_frames 走流级计数路径。"""
        rows, err = self.lines(["-select_streams", "v:%d" % vidx, "-count_frames",
                                "-show_entries", "stream=nb_read_frames",
                                "-of", "csv=p=0", path])
        if err or not rows:
            return None, err or "no row"
        m = re.search(r"\d+", rows[0])
        return (int(m.group(0)) if m else None), None

    def ffmpeg_debug_fmt(self, path, vidx):
        """路 B：ffmpeg 解码，读它自己给解码帧定档的 pixfmt/csp/range。"""
        rc, out, err = self._run([self.ffmpeg, "-hide_banner", "-loglevel", "debug",
                                  "-i", path, "-map", "0:v:%d" % vidx,
                                  "-f", "null", "-"])
        text = (out or "") + (err or "")
        if rc not in (0, None):
            return None, "ffmpeg rc=%s" % rc
        for ln in text.splitlines():
            if "input from stream" not in ln or "pixfmt:" not in ln:
                continue
            kv = dict(re.findall(r"(\w+):(\S+)", ln))
            return {"pixfmt": kv.get("pixfmt", "?"), "csp": kv.get("csp", "-"),
                    "range": kv.get("range", "-"),
                    "w": kv.get("w", "?"), "h": kv.get("h", "?")}, None
        return None, "no 'input from stream ... pixfmt:' line"

    def raw_probe(self, fmt):
        rc, out, err = self._run([self.ffmpeg, "-hide_banner", "-loglevel", "error",
                                  "-f", "lavfi",
                                  "-i", "color=c=%s:s=1x1:r=25" % PROBE_COLOR,
                                  "-frames:v", "1", "-pix_fmt", fmt,
                                  "-f", "rawvideo", "-"], binary=True)
        if rc != 0 or not out:
            return None, "rc=%s %s" % (rc, (err or "").strip()[:200])
        return out, None


# --------------------------------------------------------------------------- #
# libavutil 像素描述符（内存序/位深的权威来源，直接读被链接的那份库）
# --------------------------------------------------------------------------- #
class AVComponent(ctypes.Structure):
    _fields_ = [("plane", ctypes.c_int), ("step", ctypes.c_int),
                ("offset", ctypes.c_int), ("shift", ctypes.c_int),
                ("depth", ctypes.c_int)]


class AVPixFmtDescriptor(ctypes.Structure):
    _fields_ = [("name", ctypes.c_char_p), ("nb_components", ctypes.c_uint8),
                ("log2_chroma_w", ctypes.c_uint8), ("log2_chroma_h", ctypes.c_uint8),
                ("_pad", ctypes.c_uint8), ("flags", ctypes.c_uint64),
                ("comp", AVComponent * 4), ("alias", ctypes.c_char_p)]


class PixUtil:
    def __init__(self, dll_path):
        self.dll = dll_path
        self.ok = False
        self.error = None
        self.by_name = {}
        try:
            self.lib = ctypes.CDLL(dll_path)
            self.lib.av_get_pix_fmt.restype = ctypes.c_int
            self.lib.av_get_pix_fmt.argtypes = [ctypes.c_char_p]
            self.lib.av_pix_fmt_desc_get.restype = ctypes.POINTER(AVPixFmtDescriptor)
            self.lib.av_pix_fmt_desc_get.argtypes = [ctypes.c_int]
            self.lib.avutil_version.restype = ctypes.c_uint
            self.version = self.lib.avutil_version()
            self.ok = True
        except Exception as e:
            self.error = "load failed: %s" % e
            self.version = 0

    def desc(self, fmt_name):
        if not self.ok:
            return None
        pid = self.lib.av_get_pix_fmt(fmt_name.encode())
        if pid < 0:
            return None
        d = self.lib.av_pix_fmt_desc_get(pid)
        if not d:
            return None
        c = d.contents
        comps = []
        for i in range(4):
            k = c.comp[i]
            comps.append({"plane": k.plane, "step": k.step, "offset": k.offset,
                          "shift": k.shift, "depth": k.depth})
        return {"id": pid, "nb_components": c.nb_components, "flags": c.flags,
                "log2_chroma_w": c.log2_chroma_w, "log2_chroma_h": c.log2_chroma_h,
                "comp": comps}


def fmt_layout(d):
    """由描述符推格式的内存布局。返回 (order字符串|None, 位深, shape, 明细)。"""
    n = d["nb_components"]
    comps = d["comp"][:n]
    depth = max(c["depth"] for c in comps)
    flags = d["flags"]
    planar = bool(flags & FLAG_PLANAR) or any(c["plane"] != 0 for c in comps)
    if planar:
        planes = sorted({c["plane"] for c in comps if c["depth"]})
        return None, depth, "planar(%d planes)" % len(planes), {}
    is_rgb = bool(flags & FLAG_RGB)
    has_alpha = bool(flags & FLAG_ALPHA)
    letters = ({"R", "G", "B", "A"} if is_rgb else {"Y", "U", "V", "A"})
    names = ["R", "G", "B", "A"] if is_rgb else ["Y", "U", "V", "A"]
    slots = {}
    for idx, c in enumerate(comps):
        if c["depth"] == 0:
            continue
        slots[c["offset"]] = names[idx] if idx < 4 else "?"
    if not slots:
        return None, depth, "empty", {}
    width = max(slots) + 1
    order = "".join(slots.get(i, ".") for i in range(width))
    info = {"slots": slots, "is_rgb": is_rgb, "has_alpha": has_alpha,
            "step": comps[0]["step"] if comps else 0, "width": width,
            "letters": letters, "bytes_per_px": max(c["offset"] for c in comps) + 1}
    return order, depth, "packed", info


def classify(d, fmt_name):
    """按内存字节序/位深/语义损失给出唯一分类。"""
    if d is None:
        return "unknown", "描述符取不到"
    flags, n = d["flags"], d["nb_components"]
    order, depth, shape, info = fmt_layout(d)
    note = "%s=%s 位深=%d" % ("内存序" if shape == "packed" else "形态",
                              order if shape == "packed" else shape, depth)

    if depth > 8:
        return "10bit-or-deeper", note + " ⇒ 今天的代码走 R16G16B16A16 分支"
    if flags & (FLAG_HWACCEL | FLAG_BITSTREAM | FLAG_PAL) or n < 3:
        return "needs-swizzle", note + (" ⇒ 调色板索引，必须先展开"
                                        if flags & FLAG_PAL else
                                        " ⇒ 索引/位流/硬件格式，必须先展开")
    if info.get("is_rgb") and shape == "packed":
        if not info.get("has_alpha"):
            if order in ("RGB", "BGR"):
                return "needs-swizzle", note + " ⇒ 24-bit 无 alpha，补 A 才算转换完成"
            return "needs-swizzle", note + " ⇒ 无 alpha 语义（X 填充位，规范不保证填充值）"
        if order == "BGRA":
            return "verbatim-BGRA", note + " ⇒ 与 DXGI B8G8R8A8_UNORM 逐字节一致"
        if order == "RGBA":
            return "verbatim-RGBA", note + " ⇒ 与 DXGI R8G8B8A8_UNORM 逐字节一致"
        return "needs-swizzle", note + " ⇒ 顺序既非 BGRA 也非 RGBA"
    if not info.get("is_rgb") and n >= 3:
        return "YUV-can-use-directYuv", note + " ⇒ YCbCr 族，不是 RGB 直通问题"
    return "unknown", note


# --------------------------------------------------------------------------- #
# codec_id.h 解析 + avcodec DLL 数值 id 读取
# --------------------------------------------------------------------------- #
def parse_codec_id_header(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    start = text.index("enum AVCodecID {") + len("enum AVCodecID {")
    body = text[start:]
    end = body.index("\n};")
    body = body[:end]
    body = re.sub(r"/\*.*?\*/", " ", body, flags=re.S)   # 块注释（含 /**< ... */）
    vals, order, aliases, unparsed = {}, [], [], []
    pending = []
    for raw in body.split("\n"):
        ln = re.sub(r"//[^\n]*", "", raw).strip()
        if not ln:
            continue
        m = re.match(r"^#\s*define\s+(AV_CODEC_ID_\w+)\s+(AV_CODEC_ID_\w+)$", ln)
        if m:
            aliases.append((m.group(1), m.group(2)))
            vals[m.group(1)] = vals.get(m.group(2))
            continue
        if ln.startswith("#"):
            continue
        pending.append(ln)
        joined = " ".join(pending)
        if "," not in joined:
            continue
        stmt = joined.split(",")[0].strip()
        pending = []
        m = re.match(r"^AV_CODEC_ID_(\w+)\s*(?:=\s*(.+))?$", stmt)
        if not m:
            if stmt:
                unparsed.append(stmt[:60])
            continue
        name, val = m.group(1), m.group(2)
        if val:
            val = val.strip()
            cur = vals.get(val) if val.startswith("AV_CODEC_ID_") else int(val, 0)
        else:
            cur = 0 if not order else vals[order[-1]] + 1
        full = "AV_CODEC_ID_" + name
        vals[full] = cur
        order.append(full)
    return vals, order, aliases, unparsed


def read_header_version(include_root, lib):
    """读内核随附头声明的库版本（MAJOR 在 version_major.h，MINOR/MICRO 在 version.h）。"""
    text = ""
    for fn in ("version.h", "version_major.h"):
        p = os.path.join(include_root, lib, fn)
        if os.path.isfile(p):
            text += open(p, encoding="utf-8", errors="replace").read()
    g = dict(re.findall(r"#define\s+LIB\w+_VERSION_(MAJOR|MINOR|MICRO)\s+(\d+)", text))
    if len(g) != 3:
        return None, "%s 版本宏不全（读到 %s）" % (lib, sorted(g))
    return "%s.%s.%s" % (g["MAJOR"], g["MINOR"], g["MICRO"]), None


def dll_codec_ids(dll_path):
    out = {"path": dll_path, "ok": False, "error": None, "map": {}, "version": None,
           "sha256_16": None}
    try:
        out["sha256_16"] = hashlib.sha256(open(dll_path, "rb").read()).hexdigest()[:16]
    except Exception as e:
        out["error"] = "hash: %s" % e
    try:
        lib = ctypes.CDLL(dll_path)
        lib.avcodec_version.restype = ctypes.c_uint
        v = lib.avcodec_version()
        out["version"] = "%d.%d.%d" % (v >> 16, (v >> 8) & 0xFF, v & 0xFF)
        lib.avcodec_get_name.restype = ctypes.c_char_p
        lib.avcodec_get_name.argtypes = [ctypes.c_int]
        for i in range(0, 0x2000):
            nm = lib.avcodec_get_name(i)
            if not nm:
                continue
            nm = nm.decode("ascii", "replace")
            if nm.startswith("unknown") or nm.startswith("AV_CODEC_ID_PRIVATE"):
                continue
            out["map"].setdefault(nm, i)
        out["ok"] = bool(out["map"])
    except Exception as e:
        out["error"] = "load: %s" % e
    return out


# --------------------------------------------------------------------------- #
# 成本引证：只从既有日志里读，逐条带日志名，绝不自己算
# --------------------------------------------------------------------------- #
def harvest_costs(perf_dir):
    paths = sorted(glob.glob(os.path.join(perf_dir, "*.txt"))) + \
        sorted(glob.glob(os.path.join(perf_dir, "audit", "*.txt")))
    rows = []
    for p in paths:
        t = open(p, encoding="utf-8", errors="replace").read()
        mf = re.search(r"^file:\s*(\S+)", t, re.M)
        mc = re.search(r"转换=([\d.]+)", t)
        if not (mf and mc):
            continue
        mp = re.search(r"整窗汇总（[^）]*?呈现帧=(\d+)", t)
        st = re.search(r"状态=(\d+)", t)
        rows.append({"log": os.path.relpath(p, perf_dir).replace("\\", "/"),
                     "file": mf.group(1).replace("\\", "/").lower(),
                     "ms": mc.group(1),
                     "presented": mp.group(1) if mp else None,
                     "state": st.group(1) if st else None})
    return rows


def costs_for(rows, path):
    key = os.path.normpath(path).replace("\\", "/").lower()
    base = os.path.basename(key)
    hits = [r for r in rows if r["file"] == key or r["file"].endswith("/" + base)]
    return sorted(hits, key=lambda r: r["log"])


# --------------------------------------------------------------------------- #
def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="动图解码格式清单 (lane-W1e)")
    ap.add_argument("samples_dir", nargs="?", default=DEFAULT_SAMPLES,
                    help="动图样本目录（必填语义：作为位置参数传入）")
    ap.add_argument("--fixtures", nargs="*", default=DEFAULT_FIXTURES,
                    help="额外的测量夹具（绝对路径列表）")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--probe", default=DEFAULT_PROBE)
    ap.add_argument("--ffmpeg", default=DEFAULT_FFMPEG)
    ap.add_argument("--codec-header", default=DEFAULT_HEADER)
    ap.add_argument("--avcodec-dll", nargs="*", default=DEFAULT_DLLS,
                    help="要交叉核对 codec_id 数值的 avcodec DLL（外部 + 内核 runtime）")
    ap.add_argument("--avutil-dll", default=None,
                    help="读像素描述符用的 avutil DLL；默认取 --avcodec-dll 同目录")
    ap.add_argument("--perf-dir", default=DEFAULT_PERF_DIR,
                    help="既有审计日志目录（只读，用于引证今天的成本）")
    ap.add_argument("--timeout", type=int, default=240)
    ap.add_argument("--strict", action="store_true",
                    help="把警告（声明↔解码不一致 / 分类 unknown / 计数不符）也升级为非零退出")
    ap.add_argument("--scratch", default=None)
    args = ap.parse_args(argv)

    failures, warnings = [], []
    r = Runner(args.probe, args.ffmpeg, args.timeout)

    # ---- 0. 工具归因 -------------------------------------------------------
    prov, err = r.version_line(args.probe)
    if not prov:
        failures.append("ffprobe 不可用 %s: %s" % (args.probe, err))
    ffv, err = r.version_line(args.ffmpeg)
    if not ffv:
        failures.append("ffmpeg 不可用 %s: %s" % (args.ffmpeg, err))

    avutil_path = args.avutil_dll or (
        os.path.join(os.path.dirname(args.avcodec_dll[0]), "avutil-61.dll")
        if args.avcodec_dll else None)
    pix = PixUtil(avutil_path) if avutil_path else None
    if pix and not pix.ok:
        failures.append("avutil 加载失败（内存序无从判定）%s: %s" % (avutil_path, pix.error))

    # ---- 1. codec_id 三方一致性 -------------------------------------------
    header_vals, header_order, header_aliases, header_unparsed = {}, [], [], []
    try:
        header_vals, header_order, header_aliases, header_unparsed = \
            parse_codec_id_header(args.codec_header)
    except Exception as e:
        failures.append("codec_id.h 解析失败 %s: %s" % (args.codec_header, e))
    if len(header_order) < MIN_HEADER_ENTRIES:
        failures.append("codec_id.h 只解析出 %d 个枚举（< %d）⇒ 解析器不可信，不做判定"
                        % (len(header_order), MIN_HEADER_ENTRIES))
    # 解析自检：只有整段枚举都对，这两个显值哨兵才会落在正确位置
    sentinels = {"AV_CODEC_ID_NONE": 0, "AV_CODEC_ID_FIRST_AUDIO": 0x10000,
                 "AV_CODEC_ID_PROBE": 0x19000}
    for k, want in sentinels.items():
        got = header_vals.get(k)
        if got != want:
            failures.append("codec_id.h 解析自检失败 %s=%s 期望 %d" % (k, got, want))

    dlls = [dll_codec_ids(p) for p in args.avcodec_dll]
    for d in dlls:
        if not d["ok"]:
            failures.append("无法从 %s 取 codec id（%s）⇒ 一致性无从证明，清单对 A1 无效"
                            % (d["path"], d["error"]))

    parity_rows, parity_mismatch = [], []
    interesting = ["png", "gif", "webp", "apng", "webp_anim", "jpegxl_anim",
                   "av1", "hevc", "mjpeg", "jpegxl"]
    for nm in sorted(interesting):
        hv = header_vals.get("AV_CODEC_ID_" + nm.upper())
        row = {"name": nm, "header": hv}
        for i, d in enumerate(dlls):
            row["dll%d" % i] = d["map"].get(nm)
        parity_rows.append(row)
        present = [v for k, v in row.items() if k != "name" and v is not None]
        if present and (len(set(present)) > 1 or len(present) != len(dlls) + 1):
            parity_mismatch.append(row)

    per_dll_cmp = []
    full_mismatch = []
    for di, d in enumerate(dlls):
        if not d["ok"] or not header_vals:
            continue
        cnt = 0
        for full, hv in sorted(header_vals.items()):
            short = full[len("AV_CODEC_ID_"):].lower()
            dv = d["map"].get(short)
            if dv is None:
                continue
            cnt += 1
            if hv != dv:
                full_mismatch.append("%s header=%s dll#%d=%s" % (short, hv, di, dv))
        per_dll_cmp.append((di, cnt))
        if cnt < MIN_PARITY_COMPARED:
            failures.append("codec_id 对 %s 只比对出 %d 条（< %d）⇒ 比对本身不可信"
                            % (d["path"], cnt, MIN_PARITY_COMPARED))
    if full_mismatch:
        failures.append("codec_id 不一致 %d 条：%s" % (len(full_mismatch),
                                                      "; ".join(full_mismatch[:8])))
    dll_vs_dll = []
    # 随附头声明的库版本 vs 实际加载的 DLL 版本（本仓已因"头/件不一致"栽过两次）
    include_root = os.path.dirname(os.path.dirname(os.path.abspath(args.codec_header)))
    ver_rows = []
    for lib, getter in (("libavcodec", lambda d: d["version"]), ("libavutil", None)):
        hv, verr = read_header_version(include_root, lib)
        if verr:
            failures.append("随附头版本读不出：%s" % verr)
            continue
        if lib == "libavcodec":
            got = sorted({d["version"] for d in dlls if d["version"]})
        else:
            got = [("%d.%d.%d" % ((pix.version >> 16), (pix.version >> 8) & 0xFF,
                                  pix.version & 0xFF)) if pix and pix.ok else None]
            got = sorted({g for g in got if g})
        ver_rows.append({"lib": lib, "header": hv, "dll": got})
        for g in got:
            if g != hv:
                warnings.append("%s：随附头声明 %s，实际 DLL 是 %s ⇒ 头与件不同源；"
                                "codec_id 编号已逐名比对为一致，故本次分类仍可用"
                                % (lib, hv, g))
    for a in range(len(dlls)):
        for b in range(a + 1, len(dlls)):
            if dlls[a]["ok"] and dlls[b]["ok"]:
                shared = set(dlls[a]["map"]) & set(dlls[b]["map"])
                diff = [k for k in sorted(shared) if dlls[a]["map"][k] != dlls[b]["map"][k]]
                dll_vs_dll.append(("%s#%d" % (shq(dlls[a]["path"]), a),
                                   "%s#%d" % (shq(dlls[b]["path"]), b), len(shared),
                                   diff[:5]))
                if diff:
                    failures.append("两份 avcodec 之间 id 也不一致：%s" % diff[:5])

    # ---- 2. 内存序实测（rawvideo 逐字节 vs libavutil 描述符）--------------
    ref = {"R": 0x20, "G": 0xA0, "B": 0xE0}   # PROBE_COLOR 的三分量

    def observed_map(raw):
        """把 1×1 rawvideo 的字节按分量大小还原成内存序字符串。"""
        s = []
        for v in raw:
            dist = {abs(v - k): ch for ch, k in ref.items()}
            bd = min(dist)
            best = dist[bd] if bd <= 12 else None
            if best:
                s.append(best)
            elif v >= 0xF0:
                s.append("A")
            elif v <= 0x10:
                s.append("X")
            else:
                s.append("?")
        return "".join(s)

    byte_rows, byte_fail = [], []
    probe_set = list(dict.fromkeys(BYTEORDER_PROBE_SET))
    for fmt in probe_set:
        raw, err = r.raw_probe(fmt)
        d = pix.desc(fmt) if pix and pix.ok else None
        if raw is None:
            byte_fail.append("%s: rawvideo 探针失败 %s" % (fmt, err))
            byte_rows.append({"fmt": fmt, "observed": "-", "descriptor": "-",
                              "bytes": "-", "verdict": "PROBE-FAIL"})
            continue
        obs = observed_map(raw)
        order, depth, shape, info = fmt_layout(d) if d else (None, None, None, {})
        if d is None:
            byte_fail.append("%s: 描述符取不到" % fmt)
            byte_rows.append({"fmt": fmt, "observed": obs, "descriptor": "-",
                              "bytes": " ".join("%02x" % v for v in raw),
                              "verdict": "NO-DESC"})
            continue
        if shape != "packed":
            byte_rows.append({"fmt": fmt, "observed": obs, "descriptor": shape,
                              "bytes": " ".join("%02x" % v for v in raw),
                              "verdict": "skip(非 packed)"})
            continue
        if info["step"] and len(raw) != info["step"]:
            byte_fail.append("%s: 每像素字节数 实测 %d ≠ 描述符 step %d" %
                             (fmt, len(raw), info["step"]))
            verdict = "SIZE-MISMATCH"
        else:
            bad = ["byte%d: 实测 %s ≠ 描述符 %s" % (o, obs[o] if o < len(obs) else "-", ch)
                   for o, ch in sorted(info["slots"].items())
                   if o >= len(obs) or obs[o] != ch]
            if bad:
                byte_fail.append("%s: %s" % (fmt, "; ".join(bad)))
                verdict = "MISMATCH"
            else:
                verdict = "OK"
        byte_rows.append({"fmt": fmt, "observed": obs, "descriptor": order,
                          "bytes": " ".join("%02x" % v for v in raw),
                          "verdict": verdict})
    if byte_fail:
        failures.append("内存序交叉验证失败：%s" % "; ".join(byte_fail[:6]))

    # ---- 3. 逐文件测量 -----------------------------------------------------
    if not os.path.isdir(args.samples_dir):
        failures.append("样本目录不存在：%s" % args.samples_dir)
    listed, skipped = [], []
    if os.path.isdir(args.samples_dir):
        for name in sorted(os.listdir(args.samples_dir)):
            p = os.path.join(args.samples_dir, name)
            if os.path.isfile(p):
                (listed if os.path.splitext(name)[1].lower() in MEDIA_EXT
                 else skipped).append(p)
    for f in args.fixtures:
        if not os.path.isabs(f):
            failures.append("夹具必须给绝对路径：%s" % f)
            continue
        if f.lower() not in [x.lower() for x in listed]:
            listed.append(f)

    costs = harvest_costs(args.perf_dir) if os.path.isdir(args.perf_dir) else []
    if not costs:
        warnings.append("在 %s 没读到任何带 file:+转换= 的日志 ⇒ 成本列全部只能写"
                        "\u0027未记录\u0027" % args.perf_dir)

    measured = []
    for path in listed:
        rec = {"path": path, "exists": os.path.isfile(path)}
        if not rec["exists"]:
            rec["error"] = "文件不存在"
            failures.append("文件缺失：%s" % path)
            measured.append(rec)
            continue

        obj, err = r.json(["-show_format", "-show_streams", "-of", "json", path])
        if err or not obj:
            rec["error"] = "ffprobe 探测失败：%s" % err
            failures.append("%s: 探测失败 %s" % (os.path.basename(path), err))
            measured.append(rec)
            continue
        rec["demuxer"] = (obj.get("format") or {}).get("format_name", "-")
        streams = [s for s in obj.get("streams", []) if s.get("codec_type") == "video"]
        rec["n_streams"] = len(streams)
        rec["streams"] = [{"index": s.get("index"), "v": k,
                           "codec": s.get("codec_name"),
                           "decl_pix": s.get("pix_fmt"),
                           "decl_nb": s.get("nb_frames"),
                           "w": s.get("width"), "h": s.get("height"),
                           "attached": (s.get("disposition") or {}).get("attached_pic")}
                          for k, s in enumerate(streams)]
        if not streams:
            rec["error"] = "没有视频流"
            failures.append("%s: 无视频流 ⇒ 解不出任何帧" % os.path.basename(path))
            measured.append(rec)
            continue

        # 每个视频流都实测一遍，把帧最多的那条当"动图流"
        per_stream = []
        for k in range(len(streams)):
            fA, err = r.decoded_fmts(path, k)
            fB, errB = r.ffmpeg_debug_fmt(path, k)
            cnt, errC = r.counted(path, k)
            if fA is None or not fA:
                warnings.append("%s v:%d 逐帧解码没拿到帧（%s）" %
                                (os.path.basename(path), k, err or err or "0 行"))
                fA = []
            uniq = {}
            for f in fA:
                uniq[f] = uniq.get(f, 0) + 1
            per_stream.append({"v": k, "frames": len(fA), "seq": fA, "uniq": uniq,
                               "pathB": fB,
                               "counted": cnt, "errors": [e for e in (err, errB, errC) if e]})
        chosen = max(per_stream, key=lambda s: s["frames"])
        rec["per_stream"] = per_stream
        rec["primary"] = chosen
        rec["codec"] = streams[chosen["v"]].get("codec_name")
        rec["decl_pix"] = streams[chosen["v"]].get("pix_fmt")
        rec["codec_id_header"] = header_vals.get("AV_CODEC_ID_" +
                                                 (rec["codec"] or "").upper())
        rec["codec_id_dll"] = {"dll%d" % i: d["map"].get(rec["codec"])
                               for i, d in enumerate(dlls)}
        ids = [rec["codec_id_header"]] + [rec["codec_id_dll"]["dll%d" % i]
                                          for i in range(len(dlls))]
        rec["codec_id_all"] = ids
        if any(v is None for v in ids) or len(set(ids)) != 1:
            failures.append("%s: codec `%s` 的 id 三方不齐（头/库…=%s）" %
                            (os.path.basename(path), rec["codec"], ids))
        if chosen["frames"] == 0:
            rec["error"] = "所有视频流都解不出帧"
            failures.append("%s: 解出 0 帧" % os.path.basename(path))
            measured.append(rec)
            continue
        if len(chosen["uniq"]) > 1:
            warnings.append("%s: 同一文件内解码格式不唯一 %s" %
                            (os.path.basename(path), chosen["uniq"]))
        # 两路解码实测必须一致：ffmpeg 的定档行讲的是"第一帧"的格式
        if chosen["pathB"] is None:
            failures.append("%s v:%d: ffmpeg 路没给出 pixfmt（%s）" %
                            (os.path.basename(path), chosen["v"], chosen["errors"]))
        elif chosen["pathB"]["pixfmt"] != chosen["seq"][0]:
            failures.append("%s: 两路解码实测不一致 ffprobe首帧=%s ffmpeg=%s" %
                            (os.path.basename(path), chosen["seq"][0],
                             chosen["pathB"]["pixfmt"]))
        if chosen["counted"] is not None and chosen["counted"] != chosen["frames"]:
            warnings.append("%s: -show_frames 帧数 %d ≠ -count_frames %s" %
                            (os.path.basename(path), chosen["frames"], chosen["counted"]))
        fmts = sorted(chosen["uniq"])
        rec["decoded_pix"] = " + ".join("%s×%d" % (f, chosen["uniq"][f]) for f in fmts)
        rec["nframes"] = chosen["frames"]
        class_of, notes = {}, []
        for f in fmts:
            d = pix.desc(f) if pix and pix.ok else None
            if d is None:
                failures.append("%s: 无法从 avutil 取 %s 的描述符 ⇒ 分类不可信"
                                % (os.path.basename(path), f))
            c, nt = classify(d, f)
            class_of[c] = class_of.get(c, 0) + chosen["uniq"][f]
            notes.append("%s→%s" % (f, nt))
        rec["desc"] = pix.desc(fmts[0]) if (pix and pix.ok and fmts) else None
        rec["class"] = list(class_of)[0] if len(class_of) == 1 else "unknown"
        if len(class_of) > 1:
            notes.insert(0, "文件内格式不唯一（%s）⇒ 不硬给单一分类" %
                         "/".join(sorted(class_of)))
        rec["note"] = "; ".join(notes)
        rec["agree"] = ("MIXED" if len(fmts) > 1 else
                        ("SAME" if (rec["decl_pix"] or "") == fmts[0] else "DIVERGE"))
        if rec["agree"] == "DIVERGE":
            warnings.append("%s: 流头声明 %s ≠ 解码帧 %s（分类按解码帧）" %
                            (os.path.basename(path), rec["decl_pix"], fmts[0]))
        if rec["class"] == "unknown":
            warnings.append("%s: 分类落 unknown" % os.path.basename(path))
        rec["costs"] = costs_for(costs, path)
        measured.append(rec)

    # ---- 4. 输出 -----------------------------------------------------------
    out_dir = os.path.dirname(os.path.abspath(args.out))
    os.makedirs(out_dir, exist_ok=True)
    scratch_dir = args.scratch or os.path.join(out_dir, "scratch")
    os.makedirs(scratch_dir, exist_ok=True)

    L = []
    A = L.append
    A("# 动图解码格式清单（lane-W1e · A1 决策的事实底座）")
    A("")
    A("判定对象：动图**解码帧**能否原样（不经 `sws_scale`）上传进 D3D11 纹理。")
    A("渲染器今天的纹理由**位深**单因子决定（`kernel/FFF.Native/3FP/Render/VideoRenderer.cpp:3091`：")
    A("`inputLayout==0` 时 `bitDepth<=8 ⇒ B8G8R8A8_UNORM`，否则 `R16G16B16A16_UNORM`）。")
    A("8-bit 只有 `B8G8R8A8_UNORM`(内存 B,G,R,A) 与 `R8G8B8A8_UNORM`(内存 R,G,B,A) 两个候选，")
    A("所以决定权在**解码器交出来的内存字节序**，而字节序是内容相关的（有/无 alpha）。")
    A("")
    A("## 0 归因：用了哪套工具/库")
    A("")
    A("| 项 | 值 |")
    A("| --- | --- |")
    A("| 样本目录 | `%s` |" % shq(args.samples_dir))
    A("| ffprobe | `%s` |" % shq(args.probe))
    A("| ffprobe 版本行 | `%s` |" % (prov or "N/A"))
    A("| ffmpeg | `%s` |" % shq(args.ffmpeg))
    A("| ffmpeg 版本行 | `%s` |" % (ffv or "N/A"))
    for d in dlls:
        A("| avcodec（数值 id 取自此库） | `%s` version=%s sha256[0:16]=%s |"
          % (shq(d["path"]), d["version"] or "N/A", d["sha256_16"] or "N/A"))
    A("| avutil（像素描述符取自此库） | `%s` version=%d.%d.%d |" % (
        shq(avutil_path or "-"), (pix.version >> 16) if pix else 0,
        ((pix.version >> 8) & 0xFF) if pix else 0, (pix.version & 0xFF) if pix else 0))
    A("| codec_id.h（内核随附头） | `%s` |" % shq(args.codec_header))
    A("| 审计日志目录（只读引证） | `%s` |" % shq(args.perf_dir))
    A("| 本次子进程调用次数 | %d |" % r.calls)
    A("")
    A("## 1 清单")
    A("")
    A("| 文件 | demuxer | codec | codec_id 头/外部/内核 | 声明 pix_fmt | **实测解码 pix_fmt**(帧数) | 内存序·位深 | 分类 | 声明↔实测 | 今天的成本 ms/帧 |")
    A("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for rec in measured:
        name = shq(rec["path"]).split("/")[-1]
        if not rec.get("exists"):
            A("| `%s` | — | — | — | — | **缺失** | — | unknown | — | 未记录 |" % name)
            continue
        dllcol = "/".join(str(rec.get("codec_id_dll", {}).get("dll%d" % i))
                          for i in range(len(dlls)))
        if rec.get("costs"):
            cells = []
            for c in rec["costs"]:
                tag = ("（呈现帧=%s）" % c["presented"]
                       if c.get("presented") not in (None, "0") else "（未呈现）")
                cells.append("`%s` @%s%s" % (c["ms"], c["log"], tag))
            cost_cell = "<br>".join(cells)
        else:
            cost_cell = "未记录"
        A("| `%s` | %s | %s | %s/%s | %s | **%s**(%s) | %s | `%s` | %s | %s |" % (
            name, rec.get("demuxer"), rec.get("codec"),
            rec.get("codec_id_header"), dllcol,
            rec.get("decl_pix"), rec.get("decoded_pix"), rec.get("nframes"),
            rec.get("note", ""), rec.get("class", "unknown"),
            rec.get("agree", "-"), cost_cell))
    A("")
    A("> 分类口径（先深后浅，逐条唯一）：`位深>8 ⇒ 10bit-or-deeper`；"
      "`非 RGB/索引/位流 ⇒ needs-swizzle`；`8bit packed RGB + alpha` 才谈 BGRA/RGBA 直通；"
      "`8bit packed RGB 无 alpha（rgb24/bgr24/rgb0…）⇒ needs-swizzle`；"
      "`YCbCr 族 ⇒ YUV-can-use-directYuv`。内存序与位深不是查表得来，"
      "是从被链接的 libavutil 像素描述符（`comp[].offset/depth/flags`）读出，"
      "再与 swscale `rawvideo` 逐字节实测交叉验证（见 §3）。")
    A("")

    counts = {c: 0 for c in CLASSES}
    for rec in measured:
        counts[rec.get("class", "unknown")] = counts.get(rec.get("class", "unknown"), 0) + 1
    if len(measured) != len(listed):
        failures.append("枚举到 %d 个文件但只测量了 %d 个 ⇒ 清单被悄悄截断"
                        % (len(listed), len(measured)))
    if sum(counts.values()) != len(measured):
        failures.append("分类合计 %d ≠ 文件数 %d" % (sum(counts.values()), len(measured)))
    A("## 2 分类统计（样本数 %d，逐类相加 = %d）" % (len(measured), sum(counts.values())))
    A("")
    A("| 分类 | 数量 | 文件 |")
    A("| --- | --- | --- |")
    for c in CLASSES:
        files = [shq(x["path"]).split("/")[-1] for x in measured if x.get("class") == c]
        A("| `%s` | %d | %s |" % (c, counts.get(c, 0), ", ".join("`%s`" % f for f in files) or "—"))
    A("")
    n_bgra = counts.get("verbatim-BGRA", 0)
    n_rgba = counts.get("verbatim-RGBA", 0)
    n_other = len(measured) - n_bgra - n_rgba
    A("### 2.1 对 A1（原样上传）的直接含义")
    A("")
    A("* 今天只有 `B8G8R8A8_UNORM` 一个 8-bit 出口 ⇒ 能**逐字节原样上传**的只有 "
      "`verbatim-BGRA` = **%d/%d**。" % (n_bgra, len(measured)))
    A("* 若再加 `R8G8B8A8_UNORM` 分支（内存 R,G,B,A）⇒ 上限升到 **%d/%d**。" % (
        n_bgra + n_rgba, len(measured)))
    A("* 剩下 **%d** 个无论如何都躲不掉 CPU 转换：%s。" % (
        n_other, ", ".join("`%s`(%s)" % (shq(x["path"]).split("/")[-1], x.get("class"))
                           for x in measured
                           if x.get("class") not in ("verbatim-BGRA", "verbatim-RGBA")) or "无"))
    apng_fmts = {}
    for x in measured:
        if x.get("codec") == "apng":
            apng_fmts.setdefault(x.get("decoded_pix"), []).append(
                shq(x["path"]).split("/")[-1])
    if len(apng_fmts) > 1:
        A("* **格式是内容相关的，不能按容器/解码器名推断**：同为 `apng` 解码器，"
          "交出的解码格式随有无 alpha 分裂为 %s。" % "; ".join(
              "`%s`←%d 个(%s)" % (k, len(v), ", ".join("`%s`" % n for n in sorted(v)))
              for k, v in sorted(apng_fmts.items())))
    A("")

    A("## 3 内存序实测：swscale rawvideo 逐字节 vs libavutil 描述符")
    A("")
    A("源色 `%s`（R=%s G=%s B=%s）；1×1 帧输出的原始字节即内存序。" % (
        PROBE_COLOR, "0x20", "0xA0", "0xE0"))
    A("")
    A("| pix_fmt | 实测内存序 | 描述符内存序 | 原始字节 | 判定 |")
    A("| --- | --- | --- | --- | --- |")
    for b in byte_rows:
        A("| `%s` | %s | %s | `%s` | %s |" % (b["fmt"], b["observed"],
                                              b["descriptor"], b["bytes"], b["verdict"]))
    A("")
    A("> 判定 = 逐个分量比对**描述符给出的字节偏移**与**实测落在该偏移上的分量**；"
      "描述符里的 `.` 表示该字节不是任何已定义分量（填充位，值不由规范保证，"
      "故不参与比对——`rgb0/bgr0/0rgb/0bgr` 的直通资格因此被否）。"
      "`0rgb/0bgr` 实测首字节 0xff 是 alpha 位缺失时的不透明填充，不代表有 alpha 语义。")
    A("")

    A("## 4 codec_id 一致性（内核对账头 ↔ 外部 ffprobe 所用库 ↔ 内核 runtime 库）")
    A("")
    A("| codec | codec_id.h | " +
      " | ".join("dll#%d `%s`" % (i, shq(d["path"])) for i, d in enumerate(dlls)) +
      " | 判定 |")
    A("| --- | --- | " + " | ".join("---" for _ in dlls) + " | --- |")
    for row in parity_rows:
        cells = [str(row.get("header"))] + [str(row.get("dll%d" % i))
                                            for i in range(len(dlls))]
        present = [c for c in cells if c != "None"]
        if len(present) == len(cells) and len(set(present)) == 1:
            verdict = "一致"
        elif len(set(present)) > 1:
            verdict = "**不一致**"
        else:
            verdict = "n/a（该名在这份库里不存在）"
        A("| %s | " % row["name"] + " | ".join("`%s`" % c for c in cells) +
          " | %s |" % verdict)
    A("")
    A("逐个库与头的全量比对：%s；不一致合计 **%d** 条。" % (
        "；".join("dll#%d 覆盖 **%d** 个同名 codec" % (di, cnt) for di, cnt in per_dll_cmp)
        or "一个库都没比对成功", len(full_mismatch)))
    for a, b, n, diff in dll_vs_dll:
        A("两份库直接比对 `%s` vs `%s`：共同 %d 个名字，差异 %d。" % (a, b, n, len(diff)))
    A("头解析自检：枚举 %d 项、别名 %d 项、未能解析的行 %d 项；哨兵 "
      "`AV_CODEC_ID_FIRST_AUDIO=%s`（应 65536）、`AV_CODEC_ID_PROBE=%s`（应 102400）。" % (
          len(header_order), len(header_aliases), len(header_unparsed),
          header_vals.get("AV_CODEC_ID_FIRST_AUDIO"), header_vals.get("AV_CODEC_ID_PROBE")))
    for v in ver_rows:
        A("库版本对账 `%s`：随附头声明 **%s**，实际 DLL **%s** ⇒ %s" % (
            v["lib"], v["header"], "/".join(v["dll"]) or "取不到",
            "同源" if all(g == v["header"] for g in v["dll"]) and v["dll"]
            else "**不同源**（编号仍逐名一致，见上）"))
    A("")

    A("## 5 成本引证（今天的代码路径为每个文件付了多少）")
    A("")
    A("只从 `%s` 既有日志原样摘取，每条带日志名；不同日志来自不同内核构建，" % shq(args.perf_dir) +
      "**不做平均、不互相换算**。`转换=` 即内核自计的每帧 CPU 转换墙钟。")
    A("")
    A("| 文件 | 转换 ms/帧 | 出处日志 | 呈现帧 | 状态 |")
    A("| --- | --- | --- | --- | --- |")
    for rec in measured:
        if not rec.get("costs"):
            A("| `%s` | 未记录 | — | — | — |" % shq(rec["path"]).split("/")[-1])
            continue
        for c in rec["costs"]:
            A("| `%s` | %s | `%s` | %s | %s |" % (shq(rec["path"]).split("/")[-1],
                                                  c["ms"], c["log"],
                                                  c["presented"] or "-", c["state"] or "-"))
    A("")

    A("## 6 每文件明细（多视频流的文件：动图流 = 解出帧最多的那条）")
    A("")
    A("| 文件 | 视频流数 | 动图流 v:idx | 各流实测（帧数:格式） | 解码路 B 色彩/量程 |")
    A("| --- | --- | --- | --- | --- |")
    for rec in measured:
        if not rec.get("per_stream"):
            continue
        detail = "; ".join("v%d=%d:%s" % (s["v"], s["frames"],
                                          ",".join("%s×%d" % (k, v)
                                                   for k, v in sorted(s["uniq"].items())) or "—")
                           for s in rec["per_stream"])
        pb = rec["primary"].get("pathB") or {}
        A("| `%s` | %s | v%s | %s | %s / %s |" % (
            shq(rec["path"]).split("/")[-1], rec.get("n_streams"),
            rec["primary"]["v"], detail, pb.get("csp", "-"), pb.get("range", "-")))
    A("")

    if skipped:
        A("目录里按扩展名过滤掉的文件（未解码）：%s" %
          ", ".join("`%s`" % shq(x).split("/")[-1] for x in skipped))
        A("")
    A("## 7 判定与牙齿")
    A("")
    A("* 两路独立解码实测（ffprobe `-show_frames` 逐帧 / ffmpeg `pixfmt:` 定档行）"
      "在每个文件的动图流上必须一致。")
    A("* 流头声明与解码帧不一致的文件已在「声明↔实测」列标 `DIVERGE`，分类一律按**解码帧**。")
    A("* 硬失败条件：文件缺失 / 解出 0 帧 / 两路实测不一致 / 每个文件的 codec_id 三方不齐 / "
      "codec_id 逐库全量比对不足 %d 条或有差异 / 内存序交叉验证不一致 / 枚举数≠测量数 / "
      "工具不可运行。" % MIN_PARITY_COMPARED)
    A("")
    A("## 8 本轮没有验证的事（不要拿来当结论）")
    A("")
    A("* **没有**验证 D3D11 真能绑定这些 YUV 布局：`YUV-can-use-directYuv` 只说明"
      "「解码器交的是 YCbCr，不是 RGB 字节序问题」；`yuva420p` 是 4 平面（含独立 alpha 面），"
      "DXGI 没有对应的四平面 YUV+alpha 纹理格式，能否走 `DXVA2_VideoProcessor`/`AYUV` 是另一道题。")
    A("* **没有**测「省掉 sws_scale 之后到底省多少」：原样上传仍要一次 `Map`/`UpdateSubresource` "
      "搬运，本清单只证明**能不能**逐字节对上，不证明净收益。")
    A("* **没有**覆盖 >10bit / 16bit / float / HDR 动图夹具（只有一份 10-bit AVIF）。")
    A("* **没有**重测成本列：§5 的数字全部来自历史构建留下的日志，同一文件在不同日志里差 2 倍以上"
      "（如 `anim_4k_apng.png` 3.35~7.93），不能当同一次 A/B。")
    A("* **没有**验证内核编译时**链接**的是哪份 avcodec（`third_party/ffmpeg/lib/x64/*.lib` "
      "是导入库，`kernel/runtime/*.dll` 是出货件）；本节只证明「随附头 = 出货 DLL」的 "
      "codec_id 编号一致，以及两侧 DLL 的库版本与头的关系（见 §4 末）。")
    A("* **没有**验证色彩量程/矩阵是否也一并免掉：`rgb24`/`yuva420p` 帧的 `csp/range` 见 §6，"
      "量程不匹配时仍需处理。")
    A("* 清单的**文件集 = 目录枚举（按扩展名）+ 显式夹具**；目录内容会随其他 lane 投放新夹具而变，"
      "复现时以本文 §0 的目录与 §1 的行目为准。")
    A("")
    if failures:
        A("## 9 硬失败（%d）" % len(failures))
        A("")
        for f in failures:
            A("* FAIL %s" % f)
        A("")
    if warnings:
        A("## %d 警告（%d）" % (10 if failures else 9, len(warnings)))
        A("")
        for w in warnings:
            A("* WARN %s" % w)
        A("")

    doc = "\n".join(L) + "\n"
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(doc)
    print(doc)

    # 原始测量落盘，便于复算/复核
    with open(os.path.join(scratch_dir, "measure.json"), "w", encoding="utf-8") as fh:
        json.dump({"probe": args.probe, "probe_version": prov, "ffmpeg": args.ffmpeg,
                   "ffmpeg_version": ffv, "dlls": [{k: v for k, v in d.items()
                                                    if k != "map"} for d in dlls],
                   "parity_per_dll": per_dll_cmp, "parity_mismatch": full_mismatch,
                   "header_entries": len(header_order), "header_unparsed": header_unparsed,
                   "byte_rows": byte_rows, "measured": measured,
                   "failures": failures, "warnings": warnings},
                  fh, ensure_ascii=False, indent=1, sort_keys=True, default=str)
    if failures:
        print("EXIT 1: %d 项硬失败" % len(failures))
        return 1
    if args.strict and warnings:
        print("EXIT 1 (--strict): %d 项警告" % len(warnings))
        return 1
    print("EXIT 0: 解码帧计数 %d/%d 个文件全部 ≥1 帧；codec_id 与头逐库比对 %s 零差异；"
          "内存序交叉验证通过。" % (
              len([m for m in measured if m.get("nframes")]), len(listed),
              "/".join(str(cnt) for _, cnt in per_dll_cmp)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
