# P1 判别实验：4 路崩溃的决定因素是「设备/交换链数量」还是「并发 Present 线程数」

> 时间：2026-09-20 12:04–12:13　状态：**已定论 —— 是并发 Present 的路数（线程数），不是设备/交换链数量**
> 关联：`docs/33`（五个代码侧假设已证伪）、`docs/34`（覆盖层环境复测方案）、`docs/35`（上游 3FP 对照，A-B-A 定案触发者是 RTSS）
> 产物：本目录 `results_ab.csv`（原始逐次）／`analysis.txt`（统计输出）／`validity.txt`（操纵有效性取证）／`logs/`（每次的 stdout+stderr）
> 前置条件：**RTSS + MSI Afterburner 全程运行**（未退出、未干预），每次运行均实测注入 `RTSSHooks64.dll + nvspcap64.dll`

---

## 〇、结论先行

| 臂 | 配置 | 崩溃率 |
|---|---|---|
| **A（对照）** | 4 路**全部播放** = 4 device + 4 swapchain + **4 线程 Present** | **10/10 = 100%** |
| **B（实验）** | 4 路**全部打开**，只让 2 路播 = 4 device + 4 swapchain + **2 线程 Present** | **3/10 = 30%** |

**Fisher 精确检验：双侧 p = 0.0031（单尾 p = 0.0015，方向 A > B）。**

**⇒ 决定因素是「并发 Present 的路数」。** 设备/交换链数量保持 4 个不变、只把 Present 的路数从 4 降到 2，
崩溃率就从 100% 掉到 30%。**光有 4 个 device/swapchain 并不足以复现 90–100% 的崩溃率。**

更强的旁证（同一台机、同一天、同素材、RTSS 运行中，`docs/35` 的基线）：

| 配置 | 崩溃率 | vs B(3/10) 的 Fisher |
|---|---|---|
| 上游 3FP 单路（1 device + 1 Present） | 0/16 = 0.0% | p = 0.0462 |
| 3FC 1 路（1 device + 1 Present） | 5/16 = 31.2% | **p = 1.0000** |
| 3FC 4 路（4 device + 4 Present） | 4/4 = 100% | p = 0.0699 |
| **本实验 A（4 device + 4 Present）** | **10/10 = 100%** | p = 0.0007 |
| **本实验 B（4 device + 2 Present）** | **3/10 = 30.0%** | — |

**B 臂（4 个 device！）与 3FC 单路（1 个 device）的崩溃率统计上无法区分（p = 1.0000）。**
设备数从 1 涨到 4 对崩溃率没有可测影响；Present 路数从 1 涨到 4 才是那把扳机。

---

## 一、实验设计

**要拆开的混杂**：既有数据「2 路 17% / 4 路 90%」里，**设备数**与**并发 Present 线程数**同向增长，无法归因。

**做法**：保持**打开的路数不变**（设备/交换链数量因此不变），只改**真正在 Present 的路数**。

| 臂 | 打开的 device/swapchain | 并发 Present 线程 | 环境变量 |
|---|---|---|---|
| A | 4 | 4 | （不设） |
| B | **4（不变）** | **2** | `FC_MULTITEST_ACTIVE=2` |

- 命令：`3FCompare.exe --multitest "testmedia/media/real/real_4k_h264_60m.mp4" 4 30`
- 同批次交错：每轮各跑 1 次 A + 1 次 B，**共 10 轮（A 10 次 + B 10 次）**；
  次序按轮次交替（奇数轮 A→B，偶数轮 B→A）以平衡次序效应。
- 崩溃判定：退出码落在 `0xC0000xxx` 判 `CRASH`；`exit=1` 判 `ASSERT`（断言失败，**不是崩溃**，单独统计）；
  `exit=0` 判 `OK`；到点未退出判 `HANG`。
- 单次间隔 3s，30s/次。**全程 RTSS / Afterburner 保持运行。**

### 操纵有效性（关键前提，已取证）

`validity.txt` 给出三条独立证据：

1. **B 臂自报的 `SwapChainPresents` 增量**：`L0≈760 L1≈760 L2=2 L3=2`
   ⇒ 被 Pause 的 L2/L3 在 ~18s 的测试体里实质**零 Present**；且它们能 Present 那 2 次，
   说明**swapchain 确实已创建**（"4 device + 4 swapchain 都在场"这一前提成立）。
2. **应用内 `[Sync] Heartbeat`**：`routes=4`，L0/L1 = `Playing/dp>0`，L2/L3 = `Paused/dp=0/swap=0`，每秒一条、形态一致。
3. **无第二个实例并发**：12:11:48–12:12:03 期间组件日志只有一个会话头，Heartbeat 无交错。

---

## 二、逐次结果（20 次）

| 轮 | 位 | 臂 | 判读 | 退出码 | 耗时 s | 备注 |
|---|---|---|---|---|---|---|
| 1 | 1 | A | **CRASH** | — | 18.3 | 退出码未捕获(harness bug)；日志判据：漂移断言失败后死于收尾 |
| 1 | 2 | B | OK | 0 | 19.3 | 退出码取自日志 `准备退出 code=0` |
| 2 | 1 | B | OK | 0 | 18.8 | |
| 2 | 2 | A | **CRASH** | -1073741795 (0xC000001D) | 10.3 | |
| 3 | 1 | A | **CRASH** | -1073741819 (0xC0000005) | 11.2 | |
| 3 | 2 | B | OK | 0 | 18.7 | |
| 4 | 1 | B | OK | 0 | 18.8 | |
| 4 | 2 | A | **CRASH** | -1073741819 | 13.2 | |
| 5 | 1 | A | **CRASH** | -1073741819 | 11.2 | |
| 5 | 2 | B | **CRASH** | -1073741795 (0xC000001D) | 7.6 | Pause 后 1.5s 等待内即崩 |
| 6 | 1 | B | OK | 0 | 18.8 | |
| 6 | 2 | A | **CRASH** | -1073741819 | 11.1 | |
| 7 | 1 | A | **CRASH** | -1073741819 | 8.7 | |
| 7 | 2 | B | ASSERT | **1** | 14.5 | 断言失败但正常退出，**非崩溃** |
| 8 | 1 | B | OK | 0 | 18.6 | |
| 8 | 2 | A | **CRASH** | -1073741819 | 16.7 | |
| 9 | 1 | A | **CRASH** | -1073741819 | 12.1 | |
| 9 | 2 | B | **CRASH** | -1073741819 | 9.6 | 崩于静置播放 |
| 10 | 1 | B | **CRASH** | -1073741819 | 19.7 | 崩于并行扇出 |
| 10 | 2 | A | **CRASH** | -1073741819 | 9.3 | |

**汇总**

```
A 臂: 崩 10/10 = 100.0%   OK=0  ASSERT=0  OTHER=0  HANG=0
B 臂: 崩  3/10 =  30.0%   OK=6  ASSERT=1  OTHER=0  HANG=0

Fisher 精确检验  A(10/10) vs B(3/10):  双侧 p = 0.0031   单尾 p = 0.0015（方向：A 高于 B）
```

**崩溃签名**：A 臂 9/10 为 `0xC0000005`（读/写访问违例），1/10 为 `0xC000001D`（`STATUS_ILLEGAL_INSTRUCTION`）；
B 臂 2/10 为 `0xC0000005`、1/10 为 `0xC000001D`。与 `docs/33 §三` 记录的"异常系被投递、
execute 访问违例（间接调用打到野地址）"一致 —— 两臂是**同一个缺陷**，只是发生率不同。

**耗时旁证**：A 臂平均存活 12.2s（最短 8.7s），B 臂判 OK 的 6 次都跑满 ~18.8s。
⇒ A 臂不是"更快崩"，而是"根本走不完测试体"。

---

## 三、稳健性检查

| 检查 | 结果 |
|---|---|
| 剔除第 9 轮（该轮曾观察到一次无法解释的瞬时进程快照） | A 9/9 vs B 2/9，Fisher 双侧 **p = 0.0023**，结论不变 |
| 把 `ASSERT` 也算作"非 OK"（最保守口径） | A 10/10 vs B 4/10，Fisher 双侧 **p = 0.0108**，结论不变 |
| 次序平衡（A→B / B→A 交替） | A 崩在 pos1 与 pos2 上均出现；B 的 3 次崩溃中 2 次在 pos1、1 次在 pos2 ⇒ 无位置偏倚 |
| RTSS 在场 | 20/20 次均实测到 `RTSSHooks64.dll + nvspcap64.dll` 注入，`rtssUp=True` |

---

## 四、回答：是设备/交换链数量，还是并发 Present 线程数？

**是并发 Present 的路数（线程数）。**

- 设备/交换链数量在 B 臂**完全没有减少**（4 个），崩溃率却从 100% 掉到 30%（p = 0.0031）。
  ⇒ **"每设备状态被 RTSS 损坏、即便不 Present 也崩"这一假说被证伪**——
  4 个 device/swapchain 静静待着（`Paused`、零 Present、零 Present 调用）并不会把崩溃率推到 90%+。
- 反过来，B 臂（4 device + 2 Present）与 3FC 单路（1 device + 1 Present）的崩溃率
  **统计上完全相同（p = 1.0000）**。⇒ 一旦并发 Present 的路数降下来，
  设备数多寡就退化成不可测的次要因素。

**与既有实验的拼接**：

| 实验 | 操纵 | 结果 | 含义 |
|---|---|---|---|
| `docs/33 §二` | 4 路，Present **串行化**（进程级全局锁），线程数仍 4 | 无改善（5/6 vs 5/6，p = 1.00） | "互斥"没用 |
| **本实验** | 4 路，Present 路数 **4 → 2**，device 数不变 | 100% → 30%（p = 0.0031） | "数量"有用 |

⇒ 关键变量是**同时活着的 presenter 管线条数**（剂量-反应关系），而不是"Present 是否互斥"。
这与 `docs/33 §四` 的形态判断（悬垂指针 / 并发析构、execute 违例）**方向一致**：
4 条 presenter 线程 = 4 份"启动 / 停止 / 析构"的并发窗口，路数越多，踩中窗口的概率越高。

**注意**：本实验排除掉的严格说是"该路整条活的呈现管线"（Pause 同时停掉该路的解码/渲染线程，
A 臂 `thr≈193` vs B 臂 `thr≈146`，差 ≈47 线程 ≈23/路）。Present 是崩溃现场所在的那一步，
但"只停 Present、解码照跑"的分离实验本仓库暂无开关可做。见 §五局限。

---

## 五、局限（如实列出）

1. **Pause 是"整条管线"级的，不是"仅 Present"级的**。共变项是线程数（193 → 146）。
   若要严格分离，需要一个"解码照跑、只不 Present"的模式；内核导出面（82 个 / API=15）当前无此能力。
   不过 `docs/33 §一` 已独立证伪"解码负载"（720p vs 4K，p = 1.00），
   说明**管线里的负载量不是变量**，与本文结论相容。
2. **B 臂仍有 30% 的崩溃率**，高于上游 3FP 的 0%（p = 0.0462）。
   ⇒ 即使降到 2 条 presenter 管线，3FC 仍比普通软件脆弱。
   这与"3FC 单路 31%"同量级 ⇒ 存在一个**与路数无关的基线风险**，是另一个待查问题（非本实验范围）。
3. 单机、单素材、单 GPU、单日。n = 10/臂。
4. 第 1 轮的退出码未捕获（harness 缺陷：`Start-Process -PassThru` 在长时运行 + 重定向下
   `ExitCode` 返回 `$null`，而 PowerShell 里 `$null -lt 0` 为 `True`，会把一切误判为 CRASH）。
   已改为 .NET `Process` + `ReadToEndAsync` 并从第 2 轮起生效；第 1 轮改用日志判据，
   原始记录留档在 `results_ab_round1_raw.csv`。

---

## 六、基于此，「让 3FCompare 更像普通软件」的可行性建议

结论把改造方向从"**减少设备**"改成了"**减少并发的 presenter 管线**"。按性价比排序：

### ① 共享单一 D3D11 device —— **优先级下调，不是主攻方向**
本实验证明"4 个 device 静态存在"本身无害（B 臂 4 个 device 只有 30% 崩溃率，
与 1 个 device 的 31% 无差异）。共享 device 的收益是**显存/句柄**，不是崩溃率。
**值得做，但不要指望它修 P1。**

### ② 减少同时 Present 的管线数 —— **直击剂量-反应关系，主攻方向**
把「N 路各自独立 Present」改成「**N 路共用一条 presenter**」：各路解码照旧，
但把待显示帧汇到同一个呈现器，由**一条**线程统一 Present（或统一合成到一张大 swapchain 上再 Present 一次）。
本实验给出的预期收益：presenter 管线 4 → 1，崩溃率有望从 100% 回落到**基线量级（~30%）**。
`docs/26` 已有的"比例式对比"布局恰好是这条路的天然形态（一次合成、一次 Present）。
> 注意 `docs/33 §二` 的教训：**只是加锁串行化没用**，必须真的把线程数降下来。

### ③ 消除 presenter 的"启动/停止/析构"并发窗口 —— **低风险、可立刻做**
`docs/33 §四` 定位的三个未门控点（`SetViewTransform:1180` / `Redraw:1696` 不持
`timedTextContentMutex_` 直接调 `Redraw`，而 `Redraw` 会**启动** presenter；
`StopTimedTextThread` 在锁外 `joinable()/join()`）是"路数线性放大"的机制本体。
按"路数无关"地修好这些，才能把 B 臂那 30% 的基线风险也压下去。

### ④ 短期可落地的产品侧缓解
- 检测到 `RTSSHooks64.dll` 注入时，**默认把并发播放路数上限降到 2**（应用已有 `HookNoticeShown` 钩子检测，
  见组件日志 `[Env] HookDetected`），并在 UI 明示；
- 或提供一个"低 Present 并发"渲染模式（多路先合成、单次 Present）。

---

## 七、复现方式

```bash
# A 臂（4 路全播）
3FCompare.exe --multitest "testmedia/media/real/real_4k_h264_60m.mp4" 4 30

# B 臂（4 路打开、只播 2 路）—— PowerShell
$env:FC_MULTITEST_ACTIVE = '2'
3FCompare.exe --multitest "testmedia/media/real/real_4k_h264_60m.mp4" 4 30
```

```powershell
# 本次实验的驱动与统计
powershell -File .review_pr/device_vs_thread/run_ab.ps1 -StartRound 1 -EndRound 10 -Fresh
python .review_pr/device_vs_thread/analyze.py
```

**开关实现（仅测试代码）**：`src/3FCompare/MainWindow.SelfTest.cs`
- 新增字段 `_multitestActiveRoutes`（默认 `int.MaxValue` = 全部）；
- `RunMultitestAsync` 读环境变量 `FC_MULTITEST_ACTIVE`（= 实际播放的路数，钳制到 `[1, routes]`，默认全部）；
- `_sync.Play()` 之后立刻 `Pause()` 掉 `index >= ACTIVE` 的路（设备/交换链保留，不再 Present）；
- `CheckDrift` / `CheckPresentedGrowthAsync` 跳过 `index >= ACTIVE` 的路，
  避免被暂停的路因"位置不推进 / presented 不增长"触发 exit=1 而污染判读；
- **未改任何生产代码**；未新增自测模式（沿用 `--multitest`）。
