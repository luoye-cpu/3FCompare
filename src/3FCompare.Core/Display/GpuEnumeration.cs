using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Display;

/// <summary>GPU 适配器枚举（F26/A11，多显卡指定解码）。
/// 使用 DXGI1_2（IDXGIFactory2::EnumAdapters1 / IDXGIAdapter1::GetDesc1）手动 vtable 调用，
/// 避免 COM 互操作生成器依赖，兼容 NativeAOT。</summary>
public static class GpuEnumeration
{
    private static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");

    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();
        try
        {
            var factory = CreateDxgiFactory();
            if (factory == 0) return Fallback();

            for (uint i = 0; ; i++)
            {
                var adapter = EnumAdapters1(factory, i);
                if (adapter == 0) break;

                var desc = GetDesc1(adapter);
                if (desc.VendorId == 0 && desc.DeviceId == 0)
                {
                    ReleaseCom(adapter);
                    break;
                }

                result.Add(new AdapterInfo
                {
                    Index = (int)i,
                    Description = $"{desc.Description} (Vendor 0x{desc.VendorId:X4}, Device 0x{desc.DeviceId:X4})",
                    DedicatedMemoryBytes = desc.DedicatedVideoMemory,
                });
                ReleaseCom(adapter);
            }
            ReleaseCom(factory);

            if (result.Count == 0) return Fallback();
            result.Insert(0, new AdapterInfo { Index = -1, Description = "系统默认 (自动)", DedicatedMemoryBytes = 0 });
            return result;
        }
        catch
        {
            return Fallback();
        }
    }

    private static List<AdapterInfo> Fallback()
        => new() { new AdapterInfo { Index = -1, Description = "系统默认 (自动)", DedicatedMemoryBytes = 0 } };

    // ---- COM 互操作（手写 vtable） ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
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
        public uint Flags;
    }

    private static nint CreateDxgiFactory()
    {
        var hr = CreateDXGIFactory2(0, IID_IDXGIFactory2, out var factory);
        return hr == 0 ? factory : 0;
    }

    private static unsafe nint EnumAdapters1(nint factory, uint index)
    {
        // IDXGIFactory2 vtable: IUnknown(3) + EnumAdapters(4) + EnumAdaptersByLuid(5) + EnumAdapters1(6)
        var vtbl = *(nint**)(*(nint*)factory);
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtbl[6];
        nint adapter = 0;
        var hr = fn(factory, index, &adapter);
        return hr == 0 ? adapter : 0;
    }

    private static unsafe DxgiAdapterDesc1 GetDesc1(nint adapter)
    {
        // IDXGIAdapter1 vtable: IUnknown(3) + EnumOutputs(4) + GetDesc(5) + GetDesc1(6)
        var vtbl = *(nint**)(*(nint*)adapter);
        var fn = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)vtbl[5];
        var desc = new DxgiAdapterDesc1();
        fn(adapter, &desc);
        return desc;
    }

    private static unsafe void ReleaseCom(nint obj)
    {
        if (obj == 0) return;
        var vtbl = *(nint**)(*(nint*)obj);
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
        fn(obj);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out nint ppFactory);
}