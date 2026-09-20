using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace _3FCompare.Core.Display;

/// <summary>
/// DXGI 1.6 输出信息读取器：IDXGIFactory1::EnumAdapters1 → IDXGIAdapter::EnumOutputs
/// → IDXGIOutput6::GetDesc1 获取显示器 HDR 亮度信息（nits）。
///
/// 实现方式决策（2026-08-22，Avalonia 迁移 M0 排障结论）：
///   - 不使用 [ComImport] 接口分发：.NET 11 运行时对 ComImport 的内置封送在
///     本机（Win11 26200）上调用 DXGI 会得到 DXGI_ERROR_INVALID_CALL
///     （vtable 槽位与签名均正确的情况下依然如此，裸函数指针调用则正常）；
///     且旧实现的适配器循环仅对 NOT_FOUND 跳出，遇到 INVALID_CALL 会无限
///     重试 → 表现为「打开媒体卡死」。WinForms 版此前因此从未真正读到亮度。
///   - 改为读取对象 vtable 原始函数指针 + GetDelegateForFunctionPointer 调用；
///     历史上 delegate* 崩溃的真实原因是旧接口声明缺少 IDXGIObject 基类槽位
///     （vtable 整体错位 4 槽），并非裸调用本身有问题。
///   - 所有循环带硬上限（适配器/输出各 ≤32），任何 HRESULT 失败立即放弃，
///     彻底杜绝死循环。
///   - QueryInterface / Release <b>必须</b>从当前对象自身的 vtable 取，不能复用
///     factory 的那一份：不同 DXGI 类有各自的 QI/Release 实现地址。复用会导致
///     对 output 查询 IDXGIOutput6 恒定返回 E_NOINTERFACE，全部输出被跳过
///     （2026-09-16 实测确认，这是"HDR 亮度永远回退默认值"的真正根因）。
/// </summary>
internal static partial class DxgiOutputInfo
{
    /// <summary>IDXGIFactory1 IID（CreateDXGIFactory2 请求接口）。</summary>
    private static readonly Guid IidIDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>IDXGIOutput6 IID（GetDesc1 需 Win10 1607+）。</summary>
    private static readonly Guid IidIDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    /// <summary>DXGI_ERROR_NOT_FOUND（枚举正常终止码）。</summary>
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    /// <summary>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020（HDR10 输出色彩空间）。
    /// dxgicommon.h: DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12。</summary>
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12;

    // vtable 槽位（IUnknown=0..2；各基类方法按 dxgi.h 声明顺序占位）。
    //
    // 槽位算错不会抛异常、只是静默读到错误数据，极难定位，所以这里把方法逐个列全，
    // 防止后人再"心算累加"数错：
    //   IUnknown        : 0 QueryInterface / 1 AddRef / 2 Release
    //   IDXGIObject     : 3 GetPrivateData / 4 SetPrivateData
    //                     5 SetPrivateDataInterface / 6 GetParent
    //   IDXGIOutput     : 7  GetDesc
    //                     8  GetDisplayModeList
    //                     9  FindClosestMatchingMode
    //                     10 WaitForVBlank
    //                     11 TakeOwnership
    //                     12 ReleaseOwnership
    //                     13 GetGammaControlCapabilities
    //                     14 SetGammaControl
    //                     15 GetGammaControl
    //                     16 SetDisplaySurface        ←⚠ 极易漏掉，见下方"12 个方法"
    //                     17 GetDisplaySurfaceData
    //                     18 GetFrameStatistics
    //   IDXGIOutput1    : 19 GetDisplayModeList1
    //                     20 FindClosestMatchingMode1
    //                     21 GetDisplaySurfaceData1
    //                     22 DuplicateOutput
    //   IDXGIOutput2    : 23 SupportsOverlays
    //   IDXGIOutput3    : 24 CheckOverlaySupport
    //   IDXGIOutput4    : 25 CheckOverlayColorSpaceSupport
    //   IDXGIOutput5    : 26 DuplicateOutput1          ← 不是"系统保留槽位"，是真实方法
    //   IDXGIOutput6    : 27 GetDesc1                  ← 本文件要用的就是这一格
    //                     28 CheckHardwareCompositionSupport
    //
    // 【为什么是 27 而不是 26 —— 曾两次算错，务必读完】
    // 第一版写 27（对），后因"按头文件心算 IDXGIOutput 为 11 个方法"被改成 26（错）。
    // 真因：IDXGIOutput 是 **12** 个方法，不是 11 —— 漏掉了 `SetDisplaySurface`
    // （SDK 10.0.26100 的 dxgi.h，位于 GetGammaControl 之后、GetDisplaySurfaceData 之前）。
    // 少算这一个 ⇒ 后面所有槽位整体前移一格 ⇒ 累加出 26。
    //
    // 2026-09-16 双路交叉验证（真机 Win11 + HDR 显示器 \\.\DISPLAY5，HMONITOR 0x119d13bb）：
    // ① 逐槽位裸调（每槽独立子进程，崩溃互不影响）：
    //     槽位 7  → S_OK，DeviceName='\\.\DISPLAY5'、Monitor=0x119d13bb
    //               ⇒ 证明 vtable 基址、调用约定、结构体偏移全部正确；
    //     槽位 26 → 0xC0000005 访问违规（一处实测亦见 0x887A0004 DXGI_ERROR_UNSUPPORTED）
    //               因为 26 真实身份是 IDXGIOutput5::DuplicateOutput1，
    //               签名 (IUnknown*, UINT, UINT, const DXGI_FORMAT*, IDXGIOutputDuplication**)
    //               与我们要的 (this, DXGI_OUTPUT_DESC1*) 完全不匹配；
    //     槽位 27 → S_OK 且吐出完整合法的 DXGI_OUTPUT_DESC1：
    //               name='\\.\DISPLAY5' attached=1 BitsPerColor=10 ColorSpace=12
    //               Monitor=0x119d13bb MaxLuminance=417.7118；
    //     槽位 28 → 只写 4 字节（UINT* flags）= CheckHardwareCompositionSupport。
    // ② 改用 [ComImport] 按"12 个方法"声明 IDXGIOutput6，由 CLR 自行计算 vtable：
    //     得到的 GetDesc1 结果与裸槽位 27 **逐字段一致**（mon/max/cs 全等），
    //     而按"11 个方法"声明时 CLR 同样算出 26 并照样 AV
    //     ⇒ 不是"CLR 与手写不一致"，是声明本身就少算了一格。
    //
    // 结论：GetDesc1 = 27，且 26 是 DuplicateOutput1（调到它会崩，不是保留位）。
    // ⚠ 本文件所有槽位常量都以实测为准，改动前必须重新实测，切勿再靠数方法个数。
    private const int SltQueryInterface = 0;
    private const int SltRelease = 2;
    private const int SltEnumAdapters1 = 12;  // IDXGIFactory1::EnumAdapters1
    private const int SltEnumOutputs = 7;     // IDXGIAdapter::EnumOutputs
    private const int SltGetDesc = 8;         // IDXGIAdapter::GetDesc（IUnknown 0..2 + Object 3..6 + EnumOutputs 7）
    private const int SltGetDesc1 = 27;       // IDXGIOutput6::GetDesc1（实测确认，见上方说明）

    /// <summary>DXGI_ADAPTER_DESC 大小（字节）：WCHAR[128] 描述 + 4 个 UINT + 3 个 SIZE_T + LUID。</summary>
    private const int AdapterDescSize = 256 + 4 * 4 + 3 * 8 + 8;
    /// <summary>DXGI_ADAPTER_DESC 内各字段的字节偏移（256 描述 + 4×UINT + 3×SIZE_T + LUID）。</summary>
    private const int OffVendorId = 256;
    private const int OffDeviceId = 260;
    private const int OffDedicatedVideoMemory = 256 + 16;
    private const int OffAdapterLuid = 256 + 16 + 3 * 8;

    /// <summary>枚举硬上限。8 个在多屏/多卡（含未连接输出的独显）环境下会被轻易触及，
    /// 触顶后剩余适配器被静默漏检 ⇒ 目标显示器可能根本没被枚举到。取 32 留足余量；
    /// 上限只用于防死循环，正常情况下循环会先撞上 DXGI_ERROR_NOT_FOUND 而终止。</summary>
    private const int MaxAdapters = 32;
    private const int MaxOutputs = 32;

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory2(uint flags, in Guid riid, out nint ppFactory);

    // ---- 裸 COM 调用委托（x64 stdcall：第一参数为接口 this 指针）----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceD(nint self, in Guid riid, ref nint ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1D(nint self, uint adapterIndex, ref nint ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsD(nint self, uint outputIndex, ref nint ppOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1D(nint self, ref DXGI_OUTPUT_DESC1_RAW desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescD(nint self, nint pDesc);

    /// <summary>函数指针 → 委托缓存。
    ///
    /// <para>为什么需要缓存：<c>GetDelegateForFunctionPointer</c> 每次调用都要分配委托
    /// 并构造互调存根，而一次亮度读取里 <c>Release</c> 会在 finally 中被反复调用
    /// （适配器、输出、Output6 各一次，连最外层 factory 的释放也是一次新分配）。</para>
    ///
    /// <para>为什么必须<b>按函数指针为键</b>而不是缓存成单个静态字段：<c>IDXGIFactory</c>、
    /// <c>IDXGIAdapter</c>、<c>IDXGIOutput6</c> 各有自己的 vtable，<c>Release</c> 的实现地址
    /// 并不相同。若共用一个静态 Release 委托，就会把 A 的实现调用到 B 的对象上
    /// （引用计数减到别人的对象上，后果是野指针或漏释放）。同一函数指针在进程内稳定不变，
    /// 因此按键缓存是安全的。</para></summary>
    private static readonly ConcurrentDictionary<nint, Delegate> DelegateCache = new();

    /// <summary>取对象 vtable 指定槽位的函数指针并转为委托（同函数指针复用缓存实例）。</summary>
    private static T Vt<T>(nint obj, int slot) where T : class
    {
        var vtable = Marshal.ReadIntPtr(obj);
        var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        if (DelegateCache.TryGetValue(fn, out var cached) && cached is T typed) return typed;
        // 不同槽位/不同接口的函数指针不会相同，故无需把 slot 计入键
        var created = Marshal.GetDelegateForFunctionPointer<T>(fn);
        DelegateCache[fn] = (Delegate)(object)created;
        return created;
    }

    /// <summary>
    /// 尝试读取指定 HMONITOR 的亮度信息。任何失败返回 false（调用方回退默认值）；
    /// 所有循环带硬上限，保证必然返回。
    /// </summary>
    public static bool TryReadLuminance(
        nint hmonitor,
        out float minNits,
        out float maxNits,
        out float fullFrameNits,
        out bool hdrCapable)
    {
        minNits = 0f;
        maxNits = 0f;
        fullFrameNits = 0f;
        hdrCapable = false;
        if (hmonitor == 0) return false;

        var iidOutput6 = IidIDXGIOutput6;
        var hr = CreateDXGIFactory2(0, in IidIDXGIFactory1, out var factory);
        if (hr < 0 || factory == 0) return false;

        try
        {
            var enumAdapters = Vt<EnumAdapters1D>(factory, SltEnumAdapters1);

            // 循环变量提到外面：正常终止时 ai 会等于 MaxAdapters，据此判断是否触顶
            uint ai;
            for (ai = 0u; ai < MaxAdapters; ai++)
            {
                var adapter = nint.Zero;
                var ahr = enumAdapters(factory, ai, ref adapter);
                if (ahr == DXGI_ERROR_NOT_FOUND) break;
                if (ahr < 0 || adapter == 0) break; // 非预期错误：直接放弃（绝不重试）

                try
                {
                    var enumOutputs = Vt<EnumOutputsD>(adapter, SltEnumOutputs);
                    uint oi;
                    for (oi = 0u; oi < MaxOutputs; oi++)
                    {
                        var output = nint.Zero;
                        var ohr = enumOutputs(adapter, oi, ref output);
                        if (ohr == DXGI_ERROR_NOT_FOUND) break;
                        if (ohr < 0 || output == 0) break;

                        try
                        {
                            // QueryInterface / Release 必须从【当前对象自身】的 vtable 取：
                            // DXGI 的 CDXGIFactory / CDXGIAdapter / CDXGIOutput 各自实现了
                            // 自己的 QI/Release（C++ 里不是同一个函数地址）。历史上这里复用了
                            // factory 的 QI 去查 output 的 IDXGIOutput6，得到 E_NOINTERFACE
                            // (0x80004002)，于是每个输出都被 continue 掉 ⇒
                            // "未找到匹配 HMONITOR，HDR 亮度回退默认值"。
                            // 这是本方法最致命的一处缺陷：它不报错、只静默回退，极难定位。
                            var output6 = nint.Zero;
                            if (Vt<QueryInterfaceD>(output, SltQueryInterface)(output, in iidOutput6, ref output6) < 0
                                || output6 == 0)
                                continue;
                            try
                            {
                                var desc = new DXGI_OUTPUT_DESC1_RAW();
                                if (Vt<GetDesc1D>(output6, SltGetDesc1)(output6, ref desc) < 0) continue;
                                if (desc.Monitor != hmonitor) continue;

                                minNits = desc.MinLuminance;
                                maxNits = desc.MaxLuminance;
                                fullFrameNits = desc.MaxFullFrameLuminance;
                                hdrCapable = desc.ColorSpace >= DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;

                                // 命中即留痕：现场排查"HDR 没生效"时，这一行是唯一能证明
                                // 真实亮度确实从 DXGI 读到的证据（含 nits 与色彩空间）。
                                Diagnostics.AppLog.Debug("Display",
                                    $"DXGI 亮度读取成功：HMONITOR 0x{(long)hmonitor:x} " +
                                    $"min={minNits:0.##} max={maxNits:0.##} fullFrame={fullFrameNits:0.##} " +
                                    $"bitsPerColor={desc.BitsPerColor} colorSpace={desc.ColorSpace} hdrCapable={hdrCapable}");
                                return true;
                            }
                            finally
                            {
                                Vt<ReleaseD>(output6, SltRelease)(output6);
                            }
                        }
                        finally
                        {
                            Vt<ReleaseD>(output, SltRelease)(output);
                        }
                    }

                    // 正常终止是撞上 DXGI_ERROR_NOT_FOUND（oi < MaxOutputs）；
                    // 只有触顶才说明该适配器上还有输出没枚举到，目标显示器可能被漏掉。
                    if (oi >= MaxOutputs)
                        Diagnostics.AppLog.Debug("Display",
                            $"DXGI 输出枚举达到上限 {MaxOutputs}（适配器 {ai}），该适配器上仍有输出未被枚举");
                }
                finally
                {
                    Vt<ReleaseD>(adapter, SltRelease)(adapter);
                }
            }

            // 同上：只有触顶才说明还有适配器没枚举到（多卡环境下会漏掉整张卡的输出）。
            if (ai >= MaxAdapters)
                Diagnostics.AppLog.Debug("Display",
                    $"DXGI 适配器枚举达到上限 {MaxAdapters}，仍有适配器未被枚举");
        }
        catch
        {
            return false;
        }
        finally
        {
            if (factory != 0) Vt<ReleaseD>(factory, SltRelease)(factory);
        }

        // 走到这里 = 遍历完所有适配器/输出都没匹配上目标 monitor。
        // 这是"HDR 能力与峰值亮度静默回退默认值"的唯一收口处，必须留痕，
        // 否则"槽位算错/上限截断"与"显示器真的不支持 DXGI 1.6"在现场完全无法区分。
        Diagnostics.AppLog.Debug("Display",
            $"未找到匹配 HMONITOR 0x{(long)hmonitor:x} 的 DXGI 输出，HDR 亮度回退默认值");
        return false;
    }

    /// <summary>
    /// 枚举系统内所有 DXGI 适配器（A11「多显卡指定」的正确数据源）。
    ///
    /// <para><b>为什么必须用 DXGI 而不是 Win32 EnumDisplayDevices</b>：内核
    /// <c>PlayerVideoRenderer::EnsureDevice()</c> 选择适配器走的是
    /// <c>IDXGIFactory1::EnumAdapters1(adapterIndex)</c>，索引空间是 <b>DXGI 适配器</b>；
    /// 而 <c>EnumDisplayDevices</c> 枚举的是 <b>DISPLAY 显示设备</b>，两者数量与顺序都可能不同
    /// （典型反例：Optimus 笔记本的独显可能没有任何输出，压根不在 DISPLAY 列表里）。
    /// 托管侧若用后者、内核用前者，即使把索引传下去也会指向错误的卡 ——
    /// 这是 A11 除"没接线"之外的第二层缺陷。</para>
    ///
    /// 本方法返回的 <c>Index</c> 即 <c>EnumAdapters1</c> 的下标，与内核一一对应。
    /// 任何失败返回空列表（调用方回落旧枚举），保证不抛异常。
    /// </summary>
    internal static List<AdapterDescriptor> EnumerateAdapters()
    {
        var result = new List<AdapterDescriptor>();
        var hr = CreateDXGIFactory2(0, in IidIDXGIFactory1, out var factory);
        if (hr < 0 || factory == 0) return result;

        try
        {
            var enumAdapters = Vt<EnumAdapters1D>(factory, SltEnumAdapters1);

            for (var ai = 0u; ai < MaxAdapters; ai++)
            {
                var adapter = nint.Zero;
                var ahr = enumAdapters(factory, ai, ref adapter);
                if (ahr == DXGI_ERROR_NOT_FOUND) break;
                if (ahr < 0 || adapter == 0) break; // 非预期错误：直接放弃（绝不重试）

                try
                {
                    var pDesc = Marshal.AllocHGlobal(AdapterDescSize);
                    try
                    {
                        if (Vt<GetAdapterDescD>(adapter, SltGetDesc)(adapter, pDesc) < 0) continue;
                        // Description 为 WCHAR[128]，不保证以 '\0' 结尾 ⇒ 手工截断
                        var raw = Marshal.PtrToStringUni(pDesc, 128) ?? string.Empty;
                        var nul = raw.IndexOf('\0');
                        var name = (nul >= 0 ? raw[..nul] : raw).Trim();
                        var dedicated = (ulong)Marshal.ReadInt64(pDesc, OffDedicatedVideoMemory);
                        var luidLow = (uint)Marshal.ReadInt32(pDesc, OffAdapterLuid);
                        var luidHigh = Marshal.ReadInt32(pDesc, OffAdapterLuid + 4);
                        result.Add(new AdapterDescriptor
                        {
                            Index = (int)ai,
                            Description = name,
                            DedicatedVideoMemory = dedicated,
                            VendorId = (uint)Marshal.ReadInt32(pDesc, OffVendorId),
                            DeviceId = (uint)Marshal.ReadInt32(pDesc, OffDeviceId),
                            AdapterLuid = ((long)luidHigh << 32) | luidLow,
                        });
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pDesc);
                    }
                }
                finally
                {
                    // 同 TryReadLuminance：Release 取自适配器自身 vtable，不能复用 factory 的
                    Vt<ReleaseD>(adapter, SltRelease)(adapter);
                }
            }
        }
        catch
        {
            // 失败时返回已收集到的部分（或空），由调用方回落
        }
        finally
        {
            if (factory != 0) Vt<ReleaseD>(factory, SltRelease)(factory);
        }

        return result;
    }
}

/// <summary>DXGI 适配器描述（<see cref="AdapterInfo"/> 的原始形态）。
///
/// <para>带上 VendorId / DeviceId / AdapterLuid 是为了<b>可跨侧比对</b>：内核侧
/// <c>EnsureDevice()</c> 的诊断日志会打出实际建设备的适配器 vendor/device/luid，
/// 托管侧枚举结果必须与之一一对应，才能证明"用户选的第 N 项"真的是"第 N 张卡"。
/// 光看描述字符串不够——同型号双卡描述完全相同。</para></summary>
internal readonly record struct AdapterDescriptor
{
    public int Index { get; init; }
    public string Description { get; init; }
    public ulong DedicatedVideoMemory { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public long AdapterLuid { get; init; }
}

/// <summary>
/// DXGI_OUTPUT_DESC1 原始布局（无字符串封送：DeviceName 为 WCHAR[32]，此处以
/// 8 个 ulong 占位——调用方不使用该字段，只要求整体偏移与原生结构一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTPUT_DESC1_RAW
{
    public ulong DeviceName0, DeviceName1, DeviceName2, DeviceName3;
    public ulong DeviceName4, DeviceName5, DeviceName6, DeviceName7; // WCHAR[32] = 64B
    public int Left, Top, Right, Bottom;    // RECT
    public int AttachedToDesktop;
    public int Rotation;
    public nint Monitor;
    public uint BitsPerColor;
    public int ColorSpace;
    public float RedPrimaryX, RedPrimaryY, GreenPrimaryX, GreenPrimaryY;
    public float BluePrimaryX, BluePrimaryY, WhitePointX, WhitePointY;
    public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
}
