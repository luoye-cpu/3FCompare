# 3FCompare Avalonia 迁移规划

> 状态：**M0 已完成（go）**｜创建：2026-08-22｜M0 验收：2026-08-22
> 前置阅读：`docs/02-系统架构.md`、本文档第三节「硬约束」

## 1. 迁移动机与目标

**动机**
- 消除 WinForms 多 HWND 架构的顽疾：DPI 切换白闪、拖动撕裂、滚轮焦点 hack（IMessageFilter）、IME Disable hack
- GPU 合成线程渲染：动画不占 UI 线程，缩略图/时间轴动画流畅度提升
- 数据绑定替代 ~100 个资源键的手动 `ApplyLanguage()` 刷新链
- 为未来 Linux/macOS 监控中心部署保留可能性
- MIT 授权干净（对比 LakeUI 的 GPL/付费双授权）

**非目标（明确不做）**
- 不迁移 Core 层（引擎/SyncController 与 UI 无关，保持不动）
- 不追求跨平台首发——Windows 先行，架构上不引入平台特定 API 到共享层
- 不改原生后端 FFF.Native 的接口（除非 HWND 承载验证失败，见 §4 风险 R1）

## 2. 现有资产盘点（迁移工作量基线）

| 资产 | 行数 | 迁移策略 |
|---|---|---|
| MainForm.cs | 1823 | **重写**为 MainWindow.axaml + ViewModel 拆分 |
| SettingsDialog.cs | 583 | **重写**为 axaml（布局系统换掉后原 TLP 代码全废） |
| Program.cs | 64 | 重写为 Avalonia AppBuilder 启动 |
| TransportBar / TimelineView / CompareGridView 等 9 控件 | ~1300 | **逐个重写**为 Avalonia Control + SkiaSharp 自绘或 axaml 组合 |
| WgcFrameCapture.cs (PrintWindow 抓帧) | 124 | **保留**（纯 Win32，与 UI 框架无关） |
| AppTheme.cs | 140 | 转换为 Avalonia ResourceDictionary 主题 |
| LanguageManager.cs | 378 | **保留**，外面包一层 IXamlLocalizableProvider 或转 .resx |
| LayoutConstants / Geometry / Dpi | ~210 | Dpi.cs **删除**（Avalonia 自带 DPI 处理），其余保留 |
| Core 层（Engine/Sync/Settings/Display） | — | **零改动** |

总计重写量 ≈ 4000 行 C# → 约 2500 行 C# + 1500 行 AXAML。

## 3. 硬约束（每条都是验收门槛）

1. **视频渲染承载**：FFF.Native 输出 D3D 到子 HWND。Avalonia 用 `NativeControlHost` 包裹该 HWND。
   验收：真实模式 selftest `状态=Playing` + screentest PNG 有画面。
2. **NativeAOT 发布**：`PublishAot=true` 必须继续可用。
   验收：`dotnet publish -r win-x64 -p:PublishAot=true` 成功且运行正常。
3. **包体积红线**：精简版 ≤ 15MB（当前 5.1MB；Skia native + Avalonia 预计 +10MB，超出即触发重新评估）。
4. **功能对等清单**：9 路网格、AB 循环、偏移对齐、探针、书签、放大镜、差异叠加、双语、主题、快捷键全套、--selftest/--screentest/--autodemo 自动化模式。
5. **测试基线**：Core.Tests 40 例持续通过（不受迁移影响）；SmokeTests --demo 通过。

## 4. 风险登记

| # | 风险 | 概率 | 缓解 |
|---|---|---|---|
| R1 | NativeControlHost 无法承载 D3D 子窗口（焦点/层级/DPI 组合问题） | 中 | **第 0 周先做 PoC**。失败则退路：内核加 SwapChain 共享纹理接口（需改 FFF.Native，工期+2周） |
| R2 | AOT 下 Avalonia 编译告警/运行崩溃 | 低 | 官方已全面启用 IsAotCompatible；PoC 一并验证 |
| R3 | ThumbnailPopup 无边框置顶窗在 Avalonia 的 TopLevel 行为差异 | 中 | 用 Popup/Window+ExtendClientAreaToTitleHint 方案 PoC |
| R4 | 双语系统与绑定集成复杂 | 低 | LanguageManager 保留，做 IValueConverter 桥接 |
| R5 | 打包脚本 pack.ps1 全链路适配 | 确定 | M5 里程碑专门处理 |

## 5. 里程碑分解

### M0 — PoC 验证周（1 周，决定 go/no-go）✅ 2026-08-22 全过（go）
- [x] 新建 `src/3FCompare.Avalonia` 试验项目（不动现有 App）
- [x] PoC-A：NativeControlHost 包裹 Win32 子窗口，内部用 GDI 画个色块 → 验证承载/焦点/DPI
- [x] PoC-B：把 FFF.Native 真实会话输出到该子窗口，selftest 跑通 Playing（Debug + AOT 双验证，退出码 0）
- [x] PoC-C：AOT publish 该试验项目并运行（`dotnet publish -r win-x64` 单文件 17.5MB，真实模式 Playing）
- [x] **决策点**：三项全过 → **继续 M1**
  - 过程发现并修复 Core 缺陷：`DxgiOutputInfo` ComImport 分发在 .NET 11 下调用 DXGI 返回
    INVALID_CALL，旧循环仅对 NOT_FOUND 跳出 → 无限循环（表现为打开媒体卡死）。已改为裸
    vtable 委托调用 + 迭代硬上限。WinForms 版亮度探测此前从未真正生效（静默 null），同修复受益。
    诊断工具：`tools/DxgiInteropProbe`。
  - ⚠ 体积预警：AOT 单文件 17.5MB（不含 ffmpeg/FFF.Native）已超 §3.3 的 15MB 红线 → 按约定
    触发重评估（M5 决策：裁剪 / 调整红线 / 接受）。

### M1 — 骨架与基础设施（1 周）✅ 2026-08-22
- [x] App 主题资源（AppTheme → ThemeResources 代码装配 + FluentTheme，深色优先）
- [x] LanguageManager 桥接（链接编译共享 + Loc 索引器绑定源 + {loc:Loc Key} 标记扩展，
      语言切换自动刷新；补齐 3 个缺失键：Msg_DemoModeMissingNative / Offset_ValueFmt / Diff_PercentFmt，
      WinForms 版同受益）
- [x] MainWindow 骨架：菜单栏（与 WinForms 清单逐项一致）+ 网格容器占位 + 底部传输栏/时间轴/状态栏布局 + 右侧栏占位
- [x] MVVM 基础：CommunityToolkit.Mvvm 8.4.0，MainViewModel 承接 MainForm 状态字段
- [x] 快捷键系统（OnKeyDown 复刻 ProcessCmdKey 全表：Space/Ctrl+S/←→/Shift+←→/↑↓/F11/Esc/O/B/P/F6/R/Delete/D1-D9）
- [x] 窗口几何记忆（位置/尺寸/最大化，恢复时钳制工作区）

### M2 — 核心播放面（2 周）✅ 2026-08-22
- [x] PlayerSurfaceHost：NativeControlHost 生产版——`CreateNativeControlCore` 托管定位/尺寸/DPI；
      子窗口 `WS_EX_TRANSPARENT` 使输入穿透回 Avalonia（解决 airspace 吞输入，滚轮/拖拽/点击直达）；
      覆盖层经子类化 WndProc GDI 画于 WM_PAINT（与 WinForms 相同的 D3D 窗口信息层模式）
- [x] CompareGridView：自定义 Control + Core.Display.GridLayout 布局复用、点击选中、单屏模式、
      2x1/2x2/3x3/Auto 预设、空态本地化提示
- [x] SyncController 接线：PlaybackCoordinator 完整移植（OpenFiles 9 路钳制 / WaitForOpenCompletionAsync
      15s 轮询 / TryAutoPlayAfterOpen 计数+回调队列 / EngineEvent Dispatcher 编组 / 失败路标记）
- [x] TransportBar（axaml 组合：双步进/循环/加减路/倍速/色彩模式/HH:MM:SS:FF 时间码）
- [x] TimelineView（DrawingContext 自绘：刻度/播放头/循环区间/ScrubPreview 节流/右键设 A/B/A/B 键）
- [x] 轮询快照（DispatcherTimer 16/250ms 自适应、TickLoop 回绕、真实模式伪倍速纠正 Seek）
- [x] 文件打开（FilePicker 多选）+ 拖放；滚轮缩放（1.15/格，1..32）+ 拖拽平移广播 SetViewTransform；
      快捷键全表接真实操作；全屏 HideChromeInFullscreen；状态栏全量信息
- 验证：--selftest 走真实管线全过（就绪等待/帧步进与秒步进位置不后退断言/媒体信息/自动播放），退出码 0

### M3 — 工具面板与对话框（2 周）✅ 2026-08-22
- [x] 侧栏 ToolsSidebar（5 页签 + 折叠按钮 + 放大镜常驻开关 + GridSplitter 拖宽）
- [x] ProbePanel（TryReadPixel 读数 + JSON 复制）/ BookmarkPanel（增删跳转 + JSON源生成/CSV 导出）/
      OffsetPanel（±帧/±100ms/对齐/归零）/ MediaInfoPanel（完整技术报告）/ AudioPanel（音轨/音量/静音）
- [x] AbSliderView（占位渐变 + 拖动分割线，WinForms 同语义）/ DiffOverlayView（96×N 网格像素采样热力图，
      TryReadPixel 直接采样替代 WinForms 的 DrawToBitmap 采样）/ MagnifierOverlay
      （IsHitTestVisible=false 替代 WS_EX_TRANSPARENT hack）
- [x] SettingsWindow（7 节：语言/硬件加速+GPU枚举/步进/窗口全屏/色彩/布局/FFmpeg路径+检测测试；
      差异检测构建新 AppSettings；FFmpeg 变更重启确认流）
- [x] MessageBox/PromptDialog 等价（自制轻量对话框）+ MaybeExitDemoMode 缺件引导
- [x] 会话保存/加载（.3fcs：路径/偏移/位置/循环区间恢复）
- [x] 选中联动（探针/媒体信息/音频/偏移面板随选中表面刷新）；探针悬停读点 + 放大镜跟随（隧道指针路由）
- 验证：构建零错误 + --selftest 全过（两轮）；面板交互细节留待 GUI 人工走查

### M4 — 浮层、抓帧与自动化（1 周）
- [ ] ThumbnailPopup（无边框置顶 Window；合成链路直接上 SkiaSharp——基准显示位图缩放快 8.4×）
- [ ] WgcFrameCapture 接线验证（PrintWindow 对 Avalonia 窗口的捕获行为需实测）
- [ ] --selftest / --screentest / --autodemo 命令行模式移植（退出码语义保持一致）
- [ ] 拖放打开文件（DragDrop API 差异适配）

### M5 — 打包发布与切换（1 周）
- [ ] pack.ps1 适配：Avalonia AOT 单文件产物路径、EmbedFffNative 资源嵌入方式复核
- [ ] 包体积测量 vs 15MB 红线
- [ ] 全功能回归清单走查（§3.4 功能对等清单逐项打勾）
- [ ] 精简版/完整版双形态发布验证
- [ ] 删除 WinForms 版 `src/3FCompare.App`（或先归档一个 tag 后删除）
- [ ] slnx/csproj/README/docs 更新

**总工期估算：8 周**（单人全职；含每周缓冲。M0 失败则总决策提前一周止损）

## 6. 迁移期间的双轨策略

- `src/3FCompare.App`（WinForms）**保持可发布状态直到 M5**——迁移期间发现的 bug 仍修在旧版
- `src/3FCompare.Avalonia` 新项目并行开发，共用 Core 层无冲突
- 每个 M 里程碑结束跑一次双版本 selftest 对比
- 分支策略：`feature/avalonia-migration` 长分支，按 M 合入 main（保持大粒度提交纪律）

## 7. 明确的技术映射表

| WinForms 现状 | Avalonia 目标 |
|---|---|
| Form / IMessageFilter 滚轮 hack | 控件自带 PointerWheelChanged（无需全局过滤器）|
| ImeMode.Disable hack | 实测验证；预计 TextInput 事件模型下问题消失 |
| PerMonitorV2 + AutoScaleMode.Dpi + Dpi.cs | 框架内置 DPI 缩放，Dpi.cs 删除 |
| WinForms Timer (16/250ms 自适应) | DispatcherTimer 同样逻辑 |
| OnPaint + OptimizedDoubleBuffer | SkiaSharp 自绘控件（IRenderTargetBitmap）或 axaml Shape 组合 |
| TLP/FlowLayoutPanel 布局 | Grid/StackPanel/DockPanel（SettingsDialog 塌缩问题不存在）|
| Application.AddMessageFilter | 无对应需求（事件直达）|
| Control.MousePosition 全局取鼠标 | Windows 专用代码进 platform 目录或用 Pointer 事件参数 |
| ShowDialog 模态 | Window.ShowDialog（异步 ShowDialog<T>）|

## 8. 决策记录

- 2026-08-22: 规划创建。基于 renderbench 基准结论（矢量绘制收益小、位图合成收益大），
  自绘控件统一选型 SkiaSharp 而非 axaml Shape 混搭，保持绘制代码风格一致。
- 2026-08-22: M0 验收通过（go）。三项 PoC 全过；selftest 补充分步日志 + 25s 看门狗（退出码 3 = 卡死诊断）。
  关键发现：.NET 11（11.0.100-preview.6）内置 COM 互送对 DXGI ComImport 接口分发损坏
  （对齐 vtable 后仍返回 INVALID_CALL，裸函数指针调用正常）——Core 的 DXGI 亮度探测因此
  重写为裸 vtable 委托方案（`DxgiOutputInfo`）。此为 M0 期间唯一 Core 改动，属缺陷修复。
- 2026-08-22: 体积实测——Avalonia AOT 单文件 17.5MB > 15MB 红线，重评估挂起至 M5。
- 2026-08-22: M1 完成期间发现本机 .NET 11 预览运行时（11.0.100-preview.6）两个工程级坑：
  ① App.axaml 内嵌 `<Application.Resources>/<Application.Styles>` 会使编译 XAML 按类型查找失败
    （「No precompiled XAML found for App」）——规避：App.axaml 保持空，主题经 ThemeResources 代码装配；
    MainWindow.axaml/控件 axaml 内容不受影响。
  ② MSBuild 增量构建在源码编辑后误判 up-to-date（产物时间戳不更新、跑旧二进制造成误诊）——
    规避：一律 `dotnet build --no-incremental`。
