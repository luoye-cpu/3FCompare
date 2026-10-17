using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Display;

namespace _3FCompare.Core.Backend;

/// <summary>3FP 后端适配器（基于 fork 的 FFF.Native，MIT）。</summary>
public sealed class Fff3FpEngine : IPlayerEngine
{
    // 内核 PlayerApiVersion。FFF3FP_Create 用 `version != PlayerApiVersion` **严格相等**校验，
    // 且要求 `size >= sizeof(FFF3FPConfiguration)` ⇒ 内核与托管侧必须同步递增，否则全部会话创建失败。
    //   14 = 内核 2026.9.11 合并（f25c28f）起
    //   15 = 3FCompare A11 扩展：新增 FFF3FPConfiguration.preferredAdapterIndex（2026-09-16）
    //   16 = 3FCompare 扩展：新增 FFF3FPConfiguration.sdrScRgbMode（2026-09-29）
    //   17 = 新增 FFF3FPSnapshot 尾部的 ST 2094 动态元数据诊断字段（HDR10+）
    //   18 = FFF3FPSnapshot 新增 IAMF 空间音频字段（iamfActive/iamfChannels/iamfSoundSystem/
    //        iamfContentChannels），FFF3FP_Create 的 version 门随内核从 17 bump 到 18；
    //        不改这里，内核 PlayerApi.cpp 的 `version != PlayerApiVersion` 严格相等门会让
    //        所有会话创建失败（返回 InvalidArgument）。
    //   19 = FFF3FPSnapshot 尾追加 iamfBinaural（IAMF 双耳渲染模式），内核
    //        PlayerApiVersion 再 bump 一级，同步到此处，理由同上。
    //   20 = FFF3FPSnapshot 尾追加 audioBinaural（非 IAMF 多声道床双耳渲染），
    //        内核 PlayerApiVersion 再 bump 一级，同步到此处，理由同上。
    // internal：EngineFactory 的版本门必须与这里同源（内核对 version 严格相等），
    // 两处各写一个字面量迟早会漂移（docs/45 P1-1）。
    internal const uint ConfigVersion = 20;

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
            // A11：把用户在设置里选的解码 GPU 真正交给内核（此前该字段一直被丢弃）。
            // -1 = 跟随窗口所在显示器；≥0 = DXGI 适配器索引（与 GpuEnumeration 的 DXGI 枚举同源）。
            PreferredAdapterIndex = options.PreferredAdapterIndex,
            // v16：SDR 源是否经 scRGB 链呈现（0=从不，1=自动）。内核在 FFF3FP_Create
            // 里对 &gt;1 返回 InvalidArgument，故这里钳到 [0,1] 而不是直接透传。
            SdrScRgbMode = (uint)Math.Clamp(options.SdrScRgbMode, 0, 1),
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
            if (options.RtxVideoSuperResolution)
                // 只是"登记请求"：内核每帧自己判定要不要用（见 FFF3FP_SetVideoSuperResolution）。
                // 旧盘上 DLL 没有这个入口时，SetVideoSuperResolution 内部记一条 warn 返回 false，
                // 不抛 ⇒ 不会把一次会话创建变成崩溃（返回值同样忽略）。
                session.SetVideoSuperResolution(true);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    // internal 而非 private：docs/41 §4.5 #2 要把本类里的 ParseMediaInfoJson 纳入单测，
    // 而 private 的宿主类型会把它的 internal 成员一起挡在程序集之外
    //（IVT 只放开 internal，放开不了 private 嵌套类型）—— 只改方法可见性是够不到的。
    internal sealed class Fff3FpSession : IPlayerSession, IFrameSideDataRing
    {
        private readonly EngineSessionOptions _options;
        private nint _handle;
        /// <summary>帧边数据环导出的可用性闩（1=盘上 DLL 没有这个导出）。
        /// 见 <see cref="CopyFrameSideDataAfter"/>：老内核上这是恒定答案，
        /// 闩一次就把每帧一次的异常与 P/Invoke 省掉。</summary>
        private int _sideDataRingUnavailable;

        // ---- 可选外部色彩变换通路（kind=3）----
        // 模块缺席 / 未解锁 / 旧件 / 被篡改 ⇒ 下面两个字段恒为 null ⇒ 每帧什么都不做，
        // 与"从来没这回事"逐位相同：内核零加载痕迹、UI 零提示、不抛异常（既定口径）。
        private ColorTransformModule? _colorModule;
        private ColorTransformPump? _colorPump;
        /// <summary>装载与起线程各只有一个人能做成，靠这两件守卫：
        /// <paramref name="_colorLoadGate"/> 罩住"探测到已解锁 → 建模块 → 建泵"整段，
        /// <paramref name="_pumpThreadStarted"/> 是 0/1 的原子闩。
        /// 需要它们的理由：<see cref="StartColorTransformPump"/> 有两个调用者
        /// （建会话的那条路，和解锁成功后 <c>Task.Run</c> 里补装载的那条路），
        /// 重试线程也会走 <see cref="TryLoadModuleNow"/>；没有守卫时两边可以同时建成，
        /// 后一份直接盖掉 <c>_colorModule</c> ⇒ 前一枚模块的 pinned 句柄与 FreeLibrary
        /// 永不再发生（泄漏一整块 431 KB + 一个原生映射），而前一条泵线程还在跑。</summary>
        private readonly Lock _colorLoadGate = new();
        private int _pumpThreadStarted;
        /// <summary>宿主自己的动态元数据表（文件菜单载入的 HDR10+/Vivid JSON）。
        /// <para>注入是<b>整表替换</b>，所以本泵每次注入都要把这张表一并重发——
        /// 只发 kind=3 那一条，等于把用户从菜单载入的表悄悄清空（<see cref="ColorTransformPump"/>）。</para></summary>
        private IReadOnlyList<DynamicMetadataPacket>? _hostDynamicMetadata;
        /// <summary>上一次读到的呈现帧号。用来发现"内核把环清空了"这件事，
        /// 见 <see cref="NotePresentedFrameIndex"/>。</summary>
        private long _lastSnapshotFrameIndex = -1;
        private Thread? _pumpThread;
        private volatile bool _pumpThreadStop;
        private ManualResetEventSlim? _pumpWake;
        private bool _pumpErrorLogged;
        /// <summary>销毁状态（0=活着 1=已销毁），兼作"只销毁一次"的原子守卫。
        /// 原先用一个普通 bool 做判断-置位，那不是原子操作：两个线程可以同时通过判断
        /// 并各自调用一次 FFF3FP_Destroy（double-free）。</summary>
        private int _disposedFlag;
        /// <summary>原生临界区：FFF3FP_Destroy 与所有原生调用互斥。
        /// 轮询线程每 16ms 调 ReadSnapshot，而 RemoveSlotAt 刻意把 Dispose 放在 _gate 锁外，
        /// 二者可以并发 ⇒ 拿着正在销毁的句柄进 GetSnapshot 就是 use-after-free。
        ///
        /// ⚠ 必须用<b>读写锁</b>而不是普通 lock：
        /// FFF3FP_Open 是耗时调用（数百 ms～数秒，在线程池线程上执行）。若用独占 lock，
        /// 它持锁期间 UI 线程的轮询 ReadSnapshot 会全部阻塞在锁上；UI 线程一旦停止泵消息，
        /// 依赖窗口消息推进的原生流程就有被拖死的风险，且自测看门狗也投递在 UI 线程上，
        /// 会连带失效 ⇒ 从"卡住后退出"退化成"永久挂起"。
        /// 读写锁的语义：
        ///   · 读锁 = 绝大多数短小调用（快照/播放/定位/探针…）⇒ 彼此可并发，UI 线程不被挡；
        ///   · 写锁 = 仅 Dispose 的 Destroy ⇒ 独占，且会等所有在飞的读锁退出，
        ///     从而在"取句柄"与"销毁句柄"之间建立真正的互斥，消除 use-after-free。
        /// Open 走读锁：它不销毁句柄，与并发读快照的语义和加锁前完全一致。</summary>
        /// 递归策略选 SupportsRecursion 而非 NoRecursion：原实现是 <c>lock</c>（完全可重入），
        /// 若某条调用链上存在"持读锁时又进入另一个 WithNative"的嵌套路径（例如原生同步回调
        /// 里再发一次查询），NoRecursion 会直接抛 LockRecursionException —— 在未被测试覆盖
        /// 的路径上等于凭空引入一次崩溃。选可重入是为了**保持与加锁前相同的行为**，
        /// 代价只是极小的记账开销，远小于生产环境崩一次。
        private readonly ReaderWriterLockSlim _nativeGate =
            new(LockRecursionPolicy.SupportsRecursion);

        // ---- 结构尺寸（进程内恒定，静态缓存）----
        // 这三个尺寸只取决于结构布局，但 Marshal.SizeOf<T>() 每次调用都要再走一遍
        // 布局计算（反射式查表 + 缓存查找），而它们分别位于 ReadSnapshot（轮询线程
        // 每 16ms 一次）、TryReadPixel（放大镜/探针每次移动）等热路径上。
        // 提升为静态只读字段 ⇒ 进程内只算一次，且消除每次调用的额外分配。
        private static readonly int SnapshotSize = Marshal.SizeOf<Fff3FpSnapshot>();
        // 内核 GetSnapshot 对 version 是严格相等门（PlayerSession.cpp:2093：`version != N`
        // 直接 InvalidArgument，快照整体拒收）。IAMF 字段增长后门升到 9，尾追加
        // iamfBinaural 后内核再 bump 到 10，尾追加 audioBinaural（通用声道床双耳渲染）
        // 后再 bump 到 11；仍发旧值会让每次 ReadSnapshot 失败并落进
        // "尚未打开媒体"误报（内核 redo 轮缺陷 E 同款事故）。
        internal const uint SnapshotVersion = 20;
        private static readonly int PixelProbeSize = Marshal.SizeOf<Fff3FpVideoPixelProbe>();
        private static readonly int RenderTargetInfoSize = Marshal.SizeOf<Fff3FpRenderTargetInfo>();
        private static readonly int HdrSignalDiagnosticsSize = Marshal.SizeOf<Fff3FpHdrSignalDiagnostics>();
        private static readonly int VideoSuperResolutionStatusSize = Marshal.SizeOf<Fff3FpVideoSuperResolutionStatus>();

        /// <summary>GetSnapshot 的失败标签（内容恒定，进程内只算一次）。
        /// ReadSnapshot 是 16ms 一次的热路径：若像原来那样在<b>调用前</b>就把
        /// <c>$"GetSnapshot(size={...})"</c> 拼好，那么成功路径（绝大多数调用）
        /// 也要先付一次插值 + 一次 Marshal.SizeOf；改用预生成常量后，
        /// 成功路径不再有任何字符串分配。</summary>
        private static readonly string SnapshotOpLabel = $"GetSnapshot(size={SnapshotSize})";

        /// <summary>内核文本输出（媒体信息 JSON / 错误信息）的缓冲区上限。
        /// 内核回写的 requiredSize 来自它自己的内部结构，一旦内核与托管侧版本错配
        /// （或内核侧内存被踩），requiredSize 可能是个天文数字；直接 new byte[required]
        /// 会 OOM 或造成 2GB+ 的单次分配。4MB 对媒体信息 JSON 已绰绰有余。</summary>
        private const int MaxTextBufferBytes = 4 * 1024 * 1024;

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

        internal void AttachHandle(nint handle)
        {
            _handle = handle;
            // 拿到句柄才开这条通路：泵的每个原生调用都要走 WithNative，
            // 而句柄此刻才存在（AttachHandle 之前 _handle 还是 0）。
            StartColorTransformPump();
        }

        // ══════════════════════════════════════════════════════════════════
        // 可选外部色彩变换通路（kind=3）：装载、驱动、生命周期
        //
        // 本段是"模块接进运行时"的唯一落点。三条铁律：
        //   ① 模块缺席 / 未解锁 / 旧件 / 被篡改 ⇒ 全程 null，与没这条通路逐位相同；
        //   ② 任何一步都不许抛给调用方（装载、驱动、复位、释放各有异常边界）；
        //   ③ 不向 UI 表态：不弹窗、不改默认行为、不在状态栏提这件事。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>模块搜索目录的运行时覆盖。单一真源在
        /// <see cref="ColorTransformModule.DefaultDirectoryOverride"/>：
        /// 探测、装载、解锁三条路必须用同一个口径找同一枚 DLL，这里只做转调，
        /// 免得在两处各读一次环境变量而迟早分叉。</summary>
        private static string? ColorTransformDirectoryOverride()
            => ColorTransformModule.DefaultDirectoryOverride();

        private void StartColorTransformPump()
        {
            try
            {
                // 先问"模块在不在、锁没锁"，再决定要不要装载：
                // 只有"在且锁着"这一态才需要提示用户（见 ColorTransformModule.Probe 的注释）。
                var presence = ColorTransformModule.Probe(out var presenceReason,
                    ColorTransformDirectoryOverride());

                if (presence is ColorTransformModule.ColorTransformPresence.Absent
                    or ColorTransformModule.ColorTransformPresence.Legacy)
                {
                    // 盘上没有（绝大多数用户）/ 旧件（没有可解锁的路径）⇒ 静默，零提示。
                    if (!string.IsNullOrEmpty(presenceReason))
                        AppLog.Debug("ColorTransform", presenceReason);
                    return;
                }

                if (presence == ColorTransformModule.ColorTransformPresence.PresentLocked)
                {
                    // ── 唯一会打扰用户的分支 ──
                    // 模块在盘上、授权层可达、但没解锁 ⇒ 请宿主 UI 收一次解锁码。
                    // 铁律：① 只请求一次（进程级闩）；② 不 await（打开编排的关键路径上
                    // 不能等对话框）；③ UI 不在（自测/无人值守）⇒ 恒不请求。
                    AppLog.Info("ColorTransform",
                        "检测到色彩变换模块但未解锁 ⇒ 请求一次解锁码（取消则本次按基础层出画）");
                    RequestUnlockAsync();
                    // 用户可能在弹框里磨蹭，也可能切出去找码：起一条低频重试线程，
                    // 解锁成功后这条线程自己把泵建起来（无需广播给别的会话，见 PumpLoop）。
                    StartPumpThread();
                    return;
                }

                if (!ColorTransformModule.TryOpen(out var module, out var reason,
                        ColorTransformDirectoryOverride()))
                {
                    // 已解锁但握手没过（版本门 / ABI 容量不同水 / 被篡改）⇒ 按缺席静默，
                    // **不提示**：这类情况不是"输个码就能好"的，弹框只会误导用户。
                    if (!string.IsNullOrEmpty(reason))
                        AppLog.Debug("ColorTransform", reason);
                    return;
                }

                // 建模块/建泵这一步与重试线程、解锁回调共用同一把闸（见 _colorLoadGate）。
                if (!AdoptColorTransformModule(module)) return;
                // 逐帧驱动不能挂在 UI 轮询上：播放中的轮询是 **250ms/4Hz**
                // （MainWindow.Playback.cs 的自适应三档），而环只装 120 帧 ⇒
                // 60fps 素材下每秒产出 60 条、每秒只消费 4 条，环会一直溢出，
                // 游标停在被挤掉的旧帧号上 ⇒ 注入永久落后两秒（"静默停摆"的另一种形状）。
                // 所以泵自带一条后台线程，节拍按帧率的一半取（见 PumpIntervalMs）。
                StartPumpThread();
            }
            catch (Exception ex)
            {
                // 装载是可选增强：任何意外都按"没有这条通路"处理，绝不能把一个好会话弄坏。
                AppLog.Warn("ColorTransform",
                    $"色彩变换模块装载失败，按缺席处理：{ex.GetType().Name}: {ex.Message}");
                _colorPump = null;
                try { _colorModule?.Dispose(); } catch { /* 释放失败不该盖住上面的原因 */ }
                _colorModule = null;
            }
        }

        /// <summary>请求一次解锁码（fire-and-forget）。成功后本会话立刻补装载。
        /// <para>为什么不等它：<see cref="StartColorTransformPump"/> 在
        /// <c>CreateSession</c> 的路径上，那条路是"打开 N 路"的循环，
        /// 同步等对话框会把打开变成串行卡住（自测/自动化则会被永久挂住）。</para></summary>
        private void RequestUnlockAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var unlocked = await ColorTransformUnlock.RequestOnceAsync().ConfigureAwait(false);
                    if (!unlocked) return;
                    // 票据已落盘：直接按已解锁重新走一遍装载（不必等重试线程）。
                    AppLog.Info("ColorTransform", "解锁成功 ⇒ 重新装载模块");
                    StartColorTransformPump();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("ColorTransform", $"解锁流程异常（已忽略）：{ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>"已解锁"之后把模块装载起来（供重试线程与解锁回调共用）。
        /// 成功返回 true；仍在锁着 / 缺席 / 握手不过都返回 false（静默）。</summary>
        private bool TryLoadModuleNow()
        {
            try
            {
                lock (_colorLoadGate)
                {
                    if (_colorPump is not null) return true;
                }
                if (ColorTransformModule.Probe(out _, ColorTransformDirectoryOverride())
                    != ColorTransformModule.ColorTransformPresence.Ready)
                    return false;
                if (!ColorTransformModule.TryOpen(out var module, out var reason,
                        ColorTransformDirectoryOverride()))
                {
                    if (!string.IsNullOrEmpty(reason)) AppLog.Debug("ColorTransform", reason);
                    return false;
                }
                return AdoptColorTransformModule(module);
            }
            catch (Exception ex)
            {
                AppLog.Debug("ColorTransform", $"补装载失败（下次重试）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>把一枚刚打开的模块收进本会话。**只有第一个调用者收得进去**；
        /// 后来者手里那枚多余的当场 Dispose，返回"已经有了"。</summary>
        private bool AdoptColorTransformModule(ColorTransformModule module)
        {
            lock (_colorLoadGate)
            {
                if (_colorPump is not null)
                {
                    // 另一个调用者（解锁回调 / 重试线程）先建成了。盖掉赋值会让前一枚模块
                    // 的 pinned 句柄和 FreeLibrary 永不再发生，所以是这枚多余的该死，不是那张泵。
                    try { module.Dispose(); }
                    catch { /* 释放失败不能改写"通路已在工作"这个事实 */ }
                    return true;
                }
                _colorModule = module;
                _colorPump = new ColorTransformPump(this, module)
                {
                    // 产品路径必须开：动态元数据在静止段里成百帧内容不变，重发是白付整表替换。
                    SkipUnchangedPayload = true,
                };
                AppLog.Info("ColorTransform", "模块已装载，kind=3 通路开始工作");
                return true;
            }
        }

        private void StartPumpThread()
        {
            // 一条会话只配一条线程：StartColorTransformPump 可能被调两次（建会话 + 解锁回调），
            // 第二次若重新起线程，前一条会被 _pumpThread 字段盖掉、无人再能叫它停。
            if (Interlocked.CompareExchange(ref _pumpThreadStarted, 1, 0) != 0) return;
            _pumpWake = new ManualResetEventSlim(false);
            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,           // 进程退出时不撑着：它是纯可选通路
                Name = "3fc-colorpump",
            };
            _pumpThread.Start();
        }

        /// <summary>泵的节拍：取"半帧"为间隔，钳到 [2,12] ms。
        /// <para>取半帧是为了在环被消费前不溢出（环 120 帧 = 60fps 下 2 秒的余量，
        /// 半帧节拍 ⇒ 消费速率是产出的两倍）；钳下界 2ms 是不让空转成 CPU 杀手，
        /// 钳上界 12ms 是让"帧率还没读到"的默认档也够快（默认 8ms ⇒ 125 Hz）。</para></summary>
        private int PumpIntervalMs()
        {
            var fps = _cachedMediaInfo?.FrameRate ?? 0;
            if (fps <= 0) return 8;
            var half = (int)(500.0 / fps);
            return Math.Clamp(half, 2, 12);
        }

        /// <summary>模块"在但锁着"时，本线程先干的是低频补装载而不是泵帧：
        /// 每 2 秒探一次，探到已解锁就建泵转入正常节拍。
        /// <para>为什么用重试线程而不是"解锁后广播给所有会话"：广播需要一张进程级会话表
        /// （登记/注销各一处，漏一处就是泄漏或漏装载），而这条线程只服务自己这一路，
        /// 生命周期与会话完全一致，没有任何跨会话状态。</para>
        /// <para>上限 30 次 ≈ 60 秒：给"切出去找解锁码"留够时间；超时后线程静默退出，
        /// 本次进程不再自愈（再开新会话时票据已在，会直接装载成功）。</para></summary>
        private const int LoadRetryIntervalMs = 2000;
        private const int MaxLoadRetries = 30;

        private void PumpLoop()
        {
            var wake = _pumpWake;
            // 只记一次：这条线是"泵真的在跑"与"泵只是被构造出来"的唯一切分点。
            // 本通路默认全程静默，没有它就无法从日志回答"这条通路到底起来了没有"。
            AppLog.Debug("ColorTransform", $"kind=3 泵线程已启动（节拍 {PumpIntervalMs()}ms）");
            var loadRetries = 0;
            while (!_pumpThreadStop)
            {
                if (_colorPump is null)
                {
                    // 还没装载（通常是"锁着、等用户输入解锁码"这一段）：低频补装载。
                    if (loadRetries++ >= MaxLoadRetries)
                    {
                        AppLog.Debug("ColorTransform",
                            $"补装载重试 {MaxLoadRetries} 次仍未成功（模块仍锁着或不在）⇒ 本会话放弃，保持静默");
                        return;
                    }
                    TryLoadModuleNow();
                    if (!WaitOrExit(wake, LoadRetryIntervalMs)) return;
                    continue;
                }

                try
                {
                    _colorPump.PumpOnce(out _);
                }
                catch (ObjectDisposedException)
                {
                    // 会话正在销毁：这是本线程的正常退场路径之一，不留痕。
                }
                catch (Exception ex)
                {
                    // 兜底：泵的任何异常都不能带倒后台线程（线程一死，通路就永久哑了，
                    // 且没有任何可见症状）。只记一次——逐帧记会把日志刷满。
                    if (!_pumpErrorLogged)
                    {
                        _pumpErrorLogged = true;
                        AppLog.Warn("ColorTransform",
                            $"kind=3 泵异常（已忽略后续同类）：{ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (!WaitOrExit(wake, PumpIntervalMs())) return;
            }
        }

        /// <summary>睡一拍；事件被释放（会话销毁）时返回 false 让线程退场。</summary>
        private static bool WaitOrExit(ManualResetEventSlim? wake, int milliseconds)
        {
            if (wake is null) return false;
            try { wake.Wait(milliseconds); return true; }
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>流状态重置：seek / 帧步进 / 停止 / 换文件 / 换轨之后必须调。
        /// <para>内核在这些点把边数据环整个丢掉、presentation index 从那里重来
        /// （<c>PlayerSession::ClearVideoQueue</c> → <c>ClearSideDataRing</c>），
        /// 游标不归零的表现不是崩，是"注入静默停摆"。</para>
        /// <para>只补有宿主信号的那一半；另一半（设备恢复、解码器线程数自适应、硬解回退）
        /// 内核自己发起、宿主无从得知，由 <see cref="NotePresentedFrameIndex"/> 兜住。</para></summary>
        private void OnColorTransformStreamRestart()
        {
            try { _colorPump?.OnStreamRestart(); }
            catch (Exception ex) { AppLog.Debug("ColorTransform", $"泵复位失败（已忽略）：{ex.Message}"); }
        }

        /// <summary>"环被内核清空"的自愈：presentation index 回退即环已重来。
        /// <para>为什么必须补这一道：清环的六个点里有三个（设备恢复、解码器线程数自适应、
        /// 硬解回退软解）是内核自己发起的，宿主拿不到任何信号；而漏掉复位会让游标停在
        /// 一个已经不存在的高帧号上，之后每一趟都答 NotFound ⇒ 通路永久哑火且无症状。
        /// 快照的 frameIndex 是唯一每帧都到宿主手里的证据，所以在这里补最后一刀。</para></summary>
        private void NotePresentedFrameIndex(long frameIndex)
        {
            var pump = _colorPump;
            if (pump is null) return;
            var previous = _lastSnapshotFrameIndex;
            // 无条件更新（含 -1 = 未打开/已关闭）：否则 -1 会连着好几拍都判定"回退"，
            // 变成每帧一次复位。
            _lastSnapshotFrameIndex = frameIndex;
            if (previous > 0 && frameIndex < previous) pump.OnStreamRestart();
        }

        /// <summary>一批包里是否含 kind=3（本泵自己的产出）。
        /// <para>用来分辨"这一批是谁发的"：宿主载入的 JSON 只可能是 HDR10+/Vivid
        /// （<c>DynamicMetadataFile</c> 的 kind 解析只认这两个），所以含 kind=3 ⇒ 本泵发的，
        /// 不能拿去当"宿主的表"记下来（否则会自我累积、越记越长）。</para></summary>
        internal static bool ContainsExternalTransform(IReadOnlyList<DynamicMetadataPacket> packets)
        {
            for (var i = 0; i < packets.Count; i++)
                if (packets[i].Kind == DynamicMetadataKind.ExternalColorTransform) return true;
            return false;
        }

        /// <summary>宿主清表之后，伴报表也要跟着掉（否则下一次注入会把已清掉的表又装回去）。
        /// <para>纯函数（可单测）：kind=None ⇒ 全清；否则只掉这一种，其余留下。</para></summary>
        internal static IReadOnlyList<DynamicMetadataPacket>? KeepExcept(
            IReadOnlyList<DynamicMetadataPacket>? current, DynamicMetadataKind kind)
        {
            if (current is null || current.Count == 0) return null;
            if (kind == DynamicMetadataKind.None) return null;
            List<DynamicMetadataPacket>? kept = null;
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i].Kind == kind) continue;
                (kept ??= new List<DynamicMetadataPacket>(current.Count)).Add(current[i]);
            }
            return kept;   // 全被清掉时返回 null（"宿主没有表"与"空表"同义）
        }

        // ------------------------------------------------------------------
        // 原生调用的统一入口（A1）
        // ------------------------------------------------------------------
        // 过去每个方法都是"先 ThrowIfDisposed() 再裸调 P/Invoke"，而 Dispose 的顺序是
        //   ① Interlocked.Exchange(_disposedFlag,1) → ② lock(_nativeGate) → ③ Destroy
        // （②③ 现已合并为"写锁内先置位再 Destroy"，见 Dispose 的 #9 注释）
        // 于是存在这样的窗口：线程 A 通过 ① 之后的标志位检查（此时还没到 ②），
        // 线程 B 走完 ③ 把句柄销毁，A 随后拿着已释放的句柄进原生 ⇒ 0xC0000005，
        // 托管侧不可 catch。必须把"取句柄"和"用句柄"放进同一个临界区，
        // 且标志位要在**持锁之后**再复查一次（等锁期间 Dispose 可能已经跑完）。
        //
        // 只包住 P/Invoke 本身：锁内不能有等待/回调/长时间计算，否则轮询线程会把
        // Dispose 挡在外面，反而制造卡顿。Open 这类耗时调用也只在线程池线程上持锁
        // 执行原生那一段，锁的范围不变大。
        private T WithNative<T>(Func<nint, T> call)
        {
            // 读锁：短小调用之间可并发。Open 也走这里（见 _nativeGate 注释）。
            _nativeGate.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                if (_handle == 0) throw new ObjectDisposedException(nameof(Fff3FpSession));
                return call(_handle);
            }
            finally
            {
                _nativeGate.ExitReadLock();
            }
        }

        private void WithNative(Action<nint> call)
        {
            _nativeGate.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                if (_handle == 0) throw new ObjectDisposedException(nameof(Fff3FpSession));
                call(_handle);
            }
            finally
            {
                _nativeGate.ExitReadLock();
            }
        }

        private void OnEngineEvent(nint contextPtr, uint eventType, nint detailJsonUtf8)
        {
            // 校验 context 仍是本会话（防御性）
            try
            {
                var handle = GCHandle.FromIntPtr(contextPtr);
                // 两道检查缺一不可：
                // ① IsAllocated —— 句柄已 Free 时 FromIntPtr 得到的 GCHandle 是悬空的，
                //    读 .Target 属于未定义行为（Dispose 后原生工作线程仍可能回调一次）。
                // ② ReferenceEquals —— 只判 "is Fff3FpSession" 挡不住另一个会话的 context
                //    （原生侧复用/错传指针时会把 A 的事件派发到 B）。
                if (!handle.IsAllocated) return;
                if (!ReferenceEquals(handle.Target, this)) return;
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
                // OpenAsync 跑在线程池线程上，同样必须与 Dispose 互斥：
                // 否则"打开文件的同时关闭这一路"会拿到已销毁句柄（use-after-free，不可 catch）。
                WithNative(h =>
                {
                    var result = Fff3FpNative.FFF3FP_Open(h, localPath);
                    if (result != FffResult.Success)
                        throw new EngineException((int)result, $"FFF3FP_Open 失败: {result} ({LastError(h)})");
                });
                // 换文件：presentation index 重来，环里若还留着**上一个文件**的条目，
                // 它们会被当成新流的小帧号选中 ⇒ 把上个文件的变换套到这个文件上。必须复位。
                OnColorTransformStreamRestart();
            }, cancellationToken);
        }

        public void Play()
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_Play(h), nameof(Play)));
        }

        public void Pause()
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_Pause(h), nameof(Pause)));
        }

        public void Stop()
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_Stop(h), nameof(Stop)));
            // 内核的 Stop 走 DoClose ⇒ 清环并把 frameIndex 置 -1。
            OnColorTransformStreamRestart();
        }

        public void Seek(long position100ns)
        {
            // 同步校正线程会并发 Seek，同样必须与 Dispose 互斥（理由同 ReadSnapshot）
            WithNative(h => Check(Fff3FpNative.FFF3FP_Seek(h, position100ns), nameof(Seek)));
            OnColorTransformStreamRestart();
        }

        public void SeekFrame(long frameIndex)
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_SeekFrame(h, frameIndex), nameof(SeekFrame)));
            OnColorTransformStreamRestart();
        }

        public void StepFrame(int direction)
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_StepFrame(h, direction), nameof(StepFrame)));
            // 帧步进同样走 DoStepKeyframe ⇒ 清环。
            OnColorTransformStreamRestart();
        }

        public void SelectAudioStream(int streamIndex)
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_SelectAudioStream(h, streamIndex), nameof(SelectAudioStream)));
        }

        public void SelectVideoStream(int streamIndex)
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_SelectVideoStream(h, streamIndex), nameof(SelectVideoStream)));
            // 换视频轨要重开解码器：环里留下的是上一条轨的帧号与元数据，一并复位。
            OnColorTransformStreamRestart();
        }

        public void SetVolume(float volume, bool muted)
        {
            WithNative(h => Check(Fff3FpNative.FFF3FP_SetVolume(h, volume, muted ? 1u : 0u), nameof(SetVolume)));
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

            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_SetColorMode(h, (uint)mode, config.SdrPeakNits,
                config.HdrPeakNits, config.PaperWhiteNits, forceHdrOutput ? 1u : 0u));
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
            return WithNative<FffResult>(h => Fff3FpNative.FFF3FP_SetPresentConfig(h, tearing ? 1u : 0u)) == FffResult.Success;
        }

        /// <summary>RTX VSR 请求（内核扩展）。返回 false 有两种含义，这里都要记下来：
        /// 入口缺失（旧盘上 DLL）或原生返回非 Success。<para>不抛异常——本功能默认关，
        /// 而且出货内核可能根本不含这个导出，抛出去会让一次会话创建失败变成崩溃。</para></summary>
        public bool SetVideoSuperResolution(bool enable)
        {
            try
            {
                return WithNative<FffResult>(h => Fff3FpNative.FFF3FP_SetVideoSuperResolution(
                    h, enable ? 1u : 0u)) == FffResult.Success;
            }
            catch (EntryPointNotFoundException)
            {
                AppLog.Warn("Fff3Fp", "内核不提供 FFF3FP_SetVideoSuperResolution（盘上 DLL 较旧），VSR 保持不可用");
                return false;
            }
        }

        public void SetViewTransform(float zoom, float panX, float panY)
        {
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_SetViewTransform(h, zoom, panX, panY));
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"SetViewTransform 失败: {result}");
        }

        public void SetFitLimitToNative(bool enable)
        {
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_SetFitLimitToNative(h, enable ? 1u : 0u));
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"SetFitLimitToNative 失败: {result}");
        }

        public EngineSnapshot ReadSnapshot()
        {
            var snap = new Fff3FpSnapshot { Size = (uint)SnapshotSize, Version = SnapshotVersion };
            // ReadSnapshot 是轮询线程每 16ms 一次的热路径，而 Dispose 在 _gate 锁外执行，
            // 二者必须互斥，否则会拿着正在销毁的句柄进 GetSnapshot（use-after-free）。
            // WithNative 会在进锁后复查标志位：等锁期间 Dispose 可能已经跑完。
            WithNative(h => Check(Fff3FpNative.FFF3FP_GetSnapshot(h, ref snap), SnapshotOpLabel));
            var mapped = MapSnapshot(snap, _cachedMediaInfo?.FrameRate ?? 0);
            // 环自愈：唯一每帧都到宿主手里的证据就是呈现帧号。
            NotePresentedFrameIndex(mapped.FrameIndex);
            return mapped;
        }

        /// <summary>原生 <see cref="Fff3FpSnapshot"/> → 托管 <see cref="EngineSnapshot"/> 的
        /// <b>唯一</b>翻译点。
        /// <para>抽成 internal 静态纯函数的原因不是可读性，而是<b>可达性</b>：整段映射原先内联在
        /// ReadSnapshot 里，而 ReadSnapshot 必须先有活的原生句柄（WithNative + P/Invoke）才能走到
        /// return ⇒ 映射层在单测里完全够不到，漏映射一个字段（presentWait100ns 就是这么漏掉的，
        /// 它让 VRR 判据只剩 SetPresentConfig 的返回值，见 IPlayerEngine 的同名属性注释）不会有任何
        /// 测试变红。测试工程经 InternalsVisibleTo 直接调用本方法即可逐字段钉住映射，
        /// 无需加载 FFF.Native.dll。</para></summary>
        /// <param name="snap">内核回填后的原生快照。</param>
        /// <param name="frameRate">媒体帧率（由会话的媒体信息缓存提供，与原生快照无关，
        /// 故作为参数传入以保持本函数纯净）。</param>
        internal static EngineSnapshot MapSnapshot(in Fff3FpSnapshot snap, double frameRate) => new EngineSnapshot
        {
            Position100ns = snap.Position100ns,
            Duration100ns = snap.Duration100ns,
            FrameIndex = snap.FrameIndex,
            RawFramePts = snap.FramePts,
            FrameTimeBaseNum = snap.FrameTimeBaseNumerator,
            FrameTimeBaseDen = snap.FrameTimeBaseDenominator,
            // 媒体帧率：来自媒体信息 nominalFrameRate（缓存命中时零开销）。
            // 快照的 frameTimeBase 是流时间基（如 1/15360），不是帧率，勿混用。
            FrameRate = frameRate,
            Decoder = (int)snap.Decoder,
            ActualColorMode = snap.ActualColorMode,
            State = (PlayerState)snap.State,
            PresentedVideoFrames = (long)snap.PresentedVideoFrames,
            SwapChainPresents = (long)snap.SwapChainPresents,
            // 内核结构早已带这个字段（Fff3FpNative.cs:119 ↔ FFF.Player.Api.h:174），
            // 缺的只是这一行透传。ulong→long 的显式窄化与相邻的 SwapChainPresents 保持一致。
            PresentWait100ns = (long)snap.PresentWait100ns,
            TimelineGeneration = snap.TimelineGeneration,
            // IAMF 空间音频（内核 FFF3FPSnapshot：中部 trio @ FFF.Player.Api.h:206-208 +
            // 尾部 iamfContentChannels @ :250 + iamfBinaural @ :258）。iamfActive 是内核
            // u32 标志（0/1），托管侧语义化为 bool；iamfChannels 是渲染目标声道数
            // （7.1.4 = 12），与尾部的内容声道数是两个概念，勿在 UI 上混用。
            IamfActive = snap.IamfActive != 0,
            IamfChannels = snap.IamfChannels,
            IamfSoundSystem = snap.IamfSoundSystem,
            IamfContentChannels = snap.IamfContentChannels,
            IamfBinaural = snap.IamfBinaural,
            // API v20 尾追加（内核 FFF.Player.Api.h:268，结构体最尾部）：非 IAMF 多声道床
            // 在立体声设备上的双耳渲染。1=OBR HRTF 双耳渲染（保留空间定位），0=swr 矩阵折叠。
            AudioBinaural = snap.AudioBinaural,
        };

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
            catch (Exception ex)
            {
                // 静默吞异常曾让"媒体信息永远读不出来"无从排查（只会表现为 UI 上一片空白）。
                // 这里至少留一条 Debug：正常情况下换片间隙会命中，属预期；持续刷屏则说明有真问题。
                AppLog.Debug("Fff3Fp", $"ReadMediaInfo: 引擎未就绪，本次不读取媒体信息: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            var required = 0u;
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_GetMediaInfo(h, 0, 0, out required));
            if (result == FffResult.BufferTooSmall && required > 0 && required <= MaxTextBufferBytes)
            {
                var buffer = new byte[required];
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        // 指针不能进 lambda（闭包字段不能是指针类型）⇒ 先固化成 nint
                        var ptr = (nint)p;
                        result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_GetMediaInfo(h, ptr, required, out _));
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
        // internal 而非 private：媒体信息面板的**唯一**数据来源，必须可单测（docs/41 §4.5 #2）。
        // 配合 Testability.cs 的 InternalsVisibleTo("3FCompare.Core.Tests")，
        // 不必把它提升成 public（那会让内部解析契约变成公共 API）。
        internal static EngineMediaInfo? ParseMediaInfoJson(string json)
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
            catch (Exception ex)
            {
                // 解析失败原本完全静默（返回 null）。媒体信息里出现新的/畸形字段时
                // 表现为"信息面板全空"，无从判断是内核没给还是我们没解析出来。
                // 记 Warn + JSON 长度（不落全文，避免把整份元数据写进日志）。
                AppLog.Warn("Fff3Fp",
                    $"ParseMediaInfoJson 失败（{ex.GetType().Name}: {ex.Message}），JSON 长度={json.Length}");
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
            var probe = new Fff3FpVideoPixelProbe
            {
                Size = (uint)PixelProbeSize,
                Version = 20,
                X = (uint)Math.Max(0, x),
                Y = (uint)Math.Max(0, y),
            };
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_ReadVideoPixel(h, ref probe));
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
            var bits = 0u;
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_ReadVideoPixelRegion(h,
                (uint)Math.Max(0, x), (uint)Math.Max(0, y),
                (uint)width, (uint)height, buffer, (uint)buffer.Length, out bits));
            if (result != FffResult.Success)
                return false;
            outputBitDepth = bits;
            return true;
        }

        /// <summary>3FCompare K5：请求 presenter 线程重绘/执行挂起的 resize。</summary>
        public void Redraw()
        {
            WithNative(h => { Fff3FpNative.FFF3FP_Redraw(h); });
        }

        /// <summary>3FCompare K4：读取 swap/client/dest 尺寸诊断信息。</summary>
        public bool ReadRenderTargetInfo(out RenderTargetInfo info)
        {
            var rtInfo = new Fff3FpRenderTargetInfo
            {
                Size = (uint)RenderTargetInfoSize,
                // destX/destY 有符号（这段历史来自 v1→v2 的改动）。内核现在只接受 20：
                // v1 与 v2 布局同宽，无法靠 size 区分，收下方负原点会让旧调用方读成一个巨大
                // 无符号值 ⇒ 宁可 InvalidArgument。全项目 ABI 自报号已统一为 20。
                Version = 20,
            };
            var result = WithNative<FffResult>(h => Fff3FpNative.FFF3FP_GetRenderTargetInfo(h, ref rtInfo));
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

        /// <summary>HDR 呈现诊断（内核 <c>FFF3FP_GetHdrSignalDiagnostics</c>）。
        /// <para>盘上若残留不含该导出的旧 <c>FFF.Native.dll</c>（<c>NativeRuntime</c> 不会
        /// 覆盖已存在的同名文件，实测过），解析入口会抛 <see cref="EntryPointNotFoundException"/>；
        /// 诊断类接口不该让宿主因此崩掉，故返回 false 而非向上抛。</para></summary>
        public bool ReadHdrSignalDiagnostics(out HdrSignalDiagnostics diagnostics)
        {
            // 与内核头文件里的 static_assert(sizeof(...) == 64) 配成一对牙：
            // 托管结构体若被改成更小，内核的 size 闸门就会拒（读不到）；被改成更大，
            // 则会通过闸门并按错偏移读字段 ⇒ 这里先把自己钉死。
            if (HdrSignalDiagnosticsSize != 64)
            {
                AppLog.Warn("Fff3Fp",
                    $"FFF3FPHdrSignalDiagnostics 托管布局 {HdrSignalDiagnosticsSize} 字节 ≠ 内核 64 字节");
                diagnostics = default;
                return false;
            }
            var raw = new Fff3FpHdrSignalDiagnostics
            {
                Size = (uint)HdrSignalDiagnosticsSize,
                Version = 20,
            };
            FffResult result;
            try
            {
                result = WithNative<FffResult>(
                    h => Fff3FpNative.FFF3FP_GetHdrSignalDiagnostics(h, ref raw));
            }
            catch (EntryPointNotFoundException)
            {
                diagnostics = default;
                return false;
            }
            if (result != FffResult.Success)
            {
                diagnostics = default;
                return false;
            }
            diagnostics = new HdrSignalDiagnostics(
                raw.DynamicCurveFramesApplied, raw.DynamicCurveFramesRefused,
                raw.SetHdrMetaDataCalls, raw.SetHdrMetaDataSuppressed,
                raw.LastSignalledMaxMasteringLuminance, raw.LastSignalledMinMasteringLuminance,
                raw.LastSignalledMaxContentLightLevel, raw.LastSignalledMaxFrameAverageLightLevel,
                raw.SwapChainHdrEnabled != 0);
            return true;
        }

        /// <summary>RTX VSR 状态（内核 <c>FFF3FP_GetVideoSuperResolutionStatus</c>）。
        /// <para>与 <see cref="ReadHdrSignalDiagnostics"/> 同两条牙：先把托管布局钉死成内核的
        /// 44 字节（内核 <c>PlayerSession.cpp:2002</c> 按 <c>size &lt; sizeof || version != 20</c>
        /// 拒；改大会过闸门并按错偏移读），再吞掉旧 DLL 的
        /// <see cref="EntryPointNotFoundException"/>。</para>
        /// <para>读到的 <c>Active</c> 只说明驱动没拒绝，不构成"已增强"的证据；
        /// 内核在 Active=1 时会把 Reason 覆写成 None，所以 Reason 只在未生效时才有解释力。</para></summary>
        public bool ReadVideoSuperResolutionStatus(out VideoSuperResolutionStatus status)
        {
            if (VideoSuperResolutionStatusSize != 44)
            {
                AppLog.Warn("Fff3Fp",
                    $"FFF3FPVideoSuperResolutionStatus 托管布局 {VideoSuperResolutionStatusSize} 字节 ≠ 内核 44 字节");
                status = default;
                return false;
            }
            var raw = new Fff3FpVideoSuperResolutionStatus
            {
                Size = (uint)VideoSuperResolutionStatusSize,
                // 这里的 version 是"结构体按 API 20 的布局解释"，与内核 PlayerApiVersion 同水；
                // 复用 ConfigVersion 而不是再写一个 20 字面量（第 19 笔刚把版本面收敛到一处）。
                Version = ConfigVersion,
            };
            FffResult result;
            try
            {
                result = WithNative<FffResult>(
                    h => Fff3FpNative.FFF3FP_GetVideoSuperResolutionStatus(h, ref raw));
            }
            catch (EntryPointNotFoundException)
            {
                status = default;
                return false;
            }
            if (result != FffResult.Success)
            {
                status = default;
                return false;
            }
            status = new VideoSuperResolutionStatus(
                raw.Requested != 0, raw.NvidiaAdapter != 0, raw.Active != 0, raw.DriverRejected != 0,
                (VideoSuperResolutionReason)raw.Reason,
                raw.SourceWidth, raw.SourceHeight, raw.TargetWidth, raw.TargetHeight);
            return true;
        }

        public void Dispose()
        {
            EngineEvent = null; // 脱离回调，防止释放后仍在触发

            // ⚠ 泵的收尾必须在**取写锁之前**：泵线程是读锁的常客，而下面的
            // EnterWriteLock 会等所有在飞的读锁退出 ⇒ 若在持写锁之后才 Join，
            // 就成了"写锁等读锁、Join 等线程"的死锁（泵线程下一趟要取读锁）。
            StopColorTransformPump();

            // docs/41 #9：标志位必须移到**成功取得写锁之后**置位，否则会永久泄漏句柄。
            // 旧顺序是「先 Interlocked.Exchange(_disposedFlag,1)，再 EnterWriteLock」：
            // 若本线程此刻已持读锁（持读锁执行的原生调用同步反向 P/Invoke 进托管回调、
            // 回调链上又 Dispose 了本会话），EnterWriteLock 会抛 LockRecursionException
            // —— ReaderWriterLockSlim 即使用 SupportsRecursion 也**禁止读锁内升级到写锁**
            // （读锁持有者去等写锁，可能与其它读锁持有者互等而死锁，故直接抛）。
            // 异常一抛，Destroy 永不执行，而标志已是 1、本类又没有终结器 ⇒ 原生句柄 +
            // 整个会话图永久泄漏且无法重试。
            // 现在标志与 Destroy 同在写锁内、且先于 Destroy 置位：EnterWriteLock 失败时
            // 对象保持"未销毁"的一致状态（句柄有效、标志仍为 0），调用方可在别的线程重试；
            // 成功时仍由 Interlocked 保证**恰好一个**线程执行 Destroy —— 防 double-free /
            // use-after-free 的原意不变，只是把"静默永久泄漏"换成"一次响亮的异常"。
            //
            // 写锁：等待所有在飞的读锁（含耗时最长的 Open）退出后才销毁句柄 ——
            // 这正是 use-after-free 的修复点；同时读锁之间仍可并发，不会冻住 UI 线程。
            //
            // docs/41 #8（**刻意不修**，仅记录权衡）：这把写锁与在飞的读锁互斥，而读锁里
            // 有耗时的 FFF3FP_Open（数百 ms～数秒）。调用点 MainWindow.axaml.cs:2522 的
            // OnClosing → DestroyAllSessions() 跑在 UI 线程上，于是关窗/换片时 UI 会卡在
            // EnterWriteLock（若内核 Open 需要 UI 泵消息，还会升级成挂起）。
            // 评估过的 TryEnterWriteLock(超时) + 后台线程销毁方案被否决：那会让 Dispose
            // 返回时句柄仍活着，而调用方紧接着就销毁窗口/设备，原生会话仍持有该窗口与
            // presenter ⇒ 把"UI 冻结"换成原生侧 use-after-free；把 _callbackContext.Free()
            // 一并推迟，又会在"原生还活着"的窗口里放大回调路径的悬空 GCHandle 风险。
            // 依"宁可不改也不引入 UAF / 死锁 / 双重释放"，正确性优先于卡顿；正确解在调用方
            // （UI 线程不直接同步销毁会话），不在本文件范围内。
            _nativeGate.EnterWriteLock();
            try
            {
                // 原子守卫：并发 Dispose 只有一个线程能进入销毁路径，杜绝 double-free
                if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

                if (_handle != 0)
                {
                    var handle = _handle;
                    // 先置 0 再 Destroy：即使此刻有线程正准备发起别的原生调用，
                    // 它取到的也是 0 而不是一个即将无效的句柄。
                    _handle = 0;
                    // 原生声明为 void（FFF.Player.Api.h）：早期这里按 FffResult 声明，
                    // 实际读到的是 RAX 里的垃圾值（无人使用 ⇒ 一直没暴露，但声明必须对齐真实 ABI）。
                    Fff3FpNative.FFF3FP_Destroy(handle);
                }
            }
            finally
            {
                _nativeGate.ExitWriteLock();
            }
            if (_callbackContext.IsAllocated)
                _callbackContext.Free();

            GC.SuppressFinalize(this);
        }

        /// <summary>停掉泵线程、释放模块，并把这条通路的账写进日志。
        /// <para><b>为什么收尾要留一行日志</b>：本通路的既定口径是"缺席/未解锁一律静默"，
        /// 静默到连"它到底跑没跑"都无从判断——而那正是排查时第一个要问的问题。
        /// 一行计数（注入/未变/跳过/末帧）就把"模块在产出但画面没变"与"压根没装载"
        /// 分成两个可读的数，代价只是关会话时一行。</para>
        /// <para><b>顺序</b>：先停线程（它可能在取读锁、也可能正拿着模块实例），
        /// 再释放模块（<see cref="ColorTransformModule.Dispose"/> 与换算是同一把锁的两端）。</para></summary>
        private void StopColorTransformPump()
        {
            _pumpThreadStop = true;
            try { _pumpWake?.Set(); }
            catch (ObjectDisposedException) { /* 事件已被释放：线程下一趟自己会看到停止标志 */ }

            var thread = _pumpThread;
            if (thread is not null)
            {
                // Join 上限：本线程是后台线程且每次循环最多睡 12ms，给 2s 已极宽；
                // 超时不再等（绝不让"可选通路"拖住会话销毁），只留痕。
                if (!thread.Join(TimeSpan.FromSeconds(2)))
                    AppLog.Warn("ColorTransform", "kind=3 泵线程 2s 内未退出（后台线程，不阻塞销毁）");
                _pumpThread = null;
            }
            try { _pumpWake?.Dispose(); } catch { /* 释放失败无后果：进程退出会回收 */ }
            _pumpWake = null;

            var pump = _colorPump;
            _colorPump = null;
            if (pump is not null)
                AppLog.Info("ColorTransform", $"kind=3 通路收尾：{pump.DescribeCounters()}");

            var module = _colorModule;
            _colorModule = null;
            if (module is null) return;
            try { module.Dispose(); }
            catch (Exception ex)
            {
                AppLog.Warn("ColorTransform", $"色彩变换模块释放失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>读取内核最后一次错误文本。必须在持锁（WithNative）内调用，
        /// 因为它同样要用到原生句柄。</summary>
        private static string LastError(nint handle)
        {
            var required = 0u;
            var result = Fff3FpNative.FFF3FP_GetLastError(handle, 0, 0, out required);
            if (result != FffResult.BufferTooSmall || required == 0) return string.Empty;
            // 与 ReadMediaInfo 一样加上限：内核回写的长度不可信（版本错配/内存踩踏时会是巨值）
            if (required > MaxTextBufferBytes)
            {
                AppLog.Warn("Fff3Fp", $"GetLastError 要求缓冲区 {required} 字节，超过上限 {MaxTextBufferBytes}，已忽略");
                return string.Empty;
            }
            var buffer = new byte[required];
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    var ptr = (nint)p;
                    // 第二次调用的返回值此前被丢弃：失败时 buffer 仍是未初始化内存，
                    // 解出来的"错误信息"是随机字节，比没信息更糟（会把排查带偏）。
                    var second = Fff3FpNative.FFF3FP_GetLastError(handle, ptr, required, out _);
                    if (second != FffResult.Success) return string.Empty;
                    var len = buffer.AsSpan().IndexOf((byte)0);
                    return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
                }
            }
        }

        /// <summary>仅在失败时做字符串插值。op 应当尽量是常量（见 <see cref="SnapshotOpLabel"/>）：
        /// 成功路径才是常态，把消息拼好再传进来等于每次都付一次插值开销。</summary>
        private static void Check(FffResult result, string op)
        {
            if (result != FffResult.Success)
                throw new EngineException((int)result, $"{op} 失败: {result}");
        }

        // ---- 通用动态元数据注入通道实现 ----

        /// <summary>托管 HDR10+ 窗口 → 原生 FFF3FPHdrMetadataWindow。
        /// <para>internal static 的理由同 <see cref="MapSnapshot"/>：这是一段可纯函数化的
        /// 扁平映射（钳 15 锚点、钳 3 窗口、零补齐），其正确性不该只靠人眼守——
        /// 测试工程经 InternalsVisibleTo 直接调用即可逐字段钉住，无需加载 FFF.Native.dll。</para></summary>
        internal static Fff3FpHdrMetadataWindow MarshalWindow(HdrDynamicMetadataWindow w)
        {
            var native = new Fff3FpHdrMetadataWindow
            {
                UpperLeftX = w.UpperLeftX,
                UpperLeftY = w.UpperLeftY,
                LowerRightX = w.LowerRightX,
                LowerRightY = w.LowerRightY,
                MaxSclR = w.MaxSclR,
                MaxSclG = w.MaxSclG,
                MaxSclB = w.MaxSclB,
                AverageMaxRgb = w.AverageMaxRgb,
                FractionBrightPixels = w.FractionBrightPixels,
                ToneMappingFlag = w.ToneMappingFlag,
                KneePointX = w.KneePointX,
                KneePointY = w.KneePointY,
                NumCurveAnchors = (uint)Math.Min(w.CurveAnchors?.Count ?? 0, 15),
            };
            if (w.CurveAnchors != null)
            {
                // 扁平赋值（C# 不支持固定数组直接赋值）；Take(15) 与 NumCurveAnchors 同源钳制，
                // 多出的锚点由内核侧按 15 字段结构丢弃。
                var anchors = w.CurveAnchors.Take(15).ToArray();
                if (anchors.Length > 0) native.CurveAnchors0 = anchors[0];
                if (anchors.Length > 1) native.CurveAnchors1 = anchors[1];
                if (anchors.Length > 2) native.CurveAnchors2 = anchors[2];
                if (anchors.Length > 3) native.CurveAnchors3 = anchors[3];
                if (anchors.Length > 4) native.CurveAnchors4 = anchors[4];
                if (anchors.Length > 5) native.CurveAnchors5 = anchors[5];
                if (anchors.Length > 6) native.CurveAnchors6 = anchors[6];
                if (anchors.Length > 7) native.CurveAnchors7 = anchors[7];
                if (anchors.Length > 8) native.CurveAnchors8 = anchors[8];
                if (anchors.Length > 9) native.CurveAnchors9 = anchors[9];
                if (anchors.Length > 10) native.CurveAnchors10 = anchors[10];
                if (anchors.Length > 11) native.CurveAnchors11 = anchors[11];
                if (anchors.Length > 12) native.CurveAnchors12 = anchors[12];
                if (anchors.Length > 13) native.CurveAnchors13 = anchors[13];
                if (anchors.Length > 14) native.CurveAnchors14 = anchors[14];
            }
            return native;
        }

        /// <summary>托管 HDR10+ 条目 → 原生 FFF3FPHdrDynamicMetadataEntry（internal：见 <see cref="MarshalWindow"/> 的理由）。</summary>
        internal static Fff3FpHdrDynamicMetadataEntry MarshalEntry(HdrDynamicMetadataEntry e)
        {
            var windows = e.Windows?.ToArray() ?? Array.Empty<HdrDynamicMetadataWindow>();
            return new Fff3FpHdrDynamicMetadataEntry
            {
                Size = (uint)Marshal.SizeOf<Fff3FpHdrDynamicMetadataEntry>(),
                Version = 1,
                FrameIndex = e.FrameIndex,
                NumWindows = (uint)Math.Clamp(windows.Length, 1, 3),
                Reserved = 0,
                Window0 = windows.Length > 0 ? MarshalWindow(windows[0]) : default,
                Window1 = windows.Length > 1 ? MarshalWindow(windows[1]) : default,
                Window2 = windows.Length > 2 ? MarshalWindow(windows[2]) : default,
                TargetedDisplayNits = e.TargetedDisplayNits,
            };
        }

        /// <summary>托管 HDR Vivid 载荷 → 原生 FFF3FPHdrVividMetadataPayload（internal：见 <see cref="MarshalWindow"/> 的理由）。</summary>
        internal static Fff3FpHdrVividMetadataPayload MarshalVividPayload(HdrVividMetadataPayload p)
        {
            var windows = p.Windows?.ToArray() ?? Array.Empty<(float, float, float, float)>();
            return new Fff3FpHdrVividMetadataPayload
            {
                Size = (uint)Marshal.SizeOf<Fff3FpHdrVividMetadataPayload>(),
                Version = 1,
                NumWindows = (uint)Math.Clamp(windows.Length, 1, 3),
                Reserved = 0,
                W0MaxRGB0 = windows.Length > 0 ? windows[0].Item1 : 0f,
                W0MaxRGB1 = windows.Length > 0 ? windows[0].Item2 : 0f,
                W0MaxRGB2 = windows.Length > 0 ? windows[0].Item3 : 0f,
                W0AverageMaxRGB = windows.Length > 0 ? windows[0].Item4 : 0f,
                W1MaxRGB0 = windows.Length > 1 ? windows[1].Item1 : 0f,
                W1MaxRGB1 = windows.Length > 1 ? windows[1].Item2 : 0f,
                W1MaxRGB2 = windows.Length > 1 ? windows[1].Item3 : 0f,
                W1AverageMaxRGB = windows.Length > 1 ? windows[1].Item4 : 0f,
                W2MaxRGB0 = windows.Length > 2 ? windows[2].Item1 : 0f,
                W2MaxRGB1 = windows.Length > 2 ? windows[2].Item2 : 0f,
                W2MaxRGB2 = windows.Length > 2 ? windows[2].Item3 : 0f,
                W2AverageMaxRGB = windows.Length > 2 ? windows[2].Item4 : 0f,
                TargetedDisplayNits = p.TargetedDisplayNits,
            };
        }

        public bool SetDynamicMetadata(IReadOnlyList<DynamicMetadataPacket> packets)
        {
            ThrowIfDisposed();
            if (packets == null || packets.Count == 0)
                return ClearDynamicMetadata(DynamicMetadataKind.None);

            var payloadHandles = new List<GCHandle>();
            try
            {
                var nativePackets = BuildNativePackets(packets, payloadHandles);

                // 记下"宿主自己的表"：kind=3 的注入是整表替换，不把它一并重发
                // 就等于把用户从菜单载入的 HDR10+/Vivid 表悄悄清空。
                // 含 kind=3 ⇒ 这一批是本泵自己发的，不是宿主的表（见该方法的注释）。
                try
                {
                    if (_colorPump is not null && !ContainsExternalTransform(packets))
                    {
                        _hostDynamicMetadata = packets;
                        _colorPump.CompanionPackets = packets;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("ColorTransform", $"伴报表登记失败（已忽略）：{ex.Message}");
                }

                // 调用原生接口
                try
                {
                    return WithNative(h =>
                    {
                        var result = Fff3FpNative.FFF3FP_SetDynamicMetadata(h,
                            nativePackets, (uint)nativePackets.Length);
                        if (result == FffResult.NotSupported || result == FffResult.InvalidArgument)
                            return false; // 旧内核不支持或整批校验失败（旧表续任）
                        if (result != FffResult.Success)
                            throw new EngineException((int)result, $"SetDynamicMetadata 失败: {result}");
                        return true;
                    });
                }
                catch (EntryPointNotFoundException)
                {
                    return false; // 旧内核无此导出，静默忍受
                }
            }
            finally
            {
                foreach (var h in payloadHandles)
                    if (h.IsAllocated) h.Free();
            }
        }

        /// <summary>托管包 → 原生包数组，载荷按 kind 序列化进各自钉住的缓冲区。
        /// <para>internal static 的理由同 <see cref="MarshalWindow"/>：这段派发是纯映射，
        /// 而它的正确性靠的是"payloadSize 与内核等号校验逐项相等"——测试工程经
        /// InternalsVisibleTo 直接调用就能把尺寸钉死，不必加载 FFF.Native.dll。</para>
        /// <para>句柄追加进 <paramref name="payloadHandles"/>，由调用方在原生调用返回后
        /// 统一 Free（缓冲区数组本身由 GCHandle 根住，钉住期间 GC 不会搬动）。</para></summary>
        internal static Fff3FpDynamicMetadataPacket[] BuildNativePackets(
            IReadOnlyList<DynamicMetadataPacket> packets, List<GCHandle> payloadHandles)
        {
            ArgumentNullException.ThrowIfNull(packets);
            ArgumentNullException.ThrowIfNull(payloadHandles);

            var nativePackets = new Fff3FpDynamicMetadataPacket[packets.Count];
            for (int i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                if (p.Payload == null)
                    throw new ArgumentException($"Packet {i}: payload 不能为 null");

                object payloadObj = p.Payload;
                nint payloadPtr;
                uint payloadSize;
                uint kind = (uint)p.Kind;

                switch (p.Kind)
                {
                    case DynamicMetadataKind.Hdr10Plus:
                        if (payloadObj is not HdrDynamicMetadataEntry entry)
                            throw new ArgumentException($"Packet {i}: Hdr10Plus kind 要求 HdrDynamicMetadataEntry 载荷");
                        var nativeEntry = MarshalEntry(entry);
                        var entryHandle = GCHandle.Alloc(nativeEntry, GCHandleType.Pinned);
                        payloadHandles.Add(entryHandle);
                        payloadPtr = entryHandle.AddrOfPinnedObject();
                        payloadSize = (uint)Marshal.SizeOf<Fff3FpHdrDynamicMetadataEntry>();
                        break;

                    case DynamicMetadataKind.HdrVivid:
                        if (payloadObj is not HdrVividMetadataPayload vivid)
                            throw new ArgumentException($"Packet {i}: HdrVivid kind 要求 HdrVividMetadataPayload 载荷");
                        var nativeVivid = MarshalVividPayload(vivid);
                        var vividHandle = GCHandle.Alloc(nativeVivid, GCHandleType.Pinned);
                        payloadHandles.Add(vividHandle);
                        payloadPtr = vividHandle.AddrOfPinnedObject();
                        payloadSize = (uint)Marshal.SizeOf<Fff3FpHdrVividMetadataPayload>();
                        break;

                    case DynamicMetadataKind.ExternalColorTransform:
                        if (payloadObj is not ColorTransformTable table)
                            throw new ArgumentException($"Packet {i}: ExternalColorTransform kind 要求 ColorTransformTable 载荷");
                        // 内核对 kind=3 的 payloadSize 是**等号**校验（多一字节整批 InvalidArgument），
                        // 所以这里只能报镜像结构自己的 SizeOf；缓冲区按原生布局逐字段写、
                        // 整块 pinned，绝不把这个带 ByValArray 的结构盒子直接钉住
                        // （理由见 ColorTransformPayloadMarshal）。
                        ColorTransformPayloadMarshal.BuildPinnedBuffer(table, out var transformHandle);
                        payloadHandles.Add(transformHandle);
                        payloadPtr = transformHandle.AddrOfPinnedObject();
                        payloadSize = (uint)Marshal.SizeOf<Fff3FpColorTransformPayload>();
                        break;

                    default:
                        throw new ArgumentException($"Packet {i}: 未知 DynamicMetadataKind = {p.Kind}");
                }

                nativePackets[i] = new Fff3FpDynamicMetadataPacket
                {
                    Size = (uint)Marshal.SizeOf<Fff3FpDynamicMetadataPacket>(),
                    Version = 1,
                    Kind = kind,
                    KindVersion = 1,
                    FrameIndex = p.FrameIndex,
                    PayloadSize = payloadSize,
                    Reserved = 0,
                    Payload = payloadPtr,
                };
            }
            return nativePackets;
        }

        /// <summary>中性帧边数据环的宿主接缝（<see cref="IFrameSideDataRing"/>，
        /// 内核 <c>FFF3FP_CopyFrameSideDataAfter</c>）。
        /// <para>两趟式：<paramref name="buffer"/> 传 null（或空数组）时指针传 0、容量传 0，
        /// 内核只回报该条目的字节数与帧号、不写任何东西；传非空时把字节拷进缓冲。
        /// 两条路上出参 <paramref name="requiredSize"/> 都是条目字节数。</para>
        /// <para><b>不抛</b>：这条路的失败全都是可降级信息（句柄已释放、盘上 DLL 没有这个导出、
        /// 原生内部错误），泵的正确性不依赖区分它们。<see cref="SideDataReadOutcome.NotFound"/>
        /// 是"环里这一趟没有"，属正常回答，绝不能和失败混为一谈。</para></summary>
        public SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
            byte[]? buffer, out uint requiredSize, out long foundFrameIndex)
        {
            requiredSize = 0;
            foundFrameIndex = -1;
            if (_sideDataRingUnavailable != 0) return SideDataReadOutcome.Failed;

            GCHandle handle = default;
            try
            {
                var pointer = nint.Zero;
                var capacity = 0u;
                if (buffer is { Length: > 0 })
                {
                    // 原生形参是 void*：这里必须给一个 GC 稳定地址。数组本身很小
                    // （内核单块上限 16 KiB），钉住一次的成本低于把数据拷进非托管块。
                    handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    pointer = handle.AddrOfPinnedObject();
                    capacity = (uint)buffer.Length;
                }

                var (outcome, written, found) = WithNative(h =>
                {
                    var result = Fff3FpNative.FFF3FP_CopyFrameSideDataAfter(h, sideDataType,
                        fromFrameIndex, pointer, capacity, out var writtenSize, out var index);
                    return (MapSideDataOutcome(result), writtenSize, index);
                });
                requiredSize = written;
                foundFrameIndex = found;
                return outcome;
            }
            catch (EntryPointNotFoundException)
            {
                // 老内核盘上没这个导出：闩一次，之后每帧直接答 Failed，不再付异常成本。
                Interlocked.Exchange(ref _sideDataRingUnavailable, 1);
                Diagnostics.AppLog.Info("Fff3FpSession",
                    "盘上内核没有 FFF3FP_CopyFrameSideDataAfter，kind=3 通路按不支持处理");
                return SideDataReadOutcome.Failed;
            }
            catch (ObjectDisposedException)
            {
                return SideDataReadOutcome.Failed;   // 会话正在销毁：本帧跳过即可
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }

        /// <summary>内核返回码 → 环语义。NotFound 与别的失败分成两格是本通路的关键：
        /// 环空是常态（120 帧窗口、超 16 KiB 的块不进环），把它当故障会让泵误报错误。</summary>
        internal static SideDataReadOutcome MapSideDataOutcome(FffResult result) => result switch
        {
            FffResult.Success => SideDataReadOutcome.Found,
            FffResult.NotFound => SideDataReadOutcome.NotFound,
            FffResult.BufferTooSmall => SideDataReadOutcome.BufferTooSmall,
            _ => SideDataReadOutcome.Failed,
        };

        public bool ClearDynamicMetadata(DynamicMetadataKind kind)
        {
            ThrowIfDisposed();
            bool cleared;
            try
            {
                cleared = WithNative(h =>
                {
                    try
                    {
                        var result = Fff3FpNative.FFF3FP_ClearDynamicMetadata(h, (uint)kind);
                        return result == FffResult.Success;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        return false;
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            // 清掉之后宿主的表就少了一块：伴报表必须同步掉，
            // 否则本泵下一次注入会把刚清掉的表又原样装回去（用户点了清除却没效果）。
            if (cleared && _colorPump is not null)
            {
                try
                {
                    _hostDynamicMetadata = KeepExcept(_hostDynamicMetadata, kind);
                    _colorPump.CompanionPackets = _hostDynamicMetadata;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("ColorTransform", $"伴报表同步清除失败（已忽略）：{ex.Message}");
                }
            }
            return cleared;
        }

        public bool SetHdrDynamicMetadata(IReadOnlyList<HdrDynamicMetadataEntry> entries)
        {
            ThrowIfDisposed();
            if (entries == null || entries.Count == 0)
                return ClearHdrDynamicMetadata();

            var nativeEntries = new Fff3FpHdrDynamicMetadataEntry[entries.Count];
            var handles = new List<GCHandle>();

            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var native = MarshalEntry(entries[i]);
                    var handle = GCHandle.Alloc(native, GCHandleType.Pinned);
                    handles.Add(handle);
                    nativeEntries[i] = native;
                }

                try
                {
                    return WithNative(h =>
                    {
                        var result = Fff3FpNative.FFF3FP_SetHdrDynamicMetadata(h,
                            nativeEntries, (uint)nativeEntries.Length);
                        if (result == FffResult.NotSupported || result == FffResult.InvalidArgument)
                            return false;
                        if (result != FffResult.Success)
                            throw new EngineException((int)result, $"SetHdrDynamicMetadata 失败: {result}");
                        return true;
                    });
                }
                catch (EntryPointNotFoundException)
                {
                    return false;
                }
            }
            finally
            {
                foreach (var h in handles)
                    if (h.IsAllocated) h.Free();
            }
        }

        public bool ClearHdrDynamicMetadata()
        {
            ThrowIfDisposed();
            try
            {
                return WithNative(h =>
                {
                    try
                    {
                        // 旧内核无此导出或清拒绝都走 false，与接口文档"false=内核不支持"同口径。
                        return Fff3FpNative.FFF3FP_ClearHdrDynamicMetadata(h) == FffResult.Success;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        return false;
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public DynamicMetadataSource? GetHdrMetadataSource()
        {
            ThrowIfDisposed();
            try
            {
                // 显式泛型实参：lambda 里同时有 null 与 DynamicMetadataSource 两种返回，
                // 不指定 T 时推断会落成非可空值类型，null 无处可去。
                return WithNative<DynamicMetadataSource?>(h =>
                {
                    try
                    {
                        var result = Fff3FpNative.FFF3FP_GetHdrMetadataSource(h, out uint source);
                        if (result != FffResult.Success) return null;
                        return (DynamicMetadataSource)source;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        return null;
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        // 用 _disposedFlag（Dispose 的第一步就置位，且是原子写）而不是 _disposed（普通 bool，
        // 写入的可见性无保证）。这样"已决定销毁"能在 Destroy 之前就被其它线程看到。
        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
    }
}
