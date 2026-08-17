# 3FCompare 构建脚本

本目录存放供本仓库使用的构建/冒烟脚本（排除上游 FFF_Project 自带工具）。

| 脚本 | 用途 |
| --- | --- |
| `准备FFmpeg.ps1`（规划中） | 固定 commit 拉取 Shared FFmpeg DLL 组 + libass（参考上游 `tools/准备FFmpeg.ps1`）贩运到 `third_party/` |
| `构建3FP内核.ps1`（规划中） | 构建 fork 的 `FFF.Native`（MSBuild x64），产物输出到 `artifacts/` |
| `E3冒烟.ps1`（规划中） | 调用 `3FCompare.SmokeTests` 跑 E3 可达性冒烟 |

> 说明：本仓库不包含第三方二进制；上述脚本执行前需先按文档获取 Shared FFmpeg/libass。