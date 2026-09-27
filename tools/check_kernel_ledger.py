#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""内核本地分歧台账的哨兵（3FCompare 侧工具，检查 third_party/fff_project）。

存在的理由：`PATCHES.md` 与代码漂过两次（§五 用"符号命中数相同"把两条活补丁判成"已过时"；
§一 把上游既有的 `PlayerVideoRenderer::Redraw` 登记成"上游无、必须重放"）。这类漂移靠读文档发现不了，
所以把台账里**本来就写着的**判据抽出来自动复跑 —— 文档与代码共用同一份事实，而不是这里再抄一遍数字。

检查项：
  A 成员台账双向核对：净差异里"上游没有的 `xxx_` 成员"必须逐条出现在 §十一 的成员表里；
    反向也查：表里列了而树中已不存在的 ⇒ 红。（两条各咬一种形态：新增未登记 / 改名后账目残留。）
  B 回归探测命令复跑：按 §十一 的 ```ledger-probe``` 块逐条重数（tree 与 upstream 双向），与声明的期望数比对。
    ⇒ 树被剥掉本地补丁时，B 是第一个喊出来的（2026-09-26 实测：一次报出 16 条不一致）。
  C 重放队列一致性：`tools/patches/README.md` 的 `patch-status` 块 ↔ 目录实际文件 ↔
    `git apply --check` 反向/正向探测结果，三者必须互洽。⚠ 两条探测都带 `--check`，
    哨兵**不许**改动工作树（教训见 apply_probe 的注释）；内核树有未提交改动时反向探测可能假失败 ⇒ 记 ⚠ 不记 FAIL。
  D 覆盖缺口：净差异中"上游没有的"声明级标识符（非成员、非注释散文）若一个都不在台账文本里 ⇒ 红。
  E 每个登记项都有探测：删单条探测这种最省事的作弊由 E 拦。
  F 哨兵自身对工作树只读：进入/离开时各取一次 `FFF.Native` 工作树指纹，不等 ⇒ 红。

用法：
  py tools/check_kernel_ledger.py                 # 正常校验，PASS 退出 0
  py tools/check_kernel_ledger.py --selftest      # 自证有牙：构造三种缺陷，每种都必须判红
  py tools/check_kernel_ledger.py --upstream <sha> # 换上游锚点（默认取内核 HEAD 的第二父）
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KERNEL = os.path.join(ROOT, "third_party", "fff_project")
LEDGER = os.path.join(KERNEL, "PATCHES.md")
QUEUE_DIR = os.path.join(ROOT, "tools", "patches")

IDENT_RE = re.compile(r"\b([A-Za-z][A-Za-z0-9_]{7,})\b")
PROSE = set("""anything anywhere around because before current default during either enough
    false first following given having however inside itself later lesser little making
    mostly next once only other over overCount please rather since some still such than
    that their there these this those thus too under unless until usually what where
    which while white without world would written yellow being above below across
    against almost already always among another anyway anything are anything because
    before being below between both but came can come could does done each even every
    few first for from got had have here high how into its just large last left less
    let like line little long made make many may mean might more most much must name
    need never new next nine none no nor not now number off often once one only onto
    order other our out over own per place put quite rather real round same second see
    several shall she should side since six small so some still such take ten than
    that the their them then there these they thing third this those though three
    through too took two under until up upon us used using very want was way well
    were what when where whether which while who whom why will with within without
    would year yes yet you your across afterwards
    """.split())


def git(*args, cwd=KERNEL):
    p = subprocess.run(["git", "-C", cwd] + list(args),
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.stdout, p.returncode


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def upstream_anchor(override):
    if override:
        return override
    out, rc = git("rev-parse", "HEAD^2")
    if rc == 0:
        return out.strip()
    raise SystemExit("内核 HEAD 不是合并提交，无法自动取上游锚点；请显式 --upstream <sha>")


def net_diff(up):
    return git("diff", up, "HEAD", "--", "FFF.Native")[0]


def plus_lines(diff):
    return [l[1:] for l in diff.splitlines() if l.startswith("+") and not l.startswith("+++")]


def minus_blob(up):
    """上游整份源码拼起来，用来判'这个标识符上游有没有'。"""
    files = git("ls-tree", "-r", "--name-only", up, "--", "FFF.Native")[0].splitlines()
    return "".join(git("show", f"{up}:{f}")[0] for f in files
                   if f.endswith((".cpp", ".h", ".hpp")))


def local_only_idents(up, diff):
    up_blob = minus_blob(up)
    ids = set()
    for line in plus_lines(diff):
        if line.strip().startswith(("//", "*", "/*")):
            continue                       # 注释里的英文单词不是符号
        ids.update(IDENT_RE.findall(line))
    return {i for i in ids if i not in up_blob}


def member_table(ledger):
    """§十一 的'本地独有成员名'表 → 成员集合。"""
    seg = ledger.split("**本地独有成员名")
    if len(seg) < 2:
        return set()
    block = seg[1].split("已核对为")[0]
    # 名字里允许内部下划线（`pump_handoff_done_` 这类也要能登记，否则"表里留着已不存在的成员"
    # 这一类构造缺陷会躲过去 —— 首版就是用 `[A-Za-z0-9]{6,}_` 漏了它）
    return set(re.findall(r"`([A-Za-z][A-Za-z0-9_]{6,}_)`", block)
               )


def probe_rows(ledger):
    """解析 §十一·探测 的 ```ledger-probe``` 块。
    列：编号 | 范围(tree|upstream) | 路径 | 固定串 | 期望匹配行数。
    期望值只存在这一块里（文档别处不许再抄），所以改判据只改一处。"""
    m = re.search(r"```ledger-probe\n(.*?)```", ledger, re.S)
    if not m:
        return None
    rows = []
    for line in m.group(1).strip().splitlines():
        if line.lstrip().startswith("#"):
            continue
        cells = [c.strip() for c in line.split("|")]
        if len(cells) < 5:
            continue
        try:
            expect = int(cells[4])
        except ValueError:
            continue
        rows.append((cells[0], cells[1], cells[2], cells[3], expect))
    return rows


def count_in(scope, up, path, needle):
    """含 needle 的行数。tree = 工作树（含未提交改动），upstream = 上游锚点的同名文件。"""
    if scope == "tree":
        full = os.path.join(KERNEL, path.replace("/", os.sep))
        if not os.path.exists(full):
            return None
        with open(full, encoding="utf-8", errors="replace") as f:
            text = f.read()
    else:
        text = git("show", f"{up}:{path}")[0]
    if not text:
        return None
    return sum(1 for l in text.splitlines() if needle in l)



def apply_probe(patch_path, reverse):
    # ⚠ 两条分支都必须带 --check：`git apply -R` 没有 --check 就是**真回滚**。
    #   2026-09-26 实测：漏了它，哨兵每跑一次就把内核树反向剥掉一层（表现为"有另一个
    #   agent 每隔几分钟回滚我的树"，连着 4 次，直到 F 探测抓到哈希变化才定论是自己）。
    args = ["apply", "--check", "-R" if reverse else "--forward",
            "--ignore-whitespace", patch_path]
    _, rc = git(*args)
    return rc == 0


def dirty_kernel():
    """内核树里未提交的 FFF.Native 改动摘要（空串 = 干净）。"""
    out, _ = git("status", "--porcelain", "--", "FFF.Native")
    return " ".join(l.strip().split()[-1].split("/")[-1] for l in out.splitlines() if l.strip())


def fingerprint():
    """工作树相对索引的差异 + 状态行的摘要。哨兵自己若改动了源码，这个值就变。"""
    diff, _ = git("diff", "--", "FFF.Native")
    st, _ = git("status", "--porcelain", "--", "FFF.Native")
    return hashlib.sha1((diff + "\0" + st).encode("utf-8", "replace")).hexdigest()[:12]


def status_block():
    readme = os.path.join(QUEUE_DIR, "README.md")
    if not os.path.exists(readme):
        return None
    text = read(readme)
    m = re.search(r"```patch-status\n(.*?)```", text, re.S)
    if not m:
        return None
    out = {}
    for line in m.group(1).strip().splitlines():
        parts = [p.strip() for p in line.split("|")]
        if len(parts) >= 2:
            out[parts[0]] = parts[1]
    return out


def checks(up, ledger_text=None):
    """返回 [(名字, OK?, 说明)]。ledger_text 供 --selftest 注入被改坏的台账。"""
    res = []
    fp0 = fingerprint()
    ledger = ledger_text if ledger_text is not None else read(LEDGER)
    diff = net_diff(up)
    members = {i for i in local_only_idents(up, diff) if i.endswith("_")}
    listed = member_table(ledger)

    missing = sorted(members - listed)
    res.append(("A1 净差异成员都已登记", not missing,
                f"未登记 {len(missing)} 个：" + " ".join(missing[:8]) if missing
                else f"{len(members)} 个本地独有成员全部在 §十一 表中"))
    stale = sorted(listed - members)
    res.append(("A2 表中成员都还在净差异里", not stale,
                f"表里有但树中已无此成员 {len(stale)} 个：" + " ".join(stale[:8]) if stale
                else f"表中 {len(listed)} 个成员逐个仍在净差异里"))

    rows = probe_rows(ledger)
    if rows is None:
        res.append(("B §十一·探测块", False,
                    "PATCHES.md 缺 ```ledger-probe``` 块 ⇒ 期望值没有唯一存放处，判据无法复跑"))
    else:
        bad = []
        for did, scope, path, needle, expect in rows:
            got = count_in(scope, up, path, needle)
            if got is None:
                bad.append(f"{did}[{scope}] 文件取不到：{path}")
            elif got != expect:
                bad.append(f"{did}[{scope}] `{needle}` 实得 {got}，台账写 {expect}")
        res.append(("B 探测块逐条复跑一致", not bad,
                    "; ".join(bad) if bad else f"{len(rows)} 条（tree 与 upstream 双向）全部复现"))

    st = status_block()
    if st is None:
        res.append(("C 重放队列状态表", False,
                    "tools/patches/README.md 缺 ```patch-status``` 块（队列未被机器看住）"))
    else:
        problems = []
        notes = []
        # 台账覆盖"在役 + 退役"两处；目录名允许写 `history/xxx.patch`，比对时按 basename。
        active = {f for f in os.listdir(QUEUE_DIR) if f.endswith(".patch")}
        hist_dir = os.path.join(QUEUE_DIR, "history")
        retired = {f for f in os.listdir(hist_dir) if f.endswith(".patch")} if os.path.isdir(hist_dir) else set()
        by_name = {os.path.basename(k): v for k, v in st.items()}
        if len(by_name) != len(st):
            problems.append("状态表里同一文件名出现两次（一 basename 一带 history/ 前缀）")
        for f in sorted((active | retired) - set(by_name)):
            problems.append(f"{f} 在目录里但状态表没有")
        for f in sorted(set(by_name) - active - retired):
            problems.append(f"{f} 状态表里有但两处目录都没有")
        for f, s in sorted(by_name.items()):
            in_active, in_hist = f in active, f in retired
            if in_active and s != "in-tree":
                problems.append(f"在役补丁 {f} 状态是 {s}（在役位只允许 in-tree）")
            if in_hist and s == "in-tree":
                problems.append(f"已退役补丁 {f} 仍声明 in-tree（应在在役位，脚本会当没退役而漏判）")
            path = os.path.join(QUEUE_DIR, f) if in_active else os.path.join(hist_dir, f)
            if not os.path.exists(path):
                continue
            if s == "in-tree":
                # 反向探测是对**工作树**做的：内核树带未提交改动时它可能假失败（0013 是按 HEAD
                # 生成的，而工作树又多了 §十 那 47 行）。这种情况记成 ⚠ 而不是判红，
                # 但必须每次把"此刻不可证"这件事打在输出里 —— 不许悄悄当没看见。
                if not apply_probe(path, True):
                    dirty = dirty_kernel()
                    if dirty:
                        notes.append(f"⚠ {f} 反向探测未过，但内核树有未提交改动（{dirty}）⇒ 本轮不可证")
                    else:
                        problems.append(f"{f} 声明 in-tree 但反向探测不匹配（内容与树不一致）")
        res.append(("C 重放队列状态表自洽", not problems,
                    "; ".join(problems) if problems else
                    f"{len(by_name)} 个补丁状态与目录位置一致"
                    f"（在役 {len(active)} / 退役 {len(retired)}）"
                    + ("" if not notes else " ｜ " + " ; ".join(notes))))

    decl = {i for i in local_only_idents(up, diff) if not i.endswith("_")}
    named = {i for i in decl if i in ledger}
    unknown = sorted(i for i in decl - named
                     if i not in PROSE and not i.isupper() and len(i) >= 10)
    res.append(("D 声明级符号有归属", len(named) > 0 and len(unknown) <= 12,
                f"未在台账点名的长标识符 {len(unknown)} 个（多为散文/局部名）：" +
                " ".join(unknown[:10])))

    # E 编号覆盖：表里登记的每个 D 编号都必须至少有一条探测，否则"删掉某族的判据"
    #    这种最省事的作弊（少写一条）就无人拦。删单条探测拦不住（见 §十二 的诚实边界）。
    table_ids = set(re.findall(r"^\| \*\*(D\d+)\*\*", ledger, re.M))
    probe_ids = {r[0] for r in (rows or [])}
    uncovered = sorted(table_ids - probe_ids)
    res.append(("E 每个登记项都有探测", not uncovered,
                f"无探测的登记项：{' '.join(uncovered)}" if uncovered
                else f"{len(table_ids)} 个登记项各有探测（合计 {len(rows or [])} 条）"))

    # F：哨兵必须是只读的。它一旦顺手改了树，B/C 的读数就成了"自己刚造出来的状态"，
    #    后面所有结论都建立在被污染的样本上（2026-09-26 真实发生过 4 次）。
    fp1 = fingerprint()
    res.append(("F 哨兵对工作树只读", fp0 == fp1,
                f"进入时 {fp0} → 结束时 {fp1}：哨兵改动了内核工作树" if fp0 != fp1
                else f"前后工作树指纹一致（{fp0}）"))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--upstream")
    ap.add_argument("--selftest", action="store_true",
                    help="构造三种缺陷注入台账，逐项确认哨兵会判红")
    a = ap.parse_args()
    up = upstream_anchor(a.upstream)
    print(f"上游锚点 {up[:9]}  内核 HEAD {git('rev-parse','--short','HEAD')[0].strip()}")

    if a.selftest:
        base = read(LEDGER)
        cases = {
            "A1 少登记一个成员（删 pumpThreadId_）": (lambda t: t.replace("`pumpThreadId_`", "``", 1), "A1"),
            "A2 表里多出一个树中没有的成员（只加不改）":
                (lambda t: t.replace("`wakePending_` `generation_`",
                                     "`wakePending_` `generation_` `ghost_member_zzz_`", 1), "A2"),
            "B 探测期望数被改错（27→99）": (lambda t: t.replace("| PresentationPump                        | 27",
                                                            "| PresentationPump                        | 99", 1), "B"),
            "E 某族探测被整族删掉（D2 全删）": (lambda t: "\n".join(
                l for l in t.splitlines() if not l.startswith("D2 ")), "E"),
        }
        ok = True
        for name, (mutate, expect_check) in cases.items():
            fails = [r for r in checks(up, mutate(base)) if not r[1]]
            hit = next((r for r in fails if r[0].startswith(expect_check)), None)
            if not hit:
                ok = False
                print(f"  {name:38} → **没判到 {expect_check}**（实红={[r[0] for r in fails]}）")
            else:
                print(f"  {name:38} → 判红✓ {hit[0]}：{hit[2][:64]}")
        print("自证结论：" + ("四类构造缺陷全部按预期判红，哨兵有牙" if ok else "哨兵无牙，不可依赖"))
        return 0 if ok else 1

    bad = 0
    for name, good, detail in checks(up):
        print(f"  [{'PASS' if good else 'FAIL'}] {name} — {detail}")
        bad += 0 if good else 1
    print(("哨兵结论：台账与代码一致 / ledger matches net diff" if bad == 0
           else f"哨兵结论：{bad} 项不一致 / 请按 §十一 订正"))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
