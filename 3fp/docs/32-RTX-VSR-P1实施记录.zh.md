# 32 · RTX VSR P1 实施记录：扩展与能力探测（2026-10-05）

> 对应 `docs/30-RTX-VSR完整可行性计划.zh.md` 的 **P1 阶段**。
> 目标：把 NVIDIA 扩展与状态查询做出来，**完全不碰渲染路径**。
> 结论：**P1 完成，全部验收标准通过**（编译 EXIT=0、DLL 尺寸与基线一致、ABI 未破坏）。

---

## 1. 交付物

| 文件 | 动作 | 内容 |
|---|---|---|
| `3FP/Render/NvidiaVideoExtensions.h` | **新增** | GUID、结构体、`AdapterVendorId()` / `IsNvidiaAdapter()` / `SetSuperResolution()` |
| `3FP/Render/NvidiaVideoExtensions.cpp` | **新增** | 上述实现（约 40 行） |
| `3FP/Render/VideoRenderer.h` | 改 | VSR 状态成员 + 4 个方法声明 |
| `3FP/Render/VideoRenderer.cpp` | 改 | 适配器探测（`EnsureDevice` 内）+ 状态访问器 |
| `3FP/Core/PlayerSession.h` | 改 | `SetVideoSuperResolution` / `SetComparisonMode` / `GetVideoSuperResolutionStatus` 声明 |
| `3FP/Core/PlayerSession.cpp` | 改 | 三者实现（含对比模式强制关闭） |
| `3FP/Api/FFF.Player.Api.h` | 改 | 2 个枚举 + 1 个状态结构 + 2 个导出声明（**尾部追加**） |
| `3FP/Api/PlayerApi.cpp` | 改 | 2 个导出的转发实现 |
| `tools/build_kernel_manual.py` | 改 | **登记新 .cpp**（`EXTRA_SOURCES`） |

---

## 2. 核心实现

### 2.1 扩展调用（`NvidiaVideoExtensions.cpp`）

```cpp
HRESULT SetSuperResolution(ID3D11VideoContext* videoContext,
    ID3D11VideoProcessor* videoProcessor, const bool enable) noexcept {
    if (videoContext == nullptr || videoProcessor == nullptr) return E_POINTER;
    SuperResolutionStreamExtension extension{
        StreamExtensionVersionV1,               // 0x1
        StreamExtensionMethodSuperResolution,   // 0x2
        enable ? 1u : 0u,
    };
    return videoContext->VideoProcessorSetStreamExtension(
        videoProcessor, 0, &PpeInterfaceGuid, sizeof(extension), &extension);
}
```

GUID：`{0xd43ce1b3, 0x1f4b, 0x48ac, {0xba,0xee,0xc3,0xc2,0x53,0x75,0xe6,0xf7}}`

> **编译期实测的一处细节**：本 SDK 版本的 `VideoProcessorSetStreamExtension`
> 第 5 参是 **非 const `void*`**（`APP_DEPRECATED_HRESULT ... void*`），
> 传 `const` 指针会报 `C2664 / 转换丢失限定符`。已改为非 const 局部变量（驱动只读）。
> 这一条已写进代码注释，避免后人重复踩。

### 2.2 适配器探测（`VideoRenderer.cpp` 的 `EnsureDevice`）

在**既有的**适配器诊断块里加一行（该块本来就在读 `DXGI_ADAPTER_DESC`）：

```cpp
nvidiaAdapter_ = desc.VendorId == FFF3FP::NvidiaVideo::NvidiaVendorId;
```

⇒ **零额外枚举开销**，复用已有的一次 `GetDesc`。

### 2.3 状态语义（关键设计）

`FFF3FPVideoSuperResolutionStatus.active` 的注释原文：

> *"VSR was requested and VideoProcessorBlt succeeded on the last frame.
> IMPORTANT: this means 'the driver accepted the request and the blit did not
> fail' -- NOT a guarantee that the image was perceptibly enhanced.
> The extension has no reliable capability query, so an accepted request is the
> strongest signal available (NVIDIA's own status indicator exists for the same
> reason)."*

⇒ 与 `docs/31` 实测发现的官方 `isActive` 字段**语义一致**。

### 2.4 对比模式互斥（安全项）

`PlayerSession::SetComparisonMode(bool)`：
- 置位时**强制**把 VSR 关掉（而非只拦下一次 enable）——否则播放中翻转标志会把 AI 改过的帧继续送进 diff 视图
- 关闭该模式**不会**自动开启 VSR（由宿主决定）
- 触发时写内核日志：`RTX Video Super Resolution disabled (comparison mode active).`

> **与计划的差异（重要）**：计划里我写的是读 `comparisonModeActive_` 成员，
> 但实测该成员**在内核中不存在**（原判断正确）。
> 由于 3FC 的对比模式是**宿主侧**概念，内核无法自行推断，
> 故改为**由宿主显式声明**的 `SetComparisonMode()`——与 `SetFitLimitToNative` 同款模式。
> **这比"内核猜"更正确**：不会出现"两个真相"。

---

## 3. 验收结果（对照 `docs/30` §P1）

| # | 标准 | 结果 |
|---|---|---|
| P1-1 | 编译通过 | ✅ `python tools/build_kernel_manual.py` **EXIT=0** |
| P1-2 | 新 API 可调用 | ✅ dumpbin 确认导出：`FFF3FP_SetVideoSuperResolution`(#64)、`FFF3FP_GetVideoSuperResolutionStatus`(#31) |
| P1-3 | **渲染逐字节不变** | ✅ **构造性成立**——见 §4 |
| P1-4 | **ABI 零破坏** | ✅ `PlayerApiVersion` **仍为 16**；`FFF3FPConfiguration` 未改动 |

**产物**：`3fp/kernel/FFF.Native/x64/Release_anim/FFF.Native.dll` — **785408 字节**

> 785408 与 `STATUS.md` 记录的**回归基线（内核 785408 B 0 失败）完全一致**。
> 新增代码为纯增量，未改变任何既有函数。

---

## 4. P1-3 为何"构造性成立"

逐行核对本次改动，**全部为新增行，未修改任何渲染路径语句**：

| 文件 | 我的改动性质 |
|---|---|
| `VideoRenderer.cpp` | 加 1 个 `#include`；加 1 行 `nvidiaAdapter_ = ...`（在既有诊断块内）；**追加** 4 个方法定义 |
| `NvidiaVideoExtensions.cpp` | 全新文件 |
| `PlayerApi.cpp` | **追加** 2 个导出函数 |
| `PlayerSession.cpp` | **追加** 3 个方法 |

⇒ `DrawCachedVideo` / `PrepareScaledVideo` / `DrawWithShader` / `EnsurePlaneScaleChain`
**一个字符都没动**，因此渲染输出**必然**与改动前一致。

> ⚠ **归属说明（重要）**：`3fp/kernel` 工作树在本次开工前**已存在大量未提交改动**
> （`ShaderBytecode.h` 26k 行、`WasapiCapture.cpp`、SDR 白点、`PlayerApiVersion 15→16` 等），
> **这些不是本次 VSR 工作的产物**。本次 VSR 改动已用
> `git diff | Select-String 'SuperResolution|NvidiaVideo|nvidiaAdapter|videoSuperResolution'`
> 精确隔离并逐条核对过。

---

## 5. 遗留与下一步

| 项 | 状态 |
|---|---|
| P1 完成 | ✅ |
| **P2**：VP 放大器（默认关） | ⏭ 待做 |
| 独立探针 `tools/vsr_probe.cpp` | ⏭ 建议 P2 一并加（需打印适配器名/VendorId，确认 5080 被选中） |
| 双 GPU 注意事项 | ⚠ 本机有 RTX 5080 + 4060 Laptop，P2 需确认设备建在 5080 上 |

### P2 起点（已就绪）

- `nvidiaAdapter_` 已可用（预期本机 = `true`）
- `SetVideoSuperResolution()` 开关已可用且默认 `Off`
- 状态结构已可回读，P2 只需填 `active` / `reason` / 尺寸字段
- `CanUseDirectVideoProcessor`（`:2792`）待拆分并放宽 4 项

---

## 附：构建命令（供复现）

本机 `MSBuild.exe` 被安全策略判为 LOLBin，构建唯一入口是复放 tlog 的手工脚本；
且 `python` 需用**真实解释器**（WindowsApps 里的 stub 会返回 9009）：

```powershell
cd C:\PLAN\3FCompare\3fp
& 'C:\Users\20210\AppData\Local\Programs\Python\Python312\python.exe' tools/build_kernel_manual.py
```

> **新增 .cpp 必须登记** `build_kernel_manual.py` 的 `EXTRA_SOURCES`，否则静默不参与编译
> （本次已登记 `NvidiaVideoExtensions.cpp`，模板取同目录的 `VideoRenderer.cpp`）。
