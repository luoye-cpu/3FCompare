using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Display;

/// <summary>GPU 适配器枚举（F26/A11，多显卡指定解码）。
/// 使用 Win32 EnumDisplayDevices 纯 P/Invoke（无 COM、无手写 vtable），
/// NativeAOT 完全安全。枚举所有显示适配器。</summary>
public static class GpuEnumeration
{
    private const uint DISPLAYDEVICE_FLAGS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var dev = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dev, DISPLAYDEVICE_FLAGS)) break;
                var devName = dev.DeviceName ?? string.Empty;
                var name = dev.DeviceString ?? string.Empty;
                // 设备名形如 \\.\DISPLAY1..8；按显卡描述字符串去重，
                // 同一张 GPU 的多个显示器输出只计入一次。
                var isAdapter = devName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) ||
                    devName.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase);
                if (isAdapter && !string.IsNullOrWhiteSpace(name) && seenNames.Add(name))
                {
                    result.Add(new AdapterInfo
                    {
                        Index = (int)i,
                        Description = $"{name} [{devName}]",
                        DedicatedMemoryBytes = 0,
                    });
                }
            }
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
}