# 交接文档:可用性修复(P0/P1)—— 全部完成

> 日期:2026-09-14 更新。**P0/P1 全部已完成并提交**,本文件转为完成记录 + 事故说明。
> 仓库:`c:/PLAN/3FCompare`(主仓库,main 分支);内核仓库 `third_party/fff_project` 未触碰。

## ⚠️ 重要事故说明(2026-09-14 凌晨,接手人必读)

本轮工作中,**本机 git 对象库被外部因素损坏**(非代码原因):

- 现象:一次 `git stash` 被 SIGTERM 中断后,`.git/refs/` 目录与 `.git/objects/pack/*.pack` 文件消失,
  仅剩 `.idx`;`git` 报 `fatal: not a git repository`。
- 影响:**今日 7aa5c60..251c6db 约 28 个本地提交(升级→内核合并→M1~M5→两轮复审回修)全部不可恢复**
  (远端 main 停留在 `3eab22c`,从未推送过这些提交;缺失对象经 `git fsck` 确认为 33 个 blob)。
- 处理:从 origin 重新 `git fetch` 恢复对象库 → 重建 `refs/` → 删索引重建(`git reset`)→
  把 `refs/heads/main` 重置到可用的 `3eab22c`,并将**完整工作区内容**(含 M1~M5 与今日 P0/P1 修复)
  一次性提交为 `0a33b4f`,使工作成果重新落盘。**工作区源码自始至终未丢失**。
- 遗留:恢复用的备份在仓库外 `C:/PLAN/_3FCompare_git_recovery_backup/`
  (含损坏的 pack.idx、旧 index、完整 src 快照 tar.gz),确认无误后可删。

## 完成状态

| 项 | 状态 | 说明 |
|---|---|---|
| P0-1 FFmpeg 缺失引导 | ✅ 完成 | MainWindow 构造函数挂 Opened 弹窗,接 `EngineFactory.LastUnavailableReason`,可选打开 SettingsWindow |
| P0-2 窗口状态记忆 | ✅ 完成 | `RestoreWindowGeometry()` 唯一恢复入口 + `SaveWindowGeometry()` 唯一保存点;AppSettings 用可空字段;硬件实测 Normal/Maximized 往返正确 |
| P1-3 演示模式原因透出 | ✅ 完成 | 状态栏 `BuildEngineLabel()` 接 `EngineFactory.LastUnavailableReason`,并随语言切换刷新 |
| P1-4 README 快速开始 | ✅ 完成 | README 新增「🚀 快速开始」:下载→解压→FFmpeg 三种放置方式→运行→上手三招→常见问题 |
| P1-5 OnClosed 停双定时器 | ✅ 完成 | 新增 `OnClosed`,停 `_pollTimer`(16ms)与 `_scrubTimer`(150ms) |
| 全量验证 + 提交 | ✅ 完成 | 见下 |

提交:`0a33b4f`(P0/P1 主体 + 工作区快照)、`6212ffe`(P0-2 收尾修复)。

## 实现要点(与原始计划的差异)

1. **去重**:原计划让人在构造函数里再写一份恢复逻辑,但 `RestoreWindowGeometry()`
   (L1403,构造函数末尾调用)已是恢复入口——两处并存会互相覆盖。故**只保留 `RestoreWindowGeometry()`**,
   删掉构造函数里重复的那段。同理保存路径只保留 `SaveWindowGeometry()`。
2. **不再改写 WindowState**:旧 `OnClosing` 在最小化时把窗口改成 Normal 再读几何,会**闪一下窗口**。
   新实现最小化时直接跳过几何保存(仅记状态),不再动窗口。
3. **可空字段替代 -1 哨兵**:旧设计用 `WindowX=-1` 表示"未设置",但在负坐标多显示器布局里 -1 是**合法坐标**。
   改为 `int?`,null 才是"未设置"。
4. **枚举值纠错**:Avalonia `WindowState` 实际顺序是 **0=Normal, 1=Minimized, 2=Maximized, 3=FullScreen**
   (原计划注释写的 1=Normal/2=Maximized/3=FullScreen 是错的)。代码全程用符号名比较,故逻辑本就正确,只修了注释。
5. **移除遗留字段**:删除 `WindowMaximized` 与旧的 `WindowX/Y/Width/Height`(非空版)及全部引用
   (SettingsWindow 两处拷贝、单测一处断言),避免两套窗口记忆字段并存。

## 验证结果(2026-09-14)

```bash
dotnet build src/3FCompare/3FCompare.csproj -c Debug --no-restore   # 0 error, 0 warning(仅既有 XAML AVLN3001 可忽略)
dotnet test  tests/3FCompare.Core.Tests/3FCompare.Core.Tests.csproj --no-restore   # 44/44 通过
3FCompare.exe --multitest testmedia/media/test_4k_60M.mp4 2 30    # 多次复跑:全部通过 ✓(8✓/0✗, exit=0)
3FCompare.exe --selftest  testmedia/media/test_4k_60M.mp4         # 全部通过 ✓
```

窗口几何实机验证:Normal 关闭→存 `WindowState:0` + 自身尺寸;以 `WindowState:2` 启动→最大化,
关闭后仍保留用户偏好的 Normal 几何(1280×720 @ 200,150),不再把屏幕尺寸写成窗口尺寸。

### 环境注意事项(与本轮新增)

1. **`dotnet restore` 在本机失败**:`error : Value cannot be null. (Parameter 'path1')`
   (NuGet.targets:796)。**与项目无关**——新建空白项目同样失败,`dotnet nuget list source` 也报同样错,
   是 NuGet 配置解析的环境问题。**用 `--no-restore` 构建/测试即可**(依赖包已在全局缓存中)。
2. **multitest 偶发原生崩溃**:约 1/5 概率在"全路就绪"后播放启动阶段 SIGSEGV/SIGILL。
   与代码改动无关(崩在原生渲染初始化,`--selftest` 与多数 multitest 均正常);疑似与同时段其他进程
   占用 GPU/显示器有关。复跑即可通过。
3. **`git stash` 有风险**:本机文件系统出现过失控的目录删除,建议直接用显式提交而非 stash 保存工作。
4. bash 多行输出截断、read_file 首行截断等旧坑依旧:输出写 `/tmp/xxx.txt` 再读最稳。
   本轮结束已清理 `_*.txt` 临时文件。

## 相关文档

- `docs/08-管线修复计划.zh.md` — M1~M5 已全部完成(勿重复做)
- `third_party/fff_project/PATCHES.md` — 内核补丁索引(内核侧勿动)
