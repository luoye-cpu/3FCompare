#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针集扫编判据：判"坏名单 == 基线名单"，**不判"全绿"**。

为什么要有这一条（2026-10-14，J3）：`3fp/tools/` 有 82 个 C++ 探针，**全仓没有任何门禁编译它们**
⇒ 它们相对当前 ABI 无声烂掉（内核头改字段、导入库少符号，探针这边一个字都不动就先烂）。
2026-10-13 全量扫编实测：**55 成功 / 27 坏**（20 编译错 + 7 链接 LNK2019）。

为什么判"相等"而不是判"全绿"：那 27 个坏件是**既有事实**，另有波次在修。
拿"全绿"当门禁 ⇒ 从今天起恒红，而恒红的门禁只教会读日志的人忽略红（本仓已经吃过
`[2/11]` 恒红的亏）。这里的口径是：
  · 红名单**变长**（新坏一个）⇒ 红 —— 这才是"探针被本批改烂了"的信号
  · 红名单**变短**（修好一个）⇒ 也红，并提示"基线可以缩短"
    ⇒ 防基线只增不减，也防它修好了还挂着不删（挂着的豁免=没人再看的死人开关）
  · 失败**层漂移**（编译错变成链接错）⇒ 红：条目含义变了，必须重新对账
⇒ 想让它绿，只有两条路：把新坏的修好，或把修好的从基线删掉。没有第三条。

三层判据的形状（本仓规矩：判"缺少某实现"要分三层看，翻宏不算证据）：
  1. 二进制/编译层：cl.exe 真的编一遍（error C#### / fatal error C####）
  2. 链接层：link.exe 真的链一遍（LNK####）——这一层 cl 全过、只有链才炸，
     `build_probe.py` 只链内核导入库、不链 FFmpeg 导入库，所以 7 个探针是这一层坏的
  3. 集合层：实测坏集合 vs 基线声明集合的逐名对账
第 3 层的比较器由 `--self-test` 用构造用例证明它有牙（新增坏判红、修好判红、层漂移判红、
相等判绿），否则比较器本身死了就等于这条门禁只是装饰。

成本与"比上次更快"的做法（**没有**把判据降级成"只数文件个数"）：
  默认**增量模式**：每个探针的结论按（探针源码 sha256 + 输入指纹）缓存于
  `tools/probe_baseline_cache.txt`；指纹没变就复用上次**真编出来的**结论，
  指纹变了（探针本身被改、内核头被改、导入库被改、build_probe.py 被改、
  FFmpeg 头清单被改）就重新编。缓存超过 --max-cache-age-days 天则整份作废重扫。
  ⇒ 每次跑的仍是"编译判据"，只是不做重复功；`--full` 强制全量重扫。

用法：
    py tools/check_probe_set.py --self-test                 # 比较器自检（不编探针）
    py tools/check_probe_set.py                             # 增量扫编 + 对账
    py tools/check_probe_set.py --full                      # 全量扫编（首次建缓存用这个）
    py tools/check_probe_set.py --write-baseline            # 全量扫编后重写基线（修完探针时用）
    py tools/check_probe_set.py --only vprobe,shot_probe    # 子集（只验机制，不作全量结论）
退出码：0=坏名单与基线相等；1=不等（新坏/已修/层漂移）或前置件缺失/缓存不可用（fail-closed）；
        2=用法错。
"""
import argparse
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
TOOLS_DIR = os.path.join(REPO, "3fp", "tools")
BUILD_PROBE = os.path.join(TOOLS_DIR, "build_probe.py")
KERNEL = os.path.join(REPO, "3fp", "kernel", "FFF.Native")
BASELINE_FILE = os.path.join(HERE, "probe_baseline.txt")
CACHE_FILE = os.path.join(HERE, "probe_baseline_cache.txt")
# 缓存条目超过这个天数就整份作废：指纹覆盖不到的东西（工具链被就地替换、
# 环境变量变了…）只能靠时间兜底，不能让它无限期复用旧结论。
DEFAULT_MAX_CACHE_AGE_DAYS = 14


def sha_file(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def listing_fingerprint(root, suffixes=None):
    """目录的（相对路径|大小|mtime_ns）清单指纹。

    FFmpeg 的 include 有 977 个文件 / 5.5MB，逐个读内容去哈希会把判据拖成分钟级；
    这里按清单（含大小与 mtime）折成 12 位十六进制——足以让"换了一版 SDK"命中，
    代价是"就地改了内容而 mtime 与大小都没变"看不见（那种情况下 `--full` 与
    缓存过期天数是兜底）。写在这里是为了让下次有人问"为什么不哈希内容"时不用重想。
    """
    rows = []
    for dirpath, dirs, files in os.walk(root):
        dirs.sort()
        for name in sorted(files):
            if suffixes and not name.endswith(suffixes):
                continue
            path = os.path.join(dirpath, name)
            try:
                st = os.stat(path)
            except OSError:
                continue
            rel = os.path.relpath(path, root).replace("\\", "/")
            rows.append("%s|%d|%d" % (rel, st.st_size, st.st_mtime_ns))
    if not rows:
        return "missing-dir"
    return hashlib.sha256("\n".join(rows).encode("utf-8", "replace")).hexdigest()[:16]


def inputs_fingerprint():
    """影响"这个探针能不能编过"的全部输入，折成一个短哈希。

    少了任何一项，缓存就会在输入变了之后继续复用旧结论 = 用缓存冒充绿灯。
    """
    parts = []
    for path in (BUILD_PROBE,
                 os.path.join(KERNEL, "3FP", "Api", "FFF.Player.Api.h"),
                 os.path.join(KERNEL, "3FR", "Api", "FFF.Native.Api.h"),
                 os.path.join(KERNEL, "x64", "Release", "FFF.Native.lib")):
        if os.path.exists(path):
            parts.append(os.path.basename(path) + "=" + sha_file(path))
        else:
            parts.append(os.path.basename(path) + "=MISSING")
    for root, suffixes in ((os.path.join(REPO, "3fp", "kernel", "third_party", "ffmpeg", "include"), None),
                           (os.path.join(REPO, "3fp", "kernel", "third_party", "ffmpeg", "lib"), None),
                           (os.path.join(KERNEL, "obj", "x64", "Release", "FFF.Native.tlog"),
                            (".tlog",))):
        parts.append("%s=%s" % (os.path.relpath(root, REPO).replace("\\", "/"),
                                listing_fingerprint(root, suffixes)))
    blob = "\n".join(parts).encode("utf-8", "replace")
    return hashlib.sha256(blob).hexdigest()[:16]


def discover_probes():
    """探针全集：3fp/tools/*.cpp 的基名（与 2026-10-13 那次"82 个 C++ 探针"同口径）。

    .c 那三个是直连 FFmpeg SDK 的独立复现件，不链内核导入库，不在本判据对象内
    （基线里也就没有它们；哪天要纳入，先全量扫一遍再写进基线）。
    """
    if not os.path.isdir(TOOLS_DIR):
        return []
    return sorted(name[:-4] for name in os.listdir(TOOLS_DIR) if name.endswith(".cpp"))


def classify_failure(blob):
    """把一次失败的构建输出归到"哪一层坏的"。

    compile：cl.exe 就没过（error C#### / fatal error C####，含 C1083 缺头文件）
    link：cl 过了、link.exe 炸（LNK####）——这一层单独存在，因为 build_probe.py
          只链内核导入库，7 个直接调 FFmpeg 的探针全在这里
    unknown：两个都没扫到 ⇒ 一律按坏处理且**不等于**基线里的任何条目（红），
             绝不把"看不懂"读成"没坏"
    """
    codes = sorted(set(re.findall(r"(?:fatal error |error )(C\d{4})", blob)))
    if codes:
        return "compile", ",".join(codes[:4])
    lnk = sorted(set(re.findall(r"(?:fatal error |error )(LNK\d{4})", blob)))
    if lnk:
        return "link", ",".join(lnk[:4])
    return "unknown", ""


def compile_probe(name, python_exe):
    """真编一个探针。返回 (name, status, layer, detail)。"""
    src = os.path.join(TOOLS_DIR, name + ".cpp")
    if not os.path.exists(src):
        return name, "bad", "missing-source", "%s 不在盘上" % src
    try:
        # text=True 而不指定 encoding：cl.exe 的中文诊断走本机 OEM 码页（实测 cp936），
        # 交给 Python 按 locale 解；就算解出乱码也不影响判定——classify_failure 只认
        # `error C####` / `LNK####` 这些 ASCII 码，**不依赖本地化文案**（换英文工具链照样判）。
        proc = subprocess.run([python_exe, BUILD_PROBE, name], cwd=REPO,
                              capture_output=True, text=True, errors="replace")
    except OSError as ex:
        return name, "bad", "scanner-dead", "无法启动 build_probe.py：%s" % ex
    blob = (proc.stdout or "") + "\n" + (proc.stderr or "")
    if proc.returncode == 0:
        return name, "ok", "", ""
    layer, codes = classify_failure(blob)
    if layer == "unknown":
        tail = [ln.strip() for ln in blob.splitlines() if ln.strip()]
        codes = " ".join(tail[-2:])[:120]
    return name, "bad", layer, codes


def load_baseline(path):
    """基线文件：每行 `<探针名>\t<失败层>[\t说明]`，`#` 开头与空行忽略。"""
    entries = {}
    if not os.path.exists(path):
        return None
    with io.open(path, encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            cols = line.split("\t")
            name = cols[0].strip()
            layer = cols[1].strip() if len(cols) > 1 and cols[1].strip() else "unknown"
            note = cols[2].strip() if len(cols) > 2 else ""
            entries[name] = (layer, note)
    return entries


def cache_digest_rows(probes):
    """缓存条目的自校验摘要。

    缓存里存的是"上次真编出来的结论"，它一旦被手改（或磁盘写坏），判据就会拿假结论
    当实测——那正是"静默跳过冒充绿灯"的升级版本（静默伪造绿灯）。这里对条目做一次
    sha256，读回来时先验一遍：**验不过就整份作废、全部重编**（宁可慢，不可信假绿）。
    这不是防恶意（本地文件谁都能改），是防"手滑编辑缓存"和半写坏的 JSON。
    """
    rows = []
    for name in sorted(probes):
        st, ly, _dt, src = probes[name]
        rows.append("%s|%s|%s|%s" % (name, st, ly, src))
    return hashlib.sha256("\n".join(rows).encode("utf-8", "replace")).hexdigest()[:16]


def load_cache(path, inputs_fp):
    """读增量缓存；返回 (可用的 {name: (status, layer, detail, src_fp)},  说明)。"""
    if not os.path.exists(path):
        return {}, "缓存文件不存在（首次跑请用 --full 建立）"
    try:
        blob = json.loads(io.open(path, encoding="utf-8-sig").read())
    except (ValueError, OSError) as ex:
        return {}, "缓存文件读不出 JSON（%s）——不复用，全量重编" % ex
    rows = {}
    for name, rec in (blob.get("probes") or {}).items():
        rows[name] = (rec.get("status", "bad"), rec.get("layer", "unknown"),
                      rec.get("detail", ""), rec.get("src", ""))
    if blob.get("digest") != cache_digest_rows(rows):
        return {}, "缓存条目与自校验摘要不符（被手改或半写坏）——整份作废，全部重编"
    if blob.get("inputs") != inputs_fp:
        return {}, "缓存的输入指纹与本次不同（内核头/导入库/FFmpeg SDK/build_probe.py 改过）⇒ 全部重编"
    age_days = (time.time() - float(blob.get("built_at_epoch", 0))) / 86400.0
    if age_days > float(blob.get("max_age_days", DEFAULT_MAX_CACHE_AGE_DAYS)):
        return {}, "缓存已 %.1f 天（上限 %s 天）⇒ 整份作废重扫" % (
            age_days, blob.get("max_age_days", DEFAULT_MAX_CACHE_AGE_DAYS))
    return rows, "缓存可用（输入指纹一致、摘要自校验通过、未过期）"


def save_cache(path, probes, inputs_fp, max_age_days):
    blob = {"inputs": inputs_fp, "built_at_epoch": int(time.time()),
            "max_age_days": float(max_age_days),
            "digest": cache_digest_rows(probes),
            "note": "本文件由 tools/check_probe_set.py 生成，是**上次真编出来的结论**的备忘，"
                    "不是判据本身；判据永远以（探针源码 sha + 输入指纹 + 条目摘要）匹配为准，"
                    "对不上就重编。删掉它最多多花一次全量扫编，不会让门禁变绿。",
            "probes": {name: {"status": st, "layer": ly, "detail": dt, "src": sp}
                       for name, (st, ly, dt, sp) in probes.items()}}
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(blob, ensure_ascii=False, indent=1, sort_keys=True))
        handle.write("\n")


def self_test_cache():
    """缓存这一层的自检：写→读必须可用；三种破坏都必须**作废**（不能用半条假结论）。

    为什么值得单列一臂：缓存是这条判据唯一"可以不作真功就变绿"的入口。
    它要是能被手改一条 status 就骗过去，'红名单不许变长'就成了装饰。
    """
    import tempfile
    tmp = tempfile.mkdtemp(prefix="3fc_probecache_")
    path = os.path.join(tmp, "probe_baseline_cache.txt")
    probes = {"aaa": ("bad", "compile", "C2039", "src-a"), "bbb": ("ok", "", "", "src-b")}
    fp = "inputs-fp-1"
    save_cache(path, probes, fp, 14)
    got, _note = load_cache(path, fp)
    # 1) 输入指纹变了（内核头/导入库/FFmpeg SDK/build_probe.py 被改）⇒ 整份作废
    got2, _ = load_cache(path, "inputs-fp-2")
    # 2) 条目被手改（把坏的改成 ok）而摘要没跟着改 ⇒ 整份作废
    blob = json.loads(io.open(path, encoding="utf-8").read())
    blob["probes"]["aaa"]["status"] = "ok"
    io.open(path, "w", encoding="utf-8", newline="\n").write(json.dumps(blob, ensure_ascii=False))
    got3, _ = load_cache(path, fp)
    # 3) 超龄 ⇒ 整份作废（指纹覆盖不到的东西——就地换工具链——由这条兜底）
    save_cache(path, probes, fp, 14)
    blob = json.loads(io.open(path, encoding="utf-8").read())
    blob["built_at_epoch"] = int(time.time()) - 40 * 86400
    io.open(path, "w", encoding="utf-8", newline="\n").write(json.dumps(blob, ensure_ascii=False))
    got4, _ = load_cache(path, fp)
    # 每臂的布尔值含义统一为"**期望的行为确实发生了**"：
    # 第一臂防"缓存永远作废"（那等于增量模式是死的、每次都全量）；
    # 后三臂防"缓存永远可用"（那等于手改一条就能伪造绿灯）。两个方向都要有牙。
    occurred = [("正常回读 → 复用（防'永远作废'）", got == probes and len(got) == len(probes)),
                ("输入指纹不符 → 作废", got2 == {}),
                ("条目被手改 → 作废", got3 == {}),
                ("缓存超龄 → 作废", got4 == {})]
    print("缓存自检：%d 臂（1 臂要求复用 + 3 臂要求作废），失效 %d 臂"
          % (len(occurred), sum(1 for _n, ok in occurred if not ok)))
    for name, ok in occurred:
        print("   %-40s %s" % (name, "OK" if ok else "<-- 错（这一臂的条件没生效）"))
    import shutil
    shutil.rmtree(tmp, ignore_errors=True)
    if any(not ok for _n, ok in occurred):
        print("   [RED] 缓存这一层不可信：作废条件没生效，等于允许假结论冒充实测")
        return False
    print("   判定：缓存只在'输入指纹+条目摘要+龄期'三项都相符时复用，任一项不符就全量重编")
    return True


def write_baseline(path, bad_rows, total_ok, total):
    """--write-baseline：把**实测**的坏集合写回基线（修完探针后缩短基线就走这条路）。"""
    head = u"""\
# 3fp/tools 探针集坏名单基线（判据 tools/check_probe_set.py）
#
# 每行：	<探针名>	<失败层>	<为什么坏（说明用，不参与对账）>
# 失败层：compile=cl.exe 没过（含 C1083 缺头、C2039 ABI 字段不存在）
#         link=link.exe 没过（LNK2019/LNK1120，build_probe.py 只链内核导入库）
#
# 对账口径：**实测坏集合必须与本文件逐名、逐层相等**。
#   新坏一个 ⇒ 红（红名单不许变长）
#   修好一个 ⇒ 也红，并提示把这条删掉（基线不许只增不减、也不许挂着不删）
#   失败层变了 ⇒ 也红（条目含义变了，要重新对账）
#
# 生成方式：py tools/check_probe_set.py --write-baseline --full
# 手工加条目：要能指出"哪一行、哪一层、为什么"，写不出理由就别加。
#
"""
    with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(head)
        for name in sorted(bad_rows):
            layer, note = bad_rows[name]
            handle.write(u"%s\t%s\t%s\n" % (name, layer, note))
    print("   已写基线 %s：%d 条坏（对象共 %d 个，好 %d 个）" % (path, len(bad_rows), total, total_ok))


def self_test():
    """比较器的牙：用构造的（实测, 基线）五臂要求判定结果各自正确。

    这一条是本判据最容易死掉的地方：比较器若把"新坏"读成"相等"，整条门禁就只是装饰。
    code 约定：0=绿（集合完全相等） 1=新坏 2=修好没删 3=层漂移。
    """
    cases = (
        ("相等（绿）",
         {"a": ("compile", ""), "b": ("link", "")},
         {"a": ("compile", ""), "b": ("link", "")}, 0),
        ("红名单变长（红）",
         {"a": ("compile", ""), "b": ("link", ""), "c": ("compile", "")},
         {"a": ("compile", ""), "b": ("link", "")}, 1),
        ("红名单变短／修好没删条目（红）",
         {"a": ("compile", ""), "b": ("link", "")},
         {"a": ("compile", ""), "b": ("link", ""), "c": ("compile", "")}, 2),
        ("失败层漂移（红）",
         {"a": ("link", ""), "b": ("link", "")},
         {"a": ("compile", ""), "b": ("link", "")}, 3),
        ("基线被清空而实测坏 1 个（红，防'删空基线换全绿'）",
         {"a": ("compile", "")}, {}, 1),
    )
    wrong = []
    for name, measured, baseline, want in cases:
        got = compare_sets(measured, baseline)
        mark = "OK"
        if got["code"] != want:
            mark = "<-- 错"
            wrong.append("%s（判成 %d，应为 %d）" % (name, got["code"], want))
        print("   %-46s want=%d got=%d 新坏=%d 已修=%d 漂移=%d %s"
              % (name, want, got["code"], len(got["new_bad"]), len(got["fixed"]),
                 len(got["drift"]), mark))
    print("比较器自检：构造 %d 臂（1 绿向 + 4 红向），判定错误 %d 臂" % (len(cases), len(wrong)))
    if wrong:
        print("   [RED] 比较器不可信：%s" % "; ".join(wrong))
        return False
    print("   判定：比较器活着（新坏、修好没删、层漂移都判红；只有逐名逐层相等才判绿）")
    return True


def compare_sets(measured_bad, baseline):
    """实测坏集合 vs 基线。返回 dict(code: 0 绿 / 1 新坏 / 2 基线可缩短 / 3 层漂移)。

    优先级：先看新坏（最严重），再看已修（要求删条目），再看层漂移。
    多个同时成立时 code 取最靠前的那个，但**三类都要在明细里点名**——
    只报一个会把"两件事同时坏"读成一件。
    """
    new_bad = sorted(set(measured_bad) - set(baseline))
    fixed = sorted(set(baseline) - set(measured_bad))
    drift = sorted(n for n in set(measured_bad) & set(baseline)
                   if measured_bad[n][0] != baseline[n][0])
    code = 0
    if new_bad:
        code = 1
    elif fixed:
        code = 2
    elif drift:
        code = 3
    return {"code": code, "new_bad": new_bad, "fixed": fixed, "drift": drift}


def main(argv):
    ap = argparse.ArgumentParser(description="3fp/tools 探针集扫编判据（坏名单==基线）")
    ap.add_argument("--full", action="store_true", help="忽略缓存，全量重扫")
    ap.add_argument("--only", default="", help="只扫这几个探针（逗号分隔；子集模式不作全量结论）")
    ap.add_argument("--allow-subset", action="store_true",
                    help="配合 --only：允许子集结论直接决定退出码（默认子集模式判红，"
                         "防止有人拿'扫了一小部分都过'当全量绿灯）")
    ap.add_argument("--jobs", type=int, default=1, help="并发编译个数（默认 1，别抢工具链）")
    ap.add_argument("--baseline", default=BASELINE_FILE, help="基线 .txt 路径")
    ap.add_argument("--cache", default=CACHE_FILE, help="增量缓存 .txt 路径")
    ap.add_argument("--max-cache-age-days", type=float, default=DEFAULT_MAX_CACHE_AGE_DAYS,
                    help="缓存最长可用天数（超龄整份作废重扫）")
    ap.add_argument("--write-baseline", action="store_true",
                    help="按本次实测重写基线（修完探针后用；必须配 --full）")
    ap.add_argument("--self-test", action="store_true", help="只跑比较器自检，不编探针")
    a = ap.parse_args(argv)

    if a.self_test:
        comparator_ok = self_test()
        cache_ok = self_test_cache()
        print("结论: %s" % ("判据自身可用（比较器 + 缓存两层自检都过）"
                            if comparator_ok and cache_ok else "判红——判据自身不可信"))
        return 0 if (comparator_ok and cache_ok) else 1

    probes = discover_probes()
    print("探针集·对象：%d 个（%s）" % (len(probes), os.path.join("3fp", "tools", "*.cpp")))
    if not probes:
        print("   [RED] 一个探针都没扫到（对象目录不对或判据失效）——不能读成'全都在基线里'")
        return 1

    baseline = load_baseline(a.baseline)
    if baseline is None:
        print("   [RED] 基线文件不存在：%s ——判据没有对账对象，不能读成'通过'（fail-closed）"
              % a.baseline)
        return 1

    # 前置件：build_probe.py 或内核导入库不在，扫编就跑不起来。此时**判红**，
    # 不"跳过"——跳过就是"静默跳过冒充绿灯"。
    missing_pre = [p for p in (BUILD_PROBE, os.path.join(KERNEL, "x64", "Release", "FFF.Native.lib"),
                               os.path.join(KERNEL, "obj", "x64", "Release", "FFF.Native.tlog"))
                   if not os.path.exists(p)]
    if missing_pre:
        print("   [RED] 前置件缺失，扫编跑不起来（未判定 ≠ 通过）：%s"
              % ", ".join(os.path.relpath(p, REPO).replace("\\", "/") for p in missing_pre))
        return 1

    subset = bool(a.only)
    universe = probes
    if subset:
        wanted = [n.strip() for n in a.only.split(",") if n.strip()]
        unknown = [n for n in wanted if n not in probes]
        if unknown:
            print("   [RED] --only 里的探针名在盘上不存在：%s" % ", ".join(unknown))
            return 1
        universe = [n for n in probes if n in wanted]

    inputs_fp = inputs_fingerprint()
    if a.full or subset:
        cache = {}
        cache_note = ("指定了 --full：忽略缓存，全部重编" if a.full
                      else "子集模式（--only）：不读缓存，本次点名的探针全部真编一遍")
    else:
        cache, cache_note = load_cache(a.cache, inputs_fp)
    print("   缓存：%s" % cache_note)

    todo = []
    results = {}
    for name in universe:
        try:
            src_fp = sha_file(os.path.join(TOOLS_DIR, name + ".cpp"))
        except OSError as ex:
            results[name] = ("bad", "missing-source", "%s" % ex, "")
            continue
        hit = cache.get(name)
        if hit and hit[3] == src_fp:
            results[name] = (hit[0], hit[1], hit[2], src_fp)
        else:
            todo.append((name, src_fp))

    python_exe = sys.executable or "python"
    t0 = time.time()
    if todo:
        src_fps = dict(todo)
        workers = max(1, int(a.jobs))
        if workers == 1:
            for name, src_fp in todo:
                nm, st, ly, dt = compile_probe(name, python_exe)
                results[nm] = (st, ly, dt, src_fp)
        else:
            with ThreadPoolExecutor(max_workers=workers) as pool:
                for nm, st, ly, dt in pool.map(lambda pair: compile_probe(pair[0], python_exe), todo):
                    results[nm] = (st, ly, dt, src_fps[nm])
    reused = len(universe) - len(todo)
    print("探针集·扫编：对象 %d 个（实编 %d / 复用上次结论 %d），用时 %.1fs"
          % (len(universe), len(todo), reused, time.time() - t0))

    # 缓存只在**全量覆盖**时回写（子集模式写会把没扫的条目丢掉，"比上次快"就失真了）；
    # 且"扫编器自己没跑起来"这类结论**不进缓存**——那不是探针的属性，是这台机器的瞬时状态，
    # 缓存下来会让下次继续复用一条与探针无关的红。
    if not subset and not a.write_baseline:
        merged = dict(cache)
        for name, (st, ly, dt, src_fp) in results.items():
            if ly in ("scanner-dead", "missing-source", "unknown"):
                merged.pop(name, None)
                continue
            merged[name] = (st, ly, dt, src_fp)
        try:
            save_cache(a.cache, merged, inputs_fp, a.max_cache_age_days)
            print("   缓存已回写：%s（%d 条结论，输入指纹 %s）" % (a.cache, len(merged), inputs_fp))
        except OSError as ex:
            print("   ⚠ 缓存写不进去（不影响本次判定，下次会重编）：%s" % ex)

    measured_bad = {}
    ok_count = 0
    for name in sorted(results):
        st, ly, dt, _fp = results[name]
        if st == "ok":
            ok_count += 1
        else:
            measured_bad[name] = (ly, dt)
    # 子集模式只对账"落在本次覆盖范围内的基线条目"，否则没扫的会被读成"已修好"。
    scope = {n: v for n, v in baseline.items() if n in universe} if subset else baseline
    base_layers = {}
    for name, (ly, _note) in scope.items():
        base_layers[ly] = base_layers.get(ly, 0) + 1
    meas_counts = {}
    for name, (ly, _dt) in measured_bad.items():
        meas_counts[ly] = meas_counts.get(ly, 0) + 1
    print("探针集·基线：%d 条坏（%s）" % (
        len(scope), " / ".join("%s %d" % (k, v) for k, v in sorted(base_layers.items())) or "空"))
    print("探针集·实测：%d 条坏 / %d 条好（%s）" % (
        len(measured_bad), ok_count,
        " / ".join("%s %d" % (k, v) for k, v in sorted(meas_counts.items())) or "无"))

    if a.write_baseline:
        if not a.full:
            print("   [RED] --write-baseline 必须配 --full：拿一半缓存结论写基线 = 把缓存当实测")
            return 1
        if subset:
            print("   [RED] --write-baseline 不允许子集模式（--only）：会把没扫的读成没坏")
            return 1
        notes = {name: baseline.get(name, ("", ""))[1] for name in measured_bad}
        write_baseline(a.baseline, {n: (measured_bad[n][0], notes.get(n, ""))
                                   for n in measured_bad}, ok_count, len(probes))
        return 0

    if subset:
        print("   ⚠ 子集模式：只覆盖 %d/%d 个探针，**不构成全量结论**（门禁不许用这个模式）"
              % (len(universe), len(probes)))

    cmp = compare_sets(measured_bad, scope)
    print("探针集·对账：新坏 %d / 已修 %d / 层漂移 %d / 判定=%s"
          % (len(cmp["new_bad"]), len(cmp["fixed"]), len(cmp["drift"]),
             "通过" if cmp["code"] == 0 else "红"))
    for name in cmp["new_bad"]:
        print("   [RED] 新坏（红名单变长，本批把它弄坏了）：%s（层=%s，%s）"
              % (name, measured_bad[name][0], measured_bad[name][1]))
    for name in cmp["fixed"]:
        print("   [RED] 已修好但基线还挂着：%s（基线层=%s）⇒ 基线可缩短，请删这一条"
              "（或跑 --write-baseline --full 重写）" % (name, scope[name][0]))
    for name in cmp["drift"]:
        print("   [RED] 失败层漂移：%s 基线=%s 实测=%s ⇒ 条目含义变了，重新对账后改基线"
              % (name, scope[name][0], measured_bad[name][0]))
    if cmp["code"] == 0:
        print("   判定：坏名单与基线逐名逐层相等（%d 条）——本批没把探针弄坏，也没有修好没删的"
              % len(scope))

    if subset and not a.allow_subset:
        print("结论: 判红——子集模式（--only）不给绿灯依据：没扫的那 %d 个探针属**未判定**，"
              "不能读成'它们没坏'。确实只要子集结论时再加 --allow-subset"
              % (len(probes) - len(universe)))
        return 1
    print("结论: %s" % ("通过——坏名单==基线" if cmp["code"] == 0 else
                        "判红——坏名单与基线不等（code=%d）" % cmp["code"]))
    return 0 if cmp["code"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
