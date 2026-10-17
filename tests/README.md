# tests/ —— 测试统一目录

本目录收纳所有测试工程，与 `src/`（产品代码）分离：

| 工程 | 类型 | 用途 |
|---|---|---|
| `3FCompare.Core.Tests` | 单元测试（xunit） | FrameTimeline 帧/秒步进换算、SyncController 多会话同步（含漂移校正）、GridLayout 网格布局、**GridPartition 合并态几何（权重→格表、拖动只改相邻两列、5% 下界、末边界逐位贴边、朝向候选含 1×2）**、ToneMapping 色调映射参数、设置/会话 JSON 序列化、**PixelReadback 片源↔后台缓冲坐标域换算**、**RenderStallWatchdog 呈现停滞判定**、**EngineDto 契约** |
| `3FCompare.Platform.Tests` | 平台层单元测试（xunit） | ViewPan 缩放/平移与光标锚点、MagnifierPlacement 放大镜定位、PlaybackCoordinator 配额、FrameCapture 抓帧路由、CrashGuard 崩溃守卫、LanguageManager 中英键覆盖、ProbePoint 哨兵 |
| `3FCompare.SmokeTests` | 控制台冒烟 | E3 可达性冒烟：**始终走演示引擎**（合成画面，无需真实 DLL）全流程（打开 → 步进 → 循环 → 一致性） |

> 用例数**不在本文档写死**：它会随每次提交漂移，写死必然过期（本文档曾长期写「175 例」，
> 实际早已翻倍）。跑完看 `dotnet test` 自己的汇总即可。

## 运行

```powershell
# 全部单元测试（两套，都要跑）
dotnet test tests/3FCompare.Core.Tests
dotnet test tests/3FCompare.Platform.Tests

# E3 冒烟（演示引擎，合成画面，无需真实 DLL）
dotnet run --project tests/3FCompare.SmokeTests -- [--demo] <视频路径...>
```

> `tests/output/` 是验收 harness 的**产出目录**（日志 / 夹具 / 源码树快照），
> 除三个台账文本外**整目录已被 `.gitignore` 忽略**，不要把产物往这里提。

> 真实内核（FFF.Native）的验证请走 App 的 `--selftest`：需 Avalonia UI 消息循环
> （2026-08-22 起主程序已由 WinForms 迁移到 **Avalonia 12**，WinForms 版归档于 tag `winforms-final`），
> 控制台冒烟不适用真实模式——检测到真实 DLL 也只会打印提示，仍然实例化 `SimulatedEngine`
> （见 `3FCompare.SmokeTests/Program.cs` 头部注释）。