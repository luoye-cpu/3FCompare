using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// RTX VSR 状态结构的托管镜像 ↔ 内核布局对账。
///
/// 为什么这个测试的形状与 <see cref="HdrSignalDiagnosticsLayoutTests"/> 不同：
/// 那份从**出货内核头**（third_party 那棵）解析字段再逐偏移比；而
/// <c>FFF3FPVideoSuperResolutionStatus</c> 目前**只存在于上游 PR #19 的头里**，
/// 出货头里连这个类型都没有（实测 <c>grep VideoSuperResolution third_party/.../3FP</c> = 0，
/// 盘上那份 DLL 的 93 个导出里也没有 <c>Set/GetVideoSuperResolutionStatus</c>）。
/// 所以这里改成两条能各自成立、又都能在关键时刻变红的判据：
///   ① 把托管镜像自己钉死成内核声明的 44 字节 / 11 个 u32 / 固定顺序
///      —— 内核 <c>PlayerSession.cpp:2002</c> 是 <c>size &lt; sizeof || version != 20</c> 才拒，
///      镜像被改小会读不到（响），被改成"同尺寸换顺序"就会过闸门并按错偏移读（静默错），
///      只有逐字段偏移断言能挡后者。
///   ② 断言出货内核头**仍然没有**这个类型。等 VSR 真被移植进出货树，这条会判红，
///      逼着改的人把它换成 ①+头解析的真交叉校验 —— 而不是让镜像在没人对账的情况下漂走。
/// </summary>
public class VideoSuperResolutionLayoutTests
{
    private const string ShippingHeaderRelativePath =
        @"third_party\fff_project\FFF.Native\3FP\Api\FFF.Player.Api.h";

    /// <summary>内核结构体的字段顺序（源自 PR #19 的 FFF.Player.Api.h:649-670，全部 u32）。
    /// 原生名 → 托管名（首字母大写）。</summary>
    private static readonly (string Native, string Managed)[] ExpectedFields =
    [
        ("size", "Size"),
        ("version", "Version"),
        ("requested", "Requested"),
        ("nvidiaAdapter", "NvidiaAdapter"),
        ("active", "Active"),
        ("driverRejected", "DriverRejected"),
        ("reason", "Reason"),
        ("sourceWidth", "SourceWidth"),
        ("sourceHeight", "SourceHeight"),
        ("targetWidth", "TargetWidth"),
        ("targetHeight", "TargetHeight"),
    ];

    [Fact]
    public void MirroredStructMatchesTheKernelFieldOrderAndWidths()
    {
        var total = Marshal.SizeOf<Fff3FpVideoSuperResolutionStatus>();
        Assert.Equal(ExpectedFields.Length * 4, total);
        Assert.Equal(44, total);

        var managed = typeof(Fff3FpVideoSuperResolutionStatus).GetFields()
            .ToDictionary(f => f.Name, f => Marshal.OffsetOf<Fff3FpVideoSuperResolutionStatus>(f.Name).ToInt32());

        var problems = new List<string>();
        for (var i = 0; i < ExpectedFields.Length; i++)
        {
            var (native, managedName) = ExpectedFields[i];
            if (!managed.TryGetValue(managedName, out var offset))
            {
                problems.Add($"托管缺字段 {managedName}（原生 {native}）");
                continue;
            }
            if (offset != i * 4)
                problems.Add($"{managedName} 偏移 {offset} ≠ 期望 {i * 4}（顺序或宽度漂了）");
        }
        var extra = managed.Keys.Except(ExpectedFields.Select(e => e.Managed)).ToList();
        if (extra.Count > 0) problems.Add("托管多出字段： " + string.Join(",", extra));

        Assert.True(problems.Count == 0,
            $"FFF3FPVideoSuperResolutionStatus 镜像与内核声明不一致：\n  {string.Join("\n  ", problems)}");
    }

    /// <summary>版本戳必须跟托管侧唯一的 API 水位同值：内核那边是硬比 <c>!= 20</c>，
    /// 而不是"不低于"。这一条把"改了 ConfigVersion 忘了这里"变成测试期就炸。</summary>
    [Fact]
    public void VersionStampFollowsTheSingleApiWater()
    {
        Assert.Equal(20u, Fff3FpEngine.ConfigVersion);
        Assert.Equal(4, Marshal.OffsetOf<Fff3FpVideoSuperResolutionStatus>("Version").ToInt32());
    }

    /// <summary>
    /// 出货内核头目前应当**没有**这个类型。这是一个"记账型"断言，不是放弃对账：
    /// VSR 一旦被移植进 third_party 那棵，本测试判红，提示改成按头解析的真交叉校验。
    /// 头文件读不到一律判红（与 HdrSignalDiagnosticsLayoutTests 同规矩），不许静默跳过。
    /// </summary>
    [Fact]
    public void ShippingKernelStillLacksTheVsrStruct()
    {
        var root = FindRepositoryRoot();
        var header = Path.Combine(root, ShippingHeaderRelativePath);
        var text = File.ReadAllText(header);

        var declares = text.Contains("FFF3FPVideoSuperResolutionStatus", StringComparison.Ordinal);
        Assert.True(!declares,
            "出货内核头已出现 FFF3FPVideoSuperResolutionStatus ⇒ 本测试的前提结束了。" +
            "请把它换成与 HdrSignalDiagnosticsLayoutTests 同样的按头解析交叉校验" +
            "（字段名/类型/偏移逐个比），并把 PR #19 的 VSR 桶一并确认已落到这棵树。");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ShippingHeaderRelativePath))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"找不到内核头文件 {ShippingHeaderRelativePath}（从 {AppContext.BaseDirectory} 上溯 12 层）。" +
            "布局镜像测试不允许静默跳过：要么它在测真东西，要么它判红。");
    }
}
