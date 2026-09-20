# 38 · 内核单 Presenter 管线修复（4 路崩溃定案）

> **状态**：已实机验证成功（2026-09-20）。改动在 `third_party/fff_project`（嵌套仓，
> 被 `.gitignore` 整体忽略），**尚未提交** ⇒ 已导出留档补丁
> `tools/patches/0010-single-presenter-pipeline.patch`，避免换机/重启后丢失。
>
> 前置阅读：`docs/33 §九`（决定因素是「同时活着的 presenter 管线条数」）、
> `docs/35`（RTSS 对照）、`docs/34`（覆盖层环境复测）。

## 一、问题与根因

### 现象

4 路同播时随机崩溃，AV 落在 `dxgi!Present`，且**钩子自身不在故障栈上**（
`docs/33 §七`：这个 AV 是被"投递"的，不是真实访存）。崩溃率与**路数强相关、与解码负载无关**。

### 根因

每个 `PlayerVideoRenderer` 都自己起一条 `TimedTextThread`，在**自己的**交换链上调
`IDXGISwapChain::Present`。4 路 = 进程内 **4 条并发 presenter 管线**。

RTSS / MSI Afterburner 的覆盖层钩子按**单管线进程**设计，内部用**全局单例状态**记录
"当前 device / swapchain / immediate context"。1 条管线时永不冲突；N 条并发时这些
全局状态被互相覆盖 ⇒ 取到失效指针 ⇒ 崩在 `dxgi!Present`。

### A/B 数据

**修复前**（`docs/33 §九`，同一台机器、`--multitest <4K> 4 30`、位置平衡，
`FC_MULTITEST_ACTIVE` 可"打开 N 路但只让前 M 路播放"）：

| 臂 | 条件 | 崩溃率 | Fisher 双侧 |
|---|---|---|---|
| **A** | 4 路全播（4 device + **4 条** presenter 管线） | **10/10 = 100%** | — |
| **B** | 仍开 4 路（4 device 全在场），**只 2 路**播放 | **3/10 = 30%** | vs A：**p = 0.0031** |
| 单路 | 1 device + 1 条管线 | — | vs B：**p = 1.0000** |

操纵有效性已取证：B 臂自报 `SwapChainPresents 增量 [L0≈760, L1≈760, L2=2, L3=2]`
⇒ 4 device + 4 swapchain 全在场，只是不 Present。
**设备数多寡不可测**（B 与单路 p = 1.0000）；**"Present 是否互斥"也不是变量**
（进程级全局 Present 锁实测 p = 1.0000，`docs/33 §二`）。
⇒ 唯一变量是**同时活跃的 presenter 管线条数**。

**修复后**（RTSS / Afterburner 全程在场，16 次位置平衡）：

| 项 | 修复前 | 修复后 |
|---|---|---|
| 4 路全播崩溃率 | 8/8 = **100%** | **0/8 = 0%** |
| Fisher 双侧 | — | **p = 0.0002** |
| 进程线程数 | 184 | **181**（−3，正好 4 条 presenter 线程降为 1 条） |
| 4 路帧率 | ~60fps/路 | ~60fps/路（无回退） |
| `--selftest`（4K） | EXIT=0 | **EXIT=0** |
| `--comparemodetest 2` | EXIT=0 | **EXIT=0** |
| 导出面 | 82 / API 15 | **82 / API 15**（未变） |

> 注：`docs/33 §九` 当时预测"4→1 后回落到 ~30%（单路基线）"，
> 实测直接落到 **0%** —— 比预测更好，说明单路基线那 30% 里的 Present 侧成分也被一并消掉了。

## 二、改动摘要

改动仅 2 个文件：`FFF.Native/3FP/Render/VideoRenderer.{h,cpp}`（+297 / −102）。

### 1. 进程级 `PresentationPump`（新增，定义在 `VideoRenderer.cpp`）

文件级单例（`PresentationPump::Instance()`，Meyers singleton），**全进程唯一**一条
线程进入 `IDXGISwapChain::Present`。

- `Register` / `Unregister` 维护成员表 `members_`；第一个成员注册时起线程，最后一个
  离开时退役线程。
- 每轮 `Run()` 取成员快照 → 逐个调 `renderer->PumpPresentationOnce()`（返回该路
  **下次想被 pump 的时刻**）→ 取最小 due 时间 `wait_until`。
- **唤醒锁存 `wakePending_`**：`Wake()` 若落在 pump 正在跑批量的时刻，原先会丢
  （那时没人在条件变量上等）⇒ 置 `wakePending_` 让 pump 立刻再跑一轮。
- **世代号 `generation_`**：成员清空后 `++generation_` 退役当前线程。即使注册与
  `join` 竞争，被退役的线程也会停止 pump，而不是与继任者并行 ⇒ 不会出现两个 pump。
- `Unregister` 在 `inFlight_ == 0` 上等待，返回后**保证** pump 不再触碰该渲染器。
- **锁序**：`timedTextMutex_` 可在进入 `Register` / `Unregister` 时被持有；
  pump **从不**在自己 `mutex_` 内调用渲染器 ⇒ 两者不成环。

### 2. 渲染器改为"注册成员"

| 旧 | 新 |
|---|---|
| `TimedTextThread()`（每实例一条线程） | `PumpPresentationOnce()`（由 pump 线程驱动） |
| `StopTimedTextThread()`（锁外 `join`） | `StopPresentationPumpMembership()`（`Unregister` 内部等 `inFlight_`） |
| — | `StartPresentationPumpMembership()`（取代起线程） |
| `PresentTimedText()`（直接 Present） | `PresentTimedText()` 变 **handoff 入口**；`PresentTimedTextOnPump()` 才真 Present |

- 成员删除：`timedTextThread_`、`timedTextCondition_`；新增 `pumpRegistered_`、
  `pumpThreadId_`、`pumpObserved*Generation_`、`pumpNextPresentation_`、
  `pumpNextDevicePoll_`、`pumpHandoff*`。
- 原先活在每实例线程栈上的循环状态（`observedPresentationGeneration` /
  `observedVideoGeneration` / `nextPresentation`）改为渲染器成员 ⇒ 状态不丢。
- 4 处 `timedTextCondition_.notify_one()` → `PresentationPump::Instance().Wake()`
  （`SetWindow` / `SetTimedTextLayer` / `Render` / `Redraw` / `TryRenderCoverBackdropCache`
  / `Set360View` / `ResetMedia`）。
- `class PresentationPump` 在 `VideoRenderer.h` 前置声明 + `friend`，以便 pump 访问
  私有成员。

### 3. 首/末帧投递（handoff）

`PlayerSession.cpp:670` 与 `:2898` 的首/末帧边界在**会话队列线程**上直接调
`videoRenderer_.PresentTimedText()`。修复后该路径改为：

1. 非 pump 线程进入 `PresentTimedText()`：若自己就是 pump 线程 ⇒ 直通
   `PresentTimedTextOnPump()`；
2. 若**未注册**（无 pump 在为本渲染器合成）⇒ 回落到历史直连路径；
3. 否则置 `pumpHandoffPending_`、`Wake()`，在 `pumpHandoffCondition_` 上等待
   （**2s 超时兜底**），实际 Present 由 pump 线程执行，结果经 `pumpHandoffResult_` 回传。

⇒ 会话线程的"同步 Present"语义保留，但 Present 仍只在 pump 线程发生。

## 三、如何重建

> ⚠ **改动已在源码里**，补丁文件只作留档 / 上游更新时的重移植参考，**不是构建步骤**。
> 不要 `git apply`（会重复定义）。

### 1. 构建

本机 `MSBuild.exe` 被安全策略拦截，用复放 tlog 的手工脚本：

```powershell
python tools/build_kernel_manual.py
```

- 命令来源：`FFF.Native/obj/x64/Release/FFF.Native.tlog/{CL,rc,link}.command.1.tlog`
  （MSBuild 上次真实下发的参数，不是猜的）；
- 输出重定向到新 obj / out 目录，**不删既有 obj**：
  - obj：`third_party/fff_project/FFF.Native/obj/x64/Release_pr9/`
  - 产物：`third_party/fff_project/FFF.Native/x64/Release_pr9/FFF.NATIVE.DLL`

### 2. 校验

```powershell
python tools/check_kernel_exports.py "third_party/fff_project/FFF.Native/x64/Release_pr9/FFF.NATIVE.DLL"
```

**期望**：`导出 82 个`、`API version = 15`，且
`FFF3FP_Redraw` / `FFF3FP_SetLogCallback` / `FFF3FP_SetPresentConfig` 三个本地专属导出 = YES
（`FFF3FP_SetPacingConfig` 应为 no —— 已于 2026-09-18 移除）。

**本次专属判据**（导出面不区分新旧，用它区分）：

| 版本 | DLL 内应含字符串 |
|---|---|
| 本次修复后 | `The process presenter could not compose the latest layer.` |
| 修复前（旧） | `The independent timed-text presenter could not compose the latest layer.` |

```bash
grep -c "The process presenter" <dll>              # 期望 1
grep -c "independent timed-text presenter" <dll>   # 期望 0
```

### 3. 部署（⚠ 有坑）

`src/3FCompare/3FCompare.csproj:52` 的内嵌内核路径是

```
third_party\fff_project\FFF.Native\x64\$(KernelConfiguration)\FFF.Native.dll
```

⇒ 要内嵌 `Release_pr9` 产物，**必须**显式传：

```powershell
dotnet build -c Release -p:KernelConfiguration=Release_pr9
```

否则会内嵌 `x64/Release/` 里的**旧 DLL**（见 §五 第 6 条）。

### 4. 回归

```powershell
# 4K 自测
<app> --selftest
# 2 路对比模式
<app> --comparemodetest 2
```

两项均须 EXIT=0。

## 四、残留风险（如实记录）

1. **`ClearSurface()` 的 `Present(0, 0)` 仍走调用线程。**
   `VideoRenderer.cpp:5032` 的 `swapChain_->Present(0, 0)` 不经 pump，调用方是
   `RecreateDeviceResources()`（`:5152`）与 `Close()`（`:5208`）。
   ⇒ "进程内只有一条线程在 Present"这一不变式**有 1 个例外**。触发条件是设备重建 / 关闭，
   实测崩溃样本里未出现，但**未被门控**。

2. **handoff 2s 超时兜底会短暂放开并发。**
   超时后调用线程回落到直连 `PresentTimedTextOnPump()`，此时**理论上可能两条线程同时
   Present**。这是有意的取舍：宁可冒一次并发风险，也不永久卡住会话线程。

3. **`docs/33 §四` 三个未门控点未处理。**
   - **① 门控缺口 —— 仍在**：`PlayerSession.cpp:1180`（`SetViewTransform`）与
     `:1696`（`Redraw`）仍**不持** `timedTextContentMutex_` 直接调
     `videoRenderer_.Redraw()`。修复后该路径不再"起线程"，而是"注册进 pump"，
     **窗口性质相同**（仍可能在 `Close()` 之后把已析构对象注册进 pump）。
   - **② `std::thread` 对象竞争 —— 载体已消失**：per-instance `timedTextThread_` 成员
     与其锁外 `join` 已随重构删除，此类 UB 不再存在（**顺带消除**，非专门修复）。
   - **③ `Redraw` 放锁后启动 presenter 的释放窗口 —— 仍在**：性质同 ①，未加锁门控。

4. **未测场景**：跨监视器 / 多适配器（A11 `preferredAdapterIndex`）组合未验证；
   只在本机单适配器环境跑过。

5. **缺一条对照实验**：修复前 `Pause` 是**管线级**，"管线数"与"线程数"部分耦合
   （`docs/33 §九` 局限）。本次修复恰好把 4 条 presenter 线程收敛为 1 条（线程 184→181），
   两个变量一起动了；没有做"保持 4 条线程但只让 1 条 Present"的对照。

6. **当前部署产物是旧的（务必先重部署再复验）。**
   实测：`src/3FCompare/bin/Release/net11.0-windows/FFF.Native.dll`
   （mtime 2026-09-20 12:53，即修复构建 **之后**）**不含**本次修复
   （`grep "The process presenter"` = 0，且含旧的 `independent timed-text presenter` 字符串）。
   原因是它由未带 `-p:KernelConfiguration=Release_pr9` 的托管构建内嵌了
   `x64/Release/FFF.Native.dll`（2026-09-18 旧产物）。
   ⇒ **§一 的 0/8 结论是在手工换入的修复产物上得到的，不是当前 Release 输出。**
   提交 / 复验前先按 §三 第 3 步重部署。

## 五、归档与版本控制

| 项 | 值 |
|---|---|
| 留档补丁 | `tools/patches/0010-single-presenter-pipeline.patch`（545 行） |
| 涉及文件 | `FFF.Native/3FP/Render/VideoRenderer.cpp`、`VideoRenderer.h` |
| 补丁内容纯度 | **只含本次修复**（见下） |
| 内核仓 | `third_party/fff_project`，分支 `3fc/integrate-issue7`，HEAD `0d5856e` |
| 内核改动状态 | **未提交**（工作区脏） |

### 补丁纯度核验方法

- 嵌套仓 `git status --porcelain -uall` **只有这 2 个文件**被修改，无其它文件、无未跟踪文件；
- 补丁 16 个 hunk 全部围绕 `TimedTextThread` / `timedTextCondition_` /
  `timedTextThread_` → `PresentationPump` / `pump*` 的迁移；
- 机械核验：把所有删除行按"是否含被删符号"过滤后，剩余项全是**被删函数体的结构行**
  （`for (;;) {`、`float frameRate = 60.0f;` 等局部量）；所有新增行按同样方式过滤后，
  剩余项只有 `public:` / `private:` / `const auto presentationChanged =` 三个结构行。
  ⇒ **没有混入与本次修复无关的历史本地修改。**

> 判据说明：本 diff 的基线是 HEAD `0d5856e`，该提交**已包含** `PATCHES.md` 类别一的
> 3 项 + 类别二的 1 项本地补丁。因此"任何非 presenter 相关的 hunk"才可能是历史残留 ——
> 实测不存在。

### `.3fc_kernel_sha` 的处置

`.3fc_kernel_sha` 的语义是「**实际构建出的**内核 SHA」，由 `tools/构建全部.ps1:528`
在**成功路径末尾**写入（不在脚本中途写，避免失败退出后门禁读到假值）；
`tools/发布门禁.ps1:144` 读它与 `$KernelBaselineSha` 比对。

**当前状态是"不足以标识本次构建"**：

- 文件值 = `0d5856edd0465d5b5613dd2447af02d536c0c934` = 嵌套仓 HEAD；
- 但本次构建的产物是在 **HEAD + 未提交改动** 上编出来的
  ⇒ 同一个 SHA 既可能指"修复前"也可能指"修复后"，**裸 SHA 无法表达**。

**处置（不要手写这个文件）**：

1. 先在嵌套仓提交本次修复（需要用户确认后执行，本文件未做任何 git 写操作）；
2. 把新提交 SHA **同时**写入三处：
   - 项目根 `.3fc_kernel_sha`
   - `tools/构建全部.ps1:107-108` 的 `$KernelBaselineTag` / `$KernelBaselineSha`
   - 打归档 tag（`3fcompare-kernel-<上游版本>.<序号>`，见 `PATCHES.md §八` 第 6 步）

> ⚠ 附带发现：`tools/构建全部.ps1:232` 的基线校验只比 `$head -ne $KernelBaselineSha`，
> **区分不了"工作区脏但 HEAD 相同"**。所以"HEAD == 基线"并不等于"产物 == 基线"——
> 本次就是这个情形。若要在脚本层面堵住，需要额外检查
> `git status --porcelain` 是否为空。

## 六、待办

- [ ] 在嵌套仓提交本次修复并打归档 tag，同步 `.3fc_kernel_sha` 与 `$KernelBaselineSha`
- [ ] 用 `-p:KernelConfiguration=Release_pr9` 重部署 Release 产物，再复跑 4 路 A/B
- [ ] `PATCHES.md` 增加"类别一之外"的条目：本次修复性质上是**上游设计缺陷的本地收敛**
      （上游无等价实现），应作为**必须重放**项登记，并写清重移植要点（`PresentationPump`
      全量替换 per-instance presenter 线程）
- [ ] 评估是否处理 §四 的 ①②③（`Redraw` / `SetViewTransform` 门控、
      `ClearSurface` 的 `Present(0,0)`）
- [ ] 评估 `FC_MULTITEST_ACTIVE` 是否作为长期回归工具保留
