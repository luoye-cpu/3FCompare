namespace _3FCompare.Core.Backend;

/// <summary>演示模式后端：当真实 FFF.Native DLL 不可用时自动启用。
/// 生成合成测试画面（渐变 + 帧号 + 时间码），完整支持 播放/暂停/Seek/步进/探针，
/// 使 UI 全流程可在无后端环境下演示与开发。</summary>
public sealed class SimulatedEngine : IPlayerEngine
{
    public const string ModeName = "Simulated (演示)";

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
        => new[] { new AdapterInfo { Index = -1, Description = "System Default (Simulated)", DedicatedMemoryBytes = 0 } };

    public IPlayerSession CreateSession(EngineSessionOptions options)
        => new SimSession(options);

    private sealed class SimSession : IPlayerSession
    {
        private readonly EngineSessionOptions _options;
        private readonly long _duration100ns = 10 * TimeSpan.TicksPerSecond; // 10秒
        private readonly double _fps = 24.0;
        private readonly int _hue; // 每路不同色相
        private static int s_nextHue;

        /// <summary>引擎事件（演示模式为基础状态变更模拟；原生线程无，安全）。</summary>
        public event EventHandler<EngineEvent>? EngineEvent;

        private long _position100ns;
        private long _frameIndex;
        private bool _playing;
        private bool _opened;
        /// <summary>销毁标志（0=活着 1=已销毁）。
        /// 原先是普通 bool：Dispose 可能被 UI 关闭与播放器线程同时调用，
        /// "判断-置位"不是原子操作 ⇒ 两条线程都能通过判断（虽然后果只是重复置 null，
        /// 但 Dispose 语义应当是"只有一个线程执行销毁"）。改用 Interlocked 与真实引擎对齐。</summary>
        private int _disposedFlag;
        private string _path = string.Empty;
        private readonly object _lock = new();
        private DateTime _lastTick;
        private readonly object _tickLock = new();

        public SimSession(EngineSessionOptions options)
        {
            _options = options;
            _hue = Interlocked.Add(ref s_nextHue, 37) % 360;
        }

        public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                _path = localPath;
                _position100ns = 0;
                _frameIndex = 0;
                _opened = true;
                _playing = false;
            }
            EngineEvent?.Invoke(this, new EngineEvent(EngineEventType.OpenCompleted, "{\"success\":true}"));
            return Task.CompletedTask;
        }

        public void Play()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (!_opened) return;
                if (!_playing)
                {
                    _playing = true;
                    _lastTick = DateTime.UtcNow;
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                _playing = false;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                _playing = false;
                _position100ns = 0;
                _frameIndex = 0;
            }
        }

        public void Seek(long position100ns)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (!_opened) return;
                _position100ns = Math.Clamp(position100ns, 0, _duration100ns);
                _frameIndex = (long)(_position100ns / (TimeSpan.TicksPerSecond / _fps));
            }
        }

        public void SeekFrame(long frameIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (!_opened) return;
                _frameIndex = Math.Max(0, frameIndex);
                _position100ns = (long)(_frameIndex * (TimeSpan.TicksPerSecond / _fps));
            }
        }

        public void StepFrame(int direction)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (!_opened) return;
                _playing = false;
                // 先按 long 累加再钳制：direction 是 int，若直接放进 int 上下文
                // （例如极端值 int.MinValue）会溢出成符号翻转的帧号；long 累加不会。
                var maxFrame = (long)(_duration100ns / (TimeSpan.TicksPerSecond / _fps));
                _frameIndex = Math.Clamp(_frameIndex + direction, 0L, maxFrame);
                _position100ns = (long)(_frameIndex * (TimeSpan.TicksPerSecond / _fps));
            }
        }

        public void SelectAudioStream(int streamIndex) { /* 演示模式无音轨 */ }

        public void SelectVideoStream(int streamIndex) { /* 演示模式单视频流 */ }

        public void SetVolume(float volume, bool muted) { /* 演示模式无音频 */ }

        public void SetColorMode(ColorMode mode, bool? contentIsHdr = null) { /* 演示模式略过 */ }

        public bool SetPresentConfig(bool tearing) => !tearing; // 演示模式无呈现链，仅"关闭"语义成立

        public void SetViewTransform(float zoom, float panX, float panY) { /* 演示模式略过 */ }

        public EngineSnapshot ReadSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (_playing) AdvanceClockLocked();
                return new EngineSnapshot
                {
                    Position100ns = _position100ns,
                    Duration100ns = _duration100ns,
                    FrameIndex = _frameIndex,
                    RawFramePts = _position100ns,
                    FrameTimeBaseNum = 1,
                    FrameTimeBaseDen = (int)Math.Round(_fps),
                    Decoder = 1,
                    ActualColorMode = (uint)_options.ColorMode,
                    State = _opened ? (_playing ? PlayerState.Playing : PlayerState.Paused) : PlayerState.Idle,
                    PresentedVideoFrames = _frameIndex,
                    SwapChainPresents = _frameIndex,
                };
            }
        }

        public EngineMediaInfo? ReadMediaInfo()
        {
            // 与其余成员一致补 disposed 校验：会话释放后 UI 侧的轮询仍可能来问一次，
            // 此前只有本方法不抛 ObjectDisposedException，出错时表现为"读到一个凭空的 1920×1080"。
            ThrowIfDisposed();
            if (!_opened) return null;
            return new()
            {
                Path = _path,
                VideoWidth = 1920,
                VideoHeight = 1080,
                FrameRate = _fps,
                Codec = "simulated",
                IsHdr = false,
            };
        }

        public bool TryReadPixel(int x, int y, out PixelSample sample)
        {
            sample = new PixelSample(0.5f, 0.5f, 0.5f, 1f, 8);
            return true;
        }

        public bool TryReadPixelRegion(int x, int y, int width, int height,
            float[] buffer, out uint outputBitDepth)
        {
            outputBitDepth = 8;
            // 与真实引擎（Fff3FpEngine.TryReadPixelRegion）完全一致的两道校验：
            // ① 尺寸必须为正；② 按 long 比较长度 —— `width * height * 4` 是 **int** 运算，
            //    极端尺寸下会溢出成负数（如 65536×65536×4 溢出为 0），负数的 `buffer.Length < 负数`
            //    恒为 false ⇒ 校验被绕过，随后却按 width×height 真正写入 ⇒ 托管堆越界写。
            if (width <= 0 || height <= 0 || (long)width * height * 4 > buffer.Length) return false;
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                buffer[i] = 0.5f;
                buffer[i + 1] = 0.5f;
                buffer[i + 2] = 0.5f;
                buffer[i + 3] = 1f;
            }
            return true;
        }

        public void Redraw() { /* 演示模式无渲染链 */ }

        public bool ReadRenderTargetInfo(out RenderTargetInfo info)
        {
            info = default;
            return false;
        }

        public void Dispose()
        {
            // 原子守卫：并发 Dispose 只有一个线程真正执行销毁（与 Fff3FpEngine 对齐）
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
            EngineEvent = null; // 脱离回调，防止释放后仍在触发
            GC.SuppressFinalize(this);
        }

        private void AdvanceClockLocked()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastTick).TotalSeconds;
            if (elapsed <= 0) return;
            _lastTick = now;
            _position100ns += (long)(elapsed * TimeSpan.TicksPerSecond);
            if (_position100ns >= _duration100ns)
            {
                _position100ns = 0; // 循环
            }
            _frameIndex = (long)(_position100ns / (TimeSpan.TicksPerSecond / _fps));
        }

        // 用 Volatile.Read 读 int 标志而不是普通 bool：Dispose 可能来自另一条线程，
        // 非 volatile 的 bool 写入不保证对其它线程可见 ⇒ 可能"销毁了还在用"。
        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
    }
}