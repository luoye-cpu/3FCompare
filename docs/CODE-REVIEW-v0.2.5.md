# 3FCompare v0.2.5 全面代码审查报告

- **审查日期**：2026-09-16
- **审查范围**：`src/3FCompare.Core/`（9 个目录）+ `src/3FCompare/`（全部），共 57 个 `.cs` 文件、12,191 行
- **审查维度**：运行时错误与异常处理 / 安全 / 性能 / 逻辑一致性 / 边界条件
- **严重程度定义**
  - **严重**：导致进程崩溃、数据损坏或构成可被利用的安全漏洞
  - **中等**：功能异常、资源泄漏、明确性能瓶颈、静默失败
  - **轻微**：健壮性加固、代码质量、可维护性
- **复核说明**：报告中标注 `[已复核]` 的条目由主审独立重读源码确认；与子审查组结论冲突的已在正文订正。

---

## 一、结论摘要

**结论：当前状态未达到发布标准（No-Go）。建议完成 P0 清单后重新评审。**

| 维度 | 严重 | 中等 | 轻微 | 评估 |
|---|---|---|---|---|
| Core / Backend（原生互操作） | 2 | 4 | 4 | ⚠ 不达标 |
| Core / Settings · Diagnostics · Imaging | 0 | 7 | 10 | ⚠ 需加固 |
| Core / Sync · Display | 0 | 6 | 5 | ⚠ 需加固 |
| UI / MainWindow · Program | 2 | 4 | 6 | ⚠ 不达标 |
| UI / Controls · Panels | 0 | 6 | 6 | ⚠ 需加固 |
| Services · Localization · Platform | 0 | 4 | 7 | ⚠ 需加固 |
| **合计** | **4** | **31** | **38** | **73 项** |

**发布阻断项（P0，5 项）**：

1. 原生调用与 `Dispose` 存在真实 use-after-free 窗口（崩溃）
2. 7 处 `async void` 无异常边界 + 全局兜底不置 `Handled`（进程闪退）
3. FFmpeg DLL 加载链无完整性校验（DLL 劫持 → 任意代码执行）
4. 书签面板双击跳转**完全失效**（`SelectedItem` 类型判据不匹配）
5. `IDXGIOutput6::GetDesc1` vtable 槽位错一格（HDR 亮度探测静默全失效）

**必须先说明的既有事实**：多画面（`--multitest`）约 50% 概率在 `dxgi.dll` 崩溃，已实证归因上游内核锁竞争并提 issue #7，**不属本次审查范围**，但本身即为发布阻断项。

---

## 二、Core / Backend — 原生互操作层

### [严重] 绝大多数 P/Invoke 未与 `Dispose` 互斥，存在 use-after-free 窗口 `[已复核]`
- **位置**：`Fff3FpEngine.cs:152/167/173/179/196/202/208/214/220/230/268/277/284/292/345/555/577/596/603`
- **现象**：全文件共 21 处 `ThrowIfDisposed()`，但只有 `Seek`（`:187`）和 `ReadSnapshot`（`:298`）进入 `_nativeGate`。其余方法校验完标志位后**直接裸调原生**：
  ```csharp
  public void Play() { ThrowIfDisposed(); Check(Fff3FpNative.FFF3FP_Play(_handle), nameof(Play)); }
  ```
- **风险**：`Dispose`（`:626`）先原子置 `_disposedFlag=1`、再 `lock(_nativeGate)` 后 `FFF3FP_Destroy`。换片/移除画面/关窗时，UI 线程执行 Dispose，同时 16ms 轮询线程调 `ReadSnapshot`、漂移校正线程调 `Seek`、用户点播放/拖音量 → 可拿到**已销毁**的句柄进原生 → 野指针访问，`0xC0000005` 崩溃，托管侧不可 catch。文件 `:90-93` 的注释自己描述了这一风险，但只落实了两处。
- **建议**：统一收敛到受保护包装，所有原生调用必须经由它：
  ```csharp
  private T WithNative<T>(Func<nint, T> call)
  {
      lock (_nativeGate)
      {
          ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
          if (_handle == 0) throw new ObjectDisposedException(nameof(Fff3FpSession));
          return call(_handle);
      }
  }
  // Play() => Check(WithNative(h => Fff3FpNative.FFF3FP_Play(h)), nameof(Play));
  ```

### [严重] FFmpeg 目录来自 PATH 遍历，无任何完整性校验 → DLL 劫持
- **位置**：`NativeRuntime.cs:139-143`（`AutoDetectFfmpegDirectory`）、`:68-81`（`SetFfmpegDirectory`）、调用点 `MainWindow.axaml.cs:105-107`（启动即执行）
- **现象**：PATH 中**第一个**含 `avcodec-*.dll` 的目录即被采纳并传给 `SetDllDirectoryW`。`IsAcceptableFfmpegDirectory`（`:84-121`）只校验绝对路径 / 非 UNC / 目录存在 / 含 avcodec，**无签名校验、无可写性检查、无 reparse point 检查**。
- **风险**：PATH 中任一用户可写目录（开发工具链、用户级 bin）投放伪造 `avcodec-*.dll` → 内核 Delay-Load 加载 → **任意代码执行**。便携部署下应用目录可写，风险进一步放大。另：裸 `SetDllDirectoryW` 会关闭 Windows 安全 DLL 搜索模式。
- **建议**：
  1. `WinVerifyTrust` 校验 `avcodec`/`avformat`/`avutil` 的 Authenticode 签名；
  2. 拒绝当前用户可写的目录（或强提示）；
  3. 用 `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)` + `AddDllDirectory` 替代裸 `SetDllDirectory`；
  4. `Path.GetFullPath` 后检查 `ReparsePoint` 属性挡 junction。

### [中等] 无终结器兜底，漏 Dispose 即永久泄漏
- **位置**：`Fff3FpEngine.cs:82-119`、`:623-644`
- **现象**：`Fff3FpSession` 只有强 `GCHandle`（`:118`）+ 手动 Dispose，无 `SafeHandle` / 终结器（`:61-64` 注释已承认）。
- **风险**：上游任一漏 Dispose 分支 → 原生句柄 + D3D 设备 + 强 GCHandle 三者同时泄漏，多画面反复换片会累积。
- **建议**：把 `_handle` 包装为 `SafeHandle` 子类（`ReleaseHandle` 调 `FFF3FP_Destroy`）；**强 GCHandle 保持不变**（保持强引用是既有正确决策）。

### [中等] `SetDllDirectoryW` 返回值与 `GetLastWin32Error` 从未检查
- **位置**：`NativeRuntime.cs:11`、`:80`、`:246`
- **风险**：调用失败时静默继续，后续内核 Delay-Load 解析不到 avcodec → 打开媒体时原生崩溃（`:190` 注释自述的 `0xC0005FFE`），且日志无任何痕迹，排障成本极高。

### [中等] `IsNativeAvailableWithDirectory` 因缓存永远无效
- **位置**：`NativeRuntime.cs:264-276` + `EngineFactory.cs:19-27`
- **现象**：`EngineFactory.IsNativeAvailable()` 命中 `_nativeAvailable.HasValue` 直接返回旧值，而该字段全仓库无重置点 ⇒ 设置目录后拿到的仍是**首次**探测结果。
- **建议**：`EngineFactory` 增加 `Reset()` 或非缓存 `ProbeNow()`。

### [中等] 热路径每帧一次字符串插值 + 每次 `Marshal.SizeOf`
- **位置**：`Fff3FpEngine.cs:293`、`:301`、`:558`、`:606`
  ```csharp
  var snapshotSize = Marshal.SizeOf<Fff3FpSnapshot>();
  Check(Fff3FpNative.FFF3FP_GetSnapshot(_handle, ref snap), $"GetSnapshot(size={snapshotSize})");
  ```
- **风险**：插值字符串在**调用前**就构造，成功路径也分配；60Hz × N 路 = 每秒数百次分配。
- **建议**：`private static readonly int SnapshotSize = Marshal.SizeOf<Fff3FpSnapshot>();`；`Check` 改为仅在失败时格式化。

### [中等] `SimulatedEngine` 缓冲区校验 int 溢出（与真实引擎实现漂移）
- **位置**：`SimulatedEngine.cs:187`
  ```csharp
  if (buffer.Length < width * height * 4) return false;   // 65536×32768×4 溢出为 0
  ```
- **风险**：注释称"与真实引擎一致"，但真实引擎 `Fff3FpEngine.cs:582` 已用 `(long)width * height * 4` 修正。两份实现漂移，负/超大尺寸可绕过校验。
- **建议**：改为 `(long)width * height * 4 > buffer.Length`。

### [中等] `LastError()` 无长度上限且丢弃返回码
- **位置**：`Fff3FpEngine.cs:646-661`
- **风险**：`new byte[required]` 无上限（对比 `:362` 有 4MB 上限）→ 内核异常返回巨大 `required` 时大对象堆分配/OOM；第二次 `FFF3FP_GetLastError` 返回值被丢，真实错误信息丢失。

### 轻微项
- `Interop/Fff3FpNative.cs:193` `FFF3FP_Destroy` 声明为返回 `FffResult`，原生头文件 `FFF.Player.Api.h:571` 为 `void` → x64 下读到 RAX 垃圾值，一旦有人判返回值即得假结果。
- `Fff3FpEngine.cs:128`、`:640-641` 回调中 `GCHandle.FromIntPtr(...).Target` 未查 `IsAllocated`，注释称"校验仍是本会话"实际只判类型。
- `Fff3FpEngine.cs:349-359`、`:519-522` `ReadMediaInfo`/`ParseMediaInfoJson` 全 catch 返回 null 且无日志 → "媒体信息面板空白"无法定位。
- `NativeRuntime.cs:18-64` 释放 DLL 到应用目录不校验来源，版本比较用可伪造的 `FileVersionInfo.FileVersion` 字符串。

### ✅ 该模块做得好的地方
结构体布局与原生 ABI **逐字段对齐**（`FFF3FPConfiguration`/`FFF3FPSnapshot`/`FFF3FPVideoPixelProbe`/`FFF3FPRenderTargetInfo` 均与 `.h:94-193/420-433/554-567` 一致），`ConfigVersion=15` 与 `PlayerApiVersion=15` 一致，A11 适配器索引空间有明确注释 —— 静态一致性在同类项目中属上游水准。**未发现**任何布局错误。

---

## 三、Core / Settings · Diagnostics · Imaging

### [中等] 配置文件写在 exe 同目录，安装到 Program Files 时保存静默失败
- **位置**：`SettingsStore.cs:12`、`:107-110`
- **现象**：`GetConfigPath()` 固定 `AppContext.BaseDirectory + "settings.json"`；`Save` 只 `catch { Console.Error.WriteLine(...) }`。
- **风险**：安装在 `C:\Program Files\...` 时 `AtomicFile` 抛 `UnauthorizedAccessException` 被吞，或被 UAC 虚拟化重定向到 VirtualStore ⇒ 用户设置**永不生效且无任何提示**。
- **建议**：优先 `%LOCALAPPDATA%\3FCompare\`，exe 同目录作为便携模式回退；启动时做"写探针"决定目录；`Save` 失败向 UI 抛可见提示。
- **注**：便携部署是本项目明确设计目标，改为"优先 LOCALAPPDATA + 同目录回退"而非强制迁移。

### [中等] 解析失败静默回退默认值，且无备份可回滚
- **位置**：`SettingsStore.cs:36-40` + `AtomicFile.cs`
- **风险**：一次磁盘位翻转/半截写入 ⇒ 窗口几何、FFmpeg 目录、语言**永久丢失且用户无感**，旧内容被下一次 Save 覆盖。
- **建议**：`Move` 前 `File.Copy(path, path + ".bak", true)`；`Load` 失败先尝试 `.bak` 并提示"设置已重置"。

### [中等] `AtomicFile` 同进程多线程并发写同一路径会互相破坏
- **位置**：`AtomicFile.cs:31`、`:34`、`:46`
- **现象**：临时名只带 `Environment.ProcessId`，**同进程内两个线程拿到同一个 tmp**。A 打开（`FileShare.None`）后 B 打开失败，B 的 catch 里 `File.Delete(tmp)` 把 **A 正在写的文件删掉** ⇒ A 随后 `File.Move` 失败，本次保存丢失。
- **建议**：tmp 名加 `Guid` 或 `Interlocked.Increment`，并按 path 加进程内 `SemaphoreSlim`。

### [中等] 崩溃后临时文件永久残留
- **位置**：`AtomicFile.cs:31-41`
- **风险**：`FileStream` 创建成功到 `File.Move` 之间进程被杀 → `settings.json.<pid>.tmp` 残留，且**下次启动无任何清理逻辑**。
- **建议**：启动时清理 `settings.json.*.tmp`（按 pid 存活判定）。

### [中等] `AppLog.Raw()` 绕过 writer 空判断，且队列无界
- **位置**：`AppLog.cs:137`、`:18`（`ConcurrentQueue`）；对比 `:131` `Enqueue` 有 `_writer is null` 判断
- **现象**：`Raw` 不分情况入队，而它正是 `ConsoleErrorRerouter.cs:48/55/63` 与 `KernelLogBridge.cs:201` 的入口。`Initialize()` 失败（`:72-76` 静默置 `_writer = null`）或内核日志持续刷屏时，**队列只增不减直到 OOM**。
- **建议**：`Raw` 同样判空；队列改 `Channel.CreateBounded`（如 10000 条，满则丢弃并记一条 overflow）。

### [中等] 日志无大小上限，长会话单文件可无限增长
- **位置**：`AppLog.cs:46`、`:169-181`
- **现象**：文件名在启动时算一次，`PurgeOldLogs` 只在 `Initialize` 调一次。跨午夜仍写同一文件，24 小时连续播放可涨到 GB 级，撑满系统盘并拖慢 `EnumerateFiles`。
- **建议**：加按大小滚动（>32MB 切 `app-yyyy-MM-dd_N.log`）。

### [中等] `Shutdown` 可能挂住调用线程（UI 线程）
- **位置**：`AppLog.cs:113-116`（忙等 5s）、`:120`（`lock` 无超时）；调用点 `MainWindow.axaml.cs:1475`
- **风险**：worker 卡在 `WriteLine`（磁盘满/网络盘/杀软扫描）时，`_worker.IsAlive` 恒真 → 先空转 5s，随后 `lock (WriterLock)` **无限期阻塞** ⇒ 窗口关不掉。
- **建议**：`Monitor.TryEnter(WriterLock, 2000)`；worker 写入加 try/catch，连续失败时置 `_writer = null` 自杀退出。

### [轻微] `PngChunk` 边界检查可被整数溢出绕过 `[已复核，严重度已下调]`
- **位置**：`PngChunk.cs:89-90`、`:122-126`
  ```csharp
  var total = ChunkOverhead + len;                                  // 12 + len
  if (len < 0 || total < 0 || pos + total > png.Length) yield break;
  ```
- **现象**：`len ∈ [0x7FFFFFEC, 0x7FFFFFF3]` 时 `total` 仍为正，但 `pos + total` **溢出为负数**，`> png.Length` 判假 ⇒ 检查被绕过，可交出越界的 `(DataOffset, Length)`。
- **订正**：当前唯一调用点是 `bmp.Save(ms, ImageFormat.Png)` **自产数据**（`MainWindow.Capture.cs:189-195`），**外部输入不可达**，故下调为轻微。但 `PngChunk` 是 public API 且注释自定位为"内存安全热点"，**一旦接入外部 PNG（缩略图、贴图导入）即升级为严重**。
- **建议**：全程 `long` 计算：`long total = ChunkOverhead + (long)len;`，并把 `len` 限制在 `<= 0x7FFFFFFF`。

### 轻微项
- `PngChunk.cs:30-36` 读取路径**完全不校验 CRC**，截断/位翻转 chunk 会被当合法块，可能产出静默损坏的 PNG。
- `PngChunk.cs:83` 不校验 8 字节 PNG 签名；`ReadBigEndianInt32` 是 public 且无越界保护。
- `AppSettings.cs:119` `Language` 未纳入 `Normalize()` 收敛（其余字段均 Clamp），写 `Language=99` 会静默生效为中文且原样存回。
- `SettingsStore.cs:60` 版本号无上界，来自更新版的 `Version=2` 会被 `Save` 静默降级为 1。
- `SettingsStore.cs:20/23/27/33` 日志记录完整路径，暴露用户名与目录结构。
- `AppLog.cs:80-93` 无 `MinLevel` 开关，插值永远执行（好消息：落盘在后台线程，热路径不阻塞）。
- `AppLog.cs:40` `Initialize` 幂等检查非线程安全，并发进入会各建一个 `StreamWriter`。
- `ConsoleErrorRerouter.cs:58` `Write(char)` **不转发给 AppLog**，逐字符输出整段丢失；`:51-56` `WriteLine` 不补换行语义，多行异常会碎成多条。
- `Program.cs:27-28` 记录全部启动参数（含视频文件路径）到日志。

### ✅ 该模块做得好的地方
`AppSettings.Version` 默认值取 0、`Normalize()` 收敛越界值、迁移只在 `isLegacy` 下执行 —— STJ 反序列化"先跑属性初始化器再覆盖"这一经典语义陷阱**被正确处理**。JSON/AOT 覆盖完整（`JsonAotContext` 已注册三个类型，成员均为 AOT 友好类型），配置只有一层嵌套，**不存在深度 DoS 面**。`FfmpegDirectory` 与会话路径的外部输入校验（拒绝相对路径与 UNC、必须含 `avcodec-*.dll`）相当扎实。

---

## 四、Core / Sync · Display

### [中等] `GetDesc1` vtable 槽位错一格，HDR 亮度探测恒失败 `[已复核]`
- **位置**：`DxgiOutputInfo.cs:44`
  ```csharp
  private const int SltGetDesc1 = 27;  // 注释：7..18 Output + 19..22 Output1 + 23..26 Output2..5 + 27
  ```
- **现象**：注释把 `IDXGIOutput` 当成 **12** 个方法（7..18），实际是 **11** 个（GetDesc / GetDisplayModeList / FindClosestMatchingMode / WaitForVBlank / TakeOwnership / ReleaseOwnership / GetGammaControlCapabilities / GetGammaControl / SetGammaControl / GetDisplaySurfaceData / GetFrameStatistics，占 7..17）。正确累加：`7 + 11 + 4(Output1) + 1+1+1+1(Output2..5) = 26`。槽 27 实为 `CheckHardwareCompositionSupport(UINT*)`。
- **风险**：该调用会"S 成功"返回却只往结构体前 4 字节写一个 UINT，`desc.Monitor` 仍为 0 ⇒ `:140` 的 `desc.Monitor != hmonitor` 恒成立 ⇒ `TryReadLuminance` 永远 `return false` ⇒ `DisplayCapabilities.ReadForMonitor` 恒返回 null，**HDR 能力/峰值亮度全部走默认值且失败被静默吞掉**。
- **建议**：改为 `26` 并在实机用已知 HDR 显示器验证 `MaxLuminance`；给"枚举完所有输出仍未匹配"补一条 Debug 日志。

### [中等] 删除 master 后 A-B 循环区间未同步重基准
- **位置**：`SyncController.cs:120-128`
- **现象**：注释明确写着"A-B 循环区间也跟着偏"，但代码只重算 `_slots[i].Offset100ns -= newMasterOffset`，`_loopStart100ns/_loopEnd100ns` 原地不动。
- **风险**：删除第 0 路后规范时间轴整体平移 `newMasterOffset`，循环起止点却仍按旧轴解释 ⇒ 循环区间指向错误内容，偏移越大偏得越多。
- **建议**：同锁内 `_loopStart100ns += newMasterOffset; _loopEnd100ns += newMasterOffset;`（-1 哨兵保持跳过）。

### [中等] 帧率差异缓存在媒体信息就绪前"烤死"，状态栏永久误报
- **位置**：`SyncController.cs:452-478`（`HasFpsMismatch`）、`:423-429`
- **现象**：`_fpsMismatchCache` 只在 Add/Remove/Clear 时失效。9 路并行打开时每完成一路就触发计算，此时未打开完的路 `snap.FrameRate==0`，`EstimateFps` 一律回落 **24**，与真实 25/30 判为有差异并缓存为 1。
- **风险**：「⚠ 帧率不一致」警告常驻且**永不自愈**；反向也会漏报。另：master 若 `Failed` 被跳过，基准会取第 1 路，与"以 master 为基准"语义不符（`:462-472`）。
- **建议**：以"所有非失败路 `FrameRate>0`"作为可缓存前提，否则不写缓存；或加 TTL/版本号。

### [中等] 漂移校正 / 循环 / 刮擦 Seek 与帧步进完全无互斥
- **位置**：`SyncController.cs:495-546`（`TickDrift`）、`:549-563`（`TickLoop`）对 `_stepGate`（`:283`）零参与
- **现象**：`StepFrames` 走 `Task.Run`（`MainWindow.Playback.cs:31`），`TickDrift/TickLoop` 走 UI 定时器。典型交错：TickDrift 读完快照 → StepFrames 暂停全路并对齐 → TickDrift 用**过期** `masterPos` 调 `Seek`，把刚对齐好的从路又推走。
- **风险**：播放中逐帧/微调后**画面瞬时错帧**，症状随机不可复现。
- **建议**：加 `volatile bool _stepping`（或用 `Monitor.TryEnter(_stepGate)`，**不要直接 lock** —— `StepFramesCore` 内有 `Thread.Sleep(50)`，UI 线程阻塞会卡顿），检测到即整拍跳过。

### [中等] `StateChanged` 无异常隔离；`StepFrames` 异常成未观察任务异常
- **位置**：`SyncController.cs:216`、`:231`、`:287`；`MainWindow.Playback.cs:31`
- **风险**：任一订阅者抛出，异常穿透 `Play/Pause/SeekTo/Clear` 后续流程（`Clear():157` 的事件在释放之后抛，UI 就收不到"已清空"通知）；`StepFrames` 的异常被线程池吞成 unobserved exception，界面毫无提示。
- **建议**：统一 `try { StateChanged?.Invoke(...) } catch (Exception ex) { ReportRuntimeError(...); }`；对 `StepFrames` 的 Task 加续延记录。

### [中等] `RenderStallWatchdog` 轻量恢复失败后持续返回 FullRebuild
- **位置**：`RenderStallWatchdog.cs:70-79`
- **现象**：`_lightTried` 只在 `presented` 增长时复位。一旦轻量恢复无效且 presented 持续不涨，每 1250ms 就返回一次 `FullRebuild`，永不降级也永不停止。
- **风险**：配合 `MainWindow.Playback.cs:302` 的 `_ = RecoverFromFailedAsync()`，形成**周期性会话重建风暴**（现存"重建风暴抑制"只覆盖 `PlayerState.Failed` 路径）。
- **建议**：FullRebuild 后进入更长冷却或加次数上限，要求 `Reset()` 才恢复。

### 轻微项
- `RefreshAllPositions:408` 用 `Math.Clamp(masterPos + slot.Offset100ns, 0, long.MaxValue)`，**漏了** `ClampToDuration`，与 `SeekTo:260` 语义不一致。
- `FrameTimeline.cs:9-16`、`PlaybackSpeed.cs:22` 只挡 `fps<=0`，未挡非有限值：`fps=1e-300` 时 `1e7/fps` 为 `+Infinity`，`(long)` 饱和得 `long.MaxValue`，后续乘法静默溢出为负 ⇒ "前进一帧"跳回片头；`StepBySeconds(NaN)` 时三个比较全 false，静默不步进。建议 `if (!double.IsFinite(fps) || fps <= 0) return 0;`。
- `DxgiOutputInfo.cs:54-55` `MaxAdapters/MaxOutputs = 8` 硬截断，多屏/多卡超出 8 个输出时静默漏检。
- `DisplayCapabilities.cs:56` 注释"ColorSpace>=3 表示 HDR"与实现 `>= 12` 不一致。
- `GridLayout.cs:59` `CodeFor` 已成死代码，且其 2 路映射为 2x2、与 `ComputeGrid` 的 (2,1) 冲突，若将来用于"旧快照兼容"会改坏布局，建议删除。
- `StepFramesCore:323` 在 `StepFrame` 之前又读一次快照，是纯浪费的原生调用；`DriftCooldownMs`（`:438`）声明后从未使用。
- `SyncSlot.Failed/Offset100ns` 是普通属性却被后台线程写、UI 线程读，与类注释"所有公开成员均可从任意线程调用"不符。

### ✅ 该模块做得好的地方
并发骨架正确：锁内取副本、锁外执行与通知、Dispose 移出锁、`_fpsMismatchCache` 用 `Volatile` 失效；会话层有 `_disposedFlag` + `_nativeGate` 兜住锁外释放。**偏移符号约定（`media = canonical + offset`）在 `OffsetOf`/`SeekTo`/`StepFramesCore`/`TickDrift`/`RefreshAllPositions` 中全局一致**，时间↔帧号往返自洽（含 23.976/29.97），master 偏移恒 0 也守住了。`GridLayout` 三件套往返自洽、除零与零尺寸均有守卫。COM 释放完整（factory/adapter/output/output6 全部 finally Release，`AllocHGlobal` 配对 `FreeHGlobal`）—— **未发现 COM 泄漏**。

---

## 五、UI / MainWindow · Program

### [严重] 多个 `async void` 事件处理器无 try/catch，异常直接击穿进程 `[已复核]`
- **位置**：`MainWindow.axaml.cs:227`（OnOpenVideos）、`:734`（OnSaveSession）、`:774`（OnLoadSession）、`:931`（OnToggleDiff）、`:990`（OnOpenSettings）、`:1172`（MaybeExitDemoMode）；`MainWindow.Capture.cs:26`（OnExportFrame）
- **现象**：已逐行确认 `OnSaveSession`（734-775）体内**无任何 try/catch**，而它内部 `await SaveFilePickerAsync` + `SessionSnapshot.SaveToFile`（→ `AtomicFile.WriteAllText`）。路径非法/只读/被占用时抛 `UnauthorizedAccessException`。`SessionSnapshot.LoadFromFile` 的 `File.ReadAllText` **未被 try 包裹**（只有 `FromJson` 内部 catch 了 JSON 错误）。
- **风险**：`App.axaml.cs:17` 登记了 `Dispatcher.UIThread.UnhandledException` 但注释明确"不置 `e.Handled`" ⇒ 仅留日志后照常崩溃。保存会话失败 → **进程闪退 + 未保存数据丢失**。
- **建议**：上述处理器统一包 try/catch 并给用户可见提示；`LoadFromFile` 的读文件纳入 try；App 中对可恢复异常置 `e.Handled = true`。

### [严重] 缺少进程级异常兜底（非 UI 线程）
- **位置**：`App.axaml.cs:17-25`；全仓库无 `AppDomain.CurrentDomain.UnhandledException`
- **风险**：仅覆盖 UI 线程调度异常与 `TaskScheduler.UnobservedTaskException`。工作线程（内核回调线程、`Task.Run` 中的 `StepFramesAsync`、`TraceMultislotState`）抛出的未处理异常**不会落盘，进程静默终止**。
- **建议**：`Program.Main` 增加 `AppDomain.CurrentDomain.UnhandledException`，写 AppLog 后再退出。

### [中等] `PollSnapshots` 可重入
- **位置**：`MainWindow.Playback.cs:182`、`:277-296`
- **现象**：`async void` 由 DispatcherTimer 驱动；轻量恢复分支内 `await Task.Delay(120)`，而轮询间隔可能已被"高刷豁免窗"压到 83ms（`:200`）⇒ 同一方法可并发进入，重复 Pause→Play。
- **建议**：加 `_polling` 互锁标志。
- **注**：`:184` 已有顶层兜底注释（"任何未预期异常只记录，不崩进程"），异常处理是到位的，问题仅在重入。

### [中等] 关闭后恢复流程仍可能访问已释放会话
- **位置**：`MainWindow.axaml.cs:1366-1398`
- **现象**：`RecoverFromFailedAsync` 只在 `await Task.Delay(500)` 后检查 `_coordinator.IsClosed`；检查通过到 `_sync.Play()`/`_sync.SeekTo()` 之间无保护，而 `OnClosing`（`:1474`）已 `DestroyAllSessions()`。
- **建议**：加窗口级 `_closing` 标志，延迟后再次判定。

### [中等] 演示模式下重复弹出两个模态引导框
- **位置**：`MainWindow.axaml.cs:84-96` 与 `:1242`
- **现象**：FFmpeg 缺失时，构造期注册的 `Opened` 处理器与 `OnOpened` 中的 `MaybeExitDemoMode` 都会弹框（内容/按钮不同），用户需连续应答两次，且一个提供"关闭"、另一个提供"稍后"，语义冲突。

### [中等]（自测代码）自测看门狗从后台线程调用 `Close()`
- **位置**：`MainWindow.SelfTest.cs:400-415` → `:47-79`
- **现象**：看门狗在 `Task.Run` 中触发 `ExitSelfTest(3)`，其中 `DestroyAllSessions()`、`Close()`、`Dispatcher.UIThread.RunJobs()` 均在非 UI 线程执行，与 UI 线程轮询/会话访问并发。
- **风险**：跨线程访问 Avalonia 控件与已释放会话，**测试结论不可信**（可能假崩溃/假通过）。
- **建议**：改为 `Dispatcher.UIThread.Post(() => ExitSelfTest(3))`。

### 轻微项
- `MainWindow.axaml.cs:1465-1477` `OnClosing` 未停 `_transformFlushTimer`（`OnClosed` 才停），目前仅靠 `FlushPendingViewTransform` 里 `_sync.Count == 0` 间接保护，依赖脆弱。
- `:683-720` 全局快捷键未排除文本输入焦点：在书签备注框输入时，Space/Delete/←/→ 仍会触发播放/暂停、帧步进、`_bookmarks.RemoveSelected()`。
- `:429-436` `UpdateStatus` 消费 `LastOpenError` 后把降级原因写入 `StatusEngine.Text` 并 `return`，之后 `BuildEngineLabel()` 不再恢复，本次 `StatusInfo` 也不刷新。
- `:248-270` + `PlaybackCoordinator.cs:98-104` 已有 9 路时再拖入文件直接 `return`，界面无任何提示。
- `:1017-1022` 重启用 `Environment.Exit(0)` 硬退出，跳过 `OnClosed` 的定时器停止与 `AppLog.Shutdown()`。

### ✅ 该模块做得好的地方
生命周期清理完整：会话 `IDisposable.Dispose()`、三个定时器、缩略图窗口 `CloseAndDispose()`、`LanguageManager.SubscribeWeak` 防静态事件强引用、`PlayerSurface.DestroyNativeControlCore` 恢复 WndProc 并 `DestroyWindow` —— **未发现多开累积泄漏**。**未发现** `.Result`/`.Wait()` 死锁；`StepFramesAsync`、scrub BitBlt 已移出 UI 线程并带 in-flight 节流。拖放/命令行路径**未见注入或路径遍历面**（会话加载已拒绝相对路径与 UNC）。自测代码整体严谨（前置条件坐实、收集式断言、避免自证）。

---

## 六、UI / Controls · Panels

### [中等→必修] 书签面板双击跳转**完全失效** `[已复核]`
- **位置**：`Panels/BookmarkPanel.cs:43-47` 与 `:78-84`
  ```csharp
  _list.DoubleTapped += (_, _) => {
      if (_list.SelectedItem is BookmarkItem b)        // SelectedItem 恒为 string
          JumpRequested?.Invoke(b.Position100ns);
  };
  ...
  private void RefreshList() {
      _list.Items.Clear();
      foreach (var b in Items)
          _list.Items.Add($"...{b.Note}");             // 装入的是字符串
  }
  ```
- **现象**：`_list.Items` 装入的是 `string`，而处理器用 `is BookmarkItem` 判据 → **模式匹配永不成立**，`JumpRequested` 永不触发。
- **风险**：书签面板**核心功能（双击回跳）完全不可用**，且不报错；若自测只断言"事件存在"也会漏过。
- **建议**：`Items` 直接装 `BookmarkItem` 并用 `ItemTemplate`/`ToString()` 呈现，或改为按 `SelectedIndex` 索引 `Items`。
- **说明**：按定义不属"崩溃/数据损坏"，故标中等，但**列为 P0 必修**——它是必现功能失效。

### [中等] `MagnifierOverlay` 复用**可变**画刷，延迟渲染下 144 格将同色
- **位置**：`Controls/MagnifierOverlay.cs:134`、`:159-163`
- **现象**：`_cellBrush` 是单一 `SolidColorBrush`，循环内 `_cellBrush.Color = color` 后 `DrawRectangle`。Avalonia 12 的 `Render` 走延迟回放，`DrawRectangle` 只**记录画刷引用**，真正上色在回放时刻 ⇒ 届时画刷只剩最后一次赋值的颜色。
- **风险**：放大镜出现纯色块，核心功能失效。
- **建议**：改用 `Avalonia.Media.Immutable.ImmutableSolidColorBrush`，或按量化颜色建 256 级画刷池。

### [中等] 放大镜每次指针移动做一次同步 GPU 回读，无节流
- **位置**：`MagnifierOverlay.cs:59-75` → `:77-117`
- **对比**：`ProbePanel.cs:86-88` 同样场景做了 33ms（30Hz）节流，两处策略不一致。
- **建议**：与探针同口径加 ≥33ms 节流，并对 `gx/gy` 未变时跳过回读。

### [中等] `DiffOverlayView._cellsY` 无上界，除零与巨量分配
- **位置**：`Controls/DiffOverlayView.cs:49-52`
- **现象**：`var w = media?.VideoWidth ?? 1280;` 只挡 `null` **未挡 0**；`w==0` 时 `(double)CellsX * h / w / 2` 得 `Infinity`；极端宽高比（w=1）可算出数百万格 → `new float[CellsX*_cellsY]` 达 GB 级（被 catch 吞掉）。
- **建议**：`w = media?.VideoWidth > 0 ? … : 1280`，并对 `_cellsY` 加 `[8, 256]` 钳制。

### [中等] `DiffOverlayView` 单次重采样 = 数千次同步 P/Invoke，卡死 UI
- **位置**：`:34`、`:55-71`
- **现象**：`PointerPressed += (_, _) => Resample();` 每次点击在 **UI 线程**串行执行 `96 × _cellsY`（16:9 约 **2592**）次 `TryReadPixelAtSource`，每次含一次 GPU staging 回读。
- **风险**：点击后界面长时间无响应。
- **建议**：改为一次性 `TryReadPixelRegion` 批量回读，或后台线程 + 进度取消。

### [中等] `PlayerSurface` 的 PAINTSTRUCT 静态校验是**恒真断言**
- **位置**：`Controls/PlayerSurface.cs:38-44`、`:541-546`
- **现象**：`[StructLayout(LayoutKind.Sequential, Size = 72)]` 使 `Marshal.SizeOf<PAINTSTRUCT>()` **必然返回 72**，因此 `if (size != NativePaintStructSize) throw` 永远不成立 —— 它检验的是"Size 特性是否还在"，而非"与原生布局是否一致"。且 x86 下原生 `tagPAINTSTRUCT` 为 64 字节，会启动即抛异常。
- **风险**：**给了团队错误的安全感**（注释声称可防回归，实际防不住）。
- **建议**：去掉 `Size=72` 后与实际字段布局比对，或按 `IntPtr.Size` 分别取 72/64 校验。

### [中等] 像素坐标换算存在两套口径，且**均不含 zoom/pan**
- **位置**：`MagnifierOverlay.cs:92-101`（后台缓冲物理坐标）、`ProbePanel.cs:96` + `PixelReadback.cs:21-40`（片源坐标经 letterbox 映射）、`DiffOverlayView.cs:62`（片源坐标）
- **现象**：放大镜把 `localInSurface × RenderScaling` 直接当后台缓冲坐标；探针/差异走 `TryReadPixelAtSource`。两条路径**都没有**任何 `SharedZoom/SharedPanX/SharedPanY` 项（`PlayerSurface.cs:121-123`；`MainWindow.axaml.cs:869-896` 的 `MapPointerToVideoPixel` 同样只用了 `rt.DestX/DestY/DestW/DestH`）。
- **风险**：一旦缩放 >1 或平移非零，探针与放大镜读取的**都不是光标下的像素**。
- **建议**：在统一的 `PixelReadback` 入口增加 zoom/pan 逆变换参数，三处共用。

### 轻微项
- `ThumbnailPopup.cs:117-121` 未处理 `Stride` 为负（底向上位图）：`rowBytes` 为负时 `Buffer.MemoryCopy` 把长度当无符号巨值 → 越界拷贝；`MainWindow.Capture.cs:150` 同样直接依赖符号。建议 `Math.Abs(data.Stride)` 并按符号算行首地址。
- `PlayerSurface.cs:493-503`、`:330-331` 小地图尺寸为 `Math.Min(rect.Width, rect.Height) / 5`，窗口极小时为 0；WM_SIZE 用 `(short)` 取宽高，客户端 >32767 像素会溢出为负。
- `CompareGridView.cs:130-132` vs `:150-162`：`MeasureOverride` 用 `Math.Max(0, cellW)` 保护，`ArrangeOverride` 直接 `cw - 2`，窄窗口产生负宽 `Rect`。
- `CompareGridView.cs:36/50/102/121` `InvalidateVisual()` 无 `Render` 重写，属无效调用。
- `TimelineView.cs:210-251` 缓存了画刷却每帧新建 3 个 `FormattedText` + 1 个 `StreamGeometry`，与 `:176-186` 自身优化意图矛盾；`:228` 播放头横坐标未钳制。
- `AbSliderView.cs:63-102` 每帧新建 2 个 `LinearGradientBrush` + `Pen` + 2 个 `FormattedText`（均可 `static readonly`）。
- `OffsetPanel.cs:63/70` fps 极大（>1e7）时 `_frameTicks` 归零，`offset100ns / _frameTicks` 得 `Infinity`。建议 `Math.Max(1, …)`。
- `MagnifierOverlay.cs:22-24` vs `:29` `WidthPx=>160/HeightPx=>120` 从未使用，实际是 192/144，与类注释不符；`:70-71` 翻转后 `_position.X - Width - 8` 可为负，浮窗滑出可视区。
- `MainWindow.axaml.cs:571-574` 平移归一化在窗口尺寸为 0 时 `2.0/Min(W,H)` 为 `Infinity`，`dx==0` 算出 `NaN`，而 `Math.Clamp(NaN,-1,1)` **原样返回 NaN** 并下发到原生层。

### ✅ 该模块做得好的地方
**几何/除零的硬边界基本都守住了**（`Render` 入口普遍有 `w<=0||h<=0` 返回），**未发现**必然崩溃项。空引用与集合边界均有成对校验（`CompareGridView.SelectedIndex`、`DiffOverlayView._heat/_cellsY`、`BookmarkPanel.RemoveSelected`）。**未发现**事件订阅泄漏（`LanguageManager.SubscribeWeak` 是真正的 `WeakReference` 实现并主动剪除）。`PlayerSurface` 的 GDI 字体/渐变缓存有界且逐个 `Dispose`。

---

## 七、Services · Localization · Views · Platform

### [中等] `WaitForOpenCompletionAsync` 的兜底轮询实际是死代码 `[已复核]`
- **位置**：`Services/PlaybackCoordinator.cs:234`、`:237`、`:245`
  ```csharp
  var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
  await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
  ...
  while (DateTime.UtcNow < deadline && !_closed) { ... }   // 一次都不执行
  ```
- **现象**：`deadline` 在 `WhenAny` **之前**取好；若 `OpenCompleted` 事件未到达，`WhenAny` 等满 15s，返回时 `DateTime.UtcNow < deadline` 已为 false ⇒ 轮询一次都不执行，直接进 `:266` 标记 `Failed = true`。
- **风险**：注释承诺的"事件丢失时保留 100ms×15s 轮询兜底"**完全失效** —— 后端其实在 2s 就 Ready，仍被判失败，该路永久停在错误态。
- **建议**：把 `deadline` 作为同一超时窗口的唯一基准，事件等待用 `Task.Delay(剩余时间)`；或放弃 `WhenAny`，改为在轮询循环内同时观察 `readyTcs.Task.IsCompleted`。

### [中等] 两次 `OpenFiles` 交叉时配额与回调队列互相破坏
- **位置**：`:105-106`、`:118-123`、`:168`、`:279-284`
- **现象**：`_pendingAutoPlay` 与 `_onAllOpenedCallbacks` 是**全局单一**记账。会话恢复在飞时，用户再拖入文件且 `_surfaceAt` 返回 null，错误分支会 `Interlocked.Exchange(ref _pendingAutoPlay, 0)` + `_onAllOpenedCallbacks.Clear()`，把**另一批次**的配额与恢复回调（Seek/循环区间）一并清掉。
- **风险**：前一批次配额永远回不到 0 ⇒ 自动播放与会话恢复 Seek 永不执行（"文件都打开了但不动"）。
- **建议**：为每次 `OpenFiles` 分配批次对象（generation），配额与回调挂在批次上。

### [中等] `PlaybackCoordinator` 全程无 `CancellationToken`
- **位置**：`:237`、`:264`、`:344`（`Close()` 仅置 `_closed`）
- **风险**：关窗后仍持有 surface/slot/session 最长 15s；退出阶段 session 可能已被 Dispose，续体再访问即为已释放对象。

### [中等] `ScreenFrameCapture` 未校验虚拟屏幕边界，多屏负坐标直接下发给 BitBlt
- **位置**：`Platform/ScreenFrameCapture.cs:40`、`:125-135`
- **现象**：`GetDC(0)` + `BitBlt(..., screenX, screenY, ...)`，`screenX/Y` 直接来自 `childRect`，未用 `SM_XVIRTUALSCREEN/SM_CXVIRTUALSCREEN` 裁剪；也未带 `CAPTUREBLT(0x40000000)`。
- **风险**：副屏位于主屏左侧/上方时坐标为负 ⇒ 抓到黑图或错位区域，而 `BitBlt` 仍返回 TRUE ⇒ `:139` 的失败检查不触发，`MainWindow.Capture.cs:57-60` 的兜底路径会**导出一张错误 PNG 且无二次校验**；分层/透明窗口内容也抓不到。
- **建议**：用 `GetSystemMetrics` 钳制到虚拟屏幕，越界走 Path B；rop 改为 `SRCCOPY|CAPTUREBLT`。

### [轻微] 中文缺 `Status_ColorModeUnified` 键 `[已复核]`
- **位置**：`LanguageManager.cs:373`（仅英文），中文表无此键
- **现象**：`T()` 回退返回 key 本身（`:475`）⇒ 中文状态栏直接显示原始键名 `Status_ColorModeUnified`。使用点 `MainWindow.axaml.cs:378`。
- **建议**：补中文条目，并加"中英键集合一致性"单测（现 162/161 条，差 1）。

### [轻微] 语言切换注释与实现不符（非缺陷）`[已复核，已订正]`
- **位置**：`MainWindow.axaml.cs:997` 注释"语言即时生效（绑定自动刷新）" vs `LocExtension.cs:7-9` 注释"语言切换需重启生效（与 WinForms 版行为一致）"
- **订正**：子审查组曾将"切换语言后菜单不刷新"报为中等缺陷。**实为有意设计**（`LocExtension.ProvideValue` 一次性求值是 AOT 安全的取舍，设置窗口已弹重启询问）。此处**只需修正误导性注释**，不是功能缺陷。

### 轻微项
- `SettingsWindow.cs:229` 无效 FFmpeg 目录可被"确定"保存（状态标签已提示"✗ 目录无效"），重启后被后端静默忽略 ⇒ 用户陷入"设置→重启→无效"循环。建议 `Accept()` 中阻止关闭。
- `SettingsWindow.cs:88/212` 退化路径下 `Math.Max(0, PreferredAdapterIndex + 1)` 在只有 1 项时得 1 ⇒ `adapter = -2`，与 `AppSettings.Normalize()` 的 `[-1,15]` 不一致。建议用 `AdapterInfo.Index` 而非位置索引。
- `ScreenFrameCapture.cs:51-54` 裁剪矩形只钳右下、不钳左上，`relX/relY < 0` 时 `Clone` 抛异常被 `:59` 吞掉返回 null，静默失败无日志。
- `LanguageManager.cs:483` `Tf` 用 `string.Format(s, args)` 未指定 `CultureInfo`，`{1:0.#}` 在 de-DE 等 locale 输出逗号（仅影响显示，**未发现**任何 parse 回路）。
- `PlaybackCoordinator.cs:40/106/156` `_onAllOpenedCallbacks` 是无锁 `Queue<Action>`，依赖"续体都回到 Avalonia 同步上下文"的隐式假设；`_ = OpenSlotAsync(...)` 的 catch 只改状态、不打日志。
- `Views/MessageBox.cs:15-23` `CanResize=false`、固定 460×220、无 `ScrollViewer`，长异常文本被裁掉且无法查看。
- `ViewModels/MainViewModel.cs` 全仓未被实例化（死代码），且 `:13/16` 用字段初始化器快照 `T()`，切语言后不更新。
- `MainWindow.axaml.cs:180` 注释"16ms 播放中"与实际 250ms 不符。

### ✅ 该模块做得好的地方
`Interlocked` 配额记账、弱订阅防泄漏、`BitBlt` 返回值校验、原子写配置、FFmpeg 目录安全校验（UNC/相对路径/avcodec 白名单）均有针对性防护；`Task.Run` 中的 UI 对象访问已做隔离（`MainWindow.Playback.cs:106-128` 提前把 hwnd/screen 拷到局部变量）。`ThemeResources` 一次性装配、无重建无订阅 ⇒ **未发现**资源字典泄漏。**未发现** GDI 句柄泄漏（Bitmap/Graphics/GetHdc/ReleaseDC 成对，调用方均 `using`）。**未发现**写注册表、改系统设置、执行用户可控外部程序（唯一外部启动是 `Process.Start(Environment.ProcessPath)` 重启自身，无注入面）。

---

## 八、安全专项小结

| 面 | 结论 |
|---|---|
| DLL 劫持 / 侧载 | ⚠ **存在实打实的代码执行面**：`NativeRuntime` PATH 探测 + 裸 `SetDllDirectoryW`，零完整性校验；`settings.json` 与 exe 同目录可写，篡改 `FfmpegDirectory` 即可指定加载目录 |
| 路径遍历 | ✅ 已封：会话加载拒绝相对路径与 UNC（`MainWindow.axaml.cs:830-838`）；导出路径来自 `SaveFilePicker` |
| 反序列化 | ✅ 无 DoS 面：仅一层嵌套，AOT 上下文覆盖完整；`JsonDocument.Parse` 为 AOT 安全路径 |
| 二进制解析 | ⚠ `PngChunk` 存在 `pos+total` 溢出与零 CRC 校验，但**当前仅消费自产 PNG，外部不可达** |
| 命令注入 | ✅ 未见：命令行参数仅做等值比较；无用户可控的外部程序启动 |
| 信息泄露 | ⚠ 轻微：日志明文记录用户视频路径与目录结构，`logs/` 目录未收紧 ACL |

---

## 九、性能专项小结

| 热点 | 位置 | 影响 |
|---|---|---|
| 差异叠加重采样 | `DiffOverlayView.cs:34/55-71` | 单次点击 **2592 次**同步 GPU 回读，UI 线程串行 ⇒ 长时间无响应 |
| 放大镜指针回读 | `MagnifierOverlay.cs:59-75` | 无节流，每次 `PointerMoved` 一次 GPU 同步（探针同场景已做 33ms 节流） |
| 渲染热路径分配 | `DiffOverlayView:122/130`、`TimelineView:210-251`、`AbSliderView:63-102` | 每帧数千个 `SolidColorBrush` + 每帧新建 `FormattedText`/`StreamGeometry` |
| 热路径字符串插值 | `Fff3FpEngine.cs:293/301/558/606` | 60Hz × N 路，每秒数百次分配（成功路径也构造） |
| 日志队列无界 | `AppLog.cs:18/137` | 内核日志刷屏时内存无上限增长 |

未发现：UI 线程同步阻塞（无 `.Result`/`.Wait()` 死锁）、每帧同步写盘、每帧打日志。轮询频率设计合理（16ms 初值 → 250/250/1000ms 三档自适应，scrub 150ms + 单飞节流）。

---

## 十、修复优先级与发布结论

### P0 — 发布阻断（必修，预计 1~2 个工作日）
| # | 问题 | 模块 |
|---|---|---|
| 1 | 统一原生调用包装 `WithNative`，消除 use-after-free 窗口 | `Fff3FpEngine.cs` |
| 2 | 7 处 `async void` 加异常边界 + 补 `AppDomain.UnhandledException` | `MainWindow.*` / `Program.cs` |
| 3 | FFmpeg DLL 加载加签名校验 + 换 `SetDefaultDllDirectories`/`AddDllDirectory` | `NativeRuntime.cs` |
| 4 | 修复书签双击跳转（`SelectedItem` 类型判据） | `BookmarkPanel.cs:43-47` |
| 5 | `SltGetDesc1` 由 27 改为 26，实机验证 HDR 亮度 | `DxgiOutputInfo.cs:44` |

### P1 — 强烈建议（影响正确性与体验）
6. `WaitForOpenCompletionAsync` 兜底轮询失效（改为单一 deadline 基准）
7. 删除 master 后 A-B 循环区间重基准
8. 帧率差异缓存加"所有路 FrameRate>0"前提
9. 帧步进与漂移校正/循环加 `_stepping` 互斥
10. `AppLog` 队列上限 + 大小滚动 + `Monitor.TryEnter`
11. `AtomicFile` 加 `.bak`、tmp 名加 GUID、启动时清理残留
12. `MagnifierOverlay` 改不可变画刷 + 33ms 节流
13. `DiffOverlayView` 批量回读 + `_cellsY` 上界钳制
14. 截屏钳制虚拟屏幕边界 + `CAPTUREBLT`
15. `PollSnapshots` 重入互锁；`PlayerSurface` PAINTSTRUCT 校验改为真实布局比对

### P2 — 可延后（下一迭代）
其余全部轻微项：本地化缺键与注释订正、`SettingsWindow` 无效目录拦截、日志脱敏、`MainViewModel` 与 `GridLayout.CodeFor` 死代码清理、渲染对象缓存、各类边界加固。

### 最终结论

**未达到发布标准（No-Go）。**

判定依据有三：

1. **存在 4 项严重缺陷**，其中 2 项直接导致进程崩溃（原生调用 use-after-free、`async void` 异常击穿），1 项构成可利用的代码执行面（DLL 劫持）。这三类均属于"用户可感知的崩溃或被攻击面"，不符合发布门槛。
2. **存在 1 项必现功能失效**（书签双击跳转）与 1 项静默功能全失效（HDR 亮度探测 vtable 槽位错一格），二者均**不报错**，用户只会认为"功能不好用"，长期损害产品可信度。
3. **既有已知阻断项未解决**：多画面约 50% 概率在 `dxgi.dll` 崩溃（上游 issue #7），与本次发现无关但同样阻断发布。

**正面评价**：该代码库的工程质量明显高于同类迁移项目。结构体 ABI 逐字段对齐、`System.Text.Json` 反序列化语义陷阱处理正确、原子写配置、COM/GDI 句柄成对释放、静态事件弱订阅、渲染入口的零尺寸守卫、注释中大量"踩过的坑"记录 —— 这些都表明作者清楚风险所在并已系统化处理。**已验证的既有质量基线**：全解决方案 Release 构建 0 警告 0 错误，Core 单测 175 通过，`--selftest`/`--screentest`/`--sessiontest` 均 EXIT=0。

**建议路径**：完成上述 P0 五项（工作量不大，且每项都有明确改法）→ 补对应回归断言并做反向验证 → 重跑 `构建 + 175 单测 + selftest + sessiontest + screentest` → 即可进入发布评审。P1 可在发布候选分支并行推进。

---

---

# 附录：修复实施与验证结果（2026-09-16 上午追加）

## A. 实施方式

6 个修复组并行执行，**文件集严格不相交**以避免写冲突；所有子 agent 被禁止运行构建与任何 git 操作，验证统一由主审执行。

## B. 验证结果（全部通过）

| 闸门 | 结果 |
|---|---|
| Release 构建 | ✅ **0 错误**（1 个警告为基线既有 `AVLN3001`，非本轮引入） |
| Core 单元测试 | ✅ **175 通过 / 0 失败** |
| `--selftest` | ✅ **EXIT=0** |
| `--sessiontest` | ✅ **EXIT=0** |
| `--screentest` | ✅ **EXIT=0**，导出 PNG **673,986 字节**（与修复前基线 09:42 完全一致 ⇒ 渲染行为无回归） |

## C. 修复过程中新发现并解决的问题

### C1 [严重] 内核子模块被切到上游 master，与托管版本不匹配
- **现象**：内核 `HEAD=b046b89`（上游 issue #7 修复），`PlayerApiVersion=14`；托管 `ConfigVersion=15` ⇒ 所有会话创建失败，报 `未就绪（状态=）` 与 `找不到入口点 FFF3FP_SetLogCallback`。且该提交**不含** A11 基线 `68e1965`，多显卡指定功能已丢失。
- **处理**：内核切回 `68e1965`（API 15，含 A11）。**取舍**：本轮放弃 `b046b89` 的 issue #7 修复——它未携带 A11，直接采用会丢功能；后续需将两者合并后再统一升到 16。
- **非本轮代码审查引入**，但当时处于"完全不可运行"状态，必须先行恢复。

### C2 [严重] 内嵌资源覆盖导致内核降级
- **现象**：A 组为 `NativeRuntime.ExtractEmbeddedDll` 增加 SHA-256 校验后，按"哈希不同即覆盖"会用**更旧的内嵌基线**覆盖磁盘上刚构建好的内核，实测 API 15 → 14。
- **处理**：改为**磁盘已存在则绝不覆盖**，仅在缺失时释放；差异只记日志。内核由 `构建全部.ps1` 管理，托管侧不介入。

### C3 [中等] 原生调用锁选型错误
- **现象**：`WithNative` 初版用普通 `lock` 把所有 P/Invoke 串行化。`FFF3FP_Open` 耗时数百 ms～数秒，持锁期间 UI 线程轮询 `ReadSnapshot` 全部阻塞、UI 停止泵消息。
- **处理**：改用 `ReaderWriterLockSlim` —— 读锁覆盖绝大多数短调用（可并发、不冻 UI），写锁仅用于 `Dispose` 的 `Destroy`（独占并等待在飞读锁退出，**use-after-free 防护不变**）。

### C4 [中等] 子 agent 越界改动引入的两个缺陷（已修）
- `MainWindow.Playback` 的 FullRebuild 分支**漏复位 `_recovering`**（该标志只在 LightRecovery 的 `finally` 复位）⇒ 一旦升级到完整重建，恢复能力永久失效。已补 `finally` 归还。
- 自测看门狗改用 `Dispatcher.UIThread.Post` 后，UI 线程一忙就永不触发 ⇒ 从"40 s 后 code=3 退出"退化为"永久挂起"。已加 5 s 硬兜底 `Environment.Exit(3)`，兼顾线程安全与可诊断性。

### C5 [轻微] 新增断言与节流的冲突
- D 组自行新增的"放大镜-探针九宫格一致性"断言连续同步采样，间隔小于放大镜 33 ms 节流窗口 ⇒ 回读被丢弃、返回上一点像素，表现为"第 2 个点起全错"（极易误判为坐标公式错误）。已在每个采样点前加 `await Task.Delay(40)`。坐标域换算本身验证正确：`源中心(1920,1080) → 缓冲(997,561)`。

## D. 未强行"修到绿"、需后续处理的事项

1. **`GetDesc1` vtable 槽位已由 27 改为 26，但 HDR 亮度仍未匹配到 HMONITOR**（日志：`未找到匹配 HMONITOR ...，HDR 亮度回退默认值`）。槽位修正本身正确（`IDXGIOutput` 为 11 个方法非 12），但本机无 HDR 显示器，无法确认端到端效果 —— 需在 HDR 屏上实机复核，不建议在验证条件不具备时宣称已修复。
2. **多画面约 50% 崩溃（上游 issue #7）**仍未解决。`b046b89` 声称修复了该问题但版本不匹配、且不含 A11；需合并后再验证。此为本轮之外已知的发布阻断项。
3. **素材 `real_4k_hevc10_60m.mp4` 曾损坏至 815 KB**（标称 211 MB），导致 sessiontest 误报为"P0-1 回归"。已按 `docs/09` 重新下载（221,116,218 字节）并通过。**该素材历史上第三次以同样方式伪装成代码回归**，建议后续在测试入口加体积校验。
4. **既有警告 `AVLN3001`**（`MainWindow` 无无参构造函数）保留未改：与 HEAD 版本签名一致，属基线固有，修改会影响启动路径，风险大于收益。

## E. 结论更新

**修复后，本报告列出的 5 项 P0 与全部 P1 均已处理并通过回归验证。**

- 严重 4 项：已修复并验证（原生调用 use-after-free、`async void` 异常边界 + 进程级兜底、DLL 加载加固、内核版本不匹配导致的不可用）。
- 必现功能失效 2 项：书签双击跳转已修复；HDR 亮度探测已修正槽位但**待实机确认**（见 D.1）。

**修订后的发布意见**：代码质量层面已可达发布标准；但**多画面崩溃（上游 issue #7）仍是唯一的发布阻断项**，且其修复与 A11 的合并尚未完成。建议在该问题解决前不要对外发布多路对比能力，或先以单路/少路场景发布并明确标注限制。

*本报告由 6 个并行审查组分模块产出，主审对全部严重项与关键中等项做了独立源码复核，并订正了 3 处初判结论；附录记录修复实施与验证结果。*
