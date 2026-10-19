#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""FFF3FPSnapshot / FFF3FPConfiguration 跨树跨宿主配对矩阵。

Why this exists
---------------
两棵内核树（实验树 3fp/kernel、出货树 third_party/fff_project）当前
GetApiVersion 同为 20，但快照布局已经分叉：

    实验树  71 字段 / 408 B，IAMF 三字段在**尾部**（364/368/372），
            另有 videoUpload100ns(376) 与 audioDelay100ns(400)
    出货树  69 字段 / 392 B，IAMF 三字段在**中段**（136/140/144），
            没有那两个 100ns 字段

两个宿主各跟一棵树：

    VB 宿主（3fp/kernel/FFF.Player/Interop/播放器原生接口.vb）↔ 实验树
    托管镜像（src/3FCompare.Core/.../Fff3FpNative.cs）        ↔ 出货树

"版本号相等"这条配对判据拦不住布局分叉（已经错过三次），所以这里不比分包
版本号，而是把四份声明两两对撞：字段数、逐字段宽度、逐字段偏移、总尺寸。
顺序错不会编译报错，只会静默读到错位的值——2026-10-07 的 IAMF 事故和
2026-10-14 的 hdrDp 删槽事故都是这个形态。

 designated pairs（退出码只对它们负责）
----------------------------------------
    vb-host    ↔ kernel(exp)   VB 宿主跑实验内核（tools/校验快照布局.py 已单独看守）
    cs-mirror  ↔ shipped(TP)   托管宿主跑出货内核（SnapshotLayoutTests.cs 单独看守）

跨树对（vb-host↔shipped、cs-mirror↔kernel）是**已知分叉**，本脚本把它们
报成 RED-BY-DESIGN 并在汇总里单独计数：它们是"设计上就不该配对"，不是回归。
真正判红退出（exit 1）的只有 designated pair 漂移。

Usage
-----
    py -3 tools/check_snapshot_pairing_matrix.py
    py -3 tools/check_snapshot_pairing_matrix.py --verbose   # 逐字段对照表

Exit codes
----------
  0  designated pairs 全部一致（跨树分叉如实呈现，不影响退出码）
  1  任一 designated pair 漂移 —— 这是回归，必须修
  3  NOT JUDGED —— 有源文件缺失或字段类型无法解析，不出结论
"""

from __future__ import annotations

import argparse
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_NOT_JUDGED = 3

ALIGNMENT = 8

# ---------------------------------------------------------------- 源文件路径

KF_HEADER = os.path.join(REPO, "3fp", "kernel", "FFF.Native", "3FP", "Api", "FFF.Player.Api.h")
TP_HEADER = os.path.join(REPO, "third_party", "fff_project", "FFF.Native", "3FP", "Api", "FFF.Player.Api.h")
VB_INTEROP = os.path.join(REPO, "3fp", "kernel", "FFF.Player", "Interop", "播放器原生接口.vb")
CS_MIRROR = os.path.join(REPO, "src", "3FCompare.Core", "Backend", "Interop", "Fff3FpNative.cs")
PERFTEST_CS = os.path.join(REPO, "tests", "experiments", "PerfTest", "Program.cs")

# designated pairs：宿主/工具实际运行时所配的内核树。见模块 docstring。
#   vb-host   ↔ kernel(exp)  VB 宿主跑实验内核（tools/校验快照布局.py 已单独看守）
#   cs-mirror ↔ shipped(TP)  托管宿主跑出货内核（SnapshotLayoutTests.cs 单独看守）
#   perftest  ↔ shipped(TP)  PerfTest 运行前把 third_party 的 Release DLL 拷到己方目录
#                            （PerfTest/Program.cs 的 dllSrc），跑的是出货树内核
DESIGNATED = {
    ("vb-host", "kernel(exp)"),
    ("cs-mirror", "shipped(TP)"),
    ("perftest", "shipped(TP)"),
}

STRUCTS = ("snapshot", "config")

# ---------------------------------------------------------------- 类型宽度

CPP_SCALAR = {
    "std::uint32_t": 4, "uint32_t": 4, "std::int32_t": 4, "int32_t": 4,
    "float": 4, "double": 8,
    "std::uint64_t": 8, "uint64_t": 8, "std::int64_t": 8, "int64_t": 8,
}

VB_SCALAR = {
    "UInteger": 4, "Integer": 4, "ULong": 8, "Long": 8, "IntPtr": 8, "Single": 4,
}

CS_SCALAR = {
    "uint": 4, "int": 4, "float": 4, "long": 8, "ulong": 8, "nint": 8,
    # PerfTest 的镜像用 string（LPStr）与委托（函数指针）承载原生指针字段
    "string": 8, "IntPtr": 8,
}

_ENUM_DECL_RE = None  # 每个头文件各自编译，见 resolve_enum_width


def resolve_enum_width(header_text, type_name):
    """enum class X : <underlying> 的底层宽度；找不到返回 None。"""
    pattern = r"enum\s+class\s+%s\s*:\s*([\w:]+)" % re.escape(type_name)
    match = re.search(pattern, header_text)
    if not match:
        return None
    underlying = match.group(1)
    return CPP_SCALAR.get(underlying)


def cpp_field_width(header_text, type_name):
    """C++ 成员宽度：指针 8、标量查表、其余按 enum class 解析。"""
    cleaned = type_name.strip()
    if "*" in cleaned:
        return 8
    if cleaned in CPP_SCALAR:
        return CPP_SCALAR[cleaned]
    width = resolve_enum_width(header_text, cleaned)
    if width is not None:
        return width
    # 函数指针别名：`typedef void (__cdecl*)(...) NAME;` 或
    # `using NAME = void(__cdecl*)(...);`（FFF3FPEventCallback 是后者）
    if re.search(r"typedef[^;]*\b%s\s*;" % re.escape(cleaned), header_text):
        return 8
    if re.search(r"using\s+%s\s*=[^;]*\*" % re.escape(cleaned), header_text):
        return 8
    return None


def struct_size(widths):
    """按声明顺序布放 + 结构体尾对齐（与 校验快照布局.py 同规则）。"""
    offset = 0
    max_align = 1
    for width in widths:
        align = min(width, ALIGNMENT)
        max_align = max(max_align, align)
        offset = (offset + align - 1) // align * align
        offset += width
    return (offset + max_align - 1) // max_align * max_align


def field_offsets(widths):
    offsets = []
    offset = 0
    for width in widths:
        align = min(width, ALIGNMENT)
        offset = (offset + align - 1) // align * align
        offsets.append(offset)
        offset += width
    return offsets


def managed_name(native_name, snapshot=False):
    """C++ camelCase → 托管 PascalCase（与 SnapshotLayoutTests.ManagedNameOf 同规则）。
    decodeMode→Decoder 是**快照**镜像的历史别名；配置镜像用的就是 DecodeMode，
    别名只对快照生效，否则会把配置侧判成假红。"""
    if snapshot and native_name == "decodeMode":
        return "Decoder"
    return native_name[0].upper() + native_name[1:]


# ---------------------------------------------------------------- 解析器

def strip_cpp_comment(line):
    idx = line.find("//")
    return line if idx < 0 else line[:idx]


def parse_cpp_struct(header_text, struct_name):
    """返回 [(type, name, width)]；有无法解析的成员时返回 (None, 坏行)。"""
    lines = header_text.split("\n")
    start = None
    for i, line in enumerate(lines):
        if re.match(r"^\s*struct\s+%s\s*\{" % re.escape(struct_name), strip_cpp_comment(line)):
            start = i
            break
    if start is None:
        return None, "struct %s not found" % struct_name
    fields = []
    field_re = re.compile(r"^\s*(?P<type>.+?)\s+(?P<name>\w+)\s*(?:=[^;]*)?;\s*$")
    for line in lines[start + 1:]:
        bare = strip_cpp_comment(line).strip()
        if bare.startswith("};"):
            break
        if not bare:
            continue
        match = field_re.match(bare)
        if not match:
            # static_assert / 嵌套声明等非成员行直接跳过
            if bare.startswith(("static_assert", "struct", "//", "/*", "*")):
                continue
            return None, "unparsed member line in %s: %r" % (struct_name, bare)
        type_name = match.group("type").strip()
        width = cpp_field_width(header_text, type_name)
        if width is None:
            return None, "unresolved C++ type %r in %s" % (type_name, struct_name)
        fields.append((type_name, match.group("name"), width))
    if not fields:
        return None, "%s parsed zero fields" % struct_name
    return fields, None


def parse_vb_struct(text, struct_name):
    """返回 [(type, name, width)]。"""
    marker = "Friend Structure %s" % struct_name
    start = text.find(marker)
    if start < 0:
        return None, "Structure %s not found" % struct_name
    end = text.find("End Structure", start)
    if end < 0:
        return None, "Structure %s has no End Structure" % struct_name
    fields = []
    field_re = re.compile(r"^Public\s+(\S+)\s+As\s+(\w+)\s*$")
    for line in text[start:end].split("\n"):
        bare = line.strip()
        if not bare or bare.startswith("'"):
            continue
        match = field_re.match(bare)
        if not match:
            continue
        name, vb_type = match.group(1), match.group(2)
        if vb_type not in VB_SCALAR:
            return None, "unknown VB type %r (field %s)" % (vb_type, name)
        fields.append((vb_type, name, VB_SCALAR[vb_type]))
    if not fields:
        return None, "%s parsed zero fields" % struct_name
    return fields, None


def cs_field_width(text, cs_type):
    """C# 成员宽度：标量查表；委托按函数指针（8）；enum 读底层类型。"""
    cleaned = cs_type.strip()
    if cleaned in CS_SCALAR:
        return CS_SCALAR[cleaned]
    if re.search(r"delegate\s+[\w<>.,\s]+?\b%s\s*\(" % re.escape(cleaned), text):
        return 8
    match = re.search(r"enum\s+%s\s*:\s*(\w+)" % re.escape(cleaned), text)
    if match:
        underlying = {"uint": 4, "int": 4, "long": 8, "ulong": 8,
                      "ushort": 2, "short": 2, "byte": 1, "sbyte": 1}.get(match.group(1))
        if underlying is not None:
            return underlying
    return None


def parse_cs_struct(text, struct_name):
    """返回 [(type, name, width)]。兼容 internal/public/无修饰声明与属性前缀。"""
    lines = text.split("\n")
    start = None
    decl_re = re.compile(r"^(?:\w+\s+)*struct\s+%s\b" % re.escape(struct_name))
    for i, line in enumerate(lines):
        if decl_re.match(line.strip()):
            start = i
            break
    if start is None:
        return None, "struct %s not found" % struct_name
    fields = []
    field_re = re.compile(r"^(?:\[[^\]]*\]\s*)?public\s+(\w+)\s+(\w+)\s*;")
    for line in lines[start + 1:]:
        bare = line.strip()
        if bare == "}":
            break
        if not bare or bare.startswith(("///", "//")):
            continue
        match = field_re.match(bare)
        if not match:
            # 独立属性行（如 [StructLayout(...)]）等非成员行走这里
            continue
        cs_type, name = match.group(1), match.group(2)
        width = cs_field_width(text, cs_type)
        if width is None:
            return None, "unknown C# type %r (field %s)" % (cs_type, name)
        fields.append((cs_type, name, width))
    if not fields:
        return None, "%s parsed zero fields" % struct_name
    return fields, None


def read_text(path):
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read()


# ---------------------------------------------------------------- 对撞

def compare_pair(name_a, fields_a, name_b, fields_b, snapshot=False):
    """逐字段宽度 + 偏移 + 总数 + 字段名集合对撞。返回 (verdict, summary, details)。"""
    widths_a = [f[2] for f in fields_a]
    widths_b = [f[2] for f in fields_b]
    size_a = struct_size(widths_a)
    size_b = struct_size(widths_b)
    offsets_a = field_offsets(widths_a)
    offsets_b = field_offsets(widths_b)

    details = []
    verdict = "GREEN"
    if len(fields_a) != len(fields_b):
        verdict = "RED"
        details.append("字段数 %d vs %d" % (len(fields_a), len(fields_b)))
    limit = min(len(fields_a), len(fields_b))
    first_bad = None
    for i in range(limit):
        if widths_a[i] != widths_b[i]:
            if first_bad is None:
                first_bad = i
            if len(details) < 6:
                details.append("#%d 宽度 %s(%s,%dB) vs %s(%s,%dB)"
                               % (i + 1, fields_a[i][1], fields_a[i][0], widths_a[i],
                                  fields_b[i][1], fields_b[i][0], widths_b[i]))
        elif offsets_a[i] != offsets_b[i]:
            if first_bad is None:
                first_bad = i
            if len(details) < 6:
                details.append("#%d 偏移 %d vs %d（%s vs %s）"
                               % (i + 1, offsets_a[i], offsets_b[i],
                                  fields_a[i][1], fields_b[i][1]))
    if first_bad is not None:
        verdict = "RED"
    if size_a != size_b:
        verdict = "RED"
        details.append("总尺寸 %d B vs %d B" % (size_a, size_b))
    # 字段名集合对撞：宽度相同的相邻字段（如 5 个 4 B DEPRECATED 槽）只靠
    # 偏移发现不了，名字差集才能把"中段少了哪几个"摆到明面上。
    # VB 侧是中文名，无法与 C++/C# 映射，跳过（位置+宽度已由上面覆盖）。
    if "vb-host" not in (name_a, name_b):
        names_a = {managed_name(f[1], snapshot) for f in fields_a}
        names_b = {managed_name(f[1], snapshot) for f in fields_b}
        missing = sorted(names_a - names_b)
        extra = sorted(names_b - names_a)
        if missing:
            details.append("字段集合：%s 有而 %s 缺 [%s]" % (name_a, name_b, ", ".join(missing)))
        if extra:
            details.append("字段集合：%s 有而 %s 缺 [%s]" % (name_b, name_a, ", ".join(extra)))
        if missing or extra:
            verdict = "RED"
    summary = "%d 字段 / %d B" % (len(fields_a), size_a)
    return verdict, summary, details


def main(argv):
    parser = argparse.ArgumentParser(
        description="FFF3FPSnapshot/FFF3FPConfiguration 跨树跨宿主配对矩阵",
        epilog="exit 0 = designated pairs 一致, 1 = designated pair 漂移, 3 = NOT JUDGED")
    parser.add_argument("--verbose", action="store_true", help="打印逐字段对照表")
    args = parser.parse_args(argv)

    kf_text = read_text(KF_HEADER)
    tp_text = read_text(TP_HEADER)
    vb_text = read_text(VB_INTEROP)
    cs_text = read_text(CS_MIRROR)
    pt_text = read_text(PERFTEST_CS)

    sources = {}   # name -> {struct -> (fields, error)}
    not_judged = []

    def register(name, text, cpp_structs=(), vb_structs=(), cs_structs=()):
        entry = {}
        if text is None:
            not_judged.append("%s: 源文件缺失" % name)
            sources[name] = None
            return
        for key, struct_name in cpp_structs:
            fields, error = parse_cpp_struct(text, struct_name)
            entry[key] = (fields, error)
            if fields is None:
                not_judged.append("%s/%s: %s" % (name, key, error))
        for key, struct_name in vb_structs:
            fields, error = parse_vb_struct(text, struct_name)
            entry[key] = (fields, error)
            if fields is None:
                not_judged.append("%s/%s: %s" % (name, key, error))
        for key, struct_name in cs_structs:
            fields, error = parse_cs_struct(text, struct_name)
            entry[key] = (fields, error)
            if fields is None:
                not_judged.append("%s/%s: %s" % (name, key, error))
        sources[name] = entry

    register("kernel(exp)", kf_text,
             cpp_structs=(("snapshot", "FFF3FPSnapshot"), ("config", "FFF3FPConfiguration")))
    register("shipped(TP)", tp_text,
             cpp_structs=(("snapshot", "FFF3FPSnapshot"), ("config", "FFF3FPConfiguration")))
    register("vb-host", vb_text,
             vb_structs=(("snapshot", "原生播放器快照"), ("config", "原生播放器配置")))
    register("cs-mirror", cs_text,
             cs_structs=(("snapshot", "Fff3FpSnapshot"), ("config", "Fff3FpConfiguration")))
    register("perftest", pt_text,
             cs_structs=(("snapshot", "FFF3FPSnapshot"), ("config", "FFF3FPConfiguration")))

    names = ["kernel(exp)", "shipped(TP)", "vb-host", "cs-mirror", "perftest"]
    labels = {"kernel(exp)": "内核实验树", "shipped(TP)": "出货树",
              "vb-host": "VB 宿主", "cs-mirror": "托管镜像", "perftest": "PerfTest"}

    designated_failed = []
    cross_red = 0

    for struct_key in STRUCTS:
        print("=" * 78)
        print("结构体: %s" % ("FFF3FPSnapshot" if struct_key == "snapshot" else "FFF3FPConfiguration"))
        print("=" * 78)
        available = [n for n in names if sources.get(n) and sources[n].get(struct_key)
                     and sources[n][struct_key][0] is not None]
        for name in names:
            entry = sources.get(name)
            if not entry or not entry.get(struct_key) or entry[struct_key][0] is None:
                print("  %-12s NOT JUDGED" % labels[name])
                continue
            fields = entry[struct_key][0]
            widths = [f[2] for f in fields]
            print("  %-12s %3d 字段 / %3d B" % (labels[name], len(fields), struct_size(widths)))
        print()
        for i in range(len(available)):
            for j in range(i + 1, len(available)):
                a, b = available[i], available[j]
                fields_a = sources[a][struct_key][0]
                fields_b = sources[b][struct_key][0]
                verdict, summary, details = compare_pair(a, fields_a, b, fields_b,
                                                         snapshot=(struct_key == "snapshot"))
                pair = (a, b) if (a, b) in DESIGNATED else (
                    (b, a) if (b, a) in DESIGNATED else None)
                designated = pair is not None
                if verdict == "GREEN":
                    tag = "GREEN"
                elif designated:
                    tag = "RED（designated pair 漂移！）"
                    designated_failed.append("%s: %s ↔ %s %s"
                                             % (struct_key, a, b, "; ".join(details)))
                else:
                    tag = "RED（跨树分叉，预期内）"
                    cross_red += 1
                print("  %-12s ↔ %-12s %-8s %s" % (labels[a], labels[b], tag, summary))
                for line in details:
                    print("      · %s" % line)
        print()

    if args.verbose:
        for struct_key in STRUCTS:
            print("=" * 78)
            print("逐字段对照: %s" % struct_key)
            print("=" * 78)
            reference = "kernel(exp)"
            entry = sources.get(reference, {}).get(struct_key)
            if not entry or entry[0] is None:
                continue
            fields = entry[0]
            widths = [f[2] for f in fields]
            offsets = field_offsets(widths)
            print("%4s %-34s %-22s %s" % ("#", "实验树字段", "类型", "偏移"))
            for i, (type_name, field_name, width) in enumerate(fields):
                print("%4d %-34s %-22s %d" % (i + 1, field_name, type_name, offsets[i]))

    print("=" * 78)
    if not_judged:
        print("NOT JUDGED（%d 项）：" % len(not_judged))
        for line in not_judged:
            print("  · %s" % line)
        print("VERDICT: NOT JUDGED - 有源无法解析，这不是通过")
        return EXIT_NOT_JUDGED
    if designated_failed:
        print("DESIGNATED PAIR 漂移（%d 项）：" % len(designated_failed))
        for line in designated_failed:
            print("  · %s" % line)
        print("VERDICT: FAIL - 宿主与其内核树的布局不再一致")
        return EXIT_FAIL
    print("跨树分叉（预期内，不计入退出码）: %d 对" % cross_red)
    print("VERDICT: PASS - 两个 designated pair 布局一致；跨树分叉如上如实呈现")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
