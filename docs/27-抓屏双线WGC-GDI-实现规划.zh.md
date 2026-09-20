# 27 · 抓屏双线（WGC 主 + GDI 备）实现规划（2026-09-19）

> 前置：`docs/26 §3.2`（GDI 抓屏实测缺陷）与 `docs/26 §八`（WGC 可行性实测）。
> 目标：把 `ScreenFrameCapture` 从"单一 GDI 路径"升级为** WGC 主 + GDI 备的双线结构**，
> 并修正 GDI 路径中已被实测证伪的注释。

---

## 零、现状与问题

### 现有实现

`src/3FCompare/Platform/ScreenFrameCapture.cs`（命名空间 `_3FCompare.App.Capture`）：
- **Path A**：`BitBlt` 抓屏幕区
- **Path B**：`PrintWindow` 抓顶层窗口 → 按子窗口相对坐标裁剪
- 它是 `MainWindow.Capture.cs` 的**回退路径**（首选是内核区域回读 `ReadVideoPixelRegion`）

### 三个已证实的问题

| # | 问题 | 证据 |
|---|---|---|
| 1 | **GDI 抓屏抓不到 flip-model 内容** | `docs/26 §3.2` 实测：7 采样点只有中心 1 点偶尔命中 |
| 2 | **被遮挡时抓到遮挡物** | 该文件注释已自认 |
| 3 | **注释中的技术断言是错的** | 注释写"BitBlt 屏幕区路径抓屏幕合成结果，**含 D3D flip-model**" —— 实测证伪 |

⇒ 问题 3 尤其危险：错误的注释会让后来者信任这条路径。

### WGC 实测结论（`docs/26 §八`）

- ✅ 能正确捕获 flip-model 内容（无 OSD 干扰区域 100% 精确等于清屏色）
- ✅ **被不透明窗口完全遮挡时结果与无遮挡一致**（GDI 做不到的）
- 探针：`.review_pr/wgc_probe.cpp`，附捕获帧截图

---

## 一、关键约束（决定架构）

| 约束 | 事实 | 影响 |
|---|---|---|
| **AOT 发布** | `3FCompare.csproj` 有 `PublishAot=true` | WGC 是 **WinRT** API；AOT 下需 CsWinRT 投影并**显式保留**类型，否则被裁剪 |
| **无 D3D11 绑定** | 项目无 Vortice / SharpDX / TerraFX | WGC 的 `Direct3D11CaptureFramePool.Create` **需要 `ID3D11Device`** |
| 单文件发布 | 现内嵌 `FFF.Native.dll` | 新增原生 DLL 需改打包（`pack.ps1` / csproj 内嵌） |

### 🔑 决策 D1：WGC 走"原生辅助 DLL"，不走托管 WinRT 投影

**理由**：
1. AOT 下 WinRT 投影（`GraphicsCaptureItem` 与 interop 类型）有被裁剪的真实风险，
   需要额外的 `TrimmerRootDescriptor` 维护成本，且失败模式是**运行时**才暴露。
2. 项目本来就没有 D3D11 绑定，托管路线要新引入一个较重的 DirectX 依赖；
   原生路线可直接在 DLL 内部 `D3D11CreateDevice`，对外只暴露**纯 C ABI**。
3. **纯 C ABI 对 AOT 完全友好**（P/Invoke 是 AOT 原生支持的，且项目已启用 `BuiltInComInteropSupport`）。
4. 已有**经过实测验证**的 C++/WinRT 代码（`.review_pr/wgc_probe.cpp`）可直接复用。

**代价**：多一个原生 DLL，需改打包；需为该 DLL 增加构建脚本。

**备选（不采用，记录在案）**：纯托管 WGC（CsWinRT + Vortice.Direct3D11 + AOT 保留）。
优点是无需额外 DLL；缺点是 AOT 风险与依赖成本更高。**若后续验证 AOT 下托管 WGC 稳定，可回退到本方案。**

---

## 二、C ABI 契约（两条线并行开发的基础）

新增原生 DLL `3FC.WgcCapture.dll`（x64），暴露：

```c
// 系统是否支持 WGC（GraphicsCaptureSession::IsSupported）
int  Wgc_IsSupported(void);

// 创建 / 销毁捕获器。失败返回 0。
void* Wgc_Create(void);
void  Wgc_Destroy(void* handle);

// 抓取一帧。成功返回 0。
// outBits: RGBA32、自下而上(DIB 行序)或自上而下——**实现时必须明确并在头文件写死**，
//          建议统一为自上而下 RGBA，避免与 GDI 的 DIB 行序混淆。
// 调用方用 Wgc_FreeFrame 释放。
int  Wgc_CaptureFrame(void* handle, void* hwnd,
                      unsigned char** outBits,
                      int* outWidth, int* outHeight, int* outStride);

void Wgc_FreeFrame(void* handle, unsigned char* bits);

// 最近一次失败的诊断信息（UTF-8），供日志。失败时返回空串。
void Wgc_LastError(void* handle, char* buffer, int bufferSize);
```

**必须实现的行为**：
- **丢弃前 1~2 帧**：实测换色后首次 `StartCapture()` 的第 1 帧仍是旧内容（见 `docs/26 §八`）。
- `IsBorderRequired(false)` 关系统黄框（旧系统可能抛异常 ⇒ try/catch）。
- `IsCursorCaptureEnabled(false)`。
- 内部 `winrt::init_apartment(MTA)`；MTA + 轮询 `TryGetNextFrame` 即可，不需 STA/UI 线程/消息泵。
- 窗口最小化时 WGC 返回黑帧 ⇒ 返回明确错误码，交给上层回退。
- 超时保护：`StartCapture` 后若 N 帧内拿不到帧，返回超时错误。

---

## 三、托管侧双线架构

```
IFrameCapture (抽象)
 ├── WgcFrameCapture   —— P/Invoke 3FC.WgcCapture.dll
 └── GdiFrameCapture   —— 既有 BitBlt / PrintWindow 逻辑（原样迁移）
FrameCapture (静态门面)
 ├── 选路：WGC 可用 → WgcFrameCapture；否则 → GdiFrameCapture
 ├── 单次失败自动回退（WGC 失败 → 本次改用 GDI）
 └── 输出统一：System.Drawing.Bitmap（与现有调用方兼容）
```

### 选路与回退策略

| 条件 | 行为 |
|---|---|
| `Wgc_IsSupported() == false` | 直接走 GDI |
| DLL 缺失 / 加载失败 | 走 GDI（记 WARN，仅一次） |
| `Wgc_Create` 失败 | 走 GDI |
| `Wgc_CaptureFrame` 失败（最小化 / 超时 / 窗口不可捕获） | **本次**回退 GDI，下次仍先试 WGC |
| WGC 连续失败 N 次（建议 3） | 本会话降级为 GDI，避免每次都付超时代价 |
| 窗口最小化 | 直接 GDI（WGC 必然黑帧） |

### GDI 线必须同步修正

1. **删掉"含 D3D flip-model"这个错误断言**，改为：
   "实测 GDI 路径对 flip-model swapchain **不可靠**（`docs/26 §3.2`），仅作为 WGC 不可用时的兜底；
   结果可能不含视频内容，且窗口被遮挡时会抓到遮挡物。"
2. 返回值增加**路径标识**，让调用方知道这一帧来自哪条线（便于上层决定可信度）。

---

## 四、阶段划分（可并行）

### 阶段 0 · 骨架与契约（主 Agent）
- 定义 `IFrameCapture` / `FrameCapture` 门面（先不接 WGC 实现，只有 GDI 一条线）
- 迁移既有 GDI 逻辑为 `GdiFrameCapture`，**修正错误注释**
- 结果：**先落地一个可编译、行为与今天完全一致的"单线"版本**（低风险打底）

### 阶段 1 · 原生 WGC DLL（子 Agent A，可与阶段 0 并行）
- 基于 `.review_pr/wgc_probe.cpp` 的已验证代码
- 实现 §二 的 C ABI
- 产出：`3FC.WgcCapture.dll` + 构建脚本 + 一个最小 C/C++ 自测程序

### 阶段 2 · 托管 WGC 线接入（子 Agent B，依赖阶段 1 的 ABI 契约，可与阶段 1 后期并行）
- `WgcFrameCapture` 实现（P/Invoke，纯 C ABI，AOT 友好）
- 接入 `FrameCapture` 门面的选路与回退
- 单元测试（可测部分：选路逻辑、回退次数、路径标识）

### 阶段 3 · 打包与验证
- csproj 内嵌 `3FC.WgcCapture.dll`（参照现有内嵌 `FFF.Native.dll` 的写法）
- `dotnet publish` 验证单文件发布仍可用
- 真机双线验证：正常窗口 → WGC；最小化 / 不支持 → GDI

---

## 五、验证策略

| 层 | 方法 |
|---|---|
| 原生 DLL | C/C++ 自测程序：抓 flip-model 窗口，比对清屏色（复用 `wgc_probe` 判据） |
| 托管 P/Invoke | 单元测：加载、创建、抓帧、释放不泄漏；失败路径返回预期错误码 |
| 选路逻辑 | 单元测：`IsSupported=false` / 超时 / 连续失败降级 |
| 端到端 | 真机：正常窗口走 WGC；最小化走 GDI；抓图内容与画面一致 |

⚠ **真机验证前必须关闭 RTSS / MSI Afterburner** —— 它们的 OSD 会被注入 swapchain 并污染捕获帧
（`docs/26 §八` 已记录，也是此前 `clip_probe` 采样异常的真正原因）。

---

## 六、风险与回退

| 风险 | 影响 | 回退 |
|---|---|---|
| AOT 下 P/Invoke 原生 DLL 出问题 | 抓屏全挂 | 阶段 0 的单线版本行为与今天一致，可单独保留 |
| 新增 DLL 破坏单文件发布 | 发布失败 | 阶段 3 专测；必要时改为外置 DLL 随包分发 |
| WGC 在某些环境被策略禁用（企业/远程桌面） | 回退 GDI | 选路已覆盖 |
| OSD 污染 | 抓图带叠加层 | 文档标注 + 建议关闭覆盖软件（非代码可解） |

---

## 七、提交纪律

- 每阶段独立提交，主题化；不混入 v0.2.5 产品改动（`src/3FCompare/` 多处仍有未提交改动）。
- 新增 Core 逻辑必须带单测（两个测试工程只引用 Core）。
- 原生 DLL 的**构建产物不入库**，只入库源码与构建脚本（参照内核的处理方式）。

---

## 九、实施结果（2026-09-19）

### 阶段 1 · 原生 WGC DLL ✅

`native/wgc_capture/`：`wgc_capture.h`（C ABI）/ `wgc_capture.cpp` / `selftest.cpp` / `build.sh` / `.gitignore`。

**自测 32 项通过 / 0 失败**（`selftest.exe`），覆盖：

| 项 | 结果 |
|---|---|
| 6 个导出符号 | 全部解析，**未修饰名**（便于 P/Invoke） |
| `Wgc_IsSupported()` | 1；D3D11 feature level `0xB000`(11.0) |
| flip-model 窗口抓帧 | 400×300 stride=1600，色带位置精确 |
| **被不透明窗口完全遮挡** | **与无遮挡结果完全一致** ✅ |
| 换清屏色后跟随 | ✅（红 → `RGB(0,64,255)`） |
| 会话复用 / hwnd 变更重建 | ✅ |
| 负例 | NULL hwnd→rc=6、无效 hwnd→rc=4、NULL handle→rc=6，空指针防护未崩溃 |

错误码：`0=OK`、`4=hwnd 不是有效窗口`、`6=参数为 NULL`（完整表见 `wgc_capture.h`）。

### 阶段 0+2 · 托管侧双线 ✅

| 文件 | 行数 | 说明 |
|---|---|---|
| `Core/Capture/CaptureRouter.cs` | 141 | **纯选路逻辑**（顺序/计数/降级），可单测 |
| `Platform/IFrameCapture.cs` | 34 | 抽象 + `CapturedFrame(Bitmap, Route)` 路径标识 |
| `Platform/GdiFrameCapture.cs` | 215 | 既有 GDI 逻辑原样迁移 |
| `Platform/WgcFrameCapture.cs` | 290 | `LibraryImport` 绑定 6 个导出（AOT 友好） |
| `Platform/FrameCapture.cs` | 158 | 静态门面：探测 / 最小化判断 / 喂参 / 降级日志 |
| `tests/.../CaptureRouterTests.cs` | 319 | 25 用例 |

改动 3 处：`ScreenFrameCapture.cs` **195→31 行**（保留公开方法、内部委托门面 ⇒ 三个调用点零改动）；
`MainWindow.Capture.cs` +12/−4（来源标注三态：内核回读 / WGC 抓屏 / GDI 抓屏回退）；
错误注释已按 §三 修正。

**验证**：Core **418 通过 / 0 失败**（393→418）；Platform **31 通过**；UI 编译 **0 错误 0 警告**。
另用一次性 file-based 探针实测 ABI：6 导出名全解析、`IsSupported=1`、`Create` 成功、
NULL hwnd→rc=6、`LastError` 返回「hwnd 为 NULL」（探针已删）。

### ⚠ 认知修正（重要）

**`tests/3FCompare.Platform.Tests` 实际引用了 UI 工程**，
与长期记录的"两个测试工程只引用 Core"不符 ⇒ 该工程可用来补 App 层（`src/3FCompare`）测试，
这是之前没用起来的能力。

### 阶段 3 · 待办

- csproj 内嵌 `3FC.WgcCapture.dll`（参照现有内嵌 `FFF.Native.dll` 的写法）
  ⇒ **当前运行时 DLL 未随包分发，实际走 GDI 兜底**（缺库 WARN 路径已就位）
- `dotnet publish` 验证单文件发布仍可用
- 真机双线验证（**先关 RTSS/Afterburner**，否则 OSD 会污染捕获帧）
- 目视确认 Bitmap 行序不倒置（当前依据 `wgc_capture.h` 的"自上而下"契约，未在托管侧实测）

### 已知未覆盖

真实抓帧成功路径（需持续 Present 的 flip-model 窗口）未在托管侧端到端验证。

### 阶段 3 · 内嵌与发布 ✅（2026-09-19 完成）

| 文件 | 改动 |
|---|---|
| `src/3FCompare/3FCompare.csproj` | +21 行：`<EmbeddedResource LogicalName="3FC.WgcCapture.dll">` + `CheckWgcCaptureDll` 校验 Target（未动既有两处内嵌） |
| `src/3FCompare/Program.cs` | +18 行：接在内核解压块之后，复用既有 `ExtractEmbedded` |

**设计要点**

- 路径属性 `WgcCaptureDllPath`（默认 `..\..\native\wgc_capture\3FC.WgcCapture.dll`），
  可用 `-p:WgcCaptureDllPath=<绝对路径>` 覆盖。
  **未设 Debug/Release 开关**——`build.sh` 产物直接落在目录根，不存在配置子目录，
  加了反而是个会静默失效的开关。
- 校验 Target **缺失即硬失败**（与 `CheckKernelDll` 一致）并给出
  "请在 Git Bash 运行 `native/wgc_capture/build.sh`" 的可执行指引。
  理由：该 DLL 是 WGC 主线路的唯一实现，缺了只会**永久退回 GDI**（对 flip-model 不可靠、
  被遮挡抓到遮挡物），而构建全绿、仅一行 WARN——正是要避免的静默降级。
  负例已实测：`-p:WgcCaptureDllPath=C:/nonexistent/...` 构建立即报出指引。
- **遵循"已存在则不覆盖"铁律**：`ExtractEmbedded` 首行 `if (File.Exists(target)) return;` 原样保留，
  仅把策略写进文档注释并说明理由（原生库由 `build.sh` 独立产出，磁盘版本可能比内嵌批次更新；
  覆盖会重演 2026-09-16 的 API15→14 事故）。解压目标 `AppContext.BaseDirectory`，与 FFF.Native/Skia 同目录。

**验证**

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 错误**（1 个既有 AVLN3001 警告） |
| Core 测试 | **418 通过 / 0 失败** |
| Platform 测试 | **31 通过** |
| 嵌入证据 | `GetManifestResourceNames()` 得 `3FC.WgcCapture.dll (61952 bytes)`，与磁盘字节数一致 |
| **`dotnet publish -c Release -r win-x64`** | **成功（28s）**，单文件 **34MB** `3FCompare.exe`；同目录无残留 DLL；**exe 内检索到 `Wgc_IsSupported` 符号（4 处）** |
| AOT（ILC）告警 | 仅 4 条既有告警，**无一涉及 WGC 的 6 个 `LibraryImport` 绑定** ⇒ 源生成封送在 AOT 下无裁剪问题 |

⇒ **AOT 决策 D1（原生 DLL + 纯 C ABI）得到验证**：AOT 下没有出现 WinRT 投影被裁剪的问题，
这正是当初不走托管 WGC 路线的原因。

### 全阶段完成状态

| 阶段 | 状态 |
|---|---|
| 0 骨架与契约（GDI 单线打底） | ✅ |
| 1 原生 WGC DLL | ✅ 自测 32/32 |
| 2 托管 WGC 线接入 | ✅ Core 418 / Platform 31 |
| 3 内嵌与发布 | ✅ publish 成功、AOT 无告警 |

**剩余真机验证**：关闭 RTSS/Afterburner 后跑一次真实抓屏，确认
① 抓到的确实是画面（非 GDI 兜底）② Bitmap 行序不倒置。

---

## 十、真机验证与缺陷修复（2026-09-19）

验证程序：`.review_pr/verify_wgc_managed/`（临时，`.gitignore:111` 已忽略，`src/` 零改动）。
判据窗口：Win32 已知内容窗口，**上半纯红 / 下半纯蓝** —— 行序若倒置则结果整体互换。

### 10.1 三条目标

| 目标 | 结果 | 依据 |
|---|---|---|
| ① P/Invoke 链路通 | ✅ | `Wgc_IsSupported()=1`、`Wgc_Create` 成功、`Wgc_CaptureFrame` 产出 644×447 帧 |
| ② 行序不倒置 | ✅ | 硬判据（u=0.90 整列，与偏移映射无关）：红带 `y∈[45,244]` 整体位于蓝带 `y∈[245,444]` 之上；8/8 表决点符合 |
| ③ 选路真走 WGC | ✅ | `Route = Wgc`；且 WGC 帧 644×447 与 GDI 帧 662×456 **几何不同**，证明不是 GDI 结果 |

OSD 规避：`MSIAfterburner.exe` / `RTSS.exe` / `RTSSHooksLoader64.exe` **仍在运行**（未结束用户进程），
表决采样点全部取 `u≥0.5`（右半）与中下部避开左上角。
⚠ 但本轮验证窗口是 **GDI 绘制的普通窗口**，RTSS 未向其注入 ⇒ **真实视频窗口的 OSD 污染场景本轮未被检验**。

### 10.2 🔴 真机暴露的缺陷：静态内容必然超时回退 GDI

**现象**：窗口内容静态（不重绘）时，WGC 抓帧 **TIMEOUT** 并回退 GDI：
```
Route = Gdi（GDI 兜底线）
WGC 抓帧失败（TIMEOUT(约 2s 内未取到有效帧)：已轮询 1 帧、丢弃 1 帧…），本次回退 GDI[连续 1/3]
```
给窗口加 30ms 重绘节拍（模拟视频持续 Present）后即恢复正常走 WGC。

**根因**：`wgc_capture.cpp:329` 每次建会话都置 `discardLeft=2`，`GrabFrame` **无条件丢弃前 2 帧**；
静态窗口只产出 1 帧 ⇒ 永远等不到第 3 帧 ⇒ 必然超时。

**为什么致命**：本项目差异对比的标准流程是「**暂停 → 逐帧对齐 → 截图**」，
即**最需要高质量抓屏的场景（暂停态）恰好会退回 GDI 线**，而 GDI 对 flip-model 不可靠 —— WGC 线的价值被抵消。

### 10.3 修复

1. 丢弃只在会话建立后 **300ms 热身期**（`kWarmupMs`）内生效；同一会话后续抓帧不再丢弃。
2. 拆出 `EmitFrame`/`EmitCached`：被丢的帧保留为 `pending`，超时先返回它；
   再退到 handle 级 `cache`（成功帧像素缓存，`CloseSession` 失效）。两者皆无才 TIMEOUT。
3. 已有缓存时预算收窄到 **400ms**（`kCachedProbeMs`）——逐帧对齐需连抓，否则暂停态每次干等 2s。
4. 最小化仍走 `IsIconic → NOT_CAPTURABLE`，黑帧不当成功。

### 10.4 验收（含反向验证）

自测 **32 → 49 项通过 / 0 失败**。新增用例 `[8]`：静态内容窗口（只渲染一次，之后不 Present），
用独立 handle 新建会话**连抓两次**，断言 `rc==0` 且颜色/行序/裁剪正确。
实测：第 1 次 **rc=0 耗时 406ms**，第 2 次 **rc=0 耗时 406ms**。

**反向验证**（临时构建"旧行为"版本，已删除）：复现 `rc=3`「已轮询 1 帧、丢弃 1 帧」，
2 项 FAIL ⇒ 证明该用例确实守得住，不是"改完恰好变绿"。

回归保住：遮挡与无遮挡结果一致、会话复用、hwnd 变更重建、负例（NULL hwnd→6 / 非法 hwnd→4 / NULL handle→6）。

修复后**托管侧重跑**：`Route = Wgc`，8/8 表决点正确，行序正确。

### 10.5 已知未覆盖

- flip-model 捕获仍**只由原生层 49 项自测背书**，托管侧未用真实视频窗口复验
- 真实视频窗口的 OSD 污染场景未检验
- 被遮挡一致性 / 最小化回退 / 连续失败降级 / 内嵌资源解压路径，均未在托管侧端到端覆盖
