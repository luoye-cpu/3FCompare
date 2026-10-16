using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend.Interop;

/// <summary>外部色彩变换模块（<c>FFF.HDRdp.Test.dll</c>）的 ABI 镜像与装载入口。
/// <para>真源是与本模块同水的那份 C ABI 契约头（在未公开的独立仓里，不随本仓发布）：
/// 单导出 <c>FFF_GetColorTransformApi</c> + 版本门 + 函数表，没有 C++ 对象、
/// 没有跨边界保留指针、没有分配器所有权——输入借用一次调用，输出写调用方的缓冲区。
/// 这些常量与该头里的 <c>constexpr</c> 同水，头改数值时要同时改这里
/// （内核侧的 kind=3 尺寸由 <c>ColorTransformLayoutTests</c> 对账，本模块的尺寸没有那条线，
/// 所以 <see cref="ColorTransformModule"/> 在装载时把表自报的容量与内核结构大小对撞一次）。</para>
/// </summary>
internal static class ColorTransformAbi
{
    /// <summary>盘上的模块文件名（随包发布，缺席是常态而非错误）。</summary>
    internal const string ModuleFileName = "FFF.HDRdp.Test.dll";

    /// <summary>唯一的导出名。</summary>
    internal const string EntryPointName = "FFF_GetColorTransformApi";

    /// <summary>授权层的裸导出名。握手被拒时宿主仍要能问"为什么"、还能把解锁码送进去，
    /// 所以这几个符号必须在**没有函数表**的情况下也可解析。</summary>
    internal const string AuthorizationStatusEntryPointName = "FFF_ColorTransformAuthorizationStatus";
    internal const string StatusTextEntryPointName = "FFF_ColorTransformStatusText";
    /// <summary>把解锁码送进模块铸票的裸导出。握手被拒后它仍要可达，否则被拒的一方
    /// 只能重新下载。返回值是三态：拒 / 通过且票据已落盘 / 通过但票据写不进盘
    /// （只有本次会话有效）。**签名与取值以盘上模块为准**：读不出这三个值之一时，
    /// 宿主一律按"够不到"处理，不猜成"码不对"。</summary>
    internal const string AuthorizationUnlockEntryPointName = "FFF_ColorTransformAuthenticate";

    /// <summary><c>Authenticate</c> 的三种返回（与模块 <c>AuthenticateResult</c> 同水）。</summary>
    internal const int AuthenticateRefused = 0;
    internal const int AuthenticateAcceptedAndSaved = 1;
    internal const int AuthenticateAcceptedNotPersisted = 2;

    /// <summary>说明文本用途码：1=未授权解释，2=对话框标签，3=票据存不下。</summary>
    internal const uint TextPurposeAuthorizationExplanation = 1;
    internal const uint TextPurposeTicketUnwritable = 3;

    /// <summary>函数表版本门：装载时请求这个值，表自报不等于它即视为模块缺席。
    /// <para>2 = 握手新增了"未授权即拒"这一语义（表形状未变，仍是 size/version + 3 个成员）。
    /// 模块只应答版本 2；这里若还停在 1，即使已解锁也会被模块拒绝，
    /// 于是整条链路按"模块缺席"静默降级——症状和不装模块一模一样，最难归因。</para></summary>
    internal const uint ApiVersion = 2;

    /// <summary>本模块产出的载荷 kind（内核 <c>FFF3FPDynamicMetadataKind::ExternalColorTransform</c>）。</summary>
    internal const uint PayloadKind = 3;

    internal const uint PayloadKindVersion = 1;

    /// <summary>一次调用可能产出的最大字节数（= sizeof(FFF3FPColorTransformPayload)）。</summary>
    internal const uint PayloadCapacity = 431296;

    // 返回码（FFFColorTransformResult）。宿主只据此分流"注入 / 跳过 / 报错"。
    internal const int ResultSuccess = 0;
    internal const int ResultNoMetadata = 1;
    internal const int ResultInvalidArgument = 2;
    internal const int ResultTooSmall = 3;
    internal const int ResultUnsupported = 4;
    /// <summary>版本 2 新增：表已在手但授权中途失效（票据到期或被撤）。模块在不解引用
    /// 任何入参的前提下拒绝产出，缓冲区一字节不动。宿主应停止注入并回落基底。</summary>
    internal const int ResultUnauthorized = 5;
}

/// <summary>FFFColorTransformInput（32 字节：4+4 头 + 三个 8 字节字段）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FffColorTransformInput
{
    public uint Size;
    public uint Version;
    /// <summary>借用的帧边数据，仅在本次调用内有效，模块不得保留。</summary>
    public nint Metadata;
    public ulong MetadataSize;
    /// <summary>演示序号。<b>模块不读它、也不抄进载荷</b>（真源头 <c>Api.h</c> 明写
    /// "not read, not sent"，内核 kind=3 结构里也没有帧号字段）。它存在只是为了让宿主
    /// 自己把注入条目的帧号打上——"取最后一条 ≤ 当前帧"的匹配是宿主的职责。</summary>
    public ulong FrameIndex;
}

/// <summary>FFFColorTransformOutput（32 字节：4 个 uint + 指针 + 2 个 uint）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FffColorTransformOutput
{
    public uint Size;
    public uint PayloadKind;
    public uint PayloadKindVersion;
    /// <summary>写入 payload 的字节数；0 = 本帧无可换算据。</summary>
    public uint PayloadSize;
    /// <summary>调用方拥有的缓冲区，容量 payloadCapacity 字节。</summary>
    public nint Payload;
    public uint PayloadCapacity;
    public uint Reserved;
}

/// <summary>FFFColorTransformApi 函数表：三个函数指针按 nint 存放，
/// 由宿主 <c>GetDelegateForFunctionPointer</c> 现取现用（形参全是指针/blittable，
/// 声明成 nint 就不给源生成器留任何按值传 421 KB 结构的机会）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FffColorTransformApiTable
{
    public uint Size;
    public uint Version;
    public nint TransformVersion;
    public nint AnalyzeFrame;
    public nint Reset;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint FffTransformVersionFn();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FffAnalyzeFrameFn(nint input, nint output);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void FffResetFn();

/// <summary>模块导出 <c>FFF_GetColorTransformApi</c> 的托管声明。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FffGetColorTransformApiFn(uint version, nint api);

/// <summary>FFF_ColorTransformAuthorizationStatus：2=已授权，1=未授权。
/// 握手被拒后用来分辨"模块不在"与"模块在但锁着"——两者的画面表现一模一样，
/// 只有这个读数能分开。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint FffAuthorizationStatusFn();

/// <summary>FFF_ColorTransformStatusText(purpose)：UTF-8 说明文本，未知用途返回 NULL。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate nint FffStatusTextFn(uint purpose);

/// <summary>FFF_ColorTransformAuthenticate(code)：送进解锁码，通过则签发本地票据。
/// <para>形参是 <c>const char*</c>（模块按 ASCII 数字解析），托管侧按 UTF-8 封送；
/// 返回 0/1/2（见 <see cref="ColorTransformAbi"/> 的三个常量）。</para></summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = false)]
internal delegate int FffAuthenticateFn([MarshalAs(UnmanagedType.LPUTF8Str)] string unlockCode);

/// <summary>kernel32 的可选模块装载：只负责"按文件名找到并映射"，
/// 任何一步失败都由调用方降级成"模块缺席"，这里不参与决策。</summary>
internal static partial class NativeModuleLoader
{
    /// <summary>LOAD_WITH_ALTERED_SEARCH_PATH：让模块自己的依赖（avutil/avcodec）
    /// 先在模块所在目录解析，而不是只靠进程的默认搜索顺序。</summary>
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string lpFileName, nint hFile, uint dwFlags);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryW(string lpFileName);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint hModule, string lpProcName);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint hModule);

    /// <summary>映射模块。失败返回 0（不抛）。
    /// <para><c>LOAD_WITH_ALTERED_SEARCH_PATH</c> 只在**带目录的路径**上有定义；
    /// 候选顺序的最后一手是裸文件名（交给系统搜索顺序），对它带这个旗标是文档里的
    /// 未定义行为。所以按有没有目录分隔符分两条路：带路径走 Ex 以获得"依赖先在模块
    /// 自己目录里解析"，裸名走普通 LoadLibrary。</para></summary>
    internal static nint Load(string nameOrPath)
    {
        try
        {
            var hasDirectory = nameOrPath.Contains('/') || nameOrPath.Contains('\\');
            return hasDirectory
                ? LoadLibraryExW(nameOrPath, 0, LOAD_WITH_ALTERED_SEARCH_PATH)
                : LoadLibraryW(nameOrPath);
        }
        catch (DllNotFoundException) { return 0; }
        catch (BadImageFormatException) { return 0; }
    }

    /// <summary>取导出函数地址。失败返回 0（不抛）。导出名按 ANSI（LPCSTR）传，
    /// 这是模块侧唯一导出的实际形态。</summary>
    internal static nint Resolve(nint module, string name)
    {
        if (module == 0) return 0;
        try
        {
            return GetProcAddress(module, name);
        }
        catch (EntryPointNotFoundException) { return 0; }
    }

    internal static void Release(nint module)
    {
        if (module == 0) return;
        try { FreeLibrary(module); }
        catch (DllNotFoundException) { /* 连 kernel32 都解析不了：无从释放，静默 */ }
    }
}
