# third_party（第三方依赖说明）

本目录**不存放任何第三方 DLL/源码**（MIT 许可也允许，但为清晰起见我们零二进制入仓）。

| 依赖 | 获取方式 | 放置位置（构建时） |
| --- | --- | --- |
| FFF.Native 源码（fork 子模块） | `git submodule add https://github.com/Lake1059/FFF_Project.git third_party/fff_project`（规划中，固定 commit） | 不入构建产物 |
| Shared FFmpeg DLL | BtbN FFmpeg-Builds `...-win64-gpl-shared.zip` | `third_party/ffmpeg/` |
| libass 构建 | 上游 `tools/准备FFmpeg.ps1`（vcpkg） | `third_party/vcpkg_installed/` |

> 详见 [docs/06-风险与依赖.md](../docs/06-风险与依赖.md)。