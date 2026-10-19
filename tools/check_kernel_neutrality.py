#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本体中性判据：证明"这个二进制/这份工程里没有空间音频的受限制实现"。

需求（2026-10-13 三轮定稿）：**本体只许含不侵权的内容。**
 - 不侵权 = 公开免版税标准的实现，**包括第三方库**（libiamf/OAR/OBR/pffft/absl、opus、FLAC）
   ⇒ 允许留在本体，相应能力（IAMF 场景、元素混音、双耳 HRTF）**不降级**。
 - 侵权风险 = 需授权才写得出来/用得了的实现（Dolby Vision、FDK-AAC、MPEG-H、AC-4、DTS:X、AACS…）
   ⇒ 不得进本体，只能待在外部 DLL 或随包 FFmpeg 里（用户口径：那两者不算 3FP 本体）。
外部 DLL 自身的授权不归本脚本管——它只管一件事：
**本体里还有没有"需授权"的内容，并且这句话是有读数支撑的，不是假设。**

（本判据的第一版把"第三方库"整体判红、第二版又把免版税的 IAMF 能力算作违规，方向都反了；
 现在判的是"要不要授权"，不是"谁写的"。分层理由写在 BINARY_FATAL 的注释里。）

为什么需要一个脚本而不是一句声明：2026-10-13 实测，`pack.ps1` 与 `tools/发布门禁.ps1`
对 `license|nonfree|gpl|copying` 是 **0 命中**，`publish/` 下许可证文本 **0 份**；
"本体不含 X"过去从来没有任何判据验证过。没有牙的承诺会被下一次链接顺手破坏。

判据形状（四条，缺一即不完整）：
  1. 二进制层：解出文件的原始字节，对受限制实现的符号/路径/库名串计数。
     **先跑正控制**（内核件里必然存在的串必须命中若干），否则扫描器是死的，
     全 0 不能读成"干净"——这是本脚本最容易骗自己的地方。
  2. 工程层（--project）：vcxproj 的 `AdditionalDependencies` 里出现
     iamf/oar/obr/pffft/absl 即判红。静态库被链进来，比字符串更根本。
  3. 自检（--self-test）：把禁用词自己拼进一段字节里，要求扫描器逐条抓到。
     这证明词表与读取路径没坏（编码/大小写/切片错误都逃不掉）。
  4. **反向存在性判据**（2026-10-14 补，J1）：`BINARY_ALLOWED` 的每一项都必须
     被**证实链入**，缺任何一项即判红。此前 `BINARY_ALLOWED` 只有第 65 行的定义、
     0 个使用者（3fp/docs/94 §未修 第 9 条），于是这套判据只证明了"没有需授权的
     东西"，从没证明"该有的东西还在"。历史事故是真的：某配置缺 `FFF_WITH_LIBIAMF`
     ⇒ 出货版完全没有 IAMF 支持，而验证全跑在另一个配置上，所以**全绿**。
     判在哪一层（实测过，不硬造一条永远为假的断言）：`*.lib` 是**链接期文件名**，
     不会出现在链接后的 DLL 里——实测出货内核 FFF.Native.dll（5,398,016 字节）
     `iamf.lib`/`oar.lib`/`obr.lib`/`obr_capi.lib`/`pffft.lib` 全 **0 命中**，
     只有 `absl`=28 / `FLAC`=57 / `opus`=98 有串（那是命名空间/类名，不是库文件名
     的证据）。⇒ 存在性一律在**工程层**（`AdditionalDependencies`）判，
     二进制层命中只作**佐证**打印；二进制 0 命中**不构成**缺失证据。
     缺项的标签是「能力被摘除」，**不是**「安全违规」——两件事不许混在一个标签下。

用法：
    py tools/check_kernel_neutrality.py --self-test
    py tools/check_kernel_neutrality.py <FFF.Native.dll> [...] [--project 路径.vcxproj]
    （存在性判据要跑就必须带 --project；不带时本脚本判红，不静默跳过。）
退出码：0=干净；1=有命中或正控制缺失或免版税项被摘除；2=用法/读不到文件（fail-closed）。

注意词表里为什么没有孤立的 `oar`：它会命中 "board"/"soar" 这类无关词，
判据一旦误报，下一次就有人把它放宽——那是比红更坏的结果。用 `oar.lib`、`oar/`、
`OAR_`、`oar::` 这些带边界的形态。
"""
import argparse
import io
import os
import re
import sys

# 判据口径（2026-10-13 三轮更正；前两版方向都判反了，这里把理由写死防再犯）：
#   本体只许含**不侵权**的内容。**公开免版税标准的实现**——包括第三方库
#   libiamf/OAR/OBR/pffft/absl 与 opus/FLAC——属这一类：**留在本体，能力不降级**。
#   **需授权才写得出来/用得了的实现**（Dolby Vision、FDK-AAC、MPEG-H、AC-4、DTS:X、AACS…）
#   才是违规，它们只能待在外部 DLL 或随包 FFmpeg 里（用户口径：那两者不算本体）。
#   ⇒ FATAL 判的是"需授权族的实现 + 它的对外宣告"，**不是"是不是第三方库"**。
BINARY_FATAL = (
    # Dolby Vision：需 Dolby 授权（依据 3fp/docs/58:105）。实现符号、ABI 字段名、文案都算
    "av_dovi", "AVDOVI", "dovi_meta", "DOVI_METADATA", "dolbyVision", "Dolby Vision",
    # FDK-AAC / AAC+：nonfree，不可自由再分发
    "fdk-aac", "libfdk", "fdkaac", "aacplus",
    # 其余需授权族：本体今天没有，写在这儿是为了让它们**进不来**
    "mpegh", "MPEG-H", "ac4_", "AC4_", "ffdac", "dtsxsym", "aacs_", "libaacs", "bdplus",
)
# 参考级：公开标准名、我们自己的标识符、无实现的探测/提示文案。报告但不判红。
BINARY_ADVISORY = (
    "IAMF_", "libiamf", "OAR_", "obr_capi", "pffft", "absl", "IamfAudioDecoder",
    "BedBinauralRenderer", "binaural", "HRTF", "HDR Vivid", "HDR10+", "HLG",
    "x265", "svtav1", "AACS decoder", "BD+ decoder", "demix", "mixing", "synchro",
)
# 明确允许留在本体的免版税依赖（记下来，防止下一轮又把它们判红）
# ⚠ 这份清单**双向生效**（2026-10-14，J1）：既"出现时不判红"，也必须"存在时被证实"。
#    只有前半句话时它是死代码（0 个使用者），IAMF 整块被摘掉照样打印"本体干净"。
BINARY_ALLOWED = ("iamf.lib", "oar.lib", "obr.lib", "obr_capi.lib", "pffft.lib",
                  "absl", "opus", "FLAC")

# 正控制：内核件里**必然**存在的串。一个都不命中 ⇒ 我读的不是内核件，或我读坏了。
BINARY_CONTROLS = ("avcodec", "swresample", "D3D11", "FFF.Native", "LoadLibrary")

# 工程层：判"有没有**需授权**的库被静态链进本体"。
# 三轮更正后只剩一条真违规：`fdk-aac.lib`（nonfree）。
# libiamf/oar/obr/pffft/absl/opus/FLAC 是免版税标准实现，**允许链入本体**，故不在此列。
PROJECT_FORBIDDEN = (r"fdk-aac\.lib", r"libfdk", r"fdkaac", r"mpegh", r"\bac4\b", r"aacs", r"bdplus")
# 免版税依赖清单：报告它们在工程层的出现，方便看"本体到底链了什么"，不判红。
PROJECT_ALLOWED_NOTED = (r"iamf\.lib", r"oar\.lib", r"obr\.lib", r"pffft\.lib",
                         r"absl[\\/]", r"opus\.lib", r"FLAC\.lib")


def read_bytes(path):
    with open(path, "rb") as handle:
        return handle.read()


def count_tokens(data, tokens):
    """返回 {词: (次数, 首个命中偏移)}，字节级匹配。

    按小写去重：`IAMF_` 与 `iamf_` 是同一条判据，都列进去会把命中数翻倍——
    那正是"累计读数吹大分母"的老坑，读数必须与点名条数相等。
    """
    low = data.lower()
    out = {}
    seen = set()
    for token in tokens:
        needle = token.lower().encode("utf-8", "replace")
        if needle in seen:
            continue
        seen.add(needle)
        first = low.find(needle)
        if first < 0:
            continue
        out[token] = (low.count(needle), first)
    return out


def context_at(data, offset, width=48):
    seg = data[max(0, offset - width): offset + width]
    return re.sub(rb"[^\x20-\x7e]+", b"|", seg).decode("ascii", "replace").strip("|")


def scan_binary(path, show_context=True):
    data = read_bytes(path)
    fatal = count_tokens(data, BINARY_FATAL)
    advisory = count_tokens(data, BINARY_ADVISORY)
    controls = count_tokens(data, BINARY_CONTROLS)
    print("%s  (%d 字节)" % (path, len(data)))
    if not controls:
        print("   [FAIL] 正控制全部未命中（%s）——扫描器对这个文件没读到应有内容，"
              "全 0 不可读成'干净'" % ", ".join(BINARY_CONTROLS))
        return None, fatal, controls
    print("   正控制命中 %d/%d：%s" % (
        len(controls), len(BINARY_CONTROLS),
        ", ".join("%s=%d" % (k, v[0]) for k, v in sorted(controls.items()))))
    if not fatal:
        print("   判红级痕迹：0 命中 —— 本体中性判定成立")
        if advisory:
            print("   参考级（不判红）：%s" % ", ".join(
                "%s=%d" % (k, v[0]) for k, v in sorted(advisory.items())))
        return True, fatal, controls
    print("   判红级痕迹：%d 个词命中，共 %d 次：" % (
        len(fatal), sum(v[0] for v in fatal.values())))
    for token, (count, offset) in sorted(fatal.items(), key=lambda kv: -kv[1][0]):
        line = "     %-22s %5d 次 @%d" % (token, count, offset)
        if show_context:
            line += "  上下文: %s" % context_at(data, offset)[:72]
        print(line)
    if advisory:
        print("   参考级（不判红）：%s" % ", ".join(
            "%s=%d" % (k, v[0]) for k, v in sorted(advisory.items())))
    return False, fatal, controls


def scan_project(path):
    text = io.open(path, encoding="utf-8-sig", errors="replace").read()
    bad = []
    noted = []
    for number, line in enumerate(text.splitlines(), 1):
        for pattern in PROJECT_FORBIDDEN:
            if re.search(pattern, line, re.I):
                bad.append((number, pattern, line.strip()[:120]))
                break
        for pattern in PROJECT_ALLOWED_NOTED:
            if re.search(pattern, line, re.I):
                noted.append((number, pattern))
                break
    print("%s" % path)
    if noted:
        rows = sorted(set(n[0] for n in noted))
        print("   工程层·免版税依赖（允许链入，不判红）：命中 %d 行，行号 %s"
              % (len(rows), ",".join(str(r) for r in rows[:12])))
    if not bad:
        print("   工程层·需授权库：0 行命中 —— 判定干净")
        return True
    print("   工程层·需授权库：%d 行命中" % len(bad))
    for number, pattern, line in bad[:12]:
        print("     :%-5d /%s/  %s" % (number, pattern, line))
    if len(bad) > 12:
        print("     （另有 %d 行同类命中）" % (len(bad) - 12))
    print("     合计 %d 行" % len(bad))
    return False


# ── 反向判据（J1，2026-10-14）：免版税库**必须存在** ────────────────────────────
# 与上面三层的口径正交：那三层判"不该有的东西不在"，这一条判"该有的东西还在"。
# 缺项 ⇒ 判红，但红的是「能力被摘除」（出货版丢了 IAMF/OAR 支持），
# **不是**「安全违规」；门禁侧的标签也必须按这个区分，否则下一次有人拿"没违规"
# 去解释"功能没了"。
def link_dependencies(text):
    """取出 vcxproj 里所有 <AdditionalDependencies> 条目（分号/逗号/空白分隔）。

    只认这个标签，不认整份文件——`pffft.lib` 之类也出现在注释和其它标签里，
    在整份文本上找子串会把"没链"读成"链了"（假绿）。
    `%(AdditionalDependencies)` 是 MSBuild 继承占位，不算依赖项。
    """
    entries = []
    for match in re.finditer(r"<AdditionalDependencies>(.*?)</AdditionalDependencies>",
                             text, re.I | re.S):
        for piece in re.split(r"[;,\s]+", match.group(1)):
            piece = piece.strip().strip('"')
            if piece and not piece.startswith("%("):
                entries.append(piece)
    return entries


def dependency_proof(token, entries):
    """在链入清单里给 token 找证据；找不到返回 None（= 该项未被证实链入）。

    带 `.lib` 的按**基名精确匹配**（`obr.lib` 不能被 `obr_capi.lib` 顶替——
    子串匹配会把"只链了一半"读成"全链了"）；
    不带的（absl/opus/FLAC）按路径段包含匹配（真实清单里 absl 是
    `absl\absl_base.lib` 这样的**一条目录下的几十个库**，逐项精确匹配会恒假）。
    """
    low = token.lower()
    for entry in entries:
        norm = entry.replace("\\", "/").lower()
        if low.endswith(".lib"):
            if norm.rsplit("/", 1)[-1] == low:
                return entry
        elif low in norm:
            return entry
    return None


def binary_corroboration(token, paths):
    """二进制层佐证：{token: 命中数}。只打印，**不参与判定**。

    `*.lib` 在链接后的 DLL 里本就 0 命中，所以这里的 0 绝不能读成"没链"。
    """
    hits = {}
    for path in paths:
        try:
            data = read_bytes(path)
        except OSError:
            continue
        low = data.lower()
        needle = token.lower().encode("utf-8", "replace")
        count = low.count(needle)
        if count:
            hits[path] = hits.get(path, 0) + count
    return hits


def scan_allowed_presence(project_paths, binary_paths):
    """BINARY_ALLOWED 每一项都必须在工程层被证实链入。

    返回 (齐全?, 缺失项列表, 打印出的读数行数)。
    fail-closed：没有 --project 就没有对象 ⇒ 判红（"没跑"不等于"跑出来是齐的"）。
    """
    total = len(BINARY_ALLOWED)
    if not project_paths:
        print("   免版税存在性·工程层：无对象（未传 --project）—— 反向判据没跑过，"
              "不能读成'能力齐全'（0/%d）" % total)
        return False, list(BINARY_ALLOWED), 0
    all_missing = []
    rows = 0
    for path in project_paths:
        try:
            text = io.open(path, encoding="utf-8-sig", errors="replace").read()
        except OSError as ex:
            # 读不到就是没有对象：点名判红，不许让这一项从读数里静默消失（也别抛栈）
            print("%s -> [FAIL] 工程层存在性判据读不到该工程（%s），不能读成'能力齐全'" % (path, ex))
            rows += 1
            all_missing.extend(BINARY_ALLOWED)
            continue
        blocks = [m.group(1) for m in re.finditer(
            r"<AdditionalDependencies>(.*?)</AdditionalDependencies>", text, re.I | re.S)]
        entries = link_dependencies(text)
        present = []
        missing = []
        for token in BINARY_ALLOWED:
            proof = dependency_proof(token, entries)
            if proof is None:
                missing.append(token)
            else:
                present.append((token, proof))
        rows += 1
        print("%s" % path)
        if not entries:
            # 清单为空=解析失效，与"链了别的"是两种病；单独立一行读数让人一眼看见。
            print("   免版税存在性·工程层：0/%d（AdditionalDependencies 解析到 0 项 —— "
                  "标签名/编码不对，判据没对象，不能读成'能力齐全'）" % total)
        elif not missing:
            print("   免版税存在性·工程层：%d/%d 证实链入 —— 能力未被摘除"
                  % (len(present), total))
        else:
            print("   免版税存在性·工程层：%d/%d —— 缺 %d 项【能力被摘除，不是安全违规】：%s"
                  % (len(present), total, len(missing), ", ".join(missing)))
        # 按块读数：**判定用并集**（任一块链到即算链入），但把每块的齐全度打出来。
        # 理由：本仓的 vcxproj 按 Configuration|Platform 分块，Win32 块与只加 bluray.lib
        # 的 x64 块本就不带空间音频依赖，按"每块都必须齐全"判会恒红（假红比红更坏）。
        # 于是这里只把"有几个块、几个块齐全"摊开给人看——某配置单独漏掉依赖时，
        # 这两个数会不等，日志里看得见（判据本身仍按并集拦"整块被摘除"那种事故）。
        full_blocks = 0
        for body in blocks:
            bentries = link_dependencies("<AdditionalDependencies>%s</AdditionalDependencies>" % body)
            if all(dependency_proof(t, bentries) for t in BINARY_ALLOWED):
                full_blocks += 1
        print("   免版税存在性·按块：%d 块含链接依赖，其中 %d 块 8/8 齐全"
              "（判定按并集；块数不等只说明有些块天生不带这套依赖，不单独判红）"
              % (len(blocks), full_blocks))
        for token, proof in present:
            print("     %-14s 链入项: %s" % (token, proof))
        for token in missing:
            print("     %-14s 未在任何 AdditionalDependencies 里找到" % token)
        all_missing.extend(missing)
    if binary_paths:
        notes = []
        for token in BINARY_ALLOWED:
            hits = binary_corroboration(token, binary_paths)
            if hits:
                notes.append("%s=%s" % (token, "/".join(str(v) for v in hits.values())))
        print("   免版税存在性·二进制佐证（仅佐证：`*.lib` 是链接期文件名，"
              "链好的 DLL 里本就 0 命中，0 命中不构成缺失证据）：%s"
              % (", ".join(notes) if notes else "无可佐证串"))
    return (not all_missing), all_missing, rows


# 源码层：二进制**看不见**的"需授权实现"——include 与被解释的结构类型/被调的库函数。
# 本轮实测就是这么抓出来的：`HdrProcessor.cpp:9 #include <libavutil/dovi_meta.h>`、
# `:196 sizeof(AVDOVIDecoderConfigurationRecord)`、`:303 av_dovi_get_header(metadata)`
# —— 出货件里只有 1 个 `av_dovi_find_level` 导入串，二进制层看不到那几行解析代码。
SOURCE_FATAL = (
    r"#\s*include\s*[<\"]libavutil/dovi_meta\.h",
    r"\bAVDOVI[A-Za-z_]*",            # AVDOVIMetadata / AVDOVIDecoderConfigurationRecord …
    r"\bav_dovi_[a-z_]+",             # av_dovi_get_header / av_dovi_find_level / …
    r"\blibfdk[a-z_]*|\bfdk_aac[a-z_]*",
    # 床→双耳渲染器（2026-10-18 搬进外部模块）：本体只留中性接缝
    # FFF.Player.AudioRenderPlug.h。这个类名一旦出现在源码层，就是"受限实现回体内"。
    # 二进制层不判这条——iamf.lib 内含 binaural 29 次、oar.lib 14 次，那是 vendored
    # 库自己的符号串，判红只会把免版税依赖误伤成违规。
    r"\bBedBinauralRenderer\b",
    r"\bmpegh[a-z_]*", r"\bac4_[a-z_]+", r"\bffdac[a-z_]*",
)
# FFmpeg 的公开传输层名字（项目纪律：不可改也不可想当然删）——报告但不判红。
SOURCE_ADVISORY = (r"AV_FRAME_DATA_DOVI_METADATA", r"AV_PKT_DATA_DOVI_CONF", r"\bDOVI\b")
# 构建产物与第三方源码不算本体源码
SOURCE_SKIP_DIRS = ("obj", "bin", "x64", "Debug", "Release", "third_party", "tests", "tools")


def scan_sources(root):
    """遍历本体源码目录，按源码层词表找"需授权实现"。返回 (是否干净, 命中数)。"""
    hits = []
    scanned = 0
    for dirpath, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SOURCE_SKIP_DIRS]
        for name in files:
            if not name.endswith((".cpp", ".h", ".hpp")):
                continue
            path = os.path.join(dirpath, name)
            try:
                text = io.open(path, encoding="utf-8-sig", errors="replace").read()
            except OSError as ex:
                print("   [FAIL] 读不到 %s：%s（fail-closed）" % (path, ex))
                return False, -1
            scanned += 1
            for number, line in enumerate(text.splitlines(), 1):
                stripped = line.strip()
                if stripped.startswith("//"):
                    continue        # 纯注释里的名字不是实现，判红会逼人在注释里改名
                for pattern in SOURCE_FATAL:
                    if re.search(pattern, line, re.I):
                        hits.append((path, number, pattern, stripped[:100]))
                        break
    print("%s  （扫过 %d 个源文件，跳过 %s）" % (root, scanned, ",".join(SOURCE_SKIP_DIRS)))
    if scanned == 0:
        # fail-closed：一个源文件都没扫到，"0 命中"就不是证据而是空转。
        # 上一版这里直接进下面的"判定干净"分支 —— 把 --sources 指到一个没有 .cpp/.h 的
        # 目录就能稳定打印"本体干净"，那是假绿而不是干净。
        print("   [FAIL] 源码层一个源文件都没扫到 —— 空扫不算'干净'（检查 --sources 指向）")
        return False, -1
    if os.path.basename(os.path.abspath(root)) in SOURCE_SKIP_DIRS:
        # 跳过表只剪子目录（`dirs[:] = [...]`），对扫描根本身无效。这里不改变结果，
        # 只是把这件事打印出来：否则"tools 在跳过表里"会被读成"扫 tools 也安全"。
        print("   [提示] 扫描根的名字 %r 同时在跳过表里 —— 跳过表只作用于子目录，"
              "本次根目录内容**已被扫描**" % os.path.basename(os.path.abspath(root)))
    if not hits:
        print("   源码层·需授权实现：0 命中 —— 判定干净")
        return True, 0
    files = sorted(set(h[0] for h in hits))
    print("   源码层·需授权实现：%d 处，涉及 %d 个文件（点名条数=%d，必须相等）"
          % (len(hits), len(files), len(hits)))
    for path, number, pattern, text in hits[:20]:
        print("     %s:%-6d /%s/  %s" % (os.path.relpath(path, root), number, pattern, text))
    if len(hits) > 20:
        print("     （另有 %d 处）" % (len(hits) - 20))
    return False, len(hits)


def self_test_presence():
    """反向存在性判据的自检（与词表自检同形：造一个假对象，要求逐条点名）。

    两臂都必须成立，否则这条判据是死的：
      齐全臂：8 项全写进假 vcxproj ⇒ 必须 8/8 都给出链入证据；
      摘除臂：每次只删掉一项（8 个变体）⇒ 每个变体必须**恰好**缺被删的那一项。
    摘除臂模拟的就是历史事故形状（某配置整块缺 IAMF 依赖、验证跑在另一配置上所以全绿）。
    永真（删了也报齐全）与永假（齐全也报缺）在这里都会被当场抓到。
    """
    token_to_dep = {
        "iamf.lib": "iamf.lib", "oar.lib": "oar.lib", "obr.lib": "obr.lib",
        "obr_capi.lib": "obr_capi.lib", "pffft.lib": "pffft.lib",
        # absl 在真实清单里是 `absl\absl_base.lib` 这样的目录下一串库，不是单个 absl.lib
        "absl": r"absl\absl_base.lib", "opus": "opus.lib", "FLAC": "FLAC.lib",
    }

    def fake_vcxproj(deps):
        return ('<Project><ItemDefinitionGroup><Link><AdditionalDependencies>'
                + deps + ';%(AdditionalDependencies)'
                '</AdditionalDependencies></Link></ItemDefinitionGroup></Project>')

    def missing_of(dropped):
        deps = ";".join(token_to_dep[t] for t in BINARY_ALLOWED if t != dropped)
        entries = link_dependencies(fake_vcxproj(deps))
        return [t for t in BINARY_ALLOWED if dependency_proof(t, entries) is None]

    # dropped=None ⇒ 条件恒真 ⇒ 拼出"八项齐全"的那一份，即齐全臂
    all_missing = missing_of(None)
    detectable = len(BINARY_ALLOWED) - len(all_missing)
    wrong = []
    for dropped in BINARY_ALLOWED:
        got = missing_of(dropped)
        if got != [dropped]:
            wrong.append("%s→%s" % (dropped, ",".join(got) or "无"))
    print("   反向存在性自检（假 vcxproj）：齐全臂 %d/%d 给证；逐项摘除 %d 个变体，"
          "每变体应只缺被摘那一项" % (detectable, len(BINARY_ALLOWED), len(BINARY_ALLOWED)))
    if all_missing:
        print("     [FAIL] 齐全臂被报成缺失（判据永假，会把干净读成摘除）：%s"
              % ", ".join(all_missing))
        return False
    if wrong:
        print("     [FAIL] 摘除臂没被精确抓到（判据永真或有串项，事故会漏过去）：%s"
              % "; ".join(wrong))
        return False
    print("     8/8 齐全臂通过；8/8 摘除臂各自只缺被摘那一项（含 `obr.lib` 不被 "
          "`obr_capi.lib` 顶替、`absl` 按路径段匹配）")
    return True


def self_test():
    """把禁用词拼进一段假字节，要求扫描器逐条抓到。抓不到就是扫描器坏了。"""
    blob = bytearray(b"avcodec swresample D3D11 FFF.Native LoadLibrary ")
    all_tokens = BINARY_FATAL + BINARY_ADVISORY
    for token in all_tokens:
        blob.extend(token.encode("utf-8", "replace") + b"\0")
    hits = count_tokens(bytes(blob), all_tokens)
    # 期望数按**小写去重后**算：`AC4_` 与 `ac4_` 是同一条判据，都列会让"抓到几个"永远少一个，
    # 于是自检会把去重误报成"词扫不出来"（本轮真实踩过一次）。
    expected = set(t.lower() for t in all_tokens)
    missed = [t for t in all_tokens if t.lower() not in set(k.lower() for k in hits)]
    print("--self-test：注入 %d 个词（去重后 %d 条判据），抓到 %d 条" % (
        len(all_tokens), len(expected), len(hits)))
    if missed:
        print("   [FAIL] 这些词扫不出来，词表或读取路径坏了：%s" % ", ".join(missed))
        return False
    if len(hits) != len(expected):
        print("   [FAIL] 抓到数（%d）与去重后的判据条数（%d）不等" % (len(hits), len(expected)))
        return False
    # 反向判据（J1）的自检必须在**正控制那行之前**打印：门禁是按"最后一条含'抓到'的行"
    # 与"最后一条含'正控制'的行"取读数的，新行若带这两个词、或排在它们后面，
    # 会把主仓那条链路的对账正则顶掉（改了判据输出格式而不只是加行，是不允许的）。
    if not self_test_presence():
        return False
    print("   正控制 %d/%d 命中" % (
        len(count_tokens(bytes(blob), BINARY_CONTROLS)), len(BINARY_CONTROLS)))
    print("   判定：扫描器活着")
    return True


def main(argv):
    ap = argparse.ArgumentParser(description="3FP 本体中性判据（空间音频实现不得在本体里）")
    ap.add_argument("targets", nargs="*", help="待检查的 FFF.Native.dll（可多个）")
    ap.add_argument("--project", action="append", default=[],
                    help="附带检查的 .vcxproj（可多次）")
    ap.add_argument("--sources", action="append", default=[],
                    help="附带检查的本体源码目录（二进制看不见的那一层，可多次）")
    ap.add_argument("--self-test", action="store_true", help="只跑扫描器自检")
    a = ap.parse_args(argv)

    if a.self_test and not self_test():
        return 1
    if not a.targets and not a.project and not a.sources:
        if a.self_test:
            return 0
        print("用法：见文件头。至少要给一个 dll、--project 或 --sources。")
        return 2

    ok = True
    for path in a.targets:
        if not os.path.exists(path):
            print("%s -> [FAIL] 文件不存在（fail-closed）" % path)
            ok = False
            continue
        verdict, _, _ = scan_binary(path)
        if verdict is not True:
            ok = False
    for path in a.project:
        if not os.path.exists(path):
            print("%s -> [FAIL] 文件不存在（fail-closed）" % path)
            ok = False
            continue
        if not scan_project(path):
            ok = False
    for path in a.sources:
        if not os.path.isdir(path):
            print("%s -> [FAIL] 目录不存在（fail-closed）" % path)
            ok = False
            continue
        verdict, _ = scan_sources(path)
        if not verdict:
            ok = False

    # ── 反向判据（J1）：该有的必须还在 ──
    # 上面三层只证明"不该有的不在"。三层全绿而 IAMF/OAR 整块被摘掉时，旧版会照样打印
    # "本体干净"（BINARY_ALLOWED 是死代码），所以这一条单独跑、单独报、单独决定退出码。
    safety_ok = ok
    presence_ok, presence_missing, _rows = scan_allowed_presence(a.project, a.targets)
    if not presence_ok:
        ok = False

    if not safety_ok:
        # 这一层现在有两种红法：检出受限制实现（点名行），和"空扫/读不到"这类
        # fail-closed。把话说明白，别让人把覆盖失败读成"抓到东西了"。
        print("结论: 判红 —— 安全层或覆盖检查未通过（可能来自点名命中，也可能来自"
              "'空扫/读不到'的 fail-closed；以各层的点名行为准）")
    elif not presence_ok:
        print("结论: 无受限制实现，但免版税能力被摘除（缺 %d 项，判红；这是功能缺失、"
              "不是安全违规）：%s" % (len(presence_missing), ", ".join(presence_missing)))
    else:
        print("结论: 本体干净")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
