#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""配对守卫：待嵌入内核必须满足托管侧的实际要求，否则门禁必须红。

为什么需要它：本仓撞过的失效形状是 **"构建全绿 -> 应用静默降级演示模式 -> --selftest 退出码还是 0"**。
`EngineFactory` 只在运行时比 API 版本（不等就回退 Simulated），`check_kernel_exports.py` 只查
"必需导出齐不齐 + 报一下版本"，两者都不回答 **"这份 DLL 配不配得上这份托管代码"** 这个配对问题。
本脚本把配对变成一个可判定的退出码：

  1. 托管 `Fff3FpEngine.ConfigVersion` 必须**恰好等于**该 DLL 的 `FFF3FP_GetApiVersion()`；
     不等 ⇒ 运行时每个会话创建都被内核拒（"内核 N != 托管 M"），整轮自测在演示模式上空跑。
  2. 托管 P/Invoke 引用的 25 个入口必须**全部存在**于该 DLL 的导出表；
     缺一个就是一次 `EntryPointNotFoundException`（`AppLog` 装日志汇那处没有 try）。

导出表与版本号的解析**复用** `check_kernel_exports.py`（那份 PE 解析已被门禁与多次实测校验过），
这里不再写第二份 —— 同一个格式两处解析是本仓反复踩过的坑。

诚实的限制：**本脚本判不出"这份 DLL 是从哪棵树编出来的"**。导出表里没有来源信息，
所以"README 要求应用内核必须来自 `3fp/kernel`（`third_party` 那份缺 `IsWideGamutSource` 广色域门禁）"
这条只能靠 csproj 的 `KernelDllPath` 指对地方来保证。当前两者恰好还有旁证：两棵树的 API 版本不同
（15 vs 16），版本一项就能挡住；哪天两边版本号对上，这一项就失去鉴别力 —— 那时要加的是可观测的
版本内指纹（比如构建戳导出），不要假装现在就能查出来。

用法（Git Bash / PowerShell 均可）：
    py tools/check_kernel_pairing.py <FFF.Native.dll 路径>
    py tools/check_kernel_pairing.py <dll> --managed-cs src/3FCompare.Core/Backend/Fff3FpEngine.cs

输出刻意只用 ASCII：MSBuild/门禁管道常在 cp936 控制台上，CJK 与箭头字符会让 print 本身抛
UnicodeEncodeError，一个因为编码而跑不起来的守卫等于没有守卫。
退出码：0 = 配对可用；1 = 版本不配对或缺必需导出；2 = 用法/文件错误。
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_kernel_exports as kx  # noqa: E402  (复用同一份 PE 解析)

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_MANAGED_CS = os.path.join(REPO, "src", "3FCompare.Core", "Backend", "Fff3FpEngine.cs")


def managed_config_version(cs_path):
    """从托管侧读 ConfigVersion。读不出来就报错退出，绝不返回一个默认值蒙混。"""
    with open(cs_path, encoding="utf-8-sig") as fh:   # utf-8-sig: 本仓 .cs 带 BOM
        text = fh.read()
    m = re.search(r"ConfigVersion\s*=\s*(\d+)", text)
    if not m:
        raise SystemExit("FAIL: ConfigVersion not found in %s (did the field name change? "
                         "this guard must then be updated, not skipped)" % cs_path)
    return int(m.group(1))


def _read(path):
    with open(path, encoding="utf-8-sig") as fh:   # utf-8-sig: 本仓 .cs 带 BOM；.cpp 无 BOM 也读得动
        return fh.read()


def managed_dest_version(engine_cs):
    """托管侧向 FFF3FP_GetRenderTargetInfo 申报的 ABI 版本（`new Fff3FpRenderTargetInfo { Version = N }`）。"""
    text = _read(engine_cs)
    # 锚必须是 `new Fff3FpRenderTargetInfo` 那处初始化器，不能是类型名第一次出现的地方 ——
    # Marshal.SizeOf<Fff3FpRenderTargetInfo>() 在文件里更早（差 500 多行），拿类型名当锚会直接
    # 在窗口里找不到 Version 而误判红（本函数首版就这么错过了一次）。
    i = text.find("new Fff3FpRenderTargetInfo")
    if i < 0:
        raise SystemExit("FAIL: no 'new Fff3FpRenderTargetInfo' initializer in %s "
                         "(did the call site change? this guard must then be updated, not skipped)"
                         % engine_cs)
    m = re.search(r"Version\s*=\s*(\d+)", text[i:i + 900])
    if not m:
        raise SystemExit("FAIL: no 'Version = N' inside the Fff3FpRenderTargetInfo initializer in %s"
                         % engine_cs)
    return int(m.group(1))


def kernel_dest_version(dll_path):
    """内核会话层要求的 RenderTargetInfo 版本：从 DLL 所在树往上找 PlayerSession.cpp。

    v1 与 v2（destX/destY 有符号）**字节宽度相同**，导出表和 API 版本两项都分不出来 ——
    2026-09-29 就是靠这一点漏掉了半边回补，所以这里比的是**源码里的门槛**，并在结论里
    把两侧路径都打出来，避免"比了又好像没比"。
    """
    p = os.path.abspath(dll_path)
    for _ in range(4):
        p = os.path.dirname(p)
        cand = os.path.join(p, "3FP", "Core", "PlayerSession.cpp")
        if os.path.isfile(cand):
            text = _read(cand)
            i = text.find("PlayerSession::GetRenderTargetInfo")
            if i < 0:
                return cand, None
            m = re.search(r"info\.version\s*!=\s*(\d+)", text[i:i + 1200])
            return cand, (int(m.group(1)) if m else None)
    return None, None


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dll", help="待嵌入/部署的 FFF.Native.dll")
    ap.add_argument("--managed-cs", default=DEFAULT_MANAGED_CS,
                    help="托管侧版本常量所在文件（默认 Fff3FpEngine.cs）")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.dll):
        print("FAIL: kernel dll not found: %s" % args.dll)
        return 2

    want = managed_config_version(args.managed_cs)
    want_dest = managed_dest_version(args.managed_cs)
    # probe_api_version 的契约是 (api, err) 二者必居其一 —— 直接当标量用会让"版本相符"
    # 也被判成不符（本脚本首版就这么假红过一次）。
    got, api_err = kx.probe_api_version(args.dll)
    names = set(kx.exports(args.dll))
    missing = [e for e in kx.REQUIRED if e not in names]
    dest_src, got_dest = kernel_dest_version(args.dll)

    print("kernel      : %s" % args.dll)
    print("exports     : %d" % len(names))
    print("api version : kernel=%s managed=%d" % (
        got if got is not None else "unreadable(%s)" % api_err, want))
    print("dest abi    : managed Version=%d, kernel %s" % (
        want_dest,
        ("source %s requires %d" % (dest_src, got_dest) if got_dest is not None
         else "unreadable (找不到 %s 里的 info.version != N)"
              % (dest_src or "PlayerSession.cpp 于该 DLL 路径的上四层内"))))
    if missing:
        print("missing     : %s" % ", ".join(missing))

    bad = []
    if got is None:
        bad.append("kernel API version unreadable (%s) => cannot prove the pairing, "
                   "so this guard refuses to pass" % api_err)
    elif got != want:
        bad.append("API version mismatch: kernel %s != managed %d "
                   "=> every session creation is rejected at runtime and the app silently "
                   "falls back to demo mode" % (got, want))
    if missing:
        bad.append("%d required entry point(s) absent => EntryPointNotFoundException "
                   "(the kernel-log sink install in AppLog has no try/catch)" % len(missing))
    # dest ABI 这一项按"量不到就拦"处理：v1/v2 布局同宽，静态面看不出来，源码门槛是
    # 目前唯一能判的地方；找不到源码树就说明这条没比过，不能算通过。
    if got_dest is None:
        bad.append("cannot read the kernel-side RenderTargetInfo version gate => destX/destY "
                   "signedness (v1 vs v2) is UNVERIFIED, and v1 truncates negative origins into "
                   "huge unsigned values")
    elif got_dest != want_dest:
        bad.append("RenderTargetInfo abi mismatch: kernel requires version %d, managed declares %d "
                   "=> GetRenderTargetInfo returns InvalidArgument and every Dest-based readout "
                   "(follow-the-hand / anchor / probe mapping / pixel fidelity ratio) goes silent"
                   % (got_dest, want_dest))

    if bad:
        for b in bad:
            print("FAIL: %s" % b)
        return 1
    print("OK: kernel matches the managed binding")
    return 0


if __name__ == "__main__":
    sys.exit(main())
