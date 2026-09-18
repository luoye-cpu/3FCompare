# 本地补丁分包上游提案（2026-09-17）

> **⚠ 2026-09-17 更新：用户指示"不要拆一堆 PR，只做一个"——三个 PR 已合并为一个。**
> 最终产物：分支 `upstream/pr-b-adapter`（HEAD `7a29319`），
> 单个补丁 `docs/upstream/proposal.patch`（4 提交，9 文件 **+366/−12**，整体编译 0 error），
> PR 描述 `docs/upstream/proposal-description.md`。
> 用户将自行与上游沟通，本文档保留**完整分析过程**备查。

---

> 目标：① 重新评估本地补丁对上游的价值；② 把**能合并且不冲突**的部分打包推上游；
> ③ **严格审查这些改动会不会把上游 3FP 本身搞坏**。
> 基线对比：`ea3ce05`（上游 master，含 PR #8）→ `3ac124a`（我方基线）。
> 净差异 **11 个文件、+550/−17**，其中内核代码 **+181/−2**（几乎纯新增，对上游合并友好）。

---

## 0. 结论速览

| 分包 | 内容 | 建议 |
|---|---|---|
| **PR-A** | `ReadPixelRegion`（批量像素回读）+ `GetRenderTargetInfo` + 版本资源 `FFF.Native.rc` + `.gitignore` | ✅ **已就绪**（见 §5）——纯新增、零行为改变、无 ABI 影响 |
| **PR-B** | A11 `SetPreferredAdapterIndex`（多显卡指定） | ⚠ **推但需讨论**——价值高，但要升 `PlayerApiVersion`（ABI 破坏性） |
| **PR-C** | `SetViewTransform` 直写（修 HD/HDR 平移延迟） | ⚠ **可选**——已证伪"导致崩溃"，但改变命令队列语义，需上游认可 |
| 不推 | `TargetAudioBuffer100ns` 250ms（延迟权衡）、`SetPresentConfig`/`SetPacingConfig` shim（no-op） | ❌ 见 §3 |

> **关于"补丁是否与崩溃有关"**：**无关**（实测证伪，见 §3.1 与 `PATCHES.md` 第六节）。
> 唯一嫌疑项 `SetViewTransform` 直写经回退实验排除；其余补丁逐一排除。
> ⇒ 崩溃根因是内核里**跨渲染器并发 Present** 这一上游既有设计问题，与本地扩展无关。

---

## 1. PR-A：安全、纯新增（建议优先合并）

### 1.1 `ReadPixelRegion` —— 批量像素回读

- **价值**：上游现有 `ReadPixel`（单点）每次都要走一遍
  `AcquireBackBufferTarget` + `DrawCachedVideo` + staging 拷贝 + Map。
  批量版本一次调用取回一个矩形，省掉逐像素的 GPU 往返。缩略图/取色/放大镜都受益。
- **对上游无害的证明**：加锁与上游既有 `ReadPixel` **完全一致**——
  `deviceMutex_` → `presentMutex_`（`VideoRenderer.cpp:4638/4642` vs `ReadPixel` 的 `4564/4568`），
  复用同样的 `AcquireBackBufferTarget` / `DrawCachedVideo` 模式，不引入新的并发路径。
  ⇒ **不会成为 issue #7 那类"Present 期间读回缓冲"的新来源。**
- ⚠ **提交前需修一处**：注释写着 "Reuse a single staging texture … instead of creating
  one per call"，但代码里每次调用都 `device_->CreateTexture2D`（`4660-4673`）。
  **注释与实现不符**——要么真的做复用（缓存 + 尺寸变化时重建），要么删掉这句误导性注释。

### 1.2 `GetRenderTargetInfo` + `lastDest*` 原子量

- **价值**：暴露 swap / client / dest 矩形、输出位深、HDR 标志，供诊断与 UI 精确对齐。
- **对上游无害的证明**：唯一改动是在 `DrawCachedVideo` 成功分支把
  `if (result == Success) X;` 改成 `if (result == Success) { X; 4 个 relaxed atomic store; }`
  ——**语义等价，纯新增 4 次原子写**（`relaxed`，x86 上即普通 store，开销可忽略）。
- ⚠ 已知局限（注释已写明）：记录点锚定在 `DrawCachedVideo`，若上游重构该函数需重新锚定；
  届时最坏情况是诊断数据陈旧，**不会崩溃**。

### 1.3 版本资源 `FFF.Native.rc`（+ vcxproj 3 行）

- **价值**：`FileVersion` 便于排查"部署的 DLL 与源码基线是否匹配"——我们已多次靠它
  秒判 API 版本（`FFF3FP_GetApiVersion()` 与 rc 的 `VER_API` 三处一致）。
- **风险**：低。新文件 + `ItemGroup` 3 行。⚠ 需随 `PlayerApiVersion` 同步维护（注释已说明）。

### 1.4 `.gitignore` 增加 `vcpkg_installed/`

- 上游若用 vcpkg，构建树不该入库。零风险。

---

## 2. PR-B：A11 多显卡指定（价值高，但需升 API 版本）

- **内容**：`FFF3FPConfiguration` **末尾追加** `preferredAdapterIndex`；
  `PlayerApiVersion` 14 → 15；`SetPreferredAdapterIndex()` + `EnsureDevice()` 指定索引优先、
  失败回落 monitor 匹配；`PlayerSession` 构造期接线；`FFF.Native.rc` 版本同步。
- **价值**：多 GPU 机器（双显卡笔记本、多卡工作站）上指定解码/渲染卡是常见需求。
- **对上游无害的证明**：
  - 默认 `-1` 完全保持原有行为（monitor 匹配），**向后兼容**；
  - 越界/枚举失败静默回落内置策略，调用方不会看到硬失败；
  - 不拆除既有设备（只在下次 `EnsureDevice` 生效），避免从任意调用点触发交换链重建。
- ⚠ **需要上游拍板的两点**：
  1. **ABI 破坏性**：`FFF3FP_Create` 对 `version` 做**严格相等**校验且要求
     `size >= sizeof(config)` ⇒ 内核与所有消费方必须同批次发布。字段只能在结构体末尾追加。
  2. **建议改进（降低评审阻力）**：`preferredAdapterIndex_` 目前是 plain `std::int32_t`，
     由 `SetPreferredAdapterIndex()`（可来自任意线程）写、`EnsureDevice()` 读。
     虽然 x86 对齐读写实际安全、且语义是"下次建设备时生效"，但推上游前建议改成
     `std::atomic<std::int32_t>`，免得被质疑数据竞争。

---

## 3. 明确不推（对上游有害或无价值）

### 3.1 `SetViewTransform` 直写路径（0006 rev5）—— **改为 PR-C 可选提案**（结论已更新）

改法：绕过 `Enqueue` 命令队列，从调用线程直接写 renderer 的三个 relaxed 原子量 + `Redraw()`。
它修的是真问题：HD/HDR 播放时解码线程忙数十 ms，排队的平移命令到达过晚或被下一帧覆盖
（"水平平移失效 + 卡顿"）。

**⚠ 原判"对上游有害/是我方崩溃放大器"已被实验证伪（2026-09-17）。**
推理是：绕过后 `Redraw()` → `EnsureSwapChain()`（交换链改写）可从任意线程发起，
而"Present 撞交换链改写"正是 issue #7 触发条件 ⇒ 怀疑它是放大器。
于是做了回退实验（分支 `3fc/exp-revert-svt`，提交 `2b90a87`）：改回上游 `Enqueue` 版后
**8 路崩溃 5/8，同批基线 4/8，没有下降**。
（事后看合理：zoom 只改绘制矩形、不改 swapchain 尺寸，`EnsureSwapChain` 通常 early-return。）

⇒ **它与崩溃无关，我方不需要回退。** 但它仍然**改变上游的命令队列语义**，
推给上游需要 maintainer 认可"view transform 可以脱离队列执行"这一点。

**建议**：作为 **PR-C（可选）** 单独提出，说明动机（平移延迟）与安全依据
（三个 relaxed 原子 + 已保留 disc 保护分支），让上游自行决定；**不塞进 PR-A**。

### 3.2 `TargetAudioBuffer100ns` 120ms → 250ms —— **不推，但我方保留**（结论已修正）

⚠ **修正先前"我方应回退"的判断**：经核对上游 `ea3ce05`，音频缓冲其实是**两处独立的东西**：

| 位置 | 含义 | 上游现状 | 我方 |
|---|---|---|---|
| `WasapiRenderer.cpp` | WASAPI **缓冲区大小** | 已改为 `clamp(sharedDefaultPeriod*3, 50ms, 200ms)` 自适应 ✅ | 跟随上游 |
| `PlayerSession.cpp` `TargetAudioBuffer100ns` | **音频包投喂阈值**（`audioBuffered < 阈值` 才继续喂） | **仍是 1'200'000（120ms）固定值** | 250ms |

- 所以 `PATCHES.md` 类别二说的"已被吸收"只适用于 WASAPI 那一处；投喂阈值上游并未改，
  我方 250ms **不是与已吸收实现重复，也不是自我矛盾**。
- 性质是**延迟换抗欠载**：对 3FCompare（延迟不敏感、高码率多声道 + AV1 软解）合适，
  对通用播放器会增加延迟 ⇒ **不推上游**。
- ⇒ 我方**保留**该项；若将来高码率仍欠载，优先评估调上游 clamp 上限。

### 3.3 `SetPresentConfig` / `SetPacingConfig` —— 不推

- `SetPacingConfig` 是 **no-op**（上游无周期 keepalive present 可抑制）。
- `SetPresentConfig` 只是把 tearing 偏好存下来。
- 对上游而言，塞两个没有实质行为的导出 API 只会增加维护负担与 ABI 压力。

### 3.4 类别四（FLAC 多线程解码 / HDR 元数据去重 + `SetMaximumFrameLatency`）与 P2

- 经核对净差异，**这些已不在当前 diff 中**（已被上游吸收或我方放弃）。
- 特别是 P2 "lock-free Render 快路径"（`interactiveMove_ + try_lock`）：
  它**不在**我方净差异里，是**上游自己的代码**（`VideoRenderer.cpp:3974-3997`）。
  ⇒ 与上游沟通时不要把它当作我方补丁（此前一度误判，已更正）。

---

## 4. 对"会不会搞坏上游 3FP"的总体结论

| 关注面 | 结论 |
|---|---|
| 行为兼容性 | PR-A 全部为**纯新增**；两处删除都只是"加花括号以追加代码"，**语义等价**。PR-B 默认 `-1` 保持原行为 |
| 线程/并发 | PR-A 的两项回读/诊断**沿用上游既有锁序**（`deviceMutex_` → `presentMutex_`），不引入新并发路径 |
| 性能 | `GetRenderTargetInfo` 增加 4 次 relaxed 原子 store，可忽略；`ReadPixelRegion` 相比逐像素是净收益 |
| ABI | PR-A 无影响；PR-B 需升 `PlayerApiVersion` 并同批次发布（已在 §2 标注） |
| **风险项** | **只有 `SetViewTransform` 直写与音频缓冲 250ms 有害，已列入不推** |

---

## 5. PR-A 已就绪（2026-09-17）

| 项 | 值 |
|---|---|
| 分支 | `upstream/pr-a-diagnostics`（基于 `ea3ce05`） |
| 提交 | `bf7cf5f`（主体）+ `56ccb6c`（收尾：`.rc` 中性化 + 注释修正） |
| 改动 | 9 文件 **+267/−1** |
| 编译 | MSVC 14.51 / v145，Release x64，**0 error 0 warning** |
| API 版本 | `FFF3FP_GetApiVersion()` = **14**（未带 A11） |
| 补丁 | `docs/upstream/pr-a.patch`（26 KB，`git apply --reverse --check` 通过） |
| PR 描述 | `docs/upstream/pr-a-description.md` |

已按 §1.1 修掉 `ReadPixelRegion` 的注释/实现不符；`.rc` 的 `CompanyName` 已由
`"3FCompare"` 中性化为 `"FFF_Project"`。

## 6. 待办

- [ ] **需用户确认后**才推送分支 / 开 PR（对外动作）
- [ ] PR-B（A11）前把 `preferredAdapterIndex_` 改为 `std::atomic<std::int32_t>`，
      并拆分出独立分支（本次未做，因涉及 API 版本升级需先与上游对齐）
- [ ] 我方另行评估两项**自我修正**（均由本次审查发现）：
      - `SetViewTransform` 直写路径 —— 它让交换链改写可从任意调用线程发起，
        而我们刚证实"Present 撞交换链改写"正是 issue #7 的触发条件 ⇒ **它可能是
        我方崩溃率高于上游的原因之一**，建议重评是否回退或改成"只写原子量、不直接 Redraw"
      - 音频缓冲 250ms —— 与 `PATCHES.md` 类别二"已被上游吸收"的判断**自我矛盾**，
        上游自适应 clamp(50–200ms) 优于硬编码 250ms，建议回退
