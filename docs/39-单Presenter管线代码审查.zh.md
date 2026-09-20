# 39 · 单 Presenter 管线改造 —— 代码审查

> 审查对象：`tools/patches/0010-single-presenter-pipeline.patch`（+297/−102，2 文件）
> 审查时间：2026-09-20　结论：**设计正确、实现克制，可以保留**；但有 1 项中等风险与若干待补项。

---

## 一、总体评价

把 N 条 presenter 管线收敛为进程级 `PresentationPump` 是一条**正确的架构收敛**：
- 崩溃率 8/8 = 100% → 0/8 = 0%（Fisher p = 0.0002），且 4 路仍各自 ~60fps；
- 关键机制都用对了：**generation 令牌**防止退役线程与替代线程并存、**inFlight 计数**保证 `Unregister` 返回后泵绝不再触碰该实例、**锁序**（`timedTextMutex_` → 泵 `mutex_`，泵进入 renderer 前已释放自身锁）无环；
- 注释充分，把"为什么这么做"写进了代码（覆盖层钩子假设单管线）。

## 二、已核实「不是问题」的几项（避免误报）

| 担心 | 结论 |
|---|---|
| 静态析构时 `members_` 悬垂 | **不会**：`~PlayerVideoRenderer()` 调 `Close()`，`Close()` 调 `StopPresentationPumpMembership()` 先注销，注销会等待在途回合结束 |
| `pumpNextPresentation_` 为 `min()` 时返回 `now+1ms` 造成忙轮询 | **不可达**：`timedTextThreadRunning_` 只在 `StopPresentationPumpMembership` **末尾**（注销之后）才置 false；已注册 ⇒ running 为真 ⇒ 首帧必然 present |
| 注销后泵仍访问该 renderer | **不会**：先从 `members_` 移除，再 `idle_.wait(inFlight_ == 0)`；批量复制与 `inFlight_` 赋值在同一临界区内 |
| 锁序死锁 | **无环**：泵在调用 renderer 前已释放 `mutex_`；`Unregister` 不持 `timedTextMutex_`；且三个 `Stop` 调用点（`RecreateDeviceResources` / `ResetMedia` / `Close`）**都在取 `deviceMutex_` 之前**，注释也明确警示 |

## 三、问题清单（按严重度）

### 🟠 1. 故障隔离性下降（本次改造的固有代价）

此前每路一条 presenter 线程，**一路 Present 卡住只冻这一路**；现在**一条泵线程驱动所有路**，
任意一路的 `Present` 卡住（TDR / 驱动挂起 / 设备恢复）⇒ **全部路停止呈现**。

- 本次测试未出现（16 次主实验 + 3 次冒烟全过），但测试环境是"健康态"。
- **建议**：给 `PumpPresentationOnce` 加**每路耗时观测**（超过阈值记日志/计数），这样真出问题时能一眼定位是哪一路拖住了泵。这是低成本高收益的补强。

### 🟠 2. 跨显示器 / 多适配器场景未验证（潜在性能退化）

串行 `Present(1,0)` 在**同一显示器**下实测无 4× 惩罚（各路 ~60fps，可能落在同一 vblank 窗口）；
但若各路分布在不同显示器 / 不同适配器，**vblank 相位不同**，串行 Present 可能每路都等一帧 ⇒ **4 路降到 ~15fps**。

- 本机为 RTX 5080 + 4060 双卡，属"可能命中"的配置。
- **建议**：补测"各路分布在不同显示器"的场景，比对各路 `dp`（Δpresented）。

### 🟡 3. 「进程内只有一条线程在 Present」并非绝对

仍有两处例外：
1. `ClearSurface()` 的 `Present(0,0)` **仍走调用线程**（媒体复位路径）；
2. `PresentTimedText` 的 handoff **2 秒超时兜底**会降级为直接 Present。

若覆盖层钩子对"偶发出现第二条 Present 管线"同样敏感，这两处就是残留的低概率崩溃源（B 臂 0/8 未触及）。
**建议**：至少给这两处加**计数器/日志**，便于将来真出现偶发崩溃时判断是否与它们相关。

### 🟡 4. handoff 存在多调用者竞态

两个线程同时调 `PresentTimedText` ⇒ 都置 `pumpHandoffPending_`，泵**只 present 一次**并把 `pumpHandoffDone_` 置真 ⇒ 两者都返回同一个结果，第二个调用者的"这一帧"实际没被呈现。
当前调用点（`PlayerSession.cpp:670` 首帧 / `:2898` 末帧）是串行的，影响有限；但这是**隐式契约**，未写进注释。
**建议**：要么加注释说明"同一时刻只允许一个 handoff 调用者"，要么改成请求计数。

### 🟢 5. 一处语义变化，需确认是否有意

旧代码在 `!timedTextThreadRunning_` 时会**吸收** `presentationGeneration_` / `videoGeneration_`（避免恢复播放时突发补帧）；
新代码在 `present == false` 时**直接返回、不更新**观察到的 generation ⇒ **恢复时会多出一次 present**。
影响很小（多一帧），但属行为变化，建议在注释里写明是刻意为之。

### 🟢 6. 每轮 `batch = members_` 有分配

泵每轮把成员表复制一份（最高 ~60 次/秒的小分配）。可用成员级缓冲复用消除。属于性能 nit，不紧急。

### 🟢 7. `Unregister` 可能被其它路的 Present 拖住

`Unregister` 等 `inFlight_ == 0`（整个批量结束）。若此时另一路正在 Present 且耗时较长，本次 `Close()`/`ResetMedia()` 的调用线程会被拖住同等时间。
**建议**：无需改（正确性优先），但心里有数——真出现"关闭变慢"时知道原因在这里。

## 四、建议的后续动作（按性价比）

1. **加每路耗时观测**（应对问题 1）—— 成本最低，价值最高。
2. **补测跨显示器/多适配器**（问题 2）—— 本机是双卡，值得测一次。
3. **给两处"不变式例外"加计数/日志**（问题 3）—— 便于未来归因偶发崩溃。
4. 问题 4/5 补注释即可；6/7 可暂缓。

## 五、结论

**建议保留该改动**。它把一个"90%~100% 必崩"的问题降到 0%，且没有引入已观测到的功能退化。
上述问题是**改造的固有代价与未覆盖场景**，不是实现缺陷；按其性价比排序逐项补强即可，
不应因此回退这次修复。
