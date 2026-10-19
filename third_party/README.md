# third_party（第三方依赖说明）

本目录**不存放第三方 DLL/源码**（构建时才把外部件放进来，运行期件由脚本拉取）。git 集合实测 0 件二进制/归档（2026-10-20 把 `.3fc_kernel_baseline.bundle` 与 `tools/probe_baseline_cache.txt` 这两件"验证过程自己会改写"的归档移出索引之后）。磁盘侧仍有反例：`third_party/ffmpeg/lib/x64/` 实有 18 件（6×`.lib` + 6×`.def` + 6×`.exp`），由 `.gitignore` 排除在提交之外。逐件第三方许可与构建变体实测见仓库根的 `THIRD-PARTY-NOTICES.md`。

| 依赖 | 获取方式 | 放置位置（构建时） |
| --- | --- | --- |
| FFF.Native 源码 | `git clone https://github.com/Lake1059/FFF_Project.git third_party/fff_project`（其 `refs/heads/main` 就是 `tools/构建全部.ps1` 钉的基线提交 `578f80b…`；基线 tag 只在本机，需要时按 SHA 补）。**不是 git submodule**。2026-10-20 内核补丁已全部合并上游，旧文案"上游不含 3FCompare 的单 presenter 管线等私有改动、不能从上游克隆"已失效 | 产物 `FFF.Native\x64\<配置>\FFF.Native.dll` |
| Shared FFmpeg DLL | BtbN FFmpeg-Builds `...-win64-gpl-shared.zip` | `third_party/ffmpeg/` |
| libass 构建 | 上游 `tools/准备FFmpeg.ps1`（vcpkg） | `third_party/vcpkg_installed/` |

> 详见 [docs/06-风险与依赖.zh.md](../docs/06-风险与依赖.zh.md)。