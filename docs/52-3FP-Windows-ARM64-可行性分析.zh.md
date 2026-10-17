# 52 · 3FP 做 Windows ARM（ARM64）的阻碍分析

> 分析对象：本仓的 **3FP 播放内核**（`FFF.Native`，X64 MSVC + D3D11）以及把它作为后端的 **3FCompare**（Avalonia 12 / .NET 11 / NativeAOT）。
> 目标平台：**Windows 11 on Arm（ARM64 / AArch64）**。
> 结论日期：基于当前工作树实况（`third_party/fff_project/FFF.Native`、`src/`、`tools/`、`native/wgc_capture/`）。
>
> **证据等级声明**：本机 `pwsh` 全程不可用（每次调用均返回 `3221225794` = `STATUS_DLL_INIT_FAILED`），
> 因此**没有任何一次真实编译或二进制检查**（无 `msbuild`、无 `dumpbin`、无 `dotnet build`）。
> 所有结论均来自**源码/工程文件的逐行阅读**，凡属"需实机验证"的均已显式标注。

---

## 0. 摘要（先给结论）

**一句话**：3FP 的 **C++ 源码本身几乎没有 ARM64 移植障碍**（无内联汇编、无 SSE/AVX intrinsic、无 SEH、公开 ABI 全部用定宽类型），
真正的阻碍**集中在工程与依赖层**：**① 第三方二进制与工程文件全面只有 x64 ② 约 25 个文件硬编码 `x64` ③ 加载路径无架构感知**。
托管侧只有**两处**必须改 C# 源码（§6.6）；内核侧只有**两处**需改 C++（CUDA 回退项、3FR 的 shader 预编译）。

| 判定 | 数量 | 说明 |
| --- | --- | --- |
| 🔴 BLOCKER | 11 | 不解决则**产不出**可用的 ARM64 产物 |
| 🟠 HIGH | 5 | 能出产物但功能/性能残缺或存在静默错载风险 |
| 🟡 MEDIUM | 4 | 需实机验证或有潜在陷阱 |
| 🟢 LOW | 4 | 打磨项，不阻塞 |
| ✅ NON-ISSUE | 9 | 已验证**可移植**（含最容易被误判的几项） |

**两项最反直觉的结论**：
1. **第三方依赖几乎不缺 ARM64 版**——FFmpeg / SkiaSharp / HarfBuzz / ANGLE / NativeAOT / vcpkg 树**全部已有或可编**。
   ⇒ 这是**管道工程，不是可行性问题**。
2. **全仓 `grep arm64|aarch64|win-arm|ARM64EC` 零命中**——ARM64 从未在任何地方被考虑过，
   约 **25 个文件**携带硬编码 `x64`，且**没有 `Directory.Build.props` 之类的中枢**可统一注入。

**总工作量估计**：从零到"ARM64 上能跑真实视频播放"，属**中等偏重**——
源码改动量极小（**约 3 处 ifdef + 1 处 shader 预编译 + 2 处 C# 结构/断言**），
**绝大部分工时花在重建依赖链与工程配置上（约 25 个文件的参数化）**。

> **📌 本文的定位**：这是**"要不要做、难在哪"**的分析（可行性）。
> **已经做了哪些前瞻准备**见 [docs/53-ARM前瞻准备.zh.md](53-ARM前瞻准备.zh.md)。

---

## 1. 关键背景：为什么这个问题比表面复杂

### 1.1 Windows on Arm 的三条路，别混为一谈

微软官方文档把"上 ARM"分成互不兼容的三层（[Arm64EC 概览](https://learn.microsoft.com/en-us/windows/arm/arm64ec)）：

| 方式 | 进程类型 | 能加载的二进制 | 对本项目的意义 |
| --- | --- | --- | --- |
| **x64 仿真**（Prism / 24H2） | x64 | x64 + Arm64EC | **今天就能跑**：现有 x64 版 3FCompare 在 ARM 上可直接运行（见 §2） |
| **Arm64EC**（"Emulation Compatible"） | x64 身份 | x64 + Arm64EC | **渐进迁移的钥匙**：可先把内核编成 Arm64EC，与 x64 依赖共存 |
| **纯 ARM64**（Classic ABI） | Arm64 | **只能加载 Arm64** | 最彻底，但要求所有依赖都有 Arm64 版 |

> ⚠ **最关键的一条约束**（官方原文）：
> *"an Arm64 process can only load Arm64 binaries"*；且 *"Arm64EC code isn't compatible with code built as Arm64"*。
> 也就是说：**纯 ARM64 进程里，混进任何一个 x64 DLL 都会加载失败**。这正是本项目最大的风险点——
> 3FCompare 依赖 **4 类原生 DLL**，只要有一个拿不到 ARM64 版，纯 ARM64 路线就走不通。

### 1.2 好消息：D3D11 不是阻碍

微软明确列出 x86/x64 应用在 Arm 上的图形兼容性（[x86 应用排错](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-troubleshooting-x86)）：

> *"x86 apps that use DirectX 9, DirectX 10, DirectX 11, and DirectX 12 work on Arm."*

3FP 的渲染栈是 **D3D11 + DXGI + D2D + DWrite + WASAPI**，全部在官方支持范围内。
**"ARM 上没有 D3D11"是个误解**，不构成本项目的阻碍。

### 1.3 好消息：CUDA 的 ARM64 缺口已经被填上

内核的硬解回退链里**真的有 CUDA**（详见 §3.3）。历史上这是 ARM64 上的死路，但：

> **CUDA Toolkit 13.4（2026-09-09）正式加入 Windows on Arm 支持。**
> —— [NVIDIA 官方博客](https://developer.nvidia.com/blog/cuda-toolkit-13-4-adds-windows-on-arm-support-and-greater-control-over-shared-gpus/)

⇒ CUDA 从"绝对不可能"降级为"**有条件可用**"（需 ARM64 上的 NVIDIA RTX + 对应驱动）。
但对 3FCompare 的现实意义有限，因为见 §6.1。

---

## 2. 一个必须先讲清楚的事实：今天你**已经**能在 ARM 上跑这个软件了

**当前发行版是 `win-x64` + NativeAOT 自包含**（`docs/PACKAGING_SPEC.md:125`、`README.md:107`）。
在 Windows 11 Arm 上，这个 x64 包会走 **Prism 仿真**运行：

- ✅ 能启动、能解码、能渲染（Prism 已针对 Snapdragon X 系列优化）
- ⚠ 但**性能与功耗双输**：CPU 解码路径全程仿真；NativeAOT 的收益被仿真抵消
- ⚠ **硬解基本报废**：内核要加载 x64 的 `avcodec-63.dll` 并在 x64 仿真进程里调 D3D11VA——
  仿真层对 GPU 硬解的支持是薄弱环节，实际大概率回落软解

> **所以本问题真正的问法不是"能不能跑"，而是"能不能原生地、有硬解地、可发布地跑"。**
> 下面所有分析都按这个标准。

---

## 3. 内核源码层（`FFF.Native`）——**几乎干净**

这一层是所有人最先怀疑的地方，实测结论**远好于预期**。

### 3.1 ✅ 没有任何 x64 专属指令或 intrinsic

全树 grep `_M_X64|_M_AMD64|_WIN64|_M_ARM64|__asm|_mm_|__cpuid|_xgetbv|immintrin|__rdtsc|/arch:`
⇒ **零真实命中**（仅有 `allocat*` 这类子串误报）。

这也解释了为什么内核能这么容易谈移植：**它把重活全交给了 GPU（D3D11 shader）和 FFmpeg**，
CPU 侧只有调度、队列、时钟、COM 编排逻辑，天然是标量代码。

### 3.2 ✅ 着色器是预编译 DXBC，**架构无关**（这是最大的好消息）

`3FP/Render/ShaderBytecode.h:12` 的字节 `68, 88, 66, 67` 正是 ASCII **`DXBC`** 魔数。
内核用 7 个预编译 blob（顶点/像素/缩放/背景/字幕等）直接喂给 D3D 运行时：

```cpp
// VideoRenderer.cpp:2797-2807
if (FAILED(device_->CreateVertexShader(FFFVertexShaderBytecode,
        sizeof(FFFVertexShaderBytecode), nullptr, &vertexShader_)) || ...
```

**DXBC 由驱动在 `CreateVertexShader` 时校验与转译，与宿主 CPU 架构无关**
（x64 与 ARM64 的 D3D 驱动各自把同一份 DXBC 转成自家 ISA）。
⇒ **着色器层零移植工作**。这正是本项目早期的正确决策（`ShaderBytecode.h:5-8` 明确写了"playback never compiles HLSL at runtime"）。

### 3.3 🟠 CUDA 回退项在 ARM64 上是空转

```cpp
// PlayerSession.cpp:2013-2019
const AVHWDeviceType Backends[] = {
    // Prefer the renderer's own D3D11 device ... CUDA remains
    // available for profiles rejected by D3D11VA.
    AV_HWDEVICE_TYPE_D3D11VA,
    AV_HWDEVICE_TYPE_CUDA,      // ← ARM64 上永远失败
};
```

配套还有 `FindHardwareFormat`（`:290-293`，CUDA→`AV_PIX_FMT_CUDA`）与
`IsDedicatedCudaDecoder`（`:305-307`，识别 `*_cuvid`）。

**为什么是 HIGH 而不是 BLOCKER**：回退逻辑是健全的——
`OpenHardwareVideoDecoder` 失败后 `DoOpen`（`:2077-2079`）会回落软解，
`av_hwdevice_ctx_create` 在 ARM64 上干净失败。所以**是"每次开媒体白跑一轮"的性能损耗与噪声，不是功能中断**。

**修法**：`#ifdef _M_ARM64` 把该数组收敛为 `{D3D11VA}`。

### 3.4 ⚠ 关于 3FR 的 `D3DCompile`——**对 3FCompare 不构成阻碍**

内核 DLL 里**确实有一处运行期 HLSL 编译**（在**录屏子系统 3FR**，不是播放器 3FP）：

```cpp
// 3FR/Ffmpeg/VideoMuxer.cpp:1065
result = D3DCompile(RgbToYuvShader, sizeof(RgbToYuvShader) - 1, "RgbToYuv", macros, nullptr,
    "Convert", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &byteCode, &errors);
```

但经核实，**3FCompare 完全不碰 3FR**：`src/` 全树 grep `FFF\.Native\.Api|3FR|NativeApi|Session_|ScreenRecord`
⇒ **零命中**；托管侧只 P/Invoke `FFF3FP_*` 播放器入口（`Fff3FpNative.cs:186-290`，共 26 个入口）。

⇒ **准确表述**：
- 对 **3FCompare**（本仓产品）：此条 **不影响**，因为它不走录屏路径（函数未被调用即不触发 `D3DCompile`）。
- 对 **3FP/3FR 上游本体**（若单独发布 ARM64 版）：此条是**真 BLOCKER**，因为 `d3dcompiler_47.dll` 必须有 ARM64 版。
- **干净修法**：与播放器一致，把这一个 compute shader 也预编译成 DXBC 常量数组——
  **彻底删掉运行期依赖**，而不是想办法把依赖搬过去。

### 3.5 ✅ 其余源码层全部为 NON-ISSUE

| 项 | 证据 | 结论 |
| --- | --- | --- |
| **无 SEH** | `__try/__except/__finally` 全树**零命中** | 无需移植（ARM64 虽支持 SEH，但这里根本没有） |
| **公开 C ABI 可移植** | `FFF.Player.Api.h` / `FFF.Native.Api.h` 只用 `std::uint32_t/int64_t/float` + `void*` 句柄 + `__cdecl`；**无 `long`、无 `#pragma pack`** | ✅ 托管 P/Invoke 层**零改动** |
| **指针转换正确** | `reinterpret_cast<std::uintptr_t>`（`VideoRenderer.cpp:4569`、`VideoMuxer.cpp:1544`，FFmpeg 的 D3D11 数组索引存指针约定）；`intptr_t` 传 opaque（`PlayerSession.cpp:284/1963`） | ✅ 64 位安全 |
| **`static_cast<long>`** | 14 处，均为格式化 32 位 HRESULT | ✅ Windows 上 `long` 恒 32 位 |
| **WASAPI + MMCSS** | `WasapiRenderer.cpp:520-619`、`WasapiCapture.cpp:8,126`（`AvSetMmThreadCharacteristicsW`） | ✅ `avrt.lib` 各架构均随 SDK 提供 |
| **无内核驱动 / 无 DirectShow / 无 MF** | 全树 grep `mfapi|dshow|DeviceIoControl|SPTD|AnyDVD|\.sys` 零命中；光盘是**纯 libbluray**（`DiscInput.cpp`） | ✅ 无驱动签名障碍 |
| **QPC 时钟** | `QpcTimeline.cpp:6-14`，`LARGE_INTEGER.QuadPart` + 溢出安全拆分 | ✅ 无 `rdtsc` |
| **无 Spectre 缓解库依赖** | vcxproj 无 `<SpectreMitigation>` | ✅ |
| **`DllMain` 不在加载锁下干活** | `dllmain.cpp:7-14` 仅 `DisableThreadLibraryCalls` | ✅ |

---

## 4. 🔴 BLOCKER 集群一：第三方二进制**全面只有 x64**

这是**最硬的墙**。3FCompare 依赖 4 类原生二进制，逐一看 ARM64 可得性：

### 4.1 FFmpeg —— ✅ **好消息：ARM64 有官方预编译**

上游脚本写死了 **win64** 包：

```powershell
# third_party/fff_project/tools/准备FFmpeg.ps1:9
[string]$DownloadUrl = ".../ffmpeg-master-latest-win64-lgpl-shared.zip",
# :22  $LibraryDirectory = "third_party\ffmpeg\lib\x64"
# :138 & $Lib /nologo /machine:x64 ...
```

**但经核实 BtbN/FFmpeg-Builds 确实发布 `winarm64` 的 shared 构建**（我直接读了 GitHub Releases API）：

| 家族 | ARM64 shared 资产 |
| --- | --- |
| master | `ffmpeg-master-latest-winarm64-gpl-shared.zip`（59.7 MiB） |
| 9.0 | `ffmpeg-n9.0-latest-winarm64-gpl-shared-9.0.zip`（57.8 MiB） |
| 8.1 | `ffmpeg-n8.1-latest-winarm64-gpl-shared-8.1.zip`（59.8 MiB） |

⇒ **FFmpeg 不是阻碍**，只是脚本要参数化。
⚠ **但必须确认 ARM64 包内含本项目实际用到的组件**：内核点名要 **`libdav1d`**
（`PlayerSession.cpp:1904` `avcodec_find_decoder_by_name("libdav1d")`），
以及软编 `libsvtav1/libx265/libx264`（见 §5.2）——**ARM64 包若缺 dav1d，AV1 软解会掉档**。

### 4.2 vcpkg（libass / libbluray 及其依赖树）—— 🟠 需要自己编

```
# third_party/fff_project/vcpkg.json
"dependencies": [ "libass", "libbluray" ]
```

而**所有准备脚本都把 triplet 锁死成 x64**：

```powershell
# 准备Libass.ps1:6-7        [ValidateSet("x64-windows")]  [string]$Triplet = "x64-windows"
# 准备光盘库.ps1:1          param([ValidateSet("x64-windows")][string]$Triplet = "x64-windows")
# tools/DiscRuntime.props:3 <DiscRuntimeRoot>...\vcpkg_installed\x64-windows</DiscRuntimeRoot>
```

**结论**：vcpkg 本身**支持** ARM64（官方 triplet 存在，我已拉取确认）：

```cmake
# vcpkg/triplets/arm64-windows.cmake
set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
```

⇒ 阻碍不在"vcpkg 不支持"，而在**要新编一整棵依赖树**：
`libass → freetype / harfbuzz / fribidi / libpng / zlib / brotli / libiconv / libxml2`，
外加 `libbluray → libudfread / libxml2 / ...`。
现磁盘上只有 `vcpkg_installed\x64-windows\`（含上述全部），**没有 `arm64-windows\`**。
这是一次**真编译**（不是下载），耗时最长，且可能踩到个别 port 的 ARM64 构建问题。

**逐 port 的 ARM64 准入已核查（均为 `supports` 声明，非实测）**：

| port | `supports` 声明 | ARM64 是否放行 |
| --- | --- | --- |
| `libbluray`（**本仓 overlay 版** `tools/vcpkg-ports/libbluray/vcpkg.json:8`） | `"windows & !uwp & !xbox"` | ✅ 未排除 arm64 |
| `libass` | 无 `supports` 限制 | ✅ |
| `freetype` 2.14.3 / `harfbuzz` 14.5.1 | `!uwp` 等 | ✅ |

> ✅ **一并排除了一个我原先担心的隐患**：`vcpkg-configuration.json` 引用的
> `overlay-ports: ["tools/vcpkg-ports"]` **确实存在**
> （`third_party/fff_project/tools/vcpkg-ports/libbluray/{portfile.cmake,vcpkg.json}`），
> 且该 overlay 的 portfile **没有任何 x64/架构硬编码**，`supports` 也未排除 arm64。
> （缺失 overlay 目录会导致 vcpkg 报错，此处不存在该问题。）

⚠ 注意"port 声明支持"≠"已有人编过"。**arm64-windows 下 libass/libbluray 全树是否一次编过，仍需实测**（见 §10）。

### 4.3 SkiaSharp / HarfBuzzSharp —— ✅ 包内自带 ARM64

```xml
<!-- src/3FCompare/3FCompare.csproj:80-81 -->
<_SkiaNativePath>$(NuGetPackageRoot)skiasharp.nativeassets.win32\...\runtimes\win-x64\native\libSkiaSharp.dll</_SkiaNativePath>
<_HarfBuzzNativePath>$(NuGetPackageRoot)harfbuzzsharp.nativeassets.win32\...\runtimes\win-x64\native\libHarfBuzzSharp.dll</_HarfBuzzNativePath>
```

`skiasharp.nativeassets.win32` / `harfbuzzsharp.nativeassets.win32` 这两个包**本身就带 `runtimes\win-arm64\native\`**
（Avalonia 官方支持 Windows ARM64）。⇒ **只要把 `win-x64` 改成随 RID 取值即可**，无需自建。

### 4.4 `3FC.WgcCapture.dll` —— 🔴 **本仓自研原生件，只支持 x64**

```bash
# native/wgc_capture/build.sh:61    [ -x "$m/bin/Hostx64/x64/cl.exe" ] || continue
# native/wgc_capture/build.sh:104   CL="${WGC_CL:-...}/bin/Hostx64/x64/cl.exe"
# native/wgc_capture/build.sh:118   export LIB="$VC_W\\lib\\x64;...\\ucrt\\x64;...\\um\\x64"
```
```c
/* native/wgc_capture/wgc_capture.h:11
 *   全部导出函数使用 **__cdecl**（宏 WGC_CALL）。本 DLL 只构建 x64，而 x64 上 ... */
```

**没有 `Hostarm64\arm64` 分支、没有 `lib\arm64`**，头注释直言"只构建 x64"。
⇒ 必须改脚本并重编（C++/WinRT **有** ARM64 支持，工作量小但必做）。

> ✅ **缓解**：抓屏有 GDI 兜底（`FrameCapture.cs:218-231` 吞异常 → `DllAvailable: false`；
> `FrameCapture.cs:127-164` 路由回落）。缺 WGC 时**降级不崩**，只是 D3D flip-model 抓屏不可靠。

### 4.5 `LakeUI` —— ⚠ 未在本次审计范围内

`3fp/README.md:92` 提到 `3fp/LakeUI → third_party/LakeUI` 联接，`KERNEL_REVIEW.md:73` 记
"3FP 当前仍引用该仓库的 Debug `LakeUI.dll`"。
3FCompare 自身不依赖它（只走 `FFF3FP_*`），但**若其 ARM64 版缺失，会影响 3FP 宿主的构建**。**待核实**。

---

## 5. 🔴 BLOCKER 集群二：工程文件没有 ARM64 配置

### 5.1 原生工程：`-p:Platform=arm64` **今天直接编不动**

```xml
<!-- FFF.Native.vcxproj:4-19 —— 只有 Win32 / x64，没有 arm64 -->
<ProjectConfiguration Include="Debug|x64"> ...
<ProjectConfiguration Include="Release|x64"> ...
```

更致命的是 **`LibassRoot` 挂在 x64 专属条件里**：

```xml
<!-- FFF.Native.vcxproj:77-80 -->
<PropertyGroup Condition="'$(Platform)'=='x64'">
  <TargetName>FFF.Native</TargetName>
  <LibassRoot>$(ProjectDir)..\third_party\vcpkg_installed\x64-windows</LibassRoot>
</PropertyGroup>
```

⇒ 用 `-p:Platform=arm64` 构建时**该 PropertyGroup 不成立**，`$(LibassRoot)` 为空字符串，
`:130/:137/:156/:163` 的 include/lib 路径整体塌陷，**编译必然失败**。
（bluray 的 `:223-228` 同样挂在 x64 条件上。）

**要做**：新增 `ARM64` ProjectConfiguration；把 `LibassRoot` 改为按 `$(Platform)` 求值
（`arm64` → `arm64-windows`）；FFmpeg 库目录 `lib\x64` → `lib\arm64`。

### 5.2 托管工程与打包：`win-x64` 写死在多处

| 位置 | 内容 |
| --- | --- |
| `3FCompare.csproj:55` | `...\FFF.Native\x64\$(KernelConfiguration)\FFF.Native.dll`（**内核 DllPath**） |
| `3FCompare.csproj:80-81` | `runtimes\win-x64\native\{libSkiaSharp,libHarfBuzzSharp}.dll` |
| `docs/PACKAGING_SPEC.md:125,136` | `dotnet publish -c Release -r win-x64` |
| `docs/PACKAGING_SPEC.md:23,33-34` | 命名规范 `-x64` / `-x64-full` |
| `tools/构建全部.ps1:203` | `/p:Platform=x64` |
| `tools/构建全部.ps1:210,212,232` | `FFF.Native\x64\...`、`vcpkg_installed\x64-windows\bin\ass-9.dll` |
| `tools/构建全部.ps1:172` | 缓存目录名 `...win64-lgpl-shared\include` |

⚠ **一个特别值得警惕的耦合**：`程序集内嵌` 的 4 个原生 DLL 用的是**架构中立的 `LogicalName`**：

```xml
<!-- 3FCompare.csproj:59,85,86,113 -->
<EmbeddedResource Include="$(KernelDllPath)" LogicalName="FFF.Native.dll" Condition="Exists('$(KernelDllPath)')" />
```

而守卫**只查文件存在、不查机器类型**：

```xml
<!-- 3FCompare.csproj:89-94 CheckNativeGraphicsDlls -->
<Error Condition="!Exists('$(_SkiaNativePath)')" Text="未找到 SkiaSharp 原生库 ..." />
```

⇒ **ARM64 构建会静默把 x64 的 SkiaSharp/HarfBuzz/内核嵌进去，而且"防静默缺失"的那道门禁照样放行**。
这正是 csproj `:67-71` 注释自称已经消灭的"构建全绿、用户启动即崩"形状——**在 ARM64 上它会复活**。

> **📌 该隐患已部分封堵**：见 [docs/53-ARM前瞻准备.zh.md](53-ARM前瞻准备.zh.md) §二
> ——加了运行期 PE 架构守卫与出包前核查探针；但**构建期仍只"记录"不"拦截"**（刻意的取舍，见该文 §2.4）。

### 5.3 运行期解压：平铺目录、无架构子目录、**只查存在不覆盖**

```csharp
// src/3FCompare/Program.cs:308-318
private static void ExtractEmbedded(string dllName)
{
    var target = Path.Combine(AppContext.BaseDirectory, dllName);
    if (File.Exists(target)) return;   // ← 已存在即跳过
```
```csharp
// src/3FCompare.Core/Backend/NativeRuntime.cs:20-23（FFF.Native.dll）
var targetDir = AppContext.BaseDirectory;
var targetPath = Path.Combine(targetDir, "FFF.Native.dll");
```

**后果**：同一个目录里若先后跑过 x64 版与 ARM64 版，
`File.Exists` 早退会**把上一架构的 DLL 喂给加载器**（`NativeRuntime.cs:36-53` 的"永不覆盖"策略是有意为之，
为防 2026-09-16 内核降级事故，但它对"跨架构"这个新维度没有防护）。

⇒ 建议改为 **`runtimes\win-<arch>\native\` 布局**或**写入时校验 PE 机器类型**。
（**运行期校验已实现**，见 docs/53 §2.3；目录布局的调整仍待迁移时做。）

### 5.4 ⚠ 加载器完全不感知架构（无检测、无校验、无 resolver）

全树 grep：`RuntimeInformation.*` / `Architecture.X64` / `IsWow64*` / `PROCESSOR_ARCHITECTURE`
⇒ **零命中**；`src/` 下也**没有任何硬编码 `"x64"` 字符串**（全在 csproj/sh/ps1 里）。
唯一的加载器控制是 `SetDllDirectoryW`（`NativeRuntime.cs:10-11`），
**没有 `NativeLibrary.SetDllImportResolver`** ⇒ 没有挂"按架构选库"逻辑的钩子。

同时 FFmpeg 探测**只按文件名匹配、不校验位数**：

```csharp
// NativeRuntime.cs:145 / 421 / 496
Directory.EnumerateFiles(dir, "avcodec-*.dll").Any()
```

⇒ ARM64 进程会把一个 **x64 的 FFmpeg 目录判定为"可用"**，然后在 Delay-Load 时失败。
（**已修**：`HasAvcodec` / `ValidateFfmpegDirectory` 现在校验 PE 机器类型，见 docs/53 §2.3。）

> ✅ **两处既有缓解值得保留**：`EngineFactory.cs:86` 捕获 `BadImageFormatException`；
> `FrameCapture.cs:218-231` 吞掉抓屏探测异常。⇒ **位宽不符表现为降级（演示模式/GDI），不是崩溃**。

---

## 6. 🟠 HIGH：能出产物但会残缺或静默出错

### 6.1 硬解在 ARM64 上大概率"名存实亡"（**这是最实质的产品级阻碍**）

即把工程问题全解决，ARM64 上的**硬件解码体验仍可能显著差于 x64**，原因是硬件与驱动侧的现实：

| 环节 | x64 现状 | ARM64 现实 |
| --- | --- | --- |
| **D3D11VA 解码** | NVIDIA/Intel/AMD 驱动成熟 | 取决于 **Adreno / 厂商驱动** 对 D3D11 视频解码的暴露程度，普遍弱于桌面独显 |
| **CUDA（回退）** | 可用 | CUDA 13.4 起**有条件**可用，但需 ARM 设备上插 NVIDIA RTX —— **绝大多数 WoA 设备是 Adreno 集显** |
| **视频处理器（色彩转换）** | `OPTIMAL_QUALITY` 路径 | 可能缺失或退化为软实现；好在内核**有 shader 兜底**（见下） |
| **`NVDEC`/`QSV`/`AMF` 硬编** | 全都有 | **全都没有**；QUALCOMM 的硬编未接入（见 6.2） |

**内核在此处设计得不错**，D3D11 video processor 失败会**自动回落 DXBC shader 路径**：

```cpp
// VideoRenderer.cpp:2986-2989
const auto fail = [this]() noexcept {
    videoProcessorConfigurationFailed_ = true;
    return FFFResult::NotSupported;
};
```
（配 `CanUseDirectVideoProcessor()` `:2937-2948` 与 `:3343/3353/3370` 的失败latch）

⇒ **不会崩，会变慢、变耗电**。但对 3FCompare 这种"9 路 1080p 盯帧对比"的重负载场景，
**软解 + shader 转换的功耗/性能代价足以让产品在 ARM 上不可用**——这才是真正需要产品决策的点。

> 🟡 附带一个**值得实机验证**的隐患：设备创建**没传 `D3D11_CREATE_DEVICE_VIDEO_SUPPORT`**
> （`VideoRenderer.cpp:2363-2366` 只有 `BGRA_SUPPORT`），却使用完整 video processor 栈
> （`EnsureVideoProcessor` `:2991-3043`）。这对 video **processor** 是合法的
> （`VIDEO_SUPPORT` 是 video **decoder** API 的要求），但在 ARM64 驱动上属于**最需要实测的表面**。

### 6.2 录屏/编码后端表里没有 ARM 厂商

```cpp
// VideoMuxer.cpp:111-184 —— 硬件编码只有这三家
{ "av1_nvenc", ... }, { "av1_qsv", ... }, { "av1_amf", ... },
{ "hevc_nvenc", ...}, { "hevc_qsv", ...}, { "hevc_amf", ...},
{ "h264_nvenc", ...}, { "h264_qsv", ...}, { "h264_amf", ...},
// + 软件 libsvtav1 / libx265 / libx264
```

⇒ **无 Media Foundation、无 Qualcomm/ARM 硬编**。对 3FCompare 影响有限（不走录屏），
但对 3FP/3FR 整体是 HIGH。软编仍可用 ⇒ 是性能问题不是功能中断。

### 6.3 Delay-Load 钩子**按名字字典序**选库——跨架构静默错载风险

```cpp
// Shared/Ffmpeg/FfmpegRuntime.cpp:68
const std::wstring searchPath = std::wstring(directory) + L"\\" + prefix + L"*.dll";
// :82-84  多个候选时取"字典序最大"者
if (selected.empty() || CompareStringOrdinal(candidate.c_str(), -1, selected.c_str(), -1, TRUE) == CSTR_GREATER_THAN) {
    selected = candidate;
}
// :89-90  然后 LoadLibraryExW
```

模块名表见 `:14-23`（`avcodec-63.dll` 等 8 个）。
**若 x64 与 ARM64 的 FFmpeg DLL 曾在同一目录共存**（加 ARM64 支持时极易发生），
这里会**按文件名排序挑一个，而不是按架构挑** ⇒ 错载/加载失败，且极难定位。

⇒ 修法：**用架构后缀目录（`...\win-arm64\`）或校验 PE 机器类型**。
⚠ 该文件属内核仓（`third_party/fff_project/`），**改动必须走 `PATCHES.md` 流程**——
详见 [docs/53](53-ARM前瞻准备.zh.md) §4.2（含"我原本直接改了、已回退"的记录）。

### 6.4 `DXGI_ADAPTER_DESC` 偏移把 `sizeof(SIZE_T)=8` 写死

```csharp
// src/3FCompare.Core/Display/DxgiOutputInfo.cs:103-109
private const int AdapterDescSize = 256 + 4 * 4 + 3 * 8 + 8;   // 3 个 SIZE_T 硬编码 8
private const int OffDedicatedVideoMemory = 256 + 16;
private const int OffAdapterLuid = 256 + 16 + 3 * 8;
```
⇒ **ARM64 同为 64 位，8 依然正确**，功能上不破（降级为 MEDIUM）。
但它是**"靠巧合正确"的脆弱写法**：改架构或改结构体就会静默读错内存。
（**已修**：改为按 `IntPtr.Size` 推导，见 docs/53 §3.1。）

> 📌 **一处需要纠正的常见误判**：vtable **槽位下标**（`SltGetDesc1 = 27` 等，`:96-101`）
> **不是** ARM64 风险——槽位是 COM 接口方法表位置，由 `dxgi.h` 定义，**跨架构 ABI 稳定**；
> 且取值用的是 `slot * IntPtr.Size`（`:160`），本身已是位宽正确写法。
> 该文件 `:41-95` 那段"两个方法数错导致整体前移"的血泪注释，讲的是 **COM 接口方法计数**问题，与 CPU 架构无关。

### 6.5 抓屏与 3FP 的架构耦合

- `3FC.WgcCapture.dll` 只有 x64（§4.4）⇒ ARM64 需改 `build.sh` 重编。
- 抓屏失败**有 GDI 兜底**，不崩，但 flip-model 下不可靠（`FrameCapture.cs:157-159`）。

### 6.6 🔴 托管侧有两处**真正的源码级** ARM64 阻碍（非工程配置）

这是全项目中**唯一两处必须改 C# 代码、而非改配置**的地方。

#### (a) `NativeFaultRecorder` 手写了 **x64 `CONTEXT` 完整布局**

```csharp
// src/3FCompare/Diagnostics/NativeFaultRecorder.cs:741-764
// x64 CONTEXT 的完整布局（权威口径由本机真头文件算出来，不是手算：…）
//   sizeof(CONTEXT)=1232、ContextFlags@48、Rax@120、Rsp@152、Rip@248 …
// RtlVirtualUnwind 会把解出来的寄存器**写回**这张表 ⇒ 交进去一个截断结构就是让它往栈上越界写。
[StructLayout(LayoutKind.Sequential)]
private struct CONTEXT
{
    public ulong P1Home, P2Home, … P6Home;      // @0…40
    public uint ContextFlags, MxCsr;            // @48、52
    …
    public ulong Rip;                           // @248
    public fixed byte Tail[976];                // 占位到 1232
}
```

同文件还有 `.pdata` 展开（`:557`）与 `RUNTIME_FUNCTION` 查找（`:853`）。

**ARM64 上为何是 BLOCKER**：ARM64 用的是**完全不同的 `ARM64_NT_CONTEXT` 布局**，
`.pdata`/`RUNTIME_FUNCTION` 的编码也不同。
而这段代码的自我防卫是**运行时核对"总尺寸==1232 且 Rip@248"**（`:751` 注释）——
在 ARM64 上该断言会不成立，于是**展开段被整体放弃**（代码自称"宁可没有展开段"）。
⇒ 表现是**崩溃转储失去调用栈**（诊断能力归零），而非崩溃本身；
但结合 NativeAOT 与本文件处于异常处理路径，属**必须重写**项。

#### (b) `PlayerSurface` 的 `PAINTSTRUCT` 期望值**按指针宽度选错分支**

```csharp
// src/3FCompare/Controls/PlayerSurface.cs:730-734
/// 按指针宽度取期望值：x64 = 8+4+16+4+4+32 = 72；x86 = 4+4+16+4+4+32 = 64。
private static readonly int NativePaintStructSize = IntPtr.Size == 8 ? 72 : 64;
```

**为何是陷阱**：ARM64 **同为 8 字节指针** ⇒ 该表达式**静默走 x64 分支取 72**。
若 ARM64 的 `PAINTSTRUCT` 实际 packing 与 x64 不同，静态构造函数的断言会失败；
而按该文件自己的注释（`:706`），不符即 **"启动即失败"**。
⇒ 必须显式改用 `RuntimeInformation.ProcessArchitecture` 判定。
（**已修**：见 docs/53 §3.2；但 ARM64 那一档的期望值仍需实机回填。）

> 📌 **修正一个常见误判**：先前有人把 `PlayerSurface.cs:734` 判为 NON-ISSUE
> （理由"ARM64 也是 8 字节 ⇒ 取 72 正确"）。**这个理由只在"ARM64 的 PAINTSTRUCT 与 x64 同布局"时才成立**，
> 而该断言的全部意义就是**验证布局**。用"指针宽度"当"架构"的代理，在 ARM64 上恰好无法区分 —— **属 HIGH/MEDIUM 级隐患，需 ARM64 工具链实测确认**。

---

## 7. 🔴 BLOCKER 集群三：工具链与发布脚本

全量清单（这些都是"改起来不难，但漏一个就构建失败"的项）：

| 文件:行 | 写死内容 | ARM64 需要 |
| --- | --- | --- |
| `tools/构建全部.ps1:203` | `/p:Platform=x64` | `arm64` |
| `tools/构建全部.ps1:210,232` | `FFF.Native\x64\...` | `FFF.Native\arm64\...` |
| `tools/构建全部.ps1:212` | `vcpkg_installed\x64-windows\bin\ass-9.dll` | `arm64-windows` |
| `tools/构建全部.ps1:99` | `MSBuild\Current\Bin\amd64\MSBuild.exe` | 找 `Bin\` 或 `arm64\` |
| `third_party/.../构建3FP.ps1:45` | `/p:Platform=x64` | `arm64` |
| `third_party/.../构建3FP.ps1:29` | `ffmpeg\lib\x64\avcodec-63.lib` | `lib\arm64` |
| `Resolve-Toolchain.ps1:229,240` | `bin\Hostx64\x64\cl.exe` | `Hostx64\arm64\`（交叉编译）或 `Hostarm64\arm64\` |
| `Resolve-Toolchain.ps1:135` | `MSBuild\...\amd64\MSBuild.exe` | 同上 |
| `Resolve-Toolchain.ps1:65` | 只要求 `VC.Tools.x86.x64` 组件 | 需加 ARM64 组件检测 |
| `准备FFmpeg.ps1:9,22,138` | `win64` URL、`lib\x64`、`/machine:x64` | `winarm64`、`lib\arm64`、`/machine:arm64` |
| `准备Libass.ps1:6`、`准备光盘库.ps1:1` | `ValidateSet("x64-windows")` | 放开到 `arm64-windows` |
| `发布3FP单文件.ps1:11,32`、`发布3FR单文件.ps1:15,29` | `publish\win-x64`、`/p:Platform=x64` | `win-arm64` |
| `generate_shader_bytecode.py:79` | `Windows Kits\10\bin\*\x64\fxc.exe` | 🟢 见下 |
| `third_party/.../DiscRuntime.props:3` | `vcpkg_installed\x64-windows` | `arm64-windows` |
| `docs/PACKAGING_SPEC.md:23,33-34,125,136` | 命名 `-x64`、`-r win-x64` | 加 `-arm64` 变体 |

**追加（第二轮全仓扫描新发现，此前遗漏）**：

| 文件:行 | 写死内容 | 后果 |
| --- | --- | --- |
| **`pack.ps1:88-91`** | `$Arch = "x64"` / `$Rid = "win-x64"`，且**不是 `param()` 而是裸赋值** | **打包脚本只能产出 win-x64**，无任何参数可覆盖 |
| `tools/发布门禁.ps1:566-571` | `3FCompare-v$Version-x64` / `-x64-full` 目录名 | 即使 pack 出 arm64，门禁仍找 x64 目录 ⇒ **假红**；若两者并存则**用 x64 包冒充 arm64 验证** |
| `tools/发布门禁.ps1:38-39,300-301` | `FFF.Native\x64\<配置>` | 同上 |
| `tools/build_kernel_3fp.py:45-47,294` | `Hostx64\x64\`、**`"/MACHINE:X64"`** | 该脚本服务 `3fp/kernel` 树，`/MACHINE:X64` 对 ARM64 是硬错误 |
| `tools/build_kernel_manual.py:19-26,71-72` | `Hostx64\x64`、`obj\x64\Release` | 手工编译路径全 x64 |
| `准备FFmpeg.ps1:138` | `/machine:x64` 生成**导入库** | **x64 COFF 导入库无法链入 ARM64 镜像** ⇒ 必须用 `/machine:arm64` 重新生成全部 7 个 `.lib` |
| `tools/构建全部.ps1:183` | `$assMarker` 指向 `x64-windows\include\ass\ass.h` | ⚠ **增量跳过门**：ARM64 机器上若已有 x64 libass，脚本会**跳过 libass 准备**直接去链 x64 库 |
| `native/wgc_capture/build.sh`（见 §4.4） | `Hostx64/x64`、`lib\x64` | 需 arm64 分支 |
| **`pack.ps1:535`** | 传 `-p:WgcCaptureRequired=true` | ⇒ ARM64 发布时**缺 arm64 WGC 库 = 硬性 publish 失败**（不是警告） |
| `third_party/.../tools/*`、`3fp/kernel/tools/*`、`tools/verify-scripts/**` | 约 18+ 处 `/p:Platform=x64`、`publish\win-x64`、`x64-windows` | 测试/验证脚手架，需同步才有意义 |
| `tools/verify-scripts/_review_pr/build_*.sh:30` | **绝对机器路径** `C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64;…` | 连换台 x64 机器都不可移植 |

**量化**：全仓约 **25 个文件**携带硬编码 `x64`/`win-x64` 字面量（`grep arm64|aarch64|win-arm|ARM64EC` 全仓**零命中**——ARM64 从未在任何地方被考虑过）。
另注：仓库**不存在** `Directory.Build.props` / `Directory.Build.targets` / `Directory.Packages.props` / `nuget.config`
⇒ **没有中心化的架构注入点**，这正是字面量散落 25 处的原因。

> 🟢 **`fxc.exe` 不是阻碍**：它是**宿主工具**，输出 DXBC 与宿主架构无关。
> 在 ARM64 机器上可通过仿真运行 x64 的 `fxc.exe`；也可用 `dxc` 替代。
> 只需把硬编码路径参数化（或改走 `dxc`）。

---

## 8. 已验证**可移植**的项（避免过度悲观）

| 项 | 证据 | 判定 |
| --- | --- | --- |
| 着色器 | `ShaderBytecode.h:12` = `DXBC`；7 个 blob 由驱动转译 | ✅ |
| 无 SIMD/汇编 | 全树 grep 零命中 | ✅ |
| 无 SEH | 全树 grep 零命中 | ✅ |
| 公开 C ABI | 定宽类型 + `void*` + `__cdecl`，无 `long`/pack | ✅ 托管层零改动 |
| WASAPI/MMCSS | 标准 MMDevice + `IAudioClient`；`avrt.lib` 全架构 | ✅ |
| 光盘 | 纯 libbluray，无驱动/无 DirectShow/MF | ✅ |
| QPC 时钟 | `QueryPerformanceCounter` + 溢出安全算术 | ✅ |
| DXGI vtable **槽位** | 跨架构 ABI 稳定；`slot * IntPtr.Size` 已正确 | ✅（常见误判） |
| `IntPtr.Size == 8 ? 72 : 64`（`PlayerSurface.cs:734`） | ⚠ **原判为 NON-ISSUE 是错的**——见 §6.6(b)：ARM64 同为 8 字节 ⇒ 静默取 x64 期望值，断言失去区分力 | ⚠ 待实测（**已改按架构判定**，见 docs/53 §3.2） |
| D3D11 本身 | 微软明确：DX9/10/11/12 在 Arm 上可用 | ✅ |
| .NET / Avalonia / NativeAOT | .NET 8+ 原生支持 Arm64；Avalonia 提供 `win-arm64` 资产 | ✅ |

### 8.1 依赖 ARM64 可得性矩阵（**已逐项核实**）

| 依赖 | 获取方式 | ARM64 可得性 | 核实来源 |
| --- | --- | --- | --- |
| FFmpeg shared DLL | BtbN（URL 写死 win64） | ✅ `ffmpeg-*-winarm64-*-shared.zip` | GitHub Releases API |
| SkiaSharp 3.119.4 原生 | Avalonia 传递 → NuGet | ✅ 包内含 `runtimes/win-arm64/native/libSkiaSharp.dll` | `obj/project.assets.json` |
| HarfBuzzSharp 8.3.1.3 | 同上 | ✅ 包内含 `runtimes/win-arm64/native/…` | 同上 |
| **ANGLE**（`av_libglesv2.dll`） | Avalonia.Angle.Windows.Natives | ✅ 包内含 `runtimes/win-arm64/native/…` | 同上 |
| **NativeAOT ILCompiler** | .NET SDK | ✅ `runtime.win-arm64.Microsoft.DotNet.ILCompiler`（含本仓已解析的 `11.0.0-rc.1.26425.128`） | `project.assets.json` |
| libass / libbluray / 整棵 vcpkg 树 | vcpkg | ✅ port `supports` 未排除 arm64；triplet 存在 | 本仓 + 上游 vcpkg |
| 6 个 clang 运行期 DLL（`libc++/libomp/libunwind/libwinpthread-1/libshaderc_shared/libSPIRV-Tools-shared`） | 随 BtbN 归档附带 | ⚠ 应随 winarm64 归档提供，**未逐文件核实** | 待实测 |
| 已入库的 `runtime\*.dll`、`3FC.WgcCapture.dll` | 提交进仓库 | ⚠ **按来源推断为 x64，未做 PE 机器类型校验** | 待实测（**现可用 `tools/架构核查.ps1` 定案**） |

> 🟢 **关键结论（修正原先的悲观度）**：**本项目所需的第三方 ARM64 资产几乎全部已存在**；
> 缺的只是"把它们取对、接对"。⇒ 这是**管道工程**，不是可行性问题
> （唯二例外是 §6.6 的两处 C# 源码改动）。

---

## 9. 建议路径（按性价比排序）

### 路线 A：**Arm64EC 渐进迁移**（推荐首选）

理由：官方明确 Arm64EC 就是为"**依赖还没全好**"设计的——
*"you can start to rebuild parts of your app as Arm64EC to gain the benefits of native performance"*，
且 **Arm64EC 进程可同时加载 x64 与 Arm64EC 二进制**。

具体做法：
1. **不动 x64 依赖**（FFmpeg/libass/libbluray/LakeUI 继续用 x64 版，走仿真）。
2. 只把 **3FCompare 托管侧**编成 `win-arm64`（NativeAOT 原生，CPU 侧立刻native）。
3. 把 **`FFF.Native` 编成 Arm64EC**（`/arm64EC`），——
   它只链接 x64 的 FFmpeg/libass import lib（**Arm64EC 可以链 x64 lib**，官方允许）。
4. 收益：解码/渲染/时钟等 CPU 密集逻辑原生执行；FFmpeg 内部仍仿真。
5. 风险：`DxgiOutputInfo.cs` 的裸 COM 委托用了 `CallingConvention.StdCall`（`:125-141`），
   **ARM64EC 下的混合调用约定互操作需实测**（见 §10 待验证项）。

> ⚠ 注意：Arm64EC 的预处理器定义**与 x64 相同**（`_M_AMD64`/`_AMD64_` 被定义，见官方 ABI 文档）。
> 因此**不能用 `#ifdef _M_AMD64` 区分**，区分要用 `_M_ARM64EC`。

### 路线 B：**纯 ARM64**（最彻底，工作量最大）

必须**全部依赖都拿到 ARM64 版**，否则进程加载即失败：
1. FFmpeg：换 BtbN `winarm64` 包（✅ 已有），改脚本 + 确认含 `libdav1d`。
2. libass/libbluray：**vcpkg 编 `arm64-windows` 整棵依赖树**（最耗时的一步）。
3. SkiaSharp/HarfBuzz：csproj 改用 `win-arm64` 资产（✅ 包内已有）。
4. `3FC.WgcCapture.dll`：改 `build.sh` 支持 arm64 重编。
5. vcxproj 加 `ARM64` 配置 + 修正 `LibassRoot` 条件。
6. 全量工具链脚本去 x64 硬编码（§7）。

### 路线 C：**先发"仿真可用"版本 + 声明**（零成本，立刻可做）

在 ARM 上以 x64 包运行（Prism 仿真），**明确文档化"ARM 上为仿真运行，硬解可能不可用"**，
同时把 §5.3/§5.4 的**架构校验补上**——因为即便走仿真，
"x64 包 + 用户误放 ARM64 DLL"或反之的**静默错载**风险已经存在。

> **📌 实施进度见 [docs/53-ARM前瞻准备.zh.md](53-ARM前瞻准备.zh.md)**：
> 路线 C 里"补架构校验"的那部分**已经做了**（运行期 `NativeArchGuard` + 构建期清单 +
> `tools/架构核查.ps1`），且**未改动内核**；其余路线留待生态成熟后按本文 §11 执行。

### 无论走哪条路，都建议先做的**低风险高收益**改动

| 改动 | 位置 | 收益 | 状态 |
| --- | --- | --- | --- |
| CUDA 回退项 `#ifdef` 收敛 | `PlayerSession.cpp:2013-2019` | 去掉 ARM64 上每次开媒体的空转 | ⬜ 待办（内核，走 patch） |
| 3FR 的 `D3DCompile` 预编译为 DXBC | `VideoMuxer.cpp:1065` | **根除**运行期 `d3dcompiler_47` 依赖 | ⬜ 待办（内核，走 patch） |
| Delay-Load glob 架构感知 | `FfmpegRuntime.cpp:68,82-84` | 消除跨架构静默错载 | ⬜ 待办（内核，走 patch） |
| 内嵌 DLL 加 PE 机器类型校验 | `Program.cs`、`NativeRuntime.cs` | 让"嵌错架构"从静默变**可归因** | ✅ **已做**（docs/53 §2） |
| 解压改 `runtimes\win-<arch>\native\` | `src/3FCompare/Program.cs:308`、`NativeRuntime.cs:20` | 消除跨架构残留污染 | ⬜ 待办（迁移时做） |
| `DXGI_ADAPTER_DESC` 偏移改用 `IntPtr.Size` 推导 | `DxgiOutputInfo.cs:103-109` | 去掉"靠巧合正确" | ✅ **已做**（docs/53 §3.1） |
| `PAINTSTRUCT` 断言改用 `ProcessArchitecture` | `PlayerSurface.cs:734` | 恢复该断言的**区分力**（§6.6b） | ✅ **已做**（docs/53 §3.2） |

---

## 10. 必须在真机上验证的事项（本次无法验证）

> 本机 `pwsh` 不可用（`STATUS_DLL_INIT_FAILED`，`0xC0000142`），**没有任何一次真实构建或运行**。
> 所有"存在/不存在"的判断均来自源码与工程文件阅读，**未做任何 PE 机器类型校验**。
> （其中第 9 条**现在可以用 `tools/架构核查.ps1` 直接定案**，见 docs/53 §2.6。）

1. **`d3dcompiler_47.dll` 是否有 ARM64 版**——决定 §3.4 是 BLOCKER 还是发布细节。
   （🟢 但按 §9 的预编译修法可**直接绕开此问题**，建议不必纠结。）
2. **ARM64 上的 D3D11 视频解码与 video processor 实际能力**（Adreno 驱动）。
   判据：内核日志里 `videoProcessorConfigurationFailed_` 是否被置位；
   以及 `PresentWait100ns / SwapChainPresents` 表现（沿用既有 VRR 量法）。
3. **ARM64EC 下裸 COM 委托的调用约定互操作**（`DxgiOutputInfo.cs:125-141`）。
4. **`-p:Platform=arm64` 能否在装了 v145 + ARM64 组件后真正解析**（本次未能跑 msbuild）。
5. **BtbN `winarm64` 包是否含 `libdav1d`**（内核 `PlayerSession.cpp:1904` 点名要它）
   及本项目需要的软编器（`libsvtav1/libx265/libx264`）。
6. **`arm64-windows` triplet 编 libass/libbluray 全树是否一次通过**
   （含 freetype/harfbuzz/fribidi/libxml2；**port 声明支持 ≠ 已有人编过**）。
7. **ARM64 上 9 路 1080p 对比的性能与功耗**（软解 + shader 色彩转换的代价）。
8. **`LakeUI` 是否需 ARM64 版**（影响 3FP 宿主，不影响 3FCompare）。
9. **入库二进制的真实位数**（`runtime\*.dll` 12 个、`native\wgc_capture\3FC.WgcCapture.dll`）——
   本次按"BtbN win64 来源"推断为 x64，**未逐字节校验**。
   ✅ **现在跑 `pwsh -File tools/架构核查.ps1 -Expected x64` 即可定案**。
10. **ARM64 的 `PAINTSTRUCT` 实际 packing**（§6.6(b)）——该断言的整个意义就是验证它，
    必须用真 ARM64 工具链算出来。
11. **`ARM64_NT_CONTEXT` 与 `.pdata` 在 ARM64 上的实际布局**（§6.6(a)），以重写 `NativeFaultRecorder`。

---

## 11. 建议动手顺序（按依赖关系排序）

1. **vcxproj**：加 `ARM64` ProjectConfiguration **并**为 `'$(Platform)'=='ARM64'` 定义 `LibassRoot` → `arm64-windows`（§5.1）。
2. **参数化**：`pack.ps1` 的 `$Arch`/`$Rid` 改为 `param()`（§7）；`构建全部.ps1` 的 `-p:Platform`/triplet 同理。
3. **csproj 两处 `win-x64`** → 随 RID 取值（§5.2，资产已在包里）。
4. **`KernelDllPath`** 改为按架构取值（§5.3）；运行期位数校验**已完成**（docs/53 §2）。
5. **FFmpeg**：URL 换 `winarm64` + 导入库用 `/machine:arm64` 重生成 + 重新取那 6 个 clang DLL（§4.1/§7）。
6. **vcpkg**：编 `arm64-windows` 全树（最耗时）。
7. **port 工具**：`build_kernel_3fp.py` / `build_kernel_manual.py` / `wgc_capture/build.sh` 加 arm64 分支（§7）。
8. **门禁**：`发布门禁.ps1` 架构参数化，避免用 x64 包冒充 arm64 验证（§7）。
9. **两处 C# 源码**：`PlayerSurface` 的架构判定**已做**（docs/53 §3.2）；
   `NativeFaultRecorder` 的 `CONTEXT`（§6.6a）**仍待做**（需 ARM64 实机才能验证）。
10. **发布规范**：`PACKAGING_SPEC.md` 增 `-arm64` 变体命名与命令（§7）。

---

## 12. 结论

**"3FP 做 Windows ARM 的阻碍"** 的准确画像是：

1. **源码层几乎无阻碍**（无 SIMD/汇编/SEH，ABI 干净，着色器是架构无关 DXBC）。
   这**超出一般预期**，是本项目前期工程质量的直接体现。
   **唯二例外**是托管侧两处 C#：`NativeFaultRecorder` 手写的 x64 `CONTEXT`（§6.6a）
   与 `PlayerSurface` 用指针宽度当架构代理的断言（§6.6b）——**后者已改**。
2. **真正的阻碍是"管道"，不是"能力"**：
   - **第三方依赖几乎不缺 ARM64 版**：FFmpeg（BtbN `winarm64`）、SkiaSharp/HarfBuzz/ANGLE（包内自带）、
     NativeAOT（`runtime.win-arm64` ILCompiler）、vcpkg 树（port 未排除 arm64）——**全部已存在或可编**。
   - 缺的是**约 25 个文件里的硬编码 `x64`** 与**工程配置**：
     vcxproj 无 ARM64 配置且 `LibassRoot` 挂 x64 条件；`pack.ps1` 把 `$Arch`/`$Rid` 写成裸赋值
     （**根本没法产出 arm64 包**）；`发布门禁.ps1` 只认 `-x64` 目录名（可能**用 x64 包冒充 arm64 验证**）。
   - **全仓 `grep arm64|aarch64` 零命中**，且**没有 `Directory.Build.props`** 作中枢 ⇒ 改动面广但每处都浅。
3. **最实质的产品级阻碍不在能不能跑，而在跑得好不好**：
   ARM64 设备的 GPU 硬解/硬编能力远弱于桌面独显，CUDA 回退对绝大多数 WoA 设备（Adreno 集显）无用。
   即便全部打通，**"9 路 1080p 盯帧对比"在 ARM 上很可能只能软解**——这是需要**产品决策**（而非纯工程）的点。
4. **推荐路线**：**Arm64EC 渐进迁移**（官方为此场景设计，可保留 x64 依赖）
   → 后续再逐项替换为纯 ARM64。同时**立刻**做 §9 的低风险改动，
   它们无论最终选哪条路线都有价值，且能消除当前**已存在**的静默错载隐患。
5. **一句话**：这不是一个"能不能做"的问题，而是一个"**愿不愿意把 25 处 x64 字面量参数化、并接受 ARM 上硬解体验下降**"的问题。
