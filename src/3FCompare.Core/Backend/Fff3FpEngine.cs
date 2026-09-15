using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Display;

namespace _3FCompare.Core.Backend;

/// <summary>3FP 后端适配器（基于 fork 的 FFF.Native，MIT）。</summary>
public sealed class Fff3FpEngine : IPlayerEngine
{
    // 内核 2026.9.11 合并（f25c28f）起 PlayerApiVersion=14（FFF3FP_Create 严格校验）。
    private const uint ConfigVersion = 14;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
    {
        // 计划：扩展补丁 `FFF3FP_EnumerateAdapters`（03 §6 / A11）。
        // 当前无 API 时仅报告“系统默认”，保证冒烟可运行。
        return new[] { new AdapterInfo { Index = -1, Description = "System Default (D3D11)", DedicatedMemoryBytes = 0 } };
    }

    public IPlayerSession CreateSession(EngineSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configSize = Marshal.SizeOf<Fff3FpConfiguration>();
        var session = new Fff3FpSession(options);
        var config = new Fff3FpConfiguration
        {
            Size = (uint)configSize,
            Version = ConfigVersion,
            OutputWindow = options.OutputWindow,
            DecodeMode = (uint)(options.HardwareDecode ? FffDecodeMode.Gpu : FffDecodeMode.Cpu),
            ColorMode = (uint)(FffColorMode)options.ColorMode,
            // SdrPeakNits 和 SdrPaperWhiteNits 留到运行时调用 SetColorMode 时智能设置
            SdrPeakNits = 100f,  // 占位值，后续会被智能参数覆盖
            HdrPeakNits = 0f,    // 0=自动（由3FP内部ResolveTargetPeak处理）
            SdrPaperWhiteNits = 203f,  // 占位值，后续会被智能参数覆盖
            AudioEndpointIdUtf8 = 0,
            EventCallback = session.Callback,
            EventCallbackContext = session.CallbackContext,
            VideoScalingQuality = 1, // HighQuality
            ForceHdrOutput = options.ForceHdrOutput ? 1u : 0u, // v12：强制尝试 scRGB HDR 链
        };

        var result = Fff3FpNative.FFF3FP_Create(in config, out var handle);
        if (result != FffResult.Success)
        {
            session.Dispose();
            throw new EngineException((int)result, $"FFF3FP_Create 失败: {result}");
        }

        session.AttachHandle(handle);

        // 创建会话后立即设置智能参数与呈现节奏。
        // 任一环节抛异常都必须销毁已创建的原生句柄：session 持有**强** GCHandle
        // （原生侧保存的是 GetFunctionPointerForDelegate 的函数指针，需要 session 存活来
        // 保住委托，见 Fff3FpSession 构造），因此漏 Dispose = 原生句柄 + 整个会话图
        // 永久泄漏，且没有终结器兜底。
        try
        {
            session.ApplyInitialColorMode(options.ColorMode, options.OutputWindow, options.ForceHdrOutput);
            if (options.TearingPresent)
                session.SetPresentConfig(true); // 不支持时静默保持 VSync（返回值忽略）
            if (options.PacingEnabled)
                session.SetPacingConfig(true);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    private sealed class Fff3FpSession : IPlayerSession
    {
        private readonly EngineSessionOptions _options;
        private nint _handle;
        /// <summary>销毁状态（0=活着 1=已销毁），兼作"只销毁一次"的原子守卫。
        /// 原先用一个普通 bool 做判断-置位，那不是原子操作：两个线程可以同时通过判断
        /// 并各自调用一次 FFF3FP_Destroy（double-free）。</summary>
        private int _disposedFlag;
        /// <summary>原生临界区：FFF3FP_Destroy 与所有原生调用互斥。
        /// 轮询线程每 16ms 调 ReadSnapshot，而 RemoveSlotAt 刻意把 Dispose 放在 _gate 锁外，
        /// 二者可以并发 ⇒ 拿着正在销毁的句柄进 GetSnapshot 就是 use-after-free。</summary>
        private readonly object _nativeGate = new();

        /// <summary>创建会话时的输出窗口（用于运行时重新读取显示器能力）。</summary>
        private nint _outputWindow;

        // ---- 事件回调（原生工作线程调用，__cdecl）----
        private readonly Fff3FpEventCallback _callback;
        private readonly GCHandle _callbackContext; // 防回调委托被 GC
        internal nint Callback => Marshal.GetFunctionPointerForDelegate(_callback);
        internal nint CallbackContext => GCHandle.ToIntPtr(_callbackContext);

        /// <summary>引擎事件（原生线程触发；消费方应调度到 UI 线程）。</summary>
        public event EventHandler<EngineEvent>? EngineEvent;

        internal Fff3FpSession(EngineSessionOptions options)
        {
            _options = options;
            _outputWindow = options.OutputWindow;
            _callback = OnEngineEvent;
            // 必须是 **强** 句柄（GCHandleType.Normal）。
            // 原生侧保存的是 Marshal.GetFunctionPointerForDelegate(_callback) 的函数指针；
            // 一旦 session 被 GC，委托随之回收，那个函数指针就变成悬空指针，
            // 原生工作线程再回调就是调用已释放的 thunk ⇒ 进程崩溃。
            // 强句柄通过保住 session 间接保住委托，是此设计的必要条件，不是泄漏源。
            // 真正的泄漏风险在"未走 Dispose 的路径"——见 CreateSession 的异常分支。
            _callbackContext = GCHandle.Alloc(this);
        }

        internal void AttachHandle(nint handle) => _handle = handle;

        private void OnEngineEvent(nint contextPtr, uint eventType, nint detailJsonUtf8)
        {
            // 校验 context 仍是本会话（防御性）
            try
            {
                if (GCHandle.FromIntPtr(contextPtr).Target is not Fff3FpSession) return;
            }
            catch
            {
                return; // 已释放/非法句柄，静默忽略
            }

            var json = detailJsonUtf8 == 0
                ? string.Empty
                : Marshal.PtrToStringUTF8(detailJsonUtf8) ?? string.Empty;

            try
            {
                EngineEvent?.Invoke(this, new EngineEvent((EngineEventType)eventType, json));
            }
            catch
            {
                // 回调内不抛异常回原生层
            }
        }

        public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(localPath);
            ThrowIfDisposed();
            InvalidateMediaInfoCache();

            // 简化版打开：同步调用原生 Open，异步包装（上游托管层用异步线程 + 取消队列；骨架阶段先同步）。
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Fff3FpNative.FFF3FP_Open(_handle, localPath);
                if (result != FffResult.Success)
                    throw new EngineException((int)result, $"FFF3FP_Open 失败: {result} ({LastError()})");
            }, cancellationToken);
        }

        public void Play()
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_Play(_handle), nameof(Play));
        }

        public void Pause()
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_Pause(_handle), nameof(Pause));
        }

        public void Stop()
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_Stop(_handle), nameof(Stop));
        }

        public void Seek(long position100ns)
        {
            ThrowIfDisposed();
            // 同步校正线程会并发 Seek，同样必须与 Dispose 互斥（理由同 ReadSnapshot）
            lock (_nativeGate)
            {
                ThrowIfDisposed();
                Check(Fff3FpNative.FFF3FP_Seek(_handle, position100ns), nameof(Seek));
            }
        }

        public void SeekFrame(long frameIndex)
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_SeekFrame(_handle, frameIndex), nameof(SeekFrame));
        }

        public void StepFrame(int direction)
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_StepFrame(_handle, direction), nameof(StepFrame));
        }

        public void SelectAudioStream(int streamIndex)
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_SelectAudioStream(_handle, streamIndex), nameof(SelectAudioStream));
        }

        public void SelectVideoStream(int streamIndex)
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_SelectVideoStream(_handle, streamIndex), nameof(SelectVideoStream));
        }

        public void SetVolume(float volume, bool muted)
        {
            ThrowIfDisposed();
            Check(Fff3FpNative.FFF3FP_SetVolume(_handle, volume, muted ? 1u : 0u), nameof(SetVolume));
        }

        /// <summary>设置色彩模式（运行时切换 HDR/SDR）。</summary>
        /// <param name="contentIsHdr">内容是否为 HDR。为 null 时按本会话媒体信息自行判定；
        /// <b>多路对比时调用方必须传入统一值</b>——否则同一源的两路只要有一路 HDR 元数据丢失，
        /// 就会走不同的色调映射曲线，对比结果不再公平（P1-8）。</param>
        public void SetColorMode(ColorMode mode, bool? contentIsHdr = null)
        {
            ThrowIfDisposed();
            ApplyColorMode(mode, contentIsHdr, _options.ForceHdrOutput);
        }

        /// <summary>创建会话后应用初始色彩模式。
        /// 与 <see cref="SetColorMode"/> 共用同一实现（P1-7：过去这里是两份几乎重复的代码，
        /// 且对 NotSupported 的处理不一致）。额外职责：记录输出窗口并让显示器能力缓存失效。</summary>
        internal void ApplyInitialColorMode(ColorMode mode, nint outputWindow, bool forceHdrOutput)
        {
            // 更新输出窗口引用（CreateSession 时传入，运行时不变但保留以防后续扩展）
            _outputWindow = outputWindow;
            // 输出窗口变化时清除显示器能力缓存
            _cachedDisplayCaps = null;
            ApplyColorMode(mode, null, forceHdrOutput);
        }

        /// <summary>色调映射的<b>唯一</b>实现：显示器能力 + 内容 HDR 状态 → 计算参数 → 下发内核。</summary>
        private void ApplyColorMode(ColorMode mode, bool? contentIsHdr, bool forceHdrOutput)
        {
            // 使用会话缓存的显示器能力，避免重复 DXGI 枚举
            var displayCapabilities = GetDisplayCapabilities();
            var isHdr = contentIsHdr ?? (ReadMediaInfo()?.IsHdr ?? false);
            var config = ToneMappingParameters.Calculate(mode, displayCapabilities, isHdr);

            var result = Fff3FpNative.FFF3FP_SetColorMode(_handle, (uint)mode, config.SdrPeakNits,
                config.HdrPeakNits, config.PaperWhiteNits, forceHdrOutput ? 1u : 0u);
            // NotSupported 视为软失败：旧内核或某些模式不支持设置色调映射参数，
            // 不应让整个色彩模式切换中断（两份旧实现对此处理不一致，此处统一）。
            if (result != FffResult.Success && result != FffResult.NotSupported)
            {
                throw new EngineException((int)result, $"SetColorMode 失败: {result}");
            }
        }

        /// <summary>设置呈现节奏（内核扩展：VRR/G-SYNC 低延迟路径）。
        /// 返回 false 表示显示器链不支持撕裂（已静默保持 VSync 锁定）。</summary>
        public bool SetPresentConfig(bool tearing)
        {
            ThrowIfDisposed();
            var result = Fff3FpNative.FFF3FP_SetPresentConfig(_handle, tearing ? 1u : 0u);
            return result == FffResult.Success;
        }

        /// <summary>媒体率呈现节奏（内核扩展 A9）：抑制叠加层固定周期重翻转，
        /// 使呈现节奏跟随源视频帧率。返回 false 表示不支持。</summary>
        public bool SetPacingConfig(bool pacing)
        {
            ThrowIfDisposed();
            var result = Fff3FpNative.FFF3FP_SetPacingConfig(_handle, pacing ? 1u : 0u);
            return result == FffResult.Success;
        }

        public void SetViewTransform(float zoom, float panX, float panY)
        {
            ThrowIfDisposed();
            var result = Fff3FpNative.FFF3FP_SetViewTransform(_handle, zoom, panX, panY);
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"SetViewTransform 失败: {result}");
        }

        public EngineSnapshot ReadSnapshot()
        {
            ThrowIfDisposed();
            var snapshotSize = Marshal.SizeOf<Fff3FpSnapshot>();
            var snap = new Fff3FpSnapshot { Size = (uint)snapshotSize, Version = 8 };
            // ReadSnapshot 是轮询线程每 16ms 一次的热路径，而 Dispose 在 _gate 锁外执行，
            // 二者必须互斥，否则会拿着正在销毁的句柄进 GetSnapshot（use-after-free）。
            // 进锁后要复查：等锁期间 Dispose 可能已经跑完。
            lock (_nativeGate)
            {
                ThrowIfDisposed();
                Check(Fff3FpNative.FFF3FP_GetSnapshot(_handle, ref snap), $"GetSnapshot(size={snapshotSize})");
            }
            return new EngineSnapshot
            {
                Position100ns = snap.Position100ns,
                Duration100ns = snap.Duration100ns,
                FrameIndex = snap.FrameIndex,
                RawFramePts = snap.FramePts,
                FrameTimeBaseNum = snap.FrameTimeBaseNumerator,
                FrameTimeBaseDen = snap.FrameTimeBaseDenominator,
                // 媒体帧率：来自媒体信息 nominalFrameRate（缓存命中时零开销）。
                // 快照的 frameTimeBase 是流时间基（如 1/15360），不是帧率，勿混用。
                FrameRate = _cachedMediaInfo?.FrameRate ?? 0,
                Decoder = (int)snap.Decoder,
                ActualColorMode = snap.ActualColorMode,
                State = (PlayerState)snap.State,
                PresentedVideoFrames = (long)snap.PresentedVideoFrames,
                SwapChainPresents = (long)snap.SwapChainPresents,
                TimelineGeneration = snap.TimelineGeneration,
            };
        }

private EngineMediaInfo? _cachedMediaInfo;
	        private DisplayLuminanceCapabilities? _cachedDisplayCaps;
	        private DateTime _cachedDisplayCapsAt;
	        private static readonly TimeSpan _displayCapsTtl = TimeSpan.FromSeconds(5);

	        /// <summary>获取显示器能力（5s TTL 缓存：与 DisplayCapabilities 静态缓存对齐，
	        /// 用户中途开关 Windows HDR 后最多 5s 感知，不再会话级永不过期）。</summary>
	        private DisplayLuminanceCapabilities? GetDisplayCapabilities()
	        {
	            if (_cachedDisplayCaps is not null &&
	                DateTime.UtcNow - _cachedDisplayCapsAt < _displayCapsTtl)
	                return _cachedDisplayCaps;
	            _cachedDisplayCaps = _outputWindow != 0
	                ? DisplayCapabilities.ReadForWindow(_outputWindow) : null;
	            _cachedDisplayCapsAt = DateTime.UtcNow;
	            return _cachedDisplayCaps;
	        }

	        /// <summary>读取媒体信息（结果缓存：媒体元数据在 Open 后不变，无需每次 P/Invoke + JSON 解析）。
    /// 引擎未就绪时返回 null 且不写入缓存，避免换片间隙读到旧数据。</summary>
        public EngineMediaInfo? ReadMediaInfo()
        {
            ThrowIfDisposed();
            if (_cachedMediaInfo is not null) return _cachedMediaInfo;

            // 引擎未就绪时不缓存，避免跨越换片窗口读到旧数据
            try
            {
                var snap = ReadSnapshot();
                var state = snap.State;
                if (state is not (PlayerState.Ready or PlayerState.Playing or PlayerState.Paused))
                    return null;
            }
            catch
            {
                return null;
            }
            var required = 0u;
            var result = Fff3FpNative.FFF3FP_GetMediaInfo(_handle, 0, 0, out required);
            if (result == FffResult.BufferTooSmall && required > 0 && required <= 4 * 1024 * 1024)
            {
                var buffer = new byte[required];
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        result = Fff3FpNative.FFF3FP_GetMediaInfo(_handle, (nint)p, required, out _);
                        if (result == FffResult.Success)
                        {
                            var json = DecodeNulTerminatedUtf8(buffer);
                            _cachedMediaInfo = ParseMediaInfoJson(json);
                            return _cachedMediaInfo;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>清除媒体信息缓存（Open 新文件后调用）。</summary>
        public void InvalidateMediaInfoCache() => _cachedMediaInfo = null;

        private static string DecodeNulTerminatedUtf8(byte[] buffer)
        {
            var idx = Array.IndexOf(buffer, (byte)0);
            var len = idx < 0 ? buffer.Length : idx;
            return Encoding.UTF8.GetString(buffer, 0, len);
        }

        /// <summary>解析 3FP GetMediaInfo 的嵌套 JSON（英文驼峰字段，见诊断 dump）。</summary>
        private static EngineMediaInfo? ParseMediaInfoJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 顶层非 streams 字段处理
                var path = GetString(root, "path") ?? GetString(root, "filename") ?? string.Empty;

                // 视频流字段（streams[])
                var video = default(System.Text.Json.JsonElement);
                var hasVideo = false;
                if (root.TryGetProperty("streams", out var streams) &&
                    streams.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (string.Equals(GetString(s, "type"), "video", StringComparison.OrdinalIgnoreCase))
                        {
                            video = s;
                            hasVideo = true;
                            break;
                        }
                    }
                }

                int width = 0, height = 0;
                double fps = 0;
                string? codec = null;
                bool isHdr = false;
                bool isLossless = false;
                int bitDepth = 8;
                string? pixelFormat = null;
                string? chroma = null;
                string? colorPrimaries = null;
                string? colorTransfer = null;
                string? colorSpace = null;
                string? hdrFormat = null;
                bool interlaced = false;
                bool fieldOrder = false;
                long frameCount = 0;
                string? audioCodec = null;
                int audioChannels = 0;
                int audioSampleRate = 0;

                if (hasVideo)
                {
                    width = GetInt(video, "width") ?? 0;
                    height = GetInt(video, "height") ?? 0;
                    codec = GetString(video, "codec") ?? GetString(video, "codec_name") ?? "unknown";

                    // 帧率：优先 nominalFrameRateNum/Den，其次 averageFrameRate*。
                    // 防御：时间基类数值（如 15360）混入 fps 位时钳制到合理范围。
                    var fpsNum = GetInt(video, "nominalFrameRateNumerator") ?? GetInt(video, "averageFrameRateNumerator") ?? 0;
                    var fpsDen = GetInt(video, "nominalFrameRateDenominator") ?? GetInt(video, "averageFrameRateDenominator") ?? 1;
                    if (fpsNum > 0 && fpsDen > 0)
                    {
                        fps = (double)fpsNum / fpsDen;
                        if (fps is < 1.0 or > 480.0) fps = 0; // 异常值视为未知
                    }

                    isHdr = GetBool(video, "hdr") ||
                            string.Equals(GetString(video, "hdrFormat"), "HDR10", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(GetString(video, "hdrFormat"), "HDR10+", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(GetString(video, "hdrFormat"), "HLG", StringComparison.OrdinalIgnoreCase);

                    // 扩展字段
                    isLossless = GetBool(video, "lossless");
                    bitDepth = GetInt(video, "decoderBitDepth") ?? GetInt(video, "bitDepth") ?? 8;
                    pixelFormat = GetString(video, "pixelFormat") ?? GetString(video, "decoderPixelFormat");
                    chroma = GetString(video, "chromaSubsampling");
                    colorPrimaries = GetString(video, "colorPrimaries");
                    colorTransfer = GetString(video, "colorTransfer");
                    colorSpace = GetString(video, "colorSpace");
                    hdrFormat = GetString(video, "hdrFormat");
                    frameCount = GetLong(video, "frames") ?? 0;
                    var fo = GetInt(video, "fieldOrder") ?? 0;
                    interlaced = fo is -1 or > 0;
                    _ = fieldOrder;
                }

                // 音频流
                if (root.TryGetProperty("streams", out var streams2) &&
                    streams2.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var s in streams2.EnumerateArray())
                    {
                        if (string.Equals(GetString(s, "type"), "audio", StringComparison.OrdinalIgnoreCase))
                        {
                            audioCodec = GetString(s, "codec") ?? GetString(s, "codec_name");
                            audioChannels = GetInt(s, "channels") ?? GetInt(s, "channelCount") ?? 0;
                            audioSampleRate = GetInt(s, "sampleRate") ?? GetInt(s, "sample_rate") ?? 0;
                            break;
                        }
                    }
                }

                return new EngineMediaInfo
                {
                    Path = path,
                    VideoWidth = width,
                    VideoHeight = height,
                    FrameRate = fps,
                    Codec = codec ?? "unknown",
                    IsHdr = isHdr,
                    Format = GetString(root, "format") ?? GetString(root, "formatLongName"),
                    Duration100ns = GetLong(root, "duration100ns") ?? 0,
                    BitRate = GetLong(root, "bitRate") ?? 0,
                    FileSize = GetLong(root, "fileSize") ?? 0,
                    IsLossless = isLossless,
                    BitDepth = bitDepth,
                    PixelFormat = pixelFormat,
                    ChromaSubsampling = chroma,
                    ColorPrimaries = colorPrimaries,
                    ColorTransfer = colorTransfer,
                    ColorSpace = colorSpace,
                    HdrFormat = hdrFormat,
                    Interlaced = interlaced,
                    FrameCount = frameCount,
                    AudioCodec = audioCodec,
                    AudioChannels = audioChannels,
                    AudioSampleRate = audioSampleRate,
                };
            }
            catch
            {
                return null;
            }
        }

        private static long? GetLong(System.Text.Json.JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? (long?)v.GetInt64() : null;

        private static int? GetInt(System.Text.Json.JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? (int?)v.GetInt32() : null;

        private static string? GetString(System.Text.Json.JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;

        private static double? GetDouble(System.Text.Json.JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => v.GetDouble(),
                System.Text.Json.JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
                _ => null,
            };
        }

        private static bool GetBool(System.Text.Json.JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && (v.ValueKind == System.Text.Json.JsonValueKind.True ||
               (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.GetInt32() == 1) ||
               (v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() == "true"));

        public bool TryReadPixel(int x, int y, out PixelSample sample)
        {
            ThrowIfDisposed();
            var probe = new Fff3FpVideoPixelProbe
            {
                Size = (uint)Marshal.SizeOf<Fff3FpVideoPixelProbe>(),
                Version = 1,
                X = (uint)Math.Max(0, x),
                Y = (uint)Math.Max(0, y),
            };
            var result = Fff3FpNative.FFF3FP_ReadVideoPixel(_handle, ref probe);
            if (result != FffResult.Success)
            {
                sample = default;
                return false;
            }
            sample = new PixelSample(probe.Red, probe.Green, probe.Blue, probe.Alpha, probe.OutputBitDepth);
            return true;
        }

        /// <summary>3FCompare patch (0004)：单次 GPU staging 拷贝读取整块区域。</summary>
        public bool TryReadPixelRegion(int x, int y, int width, int height,
            float[] buffer, out uint outputBitDepth)
        {
            ThrowIfDisposed();
            outputBitDepth = 0;
            // 必须按 long 比较：width * height * 4 是 **int** 运算，
            // 极端尺寸下会溢出成负数（如 65536×65536×4 溢出为 0），从而绕过长度校验，
            // 而原生侧会按 width×height 实际写入 ⇒ 托管堆越界写（不可 catch）。
            if (width <= 0 || height <= 0 || (long)width * height * 4 > buffer.Length)
                return false;
            var result = Fff3FpNative.FFF3FP_ReadVideoPixelRegion(_handle,
                (uint)Math.Max(0, x), (uint)Math.Max(0, y),
                (uint)width, (uint)height, buffer, (uint)buffer.Length, out var bits);
            if (result != FffResult.Success)
                return false;
            outputBitDepth = bits;
            return true;
        }

        /// <summary>3FCompare K5：请求 presenter 线程重绘/执行挂起的 resize。</summary>
        public void Redraw()
        {
            ThrowIfDisposed();
            Fff3FpNative.FFF3FP_Redraw(_handle);
        }

        /// <summary>3FCompare K4：读取 swap/client/dest 尺寸诊断信息。</summary>
        public bool ReadRenderTargetInfo(out RenderTargetInfo info)
        {
            ThrowIfDisposed();
            var rtInfo = new Fff3FpRenderTargetInfo
            {
                Size = (uint)Marshal.SizeOf<Fff3FpRenderTargetInfo>(),
                Version = 1,
            };
            var result = Fff3FpNative.FFF3FP_GetRenderTargetInfo(_handle, ref rtInfo);
            if (result != FffResult.Success)
            {
                info = default;
                return false;
            }
            info = new RenderTargetInfo(
                rtInfo.SwapWidth, rtInfo.SwapHeight,
                rtInfo.ClientWidth, rtInfo.ClientHeight,
                rtInfo.DestX, rtInfo.DestY, rtInfo.DestWidth, rtInfo.DestHeight,
                rtInfo.OutputBitDepth, rtInfo.Hdr != 0);
            return true;
        }

        public void Dispose()
        {
            // 原子守卫：并发 Dispose 只有一个线程能进入销毁路径，杜绝 double-free
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

            EngineEvent = null; // 脱离回调，防止释放后仍在触发
            lock (_nativeGate)
            {
                if (_handle != 0)
                {
                    var handle = _handle;
                    // 先置 0 再 Destroy：即使此刻有线程正准备发起别的原生调用，
                    // 它取到的也是 0 而不是一个即将无效的句柄。
                    _handle = 0;
                    Fff3FpNative.FFF3FP_Destroy(handle);
                }
            }
            if (_callbackContext.IsAllocated)
                _callbackContext.Free();

            GC.SuppressFinalize(this);
        }

        private string LastError()
        {
            var required = 0u;
            var result = Fff3FpNative.FFF3FP_GetLastError(_handle, 0, 0, out required);
            if (result != FffResult.BufferTooSmall || required == 0) return string.Empty;
            var buffer = new byte[required];
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    Fff3FpNative.FFF3FP_GetLastError(_handle, (nint)p, required, out _);
                    var len = buffer.AsSpan().IndexOf((byte)0);
                    return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
                }
            }
        }

        private static void Check(FffResult result, string op)
        {
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"{op} 失败: {result}");
        }

        // 用 _disposedFlag（Dispose 的第一步就置位，且是原子写）而不是 _disposed（普通 bool，
        // 写入的可见性无保证）。这样"已决定销毁"能在 Destroy 之前就被其它线程看到。
        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
    }
}