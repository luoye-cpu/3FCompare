using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Display;

/// <summary>GPU 适配器枚举（F26/A11，多显卡指定解码）。
///
/// <para><b>2026-09-16 改版</b>：数据源由 Win32 <c>EnumDisplayDevices</c> 改为
/// <b>DXGI <c>EnumAdapters1</c></b>（经 <see cref="DxgiOutputInfo"/> 的裸 vtable 调用，
/// 与 WinForms 时代踩过的 ComImport 问题无关，NativeAOT 安全）。</para>
///
/// <para>改版理由：内核 <c>PlayerVideoRenderer::EnsureDevice()</c> 用的正是
/// <c>EnumAdapters1</c>，只有两者同源，托管侧传下去的索引才会指向同一张卡。
/// 旧实现枚举的是 DISPLAY 显示设备，索引空间不同 ⇒ 即使接线也会选错卡。
/// 旧路径保留为 DXGI 不可用时的兜底。</para></summary>
public static class GpuEnumeration
{
    private const uint DISPLAYDEVICE_FLAGS = 0;

    /// <summary>列表首项：不指定适配器，由内核按其默认策略（窗口所在显示器）自动选择。</summary>
    private static readonly AdapterInfo DefaultAdapter =
        new() { Index = -1, Description = "系统默认 (跟随窗口所在显示器)", DedicatedMemoryBytes = 0 };

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
        // ① 首选 DXGI：与内核 EnsureDevice() 的 EnumAdapters1 同一索引空间。
        //    这是 A11 能真正生效的前提 —— 旧实现用 Win32 EnumDisplayDevices，
        //    枚举的是 DISPLAY 显示设备，与 DXGI 适配器索引并不对应（见 DxgiOutputInfo 注释）。
        var dxgi = DxgiOutputInfo.EnumerateAdapters();
        if (dxgi.Count > 0)
        {
            // 同型号多卡的 Description **完全相同**（实测双 RTX 4060 Laptop），
            // 必须先统计重名，否则下拉框里会出现两个无法区分的条目。
            var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in dxgi)
                nameCount[a.Description] = nameCount.GetValueOrDefault(a.Description) + 1;

            var list = new List<AdapterInfo>(dxgi.Count + 1) { DefaultAdapter };
            foreach (var a in dxgi)
            {
                var name = string.IsNullOrWhiteSpace(a.Description)
                    ? $"适配器 {a.Index}" : a.Description;
                // 显存格式：<1GB 用 MB。旧实现一律按 GB 取整，导致集显显示成"0 GB"。
                if (a.DedicatedVideoMemory >= 1024UL * 1024 * 1024)
                    name += $"（{a.DedicatedVideoMemory / (1024UL * 1024 * 1024)} GB）";
                else if (a.DedicatedVideoMemory > 0)
                    name += $"（{a.DedicatedVideoMemory / (1024UL * 1024)} MB）";
                // 重名时附索引：这是用户唯一能看懂的区分方式
                if (nameCount.GetValueOrDefault(a.Description) > 1)
                    name += $" #{a.Index}";
                list.Add(new AdapterInfo
                {
                    Index = a.Index,            // == IDXGIFactory1::EnumAdapters1 的下标
                    Description = name,
                    DedicatedMemoryBytes = a.DedicatedVideoMemory,
                    VendorId = a.VendorId,
                    DeviceId = a.DeviceId,
                    AdapterLuid = a.AdapterLuid,
                });
            }
            return list;
        }

        // ② 兜底：DXGI 不可用（极罕见）时退回 Win32 EnumDisplayDevices。
        //    ⚠ 此路径的 Index 是 DISPLAY 设备索引，与内核 DXGI 索引**不保证一致**，
        //    仅用于让 UI 不至于空列表。
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
            result.Insert(0, DefaultAdapter);
            return result;
        }
        catch
        {
            return Fallback();
        }
    }

    private static List<AdapterInfo> Fallback() => new() { DefaultAdapter };
}