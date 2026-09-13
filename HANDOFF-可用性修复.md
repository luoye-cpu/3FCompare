# 交接文档:可用性修复(P0/P1)进行中

> 日期:2026-09-13。供接手 agent 继续"面向主流产品可用性"的 P0/P1 修复。
> 仓库:`c:/PLAN/3FCompare`(主仓库,main 分支,工作区有未提交改动);内核仓库 `third_party/fff_project` 已完成并归档,**无需触碰**。

## 背景与总体进度

可用性审查发现的差距清单及当前状态:

| 项 | 状态 | 说明 |
|---|---|---|
| P0-1 FFmpeg 缺失引导 | ✅ 已完成(未提交) | MainWindow 构造函数中挂 Opened 弹对话框,接 `EngineFactory.LastUnavailableReason`,可选打开 SettingsWindow |
| P0-2 窗口状态记忆 | 🔶 **进行到一半** | AppSettings 字段已加;MainWindow 的保存/恢复**未做** |
| P1-3 演示模式原因透出 | ⬜ 未开始 | 状态栏接 `EngineFactory.CurrentModeName` |
| P1-4 README 快速开始 | ⬜ 未开始 | |
| P1-5 OnClosed 停双定时器 | ⬜ 未开始 | 150ms 状态轮询 + 16ms UI 动画定时器 |
| 全量验证 + 提交 | ⬜ 未开始 | 构建 + 44 单测 + multitest 冒烟 + 提交 |

## P0-2 剩余工作(接手后先做这个)

**已完成**:`src/3FCompare.Core/Settings/AppSettings.cs` 类开头已新增 5 个可空字段:
`WindowX / WindowY / WindowWidth / WindowHeight / WindowState(int?, Avalonia WindowState 枚举值)`。

**待做**(`src/3FCompare/MainWindow.axaml.cs`):
1. **恢复**(构造函数,`InitializeComponent()` 之后):若 `WindowX/Y/Width/Height` 均非空且 Width/Height > 0,则 `Position = new PixelPoint(x, y)`、`Width/Height` 赋值;`WindowState` 非空且为 Minimized 以外的值(1=Normal/2=Maximized/3=FullScreen)则应用。注意多显示器坐标越界时跳过恢复。
2. **保存**(L1630 附近的 `protected override void OnClosing(WindowClosingEventArgs e)`,在现有 `_coordinator.Close()` 逻辑附近):
   - 仅在 `WindowState != Minimized` 时保存 Position/Width/Height(最小化时坐标无意义);
   - `WindowState` 原样存枚举 int 值(Maximized 时仍存 Position,恢复时先 Normal 设尺寸再 Maximized);
   - 保存走现有 `SettingsStore` 持久化路径(查 `SettingsStore` 的 Save 方法签名后调用;若 _settings 由外部共享,注意与 L1227 `CopySettings` 的更新顺序)。
3. Avalonia API 注意:`Window.Position` 是 `PixelPoint`;取 `WindowState` 枚举 `Avalonia.Controls.WindowState`。

## P1-3 要点

`EngineFactory.CurrentModeName`(含降级原因,已实现)接到状态栏。现有锚点:
- `UpdateStatus()`(MainWindow.axaml.cs L622 附近,方法开头已有 `LastOpenError` 消费逻辑,插在其后);
- 状态栏控件:`StatusEngine.Text = ...`(L77 附近)。
- 注意:`LastOpenError` 消费分支 `return` 在前,演示模式文本应作为常规状态的一部分(可拼在引擎名后)。

## P1-4 / P1-5 要点

- README.md:补"快速开始"章节(下载→解压→ffmpeg-full 与 exe 同目录→打开文件),中文为主可附英文短段;现有 README 是中文的。
- OnClosed(MainWindow 当前只有 OnClosing L1630,OnClosed 需新增 override):停两个 DispatcherTimer(查找 `_pollTimer` 与 16ms 动画定时器字段名后 `.Stop()`),OnClosing 已调 `_coordinator.Close()` 不动。

## 验证与提交要求

```bash
dotnet build src/3FCompare/3FCompare.csproj -c Debug --nologo   # 期望 0 error(1 个既有 XAML 警告可忽略)
dotnet test tests/3FCompare.Core.Tests/3FCompare.Core.Tests.csproj --nologo   # 期望 44/44
src/3FCompare/bin/Debug/net11.0-windows/3FCompare.exe --multitest testmedia/media/test_4k_60M.mp4 2 30   # 期望 exit=0, 8✓/0✗
```

提交信息风格:Conventional Commit 英文,结尾加 trailer:
`Co-Authored-By: AtomCode (glm5.3-flash) <noreply@atomgit.com>`

## 环境注意事项(重要,前人踩过的坑)

1. **bash 多行输出会被截断**:工具结果只显示第一行。对策:输出写进 `_xx.txt` 再用 read_file 读;或用 `tr '\n' '~'` 单行化;或用 grep 工具(最稳定)。**结束后清理 `_*.txt` 临时文件**(git status 检查)。
2. `read_file` 对小文件首行也可能只显示首行,需带 offset 再读或用 grep。
3. Python 不可用;PowerShell 调用注意 `$` 被 bash 吞掉,需转义。
4. multitest harness 入口:`--multitest <媒体> <路数> <秒数>`;短素材会在 EOF 处假失败(presented 冻结在 总帧数),务必用 testmedia/media/test_4k_60M.mp4。
5. 后台进程(bash `&`、Start-Process)会随命令会话结束被杀,长任务用 schtasks 方式(见 git log 内核仓库历史);本轮任务不需要。
6. 当前工作区未提交改动 = AppSettings.cs + MainWindow.axaml.cs(P0-1/P0-2 半成品),接手后在其上继续,最后**一笔提交**涵盖全部 P0/P1(或按 P0/P1 分两笔,均可)。

## 相关文档

- `docs/08-管线修复计划.zh.md` — M1~M5 已全部完成(勿重复做)
- `third_party/fff_project/PATCHES.md` — 内核补丁索引(内核侧勿动)
- 今日提交链:`7aa5c60`..`251c6db`(升级→内核合并→M1~M5→两轮复审回修),全部已验证
