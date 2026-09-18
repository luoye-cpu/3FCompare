# [Bug] 多路播放随机崩溃：`Present` 与交换链改写由两把不同的锁保护，互不排斥

> **提交状态：✅ 已于 2026-09-15 提交为上游 issue #7**
> <https://github.com/Lake1059/FFF_Project/issues/7>
>
> ---
>
> ⚠ **2026-09-17 追加：上游 PR #8（`ea3ce05` / `824093d`）已合并，但复测证明未解决。**
> 修复后 12 次 `--multitest` 崩溃 6 次（50%），与修复前 23 次 12 崩（52%）无差异。
> PR #8 只覆盖了本文建议的第 1、2 条（`PresentTimedText` ↔ `EnsureSwapChain`）；
> **第 4 条 `RequestRecoveryIfDeviceLost()`（§3.4，500 ms 空闲轮询路径）与
> 第 5 条 `ClearSurface()`（§3.5）未处理**，而"零交互静置播放也会崩"正由它们导致。
> 完整复测与源码核对见 `docs/18-上游PR8合并与issue7复测.zh.md`。
> 下文行号仍基于提交时的上游基线，作历史记录保留。
>
> 提交位置：<https://github.com/Lake1059/FFF_Project/issues/new>
> 标题：`[3FP] 多路播放随机崩溃：PresentTimedText 的 Present 与 EnsureSwapChain 的锁不同，导致 DXGI 内部访问违例`
> 语言：中文（上游为中文项目）。以下行号基于 `FFF.Native/3FP/Render/VideoRenderer.cpp`
> （对应上游 `f25c28f` ~ `d8b2c03`，该区间 `FFF.Native` 零改动，行号可直接对应）。
> 提交方式见 `docs/13-管线综合审查与建议.zh.md` §19.8（用 git 的 wincred 凭据取 OAuth token 调 API）。

---

## 一、现象

同时播放 **2 路及以上**视频时，进程会随机崩溃，退出码 `0xC0000005`（访问违例）
或 `0xC000001D`（非法指令）。单路播放稳定，不出现。

- 素材：4K HEVC 10bit / 60 Mbps 实拍片
- 路数：2
- 实测崩溃率：**约 50%**（23 次运行中 12 次崩溃），**与具体操作无关**
  ——静置播放（完全无 UI 交互）时同样会崩

## 二、崩溃证据

### 2.1 转储聚类（10 个 WER 转储）

| 簇 | 次数 | 异常 | 崩溃点 | 现场特征 |
|:--:|:--:|---|---|---|
| A | 3 | `0xC0000005` 读 | `dxgi.dll +0x19530` | 被读地址 = `0xFFFFFFFFFFFFFFFF` |
| B | 2 | `0xC000001D` 非法指令 | `dxgi.dll +0x33AF3` | 与簇 C 相距 3 字节 |
| C | 1 | `0xC0000005` 写 | `dxgi.dll +0x33AF0` | 被写地址不在任何模块内 |
| D | 1 | `0xC0000005` 执行/DEP | `0x00007FFD8EB00000` | 不属于任何已加载模块，页对齐且不可执行 |
| E | 3 | `0xC0000602` FailFast | `coreclr.dll` | 另一问题（RIP 在 `RaiseFailFastException`，主动触发），本文不涉及 |

读全 1、非法指令、跳转到不可执行页——三类都是**通过失效/被破坏的指针进入 DXGI** 的表现。

### 2.2 符号化调用栈（崩溃线程）

```
PlayerVideoRenderer::TimedTextThread + 0x2F7        ← 内核为每个渲染器起的线程
  std::condition_variable::wait_until<...>
    PlayerVideoRenderer::PresentTimedText + 0x542
      PlayerVideoRenderer::PresentCurrentFrame + 0x7E
        [d3d11.dll] → dxgi.dll                      ← 崩溃在这里
```

崩溃线程上**没有任何托管/应用层帧**。崩溃时刻进程有 106 个线程，其中 3 个原生线程
同时位于 D3D11/DXGI 调用中（而会话只有 2 路）。

## 三、根因：同一个交换链被两把不同的锁保护

### 3.1 呈现路径只持 `presentMutex_`

`PresentTimedText()`（4701 起）：

```cpp
std::unique_lock deviceLock(deviceMutex_);            // 4702
const auto chainResult = EnsureSwapChain(...);        // 4706  ← 此刻只持有 deviceMutex_
...
std::unique_lock presentLock(presentMutex_);          // 4722
...
ComPtr<IDXGISwapChain4> retainedChain = swapChain_;   // 4752
deviceLock.unlock();                                  // 4753  ← 交出 deviceMutex_
const auto result = PresentCurrentFrame(retainedChain.Get(), generation);  // 4754
```

**4754 的 `Present` 执行期间只持有 `presentMutex_`。**

### 3.2 改写路径持 `deviceMutex_`

而 `EnsureSwapChain()` 会**改写**交换链：

- `swapChain_->ResizeBuffers(...)`（2128）
- `CreateSwapChain(...)`（2141）
- `ReconfigureSwapChain(...)`（2098 / 2120 → 2234-2237 释放旧链）

**4706 处调用它时只持有 `deviceMutex_`，尚未获取 `presentMutex_`。**

⇒ **两把锁保护同一个对象，互不排斥。** 一个线程可以在改写交换链的同时，
另一个线程正在 `Present`。

### 3.3 上游自己已经写明这条不变量

`VideoRenderer.cpp:3937` 的注释：

```cpp
// Present and ResizeBuffers must never overlap. Dropping this render is
// safe because the following decoded frame publishes the latest state.
```

主渲染路径（3922 / 3933）确实是先 `deviceMutex_` 再 `presentMutex_` 两把都拿，
遵守了这条不变量；但 **`PresentTimedText` 在 4706 调 `EnsureSwapChain` 时只有
`deviceMutex_`**，这条不变量在那里不成立。

### 3.4 为什么"完全不操作"也会崩

`TimedTextThread` 空闲时（500 ms 无更新）走 `devicePollOnly`，调用：

```cpp
bool PlayerVideoRenderer::RequestRecoveryIfDeviceLost() noexcept {
    std::lock_guard deviceLock(deviceMutex_);      // 4821，只有 deviceMutex_
    return RequestRecoveryIfDeviceLostLocked();    // → device_->GetDeviceRemovedReason()
}
```

（3024 行调用）它**只持 `deviceMutex_`**，而此刻另一线程完全可能正持 `presentMutex_`
在 `Present`。这解释了为什么静置播放、无任何 UI 操作也会崩溃。

### 3.5 另一处：`ClearSurface()` 无锁

`ClearSurface()`（4781-4793）在**完全无锁**的情况下读 `swapChain_` 判空，
只在最后 `Present` 前才取 `presentMutex_`。

## 四、建议修法

1. 让交换链的**所有**改写路径（`EnsureSwapChain` / `ReconfigureSwapChain` /
   `CreateSwapChain` / `ResizeBuffers`）在**整个持续期间**持有 `presentMutex_`，
   而不是只在释放旧链的那几行持有；
2. `Present` 返回前不要交出 `deviceMutex_`（即删除或后移 4753 的 `deviceLock.unlock()`）；
3. 最稳妥的做法是把 `deviceMutex_` 与 `presentMutex_` 合并为一把锁；
4. 给 `ClearSurface()` 补上锁；
5. 若保留两把锁，建议加断言/计数器验证"Present 与 ResizeBuffers 不重叠"这条不变量。

## 五、复现方式

```
3FCompare.exe --multitest <4K HEVC10 60Mbps 视频> 2 15
```

该自测会：打开 2 路 → 静置播放 5 s → 20 次 `SetViewTransform` 扇出 → 复位 → 帧步进。
崩溃点在上述各阶段均出现过（0.45 s / 5.5 s / 7.9 s / 启动期），说明是时序竞态。

**注意**：崩溃与 `SetViewTransform` 无必然关系——静置播放（零交互）时同样会崩，
见 3.4 的 500 ms 空闲轮询。

## 六、环境

- OS：Windows 11 24H2（build 26100）
- `dxgi.dll`：10.0.26100.9444
- 运行时：.NET 11 preview（`coreclr.dll` 11.0.26.38203）
- GPU：NVIDIA（`nvwgf2umx.dll`）
- 内核版本：上游 `d8b2c03`（tag 2026.9.14）
- 崩溃线程数上下文：106 线程，其中 15 个托管线程、4 个含 `FFF.Native` 帧

---

## 附：我们这边的排查产出（供参考）

- 10 个转储的聚类与符号化方法：`tools/dump_triage.py`、`tools/pdb_resolve.py`、
  `tools/dump_symbols.py`（本机无 WinDbg，dbghelp 对 `FFF.Native.dll` 只肯装载导出符号，
  故自写了 MSF/PDB 公共符号解析）
- 完整分析记录：`docs/13-管线综合审查与建议.zh.md` §18 / §18.9 / §19
- 已确认该问题**不是**下游（3FCompare）补丁引入：崩溃链上的所有符号
  （`TimedTextThread` / `PresentTimedText` / `presentMutex_` / `deviceMutex_` /
  `RequestRecoveryIfDeviceLost` / `interactiveMove_` try_lock 快路径）在上游基线里全部存在。
