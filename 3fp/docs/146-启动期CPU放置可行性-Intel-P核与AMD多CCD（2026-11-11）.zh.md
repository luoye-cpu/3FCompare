# 146 · 启动期 CPU 放置可行性：Intel P-core 与 AMD 多 CCD（2026-11-11）

> **问题**：启动时把启动线程放进 P-core（Intel 大小核），AMD 多 CCD 机型放进"主 CCD"，能否提高启动性能与"启动瞬间打开"性能？可行吗？
>
> **结论**：
> 1. **机制上可行，而且大半资产是现成的**：内核 `PlacementPolicy` 已经实现了两类拓扑发现（Intel 效率类 / 多 L3 域即多 CCD）和 CPU Sets 的施加/恢复/负控，只是"只在播放期用、启动期没调用"。把它提前到启动期是复用，不是新造。
> 2. **但 CPU Sets 是"软亲和"**（官方口径见 §二）：调度器优先在集合内跑，必要时仍可越界；收益来自"启动线程尽量不落 E-core / 不跨 CCD 迁移 / L3 复用"，上限是 ms 级~十几 ms 量级，**必须 A/B 证实**。docs/145 已证明"CPU 排队"不是启动瓶颈，所以不要期待大钱。
> 3. **Intel 侧**：P-core 组=6C/12T（本机 i7-13650HX），把 UI 线程 + 内核 create/worker 线程软钉到 P 组即可；**不建议整进程默认**——GC/JIT/线程池/预取侧车会一起挤 P，E 核反而闲置。Windows 11 的 Thread Director + 前台提升已经做了不少，边际收益要实测。
> 4. **AMD 多 CCD 侧**：Windows **不暴露 CCD 编号**，只暴露 `LastLevelCacheIndex`（"共享至少一级缓存"的分组：Zen3/4 ≈ 一个 CCD，Zen1/2 = 一个 CCX）；**"主 CCD"没有权威公共定义**。可用的确定性定义只有"含最低逻辑处理器号的 L3 域"或"UI 线程当前所在域"；**X3D 上还有歧义**（96MB V-cache CCD 频率低、32MB CCD 频率高，选错可能更慢，而 Win32 读不到 CPPC 首选核排序）。⇒ AMD 只能做"软策略 + 开关 + 日志"，**不能默认猜**。
> 5. **多 group 机型（Threadripper 64C/128T 等）**：官方文档明确"CPU Set 赋值在 group 与线程 affinity mask 不匹配时会被忽略"⇒ 需要 group-aware 处理或直接 no-op 降级。

## 一、现成资产：`PlacementPolicy` 已经做过什么

| 能力 | 位置 | 说明 |
|---|---|---|
| Intel 效率类发现 | `PlacementPolicy.cpp:162-174` | `EfficiencyClass` 最高组 = 最快类（官方语义：数值越大越快、能效越低） |
| AMD 多 L3 域发现 | `PlacementPolicy.cpp:175-200` | `LastLevelCacheIndex` 分组；不采样负载就**主动放弃**（`ccd-layout-but-no-load-sampling`） |
| 施加机制 | `PlacementPolicy.cpp:543-572` | `SetProcessDefaultCpuSets`（进程默认，覆盖未显式选择的线程）+ `SetThreadSelectedCpuSets`（present 线程） |
| 分级/回退 | `Tier::Preferred/Spilled/Boosted` | 先只限 tier0，再放开到全部，最后才动优先级 |
| 负控/开关 | `FFF_PLACEMENT=off/prefer/spill/boost`、`FFF_PLACEMENT_TRACE` | 已有"关掉"基线臂、已有采样日志 |
| 调用时机 | `PlayerSession.cpp:9407`（播放开始） | **只有播放期**；启动期无任何调用 |

⚠️ **一个必须先补的账**：这套播放期策略的性能收益**至今没有 A/B 记录**（docs/124 只复核了它与启动期优先级的时序）。在把它搬到启动期之前，先用现成的 `FFF_PLACEMENT=off` 负控验证"播放期 tier0 到底省不省"——如果播放期都测不出，启动期更不该指望。

## 二、机制边界（官方文档口径）

- **CPU Sets = 软亲和**，"compatible with OS power management"：线程"typically"跑在所选集合的 home 处理器上，但集合被独占（Core Reservation）或负载失衡时会被调度到别处。因此它是**偏好**，不是 `SetThreadAffinityMask` 的硬钉；这也意味着收益不是确定的。
- **进程默认 vs 线程选择**：`SetProcessDefaultCpuSets` 只对"没有显式 `SetThreadSelectedCpuSets` 的线程"生效，**新建线程自动继承**；线程级选择覆盖进程默认；`NULL` 清除。
- **优先级高于 CPU Set 的两种东西**：①限制性 affinity mask（例如被 Process Lasso/启动器钉过）优先于 CPU Set；②多 group 系统上"group 与线程 affinity mask 不匹配的赋值被忽略"。
- **版本**：`GetSystemCpuSetInformation` / `SetThreadSelectedCpuSets` / `SetProcessDefaultCpuSets` 官方最低都是 **Windows 10 / Server 2016**；本仓 `FFF.Native` 已直接 import 它们（`PlacementPolicy.cpp`），所以"现网最低 OS 已满足"这件事本来就成立，启动期复用不新增兼容风险。虚拟机/容器/旧系统：`GetSystemCpuSetInformation` 给不出数据时 Discover 已有 `PLACEMENT_NONE` 静默降级路径。
- **能覆盖的时间窗有限**：managed 侧最早的钩子是 `MyApplication_Startup`（`ApplicationEvents.vb:21`），它之前已经发生 **apphost/CLR 初始化、R2R 映射、启动路径 JIT**——这一段无论怎么设 CPU set 都覆盖不到。启动期放置最多覆盖"Startup 事件 → 首帧"。
- **释放必须事件化**：首帧 presented / 首图 opened / Preroll 完成 / 上限超时（建议 3 s）；别再用固定延时（docs/145 的 1500 ms 教训）。

## 三、Intel P-core 臂：可行，但要"线程级"不要"进程级"

**能做什么**：`GetSystemCpuSetInformation` → `EfficiencyClass` 最大的一组（本机：12 个 P 线程 vs 8 个 E 线程）→ 对关键线程 `SetThreadSelectedCpuSets`（或进程默认，短期）。

**收益来源**：①启动早期窗口还没显示、应用还不是前台，Windows 没有前台提升，UI/内核 create 线程有可能落在 E-core；②减少 P↔E 迁移与 L3 冷启动；③内核设备创建（实测 150–170 ms 同步，见 docs/124 的预热消融 −446.6 ms）是 CPU+驱动同步路径，落 P 更稳。

**风险/反效果**：
- 进程默认一拉，**GC/线程池/JIT 后台线程/预取侧车/图层泵全部挤 P**，E 核闲置；启动并行段（LakeUI 表面创建、线程池）原本可以用 E 核分担。
- Windows 11 Thread Director 本来就把前台/交互线程优先放 P；显式集合的边际收益可能接近 0（与 docs/145 优先级实验同构）。
- 软亲和可能被调度器越界，收益不稳定。

**实现选项（由轻到重）**：
- C0：只 `SetThreadIdealProcessorEx` 给 UI 线程一个 P 核"理想处理器"提示（零限制、零风险，但最弱）。
- C1：UI 线程 + 内核 create 线程（都在主线程栈上）+ 内核 worker/present 线程各自 `SetThreadSelectedCpuSets` 到 P 组（推荐的最小臂）。
- C2：`SetProcessDefaultCpuSets` 到 P 组 + 首帧后清除（覆盖面最大，也最容易反效果）。

## 四、AMD 多 CCD 臂：机制可行，但"主 CCD"没有可靠定义

**能做什么**：`LastLevelCacheIndex` 分组 → 选一个域 → `SetProcessDefaultCpuSets`/`SetThreadSelectedCpuSets`。Zen3/4 上"一个 L3 域 = 一个 CCD"（8C/32MB），Zen1/2 上是一个 CCX（4C/16MB，两个 CCX 才是一个 CCD）。L3 大小可另用 `GetLogicalProcessorInformationEx(RelationCache)` 读，用来识别 X3D 的 96MB 域。

**"主 CCD"的五种候选定义，各有问题**：

| 定义 | 做法 | 问题 |
|---|---|---|
| 含最低 LP 的 L3 域（≈ CCD0） | 按 `LogicalProcessorIndex` 最小者所在域 | 只是"编号最小"，不代表更闲/更凉/更快 |
| UI 线程当前所在域 | `GetCurrentProcessorNumberEx` → 域 | 与 Windows 初始放置一致、局部性好；但可能正好落在一个忙 CCD |
| L3 最大（X3D 缓存域） | 缓存关系里比 L3 大小 | 7950X3D 的缓存 CCD 频率更低；启动以 JIT/R2R 代码取指+UI 为主，缓存与频率谁赢没有数据 |
| 频率最高 | `CallNtPowerInformation(ProcessorInformation).MaxMhz` | 现代 CPU 上不稳（PBO/同型号不同 CCD 同标称），X3D 两 CCD 可能同标称 |
| 最不忙 | 沿用播放期的 per-processor 采样 + 滞回 | 启动期没有历史窗口，采样本身要花时间；且"最闲"不等于"最快" |

**X3D 的额外风险**：`EfficiencyClass` 官方语义面向"异构处理器（如 ARM big.LITTLE）"，AMD 双 CCD **不保证**会给出不同 class（本仓在 AMD 上走的是 CacheDomain 分支）；Windows/AMD 的 "preferred core"（CPPC）排序**没有公共 Win32 读取接口**。所以"主 CCD"在 X3D 上只能靠 L3 大小/自测猜，猜错就是拿 5.0–5.7GHz 差去换 32–96MB L3——必须实机 A/B。

**多 group/Threadripper**：3990X 这类 128T 是多 group，官方明确跨 group 的 CPU Set 赋值会被忽略；`SetProcessDefaultCpuSets` 的集合若跨 group 也需要验证（文档只给了"忽略不匹配 group"的规则）。⇒ 实现里必须按 `Group` 过滤：只选当前 UI 线程所在 group 的域；否则 no-op。

**NUMA**：桌面 2-CCD 一般单 NUMA 节点（除非 BIOS 开了 NUMA 模式）；Threadripper 多 NUMA。`NumaNodeIndex` 可作为附加约束，但启动期收益证据不足。

## 五、与现有启动期 HIGH、内核 PlacementPolicy 的关系

- **CPU set 与 priority 正交**，可以共存；但建议先按 docs/145 删掉/改造启动期 HIGH，再评估 CPU set——否则两个启动期旋钮叠加，A/B 归因又回到 PR #48"两臂之和"的老问题。
- **所有权必须单一**：启动期放置（host 或 kernel startup 阶段）→ 首帧/打开完成后清除 → 播放期 `PlacementPolicy.OnPlaybackStart` 接管。别让 host 与 kernel 同时写进程默认 CPU set（`SetProcessDefaultCpuSets(NULL)` 会把对方的设置也清掉）。
- **音频/解码线程**：进程默认在播放开始前必须清掉，否则音频渲染、解码 worker 全被限在 P 组；软亲和影响有限，但这是"静默改变播放行为"的风险点。

## 六、预期收益与判定标准

- 基线：T2 中位 784.6 ms（docs/136 #0 口径）。设 CPU-bound 段占比为 X、P 相对 E 单核快 20–30%、迁移/L3 再省几 ms，理论收益上限大致 **X × 20% + 个位数 ms**；只有"关键线程真的落在 E 核/跨域迁移"时才能拿到。
- **先测两个数（阶段 0）**：
  1. 启动期 **CPU 时间 / 墙钟**（`GetProcessTimes` 到 T2）：<30% ⇒ CPU 放置不可能有实质收益，直接判死；
  2. 播放期 `FFF_PLACEMENT=off` 的 A/B：tier0 策略本身若测不出收益，启动期更不必做。
- 判据：A/B 20 轮逐轮交替、T2 中位差 **≥3%（≈25 ms）且 p<0.05** 才值得默认开；否则只保留诊断日志（`PLACEMENT_STARTUP` 行打印机型/域/线程落点），不启用。

## 七、风险清单

| 类别 | 风险 |
|---|---|
| 平台 | CPU Set 被 affinity mask 覆盖；多 group 赋值被忽略；VM/容器/旧系统枚举为空（已有降级）；Windows 11 节能/能效模式另有调度 |
| 反效果 | 进程默认把 GC/JIT/预取全拉上 P → E 核闲置、P 争抢；AMD 选错 CCD；X3D 拿到低频缓存 CCD |
| 行为 | 播放开始前未清除会静默改变音频/解码线程位置；与内核 `PlacementPolicy` 双写者 |
| 工程 | 测试矩阵大（Intel/AMD/多 group/VM）；本机只有 Intel，AMD 结论无法本机复现；每加一个启动期臂都要占安静窗 |
| 维护 | 拓扑发现已有两套（播放期），启动期再引入"第三种选域策略"会提高理解成本 |

## 八、建议落地路径（分阶段，先证伪再投入）

**阶段 0（先做，低成本）**
- 测启动期 CPU 占墙钟比（<30% 判死）；
- 播放期 `FFF_PLACEMENT=off` A/B（用现成负控，顺便给这套策略补上第一笔收益账）。

**阶段 1（Intel 最小臂，1 天量级）**
- 内核 `PlacementPolicy` 新增 `BeginStartupPlacement()/EndStartupPlacement()`：复用 `Discover()` 与 `tier0_`，只对 **UI 线程、内核 create 线程、present 线程、worker 线程** 做 `SetThreadSelectedCpuSets`（C1 臂），不做进程默认；
- host 在 `MyApplication_Startup` 调用（需要 kernel DLL 提前加载或 host 内做一次轻量 P/Invoke；两者取一，注意别把 DLL 加载成本挪到启动测时窗）；
- 首帧 presented / 首图 opened / 3 s 超时自动清除；默认关，env 开；Intel 本机 20 轮 A/B。

**阶段 2（AMD，仅探测不启用）**
- 增加一行 `PLACEMENT_STARTUP` 诊断日志：机型、域数量、每域 L3 大小、NUMA、group、UI 线程初始落点；
- 在 3–5 台真实 AMD 机（5950X / 7800X3D / 7950X / 7950X3D / 3990X 各一档）收集分布，再决定"主 CCD"策略；X3D 单独臂。

**阶段 3**
- 达标 ⇒ 默认开、写账（docs + STATUS）；不达标 ⇒ 删除启用路径，保留诊断日志。

## 九、边界

- 本轮**未实现、未跑新基准**：Intel 本机当前窗格负载 ~65%，不满足安静窗纪律；AMD 无本机数据。
- 官方语义引自 [CPU Sets](https://learn.microsoft.com/windows/win32/procthread/cpu-sets)、[SYSTEM_CPU_SET_INFORMATION](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-system_cpu_set_information)、[SetProcessDefaultCpuSets](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessdefaultcpusets)、[SetThreadSelectedCpuSets](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadselectedcpusets)；代码事实取自 `3689f77` 树；所有"ms 级"量级均为推断、标注待实测。