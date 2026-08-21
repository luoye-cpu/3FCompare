using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Display;

namespace _3FCompare.Core.Backend;

/// <summary>3FP 后端适配器（基于 fork 的 FFF.Native，MIT）。</summary>
public sealed class Fff3FpEngine : IPlayerEngine
{
    private const uint ConfigVersion = 11;

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
        };

        var result = Fff3FpNative.FFF3FP_Create(in config, out var handle);
        if (result != FffResult.Success)
        {
            session.Dispose();
            throw new EngineException((int)result, $"FFF3FP_Create 失败: {result}");
        }

        session.AttachHandle(handle);

        // 创建会话后立即设置智能参数
        session.ApplyToneMappingParameters(options.ColorMode, options.OutputWindow);

        return session;
    }

    private sealed class Fff3FpSession : IPlayerSession
    {
        private readonly EngineSessionOptions _options;
        private nint _handle;
        private bool _disposed;

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
            _callbackContext = GCHandle.Alloc(this);
        }

        internal void AttachHandle(nint handle) => _handle = handle;

        private void OnEngineEvent(nint contextPtr, uint eventType, nint detailJsonUtf8)
        {
            // 校验 context 仍是本会话（防御性）
            if (GCHandle.FromIntPtr(contextPtr).Target is not Fff3FpSession) return;

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
            Check(Fff3FpNative.FFF3FP_Seek(_handle, position100ns), nameof(Seek));
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

        /// <summary>设置色彩模式（运行时切换 HDR/SDR）。
        /// 使用智能参数计算（ToneMappingParameters），避免固定的 100 nits 导致 BT.2390 曲线失效。</summary>
        public void SetColorMode(ColorMode mode)
        {
            ThrowIfDisposed();

            // 获取当前媒体信息，判断是否为 HDR 内容
            var mediaInfo = ReadMediaInfo();
            var contentIsHdr = mediaInfo?.IsHdr ?? false;

            // 智能计算参数（与创建会话时一致：从输出窗口读取真实显示器能力）
            var displayCapabilities = _outputWindow != 0
                ? DisplayCapabilities.ReadForWindow(_outputWindow)
                : null;
            var config = ToneMappingParameters.Calculate(mode, displayCapabilities, contentIsHdr);

            var result = Fff3FpNative.FFF3FP_SetColorMode(_handle, (uint)mode, config.SdrPeakNits, config.HdrPeakNits, config.PaperWhiteNits);
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"SetColorMode 失败: {result}");
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
            Check(Fff3FpNative.FFF3FP_GetSnapshot(_handle, ref snap), $"GetSnapshot(size={snapshotSize})");
            return new EngineSnapshot
            {
                Position100ns = snap.Position100ns,
                Duration100ns = snap.Duration100ns,
                FrameIndex = snap.FrameIndex,
                RawFramePts = snap.FramePts,
                FrameTimeBaseNum = snap.FrameTimeBaseNumerator,
                FrameTimeBaseDen = snap.FrameTimeBaseDenominator,
                Decoder = (int)snap.Decoder,
                ActualColorMode = snap.ActualColorMode,
                State = (PlayerState)snap.State,
            };
        }

        public EngineMediaInfo? ReadMediaInfo()
        {
            ThrowIfDisposed();
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
                            return ParseMediaInfoJson(json);
                        }
                    }
                }
            }
            return null;
        }

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

                    // 帧率：优先 nominalFrameRateNum/Den，其次 averageFrameRate*
                    var fpsNum = GetInt(video, "nominalFrameRateNumerator") ?? GetInt(video, "averageFrameRateNumerator") ?? 0;
                    var fpsDen = GetInt(video, "nominalFrameRateDenominator") ?? GetInt(video, "averageFrameRateDenominator") ?? 1;
                    if (fpsNum > 0 && fpsDen > 0) fps = (double)fpsNum / fpsDen;

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

        /// <summary>应用智能色调映射参数（调用3FP SetColorMode）。</summary>
        public void ApplyToneMappingParameters(ColorMode colorMode, nint outputWindow)
        {
            // 获取显示器能力
            var displayCapabilities = DisplayCapabilities.ReadForWindow(outputWindow);

            // 获取当前媒体信息，判断是否为 HDR 内容
            var mediaInfo = ReadMediaInfo();
            var contentIsHdr = mediaInfo?.IsHdr ?? false;

            // 智能计算参数
            var config = ToneMappingParameters.Calculate(colorMode, displayCapabilities, contentIsHdr);

            // 调用3FP的SetColorMode API
            var result = Fff3FpNative.FFF3FP_SetColorMode(_handle, (uint)colorMode, config.SdrPeakNits, config.HdrPeakNits, config.PaperWhiteNits);
            if (result != FffResult.Success && result != FffResult.NotSupported)
            {
                // 忽略 NotSupported（3FP可能不支持某些模式）
                throw new EngineException((int)result, $"SetColorMode 失败: {result}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            EngineEvent = null; // 脱离回调，防止释放后仍在触发
            if (_handle != 0)
                Fff3FpNative.FFF3FP_Destroy(_handle);
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

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
}