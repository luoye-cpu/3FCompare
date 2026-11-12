# 153 · P2-8 内核最小面实施：PresenterTimerResolution 幂等 Raise/Release（2026-11-12）

> **一句话结论**：按 docs/152 §五 的建议，**只做内核最小面**（宿主侧待 U，见 §六）——`PresenterTimerResolution` 改成幂等 `Raise()/Release()`，`TimedTextThread` 在"本拍有 present → Raise、本拍纯 devicePollOnly → Release"；播放/呈现期间仍恒持 1ms，暂停/Ended/图片静止首个空轮询（≤~0.5 s）就释放，线程退出路径兜底 Release。**只改 `VideoRenderer.cpp` 一个文件**（3 处 hunk，+65/−22 含既有改动外的本轮 3 处），**0 头文件/0 导出/0 ABI/0 日志/0 契约变化**。内核构建 **rc=0 / warnings=0 / errors=0**，新产物 **6,528,000 B / sha16 `EC3B89A550B3BD0E`**（`3fp/build` 与宿主 bin 已同步），导出 **131 不变**（109 `FFF3FP_*` + 22 `FFF_*`）。实机验证：探针 359 帧/0 丢帧/账与快照自证不变、GUI 播放 48.96% → 暂停 0.52% → 恢复 36.46%、10 次暂停恢复循环无线程/进程异常。**零 commit / 零 push；宿主侧未改**。

## 一、背景与范围

docs/152 的只诊断结论：宿主 4 台常驻 `PrecisionTimer` 从 `Form1_Load` 起持进程级 1ms，内核 `FFF.Player.Present` 渲染过即永不退出（暂停/最小化/静图都持第二份 1ms）；实测 CPU 常驻税小，`1ms` 的真实代价在 C-state/功耗。§五 的建议是**先做内核最小面**（低风险、零可见行为、语义正确性修复），宿主侧等「100ms 兼双耳跟随心跳」U 再定；本轮即执行该范围。

**不在本轮**：宿主侧 timer 门控（Form1 状态源/字幕歌词判据/最小化降频/恢复补帧）；停 `TimedTextThread`；改 LakeUI `PrecisionTimer`；P2-8 的功耗/C-state 量具。

## 二、改动（仅 `FFF.Native/3FP/Render/VideoRenderer.cpp`）

1. **`:97-131` `PresenterTimerResolution` 幂等化**（原 `:108-114`）：新增
   - `void Raise() noexcept { if (raised) return; raised = timeBeginPeriod(1) == 0; }`
   - `void Release() noexcept { if (!raised) return; timeEndPeriod(1); raised = false; }`
   - 构造 `Raise()`、析构 `Release()` 兜底；不可拷贝/noexcept 不变。
   - 注释同步改为"呈现期持有、空轮询/退出释放"，并写明**幂等的原因**：`timeBeginPeriod/timeEndPeriod` 是进程级引用计数，多一次 `timeEndPeriod` 会误删本进程仍依赖的 1ms 请求。
2. **`:5996-5999` 线程入口注释**：原"held for the whole thread"改为"呈现期持有，空轮询/退出路径释放（P2-8）"。
3. **`:6021-6036` 呈现循环**（`wait_for(500ms)` 返回、`devicePollOnly`/`timedTextThreadRunning_` 判定之后）：
   - 防御臂 `!timedTextThreadRunning_`：先 `timerResolution.Release()`（`:6026`）再 continue；
   - `if (devicePollOnly) timerResolution.Release(); else timerResolution.Raise();`（`:6035-6036`）——位置在 cadence `wait_until` 与锁外 `PresentTimedText` **之前**，Release 也早于 `RequestRecoveryIfDeviceLost(); continue;`。
   - 效果：60 Hz 流每拍 `Raise()` 是 no-op（不重复计数）；暂停/Ended/图片静止首个 500 ms 空轮询即 Release；generation 自增/notify 唤醒的下一拍先 Raise 再 pace/present。
4. 其余 hunk（`:3`、`:1732`、`:5991`、`:6110`、`:8371` 等）是前几轮 P0-3/P2-9 遗留，**不是本轮**；EOL 保持 CRLF（9917 CRLF / 0 lone LF）。

## 三、构建与产物（E1）

| 项 | 值 |
|---|---|
| 命令 | `python _3fc_tmp/build_kernel.py Release C:\PLAN\3FCompare\3fp\kernel\FFF.Native\FFF.Native.vcxproj`（framework MSBuild，VS18） |
| 结果 | **rc=0 / warnings=0 / errors=0**（VideoRenderer.cpp 重编译 + 全量链接） |
| 产物 | `kernel\FFF.Native\x64\Release\FFF.Native.dll` = **6,528,000 B / sha16 `EC3B89A550B3BD0E`**（2026-11-12 03:05:11）；post-build 自动同步 `3fp/build/FFF.Native.dll`（同哈希） |
| 宿主 bin | 为实机验证手动同步 `FFF.Player\bin\Release\net10.0-windows\FFF.Native.dll` = 同哈希（旧 `F84A9759B6C68E32` 已替换；docs/150/151 的 F84A 是上一轮产物，历史值不改） |
| 导出/ABI | 131 导出不变（`FFF3FP_*` 109 + `FFF_*` 22）；`PlayerApiVersion 20` / `SnapshotVersion 23` 未动；无头文件/工程文件改动 |
| 内核仓状态 | 仍 15 个已修改 + `ThreadNaming.h` 未跟踪（本轮只让 `VideoRenderer.cpp` 的 diff 增加 3 处 hunk）；**未 commit / 未 push** |

## 四、实机验证（E1）

1. **探针（新 DLL，装 sink）**：`play_back_perf_bench.exe seek4k_hevc.mp4 --one --window 1280x720 --seconds 6 --adaptive 1 --decode gpu` ⇒ `frames=359 decoded=362 dropped=0`、`PRESENT p50=16.57 / p90=18.47 / p99=20.08 / max=34.65`、`PLACEMENT rc=0 tier=1 workerSets=12 presentSets=0`、`PLACEMENT_SNAPSHOT` 正常、进程 **exit=0**（关闭路径含 Release 兜底，无死锁/崩溃）。
2. **GUI 暂停/恢复（新 DLL）**：`playing 48.96%` → 空格 `paused 0.52%` → 空格 `resumed 36.46%`（单核百分比）；`FFF.Player.Present` 全程 alive（暂停时线程 CPU Δ=0ms）。与打补丁前 `42.7/0.52/40.1` 同量级，**无可见行为变化**。
3. **10 次暂停/恢复压力**：线程数 **123 → 123**、进程未退出、结束后残留进程 0；最后 CPU 1.6%（停在暂停态，符合 Release 路径）。用于抓 Raise/Release 配对/泄漏，未发现异常。
4. **图片 + 启动**：图片模式线程普查 128 条、`FFF.Player.Present` alive；`startup_image_time` 2 次冷启动开 1080p 图 T2 = 887.7 / 1039.9 ms（1 有效 + 1 spawn-slow），无回退迹象。

## 五、验证边界（必须写明）

- **无法从外部证明"暂停后进程时基回落到 15.6 ms"**：宿主 4 台 LakeUI `PrecisionTimer` 仍持进程级 1ms 引用（docs/152 §二），单改内核必然测不到；`NtQueryTimerResolution` 的系统读数还被 PresentMonService 等第三方常驻顶在 1ms，不可归因。⇒ 本项收益需**宿主侧同改后**用功耗/C-state 量具或进程内读数验证；当前只能验证"语义正确 + 无回归"。
- `timeEndPeriod` 返回值仍按改前习惯忽略；`Raise()/Release()` 的配对靠代码审查（dtor 兜底 + `raised` 门控）与上文压力测试。
- 边角：低帧率/解码停顿 >500 ms 时两个 present 之间也可能 Release 一次（下一帧前立刻 Raise），只影响 session worker 的 `Sleep(1)/wait_for` 精度，不改语义。若要严格"仅 Playing 持有"，需 `PlayerSession` 状态驱动（不在最小面，风险更大）。
- 第三方 `timeBeginPeriod` 持有者（PresentMonService 等）不属本进程，本改动不涉及。

## 六、未做 / 下一步

1. **宿主最小面（待 U）**：Form1 补 `Resize/Minimized` 状态源（当前无）；字幕/歌词泵间隔判据并入播放状态（弹幕已是此型）；`界面呈现器` 最小化时 Interval 500–1000 ms；恢复用现有"使图层失效/强制提交"立即补帧。**U**：100 ms 兼作双耳跟随设备心跳（`Form1.vb:339-342`），播放中最小化能否停；要进程时基归零必须 4 台常驻 timer 全停（只停三泵不够）；不建议改 LakeUI third_party。
2. **验证工具**：需要 C-state/功耗级量具（如 `powercfg /energy`、或同改后在进程内读 `NtQueryTimerResolution`）才能量化 P2-8 收益；本机 CPU 时间量具看不见。
3. 若用户无功耗/续航诉求，P2-8 到此为止（内核语义已正确化），宿主侧可只留方案记录。

## 七、复现命令

```powershell
# 构建内核（framework MSBuild）
python C:\PLAN\3FCompare\_3fc_tmp\build_kernel.py Release C:\PLAN\3FCompare\3fp\kernel\FFF.Native\FFF.Native.vcxproj

# 探针冒烟（新 DLL）
$env:FFF_PLACEMENT_TRACE='1'; $env:FFF3FP_PREFETCH='0'
& C:\PLAN\3FCompare\3fp\build\play_back_perf_bench.exe C:\PLAN\3FCompare\_3fc_tmp\seek4k_hevc.mp4 `
  --one --window 1280x720 --seconds 6 --adaptive 1 --decode gpu --color sdr

# GUI 暂停/恢复与 10 次循环压力（需先把新 DLL 同步到宿主 bin）
pwsh -NoProfile -ExecutionPolicy Bypass -File C:\PLAN\3FCompare\_3fc_tmp\pause_check.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File C:\PLAN\3FCompare\_3fc_tmp\pause_stress.ps1

# 线程名/存活（本地工具，已加"按线程名"一节）
pwsh -NoProfile -ExecutionPolicy Bypass -File C:\PLAN\3FCompare\3fp\build\imgperf\thread_census.ps1 `
  -Exe C:\PLAN\3FCompare\3fp\kernel\FFF.Player\bin\Release\net10.0-windows\FFF.Player.exe
```

## 八、证据分级与 U

- **E1**（本轮实机/构建）：§三 构建 rc/产物哈希/导出数；§四 探针、GUI、压力、启动数字。
- **E2**（代码可核验）：§二 的 3 处 hunk 与幂等语义；`timeBeginPeriod/timeEndPeriod` 进程级引用计数。
- **U**：①单侧改动无法证明进程时基回落（需宿主同改 + 功耗量具）；②100 ms 定时器里"双耳跟随心跳"在播放中最小化的必要性；③"仅 Playing 持有"是否需要 PlayerSession 状态驱动（更严格语义，但改动面更大）。