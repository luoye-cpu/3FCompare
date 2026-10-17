# HDR10+ 上游 PR —— 一个 PR 那一版（2026-10-09，取代同日的三分支包）

> **一句话**：一个 PR、一笔提交、**18 个文件**，base 就是**上游当前的 master**。
> 移植已真做、真编、真跑：**0 error / 0 warning**，三个部署位守卫全绿，15 条电池判据三臂逐字相同（§三）。
> **分支已 push（2026-10-09，见 §五）；PR 还没提**（那一下归你）。同日两份旧包（三分支包、以及本件早先"只能 stacked 在 api16 上"的结论）**已被本件取代**。

## 一、两个候选分支，都建在一次性克隆里（两棵活树一个字节没动）

| 分支 | 提交 | 父 | 面 | 编过吗 | 判据 |
|---|---|---|---|---|---|
| **`upstream/hdr10plus-on-master`** ← 唯一交付 | `fbd98fc` | **`12d8afc` = 上游 master 本体** | **18 文件** / +3,694 −731 | **0 error / 0 warning**（`_3fp-fix/review20261009/main/build-port-slotpark.log`） | 15 条电池三臂逐字相同 + `mw_curve` 判据全绿（§三） |
| ~~`upstream/hdr10plus-all`~~ **已退役不作 PR**（审查轮后 TP 的生成物头归位成生成器版式 ⇒ 该支会带约 2.5 万行纯版式；且它带不动 `.rc`/VB 宿主在上游 lineage 上的对账） | `1db27cf` | `3940990` | 15 文件 / +27,059 −24,158 | 出货树本身即此内容（`A7399C7E24B925A0`） | 同一套判据，且与出货 DLL 逐字节同源 |

```
克隆  C:\PLAN\3FCompare\_3fp-fix\pr1\tp-clone   （检出在 upstream/hdr10plus-on-master，工作树干净）
重建  py _3fp-fix/pr1/build_pr_branch.py        api16 那一支（幂等，含无损断言）
      _3fp-fix/pr1/{resolve_conflicts.py, setup_clone_build.py, upstream_loss_check.py}   master 那一支
```

**base 为什么终于能用 master**：早先我判"不能"，理由是 `merge-tree` 有 12 个冲突文件。把补丁真打到 `12d8afc` 之后，实况是
**5 个文件 / 9 个冲突 hunk**（`merge-tree` 统计的 12 个里含 `SdrShaderBytecode.h` 之类可重生成的、以及那 1 个已被上游删除的
VB 测试 —— 本笔根本不碰它）。9 处全部可裁决，裁决后编译与判据都过 ⇒ 结论改判。

## 二、9 处冲突的裁决记录（这就是"分析一下冲突"的结果）

| 文件 | hunk | 上游那侧是什么 | 我们的那侧是什么 | 裁决 |
|---|---|---|---|---|
| `Api/PlayerApi.cpp` | 1 | `PlayerApiVersion = 16` + 新增 `using PlayerMediaText::CopyUtf8;`（上游把拷贝助手抽了公共头） | 版本 17 + 三段升版理由注释 | **合**：留上游的 `using`，版本取 17 |
| `Core/PlayerSession.cpp` | 1 | 主帧路径改成 `Render(frameToRender, …)` 且新增 **`gamutConverted` 帧需 `av_frame_free`** | `Render(renderFrame, nextIndex, …)` | **合**：`Render(frameToRender, nextIndex, …)` + **保留上游的 free**（丢了就是内存泄漏） |
| `Hdr/HdrProcessor.cpp` | 1 | `#include "3FP/Render/ColorExtension.h"` | 我们两个新头 | **两留** |
| `Render/VideoRenderer.h` | 1 | 上游已有 `IsWideGamutSource()`（第 246 行） | 我们改 `Render` 签名 + 重复抄了一遍那个声明 | **只留签名**。我第一版按"两留"做，编译直接 `C2535 成员函数重复` ⇒ 编译器抓到，已改正 |
| `Render/VideoRenderer.cpp` | 1 | HLSL 里新增 `#ifdef FFF_SDR_LAYOUT` 编译期特化块 | 新增 `cbuffer DynamicSettings : register(b2)` + `Texture2D CurveLut : register(t3)` | **两留**（都在字符串顶部，互不遮挡） |
| 〃 | 2 | `SetHdrMetadata()` 的短注释版 | 我们的 `BuildDynamicToneSettings` + 注入入口 + 长注释 | **取我们**，并修好被粘连的 `noexcept {` |
| 〃 | 3 | （空） | 带 `frameIndex` 的 `ProcessFrame` 调用 | **删上游那行三参调用**，换成四参版（保留在函数体原位） |
| 〃 | 4 | 上游**自己写的**三档判决：`DV 兼容层 → 2/1`、`frameDeclaresTrc && transferFromFrame`、`格式回退` | 我们把它抽成 `ResolveDecodeTransfer`（注入重推路径要复用同一判决） | **取我们**，上游的 `frameDeclaresTrc/transferFromFrame` 计算仍在原位喂缓存。逐臂对过：`ResolveDecodeTransfer` 的三条 = 上游那三条，**语义无损**（这是本移植最要紧的一条，见 §四） |
| 〃 | 5 | 上游把 COM 释放改成 `ReleaseCom(T*&)` 助手，并新增 `scaleConstants_` / `sdrPixelShaders_[]` / `extensionEnhancement*` | 我们手写 `if (x) { x->Release(); … }` + 三个新成员释放 | **取上游风格** + 追加 `ReleaseCom(dynamicCurveView_/Texture_/Constants_)` 与 `dynamicCurveBuilt_ = false`。选我们那侧会**漏掉上游新增的 5 个释放** ⇒ 这是唯一一处"选错就回归"的裁决 |

**上游代码有没有被移植吃掉**（`upstream_loss_check.py`，顺序无关的集合差）：5 个文件里上游有、port 没有的**代码行共 21 条**，
逐条都能对上本笔补丁的改写点 —— `PlayerApiVersion = 16`；6 条 `Render(…)` 旧调用（全部换成了带 `kUnknownFrameIndex`/`nextIndex`
的新调用）；13 条 HDR10+ 峰值计算旧写法（含诊断标签按我们的措辞改成 `"HDR10+ per-window tone mapping"`，上游原文
`"HDR10+ metadata-guided display mapping"`）；`Render` 旧声明；三档 transfer 内联判决的 4 行。**没有一条是无关丢失。**

**着色器字节码**（移植必须重生成，本笔在 api16 层上不需要）：`tools/generate_shader_bytecode.py`（fxc 10.0.28000.0）跑两遍
——`ShaderBytecode.h` 里 **7 个数组只有 `FFFPixelShaderBytecode` 变**（134,752 → 141,872 B，与出货臂那一笔的增量**完全一致**），
其余 6 个与上游**逐字节相同**；`SdrShaderBytecode.h` 重生成后与上游**三个数组全同** ⇒ 本机 fxc 与上游工具链产出一致，
且 b2/t3 声明不改变 SDR 特化变体的字节码（未被使用时被编译期剪掉）。**结论：PR 只带 `ShaderBytecode.h` 一个生成物，不带 SDR 头。**

**探针能不能读移植后的二进制**（先验证再取数）：`FFF3FPConfiguration` 15 个字段、`FFF3FPSnapshot` 58 个字段在
`3940990` 与 `12d8afc` 之间**完全相同** ⇒ 按 TP 头编译的探针偏移量对新二进制有效，读数不是错位噪声。

## 三、判据对照（收尾趟：三臂同一轮、同一批 exe，只换 `FFF.Native.dll`）

臂身份 = sha256 前 16 位。`758DC466A28C2C84` 是 D9 修复**前**的对照臂，`B536621D4AA6D576` 是出货树修复后，
`B92A0B93A4935CB4` 是本 PR 内容编译出的移植臂。件：`_3fp-fix/artifacts/FFF.Native.slotpark-*.dll`、
`_3fp-fix/pr1/bat-*/summary.txt`、`_3fp-fix/review20261009/main/{battery-compare-slotpark.txt,harness-laneG-*.log}`。
（本表取代早先那两张"出货臂 `A7399C7E` vs 移植臂 `2A6463B2`"的读数；结论同向，数字以本表为准。）

| 判据 | 修复前对照 | 出货树 `B536621D` | PR 臂 `B92A0B93` | 性质 |
|---|---|---|---|---|
| `mw_curve` 计数探针 `[J2]` 两窗口区域=自身曲线全幅 | **FAIL 左 0.77126 / 右 0.79863**（exit 1） | **PASS 0.00000 / 0.00000**（exit 0） | **PASS 0.00000 / 0.00000**（exit 0） | **抓 D9 的那条牙** |
| 同探针其余 12 条（`[J4]`×2 / `[J1]`×4 / `[J2]` 近黑×1 / `[J3]`×5，另有 `[J5]` 不计数口径哨兵） | 全 PASS | 全 PASS | 全 PASS | 不变（该探针总计 13 条计数判据：修复前 12 PASS + 上面那 1 FAIL，修复后 13 PASS） |
| ⚠ lane F 现版 `mw_curve`（该断言被降级成不计数打印） | **12/12 PASS、exit 0 —— 在带缺陷的臂上** | 12/12 PASS | 12/12 PASS | **它的 PASS 不构成 D9 证据** |
| Vivid canon / PQ baseline / PQ `maxrgb=2048` | `fmt=5 path=6 dyn=1 outBits=16` + `peak=4000 / 10000 / 5001` | **同** | **同** | 逐字相同 |
| Vivid HLG 无静态 / +2048 / PQ 无静态 `maxrgb=0` | `peak=1000` ×3（结构字段同上） | **同** | **同** | 逐字相同 |
| HDR10+ `.hevc`（FATE 单帧件） | `fmt=2 path=2 dyn=1 peak=334 outBits=16 presented=1` | **同** | **同** | ⚠ 只作两臂同数对照：`docs/75` 第 4 条已判 334 **不构成跨树判据** |
| 真夹具 `real_hdr10plus_rolloff.mp4` | `fmt=2 path=2 dyn=1 outBits=16` | **同** | **同** | 结构逐字相同 |
| ↳ 同一行的 `peak` / `presented` | `328 / 68` | `328 / 53` | `341 / 64` | **随取样时刻走**：该夹具 `sourcePeakNits` 本就逐帧动，三臂互异且同臂复跑也异 ⇒ 不属行为判据 |
| SDR `real_4k_h264_60m.mp4` | `fmt=0 path=0 dyn=0 peak=100 outBits=8` | **同** | **同** | 逐字相同（`presented 67/22/31` 同上属时刻项） |
| 注入套件 真夹具 | `=== PASS (0 failures) ===` exit 0 | **同** | **同** | 不变 |
| 注入套件 单帧夹具（守卫**必须**判红） | `GUARD FAIL` **exit 8** | **同** | **同** | 判据仍有牙 |
| `scene` / `pause` / `eof` 装表 | `source=1 windows=1 targeted=333` / `111` / `111` | **同** | **同** | 不变（`frameIndex 258/257/259`、`eof peak 347/357/360` 属时刻项） |
| 暂停态像素（RE-03） | `4/5 moved on install, 5/5 back to baseline` | **同** | **同** | 不变 |

⇒ **15 条电池读数在"退出码 + 结构字段"层面三臂逐字相同**，D9 的行为改变只在 `built == 2` 那条判据上显形
（这批夹具的注入表全是 `windows=1`，停车分支对它们本就等价）——这正是"修了一处、其余没动"该有的形状。

**部署位守卫（两修复臂各跑一次）**：`check_kernel_exports.py` **exit 0**（`API version = 17`）；
`check_kernel_pairing.py` **exit 0**（PR 臂 **exports 92** / 出货臂 90 —— 差的 2 个是上游 master 自己多的图像模式导出，
本笔没有删任何东西；`kernel=17 / managed=17`、dest abi Version=2 相符）；`check_kernel_config_abi.py` **exit 0**
（镜像 == 头 15 字段、DLL 布局 80 字节、`judged-pass=6 / fail=0 / not-judged=0`）。
件：`_3fp-fix/review20261009/main/guard-*-{TP,PORT}-slotpark.txt`。守卫跑完部署位已还原成 `.3fc_kernel_build.json`
记录的那件并复算断言（`E5A7ADCB53B8DDB9 / 803,328 B / MATCHES record`）。


## 四、这一支还没覆盖什么（别当成已验）

1. **宿主层没跑**：3FCompare 的 `--selftest` 真机模式、`tools/发布门禁.ps1`、`构建全部.ps1` 都没跑过这支新二进制 —— 它不是出货 DLL
   （904,192 B vs 出货 `E5A7ADCB…` 的 803,328 B），要跑就得先部署到 `src/3FCompare/bin`，那属 3FC 侧、你叫搁置的范围内。
   ⇒ 上面所有绿灯都是**内核级**判据。
2. **VB 宿主项目在这台机器上编不过**（`FFF.Player` 缺 `LakeUI.*`，910 个 BC30002）：那是克隆里没有 LakeUI 依赖，
   与移植无关；`tools/构建3FP.ps1` 顺带编宿主所以整脚本 rc=1，但 `FFF.Native.vcxproj` 自己 `-> ...FFF.Native.dll` 成功、0 C++ error。
3. **`--3way` 的 `Falling back to direct application` 有 5 处**：其中 4 处是新增文件（无原像，正常），
   1 处是 `FFF.Native.vcxproj`（+5 行工程登记）。我核过它的最终内容：5 个新文件各登记一次、无重复条目。
4. 判据仍**不含**音频/字幕/呈现那几路 —— 上游这 21 笔里改了 IAMF、字幕、呈现泵；PR 不碰它们，但"整个产品在新 master 上表现如何"这个问题本件不回答。

## 五、推送（**已于 2026-10-09 执行**）+ 开 PR（那一下归你）

实际跑的那条（没改克隆的 remote 配置，直接给 URL，一次性）：

```bash
cd /c/PLAN/3FCompare/_3fp-fix/pr1/tp-clone
GIT_TERMINAL_PROMPT=0 git push https://github.com/luoye-cpu/FFF_Project.git \
  upstream/hdr10plus-on-master:refs/heads/upstream/hdr10plus-on-master
# ⇒ rc=0，`* [new branch] upstream/hdr10plus-on-master -> upstream/hdr10plus-on-master`
```

**推后两侧对账（都实跑过）**：`git ls-remote … refs/heads/upstream/hdr10plus-on-master` ⇒
`fbd98fc5eb991aaac0c7e42690350cadb9b770ce` **与本地 `git rev-parse` 逐字符相同**；
GitHub 自己那页 compare 渲染出 **1 commit + 18 changed files + 3,694 additions / 731 deletions**
⇒ 与本件 §一/§三 的本地读数一致，不是我只信自己。base 也核过：`ls-remote refs/heads/master` ⇒
`12d8afc` = 本笔的父 ⇒ compare 里只有这一笔差异。

推前自检（同轮实测值写在箭头右边）：`git rev-parse --short upstream/hdr10plus-on-master` ⇒ `fbd98fc`；
`git log -1 --format=%p upstream/hdr10plus-on-master` ⇒ `12d8afc`；
`git diff --name-only 12d8afc upstream/hdr10plus-on-master | wc -l` ⇒ `18`（15 内核 + `.rc` + 两个 VB 宿主件）；
`git show --name-only fbd98fc | grep -cE "third_party|PATCHES|\.3fc_"` ⇒ `0`（本地台账与 vendored 依赖都不在这笔里）；
`git status --porcelain | wc -l` ⇒ `0`。

**补丁路径也重验过**（D9 进提交、提交说明 amend 之后）：`git format-patch -1` ⇒
`_3fp-fix/review20261009/laneC/0001-final.patch`（232,701 B；更早那份 `0001-slotpark.patch` 231,904 B 是
纯说明 amend **之前**的），在 `12d8afc` 的干净 worktree 上 `git am --keep-cr` **exit 0**，落地树哈希
`2ebaa09233639802657f702ac0121c310a71b669` **与这笔提交的树相同** ⇒ 上游 `git am` 拿到的就是这个内容
（警告只有生成物数组的 trailing whitespace，1,860 行，属既有生成物版式；amend 只动说明，树哈希未变）。



**要稳一点就提 `1db27cf`**（base 用我们 fork 的 `3fcompare/api16-kernel`，需先推那条 base 分支；副作用是把 api16 线公开到 fork）。
它的额外保证只有一条：**与出货 DLL 逐字节同源**（244/244 全树 blob 等值 + 产物 `A7399C7E…` 七条守卫/判据已在
`_3fp-fix/pr1/` 留档）。两条的 PR 文本内容可共用 §六。

## 六、PR 文本（就是这个，直接粘）

**Title**（63 字符，GitHub 上限 70）：`Native: decode, tone-map and inject ST 2094-40 dynamic metadata`
（与提交说明的标题同源；提交说明本体见 `_3fp-fix/review20261009/main/pr-commit-message.txt`，
`git log -1 --format=%B` 与之逐字相同。）

**Body**：**`3fp/patches/hdr10plus-pr-body-2026-10-09.md`**（整份内容就是正文本身，打开→全选→复制→粘；
尺寸与哈希只在下面附录里记一次，免得两处数字各自过期）。本件不再抄一份，两份必然漂移。

写完前逐句回过量过：`0.29020/0/10`、`0.65396`、`0.77126↔0.00000`、`10000→4000 与 5001 不动`、
`134,752 → 141,872 且其余 6 个数组与上游 master 逐字节相同`（本轮用 `git show 12d8afc:` 重跑过），
`GetSnapshot rc=7`、`0.00000`（J5 口径哨兵）、`0.46945–0.48663`（同源钉参照会误杀合规内核）
全部指向盘上日志；**"adds no tests"** 那句也是量出来的：克隆里两个测试工程
`FFF.Player.Tests` / `FFF.Recorder.Tests` 各只有 `.vbproj` + `Program.vb` 两件、本笔一个都没碰，
且其中 `2094|dynamic_?metadata|hdr10\+|HdrDynamic` 命中 **0**。


## 附录、PR 正文就在这个文件里（不在本件内复制一份，两份必然漂移）

**`3fp/patches/hdr10plus-pr-body-2026-10-09.md`** —— 整份内容就是 PR 正文本身，打开 → 全选 → 复制 → 粘进 GitHub 的 description 框即可，没有围栏、没有中文旁注。
实测 `7411 B / 105 行 / LF / sha256[:16] 3033B909ED1AC730`（要核对粘出去的是不是这一份，就用这个哈希）。

标题（另有一格，63 字符）：`Native: decode, tone-map and inject ST 2094-40 dynamic metadata`

要省一次复制：**`powershell -NoProfile -Command "Get-Content -Raw -LiteralPath 'C:\PLAN\3FCompare\3fp\patches\hdr10plus-pr-body-2026-10-09.md' | Set-Clipboard"`**（会覆盖剪贴板现有内容，先自己确认）。

**分支已在 fork 上，点这一条就是新建 PR 页**（GitHub 对"单笔提交"会用提交说明自动填 Title 与描述）：

```
https://github.com/Lake1059/FFF_Project/compare/master...luoye-cpu:FFF_Project:upstream/hdr10plus-on-master?expand=1
```

**两个实测到的前提**：① 我这台机器上浏览器**没登 GitHub** ⇒ 匿名打开这条只是 compare 视图，页面里
`textbox` 命中 **0**（`take_snapshot` 实读数），填不了表单 —— 要我先代填就请先在那个浏览器里登录，
登录状态归你，我不动你的凭据；② 自动填进来的描述是**提交说明**（六条语义那版），
不是上面 105 行的完整正文（多的那几块是 Files、字节码审阅注、Compatibility、"本笔不加测试"、留给上游裁的口径偏差）。
想要完整版：先跑上面那条 `Set-Clipboard`，进页面 Ctrl+A→Ctrl+V 覆盖描述框即可。

base 已核：`git ls-remote https://github.com/Lake1059/FFF_Project.git refs/heads/master` ⇒ **`12d8afc`**，
与本笔的父提交同一个 commit ⇒ 这条 compare 只有我们这一笔的差异，不会掺进上游的新变动。



## 七、审查轮之后的新增（读这里，别读旧段猜测）

正式账本：**`3fp/docs/77-PR审查轮冲突裁决与四条真缺陷闭环（2026-10-09）.zh.md`**。要点：

- **提交内容变了 4 类**：① 注入不再改写样本的格式分类，对 SDR-transfer 源"装而不作用"（症状实测：SDR 源装表后
  10/10 采样点移动、最差 **0.29020**，`hdrProcessingPath 0→2`；修后 **0/10 移动、0→0**）。② 曲线 LUT 行中心
  `(index+0.5)/3.0` + `static_assert`（症状：三窗口表下中间区域被第三个窗口的曲线映射，与其自身曲线差
  **0.65396**；两窗口表第二区域 **5/5 采样点近黑** ⇒ 修后分别 **0.00000** 与 **0/5**）。③ pts 不再进入
  帧索引键空间；trc 捕获移到任何早退之前；`Reset()` 清 `injectedActive_`；门控结果不再被 `(void)` 丢弃。
  ④ 私有引用清零（`docs/21`、`3fp/docs/61`、私有夹具名、"3FCompare extension"、`:2741-2747`、`dynamicEnabled_`
  旧名），复查 `git diff 3940990 -- FFF.Native` 新增行 **0 处**；FFmpeg FATE 的引用**保留**（公开可查）。
- **为什么 `.rc` 与 `FFF.Player` 在这一笔里**：本 PR 抬 `PlayerApiVersion` 16→17 并给 `FFF3FPSnapshot` 加尾部 5 字段，
  而 `GetSnapshot` 对声明尺寸小于自身结构的调用方硬拒 ⇒ 上游仓库自带的 VB 播放器（两处硬编 16、快照镜像缺新尾）
  会 `FFF3FP_Create` 直接被拒、`GetSnapshot` rc=7（lane D 等价探针实测）。同笔改完才不会出现"合了内核没合外壳"。
  ⚠ VB 侧改动**未经编译验证**：克隆缺 `LakeUI` 依赖，`FFF.Player` 整项目在本机编不过（910 个 BC30002），
  只做了字段顺序/宽度与 C 头逐项对齐的静态核。**提 PR 前请在有 LakeUI 的机器上编一次 `FFF.Player`。**
- **判据表里 `peak=334` 那行的性质**：它出自 FATE 单帧夹具，`docs/75` 第 4 条已判其**不构成跨树判据**，
  本包只把它当"两臂同数"的对照；跨树判据用多帧真夹具。
- **J2 那一红最后裁成了两件事，一件修进本笔、一件登记不修**（收尾趟，取代本文件早先"J2 已随旧探针作废"的说法 ——
  那句是错的，错的机制见下）：
  ① **我先前那句"用当前版探针两臂 12/12 PASS"不构成证据**。lane F 改版时把"两窗口区域=自身曲线全幅映射"
  降级成不计数打印，于是**同一支 exe 在带缺陷的臂上同样 12/12 PASS、exit 0** —— 它对这条缺陷没有牙。
  牙在 lane G 的计数版探针里；A/B 用**同一支 exe、只换 DLL**：`758DC466`（修复前）**FAIL 左 0.77126 / 右 0.79863、exit 1**
  → `B536621D`（出货树）与 `B92A0B93`（PR 臂）**PASS 0.00000 / 0.00000、exit 0**。
  ② 根因是**真缺陷 D9**（不是题设的归一化口径）：`built == 2` 时空槽 2 落进 write 分支，装进的是
  `metadata.windows[2]` 的**结构体默认全幅矩形**；退化框在对称混合爬坡下**仍有 1 的权重**，而它的 LUT 行从没写过
  ⇒ 加权变成 `(c_own·1 + 0·1)/2`，**整幅在 PQ 码域被砍一半**（lane G 独立复刻反解出比值 0.4997–0.5005，模型预测恰 0.5000）。
  改法：**逐槽**停车（`built > 1` 写槽 1 否则停车，`built > 2` 写槽 2 否则停车）。`built == 1 / 3` 行为不变，
  HLSL 与 cbuffer 不动 ⇒ 不需重生成字节码。**行为面只有 `built == 2` 改变**，与"15 条电池三臂逐字相同"相容。
  ③ lane G 的第二个发现**另案登记、本笔不带**：多窗口归一化分母恒取 `windows[0].signalPeakNits`，
  `HdrToneCurve` 从不读各窗口自己的 `maxscl` ⇒ 除窗口 0 外无人消费（哨兵实测：只把窗口 1 自己的 maxScl 翻倍，
  右区域移动 **0.00000**）。为什么不顺手修：要给 b2 增每窗口峰值（布局 + HLSL 声明 + 两处填充 + 重生成字节码），
  而现存参照把峰值钉成窗口 0（同源钉），**内核改合规后它会对更正确的实现判红 0.46945–0.48663**；
  且 ST 2094-40 关于"曲线输入分母"的规范性逐字没拿到公开件 ⇒ **口径归属留给上游裁决**。
  已按上述写进 PR 正文的 **Not claimed**（含"请先在有 LakeUI 的机器上编一次 `FFF.Player`"）。
- **私有引用在收尾趟重扫**：base 换成 `12d8afc` 后逐行扫本笔新增行，
  `docs/NN`、`3fp/`、`3FCompare`、`_3fp-fix`/`_3fc_tmp`/`PLAN`、`PATCHES.md`/`.3fc_`、私有夹具文件名、`third_party`、lane 名
  ⇒ **0 处命中**；**FFmpeg FATE 引用保留 6 处**（公开可查，件：`main/rescan_private_refs.py`）。

