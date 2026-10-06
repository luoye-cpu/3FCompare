#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
inject_metadata.py —— 给样张注入 ffmpeg 写不出的元数据

为什么需要它（2026-09-24 实测）：
  · ffmpeg 的 png **编码器**没有 cICP / iCCP 选项 ⇒ `-color_primaries smpte432` 被静默丢弃，
    输出与不带该参数时**逐字节相同**（实测 02_display_p3.png 与 01b_srgb.png 同为 212953 B）。
    ⇒ Display P3 样张必须后处理注入 cICP 块。
  · ffmpeg 的 mjpeg **编码器**不写 EXIF ⇒ `-metadata:s:v rotate=90` 无效
    （实测 03_exif_orientation6.jpg 与 01c 同为 116846 B）。
    ⇒ EXIF Orientation=6 样张必须后处理注入 APP1 段。
  · ffmpeg **没有 tiff muxer**（`-f tiff` 报 "Requested output format 'tiff' is not known"），
    `-f image2 -update 1` 只落最后一页 ⇒ 多页 TIFF 由本脚本直接合成（baseline 未压缩）。

用法：
  python inject_metadata.py cicp   <in.png>  <out.png>  <primaries> <transfer> [matrix] [full_range]
  python inject_metadata.py exif   <in.jpg>  <out.jpg>  <orientation>
  python inject_metadata.py tiff   <out.tiff> <in1.png> [in2.png ...]
"""

import struct
import sys
import zlib


# ── PNG：在 IHDR 之后插入 cICP 块 ──────────────────────────────────────────────
def png_insert_cicp(src, dst, primaries, transfer, matrix=0, full_range=1):
    with open(src, "rb") as fh:
        data = fh.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit("不是 PNG 文件: %s" % src)

    def chunk(ctype, payload):
        return (struct.pack(">I", len(payload)) + ctype + payload
                + struct.pack(">I", zlib.crc32(ctype + payload) & 0xFFFFFFFF))

    out = bytearray(data[:8])
    pos = 8
    inserted = False
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        ctype = data[pos + 4:pos + 8]
        whole = data[pos:pos + 8 + length + 4]
        if ctype == b"cICP":                      # 已有就跳过（幂等）
            inserted = True
        out += whole
        pos += 8 + length + 4
        if ctype == b"IHDR" and not inserted:
            out += chunk(b"cICP", bytes([primaries, transfer, matrix, full_range]))
            inserted = True
        if ctype == b"IEND":
            break
    if not inserted:
        raise SystemExit("PNG 里找不到 IHDR，未注入")
    with open(dst, "wb") as fh:
        fh.write(out)
    return len(out)


# ── JPEG：在 SOI 之后插入 EXIF APP1（含 IFD0 的 Orientation）───────────────────
def jpeg_insert_exif(src, dst, orientation):
    with open(src, "rb") as fh:
        data = fh.read()
    if data[:2] != b"\xff\xd8":
        raise SystemExit("不是 JPEG 文件: %s" % src)

    # TIFF 头（little-endian）+ IFD0（1 个条目：Orientation）
    tiff = bytearray()
    tiff += b"II" + struct.pack("<H", 42) + struct.pack("<I", 8)
    tiff += struct.pack("<H", 1)                                  # 条目数
    tiff += struct.pack("<HHI", 0x0112, 3, 1) + struct.pack("<HH", orientation, 0)
    tiff += struct.pack("<I", 0)                                  # 无下一个 IFD

    payload = b"Exif\x00\x00" + bytes(tiff)
    app1 = b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload

    # 跳过 SOI 后已有的 APPn 段，避免重复
    pos = 2
    while pos + 4 <= len(data) and data[pos] == 0xFF and 0xE0 <= data[pos + 1] <= 0xEF:
        seg = struct.unpack(">H", data[pos + 2:pos + 4])[0]
        if data[pos + 1] == 0xE1 and data[pos + 4:pos + 10] == b"Exif\x00\x00":
            break
        pos += 2 + seg

    out = data[:pos] + app1 + data[pos:]
    with open(dst, "wb") as fh:
        fh.write(out)
    return len(out)


# ── TIFF：合成多页 baseline（未压缩 RGB）───────────────────────────────────────
def make_multipage_tiff(dst, sources):
    """把若干 8bit RGB PNG 合成一个多页 TIFF。

    ffmpeg 无 tiff muxer ⇒ 手工按 baseline TIFF 规范写 IFD 链。
    仅支持 RGB24、无压缩（Compression=1），足够作为"多页"样张。
    """
    pages = []
    for p in sources:
        w, h, rgb = _read_png_rgb24(p)
        pages.append((w, h, rgb))

    out = bytearray()
    out += b"II" + struct.pack("<H", 42)
    ifd0_ptr_pos = len(out)
    out += struct.pack("<I", 0)                 # 占位：IFD0 偏移
    next_ifd_offset = 0

    def add_ifd(w, h, data_off, data_len, next_off):
        """写一个 IFD，返回它的偏移；额外数据（BitsPerSample 数组）紧随其后。"""
        n_tags = 10
        ifd_off = len(out)
        out.extend(struct.pack("<H", n_tags))
        extra_off = ifd_off + 2 + 12 * n_tags + 4

        def tag(tid, typ, count, value_or_off):
            out.extend(struct.pack("<HHI", tid, typ, count) + value_or_off)

        LONG, SHORT = 4, 3
        bps_off = extra_off                     # BitsPerSample[3] 放这里
        tag(256, LONG, 1, struct.pack("<I", w))                    # ImageWidth
        tag(257, LONG, 1, struct.pack("<I", h))                    # ImageLength
        tag(258, SHORT, 3, struct.pack("<I", bps_off))             # BitsPerSample
        tag(259, SHORT, 1, struct.pack("<HH", 1, 0))               # Compression=none
        tag(262, SHORT, 1, struct.pack("<HH", 2, 0))               # Photometric=RGB
        tag(273, LONG, 1, struct.pack("<I", data_off))             # StripOffsets
        tag(277, SHORT, 1, struct.pack("<HH", 3, 0))               # SamplesPerPixel
        tag(278, LONG, 1, struct.pack("<I", h))                    # RowsPerStrip
        tag(279, LONG, 1, struct.pack("<I", data_len))             # StripByteCounts
        tag(282, SHORT, 1, struct.pack("<HH", 72, 1))              # XResolution
        # 上面是 10 个 tag；XResolution 用 SHORT 简化表示，规范上应为 RATIONAL，
        # 但读取端（ffmpeg/Windows）对多页判断只依赖 IFD 链与 StripOffsets。
        out.extend(struct.pack("<I", next_off))                    # NextIFD
        # BitsPerSample 数组
        out.extend(struct.pack("<HHH", 8, 8, 8))
        return ifd_off

    prev_ifd_pos = ifd0_ptr_pos
    for idx, (w, h, rgb) in enumerate(pages):
        # 先把像素数据写到末尾（保持 4 字节对齐）
        while len(out) % 4:
            out.append(0)
        data_off = len(out)
        out += rgb
        data_len = len(rgb)

        # 预留 IFD 空间（先写占位，再回填）
        ifd_off = len(out)
        out += b"\x00" * (2 + 12 * 10 + 4 + 6)
        # 回填上一个 IFD 的 NextIFD
        struct.pack_into("<I", out, prev_ifd_pos, ifd_off)
        # 写真正的 IFD 内容
        saved = len(out)
        del out[ifd_off:]
        add_ifd(w, h, data_off, data_len, 0)
        # 保证长度一致
        while len(out) < saved:
            out.append(0)
        prev_ifd_pos = ifd_off + 2 + 12 * 10

    # 修正第一个 IFD 的偏移占位
    with open(dst, "wb") as fh:
        fh.write(out)
    return len(out)


def _read_png_rgb24(path):
    """极简 PNG 读取：只支持 8bit RGB（color type 2）非隔行。"""
    with open(path, "rb") as fh:
        data = fh.read()
    pos = 8
    w = h = None
    idat = bytearray()
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        ctype = data[pos + 4:pos + 8]
        payload = data[pos + 8:pos + 8 + length]
        if ctype == b"IHDR":
            w, h, depth, color = struct.unpack(">IIBB", payload[:10])
            if depth != 8 or color != 2:
                raise SystemExit("只支持 8bit RGB PNG（得到 depth=%d color=%d）: %s"
                                 % (depth, color, path))
        elif ctype == b"IDAT":
            idat += payload
        elif ctype == b"IEND":
            break
        pos += 8 + length + 4

    raw = zlib.decompress(bytes(idat))
    stride = w * 3
    out = bytearray()
    prev = bytearray(stride)
    p = 0
    for _ in range(h):
        ftype = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        for i in range(stride):                    # 反滤波
            a = line[i - 3] if i >= 3 else 0
            b = prev[i]
            c = prev[i - 3] if i >= 3 else 0
            if ftype == 1:
                line[i] = (line[i] + a) & 0xFF
            elif ftype == 2:
                line[i] = (line[i] + b) & 0xFF
            elif ftype == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 0xFF
            elif ftype == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out += line
        prev = line
    return w, h, bytes(out)


def main(argv):
    if len(argv) < 2:
        raise SystemExit(__doc__)
    cmd = argv[1]
    if cmd == "cicp":
        src, dst = argv[2], argv[3]
        prim, trc = int(argv[4]), int(argv[5])
        matrix = int(argv[6]) if len(argv) > 6 else 0
        fr = int(argv[7]) if len(argv) > 7 else 1
        n = png_insert_cicp(src, dst, prim, trc, matrix, fr)
        print("cICP 注入完成: %s (%d B)" % (dst, n))
    elif cmd == "exif":
        src, dst, ori = argv[2], argv[3], int(argv[4])
        n = jpeg_insert_exif(src, dst, ori)
        print("EXIF 注入完成: %s (%d B)" % (dst, n))
    elif cmd == "tiff":
        dst = argv[2]
        n = make_multipage_tiff(dst, argv[3:])
        print("多页 TIFF 合成完成: %s (%d B, %d 页)" % (dst, n, len(argv) - 3))
    else:
        raise SystemExit("未知子命令: %s" % cmd)


if __name__ == "__main__":
    main(sys.argv)
