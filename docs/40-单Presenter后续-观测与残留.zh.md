# 40 · 单 Presenter 后续：可观测性补丁与残留崩溃

> 时间：2026-09-20　关联：`docs/38`（修复本体）、`docs/39`（代码审查）
> ⚠ **本文修正 `docs/38` 的一个结论**：修复后崩溃率**不是 0%，而是约 10%~15%**。

---

## 一、补丁 0011：presenter 可观测性

`tools/patches/0011-present-observability.patch`（嵌套仓 `c650336`）

| 项 | 实现 |
|---|---|
| 每路 Present 耗时 | `PumpPresentationOnce()` 内对 `PresentTimedTextOnPump()` 计时，交 `NotePumpPresentDuration()` |
| 阈值 | **100 ms**（≈60Hz 的 6 帧；健康 Present 受 vblank 节流，远小于一帧周期） |
| 抑制 | 同一路 **10 秒冷却**（`ObservabilityLogAllowed()`）；**计数与高水位无条件累加，只有日志被抑制** |
| 日志通道 | 既有 `FFF3FP_KernelLogImpl`（与 `FFF3FP_SetLogCallback` 同一 sink），**未新增导出** |
| 两处例外计数 | `offPumpClearSurfacePresents_`（`ClearSurface` 的非泵 Present）、`handoffTimeoutFallbacks_`（2s 超时降级） |

验收：10 个会话共 3135 行日志，观测输出仅 **37 行**；单次 30 秒冒烟最多 6 行（≤0.2 行/秒）⇒ **未刷屏**。

## 二、🔴 修正：残留崩溃（推翻"0/8"结论）

加观测后复测 **8 次**：**6 通过 / 1 崩溃 / 1 漂移断言失败**。

同批次对照（新内核 vs `c84ec2f` 修复产物）⇒ **两臂失败点与失败模式完全相同** ⇒ 不是观测引入，是**修复本身的残留**。

⇒ **修正结论：单 presenter 把 4 路崩溃从 ~100% 降到约 12.5%，未降到 0。**
（`docs/38` 的 0/8 是当时窗口内的实测，样本不足以代表长期水平。）

## 三、补丁 0012：排除非泵 Present（未达预期）

`tools/patches/0012-exclude-offpump-presents.patch`（嵌套仓 `b765a1f`）

观测曾指认 `ClearSurface()` 的非泵 Present（8 次里触发 4 次）为嫌疑，故把它排除：

- **选了方案 B（无条件跳过该 Present），而非 A（搬到泵）**。理由：`ResetMedia()` / `Close()` 都在取 `deviceMutex_` **之前**调 `StopPresentationPumpMembership()`，`ClearSurface()` 执行时该 renderer **已不是泵成员**，之后没有任何泵回合会为它合成 ⇒ A 要么把正在拆除的 renderer 重新注册，要么新增泵侧一次性 API 与新的锁序边。
- B 的代价可接受：两处调用点都是"媒体要走了"，且任何合成路径都先整屏重绘背缓冲（`DrawCachedVideo` 先 Clear），旧像素不会漏进后续帧。
- handoff 2 秒超时**保留有界等待**但**不再直连 Present**，改为返回 `DeviceFailure`（两个调用点本就有 DeviceFailure → 请求设备恢复）。

**验证结果（不如预期）**

| 项 | 结果 |
|---|---|
| `off-pump Present` 日志 | **0 次**（已从 4 次归零；改为 `pump-owned Present excluded` 68 次） |
| 20 次 4 路（5 轮 × 2 进程，位置平衡） | **崩溃 3/20 = 15%**，漂移断言失败 3/20 |
| 与改前（1/16）比较 | Fisher **p ≈ 0.6** ⇒ 既无改善证据，也无回归证据 |
| 关键反证 | **3 次崩溃的 `excl` 计数均为 0** ⇒ 本次改动的两条路径**一次都没执行**，残留崩因**不在** `ClearSurface` / handoff 超时 |

⚠ 另有 1 次崩溃前出现一条 **6567 ms 的泵内 Present**（其余最大 553 ms）—— 这正是 `docs/39 §3.1` 预言的**"泵内故障不隔离"**：单泵线程下，一路 Present 长时间阻塞会拖住全进程。

## 四、当前状态与下一步

**修复保留**（100% → ~12% 是量级改善，回归全绿：selftest / comparemodetest / Core 492 / Platform 248 均过）。

**下一步候选（按性价比）**

1. **给泵内 Present 加看门狗**：把崩溃前最后一条 slow Present（含耗时、路号、generation）**强制落盘**，这样再崩溃时能直接看到"是不是卡在某路的 Present 里"。
2. **做 `docs/38 §四-5` 的对照实验**："保持 4 条线程但只让 1 条 Present"，把"管线数"与"线程数"彻底分离（此前 `Pause` 是管线级，两个变量部分耦合）。
3. 同批次 A/B 复测（修复前 vs 修复后，当前时点），确认改善幅度是否仍显著——因为 `docs/38` 的 0/8 与本文的 3/20 存在跨批次差异。

## 五、教训

**"0/8"不能当作"已修复"。** 8 次样本只能把 ~100% 降到"个位数百分比"这个量级，
**证明不了 0**。我在 `docs/38` 里据 0/8 下了结论，被后续 20 次样本推翻。
⇒ 判定"已修复"需要**远多于证明"有差异"**的样本量；否则应表述为"降至 X%"。
