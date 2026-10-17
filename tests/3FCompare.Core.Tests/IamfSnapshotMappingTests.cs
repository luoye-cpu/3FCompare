using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// IAMF 四个快照字段的托管侧契约：原生 <see cref="Fff3FpSnapshot"/> →
/// <see cref="EngineSnapshot"/> 的逐值透传 + 结构体布局位置。
///
/// <para><b>布局是这里的重点，不是附属品</b>：内核 <c>FFF3FPSnapshot</c>
/// （3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h）把
/// <c>iamfActive</c>/<c>iamfChannels</c>/<c>iamfSoundSystem</c> <b>插在结构体中部</b>
/// （:200 <c>decodedAudioFrames</c> 之后、:209 <c>audioPosition100ns</c> 之前，
/// 即 :206-208），只有 <c>iamfContentChannels</c> 在尾部（:271）。把前三个也堆到尾部
/// 会让其后所有音频诊断字段按错偏移读——内核按 <c>size &gt;= sizeof</c> 放行，
/// 错偏移不报错、只静默读错值。本测试把两组偏移钉死。</para>
///
/// <para><b>版本门</b>：内核 <c>PlayerSession::GetSnapshot</c>
/// （3fp\kernel\FFF.Native\3FP\Core\PlayerSession.cpp:2093）对 <c>version</c> 严格相等，
/// IAMF 字段增长后门升到 9，尾追加 <c>iamfBinaural</c> 后再 bump 到 10，尾追加
/// <c>audioBinaural</c>（非 IAMF 多声道床双耳渲染）后再 bump 到 11；
/// <c>FFF3FP_Create</c> 对 <c>configuration.version</c> 同样是
/// 严格相等门（PlayerApi.cpp:94），内核随 audioBinaural 尾追加 bump 到 20 ⇒ 托管侧两个常量必须同步钉住。</para>
/// </summary>
public class IamfSnapshotMappingTests
{
    /// <summary>iamfActive 是内核 u32 标志（0/1）而非 bool 宽度，托管侧语义化为 bool；
    /// 其余四个标量逐值透传。四个字段取互不相同的值：漏一行映射 → 读 0；两行写反 → 两条断言同时失配。</summary>
    [Fact]
    public void IAMF四字段从原生快照逐值透传到托管快照()
    {
        var native = new Fff3FpSnapshot
        {
            Size = (uint)Marshal.SizeOf<Fff3FpSnapshot>(),
            Version = Fff3FpEngine.Fff3FpSession.SnapshotVersion,
            // 7.1.4 内容：渲染目标 12 声道，内容只声明 8 声道（4 个喇叭全程数字静音）。
            IamfActive = 1,
            IamfChannels = 12,
            IamfSoundSystem = 7,
            IamfContentChannels = 8,
        };

        var mapped = Fff3FpEngine.Fff3FpSession.MapSnapshot(in native, frameRate: 23.976);

        Assert.True(mapped.IamfActive);
        Assert.Equal(12u, mapped.IamfChannels);
        Assert.Equal(7, mapped.IamfSoundSystem);
        Assert.Equal(8u, mapped.IamfContentChannels);
    }

    /// <summary>iamfActive == 0 → false，且此时其余字段仍逐值透传（未接管不是"字段失效"）。
    /// iamfSoundSystem 取负值：内核是 std::int32_t，托管侧用 int 而非 uint。</summary>
    [Fact]
    public void 未接管时IamfActive为false且其余字段照常透传()
    {
        var native = new Fff3FpSnapshot
        {
            Size = (uint)Marshal.SizeOf<Fff3FpSnapshot>(),
            Version = Fff3FpEngine.Fff3FpSession.SnapshotVersion,
            IamfActive = 0,
            IamfChannels = 2,
            IamfSoundSystem = -3,
            IamfContentChannels = 0,
        };

        var mapped = Fff3FpEngine.Fff3FpSession.MapSnapshot(in native, frameRate: 0);

        Assert.False(mapped.IamfActive);
        Assert.Equal(2u, mapped.IamfChannels);
        Assert.Equal(-3, mapped.IamfSoundSystem);
        Assert.Equal(0u, mapped.IamfContentChannels);
    }

    /// <summary>内核两个严格相等门在托管侧的落点。<b>期望值不写死</b>：直接从**出货内核源**
    /// （<c>third_party\fff_project</c>，也就是 csproj <c>KernelDllPath</c> 嵌进 exe 的那一棵）
    /// 读 <c>PlayerSession::GetSnapshot</c> 的 <c>output.version != N</c> 与
    /// <c>PlayerApi.cpp</c> 的 <c>PlayerApiVersion = N</c>，再与托管常量比。
    /// <para>为什么必须这样：本仓刚发生过一次"版本面整体抬到 20，而这里的 11 留在原地"——
    /// 测试里硬编码的第二个真源一定会漂。名字说的"对齐"只能靠读内核的门来实现。</para></summary>
    [Fact]
    public void 快照与配置的版本常量与内核门对齐()
    {
        Assert.Equal(KernelSnapshotGate(), Fff3FpEngine.Fff3FpSession.SnapshotVersion);
        Assert.Equal(KernelPlayerApiVersion(), Fff3FpEngine.ConfigVersion);
    }

    /// <summary>
    /// 布局位置：trio 在中部（<c>DecodedAudioFrames</c> 之后、<c>AudioPosition100ns</c> 之前），
    /// <c>IamfContentChannels</c>/<c>IamfBinaural</c>/<c>AudioBinaural</c> 依次在尾部
    /// （<c>DynamicMetadataTargetedNits</c> 之后、结构体末尾）。
    /// 偏移数值按内核结构体（<c>FFF.Player.Api.h</c>）展开核算：trio 占 @136/140/144，
    /// 把 audioPosition100ns 推到 @152；尾部 @376 → iamfContentChannels @380 →
    /// iamfBinaural @384 → audioBinaural @388，结构体合计 <b>392 B</b>
    /// （8 B 对齐，audioBinaural 收尾、无尾部填充）。
    ///
    /// <para>⚠ <b>2026-10-14 更正——本段旧文字是错的，别再信</b>。旧文字称"已删除的
    /// Dolby-Vision 色彩扩展五个 u32 ABI 槽位……内核 FFF3FPSnapshot 同形删除、两边仍逐字节一致"。
    /// 事实是：<b>内核两棵树都没有删</b>。出货树 <c>FFF.Player.Api.h:219-223</c> 与内核树
    /// <c>:250-254</c> 都把 <c>hdrDpProfile</c>/<c>hdrDpLevel</c>/<c>hasHDRdpRpu</c>/
    /// <c>hasHDRdpEnhancementLayer</c>/<c>hdrDpEnhancementLayer</c> 标为
    /// "DEPRECATED ABI (slots retained, always zero)"——<b>改名 + 恒零 + 注释，槽位保留</b>。
    /// 只有托管侧工作区把它们删了（git show a55e347 里它们还叫 <c>DolbyVisionProfile</c> 等）。
    /// 后果：镜像 376 &lt; 内核 392 ⇒ 内核 <c>PlayerSession.cpp:1918</c> 的
    /// <c>size &lt; sizeof(FFF3FPSnapshot)</c> 闸门直接 <c>InvalidArgument</c>，
    /// <c>GetSnapshot</c> 整体失效；即便硬抬尺寸蒙过闸门，其后字段仍整体错位。
    /// 五个槽位已补回（2026-10-14），<b>中性化的正确动作是改名保留，不是删字段</b>。</para>
    /// </summary>
    [Fact]
    public void IAMF字段在托管结构体中的偏移与内核一致()
    {
        Assert.Equal(128, Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.DecodedAudioFrames)).ToInt32());

        var active = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfActive)).ToInt32();
        var channels = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfChannels)).ToInt32();
        var soundSystem = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfSoundSystem)).ToInt32();
        var audioPosition = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.AudioPosition100ns)).ToInt32();
        var targetedNits = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.DynamicMetadataTargetedNits)).ToInt32();
        var contentChannels = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfContentChannels)).ToInt32();
        var binaural = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfBinaural)).ToInt32();
        var audioBinaural = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.AudioBinaural)).ToInt32();

        // 中部 trio：紧跟 decodedAudioFrames（8 B）之后，且排在 audioPosition100ns 之前。
        Assert.Equal(136, active);
        Assert.Equal(140, channels);
        Assert.Equal(144, soundSystem);
        Assert.Equal(152, audioPosition);
        Assert.True(active < channels && channels < soundSystem && soundSystem < audioPosition,
            "IAMF trio 必须严格插在 decodedAudioFrames 与 audioPosition100ns 之间且顺序为 Active/Channels/SoundSystem");

        // trio 之后不能再有中部字段被漏掉：audioPosition100ns 与首个 trio 之间只隔 16 B。
        Assert.Equal(16, audioPosition - active);

        // 尾部：iamfContentChannels 是动态元数据尾字段之后的字段；iamfBinaural / audioBinaural
        // 依次追加，audioBinaural 是新的最后一个字段，每个都紧跟前者 +4。
        //
        // ⚠ 2026-10-14 更正：这四个数字原先是 360/364/368/372（sizeof 376），**比内核少 16 B**。
        //   原因不是"尾部少字段"，而是工作区把中段五个 hdrDp* DEPRECATED 槽位（20 B）
        //   整体删除了，导致其后所有字段前移（对齐吸收掉 4 B ⇒ 净差 16）。
        //   内核真值（FFF.Player.Api.h 推导）：376/380/384/388，sizeof 392。
        //   ⇒ 这里钉的绝对偏移只是"顺序 + 相邻 +4"的辅助校验；
        //     **逐字段偏移的权威判据在 SnapshotLayoutTests**（它从内核头解析逐个比对），
        //     内核再尾追加字段时那里会自动跟着走，而这里的常数需要人工同步。
        Assert.Equal(376, targetedNits);
        Assert.Equal(380, contentChannels);
        Assert.Equal(targetedNits + 4, contentChannels);
        Assert.Equal(384, binaural);
        Assert.Equal(contentChannels + 4, binaural);
        Assert.Equal(388, audioBinaural);
        Assert.Equal(binaural + 4, audioBinaural);
        // ⚠ 末字段偏移不是靠 sizeof - 4 反推：结构体含 long/ulong ⇒ 对齐为 8，sizeof 取到 8 的倍数。
        // audioBinaural @388 收尾，[388,392) 全为字段、无尾部填充；对比 iamfBinaural（@384）
        // 当末尾时其后有 4 B 填充。内核 FFF3FPSnapshot 同理（C++ /Zp8），两边 392。
        Assert.Equal(392, Marshal.SizeOf<Fff3FpSnapshot>());
        Assert.Equal(audioBinaural + 4, Marshal.SizeOf<Fff3FpSnapshot>());
        // 历史锚点：iamfBinaural 仍 @384，其 +8（@384 + 4 字段 + 4 被 audioBinaural 占用的原填充）仍等于 sizeof。
        Assert.Equal(binaural + 8, Marshal.SizeOf<Fff3FpSnapshot>());
    }

    /// <summary>iamfSoundSystem 的托管类型必须是有符号 <see cref="int"/>（内核 std::int32_t）：
    /// 写成 uint 会让负的 sound system 号变成天文数字。</summary>
    [Fact]
    public void IamfSoundSystem托管侧是有符号四字节()
    {
        var field = typeof(Fff3FpSnapshot).GetField(nameof(Fff3FpSnapshot.IamfSoundSystem));
        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
        Assert.Equal(4, Marshal.SizeOf(field.FieldType));
    }

    /// <summary>
    /// API v20 尾追加字段 <c>audioBinaural</c> 的托管契约（内核
    /// FFF.Player.Api.h:268）：它在 <c>iamfBinaural</c> 之后、结构体<b>最尾部</b>。
    /// 这里用"反射遍历所有字段，没有任何字段的偏移 ≥ 它"来钉"最后一个字段"，
    /// 而不是钉 <c>sizeof - 4</c>：结构体含 long/ulong ⇒ 对齐 8，靠末字段偏移反推
    /// 末尾是否有填充会被对齐欺骗。audioBinaural @388 收尾，
    /// [388,392) 全为字段、无尾部填充（sizeof = 392，内核 C++ 结构同为 392）。
    /// <para>2026-10-14：原为 @372 / sizeof 376，随中段五个 hdrDp* DEPRECATED 槽位
    /// 补回（+20 B，对齐吸收 4 ⇒ 净 +16）改为 388 / 392。</para>
    /// </summary>
    [Fact]
    public void AudioBinaural是托管快照结构体最后一个字段()
    {
        var offset = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.AudioBinaural)).ToInt32();
        var size = Marshal.SizeOf<Fff3FpSnapshot>();
        var iamfBinaural = Marshal.OffsetOf<Fff3FpSnapshot>(nameof(Fff3FpSnapshot.IamfBinaural)).ToInt32();

        Assert.Equal(388, offset);
        // 紧接在前一个尾字段 iamfBinaural（@384）之后。
        Assert.Equal(iamfBinaural + 4, offset);

        // 没有任何字段排在它之后（严格小于，而不是 ≤：同级无重复偏移字段）。
        foreach (var field in typeof(Fff3FpSnapshot).GetFields())
        {
            if (field.Name == nameof(Fff3FpSnapshot.AudioBinaural))
            {
                continue;
            }

            Assert.True(
                Marshal.OffsetOf<Fff3FpSnapshot>(field.Name).ToInt32() < offset,
                $"字段 {field.Name} 的偏移排在 audioBinaural 之后，说明它不是最后一个字段");
        }

        // audioBinaural 收尾：末字段 + 自身 4 B 即 sizeof，无尾部填充。
        // （不写 offset == sizeof - 4 来判"最后字段"——那个等式在有尾部填充时会误判；
        //  "最后字段"由上面的反射遍历保证，这里只钉精确宽/尺寸。）
        // 2026-10-14：376 → 392（中段五个 hdrDp* DEPRECATED 槽位补回，见本文件顶部更正）。
        Assert.Equal(392, size);
        Assert.Equal(offset + 4, size);

        // 宽度对齐内核 std::uint32_t。
        var audioBinauralField = typeof(Fff3FpSnapshot).GetField(nameof(Fff3FpSnapshot.AudioBinaural));
        Assert.NotNull(audioBinauralField);
        Assert.Equal(typeof(uint), audioBinauralField!.FieldType);
        Assert.Equal(4, Marshal.SizeOf(audioBinauralField.FieldType));
    }

    /// <summary>
    /// 版本门随字段增长同步。原用例写的是"各升一级"（GetSnapshot 10→11、Create 19→20），
    /// 那是 2026-10 之前的历史叙事；版本面统一拍平到 20 之后，"升一级"已不成立，
    /// 但它真正要保护的东西仍然成立，而且更强：<b>四个门必须同一水</b>——
    /// 内核 GetSnapshot 门、内核 PlayerApiVersion、托管 SnapshotVersion、托管 ConfigVersion。
    /// 漏改任一个的后果不是"读错值"：内核直接 InvalidArgument，快照拒收 / 会话创建失败。
    /// </summary>
    [Fact]
    public void 版本门四位一体()
    {
        var gate = KernelSnapshotGate();
        var api = KernelPlayerApiVersion();
        Assert.Equal(gate, api);                                     // 内核自己两处同水
        Assert.Equal(gate, Fff3FpEngine.Fff3FpSession.SnapshotVersion);
        Assert.Equal(gate, Fff3FpEngine.ConfigVersion);
        Assert.True(gate >= 11u,
            $"内核门读出来只有 {gate}，不像真实门（历史区间 9→11→20）⇒ 解析大概落到了别处");
    }

    /// <summary>定位仓库根（从二进制所在目录上溯，找得到出货内核源为止）。
    /// 找不到就抛：布局/版本对账测试不允许静默跳过。</summary>
    private static string RepositoryRoot()
    {
        string[] markers =
        [
            @"third_party\fff_project\FFF.Native\3FP\Core\PlayerSession.cpp",
            @"third_party\fff_project\FFF.Native\3FP\Api\PlayerApi.cpp",
        ];
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (markers.All(m => File.Exists(Path.Combine(dir.FullName, m)))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 上溯 12 层找不到出货内核源；" +
            "版本门对账测试宁可判红，不允许跳过。");
    }

    /// <summary>读 <c>PlayerSession::GetSnapshot</c> 里的 <c>output.version != N</c> 严格相等门。
    /// 限定在函数体内搜，避免命中 RenderTargetInfo / HdrSignalDiagnostics 等别的门。</summary>
    private static uint KernelSnapshotGate()
    {
        var path = Path.Combine(RepositoryRoot(),
            @"third_party\fff_project\FFF.Native\3FP\Core\PlayerSession.cpp");
        var text = File.ReadAllText(path);
        var m = Regex.Match(text,
            @"PlayerSession::GetSnapshot[\s\S]{0,300}?output\.version\s*!=\s*(\d+)");
        if (!m.Success)
            throw new InvalidOperationException(
                $"未能在 {path} 中定位 GetSnapshot 的 version 门 ⇒ 本测试无法证明任何事");
        return uint.Parse(m.Groups[1].Value);
    }

    /// <summary>读 <c>PlayerApi.cpp</c> 的 <c>constexpr std::uint32_t PlayerApiVersion = N;</c>。</summary>
    private static uint KernelPlayerApiVersion()
    {
        var path = Path.Combine(RepositoryRoot(),
            @"third_party\fff_project\FFF.Native\3FP\Api\PlayerApi.cpp");
        var text = File.ReadAllText(path);
        var m = Regex.Match(text, @"PlayerApiVersion\s*=\s*(\d+)\s*;");
        if (!m.Success)
            throw new InvalidOperationException(
                $"未能在 {path} 中定位 PlayerApiVersion ⇒ 本测试无法证明任何事");
        return uint.Parse(m.Groups[1].Value);
    }

    /// <summary>MapSnapshot 对 iamfBinaural 逐值透传：0 = 扬声器布局(7.1.4) swr 折叠，
    /// 1 = libiamf 双耳(HRTF)直接出立体声。两个值都验——只钉 1 会漏掉"忘了删默认值"
    /// 这类恒 1 缺陷，只钉 0 则漏掉恒 0。</summary>
    [Fact]
    public void IamfBinaural两值都透传到托管快照()
    {
        foreach (var mode in new uint[] { 0, 1 })
        {
            var native = new Fff3FpSnapshot
            {
                Size = (uint)Marshal.SizeOf<Fff3FpSnapshot>(),
                Version = Fff3FpEngine.Fff3FpSession.SnapshotVersion,
                IamfBinaural = mode,
            };

            var mapped = Fff3FpEngine.Fff3FpSession.MapSnapshot(in native, frameRate: 0);

            Assert.Equal(mode, mapped.IamfBinaural);
        }
    }

    /// <summary>MapSnapshot 对 audioBinaural 逐值透传（内核 FFF.Player.Api.h:268，API v20 尾追加）：
    /// 0 = swr 矩阵折叠，1 = OBR HRTF 双耳渲染。两个值都验，理由同 iamfBinaural
    /// （只钉 1 会漏掉"忘了删默认值"这类恒 1 缺陷，只钉 0 则漏掉恒 0）。</summary>
    [Fact]
    public void AudioBinaural两值都透传到托管快照()
    {
        foreach (var mode in new uint[] { 0, 1 })
        {
            var native = new Fff3FpSnapshot
            {
                Size = (uint)Marshal.SizeOf<Fff3FpSnapshot>(),
                Version = Fff3FpEngine.Fff3FpSession.SnapshotVersion,
                AudioBinaural = mode,
            };

            var mapped = Fff3FpEngine.Fff3FpSession.MapSnapshot(in native, frameRate: 0);

            Assert.Equal(mode, mapped.AudioBinaural);
        }
    }
}
