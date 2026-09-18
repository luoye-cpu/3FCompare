# 上游 issue #7 追加回复（草稿，待确认后发布）

> **状态：草稿，尚未发布。** 对外发布动作需用户确认后执行。
> 目标位置：<https://github.com/Lake1059/FFF_Project/issues/7>（中文）
> 相关：PR #8（`ea3ce05` / `824093d`）、`docs/18-上游PR8合并与issue7复测.zh.md`

---

## 中文正文（可直接粘贴到 issue #7）

首先感谢合并 PR #8。我们已完成合并后的完整复测，**结论是问题没有解决**，并且定位到了
PR #8 覆盖不到的那一层。以下是数据。

### 一、受控对照：崩溃率没有可测量的变化

我们造了一份"回退掉 PR #8"的对照内核（其余代码与含修复版本逐字节相同，API 同为 15），
与含修复版本在**同一批次内交替**部署运行，以消除系统状态漂移的影响
（固定 `WerSvc=Stopped`、每批前冷却 45s）：

| 场景 | 回退 PR #8 | 含 PR #8 |
|---|---|---|
| 2 路 @25s（各 8 次） | 崩溃 3/8 | 崩溃 1/8 |
| 4 路 @20s（各 8 次） | 崩溃 2/8 | 崩溃 3/8 |
| **合计** | **5/16（31%）** | **4/16（25%）** |

两个场景方向相反，合并后无显著差异（Fisher 精确检验 p=1.0）。

> 说明：我们此前在别处提过"修复前 52% / 修复后 50%"，那是**跨批次**比较得出的。
> 后续实测发现该崩溃对系统状态高度敏感（同一份内核同条件，两批相隔 40 分钟分别测得
> 4/10 与 1/20），跨批次数字不可用，特此撤回，以上面的受控对照为准。

### 二、更关键：崩溃现场与修复前完全相同

新的崩溃转储显示，修复后的崩溃仍落在**同一个 `dxgi.dll` RVA、同一条调用链**：

```
崩溃点   dxgi.dll                 +0x19530        ← 与修复前转储簇 A 同址
         FFF.Native.dll +0x3F27E  PlayerVideoRenderer::PresentCurrentFrame + 0x7E
         d3d11.dll                +0x15A75B
         FFF.Native.dll +0x3FA46  PlayerVideoRenderer::PresentTimedText  + 0x516
线程入口 FFF.Native.dll +0x6B7AA  _Cnd_timedwait_for_unchecked
```

另一次崩溃落在 `dxgi.dll +0x33AF3`（`0xC000001D` 非法指令），对应修复前的簇 B。

### 三、根因：跨渲染器的并发 Present，PR #8 的锁覆盖不到

`deviceMutex_` 与 `presentMutex_` 都是 `PlayerVideoRenderer` 的**成员锁**——
每个渲染器实例各有一套，实例之间互不相识。而 `EnsureDevice()` 里每个渲染器各自
`D3D11CreateDevice`，device / context / swapChain 也都是独立的。

崩溃瞬间的转储线程表证明了这一点（共 151 个线程）：

| TID | RIP | 栈内模块 |
|---|---|---|
| 81792（崩溃线程） | `dxgi.dll +0x19530` | fff.native + d3d11 |
| 96244（并发线程） | `d3d11.dll +0xAFFD8` | d3d11 + dxgi + fff.native |

把 96244 的栈符号化后：

```
FFF.Native.dll +0x3F27E   PlayerVideoRenderer::PresentCurrentFrame + 0x7E
dxgi.dll       +0x19535                    ← 距崩溃点仅 5 字节
```

**两个线程正同时执行 `PresentCurrentFrame` 里的 `chain->Present(...)`，同时进入
`dxgi.dll +0x19530` 区域。**

这解释了全部现象：

- 单路从不崩溃（只有一个 Present 线程）；
- 多路才崩溃（≥2 个渲染器 = ≥2 个 Present 线程，各自的锁管不到彼此）；
- PR #8 把单渲染器内部的锁序修得很正确，但**对跨渲染器并发一条都没碰**，所以无效；
- 崩溃点在 `dxgi.dll` 而不是我们的代码；
- 崩溃率与路数关系不大（2 路已足以形成并发）。

### 四、已实测：跨渲染器串行化有效（但当前实现性能不可接受）

我们做了一版实验性内核改动：新增一把**全进程共享的递归锁**，同时串行化
① 所有 swapchain 的 `Present`（`PresentCurrentFrame` 与 `ClearSurface`）与
② `EnsureSwapChain` 里的交换链改写（`ReconfigureSwapChain` / `ResizeBuffers` / `CreateSwapChain`）。

⚠ 先说一个我们踩过的坑：**4 路场景下基线崩溃率只有约 15%，完全分不出差异**，
我们据此一度误判"串行化无效"。换成 **8 路**（基线崩溃率 56%~67%）后判别力才够：

| 轮次 | 串行化版 | 基线 |
|---|---|---|
| 8 路 第 1 轮（各 8 次） | 崩溃 2/8 | 崩溃 3/8 |
| 8 路 第 2 轮（各 8 次） | 崩溃 **0/8** | 崩溃 **6/8** |
| **合计** | **2/16（12.5%）** | **9/16（56%）** |

两轮方向一致，Fisher 精确检验 **p ≈ 0.008**。

⇒ **跨渲染器并发确实是触发条件，这一点已被内核改动证实。**

⚠ 但**这版实现不能直接提交**：全局串行化让播放变慢，8 路下 8/8 都触发了
"多路漂移 >100ms"的断言（基线只有 2/8）。崩溃没了，性能不可接受。

我们进一步试了三种粒度，用 8 路同批次对照（基线本身在 56%~100% 波动，故比较同批次相对差）：

| 版本 | 串行化范围 | 崩溃率 | 同批次基线 |
|---|---|---|---|
| v1 | 仅 `Present` | 3/8（37.5%） | 8/8（100%） |
| v2 | `Present` + 交换链改写 | **2/16（12.5%）** | 9/16（56%） |
| v3 | `Present` **共享锁** + 改写独占 | **8/8（100%）** | 7/8（87.5%） |

**v3 无效是关键结论：并发 `Present` 本身就是触发条件，不能让 Present 共享**
（读写锁路线已排除）。v1、v2 都有效，v2 最好。

**建议方向（按我们的实测排序）：**

1. **串行化所有 `Present`（必需）**，并把交换链改写一并纳入（v2 效果最好）。
2. 更彻底的方案：**多路共享单一 device / swapchain**，从根上消除跨渲染器并发，
   也就不必付出串行化代价。
3. ⚠ **但串行化不能作为最终修复——它在多路下是架构性不可行的。**
   2 路（日常场景）实测：串行化版崩溃 0/8（基线 3/8），但"多路漂移 >100ms"
   从基线 0/8 升到 **3/8**；8 路下更是 8/8。
   原因：`Present(1, 0)` **同步等待 vsync**。并行时 N 路在同一个 vsync 点一起 Present、
   共约 16ms；串行化后变成**顺序等待 N 次 vsync**（约 N×16ms），帧率被压到 60/N。
   ⇒ 加锁只是把"随机崩溃"换成了"确定性掉帧"。

我们另外试了 v4：把常态 `Present(1, 0)` 换成 `Present(0, DXGI_PRESENT_DO_NOT_WAIT)`，
让持锁时间从 ~16ms 降到近乎为零（二进制确认 `SyncInterval=1` 已消失）。
崩溃同样能消除（2 路、8 路均 0/8），但**漂移比 v2 更差**（2 路 5/8，v2 为 3/8，
基线 0~2/8）——非阻塞会丢帧，播放推进反而不均。⇒ **同步串行化（v2）是这条路线的
最优解，非阻塞优化不可取。**

4. **我们建议的唯一正确方案：多路共享单一 device / swapchain。**
   各路渲染到同一个 swapchain 的不同区域，**一次 `Present` 完成所有路**——
   既消除跨渲染器并发，又不掉帧。这也符合"多路对比播放器"的天然需求
   （本来就是同一个窗口里的多个画面）。
   若短期只求止血，可在**低路数**（≤2）下串行化并同步放宽漂移阈值，但这只是权宜。

另外，本进程内存在第三方 D3D 注入钩子（`RTSSHooks64.dll` / `nvspcap64.dll`）。
我们尚未排除它们在残余 12.5% 中的贡献，会另做排除实验。

### 五、需要更正的一处

PR #8（`824093d`）的 commit message 里写着
`Verified: 2-route multitest … passes with no access violations`。
那是我们当时**只跑了一次**写下的，按上面第一节的标准并不足以支撑"已验证"。
鉴于该结论可能让 maintainer 误以为问题已解决，特此更正。

### 六、环境

- Windows 11 24H2（26100）、`dxgi.dll` 10.0.26100.9444、NVIDIA RTX 5080
- 内核：上游 master `ea3ce05` + 3FCompare 扩展（A11 等，API 15），MSVC 14.51 / v145，Release x64
- 素材：4K HEVC 10bit 60Mbps 与 4K H.264 实拍片
- ⚠ 进程内存在第三方 D3D 注入钩子：`RTSSHooks64.dll`（RivaTuner/微星小飞机）、
  `nvspcap64.dll`（NVIDIA ShadowPlay）。我们下一步会先在**退出这些软件**的环境下复测，
  以区分"内核多路 Present 本身"与"第三方 hook × 多路 Present 的交互"。
  若贵方能复现，也建议先确认这一点。

---

## English summary（如需英文补充）

After PR #8 was merged we re-tested with a controlled A/B: we built a control kernel with
PR #8 reverted (byte-identical otherwise, same API 15) and ran it **interleaved** with the
fixed kernel in the same batch. Crash counts were 5/16 (reverted) vs 4/16 (fixed) — no
significant difference. Post-fix crashes land on the *same* `dxgi.dll` RVA (`+0x19530`) and
the same call chain (`PresentTimedText` → `PresentCurrentFrame +0x7E`).

The dump shows why: at crash time **two threads are simultaneously** inside
`PresentCurrentFrame + 0x7E` heading into `dxgi.dll +0x19530`. `deviceMutex_`/`presentMutex_`
are *per-renderer member* locks, and each renderer creates its own D3D device — so PR #8,
which only fixes lock ordering *within* one renderer, cannot cover the cross-renderer case.
That matches every observation: single route never crashes, ≥2 routes do, and route count
barely matters.

Suggested direction: serialize `Present` and swap-chain rewrites across renderer instances
(process-wide lock), or offer a shared-device / shared-swap-chain multi-route mode.

Also: PR #8's message says "Verified: 2-route multitest … passes" — that was a single run
and does not meet the bar; please disregard it.
