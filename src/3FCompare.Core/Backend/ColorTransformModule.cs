using System.Runtime.InteropServices;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Core.Backend;

/// <summary>外部色彩变换模块（<c>FFF.HDRdp.Test.dll</c>）的可选装载与逐帧换算。
/// <para><b>缺席是常态，不是错误</b>：既定口径是"不装模块 = 回落 HDR10 基底且不提示"，
/// 内核侧对这件事零加载痕迹，所以装载全部发生在托管宿主层，任何一步失败都只是
/// 让 <see cref="TryOpen"/> 返回 false——不抛异常、不改任何默认行为、不要求 UI 表态。
/// 失败原因只进日志（<see cref="AppLog"/>），供排查"为什么这路没注入"时归因。</para>
/// <para>一个实例对应一份函数表与一组复用缓冲区：同一进程里多个会话各自持有一个实例
/// （见 <see cref="ColorTransformPump"/> 的用法说明），实例内部的 <see cref="TryAnalyzeFrame"/>
/// 已经加锁串行化。</para>
/// </summary>
public sealed class ColorTransformModule : IDisposable
{
    private readonly nint _module;
    private readonly FffAnalyzeFrameFn? _analyze;
    private readonly FffResetFn? _reset;

    // 复用缓冲区：pinned 数组由 GCHandle 根住，句柄在 Dispose 里 Free，
    // 不走 AllocHGlobal（那是 421 KB 的非托管块，漏一次 Free 就是每帧一次的泄漏）。
    private readonly byte[] _payloadBuffer;
    private readonly byte[] _abiHeader;   // input(32B) + output(32B)，原生布局
    private readonly GCHandle _payloadHandle;
    private readonly GCHandle _headerHandle;
    private readonly Lock _gate = new();
    private bool _disposed;
    /// <summary>床渲染函数表已交给内核 ⇒ 模块必须留在进程里。
    /// 表里是模块代码段中的裸函数指针，还带着模块自己分配的渲染上下文；
    /// 在这里 FreeLibrary 之后，内核下一次 render() 就是踩空。</summary>
    private bool _audioHandedOff;

    /// <summary>上一帧的原始载荷副本 + 由它换算出的那张表。
    /// <para>存在的理由只有一个：<see cref="ColorTransformTable.TryFromPayload"/> 每次要新写
    /// 107 811 个 float（431 KB，直接进 LOH），而动态元数据在长段静止画面里成百帧内容不变
    /// （实测一件单层素材 259 帧的 RPU 全同）。把"没变"判在换算**之前**，
    /// 不变的那些帧就复用同一枚不可变表；判在换算之后只省了注入，省不掉分配。</para>
    /// <para>随 <see cref="Reset"/> / <see cref="Dispose"/> 一起作废：它们都表示
    /// "上一张表所属的流已经结束"，留着会把旧流的答案继续往外发。</para></summary>
    private byte[]? _lastPayloadBytes;
    private ColorTransformTable? _lastTable;

    private ColorTransformModule(nint module, FffColorTransformApiTable table,
        byte[] payloadBuffer, byte[] abiHeader,
        GCHandle payloadHandle, GCHandle headerHandle)
    {
        _module = module;
        _payloadBuffer = payloadBuffer;
        _abiHeader = abiHeader;
        _payloadHandle = payloadHandle;
        _headerHandle = headerHandle;
        _analyze = table.AnalyzeFrame == 0 ? null :
            Marshal.GetDelegateForFunctionPointer<FffAnalyzeFrameFn>(table.AnalyzeFrame);
        _reset = table.Reset == 0 ? null :
            Marshal.GetDelegateForFunctionPointer<FffResetFn>(table.Reset);
    }

    /// <summary>模块自报的载荷布局版本（<c>transformVersion()</c>），仅用于日志归因。</summary>
    public uint LayoutVersion { get; private set; }

    /// <summary>模块已装载且能换算一帧。<see cref="TryOpen"/> 成功时为 true。</summary>
    public bool IsAvailable => !_disposed && _analyze != null;

    /// <summary>尝试装载模块。<b>任何失败都返回 false</b>（模块不在盘上、导出缺失、
    /// 版本门不过、ABI 容量与内核结构不一致……），并给出只进日志的原因。
    /// <param name="directoryOverride">优先搜索的目录（用户在设置里指定模块位置时用）；
    /// null 表示只按发布目录与默认搜索顺序找，绝不写死绝对路径。</param></summary>
    public static bool TryOpen([System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out ColorTransformModule? module, out string? reason,
        string? directoryOverride = null)
    {
        module = null;
        reason = null;
        nint loaded = 0;
        GCHandle payloadHandle = default, headerHandle = default;
        byte[]? payloadBuffer = null, abiHeader = null;
        try
        {
            // 模块 ABI 与内核 kind=3 载荷的尺寸必须同源：不一致说明两仓漂移了，
            // 继续走只会让每一帧都被内核整批拒绝 ⇒ 按"模块缺席"降级。
            if (ColorTransformAbi.PayloadCapacity !=
                (uint)ColorTransformPayloadMarshal.NativePayloadByteCount)
            {
                reason = $"模块载荷容量 {ColorTransformAbi.PayloadCapacity} 与内核结构大小 " +
                         $"{ColorTransformPayloadMarshal.NativePayloadByteCount} 不同水";
                return false;
            }

            foreach (var candidate in CandidatePaths(directoryOverride))
            {
                loaded = NativeModuleLoader.Load(candidate);
                if (loaded != 0) break;
            }
            if (loaded == 0)
            {
                reason = $"{ColorTransformAbi.ModuleFileName} 未装载（Win32 错误码 " +
                         $"{Marshal.GetLastPInvokeError()}）——按既定口径静默降级";
                return false;
            }

            var entry = NativeModuleLoader.Resolve(loaded, ColorTransformAbi.EntryPointName);
            if (entry == 0)
            {
                reason = $"{ColorTransformAbi.ModuleFileName} 里没有导出 {ColorTransformAbi.EntryPointName}";
                NativeModuleLoader.Release(loaded);
                return false;
            }
            var getApi = Marshal.GetDelegateForFunctionPointer<FffGetColorTransformApiFn>(entry);

            // 函数表按原生布局读写：结构是全标量/nint 的 blittable 镜像，
            // 所以直接把它写进一块 pinned 缓冲再读回，不给"按值传 32 字节表"留歧义。
            var api = new FffColorTransformApiTable
            {
                Size = (uint)Marshal.SizeOf<FffColorTransformApiTable>(),
                Version = 0,
            };
            var apiBuffer = new byte[Marshal.SizeOf<FffColorTransformApiTable>()];
            MemoryMarshal.Write(apiBuffer.AsSpan(), in api);
            var apiHandle = GCHandle.Alloc(apiBuffer, GCHandleType.Pinned);
            int entryResult;
            try
            {
                entryResult = getApi(ColorTransformAbi.ApiVersion, apiHandle.AddrOfPinnedObject());
                api = MemoryMarshal.Read<FffColorTransformApiTable>(apiBuffer.AsSpan());
            }
            finally
            {
                apiHandle.Free();
            }

            // 版本门不过 ⇒ 头里的约定："与模块不在盘上同等处理"。
            // 二次核对表内容：Size/Version 自描述 + 两个必用指针齐备（TransformVersion 可缺，只用于日志）。
            if (entryResult != 0 || api.Version != ColorTransformAbi.ApiVersion ||
                api.Size < (uint)Marshal.SizeOf<FffColorTransformApiTable>() ||
                api.AnalyzeFrame == 0 || api.Reset == 0)
            {
                // 握手被拒有三种完全不同的原因，画面却一模一样，必须分开留痕：
                //   1) 盘上是早于 ABI 版本 2 的旧件（不含授权层，也不认这个版本门）
                //   2) 模块在、但**未解锁** —— 这是授权门的设计结果，不是故障
                //   3) 模块自称已解锁却仍然被拒 —— 真故障（多半是两侧版本没对上）
                // 分不开它们，"没装模块"与"装了没解锁"在日志里就同形，最难归因。
                var authState = "模块不含授权层（早于 ABI 版本 2 的旧件）";
                var statusAddr = NativeModuleLoader.Resolve(loaded,
                    ColorTransformAbi.AuthorizationStatusEntryPointName);
                if (statusAddr != 0)
                {
                    var statusFn = Marshal.GetDelegateForFunctionPointer<FffAuthorizationStatusFn>(
                        statusAddr);
                    authState = statusFn() switch
                    {
                        1u => "模块已装载但未解锁：按基底出画，属授权门的既定结果而非故障",
                        2u => "模块自称已解锁却被握手拒绝 ⇒ 真故障，先查两侧 ABI 版本是否同水",
                        var other => $"授权状态返回未知值 {other}",
                    };
                    var textAddr = NativeModuleLoader.Resolve(loaded,
                        ColorTransformAbi.StatusTextEntryPointName);
                    if (textAddr != 0)
                    {
                        var textFn = Marshal.GetDelegateForFunctionPointer<FffStatusTextFn>(textAddr);
                        var what = textFn(ColorTransformAbi.TextPurposeAuthorizationExplanation);
                        if (what != 0)
                            authState += "；模块原话：" + Marshal.PtrToStringUTF8(what);
                    }
                }

                reason = $"{ColorTransformAbi.EntryPointName} 返回 {entryResult}，" +
                         $"或函数表不合格（version={api.Version} size={api.Size} " +
                         $"analyze={api.AnalyzeFrame} reset={api.Reset}）；{authState}";
                NativeModuleLoader.Release(loaded);
                return false;
            }

            uint layoutVersion = 0;
            if (api.TransformVersion != 0)
            {
                var versionFn = Marshal.GetDelegateForFunctionPointer<FffTransformVersionFn>(
                    api.TransformVersion);
                layoutVersion = versionFn();
            }

            payloadBuffer = new byte[ColorTransformAbi.PayloadCapacity];
            abiHeader = new byte[Marshal.SizeOf<FffColorTransformInput>() +
                                 Marshal.SizeOf<FffColorTransformOutput>()];
            payloadHandle = GCHandle.Alloc(payloadBuffer, GCHandleType.Pinned);
            headerHandle = GCHandle.Alloc(abiHeader, GCHandleType.Pinned);

            module = new ColorTransformModule(loaded, api, payloadBuffer, abiHeader,
                payloadHandle, headerHandle)
            {
                LayoutVersion = layoutVersion,
            };
            AppLog.Info("ColorTransformModule",
                $"已装载 {ColorTransformAbi.ModuleFileName}（布局版本 {layoutVersion}）");
            return true;
        }
        catch (Exception ex)
        {
            // 装载层唯一的硬约束是"不得抛给调用方"：DllNotFoundException、
            // BadImageFormatException、架构不匹配、 marshalling 意外都归成"模块缺席"。
            reason = $"{ColorTransformAbi.ModuleFileName} 装载异常 {ex.GetType().Name}: {ex.Message}";
            if (payloadHandle.IsAllocated) payloadHandle.Free();
            if (headerHandle.IsAllocated) headerHandle.Free();
            if (loaded != 0) NativeModuleLoader.Release(loaded);
            module = null;
            return false;
        }
    }

    /// <summary>模块在盘上的"存在形态"（三态 + 旧件）。
    /// <para>为什么必须把它从 <see cref="TryOpen"/> 的那条 reason 字符串里独立出来：
    /// 宿主要在"模块在、但锁着"时<b>提示输入解锁码</b>，在"模块不在"时<b>完全不提示</b>；
    /// 而这两者在握手层的表现是一样的（都是 <see cref="TryOpen"/> 返回 false），
    /// 只靠一句 reason 去分，等于把 UI 行为挂在一个给人看的字符串上。</para></summary>
    public enum ColorTransformPresence
    {
        /// <summary>盘上没有、或装载/解析失败 ⇒ 静默，绝不提示。</summary>
        Absent,
        /// <summary>能装载但不含授权层（早于 ABI 版本 2 的旧件）⇒ 没有可解锁的路径，按缺席处理。</summary>
        Legacy,
        /// <summary>装载成功、授权层可达、<c>AuthorizationStatus()==1</c> ⇒ <b>唯一需要提示的态</b>。</summary>
        PresentLocked,
        /// <summary><c>AuthorizationStatus()==2</c> ⇒ 已解锁，直接走 <see cref="TryOpen"/> 装载。</summary>
        Ready,
    }

    /// <summary>解锁码送进模块后的结果（对应模块 <c>AuthenticateResult</c> 加一层"够不到模块"）。</summary>
    public enum UnlockOutcome
    {
        /// <summary>没能调用到模块（不在盘上 / 不含授权层 / 封送失败）⇒ 与"缺席"同义，不提示。</summary>
        Unreachable,
        /// <summary>码不对（模块返回 0）：可重试。</summary>
        Refused,
        /// <summary>通过，且票据已落盘（模块返回 1）。</summary>
        Accepted,
        /// <summary>通过，但票据写不进目标目录（模块返回 2）⇒ 仅本次进程有效，重启后要重输。</summary>
        AcceptedNotPersisted,
    }

    /// <summary>探测模块的存在形态。<b>不握手、不建实例、不抛</b>。
    /// <para>只做三件事：映射模块 → 找授权层的裸导出 → 读 <c>AuthorizationStatus()</c>。
    /// 它回答"要不要提示用户"，而"能不能真的用"仍由 <see cref="TryOpen"/> 回答——
    /// 本方法返回 <see cref="ColorTransformPresence.Ready"/> 之后 <see cref="TryOpen"/> 仍可能失败
    /// （版本门、ABI 容量不同水），那种情况按缺席静默处理，不提示。</para></summary>
    public static ColorTransformPresence Probe(out string? reason, string? directoryOverride = null)
    {
        reason = null;
        nint loaded = 0;
        try
        {
            foreach (var candidate in CandidatePaths(directoryOverride))
            {
                loaded = NativeModuleLoader.Load(candidate);
                if (loaded != 0) break;
            }
            if (loaded == 0)
            {
                reason = $"{ColorTransformAbi.ModuleFileName} 未装载（Win32 错误码 " +
                         $"{Marshal.GetLastPInvokeError()}）";
                return ColorTransformPresence.Absent;
            }

            var statusAddress = NativeModuleLoader.Resolve(loaded,
                ColorTransformAbi.AuthorizationStatusEntryPointName);
            if (statusAddress == 0)
            {
                reason = "模块不含授权层（早于 ABI 版本 2 的旧件）：没有可解锁的路径";
                return ColorTransformPresence.Legacy;
            }

            var statusFn = Marshal.GetDelegateForFunctionPointer<FffAuthorizationStatusFn>(statusAddress);
            var status = statusFn();
            // 只有 2（已授权）与 1（未授权）是模块声明过的取值；别的都按"缺席"处理——
            // 宁可不提示，也不在一个看不懂的返回值上弹框。
            if (status == 2u)
            {
                reason = "已解锁";
                return ColorTransformPresence.Ready;
            }
            if (status == 1u)
            {
                reason = "模块已装载但未解锁：这是唯一需要提示用户的一格";
                return ColorTransformPresence.PresentLocked;
            }
            reason = $"授权状态返回未知值 {status}";
            return ColorTransformPresence.Absent;
        }
        catch (Exception ex)
        {
            // 探测是"要不要打扰用户"的判据：宁可判缺席（不提示），也不能抛。
            reason = $"探测异常 {ex.GetType().Name}: {ex.Message}";
            return ColorTransformPresence.Absent;
        }
        finally
        {
            if (loaded != 0) NativeModuleLoader.Release(loaded);
        }
    }

    /// <summary>把一枚解锁码送进模块铸票。<b>不抛</b>；够不到模块一律答
    /// <see cref="UnlockOutcome.Unreachable"/>。
    /// <para>模块自己负责"写票到哪儿"（host exe 同目录），宿主不介入路径决策——
    /// 那条路径是本模块授权层的既有属性，动它等于把"票据落点"变成宿主的知识。</para>
    /// <para>码本身<b>绝不</b>进日志：只记结果与原因。</para></summary>
    public static UnlockOutcome TryAuthenticate(string code, out string? detail,
        string? directoryOverride = null)
    {
        detail = null;
        if (string.IsNullOrWhiteSpace(code)) return UnlockOutcome.Refused;
        nint loaded = 0;
        try
        {
            // 搜索口径必须与 Probe / TryOpen 同一份：否则探测说"在且锁着"、弹了框，
            // 而这里去解锁的是另一枚拷贝（或压根找不到），框白弹一次。
            foreach (var candidate in CandidatePaths(directoryOverride ?? DefaultDirectoryOverride()))
            {
                loaded = NativeModuleLoader.Load(candidate);
                if (loaded != 0) break;
            }
            if (loaded == 0)
            {
                detail = $"{ColorTransformAbi.ModuleFileName} 未装载，无法提交解锁码";
                return UnlockOutcome.Unreachable;
            }

            var address = NativeModuleLoader.Resolve(loaded,
                ColorTransformAbi.AuthorizationUnlockEntryPointName);
            if (address == 0)
            {
                detail = "模块不含解锁入口（旧件）";
                return UnlockOutcome.Unreachable;
            }

            var authenticate = Marshal.GetDelegateForFunctionPointer<FffAuthenticateFn>(address);
            var result = authenticate(code);
            if (result == ColorTransformAbi.AuthenticateAcceptedAndSaved) return UnlockOutcome.Accepted;
            if (result == ColorTransformAbi.AuthenticateAcceptedNotPersisted)
                return UnlockOutcome.AcceptedNotPersisted;
            if (result == ColorTransformAbi.AuthenticateRefused) return UnlockOutcome.Refused;
            // 读不懂的返回码不能当成"码不对"：那会让对话框再要一次输入，
            // 而真相是我们与模块的答复形状对不上了（多半是版本不同水）。
            detail = $"模块返回未知值 {result}";
            return UnlockOutcome.Unreachable;
        }
        catch (Exception ex)
        {
            detail = $"提交解锁码异常 {ex.GetType().Name}: {ex.Message}";
            return UnlockOutcome.Unreachable;
        }
        finally
        {
            if (loaded != 0) NativeModuleLoader.Release(loaded);
        }
    }

    /// <summary>模块搜索目录的运行时覆盖（环境变量 <c>FC_COLORTRANSFORM_DIR</c>）。
    /// <para>放在模块这一侧而不是宿主那一侧：Probe / TryOpen / TryAuthenticate 三个入口
    /// 必须用同一个口径找同一枚 DLL，写在两处迟早分叉（分叉的表现不是报错，
    /// 是"探测说在、解锁说够不到"这种自相矛盾的框）。</para>
    /// <para>存在的理由：模块默认只按 exe 同目录 / 默认搜索顺序找，而对照实验需要
    /// "同一份 exe、只改模块的有无与新旧"，把 DLL 搬进搬出发布目录既脏又容易漏。</para></summary>
    public static string? DefaultDirectoryOverride()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("FC_COLORTRANSFORM_DIR");
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch
        {
            return null;   // 环境变量都读不到（极窄）：按"没有覆盖"处理
        }
    }

    /// <summary>搜索顺序：显式目录 → 应用目录 → 默认搜索顺序（裸文件名）。
    /// 每个候选都是"存在才试"，不存在就下一个——绝不写死绝对路径。</summary>
    private static IEnumerable<string> CandidatePaths(string? directoryOverride)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(directoryOverride)) dirs.Add(directoryOverride);
        dirs.Add(AppContext.BaseDirectory);
        foreach (var dir in dirs)
        {
            string? full = null;
            try { full = Path.Combine(dir, ColorTransformAbi.ModuleFileName); }
            catch (ArgumentException) { continue; }   // 非法路径字符：跳过这个候选
            if (full is null || !seen.Add(full)) continue;
            if (File.Exists(full)) yield return full;
        }
        // 最后一手交给系统搜索顺序（PATH 等）；LoadLibraryExW 对裸文件名即此语义。
        yield return ColorTransformAbi.ModuleFileName;
    }

    /// <summary>把一帧的边数据交给模块换算成可注入的表。
    /// <para>返回 true 表示拿到了表；返回 false 时 <paramref name="error"/> 为 null 表示
    /// 模块明说"本帧无可换算据"（<c>NoMetadata</c>）——那是正常回答，不是故障，
    /// 调用方只需跳过本帧的注入；非 null 才是需要留痕的失败。
    /// 缓冲区不足（<c>TooSmall</c>）按契约不可能发生：容量是按内核结构大小开的。</para>
    /// <para><paramref name="sideData"/> 在内核环里上限 16 KiB/块，且只在本次调用内被借用，
    /// 所以这里直接按 pinned 指针传，不做中间拷贝。</para></summary>
    public bool TryAnalyzeFrame(ReadOnlySpan<byte> sideData, long frameIndex,
        out ColorTransformTable? table, out string? error)
    {
        table = null;
        error = null;
        if (_analyze == null) { error = "模块没有 analyzeFrame 指针"; return false; }
        if (sideData.IsEmpty) { return false; }  // 空块：等同于本帧无据，不进模块

        lock (_gate)
        {
            // 释放判定必须在锁内：Dispose 与换算是同一把锁的两端，锁外读到的
            // _disposed 只说明"进锁之前还没释放"。
            if (_disposed) { error = "模块实例已释放"; return false; }
            var inputSize = Marshal.SizeOf<FffColorTransformInput>();
            var outputSize = Marshal.SizeOf<FffColorTransformOutput>();
            var header = new Span<byte>(_abiHeader);

            var input = new FffColorTransformInput
            {
                Size = (uint)inputSize,
                Version = ColorTransformAbi.ApiVersion,
                Metadata = 0,
                MetadataSize = (ulong)sideData.Length,
                FrameIndex = unchecked((ulong)frameIndex),
            };
            var output = new FffColorTransformOutput
            {
                Size = (uint)outputSize,
                PayloadKind = 0,
                PayloadKindVersion = 0,
                PayloadSize = 0,
                Payload = _payloadHandle.AddrOfPinnedObject(),
                PayloadCapacity = ColorTransformAbi.PayloadCapacity,
                Reserved = 0,
            };

            // Metadata 指向调用方的边数据：ReadOnlySpan 在 fixed 语句期间被钉住，
            // 且整个 analyzeFrame 调用都在这个 fixed 作用域里。
            int code;
            unsafe
            {
                fixed (byte* raw = sideData)
                {
                    input.Metadata = (nint)raw;
                    MemoryMarshal.Write(header.Slice(0, inputSize), in input);
                    MemoryMarshal.Write(header.Slice(inputSize, outputSize), in output);
                    // 宿主层的"不得抛"在这里落地一次：模块内部托管不到（原生崩了谁都接不住），
                    // 但封送/句柄失效这类托管异常必须被接住，否则泵的每帧调用会把异常甩进主循环。
                    try
                    {
                        code = _analyze(_headerHandle.AddrOfPinnedObject(),
                            _headerHandle.AddrOfPinnedObject() + inputSize);
                    }
                    catch (Exception ex)
                    {
                        error = $"analyzeFrame 抛 {ex.GetType().Name}: {ex.Message}";
                        return false;
                    }
                }
            }

            // 出参从同一块缓冲区读回（模块写的是 output 结构本身）。
            var written = MemoryMarshal.Read<FffColorTransformOutput>(
                header.Slice(inputSize, outputSize));

            switch (code)
            {
                case ColorTransformAbi.ResultSuccess:
                    break;
                case ColorTransformAbi.ResultNoMetadata:
                    return false;                    // 正常回答：本帧无据，error 留空
                case ColorTransformAbi.ResultTooSmall:
                    error = $"模块报缓冲区不足（written={written.PayloadSize}），" +
                            $"容量 {ColorTransformAbi.PayloadCapacity} 是按内核结构大小开的";
                    return false;
                case ColorTransformAbi.ResultUnsupported:
                    error = "模块不支持这帧的元数据修订（Unsupported）";
                    return false;
                case ColorTransformAbi.ResultInvalidArgument:
                    error = "模块拒绝了入参（InvalidArgument）：边数据或结构版本不符";
                    return false;
                case ColorTransformAbi.ResultUnauthorized:
                    // 表是解锁时拿到的，票据在进程存活期内到期或被撤。不是本帧的问题，
                    // 也不是入参错：按"模块不再产出"处理，回落基底并留一句可诊断的话。
                    error = "模块授权已失效（Unauthorized）：停止注入，画面按基底出";
                    return false;
                default:
                    error = $"模块返回未知码 {code}";
                    return false;
            }

            if (written.PayloadSize == 0) return false;   // Success 但没写：按无据处理
            if (written.PayloadKind != ColorTransformAbi.PayloadKind ||
                written.PayloadKindVersion != ColorTransformAbi.PayloadKindVersion)
            {
                error = $"模块自报 kind={written.PayloadKind}/kindVersion={written.PayloadKindVersion}，" +
                        $"与内核 kind=3 v{ColorTransformAbi.PayloadKindVersion} 不符";
                return false;
            }
            if (written.PayloadSize > ColorTransformAbi.PayloadCapacity)
            {
                error = $"模块自报 payloadSize={written.PayloadSize} 超出容量 " +
                        $"{ColorTransformAbi.PayloadCapacity}";
                return false;
            }
            // 内核的 payloadSize 门是等号：写了不到 431296 字节就没法注入，
            // 在这里判掉，省一次"整批 InvalidArgument 且旧表续任"的哑账。
            if (written.PayloadSize != (uint)ColorTransformPayloadMarshal.NativePayloadByteCount)
            {
                error = $"模块写出 {written.PayloadSize} 字节，内核只收 " +
                        $"{ColorTransformPayloadMarshal.NativePayloadByteCount} 字节（等号校验）";
                return false;
            }

            // 快路：载荷与上一帧逐字节相同 ⇒ 表也必然相同（表的全部成员就是这段字节），
            // 直接把上一枚不可变实例交回去，不新分配 431 KB。调用方（泵）随后按内容
            // 判定"未变"，拿到同一枚实例时 SequenceEqual 天然成立。
            var payloadBytes = _payloadBuffer.AsSpan(0, (int)written.PayloadSize);
            if (_lastTable is not null && PayloadMatches(payloadBytes, _lastPayloadBytes))
            {
                table = _lastTable;
                return true;
            }

            if (!ColorTransformTable.TryFromPayload(_payloadBuffer, out table, out error))
                return false;
            _lastPayloadBytes = payloadBytes.ToArray();
            _lastTable = table;
            return true;
        }
    }

    /// <summary>这一帧的载荷是否与<paramref name="lastBytes"/>逐字节相同。
    /// <para>判据必须比得**完整**：少比一个字节就是把变了的表说成没变，画面会停在旧色调
    /// 且没有任何失败痕迹。所以长度不等直接算"变了"，不做前缀比较。</para>
    /// <para>抽成独立方法只为这一条判据可被单测钉住（真模块只在盘上有件时才能走完整路径，
    /// 而这条快路的错误方向是静默的）。</para></summary>
    internal static bool PayloadMatches(ReadOnlySpan<byte> incoming, byte[]? lastBytes) =>
        lastBytes is not null && incoming.Length == lastBytes.Length
        && incoming.SequenceEqual(lastBytes);

    /// <summary>丢弃模块的逐流状态：seek、换文件、解码器重建之后必须调
    /// （<c>reset()</c> 契约：任何时刻都安全，包括首次 analyzeFrame 之前）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            // 释放判定必须在锁内：与 TryAnalyzeFrame 同理，锁外读到的 _disposed
            // 只说明"进锁之前还没释放"，而 Dispose 就在这把锁的另一端 FreeLibrary。
            if (_disposed || _reset == null) return;
            try { _reset(); }
            catch (Exception ex)
            {
                // 模块是可选件：它把自己的 reset 弄砸也不能把宿主的主循环带倒。
                AppLog.Warn("ColorTransformModule", $"reset() 抛 {ex.GetType().Name}: {ex.Message}");
            }
            _lastPayloadBytes = null;
            _lastTable = null;
        }
    }

    /// <summary>握手取外部床渲染函数表。
    /// <para>成功返回一块调用方拥有的 56 字节非托管缓冲（内核在装载点拷贝整张表，
    /// 所以 <c>SetAudioRenderPlug</c> 一返回就该 <c>FreeHGlobal</c>）；表内的 context
    /// 所有权随表归内核，模块每次握手新建一个渲染器，由内核在替换或会话拆解时释放——
    /// 于是同进程多个会话各持一条 DSP 管线，而不是后者盖掉前者的床。</para>
    /// <para>失败返回 0 并给一条只进日志的原因：旧模块没有这一面 / 未解锁 /
    /// 镜像与契约不同水 / 表不合格。四类都不提示、不抛、不改默认行为——
    /// 床继续由内核自己的矩阵折叠处理，那是现网行为。</para></summary>
    public nint TryGetAudioRenderTable(out string? reason)
    {
        reason = null;
        lock (_gate)
        {
            if (_disposed) { reason = "色彩模块实例已释放，音频表未取"; return 0; }
            if (8 + 6 * IntPtr.Size != ColorTransformAbi.AudioTableSize)
            {
                reason = $"音频函数表镜像按 {8 + 6 * IntPtr.Size} 字节算，与契约 " +
                         $"{ColorTransformAbi.AudioTableSize} 不同水——不交出表";
                return 0;
            }
            var entry = NativeModuleLoader.Resolve(_module, ColorTransformAbi.AudioEntryPointName);
            if (entry == 0)
            {
                reason = $"{ColorTransformAbi.ModuleFileName} 里没有导出 " +
                         $"{ColorTransformAbi.AudioEntryPointName}——床保持矩阵折叠";
                return 0;
            }
            var get = Marshal.GetDelegateForFunctionPointer<FffGetAudioRenderApiFn>(entry);
            var buffer = Marshal.AllocHGlobal(ColorTransformAbi.AudioTableSize);
            for (var offset = 0; offset < ColorTransformAbi.AudioTableSize; offset += IntPtr.Size)
                Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
            var rc = get(ColorTransformAbi.AudioTableVersion, buffer);
            var size = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var version = unchecked((uint)Marshal.ReadInt32(buffer, 4));
            var context = Marshal.ReadIntPtr(buffer, 8);
            var supports = Marshal.ReadIntPtr(buffer, 24);
            var render = Marshal.ReadIntPtr(buffer, 32);
            var reset = Marshal.ReadIntPtr(buffer, 40);
            if (rc != 0 || version != ColorTransformAbi.AudioTableVersion ||
                size < ColorTransformAbi.AudioTableSize || context == 0 ||
                supports == 0 || render == 0 || reset == 0)
            {
                Marshal.FreeHGlobal(buffer);
                reason = $"{ColorTransformAbi.AudioEntryPointName} rc={rc} version={version} " +
                         $"size={size} context={context}——床保持矩阵折叠";
                return 0;
            }
            _audioHandedOff = true;
            return buffer;
        }
    }

    public void Dispose()
    {
        // 与换算同锁：否则"正在 analyzeFrame"与"释放句柄/卸载模块"可以交错，
        // 在飞的那次调用会拿着已 Free 的 pinned 指针读写。
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lastPayloadBytes = null;
            _lastTable = null;
            if (_payloadHandle.IsAllocated) _payloadHandle.Free();
            if (_headerHandle.IsAllocated) _headerHandle.Free();
            if (_module != 0 && !_audioHandedOff) NativeModuleLoader.Release(_module);
        }
    }
}
