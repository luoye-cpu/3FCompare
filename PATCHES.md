# 3FCompare 内核补丁索引（PATCHES）

> 基线：上游 tag `upstream-baseline-2026.9.11`（f25c28f）。
> 本地分支：`3fcompare/zoom-viewport-cover`，归档 tag `3fcompare-kernel-2026.9.11.1`。
> 本文档固化"哪些补丁必须在每次上游更新后重放"的清单，避免合并时靠记忆裁决。
> 原则：**上游优先**——上游已有等价实现的一律不重放；仅托管 API 硬依赖且上游无等价的扩展保留。

## 一、必须重放（托管 API 硬依赖，上游无等价）

每次上游更新后需人工核对签名/上下文并重移植，按维护成本从低到高排列：

| 补丁 | 锚点 | 托管侧依赖 | 重移植要点 |
|---|---|---|---|
| SetPresentConfig / SetPacingConfig（最小 shim） | `VideoRenderer.cpp/.h`，PlayerApi 导出 `FFF3FP_SetPresentConfig` / `FFF3FP_SetPacingConfig` | `Fff3FpEngine`、`MainWindow` | tearing 存偏好位；pacing 为 no-op（上游无周期 keepalive present 可抑制） |
| GetRenderTargetInfo（K4 诊断） | `VideoRenderer.cpp/.h` 的 `RenderTargetInfo` 结构 + `lastDestX_/Y/Width/Height_` atomics；PlayerApi 导出 `FFF3FP_GetRenderTargetInfo` | `Fff3FpEngine` 轮询诊断 | `lastDest*` 由 `DrawCachedVideo` 成功路径记录——上游若重写该函数需重新锚定记录点 |
| ReadPixelRegion（原 patch 0004，批量像素回读） | `VideoRenderer.cpp/.h`；PlayerApi 导出 `FFF3FP_ReadVideoPixelRegion` | `Fff3FpEngine.TryReadPixelRegion`（缩略图/取色） | 单次 staging 拷贝 + Map，替代逐像素 GPU 往返；依赖 `AcquireBackBufferTarget` / `DrawCachedVideo` / `swapOutputBits_`，上游渲染器重构时签名可能漂移 |
| SetViewTransform 直写原子路径（原 0006 rev5） | `PlayerSession.cpp` `SetViewTransform`（绕过 Enqueue 命令队列直写渲染器原子量 + Redraw） | `Fff3FpEngine.SetViewTransform` ← UI 平移/缩放主路径 | 动机：命令队列在 Worker（解码）线程上执行，HD/HDR 播放时平移命令延迟数十 ms（"水平平移失效+卡顿"）。重放时保留上游的 disc 保护分支 |

## 二、已被上游吸收（不重放，更新后需复核）

| 补丁 | 上游等价实现 | 复核点 |
|---|---|---|
| 6e7469f DWM 修复（ResizeBuffers 后 `Present(0,0)` 解除 DWM 停滞） | 上游 `VideoRenderer.cpp` EnsureSwapChain 失败恢复路径自带 `swapChain_->Present(0, 0)`（2026.9 master） | 上游若重构 swapchain 恢复逻辑，确认该路径仍在 |
| 8204c02 音频缓冲 250ms 硬编码 | 上游 WASAPI 重构：`bufferDuration = clamp(sharedDefaultPeriod*3, 50ms, 200ms)` 自适应 | 若高码率多声道仍欠载，评估调 clamp 上限而非恢复硬编码 |

## 三、已移除（有意不保留）

| 补丁 | 移除原因 | 重引入条件 |
|---|---|---|
| P3 原生变速 SetSpeed（`7e85c99`：时钟斜率 + Wasapi `speed_` 缩放 + `SpeedChanged` 事件 + 渲染器 speedBits shim） | 内核完整但托管侧 0 绑定（死代码）；与 UI 伪变速（每秒 Seek）语义冲突；每次上游更新白付重移植税。2026-09-13 从 PlayerApi 导出、API 头声明/枚举、PlayerSession、WasapiRenderer、VideoRenderer 全链路移除 | 托管侧正式接线时（导出 `FFF3FP_SetSpeed`、删除 UI 伪变速、加声画漂移测试 ≤100ms），从历史提交 `7e85c99` 整体重移植 |

## 四、保留但非重放义务（逐次评估）

| 项 | 现状 | 评估要点 |
|---|---|---|
| a4a7ab0 FLAC 多线程解码（FF_THREAD_FRAME） | 在净差异中，已与上游光盘 MPEG2 低延迟分支共存 | 上游未采纳说明作者不认为音频解码是瓶颈；仅 6ch 高码率场景有收益。上游再改 OpenDecoder 时重新评估，不机械重放 |
| P2 lock-free Render 快路径（`16f4272`，稳态 try_to_lock 跳过 deviceMutex_） | 在净差异中（VideoRenderer.cpp） | 性能热点优化（消除呈现/解码线程 mutex 对峙）但锁粒度行为最敏感；上游渲染器持续重构时**第一个放弃**。重放后必须回归 HDR/8K |
| c941da3 HDR 元数据去重 + SetMaximumFrameLatency(1→2) | 低风险 | 上游若已做同等优化即弃 |
| patch 0007 zoom viewport cover（b0ff668） | **未重放**：上游 shader 已删除 ViewZoom/ViewPan 常量，UV 空间缩放无处生效；上游回到视口整体放大（4 倍 zoom 时视口达源分辨率×zoom，GPU 负载随 zoom 增长） | 仅当上游重引 shader 常量或出现 4K+ 高倍缩放 GPU 尖峰实测问题时再评估；补丁永久留档于本分支历史 |

## 五、纯增量（随分支走，无重放成本）

- `2fc46e9` 版本资源 `FFF.Native.rc`（FileVersion 主段 = PlayerApiVersion，当前 13；**升内核 API 时同步递增**，供托管 `NativeRuntime.ExtractEmbeddedDll` 版本比较）。
- `11b7f6d` `.gitignore` 忽略 `vcpkg_installed/`。

## 上游更新操作流程

1. `git fetch origin`，对照本文档逐类核对；
2. 类别一 4 项重移植（以 PlayerApi 导出面为完成判据：`FFF3FP_SetPresentConfig` / `SetPacingConfig` / `GetRenderTargetInfo` / `ReadVideoPixelRegion` 可编译可链接）；
3. 类别二 2 项复核上游是否仍覆盖；
4. 类别四逐项决策（默认：FLAC 多线程与 P2 保到不能再保）；
5. MSBuild Release x64 构建 + 托管 `dotnet build` + `3FCompare.Core.Tests` 全绿；
6. 打新归档 tag（`3fcompare-kernel-<上游版本>.<序号>`）。
