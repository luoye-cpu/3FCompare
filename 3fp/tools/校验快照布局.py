#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校准宿主 原生播放器快照 与 C 侧 FFF3FPSnapshot 的字段布局。

## 为什么需要

2026-10-07 发现宿主结构体把 IAMF 三字段**追加到了末尾**，而 C 侧它们在
第 26-28 位（紧跟 decodedAudioFrames）。字段顺序错不会编译报错，只会
**静默读出错位的值** —— 其后约 30 个字段全部偏移，UI 显示的是别的字段内容。

本脚本把两边的字段序列都解析出来逐位比对，把"顺序错"变成**可见的失败**。

## 检查内容

1. 字段数量一致
2. 逐字段类型/宽度一致（由 C 类型映射到 VB 类型）
3. 计算出的结构体总大小一致（含对齐）

用法：
    python tools/校验快照布局.py            # 校验，不一致则退出码 1
    python tools/校验快照布局.py --verbose  # 打印完整对照表
"""

import os
import re
import sys

ROOT = r"C:\PLAN\3FCompare\3fp"
API_HEADER = os.path.join(ROOT, "kernel", "FFF.Native", "3FP", "Api", "FFF.Player.Api.h")
INTEROP_VB = os.path.join(ROOT, "kernel", "FFF.Player", "Interop", "播放器原生接口.vb")

# C 类型 -> (VB 类型, 字节数)。枚举按 4 字节处理（与 C 侧一致）。
C_TYPE_MAP = {
    "std::uint32_t": ("UInteger", 4),
    "std::int32_t": ("Integer", 4),
    "std::uint64_t": ("ULong", 8),
    "std::int64_t": ("Long", 8),
    "void*": ("IntPtr", 8),
    "const char*": ("IntPtr", 8),
}
# 枚举类型（FFF3FPState 等）统一 4 字节
ENUM_BYTES = 4

# VB 类型 -> 字节数（x64）
VB_TYPE_BYTES = {
    "UInteger": 4, "Integer": 4,
    "ULong": 8, "Long": 8,
    "IntPtr": 8,
}


def parse_c_snapshot(path):
    """返回 [(字段名, C类型, 字节数)]。"""
    text = open(path, encoding="utf-8", errors="replace").read()
    match = re.search(r"struct FFF3FPSnapshot\s*\{(.*?)\n\s*\};", text, re.S)
    if not match:
        raise SystemExit("找不到 FFF3FPSnapshot 定义")
    fields = []
    for line in match.group(1).split("\n"):
        line = line.strip()
        if not line or line.startswith("//"):
            continue
        # 形如 `std::uint32_t size;` 或 `FFF3FPState state;`
        m = re.match(r"^([\w:<>*\s]+?)\s+(\w+)(?:\[(\d+)\])?;", line)
        if not m:
            continue
        ctype, name = m.group(1).strip(), m.group(2)
        if ctype in C_TYPE_MAP:
            vb, size = C_TYPE_MAP[ctype]
        else:
            vb, size = "UInteger", ENUM_BYTES
        fields.append((name, ctype, vb, size))
    return fields


def parse_vb_snapshot(path):
    """返回 [(VB字段名, VB类型, 字节数)]。"""
    text = open(path, encoding="utf-8", errors="replace").read()
    match = re.search(r"Friend Structure 原生播放器快照(.*?)End Structure", text, re.S)
    if not match:
        raise SystemExit("找不到 原生播放器快照 定义")
    fields = []
    for line in match.group(1).split("\n"):
        line = line.strip()
        if line.startswith("'") or not line:
            continue
        m = re.match(r"^Public (\S+) As (\w+)$", line)
        if not m:
            continue
        name, vbtype = m.group(1), m.group(2)
        if vbtype not in VB_TYPE_BYTES:
            raise SystemExit("未知的 VB 类型：%s（字段 %s）" % (vbtype, name))
        fields.append((name, vbtype, VB_TYPE_BYTES[vbtype]))
    return fields


def struct_size(sizes, alignment=8):
    offset = 0
    max_align = 1
    for size in sizes:
        align = min(size, alignment)
        max_align = max(max_align, align)
        offset = (offset + align - 1) // align * align
        offset += size
    offset = (offset + max_align - 1) // max_align * max_align
    return offset


def main():
    verbose = "--verbose" in sys.argv

    c_fields = parse_c_snapshot(API_HEADER)
    vb_fields = parse_vb_snapshot(INTEROP_VB)

    print("C  侧 FFF3FPSnapshot   : %d 字段" % len(c_fields))
    print("宿主 原生播放器快照     : %d 字段" % len(vb_fields))

    problems = []
    if len(c_fields) != len(vb_fields):
        problems.append("字段数不一致：C=%d 宿主=%d" % (len(c_fields), len(vb_fields)))

    limit = min(len(c_fields), len(vb_fields))
    for i in range(limit):
        cname, ctype, cvb, csize = c_fields[i]
        vname, vtype, vsize = vb_fields[i]
        if cvb != vtype:
            problems.append("#%d 类型不符：C %s(%s) vs 宿主 %s(%s)" % (i + 1, cname, cvb, vname, vtype))
        elif csize != vsize:
            problems.append("#%d 宽度不符：C %s vs 宿主 %s" % (i + 1, csize, vsize))

    c_total = struct_size([f[3] for f in c_fields])
    vb_total = struct_size([f[2] for f in vb_fields])
    print("C  侧 sizeof : %d 字节" % c_total)
    print("宿主 sizeof  : %d 字节" % vb_total)
    if c_total != vb_total:
        problems.append("结构体大小不一致：C=%d 宿主=%d" % (c_total, vb_total))

    if verbose:
        print("\n%4s  %-34s %-20s %-34s %s" % ("#", "C 字段", "C 类型", "宿主字段", "宿主类型"))
        for i in range(max(len(c_fields), len(vb_fields))):
            c = c_fields[i] if i < len(c_fields) else ("-", "-", "-", 0)
            v = vb_fields[i] if i < len(vb_fields) else ("-", "-", 0)
            flag = "" if (c[2] == v[1]) else "  <<<"
            print("%4d  %-34s %-20s %-34s %-10s%s" % (i + 1, c[0], c[1], v[0], v[1], flag))

    if problems:
        print("\n布局不一致（%d 项）：" % len(problems))
        for p in problems[:20]:
            print("  · %s" % p)
        if len(problems) > 20:
            print("  … 另有 %d 项" % (len(problems) - 20))
        print("\n提示：字段顺序错**不会**编译报错，只会静默读到错位的值。")
        print("      请按 C 侧顺序重排 原生播放器快照，而不是把新字段追加到末尾")
        print("      （除非 C 侧本身也是追加在末尾）。")
        return 1

    print("\n布局一致 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
