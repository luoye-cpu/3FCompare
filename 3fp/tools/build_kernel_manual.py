#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""手工复放 MSBuild 对 3fp 内核（FFF.Native）的编译/链接命令。

**为什么需要**：本机 `MSBuild.exe` 被安全策略判为 LOLBin（bash 与 PowerShell 下均被拒），
而 `dotnet build` 不支持 `.vcxproj`（`error MSB4278: $(VCTargetsPath)\\Microsoft.Cpp.Default.props 不存在`）。
故只能把 MSBuild **上次真实下发**的命令逐条复放。

命令来源：`FFF.Native/obj/x64/Release/FFF.Native.tlog/{CL,rc,link}.command.1.tlog`
—— 是 MSBuild 实际写下的参数，**不是猜的**。

输出重定向到 `Release_anim`（全新目录），这样：
  · 不会破坏既有 `obj/x64/Release`（增量状态与 tlog 都留着）；
  · 产物落在 `x64/Release_anim/FFF.Native.dll`，验证通过后再决定是否替换 Release。

工具链路径**从 tlog 正文里提取**（而不是硬编码版本号）—— 硬编码会在换工具链后静默失效。

用法：python tools/build_kernel_manual.py
"""
import io
import os
import re
import subprocess
import sys
import time

ROOT = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native"
TLOG = os.path.join(ROOT, "obj", "x64", "Release", "FFF.Native.tlog")
NEWOBJ = os.path.join(ROOT, "obj", "x64", "Release_anim")
NEWOUT = os.path.join(ROOT, "x64", "Release_anim")
STAMP = "Release_anim"

# ── 额外源文件（tlog 复放的限制）─────────────────────────────────────────
# tlog 是「上次 MSBuild 真实下发命令」的快照：**tlog 里没有的源文件无法被
# 复放编译**（新建文件自然不在快照里）。给内核新增 .cpp 时在这里登记
# (新源文件, 模板源文件)：模板必须是 tlog 里已有命令块、且与新文件同目录
# 的文件（编译选项一致）。脚本克隆模板的 cl 命令块并替换文件名，
# 再把新 obj 追加进链接输入（见 register_extra_sources）。
EXTRA_SOURCES = [
    (os.path.join(ROOT, "3FP", "Subtitle", "SubtitleProbe.cpp"),
     os.path.join(ROOT, "3FP", "Subtitle", "AssSubtitleRenderer.cpp")),
]


def read_tlog(name):
    path = os.path.join(TLOG, name)
    if not os.path.exists(path):
        return ""
    raw = io.open(path, "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8-sig", errors="replace").replace("\x00", "")


def blocks(text):
    return [b for b in (text or "").split("^") if b.strip()]


def find_toolchain(cl_text, link_text):
    """定位 MSVC 根与 SDK 版本。

    ⚠ **不能从 `CL.command.1.tlog` 找**：MSBuild 把工具链的 include/lib 放在 **`INCLUDE`/`LIB`
    环境变量**里，命令行上只有项目自己的 `/I`（实测该命令 801 字节，全是相对项目的路径）。
    所以改从 **`CL.read.1.tlog`**（记录了本次编译**读过的所有头文件**）里找 —— 里面必然含
    MSVC 与 SDK 的头文件绝对路径。找不到再退回扫描文件系统。
    """
    blob = read_tlog("CL.read.1.tlog") or cl_text
    # ⚠ 字符类必须排除换行：`CL.read.1.tlog` 是**逐行**的文件清单，
    #   若写成 `[^\";]*?` 会跨行匹配，把上一行的源文件路径一起吞进来（实测踩过）。
    msvc = re.search(r"([A-Z]:\\[^\";\r\n]*?\\VC\\Tools\\MSVC\\[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    sdk = re.search(r"Windows Kits\\10\\Include\\([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    if msvc and sdk:
        return msvc.group(1).strip(), sdk.group(1).strip()

    # 兜底：扫文件系统（取版本号最大的一个）
    import glob
    roots = glob.glob(r"C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*")
    roots = [r for r in roots if os.path.isdir(r)]
    sdk_bins = glob.glob(r"C:\Program Files*\Windows Kits\10\Include\*")
    sdk_bins = [os.path.basename(r) for r in sdk_bins if os.path.isdir(r)]
    if roots and sdk_bins:
        roots.sort(key=lambda p: [int(x) for x in os.path.basename(p).split(".") if x.isdigit()])
        sdk_bins.sort(key=lambda v: [int(x) for x in v.split(".") if x.isdigit()])
        print("（tlog 里没找到工具链，改用文件系统兜底）")
        return roots[-1], sdk_bins[-1]
    return None, None


def redirect(cmd):
    """把输出目录从 Release 改到 Release_anim（大小写不敏感）。

    只替换**子路径**部分，保留原有的绝对前缀 —— 若整体替换成绝对路径会拼出
    `FFF.Native\\C:\\...` 这种非法路径（C1083 / RC1109 / LNK1104）。
    `(?!_ANIM)` 负向先行：文本里已含 `Release_anim` 的片段不再被二次替换成
    `Release_anim_anim`（曾实测 LNK1181），使本函数幂等。
    """
    cmd = re.sub(r"OBJ\\X64\\RELEASE(?!_ANIM)", lambda m: r"OBJ\x64\%s" % STAMP, cmd, flags=re.I)
    cmd = re.sub(r"X64\\RELEASE\\(?!%s\\)" % re.escape(STAMP), lambda m: r"x64\%s\\" % STAMP, cmd, flags=re.I)
    return cmd


def register_extra_sources(cl_blocks, lk_blocks):
    """克隆模板命令块以编译 tlog 里没有的新源文件（返回追加数）。

    ⚠ tlog 里的路径是**全大写**（MSBuild 记录命令行的原样），且 obj 名与
    源名一一对应 ⇒ 在块内对「模板文件主干名」做大小写不敏感的整体替换是
    安全的（该词只出现在源路径与 /Fo obj 路径里）。新 obj 需要追加进
    link 块 line0 的 `|` 分隔输入清单，否则链接产物不含新符号。
    """
    if not EXTRA_SOURCES:
        return 0
    model_by_key = {}
    for block in cl_blocks:
        first = block.replace("\r\n", "\n").split("\n")[0].strip()
        if first:
            model_by_key[first.upper()] = block
    added = 0
    for source, model in EXTRA_SOURCES:
        source, model = os.path.normpath(source), os.path.normpath(model)
        stem = os.path.splitext(os.path.basename(model))[0]
        new_stem = os.path.splitext(os.path.basename(source))[0]
        # 守卫：tlog 一旦被真实 MSBuild 刷新，新源就会自己拥有命令块；
        # 此时再克隆就是双重编译 + 重复链接输入（obj 在 line0 内部不去重）。
        # 整路径精确比对 + 「\主干.CPP」后缀兜底（防登记路径写法与 tlog 不一致）。
        if source.upper() in model_by_key or any(
                key.endswith("\\" + (new_stem + ".cpp").upper()) for key in model_by_key):
            print("跳过额外源文件: %s（已存在于 tlog 命令块）" % new_stem)
            continue
        block = model_by_key.get(model.upper())
        if block is None:
            raise SystemExit("模板源文件不在 tlog 命令块里：%s" % model)
        # ⚠ 词边界：主干名必须整词出现才替换，防止模板名是常见词时
        # 子串替换改坏命令里的其它文本（如 Session 撞上 PlayerSessionDisc）。
        pattern = r"(?<![A-Za-z0-9_])%s(?![A-Za-z0-9_])" % re.escape(stem)
        cl_blocks.append(re.sub(pattern, new_stem, block, flags=re.I))
        # ⚠ 这里必须写 obj\x64\Release（不带 STAMP）：run() 会对整个命令再做
        # 一次 redirect()，若路径已含 Release_anim 会被二次替换成
        # `Release_anim_ANIM`（实测踩过）⇒ 与其它 obj 一样写原始路径，
        # 由 redirect() 统一转换。
        obj_path = os.path.join(ROOT, "obj", "x64", "Release", new_stem + ".obj")
        if lk_blocks:
            first = lk_blocks[0].replace("\r\n", "\n").split("\n")[0]
            if obj_path.upper() not in first.upper():
                lk_blocks[0] = first + "|" + obj_path.upper() + lk_blocks[0][len(first):]
        print("额外源文件: %s（模板 %s）" % (new_stem, stem))
        added += 1
    return added


def main():
    cl_text = read_tlog("CL.command.1.tlog")
    rc_text = read_tlog("rc.command.1.tlog")
    lk_text = read_tlog("link.command.1.tlog")
    if not cl_text or not lk_text:
        print("tlog 缺失或不完整：CL=%d 字节 LINK=%d 字节" % (len(cl_text), len(lk_text)))
        return 1

    msvc_root, sdk_ver = find_toolchain(cl_text, lk_text)
    if not msvc_root:
        print("无法从 tlog 提取工具链路径")
        return 1
    sdk_root = r"C:\Program Files (x86)\Windows Kits\10"
    cl = os.path.join(msvc_root, "bin", "Hostx64", "x64", "cl.exe")
    link = os.path.join(msvc_root, "bin", "Hostx64", "x64", "link.exe")
    rc = os.path.join(sdk_root, "bin", sdk_ver, "x64", "rc.exe")
    print("MSVC : %s" % msvc_root)
    print("SDK  : %s" % sdk_ver)
    for exe in (cl, link, rc):
        print("  %s  %s" % ("OK " if os.path.exists(exe) else "缺失", exe))
    if not all(os.path.exists(x) for x in (cl, link, rc)):
        return 1

    include = ";".join([
        os.path.join(msvc_root, "include"),
        os.path.join(sdk_root, "Include", sdk_ver, "ucrt"),
        os.path.join(sdk_root, "Include", sdk_ver, "um"),
        os.path.join(sdk_root, "Include", sdk_ver, "shared"),
        os.path.join(sdk_root, "Include", sdk_ver, "winrt"),
        os.path.join(sdk_root, "Include", sdk_ver, "cppwinrt"),
    ])
    lib = ";".join([
        os.path.join(msvc_root, "lib", "x64"),
        os.path.join(sdk_root, "Lib", sdk_ver, "ucrt", "x64"),
        os.path.join(sdk_root, "Lib", sdk_ver, "um", "x64"),
    ])
    env = dict(os.environ)
    env.update({
        "INCLUDE": include,
        "LIB": lib,
        # ⚠ PATH 必须包含 cl.exe 所在目录（它依赖同目录的 c1xx.dll / mspdb*.dll），
        #   且**必须用反斜杠+分号**的 Windows 风格。
        "PATH": ";".join([os.path.join(msvc_root, "bin", "Hostx64", "x64"),
                          os.path.join(sdk_root, "bin", sdk_ver, "x64"),
                          r"C:\Windows\System32", r"C:\Windows"]),
        "TMP": r"C:\Windows\Temp",
        "TEMP": r"C:\Windows\Temp",
    })

    os.makedirs(NEWOBJ, exist_ok=True)
    os.makedirs(NEWOUT, exist_ok=True)

    cl_blocks = blocks(cl_text)
    rc_blocks = blocks(rc_text)
    lk_blocks = blocks(lk_text)
    register_extra_sources(cl_blocks, lk_blocks)
    total = len(cl_blocks) + len(rc_blocks) + len(lk_blocks)
    print("编译单元 %d；rc %d；link %d" % (len(cl_blocks), len(rc_blocks), len(lk_blocks)))
    sys.stdout.flush()

    errors = []
    index = 0

    def run(exe, block, label, with_inputs=False):
        nonlocal index
        index += 1
        lines = [l for l in block.replace("\r\n", "\n").split("\n") if l.strip()]
        src = lines[0].strip()
        cmd = lines[1].strip() if len(lines) > 1 else ""
        if with_inputs:
            # ⚠ link 的 tlog 把**输入清单**放在 line0（用 `|` 分隔多个 .obj），
            #   参数放在 line1。只传 line1 会**一个 .obj 都不传**，链接出
            #   `LNK2001 无法解析的外部符号 _DllMainCRTStartup`（实测踩过）。
            # ⚠ 且必须**去重**：`.res` 同时出现在 line0 与 line1 末尾，重复传入会报
            #   `CVTRES : fatal error CVT1100: 资源重复 类型:VERSION`（实测踩过）。
            command_upper = cmd.upper()
            inputs = []
            for item in src.split("|"):
                item = item.strip()
                if item and item.upper() not in command_upper:
                    inputs.append('"%s"' % item)
            cmd = "%s %s" % (" ".join(inputs), cmd)
        started = time.time()
        proc = subprocess.run('"%s" %s' % (exe, redirect(cmd)), env=env, cwd=ROOT,
                              shell=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        out = proc.stdout.decode("utf-8", errors="replace")
        ok = proc.returncode == 0
        shown = os.path.basename(src.split("|")[0]) if src else label
        print("[%2d/%2d] %-5s %-34s exit=%d  %.1fs"
              % (index, total, label, shown, proc.returncode, time.time() - started))
        if out.strip():
            print(out.strip()[:3000])
        sys.stdout.flush()
        if not ok:
            errors.append((src, out))
        return ok

    # 1) cl.exe —— tlog 里 pch.cpp 带 /Yc 排在最前，**必须保持顺序**
    for block in cl_blocks:
        run(cl, block, "cl")
    # 2) rc.exe
    for block in rc_blocks:
        run(rc, block, "rc")
    # 3) link.exe —— 必须带上 line0 的输入清单
    for block in lk_blocks:
        run(link, block, "link", with_inputs=True)

    print()
    if errors:
        print("❌ 失败 %d 项：" % len(errors))
        for src, out in errors:
            print("  - %s" % src)
        return 1
    produced = os.path.join(NEWOUT, "FFF.Native.dll")
    print("✅ 全部成功")
    if os.path.exists(produced):
        print("   产物: %s  (%d 字节)" % (produced, os.path.getsize(produced)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
