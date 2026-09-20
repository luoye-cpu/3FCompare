# 交接文档:v0.2.5 管线审查与版本规整

> 日期:2026-09-16 凌晨。仓库:`C:\PLAN\3FCompare`,分支 `main`,远端 `origin/main` 已同步。
> 本文件为接手人**唯一必读**入口;细节见 `.workbuddy-ai/memory/` 与 `docs/13~16`。

---

## 0. 状态速览

| 项 | 值 |
|---|---|
| HEAD | `41ae72c` `[v0.2.5] docs+release` |
| 提交数 | 10(由 28 条规整而来) |
| 版本 | `0.2.5`(两个 csproj 已同步) |
| tag | `v0.1.0-beta` / `v0.1.4` / `v0.2.5`(新);`0.1.4`、`winforms-final`(旧,勿动) |
| 远端 | 一致(`main` == `origin/main`) |
| 未提交改动 | 7 改 + 5 新增(§2.1、§2.2 修复代码已就绪且编译通过) |
| 单元测试 | **175 通过**(Core 层已实测) |
| 构建能力 | **✅ 已恢复**——`dotnet restore` 成功,Avalonia XAML 编译器到位(见 §3.1) |
| **实机验证** | **✅ selftest / screentest / sessiontest 全通过**(见 §8) |

---

## 1. 已完成的工作

### 1.1 三轮完整管线审查(本轮主线)

| 轮次 | 文档 | 定位 | 主要产出 |
|---|---|---|---|
| 第二轮 | `docs/14` | 工程基础设施 | 崩溃/资源/并发/门禁 |
| 第三轮 | `docs/15` | **业务正确性** | 帧步进、放大镜、偏移、漂移 |
| 专项 | `docs/16` | 间歇性渲染停滞 | 定位到内核 `Redraw` 契约 |

第三轮换角度问「这个软件做对了没有」,结果最严重的两个问题都在业务语义上,
重要性超过第二轮全部发现。

**核心修复(均有反向验证:摘掉修复 → 确认报错 → 恢复)**:
- 帧步进不暂停(master 被内核置 Paused、从路走 Seek 继续播 ⇒ 错帧)
- 放大镜采样点错位(传面板全局坐标、漏乘 RenderScaling、未排 letterbox)
- master 偏移恒为 0(基准路自身也被加偏移 ⇒ 两套语义矛盾、微调累积)
- 配置非原子写(失败即损坏 + 静默回退默认)
- 会话路径 / FFmpeg 目录未校验(UNC 会外泄 NTLM 凭据)
- 性能:空闲轮询 62Hz→1Hz、探针回读节流、放大镜热路径分配

**复核后判定为非缺陷(勿再纠结)**:
- 帧步进的「帧率分支」——帧率不同时按**时间**对齐才对(24fps 第 100 帧在 4.17s,
  60fps 第 100 帧在 1.67s,按帧号对齐反而指向无关画面)
- 配置迁 `%APPDATA%`——便携部署依赖「配置与 exe 同目录」

### 1.2 版本化历史规整(28 → 10 条)

版本序列(用户指定):

```
v0.1.0-beta → v0.1.1 → v0.1.2 → v0.1.3 → v0.1.4 → v0.2.0 → v0.2.5(4 条)
```

⚠ **`0.1.4`(无 v 前缀)指向 WinForms 代码 `36daa62` 且关联 GitHub release,
绝对不要覆盖**。新序列一律用 `v` 前缀。

### 1.3 移除 CI

`.github/workflows/ci.yml` 已从工作区与**全部提交历史**中移除(不是只在最新版本删,
是重写历史,任何一条都不含它)。`docs/11`、`docs/13` 中的相关条目已标注
「已决定不实施」,保留原文以维持审查记录完整性。

---

## 2. 未完成的工作(按优先级)

### 2.1 滚轮缩放的最终值会被节流吞掉 —— ✅ 已修 + 实机验证通过

**位置**:`src/3FCompare/MainWindow.axaml.cs`

- `ApplyViewTransform()` 第 609 行:`if (now - _lastPanApplyTicks < 16) return;`
  16ms 节流**直接丢弃**更新
- `_lastPanApplyTicks` 全项目仅 3 处引用:声明、节流判断、**`OnSurfaceRelease` 第 574 行补发**
- `OnSurfaceWheel`(第 512-517 行)只调 `ApplyViewTransform()`,**没有补发路径**

**后果**:滚轮事件停止后不会再有调用,最后一次缩放永久丢失。画面停在旧缩放级别,
与状态栏显示不符,且**不自愈**。触控板惯性滚动(间隔可低至 8-16ms)极易触发;
「缩放后立刻按重置视图」同样可能被吞(第 622 行 `ResetViewTransform` 也走同一函数)。

**修复方案(✅ 已实施并通过反向验证)**:
节流丢弃时 `TransformDroppedCount++` 并 `ScheduleTransformFlush()` —— 一次性
`DispatcherTimer`(16ms,**UI 线程**),补发前校验 `_sync.Count > 0`。
⚠ 切勿用 `Task.Run` 后台 P/Invoke —— 代码注释已说明:会话重建/关闭时 UI 线程已释放
原生句柄,后台访问会触发 0xC0000005 闪退。
⚠ `ScheduleTransformFlush` 里 `IsEnabled` 时**不能重复 Start**(会重置计时 ⇒ 补发被饿死)。

**实机结果**:5 个真实素材全部 `丢弃=3 → 补发后 已下发=1.749 == _viewZoom` ✅(见 §8.2)。
反向验证:注释掉 `Start()` ⇒ 内核停在 1.150 而 UI 为 1.749 ⇒ 用例确实测得出差异。

### 2.2 放大镜 vs 探针一致性断言 —— ✅ 已编译 + 实机验证通过

- `MagnifierOverlay.TryGetCenterSample()`(新增 internal):取采样网格中心像素
- `MainWindow.SelfTest.cs` 新增「放大镜-探针一致性」步骤:两者指向同一点比对 RGB,
  差 > 0.02 抛异常

用途:放大镜和探针是「取光标下像素」的**两份实现**,本轮 P0 正是放大镜坐标错而探针对。
只验证坐标公式不够(公式对了也可能传错参数),用真实像素值交叉验证才可靠。
单路即可做,不受多路崩溃阻塞。

**状态**:✅ 已编译并实机验证(原阻塞于 §3 环境限制,该限制已解除)。
注:实施过程中还发现并修复了**探针坐标域错误**(内核 `FFF3FP_ReadVideoPixel` 的坐标域是
后台缓冲、不是片源分辨率),已收敛到 `Core/Backend/PixelReadback.cs` 单一实现;
断言也从单点升级为 **3×3 九宫格**(单点可能"碰巧一致"而漏判 RenderScaling 类错误)。
实机结果见 §8.2。

### 2.3 停滞修复疗效未验证

`docs/16` 记录:定位到「子 HWND resize 后须由应用调 `Redraw()` 才会继续 flips」的
内核契约,第一级停滞恢复补了 `RedrawAll`。**12 次尝试未复现,疗效未确认。**

### 2.4 漂移校正参数未实测

1s 冷却 / 半帧阈值为保守值,未在真实多路长片调参。
**阻塞原因**:多路播放崩溃(根因在上游 `FFF.Native`,已提交上游 issue #7)。

### 2.5 其它已知未决

多路崩溃本身。
(放大镜多格/DPI 实机断言已于 2026-09-16 完成 —— 单路 3×3 九宫格即可覆盖,见 §2.2/§8.2。)

### 2.6 ✅ 已修复:设置里的「解码 GPU 选择」从未生效(2026-09-16 发现并修复)

> **状态:已于 2026-09-16 完整实现并实机验证通过。** 下方缺陷分析保留作定位记录,
> 实现与验证见 §8.7。

核实 `docs/03` / `docs/06` 的待确认项 A11 时发现,**不是"未实测",而是根本没接线**:

| 环节 | 状态 |
|---|---|
| 设置窗口 GPU 下拉框(选项来自 `GpuEnumeration` / Win32 `EnumDisplayDevices`) | ✅ 有 |
| `AppSettings.PreferredAdapterIndex` 持久化(-1=系统默认) | ✅ 有 |
| 会话存档 `SessionItem.AdapterIndex` | ✅ 有 |
| `EngineSessionOptions.PreferredAdapterIndex` | ✅ 有 |
| `PlaybackCoordinator` 传给 `_engine.CreateSession` | ✅ 有 |
| **`Fff3FpEngine.CreateSession` 使用它** | ❌ **完全没有** |
| **内核 `Fff3FpConfiguration`(Api.h v14)有 adapter 字段** | ❌ **没有**,且无 `FFF3FP_SetAdapter` |

⇒ **用户选了第 2 张 GPU,实际仍用系统默认,界面无任何提示** —— 静默给出假的设备能力承诺。

附带发现:`IPlayerEngine.EnumerateAdapters()` 返回硬编码 `"System Default (D3D11)"`,
**生产路径并不使用它**(只有 SmokeTests 打印),真实下拉框走 `GpuEnumeration`。
方法名与真实能力不符,易误导后来者。

**建议**(未实施,需内核侧扩展):要么在内核加 `FFF3FP_Create` 适配器参数或 `SetAdapter`
(属 `third_party/fff_project/PATCHES.md` 重移植范畴);要么先把 UI 选项**禁用或标注"暂不生效"**。
详见 `docs/06` §3 A11 专项。

---

## 3. 环境状态(接手人必读)

### 3.1 构建能力:✅ 已恢复(2026-09-16 实机确认)

| 工程 | 外部包 | 状态 |
|---|---|---|
| `3FCompare.Core` | 0 | ✅ 可构建 |
| `3FCompare`(UI) | 5(44 传递) | ✅ 可构建,XAML 编译正常 |

**卡点曾为**:`avalonia.build.tasks` **不在 NuGet 缓存**(缓存有 10 个 Avalonia 包,
独缺 XAML 编译器)⇒ XAML 无法编译,报 154 个
`CS0103: 当前上下文中不存在名称 MainArea / BtnPlayPause / StatusInfo`。

**已用一条命令解除**(实机结果见 §8.1):
```powershell
cd C:\PLAN\3FCompare
dotnet restore
dotnet build src\3FCompare.slnx -c Release -p:KernelConfiguration=Release        # 0 警告 0 错误
dotnet test  tests\3FCompare.Core.Tests\3FCompare.Core.Tests.csproj -c Release   # 175 通过
```

### 3.2 `dotnet restore` 失败的根因(**仅特定沙箱;本机不复现**)

> **2026-09-16 更新**:本机 `dotnet --version` = `11.0.100-preview.7.26381.103`、
> `APPDATA` 已有值,`dotnet restore` **一次成功**,本节故障不复现。
> 原文保留备查——换环境/换终端若再次出现下列报错,可直接照本节结论判断,不必重排。

`NuGet.targets(796,5): Value cannot be null (Parameter 'path1')`

机制(已穷举验证,**不必重复排查**):
1. 根因:shell 里 `APPDATA` 为空 ⇒ NuGet 拼 `%APPDATA%\NuGet\NuGet.Config` 时
   `Path.Combine(null,…)` 抛异常。加 `--configfile` 后错误会变成
   `ConfigurationDefaults 类型初始化失败`(同一根因,可作判据)。
2. Git Bash 陷阱:`APPDATA="C:\\..."` 会被解析成单反斜杠再被 MSYS 转成 `C://Users//...`。
   正确写法是**单引号 + 正斜杠**:`APPDATA='C:/Users/20210/AppData/Roaming'`
3. **但传对了也没用**:NuGet 走 `Environment.GetFolderPath(ApplicationData)`,
   读 Windows Shell API 而非环境变量。补全
   APPDATA/LOCALAPPDATA/ProgramData/USERPROFILE/HOME/NUGET_PACKAGES 全无效。
4. PowerShell 工具里 `dotnet --version` 也为空 ⇒ **该工具跑不了 dotnet**。

### 3.3 部分恢复技巧(**已不再需要,仅备查**)

> **2026-09-16 更新**:`dotnet restore` 已成功,本节技巧**不要再用**。
> 若沿用手工生成的 `obj/project.assets.json`,反而可能与真实依赖漂移。
> 想恢复干净状态:删掉对应工程的 `obj/` 后重跑 `dotnet restore`。

绕开 restore 的旧办法(手工写 `obj/project.assets.json`):

- **Core(0 包)**:手写最小 assets(targets 空、libraries 空、
  `frameworkReferences` 给 `Microsoft.NETCore.App`)⇒ 构建 + 151 单测全通过。
  当前 `src/3FCompare.Core/obj/project.assets.json` 就是这么来的。
- **App**:可从缓存递归解析依赖生成(44 包),但缺 `avalonia.build.tasks` 仍失败。
  ⚠ 生成时必须包含包内 `build/` 与 `buildTransitive/` 下的 `.props`/`.targets`
  (Avalonia XAML 编译器靠它们注入),只列 `lib/` 会同样报 CS0103。
  ⚠ 目标框架是 `net11.0-windows`,写成 `net11.0` 会报 NETSDK1005。

### 3.4 其它沙箱约束

- 构建/测试必须前置 `APPDATA='C:/Users/20210/AppData/Roaming'`(单引号正斜杠)
- **禁止 `git stash`**(曾因 SIGTERM 中断导致对象丢失、28 个提交不可恢复)
- `msbuild` / `cmd.exe` / PowerShell `Add-Type` 被安全策略拦截
- `.git/refs/` 下写 ref 可能被静默拦截;写远端跟踪 ref 用 PowerShell
  `New-Item -ItemType Directory -Force` + `WriteAllText`(**必须带结尾 LF**),写完复核
- `Remove-Item` 会触发 bulk-delete 守卫,删文件用 Bash `rm -f`
- 沙箱无回收站;可恢复删除改用 `mv` 到项目外

---

## 4. 关键约定(必须遵守)

### 4.1 提交规范

- 标题统一 `[vX.Y.Z] <type>: <摘要>`
- 每个版本合并为 **3~4 条主题化提交**(用户明确嫌 7 条太碎、24 条更不可接受)
- 三者必须同步:**git tag / csproj `<Version>` / `CHANGELOG.md`**

### 4.2 提交时机由用户决定 —— 最容易踩的坑

**不得自行提交**。必须:跑完「单测 + 构建 + selftest + sessiontest + screentest」
→ 把结果和分组方案给用户确认 → 等指示后才动 git。

> 2026-09-15 因未等完整实机测试就自行提交 24 条,被要求全部撤回
> (`git reset --soft` + force push,代码未动靠 `git diff` 核对为空确认)。

### 4.3 其它

- **禁止 CI / GitHub Actions**,所有构建与验证只在本地进行
  (`tools/构建全部.ps1` + `--selftest/--sessiontest/--screentest`)
- 自测素材:禁用 ffmpeg 合成的 `testmedia/media/small.mp4`;
  采用 `testmedia/media/real/`(Jellyfin 官方,真实拍摄)
- **多路测试失败时,先核对素材体积再怀疑代码**(2026-09-16 血泪):
  `real_4k_hevc10_60m.mp4` 曾只有 1 MB(标称 211 MB),导致该路永不 Ready,
  `sessiontest` 误报「重载未就绪 / 步进前置不足」,一度被误判为多路回归(§8.3)。
  素材体积对照见 `testmedia/media/real/SOURCES.md`;`docs/09` 有重新下载 URL
- **新增自测模式必须同步加入 `MainWindow` 构造最前面的 `_selfTestMode` 白名单**,
  否则退出时 `SettingsStore.Save` 会把测试窗口几何写进用户 `settings.json`
- 自测分发读 `Environment.GetCommandLineArgs()`,不要手工拼 `-internal`
- 统一退出口 `ExitSelfTest(code)` 末尾是 `TerminateProcess`,其后代码不执行
  (看着像 bug,其实不是,别"修")
- 内核基线归档在 `.3fc_kernel_baseline.bundle`(约 2MB,有意入库);
  校验判据 = `FFF.Player.Api.h` 同时含 `FFF3FP_SetPresentConfig` / `SetPacingConfig` /
  `GetRenderTargetInfo` / `ReadVideoPixelRegion`

---

## 5. 记忆与文档索引

| 路径 | 内容 |
|---|---|
| `.workbuddy-ai/memory/MEMORY.md` | **项目长期约定(权威)**,含环境约束、提交规范、内核基线 |
| `.workbuddy-ai/memory/2026-09-15.md` | 本轮工作日志(审查、撤回、规整、移除 CI、环境故障) |
| `.workbuddy-ai/memory/2026-09-14.md` | 前一日日志(git 对象损坏事故) |
| `docs/14` | 第二轮管线审查(工程基础设施) |
| `docs/15` | 第三轮管线审查(业务正确性)+ **已回填修复状态追踪** |
| `docs/16` | 间歇性渲染停滞专项排查 |
| `docs/upstream/issue-*.md` | 上游多路崩溃 issue 全文 |
| `CHANGELOG.md` | v0.1.0-beta ~ v0.2.5 完整变更记录 |
| `testmedia/media/real/SOURCES.md` | 真实素材清单(规格/**体积**)——多路测试前核对体积用 |
| 本文档 §8 | **实机验证结果**(构建/单测/四类跑测 + 素材损坏事故) |
| 本文档 §9 | 提交前待办与建议分组(§4.2:等用户指示) |

---

## 6. 下一步(2026-09-16 更新:1~4 已完成)

| # | 原计划 | 状态 |
|---|---|---|
| 1 | 恢复构建(§3.1 一条命令) | ✅ `dotnet restore` 成功,0 警告 0 错误 |
| 2 | 修滚轮缩放节流丢失(§2.1) | ✅ 已修 + 5 素材实机通过 |
| 3 | 编译验证放大镜一致性断言(§2.2) | ✅ 已编译 + 5 素材实机通过 |
| 4 | 跑完整验证并给用户确认 | ✅ 已跑完,结果见 §8;**等用户指示才动 git**(§4.2) |

**之后的下一步**(均依赖稳定的多路长播,当前被 `--multitest` 原生崩溃阻塞):

1. 多路崩溃跟进上游 issue #7(唯一能解锁下面两项的前置)
2. 停滞修复疗效复测(§2.3,现为 12 次未复现、疗效未确认)
3. 漂移校正参数实测(§2.4,1s 冷却 / 半帧阈值为保守值)
4. 放大镜多格/DPI 实机断言(§2.5)

---

## 7. 本轮元发现(对后续开发有指导意义)

三处缺陷都源于**同一功能存在两份实现,其中一份是错的**:
探针的坐标映射是对的、放大镜的是错的;预设映射与恢复逻辑不同源。
⇒ **收敛到单一实现,比事后补测试更能防住这类问题。**
§2.2 的交叉验证断言正是针对这一点设计的。

---

## 8. 实机验证结果(2026-09-16,接手人完成)

### 8.1 构建能力已恢复(§3.1 卡点解除)

```powershell
cd C:\PLAN\3FCompare
dotnet restore src\3FCompare.slnx          # ✅ 成功（APPDATA 在本机有值，NuGet 不再抛 path1）
dotnet build  src\3FCompare.slnx -c Release -p:KernelConfiguration=Release
# ✅ 已成功生成。0 个警告 0 个错误（含 App/UI 工程，XAML 编译正常）
dotnet test   tests\3FCompare.Core.Tests\3FCompare.Core.Tests.csproj -c Release
# ✅ 已通过! 失败 0，通过 175（较交接时的 151 增加 24：PixelReadback + RenderStallWatchdog 新增用例）
```

> §3.2 记录的环境故障**在本机不复现**:`dotnet --version` = `11.0.100-preview.7.26381.103`,
> `APPDATA` 已正确设置。§3.3 的手工 `project.assets.json` 技巧**不再需要**。

### 8.2 实机跑测(素材一律用 `testmedia/media/real/`,非合成图案)

| 模式 | 素材 | 结果 |
|---|---|---|
| `--selftest` | 4K H.264 / 4K HEVC HDR10 / 4K AV1 10bit / 8K AV1 HDR10 / 8K HEVC 10bit | ✅ 全部 `exit=0` |
| `--screentest` | 4K H.264(+ HEVC 第二路) | ✅ `exit=0`,导出 1995×1122,PNG 794 KB |
| `--sessiontest` | 4K H.264 + 4K HEVC HDR10 | ✅ `exit=0` |
| `--sessiontest` | 4K H.264 + 4K HEVC 10bit | ✅ `exit=0`(连跑 2 次) |
| `--multitest` | 4K H.264 × 2 路 | ⚠ 概率性原生崩溃(见 §8.4) |

**§2.1 滚轮缩放节流补发**——全部 5 个素材均通过,实机日志:

```
连发 4 次：_viewZoom=1.749 已下发=1.150 丢弃=3
补发后：_viewZoom=1.749 已下发=1.749（期望≈1.749）
✅ 节流补发通过：被丢弃的最终值已补发到内核
```

**§2.2 放大镜-探针一致性**——全部 5 个素材均通过,最大差 **0.0000~0.0010**(阈值 0.02):

```
✅ 九宫格一致性通过：6/9 个有效采样点全部一致（最大差 0.0000 @ 1/4，RenderScaling=1.5）
✅ 九宫格一致性通过：7/9 个有效采样点全部一致（最大差 0.0000 @ 1/4，RenderScaling=1.5）  ← 8K AV1
```

**§2.1 连带验证(docs/15 §2.1 帧步进全路暂停)**——sessiontest 实机确认:

```
多路帧步进后状态: 路0=Paused, 路1=Paused     ← 修复生效（从路不再继续播）
✓ 路数=2 位置=1003ms 状态=Playing 布局=3x3   ← 位置/布局随会话还原
```

### 8.3 顺带发现并修复:测试素材 `real_4k_hevc10_60m.mp4` 损坏

| 项 | 值 |
|---|---|
| 现象 | 该素材实际 **1 MB**,而 `SOURCES.md` / `docs/09` 均标称 **211 MB** |
| 影响 | 用它跑多路测试 ⇒ 该路永远不 Ready,`sessiontest` 误报「重载未在 30s 内就绪 / 步进前置不足」 |
| 判定 | **不是产品缺陷**,是素材下载不完整(其余 5 个素材体积全部与标称一致) |
| 处理 | 已按 `docs/09` 的 URL 重新下载 → **211 MB 校验通过**;损坏副本已删除 |

> ⚠ **踩坑记录**:这一度让 sessiontest 看起来像"多路回归",差点误判为 §2.4 的上游崩溃。
> **多路测试失败时,先核对素材体积再怀疑代码。**

### 8.4 仍未通过的项(与交接判断一致,非本轮回归)

- `--multitest` 多路长播**概率性原生崩溃**:`0xC0000005` / `0xC000001D`。
  20s 预算 3 次跑 = 1 通过 2 崩溃;25s 预算 2 次跑 = 2 崩溃。
  `SIGILL`(0xC000001D)是 CPU 级非法指令,托管 C# 不可能产生 ⇒ 必在原生内核/D3D 路径,
  对应**上游 issue #7**,不在本仓库可修范围。
- 由此 §2.3(停滞修复疗效)、§2.4(漂移参数实测)仍被阻塞 —— 二者都依赖稳定的多路长播。

### 8.5 一处非缺陷确认(勿改)

单路 selftest 日志里会出现 `运行时错误=播放第 0 路: Play 失败: InvalidState`。
追查确认来源为 `MainWindow.axaml.cs:1385` 的**会话重建后**兜底 Play
(`RecoverFromFailedAsync` 内,状态为 Paused/Ready/Ended 时补一次 Play)。
该 Play 只在"会话重建"上下文中执行,语义正确(重建后本就要恢复播放),
且 `LastRuntimeError` 成功后不清除 ⇒ 旧值会残留显示。**不影响功能,不是缺陷,不要"修"。**

### 8.6 ✅ 已解决:内核 DLL 旧基线问题(并顺带补齐 9.14.1 缺失的构建验证)

> **状态:已解决。** 在修复 §2.6(A11)时一并重建了内核,现基线为 `68e1965`
> (`3fcompare-kernel-2026.9.14.2`),`.3fc_kernel_sha` 与 S5 戳记均已同步,
> `tools\构建全部.ps1` 端到端跑通。下方为当时的定位记录。

修复前的实测值:

| 项 | 实测值(修复前) |
|---|---|
| 内核 HEAD | `025198f`(基线 `3fcompare-kernel-2026.9.14.1`) |
| 项目根 `.3fc_kernel_sha` | `6bc8d61c`(**旧基线 9.11.1**) |
| `FFF.Native\x64\Release\FFF.Native.dll.3fcbuild` 戳记 | **不存在** |

⇒ 当时部署的 DLL **仍是旧基线产物**,而且**§8 的全部实机验证就跑在这个旧 DLL 上**。

**为什么功能结论依然成立**:上游该区间只改了 `FFF.Player` / `FFF.Recorder` 两个 vbproj(8 行),
`FFF.Native` **源码零改动**;4 项扩展 API 两个基线都具备(已由 `Assert-KernelExtensions` 按
`FFF.Player.Api.h` 导出面校验)。

**但发布前必须补这一步**,否则产物与钉死基线不符:

```powershell
.\tools\构建全部.ps1    # S5 戳记校验会发现「无戳记 / 属旧 HEAD」⇒ 自动 /t:Rebuild
# 跑完确认 .3fc_kernel_sha 变为 025198f…,再重跑 --selftest / --sessiontest / --screentest
```

> 工具链**已不再是阻塞项**:本机有 VS 18 Community 的 MSBuild + MSVC `cl.exe` 14.51.36231,
> 且 libass(vcpkg)与 FFmpeg 头文件均已就绪。
> ⚠ 这条同时更正了 `MEMORY.md` §6 与 `docs/13` §19.7 中"本机无 MSBuild"的旧记载。

### 8.7 A11「多显卡指定」实现与验证(2026-09-16)

**内核**(归档 `3fcompare-kernel-2026.9.14.2` / `68e1965`,两条提交):
`FFF3FPConfiguration` 末尾追加 `preferredAdapterIndex` → `PlayerApiVersion` 14→**15** →
`VideoRenderer` 成员 + `SetPreferredAdapterIndex()` + `EnsureDevice()`「指定索引优先、
失败回落 monitor 匹配」 → `PlayerSession` 构造期接线 → `FFF.Native.rc` 版本同步。
内核侧另加了 `A11: device adapter requested=… vendor=… luid=…` 诊断日志,
走 `FFF3FP_KernelLogImpl` 落盘到 `logs/app-*.log`(**不输出到控制台**)。

**托管**:`Fff3FpNative.cs` 同步字段;`Fff3FpEngine.cs` `ConfigVersion`→15 并传入
`options.PreferredAdapterIndex`;`GpuEnumeration` 数据源改为 **DXGI `EnumAdapters1`**。

**为什么还得改枚举源**(第二层缺陷):内核用 DXGI 索引,旧托管枚举用 Win32
`EnumDisplayDevices`(DISPLAY 设备),**索引空间不同** ⇒ 只接线不改枚举会选错卡。
本机实测:DXGI 有 5 个适配器(3×NVIDIA + Intel + Microsoft Basic Render Driver),
而 DISPLAY 枚举列不全。

**验证**(新增内核诊断日志使"是否生效"可被直接观测):

| 设置 | 内核 vendor | 实际 GPU | selftest |
|---|---|---|---|
| 默认 `-1` | `4318`(0x10DE) | NVIDIA(窗口所在显示器) | EXIT=0 ✅ |
| 指定 `0` | `4318`(0x10DE) | NVIDIA RTX 5080 | EXIT=0 ✅ |
| 指定 `1` | `32902`(0x8086) | **Intel UHD(切换成功)** | EXIT=1(测试脆弱性,见下) |

vendor 与 LUID 均随指定改变 ⇒ **选择真正生效**。
其余:`--sessiontest` / `--screentest` / 单测 175 / `构建全部.ps1` 全部通过。

⚠ **遗留(待定)**:指定 Intel 集显时 selftest 报 `节流用例空转:没有更新被丢弃`。
根因是集显渲染慢 ⇒ 同步连发 4 次 `OnSurfaceWheel` 间隔超过 16ms ⇒ 无一次被丢弃 ⇒
用例"前置条件坐实"分支判定空转。**属既有测试脆弱性**(依赖真实时间),非 A11 功能缺陷;
默认 GPU 下不受影响。是否放宽该断言请用户定夺。

⚠ **ABI 破坏性**:`FFF3FP_Create` 对 `version` 做**严格相等**校验且要求 `size >= sizeof(config)`,
内核与托管**必须同批次发布**;字段只能追加在结构体末尾。已登记为 `PATCHES.md` 类别一第 5 项。

---

## 9. 提交前待办(§4.2:不得自行提交,等用户指示)

验证已全部跑完,结果如上。建议的提交分组(待用户确认后才动 git):

1. `[v0.2.5] fix: 滚轮缩放节流补发`(§2.1,`MainWindow.axaml.cs` + 自测步骤)
2. `[v0.2.5] test: 放大镜-探针一致性断言`(§2.2,`MagnifierOverlay` + `MainWindow.SelfTest`)
3. `[v0.2.5] feat: 像素回读坐标域换算 + 呈现停滞看门狗`(Core 新增 2 文件 + 2 测试)
4. `[v0.2.5] docs: 交接文档与实机验证结果`

版本三处同步(§4.1):csproj `<Version>` 已是 `0.2.5`;`git tag v0.2.5` 已打;
**`CHANGELOG.md` 已补记本轮项**(含单测 175、坐标域换算、看门狗、节流补发、一致性断言),
本次文档更新一并修正了它的**版本倒序**问题——原先 `[v0.2.5]` 被追加到文件末尾。

**打包发布前另需**(不属于上面 4 条提交):
- 补跑 `.\tools\构建全部.ps1` 重建内核,使 DLL 与基线 `025198f` 对齐(见 §8.6)。

---

## 10. 文档维护记录(2026-09-16 全仓 md 清理)

本次按"更新所有 md、清理过时 md"过了一遍全仓 33 个 md。
**未删除任何文件**——项目约定"审查记录保留原文以维持完整性",
过时结论一律采用**顶部提示 + 指向本文档**的方式处理,而非改写或删除。

### 10.1 内容性修正(直接影响正确性)

| 文件 | 修正 |
|---|---|
| `CHANGELOG.md` | **版本倒序修正**:原 `[v0.2.5]` 被追加到文件**末尾**(在 `v0.1.0-beta` 之后),极易误判"缺失";已重排为倒序并加顶部说明。单测 97→**175**;补记本轮未收录项(坐标域换算 / 看门狗 / 节流补发 / 一致性断言) |
| `README.md` | 工程状态标题 `0.2.0-BETA,2026-08-25` → **v0.2.5**;单测 53 → **175**;VRR 由"待实测"→ **已实测支持**;补 v0.2.x 功能条目 |
| `PACKAGING_SPEC.md` | §5.1 版本示例 `0.2.0` → **0.2.5**(规范自身与真源脱节);顶部加当前版本 |
| `docs/09` | 补**素材体积校验**警告(hevc10 损坏事故);多路崩溃由"必现"改为**高概率 + 量化**;§4 环境提示标注失效 |
| `docs/15` | 回填 2.2 / 3.3 / 放大镜 DPI 断言的实机结果;**订正元发现**——原判"探针对、放大镜错"有误,实测**两者都错**(错在不同层面) |
| `docs/03` | VRR(A8/A9)由"待确认"→ **已确认支持**;补 A2/A3/A4 实测结论;补齐**原表缺失的 A8/A9/A11 编号** |
| `docs/13` | §19.7 内核构建:标注本机**已有 MSBuild + MSVC**,并记入"DLL 仍属旧基线"这一真实缺口 |
| `MEMORY.md` | §0 补实机状态;§1 标注沙箱约束与本机差异;§3 固化素材体积约定;§6 更正"本机无 MSBuild";§8 补崩溃量化 |

### 10.1.1 第二轮(核实「待确认」项后追加)

按用户要求核实 `docs/03` / `docs/06` 遗留的 A1~A11:

| 文件 | 修正 |
|---|---|
| `docs/06` | 这里有**比 docs/03 更完整**的 A1~A11 清单(docs/03 只到 A7 且缺 A10)。全部回填状态;新增 **A5 专项**(伪命题)与 **A11 专项**(未接线缺陷) |
| `docs/03` | 补齐 A5/A7/A10/A11;`GetSnapshot` 错误理解更正;§2 表两行更新 |
| `README.md` | 修正对外失实宣称:**「多显卡解码指定」实际未生效**(原先 3 处宣称支持) |
| `HANDOFF` §2.6 / `MEMORY.md` §12 | 记录新增缺陷,防丢失 |

**核实结论**:A1、A2、A3、A4、A7、A8、A9、A10 均已落地(见 `docs/06` §3);
**A5 是伪命题**(`GetSnapshot` 是状态快照不是截图);
**A6 仍未实测**(无窗口模式帧序列稳定性);
**A11 是真实缺陷**(见 §2.6)。

### 10.2 加存档导航(结论保留,仅加顶部指引)

`HANDOFF-可用性修复.md`(标为已归档,并订正其已失效的两处结论)、
`docs/08`、`docs/11`、`docs/12`、`docs/16`(补判定下沉 Core 的进展)、
`tests/README.md`(53→175 例)、`tools/README.md`(restore 约定失效 + 内核恢复改走 bundle)、
`third_party/README.md`(内核获取非 submodule)。

### 10.3 仍未处理(需用户决定)

- 是否**删除** `HANDOFF-可用性修复.md`(2026-09-14 已完成,仅剩事故说明价值)。
- `docs/01~07`(需求/架构/设计)中仍有少量"待确认"表述(如 A11 多显卡 GPU 指定、
  截图是否含 OSD),未做实测,保持原样。
