"""本机 CUDA 识别 + 后端决策（供 llama.cpp 后端使用）。

目的：不随包分发 NVIDIA 的 cublasLt/cublas/cudart（约 548 MB），而是在
      启动时自动扫描本机已有的 CUDA，按版本决定怎么跑；识别不到就回退 Vulkan。

判定链（全部实测过，见 docs §30）：
1. 扫描：toolkit / PATH / pip 的 nvidia-* 包 / 常见 AI 软件自带目录。
2. 分组：cudart / cublas / cublasLt 三个库必须同 major（llama.cpp 预构建链接的是 _12）。
3. 验证：子进程里按该组合设 PATH，用 ctypes 真加载三个 dll 并调
   cudaRuntimeGetVersion + cudaGetDeviceCount —— 只认「能加载、能报版本、能看到卡」，
   不认「文件存在」。
4. 决策：major==12 → 直接复用；major==13 → 需 shim（缺 cudaGetDeviceProperties_v2）；
   其他/无 → vulkan。

用法：
    python llama_cuda_probe.py            # 人读
    python llama_cuda_probe.py --json     # 机器读（worker 用）
"""
from __future__ import annotations

import ctypes
import glob
import json
import os
import re
import subprocess
import sys

LIBS = ("cudart64_{m}.dll", "cublas64_{m}.dll", "cublasLt64_{m}.dll")
SUPPORTED_MAJOR = 12          # llama.cpp 预构建链接 _12
NEEDS_SHIM_MAJOR = 13         # 可直接驱动，但缺 cudaGetDeviceProperties_v2
MIN_MAJOR = 11                # 更老的（如 PhysX 自带的 6.5）不参与判定
MAX_MAJOR = 20                # 同上，防 cudart64_65 这类假版本号混入

# 优先级：只认能原生复用的 12。
# 13 理论上可用 .def 转发 shim 接上（本仓库 make_shim.py 生成，8 KB），
# 但实测会崩：llama-server 起服务后 28.6 s 在 audio 初始化阶段 rc=0xC0000005。
# 同一份 ggml、同一段音频用 12.9 则正常 ⇒ 根因在 CUDA 13 与预构建的 ABI/语义差异，
# 不是 shim 写法问题。故 13 不作为可用档，直接回退 Vulkan。详见 docs §30。
PREFERENCE = (SUPPORTED_MAJOR,)


# ---------------------------------------------------------------- 扫描

def _reg_toolkits() -> list[str]:
    """注册表里的 CUDA toolkit 安装路径。"""
    out: list[str] = []
    try:
        import winreg
    except Exception:
        return out
    for root, sub in (
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\NVIDIA Corporation\GPU Computing Toolkit\CUDA"),
    ):
        try:
            with winreg.OpenKey(root, sub) as k:
                i = 0
                while True:
                    try:
                        name = winreg.EnumKey(k, i)
                    except OSError:
                        break
                    i += 1
                    try:
                        with winreg.OpenKey(k, name) as vk:
                            p, _ = winreg.QueryValueEx(vk, "InstallDir")
                            out.append(p)
                    except OSError:
                        pass
        except OSError:
            pass
    return out


def _pip_nvidia_dirs() -> list[str]:
    """pip 的 nvidia-* 包（PyTorch 依赖）里的 bin 目录。"""
    out: list[str] = []
    pats = [
        r"C:\Users\*\AppData\Local\Programs\Python\*\Lib\site-packages\nvidia",
        r"C:\Users\*\AppData\Local\Programs\Python\*\Lib\site-packages\torch\lib",
        r"C:\Python*\Lib\site-packages\nvidia",
        r"C:\Python*\Lib\site-packages\torch\lib",
        r"C:\Program Files\Python*\Lib\site-packages\nvidia",
        r"C:\ProgramData\miniconda3\Lib\site-packages\nvidia",
        r"C:\ProgramData\anaconda3\Lib\site-packages\nvidia",
        r"C:\Users\*\miniconda3\Lib\site-packages\nvidia",
        r"C:\Users\*\anaconda3\Lib\site-packages\nvidia",
    ]
    seen = set()
    for pat in pats:
        for base in glob.glob(pat):
            if not os.path.isdir(base):
                continue
            # nvidia/<pkg>/bin 或 torch/lib 本身
            subs = [base] if os.path.basename(base) == "lib" else \
                   [os.path.join(base, d, "bin") for d in ("cublas", "cuda_runtime", "cuda_cupti")]
            for s in subs:
                if os.path.isdir(s) and s.lower() not in seen:
                    seen.add(s.lower())
                    out.append(s)
    return out


def _ai_app_dirs() -> list[str]:
    """常见本地推理软件自带的 CUDA 运行时。"""
    local = os.environ.get("LOCALAPPDATA", "")
    pats = [
        os.path.join(local, "Programs", "Ollama", "lib", "ollama"),
        os.path.join(local, "Programs", "Ollama"),
        os.path.join(local, "LM-Studio", "resources", "app", ".webpack"),
        os.path.join(local, "Programs", "lm-studio", "resources"),
        r"C:\Program Files\NVIDIA Corporation\Nsight Compute*\extras\CUPTI",
    ]
    out: list[str] = []
    for p in pats:
        for hit in glob.glob(p):
            if os.path.isdir(hit):
                out.append(hit)
                # 往下两层找 bin / cuda_v12 之类
                for sub in glob.glob(os.path.join(hit, "*")):
                    if os.path.isdir(sub):
                        out.append(sub)
                        for s2 in glob.glob(os.path.join(sub, "*")):
                            if os.path.isdir(s2):
                                out.append(s2)
    return out


def _path_dirs() -> list[str]:
    parts = [p for p in os.environ.get("PATH", "").split(os.pathsep) if p]
    hits = [p for p in parts if re.search(r"cuda|nvidia", p, re.I)]
    cp = os.environ.get("CUDA_PATH")
    if cp:
        hits += [os.path.join(cp, "bin"), os.path.join(cp, "bin", "x64"), cp]
    return hits


def _toolkit_glob() -> list[str]:
    out: list[str] = []
    for base in (r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA",
                 r"C:\Program Files (x86)\NVIDIA GPU Computing Toolkit\CUDA"):
        for v in glob.glob(os.path.join(base, "v*")):
            out += [os.path.join(v, "bin"), os.path.join(v, "bin", "x64")]
    return out


def candidate_dirs() -> list[str]:
    seen, out = set(), []
    for d in (_reg_toolkits() + _toolkit_glob() + _path_dirs()
              + _pip_nvidia_dirs() + _ai_app_dirs()):
        if not d:
            continue
        d = os.path.normpath(d)
        if not os.path.isdir(d):
            continue
        k = d.lower()
        if k in seen:
            continue
        seen.add(k)
        out.append(d)
    return out


def scan(dirs: list[str] | None = None) -> dict[int, dict]:
    """按 major 聚合：{major: {"cudart": path, "cublas": path, "cublasLt": path, "dirs": [...]}}"""
    found: dict[int, dict] = {}
    for d in (dirs if dirs is not None else candidate_dirs()):
        for name, tmpl in zip(("cudart", "cublas", "cublasLt"), LIBS):
            for p in glob.glob(os.path.join(d, tmpl.format(m="*"))):
                m = re.search(r"_(\d+)\.dll$", os.path.basename(p), re.I)
                if not m:
                    continue
                major = int(m.group(1))
                if major < MIN_MAJOR or major > MAX_MAJOR:
                    continue
                # nvblas 之类会撞名，只认严格文件名
                if os.path.basename(p).lower() != tmpl.format(m=major).lower():
                    continue
                slot = found.setdefault(major, {"dirs": []})
                if name not in slot:
                    slot[name] = p
                if d not in slot["dirs"]:
                    slot["dirs"].append(d)
    return found


# ---------------------------------------------------------------- 验证

# 注意：Python 3.8+ 的 ctypes 默认用 LOAD_LIBRARY_SEARCH_DEFAULT_DIRS，**不搜 PATH**。
# llama-server.exe 是原生程序，走标准搜索（含 PATH）。为对齐行为，这里显式
# winmode=0（退回 LoadLibrary 语义）+ 绝对路径 + add_dll_directory 三保险。
_VERIFY_SNIPPET = r"""
import ctypes, json, os, sys
cfg = json.loads(sys.argv[1])
res = {"ok": False}
for d in cfg["dirs"]:
    try:
        os.add_dll_directory(d)
    except Exception:
        pass
loaded = {}
for key in ("cublasLt", "cublas", "cudart"):     # 依赖在前
    p = cfg["libs"].get(key)
    if not p:
        res["error"] = "缺 " + key
        print(json.dumps(res)); raise SystemExit(0)
    try:
        loaded[key] = ctypes.WinDLL(p, winmode=0)
    except OSError as e:
        res["error"] = "%s: %s" % (os.path.basename(p), e)
        print(json.dumps(res)); raise SystemExit(0)
cd = loaded["cudart"]
ver = ctypes.c_int(0)
if hasattr(cd, "cudaRuntimeGetVersion"):
    cd.cudaRuntimeGetVersion(ctypes.byref(ver))
res["runtime_version"] = ver.value          # 12090 形如
n = ctypes.c_int(-1)
if hasattr(cd, "cudaGetDeviceCount"):
    rc = cd.cudaGetDeviceCount(ctypes.byref(n))
    res["ret"] = rc
    res["count"] = n.value
    res["ok"] = (rc == 0 and n.value > 0)
else:
    res["error"] = "cudaGetDeviceCount missing"
print(json.dumps(res))
"""


def verify(major: int, slot: dict, timeout: int = 60) -> dict:
    """在子进程里按给定目录组合真加载并查询；子进程隔离，避免 DLL 污染主进程。"""
    libs = {k: slot.get(k) for k in ("cudart", "cublas", "cublasLt")}
    cfg = {"major": major, "dirs": slot.get("dirs", []), "libs": libs}
    env = os.environ.copy()
    env["PATH"] = os.pathsep.join(slot.get("dirs", [])) + os.pathsep + env.get("PATH", "")
    try:
        r = subprocess.run([sys.executable, "-c", _VERIFY_SNIPPET, json.dumps(cfg)],
                           capture_output=True, text=True, timeout=timeout, env=env)
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "timeout"}
    line = [l for l in r.stdout.strip().splitlines() if l.startswith("{")]
    if not line:
        return {"ok": False, "error": (r.stderr or r.stdout or "no output")[-300:]}
    try:
        return json.loads(line[-1])
    except Exception:
        return {"ok": False, "error": "bad json: " + line[-1][:200]}


def gpu_names() -> list[str]:
    try:
        o = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total",
                            "--format=csv,noheader"], capture_output=True, text=True, timeout=10)
        return [l.strip() for l in o.stdout.strip().splitlines() if l.strip()]
    except Exception:
        return []


# ---------------------------------------------------------------- 决策

def decide(force_major: int | None = None) -> dict:
    out: dict = {"decided": "vulkan", "reason": "", "candidates": [], "lib_dirs": [],
                 "cuda_major": None, "runtime_version": None, "gpu_count": 0,
                 "gpus": gpu_names()}
    dirs = candidate_dirs()
    found = scan(dirs)

    for major in sorted(found, reverse=True):
        slot = found[major]
        have = [k for k in ("cudart", "cublas", "cublasLt") if k in slot]
        ent = {"major": major, "dirs": slot["dirs"], "libs": {k: slot.get(k) for k in
                                                            ("cudart", "cublas", "cublasLt")},
               "complete": len(have) == 3}
        if ent["complete"]:
            ent["verify"] = verify(major, slot)
        else:
            ent["verify"] = {"ok": False, "error": "缺 " + ",".join(
                k for k in ("cudart", "cublas", "cublasLt") if k not in slot)}
        out["candidates"].append(ent)

    if not out["gpus"]:
        out["reason"] = "未检测到 NVIDIA 显卡（nvidia-smi 无输出）"
        return out

    if os.environ.get("LLAMA_NO_SYSTEM_CUDA"):
        out["reason"] = "环境变量 LLAMA_NO_SYSTEM_CUDA 已设置，跳过系统 CUDA ⇒ 走 Vulkan"
        return out

    # 按偏好挑：只认原生可复用的 12；同 major 多份时取第一个验证通过的。
    for want in ((force_major,) if force_major else PREFERENCE):
        for ent in out["candidates"]:
            v = ent["verify"]
            if ent["major"] != want or not v.get("ok"):
                continue
            out.update(decided="cuda-shim" if ent["major"] == NEEDS_SHIM_MAJOR else "cuda",
                       cuda_major=ent["major"],
                       lib_dirs=ent["dirs"],
                       runtime_version=v.get("runtime_version"),
                       gpu_count=v.get("count", 0),
                       reason="本机 CUDA %d 可直接被 llama.cpp 复用（无需随包分发）" % ent["major"])
            return out

    reasons = []
    for e in out["candidates"]:
        v = e["verify"]
        if not v.get("ok"):
            reasons.append("CUDA %s: %s" % (e["major"], v.get("error", "验证未通过")))
        elif e["major"] == NEEDS_SHIM_MAJOR:
            reasons.append("CUDA %s: 库可加载，但与 llama.cpp 预构建不兼容"
                           "（实测 audio 初始化 0xC0000005），不采用" % e["major"])
        else:
            reasons.append("CUDA %s: 该版本不受支持" % e["major"])
    out["reason"] = ("；".join(reasons) or "本机未找到 cudart64_*/cublas64_*/cublasLt64_*") \
                    + " ⇒ 回退 Vulkan"
    return out


# ---------------------------------------------------------------- 设备与启动计划

# 核显/软渲染不作为首选：Vulkan 在 Intel iGPU 上实测是负收益（0.458 vs CPU 0.183）。
_WEAK_GPU = re.compile(r"intel|uhd|iris|\bhd graphics\b|swiftshader|microsoft basic|llvmpipe", re.I)


def list_devices(exe: str, env_extra: dict | None = None, timeout: int = 90) -> list[dict]:
    """跑 `<exe> --list-devices`，解析成 [{"idx","name","vram_mb"}]。"""
    env = os.environ.copy()
    if env_extra:
        env.update(env_extra)
    try:
        r = subprocess.run([exe, "--list-devices"], capture_output=True, text=True,
                           timeout=timeout, env=env, cwd=os.path.dirname(exe))
    except Exception:
        return []
    out = []
    for ln in (r.stdout or "").splitlines():
        m = re.match(r"\s*([A-Za-z]+\d+)\s*:\s*(.+?)\s*\((\d+)\s*MiB", ln)
        if m:
            out.append({"idx": m.group(1), "name": m.group(2), "vram_mb": int(m.group(3))})
    return out


def choose_device(devs: list[dict]) -> dict | None:
    """优先独显（NVIDIA/AMD），排除核显；同档按显存取大。"""
    if not devs:
        return None
    strong = [d for d in devs if not _WEAK_GPU.search(d["name"])]
    return max(strong or devs, key=lambda d: d["vram_mb"])


def plan(llama_root: str, force: str | None = None, force_major: int | None = None) -> dict:
    """按识别结果给出可直接执行的启动计划。

    llama_root 期望布局（两个子目录，互不共享 dll）：
        <root>/cuda/    ggml-cuda + llama 自己的 dll（NVIDIA 三个库从系统借）
        <root>/vulkan/  ggml-vulkan + llama 自己的 dll

    返回：
        backend  cuda | vulkan
        exe      要起的 llama-server.exe
        device   --device 的值
        env      需要合并进子进程的环境变量（主要是 PATH）
        devices  该后端枚举到的设备（排查用）
    """
    dec = decide(force_major)
    llama_root = os.path.abspath(llama_root)
    res = {"backend": None, "exe": None, "device": None, "env": {},
           "devices": [], "reason": "", "cuda_major": dec["cuda_major"],
           "lib_dirs": dec["lib_dirs"], "probe_reason": dec["reason"]}

    cuda_dir = os.path.join(llama_root, "cuda")
    vk_dir = os.path.join(llama_root, "vulkan")
    shim_dir = os.path.join(llama_root, "shim")
    shim_ok = all(os.path.isfile(os.path.join(shim_dir, f))
                  for f in ("cudart64_12.dll", "cublas64_12.dll"))

    def attempt_cuda() -> tuple[dict | None, str]:
        if dec["decided"] == "vulkan":
            return None, "无可用系统 CUDA"
        dirs = list(dec["lib_dirs"])
        if dec["decided"] == "cuda-shim":
            if not shim_ok:
                return None, "只有 CUDA %s，且包内无转发 shim（应放在 %s）" % (
                    dec["cuda_major"], shim_dir)
            dirs = [shim_dir] + dirs        # shim 必须排在系统 13 之前
        exe = os.path.join(cuda_dir, "llama-server.exe")
        if not os.path.isfile(exe):
            return None, "缺少 %s" % exe
        env = {"PATH": os.pathsep.join(dirs + [os.environ.get("PATH", "")])}
        devs = list_devices(exe, env)
        cuda_devs = [d for d in devs if d["idx"].upper().startswith("CUDA")]
        if not cuda_devs:
            return None, "cuda 目录 --list-devices 未列出 CUDA 设备"
        d = choose_device(cuda_devs)
        return {"exe": exe, "device": d["idx"], "env": env, "devices": devs}, ""

    def attempt_vk() -> tuple[dict | None, str]:
        exe = os.path.join(vk_dir, "llama-server.exe")
        if not os.path.isfile(exe):
            return None, "缺少 %s" % exe
        devs = list_devices(exe)
        vk = [d for d in devs if d["idx"].upper().startswith("VULKAN")]
        if not vk:
            return None, "vulkan 目录 --list-devices 未列出 Vulkan 设备"
        d = choose_device(vk)
        return {"exe": exe, "device": d["idx"], "env": {}, "devices": devs}, ""

    order = [("cuda", attempt_cuda), ("vulkan", attempt_vk)]
    if force == "vulkan":
        order = [("vulkan", attempt_vk)]
    elif force == "cuda":
        order = [("cuda", attempt_cuda)]

    reasons = []
    for name, fn in order:
        got, why = fn()
        if got:
            res.update(backend=name, **got)
            res["reason"] = ("%s（系统 CUDA %s，来自 %s）" % (
                dec["reason"], dec["cuda_major"], os.path.dirname(dec["lib_dirs"][0]))
                if name == "cuda" else "无可用系统 CUDA，走 Vulkan")
            return res
        reasons.append("%s: %s" % (name, why))
    res["reason"] = "；".join(reasons)
    return res


def main(argv: list[str]) -> int:
    if "--plan" in argv:
        i = argv.index("--plan")
        root = argv[i + 1] if len(argv) > i + 1 and not argv[i + 1].startswith("-") else ""
        force = argv[argv.index("--force") + 1] if "--force" in argv else None
        major = int(argv[argv.index("--major") + 1]) if "--major" in argv else None
        p = plan(root, force, major)
        print(json.dumps(p, ensure_ascii=False, indent=2))
        return 0 if p["backend"] else 1

    res = decide()
    if "--json" in argv:
        print(json.dumps(res, ensure_ascii=False, indent=2))
    else:
        print("决策：%s" % res["decided"])
        print("原因：%s" % res["reason"])
        if res["gpus"]:
            print("显卡：")
            for g in res["gpus"]:
                print("   " + g)
        if res["lib_dirs"]:
            print("复用目录：")
            for d in res["lib_dirs"]:
                print("   " + d)
        if res["candidates"]:
            print("扫描到的 CUDA：")
            for c in res["candidates"]:
                v = c["verify"]
                tag = "OK" if v.get("ok") else "×"
                print("   [%s] major=%s ver=%s 设备数=%s  %s" % (
                    tag, c["major"], v.get("runtime_version"), v.get("count"),
                    v.get("error", "")))
                for k, p in c["libs"].items():
                    print("        %-9s %s" % (k, p))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
