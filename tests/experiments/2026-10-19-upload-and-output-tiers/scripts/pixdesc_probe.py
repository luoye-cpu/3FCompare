# -*- coding: utf-8 -*-
"""Read the AVPixFmtDescriptor of every format in 3FP's DescribeInput table out of the
libavutil DLL the product actually links (3fp/build/avutil-61.dll), then check the
table's five fields against it.

Why this and not the header comments: pixfmt.h says P410 is "data in the high bits" and
NV16/NV20 are not, i.e. the LSB-vs-MSB question that decides sampleScale is prose. The
descriptor's comp[].shift is the machine-readable answer, and reading it from the linked
DLL also proves the vendored headers and the shipped library agree.

Self-validating: the probe first reproduces formats whose rows are already trusted in
production (yuv420p, p010le); if those come out wrong, the struct layout is mine being
wrong and every line below is invalid, so the script says PROBE-DEAD and exits 3.
"""
import ctypes
import os
import sys

DLL = r"C:\PLAN\3FCompare\3fp\build\avutil-61.dll"

FLAG_PLANAR = 1 << 2
FLAG_BITSTREAM = 1 << 0
FLAG_HWACCEL = 1 << 5
FLAG_ALPHA = 1 << 1


class Component(ctypes.Structure):
    # field order is the vendored pixdesc.h's, not a guess: plane, step, offset, shift, depth
    _fields_ = [("plane", ctypes.c_int), ("step", ctypes.c_int), ("offset", ctypes.c_int),
                ("shift", ctypes.c_int), ("depth", ctypes.c_int)]


class PixFmtDesc(ctypes.Structure):
    _fields_ = [("name", ctypes.c_char_p), ("nb_components", ctypes.c_uint8),
                ("log2_chroma_w", ctypes.c_uint8), ("log2_chroma_h", ctypes.c_uint8),
                ("flags", ctypes.c_uint64), ("comp", Component * 4)]


lib = ctypes.CDLL(DLL)
lib.av_pix_fmt_desc_get.restype = ctypes.POINTER(PixFmtDesc)
lib.av_pix_fmt_desc_get.argtypes = [ctypes.c_int]
lib.av_get_pix_fmt_name.restype = ctypes.c_char_p
lib.av_get_pix_fmt_name.argtypes = [ctypes.c_int]
lib.av_pix_fmt_count_planes.restype = ctypes.c_int
lib.av_pix_fmt_count_planes.argtypes = [ctypes.c_int]
try:
    lib.av_version_info.restype = ctypes.c_char_p
    version = lib.av_version_info().decode()
except Exception:
    version = "unknown"

# enum values are resolved by name so the probe never hard-codes a number
names = {}
for value in range(0, 400):
    n = lib.av_get_pix_fmt_name(value)
    if n:
        names[n.decode()] = value


def desc(name):
    if name not in names:
        return None
    d = lib.av_pix_fmt_desc_get(names[name])
    if not d:
        return None
    return d.contents


def layout_of(d):
    """1 = three separate planes, 2 = Y + interleaved UV, 0 = anything else (CPU route).

    The criterion is which plane each component lives in, not comp[].step: step is
    counted in *elements* and for the 16-bit formats it comes out 4, so a fixed "==2"
    test silently mislabels P010/P410/P416 as packed.
    """
    planes = [d.comp[i].plane for i in range(d.nb_components)]
    if d.nb_components < 3:
        return 0
    if planes[1] == planes[2]:
        return 2
    if len(set(planes)) == 3:
        return 1
    return 0


# --- liveness: reproduce two rows the product has always had -----------------------------
ctl = [("yuv420p", 1, 8, 1, 1, 0), ("p010le", 2, 10, 1, 1, 6)]
dead = []
for name, lay, depth, wx, hy, offset in ctl:
    d = desc(name)
    if d is None:
        dead.append("%s absent from the linked library" % name)
        continue
    got = (layout_of(d), d.comp[0].depth, d.log2_chroma_w, d.log2_chroma_h, d.comp[0].shift)
    want = (lay, depth, wx, hy, offset)
    if got != want:
        dead.append("%s got %s want %s" % (name, got, want))
if dead:
    print("PROBE-DEAD:", "; ".join(dead))
    sys.exit(3)
print("probe alive (libavutil %s, %s): yuv420p/p010le reproduce the table's trusted rows"
      % (os.path.basename(DLL), version))

# --- the rows B1 added --------------------------------------------------------------------
# name -> (layout, bitDepth, chromaWidthShift, chromaHeightShift, expected offset)
EXPECT = {
    "nv16":     (2, 8, 1, 0),
    "nv20le":   (2, 10, 1, 0),
    "nv24":     (2, 8, 0, 0),
    "p410le":   (2, 10, 0, 0),
    "p416le":   (2, 16, 0, 0),
    "yuv410p":  (1, 8, 2, 2),
    "yuv440p":  (1, 8, 0, 1),
    # neighbours already in the table, printed so a wrong new row is visible against them
    "yuv422p":  (1, 8, 1, 0),
    "yuv444p":  (1, 8, 0, 0),
    "yuv444p10le": (1, 10, 0, 0),
    "yuv444p16le": (1, 16, 0, 0),
    "yuv422p10le": (1, 10, 1, 0),
    "nv12":     (2, 8, 1, 1),
    "p210le":   (2, 10, 1, 0),
}
print("\n%-13s %-6s %-6s %-5s %-5s %-6s %-8s %s" % (
    "format", "lay", "depth", "w>>", "h>>", "planes", "offset", "verdict"))
bad = 0
for name, (lay, depth, wx, hy) in EXPECT.items():
    d = desc(name)
    if d is None:
        print("%-13s ABSENT from the linked library" % name)
        bad += 1
        continue
    got = (layout_of(d), d.comp[0].depth, d.log2_chroma_w, d.log2_chroma_h)
    planes = lib.av_pix_fmt_count_planes(names[name])
    ok = got == (lay, depth, wx, hy)
    if not ok:
        bad += 1
    offs = ",".join(str(d.comp[i].shift) for i in range(d.nb_components))
    print("%-13s %-6d %-6d %-5d %-5d %-6d %-8s %s" % (
        name, got[0], got[1], got[2], got[3], planes, offs, "ok" if ok else "*** MISMATCH want %s" % (
            (lay, depth, wx, hy),)))
    print("      %s flags=0x%x planes=%d" % (" ".join(
        "c%d{plane%d,step%d,off%d,shift%d,depth%d}" % (i, d.comp[i].plane, d.comp[i].step,
            d.comp[i].offset, d.comp[i].shift, d.comp[i].depth)
        for i in range(d.nb_components)), d.flags, planes))

# MSB-alignment witness: the two 10-bit semi-planar rows that must NOT share a sampleScale
for name in ("nv20le", "p210le", "p410le", "yuv422p10le", "yuv444p10le", "p010le"):
    d = desc(name)
    if d is None:
        continue
    msb = d.comp[0].shift > 0
    print("  %-13s depth=%d shift=%d -> %s" % (name, d.comp[0].depth, d.comp[0].shift,
        "MSB-aligned (scale 65535/65472)" if msb and d.comp[0].depth == 10 else
        ("LSB-aligned (scale 65535/1023)" if not msb and d.comp[0].depth == 10 else "n/a")))

print("\nmismatches = %d" % bad)
sys.exit(1 if bad else 0)
