// 诊断工具：验证 ComImport 接口分发 vs 裸 vtable 调用 DXGI 的行为差异
// 用法: dotnet run --project tools/DxgiInteropProbe
using System;
using System.Runtime.InteropServices;
using System.Threading;
using _3FCompare.Core.Display;

Console.WriteLine($"线程 ApartmentState={Thread.CurrentThread.GetApartmentState()}");
Console.WriteLine($"Runtime={Environment.Version} {Environment.OSVersion}");

// 变体 1：Core 的 DisplayCapabilities.ReadForWindow（生产代码路径）
Console.WriteLine("[1] Core ReadForWindow(桌面) 调用…");
var sw = System.Diagnostics.Stopwatch.StartNew();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
var t1 = Task.Run(() =>
{
    var caps = DisplayCapabilities.ReadForWindow(Win32.GetDesktopWindow());
    Console.WriteLine($"[1] 返回: {(caps == null ? "null" : $"max={caps.MaximumNits}nits")} (耗时 {sw.ElapsedMilliseconds}ms)");
});
try { t1.Wait(cts.Token); }
catch (OperationCanceledException) { Console.WriteLine($"[1] ⚠ 未在 8s 内返回（疑似死循环，与 Avalonia 进程现象一致）"); }

// 变体 2：裸 vtable 调用 EnumAdapters1（槽位 12）
Console.WriteLine("[2] 裸 vtable EnumAdapters1…");
var hr = Win32.CreateDXGIFactory1(0, in Win32.IidFactory1, out var factoryPtr);
Console.WriteLine($"[2] CreateDXGIFactory2 hr=0x{hr:X8}");
nint adapterRaw = 0;
if (hr >= 0)
{
    var vt = Marshal.ReadIntPtr(factoryPtr);
    var fn = Marshal.ReadIntPtr(vt, 12 * IntPtr.Size);
    var stub = Marshal.GetDelegateForFunctionPointer<Win32.RawEnumAdapters1>(fn);
    hr = stub(factoryPtr, 0, ref adapterRaw);
    Console.WriteLine($"[2] EnumAdapters1 hr=0x{hr:X8}, adapter={adapterRaw}");
}

// 变体 3：修正 vtable 对齐的 ComImport 接口（含 IDXGIObject 槽位 3-6）
Console.WriteLine("[3] ComImport(对齐vtable) EnumAdapters1…");
if (factoryPtr != 0)
{
    var factory = (Win32.AlignedFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
    var hr3 = factory.EnumAdapters1(0, out var adapterPtr);
    Console.WriteLine($"[3] hr=0x{hr3:X8}, adapter={adapterPtr}");
}

// 变体 4：完整裸 vtable 链（factory→EnumAdapters1→EnumOutputs→QI(Output6)→GetDesc1）
Console.WriteLine("[4] 完整裸链枚举…");
if (factoryPtr != 0)
{
    var targetMon = Win32.MonitorFromWindow(Win32.GetDesktopWindow(), 2);
    Console.WriteLine($"[4] 目标 HMONITOR={targetMon}");
    var vtF = Marshal.ReadIntPtr(factoryPtr);
    var enumA = Marshal.GetDelegateForFunctionPointer<Win32.RawEnumAdapters1>(Marshal.ReadIntPtr(vtF, 12 * IntPtr.Size));
    var release = Marshal.GetDelegateForFunctionPointer<Win32.RawRelease>(Marshal.ReadIntPtr(vtF, 2 * IntPtr.Size));
    var qi = Marshal.GetDelegateForFunctionPointer<Win32.RawQueryInterface>(Marshal.ReadIntPtr(vtF, 0));

    for (uint ai = 0; ai < 8; ai++)
    {
        nint adapter = 0;
        if (enumA(factoryPtr, ai, ref adapter) != 0 || adapter == 0) break;
        try
        {
            var vtA = Marshal.ReadIntPtr(adapter);
            var enumO = Marshal.GetDelegateForFunctionPointer<Win32.RawEnumOutputs>(Marshal.ReadIntPtr(vtA, 7 * IntPtr.Size));
            for (uint oi = 0; oi < 8; oi++)
            {
                nint output = 0;
                var ohr = enumO(adapter, oi, ref output);
                if (ohr != 0 || output == 0) { Console.WriteLine($"[4] adapter{ai} output{oi}: EnumOutputs hr=0x{ohr:X8}"); break; }
                try
                {
                    var iid6 = Win32.IidOutput6;
                    nint out6 = 0;
                    var qiHr = qi(output, ref iid6, ref out6);
                    if (qiHr != 0 || out6 == 0) { Console.WriteLine($"[4] adapter{ai} output{oi}: QI(Output6) hr=0x{qiHr:X8}"); continue; }
                    try
                    {
                        var vtO = Marshal.ReadIntPtr(out6);
                        var getDesc1 = Marshal.GetDelegateForFunctionPointer<Win32.RawGetDesc1>(Marshal.ReadIntPtr(vtO, 27 * IntPtr.Size));
                        var desc = new Win32.Desc1Raw();
                        var dhr = getDesc1(out6, ref desc);
                        Console.WriteLine($"[4] adapter{ai} output{oi}: GetDesc1 hr=0x{dhr:X8}, Monitor={desc.Monitor}(匹配={desc.Monitor == targetMon}), ColorSpace={desc.ColorSpace}, Max={desc.MaxLuminance}nits");
                    }
                    finally { release(out6); }
                }
                finally { release(output); }
            }
        }
        finally { release(adapter); }
    }
}

Console.WriteLine("完成");

internal static class Win32
{
    public static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    [DllImport("user32.dll")] public static extern nint GetDesktopWindow();

    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2")]
    public static extern int CreateDXGIFactory1(uint flags, in Guid riid, out nint ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int RawEnumAdapters1(nint @this, uint adapter, ref nint ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int RawEnumOutputs(nint @this, uint output, ref nint ppOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int RawQueryInterface(nint @this, ref Guid riid, ref nint ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint RawRelease(nint @this);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int RawGetDesc1(nint @this, ref Desc1Raw desc);

    [StructLayout(LayoutKind.Sequential)]
    public struct Desc1Raw
    {
        public ulong Name0, Name1, Name2, Name3, Name4, Name5, Name6, Name7;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public float RedPrimaryX, RedPrimaryY, GreenPrimaryX, GreenPrimaryY;
        public float BluePrimaryX, BluePrimaryY, WhitePointX, WhitePointY;
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface AlignedFactory1
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out nint ppvObject);
        [PreserveSig] uint AddRef();
        [PreserveSig] uint Release();
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, nint pData);
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, nint pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, nint pUnknown);
        [PreserveSig] int GetParent(ref Guid riid, out nint ppParent);
        [PreserveSig] int EnumAdapters(uint adapter, out nint ppAdapter);
        [PreserveSig] int MakeWindowAssociation(nint hwnd, uint flags);
        [PreserveSig] int GetWindowAssociation(out nint pWindowHandle);
        [PreserveSig] int CreateSwapChain(nint pDevice, nint pDesc, out nint ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(nint module, out nint ppAdapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out nint ppAdapter);
        [PreserveSig] int IsCurrent();
    }
}
