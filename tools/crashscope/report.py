"""汇总多次运行的取证结果，输出人可读结论。

把三路证据合到一起：
  1. WER 归档（全量历史 + 本次运行窗口）—— 异常码 / 故障模块 / 模块内 RVA
  2. 每次运行的 minidump —— 异常地址 + 崩溃线程栈模块序列
  3. 每次运行的模块快照 —— 基址交叉验证 + 钩子模块是否加载

用法：
    python report.py --runs-dir runs --dump-dir <dumps> --out report.json
    python report.py --runs-dir runs --wer-only        # 只看 WER 历史分布
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import attribute as at  # noqa: E402
import minidump as md  # noqa: E402
import wer as wermod  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DUMPS = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Temp", "crashscope", "dumps")
HOOKS = ("RTSSHooks64.dll", "nvspcap64.dll")


def load_runs(runs_dir: str):
    out = []
    for p in sorted(glob.glob(os.path.join(runs_dir, "*.json"))):
        if os.path.basename(p).startswith("report"):
            continue
        try:
            out.append(json.load(open(p, encoding="utf-8")))
        except Exception:
            pass
    out.sort(key=lambda r: r.get("start_epoch", 0))
    return out


def find_dump_for(run: dict, dump_dir: str):
    if not dump_dir or not os.path.isdir(dump_dir):
        return None
    t0, t1 = run.get("start_epoch", 0), run.get("end_epoch", 0)
    best = None
    for f in os.listdir(dump_dir):
        if not f.lower().endswith(".dmp"):
            continue
        p = os.path.join(dump_dir, f)
        mt = os.path.getmtime(p)
        if t0 - 5 <= mt <= t1 + 30:
            if best is None or mt > os.path.getmtime(best):
                best = p
    return best


def wer_history(archive_root=None):
    reps = wermod.load_all(archive_root)
    by_mod = collections.Counter()
    by_code = collections.Counter()
    by_sig = collections.Counter()
    wild = 0
    for w in reps:
        by_mod[w.faulting_module] += 1
        by_code[w.exception_code] += 1
        if w.is_stackhash:
            wild += 1
            by_sig[("WILD(StackHash)", w.exception_code, w.pch_hint)] += 1
        else:
            by_sig[(w.faulting_module, w.exception_code, w.exception_offset)] += 1
    return {
        "count": len(reps),
        "by_faulting_module": dict(by_mod.most_common()),
        "by_exception_code": dict(by_code.most_common()),
        "by_signature": {f"{k[0]} | {k[1]} | {k[2]}": v for k, v in by_sig.most_common()},
        "wild_count": wild,
        "reports": reps,
    }


def load_scenes(scenes_dir: str):
    out = []
    if not scenes_dir or not os.path.isdir(scenes_dir):
        return out
    for p in sorted(glob.glob(os.path.join(scenes_dir, "*.json"))):
        if os.path.basename(p).startswith("report"):
            continue
        try:
            out.append(json.load(open(p, encoding="utf-8")))
        except Exception:
            pass
    return out


def summarize_scenes(scenes: list):
    """把实时调试器抓到的现场汇总成"结论可直接引用"的统计。"""
    attr_counter = collections.Counter()
    code_counter = collections.Counter()
    top_mod = collections.Counter()
    callsite_mod = collections.Counter()
    callsite_text = collections.Counter()
    stack_mod = collections.Counter()
    hook_on_stack = 0
    kernel_on_stack = 0
    rva_counter = collections.Counter()
    rows = []
    for s in scenes:
        cs = s.get("crash_scene")
        if not cs:
            rows.append({"run_id": s.get("run_id"), "note": "未捕获到致命异常"})
            continue
        e = cs["exception"]
        aa = cs.get("attribution") or {}
        ss = cs.get("stack_scan") or {}
        ct = cs.get("callsite") or {}
        seq = ss.get("sequence") or []
        row = {
            "run_id": s.get("run_id"),
            "exit_code": s.get("exit_code_hex"),
            "duration_s": s.get("duration_s"),
            "code": e["code"], "code_name": e["code_name"],
            "av_kind": e.get("av_kind"),
            "address": e["address"],
            "av_target": e.get("av_target"),
            "rip_matches_address": e.get("rip_matches_address"),
            "attribution_kind": aa.get("kind"),
            "attribution_module": aa.get("module"),
            "attribution_rva": aa.get("rva"),
            "in_exec_section": aa.get("in_exec_section"),
            "instruction_in_memory": aa.get("instruction_in_memory"),
            "memory_probe": aa.get("memory_probe"),
            "nearest_below": aa.get("nearest_below"),
            "return_module": ct.get("return_module"),
            "return_rva": ct.get("return_rva"),
            "callsite_module": (ct.get("callsite") or {}).get("module"),
            "callsite_rva": (ct.get("callsite") or {}).get("rva"),
            "callsite_text": (ct.get("callsite") or {}).get("text"),
            "callsite_raw": (ct.get("callsite") or {}).get("raw"),
            "callsite_selected_by": (ct.get("callsite") or {}).get("selected_by"),
            "stack_top": seq[:8],
            "hooks_on_stack": ss.get("hook_modules_on_stack") or [],
            "kernel_ours_on_stack": ss.get("kernel_ours_on_stack") or [],
            "registers": cs.get("registers"),
        }
        rows.append(row)
        code_counter[f"{e['code']} {e['code_name']}"] += 1
        key = aa.get("module") or "WILD"
        attr_counter[key] += 1
        if aa.get("module"):
            rva_counter[f"{aa['module']}+{aa.get('rva')}"] += 1
        if seq:
            top_mod[seq[0]["module"]] += 1
            for x in seq:
                stack_mod[x["module"]] += 1
        if row["callsite_module"]:
            callsite_mod[row["callsite_module"]] += 1
        if row["callsite_text"]:
            callsite_text[row["callsite_text"]] += 1
        if row["hooks_on_stack"]:
            hook_on_stack += 1
        if row["kernel_ours_on_stack"]:
            kernel_on_stack += 1
    return {
        "count": len(scenes),
        "rows": rows,
        "by_exception": dict(code_counter),
        "by_attribution": dict(attr_counter),
        "by_faulting_rva": dict(rva_counter),
        "stack_top_module": dict(top_mod),
        "callsite_module": dict(callsite_mod),
        "callsite_text": dict(callsite_text),
        "stack_modules": dict(stack_mod.most_common()),
        "runs_with_hook_on_stack": hook_on_stack,
        "runs_with_kernel_on_stack": kernel_on_stack,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs-dir", default=os.path.join(HERE, "runs"))
    ap.add_argument("--scenes-dir", default=os.path.join(HERE, "scenes"))
    ap.add_argument("--dump-dir", default=DEFAULT_DUMPS)
    ap.add_argument("--out", default=os.path.join(HERE, "runs", "report.json"))
    ap.add_argument("--wer-only", action="store_true")
    ap.add_argument("--wer-root", default=None)
    a = ap.parse_args()

    hist = wer_history(a.wer_root)
    print("=" * 78)
    print(f"一、WER 归档历史分布（{hist['count']} 份）")
    print("-" * 78)
    for sig, n in hist["by_signature"].items():
        print(f"  {n:4d}  {sig}")
    print(f"  其中『异常地址不在任何模块内』(StackHash 野地址)：{hist['wild_count']} 份")
    if a.wer_only:
        return

    scenes = load_scenes(a.scenes_dir)
    sc = summarize_scenes(scenes)
    print()
    print(f"二、实时调试器现场（{sc['count']} 次，不依赖 WER）")
    print("-" * 78)
    for r in sc["rows"]:
        if r.get("note"):
            print(f"  [{r['run_id']}] {r['note']}")
            continue
        print(f"  [{r['run_id']}] 退出码={r['exit_code']} 耗时={r['duration_s']}s")
        print(f"      异常码={r['code']} {r['code_name']}  访问性质={r['av_kind']}  "
              f"故障地址={r['address']}  访问目标={r['av_target']}  RIP==地址={r['rip_matches_address']}")
        if r["attribution_kind"] == "in_module":
            print(f"      故障地址归属={r['attribution_module']} RVA={r['attribution_rva']} "
                  f"可执行节={r['in_exec_section']}")
            print(f"      内存指令={r['instruction_in_memory']}")
        else:
            mp = r.get("memory_probe") or {}
            print(f"      故障地址归属=野地址（不在任何模块） 下方最近={r.get('nearest_below')}")
            if mp:
                print(f"      该地址内存={mp.get('state')}/{mp.get('type')}/"
                      f"protect={mp.get('protect')} 区基址={mp.get('region_base')}")
        if r.get("callsite_module"):
            print(f"      调用点={r['callsite_module']} RVA={r['callsite_rva']} "
                  f"{r['callsite_text']} raw={r['callsite_raw']} "
                  f"[{r.get('callsite_selected_by')}]")
        print(f"      栈顶返回地址={r['return_module']}+{r['return_rva']}")
        if r.get("stack_top"):
            print("      栈顶模块序列: " +
                  " -> ".join(f"{x['module']}(c{x['count']})" for x in r["stack_top"]))
        print(f"      栈上钩子={r['hooks_on_stack'] or '无'}  "
              f"栈上内核={r['kernel_ours_on_stack'] or '无'}")

    print()
    print("三、采集运行（capture.py，含模块快照与 WER 命中）")
    print("-" * 78)

    runs = load_runs(a.runs_dir)
    if not runs:
        print("  (runs 目录为空)")

    per_run = []
    attr_counter = collections.Counter()
    code_counter = collections.Counter()
    stack_mod_counter = collections.Counter()
    hook_on_stack = 0
    kernel_on_stack = 0
    seq_samples = []

    for r in runs:
        dmp = find_dump_for(r, a.dump_dir)
        entry = {
            "run_id": r.get("run_id"),
            "exit_code": r.get("exit_code_hex"),
            "exit_kind": r.get("exit_kind"),
            "duration_s": r.get("duration_s"),
            "modules_snapshot": len(r.get("modules_last") or []),
            "hooks_loaded": {
                h: any((m.get("name") or "").lower() == h.lower()
                       for m in (r.get("modules_last") or []))
                for h in HOOKS},
            "dump": dmp,
            "exception": None,
            "attribution": None,
            "stack_top": None,
            "hooks_on_stack": [],
            "kernel_ours_on_stack": [],
            "wer_hits": r.get("wer_hits") or [],
        }
        if dmp:
            try:
                an = at.analyze_dump(dmp, r)
                entry["exception"] = an.get("exception")
                entry["attribution"] = an.get("attribution")
                ss = an.get("stack_scan") or {}
                seq = ss.get("sequence") or []
                entry["stack_top"] = seq[:8]
                entry["hooks_on_stack"] = ss.get("hook_modules_on_stack") or []
                entry["kernel_ours_on_stack"] = ss.get("kernel_ours_on_stack") or []
                entry["cross_check"] = an.get("cross_check")
                if seq:
                    seq_samples.append((r.get("run_id"), seq[:8]))
                for s in seq:
                    stack_mod_counter[s["module"]] += 1
                if entry["hooks_on_stack"]:
                    hook_on_stack += 1
                if entry["kernel_ours_on_stack"]:
                    kernel_on_stack += 1
            except Exception as ex:
                entry["error"] = f"{type(ex).__name__}: {ex}"

        # 归属统计：优先用 dump，退化用 WER
        a_kind = None
        if entry["attribution"]:
            a = entry["attribution"]
            a_kind = a.get("module") or "WILD"
        elif entry["wer_hits"]:
            h = entry["wer_hits"][0]
            a_kind = ("WILD" if h["faulting_module"].lower().startswith("stackhash")
                      else h["faulting_module"])
        if a_kind:
            attr_counter[a_kind] += 1
        if entry["exception"] and entry["exception"].get("code"):
            code_counter[entry["exception"]["code"]] += 1
        per_run.append(entry)

        print(f"  [{entry['run_id']}] 退出码={entry['exit_code']} ({entry['exit_kind']}) "
              f"耗时={entry['duration_s']}s 模块={entry['modules_snapshot']}")
        if entry["exception"]:
            e = entry["exception"]
            print(f"      异常码={e['code']} {e['code_name']}  地址={e.get('address')}  "
                  f"性质={e.get('av_kind')} 数据={e.get('params')}")
        if entry["attribution"]:
            at_ = entry["attribution"]
            if at_["kind"] == "in_module":
                print(f"      归属={at_['module']} [{at_['category']}] RVA={at_['rva']} "
                      f"可执行节={at_.get('in_exec_section')} 指令={at_.get('instruction')}")
            else:
                print(f"      归属=野地址（不在任何模块内） 下方最近={at_.get('nearest_below')}")
        if entry["stack_top"]:
            s = " -> ".join(f"{x['module']}(c{x['count']})" for x in entry["stack_top"])
            print(f"      栈顶模块序列: {s}")
            print(f"      栈上钩子={entry['hooks_on_stack'] or '无'}  "
                  f"栈上内核={entry['kernel_ours_on_stack'] or '无'}")
        elif not dmp:
            print(f"      (无 dump；WER 命中 {len(entry['wer_hits'])} 条)")
        for h in entry["wer_hits"]:
            print(f"      WER {h['delta_s']:+.1f}s {h['faulting_module']} "
                  f"code={h['exception_code']} off={h['exception_offset']} data={h['exception_data']}")

    print()
    print("四、汇总")
    print("-" * 78)
    if sc["count"]:
        print(f"  [实时调试器] 异常码分布：{sc['by_exception']}")
        print(f"  [实时调试器] 故障地址归属分布：{sc['by_attribution']}")
        print(f"  [实时调试器] 故障地址 RVA 分布：{sc['by_faulting_rva']}")
        print(f"  [实时调试器] 栈顶第一段模块分布：{sc['stack_top_module']}")
        print(f"  [实时调试器] 调用点模块分布：{sc['callsite_module']}")
        print(f"  [实时调试器] 调用点指令分布：{sc['callsite_text']}")
        print(f"  [实时调试器] 栈上出现钩子的运行数：{sc['runs_with_hook_on_stack']}/{sc['count']}")
        print(f"  [实时调试器] 栈上出现 FFF.Native.dll 的运行数：{sc['runs_with_kernel_on_stack']}/{sc['count']}")
        print(f"  [实时调试器] 栈上模块频次：{sc['stack_modules']}")
    print(f"  [采集运行] 归属分布：{dict(attr_counter) or '无'}")
    print(f"  [采集运行] 异常码分布：{dict(code_counter) or '无'}")
    print("  [采集运行] 栈上出现的模块（按出现在多少次运行的栈顶序列里计）：")
    for m, n in stack_mod_counter.most_common(15):
        print(f"      {n:4d}  {m}")
    print(f"  [采集运行] 栈上出现覆盖层/钩子的运行数：{hook_on_stack}/{len(runs)}")
    print(f"  [采集运行] 栈上出现 FFF.Native.dll 的运行数：{kernel_on_stack}/{len(runs)}")

    if seq_samples:
        print()
        print("四、栈顶模块序列样本（每次运行前 8 段）")
        print("-" * 78)
        for rid, seq in seq_samples:
            print(f"  [{rid}] " + " -> ".join(f"{s['module']}(c{s['count']})" for s in seq))

    result = {"wer_history": {k: v for k, v in hist.items() if k != "reports"},
              "live_scenes": sc,
              "runs": per_run,
              "summary": {
                  "runs": len(runs),
                  "live_scene_runs": sc["count"],
                  "live_scene_attribution": sc["by_attribution"],
                  "live_scene_faulting_rva": sc["by_faulting_rva"],
                  "live_scene_callsite_module": sc["callsite_module"],
                  "live_scene_stack_top_module": sc["stack_top_module"],
                  "live_scene_runs_with_hook_on_stack": sc["runs_with_hook_on_stack"],
                  "live_scene_runs_with_kernel_on_stack": sc["runs_with_kernel_on_stack"],
                  "attribution": dict(attr_counter),
                  "exception_codes": dict(code_counter),
                  "stack_modules": dict(stack_mod_counter.most_common()),
                  "runs_with_hook_on_stack": hook_on_stack,
                  "runs_with_kernel_on_stack": kernel_on_stack,
              }}
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2, default=str)
    print(f"\n-> {a.out}")


if __name__ == "__main__":
    main()
