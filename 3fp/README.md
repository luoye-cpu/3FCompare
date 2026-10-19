# 3fp —— 3FP「图片模式」独立工作区

> 上游项目 `Lake1059/FFF_Project`（3FP）的**图片查看功能**改动，全部在这里做。
> 与 3FCompare（3FC）的日常开发区域**完全隔离**。

---

## 一、为什么要有这个独立区域

3FP 的内核源码同时是 3FC 的构建依赖（`third_party/fff_project/`），而 `tools/构建全部.ps1`
会**钉死基线 SHA**（`$KernelBaselineSha`）并把内核实况记进 `.3fc_kernel_build.json`。
（⚠ 早期文档说的「拒绝 dirty、`-AllowKernelDrift` 才能绕过」**已不成立**：该参数不存在，
脏树**可以照常构建**——拦截发生在 `tools/发布门禁.ps1`，脏内核不进发布包。）
如果把图片模式的试验性改动直接写进 `third_party/fff_project/`：

- 3FC 的每次构建都会带上未定稿的内核改动，发布门禁（`.3fc_kernel_sha`）会失真
- 图片模式改动与 3FC 自身的 Present/同步改动混在一起，无法独立评估与回滚
- 无法判断"某个回归是 3FC 引起的还是图片模式引起的"

⇒ 因此本区域用**从基线 bundle 克隆的独立内核副本**承载改动，
`third_party/fff_project/` 保持不动，直到改动成熟再按 `third_party/fff_project/PATCHES.md` 重移植进 3FC 基线。

---

## 二、目录结构

```
3fp/
├── README.md              ← 本文件：区域说明与铁律（进入前必读）
├── STATUS.md              ← 交接状态：进度 / 下一步 / 阻塞（其他 agent 的第一个入口）
├── docs/                  ← 本区域全部文档（00~28，**缺 18**：该号未使用，不是遗失）
│   ├── 00-图片模式设计规格.zh.md   ← 模式隔离架构、API 草案、交互映射、状态机
│   ├── 01-可行性分析.zh.md         ← 能不能做、值不值得做（含实测数据）
│   ├── 02-实现规划.zh.md           ← 怎么做、按什么顺序做（分期与验收）
│   └── 03~28 …                     ← 按日期推进的验证/交接/PR 记录（不再在此逐个枚举，会过期）
├── kernel/                ← ★ 内核 + 宿主工作副本（嵌套 git 仓，**按任务切分支**）
│   ├── FFF.Native/        ← C++ 内核（C API + PlayerSession + 渲染/色彩）
│   └── FFF.Player/        ← VB.NET 宿主外壳
├── tools/                 ← 本区域专用探针/验证脚本（入库）
├── samples/
│   ├── README.md          ← 样张集清单与生成方法
│   ├── tools/             ← 样张生成脚本（入库）
│   └── media/             ← 样张文件本体（**不入库**，体积大）
├── patches/               ← 导出给上游 issue/PR 的补丁（入库）
└── build/                 ← 构建产物（**不入库**）
```

---

## 三、三条铁律

### 铁律 1 · 绝不改 `third_party/fff_project/`

那是 3FC 的构建基线。本区域的一切改动只落在 `3fp/kernel/`。
需要同步给 3FC 时，走 `third_party/fff_project/PATCHES.md` 的重移植流程，**不是复制文件**。

### 铁律 2 · 视频模式"只加不改"

新增**图片模式**，视频模式的行为必须**逐字节不变**。
所有图片逻辑必须由模式开关门控，且在开关关闭时**不可达**（不是"不生效"，是根本走不到）。
详见 `docs/00-图片模式设计规格.zh.md` §2。

> 唯一例外：内核里确属**既有缺陷**且对视频也有害的修正（K1/K2/K3/K4），
> 但它们也必须保持"视频路径行为不变"，且单独成提交，便于上游单独评估。

### 铁律 3 · 大文件不入库

样张（`samples/media/`）与构建产物（`build/`）已在 `.gitignore` 中排除。
`kernel/` 本身是嵌套 git 仓，同样不入库（与 `third_party/fff_project/` 同理）。

---

## 四、环境准备（新机器 / 新 agent 接手第一步）

```powershell
# 1) 内核工作副本（若 3fp/kernel 缺失）
cd c:/PLAN/3FCompare
git clone https://github.com/luoye-cpu/FFF_Project.git 3fp/kernel   # 上游与 fork 的 main 同为基线 578f80b；旧谱系归档 bundle 只在本机，不入库

# 2) 按**本次任务**切分支——不要假设还叫 feature/image-mode
#    分支随任务漂移（图片模式 / api16-scrgb / mks-subtitle …），开工前先确认：
git -C 3fp/kernel branch --show-current
git -C 3fp/kernel log --oneline -1
#    ⚠ 早期文档要求「必须输出 b765a1f」——那是 2026-09 的图片模式基线，
#    3FC 出货基线此后已推进到 b661365 → 3940990，本区域也不再固定在该 SHA。

# 3) Shared FFmpeg DLL：本区域复用主仓库的 runtime/（不复制）
#    内核通过延迟加载在运行时查找 avcodec-63.dll 等，见下方"编译与运行"

# 4) 两个**目录联接**（ junction ）：宿主与内核构建都要用主仓库的第三方依赖，
#    用联接而不是复制，省下几百 MB。clone 之后或清理过 third_party 都需要重建。
New-Item -ItemType Junction -Path 3fp\LakeUI              -Target third_party\LakeUI
New-Item -ItemType Junction -Path 3fp\kernel\third_party  -Target third_party\fff_project\third_party

# 5) LakeUI 需要的是**新版**（旧版缺 ExcellentTrackBar.ChapterMarker 等成员，宿主编译不过）
git -C third_party/LakeUI pull --ff-only origin master
dotnet build third_party/LakeUI/LakeUI/LakeUI.vbproj -c Debug   # HintPath 指向 Debug 产物

# 6) 确认 FFmpeg 能力面（图片格式覆盖取决于用户 DLL，必须实测不能猜）
powershell -ExecutionPolicy Bypass -File ..\..\tools\探测FFmpeg能力.ps1
```

> ⚠ 联接是 Windows 目录联接，且指向主仓库的 `third_party/`。
> 清理 `third_party/` 会让构建失效 —— 按上面第 4 步重建即可。
> `3fp/LakeUI/` 已在 `.gitignore` 中排除：不加那条，`git add -A` 会顺着联接
> 把第三方 UI 库塞进主仓库。

---

## 五、编译与运行

内核工程：`3fp/kernel/FFF.Native/FFF.Native.vcxproj`（MSVC，vcpkg manifest 依赖 libass / libbluray）。

```powershell
# 构建（需 VS2022 C++ 桌面负载）
msbuild 3fp/kernel/FFF.Native/FFF.Native.vcxproj -p:Configuration=Release -p:Platform=x64

# 运行时把 Shared FFmpeg 放到产物目录（内核延迟加载这些 DLL）
copy c:\PLAN\3FCompare\runtime\*.dll 3fp\build\
```

⚠ 构建需要 vcpkg 依赖；若 `third_party/vcpkg_installed/` 已就绪，可复用其配置。
**不要**用 `tools/构建全部.ps1` 来构建本区域 —— 那个脚本只服务于 `third_party/fff_project/` 与 3FC 主程序。

---

## 六、改动如何回流（两个方向）

| 目标 | 路径 |
|---|---|
| **上游 3FP** | `3fp/patches/` 导出补丁 → GitHub issue 探路 → PR（本机对上游**无写权限**，只能 issue/PR） |
| **3FC** | 按 `third_party/fff_project/PATCHES.md` 重移植 → 推进 `构建全部.ps1` 的 `$KernelBaselineSha` → 更新 bundle |

⚠ **顺序**：先上游/本区域定稿，再重移植进 3FC。
不要在 3FC 侧另起一份实现 —— 那会导致后续无法合并。

---

## 七、相关文档位置（本区域外）

| 内容 | 位置 |
|---|---|
| FFmpeg 能力探测脚本 | `tools/探测FFmpeg能力.ps1`（主仓库，本区域复用） |
| 3FC 内核基线与补丁归档 | `third_party/fff_project/PATCHES.md` |
| 3FC 构建/基线校验 | `tools/构建全部.ps1` |
| 上游 PR 历史（PR #9 的 ABI 教训） | `docs/upstream/PR-SUBMISSION.md` |
