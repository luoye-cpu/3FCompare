r"""
多路崩溃率 A/B 统计（正确的退出码口径）
======================================
⚠ 用 Python 而不是 bash：Windows 上 bash 的 `$?` 会把进程异常码压成 1，
   而 `--multitest` 的 **exit=1 是"漂移断言失败"，不是崩溃**。
   Python 的 subprocess.returncode 能拿到真实的 NTSTATUS。

退出码分类（详见下方 `classify` 上方的"分类依据"，勿凭直觉改）：
  0                 → OK      正常退出
  1                 → DRIFT   漂移断言失败（**不是崩溃**）
  2                 → ARG     参数/素材错误，或托管异常被 catch
  3                 → HANG    看门狗卡死硬兜底退出（无有效测量）
  4                 → SKIP    闸门 SKIPPED（无有效测量）
  127               → NORUN   延迟加载 DLL 缺失 ⇒ 进程根本没跑起来（**无有效测量**）
  0xC0000000..0xD0000000 → CRASH_*  原生崩溃
  其它              → UNKNOWN 未登记退出码（**计入异常，不许静默丢**）

⚠⚠ Python 在 Windows 上返回的是**无符号** 32 位退出码（实测 3221225501），
   **不是**负数 -1073741795。两种都登记，避免再踩。

用法：
  # 臂内写法 "routes:active"，active<routes 时其余路 Pause（device/swapchain 仍在）
  python tools/crash_rate.py --arms 9:9,9:3 --rounds 8 --seconds 30
  python tools/crash_rate.py --arms 4:4 --rounds 20 --seconds 30
  # 接门禁：崩溃率 >30% 或 出现无效轮次 时返回非 0
  python tools/crash_rate.py --arms 4:4,9:9 --rounds 20 --max-crash-rate 0.30

本脚本自身的退出码：
  0  正常（无无效轮次，且未触发 --max-crash-rate）
  1  崩溃率超过 --max-crash-rate
  2  存在无效轮次（超时 / NORUN / HANG）⇒ 本轮数据不可用
  3  脚本未捕获异常
"""

import argparse
import os
import subprocess
import sys
import time
import traceback
from collections import Counter

# 默认路径从脚本位置推导（不再硬编码 C:\PLAN\... 这类机器特定路径）；
# 仍可用 --exe / --media 覆盖。
_REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(_REPO, "src", "3FCompare", "bin", "Release", "net11.0-windows",
                   "3FCompare.exe")
MEDIA = os.path.join(_REPO, "testmedia", "media", "real", "real_4k_hevc10_60m.mp4")

_BASE = {
    0: "OK",
    1: "DRIFT",
    2: "ARG",
    0xC0000005: "CRASH_AV_0xC0000005",
    0xC000001D: "CRASH_ILL_0xC000001D",
    0xC0000096: "CRASH_PRIV_0xC0000096",
    0xC0000374: "CRASH_HEAP_0xC0000374",
    0xC0000409: "CRASH_STACKOVERRUN_0xC0000409",
    0xC00000FD: "CRASH_STACKOVERFLOW_0xC00000FD",
}
# Python 返回无符号；某些宿主可能返回有符号，两种都收录
KIND = dict(_BASE)
for k, v in list(_BASE.items()):
    if k >= 0x80000000:
        KIND[k - 0x100000000] = v

# ── 分类依据（本项目已定案；改之前先读 docs/43、docs/44 §3.3、CrashExitCode.cs）──
#
# 判据只有一条：**这个退出码有没有产生"可用的一轮测量"**，以及它是不是异常终止。
#
#  ① 正常退出
#       0  OK
#
#  ② 已知非崩溃语义码 —— 进程跑起来了，退出码是程序自己决定的，语义明确：
#       1  DRIFT  漂移断言失败（docs/18/35/41/43 反复定案：**不是崩溃**）
#       2  ARG    参数/素材文件错误，或托管异常被 catch（Program.cs Run*test）
#       4  SKIP   闸门 SKIPPED（docs/41 §12.3）
#
#  ③ 无有效测量 —— 有定义的码，但这一轮**没跑出结果**，与超时同类：
#       3  HANG   看门狗卡死硬兜底退出（MainWindow.SelfTest.cs:702/755）
#       127 NORUN 延迟加载 DLL 缺失 ⇒ 进程根本没起来
#                 （docs/26:722：FFmpeg DLL 不在 exe 同目录就 exit=127）
#
#  ④ 原生崩溃 —— 位模式落在 NTSTATUS 严重错误区间（与 CrashExitCode.cs 一致）：
#       0xC0000000..0xD0000000  CRASH_*
#
#  ⑤ 未登记
#       其它  UNKNOWN —— 必须显式计入异常并单列，**不许静默丢弃**
#
# ⚠ 两个方向都不能错：
#   · 不能把所有非 0 码都叫"崩溃" ⇒ 会让 1(DRIFT) 污染分子，崩溃率虚高；
#   · 也不能把 UNKNOWN 当成"没崩溃" ⇒ 会静默漏计（这是本文件修掉的老 bug）。
#   · ②③④ 进 2x2（崩溃 vs 非崩溃）；③⑤ 不产生可判定结果，**不进 2x2**，
#     否则会把崩溃率**稀释**（老 bug：exit=127 被当"正常运行"计入分母）。
OK_CODES = {0}
KNOWN_NONCRASH = {1: "DRIFT", 2: "ARG", 4: "SKIP"}
NO_MEASURE = {3: "HANG", 127: "NORUN"}
CRASH_FLOOR = 0xC0000000
CRASH_CEILING = 0xD0000000

TIMEOUT = "TIMEOUT"


def classify(rc):
    """退出码 → 分类标签。

    入参按**位模式**处理：.NET 给有符号 -1073741819、Python 给无符号 3221225477，
    指向同一个 NTSTATUS 0xC0000005，两种口径都必须认（docs/44 §3.3 踩过）。
    """
    u = rc & 0xFFFFFFFF
    if u in OK_CODES:
        return "OK"
    if u in KNOWN_NONCRASH:
        return KNOWN_NONCRASH[u]
    if u in NO_MEASURE:
        return NO_MEASURE[u]
    if u in KIND:
        return KIND[u]
    if CRASH_FLOOR <= u < CRASH_CEILING:
        return f"CRASH_0x{u:08X}"
    return f"UNKNOWN({u})" if u < 0x10000 else f"UNKNOWN(0x{u:08X})"


def is_crash(k):
    """是否为原生崩溃（2x2 的分子）。"""
    return k.startswith("CRASH")


def is_no_measure(k):
    """是否"没跑出可用测量"：超时 / NORUN / HANG。

    这些轮次既不进 2x2 分子也不进分母 —— 当成"未崩溃"会稀释崩溃率
    （docs/43 的教训：exit=127 根本没跑起来，却被当成正常运行）。
    """
    return k == TIMEOUT or k in NO_MEASURE.values()


def is_undecided(k):
    """是否无法判定为"崩溃/非崩溃"：无有效测量 + 未登记码。这些轮次不进 2x2。"""
    return is_no_measure(k) or k.startswith("UNKNOWN")


def kill_tree(proc):
    """终止整棵进程树（含孙进程）。

    ⚠ 不能只 proc.kill()：3FCompare 会派生子进程（崩溃自愈守护 --child），
    只杀父进程会留下孙进程继续占着 GPU/文件句柄，污染下一轮结果。
    Windows 用系统自带的 taskkill /T（不引第三方依赖），失败再退化为杀父进程。
    """
    if os.name == "nt":
        try:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=20)
        except Exception:
            pass
    try:
        proc.kill()
    except Exception:
        pass
    try:
        proc.wait(timeout=15)
    except Exception:
        pass


def run_one(routes, active, seconds, timeout):
    """跑一轮，返回 (rc, 耗时, err_label)。

    rc 为 None 表示没拿到退出码（超时被强杀 / 进程起不来），此时 err_label
    给出标签（TIMEOUT / NORUN）；否则 err_label 为 None，由调用方 classify(rc)。
    """
    env = dict(os.environ)
    env.pop("_3FC_CRASH_TRACE", None)
    if active < routes:
        env["FC_MULTITEST_ACTIVE"] = str(active)
    else:
        env.pop("FC_MULTITEST_ACTIVE", None)
    cmd = [EXE, "--multitest", MEDIA, str(routes), str(seconds)]
    t0 = time.time()
    try:
        p = subprocess.Popen(cmd, env=env,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        # exe 不存在 / 无法创建进程：等价于"根本没跑起来"，与 exit=127 同类
        return None, time.time() - t0, "NORUN"
    try:
        return p.wait(timeout=timeout), time.time() - t0, None
    except subprocess.TimeoutExpired:
        kill_tree(p)
        return None, time.time() - t0, TIMEOUT


def main():
    global EXE, MEDIA
    ap = argparse.ArgumentParser(
        description="多路崩溃率 A/B 统计（正确的退出码口径）")
    ap.add_argument("--arms", default="9:9,9:3")
    ap.add_argument("--rounds", type=int, default=8)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--exe", default=EXE)
    ap.add_argument("--media", default=MEDIA)
    ap.add_argument("--timeout", type=float, default=0.0,
                    help="单轮硬超时秒数；0=自动（seconds*4+240，"
                         "即应用自身看门狗 durationSec*4+180 之上再留 60s 余量）")
    ap.add_argument("--max-crash-rate", type=float, default=None,
                    help="崩溃率超过该值(0~1)时脚本返回 1；默认不启用（保持既有行为）")
    a = ap.parse_args()

    arms = []
    for s in a.arms.split(","):
        s = s.strip()
        if ":" in s:
            r, act = s.split(":")
        else:
            r, act = s, s
        arms.append((int(r), int(act)))

    EXE, MEDIA = a.exe, a.media
    # 应用侧 multitest 自带 durationSec*4+180 的卡死看门狗；脚本超时必须比它更宽，
    # 否则会抢在应用自诊断之前强杀，拿不到 code=3 这个更有信息量的信号。
    timeout = a.timeout if a.timeout > 0 else a.seconds * 4 + 240

    print(f"素材 {os.path.basename(MEDIA)}   时长 {a.seconds}s   每臂 {a.rounds} 轮（交替对照）")
    print(f"臂：{arms}   单轮超时 {timeout:.0f}s\n")

    stats = {arm: Counter() for arm in arms}
    durations = {arm: [] for arm in arms}
    n_no_measure = 0

    for i in range(a.rounds):
        for arm in arms:
            routes, active = arm
            rc, dt, err = run_one(routes, active, a.seconds, timeout)
            k = err or classify(rc)
            stats[arm][k] += 1
            durations[arm].append(dt)
            if is_no_measure(k):
                n_no_measure += 1
            flag = ""
            if is_crash(k):
                flag = "  ★★★ 崩溃"
            elif k == "NORUN":
                flag = "  ⚠⚠ 未启动（延迟加载 DLL 缺失？）本轮数据无效"
            elif k == TIMEOUT:
                flag = "  ⚠⚠ 超时强杀，本轮数据无效"
            elif k == "HANG":
                flag = "  ⚠⚠ 看门狗判卡死，本轮数据无效"
            elif k.startswith("UNKNOWN"):
                flag = "  ⚠ 未登记退出码（计入异常，不进 2x2）"
            rc_s = "-" if rc is None else str(rc)
            print(f"  轮{i+1}/{a.rounds}  路数={routes} 播放={active}  "
                  f"exit={rc_s} ({k})  {dt:.1f}s{flag}")

    print("\n=== 汇总 ===")
    arm_counts = {}
    worst_rate = 0.0
    for arm in arms:
        routes, active = arm
        c = stats[arm]
        n = sum(c.values())
        crashes = sum(v for k, v in c.items() if is_crash(k))
        undecided = sum(v for k, v in c.items() if is_undecided(k))
        # 2x2 只统计"可判定"的轮次：无有效测量与未登记码都不进，避免稀释崩溃率
        n_judged = n - undecided
        avg = sum(durations[arm]) / max(1, len(durations[arm]))
        detail = "  ".join(f"{k}={v}" for k, v in sorted(c.items()))
        rate = crashes / max(1, n_judged)
        worst_rate = max(worst_rate, rate)
        arm_counts[arm] = (crashes, n_judged)
        print(f"  路数={routes} 播放={active}: n={n}  "
              f"崩溃 {crashes}/{n_judged} = {100*rate:.1f}%   平均耗时 {avg:.1f}s")
        print(f"      {detail}")
        if undecided:
            # 单列显示，让人一眼看出是 UNKNOWN 还是真崩溃（不能只给一个总数）
            bits = []
            for lbl in ("UNKNOWN", TIMEOUT, "NORUN", "HANG"):
                v = sum(n2 for k2, n2 in c.items() if k2.startswith(lbl))
                if v:
                    bits.append(f"{lbl} {v}")
            print(f"      ⚠ 不可判定 {undecided} 轮（{'、'.join(bits)}）"
                  f"→ 已从 2x2 剔除；真崩溃 {crashes} 轮仍计入")

    # 两两 Fisher 精确检验（2x2：崩溃 vs 非崩溃）
    if len(arms) == 2:
        (a1, a2) = arms
        c1, n1 = arm_counts[a1]
        c2, n2 = arm_counts[a2]
        try:
            from scipy.stats import fisher_exact
            _, p = fisher_exact([[c1, n1 - c1], [c2, n2 - c2]])
            src = "scipy"
        except Exception:
            p = fisher_manual(c1, n1, c2, n2)
            src = "手写（无 scipy）"
        print(f"\n  Fisher 双侧 p = {p:.4f}  （{src}）")
        print(f"  【{a1[0]}路播{a1[1]}】{c1}/{n1}  vs  【{a2[0]}路播{a2[1]}】{c2}/{n2}")

    # ── 脚本自身退出码（原实现恒 return 0 ⇒ 全崩也假绿，接门禁必漏报）──
    rc_out = 0
    if a.max_crash_rate is not None and worst_rate > a.max_crash_rate:
        print(f"\n⚠ 崩溃率 {100*worst_rate:.1f}% 超过阈值 "
              f"{100*a.max_crash_rate:.1f}% ⇒ 退出码 1")
        rc_out = 1
    if n_no_measure:
        print(f"\n⚠⚠ 有 {n_no_measure} 轮没跑出结果（超时/NORUN/HANG）"
              f"⇒ 本轮数据不可用，退出码 2")
        rc_out = 2   # 覆盖 1：数据不可用时崩溃率结论本身没有意义
    return rc_out


def fisher_manual(c1, n1, c2, n2):
    """2x2 Fisher 精确检验（双侧），无 scipy 时的手写实现

    表：[a  b]  = [崩溃, 未崩溃] 第 1 臂
        [c  d]                  第 2 臂
    """
    from math import comb
    a, b = c1, n1 - c1
    c, d = c2, n2 - c2
    r1, r2 = a + b, c + d          # 行和
    k = a + c                      # 第 1 列和（总崩溃数）
    total = r1 + r2

    def pr(x):
        y = k - x
        if not (0 <= x <= r1 and 0 <= y <= r2):
            return 0.0
        return comb(r1, x) * comb(r2, y) / comb(total, k)

    p_obs = pr(a)
    lo = max(0, k - r2)
    hi = min(r1, k)
    s = 0.0
    for x in range(lo, hi + 1):
        px = pr(x)
        if px <= p_obs * (1 + 1e-9):
            s += px
    return min(1.0, s)


if __name__ == "__main__":
    # ⚠ 必须 sys.exit(main())：原实现 main 恒 return 0 ⇒ 全崩也假绿。
    try:
        sys.exit(main())
    except SystemExit:
        raise                      # argparse --help / 参数错误 的正常退出
    except BaseException:
        traceback.print_exc()
        sys.exit(3)                # 未捕获异常 ⇒ 非 0
