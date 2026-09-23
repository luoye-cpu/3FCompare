# 3fp —— 3FP「图片模式」独立工作区

> 上游项目 `Lake1059/FFF_Project`（3FP）的**图片查看功能**改动，全部在这里做。
> 与 3FCompare（3FC）的日常开发区域**完全隔离**。

---

## 一、为什么要有这个独立区域

3FP 的内核源码同时是 3FC 的构建依赖（`third_party/fff_project/`），而 `tools/构建全部.ps1`
会**钉死基线 SHA 并拒绝 dirty 工作区**（`-AllowKernelDrift` 才能绕过）。
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
├── docs/
│   ├── 00-图片模式设计规格.zh.md   ← 模式隔离架构、API 草案、交互映射、状态机
│   ├── 01-可行性分析.zh.md         ← 能不能做、值不值得做（含实测数据）
│   └── 02-实现规划.zh.md           ← 怎么做、按什么顺序做（分期与验收）
├── kernel/                ← ★ 内核 + 宿主工作副本（git 仓，分支 feature/image-mode）
│   ├── FFF.Native/        ← C++ 内核（C API + PlayerSession + 渲染/色彩）
│   └── FFF.Player/        ← VB.NET 宿主外壳
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
# 1) 内核工作副本（若 3fp/kernel 缺失；2 MB，秒级完成）
cd c:/PLAN/3FCompare
git clone .3fc_kernel_baseline.bundle 3fp/kernel
git -C 3fp/kernel checkout -b feature/image-mode

# 2) 确认起点正确（必须输出 b765a1f）
git -C 3fp/kernel log --oneline -1

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
