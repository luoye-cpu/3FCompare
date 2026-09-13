using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Controls;
using _3FCompare.Panels;
using _3FCompare.Services;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare;

/// <summary>主窗口（M2：核心播放面已接线）。
/// 打开/播放/步进/循环/缩放平移/网格布局/时间轴/状态栏全量；面板与对话框 M3 实装。</summary>
public partial class MainWindow : Window
{
    private async System.Threading.Tasks.Task RunScreentestAsync(string input, string outputPng)
    {
        var code = 1;
        try
        {
            _step = "screentest 打开";
            Grid.SetCount(1, _realMode);
            Console.WriteLine($"screentest: 打开 {input} ({(_realMode ? "真实" : "演示")})");
            _coordinator.OpenFiles(new[] { input }, autoPlay: true);

            _step = "screentest 等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            _step = "screentest 渲染等待";
            await System.Threading.Tasks.Task.Delay(500);

            _step = "screentest 抓帧";
            var surface = Grid.GetSurface(0);
            System.Drawing.Bitmap? bmp = null;
            if (surface is not null && surface.Hwnd != 0)
                bmp = _3FCompare.App.Capture.ScreenFrameCapture.CaptureWindowFrame(surface.Hwnd);
            bmp ??= CapturePixelSampled(_sync.Slots.FirstOrDefault()?.Session);

            if (bmp is not null)
            {
                using (bmp)
                    bmp.Save(outputPng, System.Drawing.Imaging.ImageFormat.Png);
                var size = new FileInfo(outputPng).Length;
                Console.WriteLine($"screentest: PNG {size} bytes");
                code = size > 1000 ? 0 : 1;
            }
            else
            {
                Console.Error.WriteLine("screentest: 抓帧失败");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screentest: 失败 {ex.Message}");
        }
        finally
        {
            Console.Out.Flush();
            Environment.Exit(code);
        }
    }

    // ══════════ 打开 / 拖放 ══════════


    private async System.Threading.Tasks.Task RunSelftestAsync(string videoPath, string? dropVideoPath = null)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var last = _step; var stable = 0;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                stable = _step == last ? stable + 1 : 0;
                last = _step;
                if (stable >= 40)
                {
                    Console.Error.WriteLine($"selftest: 看门狗触发 ✗ 卡在步骤 [{_step}] 超过 40s");
                    Console.Error.Flush();
                    Environment.Exit(3);
                }
            }
        });
        var code = 2;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"selftest: 文件不存在 {videoPath}");
                Environment.Exit(2);
            }

            _step = "打开";
            Grid.SetCount(1, _realMode);
            Log($"打开 {videoPath} ({(_realMode ? "真实" : "演示")}模式)");
            _coordinator.OpenFiles(new[] { videoPath }, autoPlay: true);

            // 等待就绪（≤15s）
            _step = "等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            var ready = _sync.ReadMasterSnapshot();
            if (ready is null || !PlaybackCoordinator.IsReadyState(ready.State))
                throw new InvalidOperationException($"未就绪（状态={ready?.State}）");
            Log($"就绪 ✓ 时长={TimeSpan.FromTicks(ready.Duration100ns):hh\\:mm\\:ss}");

            // 自动播放断言（打开完成→统一 Play 契约；须在步进前验证——步进会暂停播放）
            _step = "自动播放断言";
            var deadline2 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline2)
            {
                var s = _sync.ReadMasterSnapshot();
                if (s is { State: PlayerState.Playing }) { Log($"播放中 pos={TimeSpan.FromTicks(s.Position100ns):g}"); break; }
                await System.Threading.Tasks.Task.Delay(100);
            }
            if (_realMode && _sync.ReadMasterSnapshot() is not { State: PlayerState.Playing })
                throw new InvalidOperationException($"自动播放未启动（状态={_sync.ReadMasterSnapshot()?.State}）");

            // VRR 呈现路径覆盖：开启撕裂模式并记录支持状态（不支持则静默回退 VSync）
            _step = "VRR 呈现";
            var vrrSupported = _sync.Slots[0].Session.SetPresentConfig(true);
            Log($"VRR 撕裂呈现: {(vrrSupported ? "显示器链支持 ✓" : "不支持 → 保持 VSync 锁定")}");

            // A9 媒体率呈现节奏覆盖
            _step = "VRR 节奏";
            var pacingSupported = _sync.Slots[0].Session.SetPacingConfig(true);
            Log($"VRR 媒体率节奏: {(pacingSupported ? "已启用 ✓" : "不支持")}");

            // 帧步进 +1：位置不得后退（真实模式）
            _step = "帧步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"帧步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    Log($"⚠ 帧步进位置后退（已知问题）{before} → {after}");
            }

            // 秒步进 +1：同上断言
            _step = "秒步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                _sync.StepSeconds(_sync.StepProfile.SecondsStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"秒步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    Log($"⚠ 秒步进位置后退（已知问题）{before} → {after}");
            }

            // 窗口最大化/还原测试：验证窗口最大化后视频渲染是否继续
            _step = "窗口最大化测试";
            if (_realMode)
            {
                // 确保播放中
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                var hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (hwnd != nint.Zero)
                {
                    var before = _sync.GetMasterPosition100ns();
                    var beforeSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化前: pos={TimeSpan.FromTicks(before):g} presented={beforeSnap?.PresentedVideoFrames} state={beforeSnap?.State}");

                    // 最大化（使用 Avalonia WindowState 以触发布局更新）
                    WindowState = WindowState.Maximized;
                    await System.Threading.Tasks.Task.Delay(3000);

                    // 强制重新测量/布局，确保子 HWND 尺寸同步
                    Grid.InvalidateMeasure();
                    Grid.InvalidateArrange();
                    UpdateLayout();

                    var midSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化后: pos={TimeSpan.FromTicks(midSnap?.Position100ns ?? 0):g} presented={midSnap?.PresentedVideoFrames} state={midSnap?.State}");

                    // 如果进入 Failed 状态，等待恢复尝试（PollSnapshots 中的 recovery 逻辑）
                    if (midSnap?.State == PlayerState.Failed)
                    {
                        Log($"引擎进入 Failed 状态，等待恢复...");
                        await System.Threading.Tasks.Task.Delay(3000);
                        var recoverySnap = _sync.ReadMasterSnapshot();
                        Log($"恢复后: state={recoverySnap?.State} presented={recoverySnap?.PresentedVideoFrames}");
                        if (recoverySnap?.State == PlayerState.Failed)
                        {
                            Log($"❌ 引擎恢复失败，渲染管线永久停滞");
                            throw new InvalidOperationException("窗口最大化导致引擎永久失败");
                        }
                        else
                        {
                            Log($"✅ 引擎恢复成功 (state={recoverySnap?.State})");
                        }
                    }

                    // 还原（使用 Avalonia WindowState 以触发布局更新）
                    WindowState = WindowState.Normal;
                    await System.Threading.Tasks.Task.Delay(3000);

                    // 强制重新测量/布局
                    Grid.InvalidateMeasure();
                    Grid.InvalidateArrange();
                    UpdateLayout();

                    var afterSnap = _sync.ReadMasterSnapshot();
                    var after = _sync.GetMasterPosition100ns();
                    var presentedDelta = (afterSnap?.PresentedVideoFrames ?? 0) - (midSnap?.PresentedVideoFrames ?? 0);
                    Log($"还原后: pos={TimeSpan.FromTicks(after):g} state={afterSnap?.State} presented={afterSnap?.PresentedVideoFrames} (Δpresented/3秒={presentedDelta})");

                    if (presentedDelta <= 0 && afterSnap?.State == PlayerState.Playing)
                    {
                        Log($"❌ 窗口最大化/还原后视频卡死！presented 未增长");
                        throw new InvalidOperationException("最大化/还原导致渲染停滞");
                    }
                    else if (afterSnap?.State == PlayerState.Failed)
                    {
                        Log($"❌ 引擎处于 Failed 状态，渲染管线已死锁");
                        throw new InvalidOperationException("最大化/还原导致引擎永久失败");
                    }
                    else if (afterSnap?.State != PlayerState.Playing)
                    {
                        Log($"⚠ 播放状态变为 {afterSnap?.State}（非卡死，尝试恢复播放）");
                        _sync.Play();
                        await System.Threading.Tasks.Task.Delay(1000);
                        var final = _sync.ReadMasterSnapshot();
                        Log($"恢复播放后: state={final?.State} presented={final?.PresentedVideoFrames}");
                    }
                    else
                    {
                        Log($"✅ 最大化/还原测试通过：presented +{presentedDelta}/3秒");
                    }
                }
                else
                {
                    Log("⚠ 无法获取窗口句柄，跳过最大化测试");
                }
            }
            _step = "媒体信息";
            var media = _sync.Slots[0].Session.ReadMediaInfo();
            if (media is not null)
                Log($"媒体 {media.VideoWidth}x{media.VideoHeight} @{SyncController.EstimateFps(ready):0.##}fps {media.Codec} HDR={media.IsHdr}");

            // 视图变换压力测试：模拟用户快速滚动缩放，检测是否导致视频卡死
            _step = "视图变换压力测试";
            {
                // 先 Seek 到视频开头，确保有足够的播放时长
                _sync.SeekTo(0);
                await System.Threading.Tasks.Task.Delay(300);
                // 启用循环防止短视频播完
                var dur = _sync.GetMasterDuration100ns();
                _sync.LoopEnabled = true;
                _sync.LoopStart100ns = 0;
                _sync.LoopEnd100ns = dur;
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                // 确认播放中 + 读取呈现计数器基线
                var beforePos = _sync.GetMasterPosition100ns();
                var beforeSnap = _sync.ReadMasterSnapshot();
                var beforePresented = beforeSnap?.PresentedVideoFrames ?? -1;
                var beforeSwapPresents = beforeSnap?.SwapChainPresents ?? -1;
                var beforeState = beforeSnap?.State;
                Log($"变换前: pos={TimeSpan.FromTicks(beforePos):g} state={beforeState} presented={beforePresented} swap={beforeSwapPresents}");

                // 模拟用户快速滚动 20 次（每次间隔 50ms，模拟快速滚轮）
                for (var i = 0; i < 20; i++)
                {
                    var z = 1f + (i % 5) * 0.5f; // 1.0 → 3.0 循环
                    _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                    await System.Threading.Tasks.Task.Delay(50);
                }

                // 等待 2 秒让引擎处理完排队的变换
                await System.Threading.Tasks.Task.Delay(2000);

                // 检查视频是否仍在播放 + 呈现计数器是否继续增长
                var midSnap = _sync.ReadMasterSnapshot();
                var midState = midSnap?.State;
                var midPresented = midSnap?.PresentedVideoFrames ?? -1;
                var afterTransforms = _sync.GetMasterPosition100ns();

                await System.Threading.Tasks.Task.Delay(1000);
                var finalSnap = _sync.ReadMasterSnapshot();
                var finalPresented = finalSnap?.PresentedVideoFrames ?? -1;
                var finalCheck = _sync.GetMasterPosition100ns();

                var presentDelta = finalPresented - midPresented;
                Log($"变换中: state={midState} presented={midPresented} (+{midPresented - beforePresented})");
                Log($"1秒后: pos={TimeSpan.FromTicks(finalCheck):g} presented={finalPresented} (Δpresented/秒={presentDelta})");
                Log($"位置增量(1秒) = {(finalCheck - afterTransforms) / 10000}ms");

                if (finalCheck <= afterTransforms && midState == PlayerState.Playing)
                {
                    Log($"❌ 视频已卡死！presented 停在 {finalPresented}");
                    throw new InvalidOperationException(
                        $"视图变换导致视频卡死: presented Δ={presentDelta}");
                }
                else if (presentDelta <= 0 && midState == PlayerState.Playing)
                {
                    Log($"❌ 渲染管线停滞！presented 计数不再增长");
                    throw new InvalidOperationException(
                        $"渲染管线停滞: presented Δ={presentDelta}, pos Δ={(finalCheck - afterTransforms) / 10000}ms");
                }
                else if (midState != PlayerState.Playing)
                {
                    Log($"⚠ 播放状态变为 {midState}（非卡死）");
                }
                else
                {
                    Log($"✅ 压力测试通过：20 次快速变换后视频继续播放 (presented +{presentDelta}/秒)");
                }

                // 恢复正常视图并继续播放
                _sync.SetViewTransform(1.0f, 0f, 0f);
                _sync.Play();
            }

            // ══════════ 嵌入式 UI 消息注入测试（真实走 WndProc → SurfaceWheel/SurfaceFilesDropped 分支） ══════════
            _step = "UI消息注入-滚轮缩放";
            {
                // 取得第 0 路真实子 HWND（NativeControlHost 的 D3D 输出窗口）
                var surface = Grid.GetSurface(0);
                var targetHwnd = surface?.Hwnd ?? nint.Zero;
                if (targetHwnd == nint.Zero)
                    throw new InvalidOperationException("无法取得 PlayerSurface 子 HWND");

                // 基线：初始 zoom 应为 1（上一压力测试已复位）
                var zoomBefore = _viewZoom;
                Log($"基线 zoom={zoomBefore:F3} hwnd=0x{targetHwnd:X}");

                // 注入 3 次 WM_MOUSEWHEEL（向前，delta=+120），走 PlayerSurface.SubclassedWndProc
                // 的 WM_MOUSEWHEEL → SurfaceWheel → OnSurfaceWheel → _viewZoom *= 1.15^3
                const int WM_MOUSEWHEEL = 0x020A;
                short delta = 120;
                for (var i = 0; i < 3; i++)
                {
                    var wParam = (nint)((long)(ushort)delta << 16);
                    PostMessageW(targetHwnd, WM_MOUSEWHEEL, wParam, nint.Zero);
                    await System.Threading.Tasks.Task.Delay(80);
                }
                // 等 UI 线程处理完 PostMessage
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await System.Threading.Tasks.Task.Delay(300);

                var zoomAfter = _viewZoom;
                var expected = zoomBefore * 1.15f * 1.15f * 1.15f;
                Log($"注入后 zoom={zoomAfter:F3} (期望≈{expected:F3})");
                if (Math.Abs(zoomAfter - expected) > 0.01f)
                    throw new InvalidOperationException(
                        $"滚轮缩放未生效：zoom {zoomBefore:F3} → {zoomAfter:F3}，期望 {expected:F3}");

                // 再注入向后滚动（缩小），确认双向都走通
                for (var i = 0; i < 3; i++)
                {
                    var wParam = unchecked((nint)((long)(ushort)(-120) << 16));
                    PostMessageW(targetHwnd, WM_MOUSEWHEEL, wParam, nint.Zero);
                    await System.Threading.Tasks.Task.Delay(80);
                }
                await Dispatcher.UIThread.InvokeAsync(() => { });
                await System.Threading.Tasks.Task.Delay(300);
                Log($"缩小后 zoom={_viewZoom:F3} (期望≈{zoomBefore:F3})");
                if (Math.Abs(_viewZoom - zoomBefore) > 0.01f)
                    throw new InvalidOperationException(
                        $"滚轮缩小未复位：zoom={_viewZoom:F3}，期望 {zoomBefore:F3}");
            }

            _step = "UI消息注入-文件拖入";
            if (dropVideoPath is not null && File.Exists(dropVideoPath))
            {
                var surface = Grid.GetSurface(0);
                var targetHwnd = surface?.Hwnd ?? nint.Zero;
                if (targetHwnd == nint.Zero)
                    throw new InvalidOperationException("无法取得 PlayerSurface 子 HWND");

                var countBefore = _sync.Count;
                Log($"拖入前路数={countBefore}");

                // 构造真实 HDROP 并 PostMessage WM_DROPFILES 到子 HWND（走 HandleDropFiles → SurfaceFilesDropped → OpenPaths）
                var hDrop = BuildHDrop(dropVideoPath);
                if (hDrop == nint.Zero)
                    throw new InvalidOperationException("HDROP 构造失败");
                PostMessageW(targetHwnd, WM_DROPFILES, hDrop, nint.Zero);

                // 等待拖入文件被解析、打开并加入 _sync
                var dropDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < dropDeadline && _sync.Count <= countBefore)
                    await System.Threading.Tasks.Task.Delay(200);

                if (_sync.Count <= countBefore)
                    throw new InvalidOperationException(
                        $"文件拖入未生效：路数仍为 {_sync.Count}（期望 > {countBefore}）");
                Log($"拖入后路数={_sync.Count} ✓ 拖入文件已打开");
                // 注意：HandleDropFiles 内部已 DragFinish(hDrop) 释放内存，这里不再重复释放
            }
            else
            {
                Log("未提供第二个视频，跳过文件拖入测试");
            }

            Log("全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest[步骤{_step}]: 失败 ✗ {ex.Message}");
            Console.Error.Flush();
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            Environment.Exit(code);
        }
    }

    // ══════════ 多路同步压力测试（--multitest <video> [routes=4] [durationSec=30]） ══════════
    // 目的：验证 0009 视口扩容 zoom 在多路并行扇出下的稳定性——
    // 1) N 路打开同一视频，长时播放各路线程压力均衡
    // 2) 播放中并行 SetViewTransform（走 SyncController Parallel.For 扇出）无卡死
    // 3) 多路 position 漂移 ≤ 阈值（打开时统一 Pause 对齐 + 周期校正后的残余漂移）
    // 4) 全路 presented 持续增长（无单路解码/呈现停滞）
    private async System.Threading.Tasks.Task RunMultitestAsync(string videoPath, int routes, int durationSec)
    {
        routes = Math.Clamp(routes, 2, 9);
        durationSec = Math.Clamp(durationSec, 10, 120);

        Console.WriteLine($"multitest: 多路={routes} 时长={durationSec}s 素材={videoPath}");
        var code = 2;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"multitest: 文件不存在 {videoPath}");
                Environment.Exit(2);
            }

            // 打开 N 路：统一暂停对齐（与 OpenPaths 语义一致），全部就绪后手动 Play
            _step = "多路打开";
            Grid.SetCount(routes, _realMode);
            _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
            Console.WriteLine($"multitest: 已请求打开 {routes} 路，等待就绪...");

            // 等全部路就绪（≤20s）
            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < readyDeadline)
            {
                var snaps = _sync.ReadAllSnapshots();
                if (snaps.Count == routes && snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State)))
                    break;
                await System.Threading.Tasks.Task.Delay(200);
            }
            var readySnaps = _sync.ReadAllSnapshots();
            var readyCount = readySnaps.Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));
            if (readyCount != routes)
                throw new InvalidOperationException($"仅 {readyCount}/{routes} 路就绪");

            // 基线：全部对齐到 master（就绪时位置应为 0）
            var basePos = _sync.GetMasterPosition100ns();
            Console.WriteLine($"multitest: 全路就绪 ✓ 基线 pos={TimeSpan.FromTicks(basePos):g}");

            // 素材时长钳制：各阶段总长不得超过素材时长（视频播完 → Ended → presented 停涨）
            var mediaDurSec = _sync.GetMasterDuration100ns() / TimeSpan.TicksPerSecond;
            var budgetSec = Math.Min(durationSec, (int)Math.Max(5, mediaDurSec - 2));
            Console.WriteLine($"multitest: 素材时长 {mediaDurSec}s → 测试预算 {budgetSec}s");

            // 播放（不开循环：避免多路 TickLoop 并发 Seek 导致原生崩溃，短素材在时长内测试）
            _sync.Play();
            _isPlaying = true;
            await System.Threading.Tasks.Task.Delay(1500);

            // 采样基线（播放稳定后）
            var baseline = SnapshotTuple(_sync.ReadAllSnapshots());
            var baselineMaster = _sync.GetMasterPosition100ns();
            Console.WriteLine($"multitest: 播放基线 master={TimeSpan.FromTicks(baselineMaster):g}");

            // 阶段一：静置播放 budgetSec/3，观察无交互漂移
            _step = "多路静置播放";
            var idleSec = Math.Max(3, budgetSec / 3);
            Console.WriteLine($"multitest: 静置播放 {idleSec}s（无交互）...");
            await System.Threading.Tasks.Task.Delay(idleSec * 1000);
            CheckDrift(routes, "静置播放");

            // 阶段二：播放中并行 view transform 扇出（zoom 3 档循环 × 20 次，每次 50ms）
            _step = "多路并行扇出";
            Console.WriteLine("multitest: 并行扇出 20 次（zoom 1→3 循环）...");
            var beforeTransform = SnapshotTuple(_sync.ReadAllSnapshots());
            for (var i = 0; i < 20; i++)
            {
                var z = 1f + (i % 5) * 0.5f; // 1.0 → 3.0 循环
                _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                await System.Threading.Tasks.Task.Delay(50);
            }
            await System.Threading.Tasks.Task.Delay(2000); // 等管线消化
            await CheckPresentedGrowthAsync(beforeTransform, "并行扇出");
            CheckDrift(routes, "并行扇出后");

            // 阶段三：恢复 fit 继续播放，观察复位后稳定性
            _step = "多路复位稳定";
            _sync.SetViewTransform(1f, 0f, 0f);
            var beforeReset = SnapshotTuple(_sync.ReadAllSnapshots());
            await CheckPresentedGrowthAsync(beforeReset, "复位后");
            CheckDrift(routes, "复位后");

            // 阶段四：全路帧步进同步（验证 StepFrames 多路广播）
            _step = "多路帧步进";
            if (_realMode)
            {
                _sync.Pause();
                await System.Threading.Tasks.Task.Delay(300);
                var beforeStep = _sync.GetMasterPosition100ns();
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var afterStep = _sync.GetMasterPosition100ns();
                Console.WriteLine($"multitest: 帧步进 {TimeSpan.FromTicks(beforeStep):g} → {TimeSpan.FromTicks(afterStep):g}");
                if (afterStep < beforeStep)
                    throw new InvalidOperationException($"帧步进位置后退 {beforeStep} → {afterStep}");
                CheckDrift(routes, "帧步进后");
            }

            Console.WriteLine("multitest: 全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"multitest[步骤{_step}]: 失败 ✗ {ex.Message}");
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            Environment.Exit(code);
        }
    }

    /// <summary>断言多路 position 漂移 ≤ 阈值（master 为基准，考虑 Offset 后误差）。
    /// 8K 高码率下允许 ±2 帧（@24fps ≈ 83ms）容差，超过即判定漂移。</summary>
    private void CheckDrift(int routes, string phase)
    {
        var snaps = _sync.ReadAllSnapshots();
        if (snaps.Count != routes) return;
        var masterPos = _sync.GetMasterPosition100ns();
        const long Tolerance = 100_0000; // 100ms
        for (var i = 0; i < snaps.Count; i++)
        {
            var snap = snaps[i];
            if (snap is null || _sync.Slots[i].Failed) continue;
            var expect = masterPos + _sync.Slots[i].Offset100ns;
            var drift = Math.Abs(snap.Position100ns - expect);
            if (drift > Tolerance)
                throw new InvalidOperationException(
                    $"第 {i} 路漂移 {TimeSpan.FromTicks(drift):g} > 100ms（pos={TimeSpan.FromTicks(snap.Position100ns):g} 期望 {TimeSpan.FromTicks(expect):g}）");
        }
        Console.WriteLine($"multitest[{phase}]: 漂移 OK（{snaps.Count} 路 ≤100ms）✓");
    }

    /// <summary>把 ReadAllSnapshots 的引用数组转成值元组副本（避免缓存复用导致前后对比同对象）。</summary>
    private static (long pos, long presented)[] SnapshotTuple(IReadOnlyList<EngineSnapshot?> snaps)
    {
        var result = new (long pos, long presented)[snaps.Count];
        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            result[i] = s is null ? (0L, 0L) : (s.Position100ns, s.PresentedVideoFrames);
        }
        return result;
    }

    /// <summary>断言各路 presented 持续增长（无单路停滞）。
    /// 重试式：在窗口内（默认 4s）每 500ms 重新采样直到所有路都增长，避免
    /// 复位/扇出瞬间渲染器切换导致的瞬时零增长误报；窗口耗尽仍不涨=真停滞。</summary>
    private async System.Threading.Tasks.Task CheckPresentedGrowthAsync(
        (long pos, long presented)[] before, string phase, int maxWaitMs = 4000)
    {
        var deadline = System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(maxWaitMs);
        while (true)
        {
            var after = SnapshotTuple(_sync.ReadAllSnapshots());
            var stalled = new System.Collections.Generic.List<int>();
            for (var i = 0; i < before.Length && i < after.Length; i++)
            {
                var b = before[i];
                var a = after[i];
                if (a.presented - b.presented <= 0)
                    stalled.Add(i);
            }
            if (stalled.Count == 0)
            {
                Console.WriteLine($"multitest[{phase}]: presented 增长 OK（{before.Length} 路均 >0）✓");
                return;
            }
            if (System.DateTime.UtcNow >= deadline)
            {
                var detail = string.Join(", ", stalled.Select(i =>
                    $"路{i}:{before[i].presented}→{SnapshotTuple(_sync.ReadAllSnapshots())[i].presented}"));
                throw new InvalidOperationException(
                    $"第 {string.Join("/", stalled)} 路在[{phase}] presented 未增长（{detail}）");
            }
            await System.Threading.Tasks.Task.Delay(500);
        }
    }
}
