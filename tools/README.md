# 3FCompare 构建脚本

本目录存放供本仓库使用的构建/冒烟/调试脚本（排除上游 FFF_Project 自带工具）。

## 构建 / 内核维护

| 脚本 | 用途 |
| --- | --- |
| `构建全部.ps1` | 一键构建 FFF.Native 内核（Release x64）+ 部署 DLL + 可跳过测试（`-SkipTests`）。前置：Visual Studio 2022+（C++ 桌面负载）、Git |
| `更新内核.ps1` | 将 `third_party/fff_project` 子模块更新到上游最新 commit，自动重打 `patches/` 自定义补丁，重建并部署内核（`-CheckOnly` 仅检查更新） |
| `patches/` | 3FCompare 自研扩展补丁存放目录（VRR 交换链 / 视口子区域 / 全帧回读等），构建与更新子模块时自动重打 |

## 调试辅助脚本（LakeUI/窗口自动化）

| 脚本 | 用途 |
| --- | --- |
| `枚举顶层窗口.ps1` | 枚举某进程的所有顶层窗口 |
| `窗口操作.ps1` | 向指定窗口发送消息或键盘按键 |
| `激活并点击.ps1` | 激活目标窗口并点击指定坐标 |
| `点击坐标.ps1` | 点击指定屏幕坐标 |
| `扫描按钮.ps1` | 扫描窗口标题栏区域图标像素分布，定位各按钮位置 |
| `验证播放列表.ps1` | 3FP 播放列表左右分区验证（点击播放列表按钮 → 验证 DragSelectZoneWidth 生效） |
| `反射验证LakeUI.ps1` | 反射验证 `LakeUI.UltraDetailListView` 的 `DragSelectZoneWidth` 属性（从 FFF.Player 输出目录加载） |
| `LakeUIReflect/` | 反射验证小工具工程（.NET，随上述脚本使用） |

> 说明：本仓库不包含第三方二进制；FFmpeg DLL 取自 `third_party/fff_project/runtime/`（BtbN 构建），libass 由 vcpkg 准备。
> 单元测试与 E3 冒烟分别通过 `dotnet test tests/3FCompare.Core.Tests` 与 `dotnet run --project tests/3FCompare.SmokeTests` 执行。