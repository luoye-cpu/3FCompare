# third_party（第三方依赖说明）

本目录**不存放第三方 DLL/源码**（构建时才把外部件放进来，运行期件由脚本拉取）。
⚠ 但"零二进制入仓"在本仓**不成立**，两处反例：① 本文件 `:7` 行让你 `git clone .3fc_kernel_baseline.bundle` —— 那只 bundle（2 371 867 B）是**被主仓 git 跟踪并随仓分发**的二进制包；② 磁盘上 `third_party/ffmpeg/lib/x64/` 实有 18 件（6×`.lib` + 6×`.def` + 6×`.exp`，虽被 `.gitignore` 排除在提交之外）。逐件第三方许可与构建变体实测见仓库根的 `THIRD-PARTY-NOTICES.md`。

| 依赖 | 获取方式 | 放置位置（构建时） |
| --- | --- | --- |
| FFF.Native 源码（3FCompare 归档分支） | ① 首选 `git clone .3fc_kernel_baseline.bundle third_party/fff_project`（随主仓库分发，无需网络）；② 备选云端归档分支 `kernel/3fcompare-zoom-viewport-cover`。**不是 git submodule**；也**不能**从上游 `Lake1059/FFF_Project` 直接克隆。⚠ **表述已收窄（2026-09-24）**：旧文案写"上游不含 3FCompare 扩展"**过宽**——PR #9 合入后 `ReadVideoPixelRegion` / `GetRenderTargetInfo` / `preferredAdapterIndex` **已在上游**；准确说法是「上游不含 3FCompare 的**单 presenter 管线**等后续私有改动」，从上游克隆会缺这些扩展、构建却"成功" | 产物 `FFF.Native\x64\<配置>\FFF.Native.dll` |
| Shared FFmpeg DLL | BtbN FFmpeg-Builds `...-win64-gpl-shared.zip` | `third_party/ffmpeg/` |
| libass 构建 | 上游 `tools/准备FFmpeg.ps1`（vcpkg） | `third_party/vcpkg_installed/` |

> 详见 [docs/06-风险与依赖.zh.md](../docs/06-风险与依赖.zh.md)。