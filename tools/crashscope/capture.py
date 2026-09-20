"""现场采集：启动 3FCompare.exe 的 4 路播放，在进程存活期间抓模块快照。

为什么要抓：ASLR 让每次运行的模块基址都不同，事后拿 dump/Report.wer 做归属时
必须有一份"这一次运行"的 [Base, Base+Size) 表来交叉验证。

采集方式：EnumProcessModulesEx + GetModuleInformation（64 位基址不截断，
Toolhelp32 的 modBaseAddr 是 DWORD 会截断，不能用）+ GetModuleFileNameExW 取路径。

用法：
    python capture.py --run-id r1 --out runs/r1.json
    python capture.py --media testmedia/media/real/real_4k_h264_60m.mp4 --routes 4 --seconds 30
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wer as wermod  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_EXE = os.path.join(REPO, "src", "3FCompare", "bin", "Debug",
                           "net11.0-windows", "3FCompare.exe")
DEFAULT_MEDIA = os.path.join(REPO, "testmedia", "media", "real", "real_4k_h264_60m.mp4")

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
LIST_MODULES_ALL = 0x03
MAX_PATH = 32768

_psapi = None
for _dll in ("psapi", "kernel32"):
    try:
        _psapi = ctypes.WinDLL(_dll)
        break
    except OSError:
        continue


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p),
                ("SizeOfImage", wt.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


def _bind_prototypes():
    """必须显式声明 argtypes/restype：否则 ctypes 会把 64 位 HMODULE 当 c_int 截断。"""
    try:
        enum_fn = _psapi.EnumProcessModulesEx
    except AttributeError:
        enum_fn = ctypes.WinDLL("kernel32").K32EnumProcessModulesEx
    enum_fn.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p), wt.DWORD,
                        ctypes.POINTER(wt.DWORD), wt.DWORD]
    enum_fn.restype = wt.BOOL

    get_info = _psapi.GetModuleInformation
    get_info.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MODULEINFO), wt.DWORD]
    get_info.restype = wt.BOOL

    get_name = _psapi.GetModuleFileNameExW
    get_name.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPWSTR, wt.DWORD]
    get_name.restype = wt.DWORD
    return enum_fn, get_info, get_name


def snapshot_modules(pid: int):
    """返回 [{name, path, base, size}]；进程已退出或无权访问时返回 None。"""
    enum_fn, get_info, get_name = _bind_prototypes()
    k32 = ctypes.WinDLL("kernel32")
    k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    k32.OpenProcess.restype = wt.HANDLE
    k32.CloseHandle.argtypes = [wt.HANDLE]
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return None
    try:
        need = wt.DWORD(0)
        if not enum_fn(h, None, 0, ctypes.byref(need), LIST_MODULES_ALL):
            return None
        n = need.value // ctypes.sizeof(ctypes.c_void_p)
        if n <= 0:
            return None
        arr = (ctypes.c_void_p * (n + 32))()
        got = wt.DWORD(0)
        if not enum_fn(h, arr, ctypes.sizeof(arr), ctypes.byref(got), LIST_MODULES_ALL):
            return None
        cnt = got.value // ctypes.sizeof(ctypes.c_void_p)

        buf = ctypes.create_unicode_buffer(MAX_PATH)
        out = []
        for i in range(cnt):
            hm = arr[i]
            if not hm:
                continue
            mi = MODULEINFO()
            if not get_info(h, hm, ctypes.byref(mi), ctypes.sizeof(mi)):
                continue
            if not mi.lpBaseOfDll or not mi.SizeOfImage:
                continue
            ln = get_name(h, hm, buf, MAX_PATH)
            path = buf.value if ln else ""
            out.append({
                "base": int(mi.lpBaseOfDll),
                "size": int(mi.SizeOfImage),
                "path": path,
                "name": path.replace("\\", "/").rsplit("/", 1)[-1] if path else f"0x{int(hm):x}",
            })
        out.sort(key=lambda m: m["base"])
        return out
    finally:
        k32.CloseHandle(h)


def find_wer_for(start_epoch: float, end_epoch: float, slack: float = 25.0):
    """找出落在 [start-slack, end+slack] 内生成的 WER 归档（用于把崩溃事件对上这次运行）。"""
    hits = []
    for w in wermod.load_all():
        t = wermod.filetime_to_epoch(w.event_time)
        if start_epoch - slack <= t <= end_epoch + slack:
            hits.append({
                "dir": w.dir,
                "event_epoch": t,
                "delta_s": round(t - start_epoch, 2),
                "faulting_module": w.faulting_module,
                "exception_code": w.exception_code,
                "exception_offset": w.exception_offset,
                "exception_data": w.exception_data,
            })
    hits.sort(key=lambda h: h["event_epoch"])
    return hits


def run_once(exe: str, media: str, routes: int, seconds: int, run_id: str,
             snapshot_interval: float = 0.35, hard_timeout: float = 180.0,
             log_dir: str | None = None):
    if not os.path.exists(exe):
        raise SystemExit(f"exe 不存在：{exe}")
    if not os.path.exists(media):
        raise SystemExit(f"素材不存在：{media}")

    cwd = os.path.dirname(exe)
    cmd = [exe, "--multitest", media, str(routes), str(seconds)]
    log_path = None
    fh = None
    if log_dir:
        os.makedirs(log_dir, exist_ok=True)
        log_path = os.path.join(log_dir, f"{run_id}.log")
        fh = open(log_path, "w", encoding="utf-8", errors="replace")

    t0 = time.time()
    proc = subprocess.Popen(cmd, cwd=cwd, stdout=fh or subprocess.DEVNULL,
                            stderr=subprocess.STDOUT)
    pid = proc.pid

    snaps = []
    last_good = None
    seen_paths = set()
    n_fail = 0
    try:
        while True:
            rc = proc.poll()
            el = time.time() - t0
            if rc is not None:
                break
            if el > hard_timeout:
                proc.kill()
                proc.wait()
                break
            s = snapshot_modules(pid)
            if s:
                last_good = s
                snaps.append({"t": round(el, 3), "count": len(s)})
                for m in s:
                    if m["path"]:
                        seen_paths.add(m["path"])
            else:
                n_fail += 1
            time.sleep(snapshot_interval)
    except BaseException:
        # 采集器自身出错时别留下孤儿进程
        try:
            proc.kill()
            proc.wait(timeout=10)
        except Exception:
            pass
        raise
    finally:
        if fh:
            fh.close()

    rc = proc.wait()
    t1 = time.time()

    exit_code = rc if rc is not None else None
    unsigned = exit_code & 0xFFFFFFFF if exit_code is not None and exit_code < 0 else exit_code

    result = {
        "run_id": run_id,
        "cmd": cmd,
        "cwd": cwd,
        "pid": pid,
        "start_epoch": t0,
        "end_epoch": t1,
        "start_local": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(t0)),
        "end_local": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(t1)),
        "duration_s": round(t1 - t0, 2),
        "exit_code": exit_code,
        "exit_code_hex": f"0x{unsigned:08X}" if unsigned is not None else None,
        "snapshots": snaps,
        "snapshot_failures": n_fail,
        "modules_last": last_good or [],
        "module_paths_seen": sorted(seen_paths),
        "log": log_path,
    }

    # 退出码归类
    ec = unsigned
    if ec is None:
        kind = "unknown"
    elif ec == 0:
        kind = "clean"
    elif ec in (0xC0000005, 0xC000001D, 0xC0000006, 0xC0000409, 0xC00000FD):
        kind = "crash"
    else:
        kind = "other"
    result["exit_kind"] = kind

    result["wer_hits"] = find_wer_for(t0, t1)
    return result


def main():
    ap = argparse.ArgumentParser(description="3FCompare 4 路播放现场采集")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--media", default=DEFAULT_MEDIA)
    ap.add_argument("--routes", type=int, default=4)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--run-id", default=None)
    ap.add_argument("--out", default=None, help="结果 JSON 路径")
    ap.add_argument("--outdir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "runs"))
    ap.add_argument("--no-wer-scan", action="store_true", help="跳过 WER 归档扫描（快很多）")
    args = ap.parse_args()

    run_id = args.run_id or time.strftime("run_%Y%m%d_%H%M%S")
    if args.no_wer_scan:
        globals()["find_wer_for"] = lambda *a, **k: []
    res = run_once(args.exe, args.media, args.routes, args.seconds, run_id,
                   log_dir=os.path.join(args.outdir, "logs"))
    out = args.out or os.path.join(args.outdir, f"{run_id}.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=2)

    print(f"[{run_id}] pid={res['pid']} 退出码={res['exit_code_hex']} ({res['exit_kind']}) "
          f"耗时={res['duration_s']}s 模块数={len(res['modules_last'])} "
          f"快照={len(res['snapshots'])} 失败={res['snapshot_failures']}")
    for h in res["wer_hits"]:
        print(f"    WER {h['delta_s']:+.1f}s  {h['faulting_module']}  "
              f"code={h['exception_code']} off={h['exception_offset']} data={h['exception_data']}")
    print(f"    -> {out}")


if __name__ == "__main__":
    main()
