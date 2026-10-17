using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend;

/// <summary>
/// 原生二进制的**架构一致性**守卫（ARM 前瞻准备，docs/51）。
///
/// <para><b>为什么需要它</b>：本项目把原生 DLL 作为<b>嵌入资源</b>随程序集分发
/// （csproj 的 <c>EmbeddedResource LogicalName="FFF.Native.dll"</c> 等四处），
/// 而 <c>LogicalName</c> 是<b>架构中立</b>的。一旦开始同时产出 x64 与 arm64 包，
/// 就会退化成"两条产线写同一个文件名"，此时：</para>
/// <list type="number">
/// <item>构建期守卫（csproj 的 <c>CheckNativeGraphicsDlls</c> 等）过去只查
/// <c>Exists()</c>，**会照常放行**一份 x64 的 DLL 进 arm64 包；</item>
/// <item>运行期解压又按 <c>File.Exists</c> 早退（<c>ExtractEmbedded</c> 注释里的
/// "已存在则跳过"策略），于是<b>上一次另一个架构</b>留下的同名 DLL 会被一直沿用。</item>
/// </list>
/// <para>两者都不报错、只表现为"启动即崩"或"静默回落演示模式"，与
/// docs/14 §2.2 记录过的那类事故同形。本类把"这份 DLL 到底是不是当前进程能加载的架构"
/// 变成一次**显式断言**，让失败发生在能被读懂的位置。</para>
///
/// <para><b>与 ARM64EC 的关系</b>：Arm64EC 二进制的 PE 头仍是 <c>0x8664</c>（x64），
/// 只是带 ARM64X 标记，因此它能被 x64 进程加载——本守卫据此把 EC 视作"x64 兼容"，
/// 不会误伤 Arm64EC 路线（详见 docs/51 §3）。</para>
/// </summary>
public static class NativeArchGuard
{
    // PE 机器类型（IMAGE_FILE_MACHINE_*）。
    private const ushort ImageFileMachineI386 = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;   // x64；Arm64EC 亦用此值
    private const ushort ImageFileMachineArm64 = 0xAA64;

    /// <summary>PE 头里读出的机器类型；不是合法 PE 时返回 0。</summary>
    public enum PeMachine
    {
        /// <summary>不是可识别的 PE 映像（读不出 / 魔数不符）。</summary>
        Unknown = 0,
        /// <summary>0x014C —— 32 位 x86。</summary>
        X86 = ImageFileMachineI386,
        /// <summary>0x8664 —— x64（Arm64EC 也会报到这一档，见类注释）。</summary>
        X64 = ImageFileMachineAmd64,
        /// <summary>0xAA64 —— 纯 ARM64。</summary>
        Arm64 = ImageFileMachineArm64,
    }

    /// <summary>当前进程自身的架构（用于比对）。</summary>
    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    /// <summary>把 <see cref="PeMachine"/> 映射为 .NET 的 <see cref="Architecture"/>。
    /// Arm64EC 会映射到 <see cref="Architecture.X64"/>（它的 PE 头就是 x64），
    /// 这正是我们想要的：x64 进程能加载它。</summary>
    public static Architecture ToArchitecture(PeMachine machine) => machine switch
    {
        PeMachine.X64 => Architecture.X64,
        PeMachine.Arm64 => Architecture.Arm64,
        PeMachine.X86 => Architecture.X86,
        _ => Architecture.X64, // Unknown 不参与判定，调用方先判 Unknown
    };

    /// <summary>从 PE 字节流读出机器类型。任何结构异常返回 <see cref="PeMachine.Unknown"/>，
    /// <b>绝不抛出</b>——本方法用在启动路径与缺失兜底上，抛异常会把"诊断"变成"新的故障源"。</summary>
    public static PeMachine ReadMachine(ReadOnlySpan<byte> image)
    {
        // 布局：DOS 头 e_lfanew 在 0x3C（4 字节）→ PE 签名 "PE\0\0" → COFF 头首个字段即 Machine（2 字节）。
        const int PeHeaderOffsetField = 0x3C;
        const int MachineFieldSize = 2;

        if (image.Length < PeHeaderOffsetField + 4) return PeMachine.Unknown;

        var peOffset = BitConverter.ToInt32(image.Slice(PeHeaderOffsetField, 4));
        // 下界防负数/回绕；上界保证签名与 Machine 字段都在缓冲区内。
        if (peOffset < 0 ||
            peOffset > image.Length - (4 + 20) ||          // PE 签名(4) + COFF 头前 20 字节足够覆盖 Machine
            image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E' ||
            image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
        {
            return PeMachine.Unknown;
        }

        var machineOffset = peOffset + 4;
        if (machineOffset > image.Length - MachineFieldSize) return PeMachine.Unknown;
        var machine = BitConverter.ToUInt16(image.Slice(machineOffset, MachineFieldSize));

        return machine switch
        {
            ImageFileMachineAmd64 => PeMachine.X64,
            ImageFileMachineArm64 => PeMachine.Arm64,
            ImageFileMachineI386 => PeMachine.X86,
            _ => PeMachine.Unknown,
        };
    }

    /// <summary>从磁盘文件读出机器类型。读失败返回 <see cref="PeMachine.Unknown"/>。</summary>
    public static PeMachine ReadMachineFromFile(string path)
    {
        try
        {
            // 只读 PE 头所需的前 4 KiB：这些 DLL 有几十 MB，整文件读会白白吃 I/O 与内存。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read <= 0 ? PeMachine.Unknown : ReadMachine(buffer.AsSpan(0, read));
        }
        catch
        {
            return PeMachine.Unknown;
        }
    }

    /// <summary>一份 PE 映像能否被<b>当前进程</b>加载。
    /// <para>判定保守：机器类型读不出（<see cref="PeMachine.Unknown"/>）时返回 <c>true</c>
    /// —— 本守卫只用来<b>拦下已确证的错配</b>，不把"读不出头"升级成拒绝加载，
    /// 否则会误伤加壳/非常规产出的合法 DLL。</para></summary>
    public static bool IsLoadableByCurrentProcess(PeMachine machine)
    {
        if (machine == PeMachine.Unknown) return true;
        return ToArchitecture(machine) == ProcessArchitecture;
    }

    /// <summary>供日志/断言使用的中文可读名。</summary>
    public static string Describe(PeMachine machine) => machine switch
    {
        PeMachine.X64 => "x64",
        PeMachine.Arm64 => "ARM64",
        PeMachine.X86 => "x86",
        _ => "未知",
    };

    /// <summary>当前进程架构的可读名。</summary>
    public static string DescribeProcess() => ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "ARM64",
        Architecture.X86 => "x86",
        var other => other.ToString(),
    };
}
