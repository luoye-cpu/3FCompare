# 3FCompare 内核补丁索引（PATCHES）

> 基线：上游 master `440e662`（2026-09-18，含已合并的 PR #8 与 **PR #9**）。
> 本地分支：`3fc/integrate-issue7`。
>
> **2026-09-18 重大变更：上游合并了 PR #9。** 该 PR 把我们长期自行维护的 5 项扩展
> （A11 显卡指定、批量像素回读、渲染目标诊断、SetViewTransform 直写、16F/HDR 越界修复）
> 连同版本资源一并吸收。**重移植负担从 8 项降到 4 项**（类别一 7→3，类别二 1 项不变）。
>
> 本文档固化"哪些补丁必须在每次上游更新后重放"的清单，避免合并时靠记忆裁决。
> 原则：**上游优先**——上游已有等价实现的一律不重放；仅托管 API 硬依赖且上游无等价的扩展保留。

## 升级记录

| 日期 | 归档 tag | 上游 | 说明 |
|---|---|---|---|
| 2026-09-11 | `3fcompare-kernel-2026.9.11.1`（`6bc8d61`） | `f25c28f`（上游 9.12） | 首次 re-port |
| 2026-09-15 | `3fcompare-kernel-2026.9.14.1`（`025198f`） | `d8b2c03`（上游 9.14） | 仅 2 个 vbproj 变化，扩展无需重移植 |
| 2026-09-16 | `3fcompare-kernel-2026.9.14.2`（`68e1965`） | `d8b2c03`（上游 9.14） | 新增 A11 `preferredAdapterIndex`（API 14→15），首次 MSVC 构建验证 |
| 2026-09-16 | `3fcompare-kernel-2026.9.14.3`（`0fe33c4`） | `d8b2c03`（上游 9.14） | 追加 issue #7 修复（上游 `824093d` 的 cherry-pick） |
| 2026-09-17 | `3fcompare-kernel-2026.9.17.1`（`b6b96a6`） | `ea3ce05`（PR #8 合并后） | 基线溯源归正，`FFF.Native/` 与上一基线逐字节一致 |
| 2026-09-18 | `3fcompare-kernel-2026.9.18.1`（`ba6d875`） | `ea3ce05`（PR #8 合并后） | 新增 16F/HDR 越界修复；**PR #9 提交/合并前的最后本地态** |
| **2026-09-18** | *（本次合并，待打 tag）* | **`440e662`（PR #9 合并后）** | **上游吸收 5 项扩展。本地残留 = 类别一 3 项 + 类别二 1 项** |
| 2026-09-24 | *（待打 `3fcompare-kernel-2026.9.24.1`）* | **`a87046e`（上游 9.24-3）** | merge 提交 `a8e437e`（双亲 `b765a1f` + `a87046e`）。上游 11 个提交中 5 个触及 `FFF.Native`。冲突 2 处（`FFF.Player.Api.h`、`PlayerApi.cpp`），均为"两侧同位置各自追加"⇒ 两边都留。导出面 **82 → 86**，`PlayerApiVersion` 仍 **15** |

## 〇、⚠ 上游会"改结构体布局但不升 `PlayerApiVersion`"（2026-09-24 实测）

`a87046e` 区间内上游把 `FFF3FPAudioPeakLevels` 的 `std::uint32_t reserved` 换成
`inputChannelCount` + `float inputValues[8]`（结构体变大 32 字节），**版本号一字未动**。
它自己用 `legacySize = offsetof(FFF3FPAudioPeakLevels, inputValues)` + `version∈{1,2}` 做了向后兼容，
且 3FC 托管侧没有这个结构 ⇒ 本次无害。

⇒ **以后每次合并必须逐结构核对布局，不能拿 `PlayerApiVersion` 当 ABI 不变的凭据。**
内核对 version 只做"相等才收"（D5），它**保证不了结构体没被改过**。
判据应是：`git diff <旧基线>..<新上游> -- FFF.Native/3FP/Api/FFF.Player.Api.h` 里
凡是 `struct FFF3FP*` 内部有 `+`/`-` 行的，逐个回托管侧比对 `Marshal.SizeOf` 与字段偏移。

## 一、必须重放（托管 API 硬依赖，上游无等价）—— 3 项

> ⚠ **术语澄清**：`SetPresentConfig` 仅存偏好位（内核几乎无行为），但**不是废弃项**——
> 托管侧有活跃的 P/Invoke 调用链（`Fff3FpEngine`、`MainWindow.SelfTest`）。
> 称其为 "shim" 只是指**对上游无价值**（上游无对应机制）；删除导出会立即
> `EntryPointNotFoundException`。

| 补丁 | 锚点 | 托管侧依赖 | 重移植要点 |
|---|---|---|---|
| **Redraw（K5 导出）** | `FFF.Player.Api.h` 的 `FFF3FP_Redraw` 声明 + `PlayerApi.cpp` 实现 + `PlayerSession::Redraw` 转发。<br>⚠ **`PlayerVideoRenderer::Redraw` 不是本地补丁**——上游 `a87046e` 自己就有（`VideoRenderer.cpp:4157`），本地一行未改 | `PlayerSurface.SubclassedWndProc`（子 HWND resize 后调用）→ `Fff3FpEngine.Redraw`（`Fff3FpEngine.cs:678`） | 让 presenter 感知尺寸变化并 swapchain resize + 重绘一帧。**上游无此导出** ⇒ 缺失时本地 app 直接 `EntryPointNotFoundException`。<br>⚠ 本行此前把渲染器方法也列进锚点，照它"重放"会造出重复定义 —— 正是 §四 警告过的那类事故（`PlayerSession::ReadVideoPixelRegion` 与 `FFF3FP_GetRenderTargetInfo` 已各重复过一次）。2026-09-26 净差异实测订正 |
| **SetLogCallback（F-LOG）** | `FFF.Player.Api.h` 的 `FFF3FPLogCallback` typedef + `PlayerApi.cpp` 的 `g_logSink` / `g_logContext` / `FFF3FP_KernelLogImpl` | `AppLog`（内核日志落盘到 `logs/app-*.log`） | 内核日志回调注册。**上游无此导出**。<br>⚠ `VideoRenderer.cpp` 的 `EnsureDevice()` 诊断行会 `extern` 声明并调用 `FFF3FP_KernelLogImpl`；该函数定义在 `PlayerApi.cpp`，重移植时不要漏 |
| **SetPresentConfig（tearing 偏好位）** | `VideoRenderer.h/.cpp` 的 `SetPresentConfig(bool)` + `PlayerSession` 转发 + `PlayerApi` 导出 | `Fff3FpEngine`、`MainWindow` | 只写 `swapAllowTearing_`（该成员是**上游既有**），不新增渲染器状态。原配对的 `SetPacingConfig` 已于 2026-09-18 全链路移除 |

## 二、本地专用（有效，但不推上游）—— 1 项

| 补丁 | 说明 |
|---|---|
| 音频缓冲 250ms | `PlayerSession.cpp` 的 `TargetAudioBuffer100ns`（音频包**投喂阈值**）由 120ms 改为 250ms。性质是"延迟换抗欠载"：对视频对比工具合适（延迟不敏感、抗欠载优先），**但会增加延迟 ⇒ 不推上游**。<br>⚠ 别与 WASAPI 缓冲区混淆：`WasapiRenderer.cpp` 的 bufferDuration 上游已改为自适应 `clamp(sharedDefaultPeriod*3, 50ms, 200ms)`，那一处跟随上游即可。<br>若高码率多声道仍欠载，优先评估调上游 clamp 上限，而非继续加大此值。 |

## 三、已移除（有意不保留）

| 补丁 | 移除原因 | 重引入条件 |
|---|---|---|
| P3 原生变速 SetSpeed（`7e85c99`） | 内核完整但托管侧 0 绑定（死代码）；与 UI 伪变速（每秒 Seek）语义冲突；每次上游更新白付重移植税。已从 PlayerApi 导出、API 头声明/枚举、PlayerSession、WasapiRenderer、VideoRenderer 全链路移除 | 托管侧正式接线时（导出 `FFF3FP_SetSpeed`、删除 UI 伪变速、加声画漂移测试 ≤100ms），从历史提交 `7e85c99` 整体重移植 |

## 四、已被上游 PR #9 吸收（2026-09-18）—— **禁止重放**

> PR #9 = 上游 `a6c74b3`（Native）+ `7deabbd`（Player），合并为 `440e662`。
> 以下各项**已在上游 `origin/master` 中**，本地保留的只是合并结果。
> ⚠ **严禁按历史提交重放**：已实测会产生重复定义（`PlayerSession::ReadVideoPixelRegion`、
> `PlayerApi.cpp` 的 `FFF3FP_GetRenderTargetInfo` 都重复过一次）。
> 上游版本在若干点上**比我们的更强**（见右列），合并时**优先取上游**。

| 补丁 | 本地原提交 | 上游改进（相对我们的版本） |
|---|---|---|
| A11 `preferredAdapterIndex`（多显卡指定） | `68e1965` | 成员改为 `= -1` 默认初始化，并注释点明"**0 是合法索引、不是未设置**" ⇒ 调用方若零值会静默钉死到 adapter #0。**托管侧仍必须显式置 -1**（`IPlayerEngine.PreferredAdapterIndex` 默认已是 -1，`AppSettings.Normalize` 钳制 -1..15） |
| `FFF3FP_ReadVideoPixelRegion`（原 patch 0004） | 随 re-port 带入 | ① `dstFloatCount` 比较改 64 位（防 uint32 溢出）；② 越界**拒绝** `InvalidArgument`（原为静默截断返回 Success）；③ 拷贝尺寸直接取请求尺寸 |
| `FFF3FP_GetRenderTargetInfo`（K4 诊断） | 随 re-port 带入 | ① 校验 `size` / `version`；② 无 swapchain 时返回 `InvalidState`（原为全零 Success，与真实 0x0 目标不可区分） |
| `SetViewTransform` 直写原子路径（原 0006 rev5） | 随 re-port 带入 | 用 `std::atomic<bool> discOpened_` 替代跨线程裸读 `disc_`（消除 data race / use-after-free 窗口） |
| 16F/HDR HALF 越界修复 | `ba6d875` | 上游 `ReadPixelRegion` 的 16F 分支本身就是 `HALF*` + `XMConvertHalfToFloat`，修复随 PR #9 一起进入上游 |
| `FFF.Native.rc` 版本资源 | `2fc46e9` | 上游已采纳（VER_API 15）。⚠ 这是**纯本地产物被 PR #9 一并带上去了**，现已是上游的一部分，无需再维护 |
| `.gitignore` 忽略 `vcpkg_installed/` | `11b7f6d` | 同上，已进上游 |

> **附带说明**：PR #9 还带上了我们在评审中提出的加固项（越界拒绝、64 位比较、RTInfo 校验、
> `EnsureDevice()` 内改用固定栈缓冲以避免 noexcept 下 `std::terminate`、`discOpened_` 原子）。
> 这些已是上游代码，不再属于本地补丁。

## 五、历史留档（已过时 —— **禁止重放**）

> **2026-09-18 复核：以下补丁均已不在当前代码中。**
> 判据：我方 HEAD 与上游对 `FF_THREAD_FRAME` / `SetMaximumFrameLatency` / `Present(0, 0)`
> 的命中数完全相同，说明这些符号全是上游自己的代码，我方补丁无残留。
>
> ⚠⚠ **2026-09-26 净差异复核：上面这条判据本身是错的，本节有两行结论要翻案。**
> 用 `git diff a87046e HEAD -- FFF.Native` 实测：
> · `FF_THREAD_FRAME` 命中数 **上游 1 / 我们 2**（我们多一条 `!video` 的音频多线程分支，见 §十一 D6）——
>   "命中数相同"这一条今天就不成立，本节据此得出的"无残留"结论不成立；
> · `Present(0, 0)` 上游那唯一 1 处在 **`ClearSurface`（`a87046e` cpp:4924）**，
>   `EnsureSwapChain` 里**根本没有**（下面第 1 行的理由是假的）；我们这边真实调用 **0 处**
>   （现存 3 处命中 1771/3377/5221 全是注释，内容正是"它已被删掉"）⇒ 那是 §十一 D3 的活补丁。
> **符号命中数只能证明"存在"，不能证明"归属"** —— 判遗留一律改用 §十二 的净差异法。

| 补丁 | 为何过时 |
|---|---|
| ~~`6e7469f` DWM 修复（ResizeBuffers 后 `Present(0,0)`）~~ | **翻案（2026-09-26）**：原写的理由"上游 `EnsureSwapChain` 失败恢复路径自带 `swapChain_->Present(0, 0)`"是假的——上游那 1 处 `Present(0,0)` 在 `ClearSurface`（`a87046e` cpp:4924），`EnsureSwapChain` 里没有。我们**主动删掉了 `ClearSurface` 的这次呈现**（只清不呈现 + 计数），属活的本地分歧 ⇒ 见 §十一 **D3** |
| ~~`a4a7ab0` FLAC 多线程解码（FF_THREAD_FRAME）~~ | **翻案（2026-09-26）**：原写的理由"上游已实现；不在净差异中"是假的——上游只有 `video && !hardwareRequested` 分支，我们另有一条 `!video && !hardwareRequested` 的**音频**多线程分支（`PlayerSession.cpp:1915-1917`）；`FF_THREAD_FRAME` 命中数上游 1 / 我们 2 ⇒ 见 §十一 **D6** |
| P2 lock-free Render 快路径 | 上游 2026.9 渲染器重构后已有等价实现。<br>⚠ `interactiveMove_` + try_lock **本就是上游自己的代码**，曾被误当作我方补丁，**不要计为本地补丁** |
| `c941da3` HDR 元数据去重 + `SetMaximumFrameLatency(1→2)` | 两边同为 **1**，该改动未保留 |
| patch 0007 zoom viewport cover（`b0ff668`） | 上游 shader 已删除 ViewZoom/ViewPan 常量；**从未重放** |
| `SetPacingConfig`（A9 媒体率呈现节奏） | **2026-09-18 全链路移除**：内核实现为空操作，纯占导出位；托管侧 P/Invoke、`AppSettings.VrrPacingEnabled`、设置窗口复选框一并删除。导出数 83→82 |

## 六、纯增量（随分支走，无重放成本）

**已清空。** 原两项（`FFF.Native.rc`、`.gitignore`）已随 PR #9 进入上游，见类别四。

## 七、本地补丁与 issue #7（多路随机崩溃）的关系 —— **无关**（2026-09-17 实测）

用户曾质疑：本地有大量补丁，崩溃是否由它们引入？**实测结论：不是。**

**唯一的嫌疑项与证伪过程。** 本地补丁中唯一触及"交换链改写"的是
`SetViewTransform` 直写路径（现已属上游）——把它改回上游 `Enqueue` 版并编译后，
**8 路崩溃 5/8，同批次基线 4/8，没有下降 ⇒ 假设证伪。**
（分支 `3fc/exp-revert-svt`，提交 `2b90a87`。事后看也合理：zoom 只改绘制矩形、
不改 swapchain 尺寸，`EnsureSwapChain` 通常 early-return。）

**其余补丁逐一排除：**
- A11 —— 只在 `EnsureDevice()` 建设备时生效，运行时不参与；
- `ReadPixelRegion` / `GetRenderTargetInfo` —— 持锁与上游既有 `ReadPixel` 一致，且 `--multitest` 不调用；
- 音频 250ms —— 只影响音频包投喂，不碰 GPU/DXGI；
- `SetPresentConfig` —— 仅偏好位；
- 类别五各项 —— 已不在当前代码中。

⇒ **不要再往"本地补丁导致崩溃"方向排查。** 根因是内核跨渲染器并发 Present 这一
**上游既有设计**问题。

## 八、上游更新操作流程（2026-09-18 实测修订）

1. `git fetch origin --prune`，对照本文档逐类核对；
2. `git merge --no-ff --no-commit origin/master`，**逐文件解决冲突**；
3. ⚠ **合并必产生重复定义**（两侧都新增过同一函数）⇒ 解决完冲突后**必须扫描重名**：
   `grep -oP '^\s*\w+\s+(PlayerSession|PlayerVideoRenderer)::\w+' <file> | sort | uniq -d`；
4. 以 PlayerApi 导出面为完成判据：Redraw / SetLogCallback / SetPresentConfig 三个本地
   专属导出必须还在，导出总数应为 **86**（2026-09-26 实测；此前本节写 82，是后续加导出后没跟数——
   判据写死数字就必须同时写"怎么重测"：`py tools/check_kernel_exports.py <dll>` 打印的那一行即为唯一真值），
   API 版本 **15**；
5. 构建（见下）+ 托管 `dotnet build` + `3FCompare.Core.Tests` 全绿；
6. 打新归档 tag（`3fcompare-kernel-<上游版本>.<序号>`），同步更新
   `tools/构建全部.ps1` 的 `$KernelBaselineSha` / `$KernelBaselineTag` 与 `.3fc_kernel_sha`。

> ⚠ **第 2 步的冲突解决方向不是一律"取上游"**。§十一 的 D1–D5（呈现泵家族）在
> 12 个 `notify_one` 点、`TimedTextThread` 整体改写处、`ClearSurface` 的 `Present(0,0)` 处必然冲突，
> 这些地方**取上游 = 把 §七 认定的 issue #7 根因（跨渲染器并发 Present）原样装回**。
> 逐项的处置写在 §十一 的"冲突时"列。
>
> ⚠ **判"某补丁还在不在"只准用净差异**：`git diff <上游锚点> HEAD -- FFF.Native`
> （或 §十二 的哨兵脚本）。**禁止再用"某符号两边命中数相同"这类判据** ——
> §五 就是用它把两条活补丁判成"已过时"的（实测 `FF_THREAD_FRAME` 1 vs 2、
> `Present(0, 0)` 真调用 1 vs 0）。

> ⚠ **MSBuild.exe 在本机被安全策略拦截** ⇒ 用 `tools/build_kernel_manual.py`
> （复放 `FFF.Native.tlog` 中 MSBuild 真实下发的 cl/rc/link 命令，输出到新 obj 目录
> 以避免删除既有 obj）。产物校验：`tools/check_kernel_exports.py <dll>`。

## 九、本次合并（`a87046e`）的验收

**全部读数、A/B 表、退路与遗留项见 `docs/48-上游内核升级到a87046e（2026-09-24）.zh.md`（3FC 主仓 docs 下）。**
本节只留两条**每次合并都适用的规程**，不复述数据：

1. ⚠ **验收期间禁止削弱环境。** 本机 RTSS / Afterburner 在跑是 3FC 的使用态，不是测量噪声
   （§七 那族偶发崩溃本来就在钩子下成立）。关覆盖层、改窗口位置、减并发、放宽阈值、跳过用例
   全属禁项——与"放宽断言换绿灯"同罪。要变的是**样本量与交替方式**：同批交替、每臂 N≥8、
   退出码按"通过 / 断言判红 / 原生崩溃"三态分开计数并记崩溃顶帧。
   若某条件一摘崩溃就消失，结论写成"该条件参与致崩"（新证据），**不是**"原来没崩"。
2. ⚠ **`build_kernel_manual.py` 不可复现**：同一份源码两次构建大小相同而 SHA 不同。
   跨构建比 SHA 无意义；"产物一致"只能靠导出面与大小判。
   `构建全部.ps1` 的 S5 比的是同一次构建内"源→落地"，仍然有效。

## 十、`RenderTargetInfo` v2：把 destination 原点改成有符号（2026-09-26，本地分歧）

**动了什么**：`VideoRenderer.cpp` 的 `struct VideoDestination{x,y}` 由 `uint32_t` 改 `int32_t`、
平移段去掉 `max(0,·)` 换成"必须仍盖住拟合盒"的双边 clamp；`lastDestX_/lastDestY_` → `atomic<int32_t>`；
导出结构 `FFF3FPRenderTargetInfo.destX/destY` → `int32_t`，**`version` 1 → 2 且拒绝 v1**。
托管侧 `RenderTargetInfo.DestX/DestY` 同步改 `int`，并改掉两处只为无符号写的判据
（`NativeFrameReadback` 的 `> int.MaxValue` 坐标溢出分支 → 改由"求交为空 ⇒ null"兜住）。

**为什么**：上游这段用无符号原点存绘制盒，负值一律被 `max(0,·)` 抹平 ⇒ **往右下平移的那半段量程整段消失**。
3FCompare 实测过形状：`pan` 从 −1 扫到 +1，内核上报的原点恒为 `(0,0)`，用户侧表现为"放大后拖不动"。
改完同一条扫描给出 `横 1995px / 纵 1122px`，恰等于拟合盒尺寸（独立参照，非实现自证）。

**为什么拒绝 v1 而不是兼容**：v1/v2 的**字节宽度完全相同**，`size` 分不出双方；若收下方 v1 调用方
拿到负原点会读成一个巨大无符号值（静默错内容）。version 是唯一判别位 ⇒ 让它响亮地 `InvalidArgument`。
本仓外唯一的 `SetViewTransform` 调用方是 `FFF.Player` 宿主（`播放器会话.vb:220`），它**不读** RTInfo
⇒ 接受 v1 没有受益者，只有风险。

**合并时的取舍**：这是"上游更弱、本地更强"的一类（与 §四 表右列相反）。上游若仍坚持无符号原点，
优先提 PR 把原点签名改掉；PR 未合前，**下次合并会在这里冲突**，冲突点是
`VideoDestination` 定义、平移段 clamp、`FFF3FPRenderTargetInfo.version` 三处，逐个回读本节再决定，
不要机械取上游（取上游 = 平移量程重新变成 0）。

## 十一、净差异台账（`git diff a87046e HEAD -- FFF.Native` 的全量归属，2026-09-26）

> 本节是"本地到底改了内核什么"的**唯一完整答案**。§一/§二 只覆盖 D7–D9 三项；
> **D1–D6 此前在账本里一个字都没有**，而 D1–D5 占 `VideoRenderer.cpp` 净增行的约八成。
> 总量：`git diff --shortstat a87046e HEAD -- FFF.Native` = **6 个文件 / +578 / −115**
> （⚠ 别用 `grep -c '^[+-]'` 数：它把 `+++`/`---` 两行头也算进去，我第一版就据此写成了 +703）。
> 宿主 `FFF.Player*` 与上游**逐字节相同**（3FP 侧改动都在 `3fp/` PR 工作区，不在基线）。
> 每行都给了"怎么重测"——数字过期时以命令为准，不以本表为准。

| 编号 | 本地分歧 | 锚点 | 规模 | 为什么存在 | 合并冲突时 | 回归探测命令（期望值） |
|---|---|---|---|---|---|---|
| **D1** | **`PresentationPump`：把 N 路 per-instance presenter 收敛成一个进程级呈现线程**（含构造函数注册/注销、`StopPresentationPumpMembership`、`PumpPresentationOnce`） | `VideoRenderer.cpp:1751-1883`（类）、`1885`（ctor）、`1999`、`3218-3341`（Start/Stop/PumpOnce）；`VideoRenderer.h:49-53,307-311,471-477` | cpp +247 / .h +36 | issue #7 根因是"多路各自 Present 抢占同一 swapchain"（§七 已定案）。这是**治它的那刀**，不是清理 | **取本地**。取上游 = 根因原样装回 | 探测见下方 **§十一·探测** 的 `D1` 行；成员见 §十一·成员表 |
| **D2** | 上游所有的 `timedTextCondition_.notify_one()` 唤醒点 → 一律改成本地的 `PresentationPump::Instance().Wake()`（两侧数量见 §十一·探测） | 散布在 D1 列出的区域 + `4385-4417`、`4586`、`4779`、`5359-5426` | 含在 D1 内 | 条件变量唤醒必须换成唤醒泵，否则字幕/弹幕更新不再驱动呈现 | **取本地**（自动合并会把上游新写的 `notify_one` 原样带回来，必须人工扫） | 探测见下方 **§十一·探测** 的 `D2` 行；成员见 §十一·成员表 |
| **D3** | `ClearSurface()` **删掉收尾的 `Present(0,0)`**：只清不呈现 + 计数 | `VideoRenderer.cpp:5216-5264`（注释 5217-5250 解释取舍，即 docs/40 方案 B） | +36 / −4 | 这次 `Present` 发生在**调用者线程**，是"泵外第二呈现点"。上游 `a87046e` 仍在 `ClearSurface`（其 cpp:4924）里呈现 | **取本地** | 探测见下方 **§十一·探测** 的 `D3` 行；成员见 §十一·成员表 |
| **D4** | 呈现**可观测性**：慢帧阈值 100ms、日志冷却 10s、`offPumpClearSurfacePresents_` / `handoffTimeoutFallbacks_` / `NotePumpPresentDuration` | `VideoRenderer.cpp:1730-1750, 3343-3396`；`.h:486-497` | +79 / .h +22 | 没有它就无法区分"泵在正常工作"与"泵饿死/回退"，issue #7 类问题只能靠崩后猜 | 取本地（上游无对应机制，不会真冲突） | `grep -c 'NotePumpPresentDuration\|handoffTimeoutFallbacks_\| 探测见下方 **§十一·探测** 的 `D4` 行；成员见 §十一·成员表 |
| **D5** | **排除最后两笔泵外 Present**（`NotePumpOwnedPresentExcluded`：`ClearSurface` 与 2s 交泵超时） | `VideoRenderer.cpp:5090-5133`（`PresentTimedText` 拆成"线程判定 + 交泵"外壳，本体 `PresentTimedTextOnPump`）、`3377` 附近；`.h:317` | +45 / .h +3 | D1 的不变式是"进程内只有一个线程在 `Present` 里"，此前有两个合法例外；这一项把例外关掉 | **取本地**。超时那支返回 `DeviceFailure`，依赖 `PlayerSession.cpp:672/2900` 的恢复路径 ⇒ 两处要一起看 | `grep -c 'NotePumpOwnedPresentExcluded\| 探测见下方 **§十一·探测** 的 `D5` 行；成员见 §十一·成员表 |
| **D6** | **音频多线程解码**：`!video && !hardwareRequested` 时 `thread_count=min(hardware, MaximumSoftwareDecoderThreads)` + `FF_THREAD_FRAME` | `PlayerSession.cpp:1915-1917`（上游只有 `video` 分支，在其 1883-1886） | ~6 行 | FLAC 支持帧级并行；缓解 AV1 视频解码与 FLAC 音频解码抢 CPU | **取本地**（§五 曾误判它"已过时"，见该节翻案） | 探测见下方 **§十一·探测** 的 `D6` 行；成员见 §十一·成员表 |
| **D7** | 音频缓冲 `TargetAudioBuffer100ns` 120ms → **250ms** | `PlayerSession.cpp` 顶部常数 | 1 行 + 注释 | 高码率多声道（FLAC 6ch + AV1 软解）抗欠载；性质是延迟换稳定 | 取本地 | 探测见下方 **§十一·探测** 的 `D7` 行；成员见 §十一·成员表 |
| **D8** | 三个本地专属**导出**：`FFF3FP_Redraw`(K5) / `FFF3FP_SetLogCallback`(F-LOG) / `FFF3FP_SetPresentConfig`(tearing) | `FFF.Player.Api.h`、`PlayerApi.cpp`、`PlayerSession.{h,cpp}`、`VideoRenderer.{h,cpp}` | 约 +70 | §一（托管侧硬依赖，缺一个就 `EntryPointNotFoundException`） | 取本地 | 探测见下方 **§十一·探测** 的 `D8` 行；成员见 §十一·成员表 |
| **D9** | `RenderTargetInfo` **v2：有符号 destination 原点** | 见 **§十**（cpp:786-791/840-845/4818-4838、`.h:146-148/409-411`、`FFF.Player.Api.h`、`PlayerSession.cpp`） | +96 / −19（**未提交**） | 上游把绘制盒原点存成 `uint32_t` 并用 `max(0,·)` 夹负值 ⇒ "往右下平移"整半段量程消失（实测 pan 扫全程原点恒 `(0,0)`） | 取本地；同时评估向上游提 PR（宿主 `播放器会话.vb:220` 也调 `SetViewTransform` ⇒ 这是替上游修 bug，且除 3FC 外无人读 `destX/destY`） | 探测见下方 **§十一·探测** 的 `D9` 行；成员见 §十一·成员表 |

**本地独有成员名（20 个，逐个归行；哨兵双向核对：净差异里新出现的 `xxx_` 成员若不在本表 ⇒ 红，
本表列了而树里已不存在 ⇒ 也红）**

| 归属 | 成员 |
|---|---|
| **D1** | `pumpRegistered_` `pumpThreadId_` `members_` `inFlight_` `wakePending_` `generation_` |
| **D3** | `offPumpClearSurfacePresents_` |
| **D4** | `pumpNextPresentation_` `pumpNextDevicePoll_` `pumpPresentMax100ns_` `pumpSlowPresentCount_` `pumpSlowPresentLogAt_` `offPumpPresentLogAt_` `pumpObservedPresentationGeneration_` `pumpObservedVideoGeneration_` |
| **D5** | `pumpHandoffPending_` `pumpHandoffDone_` `pumpHandoffResult_` `pumpHandoffCondition_` `handoffTimeoutFallbacks_` |

（D8 的三个导出、D9 的 v2 字段不带 `_` 成员，故不在本表；`.h:471-482` 是 D1/D5 的声明处。）

**§十一·探测**（唯一存放期望值的地方；`tools/check_kernel_ledger.py` 直接解析这块并逐条复跑，
文档里别处不再抄这些数字。`范围` 列：`tree` = 当前 HEAD 的工作树文件，`upstream` = 上游锚点同名文件。
判据一律是"含此固定串的行数"，两边都记 ⇒ 那个 0 才有意义）

```ledger-probe
# 编号   | 范围     | 路径                                        | 固定串                                | 期望
D1       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | PresentationPump                        | 27
D1       | tree     | FFF.Native/3FP/Render/VideoRenderer.h       | PresentationPump                        | 5
D2       | upstream | FFF.Native/3FP/Render/VideoRenderer.cpp     | timedTextCondition_.notify_one          | 7
D2       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | timedTextCondition_.notify_one          | 0
D2       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | PresentationPump::Instance().Wake()     | 8
D3       | upstream | FFF.Native/3FP/Render/VideoRenderer.cpp     | swapChain_->Present(0, 0)               | 1
D3       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | swapChain_->Present(0, 0)               | 0
D3       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | offPumpClearSurfacePresents_            | 3
D4       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | pumpSlowPresentCount_                   | 2
D4       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | handoffTimeoutFallbacks_                | 3
D5       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | PresentTimedTextOnPump                  | 4
D5       | tree     | FFF.Native/3FP/Render/VideoRenderer.cpp     | NotePumpOwnedPresentExcluded            | 3
D6       | upstream | FFF.Native/3FP/Core/PlayerSession.cpp       | FF_THREAD_FRAME                         | 1
D6       | tree     | FFF.Native/3FP/Core/PlayerSession.cpp       | FF_THREAD_FRAME                         | 2
D7       | upstream | FFF.Native/3FP/Core/PlayerSession.cpp       | TargetAudioBuffer100ns = 1'200'000      | 1
D7       | tree     | FFF.Native/3FP/Core/PlayerSession.cpp       | TargetAudioBuffer100ns = 2'500'000      | 1
D7       | tree     | FFF.Native/3FP/Core/PlayerSession.cpp       | TargetAudioBuffer100ns = 1'200'000      | 0
D8       | tree     | FFF.Native/3FP/Api/FFF.Player.Api.h         | FFF3FP_Redraw                           | 1
D8       | tree     | FFF.Native/3FP/Api/FFF.Player.Api.h         | FFF3FP_SetLogCallback                   | 1
D8       | tree     | FFF.Native/3FP/Api/FFF.Player.Api.h         | FFF3FP_SetPresentConfig                 | 1
D8       | upstream | FFF.Native/3FP/Api/FFF.Player.Api.h         | FFF3FP_SetLogCallback                   | 0
D9       | tree     | FFF.Native/3FP/Render/VideoRenderer.h       | std::atomic<std::int32_t> lastDestX_    | 1
D9       | tree     | FFF.Native/3FP/Api/FFF.Player.Api.h         | std::int32_t destX                      | 1
```

> D8 的三行只证明"导出声明在不在"；导出**总数**（现 86）与 API 版本由
> `py tools/check_kernel_exports.py <dll>` 判，不在此抄数（§八 第 4 步）。
> D9 是**未提交**的 §十改动 ⇒ 它的两行现在就能过（脚本读工作树），提交后依然过。
>
> ⚠ **本块的已知边界（`--selftest` 实测出来的，别高估它）**：
> · 能挡：新增本地成员没登记（A1）、登记了但代码里已没有（A2）、期望数被改错（B）、
>   **某族的探测被整族删掉**（E）、重放队列状态表与 `git apply` 实测不自洽（C）；
> · **挡不住**：把某族的探测**删掉一条但留着其它条**（E 只按"有没有"判，不按条数判）。
>   要拦这种就得给每个 D 编号钉最小条数 —— 那是下一轮的事，现在先如实记下边界。
> 复跑：`py tools/check_kernel_ledger.py`（PASS/FAIL + 明细），自证有牙：`--selftest`。

**已核对为"不是本地分歧"的**（避免下次又误登记）：`PlayerVideoRenderer::Redraw`（上游 `a87046e` cpp:4157）、
`swapAllowTearing_`（上游既有成员）、`SetError` / 错误上报骨架、`ColorExtension`/Dolby 那批上游 9.24 自身的改动。

## 十二、判定方法与重放队列现状（2026-09-26）

1. **判"本地分歧"只认净差异**：`git diff <上游锚点> HEAD -- FFF.Native`。禁止用符号命中数（§五 的教训）。
2. **哨兵**：`py C:/PLAN/3FCompare/tools/check_kernel_ledger.py`（在 3FC 仓，不在内核仓）——
   A1/A2 成员双向、B 按 §十一 的 `ledger-probe` 块逐条重数（工作树 + 上游双向）、
   C `tools/patches/README.md` 的 `patch-status` ↔ 目录 ↔ **`git apply --check -R` 反向可打性**、
   D 声明级符号归属、E 每个 D 编号都有探测、F 哨兵自身对工作树只读。
   合并上游后必须跑它 + `check_kernel_exports.py`，输出留档在 `.3fc_dumps/`。
   （三探测 `反向/正向/--3way` 是 `构建全部.ps1` 的 `Invoke-KernelPatches` 做的，不是哨兵。）
3. **⚠ 探测一律 `--check`，且哨兵要能自证只读**（2026-09-26 实测事故）：哨兵的 `apply_probe`
   曾经把反向探测拼成 `git apply -R`（漏 `--check`）⇒ **每跑一次就把内核树真反打一次**
   （呈现泵家族与三个本地导出从工作树消失，−597 行），而 `rc==0` 被 C 项读成"内容确在树中 ⇒ PASS"，
   也就是**破坏与绿灯同一次发生**。连续 4 次现场被误判成"有另一个 agent 在回滚我的树"，
   直到用一次 monkeypatch 负样本（旧版函数装回去 → C=PASS、F=FAIL、工作树指纹变）才把因果定死。
   ⇒ 任何"用返回码代替探测"的写法，先问一句：**它失败/成功的方向，会不会正好是它动手的方向？**
   见证值：修好的树 + 方案 A 未提交 ⇒ 工作树指纹 `261da1267762`；不等就是树被动过。
   复活两条命令：`git checkout HEAD -- FFF.Native` + `py C:/PLAN/3FCompare/.3fc_dumps/kernel-upgrade-20260924/apply_planA.py`。
4. **重放队列**：`tools/patches/` 的 9 个历史 `.patch` 是对着**合并前的旧基线**做的，
   实测 8 个在净差异树上反向/正向/`--3way` **三探测全失败** ⇒ `构建全部.ps1` 的
   `Invoke-KernelPatches` 一旦执行到就会 throw（此前构建都走 `tools/build_kernel_manual.py` 才没暴露）。
   处置见 `tools/patches/README.md`：队列收敛为一份与净差异等价的 `0013-baseline-vs-a87046e.patch`，
   旧文件移入 `tools/patches/history/`（只移不删），并把 §八 第 4 步的导出数从过期的 82 订正为实测 86。
