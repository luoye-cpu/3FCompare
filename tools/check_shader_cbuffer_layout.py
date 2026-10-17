#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""着色器 cbuffer 布局判据：C++ 镜像与 HLSL 声明必须**逐元素**对上。

为什么需要这一层
----------------
`DynamicShaderSettings`（VideoRenderer.h）与 HLSL 里的 `cbuffer DynamicSettings`
（VideoRenderer.cpp 内嵌字符串）靠两份手写声明保持一致，编译器**不会**替我对账：
cbuffer 是按字节读的，字段名对不上、顺序错了、填充错了，都是"编译通过、跑起来像素错了"。

已经存在的读数只到"行数"为止（9 行 = 144 字节）。行数相同而**元素落点不同**是可能的，
而且真的发生了：把行主序 3x3 矩阵写成扁平 `float m[9]`，紧跟在 4 个 uint 后面时，
m[3] 落在 TransformMatrix0.w —— 着色器只读 `.xyz`，于是那个元素被丢掉，
第二、三行整体错一位。字节数没错，行数没错，画面错。

本判据量两侧的一手事实：
  · HLSL 侧：把内嵌 HLSL 抽出来交给 fxc `/Fc`（汇编清单），读它自己打印的每个成员偏移；
  · C++ 侧：把 struct 声明**逐字节原样**抄进一个临时编译单元，用 cl 编出来打印 offsetof。
两边都由工具链裁决，不由我的算术裁决。struct 里的字段名清单是我从声明里解析的——
解析不到某个成员就判红（fail-closed），不会静少比一项。

正控制（缺一不可，否则本次读数不作数）
  · fxc 必须同时报出 b0 `Settings`（改动前就在的缓冲）；
  · ST 2094 那批字段（enabled/windowBlend/sourcePeak/targetPeak/windowMin0..windowMax2）
    必须逐个对上——它们对上，才证明我这台对账机器是活的；
  · offsetof 读数必须包含 struct 的 sizeof，且与 static_assert 的 144 相等。

用法：
    py tools/check_shader_cbuffer_layout.py            # 两棵树都查
    py tools/check_shader_cbuffer_layout.py --tree tp  # 只查出货树
退出码：0 全部对上；1 有任何不一致或读数拿不到；2 用法/工具链缺失。
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

TREES = {
    "tp": dict(
        label="出货树 third_party/fff_project",
        header=os.path.join(REPO, "third_party", "fff_project", "FFF.Native",
                            "3FP", "Render", "VideoRenderer.h"),
        cpp=os.path.join(REPO, "third_party", "fff_project", "FFF.Native",
                         "3FP", "Render", "VideoRenderer.cpp"),
        tlog=os.path.join(REPO, "third_party", "fff_project", "FFF.Native",
                          "obj", "x64", "Release", "FFF.Native.tlog"),
    ),
    "kf": dict(
        label="内核树 3fp/kernel",
        header=os.path.join(REPO, "3fp", "kernel", "FFF.Native",
                            "3FP", "Render", "VideoRenderer.h"),
        cpp=os.path.join(REPO, "3fp", "kernel", "FFF.Native",
                         "3FP", "Render", "VideoRenderer.cpp"),
        tlog=os.path.join(REPO, "3fp", "kernel", "FFF.Native",
                          "obj", "x64", "Release", "FFF.Native.tlog"),
    ),
}

STRUCT_NAME = "DynamicShaderSettings"
CBUFFER_NAME = "DynamicSettings"

# HLSL 成员 -> C++ 成员。改名过的历史字段（HLSL DynamicEnabled vs C++ enabled）
# 靠这张表对起来；表里没有的 HLSL 矩阵成员单独按"逐元素"规则处理。
SCALARS = [
    # (hlsl, cpp, 是否正控制)
    ("DynamicEnabled", "enabled", True),
    ("DynamicWindowCount", "windowCount", True),
    ("DynamicFlags", "flags", True),
    ("WindowBlend", "windowBlend", True),
    ("DynamicSourcePeak", "sourcePeak", True),
    ("DynamicTargetPeak", "targetPeak", True),
    ("DynamicPad0", "pad0", True),
    ("WindowMin0", "windowMin0", True),
    ("WindowMax0", "windowMax0", True),
    ("WindowMin1", "windowMin1", True),
    ("WindowMax1", "windowMax1", True),
    ("WindowMin2", "windowMin2", True),
    ("WindowMax2", "windowMax2", True),
    ("TransformEnabled", "transformEnabled", False),
    ("TransformDomain", "transformDomain", False),
    ("TransformLutBound", "transformLutBound", False),
    ("TransformPad0", "transformPad0", False),
]
MATRIX_HLSL = ["TransformMatrix0", "TransformMatrix1", "TransformMatrix2"]
MATRIX_CPP = "outputMatrixRows"      # C++ 侧的矩阵字段名前缀（[3][4] 或扁平 [9]）


def find_fxc() -> str | None:
    hits = glob.glob(r"C:\Program Files (x86)\Windows Kits\10\bin\*\x64\fxc.exe")
    if not hits:
        hits = glob.glob(r"C:\Program Files\Windows Kits\10\bin\*\x64\fxc.exe")
    if not hits:
        return None
    def key(p):
        v = re.search(r"\\(\d+\.\d+\.\d+\.\d+)\\", p)
        return [int(x) for x in v.group(1).split(".")] if v else [0]
    return sorted(hits, key=key)[-1]


def toolchain(tlog: str):
    """从内核构建的 CL.read.1.tlog 里拿 MSVC/SDK 路径（与 build_probe.py 同源做法）。"""
    path = os.path.join(tlog, "CL.read.1.tlog")
    if not os.path.exists(path):
        return None, None
    raw = open(path, "rb").read()
    blob = (raw.decode("utf-16", errors="replace") if raw[:2] in (b"\xff\xfe", b"\xfe\xff")
            else raw.decode("utf-8-sig", errors="replace")).replace("\x00", "")
    msvc = re.search(r"([A-Z]:\\[^\";\r\n]*?\\VC\\Tools\\MSVC\\[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    sdk = re.search(r"Windows Kits\\10\\Include\\([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    return (msvc.group(1).strip() if msvc else None, sdk.group(1).strip() if sdk else None)


DECLARATION = re.compile(r'constexpr\s+const\s+char\*\s+(\w+)\s*=\s*R"\((.*?)\)";', re.DOTALL)


def extract_hlsl(cpp_path: str) -> str:
    text = open(cpp_path, encoding="utf-8-sig", errors="replace").read()
    sources = dict(DECLARATION.findall(text))
    if "PixelShaderSource" not in sources:
        raise RuntimeError("VideoRenderer.cpp 里找不到 PixelShaderSource（抽取模式失配）")
    return sources["PixelShaderSource"]


def struct_body(header_path: str) -> str:
    text = open(header_path, encoding="utf-8-sig", errors="replace").read()
    m = re.search(r"struct\s+%s\s*\{(.*?)\n\};" % STRUCT_NAME, text, re.DOTALL)
    if not m:
        raise RuntimeError("%s 里找不到 struct %s" % (header_path, STRUCT_NAME))
    return m.group(1)


def declarators(body: str):
    """从 struct 正文解析 (字段名, 声明文本)。逗号分隔的多声明符会拆开。"""
    # 去注释，按 ; 切声明（数组维度里不含 ;，初始化列表里不含顶层 ;，故安全）
    body = re.sub(r"//[^\n]*", "", body)
    body = re.sub(r"/\*.*?\*/", "", body, flags=re.S)
    out = []
    for decl in body.split(";"):
        decl = decl.strip()
        if not decl:
            continue
        # 每个声明可能是 `type a = {}, b[2] = {}`：按顶层逗号再拆
        depth = 0
        parts, cur = [], ""
        for ch in decl:
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            if ch == "," and depth == 0:
                parts.append(cur)
                cur = ""
            else:
                cur += ch
        parts.append(cur)
        type_split = parts[0].split(None, 1)
        if len(type_split) < 2:
            continue                      # 只剩类型名（末尾空声明）
        base, head = type_split
        for piece in [head] + parts[1:]:
            # 允许任意个维度组（`float m[3][4] = {}`），维度里可能为空。
            name = re.search(r"([A-Za-z_]\w*)((\s*\[[^\]]*\])*)\s*(=.*)?$", piece.strip())
            if not name:
                continue
            arr = re.findall(r"\[([^\]]*)\]", piece)
            out.append((name.group(1), base, arr))
    return out


CPP_TEMPLATE = """#include <cstddef>
#include <cstdint>
#include <cstdio>

struct %(struct_name)s {
%(body)s
};

static size_t g_size = sizeof(%(struct_name)s);

#define P(n) printf("%%s %%zu %%zu\\n", #n, offsetof(%(struct_name)s, n), \\
                    sizeof(((const %(struct_name)s*)0)->n))
int main() {
    printf("sizeof %%zu\\n", g_size);
%(prints)s
    return 0;
}
"""


def cpp_offsets(tree: dict, workdir: str):
    body = struct_body(tree["header"])
    fields = declarators(body)
    if not fields:
        raise RuntimeError("struct 正文解析出 0 个字段（解析器失配，不能读成「干净」）")
    names = [n for n, _, _ in fields]
    src = os.path.join(workdir, "layout_probe.cpp")
    exe = os.path.join(workdir, "layout_probe.exe")
    text = CPP_TEMPLATE % dict(
        struct_name=STRUCT_NAME,
        body=body,
        prints="\n".join("    P(%s);" % n for n in names))
    with open(src, "w", encoding="utf-8", newline="\n") as h:
        h.write(text)

    msvc, sdk = toolchain(tree["tlog"])
    if not msvc or not sdk:
        raise RuntimeError("找不到工具链（tlog=%s）——先构建该树的内核" % tree["tlog"])
    cl = os.path.join(msvc, "bin", "Hostx64", "x64", "cl.exe")
    env = dict(os.environ)
    sdk_inc = r"C:\Program Files (x86)\Windows Kits\10\Include\%s" % sdk
    env["INCLUDE"] = ";".join([os.path.join(msvc, "include"),
                               os.path.join(sdk_inc, "ucrt"),
                               os.path.join(sdk_inc, "shared"),
                               os.path.join(sdk_inc, "um")])
    env["LIB"] = ";".join([os.path.join(msvc, "lib", "x64"),
                           os.path.join(r"C:\Program Files (x86)\Windows Kits\10\Lib\%s" % sdk,
                                        "ucrt", "x64"),
                           os.path.join(r"C:\Program Files (x86)\Windows Kits\10\Lib\%s" % sdk,
                                        "um", "x64")])
    env["PATH"] = os.path.join(msvc, "bin", "Hostx64", "x64") + os.pathsep + env.get("PATH", "")
    r = subprocess.run([cl, "/nologo", "/std:c++17", "/EHsc", "/W3", src, "/Fe:" + exe,
                        "/Fo:" + os.path.join(workdir, "layout_probe.obj")],
                       env=env, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(exe):
        raise RuntimeError("cl 编译失败：\n%s\n%s" % (r.stdout, r.stderr))
    run = subprocess.run([exe], capture_output=True, text=True)
    if run.returncode != 0:
        raise RuntimeError("布局探针运行失败 rc=%d" % run.returncode)
    offsets, size = {}, None
    for line in run.stdout.splitlines():
        line = line.strip()
        if line.startswith("sizeof"):
            size = int(line.split()[1])
        else:
            parts = line.split()
            if len(parts) == 3:
                offsets[parts[0]] = (int(parts[1]), int(parts[2]))
    if size is None or not offsets:
        raise RuntimeError("探针输出解析不到 sizeof 或字段（读数缺失≠布局正确）")
    return offsets, size, fields


def hlsl_offsets(hlsl: str, fxc: str, workdir: str):
    src = os.path.join(workdir, "shader.hlsl")
    with open(src, "w", encoding="utf-8", newline="\n") as h:
        h.write(hlsl)
    listing = os.path.join(workdir, "shader.asm.txt")
    obj = os.path.join(workdir, "shader.cso")
    r = subprocess.run([fxc, "/nologo", "/T", "ps_5_0", "/E", "main",
                        "/Fc", listing, "/Fo", obj, src],
                       capture_output=True, text=True)
    if not os.path.exists(listing):
        raise RuntimeError("fxc 没写出清单：\n%s\n%s" % (r.stdout, r.stderr))
    text = open(listing, encoding="ascii", errors="ignore").read()
    # 清单里每个 cbuffer 成员形如：
    #   //   float4 TransformMatrix0;           // Offset:   96 Size:    16
    rows = {}
    current = None
    for line in text.splitlines():
        m = re.search(r"//\s*cbuffer\s+(\w+)", line)
        if m:
            current = m.group(1)
            rows[current] = {}
            continue
        if current:
            if re.match(r"^//\s*\}", line):
                current = None
                continue
            m = re.search(r"//\s*[\w<>]+\s+([A-Za-z_]\w*)\s*;\s*//\s*Offset:\s*(\d+)\s+Size:\s*(\d+)",
                          line)
            if m:
                rows[current][m.group(1)] = (int(m.group(2)), int(m.group(3)))
    return rows


def matrix_element_offsets(fields, offsets):
    """按 C++ 声明**自己的形状**给出行主序 3x3 的 9 个元素落点。

    [3][4]  ⇒ 元素 e 在 base + (e/3)*sizeof(row) + (e%3)*4（每行补到 16 字节）
    [9]/[3][3] ⇒ 元素 e 在 base + 4*e（紧凑，无行填充）
    其他形状一律判不了 —— 抛错，不猜。
    """
    for name, base_type, dims in fields:
        if not name.startswith(MATRIX_CPP) and name != "outputMatrix":
            continue
        if base_type != "float":
            raise RuntimeError("矩阵字段 %s 类型不是 float（%s）——规则不覆盖" % (name, base_type))
        off, size = offsets[name]
        if len(dims) == 2:
            r, c = int(dims[0] or 3), int(dims[1] or 3)
            if r != 3 or c not in (3, 4):
                raise RuntimeError("矩阵字段 %s 形状 %dx%d：必须是 3 行、每行 3 或 4 个 float"
                                   % (name, r, c))
            row_bytes = size // r
            return name, "%d 行 x %d float（row pitch %d B）" % (r, c, row_bytes), {
                e: off + (e // 3) * row_bytes + (e % 3) * 4 for e in range(9)}
        if len(dims) == 1:
            if size != 36:
                raise RuntimeError("扁平矩阵字段 %s sizeof=%d，不是 9 个 float" % (name, size))
            return name, "flat", {e: off + 4 * e for e in range(9)}
        raise RuntimeError("矩阵字段 %s 没有数组维度，无法定位 9 个元素" % name)
    return None, None, None


def check(tree_key: str, fxc: str, workdir: str) -> int:
    tree = TREES[tree_key]
    print("== %s (%s)" % (tree["label"], tree_key))
    for path in (tree["header"], tree["cpp"]):
        if not os.path.exists(path):
            print("   [FAIL] 文件不存在：%s" % path)
            return 1
    try:
        offsets, size, fields = cpp_offsets(tree, workdir)
        rows = hlsl_offsets(extract_hlsl(tree["cpp"]), fxc, workdir)
    except RuntimeError as ex:
        print("   [FAIL] %s" % ex)
        return 1

    if CBUFFER_NAME not in rows:
        print("   [FAIL] fxc 清单里找不到 cbuffer %s；看见的是 %s"
              % (CBUFFER_NAME, sorted(rows) or "NONE"))
        return 1
    if "Settings" not in rows:
        print("   [FAIL] 正控制失败：连 b0 'Settings' 都没报出来，这份清单不能作数")
        return 1
    if not rows["Settings"]:
        print("   [FAIL] 正控制失败：b0 'Settings' 解析到 0 个成员——清单解析器是死的")
        return 1
    dy = rows[CBUFFER_NAME]
    if not dy:
        print("   [FAIL] %s 解析到 0 个成员（读数缺失≠布局正确，判红）" % CBUFFER_NAME)
        return 1
    print("   C++ sizeof=%d；HLSL %s 成员 %d 个（b0 Settings 成员 %d 个，正控制在位）"
          % (size, CBUFFER_NAME, len(dy), len(rows["Settings"])))

    fails, controls = [], 0
    for hl, cp, is_control in SCALARS:
        if hl not in dy:
            fails.append("HLSL 里没有 %s" % hl)
            continue
        if cp not in offsets:
            fails.append("C++ 里没有 %s" % cp)
            continue
        ho, hs = dy[hl]
        co, cs = offsets[cp]
        if ho != co or hs != cs:
            fails.append("%s/%s：HLSL @%d size %d ≠ C++ @%d size %d" % (hl, cp, ho, hs, co, cs))
        else:
            controls += 1

    name, shape, elems = matrix_element_offsets(fields, offsets)
    if not elems:
        fails.append("解析不到 C++ 侧矩阵字段（名字前缀 %s / outputMatrix）" % MATRIX_CPP)
    else:
        for hl in MATRIX_HLSL:
            if hl not in dy:
                fails.append("HLSL 里没有 %s" % hl)
        if not any("HLSL 里没有" in f for f in fails):
            read = {}
            for i, hl in enumerate(MATRIX_HLSL):
                base, width = dy[hl]
                if width != 16:
                    fails.append("%s Size=%d，不是 float4 行" % (hl, width))
                for j in range(3):
                    read[3 * i + j] = base + 4 * j
            bad = [(e, read[e], elems[e]) for e in range(9) if read[e] != elems[e]]
            print("   C++ 矩阵字段 %s 形状=%s @%d；着色器逐格读点 vs C++ 元素落点：%s"
                  % (name, shape, elems[0],
                     "9/9 对上" if not bad else "%d/9 对上，%d 个错" % (9 - len(bad), len(bad))))
            for e, want, got in bad:
                fails.append("矩阵元素 m%d：着色器从 @%d 读，C++ 把它放在 @%d" % (e, want, got))
                print("      ✗ m%d  shader@%-4d  c++@%-4d" % (e, want, got))

    print("   标量/数组成员对账：%d/%d 对上（含正控制）" % (controls, len(SCALARS)))
    if fails:
        for f in fails:
            print("   [RED] %s" % f)
        print("   判定：%s 布局不一致" % tree["label"])
        return 1
    print("   判定：%s C++ 镜像与 HLSL 声明逐元素一致" % tree["label"])
    return 0


def main(argv) -> int:
    ap = argparse.ArgumentParser(description="着色器 cbuffer 布局逐元素判据")
    ap.add_argument("--tree", action="append", choices=sorted(TREES),
                    help="只查内置的两棵树之一（默认两棵都查）")
    ap.add_argument("--native-dir", action="append", default=[], metavar="目录",
                    help="按 FFF.Native 目录现查：<{0}>/3FP/Render/VideoRenderer.h 与同目录的 "
                         "obj/x64/Release/FFF.Native.tlog；可多次，给发布门禁用".format("DIR"))
    a = ap.parse_args(argv)
    fxc = find_fxc()
    if not fxc:
        print("[FAIL] 找不到 fxc.exe（Windows SDK HLSL 编译器）")
        return 2
    print("fxc: %s" % fxc)
    keys = list(a.tree or [])
    for index, directory in enumerate(a.native_dir):
        key = "arg%d" % index
        TREES[key] = dict(
            label="指定目录 " + directory,
            header=os.path.join(directory, "3FP", "Render", "VideoRenderer.h"),
            cpp=os.path.join(directory, "3FP", "Render", "VideoRenderer.cpp"),
            tlog=os.path.join(directory, "obj", "x64", "Release", "FFF.Native.tlog"))
        keys.append(key)
    if not keys:
        keys = sorted(TREES)
    rc = 0
    scratch = os.path.join(REPO, "tests", "output", "shader-cbuffer-layout")
    os.makedirs(scratch, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=scratch) as workdir:
        for key in keys:
            rc |= check(key, fxc, workdir)
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
