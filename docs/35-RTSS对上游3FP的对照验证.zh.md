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

## 六、尚未完成：B 臂（RTSS 退出后）

**A 臂已足以回答「上游是否受影响」（否）。** 剩余增量问题只有一个：
**3FC 单路那 31% 是否同样由 RTSS 引起**（即退出 RTSS 后 1 路是否归零）。
若归零，则「RTSS 是必要条件」在 1 路场景也成立，可写进发布说明的运行建议。

需用户手动退出 RTSS 与 MSI Afterburner 后执行：

```powershell
& "C:\PLAN\3FCompare\.review_pr\rtss_3fp\run_arm.ps1" -Label "B" -Rounds 8 -Only @('FP1','FC1')
```

（脚本自带 `rtssUp` 字段，会自动校验该臂期间 RTSS 确实未运行，防止条件串味。）
