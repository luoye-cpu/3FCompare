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
///   - 所有循环带硬上限（适配器/输出各 ≤8），任何 HRESULT 失败立即放弃，
///     彻底杜绝死循环。
/// </summary>
internal static partial class DxgiOutputInfo
{
    /// <summary>IDXGIFactory1 IID（CreateDXGIFactory2 请求接口）。</summary>
    private static readonly Guid IidIDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>IDXGIOutput6 IID（GetDesc1 需 Win10 1607+）。</summary>
    private static readonly Guid IidIDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    /// <summary>DXGI_ERROR_NOT_FOUND（枚举正常终止码）。</summary>
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    /// <summary>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020（HDR10 输出色彩空间）。</summary>
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 3;

    // vtable 槽位（IUnknown=0..2；各基类方法按 dxgi.h 顺序占位）：
    //   IUnknown QI/AddRef/Release = 0/1/2
    //   IDXGIObject GetPrivateData/SetPrivateData/SetPrivateDataInterface/GetParent = 3..6
    private const int SltQueryInterface = 0;
    private const int SltRelease = 2;
    private const int SltEnumAdapters1 = 12;  // IDXGIFactory1::EnumAdapters1
    private const int SltEnumOutputs = 7;     // IDXGIAdapter::EnumOutputs
    private const int SltGetDesc1 = 27;       // IDXGIOutput6::GetDesc1（0..2 IUnknown + 3..6 Object + 7..18 Output + 19..22 Output1 + 23..26 Output2..5 + 27）

    private const int MaxAdapters = 8;
    private const int MaxOutputs = 8;

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

    /// <summary>取对象 vtable 指定槽位的函数指针并转为委托。</summary>
    private static T Vt<T>(nint obj, int slot) where T : class
    {
        var vtable = Marshal.ReadIntPtr(obj);
        var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer<T>(fn);
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
            var release = Vt<ReleaseD>(factory, SltRelease);
            var qi = Vt<QueryInterfaceD>(factory, SltQueryInterface);

            for (var ai = 0u; ai < MaxAdapters; ai++)
            {
                var adapter = nint.Zero;
                var ahr = enumAdapters(factory, ai, ref adapter);
                if (ahr == DXGI_ERROR_NOT_FOUND) break;
                if (ahr < 0 || adapter == 0) break; // 非预期错误：直接放弃（绝不重试）

                try
                {
                    var enumOutputs = Vt<EnumOutputsD>(adapter, SltEnumOutputs);
                    for (var oi = 0u; oi < MaxOutputs; oi++)
                    {
                        var output = nint.Zero;
                        var ohr = enumOutputs(adapter, oi, ref output);
                        if (ohr == DXGI_ERROR_NOT_FOUND) break;
                        if (ohr < 0 || output == 0) break;

                        try
                        {
                            var output6 = nint.Zero;
                            if (qi(output, in iidOutput6, ref output6) < 0 || output6 == 0) continue;
                            try
                            {
                                var desc = new DXGI_OUTPUT_DESC1_RAW();
                                if (Vt<GetDesc1D>(output6, SltGetDesc1)(output6, ref desc) < 0) continue;
                                if (desc.Monitor != hmonitor) continue;

                                minNits = desc.MinLuminance;
                                maxNits = desc.MaxLuminance;
                                fullFrameNits = desc.MaxFullFrameLuminance;
                                hdrCapable = desc.ColorSpace >= DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                                return true;
                            }
                            finally
                            {
                                Vt<ReleaseD>(output6, SltRelease)(output6);
                            }
                        }
                        finally
                        {
                            release(output);
                        }
                    }
                }
                finally
                {
                    release(adapter);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (factory != 0) Vt<ReleaseD>(factory, SltRelease)(factory);
        }

        return false;
    }
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
