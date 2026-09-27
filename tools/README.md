# 3FCompare 构建脚本

本目录存放供本仓库使用的构建/内核维护/发布脚本（排除上游 FFF_Project 自带工具）。

## 构建 / 内核维护

| 脚本 | 用途 |
| --- | --- |
| `构建全部.ps1` | 构建 FFF.Native 内核并部署 DLL。**内核补丁的唯一入口**。参数：`-Configuration Release\|Debug`、`-SkipTests`、`-SkipPatches`、`-ForcePatches`、`-AllowKernelDrift`、`-CheckOnly`（只校验内核基线，不 checkout / 不构建 / 不写文件） |
| `更新内核.ps1` | 内核升级**体检**：核对基线 SHA、查询上游差速与归档 tag 是否可复现、列出人工重移植流程。默认会 `git fetch`；`-CheckOnly` 只查不联网 |
| `发布门禁.ps1` | 发布前门禁：编译零告警 → 单元测试全绿 → 打包 → 产物自检（见下） |
| `patches/` | 3FCompare 自研扩展的**历史留档**。当前基线已内置这些扩展，构建时默认跳过；重放规则见该目录 README |

### 内核基线

`构建全部.ps1` 钉死内核完整 SHA（tag 只是可读别名，可被移动）。

> **唯一真源 = `构建全部.ps1` 里的 `$KernelBaselineTag` / `$KernelBaselineSha`。**
> `发布门禁.ps1` 与 `更新内核.ps1` 都从该脚本正则提取，**不要在别处再复制常量**。
> 复制的后果已经出现过一次风险：改基线时漏改一处，会变成「体检脚本说与基线一致、
> 构建却按另一份基线拦下」。改基线只改 `构建全部.ps1` 一处。

内核目录 `third_party/fff_project/` 被 `.gitignore` 整体忽略，且该归档分支与 tag
**目前只存在于本机**——远端 `Lake1059/FFF_Project` 没有 `3fcompare-kernel-*` tag。
新机器裸克隆无法复现基线，此时脚本会**报错中止**，绝不会静默退回上游默认分支
（旧版本正是这么做的，会拿没有 3FCompare 扩展的内核构建出"成功"的假象）。

修复可复现性（按优先级取用）：

1. **首选 —— 随仓库携带的基线归档**（无需网络、无需远端权限）：

   ```bash
   git clone .3fc_kernel_baseline.bundle third_party/fff_project
   ```

2. 云端归档分支（需网络，但**不需要上游写权限**）：

   ```bash
   git clone --branch kernel/3fcompare-zoom-viewport-cover \
     https://github.com/luoye-cpu/3FCompare.git third_party/fff_project
   ```

3. ⚠ **不要再尝试 push 到上游** `Lake1059/FFF_Project`：本机账号对它**只有读权限**
   （admin=false, push=false），推不上去。上面"该分支只存在于本机"的说法已随
   bundle 入仓失效。

**当前基线**：`3fcompare-kernel-2026.9.18.3` / `b765a1f8d76619da8583f7ac512621fbfb55dfa5`
（上一基线 `3fcompare-kernel-2026.9.18.2` / `0d5856ed…` 为回滚点）。
⚠ 本行只是便于阅读的副本：**以 `构建全部.ps1` 的 `$KernelBaselineTag` / `$KernelBaselineSha` 为准**
（该值随每次内核升级漂移，本行不保证同步）。

升级内核是**人工重移植**流程，见 `third_party/fff_project/PATCHES.md`。

## 调试/诊断工具

| 工程 / 脚本 | 用途 |
| --- | --- |
| `DxgiInteropProbe/` | DXGI 互操作诊断工具（验证 ComImport vs 裸 vtable 调用差异，**迁移文档 §M0 关键工具**） |
| `debug/` | 一次性 UI 自动化脚本（硬编码屏幕坐标与进程号，不可复用）。**已 gitignore，不入库** |

### Python 工具（纯标准库 —— 本机**没有 pillow / scipy**，勿引入第三方依赖）

| 脚本 | 用途 | 可否接门禁 |
| --- | --- | --- |
| `check_kernel_exports.py` | 解析 FFF.Native.dll 的 **PE 导出表**，核对 25 个必需导出 + 导出数下界（默认 82）+ API 版本（默认 15）。已接 `发布门禁.ps1` | ✅ 门禁判据（有明确 exit 0/1 语义） |
| `analyze_crashdump.py` | 直接解析 minidump（无需 WinDbg）：异常码 / 寄存器 / 近似调用栈 / 全线程 RIP 分布 / 模块清单 | ❌ 人读型取证脚本 |
| `screenshot.py` | ctypes 调 GDI 抓屏并手工编码 PNG（PowerShell `Add-Type` 被策略拦截时的替代路径） | ❌ 人读型取证脚本 |
| `probe_tooltip.py` | 悬停探针：临时把遮挡窗口压到 Z 序底部后抓 ToolTip Popup 取证 | ❌ 人读型取证脚本（且会**临时改动本机窗口 Z 序**） |
| `crash_rate.py` | 统计崩溃率（按转储时间窗聚合） | ❌ 人读型取证脚本 |
| `watch_hook.py` | 观察 inline hook / 模块代码改写（对比进程内存与磁盘原文件） | ❌ 人读型取证脚本 |
| `verify_guard_selftest.py` | CrashGuard 端到端自检（用真实 32 位退出码判定，需一份**已构建可运行**的 exe） | ⚠ 依赖已构建产物，非本机常备 |

> ⚠ **重要结论（2026-09-22 代码审查）**：上表标 ❌ 的六个都是**人读型取证脚本** ——
> 它们产出的是给人看的证据（截图、调用栈、RIP 分布、崩溃率），**不是可判定的断言**。
> **不可接进门禁当判据**：这类输出随环境（有无 IDE 遮挡、转储是否完整、DPI）漂移，
> 接进去只会制造假红/假绿。门禁只允许用 `check_kernel_exports.py` 这类
> **退出码即结论**的脚本。

> 说明：本仓库不包含第三方二进制；FFmpeg DLL 取自 `third_party/fff_project/runtime/`（BtbN 构建），libass 由 vcpkg 准备。
> 单元测试与 E3 冒烟分别通过 `dotnet test tests/3FCompare.Core.Tests` 与 `dotnet run --project tests/3FCompare.SmokeTests` 执行。

> ~~⚠️ 本机 `dotnet restore` 全域失败（`Value cannot be null. (Parameter 'path1')`）。
> 根因是沙箱里 `APPDATA` 为空。所有 build/test 必须前置环境变量：
> `APPDATA="C:\Users\<用户>\AppData\Roaming" dotnet build ... --no-restore`~~
>
> **2026-09-16 更新**：本机 `dotnet restore` **已能成功**（`APPDATA` 已有值），
> 上面这条约定**已失效**，不再需要 `--no-restore`。
> 仅当换环境后再次出现 `path1` 报错时才照此处理，且**不必重复穷举根因**
> （见 `HANDOFF-v0.2.5` §3.2）。
