# 3FCompare 构建脚本

本目录存放供本仓库使用的构建/内核维护/发布脚本（排除上游 FFF_Project 自带工具）。

## 构建 / 内核维护

| 脚本 | 用途 |
| --- | --- |
| `构建全部.ps1` | 构建 FFF.Native 内核并部署 DLL。**内核补丁的唯一入口**。参数：`-Configuration Release\|Debug`、`-SkipTests`、`-SkipPatches`、`-ForcePatches`、`-AllowKernelDrift` |
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

修复可复现性：

```bash
git -C third_party/fff_project push origin 3fcompare/zoom-viewport-cover
git -C third_party/fff_project push origin 3fcompare-kernel-2026.9.11.1
```

升级内核是**人工重移植**流程，见 `third_party/fff_project/PATCHES.md`。

## 调试/诊断工具

| 工程 / 脚本 | 用途 |
| --- | --- |
| `DxgiInteropProbe/` | DXGI 互操作诊断工具（验证 ComImport vs 裸 vtable 调用差异，**迁移文档 §M0 关键工具**） |
| `debug/` | 一次性 UI 自动化脚本（硬编码屏幕坐标与进程号，不可复用）。**已 gitignore，不入库** |

> 说明：本仓库不包含第三方二进制；FFmpeg DLL 取自 `third_party/fff_project/runtime/`（BtbN 构建），libass 由 vcpkg 准备。
> 单元测试与 E3 冒烟分别通过 `dotnet test tests/3FCompare.Core.Tests` 与 `dotnet run --project tests/3FCompare.SmokeTests` 执行。

> ⚠️ 本机 `dotnet restore` 全域失败（`Value cannot be null. (Parameter 'path1')`）。
> 根因是沙箱里 `APPDATA` 为空。所有 build/test 必须前置环境变量：
> `APPDATA="C:\Users\<用户>\AppData\Roaming" dotnet build ... --no-restore`
