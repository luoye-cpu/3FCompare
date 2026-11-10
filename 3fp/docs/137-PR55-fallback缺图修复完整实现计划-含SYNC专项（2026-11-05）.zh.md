# 137 · PR55 fallback 缺图修复完整实现计划（含 SYNC 专项审查结论）

日期：2026-11-05
分支：`feature/cuda-d3d11-interop`（HEAD = e137985，即上游 PR55 提交本体）
性质：**实现计划**（待用户裁决后执行）；全程零 git 写操作，实现产出以 patch 文件交付

---

## 0. 一页速览

| 项 | 内容 |
|---|---|
| 要修什么 | PR55 的 must-fix：Render 中 CUDA Import/Bind 失败的 fallback 分支（VideoRenderer.cpp:7502-7539）下载了帧数据却**没有任何代码把它上传进纹理**，该帧缺图（单帧黑闪）+ 白付一次 PCIe 回读 |
| 怎么修 | 把 directYuv 常规上传块（:7572-7705）**整块提炼**为私有方法 `UploadPlanarFrames`，fallback 分支在 `EnsurePipeline(...,false,false)` 重建后调用；三处注释在修复后语义自动成立，仅微调措辞；Api.h 注释补 uploadPath=8；顺带修 CudaD3D11Interop::Reset 的 push 失败路径残留 |
| SYNC 结论 | **无需新增任何同步原语**。S1-S6 全安全（详见 §2）：解码流与拷贝同 context、map/unmap 有 CUDA Driver API 13.5 官方双向同步契约背书、帧生命周期由引用计数+同步拷贝保证、全部调用点在 deviceMutex_ 内 |
| 验证 | 新增一次性诊断开关 `FFF_CUDA_INTEROP_PROBE_REFUSE=1` 强制 Probe 拒绝 → 精确命中 fallback 修复路径，四场景矩阵验证（§6） |
| 不做什么 | 不动 Import 同步语义、不动 PlayerSession 门禁、不做批量 map/unmap 优化（与修复解耦）、不动 EnsurePipeline 重建顺序 |

---

## 1. 缺陷定位（引用 PR55 审查结论）

**控制流**（VideoRenderer.cpp，本地行号）：

```
:7312  const auto cudaFrame = frame->format == AV_PIX_FMT_CUDA;  // const，按原始帧
:7467  if (cudaFrame) {
:7480      if (Import 成功) { cuMemcpy2D 已填充纹理; store(8) }          // 正常路径 ✅
:7502      else {  // fallback
              置闩锁(:7521) → av_hwframe_transfer_data 下载(:7528)
              → frame = cudaTransferFrame_(:7533) → uploadPlanes/Lines = frame->data(:7534-35)
              → EnsurePipeline(...,false,false) 重建 CPU 可写纹理(:7536)
              → 【分支结束，无任何上传】                                  // ⚠️ 缺陷
           }
:7540  } else if (d3d11Frame) {...}
:7572  } else if (directYuv) { /* 唯一的 CPU planar 上传代码 */ }        // 被 cudaFrame 拦截
```

`cudaFrame` 是 `:7312` 的 const（按**原始**帧格式），fallback 中 `frame = cudaTransferFrame_` 的局部重定向不改变 else-if 链选择 → `cudaTransferFrame_` 的 CPU 平面在本次 Render 调用中无人消费 → 该帧采样空/旧纹理。

影响范围：非持续黑帧。闩锁（:7521 置位 → CanImportCudaFrame :4248 读取）+ PlayerSession.cpp:6404 每帧门禁 + `CanPresentHardwareFrameDirectly` 对 CUDA 帧恒 false（DecodeDeviceSelection.cpp:78-82）→ 下一帧起 PlayerSession 降级为 CPU 帧恢复。但**首次失败当帧缺图**，且 preroll 首帧同样缺图（此前 PlayerSession.cpp:1175-1179 的注释声明 "cannot leave the preroll without a picture" 依赖渲染器 fallback 出图——修复前为假）。

---

## 2. SYNC 专项审查结论（S1-S6，全部安全）

| # | 审查项 | 结论 | 关键依据 |
|---|---|---|---|
| S1 | 解码流与 Import 拷贝同流有序性 | ✅ 安全 | `state_->stream` 取自 hwctx->stream（CudaD3D11Interop.cpp:236-238，ABI 与发行头 hwcontext_cuda.h:44-48 逐位一致）；发行 FFmpeg（N-126168-gb16b5f2a01，libavutil 61.5.100）上游 `cuda_context_init` 将 stream 置 **NULL（legacy 默认流）** → 实际走 Import 的同步 `memcpy2D` 分支（:394-396）；nvdec 同 context 且 `cuvidMapVideoFrame` 同步返回，解码写入先于拷贝提交 |
| S2 | 帧池生命周期竞争 | ✅ 安全（修复后） | 帧表面归还由引用计数控制；PresentVideoFrame 内 Render（Import 同步完成）先于 av_frame_unref（PlayerSession.cpp:5496-5497）；`cudaTransferFrame_` 复用在 :7527 unref 前由 UpdateSubresource/Map **同步拷贝语义**保证消费完毕 |
| S3 | map/copy/unmap 与 D3D11 采样的跨 API 同步 | ✅ 安全，遗留疑点关闭 | **CUDA Driver API 13.5 官方文档逐字契约**：cuGraphicsMapResources/UnmapResources 提供双向同步保证（map 前的 graphics 调用 → CUDA 工作；stream 中的 CUDA 工作 → 其后提交的 graphics 工作）。本项目叠加 streamSynchronize/同步 memcpy2D **比契约更保守**。Probe/Bind/Import/Reset 全部 return 点逐行核对，push/pop 配对完整 |
| S4 | fallback 修复插入点 | ✅ 方案成立 | EnsurePipeline(false,false) 后 wantsRing/dynamicPlanes 谓词（:4151-4155）与常规 directYuv 帧完全一致，上传代码无需分支特判；`sourceCudaInterop_(true)≠cudaInteropSource(false)` 保证 :4016-4022 不早退、:4025 Reset 先行 → **doubleRing swap 的 Bound()==false 不变量成立**；遥测需整块提炼（含 :7703-7705 尾部记账）避免重复记账 |
| S5 | 线程与门禁 | ✅ 安全 | CanImportCudaFrame 读写点全部在 session 主循环线程（preroll 与 present 同线程），atomic relaxed 为防御性设计；state_ 缓存全部触碰点在 deviceMutex_ 内；锁序统一 deviceMutex_→presentMutex_ |
| S6 | 遗留疑点复核 | ✅ 关闭 | "unmap 后无 sync 即 D3D11 采样"凭官方文档契约关闭；preroll 门禁注释（:1172-1189）在修复后**自动成立**（Import/上传在 prepareOnly 早退点之前执行，数据先落地） |

**S3 逐行核对发现的唯一代码缺口**（非竞争，错误路径残留）：`CudaD3D11Interop::Reset`（CudaD3D11Interop.cpp:407-437）中 `ctxPushCurrent(state_->context)` 失败时跳过 unregister 且**不清本地状态**（resources/geometry 仍非零）→ `Bound()` 恒 true → EnsurePipeline 的缓存判断（:4016-4022）误认为绑定有效。归入 T5 顺带修复。

---

## 3. 实现任务清单

### T1（核心）：提炼 `UploadPlanarFrames` 私有方法

**位置**：VideoRenderer.h（声明区，`EnsurePipeline` 声明附近，约 :602 区域）+ VideoRenderer.cpp（实现放 `Render` 之前的合理位置，如 `ReleaseSuperResolutionResources` 系列附近）。

**签名**（确定性，照抄）：

```cpp
// VideoRenderer.h（private 区）
/// The ordinary planar upload: staging ring (1) / Map WRITE_DISCARD (2) /
/// packed UpdateSubresource (3) / env-gated UpdateSubresource arm (6), exactly
/// the code the directYuv arm of Render has always run. Returns the same
/// FFFResult the inline code would have.
FFFResult UploadPlanarFrames(const std::uint8_t* const* uploadPlanes,
    const int* uploadLines, std::uint32_t uploadWidth, std::uint32_t uploadHeight,
    const InputDescription& input) noexcept;
```

**函数体**：VideoRenderer.cpp:7586-7705 **整块原样搬移**（`uploadStart` 计时起、`:7703-7705` 尾部记账止），唯二改动：
1. 局部 `uploadPlanes/uploadLines/uploadWidth/uploadHeight/input` → 函数参数
2. 分支条件 `directYuv` 不再需要——函数只服务 planar 语义；保留内部 `input.layout == 1 || input.layout == 2` 判断与 else packed 臂（layout 3/0 不可能出现在 CUDA fallback，但保留以维持代码与常规臂逐字一致，符合"最小外科手术"原则）

**调用前提不变量**（写进函数头注释，SYNC 审查给出）：

1. **形态**：EnsurePipeline 已按 `(uploadWidth, uploadHeight, input.layout, input.bitDepth, shifts, externalSource=false, cudaInteropSource=false)` 成功建好纹理；`cudaInterop_.Bound() == false`（doubleRing 的 `std::swap(sourceTextures_, uploadTextures_)` :7621-7624 安全的前提——CUDA 绑定残留时 swap 会换走被注册纹理）
2. **锁域**：deviceMutex_ 持有（仅 DrawCachedVideo 调用链可达）
3. **指针**：uploadPlanes/uploadLines 指向已解码 CPU 平面，各平面行宽 ≥ 纹理行字节
4. **消费**：Map/UpdateSubresource 为同步拷贝，返回后 uploadPlanes 可释放/复用
5. **记账**：函数内完整记账（uploadCount_ +1、upload100ns_ 一次、uploadPath_ 按臂 1/2/3/6）——调用方不得重复记账

**注意**：`InputDescription` 是 VideoRenderer.cpp:1488 的文件局部结构（含 layout/bitDepth/sampleScale/chromaWidthShift/chromaHeightShift）——声明区放头文件需把它一并上移或改为按值传参的 5 个标量。**决策：将 `struct InputDescription` 上移至 VideoRenderer.h 的类外（namespace 内）**，因为 `DescribeInput` 与 static_assert 断言矩阵（:1823-1834）留在 .cpp 不动，仅结构定义上移，.cpp 内 `using` 不需要（同名直接可见）。风险为零：结构无成员函数、无依赖。

### T2：fallback 分支调用点

VideoRenderer.cpp:7538（`if (rebuild != FFFResult::Success) return rebuild;` 之后、`:7539` 分支闭合 `}` 之前）插入：

```cpp
const auto uploaded = UploadPlanarFrames(uploadPlanes, uploadLines,
    uploadWidth, uploadHeight, input);
if (uploaded != FFFResult::Success) return uploaded;
```

- `uploadPlanes/uploadLines` 已在 :7534-7535 指向 `cudaTransferFrame_` 平面 ✓
- CUDA 帧经 `SupportsPixelFormat` 门禁，`input.layout` 恒为 1（planar）→ 走 layout==1 子块（staging/dynamic/实验臂），遥测记 **1/2/6**（不再是 8——正确反映真实搬运路径）
- 记账不重复：Import 成功分支（:7480-7501）与本调用互斥（else 分支），uploadCount_/upload100ns_ 每帧恰好一次 ✓

### T3：三处注释微调（修复后语义成立，措辞对齐新现实）

1. **VideoRenderer.cpp:7503-7505**（fallback 注释）：改 "so the picture is carried by the ordinary planar route rather than dropped" → "so the picture is carried by the ordinary planar upload (`UploadPlanarFrames`) rather than dropped"，并补一句 "the refusal is latched for the stream" 原句保留。
2. **VideoRenderer.cpp:4179-4181**（EnsurePipeline Bind 失败路径）："let the ordinary planar upload carry the frame" → "let the ordinary planar upload (`UploadPlanarFrames`, called from Render's CUDA fallback) carry the frame"。该路径修复后的完整链路：EnsurePipeline 内 Bind 失败 → latch + 重建 → Render :7473 条件假（sourceCudaInterop_=false）→ imported=Refused → fallback 分支 → 下载 + UploadPlanarFrames ✓ 成立。
3. **PlayerSession.cpp:1175-1179**（preroll 门禁）：**不改**——修复后 "a refusal there falls back to the download route, so letting the frame through cannot leave the preroll without a picture" 从假变真（SYNC S6 确认数据先于 prepareOnly 早退点落地）。保留原文。

### T4：Api.h uploadPath 注释补 8（纯注释，offset pin :456/:467 不受影响）

FFF.Native/3FP/Api/FFF.Player.Api.h:404 之后（`:399-404` 的 path 7 条目与 `:405` 总结行之间）插入：

```
    //   8 CUDA->D3D11 interop direct paste -- the decoded CUDA surface is copied
    //     into the shader textures by the driver (cuMemcpy2D into the registered
    //     planes), no PCIe traffic in either direction. upload100ns ==
    //     uploadCopy100ns and there is no separate map-wait interval. Bytes are
    //     the planar YUV size (1 byte per sample at 8 bit, 2 above). When the
    //     interop refuses a frame, the fallback download records paths 1/2/6
    //     like any CPU planar frame, so a run that mixed both shows both.
```

### T5：CudaD3D11Interop::Reset 的 push 失败路径残留（SYNC 唯一代码缺口）

CudaD3D11Interop.cpp `Reset()`（:407-437）：push 失败分支补本地状态清理（不尝试 unregister——unregister 必须 context 绑定，push 失败本身意味着 context 不可用，流将被重建）：

```cpp
    if (driver.ctxPushCurrent != nullptr && state_->context != nullptr &&
        driver.ctxPushCurrent(state_->context) == CudaSuccess) {
        for (...) driver.graphicsUnregisterResource(state_->resources[plane]);
        CUcontext popped = nullptr;
        driver.ctxPopCurrent(&popped);
    } else if (state_->planeCount != 0 && InteropTraceEnabled()) {
        std::fprintf(stderr, "[cuda] reset could not re-enter the context; "
            "%u registration(s) left with the driver\n", state_->planeCount);
    }
```

（`else if` 分支新增；原有资源/几何清零逻辑在函数尾部对两个分支都生效，不动。）

### T6（验证注入点，随本补丁交付）：`FFF_CUDA_INTEROP_PROBE_REFUSE=1` 诊断开关

CudaD3D11Interop.cpp `Probe()` 入口（Enabled() 检查之后）：

```cpp
    // One-shot diagnostic injection: FFF_CUDA_INTEROP_PROBE_REFUSE=1 makes the
    // device-side probe refuse every stream, which drives Render's CUDA fallback
    // (download + UploadPlanarFrames + latch) without needing a misbehaving
    // driver. Same family as FFF_CUDA_INTEROP_TRACE; off by default.
    static const bool probeRefuse = [] {
        wchar_t value[8]{};
        return GetEnvironmentVariableW(L"FFF_CUDA_INTEROP_PROBE_REFUSE", value, 8) != 0 &&
            value[0] == L'1' && value[1] == L'\0';
    }();
    if (probeRefuse) return Status::Refused;
```

**为什么能精确命中 fallback**：CanImportCudaFrame（:4230-4262）不含 Probe，PlayerSession:6404 门禁照常放行 CUDA 帧 → 渲染器 :7450 Probe 恒 Refused → cudaInteropSource=false → :7473 条件假 → imported=Refused → **fallback 分支**（下载+上传+latch）→ 下一帧起 PlayerSession 读闩锁降级。这正是修复要覆盖的完整时序。

---

## 4. 明确不做（不变量保护）

| 不做 | 理由 |
|---|---|
| 不新增任何同步原语 | S1-S6 全安全；map/unmap 有 CUDA 官方双向同步契约，现状已比契约保守 |
| 不改 Import 的同步语义 | streamSynchronize/同步 memcpy2D 保持原样 |
| 不动 PlayerSession 门禁逻辑（:1172-1189、:6392-6404） | 修复后 preroll 注释自动成立；门禁/闩锁时序已由 S5 确认 |
| 不动 EnsurePipeline 重建顺序（:4025 Reset 先行） | 防悬空注册的既有防护，保持逐字 |
| 不做批量 map/unmap、cuGraphicsRegisterFlagsReadonly | 纯优化与修复解耦，避免污染 must-fix 补丁的可审性 |
| 不动 `Render` 的 else-if 链结构（不改 cudaFrame 为非 const） | else-if 语义是其它路径（遥测/绘制分支）的正确性依赖，提炼函数是唯一安全改动面 |

---

## 5. 逐文件改动汇总

| 文件 | 改动 | 性质 |
|---|---|---|
| FFF.Native/3FP/Render/VideoRenderer.h | `InputDescription` 结构上移；`UploadPlanarFrames` 声明 + 5 条不变量注释 | 接口 |
| FFF.Native/3FP/Render/VideoRenderer.cpp | :7586-7705 整块搬移为 `UploadPlanarFrames`；:7538 后插 fallback 调用；:7503-7505 / :4179-4181 注释微调 | 核心 |
| FFF.Native/3FP/Render/CudaD3D11Interop.cpp | Probe 入口加 PROBE_REFUSE 开关（T6）；Reset push 失败分支补清理与 trace（T5） | 辅助 |
| FFF.Native/3FP/Api/FFF.Player.Api.h | :404 后补 uploadPath=8 注释块（T4） | 文档 |

预计净增 ~40 行（函数壳 + 注释 + 开关），搬移 ~120 行，删除原位 ~120 行。**零行为变化面**：常规 directYuv 帧、D3D11VA 帧、floatRgb、layout0 RGB、Import 成功路径全部逐字不动。

---

## 6. 验证方案

构建（记忆约束）：`pwsh -File tools\构建3FP.ps1 -Configuration Release -SkipDependencyPreparation`

测试矩阵（样例：D:\TEST 任一 CUDA 可解流；RTX 4060 Laptop driver 616.92 为既有实测环境）：

| # | 场景 | 环境变量 | 预期 |
|---|---|---|---|
| V1 | 默认回归：CUDA 流直拷 | 无 | 画面正常，遥测 uploadPath=8，uploads 每帧 +1 |
| V2 | **fallback 修复路径** | `FFF_CUDA_INTEROP_PROBE_REFUSE=1` + `FFF_CUDA_INTEROP_TRACE=1` | **第一帧起画面正常（修复前缺图）**；日志序列 `[cuda] probe refused` → `[cuda] import refused (status=1 ...)` → 常规上传；遥测 uploadPath=1/2/6；第二帧起 PlayerSession 降级（闩锁生效），路径转常规 CPU 帧 |
| V3 | 旧路径回归 | `FFF3FP_CUDA_D3D11_INTEROP=0` | 画面正常（门禁关闭，PlayerSession 全量下载） |
| V4 | 静态图 | PROBE_REFUSE=1 | 静态图仍走下载（PlayerSession:6405-6407 强制），不受开关影响 |
| V5 | 记账核对 | V2 场景 | uploads 计数每帧恰好 +1（无重复记账）、bytes_per_upload 数值合理 |
| V6 | preroll | V2 场景 + 冷启动 | 首帧出图（preroll 注释声明成立） |

像素级验收（V2 vs V3 基线）：同帧画面对比一致（fallback 上传与 INTEROP=0 路径的最终纹理应逐位同源——同一解码表面下载后同一条上传代码）。

---

## 7. 提交与交付策略

1. 全部改动先以 **patch 文件**交付到 `3fp/patches/`（记忆约束：跨会话改动交换用 patch 文件，不 amend；git 写操作前必核 branch+reflog；push 由用户本地执行）
2. 单一 commit，信息风格随仓库惯例（中文）：
   `内核：CUDA 直拷 fallback 帧补 planar 上传（修复单帧缺图）`
3. 上游 PR55 评论区意见稿（供用户裁决后发布）：must-fix 描述 + 行为分析（单帧缺图时序）+ 本补丁的 diff；语气按"作者自查发现"惯例

---

## 8. 待用户裁决的决策点

1. **T6 诊断开关随补丁交付**（推荐：作者也需要它测 fallback）还是仅本地临时 patch 验证后撤掉？
2. **T1 的 InputDescription 上移头文件** vs 改为 5 标量传参（前者更整洁，后者改动更小）？
3. 提交策略：patch 交付 → 用户本地验证后自行 commit/push（默认，遵守操作边界），还是授权在 feature/cuda-d3d11-interop 上直接 commit？