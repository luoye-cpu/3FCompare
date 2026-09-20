# -*- coding: utf-8 -*-
"""最终版：主仓历史提交「错误追踪」分析。

修正要点：
1. `core.quotepath=false` —— 否则中文路径被转义成 "HANDOFF-\345..."，与 rev-list 原始 UTF-8 对不上，
   会把仍在追踪的文件误判成「已删除」。
2. 只保留 type=blob 的对象 —— rev-list --objects 会把 tag 对象也列出来（第二列是 tag 名）。
"""
import io
import os
import re
import subprocess
from collections import defaultdict

REPO = r"C:\PLAN\3FCompare"
BASE = ["git", "-C", REPO, "-c", "core.quotepath=false"]
OUT = []


def git(a):
    return subprocess.run(BASE + a, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE).stdout.decode("utf-8", "replace")


def say(s=""):
    OUT.append(s)


# ---------- 对象清单（仅 blob） ----------
lines = git(["rev-list", "--all", "--objects"]).splitlines()
cand = []
for l in lines:
    f = l.split(" ", 1)
    if len(f) == 2 and f[1]:
        cand.append((f[0], f[1]))

p = subprocess.run(BASE + ["cat-file", "--batch-check"],
                   input=("\n".join(s for s, _ in cand) + "\n").encode(),
                   stdout=subprocess.PIPE)
types, sizes = {}, {}
for line in p.stdout.decode("utf-8", "replace").splitlines():
    f = line.split()
    if len(f) >= 3:
        types[f[0]] = f[1]
        sizes[f[0]] = int(f[2])

pairs = [(s, p_) for s, p_ in cand if types.get(s) == "blob"]
say("可达对象 %d 行，其中 blob 条目 %d，去重后 blob %d 个，去重路径 %d 个"
    % (len(lines), len(pairs), len({s for s, _ in pairs}), len({p_ for _, p_ in pairs})))

path_shas = defaultdict(set)
for s, p_ in pairs:
    path_shas[p_].add(s)

tracked = set(git(["ls-files"]).splitlines())
say("当前追踪文件 %d 个" % len(tracked))
say("")

# ---------- 分类规则 ----------
RE_BUILD = re.compile(r"(^|/)(bin|obj|x64|x86|Debug|Release)(/|$)")
RE_LOCAL = re.compile(r"(^|/)(\.vs|\.idea|\.review_pr|\.3fc_dumps|\.3fc_verify[^/]*|\.verify[^/]*|logs|\.workbuddy-ai)(/|$)")
RE_MEDIA = re.compile(r"(^|/)testmedia/media(/|$)")
EXT_BIN = {".dll", ".pdb", ".exe", ".obj", ".lib", ".exp", ".ilk", ".zip", ".7z",
           ".msi", ".nupkg", ".mp4", ".mkv", ".avi", ".mov", ".so", ".a", ".o"}
EXT_ART = {".user", ".suo", ".tlog", ".log", ".tmp", ".cache"}
WHITELIST = {".3fc_kernel_baseline.bundle"}


def classify(path):
    ext = os.path.splitext(path)[1].lower()
    if path in WHITELIST:
        return None
    if RE_LOCAL.search(path):
        return "本地工作目录"
    if RE_BUILD.search(path):
        return "构建输出目录"
    if path.startswith("third_party/fff_project/"):
        return "嵌套仓库内容"
    if RE_MEDIA.search(path):
        return "测试素材"
    if ext in EXT_BIN:
        return "二进制产物"
    if ext in EXT_ART:
        return "构建中间产物"
    if ext in {".pdf", ".iso"}:
        return "大型二进制文档"
    return None


rows = []
for path, shas in path_shas.items():
    cat = classify(path)
    if not cat:
        continue
    rows.append((path, cat, len(shas), sum(sizes.get(s, 0) for s in shas),
                 path in tracked))
rows.sort(key=lambda r: (-r[3], r[0]))

say("=" * 76)
say("A. 命中「本不该入库」规则的路径")
say("=" * 76)
if not rows:
    say("  无。历史提交中不存在构建产物 / 二进制 / 素材 / 本地目录类错误追踪。")
else:
    tot = sum(r[3] for r in rows)
    say("  共 %d 个，累计 %.2f MiB" % (len(rows), tot / 1048576.0))
    by = defaultdict(lambda: [0, 0])
    for _, c, _, sz, _ in rows:
        by[c][0] += 1
        by[c][1] += sz
    for c, (n, sz) in sorted(by.items(), key=lambda kv: -kv[1][1]):
        say("    %-14s %3d 个  %8.2f MiB" % (c, n, sz / 1048576.0))
    say("")
    for path, c, nv, sz, still in rows[:20]:
        say("    %-52s %8.2f MiB  %s" % (path[:52], sz / 1048576.0,
                                         "仍追踪" if still else "已删除"))
say("")

# ---------- 100 KiB 以上文本文件（潜在误入） ----------
say("=" * 76)
say("B. 体积 >= 100 KiB 的非归档类文件（确认是否误入）")
say("=" * 76)
big = []
for path, shas in path_shas.items():
    m = max(sizes.get(s, 0) for s in shas)
    if m >= 100 * 1024:
        big.append((m, path, path in tracked))
for m, path, still in sorted(big, reverse=True)[:15]:
    say("  %7.1f KiB  %-56s %s" % (m / 1024.0, path[:56],
                                   "仍追踪" if still else "已删除"))
say("")

# ---------- churn ----------
ever = set(path_shas)
gone = sorted(ever - tracked)
say("=" * 76)
say("C. 曾入库但当前 HEAD 已不存在的路径（%d 个）" % len(gone))
say("=" * 76)
by_ext = defaultdict(list)
for p_ in gone:
    by_ext[os.path.splitext(p_)[1].lower()].append(p_)
for ext, lst in sorted(by_ext.items(), key=lambda kv: -len(kv[1]))[:10]:
    say("  %-12s %4d 个  例: %s" % (ext or "(无扩展)", len(lst), lst[0][:58]))
say("")
gs = [(p_, max(sizes.get(s, 0) for s in path_shas[p_])) for p_ in gone]
say("  体积最大的 12 个（判断是否为误入后被删）：")
for p_, sz in sorted(gs, key=lambda kv: -kv[1])[:12]:
    say("    %8.1f KiB  %s" % (sz / 1024.0, p_))
say("")

# ---------- 引入提交 ----------
say("=" * 76)
say("D. 体积最大的 8 个「已删除」路径的引入提交")
say("=" * 76)
for p_, sz in sorted(gs, key=lambda kv: -kv[1])[:8]:
    lg = git(["log", "--all", "--oneline", "--diff-filter=A", "--", p_]).splitlines()
    dl = git(["log", "--all", "--oneline", "--diff-filter=D", "--", p_]).splitlines()
    say("  %s" % p_)
    say("      引入: %s" % (lg[-1] if lg else "?"))
    say("      删除: %s" % (dl[0] if dl else "（未删除，可能仅改名）"))
say("")

txt = "\n".join(OUT)
io.open(r"C:\PLAN\3FCompare\.3fc_dumps\tracking_report3.txt", "w",
        encoding="utf-8").write(txt)
print(txt)
