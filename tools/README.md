# 3FCompare 构建脚本

本目录存放供本仓库使用的构建/冒烟/调试脚本（排除上游 FFF_Project 自带工具）。

## 构建 / 内核维护

| 脚本 | 用途 |
| --- | --- |
| `构建全部.ps1` | 一键构建 FFF.Native 内核（Release x64）+ 部署 DLL + 可跳过测试（`-SkipTests`）。前置：Visual Studio 2022+（C++ 桌面负载）、Git |
| `更新内核.ps1` | 将 `third_party/fff_project` 子模块更新到上游最新 commit，自动重打 `patches/` 自定义补丁，重建并部署内核（`-CheckOnly` 仅检查更新） |
| `patches/` | 3FCompare 自研扩展补丁存放目录（VRR 交换链 / 视口子区域 / 全帧回读等），构建与更新子模块时自动重打 |

## 调试/诊断工具

| 工程 / 脚本 | 用途 |
| --- | --- |
| `DxgiInteropProbe/` | DXGI 互操作诊断工具（验证 ComImport vs 裸 vtable 调用差异，**迁移文档 §M0 关键工具**） |

> 说明：本仓库不包含第三方二进制；FFmpeg DLL 取自 `third_party/fff_project/runtime/`（BtbN 构建），libass 由 vcpkg 准备。
> 单元测试与 E3 冒烟分别通过 `dotnet test tests/3FCompare.Core.Tests` 与 `dotnet run --project tests/3FCompare.SmokeTests` 执行。