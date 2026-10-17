# 第三方组件与许可清单 / Third-Party Notices

本文件覆盖**随包分发**的第三方件，分两条发布链：

- **3FP 播放器**：`3fp/kernel` 构建（`FFF.Native.dll` + `FFF.Player.exe` + 运行期 DLL）
- **3FCompare 比对工具**：`pack.ps1` 产出的载荷（`3FCompare.exe` + `ffmpeg-full\*.dll`）

许可标识一律注明**证据来源**：`PE StringFileInfo` / `vcpkg.spdx.json 的 licenseConcluded` /
`NuGet .nuspec 的 <license> 节点` / `随包许可文本`。读不到的写 **未判定**，不猜。
法律定性（是否合规、要不要买授权）一律标 **需法务**。

---

## 1. FFmpeg（两条链都分发）

| 项 | 读数 |
|---|---|
| 分发件 | 13 个 `.dll`：`avcodec-63` `avformat-63` `avutil-61` `avfilter-12` `swscale-10` `swresample-7` `avdevice-63` + 6 个 llvm-mingw/着色器伴生件（见 §2） |
| 发货源（实测） | `pack.ps1:619` 写死 `publish\PLAN\ffmpeg-full`；`pack.ps1:626` 只拷 `*.dll` ⇒ 同目录的 `ffmpeg.exe`/`ffprobe.exe`/`ffplay.exe` **不进包** |
| 版本串 | `git-2026-10-11-edadf711a9`（实测 `-version`）；PE FileVersion：avcodec 63.19.101 / avutil 61.14.100 / avformat 63.8.100 / avfilter 12.5.100 / swscale 10.2.100 / swresample 7.3.101 / avdevice 63.2.100 |
| 版权串 | PE `CompanyName = FFmpeg Project`，`LegalCopyright = Copyright (C) 2000-2026 FFmpeg Project` |
| 构建变体（实测 `-buildconf`，109 个 flag） | `--enable-gpl` **在**、`--enable-nonfree` **在**、`--enable-version3` **在**、`--enable-shared --disable-static`、`--prefix=/home/dominic/ffmpeg/bin`（BtbN 构建农场） |
| 其中触发 GPL 的组件 | `--enable-libx264` `--enable-libx265`（x264/x265 为 GPL-2.0+） |
| 其中触发 nonfree 的组件 | `--enable-libfdk-aac` + `--enable-encoder=libfdk_aac`（Fraunhofer FDK-AAC 许可不允许自由再分发） |
| 静态内嵌证据 | `avcodec-63.dll` 内字面量命中 `x265`=230、`x264`=150、`vvenc`=227、`libfdk`=7、`fdk-aac`=2 ⇒ 这些三方编码器是被打进 FFmpeg 库的，不是外挂 DLL |
| 链接方式 | 动态。`3fp/kernel/FFF.Native/FFF.Native.vcxproj:79-81/105-107` 链的是 **DLL 导入库**（`avcodec-63.def` 首行 `LIBRARY "avcodec-63"`，`.lib` 体内 `__imp_` 388 次），`DelayLoadDLLs` 列 7 件；产物 `FFF.Native.dll` 的 delay-import 目录实测列出 8 个实名（含 `bluray-3.dll`）。运行期由自研 hook `Shared/Ffmpeg/FfmpegRuntime.cpp:89-116` 按 `prefix*.dll` 定位 |
| 本机可读到的 FFmpeg 许可文本 | `third_party/ffmpeg/` 内**只有** `COPYING.LGPLv2.1` 与 `COPYING.LGPLv3`；**没有** `COPYING.GPLv2/GPLv3`，也没有 `LICENSE.md` |

⚠ **未解决项（需法务）**：盘上分发的是 `--enable-gpl --enable-nonfree` 组合，而仓内只随附 LGPL 文本。
该组合的分发条件、以及"整体是否因此必须置于 GPL 并提供对应源码"，都要回 FFmpeg 上游
`LICENSE.md`/各组件许可原文核对 —— 本文件只登记**实测到的构建串与组件**，不下结论。

## 2. FFmpeg 的运行期伴生件（同目录随包）

| 件 | 字节 | 许可 | 证据 |
|---|---:|---|---|
| `libc++.dll` | 1 289 216 | **未判定** | PE 无 StringFileInfo |
| `libunwind-1.dll` | 51 712 | **未判定** | 同上 |
| `libwinpthread-1.dll` | 57 856 | MIT 派生（MinGW-w64） | PE `POSIX WinThreads for Windows` / `Copyright (C) MinGW-W64 Project Members 2010-2023` |
| `libomp.dll` | 736 768 | Apache-2.0 w/ LLVM exception（**未判定**，按上游） | PE `LLVM* OpenMP* Runtime Library` / `CompanyName LLVM` |
| `libshaderc_shared.dll` | 5 825 024 | **未判定** | PE 无 StringFileInfo |
| `libSPIRV-Tools-shared.dll` | 2 237 440 | **未判定** | 同上 |

它们由 FFmpeg 的 vulkan/libplacebo 着色器链硬依赖（`avfilter-12.dll` 导入表实测含 `libomp.dll`、`libshaderc_shared.dll`、`vulkan-1.dll`）。

## 3. libass 字幕栈与 vcpkg 传递依赖（3FP 播放器链）

源：`third_party/fff_project/third_party/vcpkg_installed/x64-windows/share/<port>/vcpkg.spdx.json` 的 `licenseConcluded`（`licenseDeclared` 全为 `NOASSERTION`）+ 各 port 的 `copyright` 文件首行。

| port | 许可 | 随包件（字节） |
|---|---|---|
| libass | **ISC** | `ass-9.dll` 245 760 |
| libbluray | **LGPL-2.1-or-later** | `bluray-3.dll` 272 384 |
| libudfread | **LGPL-2.1-or-later** | `udfread-3.dll` 35 328 |
| libiconv | **LGPL**（vcpkg 侧记 `LicenseRef-vcpkg-null`，其 `COPYING.LIB` 原文自述 LGPL） | `iconv-2.dll` 1 092 608、`charset-1.dll` 11 264 |
| fribidi | **LGPL-2.1-or-later** | `fribidi-0.dll` 120 320 |
| freetype | **FTL OR GPL-2.0-or-later** | `freetype.dll` 707 072 |
| harfbuzz | **MIT-Modern-Variant** | `harfbuzz.dll` 1 222 656 |
| brotli | **MIT** | `brotlicommon.dll` 137 728、`brotlidec.dll` 52 224 |
| bzip2 | **bzip2-1.0.6** | `bz2.dll` 75 776 |
| libpng | **libpng-2.0** | `libpng16.dll` 197 632 |
| zlib | **Zlib** | `z.dll` 91 648 |
| libxml2 | **MIT** | `libxml2.dll` 1 220 096 |

顶层 `3fp/kernel/vcpkg.json` 只声明 `libass` + `libbluray`，其余为传递依赖。
`3fp/kernel/FFF.Player/FFF.Player.vbproj:42` 把 Debug 配置的 `LibassRuntimeDirectory` 指向 `debug/bin` ⇒ **Debug 构建会拷 debug 版 DLL 且同目录伴随 19 个 `.pdb`（最大 `harfbuzz.pdb` 20 344 832）**；发布用 Release 配置。

## 4. libiamf 一族：**静态**链进内核二进制

| 树 | 链 libiamf 的配置数 | 静态件命中 | `fdk-aac.lib` |
|---|---|---|---|
| 出货树 `third_party/fff_project/FFF.Native/FFF.Native.vcxproj`（`发布门禁.ps1` 的 `[2/11]` 由 csproj 的 `KernelDllPath` 反查到它） | 2 组 | 各 **66** 件 | **不在**（T2 已落地） |
| PR 工作树 `3fp/kernel/FFF.Native/FFF.Native.vcxproj` | 1 组 | **66** 件 | **已摘除**（2026-10-15，见下） |

链入项：`iamf.lib; oar.lib; obr_capi.lib; obr.lib; pffft.lib; FLAC.lib; opus.lib` + **59** 个 `absl_*.lib`（磁盘实有 59 个，逐名相符）。这些 `.lib` 头 8 字节均为 `!<arch>`、体内 0 个 `.dll` 名引用 ⇒ 静态归档。旁证：PDB 内 `FLAC__` 180 / `IAMF` 183 / `opus_multistream` 7 / `obr_renderer` 1 命中。

**PR 树那处 `fdk-aac.lib` 的摘除经过**（它原本是上游原样：`git show d3f0945:…vcxproj` 命中 1，`c8186bf` 未碰该行）：先量依赖——`dumpbin /symbols iamf.lib` 的 `EXTERNREF` 里 `aac` **0 命中**，整档内 `aacDecoder_DecodeFrame`/`FRANZHOFER`/`fdk` 字符串 **0 命中** ⇒ 已无人需要它；随后删掉那一个 token 并跑四步构建：`VERDICT=PASS`、`MSBUILD_NATIVE_RC=0`（**链接通过 ⇒ 未定义引用为零是实测而非推断**）。改后核对：导出表 **107 个名字与改前集合完全相同**（其中 `FFF3FP_*` 85 个）、DLL 内 `fdk`/`FRANZHOFER`/`aacDecoder` 三串 **0 命中**、`check_kernel_neutrality.py --project 两棵树` 结论"本体干净"（RC=0，含 8 项免版税存在性反向判据全齐）、四臂回归复跑 **74/0、74/0、39/0、31/0**。
**体积读数已用 A/B 归因**：摘除后 DLL 由 5 495 296 → 5 499 392（+4 096，恰为一个节对齐粒度）。把该 token 放回去重编得 **5 499 392**，再次摘除重编仍 **5 499 392** ⇒ 摘除这个动作**不改变尺寸**，那 +4 096 来自同一时间窗内本树其它源码编辑（`PlayerApi.cpp` / `VideoRenderer.cpp` / `PlayerSession.cpp` / `FFF.Player.Api.h` 等，见交接台账 §11.2 F12）。导出名集合在三个状态下均为同一组 107 个。

⇒ **出货内核不再静态链 `fdk-aac`**（AAC 走 FFmpeg 原生后端，`iamf.lib` 对 `aacDecoder_*` 的 7 条未定义引用为 0，两棵树干净重建 `MSBUILD_RC=0`、导出面不变、DLL 各小 783 728 / 783 360 字节 —— 读数见 `tools/发布门禁.ps1:534-541` 的 T2 记录）。
`FLAC`（BSD-3-Clause）/ `opus`（BSD-3-Clause）/ `abseil-cpp`（Apache-2.0）/ libiamf（BSD-3-Clause，IIS）的许可文本：包内 `licenses\` 由 `pack.ps1` 的 F10 步收集（实测 15/15 件），但 **libiamf / FLAC / opus / absl 的原文前不在仓内可读**（只有 `libiamf/LICENSE.libiamf.txt` 一份）⇒ 这几项仍需在取用目录补齐后才能纳入同一收集步。

## 5. LakeUI —— **GPL-3.0-only，且作者声明商用需付费许可**

| 项 | 读数 |
|---|---|
| 许可文本 | `3fp/LakeUI/LICENSE.txt` 35 149 B，头两行 `GNU GENERAL PUBLIC LICENSE / Version 3, 29 June 2007`；全文无运行库例外条款（无 `GCC runtime exception`、无 `Runtime Library` 字样） |
| 作者声明 | `3fp/LakeUI/README.md:84` 原文"请注意，LakeUI 是**收费软件**，要在公开发布的产品中使用必须取得对应许可"；`:35` 为购买链接 |
| 引用方式 | `3fp/kernel/FFF.Player/FFF.Player.vbproj:294` `<HintPath>..\..\LakeUI\LakeUI\bin\Debug\net10.0-windows10.0.17763.0\LakeUI.dll</HintPath>`（**Debug 产物**，2 375 168 B）；`3fp/kernel/FFF.Recorder/FFF.Recorder.vbproj:76` `<PackageReference Include="LakeUI.Notifications" Version="1.0.0" />`（许可 **未判定**：本机 NuGet 缓存无包） |
| 分发形态 | `publish/3FCompare/3FCompare.exe` 41 377 792 B（NativeAOT 单文件），件内 `LakeUI` 字面量命中 2 次 ⇒ LakeUI 代码被编进发布的 exe |

⚠ **需法务**：静态/AOT 内嵌一个 GPL-3.0-only 库，对整体作品的许可影响，以及在作者另设付费许可的前提下"是否必须先取得许可"，本文件不下结论。

## 6. NuGet / .NET

| 包 | 许可（`.nuspec` `<license>` 原文） | 引用点 |
|---|---|---|
| `Svg 3.4.8` | **MS-PL** | `FFF.Player.vbproj:77` |
| `Vortice.Direct2D1/.Direct3D11/.DirectComposition/.DirectX/.DXGI 3.8.3` | **MIT** ×5 | `FFF.Player.vbproj:78-82` |
| `Avalonia` / `.Desktop` / `.Themes.Fluent` 12.1.1 | **MIT** ×3 | `src/3FCompare/3FCompare.csproj:28-30` |
| `Avalonia.Native` / `.Skia` / `.HarfBuzz` 12.1.1（传递） | **MIT** ×3 | 同上（csproj:72 注释说明版本由 Avalonia 传递决定） |
| `avalonia.angle.windows.natives 2.1.27548.20260419` | `<license type="file">LICENSE</license>`，包内 `LICENSE` 首两行为 ANGLE Project 的 BSD-3 条款文 | 传递 |
| `CommunityToolkit.Mvvm 8.4.2` | **MIT** | `3FCompare.csproj:31` |
| `System.Drawing.Common 10.0.12` | **MIT** | `3FCompare.csproj:33` |
| `SkiaSharp 3.119.x` / `.NativeAssets.Win32`、`HarfBuzzSharp 8.3.1.x` / `.NativeAssets.Win32` | **MIT**（各附 `LICENSE.txt`） | 传递；产物 `libSkiaSharp.dll` 11 628 896 B、`libHarfBuzzSharp.dll` 1 816 088 B |
| `microsoft.dotnet.ilcompiler 10.0.12`、`runtime.win-x64.*.ilcompiler 11.0.0-rc.1.26425.128` | **MIT** | AOT 工具链（构建期，不随包） |

运行时形态：`3FCompare` 为 `PublishAot=true` + `PublishSelfContained=true`（`3FCompare.csproj:14-15`、`pack.ps1:533-534`），实测发布目录内 **0 个** `coreclr.dll`/`System.*.dll` ⇒ 运行时被 AOT 编入 exe。
`3FP` 播放器为框架依赖：`3fp/kernel/tools/发布3FP.ps1:45` `-r win-x64 --self-contained false`。

## 7. 内核源码本体的归属（不是"第三方二进制"，但必须在这里说清）

- `3fp/kernel` 的 `HEAD` 251 个被跟踪文件里，**212 个（84.5%）与上游根提交 `d3f0945` 逐字节相同**（我的口径：212 同 / 33 改 / 6 增 / 3 删，与 `c8186bf` 的 42 files 互洽）。`d3f0945` 作者为 `1059 Studio <vb1059@outlook.com>`，即 `Lake1059/FFF_Project` 上游。
- `3fp/kernel/LICENSE.txt` 是**未填写的 MIT 模板**：第 3 行原文 `Copyright (c) [year] [fullname]`（`[year]`、`[fullname]` 均为占位符）。因此"内核以 MIT 授权"这件事**在本仓内没有可读回的凭据**，历史 README 里"MIT 已确认"的措辞已按实测改为"未经确认"。
- `3fp/kernel/FFF.Native/FFF.Native.rc` 的 `LegalCopyright` 值为 `"MIT License"` —— 它是编进二进制的字符串，与 §1/§4/§5 的捆绑现实不一致，保留原值并在此登记矛盾。

## 8. 随包分发时**必须**同时处理（当前缺口）

1. §1/§2/§3/§4/§6 各组件的许可**全文**需随包（现在只有 LGPL 两份）。
2. 触发 GPL 的构建需要"对应源码"义务：要么随包附源、要么在包内 `使用说明.txt` 里给出书面要约与联系方式 —— **占位待填**：`<源码提供方式 / 有效期 / 联系地址>`。
3. `--enable-nonfree` 组合的分发限制需回上游核对后决定是否换构建变体（见修复方案 D3）。
4. LakeUI 的许可取得状态需确认（见修复方案 D2）。

> 本文件由 2026-10-15 的 IP 普查生成，测量脚本与原始输出见 `.3fc_dumps/ip-audit-20261015/`（不随包分发）。
