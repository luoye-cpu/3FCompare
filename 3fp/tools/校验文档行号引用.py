#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查文档里的行号引用是否已经漂移。

## 为什么需要

文档里大量使用 `HdrProcessor.cpp:245` 这种引用。**代码一改，这些行号就静默失效** ——
不会报错，只会让读者跳到错误的行，读到无关的代码并据此形成错误判断。

2026-10-08 实际发生过：docs/59/60/61 引用的 `HdrProcessor.cpp` 行号**整体偏移 +17 行**，
而 docs/60 §3.3.4 等处的引用被后续实现者当成"当前事实"使用。

## 判据（不是简单比对行号，那没有意义）

行号变了不代表引用错了 —— 关键是**那一行现在是不是它声称的东西**。
所以本工具的判定方式是：把引用文本 `路径:行号` 取出来，
再看**该行实际内容**与引用附近的**描述性上下文**是否还吻合。

由于无法自动理解自然语言，本工具做**可判定的那一半**：

1. **文件是否存在**（路径写错/文件改名 ⇒ 必错）
2. **行号是否越界**（文件变短 ⇒ 必错）
3. **该行是否为有意义的位置** —— 是否命中函数定义/关键语句，
   而不是空行、纯注释、或 `};` 这类不承载语义的行
4. 输出**该行的实际内容**，供人一眼判断是否仍需修正

最后一项让"漂移"变得**可见且可复核**，而不是等它误导下一个人。

用法：
    python tools/校验文档行号引用.py              # 摘要
    python tools/校验文档行号引用.py --verbose    # 逐条列出
    python tools/校验文档行号引用.py --only 60    # 只看 docs/60
    python tools/校验文档行号引用.py --all       # 三条清单都全量打印（门禁排障用）

覆盖面（2026-10-08 扩，原 `DOCS=3fp/docs` 写死）：
    · 3fp/docs/*.md      —— 3FP 内核侧账本
    · docs/*.md          —— 主仓文档（顶层，archive/ 不扫）
    · README.md / CHANGELOG.md / tools/README.md —— 根与工具索引
    主仓那 736 处引用此前**一行都不检查**，"缺文件/越界"两类硬失败静默放行。
"""

import io
import os
import re
import sys

ROOT = r"C:\PLAN\3FCompare\3fp"
# 被引用的源文件可能散落在整个工作区：内核在 3fp/ 下，
# 而 3FCompare 对比工具在 3fc-followhand/ 与 src/ 下（文档 58/64 引用的就是后者）。
WORKSPACE = os.path.dirname(ROOT)

# 扫描面 = (树名, 目录, 顶层通配 | 显式文件列表)。只取目录的**顶层** .md，
# archive/ 之类的归档子目录不回扫（归档件的行号属历史快照，扫了只会制造噪声）。
DOC_SETS = [
    ("3fp/docs", os.path.join(ROOT, "docs"), None),
    ("docs", os.path.join(WORKSPACE, "docs"), None),
    ("root", WORKSPACE, ["README.md", "CHANGELOG.md"]),
    ("tools", os.path.join(WORKSPACE, "tools"), ["README.md"]),
]


def collect_docs(only=None):
    """返回 [(树名/相对显示路径, 绝对路径)]，按树名+文件名排序。"""
    found = []
    for tree, base, names in DOC_SETS:
        if not os.path.isdir(base):
            continue
        if names is None:
            entries = sorted(f for f in os.listdir(base) if f.endswith(".md"))
        else:
            entries = [f for f in names if os.path.isfile(os.path.join(base, f))]
        for f in entries:
            if only and not f.startswith(only):
                continue
            found.append(("%s/%s" % (tree, f), os.path.join(base, f)))
    return found

# 引用形如 `Foo.cpp:123`、`Foo.h:123-456`、`3FP/Hdr/Foo.cpp:123`
REF_PATTERN = re.compile(r"([A-Za-z0-9_./\\-]+\.(?:cpp|h|hpp|c|cs|vb)):(\d+)(?:\s*[-–]\s*(\d+))?")

# 兜底索引只收这些扩展名（与 REF_PATTERN 一致），避免把整仓文件读进内存。
INDEX_EXTS = {"cpp", "h", "hpp", "c", "cs", "vb"}

# 在哪些目录里找被引用的源文件（按顺序尝试）。
# `src` 排在 `3fc-followhand` 之前：后者是 v0.2.5 时代的旧副本，
# 同名文件（`MainWindow.*.cs`）拿它当目标会把"现行行号"读成历史行号。
SOURCE_ROOTS = [
    os.path.join(ROOT, "kernel", "FFF.Native"),
    os.path.join(ROOT, "kernel", "FFF.Player"),
    ROOT,
    os.path.join(WORKSPACE, "src"),
    os.path.join(WORKSPACE, "tools"),
    os.path.join(WORKSPACE, "third_party"),
    os.path.join(WORKSPACE, "_3fc_tmp"),
    os.path.join(WORKSPACE, "3fc-followhand"),
    WORKSPACE,
]

# 全局按文件名兜底查找时，跳过这些体积大且无关的目录。
# 注意两处刻意**不跳过**：
#   · third_party —— 文档大量引用 FFmpeg/libiamf 头文件，那些引用同样会漂移
#   · _3fc_tmp    —— 规范取证副本（如 JXRGlue.h）放在这里，docs/70 引用它
# 只跳过真正的构建产物目录，以及**点开头的缓存/快照目录**（`.3fc_dumps`、
# `.qoder`、`.vs` 这些整棵树都是别处的副本，拿它当"现行代码"会把行号读歪）。
SKIP_DIRS = {".git", "node_modules", "bin", "obj", "publish", "build", "x64",
             ".vs", "packages", "testmedia", "kit", "3fc-backups", "samples",
             "_quarantine_20260924"}


def _skip_dir(name):
    return name in SKIP_DIRS or name.startswith(".")

# 已知不在本仓、且**引用本身是合理的**文件：不视为缺陷。
# 每条都要写明为什么可以缺席，避免这里变成"消音名单"。
KNOWN_EXTERNAL = {
    # Windows SDK 头；文档引用的是系统错误码常量
    "winerror.h": "Windows SDK 头文件，不随本仓分发",
    # libiamf 的上游测试源码，未随库分发；文档用它说明参考实现的输出格式
    "test_iamfdec.c": "libiamf 上游测试源码，未随库分发",
    # 文档 27 是「可行性规划」，该编码器**尚未实现**，引用的是规划目标
    "JpegGainMapEncoder.cs": "尚未实现的规划目标（docs/27 为可行性规划）",
    # 以下三条 2026-10-08 由主仓文档扩面时逐条登记，各带取证位置（不并入"整体忽略缺文件"）
    # docs/50 §符号补丁口径、CHANGELOG 的 SYMBOL_INFOW/Tag 对账都按本机 SDK 头核对
    "dbghelp.h": "Windows SDK 调试头（本机 `Windows Kits/10/Include/10.0.28000.0/um/dbghelp.h`），不随本仓分发",
    # docs/09 记录的 coreclr 启动路径取证，属 .NET 运行时上游源码
    "ceemain.cpp": "coreclr 上游源码（`dotnet/runtime`），本机只有二进制，不随本仓分发",
}


def find_source(rel_path):
    """把引用里的相对路径解析成真实文件；找不到返回 None。"""
    rel_path = rel_path.replace("\\", "/").lstrip("./")
    # 写全了"从仓库根算起"的引用，必须**先精确命中它**。
    # 否则下面"剥掉前缀逐级试"的逻辑会把 third_party/…/HdrProcessor.cpp 解析到
    # 3fp/kernel 下的同名文件（两棵树有几十个同名源文件、行数不同），
    # 于是一条完全正确的引用被判成"该文件只有 578 行 ⇒ 越界"。
    # 这不是理论风险：SOURCE_ROOTS[0] 就是 3fp/kernel，先到先得。
    exact = os.path.join(WORKSPACE, rel_path.replace("/", os.sep))
    if os.path.isfile(exact):
        return exact
    # 先按原样试，再按"去掉前缀目录"逐级试（文档里常省略 3FP/ 之类的中间目录）
    candidates = [rel_path]
    parts = rel_path.split("/")
    for i in range(1, len(parts)):
        candidates.append("/".join(parts[i:]))
    for root in SOURCE_ROOTS:
        for cand in candidates:
            full = os.path.join(root, cand.replace("/", os.sep))
            if os.path.isfile(full):
                return full
    # 最后退化为按文件名全局查找（一次建索引，不是每条引用重走一遍全仓）
    return build_name_index().get(os.path.basename(rel_path))


_NAME_INDEX = None


def build_name_index():
    """{文件名: 绝对路径}，只收文档可能引用的源码扩展名。

    按 SOURCE_ROOTS 的顺序插入、先到先得 ⇒ 同名文件永远解析到**优先级最高的那棵树**
    （现行 src/3FCompare 赢过 3fc-followhand 的 v0.2.5 旧副本）。
    """
    global _NAME_INDEX
    if _NAME_INDEX is not None:
        return _NAME_INDEX
    index = {}
    for root in SOURCE_ROOTS:
        if not os.path.isdir(root):
            continue
        for dirpath, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if not _skip_dir(d)]
            for f in files:
                if f.rpartition(".")[2] in INDEX_EXTS and f not in index:
                    index[f] = os.path.join(dirpath, f)
    _NAME_INDEX = index
    return index


def classify_line(text):
    """判断某一行是否"承载语义"。空行/纯注释/孤立大括号视为不承载。"""
    stripped = text.strip()
    if not stripped:
        return False, "空行"
    if stripped in ("{", "}", "};", ")", ");", "},"):
        return False, "孤立括号"
    if stripped.startswith(("//", "/*", "*", "'''", "Rem ", "'")):
        return False, "纯注释"
    return True, ""


def main():
    verbose = "--verbose" in sys.argv
    # 硬失败（缺文件 / 越界）逐条可见：截断会让人以为"只有这几条"，
    # 而门禁是按下条数判红的。--all 只是把打印上限抬到实际条数。
    cap = 10 ** 6 if "--all" in sys.argv else 15
    warn_cap = 10 ** 6 if "--all" in sys.argv else 25
    only = None
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1]

    docs = collect_docs(only)

    total_refs = 0
    missing_file, out_of_range, empty_line, external = [], [], [], []
    checkable = 0
    per_doc = []

    for doc_name, doc_path in docs:
        text = io.open(doc_path, encoding="utf-8", errors="replace").read()
        refs = list(REF_PATTERN.finditer(text))
        if not refs:
            continue
        doc_stat = {"doc": doc_name, "refs": 0, "bad": 0}

        for m in refs:
            rel, start, end = m.group(1), int(m.group(2)), m.group(3)
            src = find_source(rel)
            if src is None:
                if os.path.basename(rel) in KNOWN_EXTERNAL:
                    external.append((doc_name, rel, KNOWN_EXTERNAL[os.path.basename(rel)]))
                else:
                    missing_file.append((doc_name, rel, start))
                    doc_stat["bad"] += 1
                continue

            try:
                lines = io.open(src, encoding="utf-8", errors="replace").read().split("\n")
            except OSError:
                missing_file.append((doc_name, rel, start))
                doc_stat["bad"] += 1
                continue

            doc_stat["refs"] += 1
            total_refs += 1
            if start > len(lines):
                out_of_range.append((doc_name, rel, start, len(lines)))
                doc_stat["bad"] += 1
                continue

            meaningful, why = classify_line(lines[start - 1])
            checkable += 1
            if not meaningful:
                empty_line.append((doc_name, rel, start, why, lines[start - 1].strip()[:70]))
                doc_stat["bad"] += 1
            elif verbose:
                print("  %-46s %s:%d -> %s" % (
                    doc_name[:44], os.path.relpath(src, WORKSPACE).replace("\\", "/"), start,
                    lines[start - 1].strip()[:64]))

        if doc_stat["refs"] or doc_stat["bad"]:
            per_doc.append(doc_stat)

    def count_of(tree_prefix, bucket):
        return sum(1 for item in bucket if item[0].startswith(tree_prefix + "/"))

    print("文档行号引用校验")
    print("  扫描文档          : %d 篇" % len(docs))
    print("  引用总数          : %d" % (total_refs + len(missing_file)))
    print("  可定位            : %d" % checkable)
    print("  源文件找不到      : %d" % len(missing_file))
    print("  已声明为仓外/未实现: %d" % len(external))
    print("  行号越界          : %d" % len(out_of_range))
    print("  指向无语义行      : %d" % len(empty_line))
    for tree, _base, _names in DOC_SETS:
        print("  ├ %-10s 引用 %3d / 缺文件 %2d / 越界 %2d / 无语义 %3d" % (
            tree,
            sum(d["refs"] for d in per_doc if d["doc"].startswith(tree + "/")),
            count_of(tree, missing_file), count_of(tree, out_of_range),
            count_of(tree, empty_line)))

    if missing_file:
        print("\n[源文件找不到]")
        for doc, rel, ln in missing_file[:cap]:
            print("  %-44s %s:%d" % (doc[:42], rel, ln))
        if len(missing_file) > cap:
            print("  … 另有 %d 条" % (len(missing_file) - cap))

    if external:
        print("\n[仓外或未实现 —— 已声明，不计为缺陷]")
        for doc, rel, why in external:
            print("  %-44s %s（%s）" % (doc[:42], rel, why))

    if out_of_range:
        print("\n[行号越界]")
        for doc, rel, ln, size in out_of_range[:cap]:
            print("  %-44s %s:%d（该文件只有 %d 行）" % (doc[:42], rel, ln, size))
        if len(out_of_range) > cap:
            print("  … 另有 %d 条" % (len(out_of_range) - cap))

    if empty_line:
        print("\n[指向无语义行 —— 多半已漂移，需人工确认]")
        for doc, rel, ln, why, content in empty_line[:warn_cap]:
            print("  %-40s %s:%-5d %s | %s" % (doc[:38], os.path.basename(rel), ln, why, content))
        if len(empty_line) > warn_cap:
            print("  … 另有 %d 条" % (len(empty_line) - warn_cap))

    print("\n说明：'指向无语义行'不等于引用已失效 —— 但它是**最可疑的一类**，")
    print("      应当人工确认该行号现在指的是不是文档声称的东西。")
    print("      文档引用建议改为**按符号名**（如 HdrProcessor::ProcessFrame），")
    print("      行号只作辅助，这样重构后不会静默失效。")

    # 有越界或文件缺失即视为硬失败；无语义行只警告。
    return 1 if (missing_file or out_of_range) else 0


if __name__ == "__main__":
    sys.exit(main())
