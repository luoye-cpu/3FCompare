# 53 · ARM（Windows on Arm）前瞻准备

> **本文的性质**：这是一份**"先做最低成本准备、等生态成熟再真正迁移"**的记录。
> 它**不是**迁移计划，也**不启动**任何 ARM64 产出。
>
> 决策（用户）：**现在不做 ARM 移植**。只做那些"**将来迁移时最难回头、而现在做几乎零成本**"的事；
> 其余（加 ARM64 工程配置、编 arm64 依赖树、改 25 处构建脚本）一律**留到生态成熟后**，
> 届时按 [52-3FP-Windows-ARM64-可行性分析.zh.md](52-3FP-Windows-ARM64-可行性分析.zh.md) §11 的顺序执行。
>
> **可行性分析的结论见 [docs/52](52-3FP-Windows-ARM64-可行性分析.zh.md)**（11 项 BLOCKER、依赖可得性矩阵、三条迁移路线）。
> 本文只记"**已经做了什么**"与"**将来怎么接上**"。

---

## 一、为什么只做这些（判断依据）

迁移 ARM 的昂贵之处**不在写代码**，而在三件"事后要回头重做、且容易做错"的事。
本次准备正是针对它们，且**每一项都不改变现有 x64 行为**：

| # | 将来最痛的点 | 为什么"事后做"代价高 | 本次应对 |
| --- | --- | --- | --- |
| 1 | **内嵌原生 DLL 的架构静默错配** | 内嵌资源的 `LogicalName` 是架构中立的，两个产线写同一个名字。等真出 arm64 包时才发现 ⇒ 要在一堆"构建全绿但启动即崩"里定位 | ✅ 加**运行期**守卫（`NativeArchGuard`）＋**构建期**清单 |
| 2 | **"靠巧合正确"的硬编码** | `sizeof(SIZE_T)=8`、`IntPtr.Size==8` 当架构代理——今天恰好对，将来会静默读错内存 | ✅ 改成按架构/指针宽度**显式表达** |
| 3 | **内核侧的改动纪律** | 内核是独立 git 仓，改动必须走 patch 流程；**临时直接改会污染构建基线** | ✅ 本次**未改动内核**，把该改的登记为待办（§四） |

> ⚠ **本次刻意"不做"的**：ARM64 工程配置、`arm64-windows` vcpkg 树、25 处 x64 字面量、`pack.ps1` 参数化、
> `NativeFaultRecorder` 的 x64 `CONTEXT` 重写。理由：它们要么成本高，要么**只有在真要出 arm64 包时才有意义**，
> 现在做等于维护一份无人使用的分支。

---

## 二、已落地：架构一致性守卫（对应可行性分析 §5.3 / §5.4 / §6.3）

### 2.1 背景：一个真实存在的隐患（今天就成立）

不依赖将来，**现在**就有这个形状的问题：

1. 4 个原生 DLL 以**架构中立**的 `LogicalName` 内嵌（`FFF.Native.dll`、`libSkiaSharp.dll`、
   `libHarfBuzzSharp.dll`、`3FC.WgcCapture.dll`），而来源路径全部写死 x64；
2. 运行期解压按 `File.Exists` **早退且不覆盖**（那是有意为之，为防 2026-09-16 的内核降级事故）；
3. FFmpeg 目录探测**只按文件名匹配** `avcodec-*.dll`，**不看位数**；
4. 内核的 Delay-Load 钩子（`FfmpegRuntime.cpp`）在多个候选间取**字典序最大**者，**不看架构**。

⇒ 若同一目录先后被两个架构的包使用过，会拿到**另一个架构**的同名 DLL：
它既不缺失、也不报错，只在加载时失败——表现为"静默回落演示模式"或"启动即崩"，
而**现有的所有守卫（`CheckNativeGraphicsDlls` / `CheckKernelDll` / `CheckWgcCaptureDll`）
都只查 `Exists()`，会照常放行**。

### 2.2 新增：`NativeArchGuard`（`src/3FCompare.Core/Backend/NativeArchGuard.cs`）

一个小而独立的工具类，读 PE 头的机器类型并回答"这份 DLL 当前进程能不能加载"：

| 成员 | 作用 |
| --- | --- |
| `ReadMachine(ReadOnlySpan<byte>)` / `ReadMachineFromFile(path)` | 读 `e_lfanew(@0x3C) → "PE\0\0" → Machine(2B)`；**任何异常返回 `Unknown`，绝不抛出** |
| `IsLoadableByCurrentProcess(machine)` | 与 `RuntimeInformation.ProcessArchitecture` 比对 |
| `Describe` / `DescribeProcess` | 中文可读名，供日志与报错使用 |

**两条刻意的设计取舍**（都是"宁可漏报也不误伤"）：
- **读不出机器类型 ⇒ 放行**。加壳/非常规产出的合法 DLL 不应被拦。
- **Arm64EC 视作 x64**。Arm64EC 的 PE 头就是 `0x8664`，x64 进程能加载它 ⇒
  **不会误伤 Arm64EC 迁移路线**（可行性分析 §9 路线 A）。
  ⚠ 但**不能用 `_M_AMD64` 区分 EC 与真 x64**——EC 下该宏同样被定义，要用 `_M_ARM64EC`。

### 2.3 接线的三处

| 位置 | 行为 | 为什么这样选 |
| --- | --- | --- |
| `Program.cs` `ExtractEmbedded`（4 个托管原生库） | 磁盘上已有该文件时核对架构，**错配则记 Error + 抛异常** | 这 4 个库**没有降级路径**，错配必然崩；抛出的异常被调用方 `catch` 转成可读原因，比"启动即崩"强 |
| `NativeRuntime.ExtractEmbeddedDll`（`FFF.Native.dll`） | 错配时**只记 Error、不抛** | 内核**有**降级路径（`EngineFactory` 回落演示模式），抛出会把"可降级"变成"启动失败" |
| `NativeRuntime.HasAvcodec` / `ValidateFfmpegDirectory` | 目录里的 `avcodec*.dll` 全部架构错配时判为**不可用**，并在提示里写出实际架构 | 过去用户会在设置里看到"有效"绿灯、实际回落演示模式，原因完全不指向位数 |

> **不误伤多架构共存**：`HasAvcodec` 只要**有一个** DLL 是本架构可加载的就算目录可用。
> 目录里同时躺着两种架构时不会因为"存在错配的那份"而整体否掉一个本来能用的目录。

### 2.4 构建期：只"记录"，不"拦截"

`3FCompare.csproj` 新增 `WriteNativeArchManifest` target，把**期望架构**与各内嵌原生件的
路径/是否存在写成 `$(TargetDir)native-arch-manifest.txt`。

**为什么这里不直接报错**（重要取舍）：
在 MSBuild 里比对 PE 机器类型需要**内联任务**（本仓库此前**零内联任务先例**），
而它跑在**每一次构建**上；一旦写歪，代价是**把当前可用的 x64 构建弄坏**——与"前瞻准备"的目的相反。
故该 target **只用纯属性/ItemGroup 与 `WriteLinesToFile`**，不引入任何 task、不调用外部进程，
**构建期绝不可能失败**；判定交给下面两个更安全的位置。

### 2.5 出包前：`tools/架构核查.ps1`（**显式调用，不接入既有流程**）

一个独立的核查探针，读仓库内所有原生二进制（`runtime/`、`native/wgc_capture/`、内核输出目录、
`bin/`）的 PE 机器类型，报告与给定架构是否一致：

```powershell
pwsh -File tools/架构核查.ps1                      # 按当前进程架构核查
pwsh -File tools/架构核查.ps1 -Expected arm64       # 显式指定（迁移时用）
pwsh -File tools/架构核查.ps1 -Expected x64 -Quiet  # 只出结论行
```

- **退出码**：0 = 无错配；1 = 存在错配（可直接用于 CI）。
- **不接入任何既有流程**：不跑就不可能弄坏现在的构建（这是本准备项的底线）。
- **默认不扫 `third_party`**（那里 4 万+ 文件、绝大多数是 vcpkg 源码树里与我们产物无关的 DLL）：
  加 `-IncludeThirdParty` 才递归。

### 2.6 即时价值：它已经定案了一个悬而未决的问题

可行性分析 §10 第 9 条遗留了一个**未能验证**的问题：
**入库的 12 个 `runtime\*.dll` 与 `3FC.WgcCapture.dll` 究竟是不是 x64？**

✅ **已定案（本次实跑）**：

```
$ pwsh -NoProfile -File tools\架构核查.ps1 -Expected x64
  期望架构 : x64  （可接受的机器类型: 0x8664）
  扫描根   : 6 个目录
  原生文件 : 93 个（一致 93 / 错配 0 / 读不出 0 / 跳过托管 0）
✅ 未发现与期望架构 x64 错配的原生二进制。      （退出码 0）
```

⇒ **仓库内 93 个原生二进制全部是 x64，没有混入其它架构**。
之前只能"按 BtbN win64 来源推断"，现在是实测结论。
（退出码可用：`-Expected arm64` 时正确返回 **1** 并列出全部错配项。）

**顺带得到的两个副产品**：
1. **完成了一件悬着的验证**——§10 第 9 条现在可以划掉。**若无本次改动，这条会一直悬着。**
2. 探针会扫 `publish\`，于是**已发布的包也能顺手复检**
   （实测 `publish\3FCompare-v0.3.0-beta-x64-full\` 内的 exe/DLL 全是 x64）。

### 2.7 运行期守卫的行为已验证（实跑）

用一个临时控制台工程直接调用 `NativeArchGuard`，四种输入的结果：

| 输入 | 识别为 | 当前进程可加载 | 是否符合预期 |
| --- | --- | --- | --- |
| `third_party\...\FFF.Native.dll`（真实） | x64 | True | ✅ |
| `native\wgc_capture\3FC.WgcCapture.dll`（真实） | x64 | True | ✅ |
| **合成的 ARM64 PE**（机器类型 `0xAA64`） | ARM64 | **False** | ✅ **正确拦下跨架构** |
| 随机字节（非 PE） | 未知 | True | ✅ 按设计**放行**（不误伤） |

⇒ 两条关键性质都成立：**该拦的拦住了，该放的没误伤**。

---

## 三、已落地：去掉"靠巧合正确"的写法（对应可行性分析 §6.4 / §6.6b）

这两处的共同特征是：**今天在 x64 上结果正确，但正确的原因是巧合**，且都用"指针宽度"当了"架构"的代理。

### 3.1 `DxgiOutputInfo.cs`：把 `sizeof(SIZE_T)` 从常量改为推导

```csharp
// 改前（注释自己都写着"3 个 SIZE_T"，却把 8 烘进了 const）
private const int AdapterDescSize = 256 + 4 * 4 + 3 * 8 + 8;
private const int OffAdapterLuid    = 256 + 16 + 3 * 8;

// 改后
private static readonly int SizeOfSizeT = IntPtr.Size;
private static readonly int AdapterDescSize = 256 + 4 * 4 + 3 * SizeOfSizeT + 8;
private static readonly int OffAdapterLuid  = 256 + 4 * 4 + 3 * SizeOfSizeT;
private static readonly int OffDedicatedVideoMemory = 256 + 4 * 4;
```

- **语义变了没有？** 没有。x64 与 ARM64 指针同为 8 字节 ⇒ 数值完全一致，**现有行为零变化**。
- **改的意义**：`DXGI_ADAPTER_DESC` 里的 `SIZE_T` 是**随指针宽度变化**的字段，
  原写法把它表达成了**架构无关的常量**。一旦将来出现 32 位目标或结构体调整，
  就会**静默读到错位字段**（`Marshal.ReadInt64` 去读一个 4 字节字段）。
- `OffVendorId` / `OffDeviceId` 位于 3 个 `SIZE_T` **之前**，与指针宽度无关，故保留字面量并注明依据。
- ⚠ 静态字段初始化按**文本顺序**执行，故 `SizeOfSizeT` 必须声明在依赖它的字段**之前**（已如此）。

### 3.2 `PlayerSurface.cs`：`PAINTSTRUCT` 断言改按架构判定

```csharp
// 改前：IntPtr.Size 是"指针宽度"的代理，不是"架构"的代理
private static readonly int NativePaintStructSize = IntPtr.Size == 8 ? 72 : 64;

// 改后：显式按架构取期望值
private static readonly int NativePaintStructSize =
    RuntimeInformation.ProcessArchitecture == Architecture.X86 ? 64 : 72;
```

**为什么这不是等价改写**（这是本次改动里最值得记的一条）：
Windows 的 **x64 与 ARM64 指针同为 8 字节**，所以改前的表达式在 ARM64 上会
**静默走进 x64 分支取 72**。该断言的全部意义就是**验证 `PAINTSTRUCT` 的布局**，
而它恰好**无法区分 x64 与 ARM64** ⇒ 在 ARM64 上它要么误报、要么**从未真正验过 ARM64**。

- **对现有 x64 行为的影响**：零（x64 仍取 72，与改前一致）。
- ⚠ **ARM64 那一档的 72 尚未实测**：它是按"x64 与 ARM64 都是 8 字节指针 ⇒ 同一布局"推出的
  （与 x64 同理成立）。**迁移时应在 ARM64 上真跑一次，把结果写回该行注释**——这正是这个断言的用途。

---

## 四、登记在案：将来迁移时的内核侧改动（**本次未做，且不应临时做**）

### 4.1 为什么没做：内核改动有纪律

内核源码 `third_party/fff_project/` 是**独立 git 仓库**，是 3FC 的**构建基线**，
被 `tools/构建全部.ps1` 按**钉死的 SHA** 构建，实况记进 `.3fc_kernel_build.json`；
`tools/发布门禁.ps1` 会据此判红、阻止出包。
`3fp/README.md` 的**铁律 1** 明确：**绝不改 `third_party/fff_project/`**，
改动走 `PATCHES.md` 的重移植流程。

> **本次实践**：我一开始把 §4.2 的架构感知直接写进了
> `third_party/fff_project/FFF.Native/Shared/Ffmpeg/FfmpegRuntime.cpp`，
> 核对铁律后**已完整回退**（该文件恢复为原始 116 行，逐行核对过）。
> 记在这里是因为**将来也会有人想直接改**——那时请走 patch 流程，不要直接写。

### 4.2 待办 A：Delay-Load 的架构感知（可行性分析 §6.3）

**位置**：`FFF.Native/Shared/Ffmpeg/FfmpegRuntime.cpp` → `LoadMatchingModule()`

**现状**：按 `prefix*.dll` 通配挑库，多个候选时取**字典序最大**者（`CompareStringOrdinal(...) == CSTR_GREATER_THAN`）。
一旦同目录并存两种架构的 FFmpeg，挑选取决于**文件名排序**而非**架构** ⇒
"有时能播、有时加载失败"，且失败点落在 Delay-Load 里，几乎没有可归因信息。

**建议做法**：在该函数内读 PE 头机器类型，只接受与**本模块**同架构的候选；读不出的留作兜底。
编译期判定**必须用 `_M_ARM64` / `_M_ARM64EC`**，**不能用 `_M_AMD64`/`_M_X64`**
（Arm64EC 下这两个宏同样被定义，见 Microsoft *Understanding Arm64EC ABI*）。

**更省事的替代方案**：让 FFmpeg DLL 按架构分目录存放（`win-x64\` / `win-arm64\`），
从根上避免"同目录并存"——若采用这条，本待办可以**不做**。

### 4.3 待办 B：CUDA 回退项在 ARM64 上空转（可行性分析 §3.3）

**位置**：`FFF.Native/3FP/Core/PlayerSession.cpp` 的 `Backends[]`（约 `:2013-2019`）

`{D3D11VA, CUDA}` 是硬编码有序表，ARM64 上 CUDA 那一档永远失败（除非 ARM 设备插 NVIDIA RTX 且装了
[支持 Windows on Arm 的 CUDA 13.4+](https://developer.nvidia.com/blog/cuda-toolkit-13-4-adds-windows-on-arm-support-and-greater-control-over-shared-gpus/)）。
**不阻断**（失败后会干净回落软解），只是每次开媒体白跑一轮。
建议 `#ifdef _M_ARM64` 收敛为 `{D3D11VA}`。

### 4.4 待办 C：3FR 录屏的 `D3DCompile`（可行性分析 §3.4）

**位置**：`FFF.Native/3FR/Ffmpeg/VideoMuxer.cpp:1065`

运行期编译一个 compute shader ⇒ 需要 ARM64 的 `d3dcompiler_47.dll`。
**对 3FCompare 无影响**（它只 P/Invoke `FFF3FP_*`，不走 3FR 路径），
但对 3FP/3FR 本体是 BLOCKER。
**推荐修法**：与播放器一致，把这一个 shader **预编译成 DXBC 常量数组**——**根除**运行期依赖，
比"把依赖搬过去"干净。

### 4.5 待办 D：`NativeFaultRecorder` 的 x64 `CONTEXT`（可行性分析 §6.6a）

**位置**：`src/3FCompare/Diagnostics/NativeFaultRecorder.cs:741-764`（另见 `:557`、`:853`）

手写了 x64 `CONTEXT` 的完整布局（`sizeof=1232`、`Rip@248`、`Tail[976]` 占位），
并依赖"运行时核对尺寸==1232 且 Rip@248"。ARM64 用完全不同的 `ARM64_NT_CONTEXT`，
`.pdata`/`RUNTIME_FUNCTION` 编码也不同 ⇒ 该断言在 ARM64 上不成立，**展开段被整体放弃**
（代码自称"宁可没有展开段"）⇒ **崩溃转储失去调用栈**（诊断能力归零），而非崩溃本身。

**为什么本次没做**：这是**真正的代码重写**（不是配置改动），且**只有在 ARM64 上跑过才能验证**。
现在写等于写一份无法验证的代码 ⇒ 违反"本次只做可静态确认的准备"。

⚠ **注意**：托管侧需要改 C# 的只有两处，另一处（§3.2）本次已做。

---

## 五、将来怎么接上（迁移时的入口）

生态成熟、决定真做时，**从这里开始**：

1. **读 [docs/52](52-3FP-Windows-ARM64-可行性分析.zh.md)** —— 11 项 BLOCKER 与完整依赖矩阵。
2. **§9 选路线**：推荐 **Arm64EC 渐进迁移**（可保留 x64 依赖，官方为此场景设计）。
3. **按 docs/52 §11 的 10 步顺序执行**（vcxproj → 参数化 → csproj 路径 → …）。
4. **用本次留下的工具收口**：
   - `pwsh -File tools/架构核查.ps1 -Expected arm64` —— 出包前核查**所有**原生件架构一致；
   - `native-arch-manifest.txt`（构建产物）—— 快速看这次构建"期望什么架构、嵌了哪些原生件"；
   - 运行期 `NativeArchGuard` 的 Error 日志 —— 用户现场"启动即崩"时的第一手归因。
5. **补 §4 的四个内核待办**（走 `PATCHES.md` 流程，不要直接改 `third_party/`）。
6. **在 ARM64 上实测并回填**：
   - `PlayerSurface.NativePaintStructSize` 的 ARM64 档（§3.2）；
   - `ARM64_NT_CONTEXT` / `.pdata` 布局（§4.5）；
   - ARM64 上 D3D11 视频处理器是否可用（判据：内核日志里 `videoProcessorConfigurationFailed_`）。

---

## 六、本次改动的完整清单

| 文件 | 类型 | 改动 | 对 x64 行为的影响 |
| --- | --- | --- | --- |
| `src/3FCompare.Core/Backend/NativeArchGuard.cs` | 🆕 新增 | PE 机器类型读取与比对工具（约 190 行） | 无（新增，未被调用前不生效） |
| `src/3FCompare/Program.cs` | 修改 | `ExtractEmbedded` 增加架构核对；错配记 Error + 抛（三处调用点均已 `catch`） | 无（仅在错配时触发） |
| `src/3FCompare.Core/Backend/NativeRuntime.cs` | 修改 | 内核错配记 Error（不抛）；`HasAvcodec`/`ValidateFfmpegDirectory` 加架构判定 | 无（正常 x64 目录一律判定为可用） |
| `src/3FCompare.Core/Display/DxgiOutputInfo.cs` | 修改 | `sizeof(SIZE_T)` 由常量改为按 `IntPtr.Size` 推导 | **零**（数值等价） |
| `src/3FCompare/Controls/PlayerSurface.cs` | 修改 | `PAINTSTRUCT` 断言改按架构判定 | **零**（x64 仍为 72） |
| `src/3FCompare/3FCompare.csproj` | 修改 | 新增 `WriteNativeArchManifest`（纯 MSBuild，不报错）+ 期望架构属性 | 无（只多产出一个 txt） |
| `tools/架构核查.ps1` | 🆕 新增 | 独立架构核查探针（退出码 0/1） | 无（不接入既有流程） |
| `docs/53-…（本文）` | 🆕 新增 | 前瞻准备记录 | 无 |
| `third_party/fff_project/**` | **未改动** | 曾误改 `FfmpegRuntime.cpp`，**已完整回退** | 无 |

---

## 七、验证结果（**本次已实跑**）

> 会话中途环境恢复（文件策略改为完全访问后 `pwsh` 可用），因此**下面全部是实跑结论**，
> 不再是"仅静态阅读"。

### 7.1 构建

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/3FCompare.Core/3FCompare.Core.csproj -c Release` | ✅ **0 警告 0 错误** |
| `dotnet build src/3FCompare/3FCompare.csproj -c Release` | ✅ **0 错误**（仅 1 条既有 `AVLN3001`，属发布门禁已登记的允许告警） |
| `dotnet build tests/3FCompare.SmokeTests -c Release` | ✅ 0 错误 |
| `WriteNativeArchManifest` target | ✅ 正常产出 `native-arch-manifest.txt`，四个原生件 `exists=yes` |

### 7.2 测试

| 套件 | 结果 |
| --- | --- |
| `3FCompare.Platform.Tests` | ✅ **476 通过 / 0 失败**（含 `PlayerSurface`，即本次改动的文件之一） |
| `3FCompare.Core.Tests` | ⚠ 657 通过 / **45 失败**——**全部为环境性、与本次改动无关**（见下） |

**那 45 个失败已定性为环境问题，证据是"改前改后完全一致"**：

1. 失败用例全部落在**同一处测试夹具** `tests\3FCompare.Core.Tests\Infrastructure\TempDir.cs:28`
   的 `Directory.CreateDirectory`，异常为 `UnauthorizedAccessException`（`%TEMP%` 下建目录被拒）；
2. 该夹具**本次未被改动**，且失败集合是 `AppLogTests` / `ConsoleErrorRerouterTests` /
   `NativeRuntimeFfmpegDirTests` 这类**都要碰临时目录**的类；
3. **决定性对比**：把本次改动的两个受版本控制的文件 `git stash` 后重跑 ⇒
   **同样是 45 失败 / 657 通过**（逐字相同的计数）；`git stash pop` 恢复后仍是 45/657。
   ⇒ **本次改动对测试结果零影响**。
4. 交叉验证：全部输出中 grep `NativeArchGuard|架构不匹配|架构守卫` ⇒ **零命中**，
   即**没有任何一个失败与本次新增的守卫有关**。

> 注：同样的 `Directory.CreateDirectory` 在 `pwsh` 里**手动执行是成功的**（实测），
> 说明受限发生在**测试宿主进程**侧，属本会话沙箱的残留限制，不是代码缺陷。

### 7.3 架构守卫与探针（实跑，详见 §2.6 / §2.7）

- `架构核查.ps1 -Expected x64` ⇒ 93 个原生文件全部一致，**退出码 0**；
- `架构核查.ps1 -Expected arm64` ⇒ 正确列出全部错配，**退出码 1**；
- `NativeArchGuard` 对真实 x64 DLL、合成 ARM64 PE、非 PE 字节三种输入的判定**全部符合设计**。

### 7.4 仍未验证的（需 ARM64 实机）

上面证明了"现在的 x64 一切照旧"，**但不能证明 ARM64 上行为正确**——下列各项仍待真机（同 §5 第 6 步）：
`PlayerSurface.NativePaintStructSize` 的 ARM64 档、`ARM64_NT_CONTEXT`/`.pdata` 布局、
ARM64 上 D3D11 视频处理器能力、以及 `-p:Platform=arm64` 的真实解析。
