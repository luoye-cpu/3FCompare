using System.Reflection;
using System.Runtime.InteropServices;
using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><c>DxgiOutputInfo</c> 里 <c>DXGI_ADAPTER_DESC</c> 的**原生布局**判据（RA-05）。
///
/// <para><b>独立参照从哪来</b>：注释（:109）声称"改由 <c>IntPtr.Size</c> 推导 ⇒ 语义与原生布局
/// 一一对应"，而被读的字段是不是 <c>SIZE_T</c>、它在结构里占几字节，都不能由被审的这段代码自己说。
/// 这里把 Windows SDK 头文件
/// <c>C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\dxgi.h</c> 里的
/// <c>DXGI_ADAPTER_DESC</c> 逐字段抄成一个 <see cref="StructLayoutAttribute"/> 顺序布局的托管镜像，
/// 交给 CLR 的布局引擎算偏移与尺寸 —— 那与 <c>DxgiOutputInfo</c> 里"手算 + IntPtr.Size"是<b>两条独立的路</b>，
/// 两者不一致就是其中一条错了（头文件实读：4 个 UINT 之后是 <c>DedicatedVideoMemory</c> /
/// <c>DedicatedSystemMemory</c> / <c>SharedSystemMemory</c> 三个 SIZE_T，再是 <c>LUID AdapterLuid</c>；
/// 注意 <b>没有</b> <c>SharedVideoMemory</c>，那个字段属于 <c>DXGI_ADAPTER_DESC1</c> 一族之外的旧结构）。</para>
///
/// <para><b>为什么本条的成对证据是结构性的</b>：本工程唯一在跑的目标是 x64/ARM64，
/// 那里 <c>sizeof(SIZE_T) == 8</c> ⇒ <c>Marshal.ReadInt64</c> 与 <c>Marshal.ReadIntPtr</c>
/// 取到的字节完全相同 ⇒ 任何"跑一遍看读出来的值"的判据在两种写法下都绿，红不起来。
/// 缺陷本身是"读宽写死、只有偏移跟着 <c>IntPtr.Size</c> 走"，所以判据打在**读点用的宽度**上
/// （<see cref="DedicatedVideoMemory_ReadWidth_MustFollowIntPtrSize"/>），
/// 而布局偏移由镜像结构独立复核（<see cref="AdapterDesc_Offsets_MatchClrComputedLayout"/>）。</para></summary>
public class DxgiAdapterDescLayoutTests
{
    /// <summary>逐字段抄自 SDK <c>dxgi.h::DXGI_ADAPTER_DESC</c>（WCHAR[128] + 4×UINT + 3×SIZE_T + LUID）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescNative
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public ushort[] Description;          // WCHAR[128] = 256 字节
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nint DedicatedVideoMemory;     // SIZE_T
        public nint DedicatedSystemMemory;    // SIZE_T
        public nint SharedSystemMemory;       // SIZE_T
        public uint AdapterLuidLowPart;       // LUID { LONG LowPart; LONG HighPart; }
        public uint AdapterLuidHighPart;

        public DxgiAdapterDescNative() => Description = new ushort[128];
    }

    private static int NativeOffset(string fieldName) =>
        Marshal.OffsetOf<DxgiAdapterDescNative>(fieldName).ToInt32();

    /// <summary>取 <c>DxgiOutputInfo</c> 里那几个私有布局常数（不为了测试放宽可见性，用反射读）。</summary>
    private static int PrivateConst(string name)
    {
        var f = typeof(DxgiOutputInfo).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.NotNull(f);       // 常数被改名 ⇒ 判据必须红，不许静默失效
        return f!.IsLiteral
            ? System.Convert.ToInt32(f.GetRawConstantValue())
            : System.Convert.ToInt32(f.GetValue(null));
    }

    /// <summary>偏移与总尺寸：手算常数必须等于 CLR 按镜像布局算出来的那一份。
    /// （常数是 <c>private</c>，<c>nameof</c> 也够不着 ⇒ 这里一律用字符串名，读不到就红。）</summary>
    [Fact]
    public void AdapterDesc_Offsets_MatchClrComputedLayout()
    {
        Assert.Equal(NativeOffset(nameof(DxgiAdapterDescNative.VendorId)),
            PrivateConst("OffVendorId"));
        Assert.Equal(NativeOffset(nameof(DxgiAdapterDescNative.DedicatedVideoMemory)),
            PrivateConst("OffDedicatedVideoMemory"));
        // LUID 在托管镜像里拆成两个 UINT（同一 8 字节），取低位的偏移
        Assert.Equal(NativeOffset(nameof(DxgiAdapterDescNative.AdapterLuidLowPart)),
            PrivateConst("OffAdapterLuid"));
        Assert.Equal(Marshal.SizeOf<DxgiAdapterDescNative>(),
            PrivateConst("AdapterDescSize"));
    }

    /// <summary>被读字段的原生**宽度**：两个相邻 SIZE_T 的间距就是 sizeof(SIZE_T)。</summary>
    [Fact]
    public void AdapterDesc_SizeTFieldWidth_IsThePointerWidth()
    {
        var stride = NativeOffset(nameof(DxgiAdapterDescNative.DedicatedSystemMemory)) -
                     NativeOffset(nameof(DxgiAdapterDescNative.DedicatedVideoMemory));
        Assert.Equal(IntPtr.Size, stride);                       // 独立参照给出的结论
        Assert.Equal(stride, PrivateConst("SizeOfSizeT"));
    }

    private static string ReadCoreSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "3FCompare.Core", "Display", "DxgiOutputInfo.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 逐级向上没找到 src/3FCompare.Core/Display/DxgiOutputInfo.cs " +
            "——本判据不许在找不到源码时静默通过");
    }

    /// <summary>RA-05 的成对判据：读 <c>DedicatedVideoMemory</c>（SIZE_T）那一行，
    /// 宽度必须是 <c>IntPtr.Size</c> 推出来的（<c>ReadIntPtr</c>），不能是写死的 8 字节（<c>ReadInt64</c>）。
    ///
    /// <para>牙：修复前那一行是 <c>(ulong)Marshal.ReadInt64(pDesc, OffDedicatedVideoMemory)</c>
    /// ——偏移随 <c>IntPtr.Size</c> 走、读宽写死，正是 :109 注释里点名的"静默读到错位的字段"那种坏法，
    /// 而注释却宣称已经"与原生布局一一对应"。</para></summary>
    [Fact]
    public void DedicatedVideoMemory_ReadWidth_MustFollowIntPtrSize()
    {
        var lines = ReadCoreSource().Replace("\r\n", "\n").Split('\n');
        var readSites = lines
            .Select((line, i) => (Line: line, No: i + 1))
            .Where(x => x.Line.Contains("OffDedicatedVideoMemory") && x.Line.Contains("Marshal.Read"))
            .ToList();

        // 读点消失/改名 ⇒ 判据失效，必须红（本仓有过"写好但没被调用的读数"的前科）
        Assert.NotEmpty(readSites);

        foreach (var (line, no) in readSites)
        {
            Assert.True(!line.Contains("ReadInt64"),
                $"第 {no} 行仍按写死的 8 字节读 SIZE_T 字段：{line.Trim()}");
            Assert.True(line.Contains("ReadIntPtr"),
                $"第 {no} 行的读宽没走 IntPtr.Size（应为 ReadIntPtr）：{line.Trim()}");
        }
    }

    /// <summary>行为侧（独立参照灌进真缓冲区，驱动生产读点）：把上面那份镜像结构用
    /// <see cref="Marshal.StructureToPtr"/> 摆到非托管内存里 —— 字节位置由 <b>CLR 的布局引擎</b>算，
    /// 不是 <c>DxgiOutputInfo</c> 自己那套手算常数 —— 再交给 <c>DxgiOutputInfo.ReadAdapterDesc</c> 解回来，
    /// 逐项必须与灌进去的值相等。</summary>
    ///
    /// <para><c>DedicatedVideoMemory</c> 特意取 <c>0x0000_0001_FFFF_0000</c>：高低两个 4 字节不同，
    /// 于是"按 4 字节读"会拿到 <c>0xFFFF0000</c>、"按 8 字节读"才拿到全值 ⇒ 这一项在当前目标上
    /// 钉住的是<b>读宽等于该字段的原生宽度</b>，不是"读出来看着像"。</para>
    ///
    /// <para>本条在修复前无法运行（读点埋在 <c>EnumerateAdapters</c> 的 COM 循环里，测试够不着），
    /// 它跟着 <c>ReadAdapterDesc</c> 这个抽出点一起进来；改前红由上面那条读宽判据负责。</para>
    [Fact]
    public void ReadAdapterDesc_MatchesMarshallerBuiltImage()
    {
        var native = new DxgiAdapterDescNative
        {
            VendorId = 0x1002,
            DeviceId = 0x73BF,
            SubSysId = 0x00001462,
            Revision = 195,
            DedicatedVideoMemory = (nint)0x0000_0001_FFFF_0000,   // 高/低半字不同 ⇒ 读宽错就露出来
            DedicatedSystemMemory = (nint)0x0000_0000_8000_0000,
            SharedSystemMemory = (nint)0x0000_0000_4000_0000,
            AdapterLuidLowPart = 0x1234_5678,
            AdapterLuidHighPart = 0x9ABC_DEF0,
        };
        const string name = "Radeon-X";
        for (var k = 0; k < name.Length; k++) native.Description[k] = name[k];

        var size = Marshal.SizeOf<DxgiAdapterDescNative>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(native, buf, false);

            var got = DxgiOutputInfo.ReadAdapterDesc(buf, 3);

            Assert.Equal(3, got.Index);
            Assert.Equal(name, got.Description);
            Assert.Equal((ulong)native.DedicatedVideoMemory, got.DedicatedVideoMemory);
            Assert.Equal(native.VendorId, got.VendorId);
            Assert.Equal(native.DeviceId, got.DeviceId);
            Assert.Equal(((long)native.AdapterLuidHighPart << 32) | native.AdapterLuidLowPart,
                got.AdapterLuid);
        }
        finally
        {
            Marshal.DestroyStructure<DxgiAdapterDescNative>(buf);
            Marshal.FreeHGlobal(buf);
        }
    }
}
