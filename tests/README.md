# tests/ —— 测试统一目录

本目录收纳所有测试工程，与 `src/`（产品代码）分离：

| 工程 | 类型 | 用途 |
|---|---|---|
| `3FCompare.Core.Tests` | 单元测试（xunit） | FrameTimeline 帧/秒步进换算、SyncController 多会话同步、设置/会话 JSON 序列化 |
| `3FCompare.SmokeTests` | 控制台冒烟 | E3 可达性冒烟：演示引擎全流程（打开 → 步进 → 循环 → 一致性） |

## 运行

```powershell
# 全部单元测试
dotnet test tests/3FCompare.Core.Tests

# E3 冒烟（默认演示引擎，合成画面，无需真实 DLL）
dotnet run --project tests/3FCompare.SmokeTests -- [--demo] <视频路径...>
```

> 真实内核（FFF.Native）的验证请走 App 的 `--selftest`：需 WinForms 消息循环，
> 控制台冒烟不适用真实模式（见 `3FCompare.SmokeTests/Program.cs` 头部注释）。