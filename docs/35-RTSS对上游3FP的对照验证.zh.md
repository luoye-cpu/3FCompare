# 35 · RTSS / Afterburner 是否也导致上游 3FP（FFF.Player）崩溃 —— 与 3FC 的对照验证

> 时间：2026-09-20 10:35–10:54　状态：**A 臂（RTSS 运行中）已完成并定论**
> 关联：`docs/33` §八（3FC 侧已定案：触发者是 RTSS 钩子）、`docs/32`（系统蓝屏，与进程崩溃是两回事）
> 实验脚本：`.review_pr/rtss_3fp/run_arm.ps1`（驱动）／`fisher.py`（统计）／`results_*.csv`（原始逐次记录）

---

## 〇、结论先行

| 被测对象 | 崩溃率（RTSS 运行中） | 与 3FP 的 Fisher 双侧 |
|---|---|---|
| **上游 3FP**（FFF.Player，单路 4K） | **0 / 16 = 0.0%** | — |
| **3FC 1 路** | **5 / 16 = 31.2%** | **p = 0.0434** |
| **3FC 4 路**（阳性对照） | **4 / 4 = 100%** | **p = 0.0002** |

**三条结论：**

1. **上游 3FP 同样被 `RTSSHooks64.dll` 注入，但 16 次零崩溃。**
   ⇒ 这不是「上游内核的通用缺陷」。P1 改判为外部注入冲突时不必向上游报 bug，也不必指望上游修复。
2. **3FC 不是「多路才崩」——单路崩溃率 31%。** 路数是**放大因子**而非必要条件
   （1 路 31% → 4 路 100%，Fisher p = 0.0260）。
3. ⇒ 崩溃需要「RTSS 钩子」与「3FC 侧某个随路数放大的条件」**共同**作用。
   上游不崩 ⇒ 那个条件在 3FC 侧，不在内核的 Present 调用本身。

---

## 一、实验设计

### 被测配置

| 代号 | 程序 | 参数 | 是否自动退出 |
|---|---|---|---|
| `FP1` | `third_party/fff_project/FFF.Player/bin/Release/…/FFF.Player.exe` | 命令行传 4K 素材，打开即自动播放 | 否（到点外部强杀） |
| `FC1` | `src/3FCompare/bin/Release/net11.0-windows/3FCompare.exe` | `--multitest <4K> 1 30` | 是 |
| `FC4` | 同上 | `--multitest <4K> 4 30` | 是 |

统一：`real_4k_h264_60m.mp4`（211 MB，真实素材）、单次 30 秒、间隔 3 秒。
两侧共用**同一份内核** `FFF.Native.dll`（播放器 bin 内已确认 **API version = 15**，与 3FC 一致）。

### 次序控制（必须，否则会得到假显著）

按 `docs/33 §六` 的教训做**拉丁方位置平衡**：每轮换序（`FP1→FC1→FC4` / `FC1→FC4→FP1` / `FC4→FP1→FC1`），
保证每个配置出现在 pos1/pos2/pos3 的次数相等。

### 崩溃判定

退出码落在 `0xC0000xxx` 区间判为 `CRASH`（实测全部为 `-1073741819` = `0xC0000005`）；
`0` 判 `OK`；到点未退出且本应自动退出的判 `HANG`。

---

## 二、逐次结果

```
FP1 : 16 次全部跑满 30.3~30.4 s，CPU 46~48 s（≈1.55 核持续工作），无一次崩溃
FC1 : 崩 5 次，崩溃发生在 8.1 / 8.1 / 12.2 / 14.2 / 21.3 s；其余 11 次 exit=0 正常播完
FC4 : 崩 4 次，崩溃发生在 9.1 / 11.2 / 12.2 / 19.2 s
```

每次运行均记录到该进程的注入模块，三个配置**逐次一致**：`RTSSHooks64.dll + nvspcap64.dll`。
`rtssUp` 字段全程 `True`。

---

## 三、有效性核查（防止假阴性——本次最关键的部分）

「3FP 没崩」只有在「3FP 确实在渲染」的前提下才成立。四条独立证据：

| 证据 | 结果 |
|---|---|
| ① 注入对称 | `RTSSHooks64.dll` + `nvspcap64.dll` **逐次**注入 3FP 与 3FC；RTSS 的 `Profiles\` 下只有 `DJI Studio.exe.cfg` ⇒ **双方均无排除项**，条件对称 |
| ② 确实在渲染 | 3FP 进程 GPU `engtype_3d` 利用率 **29.83%**；CPU 46–48 s / 30 s；已加载 `dxgi.dll`/`d3d11.dll`/`FFF.Native.dll` |
| ③ 渲染链路可用 | `FFF.Player.Tests.exe --shutdown-child <4K> playing` × 3，退出码全 0，输出 `playing=True`（内部断言已呈现帧数 ≥ 15） |
| ④ 系统侧零漏检 | 实验窗口内 Windows `Application` 日志：**FFF.Player 崩溃事件 0 条**；无残留进程。同期 3FC 崩溃记录全部为 `P1: 3FCompare.exe / P4: dxgi.dll / 异常 c0000005 / 偏移 0x19530`，与 `docs/33 §七` 取证**逐字吻合** |

> ④ 同时排除了「3FP 崩溃后弹托管异常对话框挂住 ⇒ 被脚本误判为 OK」这一风险：
> 若发生，必然产生 `.NET Runtime 1026` / `Application Error 1000` 事件，实测为零。

---

## 四、一个被排除的机制假设（记录，勿重复投入）

发现 3FC 对每个会话无条件调用 `session.SetPresentConfig(true)`（`Fff3FpEngine.cs:70`），
而上游 3FP **从不调用**该本地专属导出 ⇒ 一度怀疑 tearing 是两侧差异。

**核实后排除**：`VideoRenderer.cpp:4751-4754` 中 `swapAllowTearing_` **只影响交互拖动时的 Present**，
正常播放两侧**都走完全相同的 `chain->Present(1, 0)`**；
且交换链的 `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING` 由内核按适配器能力统一设置（`:2231`），两侧一致。
⇒ **tearing 不是差异点**。

---

## 五、与 3FC 既有结论的关系

| 项目 | `docs/33 §八`（3FC 4 路） | 本次 |
|---|---|---|
| RTSS 在运行 | 18/20 = 90% | FC4 4/4 = 100%（复现，且偏移 `0x19530` 一致） |
| RTSS 已退出 | 0/20 = 0% | **本次未做**（见 §六） |
| 上游 3FP | 从未测过 | **0/16 = 0%** |

⇒ 本次把「只有 3FC 崩」从推断变成了实测，并把「多路才崩」这一误解纠正为「单路也崩、路数放大」。

---

## 五之二、⚠ 本节结论被误读过（2026-09-21 补记）

上游结论（"不是上游内核的锅"）被后续记忆简写成 **"P1 4 路崩溃已定案：触发者是 RTSS"**，
并在发布话术里被当作"已解决"，给出的建议是**让用户把 RTSS 设为 None**。
**这是不充分的，且把兼容性成本转嫁给了用户** —— 一个要发布的软件不能要求用户卸载
硬件监控工具（RTSS/MSI Afterburner 是显卡用户的常驻软件）。

### 已确定的部分

- RTSS 钩子是**触发者**（A-B-A：90% → 0% → 91.7%，p=0.0001）。
- 故障模块是 **`dxgi.dll`（偏移 `0x19530`）**，**不是 3FC 自己的代码** ⇒ 崩在被 hook 改过的
  DXGI Present 路径上，而不是 3FC 的业务逻辑。
- tearing 已排除（两侧正常播放同为 `Present(1,0)`）。

### 尚未确定的部分（真正的缺口）

**3FC 侧那个"使之可被触发"的条件至今没有定位。** 上游 0/16 与 3FC 5/16 的对照说明
差异在 3FC 侧，但我们只做了"排除上游"，没有做"定位差异"。
⇒ 在定位之前，"请关闭 RTSS" 只能是**临时规避**，不能作为发布结论。

### 候选差异（按可能性排序，均需实测）

| # | 候选 | 依据 | 备注 |
|---|---|---|---|
| 1 | **输出窗口是子 HWND** | 3FC 每路把 `NativeControlHost` 的子窗口句柄作为 `OutputWindow`（`PlaybackCoordinator.cs:614`）；上游用顶层窗口。父窗口还带 `WS_CLIPCHILDREN`（`MainWindow.axaml.cs:2421`） | RTSS hook Present 后需定位绘制目标窗口；子窗口 + `WS_CLIPCHILDREN` 是易错组合 |
| 2 | **窗口区域裁剪 `SetWindowRgn`** | `MainWindow.axaml.cs:643` 自述 "`SetWindowRgn` 与内核 D3D11 flip-model swapchain 的交互" | 叠加/对比模式才启用；单路也崩 31% ⇒ 不是单路的必要条件，可能是叠加模式的额外放大项 |
| 3 | **多路 = 多 swapchain / 多设备** | 1 路 31% → 4 路 100%（p=0.0260） | 是放大因子，不是必要条件 |
| — | tearing | 已排除，勿重复投入 | §四 |

### 建议的判别实验（最小代价，把差异收敛到一个变量）

- **对照 A**：让 3FC **单路**改用**顶层窗口句柄**输出（临时绕过子 HWND），RTSS 保持运行，跑 16 次。
  若崩溃率归零 ⇒ 差异点就是输出窗口形态，**且这是 3FC 侧可以自己改的**，用户什么都不用做。
- **对照 B**：若上游 3FP 支持传入自定义窗口句柄，让它也用子窗口输出。若开始崩 ⇒ 同方向印证。

判据沿用 §一：拉丁方位置平衡、每臂 ≥16 次、逐次记录注入模块与 DLL sha256、crash 与 drift 分列。

---

## 六、尚未完成：B 臂（RTSS 退出后）

**A 臂已足以回答「上游是否受影响」（否）。** 剩余增量问题只有一个：
**3FC 单路那 31% 是否同样由 RTSS 引起**（即退出 RTSS 后 1 路是否归零）。
若归零，则「RTSS 是必要条件」在 1 路场景也成立，可写进发布说明的运行建议。

需用户手动退出 RTSS 与 MSI Afterburner 后执行：

```powershell
& "C:\PLAN\3FCompare\.review_pr\rtss_3fp\run_arm.ps1" -Label "B" -Rounds 8 -Only @('FP1','FC1')
```

（脚本自带 `rtssUp` 字段，会自动校验该臂期间 RTSS 确实未运行，防止条件串味。）

---

## 七、深度分析：差异点到底在哪（2026-09-21 11:10）

> 起因：用户质疑"别的软件覆盖层怎么没事""难道要我电脑只装 3FC"。
> ⇒ 本节做两件事：① **推翻** §五之二 里的候选 #1；② **发现对照实验本身的未受控变量**。

### 7.1 ❌ 候选 #1（子 HWND）不成立 —— 前提就是错的

我此前假设"3FC 传子 HWND、上游传顶层窗口"，**实测证伪**：

- 上游 `FFF.Player/UI/播放器画面控件.vb`：`视频输出窗口` 是一个 `Panel`，
  经 `Controls.Add(视频输出窗口)` 挂进父控件，`输出窗口句柄 => 视频输出窗口.Handle`
  ⇒ **同样是子 HWND**（WinForms 控件句柄）。
- 内核侧也印证：`VideoRenderer.cpp:2348-2349` 用 `GetClientRect(window_)` **覆盖**入参宽高，
  其余 swapchain 参数（`FLIP_DISCARD` / `BufferCount=2` / `AlphaMode=IGNORE` /
  `Scaling=NONE` / `Flags=ALLOW_TEARING`）**与 HWND 形态无关**。

⇒ 窗口"子 vs 顶层"不是差异点，**此前建议的"改顶层窗口句柄"实验应当作废**。

### 7.2 🔴 对照实验本身有未受控变量：两侧内核**不是同一份**

`docs/35 §一` 写"两侧共用同一份内核"，**实测不成立**（API version 同为 15，但二进制不同）：

| | 路径 | SHA256 | 大小 | 日期 | 导出数 |
|---|---|---|---|---|---|
| 上游 bin | `FFF.Player/bin/…/FFF.Native.dll` | `79cb457e…` | 756,736 B | 09-18 | **79** |
| 3FC | `FFF.Native/x64/Release/FFF.Native.dll` | `6029fd95…` | 763,392 B | 09-20 | **82** |

**仅 3FC 有、上游完全没有的 3 个导出**（3FC 是上游的严格超集，上游没有 3FC 缺的）：

```
FFF3FP_Redraw          ← 最可疑，见 7.3
FFF3FP_SetLogCallback
FFF3FP_SetPresentConfig
```

⇒ "上游 0/16"这个基线**不是干净的对照**：它同时改变了「内核二进制」与「调用哪些导出」两个变量。

### 7.3 🎯 最可疑的具体路径：`FFF3FP_Redraw` → 重建 swapchain

`FFF3FP_Redraw`（`PlayerApi.cpp:157-160` → `PlayerSession.cpp:1695-1700` → `VideoRenderer.cpp:4340-4355`）：

- `PlayerSession.cpp:1694` 注释自述 **"upstream has no equivalent"** —— 上游根本没有这条路。
- 它**不 Present**，而是持 `deviceMutex_ + presentMutex_` 调 `EnsureSwapChain`
  ⇒ 可能走 `ResizeBuffers`（`:2361`）或 **整条 swapchain 重建**（`CreateSwapChainForHwnd`，`:2374`）。
- 执行在**调用者线程**；3FC 侧即 UI 线程（`PlayerSurface.cs:437-444`：WM_SIZE →
  `Dispatcher.UIThread.Post(..., Background)` → `Redraw()`）。

**为什么这条路径与 RTSS 致命**：RTSS 的钩子正是挂在 swapchain 的 Present 上，并持有
per-swapchain 的状态。**swapchain 被 `ResizeBuffers` / 重建的瞬间，钩子持有的旧引用悬空**
—— 这是注入式 overlay 最经典的崩溃场景，而**上游根本不会触发它**。

旁证：`--sessiontest` 的低频崩溃 `0xC0000005 @ FFF3FP_Redraw`（栈顶 WM_SIZE→Redraw）
**是同一个栈顶家族**。

### 7.4 📊 崩溃数据的统计复核（我的独立计算）

| 问题 | 计算 | 结论 |
|---|---|---|
| 单路 vs 上游 | Fisher 双侧 **p = 0.0434** | 复算与 §〇 一致 |
| **4 路 100% 需要额外机制吗？** | 单路 p₁=31.25%；若 4 路独立，至少一路崩 = 1-(1-p₁)⁴ = **77.7%**；实测 4/4，在该假设下出现概率 **p=0.364** | **不矛盾** ⇒ 无需"多路特有机制"，**概率叠加即可解释** |
| 崩溃时刻形态 | 8.1 / 8.1 / 12.2 / 14.2 / 21.3 s，均值 12.78 s | 分散在播放中段 ⇒ **运行期偶发/竞态**，不是启动期结构性失败 |
| 判别实验样本量 | 若 3FC 侧真归零：n=16 → p=0.043；**n=24 → p=0.0039**；n=32 → p=0.0009 | 取 **n≥24/臂** 才够硬 |

⇒ **结论：应当集中解释"单路 31%"，而不是去找"4 路特有的东西"。**

### 7.5 修正后的实验方案（按性价比排序）

> 作废：原"改顶层窗口句柄"实验（7.1 已推翻前提）。

| 序 | 实验 | 变量 | 判据 | 价值 |
|---|---|---|---|---|
| **C** | 3FC **单路** + **RTSS 退出**（`run_arm.ps1 -Label B -Only FC1`） | RTSS 有无 | 崩溃率是否归零 | 确立"RTSS 在单路也是必要条件"（§六 欠账，一直没做）。若不归零 ⇒ 还有第二个独立原因，问题性质改变 |
| **A** | 3FC 单路 + **屏蔽 `FFF3FP_Redraw` 调用**（临时 env 开关），RTSS **保持运行** | Redraw 路径 | 崩溃率是否归零 | **定位具体路径**。归零 ⇒ 根因锁定为"Redraw→重建 swapchain 与注入钩子竞态"，修复方向明确且在 3FC 侧 |
| B | 把 3FC 内核放进上游 3FP bin | 内核二进制 | — | **判别力有限**：上游不调用那 3 个导出，换了内核也不会走 Redraw 路径 ⇒ 不做 |

样本量：**n ≥ 24/臂**（7.4），沿用拉丁方位置平衡、crash/drift 分列、逐次记 DLL sha256。

### 7.6 🎯 机制链条（2026-09-21 11:25 定位，本轮最重要结论）

沿「每帧 `EnsureSwapChain`」往上追，找到**播放中会重建 swapchain 的真实来源**：

```
VideoRenderer.cpp:742    HdrSupportProbeCacheDuration = 750ms   ← HDR 能力探测的缓存期
        ↓ 缓存过期即重新探测（PresentTimedTextOnPump 每帧调 EnsureSwapChain ⇒ 每 750ms 触发一次重探）
:2220  preservePrevious = hdrSupportValid_ && previousMonitor == monitor
:2222  cachedUsable = forceHdrOutput_ || hdrSupported_
:2231  hdrSupported_ = false;          ← 重探先置 false，再由 DXGI 查询决定
:2263  hdrSupported_ = (BitsPerColor>=10 && ColorSpace==G2084)   ← **系统 HDR 状态**
        ↓ 若本次结论与 swapHdr_ 不同
:2356  if (width==swapWidth_ && height==swapHeight_ && hdr==swapHdr_ && outputBits==swapOutputBits_) return;
:2374  return CreateSwapChain(...);    ← **整条 swapchain 重建**（不是温和的 ResizeBuffers）
        ↓
        RTSS 钩子持有的 per-swapchain 状态/引用悬空 ⇒ 0xC0000005 @ dxgi.dll
```

**为什么这个链条能解释全部观测**：

| 观测 | 解释 |
|---|---|
| 崩溃时刻分散 8.1/12.2/14.2/21.3 s | 每 750ms 一次重探，结论**偶然**翻转才崩 |
| 单路只有 31% 而非 100% | 需要"探测结论恰好翻转"这个窄窗口 |
| 4 路 100% | 4 路独立 × 31% 的叠加（7.4 已证） |
| 崩在 `dxgi.dll` 而非 3FC 代码 | 钩子在 DXGI Present 路径上，重建使其悬空 |
| 上游 0/16 | 上游不进 HDR 输出路径 / 环境不同 ⇒ `swapHdr_` 恒 false，无翻转空间 |

**旁证（内核注释自承 HDR 状态会自发抖动）**：`:2265-2267`
> "AdvancedColorInfo can briefly fail while Windows reapplies HDR calibration."
⇒ **Windows 会自发重新应用 HDR 校准**，这正是"结论翻转"的来源。

**已排除的本轮候选**：Redraw 路径（实测 30 s 播放中 Redraw **只在启动期 2 次**，播放中段为 0 次）、
`OnColorModeChanged`（只由用户操作下拉框触发，播放中不自动调用）。

### 7.7 试验：延长 HDR 探测缓存 —— ❌ **未获支持，已回滚**

**改动**：`HdrSupportProbeCacheDuration` 750ms → 30s（`VideoRenderer.cpp:742`），重新编译内核
（`tools/build_kernel_manual.py`，22/22，产物 82 导出 / API 15 通过）。
已验证新内核确实生效：删磁盘副本后由 exe 从内嵌资源解压，sha256 = `20923ff2…`（新版），
而非原 `6029fd95…`。

**A/B 交替对照**（同批次、每轮 OLD→NEW，各 8 次；单路 4K / 30s；**RTSS 保持运行**）：

| 臂 | OK | 漂移失败(exit=1) | 真实崩溃 | 崩溃码 |
|---|---|---|---|---|
| OLD（原内核 `6029fd95…`） | 6 | 1 | **1** | `0xC000001D` |
| NEW（30s 缓存 `20923ff2…`） | 5 | 3 | **0** | — |

**判定：样本不足以定论，但看不到崩溃率改善**（NEW 的"漂移失败"反而 3:1 更多，
而漂移失败是擦线噪声，与被测变量无关）。⇒ **假设未获支持，改动与内核 DLL 均已回滚**
（源码恢复 750ms、DLL 恢复 `6029fd95…`、UI 已重建并确认内嵌回原内核）。

### 7.8 ⚠⚠ 本轮最贵的方法论教训：退出码口径

> **`--multitest` 的 `exit=1` 是"漂移断言失败"，不是崩溃。**
> 崩溃是进程被异常码终止（`0xC0000005` = `-1073741819` 等）。

本轮第一次统计把 `rc != 0` 全当崩溃，得到"修复后 2/8 = 25%"，据此几乎得出"略有改善"的
错误结论。实际那 2 次是漂移失败。
⇒ **以后任何崩溃率统计都必须先按异常码分类**（`0` / `-1073741819` / 其它），
用 PowerShell 取 `$LASTEXITCODE`（bash 的 `$?` 会把异常码压成 1）。

### 7.9 🔍 新线索（未验证）：`0xC000001D` STATUS_ILLEGAL_INSTRUCTION

OLD 臂那次真实崩溃是 **`0xC000001D`（非法指令）**，**不是** `0xC0000005`（访问违规）。

- 访问违规 = 悬空指针/野引用；
- **非法指令 = 执行流跳进了被改写的机器码区域**。
- RTSS 的注入钩子正是用 **inline hook** 改写 `dxgi.dll` 目标函数入口的机器码做 trampoline。
  ⇒ 该码型**更直接地指向"hook trampoline 与执行流冲突"**，与"故障模块 dxgi.dll + 0x19530"吻合。

样本仅 1 次，**尚需更多崩溃样本做码型分布统计**才能定论。

### 7.10 已排除清单（累计，勿重复投入）

- tearing（`SetPresentConfig` 只影响交互拖动；正常播放两侧同为 `Present(1,0)`）
- 并发 Present / 解码负载 / 漂移校正 Seek / 多卡争用 / 变换风暴 / `nvspcap64`
- **子 HWND vs 顶层窗口**（上游也是子窗口）
- **Redraw 频率**（实测 30s 播放中 Redraw 仅启动期 2 次，播放中段 0 次）
- **HDR 探测缓存**（本轮 A/B 实测，无改善）
- **WGC 抓屏**（multitest 路径未调用，与崩溃样本无关）
- `OnColorModeChanged`（只由用户操作下拉框触发）

### 7.8 顺带记下的内核观察（未定论）

- `VideoRenderer.cpp`：Present 对 `DEVICE_REMOVED/RESET/HUNG/DRIVER_INTERNAL_ERROR` 有
  `RequestDeviceRecovery`（`:5200-5214`），但 **`DXGI_STATUS_OCCLUDED` 源码里没有处理**。
- 交换链重建前必须先把 backbuffer 引用清零（`:5116-5128` 注释自承），否则新旧引用互等 ——
  这条注释本身就说明**重建链是已知的竞态敏感区**。
- 环境差异（非 HWND 形态）：3FC 父窗口被加了 `WS_CLIPCHILDREN`（`MainWindow.axaml.cs:2420-2423`）
  且叠 Avalonia 合成器；上游是 WinForms `Panel`。属"窗口环境"差异，尚未评估。
