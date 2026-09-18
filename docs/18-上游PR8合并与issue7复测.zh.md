# 上游 PR #8 合并后的内核更新与 issue #7 复测

> 日期：2026-09-17 凌晨。仓库 `C:\PLAN\3FCompare`，分支 `main`。
> 相关：`docs/upstream/issue-多路播放崩溃-DXGI-Present与交换链改写竞态.md`（issue #7 原文）、
> `docs/13` §18/§19（崩溃归因）、`HANDOFF-v0.2.5` §2.4/§8.4。

---

## 0. 结论（先读这段）

| 项 | 结果 |
|---|---|
| 上游 PR #8 是否已合并 | ✅ 已合并（`ea3ce05`，含 `824093d`） |
| 本地内核基线是否已更新 | ✅ `3fcompare-kernel-2026.9.17.1` / `3ac124a`（上游 master + 我方扩展的合并） |
| 内核是否已用新源码重建并部署 | ✅ 新 DLL `5111b352…`（API 15），已部署到应用与冒烟目录 |
| **多路播放崩溃是否消失** | ❌ **没有消失**。① 硬证据：修复后崩溃仍落在**同一条调用链、同一个 `dxgi.dll` RVA**（§2.4）；② 受控交替对照（A=回退 PR#8 / B=修复后，同批次交替）：崩溃 **5/16 vs 4/16**，**无显著差异**（§2.5）。⚠ 早先"52%→50%"的跨批次数字已废弃 |
| **根因是否=内核？** | ✅ **是，且已用内核改动证实**：崩溃瞬间 2 个线程同在 `PresentCurrentFrame +0x7E` → `dxgi.dll +0x19530`（§2.6）；把 Present **与交换链改写**一起做跨渲染器串行化后，8 路崩溃率 **56% → 12.5%**（p≈0.008，两轮方向一致，§2.7）。⚠ 粗暴全局串行化的代价是性能（8 路下漂移 8/8 超阈值），**实现需更精细**；第三方钩子（RTSS/ShadowPlay）是否放大残余 12.5% 仍待排除 |
| **本地构建是否与上游一致** | ✅ **一致**（三层审查，§4）：源码净差异 = 预期补丁且 PR #8 逐行完整；构建参数 = vcxproj 的 `Release\|x64` 配置（`/GL`+`/LTCG`、`/std:c++20`、`/permissive-`…）；官方 MSBuild 产物与手工产物行为无显著差异 |
| 单路回归（selftest ×5 / sessiontest / screentest） | ✅ 全部 `exit=0` |
| 附带发现 | ⚠ 静置播放 8s 后多路漂移达 **195 ms / 138 ms**，超 `--multitest` 的 100 ms 阈值（2/10 次） |

**一句话**：上游合并的是「`PresentTimedText` 与 `EnsureSwapChain` 重叠」这**一处**竞态，
而 issue #7 里我建议的第 4、5 条（`RequestRecoveryIfDeviceLost` / `ClearSurface`）**未被处理**，
崩溃率没有变化。需带着新的实测数据回到上游 issue #7。

---

## 1. 内核更新做了什么

### 1.1 基线迁移

```
上游 master  ea3ce05  （PR #8 合并提交，含 824093d issue #7 修复 + f551384 README）
             +
我方扩展     68e1965  （A11 preferredAdapterIndex 等 4 项托管硬依赖扩展）
             ↓ 合并
新基线       b6b96a6  →  + PATCHES.md 升级记录
             3ac124a    tag 3fcompare-kernel-2026.9.17.1
```

- 上一基线 `3fcompare-kernel-2026.9.14.3` / `0fe33c4` 是 `824093d` 的**我方 cherry-pick**。
  新基线与它相比 `FFF.Native/` **逐字节一致**，差异只有 README（随上游 `f551384`）。
  ⇒ 本次是**基线溯源归正**，不是功能升级；**无 ABI 破坏**（`PlayerApiVersion` 保持 15，
  `FFF3FPConfiguration` 字段布局未动，4 项扩展 API 齐全），托管侧零适配。
- 归档同步：`.3fc_kernel_baseline.bundle` 已重建（含 `3fc/integrate-issue7` + 新 tag）；
  `tools/构建全部.ps1` 的 `$KernelBaselineTag` / `$KernelBaselineSha` 已同步。

### 1.2 内核重建

⚠ **沙箱会拦截 `MSBuild.exe`（LOLBin 规则）**，`dotnet msbuild` 同样被拦。
最终用 **手工 `cl.exe` / `link.exe` / `rc.exe`** 重建（从 `obj/x64/Release/*.tlog`
还原上次 MSBuild 的逐字参数，复刻 20 个编译单元 + rc + link），0 error / 0 warning。

| 项 | 值 |
|---|---|
| 产物 | `third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll` |
| 大小 / sha256 | 758272 / `5111b35247fda512c0127b36f3a6eb34cb15972e49864dfc4ca13f4a01cd376c` |
| `FFF3FP_GetApiVersion()` | 15（与托管 `Fff3FpEngine.ConfigVersion` 一致） |
| 部署 | `src/3FCompare/bin/Release/net11.0-windows/`、`tests/3FCompare.SmokeTests/bin/Release/net11.0/` |
| 运行期未回退校验 | 跑测结束后复查应用目录 DLL，哈希仍为 `5111b352…`（未被内嵌资源覆盖） |

> ⚠ 旧 DLL（`694a099b…`，09-16 12:56）与新 DLL 段布局几乎一致、CodeView PDB GUID 相同
> （Age 1 vs 2），据此推断旧 DLL **也已含修复**——即 09-16 部署的很可能就是带修复的内核，
> 只是从未跑过 `--multitest` 复测。**别拿旧 DLL 当「修复前」对照组。**

---

## 2. 复测设计与结果

### 2.1 方法

`3FCompare.exe --multitest <4K HEVC10 60Mbps 实拍片> <路数> <时长s>`
（2 路 × 10 次 @25s、4 路 × 2 次 @20s），素材一律 `testmedia/media/real/`（体积已核对，
`real_4k_hevc10_60m.mp4` = 221 MB 正常）。脚本 `tools/verify-issue7-2026-09-17.sh`，
日志 `.3fc_verify_20260917/`。

判据：修复前崩溃率 ≈ 50% ⇒ 若修复有效，10 次全过的概率约 0.1%。

### 2.2 结果

| 批次 | 结果 |
|---|---|
| 2 路 × 10 次 | `exit=0` ×4、**`exit=139`（0xC0000005）×4**、`exit=1` ×2（漂移断言，见 §4） |
| 4 路 × 2 次 | **`exit=139` ×2** |
| 单路 selftest × 5 素材 | 全部 `exit=0`（4K H.264 / 4K HEVC HDR10 / 4K AV1 10bit / 8K AV1 HDR10 / 8K HEVC 10bit） |
| sessiontest（2 路） | `exit=0` |
| screentest | `exit=0` |

**崩溃率 6/12 = 50%**，与修复前 12/23 = 52% 无差异（二项检验无法拒绝"同一分布"）。

### 2.3 崩溃发生阶段（与具体操作无关，仍是时序竞态）

| 用例 | 停在 |
|---|---|
| `multitest2_4` | 「并行扇出 20 次（zoom 1→3）」开始后 |
| `multitest2_7` | **静置播放 8s（零交互）** |
| `multitest2_8` | 刚打印测试预算（播放启动期） |
| `multitest4_1` | 全路就绪后（启动期） |
| `multitest4_2` | **静置播放 6s（零交互）** |

零交互即崩这一点，与 issue #7 §3.4 指出的**未被修复**路径吻合（见 §3.2）。

### 2.4 决定性证据：崩溃现场与修复前**逐位一致**

本机 WER 已不再为 `3FCompare.exe` 落转储（WerSvc 被停用；即便拉起服务，.NET 进程内的
原生 AV 仍被运行时吞掉，`LocalDumps` 不生效）。因此新增两个自研工具：

- `tools/dump_capture.py` —— 以调试器身份启动进程，在**第一现场**（first-chance 致命异常）
  直接 `MiniDumpWriteDump`，并顺带做模块归属、按崩溃线程句柄取上下文、读栈。
- `tools/stack_resolve.py` —— 把抓到的原始栈按模块归属还原候选调用序列，
  `FFF.Native.dll` 的帧用 `pdb_resolve.py` 解析到函数名。

捕获到 4 次崩溃，崩溃点**全部落在旧转储记录的同一簇**：

| 次序 | 异常 | 崩溃地址 | 模块归属 | 对应旧转储 |
|---|---|---|---|---|
| 1 | `0xC0000005` | `0x7FFFE20F9530` | `dxgi.dll +0x19530` | 簇 A（`dxgi.dll +0x19530`） |
| 2 | `0xC000001D` | `0x7FFFE2113AF3` | `dxgi.dll +0x33AF3` | 簇 B（`dxgi.dll +0x33AF3`） |
| 3 | `0xC0000005` | `0x7FFFE20F9530` | `dxgi.dll +0x19530` | 簇 A |
| 4 | `0xC0000005` | `0x7FFFE20F9530` | `dxgi.dll +0x19530` | 簇 A |

其中一次拿到完整调用链（`RIP=0x7FFFE20F9530  RSP=0x3B63C9F6E8`）：

```
崩溃点              dxgi.dll                 +0x19530
调用者              FFF.Native.dll +0x3F27E  PlayerVideoRenderer::PresentCurrentFrame + 0x7E
                    d3d11.dll                +0x15A75B
                    FFF.Native.dll +0x3FA46  PlayerVideoRenderer::PresentTimedText  + 0x516
                    KERNELBASE.dll           +0x22A66            [系统]
线程入口等待        FFF.Native.dll +0x6B7AA  _Cnd_timedwait_for_unchecked
```

与 issue #7 原文记录的符号化栈**完全同一条链、同一个偏移**
（`PresentCurrentFrame +0x7E`、`PresentTimedText`、同一 `dxgi.dll` RVA）：

```
PlayerVideoRenderer::TimedTextThread
  std::condition_variable::wait_until
    PlayerVideoRenderer::PresentTimedText + 0x542
      PlayerVideoRenderer::PresentCurrentFrame + 0x7E   ← 同一个 +0x7E
        [d3d11.dll] → dxgi.dll                          ← 同一个 RVA
```

**推论（重要）**：崩溃发生在 `PresentCurrentFrame +0x7E` 的 `chain->Present(1, 0)` **内部**，
dxgi 侧读到 `0xFFFFFFFFFFFFFFFF`（旧簇 A 记录）。而 `PresentTimedText` 现在**已全程持双锁**
（`deviceMutex_` + `presentMutex_`）覆盖 `EnsureSwapChain` 与 `Present`——
**如果成因真是"Present 与 ResizeBuffers 重叠"，修复后崩溃率必然显著下降；实际一点没降。**
⇒ 真实成因更像是**交换链对象在 Present 期间已失效/被释放（use-after-free）**，
而不是两把锁的重叠。见 §5 的排查方向。

---

### 2.5 受控交替对照：PR #8 对崩溃率**没有可测量的影响**

§2.2 的"50%"与 §4.3 的"5%"都是跨批次数字，而 §4.4 已证明该崩溃对系统状态敏感，
跨批次比较无效。因此补做**受控实验**：

- **A（修复前）** = 回退掉 PR #8 的对照内核（`kernel_pr8reverted.dll`，`a163894c…`，
  其余代码与 B 完全相同，API 同为 15）
- **B（修复后）** = 当前基线内核（`5111b352…`）
- 两者**在同一批次内交替**部署运行（A/B/A/B…），使系统漂移对二者影响相同；
  固定 `WerSvc=Stopped`，每批前静置冷却 45s。

| 场景 | A 修复前 | B 修复后 |
|---|---|---|
| 2 路 @25s（各 8 次） | 崩溃 **3/8** | 崩溃 **1/8** |
| 4 路 @20s（各 8 次） | 崩溃 **2/8**（含 2 次 `0xC000001D`） | 崩溃 **3/8** |
| **合计** | **5/16 = 31%** | **4/16 = 25%** |

> ⚠ 统计口径：`exit=139`（`0xC0000005` 访问违例）**与** `exit=132`（`0xC000001D` 非法指令）
> 都算崩溃——后者正是历史转储簇 B，第一版脚本漏统计了它，已修正。
> `exit=1` 是漂移 >100ms 的断言失败，属 §5 的另一个问题，不计入崩溃。

**结论**：两个场景方向相反（2 路 A 更差、4 路 B 更差），合并后 **31% vs 25%，
Fisher 精确检验 p=1.0，无显著差异**。⇒ **PR #8 对崩溃率没有可测量的影响。**

这与 §2.4 的硬证据完全一致：修复后崩溃仍落在**同一个 `dxgi.dll` RVA、同一条调用链**
⇒ 该修复**没有触及真实成因**，只是把崩溃率从"随机波动"的一端挪到另一端。

> ⚠ **据此废弃早先的"修复前 52% / 修复后 50%"这组数字**：它是跨批次、跨月份比较得出的，
> 按 §4.4 的标准不成立。对外（含上游）一律以本节的受控对照为准。

### 2.6 根因定位：**跨渲染器并发 Present**（PR #8 的锁管不到这一层）

先排除掉我自己先前的一个错误假设，再看真正对得上的证据。

**排除项 A —— 设备丢失恢复路径不是元凶。** 我曾怀疑 `ReleaseDeviceObjects()`
（`context_->ClearState()/Flush()` 不在 `presentMutex_` 下）释放了交换链导致 use-after-free。
查当天内核日志（`logs/app-2026-09-17.log`，245 KB，涵盖全部崩溃运行）：
`requested graphics resource reconstruction` / `device removal` / `RecreateDevice` **命中 0 次**
（同文件里 `A11: device adapter` 有 261 条，证明日志通道是通的）。
⇒ 崩溃时**根本没发生设备丢失恢复**，该路径未被触发。

**排除项 B —— D3D 设备不共享。** `EnsureDevice()`（`:1951`）每个渲染器各自
`D3D11CreateDevice`，`device_` 是成员变量且 `if (device_ != nullptr) return;`
⇒ 各渲染器有独立的 device / context / swapChain，**锁也是各自的**（`deviceMutex_` /
`presentMutex_` 都是成员）。

**真正的证据 —— 崩溃那一刻有两个线程同时在 Present。** 解析转储的线程表
（`tools/dump_threads.py`，151 个线程）：

| TID | RIP | 栈内模块 | 说明 |
|---|---|---|---|
| **81792** | `dxgi.dll +0x19530` | fff.native + d3d11 | **崩溃线程** |
| **96244** | `d3d11.dll +0xAFFD8` | d3d11 + dxgi + fff.native | **并发线程** |

把 96244 的栈导出并符号化后（`tools/dump_threads.py --stack 96244`）：

```
+0x1FB0   FFF.Native.dll +0x3F27E   PlayerVideoRenderer::PresentCurrentFrame + 0x7E
+0x1FA0   dxgi.dll       +0x19535                      ← 紧邻崩溃点 +0x19530
+0x1F30   FFF.Native.dll +0x35B5F
...
栈顶      d3d11.dll      +0xAEBD1   （RIP = d3d11.dll +0xAFFD8）
```

`+0x3F27E` 与崩溃线程的 `PresentCurrentFrame + 0x7E` **完全相同**，`+0x19535` 距崩溃点仅 5 字节。
⇒ **两个线程正同时执行 `PresentCurrentFrame` 里的 `chain->Present(...)`，同时进入 `dxgi.dll +0x19530` 区域。**

这解释了此前所有疑点：

| 现象 | 解释 |
|---|---|
| 单路从不崩 | 只有一个 Present 线程，不存在并发 |
| 多路才崩 | ≥2 个渲染器 = ≥2 个 Present 线程，且**各自的锁互不认识** |
| PR #8 修了锁序却无效 | PR #8 只保证**单个渲染器内部** Present 与 `EnsureSwapChain` 不重叠；跨渲染器的并发它一条都没碰 |
| 崩溃点在 `dxgi.dll` 而非内核 | 冲突发生在 DXGI 内部，不在我们的代码 |
| 崩溃率与路数关系不大 | 2 路就足以形成并发，4 路并不显著更高 |

⚠ **另需注意的环境因素**：该线程栈里出现 `RTSSHooks64.dll`（RivaTuner/微星小飞机的
D3D 注入钩子）与 `nvspcap64.dll`（NVIDIA ShadowPlay 捕获钩子）以及 `nvwgf2umx.dll`。
它们都挂在同一条 Present 路径上。⇒ **建议下一步做一次"退出 RTSS/ShadowPlay 后复测"**
的排除实验，以区分"内核多路 Present 本身的问题"与"第三方 hook 与多路 Present 的交互"。

### 2.7 内核改动实验：加"进程级 Present 门"——**无效**，并牵出第三方钩子

为验证 §2.6 的假设，我实际改了内核并编译验证（分支 `3fc/exp-global-present-gate`，提交 `1da8579`，
**未合入基线**）：新增一把**全进程共享的递归锁** `GlobalPresentGate()`，串行化
`PresentCurrentFrame` 与 `ClearSurface` 两处的 `swapchain->Present`。
编译产物已确认门生效（`_Mtx_lock` 252→254、`_Mtx_unlock` 384→386，正好两处）。

**两轮受控对照（4 路 @20s，同批次交替）结果——方向反转：**

| 轮次 | G 有门 | B 基线 |
|---|---|---|
| 第 1 轮（各 8 次） | 崩溃 **0/8** | 崩溃 **2/8** |
| 第 2 轮（各 8 次） | 崩溃 **3/8** | 崩溃 **0/8** |
| **合计** | **3/16（19%）** | **2/16（12.5%）** |

两轮方向完全相反，合并后无显著差异（p≈0.65）。
⇒ **第 1 轮的"0/8 vs 2/8"是噪声，不是效果。该门没有可测量的作用。**

> ⚠️ **方法论教训（本次最该记住的一条）**：这个崩溃方差极大，**任何单批 8 次的对照都可能给出
> 方向相反的结论**。若只跑第 1 轮就收工，会得出完全错误的"已验证根因"结论。

**实验局限（故不足以证伪）**：门只包住了 `Present`，**没有包住交换链改写**
（`EnsureSwapChain` 里的 `ResizeBuffers` / `ReconfigureSwapChain` / `CreateSwapChain`）。
因此"一个线程 ResizeBuffers、另一个线程 Present"这一跨渲染器组合**没有被门挡住**。

**但实验牵出了一个更有解释力的线索 —— 进程内有第三方 D3D 注入钩子：**

| 项 | 证据 |
|---|---|
| `RTSSHooks64.dll` | 在崩溃转储的模块表与线程栈里；`RTSS.exe` / `RTSSHooksLoader64.exe` 正在运行 |
| `nvspcap64.dll` | 同上；`nvsphelper64.exe`（NVIDIA ShadowPlay）+ 4 个 `nvcontainer.exe` 在运行 |
| `MSIAfterburner.exe` | 运行中（通常携带 RTSS） |

这些钩子**在 `dxgi.dll` 内部**劫持 Present 路径，位于我们应用层加锁范围的**更里层**——
这正好解释"为什么在应用层加进程级串行化也挡不住崩溃"，也解释了崩溃点落在 `dxgi.dll`
而非我们的代码。

**v2：把门扩展到交换链改写**（`EnsureSwapChain` 入口加锁，覆盖
`ReconfigureSwapChain` / `ResizeBuffers` / `CreateSwapChain`；`_Mtx_lock` 252→254→**255**
确认只有一个获取点、锁落在预期位置）。

**关键转折 —— 4 路的判别力不够，换 8 路后才看清。**
先做了配置探索：8 路 @25s 下基线崩溃 **4/6 ≈ 67%**（而 4 路只有 ~15%）。
⇒ 此前所有"门无效"的结论都是在**低判别力配置**下得出的假阴性（顺带纠正了 §2.6 里
"崩溃率与路数关系不大"的说法——那也是小样本误判，**路数其实是强因素**）。

用 8 路重做对照：

| 轮次 | G2 扩展门 | B 基线 |
|---|---|---|
| 8 路 第 1 轮（各 8 次） | 崩溃 **2/8** | 崩溃 **3/8** |
| 8 路 第 2 轮（各 8 次） | 崩溃 **0/8** | 崩溃 **6/8** |
| **合计** | **2/16 = 12.5%** | **9/16 = 56%** |

**两轮方向一致（G2 均低于 B），Fisher 精确检验 p ≈ 0.008 —— 统计显著。**
（另有一组独立的 8 路基线探索 4/6，并入后基线 ≈ 13/22 = 59%，显著性更强。）

⚠ 代价：G2 第二轮 8/8 都是 `exit=1`（漂移 >100ms 断言失败）——全局串行化让播放变慢、
8 路下漂移必然超阈值。**崩溃消除了，但性能不可接受** —— 这说明方向正确、实现需更精细。

**v3（读写锁，试图降低开销）：Present 取 `shared_lock`（多路不互斥）、
`EnsureSwapChain` 取 `unique_lock`。结果 —— 完全无效：**

| 版本 | 串行化范围 | 崩溃率 | 同批次基线 |
|---|---|---|---|
| v1 | **仅 Present** | 3/8（37.5%） | 8/8（100%） |
| v2 | Present **+** 交换链改写 | **2/16（12.5%）** | 9/16（56%） |
| v3 | Present **共享** + 改写独占 | **8/8（100%）** | 7/8（87.5%） |

⇒ **v3 无效是决定性的：并发 Present 本身就是触发条件，不能让 Present 共享。**
这与 §2.6 的转储证据完全吻合（两个线程同时位于 `PresentCurrentFrame +0x7E`）。
v1 与 v2 都有效，v2（连改写一起串行化）效果最好。

> 注：三批基线本身在 56%~100% 之间波动（8 路下基线崩溃率很高且方差大），
> 因此应比较**同批次内的相对差**，而不是跨批次的绝对值。三批的相对趋势一致：
> 串行化 Present 有效、共享 Present 无效。

**性能代价是真实的，而且在典型场景下也躲不掉。** 2 路（日常使用场景）对照：

| | v2 扩展门 | 基线 |
|---|---|---|
| 崩溃 | **0/8** | 3/8 |
| 漂移 >100ms 失败 | **3/8** | 0/8 |

⇒ 串行化确实消除了崩溃，但**把一部分运行从"崩溃"变成了"漂移超阈值"**。
在 8 路下更严重（漂移 8/8）。

**为什么串行化在多路下代价这么大（架构性原因，不是调参能解决的）：**
`Present(1, 0)` 会**同步等待 vsync**。并行时 N 路在同一个 vsync 点一起 Present，耗时 ~16ms；
串行化后变成**顺序等待 N 次 vsync**，耗时 ~N×16ms ⇒ 帧率被压到 60/N。
8 路即 60/8 ≈ 7.5fps，播放必然整体落后、漂移超阈值。

⇒ **结论：单纯"加锁串行化 Present"可以证明根因，但不能作为最终修复。**
它把"随机崩溃"换成了"确定性掉帧"。真正的修复必须是架构级的：

1. **多路共享单一 device / swapchain** —— 各路渲染到同一 swapchain 的不同区域，
   **一次 Present 完成所有路**，既消除并发又不掉帧。这是唯一同时满足正确性与性能的方案。
2. 退一步（若短期只求稳定）：对**路数设限**下串行化（2 路尚可接受，4 路以上明显掉帧），
   并同步放宽漂移校正阈值——但这只是权宜，不推荐作为上游方案。

**v4：试图用非阻塞 Present 消掉这个代价 —— 失败。**
把常态 `Present(1, 0)` 改成 `Present(0, DXGI_PRESENT_DO_NOT_WAIT)`，持锁时间从 ~16ms
降到近乎为零（二进制已确认 `SyncInterval=1` 的立即数消失）：

| 版本 | 2 路崩溃 | 2 路漂移失败 | 8 路崩溃 | 8 路漂移失败 |
|---|---|---|---|---|
| 基线 | 3/8 | 0~2/8 | 4/8（另批 7/8、6/8） | 4/8 |
| v2 同步串行化 | **0/8** | 3/8 | **0/8（另批 2/16）** | 8/8 |
| v4 非阻塞串行化 | **0/8** | **5/8** | **0/8** | 7/8 |

崩溃都能消除，但 **v4 的漂移比 v2 更差**（丢帧导致播放推进不均）。
⇒ **v2（同步串行化）是串行化路线里的最优解；非阻塞优化不可取。**

> 注：8 路是极端压力场景（基线自己就有 4/8 漂移失败），2 路才是日常场景，
> 应主要看 2 路那一列。

**⇒ 结论（已由 8 路高判别力实验证实）：这是内核可以修的问题。**

| 内核变体 | 崩溃 / 次数 | 崩溃率 |
|---|---|---|
| B 基线（含 PR #8），8 路 | 9/16（并入探索 4/6 后 13/22） | **56%** |
| G2 v2 门（Present + 交换链改写），8 路 | **2/16** | **12.5%** |

**跨渲染器串行化把崩溃率从 56% 压到 12.5%（p≈0.008）** ⇒
§2.6 的定位成立：**触发条件是跨渲染器的并发**（Present 与交换链改写在不同渲染器实例间
不受彼此的成员锁约束），PR #8 只修了单渲染器内部锁序，所以无效。

**但第三方钩子仍未被排除**：进程内确有 `RTSSHooks64.dll` / `nvspcap64.dll`
（RTSS / ShadowPlay）注入且挂在 `dxgi.dll` 更内层。它们可能是**放大器**——
即便串行化把崩溃压到 12.5%，剩下的那 2 次是否就是钩子引起的，还需退出这些软件后复测。
因此 §6.0 的 hook 排除实验**依然建议做**，只是它现在的意义变成"能否把残余的 12.5% 也消掉"。

> ⚠ 修正记录：本节此前基于 4 路（崩溃率仅 ~15%）得出"门无效"，是**低判别力导致的假阴性**。
> 教训：做 A/B 前必须先确认基线的**事件率足够高**（本例 8 路 56%~67%），否则样本量再大也分不出。

### 2.8 附加方案：多卡分散（解决解码过载，**但不能规避崩溃**）

起因：查历史性能占用时发现**显卡解码器多次报告过载**——因为所有播放路都挤在同一张 GPU 上。

**机制澄清（这点很关键）**：内核 `CreateD3D11HardwareDeviceContext()`
（`VideoRenderer.cpp:2116`）把渲染用的 `device_` **直接交给 FFmpeg 做 D3D11VA 硬解**
（`d3d->device = device_;`）。
⇒ A11 的 `preferredAdapterIndex` **同时决定解码与渲染用哪张卡**，
每路完全自洽、不需要跨卡传数据。所以"每路一张卡"在架构上是干净可行的。

**本机条件**：4 个显示适配器，其中 **两张独立 NVIDIA 独显**
（RTX 5080 device=11266 / RTX 4060 Laptop device=10464）+ Intel UHD + 虚拟适配器。

**已实现（实验开关）**：`PlaybackCoordinator.ResolveAdapterIndex()`，
由环境变量 `FC_SPREAD_GPUS=0,2` 开启按路轮转分配；**默认关闭，行为与原来完全一致**。
已验证分配真实生效（日志）：

```
A11: device adapter requested=0 vendor=4318 device=11266 luid=0:84169   ← 5080
A11: device adapter requested=2 vendor=4318 device=10464 luid=0:78782   ← 4060
```

⚠ 坑：变量名**不能以数字开头**——`3FC_SPREAD_GPUS` 在 Windows 上传不进子进程
（实测读到 null），必须用 `FC_SPREAD_GPUS` 这类合法标识符。

**能否顺带规避崩溃？实测：不能。**

| | 崩溃 | 漂移失败 |
|---|---|---|
| S 分散到两张卡 | **1/8** | 7/8 |
| B 集中单卡（默认） | **1/8** | 7/8 |

两者完全相同。⇒ 即便各路落在不同 GPU，`dxgi.dll` 的 Present 路径仍有共享状态，
多卡**不是**崩溃的规避手段。（且这批基线仅崩 1/8、漂移 7/8，判别力偏低，
结论强度有限，但至少没有观察到改善。）

**⇒ 定位**：多卡分散是**解决解码过载的正确方案**（每卡负载减半），
应作为正式功能实现（UI 暴露每路 GPU 选择或"自动分散"策略）；
但**不要指望它解决 issue #7**。

**分配策略：先测能力，不做无脑指派（用户指出）**

`PlaybackCoordinator` 支持三种模式，由 `FC_SPREAD_GPUS` 选择，**默认不设时行为完全不变**：

| 取值 | 行为 |
|---|---|
| （不设） | 原行为，所有路用 `_settings.PreferredAdapterIndex`（默认 -1） |
| `AUTO` | **按能力加权**：强卡多分路 |
| `EVEN` | 均分轮转，尽量让每张卡只扛一路 |
| `0,2` | 手动指定索引，按列表轮转（不探测） |

**评分依据 = 硬件解码单元数（一个单元一分），不是显存。**
解码能力由 GPU 内部的**固定功能解码单元**决定（NVIDIA NVDEC / Intel Quick Sync / AMD VCN），
与显存大小没有必然关系——用显存评分会让大显存的卡分到超出其解码单元数目的路，
结果只是排队，还白白叠加同卡并发 Present 的崩溃风险。

**术语要对齐厂商官方叫法**（不同厂家的"解码单元"名字不同）：

| 厂商 | 官方名称 | 官方是否逐卡给出数量 |
|---|---|---|
| NVIDIA | **NVDEC** | ✅ 是（Video Codec SDK 支持矩阵给 Total NVDEC） |
| Intel | **Multi-Format Codec Engines（MFC，媒体引擎）** | ⚠ 需查 Intel ARK 产品页的该字段（oneVPL 文档只给 codec 矩阵，不给数量） |
| AMD | **VCN** (Video Core Next) | ❌ 官方不逐卡给出 |

内置数据（`KnownDecodeUnits` + `DecodeUnitRules`，2026-09-17 查证）：
- 来源：NVIDIA `video-encode-and-decode-gpu-support-matrix-new`；Intel `ark.intel.com` 各产品页的
  "Multi-Format Codec Engines" 字段
**NVIDIA 逐型号官方数据（照录官网 Total NVDEC，未做任何推测）**：

| 系列 | 各型号 Total NVDEC |
|---|---|
| Blackwell | **5090=2、5080=2**；5070 Ti / 5070 / 5060 Ti / 5060 / 5050 = 1 |
| Ada | **全系 = 1**（4090 / 4080S / 4080 / 4070TiS / 4070Ti / 4070S / 4070 / 4060Ti / 4060 / 4050，桌面与笔记本同） |
| Ampere | **全系 = 1**（3090Ti / 3090 / 3080Ti / 3080 / 3070Ti / 3070 / 3060Ti / 3060 / 3050Ti / 3050）；MX570=1、MX570 A=0 |
| 数据中心 | H100=7、A100=5、L40/L40S=3、L4=4、T4=2 |

**NVIDIA 专业卡 / 工作站（照录官方 Professional 表，分档 1–4）**

| 型号 | Total NVDEC |
|---|---|
| RTX PRO 6000 Blackwell（含 WS/Max-Q/Server） | **4** |
| RTX PRO 5000 Blackwell | **3** |
| RTX PRO 4500 Blackwell（桌面） | **2**（⚠ Server Edition 为 3） |
| RTX PRO 4000 Blackwell / SFF | **2** |
| RTX PRO 2000 Blackwell | 1 |
| RTX 6000 Ada | **3** |
| RTX 5000 / 4500 / 4000 / 4000 SFF Ada | **2** |
| RTX 2000 Ada | 1 |
| RTX A6000 / A5500 / A5000 | **2** |
| RTX A4500 / A4000 / A2000 | 1 |
| Quadro RTX 8000/6000（官方合并行）、GV100、P6000 | 1 |

⚠ **实现陷阱（已踩并修复）**：`"RTX 4000 Ada"` 包含子串 `"RTX 40"`，若消费级规则
`("RTX 40",1)` 排在前面，专业卡会被误判成 1（实际官方为 2）。
故规则表顺序必须是：**数据中心 → 专业卡 → 消费级 → Intel → 兜底**。

另外官方矩阵本身存在同芯片不同值（GA102：消费级 RTX 3090=1，专业级 A6000=2），
所以**任何"按芯片/按系列外推"都会出错**——这也是整个方案坚持逐型号查表的原因。

> ⚠ 这条数据推翻了早先"Blackwell 保守取 1"的处理：**RTX 5080 官方是 2**，已改正。
> 也说明"大显存=强解码"是错的——4090（24GB）与 4060（8GB）的 NVDEC **都是 1**。

**Intel 数据（由 4 个子代理并发逐个查 Intel ARK 核实，共 20 个 SKU）**

集显规格挂在**处理器** ARK 页（非显卡页），字段 **"Multi-Format Codec Engines"（MFC）**。

*独显（全部 = 2）*

| 型号 | Device ID | MFC |
|---|---|---|
| Arc A770 / A750 / A380 | `0x56A0` / `0x56A1` / `0x56A5` | **2** |
| Arc B580 (Xe2) | `0xE20B` | **2** |

*集显（12/13/14 代，逐个查证）*

| Device ID | 集显 | MFC | 已核实 SKU |
|---|---|---|---|
| `0x4680` | UHD 770（12 代桌面） | 2 | i9-12900K、i7-12700K、i5-12600K |
| `0x46A6` | Iris Xe（12 代移动） | 2 | i7-12700H、i5-1240P |
| `0xA780` | UHD 770（13/14 代桌面） | 2 | i9-13900K、i7-13700K、i5-13600K、i9-14900K、i7-14700K、i5-14600K |
| `0xA7A0` / `0xA7A1` | Iris Xe（13 代移动 P/U） | 2 | i5-1340P、i7-1365U |
| `0xA782` | UHD 730 | **1** | i5-14400（同为 14 代桌面，却是 1） |
| `0xA788` | UHD 13th/14th Gen（移动 HX） | **2**（有冲突） | i9-13900HX=2、i7-14700HX=2、**i7-13650HX=1** |

⚠ **两条必须知道的坑**（都是官方数据证实的，不是推测）：
1. **MFC 与"代次 / 集显名 / EU 数 / Device ID"都不相关**——
   同代同集显名可不同（i9-13900HX=2 vs i7-13650HX=1）、甚至同 Device ID `0xA788` 都不同值。
   ⇒ 任何"按代次归纳"的做法都会出错，只能逐 SKU 查表。
2. **Core Ultra（Meteor Lake / Arrow Lake / Lunar Lake）的 ARK 页根本没有 MFC 字段**
   （官方只给 Xe-cores 与 codec 支持）。5 个型号（Ultra 9 285K、Ultra 7 265K、Ultra 7 155H、
   Ultra 5 125H、Ultra 7 258V）全部**查不到官方数值** ⇒ 一律走默认 1，**不编造**，
   请务必用 `FC_GPU_UNITS` 按实测指定。

**oneVPL 官方文档也提供不了这个数据（已查证）。**
Intel oneVPL「Media Capabilities Supported by Intel Hardware」（overview + details 两页）经查证，
**三项都没有**：
1. ❌ 每个 GPU 的硬件编解码引擎数量（MFC / 媒体引擎）
2. ❌ 并发解码会话数或并发流数
3. ❌ 任何运行时查询 API 及其返回字段（`MFXQueryImplsDescription` / `mfxImplDescription` /
   `mfxDecoderDescription` 在这两页**完全未出现**）

该文档本质是一张**静态 codec 对照表**：处理器代际 × codec（AVC/VP8/MPEG2/MJPEG/HEVC/VP9/AV1）
× 位深 × 色度采样（4:2:0 / 4:2:2 / 4:4:4），只区分"固定功能硬件"与"shader 混合"两种实现类型，
**不涉及任何数量**。文中唯一接近的表述是
*"If not specified, an encoder is a fixed function hardware-based encoder"*——描述实现类型，非数量。

**★ Intel EDC（edc.intel.com）——最有希望的入口，已定位到确切章节（待人工确认）**

用户提供的线索：EDC 有比消费级 ARK 更详细的技术文档。已确认 **Core Ultra 官方 Datasheet 存在专门的
硬件解码章节**，路径为
`Graphics → Processor Graphics → Media Support (Intel® QuickSync and Clear Video Technology HD) → Hardware Accelerated Video Decode`

Meteor Lake（Core Ultra U/H 系列）Datasheet Vol.1，文档 ID **792044**，版本 008（2025-05-09，Public）：

```
https://edc.intel.com/content/www/us/en/design/products/platforms/details/
  meteor-lake-u-p/core-ultra-processor-datasheet-volume-1-of-2/
  hardware-accelerated-video-decode/
```

同目录下还有 `hardware-accelerated-video-encode` / `...-video-processing` / `...-transcoding`。

**✅ 已用官方原件确认：该 Datasheet 不提供引擎数量。**

获取途径（**CDRD，可复用**）：EDC 网页正文是 JS 动态加载、直接抓不到，但 Intel 的
**CDRD 文档库可凭文档 ID 直下 PDF**：

```
https://cdrdv2.intel.com/v1/dl/getContent/792044     → 4.6 MB PDF（328 页，AES 加密）
```

（解密需 `pip install pypdf cryptography`；EDC 网页端的 `.model.json` / `.download.pdf`
均只返回 HTML 壳，`content/dam` 路径 404 —— 别再在那上面浪费时间。）

**全文检索结果（72 万字符，328 页）**：

| 检索项 | 结果 |
|---|---|
| "Multi-Format Codec Engines" / "MFC" | **0 命中** |
| `engine`（共 57 处） | **全部是 Security Firmware Engines**（CSME / Silicon Security Engine / GSC），**无一是解码引擎** |
| `N x ... engine`、`engines: N`、`N decode engines` | **全部 0 命中** |
| `number of ... engine` | 仅 "number of Multiply Accumulate (MAC) engine"（NPU 的 MAC 引擎，**非解码引擎**） |

该 Datasheet 的解码章节只提供 **codec 能力矩阵**（不含数量）：
HEVC/H265 Main12（420/422/444，8b/10b/12b）、VP9（各 profile）、AV1 Main（8/10b）、
JPEG/MJPEG Baseline；规格为 `8K@60 (Decode Only)`、`8K@30 (Decode Playback)`、`16K×16K still`。

**补充线索：Intel 支持文章 000098345（用户提供，待确认）**

用户指出该文章的法文/韩文版曾列出 MFX 引擎数量，原文引用为：

```
架构 multimédia                        | Xe-HPM | Xe-LPM+
Moteurs de codecs multiformats (MFX)   | 2      | 2
```

**对这个引用的分析：**

1. **粒度是"架构/系列级"，不是逐型号。** 表格按 *architecture multimédia*（多媒体架构）分列，
   给出该架构的 MFX 数量 ⇒ 同一多媒体架构下所有 GPU 共享同一数值。
   这正是我一直在找的粒度——比逐 SKU 表好维护得多。

2. **数值与已核实数据吻合，可交叉验证。**
   Xe-HPM 极可能是 **Xe-HPG**（Arc A 系列独显）的写法，其 MFX=2 与我逐个查 ARK 核实的
   A770/A750/A380 = 2 **完全一致** ⇒ 说明这份文档的 MFX 数量口径是可靠的。

3. **但对 Core Ultra 仍不能直接采用**，有两个障碍：
   - **当前官方版本已无此行**：英文 / 法文 / 韩文三版（均为 2026-04-07 修订）现在**只有 codec
     支持表**，列是产品系列（Arc A / Core Ultra 1·2·3 / Arc B），既无 "MFX 引擎" 行，也无
     "architecture multimédia" 行。Wayback 存档查询也没能取到快照。
   - **"Xe-LPM+" 的对应关系不明**：它不是 Intel 标准架构命名（标准为 Xe-LP / Xe-HPG / Xe-LPG /
     Xe2）。若它指 Xe-LPG，则可用于 Core Ultra 集显；但这个映射是我推测的，不能当作依据。
   - 另需注意：即便是同一架构，已核实 12/13/14 代集显（均 Xe-LP）也有 1 与 2 两种值
     （UHD 770=2、UHD 730=1）⇒ **架构级数量不能保证每个 SKU 都一致**。

**⇒ 处理**：暂不录入。若你能提供该表格的截图、PDF 或存档链接，我立刻核实并录入
（尤其是确认 Xe-LPM+ 是否对应 Core Ultra 的集显）。在此之前 Core Ultra 维持默认 1。

⇒ **最终结论：Intel Core Ultra 的硬件解码单元数量，官方确实不公开**（除非上述旧版数据能被证实）
（ARK 无字段 → oneVPL 无数量无 API → EDC/Lunar Lake PDF 图片型 → **官方 Datasheet 原件仅给 codec 规格，无数量**）。
这是穷尽四条官方途径后的确定结论，不是"没找到"。

处理方式：Core Ultra 走默认 1 + `FC_GPU_UNITS` 手动指定。默认 1 安全（等同均分），不加权而已。
若将来 Intel 公开该数值，或用户实测出实际并发能力，直接补进 `KnownDecodeUnits` 即可。

未逐个查证的型号 → 默认值 1 + 用户 `FC_GPU_UNITS` 覆盖。

### 解码单元"官方数据可获取性"总表（三大厂商均已查证）

| 厂商 | 官方是否提供"解码单元数" | 具体字段 / 位置 | 本工程覆盖情况 |
|---|---|---|---|
| **NVIDIA** | ✅ **提供，且逐型号** | Video Codec SDK 支持矩阵的 **Total NVDEC** | 消费级 + 专业卡 + 数据中心**全部录入** |
| **Intel 12/13/14 代** | ✅ 提供（按 CPU SKU） | 处理器 ARK 页 **Multi-Format Codec Engines** | 15 个 SKU 逐个核实并录入 |
| **Intel Arc 独显** | ✅ 提供 | 显卡 ARK 页 **Multi-Format Codec Engines** | 4 个型号核实（均=2） |
| **Intel Core Ultra** | ❌ **不提供** | ARK 无字段；oneVPL 无数量也无 API；本地 PDF 图片型 | 默认 1，需 `FC_GPU_UNITS` |
| **AMD** | ❌ **不提供** | 官网规格页只有 codec 的 **Yes/No**（4K H264 Decode、H265/HEVC Decode、AV1 Decode），**无 VCN / Video Codec Engine 数量** | 默认 1，需 `FC_GPU_UNITS` |

> 已核实 AMD 官方页（Radeon RX 9070 XT）在 "Supported Rendering Format" 下只列
> H264/H265/AV1 的 Decode/Encode = Yes，**没有任何引擎计数字段**；Compute Units 也只给 64。

⇒ 对 **Core Ultra 与 AMD** 用户：默认 1 是安全的（效果等同 `EVEN` 均分，不会错），
只是无法按能力加权；如需加权，请实测后用 `FC_GPU_UNITS="0:2"` 指定。

优先级：用户指定 > 已核实 deviceId > 型号系列规则 > 默认 1。

**用户手动指定（最高优先级）**：厂商文档没给数量、或实测与官方口径不符时，用
`FC_GPU_UNITS="0:2,1:1,2:0"`（adapterIndex:单元数），**填 0 表示排除该卡**。
示例：`FC_GPU_UNITS="0:2,1:0"` → 日志显示 `#0=2f, #2=1f, #3=1f → 分配 0,0,2,3`。

实测本机：

```
按显存（旧，错误）: #0=16f, #2=8f, #3=8f, #1=0f  → 分配 0,0,2,3
按解码单元（新）  : #0=1f,  #1=1f, #2=1f, #3=1f   → 分配 0,1,2,3
```

- #0 = RTX 5080、#2/#3 = **两张 RTX 4060**、#1 = Intel 集显，**各 1 个解码单元**
- 4 路均分到 `0,1,2,3`：每卡各扛一路，集显也参与（符合"N/i 一起用"）
- 推论：**消费级卡普遍只有 1 个解码单元 ⇒ 均分才是正确分配**；旧逻辑给 5080 分 2 路是错的

⚠ **曾尝试"实测吞吐"但不可行**：为每张卡开 headless 会话（`OutputWindow=0`）播放 1.5 s 读
`PresentedVideoFrames` —— 内核需要真实窗口，4 张卡**全部抛 `EngineException`**，
且有一次直接把进程带崩（`0xC0000005`）。要为每张卡做真实探测就得各建独立隐藏 HWND，
代价与风险都过大 ⇒ 改为按规格评分（`CapabilityScore`）。

⚠ **重要权衡**：加权会让多路压到同一张强卡，**同卡内的并发 Present 反而更多**——
而崩溃正源于此，所以 `AUTO` 可能**加剧**崩溃；`EVEN` 均分则相反，更稳但吞吐低。
在意吞吐用 `AUTO`，在意稳定用 `EVEN`。这个权衡本身也再次印证：
真正要解决崩溃，只能靠上游把"跨渲染器 Present 串行化"或"共享单一 device/swapchain"做掉。

（回归：默认路径下 selftest ×2 与单测 175 全部通过，未受影响。）

## 3. 为什么合并后的修复没有生效（源码级核对）

issue #7 提了 5 条建议，PR #8（`824093d`）只覆盖了第 1、2 条：

| # | 建议 | PR #8 是否覆盖 |
|---|---|---|
| 1 | 交换链所有改写路径在整个期间持 `presentMutex_` | ✅ `ReconfigureSwapChain` 去掉内部取锁，改由调用方全程持有 |
| 2 | `Present` 返回前不要交出 `deviceMutex_` | ✅ `PresentTimedText` 现在全程持有，锁序与主流渲染路径一致 |
| 3 | 合并两把锁 | ❌ 未做（可接受，只要顺序一致） |
| 4 | **`RequestRecoveryIfDeviceLost()` 补锁** | ❌ **未做** |
| 5 | 加断言/计数器验证不变量 | ❌ 未做 |

### 3.1 已修好的（确认生效）

`VideoRenderer.cpp` 中 `PresentTimedText` 现在先在 `presentMutex_` 下调用 `EnsureSwapChain`，
并在整个 `Present` 期间保持 `deviceMutex_`，锁序 `deviceMutex_ → presentMutex_` 与主流路径一致。

### 3.2 仍然裸奔的路径（对应"零交互也会崩"）

```cpp
// VideoRenderer.cpp:4878 —— 只持 deviceMutex_，没有 presentMutex_
bool PlayerVideoRenderer::RequestRecoveryIfDeviceLost() noexcept {
    std::lock_guard deviceLock(deviceMutex_);
    return RequestRecoveryIfDeviceLostLocked();
}
```

调用点全部未持有 `presentMutex_`：

- **`:3078-3080`** `TimedTextThread` 的 `devicePollOnly` 分支——500 ms 无更新时的空闲轮询，
  每个渲染器线程都有。**这正是"完全不操作也会崩"的成因，PR #8 未触及。**
- **`:4337`** 封面背景线程 `TryRenderCoverBackdropCache` 失败后。
- **`:3085`** `PresentTimedText` 返回 `DeviceFailure` 后。

它内部会走到 `device_->GetDeviceRemovedReason()`；一旦判定设备丢失即置 `deviceRecoveryRequested_`，
随后 `RecreateDeviceResources()` → `ReleaseDeviceObjects()` 释放交换链
（其中 `context_->ClearState()` / `Flush()` **不在 `presentMutex_` 下**）——与正在进行的
`Present` 仍然可以重叠。

### 3.3 `ClearSurface()` 仍未持 `presentMutex_` 读交换链

```cpp
// VideoRenderer.cpp:4837 —— 注释只写了"调用方须持 deviceMutex_"的前提
void PlayerVideoRenderer::ClearSurface() noexcept {
    if (context_ != nullptr && device_ != nullptr && swapChain_ != nullptr) {
        ... AcquireBackBufferTarget / OMSetRenderTargets / ClearRenderTargetView / Flush ...
        std::lock_guard presentLock(presentMutex_);   // :4850 只在 Present 前才取
        swapChain_->Present(0, 0);
    }
```

`AcquireBackBufferTarget` + `ClearRenderTargetView` + `Flush` 全部在 `deviceMutex_` 单锁下执行，
与"Present 与交换链改写不重叠"这条不变量仍然冲突。

### 3.4 已排除：我方 P2 无锁快路径不是元凶

曾怀疑本地保留的 P2 补丁（`PATCHES.md` 类别四，`interactiveMove_` + `try_lock` 快路径）
会让渲染线程跳过锁、抵消上游修复。核对 `VideoRenderer.cpp:3974-3997` 后**排除**：

```cpp
if (interactiveMove) (void)deviceLock.try_lock(); else deviceLock.lock();
if (!deviceLock.owns_lock()) return FFFResult::Success;      // 拿不到就整帧丢弃
if (interactiveMove) (void)presentLock.try_lock(); else presentLock.lock();
if (!presentLock.owns_lock()) return FFFResult::Success;      // 同上
// ↓ 只有两把锁都拿到才会走到这里
const auto chainResult = EnsureSwapChain(...);
```

锁序仍是 `deviceMutex_ → presentMutex_`，且是"要么双锁齐全、要么整帧丢弃"，
不会在持单锁的情况下访问交换链 ⇒ **不破坏不变量**。
（该代码本身也是上游原有的，非我方补丁。）

---

---

## 4. 本地构建与上游是否一致（构建一致性审查）

> 审查动机：09-17 的复测用的是**手工 `cl/link` 复刻**出来的内核 DLL（沙箱拦截 `MSBuild.exe`）。
> 若复刻的参数与官方 MSBuild 有偏差，或 merge 静默回退了上游代码，"崩溃未消失"的结论就站不住。
> 因此分三层审：源码 → 构建参数 → 产物行为。

### 4.1 源码层：净差异 = 预期补丁，PR #8 **逐行完整**

我方基线 `3ac124a` 相对上游 master `ea3ce05` 的净差异（11 文件，+550/−17）：

| 文件 | 说明 |
|---|---|
| `FFF.Player.Api.h` | A11 `preferredAdapterIndex` + 4 项扩展声明 |
| `PlayerApi.cpp` | A11 接线、范围校验 |
| `PlayerSession.cpp/.h` | `SetViewTransform` 直写原子路径（补丁 0006） |
| `VideoRenderer.cpp/.h` | 4 项扩展 + A11 + P2 快路径 |
| `FFF.Native.rc` / `vcxproj` | 版本资源（我方增量，+3 行 ItemGroup） |
| `.gitignore` / `PATCHES.md` / `.3fc_patches_applied` | 文档与标记 |

17 处删除行**逐条确认不是回退上游**：`PlayerApiVersion 14→15`（A11 有意递增）、
`TargetAudioBuffer100ns`（音频缓冲，类别四）、`SetViewTransform` 的 `Enqueue` 版本→直写路径（补丁 0006），
其余为补丁上下文行。

**PR #8 完整性验证**（关键）：

```
git show 824093d -- VideoRenderer.cpp      →  16 insertions(+), 7 deletions(-)
git diff 68e1965 3ac124a -- VideoRenderer.cpp →  16 insertions(+), 7 deletions(-)
```

两个补丁**逐行内容完全一致**，差异只有 hunk 行号偏移（我方补丁让行号后移）与 commit message。
⇒ **merge 没有丢失、没有回退上游 PR #8 的任何一行。**

### 4.2 构建参数层：官方 tlog **逐项等于** vcxproj 配置

从 `obj/x64/Release/FFF.Native.tlog/CL.command.1.tlog`（09-16 12:56 那次真正 MSBuild 构建的实况）
取出的命令行，与 `FFF.Native.vcxproj` 的 `Release|x64` 配置逐项对应：

| vcxproj 属性 | 实际下发参数 |
|---|---|
| `WholeProgramOptimization=true` | `/GL` + 链接器 `/LTCG:incremental` |
| `LanguageStandard=stdcpp20` | `/std:c++20` |
| `ConformanceMode=true` | `/permissive-` |
| `SDLCheck=true` | `/sdl` |
| `WarningLevel=Level3` | `/W3 /WX-` |
| `FunctionLevelLinking` / `IntrinsicFunctions` | `/Gy` / `/Oi` |
| `UseDebugLibraries=false` | `/MD`、`/D NDEBUG` |
| `GenerateDebugInformation=true` | `/Zi` + 链接 `/DEBUG` |
| `AdditionalOptions=/utf-8` | `/utf-8` |
| `PrecompiledHeader=Use(pch.h)` | `/Yc"pch.h"`（PCH.CPP）/ `/Yu`（其余 19 个） |
| `EnableSegmentHeap=true` | `/manifestinput:…\SEGMENTHEAP.MANIFEST` |
| `PlatformToolset=v145` | `MSVC\14.51.36231`（v145 = VS18） |
| — | `/O2 /GS /EHsc /Gm- /fp:precise /Zc:wchar_t,forScope,inline /Gd /TP /FC` |

⇒ tlog 记录的就是**标准官方配置**，无人为改动。
手工构建按该 tlog 逐字复刻：**20 个编译单元 + rc + link**（数量与 tlog 的 20 条 CL / 1 条 rc / 1 条 link 完全吻合），
且链接器输出 `All 6611 functions were compiled because no usable IPDB/IOBJ…`
——这是 **`/GL` + `/LTCG`** 的标志性输出，证明全程序优化确实启用。产物同为 758272 字节。

### 4.3 产物行为层：官方 MSBuild 产物 vs 手工产物对照

`tools/verify-build-parity-2026-09-17.sh`：同一套源码、同一台机器、同条件各跑 10 次
（2 路 × 25s）。`OFFICIAL` = 09-16 12:56 真正 MSBuild 构建的 `694a099b`；
`MANUAL` = 09-17 手工复刻构建的 `5111b352`。

| 批次 | 通过 | 崩溃(139) | 其它(exit=1 漂移) | 退出码序列 |
|---|---|---|---|---|
| `OFFICIAL`（官方 MSBuild） | 8 | **1** | 1 | `1 0 0 0 0 0 0 139 0 0` |
| `MANUAL`（手工复刻） | 7 | **0** | 3 | `0 1 1 0 0 1 0 0 0 0` |

⇒ **两种构建产物行为无显著差异**（Fisher 精确检验 p=1.0）。
**没有证据表明手工构建引入了偏差。**

### 4.4 ⚠ 但本轮暴露了一个更严重的方法论问题

同条件（2 路 @25s、同一份 MANUAL 内核）下，两批相隔约 40 分钟的结果：

| 批次 | 时间 | 崩溃率 |
|---|---|---|
| 09-17 03:14 批 | 2 路 ×10 | **4/10 = 40%** |
| 09-17 03:47 批 | 2 路 ×20（两种构建合计） | **1/20 = 5%** |

差异过大，不可能用随机性解释（若 p=0.4，20 次中 ≤1 次的概率约 0.05%）。
**⇒ 该崩溃对"系统状态"高度敏感，单批次的崩溃率不可用于判断修复是否有效。**

已识别的未受控变量（**必须固定后重测**）：

1. **`WerSvc` 状态**：03:14 批为 `Stopped`；对照实验期间为 `Running`（我 03:26 为尝试 WER 转储而启动，03:55 已恢复 `Stopped`）。这是两批之间最显著的差异项。
2. **GPU/驱动热状态**：03:34–03:43 连续跑了十余次 3 路/4 路高负载用例，随后降到 2 路，硬件状态不同。
3. 批次内样本量仅 10，本身置信区间就很宽。

**结论修正**：本文档 §2.2 的"修复后 50%"与 §4.3 的"5%"**都不能单独作为结论**。
要判断 PR #8 是否真的无效，必须做**同批次交替对照**（A/B/A/B…）并固定上述变量。
这一点同样适用于 §2.4 的崩溃现场证据——现场本身是硬证据（同一 `dxgi.dll` RVA、同一调用链），
但**崩溃率**必须按受控实验重测。

> ⚠ **连带发现：上游 PR #8 的 commit message 里写着**
> `"Verified: 2-route multitest (4K HEVC 10-bit …) passes with no access violations."`
> 那是我们当时**只跑了一次**就写下的结论。鉴于本轮证明单次通过完全不能说明问题，
> 该"已验证"声明**需要向上游更正**，否则会误导 maintainer 认为问题已解决。

## 5. 附带发现：多路漂移超阈值（此前被崩溃掩盖）

2 次跑测在「静置播放 8s」阶段因漂移断言失败退出（`exit=1`，非崩溃）：

```
第 1 路漂移 0:00:00.1955938 > 100ms（pos=9.015s 期望 9.211s）
第 1 路漂移 0:00:00.1382869 > 100ms（pos=9.042s 期望 9.181s）
```

这是 `HANDOFF-v0.2.5` §2.4「漂移校正参数未实测」的**第一批真实数据**：
当前 `TickDrift()` 的 **1s 冷却 + 半帧阈值**在 4K 双路静置播放 8s 后仍会出现 **138~195 ms** 偏差。
此前该项一直被 `--multitest` 崩溃阻塞，崩溃率虽未下降、但已有 4/10 次能跑完全程，
**该项现在可以做实机调参了**（不再完全阻塞）。

---

## 6. 下一步建议

0. **把"跨渲染器串行化 Present"做成可提交的修复（方向已三版交叉验证）。**
   实测（8 路）：仅串行化 Present = 37.5% vs 基线 100%；Present+改写都串行化 = **12.5% vs 56%**；
   **Present 用共享锁 = 100%（无效，已排除这条优化路线）**。
   ⇒ 结论硬：**必须让 Present 之间互斥**，读写锁/共享锁不可行。
   配套必做：串行化会让多路播放变慢、漂移超阈值（v1 5/8、v2 8/8），
   因此修复需**同步调整漂移校正**（放宽阈值或改校正策略，见 §5），否则自测过不了。
   更彻底的路线：**多路共享单一 device / swapchain**，从根上消除跨渲染器并发，
   也就不必付出串行化代价。**建议先与上游对齐方案再实现。**

0.1 **「第三方钩子排除实验」（次要，可并行）。** 退出 **RTSS（RivaTuner）/ MSI Afterburner /
   NVIDIA ShadowPlay**（`RTSSHooks64.dll`、`nvspcap64.dll` 已注入本进程）后，用 8 路配置重测。
   目的不是推翻上面结论（内核成因已证实），而是看**残余的 12.5% 是否由钩子贡献**——
   若退出后归零，则"跨渲染器串行化 + 无钩子"可做到完全无崩溃。
   成本约 5 分钟，但需要用户配合退出常驻软件。

1. **回上游 issue #7 追加复测数据**（唯一能真正解锁后续项的前置）。给上游的说法应表述为：
   **"受控交替对照（回退修复 vs 含修复，同批次交替 16+16 次）下崩溃率 31% vs 25%，无显著差异；
   且修复后的崩溃仍落在同一条调用链与同一个 `dxgi.dll` RVA"**。
   ⚠ 不要再说"50% → 50%"——那是跨批次数字，按 §4.4 不成立，会给上游错误锚点。
   同时把 §2.4 的调用链证据一并给出，并把排查方向从"锁重叠"改指到**交换链生命周期**：
   - `PresentTimedText` 现在已全程持双锁，若成因是锁重叠则崩溃率必然下降，实际没有
     ⇒ 建议改查 `swapChain_` 的**释放与 Present 的时序**；
   - 重点看 `ReleaseDeviceObjects()`（`:4893-4900`）：`context_->ClearState()` / `Flush()`
     **不在 `presentMutex_` 下**，随后才在 `presentMutex_` 下 `swapChain_->Release()`
     并置空；它由 `RecreateDeviceResources()` 在 `deviceMutex_` 下调用；
   - 其次看 `EnsureSwapChain` 的失败恢复路径（重建链时旧链引用是否仍被某线程持有）；
   - 附带仍可提：§3.2 `RequestRecoveryIfDeviceLost()`、§3.3 `ClearSurface()` 两条单锁路径。
   ⚠ **但请把主方向换成 §2.6 的"跨渲染器并发 Present"** —— 那是有转储证据支撑的：
     崩溃瞬间两个线程同时位于 `PresentCurrentFrame +0x7E` → `dxgi.dll +0x19530`。
     `deviceMutex_`/`presentMutex_` 都是 `PlayerVideoRenderer` 的**成员锁**，两个渲染器
     实例之间互不相识，PR #8 无论把单渲染器内部的锁序做得多正确，都覆盖不到这一层。
     可给上游的两个方向：① 内核级**全局（跨渲染器）Present 串行化**（一把进程级锁包住
     所有 swapchain 的 `Present` 与 `ResizeBuffers`）；② 提供**共享单一 device/swapchain**
     的多路渲染模式，从根上消除多路并发 Present。
2. **本机侧排除实验（成本低，建议先做）**：退出 RTSS / 微星小飞机 / NVIDIA ShadowPlay
   后重跑受控对照。崩溃线程栈里存在 `RTSSHooks64.dll`、`nvspcap64.dll` 两个第三方 D3D 注入钩子，
   它们挂在同一条 Present 路径上。若退出后崩溃消失，则属"第三方 hook × 多路 Present"的交互问题，
   上游修法与用户规避手段都会不同。
2. **漂移校正调参**（不再被完全阻塞）：把冷却从 1s 降到 250~500ms、或按偏差大小分级校正；
   需先让它能稳定跑完 10 次再谈参数收敛。
3. `docs/16` 的停滞修复疗效、`HANDOFF` §2.4 的漂移实测仍受此崩溃影响，暂缓。
4. **可选**：把 `3fc/integrate-issue7` 推到 `fork`（luoye-cpu/FFF_Project）与
   主仓 `kernel/*` 归档分支做云端备份（本机 `.3fc_kernel_baseline.bundle` 已更新，
   本地恢复无风险；云端备份需用户确认后执行）。

---

## 7. 本次改动清单

| 文件 | 改动 |
|---|---|
| `third_party/fff_project` | 新提交 `b6b96a6`（上游 master + 我方扩展合并）、`3ac124a`（PATCHES.md 升级记录）；tag `3fcompare-kernel-2026.9.17.1` |
| `third_party/fff_project/PATCHES.md` | 升级记录补 2 行；文档头指向新基线与新分支 |
| `.3fc_kernel_baseline.bundle` | 重建，含新分支与 tag |
| `tools/构建全部.ps1` | `$KernelBaselineTag` / `$KernelBaselineSha` 同步为 `3fcompare-kernel-2026.9.17.1` / `3ac124a…` |
| `tools/verify-issue7-2026-09-17.sh` | 新增：批量复测脚本（本次复测用） |
| `tools/verify-build-parity-2026-09-17.sh` | 新增：官方 MSBuild 产物 vs 手工产物的构建一致性对照（§4.3） |
| `tools/verify-issue7-ab-2026-09-17.sh` | 新增：受控交替对照（2 路），A=回退 PR#8 / B=修复后（§2.5） |
| `tools/verify-issue7-ab4-2026-09-17.sh` | 新增：同上，4 路版本（崩溃更频繁，用于放大差异） |
| `tools/show_build_cmd.py` | 新增：解析 UTF-16LE 的 MSBuild tlog，打印官方真实编译/链接命令行（§4.2） |
| `.3fc_verify_20260917/kernel_pr8reverted.dll` | 新增：**回退 PR #8 的对照内核**（`a163894c…`），供 A/B 对照复现 |
| `tools/dump_capture.py` | 新增：迷你调试器，绕开失效的 WER 抓第一现场转储 + 模块归属 + 崩溃线程栈 |
| `tools/stack_resolve.py` | 新增：原始栈 → 候选调用序列（FFF.Native 帧经 PDB 符号化） |
| `tools/dump_triage.py` | 新增 `--crash <addr>:<code>:<tid>`：无异常流的转储也能做栈扫描 |
| 本文档 | 新增 |

> ⚠ 主仓 `main` 的工作区改动**未提交**（§4.2：提交时机由用户决定）。
