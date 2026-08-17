using System.Runtime.InteropServices;

namespace _3FCompare.Core.Display;

/// <summary>
/// DXGI 1.6 输出信息读取器：通过 IDXGIFactory1 → IDXGIAdapter → IDXGIOutput
/// → IDXGIOutput6::GetDesc1 获取显示器 HDR 亮度信息（nits）。
///
/// 设计约束（NativeAOT 安全）：
///   - 不使用手写 COM vtable（delegate* 曾导致 0xC0000005 崩溃）；
///   - 全部通过 [ComImport] 接口 + LibraryImport 导出调用，由运行时生成
///     marshalling stub（JIT 与 NativeAOT 均支持；
///   - 所有调用包裹在 try/catch 中，失败时静默回退（调用方使用默认值）。
///
/// 亮度语义（DXGI_OUTPUT_DESC1）：
///   - MaxLuminance：显示器峰值亮度（nits；0 = 未知）
///   - MaxFullFrameLuminance：全屏亮度（nits；0 = 未知）
///   - MinLuminance：黑电平（nits）
///   - ColorSpace >= DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020(3)：HDR 输出就绪
/// </summary>
internal static partial class DxgiOutputInfo
{
    /// <summary>IDXGIFactory1 IID（CreateDXGIFactory2 请求接口）。</summary>
    private static readonly Guid IidIDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>IDXGIOutput6 IID（GetDesc1 需 Win10 1607+）。</summary>
    private static readonly Guid IidIDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    /// <summary>DXGI_ERROR_NOT_FOUND（枚举终止码）。</summary>
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    /// <summary>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020（HDR10 输出色彩空间）。</summary>
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 3;

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory2(uint flags, in Guid riid, out nint ppFactory);

    /// <summary>
    /// 尝试读取指定 HMONITOR 的亮度信息。
    /// </summary>
    /// <param name="hmonitor">目标显示器句柄。</param>
    /// <param name="minNits">黑电平（nits）。</param>
    /// <param name="maxNits">峰值亮度（nits）。</param>
    /// <param name="fullFrameNits">全屏亮度（nits）。</param>
    /// <param name="hdrCapable">显示器当前是否处于 HDR 输出色彩空间。</param>
    /// <returns>找到并读取到匹配输出时为 true；否则 false（调用方回退默认值）。</returns>
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

        try
        {
            var hr = CreateDXGIFactory2(0, IidIDXGIFactory1, out var factoryPtr);
            if (hr < 0 || factoryPtr == 0) return false;

            try
            {
                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
                try
                {
                    for (var adapterIndex = 0u; ; adapterIndex++)
                    {
                        var enumHr = factory.EnumAdapters1(adapterIndex, out var adapter);
                        if (enumHr == DXGI_ERROR_NOT_FOUND) break;
                        if (enumHr < 0 || adapter is null) continue;

                        try
                        {
                            for (var outputIndex = 0u; ; outputIndex++)
                            {
                                var outHr = adapter.EnumOutputs(outputIndex, out var output);
                                if (outHr == DXGI_ERROR_NOT_FOUND) break;
                                if (outHr < 0 || output is null) continue;

                                try
                                {
                                    // 升级到 IDXGIOutput6 以读取 HDR 亮度（GetDesc1 位于 vtable slot 23）
                                    var qiHr = output.QueryInterface(IidIDXGIOutput6, out var out6Ptr);
                                    if (qiHr < 0 || out6Ptr == 0) continue;

                                    try
                                    {
                                        var output6 = (IDXGIOutput6)Marshal.GetObjectForIUnknown(out6Ptr);
                                        try
                                        {
                                            var descHr = output6.GetDesc1(out var desc);
                                            if (descHr < 0) continue;
                                            if (desc.Monitor != hmonitor) continue;

                                            minNits = desc.MinLuminance;
                                            maxNits = desc.MaxLuminance;
                                            fullFrameNits = desc.MaxFullFrameLuminance;
                                            hdrCapable = desc.ColorSpace >= DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                                            return true;
                                        }
                                        finally
                                        {
                                            Marshal.FinalReleaseComObject(output6);
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.Release(out6Ptr); // 释放 QueryInterface 的原始引用
                                    }
                                }
                                finally
                                {
                                    Marshal.FinalReleaseComObject(output);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(factory);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr); // 释放 CreateDXGIFactory2 的原始引用
            }
        }
        catch
        {
            // COM/RCW 不可用等任何异常：静默回退（调用方使用默认参数）
            return false;
        }

        return false;
    }
}

/// <summary>IDXGIFactory1（vtable 0..9；仅声明调用所需的顺序方法）。</summary>
[ComImport]
[Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
    [PreserveSig]
    int QueryInterface(ref Guid riid, out nint ppvObject);

    [PreserveSig]
    uint AddRef();

    [PreserveSig]
    uint Release();

    // ---- IDXGIFactory ----
    [PreserveSig]
    int EnumAdapters(uint adapter, out IDXGIAdapter ppAdapter);

    [PreserveSig]
    int MakeWindowAssociation(nint hwnd, uint flags);

    [PreserveSig]
    int GetWindowAssociation(out nint pWindowHandle);

    [PreserveSig]
    int CreateSwapChain(nint pDevice, nint pDesc, out nint ppSwapChain);

    [PreserveSig]
    int CreateSoftwareAdapter(nint module, out IDXGIAdapter ppAdapter);

    // ---- IDXGIFactory1 ----
    [PreserveSig]
    int EnumAdapters1(uint adapter, out IDXGIAdapter ppAdapter);

    [PreserveSig]
    int IsCurrent();
}

/// <summary>IDXGIAdapter（vtable 0..5）。</summary>
[ComImport]
[Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter
{
    [PreserveSig]
    int QueryInterface(ref Guid riid, out nint ppvObject);

    [PreserveSig]
    uint AddRef();

    [PreserveSig]
    uint Release();

    [PreserveSig]
    int EnumOutputs(uint output, out IDXGIOutput ppOutput);

    [PreserveSig]
    int GetDesc(out DXGI_ADAPTER_DESC pDesc);

    [PreserveSig]
    int CheckInterfaceSupport(ref Guid interfaceName, out long pUMDVersion);
}

/// <summary>IDXGIOutput 基础接口（vtable 0..3 QI 升级用）。</summary>
[ComImport]
[Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput
{
    [PreserveSig]
    int QueryInterface(ref Guid riid, out nint ppvObject);

    [PreserveSig]
    uint AddRef();

    [PreserveSig]
    uint Release();

    [PreserveSig]
    int GetDesc(out nint pDesc);
}

/// <summary>
/// IDXGIOutput6（Win10 1607+；GetDesc1 位于 vtable slot 23）。
/// 中间方法以简化签名声明（仅需保证 vtable 顺序连续）。
/// </summary>
[ComImport]
[Guid("068346e8-aaec-4b84-add7-137f513f77a1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput6
{
    [PreserveSig]
    int QueryInterface(ref Guid riid, out nint ppvObject);

    [PreserveSig]
    uint AddRef();

    [PreserveSig]
    uint Release();

    // ---- IDXGIOutput ----
    [PreserveSig]
    int GetDesc(out nint pDesc);

    [PreserveSig]
    int GetDisplayModeList(uint enumFormat, uint flags, ref uint pNumModes, nint pDesc);

    [PreserveSig]
    int FindClosestMatchingMode(nint pModeToMatch, out nint pClosestMatch, nint pConcernedDevice);

    [PreserveSig]
    int WaitForVBlank();

    [PreserveSig]
    int TakeOwnership(nint pDevice, bool exclusive);

    [PreserveSig]
    void ReleaseOwnership();

    [PreserveSig]
    int GetGammaControlCapabilities(out nint pGammaCaps);

    [PreserveSig]
    int SetGammaControl(uint numArraysEntries, nint pGammaRamp);

    [PreserveSig]
    int GetGammaControl(out nint pGammaRamp);

    [PreserveSig]
    int SetDisplaySurface(nint pScanoutSurface);

    [PreserveSig]
    int GetDisplaySurfaceData(nint pDestinationSurface);

    [PreserveSig]
    int GetFrameStatistics(out nint pStats);

    // ---- IDXGIOutput1 ----
    [PreserveSig]
    int GetDisplayModeList1(uint enumFormat, uint flags, ref uint pNumModes, nint pDesc);

    [PreserveSig]
    int FindClosestMatchingMode1(nint pModeToMatch, out nint pClosestMatch, nint pConcernedDevice);

    [PreserveSig]
    int GetDisplaySurfaceData1(nint pDestinationSurface);

    [PreserveSig]
    int DuplicateOutput(nint pDevice, out nint ppOutputDuplication);

    // ---- IDXGIOutput2 ----
    [PreserveSig]
    int SupportsOverlays();

    // ---- IDXGIOutput3 ----
    [PreserveSig]
    int CheckOverlaySupport(uint enumFormat, nint pConcernedDevice, out uint pFlags);

    // ---- IDXGIOutput4 ----
    [PreserveSig]
    int CheckOverlayColorSpaceSupport(uint colorSpace, nint pConcernedDevice, out uint pFlags);

    // ---- IDXGIOutput5 ----
    [PreserveSig]
    int DuplicateOutput1(nint pDevice, uint flags, uint supportedFormatsCount, nint pSupportedFormats, out nint ppOutputDuplication);

    // ---- IDXGIOutput6 ----
    [PreserveSig]
    int GetDesc1(out DXGI_OUTPUT_DESC1 pDesc);
}

/// <summary>DXGI_ADAPTER_DESC（GetDesc 输出；本模块仅占位，不实际调用）。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_ADAPTER_DESC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;

    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public long AdapterLuid;
}

/// <summary>DXGI_OUTPUT_DESC1（IDXGIOutput6::GetDesc1 输出）。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_OUTPUT_DESC1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public nint Monitor;
    public uint BitsPerColor;
    public int ColorSpace;
    public float RedPrimaryX;
    public float RedPrimaryY;
    public float GreenPrimaryX;
    public float GreenPrimaryY;
    public float BluePrimaryX;
    public float BluePrimaryY;
    public float WhitePointX;
    public float WhitePointY;
    public float MinLuminance;
    public float MaxLuminance;
    public float MaxFullFrameLuminance;
}