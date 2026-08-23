using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare.Services;

/// <summary>多路打开编排器（WinForms MainForm.OpenFiles / OpenSlotAsync /
/// WaitForOpenCompletionAsync / TryAutoPlayAfterOpen / HandleEngineEvent 的移植）。
///
/// 关键时序（真实模式）：FFF3FP_Open 仅把 DoOpen 入队即返回 Success，OpenAsync 完成
/// 时后端仍是 Opening，此阶段 Play() 得 InvalidState；必须轮询快照至
/// Ready/Playing/Paused（≤15s）再由 TryAutoPlayAfterOpen 统一启动播放（首帧渲染契约）。</summary>
public sealed class PlaybackCoordinator
{
    private readonly IPlayerEngine _engine;
    private readonly SyncController _sync;
    private readonly AppSettings _settings;
    private readonly bool _realMode;
    private readonly Func<int, PlayerSurface?> _surfaceAt;

    private int _pendingAutoPlay;
    private readonly Queue<Action> _onAllOpenedCallbacks = new();
    private bool _closed;

    public PlaybackCoordinator(IPlayerEngine engine, SyncController sync, AppSettings settings,
        Func<int, PlayerSurface?> surfaceAt)
    {
        _engine = engine;
        _sync = sync;
        _settings = settings;
        _surfaceAt = surfaceAt;
        _realMode = engine is Fff3FpEngine;
    }

    public bool RealMode => _realMode;
    public SyncController Sync => _sync;

    /// <summary>状态变化通知（失败路变化/事件到达等，UI 据此刷新状态栏）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>打开文件（≤9 路总量钳制）。autoPlay：全部就绪后统一 Play；
    /// onAllOpened：播放前回调队列（会话恢复 Seek 等）。</summary>
    public async void OpenFiles(IReadOnlyList<string> files, bool autoPlay = false, Action? onAllOpened = null)
    {
        var count = Math.Min(files.Count, 9 - _sync.Count);
        if (count <= 0)
        {
            onAllOpened?.Invoke();
            return;
        }
        if (autoPlay) _pendingAutoPlay += count;
        if (onAllOpened is not null) _onAllOpenedCallbacks.Enqueue(onAllOpened);

        for (var i = 0; i < count; i++)
        {
            var path = files[i];
            var surface = _surfaceAt(_sync.Count);
            if (surface is null) continue;
            surface.FileName = Path.GetFileName(path);
            surface.IsFailed = false;
            surface.ErrorText = string.Empty;

            try
            {
                // 真实模式需要子 HWND 作为输出窗口：等待 NativeControlHost 附件创建
                nint hwnd = 0;
                if (_realMode)
                {
                    hwnd = await surface.EnsureHwndAsync();
                    if (hwnd == nint.Zero)
                        throw new InvalidOperationException("输出窗口 HWND 未创建");
                }

                // 解析 Auto 色彩模式：根据显示器能力自动选择 HDR/SDR
                var resolvedColorMode = _3FCompare.Core.Settings.ColorModeHelper.Resolve(
                    _settings.ColorMode,
                    hwnd != 0 ? _3FCompare.Core.Display.DisplayCapabilities.ReadForWindow(hwnd) : null);
                var session = _engine.CreateSession(new EngineSessionOptions
                {
                    OutputWindow = hwnd,
                    HardwareDecode = _settings.HardwareDecode,
                    PreferredAdapterIndex = _settings.PreferredAdapterIndex,
                    ColorMode = resolvedColorMode,
                    TearingPresent = _settings.VrrTearingPresent,
                    PacingEnabled = _settings.VrrPacingEnabled,
                });
                surface.AttachSession(session);
                _sync.AddSlot(session, path);

                _ = OpenSlotAsync(_sync.Slots[^1], surface, path);
            }
            catch (Exception ex)
            {
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenSlotAsync(SyncController.SyncSlot slot, PlayerSurface surface, string path)
    {
        try
        {
            // 引擎事件（原生工作线程）→ UI 线程
            slot.Session.EngineEvent += (_, evt) =>
            {
                if (_closed) return;
                try { Dispatcher.UIThread.Post(() => HandleEngineEvent(slot, surface, evt)); }
                catch { /* 窗口已关闭 */ }
            };

            await slot.Session.OpenAsync(path);
            if (!_closed)
            {
                surface.FileName = Path.GetFileName(path);
                if (_realMode)
                    await WaitForOpenCompletionAsync(slot, surface);
                // 播放中拖入新视频：同步到主时间轴当前位置
                if (!slot.Failed && _sync.Count > 1)
                {
                    var masterPos = _sync.GetMasterPosition100ns();
                    if (masterPos > 0)
                        slot.Session.Seek(masterPos + slot.Offset100ns);
                }
                TryAutoPlayAfterOpen();
            }
        }
        catch (Exception ex)
        {
            slot.Failed = true;
            slot.Error = ex.Message;
            if (!_closed)
            {
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
                TryAutoPlayAfterOpen(); // 失败路也计入完成，避免卡住
            }
            else if (_pendingAutoPlay > 0)
            {
                _pendingAutoPlay = 0;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>真实模式等待 3FP 后端真正就绪；失败/超时(15s)也视为完成（标记失败）。</summary>
    private async Task WaitForOpenCompletionAsync(SyncController.SyncSlot slot, PlayerSurface surface)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !_closed)
        {
            try
            {
                var snap = slot.Session.ReadSnapshot();
                if (IsReadyState(snap.State)) return;
                if (snap.State == PlayerState.Failed)
                {
                    slot.Failed = true;
                    slot.Error = "内核打开失败";
                    surface.IsFailed = true;
                    surface.ErrorText = slot.Error;
                    return;
                }
            }
            catch
            {
                // 快照读取失败：继续等
            }
            await Task.Delay(100);
        }
        if (!_closed)
        {
            slot.Failed = true;
            slot.Error = "打开超时（后端未就绪）";
            surface.IsFailed = true;
            surface.ErrorText = slot.Error;
        }
    }

    /// <summary>全部就绪后：先跑恢复回调，再统一 Play（跳过 Failed 槽）。</summary>
    private void TryAutoPlayAfterOpen()
    {
        if (_pendingAutoPlay <= 0) return;
        if (--_pendingAutoPlay > 0) return;

        while (_onAllOpenedCallbacks.Count > 0)
            _onAllOpenedCallbacks.Dequeue().Invoke();

        _sync.Play();
    }

    private void HandleEngineEvent(SyncController.SyncSlot slot, PlayerSurface surface, EngineEvent evt)
    {
        switch (evt.Type)
        {
            case EngineEventType.Error:
                if (evt.DetailJson.Contains("\"state\":6", StringComparison.OrdinalIgnoreCase)
                    || evt.DetailJson.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    slot.Failed = true;
                    slot.Error = $"内核错误: {evt.DetailJson}";
                    surface.IsFailed = true;
                    surface.ErrorText = slot.Error;
                }
                break;
            case EngineEventType.PlaybackEnded:
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public static bool IsReadyState(PlayerState state)
        => state is PlayerState.Ready or PlayerState.Playing or PlayerState.Paused;

    public void Close()
    {
        _closed = true;
        _pendingAutoPlay = 0;
        _onAllOpenedCallbacks.Clear();
    }
}
