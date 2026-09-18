# PR 提交文本（待最终审核）

分支：`upstream/pr-b-adapter` → 目标 `Lake1059/FFF_Project:main`
内容：2 个提交 / 11 文件 / +425 −18，基于 `ea3ce05`

> 使用方式：审核通过后，复制下面「标题」与「正文」两节的内容，
> 在 GitHub 上对 `luoye-cpu/FFF_Project` 的 `upstream/pr-b-adapter` 分支发起 PR 即可。
> 正文从 `## 概要` 开始，到 `## English summary` 结束。

---

## 标题

```
Add explicit DXGI adapter selection, batched pixel readback, render-target diagnostics and a view-transform fast path (player API 14 -> 15)
```

---

## 正文

### 概要

本 PR 为 `FFF.Native` 增加三项能力（批量像素回读、渲染目标诊断、可指定 DXGI 适配器），
并把 `SetViewTransform` 改为不经过命令队列的直写快路径。

⚠ **这是一个 breaking change：`PlayerApiVersion` 14 → 15。**
因此本 PR **同时包含** `FFF.Native`（内核）与 `FFF.Player`（播放器）两侧改动，
**两者必须一起合并**；只合并内核会让播放器无法打开任何文件。

提交划分为两个：`Native:`（内核，9 文件）与 `Player:`（播放器，2 文件）。

### 一、改动清单

| # | 改动 | 类型 | 是否改 ABI |
|---|---|---|---|
| 1 | `FFF3FP_ReadVideoPixelRegion` —— 批量像素回读 | 新增导出 + 实现 | 否 |
| 2 | `FFF3FP_GetRenderTargetInfo` —— 渲染目标诊断 | 新增导出 + 新类型 | 否 |
| 3 | `preferredAdapterIndex` —— 可指定创建 D3D11 设备的 DXGI 适配器 | 配置字段 + 版本递增 | **是（14 → 15）** |
| 4 | `SetViewTransform` 改为直写快路径 | 行为变更 | 否 |
| 5 | `FFF.Native.rc` —— 版本资源 | 新增文件 | 否 |
| 6 | `.gitignore` —— 忽略 `vcpkg_installed/` | 构建配置 | 否 |
| 7 | 修正第 1 项在 16 位交换链下的转换 | 缺陷修复（针对本 PR 新增代码） | 否 |
| 8 | `FFF.Player` 适配 API 15 | 宿主侧必需改动 | **是** |
| 9 | 针对第 1–4 项的加固（越界拒绝、未就绪显式报错、`noexcept` 内不分配、跨线程标志） | 加固 | 否 |

### 二、逐项说明

#### 1. 批量像素回读 `FFF3FP_ReadVideoPixelRegion`

现有 `FFF3FP_ReadVideoPixel` 一次只能取一个点，每次都要完整走一遍
`AcquireBackBufferTarget` → `DrawCachedVideo` → staging 拷贝 → `Map`。
批量版本一次调用取回一个矩形，用**单次** staging 拷贝 + `Map` 完成，省去逐像素的 GPU 往返。

**加锁顺序与既有的 `PlayerVideoRenderer::ReadPixel` 完全相同**
（先 `deviceMutex_`，再 `presentMutex_`），复用同样的 `AcquireBackBufferTarget` / `DrawCachedVideo`，
不会引入新的并发路径。

**参数校验**：`dst == nullptr`、`width/height == 0`、`dstFloatCount < width * height * 4`
均返回 `InvalidArgument`；起点越界返回 `InvalidState`；
源格式仅支持 `B8G8R8A8_UNORM` / `R10G10B10A2_UNORM` / `R16G16B16A16_FLOAT`，其余返回 `NotSupported`；
`outputBitDepth` 允许传 `nullptr`。

**越界一律拒绝，不静默裁剪**：若 `x + width > swapWidth_` 或 `y + height > swapHeight_`，
返回 `InvalidArgument`。按 swap 尺寸裁剪后仍返回 `Success` 的写法会写出**紧凑行距**，
与头文件承诺的"w×h 行主序"矛盾，尾部采样仍是调用方传入的初值且无法察觉。

> `dstFloatCount` 的比较在 **64 位**下进行。`width * height * 4` 在 32 位下会回绕，
> 若用它做校验，一个超大的请求反而会让过小的缓冲区通过检查。

⚠ **每次调用会创建一张 staging 纹理**（代码注释已说明）。高频调用请自行节流。

#### 2. 渲染目标诊断 `FFF3FP_GetRenderTargetInfo`

新增结构体 `FFF3FPRenderTargetInfo`（`size`、`version`，以及 `swap*`、`client*`、
`dest*`、`outputBitDepth`、`hdr` 共 10 个数据字段）。

**调用约定**与同族的 `GetSnapshot` / `ReadVideoPixel` 一致：调用方先填 `size` 与 `version`
（`version == 1`），内核校验后再回填，避免向更小的调用方结构体越界写入。

`swap*`、`outputBitDepth`、`hdr` 取自渲染器当前状态；`client*` 由 `GetClientRect(window_)` 得到；
`dest*` 来自 `DrawCachedVideo` **成功路径**上记录的四个 `relaxed` 原子量。

**未就绪时返回 `InvalidState`**，而不是"全零 + `Success`"。交换链尚未建立时这些字段都是 0，
与真实的 0×0 目标无法区分；而调用方正是用它们映射像素回读坐标，读到全零会静默算错。

#### 3. 可指定 DXGI 适配器 `preferredAdapterIndex`

`FFF3FPConfiguration` **末尾**追加 `std::int32_t preferredAdapterIndex`（结构体 72 → 80 字节），
`PlayerApiVersion` 14 → **15**。`EnsureDevice()` 增加"指定索引优先、失败回落"的分支，
并通过 `OutputDebugStringA` 输出实际选中的 vendor / device / LUID 便于排查。

**语义**：
- `-1` 保持现有行为——由窗口所在显示器对应的适配器决定；
- `>= 0` 作为 `IDXGIFactory1::EnumAdapters1` 的索引使用；
- **越界或枚举失败一律静默回落**，不会让 `FFF3FP_Create` 失败。

⚠ **调用方必须显式把它初始化为 `-1`。** 原生字段有默认成员初始化器 `= -1`（对 C++ 调用方有效），
但通过 P/Invoke 声明结构体的宿主（例如 VB 的 `Structure`）是**零初始化**的，会得到 `0`；
而 `0` 是**合法的适配器索引**，会静默把播放钉死在 0 号卡并覆盖"按显示器选卡"的默认策略。
本 PR 的 `FFF.Player` 提交即按此显式赋 `-1`。

索引必须与内核使用**同一种枚举**——`IDXGIFactory1::EnumAdapters1`；
改用 `IDXGIFactory6::EnumAdapterByGpuPreference` 会因顺序不同而指向另一张卡。

> 由于 `CreateD3D11HardwareDeviceContext()` 把同一个 `ID3D11Device` 交给 FFmpeg 做 D3D11VA，
> 该字段**同时决定解码与渲染使用哪张适配器**。

#### 4. `SetViewTransform` 改为直写快路径

**原实现**把变换操作 `Enqueue` 到命令队列、由 Worker（解码）线程执行。
HD/HDR 播放时该线程忙于解码，排队的平移/缩放命令被明显延迟甚至被下一帧覆盖，
表现为"水平平移失效 + 卡顿"。

**改后**直接在调用线程写渲染器的三个 `relaxed` 原子量，再调 `Redraw()` 唤醒 presenter。
disc 播放的保护分支完整保留。

> ⚠ **这会改变命令执行语义**（transform 脱离命令队列），**请 maintainer 确认是否可接受。**
> 参数校验不变：`zoom <= 0` 或 `panX/panY` 非有限值返回 `InvalidArgument`。

该分支原本直接读 `disc_`（`std::unique_ptr<DiscInput>`），而 `disc_` 由 Worker 线程在
`DoOpen()` 赋值、在 `DoClose()` `reset()`，本入口却在**调用方线程** ⇒ 是数据竞争。
现改读新增的 `std::atomic<bool> discOpened_` 镜像（只判真假，不解引用）。

#### 5. 版本资源 `FFF.Native.rc`

新增文件，并在 `FFF.Native.vcxproj` 中加一个 `ResourceCompile` 项。
`FileVersion` 主段跟随 `PlayerApiVersion`，便于在部署现场判断 DLL 对应的 API 版本。
（若贵方另有版本管理约定，此项可单独取舍。）

#### 6. `.gitignore` 增加 `vcpkg_installed/`

上游已有 `third_party/vcpkg_installed/`，但它只覆盖特定路径；补一条**不带路径前缀**的规则
以覆盖仓库根或其他子目录下的同名目录。

#### 7. 修正批量回读在 16 位交换链下的转换

第 1 项新增的 `ReadPixelRegion` 最初把 `R16G16B16A16_FLOAT` 当作 `float` 读取。
但该格式每通道是 **HALF（2 字节）**，4 通道共 **8 字节/像素**，不是 16 字节
⇒ ① 数值全错（HALF 位模式被当 float 解释）② 越界读取一倍内存。
现按 `HALF*` 取值后逐通道 `XMConvertHalfToFloat`，与既有的单点 `ReadPixel` 一致。

#### 9. 针对新增代码的加固

| 位置 | 问题 | 处理 |
|---|---|---|
| `VideoRenderer::EnsureDevice()` | 诊断串用 `std::string` 拼接 ⇒ 在 `noexcept` 函数里可能抛 `bad_alloc` | 改为 `_snprintf_s` 写入栈缓冲，永不分配 |
| `VideoRenderer::ReadPixelRegion()` | 越界区域被裁剪后仍返回 `Success` | 返回 `InvalidArgument` |
| `VideoRenderer::GetRenderTargetInfo()` | 无交换链时返回"全零 + `Success`" | 返回 `InvalidState` |
| `PlayerSession::SetViewTransform()` | 在调用方线程读 Worker 持有的 `disc_` | 改读 `std::atomic<bool>` |

> `EnsureDevice()` 的 `noexcept` 尤其关键：它位于设备创建与设备丢失恢复路径上，
> 一次分配失败会把"返回错误码"变成 `std::terminate`。

### 三、`FFF.Player` 侧改动

`FFF3FP_Create` 对 `version` 做**严格相等**校验，且要求 `size >= sizeof(config)`。
内核升到 15 后，播放器若仍按 14 构造会话，**每一个会话创建都会失败**。因此第二个提交包含：

1. `FFF.Player/Core/播放器会话.vb`：`FFF3FP_GetApiVersion() <> 14UI` → **15UI**
2. 同文件：`.版本 = 14UI` → **15UI**
3. `FFF.Player/Interop/播放器原生接口.vb`：`原生播放器配置` 结构**末尾**追加 `首选适配器索引 As Integer`
4. `FFF.Player/Core/播放器会话.vb` 构造处：初始化 `.首选适配器索引 = -1`

第 3 点后 `原生播放器配置` 由 **72 字节**变为 **80 字节**，与内核 `sizeof(FFF3FPConfiguration)` 一致；
VB 侧大小由 `Marshal.SizeOf` 运行时计算，无需手工维护。第 4 点是必需的（原因见 §二.3）。

> 若希望分成两个 PR 合并，**请先合并播放器侧，再合并内核侧**；反过来会让中间状态不可用。

### 四、API 版本变更

本 PR 将 `PlayerApiVersion` 从 **14 提升到 15**，原因是 `FFF3FPConfiguration` 增加了字段。
新字段**只追加在结构体末尾**，不移动、不插入既有字段。
若希望只合并不带 ABI 变更的部分，可只取第 1、2、4–7、9 项（此时版本仍为 14）。

### 五、其他

- 全部改动为**新增或局部修改**，未调整既有函数的对外行为（第 4 项除外，已标注）。
- 第 1、2 项可独立使用，不依赖第 3 项的版本变更。
- 已验证补丁可干净应用到 `ea3ce05`（`git am --3way`，2/2 提交）。
- 内核按仓库自带的构建配置重新构建并加载验证，`FFF3FP_GetApiVersion()` 返回 15。
- 新增的导出与适配器选择均已在真实会话中跑通（创建 → 打开 → 播放 → 回读 → 变换）。

---

## English summary

Two commits: one for `FFF.Native`, one for `FFF.Player`. **Breaking change — player API 14 → 15.**

1. **`FFF3FP_ReadVideoPixelRegion`** — batched pixel readback: one staging copy + `Map` per call
   instead of a full GPU round-trip per pixel. Uses the **same lock order as the existing
   `ReadPixel`** (`deviceMutex_` → `presentMutex_`), so no new concurrency path is introduced.
   The `dstFloatCount` check is done in 64-bit, since `width * height * 4` wraps in 32-bit and would
   otherwise let an undersized buffer pass. A region that does not fit inside the swapchain is
   **rejected with `InvalidArgument`** rather than clamped: clamping produced a compact row pitch
   that contradicts the documented "w×h row-major" layout while still returning `Success`.
   A staging texture is created per call — callers are expected to throttle.
2. **`FFF3FP_GetRenderTargetInfo`** — reports swap / client / destination rectangles, output bit
   depth and HDR flag. It follows the same negotiation contract as `GetSnapshot`: the caller fills
   `size`/`version` first. It returns `InvalidState` while no swapchain exists, so a caller can
   never mistake an all-zero struct for a real 0×0 target.
3. **`preferredAdapterIndex`** — lets the caller choose which DXGI adapter creates the D3D11 device.
   `-1` keeps today's behaviour; out-of-range or non-enumerable indices **silently fall back**.
   Because FFmpeg's D3D11VA context reuses the same device, this selects the adapter used for
   **both decode and render**. Requires `PlayerApiVersion` **14 → 15** (field appended at the end).
   ⚠ Callers **must initialise it to `-1` explicitly** — hosts that declare the struct via P/Invoke
   get zero-initialised memory, and `0` is a **valid adapter index** that would silently pin
   playback to adapter #0. The index must come from `IDXGIFactory1::EnumAdapters1`.
4. **`SetViewTransform`** — writes the renderer's three relaxed atomics directly instead of queueing
   the work on the decode thread. The disc-playback guard is preserved, but now reads an atomic
   mirror instead of `disc_` itself (which the worker thread owns).
   **This changes command-execution semantics — please confirm.**
5. **`FFF.Native.rc`** — version resource whose `FileVersion` tracks `PlayerApiVersion`.
6. **`.gitignore`** — adds a path-less `vcpkg_installed/` rule.
7. **Fix for (1) on 16-bit swap chains** — `R16G16B16A16_FLOAT` stores each channel as **HALF**
   (8 bytes/pixel), but the new readback initially treated it as `float` (16 bytes/pixel).
8. **`FFF.Player`** — adopts API 15: the two `14UI` occurrences become `15UI`, and the
   `原生播放器配置` struct gains `首选适配器索引 As Integer` at the end (72 → 80 bytes), initialised
   to `-1`. Without this, every session creation fails. **If split into two PRs, merge the host
   change first.**
9. **Hardening of the new code** — the adapter diagnostic in `EnsureDevice()` formats into a
   stack buffer (that function is `noexcept`); the remaining items are covered under (1), (2), (4).

---

## 提交前 checklist

- [x] 分支已推到 fork：`luoye-cpu/FFF_Project` → `upstream/pr-b-adapter` = `7deabbd`
- [x] 补丁可干净应用到 `ea3ce05`（`git am --3way` 2/2）
- [x] 内核构建通过并验证 `GetApiVersion()` = 15
- [x] 内核 API 级端到端：3 路真实 4K 素材 × 18 项断言 = 54/54
- [x] 托管播放器端到端：`--startup-regression` 2/3 轮全绿（6 路 drop 全 0）
- [x] 无品牌名 / 内部编号 / 本机实测数字残留
- [ ] 您确认后 → 在 GitHub 发起 PR
