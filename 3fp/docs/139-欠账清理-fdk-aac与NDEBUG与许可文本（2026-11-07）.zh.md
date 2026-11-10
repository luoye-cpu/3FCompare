# 139 · 欠账清理——fdk-aac / NDEBUG / 许可文本 / 模块 README 导入表（2026-11-07）

> 触发：docs/110 §三 与近期审计挂起的四项欠账，本 lane（欠账清理）一次清账。
> 方法：全只读取证先行（dumpbin /symbols、二进制串扫、git log -S、CMakeCache、上游源码），
> 裁决后再落笔。铁律遵守情况：`third_party\fff_project\`（TP）零修改——注意本文 §一.1 的
> junction 事实：KF 的 `third_party` **指向** TP 树，本 lane 没有任何写入该目录的动作；
> `C:\PLAN\ColorTransform` 只改了 README 陈述（用户指令允许），源码零触碰；
> `3fp\kernel` 不 commit，只改工作树。
> KF git 起点：分支 `feature/cuda-d3d11-interop`，HEAD `72af915`。

---

## 0. 判词（四条，先说结论）

1. **fdk-aac 裁决：仍引用，保持链接不动。** 2026-11-07 实测 iamf.lib（41 个归档成员）内
   `aac_multistream_decoder.obj` 持有**全部 7 个 FDK API 的 UNDEF External 引用**，fdk-aac.lib
   恰好定义这 7 个符号；旧 NOTICES 的"已于 2026-10-15 从本树摘除、摘除前无 FDK 引用"与
   KF 三处实况（vcxproj 链接行、准备脚本 staging、磁盘文件）**全部矛盾**，已改写为实测裁决
   （THIRD-PARTY-NOTICES.md §3）。机制上链接器按需拉取、该成员当前不在闭包内——TP 不链也通过、
   KF 成品 `aacDecoder_DecodeFrame` 串 0 命中（零进包），故保持链接是防御性选择，不是功能需要。
2. **NDEBUG：断言串账已平，显式化加固已落。** docs/110 记的 88 条断言串 + 54 条 `C:\PLAN\3FUI`
   路径串在 2026-11-06 重编（TEMP 位置、CMake 默认 Release 旗标本就含 /DNDEBUG）后于成品
   `FFF.Native.dll` 内**实测归零**；本轮把 `-DCMAKE_C_FLAGS_RELEASE=/O2 /Ob2 /DNDEBUG`
   与 C++ 同款**显式**写进 KF 准备脚本 configure 行（防上游未来改旗标让断言回归）。TP 待应用（§七）。
3. **许可文本：四族原文已入 `licenses\`，仍缺三件。** libiamf（LICENSE+PATENTS）、OAR、
   pffft（头部声明）、abseil 落盘 6 个文件；NOTICES §3 表格与 §7.2 同步改写。
   仍缺：FLAC / opus 原文（dep_codecs 只给预编译件）、OBR 单独文本（上游无独立文件）、fdk-aac 原文。
4. **模块 README 导入表：已按 2026-11-07 实测更新为 15 项逐名清单。** 旧"KERNEL32+CRYPT32+bcrypt"
   读数保留为历史记录；并修正 2026-10-18 块里 api-ms-win-crt-* "7 项"与总数 15 不自洽之处
   （实点 **8 项**，7+8=15 才对上）。

---

## 一、欠账 1：fdk-aac 摘除矛盾——裁决"仍引用"，NOTICES 改写

### 1.1 先校一个几何事实：两棵树共享同一份 libiamf 物理件

- `Get-Item 3fp\kernel\third_party` → **LinkType=Junction**，Target=
  `c:\PLAN\3FCompare\third_party\fff_project\third_party`。docs/100:368 的结论（"同 inode、
  两侧永远相等"）与本次实测一致；docs/110:32"共享 junction"的警告依然有效。
- 两树"各自"的 iamf.lib SHA256 相同（`F671CAF56805B201084C943CD1616D99E64FD390AE116F2026553A1DC529FF91`）
  不是巧合——是同一个文件（2026-11-06 0:26 构建，1,076,318 B）。
- **`lib\x64\debug\` 子目录不存在**（vcxproj:60 的 Debug 库目录指向空）——K 树 Debug 配置若走
  HaveLibiamf 分支会链接失败。本次不修（不在清单内），登记待办。

### 1.2 磁盘与文件实况（2026-11-07 实测）

| 位置 | 实况 |
|---|---|
| `3fp\kernel\FFF.Native\FFF.Native.vcxproj:122` | `AdditionalDependencies` 含 `fdk-aac.lib`（HaveLibiamf 分支） |
| TP `third_party\fff_project\FFF.Native\FFF.Native.vcxproj:120` | **不含** fdk-aac.lib |
| 磁盘 `lib\x64\fdk-aac.lib` | **在**，4,170,630 B，2026-11-06 0:21（staged 于最近一次脚本运行） |
| KF `tools\准备Libiamf.ps1:151` | `@{ Name = "fdk-aac.lib"; Why = "iamf.lib 的 aac_multistream_decoder.obj" }` |
| TP `tools\准备Libiamf.ps1:119` | 同款 staging 行（TP vcxproj 不链，staging 产物闲置在盘） |
| KF `tools\Resolve-NativeDependencies.ps1:11-19` | **无硬编码清单**：从 vcxproj 动态读 `AdditionalDependencies` ⇒ vcxproj 是唯一真源，本裁决不动 vcxproj 则该脚本自动一致，**无需修改**（TP 树根本没有这个文件） |
| NOTICES（KF 与 TP 两份同文）L65-68 | "已于 2026-10-15 从本树摘除……EXTERNREF 里 aac 0 命中" ⇒ **与上述三处实况矛盾** |

### 1.3 git 史：摘除发生过、又被加回

`git log -S fdk-aac`（范围：vcxproj / 准备Libiamf.ps1 / Resolve-NativeDependencies.ps1 / NOTICES）命中三次：

| 提交 | 日期 | 对 fdk-aac 做了什么 |
|---|---|---|
| `d3f0945` | 2026-10-12 | 上游基线自带：vcxproj 链 fdk-aac.lib + 准备脚本 stage 它（`+` 行实见） |
| `d19d198` | 2026-10-15 | **从 vcxproj 摘除**（`-` 行含 fdk-aac），同时写下 NOTICES"已摘除 + 无 FDK 引用"陈述；**但未动准备脚本**——staging 照旧，"摘除"从一开始就不彻底 |
| `b27c959` | 2026-11-05 | **vcxproj 重新加回 fdk-aac.lib**（`+` 行实见），现行状态 |

即：NOTICES 的"已摘除"描述的是 10-15 至 11-05 之间的窗口，且从未覆盖准备脚本；11-05 起该陈述过时。

### 1.4 符号级实测（dumpbin，工具链 `VS 18 Community / MSVC 14.51.36231`）

- iamf.lib `/symbols`：输出 12,643 行，**UNDEF External 符号 339 个**，其中 aac/fdk 相关 **12 个**：
  `aacDecoder_{Close,ConfigRaw,DecodeFrame,Fill,GetStreamInfo,Open,SetParam}`（7 个 FDK API）、
  `aac_multistream_{decode,decoder_close,decoder_get_delay,decoder_open}`（4 个）、`iamf_aac_decoder`（1 个）。
- 归档成员定位（`lib /list` → 41 个成员；`lib /extract` + 对 obj 再跑 dumpbin）：
  **`iamf.dir\Release\aac_multistream_decoder.obj` 自身 UNDEF 恰为那 7 个 FDK API**——引用源钉死；
  `iamf_aac_decoder.obj` 无 FDK 引用（它定义 `iamf_aac_decoder`、调用 `aac_multistream_*`，
  这两个符号由 iamf.lib 自身成员定义，不经 fdk-aac.lib）。
- fdk-aac.lib `/symbols`（42,642 行）：7 个 `aacDecoder_*` **各 1 个 Defined External**、UNDEF=0
  ——它就是这 7 个符号的提供者。`aac_multistream_*` / `iamf_aac_decoder` 在 fdk-aac.lib 内 0 命中
  （它们不来自 FDK）。
- 二进制 ASCII 串旁证（全 8 个 .lib 扫 `aacDecoder_` / `aacDec` / `fdkAac` / `Fraunhofer` / `fdk_`）：
  iamf.lib 7 / 7 / 0 / 0 / 0；fdk-aac.lib 201 / 215 / 0 / 0 / 617；oar、obr、obr_capi、pffft、FLAC、
  opus **全 0**——FDK 引用面只在 iamf.lib。
- 口径更正：旧陈述写的"`dumpbin /symbols` 的 EXTERNREF"这一字段**不存在**（dumpbin 输出中
  EXTERNREF 字样 0 行）；COFF 静态库的"未解析外部引用"落在 **UNDEF + External** 行。旧读数
  无论当时怎么测出的 0，都解释不了今天的实测。

### 1.5 机制：为什么"仍引用"却不缺件、TP 不链也通过

静态库是归档，链接器按需拉取成员：只要最终闭包不抽取 `aac_multistream_decoder.obj`，它带的
7 个 UNDEF 就永远不要求解析。三重印证：

1. TP 链接行**没有** fdk-aac.lib（TP vcxproj:120），10-15 摘除后 `MSBUILD_NATIVE_RC=0`
   （旧 NOTICES 自己记录的实测），TP 出货件带 IAMF（docs/110 成品实扫）。
2. KF 成品 `FFF.Native.dll`（6,449,152 B，2026-11-06 1:52 链接，此时链接行**含** fdk-aac.lib）
   内 `aacDecoder_DecodeFrame` 串 **0 命中**——fdk-aac 代码零进包，挂着也只是链接时多读 4 MB 归档。
3. 两侧引用的 iamf.lib 是同一物理件（§1.1）——同一份引用链，一条链接行带它、一条不带，都过。

### 1.6 裁决与落地

- **裁决：证据支持「仍引用」→ 保持链接不动**（vcxproj:122 与准备脚本:151 均不改）。
  理由：当前闭包不抽取该成员是**内核源码现状的函数**，不是链接行的功劳；若未来内核改动
  （例如接入 IAMF AAC 子流路径）使该成员进入抽取闭包，链接行里的 fdk-aac.lib 让失败以
  LNK2019 立即暴露，而不是静默缺件。删除的收益为零（产物不变），风险为非零。
- **KF 改动仅一处（NOTICES §3）**：旧"已摘除"段（原 L65-68）整段改写为实测裁决（新文本含
  git 三点史、dumpbin 证据、机制、处置与许可状态五块，见 §六 diff 摘要）。
- **fdk-aac 许可状态**：libiamf 仓内 `code\dep_codecs\` 只有 bin/include/lib（预编译件落点，
  全树 0 个 LICENSE/NOTICE 文件），原文不在手 ⇒ NOTICES 登记**未判定**；其代码零进包 ⇒
  当前不构成随包分发义务；若未来闭包变化使 FDK 进包，必须先收原文再发布（NOTICES 已写明）。

---

## 二、欠账 2：NDEBUG——账已平，加固已落，TP 记待办

### 2.1 为什么产物曾带断言串（机制复盘）

- CMake 的 MSVC Release 默认旗标本就含 /DNDEBUG：TEMP 构建缓存
  `%TEMP%\libiamf-prepare\build\CMakeCache.txt` 实测
  `CMAKE_C_FLAGS_RELEASE:STRING=/O2 /Ob2 /DNDEBUG`（CXX 同）。
- 上游 `code/CMakeLists.txt`（pinned `b276f437`，已抓原文）只有
  `set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -O3")` 一处全局追加，**不覆盖** per-config 旗标、不碰 NDEBUG。
- docs/110:30-31 记的断言串来自**当时**（10 月）那轮构建，其构建位置
  `C:\PLAN\3FUI\fixtures\decode-research\build-libiamf` 表明它是 research 手工链，不经现脚本；
  且 pinned 版 libiamf **仓库根没有 CMakeLists.txt**（本地 repo 实测：根层只有
  code/ tests/ LICENSE PATENTS 等）——TP 旧脚本的 `-S $RepoPath`（仓库根）在当前 pinned 版本上
  根本无法 configure。旧断言串的确切成因（无存档配置）不复盘为"未判定"，但其消除途径已被
  11-06 重编实证：同一默认旗标下断言串归零。
- **2026-11-06 重编的现账**（成品 `FFF.Native.dll` 6,449,152 B 实测）：
  `Assertion failed` = **0**；`C:\PLAN\3FUI` = **0**（docs/110 的 54 条本机路径串消失）；
  `IAMF_decoder_open` = 1（功能在）。**新形态残留**：`libiamf-prepare`（TEMP 构建路径）**88 条**，
  抽样确认形态为 `C:\Users\...\Temp\libiamf-prepare\repo\code\src\iamf_dec\iamf_decoder.c` +
  `IAMF_DEC` 等日志标签 ⇒ 来源是 libiamf **日志宏的 `__FILE__`**（普通字符串常量，非 assert，
  NDEBUG 管不掉）。docs/110 的"路径串归零"验收按字面达成（3FUI 串没了），但本机路径串以
  TEMP 形态等量存在——要彻底归零需改上游日志宏或在 staging 时过滤，另行立账（§七）。

### 2.2 修改（最小 diff）

`3fp\kernel\tools\准备Libiamf.ps1` configure 行（原 L107-110）追加两个显式旗标参数，并加 7 行注释：

```
'-DCMAKE_C_FLAGS_RELEASE=/O2 /Ob2 /DNDEBUG' '-DCMAKE_CXX_FLAGS_RELEASE=/O2 /Ob2 /DNDEBUG'
```

- 与 CMake 默认值**等价**（含 /O2 /Ob2，避免意外降优化），价值在**显式化**：上游将来若改写
  per-config 旗标（像它已经改了全局旗标那样），断言不会无声回归成品。
- Debug 不加（旗标只写 `_RELEASE` 变量，`-DCMAKE_CONFIGURATION_TYPES=Debug;Release` 的 Debug
  保留 /RTC1 与断言语义）——符合任务约束。
- `tools\Resolve-NativeDependencies.ps1` **不需要也不应该加清单**（§1.2：它从 vcxproj 派生）。

### 2.3 验证方式与"不重编"的理由

- **已做的验证**：① CMakeCache 实测默认旗标含 NDEBUG；② 11-06 产物（.lib 与成品 DLL）断言串 0、
  3FUI 串 0——NDEBUG 路径已被现产线实证；③ 显式参数与默认值逐字等价（/O2 /Ob2 /DNDEBUG）。
- **不做全量重编**的理由：重跑准备脚本最后一步会把产物 stage 进 `third_party\libiamf`——
  那是经 junction 落在 **TP 树**的共享暂存件（铁律禁止写入，docs/110:32 也警告覆盖两棵树）；
  而重编能提供的增量信息（"显式旗标下断言仍为 0"）已由等价性 + 现产物实测覆盖。
  代价（10-40 分钟 + 违反铁律风险）大于收益，如实记为"已备好未编"——源码 repo 在
  `%TEMP%\libiamf-prepare\repo`（pinned `b276f437`，oar 子模块 `ca14b543`），随时可编。

### 2.4 待 TP 应用清单（TP 只读，本 lane 不动手）

1. `third_party\fff_project\tools\准备Libiamf.ps1:78-80` configure 行追加同款
   `'-DCMAKE_C_FLAGS_RELEASE=/O2 /Ob2 /DNDEBUG'` 与 CXX 同款；
2. 同脚本 `-S $RepoPath` 需改为 `-S (Join-Path $RepoPath "code")`——当前 pinned 版本根层无
   CMakeLists.txt，不改这条脚本在现 commit 上跑不通；
3. TP 的 `third_party\fff_project\THIRD-PARTY-NOTICES.md:65-68` 有与 KF 完全相同的过期
   "fdk-aac 已摘除"陈述，需按本文 §一 同步改写（TP 的 NOTICES 是独立文件）；
4. TP 树自身的 `licenses\` 收集（KF 的 licenses\ 目录只服务 KF 发布链）。

---

## 三、欠账 3：许可文本——四族入仓，三件仍缺

### 3.1 落盘清单（`3fp\kernel\licenses\`，全部 2026-11-07 收取）

| 文件 | 来源 | 备注 |
|---|---|---|
| `libiamf-LICENSE.txt`（1,695 B） | raw.githubusercontent.com/AOMediaCodec/libiamf/main/LICENSE | BSD-3-Clause Clear（AOM 2022）；与构建副本 `third_party\libiamf\LICENSE.libiamf.txt`（脚本 L170 从 pinned repo 拷贝）同源 |
| `libiamf-PATENTS.txt`（5,809 B） | 本地 pinned repo 顶层 `PATENTS` | 上游随附专利声明；docs/110:23 的"专利拒授句在案"指的就是它 |
| `oar-LICENSE.txt`（1,725 B） | 本地 pinned repo `code\dep_external\src\oar\LICENSE` | **以编译实况为准**（oar 子模块 HEAD `ca14b543`）；与上游 main 现行文本哈希不同（版本漂移），main 版留档 `oar-LICENSE-main-branch.txt`（1,695 B） |
| `pffft-license-header.txt`（2,840 B） | hayguen/pffft master `pffft.c` 头部第 1-54 行 | Julien Pommier 2013 + Hayati Ayguen 2020，FFTPACK/UCAR 条款；libiamf 树内**无** pffft 源码与许可件（oar 经 `external\pffft.BUILD` bazel 拉取），故收上游头部 |
| `abseil-cpp-LICENSE.txt`（11,361 B） | raw.githubusercontent.com/abseil/abseil-cpp/master/LICENSE | Apache-2.0 |

### 3.2 查证记录（为什么缺的三件是真的缺）

- **OBR**：libiamf 仓内 OBR 代码位于 oar 子模块内 `src\renderer\obr\obr_capi\obr`（README 自述
  "Open Binaural Renderer (obr)"）；oar 子模块全树许可类文件**只有** `oar\LICENSE` 一份——OBR
  无独立 LICENSE。外部探测：`AOMediaCodec/obr`、`AOMediaCodec/BedBinauralRenderer` 两仓 raw
  均 404（不存在或不可见）。⇒ NOTICES 写"随 oar 仓分发、无独立文本、按宿主仓 BSD-3-Clear
  对待、具体授权含义未判定"。
- **FLAC / opus**：libiamf 的 `code\dep_codecs\` 只有 bin/include/lib（预编译件落点），全树
  0 个许可文件——Xiph 原文不在 libiamf 仓内，本次不越清单抓取，NOTICES 维持"未判定（无原文）"。
- **fdk-aac**：同 dep_codecs 结论（§1.6）。
- **NOTICES 落笔**：§3 表格 7 行重写（每行给出原文文件或未判定理由）、§7.2 缺口清单改为
  "已收四族 / 仍缺三件"。

---

## 四、欠账 4：模块 README 导入表——实测 15 项，README 已更新

- 实测对象：`C:\PLAN\ColorTransform\build\FFF.HDRdp.Test.dll`（4,267,520 B，2026-11-03 21:42
  构建；只读该 DLL，未构建）。`dumpbin /imports` 逐名：

  1. KERNEL32  2. MSVCP140  3. bcrypt  4. CRYPT32  5. VCRUNTIME140  6. VCRUNTIME140_1
  7. api-ms-win-crt-math-l1-1-0  8. api-ms-win-crt-heap-l1-1-0  9. api-ms-win-crt-stdio-l1-1-0
  10. api-ms-win-crt-filesystem-l1-1-0  11. api-ms-win-crt-runtime-l1-1-0
  12. api-ms-win-crt-string-l1-1-0  13. api-ms-win-crt-time-l1-1-0  14. api-ms-win-crt-utility-l1-1-0
  15. dbghelp

- 与 docs/100 §11（:292"从 3 项胖到 15 项"）与 README §四 2026-10-18 复测块的"15 项"吻合；
  **修正一处旧账**：2026-10-18 块写 api-ms-win-crt-* "7 项"，但其点名清单 6 系统 + dbghelp + 7 crt
  = 14 ≠ 15，本轮逐名实点为 **8 项**（7+8=15 才自洽）。动态 CRT 的结论不变。
- **README 改动两处**（其余为零）：§四判据句把"实测 KERNEL32 + CRYPT32 + bcrypt"改标为
  **2026-10-17 历史读数**并指向 2026-11-07 补测；2026-10-18 复测块之后新增 2026-11-07 复测块
  （15 项逐名 + 8 项更正说明 + "FFmpeg 符号仍 0 项"）。历史测量记录按要求保留原文。
- docs/100:300 的待办"模块 README §四 的导入表陈述待更新"就此关闭。

---

## 五、书面作废：docs/92 的 P2-P5（docs/100:234 挂账至今的那条）

**作废记录（即刻生效）**：`docs/92`《本体中性化设计——空间音频实现整体外置》的 **§2 目标架构
（本体只留三条中性缝）、§3 分期计划 P2–P5、§4 施工清单**，自 2026-10-13 起**从未进入实施**，
且已被两份后继文档**实质取代**，不再作为任何后续工作的依据：

1. **docs/93（v3，同日）规则级推翻**：docs/93:3-6 明言"规则……覆盖本文件前两版与 docs/92"、
   "我前两版搞的把 libiamf/OBR 请出本体……整套**作废**：那是把『谁写的』当成了判据，而规则是
   『要不要授权』"。判据换成"免版税标准的实现（含第三方库）留本体、能力不降级"——docs/92 的
   P2-P5 服务的是旧判据，随判据一并作废。
2. **docs/105 §14 终态裁决**：空间音频架构按 6e574b2（删内置 BedBinauralRenderer、改
   AudioRenderPlug 函数表外置）+ e010857/1cb205f（发布脚本**强制携带** libiamf 全家，§14.4 已推
   上游 main）定型——**与 docs/92 P2-P5 的"整体外置"方向相反**：解码层条件内置、渲染层外置
   plug、回退为常规解码（docs/105:454-466 分层表）。
3. **范围澄清**：作废的是设计与分期（P2-P5 及其验收门），**不作废**其中的机制性事实——
   "翻宏 ≠ 搬干净"（docs/92 §0-2）、两树 ABI 分叉记录（§0-3）、判据脚本
   `tools/check_kernel_neutrality.py` 本身（docs/93 已按新规则改向并加源码层）。docs/92 保留
   为历史记录，其结论引用一律以 docs/93 v3 与 docs/105 §14 为准。
4. 背景：docs/96:170 曾要求对 92 的 P2-P4 出书面作废文本，docs/100:234 复核时挂账
   "至今未见作废文本"——本节即该账的闭合（范围按现行任务口径扩到 P2-P5）。

---

## 六、改动文件与 diff 摘要

| 文件 | 改动 |
|---|---|
| `3fp\kernel\tools\准备Libiamf.ps1` | configure 调用追加 2 个显式旗标参数（C/CXX Release），前置 7 行注释；L104-115 区域，净 +9 行 |
| `3fp\kernel\THIRD-PARTY-NOTICES.md` | §3 表格 7 行重写（原文收集状态）；§3 fdk-aac 段 4 行旧文 → 22 行实测裁决（git 三点史 + dumpbin 证据 + 机制 + 处置 + 许可）；§7.2 缺口清单改写；净 +26 行左右 |
| `C:\PLAN\ColorTransform\README.md` | §四判据句标历史读数并指向补测；新增 2026-11-07 复测块（15 项逐名 + 8 项更正）；净 +9 行 |
| `3fp\kernel\licenses\`（新建目录） | 6 个文件（§3.1 表） |
| `3fp\docs\139-欠账清理-fdk-aac与NDEBUG与许可文本（2026-11-07）.zh.md` | 本文 |
| **明确未改** | `FFF.Native.vcxproj`（裁决 B：保持链接）；`tools\Resolve-NativeDependencies.ps1`（从 vcxproj 派生，无需动）；TP 树任何文件；ColorTransform 源码；git 历史 |

---

## 七、未做与堵点（别当已清）

1. **TP 三处待应用**（§2.4）：TP 准备脚本 NDEBUG 旗标、`-S code` 修正、TP NOTICES 同步改写。
   TP 只读，等有 TP 写权限的 lane 执行。
2. **libiamf 未重编验证**：源码已备（%TEMP%\libiamf-prepare\repo），不编的理由见 §2.3
   （铁律：不可覆盖共享暂存件 + 等价性已证）。若要编：产物只留 TEMP，验证命令 =
   对新 iamf.lib/oar.lib 跑本文 §二.1 的三个串扫（断言/3FUI/TEMP）。
3. **TEMP 路径串 88 条**（成品内）：libiamf 日志宏 `__FILE__` 所致，NDEBUG 管不掉；归零需上游
   宏改造或链接后清理，另行立账。
4. **许可仍缺三件**：FLAC / opus 原文（可从 Xiph 上游抓，超出本次清单未做）、OBR 独立文本
   （上游不存在）、fdk-aac 原文（libiamf 仓不含；其代码零进包，暂无随包义务）。
5. **`lib\x64\debug\` 缺席**（§1.1）：KF Debug 配置的 IAMF 链接当前不可用；不在本次清单，登记待办。
6. **`tools\发布3FP.ps1` 的许可收集步骤**需要把新的 `licenses\` 目录纳入收集（NOTICES §7.4 的
   既有随包机制）——属发布链改动，本次不动，登记待办。

## 八、复现

```powershell
# §1.4 FDK 符号
dumpbin /symbols 3fp\kernel\third_party\libiamf\lib\x64\iamf.lib | Select-String 'UNDEF' | Select-String 'aac|fdk'
# §1.5 成品零进包
# ASCII 串扫 FFF.Native.dll 内 aacDecoder_DecodeFrame（期望 0）
# §2.1 NDEBUG 现账
# ASCII 串扫 3fp\build\FFF.Native.dll：'Assertion failed'=0、'C:\PLAN\3FUI'=0、'libiamf-prepare'=88
# §四 导入表
dumpbin /imports C:\PLAN\ColorTransform\build\FFF.HDRdp.Test.dll
# §1.3 git 史
git -C 3fp\kernel log --oneline -S fdk-aac -- FFF.Native/FFF.Native.vcxproj tools/准备Libiamf.ps1 THIRD-PARTY-NOTICES.md
```
