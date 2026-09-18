# 上游 PR 描述

> 分支 `upstream/pr-b-adapter`（基于上游 `ea3ce05`），**2 个提交**，11 文件 **+425 / -18**
> 补丁：`docs/upstream/proposal.patch`
>
> ⚠ 这是一个 **breaking change**：`PlayerApiVersion` 14 → 15。
> 因此本 PR **同时包含** `FFF.Native`（内核）与 `FFF.Player`（播放器）两侧改动，
> 两者必须一起合并，缺一侧则会话创建失败。详见 §三。

## 标题

```
Add explicit DXGI adapter selection, batched pixel readback, render-target
diagnostics and a view-transform fast path (player API 14 -> 15)
```

## 一、改动总览

| # | 改动 | 类型 | 是否改 ABI |
|---|---|---|---|
| 1 | `FFF3FP_ReadVideoPixelRegion` —— 批量像素回读 | 新增导出 + 实现 | 否 |
| 2 | `FFF3FP_GetRenderTargetInfo` —— 渲染目标诊断 | 新增导出 + 新类型 | 否 |
| 3 | `preferredAdapterIndex` —— 可指定创建 D3D11 设备的 DXGI 适配器 | 配置字段 + 版本递增 | **是（14 -> 15）** |
| 4 | `PlayerSession::SetViewTransform` 改为直写快路径 | 行为变更 | 否 |
| 5 | `FFF.Native.rc` —— 版本资源 | 新增文件 | 否 |
| 6 | `.gitignore` —— 忽略 `vcpkg_installed/` | 构建配置 | 否 |
| 7 | 修正第 1 项在 16 位交换链下的转换 | 缺陷修复（针对本 PR 新增代码） | 否 |
| 8 | `FFF.Player` 适配 API 15（版本校验 / 配置结构 / 适配器字段） | 宿主侧必需改动 | **是** |
| 9 | 针对第 1–4 项的加固：越界拒绝、未就绪显式报错、`noexcept` 内不分配、跨线程标志 | 加固（针对本 PR 新增代码） | 否 |

提交划分即按此：第 1–7、9 项在一个 `FFF.Native` 提交里，第 8 项单独一个 `FFF.Player` 提交。

---

## 二、逐项说明

### 1. 批量像素回读 `FFF3FP_ReadVideoPixelRegion`

**代码位置**：`FFF.Player.Api.h` 导出声明、`PlayerApi.cpp` 转发、
`PlayerSession::ReadVideoPixelRegion`、`PlayerVideoRenderer::ReadPixelRegion`。

**作用**：现有 `FFF3FP_ReadVideoPixel` 一次只能取一个点，每次都要完整走一遍
`AcquireBackBufferTarget` -> `DrawCachedVideo` -> staging 拷贝 -> `Map`。批量版本一次调用取回一个矩形，
用**单次** staging 拷贝 + `Map` 完成，省去逐像素的 GPU 往返。缩略图、取色、放大镜这类场景直接受益。

**与既有实现的一致性**：加锁顺序与既有的 `PlayerVideoRenderer::ReadPixel` **完全相同**
（先 `deviceMutex_`，再 `presentMutex_`），复用同样的 `AcquireBackBufferTarget` / `DrawCachedVideo`，
因此不会引入新的并发路径。

**参数校验**：`dst == nullptr`、`width/height == 0`、
`dstFloatCount < width * height * 4` 均返回 `InvalidArgument`；起点越界（`x >= swapWidth_`
或 `y >= swapHeight_`）返回 `InvalidState`；
源格式仅支持 `B8G8R8A8_UNORM` / `R10G10B10A2_UNORM` / `R16G16B16A16_FLOAT`，其余返回 `NotSupported`；
`outputBitDepth` 允许传 `nullptr`。

**越界一律拒绝，不静默裁剪**：若 `x + width > swapWidth_` 或 `y + height > swapHeight_`，
返回 `InvalidArgument`。早期实现用 `min(width, swapWidth_ - x)` 裁剪后仍返回 `Success`，
但此时写回的是**紧凑行距**，与头文件承诺的"w×h 行主序"矛盾，尾部采样仍保持调用方传入的初值，
调用方无法察觉。**宁可显式报错，也不要返回看起来成功、实则错误的数据。**

> `dstFloatCount` 的比较在 **64 位**下进行（`static_cast<std::uint64_t>(width) * height * 4u`）。
> `width * height * 4` 在 32 位下会回绕，若用它做校验，一个超大的请求反而会让过小的缓冲区通过检查。

⚠ **每次调用会创建一张 staging 纹理**（代码注释已说明）。若调用方高频调用，建议自行节流；
若后续回读变热，可按 `(format, width, height)` 缓存并在交换链重建时失效。

### 2. 渲染目标诊断 `FFF3FP_GetRenderTargetInfo`

**代码位置**：`FFF.Player.Api.h` 新增 `FFF3FPRenderTargetInfo` 与导出声明；
`PlayerApi.cpp` / `PlayerSession` 转发；`PlayerVideoRenderer::GetRenderTargetInfo`
在 `deviceMutex_` 下读取并填充。

**结构体**（共 12 个字段：2 个协商字段 + 10 个数据字段）：`size`、`version`、
`swapWidth/Height`、`clientWidth/Height`、`destX/Y`、`destWidth/Height`、`outputBitDepth`、`hdr`。

**调用约定**：与同族的 `GetSnapshot` / `ReadVideoPixel` 一致，调用方需先填 `size` 与 `version`
（`version == 1`）；内核校验后再回填，避免向更小的调用方结构体越界写入。

**未就绪时返回 `InvalidState`**，而不是返回"全零 + `Success`"。交换链尚未建立时（例如首帧呈现之前）
结构体的 `swap*` / `dest*` 都是 0，与一个真实的 0×0 目标无法区分；而调用方正是用这些数值去映射
像素回读坐标，读到全零会静默算出错误的采样结果，而不是一次可诊断的失败。

**数据来源**：`swap*`、`outputBitDepth`、`hdr` 取自渲染器当前状态；`client*` 由
`GetClientRect(window_)` 得到；`dest*` 来自 `DrawCachedVideo` **成功路径**上记录的四个
`relaxed` 原子量（`lastDestX_/Y_/Width_/Height_`）。

> 上游若重构 `DrawCachedVideo`，需重新锚定这四个原子量的记录点。它们只是诊断数据，
> 失效时表现为数值陈旧，不会导致崩溃。

### 3. 可指定 DXGI 适配器 `preferredAdapterIndex`

**代码位置**：
- `FFF.Player.Api.h`：`FFF3FPConfiguration` **末尾**追加 `std::int32_t preferredAdapterIndex`；
  `PlayerApiVersion` 14 -> **15**
- `PlayerApi.cpp`：`FFF3FP_Create` 中读取并做范围校验（`< -1` 或 `> 15` 返回 `InvalidArgument`）
- `VideoRenderer.h/.cpp`：新增成员 `preferredAdapterIndex_`（`std::atomic<std::int32_t>`）与
  `SetPreferredAdapterIndex()`；`EnsureDevice()` 增加"指定索引优先、失败回落"的分支，
  并输出一条 `OutputDebugStringA` 诊断（实际选中的 vendor / device / LUID，栈缓冲格式化，见 §二.9）
- `PlayerSession.cpp`：构造期接线（必须在首次 `EnsureDevice()` 之前）

**语义**：
- `-1` 保持现有行为——由窗口所在显示器对应的适配器决定；
- `>= 0` 作为 `IDXGIFactory1::EnumAdapters1` 的索引使用；
- **越界或枚举失败一律静默回落**，不会让 `FFF3FP_Create` 失败，调用方无需处理降级。

⚠ **调用方必须显式把它初始化为 `-1`。** 原生字段已提供默认成员初始化器 `= -1`（对 C++ 调用方有效），
但通过 P/Invoke 声明结构体的宿主（例如 VB 的 `Structure`）是**零初始化**的，会得到 `0`。
而 `0` 是**合法的适配器索引**，不是"未设置"——零初始化的调用方会把播放钉死在 0 号适配器上，
静默覆盖内核"按窗口所在显示器选卡"的默认策略。本 PR 的 `FFF.Player` 提交即按此显式赋 `-1`。

> 另外请注意：索引必须与内核使用**同一种枚举**保持一致，即 `IDXGIFactory1::EnumAdapters1`。
> 宿主若改用 `IDXGIFactory6::EnumAdapterByGpuPreference` 之类的方式枚举，顺序可能不同，
> 同一个索引会指向另一张卡。

**为何只作用于 `EnsureDevice()`**：适配器只影响设备创建（含设备丢失后的重建）。
`SetPreferredAdapterIndex()` 不主动拆除已有设备，避免在任意调用点触发交换链重建。

> 由于 `CreateD3D11HardwareDeviceContext()` 把同一个 `ID3D11Device` 交给 FFmpeg 做 D3D11VA，
> 该字段**同时决定解码与渲染使用哪张适配器**，每个会话自洽，不需要跨设备传输帧数据。

### 4. `SetViewTransform` 改为直写快路径

**代码位置**：`PlayerSession.cpp` 的 `SetViewTransform`。

**原实现**：把变换操作 `Enqueue` 到命令队列，由 Worker（解码）线程执行。
**问题**：HD/HDR 播放时该线程忙于解码，排队的平移/缩放命令会被明显延迟，甚至被下一帧覆盖，
表现为"水平平移失效 + 卡顿"。

**改后**：直接在调用线程写渲染器的三个 `relaxed` 原子量，再调 `Redraw()` 唤醒 presenter。
上游的 **disc 保护分支完整保留**，disc 播放仍走原路径。

> ⚠ 该分支原本直接读 `disc_`（`std::unique_ptr<DiscInput>`）。`disc_` 由 Worker 线程在
> `DoOpen()` 里赋值、在 `DoClose()` 里 `reset()`，而 `FFF3FP_SetViewTransform` 在**调用方线程**
> 执行 ⇒ 直接读这个指针是数据竞争，且在 `reset()` 之后存在 use-after-free 窗口。
> 本 PR 新增 `std::atomic<bool> discOpened_` 作为"disc 是否存活"的跨线程镜像，
> 该分支改为读这个标志（只判真假，不解引用 `disc_`）。

> ⚠ 这会改变命令执行语义（transform 脱离命令队列），请 maintainer 确认是否可接受。
> 参数校验不变：`zoom <= 0` 或 `panX/panY` 非有限值返回 `InvalidArgument`。

### 5. 版本资源 `FFF.Native.rc`

新增文件，并在 `FFF.Native.vcxproj` 中加一个 `ResourceCompile` 项（3 行）。
`FileVersion` 主段跟随 `PlayerApiVersion`，便于在部署现场用资源管理器或 `GetFileVersionInfo`
直接判断 DLL 对应的 API 版本，不必加载 DLL 调 `FFF3FP_GetApiVersion()`。
（若贵方另有版本管理约定，此项可单独取舍。）

### 6. `.gitignore` 增加 `vcpkg_installed/`

上游已有 `third_party/vcpkg_installed/`，但它只覆盖特定路径；`vcpkg_installed/`
也可能直接出现在仓库根或其他子目录，故补一条**不带路径前缀**的规则（匹配任意层级），
避免构建产物被误纳入版本控制。

### 7. 修正批量回读在 16 位交换链下的转换

第 1 项新增的 `ReadPixelRegion` 最初把 `R16G16B16A16_FLOAT` 当作 `float` 读取
（`memcpy(copyWidth * 4 * sizeof(float))`）。但该格式每通道是 **HALF（2 字节）**，
4 通道共 **8 字节/像素**，不是 16 字节 ⇒ ① 数值全错（HALF 位模式被当 float 解释）
② 越界读取一倍内存。

**修正**：与既有的单点 `ReadPixel` 保持一致 —— 按 `HALF*` 取值后逐通道
`XMConvertHalfToFloat` 转换。

因为这段代码是本 PR 新增的，缺陷修复直接并入本 PR，不单独拆出。

### 9. 针对本 PR 新增代码的加固

四项，均只触及本 PR 引入的代码：

| 位置 | 问题 | 处理 |
|---|---|---|
| `VideoRenderer::EnsureDevice()` | 诊断串用 `std::string` + `std::to_string` 拼接 ⇒ 在 `noexcept` 函数里可能抛 `bad_alloc` | 改为 `_snprintf_s` 写入 192 字节栈缓冲，任何情况下都不会分配 |
| `VideoRenderer::ReadPixelRegion()` | 越界区域被 `min()` 裁剪后仍返回 `Success`，行距与契约不符 | 返回 `InvalidArgument`（详见 §二.1） |
| `VideoRenderer::GetRenderTargetInfo()` | 无交换链时返回"全零 + `Success`" | 返回 `InvalidState`（详见 §二.2） |
| `PlayerSession::SetViewTransform()` | 在调用方线程读 Worker 线程持有的 `disc_` | 改读 `std::atomic<bool> discOpened_`（详见 §二.4） |

> `EnsureDevice()` 的 `noexcept` 尤其关键：它位于设备创建与设备丢失恢复路径上，
> 一次分配失败会把"返回错误码"变成 `std::terminate`，即整个进程退出。

---

## 三、`FFF.Player` 侧改动（**已包含在本 PR 中**）

`FFF3FP_Create` 对 `version` 做**严格相等**校验，且要求 `size >= sizeof(config)`。
内核升到 15 后，播放器若仍按 14 构造会话，**每一个会话创建都会失败**（播放器将打不开任何文件）。
因此本 PR 的第二个提交直接带上播放器侧改动：

1. `FFF.Player/Core/播放器会话.vb`（约 41 行）
   `FFF3FP_GetApiVersion() <> 14UI` → **15UI**
2. 同文件（约 49 行）
   `.版本 = 14UI` → **15UI**
3. `FFF.Player/Interop/播放器原生接口.vb`
   `原生播放器配置` 结构**末尾**追加 `首选适配器索引 As Integer`
4. `FFF.Player/Core/播放器会话.vb` 构造处
   初始化 `.首选适配器索引 = -1`

第 3 点后 `原生播放器配置` 由 **72 字节**变为 **80 字节**（8 字节对齐），与内核
`sizeof(FFF3FPConfiguration)` 一致；若不追加该字段，`Marshal.SizeOf` 小于 80，
`size >= sizeof(config)` 校验会失败。第 4 点是必需的，原因见 §二.3（`0` 是合法索引）。

> 若贵方希望把内核与播放器分成两个 PR 合并，请先合并播放器侧，再合并内核侧；
> 反过来会让中间状态不可用。

---

## 四、API 版本变更（需 maintainer 留意）

本 PR 将 `PlayerApiVersion` 从 **14 提升到 15**，原因是 `FFF3FPConfiguration` 增加了字段。

- `FFF3FP_Create` 对 `version` 做**严格相等**校验，并要求 `size >= sizeof(config)`；
- 因此内核与所有调用方需同批次发布（见上一节）；
- 新字段**只追加在结构体末尾**，不移动、不插入既有字段。

> 若希望只合并不带 ABI 变更的部分，可只取第 1、2、4–7 项（此时版本仍为 14）；
> 第 3 项（多显卡）与第 8 项需单独讨论版本协调。

## 五、其他

- 全部改动为**新增或局部修改**，未调整既有函数的对外行为（第 4 项除外，已标注）。
- 第 1、2 项可独立使用，不依赖第 3 项的版本变更。
- 已验证补丁可干净应用到 `ea3ce05`（`git am --3way`，2/2 提交）。
- 内核按仓库自带的构建配置重新构建并加载验证，`FFF3FP_GetApiVersion()` 返回 15。
- 新增的导出（`ReadVideoPixelRegion` / `GetRenderTargetInfo`）与适配器选择均已在真实
  会话中跑通（创建 → 打开 → 播放 → 回读 → 变换）。测试细节留在本机记录中，
  如需我可以把验证程序一并提上来。

---

## English summary

Two commits: one for `FFF.Native`, one for `FFF.Player`. **Breaking change — player API 14 → 15.**

1. **`FFF3FP_ReadVideoPixelRegion`** — batched pixel readback: one staging copy + `Map` per call
   instead of a full GPU round-trip per pixel. Uses the **same lock order as the existing
   `ReadPixel`** (`deviceMutex_` → `presentMutex_`), so no new concurrency path is introduced.
   The `dstFloatCount` check is done in 64-bit, since `width * height * 4` wraps in 32-bit and would
   otherwise let an undersized buffer pass. A region that does not fit inside the swapchain is
   **rejected with `InvalidArgument`** rather than clamped: clamping produced a compact row pitch
   that contradicts the documented "w×h row-major" layout while still returning `Success`, so the
   caller could not tell that the tail samples were never written. A staging texture is created per
   call — callers are expected to throttle.
2. **`FFF3FP_GetRenderTargetInfo`** — reports swap / client / destination rectangles, output bit
   depth and HDR flag (12 fields: `size`, `version`, plus 10 data fields). It follows the same
   negotiation contract as `GetSnapshot`: the caller fills `size`/`version` first, so the kernel
   never writes past a smaller caller-side layout. The destination rect comes from four relaxed
   atomics recorded on the success path of `DrawCachedVideo` (diagnostic only; re-anchor if that
   function is refactored). It returns `InvalidState` while no swapchain exists, so a caller can
   never mistake an all-zero struct for a real 0×0 target when mapping pixel-probe coordinates.
3. **`preferredAdapterIndex`** — lets the caller choose which DXGI adapter creates the D3D11 device.
   `-1` keeps today's behaviour; out-of-range or non-enumerable indices **silently fall back**, so
   callers never see a hard failure. Because FFmpeg's D3D11VA context reuses the same device, this
   selects the adapter used for **both decode and render**. Requires `PlayerApiVersion` **14 → 15**
   (field appended at the end of `FFF3FPConfiguration`; strict equality check in `FFF3FP_Create`).
   ⚠ Callers **must initialise it to `-1` explicitly**. The native field carries a default member
   initialiser, but hosts that declare the struct via P/Invoke get zero-initialised memory, and
   `0` is a **valid adapter index** — that would silently pin playback to adapter #0 and override
   the monitor-based default. The index also has to come from the same enumeration the kernel uses,
   `IDXGIFactory1::EnumAdapters1`; other enumerations can order adapters differently.
4. **`SetViewTransform`** — writes the renderer's three relaxed atomics directly instead of queueing
   the work on the decode thread, which could delay pans noticeably under HD/HDR load.
   The disc-playback guard is preserved, but it now reads a `std::atomic<bool>` mirror instead of
   `disc_` itself: `disc_` is assigned in `DoOpen()` and reset in `DoClose()` on the worker thread,
   while this entry point runs on the caller's thread, so reading the `unique_ptr` directly was a
   data race with a use-after-free window. This changes command-execution semantics — please confirm.
5. **`FFF.Native.rc`** — version resource whose `FileVersion` tracks `PlayerApiVersion`.
6. **`.gitignore`** — adds a path-less `vcpkg_installed/` rule to cover locations the existing
   `third_party/vcpkg_installed/` entry does not.
7. **Fix for (1) on 16-bit swap chains** — `R16G16B16A16_FLOAT` stores each channel as **HALF**
   (2 bytes, 8 bytes/pixel), but the new readback initially treated it as `float` (16 bytes/pixel),
   which produced wrong values and over-read the row by 2×. Now converts per channel via
   `XMConvertHalfToFloat`, matching the existing single-point `ReadPixel`.
9. **Hardening of the new code** — the adapter diagnostic in `EnsureDevice()` now formats into a
   fixed-size stack buffer, because that function is `noexcept` and an allocation there would turn
   an out-of-memory condition into `std::terminate` on the device-creation and device-loss-recovery
   paths. The remaining three items are covered under (1), (2) and (4) above.
8. **`FFF.Player`** — adopts API 15: the two `14UI` occurrences become `15UI`, and the
   `原生播放器配置` struct gains `首选适配器索引 As Integer` at the end (72 → 80 bytes), initialised
   to `-1`. Without this, every session creation fails and the player cannot open any file. Because
   `FFF3FP_Create` requires an exact version match, the kernel and this host change must land
   together (host first if they are split into two PRs).
