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
"""

import io
import os
import re
import sys

ROOT = r"C:\PLAN\3FCompare\3fp"
DOCS = os.path.join(ROOT, "docs")
# 被引用的源文件可能散落在整个工作区：内核在 3fp/ 下，
# 而 3FCompare 对比工具在 3fc-followhand/ 与 src/ 下（文档 58/64 引用的就是后者）。
WORKSPACE = os.path.dirname(ROOT)

# 引用形如 `Foo.cpp:123`、`Foo.h:123-456`、`3FP/Hdr/Foo.cpp:123`
REF_PATTERN = re.compile(r"([A-Za-z0-9_./\\-]+\.(?:cpp|h|hpp|c|cs|vb)):(\d+)(?:\s*[-–]\s*(\d+))?")

# 在哪些目录里找被引用的源文件（按顺序尝试）
SOURCE_ROOTS = [
    os.path.join(ROOT, "kernel", "FFF.Native"),
    os.path.join(ROOT, "kernel", "FFF.Player"),
    ROOT,
    os.path.join(WORKSPACE, "3fc-followhand"),
    os.path.join(WORKSPACE, "src"),
    WORKSPACE,
]

# 全局按文件名兜底查找时，跳过这些体积大且无关的目录。
# 注意两处刻意**不跳过**：
#   · third_party —— 文档大量引用 FFmpeg/libiamf 头文件，那些引用同样会漂移
#   · _3fc_tmp    —— 规范取证副本（如 JXRGlue.h）放在这里，docs/70 引用它
# 只跳过真正的构建产物目录。
SKIP_DIRS = {".git", "node_modules", "bin", "obj", "publish", "build", "x64",
             ".vs", "packages"}

# 已知不在本仓、且**引用本身是合理的**文件：不视为缺陷。
# 每条都要写明为什么可以缺席，避免这里变成"消音名单"。
KNOWN_EXTERNAL = {
    # Windows SDK 头；文档引用的是系统错误码常量
    "winerror.h": "Windows SDK 头文件，不随本仓分发",
    # libiamf 的上游测试源码，未随库分发；文档用它说明参考实现的输出格式
    "test_iamfdec.c": "libiamf 上游测试源码，未随库分发",
    # 文档 27 是「可行性规划」，该编码器**尚未实现**，引用的是规划目标
    "JpegGainMapEncoder.cs": "尚未实现的规划目标（docs/27 为可行性规划）",
}


def find_source(rel_path):
    """把引用里的相对路径解析成真实文件；找不到返回 None。"""
    rel_path = rel_path.replace("\\", "/").lstrip("./")
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
    # 最后退化为按文件名全局查找
    base = os.path.basename(rel_path)
    for root in SOURCE_ROOTS:
        if not os.path.isdir(root):
            continue
        for dirpath, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            if base in files:
                return os.path.join(dirpath, base)
    return None


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
    only = None
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1]

    docs = sorted(f for f in os.listdir(DOCS) if f.endswith(".md"))
    if only:
        docs = [f for f in docs if f.startswith(only)]

    total_refs = 0
    missing_file, out_of_range, empty_line, external = [], [], [], []
    checkable = 0
    per_doc = []

    for doc in docs:
        doc_path = os.path.join(DOCS, doc)
        text = io.open(doc_path, encoding="utf-8", errors="replace").read()
        refs = list(REF_PATTERN.finditer(text))
        if not refs:
            continue
        doc_stat = {"doc": doc, "refs": 0, "bad": 0}

        for m in refs:
            rel, start, end = m.group(1), int(m.group(2)), m.group(3)
            src = find_source(rel)
            if src is None:
                if os.path.basename(rel) in KNOWN_EXTERNAL:
                    external.append((doc, rel, KNOWN_EXTERNAL[os.path.basename(rel)]))
                else:
                    missing_file.append((doc, rel, start))
                    doc_stat["bad"] += 1
                continue

            try:
                lines = io.open(src, encoding="utf-8", errors="replace").read().split("\n")
            except OSError:
                missing_file.append((doc, rel, start))
                doc_stat["bad"] += 1
                continue

            doc_stat["refs"] += 1
            total_refs += 1
            if start > len(lines):
                out_of_range.append((doc, rel, start, len(lines)))
                doc_stat["bad"] += 1
                continue

            meaningful, why = classify_line(lines[start - 1])
            checkable += 1
            if not meaningful:
                empty_line.append((doc, rel, start, why, lines[start - 1].strip()[:70]))
                doc_stat["bad"] += 1
            elif verbose:
                print("  %-46s %s:%d -> %s" % (
                    doc[:44], os.path.basename(rel), start, lines[start - 1].strip()[:64]))

        if doc_stat["refs"] or doc_stat["bad"]:
            per_doc.append(doc_stat)

    print("文档行号引用校验")
    print("  扫描文档          : %d 篇" % len(docs))
    print("  引用总数          : %d" % (total_refs + len(missing_file)))
    print("  可定位            : %d" % checkable)
    print("  源文件找不到      : %d" % len(missing_file))
    print("  已声明为仓外/未实现: %d" % len(external))
    print("  行号越界          : %d" % len(out_of_range))
    print("  指向无语义行      : %d" % len(empty_line))

    if missing_file:
        print("\n[源文件找不到]")
        for doc, rel, ln in missing_file[:15]:
            print("  %-44s %s:%d" % (doc[:42], rel, ln))
        if len(missing_file) > 15:
            print("  … 另有 %d 条" % (len(missing_file) - 15))

    if external:
        print("\n[仓外或未实现 —— 已声明，不计为缺陷]")
        for doc, rel, why in external:
            print("  %-44s %s（%s）" % (doc[:42], rel, why))

    if out_of_range:
        print("\n[行号越界]")
        for doc, rel, ln, size in out_of_range[:15]:
            print("  %-44s %s:%d（该文件只有 %d 行）" % (doc[:42], rel, ln, size))

    if empty_line:
        print("\n[指向无语义行 —— 多半已漂移，需人工确认]")
        for doc, rel, ln, why, content in empty_line[:25]:
            print("  %-40s %s:%-5d %s | %s" % (doc[:38], os.path.basename(rel), ln, why, content))
        if len(empty_line) > 25:
            print("  … 另有 %d 条" % (len(empty_line) - 25))

    print("\n说明：'指向无语义行'不等于引用已失效 —— 但它是**最可疑的一类**，")
    print("      应当人工确认该行号现在指的是不是文档声称的东西。")
    print("      文档引用建议改为**按符号名**（如 HdrProcessor::ProcessFrame），")
    print("      行号只作辅助，这样重构后不会静默失效。")

    # 有越界或文件缺失即视为硬失败；无语义行只警告。
    return 1 if (missing_file or out_of_range) else 0


if __name__ == "__main__":
    sys.exit(main())
