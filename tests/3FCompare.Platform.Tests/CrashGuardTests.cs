using _3FCompare.Diagnostics;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="CrashGuard.ShouldGuard"/> 的<b>守护判据契约</b>（今天代码审查的 P1 #5）。
///
/// <para><b>为什么必须测</b>：它是"所有自动化门禁是否被套上父进程"的<b>唯一</b>开关，
/// 判错一次的两个后果方向相反、且都是<b>静默</b>的：① 该守护的没守护 ⇒ 崩溃自愈无声失效
/// （要等现场真崩一次才发现）；② 不该守护的被守护 ⇒ 门禁的退出码/stdout 判定全部失真
/// （守护进程的退出码顶替了子进程的），而门禁脚本正是本项目唯一的自动化防线。
/// 这个方法的实现很短，恰恰属于"看一眼觉得对、把边界写下来才发现坑很多"的类型 ——
/// 比如"文件路径在 args[0] 时不能因为不是 <c>--</c> 就误判"。</para>
///
/// <para><b>期望值来源（独立推算，不是从被测代码读的）</b>：模式位名单照
/// <c>Program.cs:146-179</c> 与 <c>MainWindow.axaml.cs:133-291</c> 的<b>真实分发白名单</b>
/// 手写为字面量；文件名用形如 <c>C:\videos\a.mp4</c> 的字面量路径；逃生口环境变量名
/// <c>FC_NO_GUARD</c> 也是手写字面量（生产代码里是内联字符串，无常量可引用）。</para>
///
/// <para><b>为什么整类串行化</b>：判据会读<b>进程级</b>环境变量 <c>FC_NO_GUARD</c>，
/// 而本文件必须设/清它才能覆盖逃生口分支。xunit 默认让不同测试类并行 ⇒ 任何同时读该
/// 变量的测试都会互相踩。用 <c>DisableParallelization</c> 的集合把本类单独跑，与
/// <see cref="LanguageManagerKeysTests"/> 处理静态语言状态的做法同款。</para>
///
/// <para><b>不测什么</b>：<see cref="CrashGuard.RunGuard"/> / <see cref="CrashGuard.RunSelfTest"/>
/// 会<b>真的启动进程</b>，不在单测范围内（那是端到端自检，靠 <c>--guard-selftest</c> 手工跑）。</para>
/// </summary>
[Collection(CrashGuardEnvCollection.Name)]
public class CrashGuardTests
{
    /// <summary>逃生口环境变量名。刻意写成字面量而不引用生产常量 ——
    /// 名字本身就是"用户照文档敲的那个字符串"，从被测代码读会丢掉独立判据的意义。</summary>
    private const string NoGuardEnv = "FC_NO_GUARD";

    /// <summary>在"逃生口确定未开启"的前提下判定。
    ///
    /// <para><b>为什么需要它</b>：本机（或 CI）真设了 <c>FC_NO_GUARD=1</c> 时，
    /// 所有"应该守护"的期望值都会变成假红。用例必须能区分"判据坏了"与"我的机器上
    /// 开过逃生口"，所以除逃生口专项用例外，一律先把变量清掉再判定，并在
    /// <c>finally</c> 里还原原值（进程级全局状态，不能留在测试里）。</para></summary>
    private static bool 在逃生口未开启时判定(string[] args)
    {
        var saved = Environment.GetEnvironmentVariable(NoGuardEnv);
        try
        {
            Environment.SetEnvironmentVariable(NoGuardEnv, null);
            return CrashGuard.ShouldGuard(args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NoGuardEnv, saved);
        }
    }

    /// <summary>无任何命令行参数 ⇒ 守护。这是用户双击图标、拖放、文件关联的形态，
    /// 也是这个特性存在的<b>唯一目的</b>：这些场景崩了没人去重新打开 9 路 4K。
    /// 期望值 = <c>true</c>，即"裸启动必须受保护"这条产品承诺。</summary>
    [Fact]
    public void 无任何参数时应守护()
    {
        Assert.True(在逃生口未开启时判定(Array.Empty<string>()),
            "双击启动（无参数）必须受守护，否则自愈对真实用户完全失效");
    }

    /// <summary>真实存在的每一个门禁模式位都<b>不</b>能守护。
    ///
    /// <para><b>期望值</b>：以下参数组照 <c>Program.cs</c> 与 <c>MainWindow.axaml.cs</c> 的
    /// 分发白名单手写，参数个数也照真实调用形态给（<c>--magnifybench</c> 要 5 个以上、
    /// <c>--screentest</c> 要 3 个以上）—— 期望一律为 <c>false</c>。多一层父进程会让
    /// 这些脚本读到的退出码与 stdout 全部失真，是"不该守护的被守护"这一事故的全部内容。</para></summary>
    [Fact]
    public void 各真实门禁模式位都不应守护()
    {
        var cases = new (string 模式, string[] 参数)[]
        {
            ("--selftest",        new[] { "--selftest", @"C:\videos\a.mp4" }),
            ("--screentest",      new[] { "--screentest", @"C:\videos\a.mp4", @"C:\shots\out.png" }),
            ("--sessiontest",     new[] { "--sessiontest", @"C:\videos\a.mp4" }),
            ("--multitest",       new[] { "--multitest", @"C:\videos\a.mp4", "4", "30" }),
            ("--comparemodetest", new[] { "--comparemodetest", @"C:\videos\a.mp4", "3" }),
            ("--magnifybench",    new[] { "--magnifybench", @"C:\videos\a.mp4", "3", "4", "8", "0" }),
            ("--guard-selftest",  new[] { "--guard-selftest", @"C:\temp\counter.txt", "2" }),
        };

        foreach (var (模式, 参数) in cases)
        {
            Assert.False(在逃生口未开启时判定(参数),
                $"{模式} 是自动化门禁，被套上父进程会让退出码/stdout 判定失真");
        }
    }

    /// <summary><c>--autodemo</c> 是判据里<b>唯一</b>的例外：它虽然带 <c>--</c>，但要守护。
    ///
    /// <para><b>期望值</b>：<c>true</c>。理由与判据本身同源 —— 它是<b>无人值守</b>的
    /// 演示/巡检模式，现场没人在，崩了没人重新打开（见 <c>CrashGuard.cs:72-76</c> 的定案：
    /// 全仓核查确认 <c>tools/</c>、<c>pack.ps1</c>、发布门禁均未使用 <c>--autodemo</c>，
    /// 故不存在判定失真的风险）。同一用例里放一条带参数的对照：<c>--autodemo</c> 后面
    /// 跟的是<b>文件路径</b>（不是模式位），不能让"任意后续参数"破坏这个例外。</para></summary>
    [Fact]
    public void 自动演示模式是唯一例外仍应守护()
    {
        Assert.True(在逃生口未开启时判定(new[] { "--autodemo" }),
            "--autodemo 无文件时也是无人值守演示，必须守护");
        Assert.True(在逃生口未开启时判定(new[] { "--autodemo", @"C:\videos\a.mp4", @"C:\videos\b.mp4" }),
            "--autodemo + 文件路径（非模式位）仍应守护");
    }

    /// <summary>显式关闭参数 <c>--no-guard</c> ⇒ 不守护。它<b>不需要</b>单独判断（任何
    /// <c>--</c> 前缀都会落到同一条规则上），但它是文档里告诉用户"怎么关掉自愈"的那个
    /// 拼写，所以必须钉住它确实生效 —— 否则文档给的答案就是错的。</summary>
    [Fact]
    public void 显式关闭参数不应守护()
    {
        Assert.False(在逃生口未开启时判定(new[] { CrashGuard.NoGuardArg }),
            $"{CrashGuard.NoGuardArg} 是文档承诺的关闭方式，必须真的不守护");
    }

    /// <summary>守护子进程自带的 <c>--child</c> / <c>--crash-restore</c> ⇒ 不守护。
    ///
    /// <para><b>为什么必须测</b>：这正是"天然不递归"的全部依据（<c>CrashGuard.cs:27</c>）。
    /// 一旦这两个标记不再被前缀规则拦下，子进程会再启一个守护进程、无限套娃，
    /// 而症状是"机器越跑越卡"这种极难归因的表现。</para></summary>
    [Fact]
    public void 守护子进程标记不应守护()
    {
        Assert.False(在逃生口未开启时判定(new[] { CrashGuard.ChildArg, "1234" }),
            "子进程带 --child，必须不守护，否则守护会无限递归");
        Assert.False(在逃生口未开启时判定(new[] { CrashGuard.ChildArg, "1234", CrashGuard.RestoreArg }),
            "崩溃重启后的子进程同样带 --child，仍须不守护");
    }

    /// <summary>拖放 / 文件关联：命令行首个参数是<b>文件路径</b>（不以 <c>--</c> 开头）⇒ 守护。
    ///
    /// <para><b>为什么单列</b>：这是判据最容易写错的地方 —— 实现若退化成"只看
    /// <c>args[0]</c> 是否等于某个模式位"或"args[0] 不是 <c>--</c> 就 false"，拖放场景
    /// 就会静默失去守护。期望值 = <c>true</c>（单个文件、多个文件两种形态）。</para></summary>
    [Fact]
    public void 拖放文件路径在首位时仍应守护()
    {
        Assert.True(在逃生口未开启时判定(new[] { @"C:\videos\a.mp4" }),
            "文件关联/拖放只有一个路径，必须守护");
        Assert.True(在逃生口未开启时判定(new[] { @"C:\videos\a.mp4", @"C:\videos\b.mp4", @"C:\videos\c.mp4" }),
            "多选拖放也只有路径，必须守护");
    }

    /// <summary>模式位出现在<b>路径之后</b>时也不守护 —— 判据看的是"全数组里有没有
    /// <c>--</c> 前缀"，不是"args[0] 长什么样"。
    ///
    /// <para><b>为什么必须测</b>：这是把"任意 <c>--</c> 不守护"改坏成"只看 args[0]"后
    /// <b>唯一会判红</b>的形态。真实来源：部分启动器 / 快捷方式会在文件路径之后追加开关，
    /// 此时 args[0] 是路径、args[1] 才是模式位；只看 args[0] 的实现在这里会错误地守护，
    /// 于是门禁的退出码被父进程顶替。期望值 = <c>false</c>。</para></summary>
    [Fact]
    public void 模式位出现在路径之后时仍不应守护()
    {
        Assert.False(在逃生口未开启时判定(new[] { @"C:\videos\a.mp4", "--selftest", @"C:\shots\out.png" }),
            "args[0] 是路径但数组里有 --selftest ⇒ 仍是门禁，不能被守护");
        Assert.False(在逃生口未开启时判定(new[] { @"C:\videos\a.mp4", "--sessiontest" }),
            "模式位在第二个位置同样必须拦下");
    }

    /// <summary>逃生口：<c>FC_NO_GUARD=1</c> ⇒ 不守护，且<b>优先级高于 <c>--autodemo</c> 例外</b>。
    ///
    /// <para><b>为什么必须测</b>：这是"无命令行参数也能关掉守护"的唯一途径（快捷方式场景
    /// 用），也是排查自愈自身问题时不必改代码重编译的手段。实现里它排在例外判断<b>之前</b>，
    /// 若被挪到后面，<c>--autodemo</c> 场景就再也关不掉守护。期望值 = <c>false</c>。</para>
    ///
    /// <para><b>环境变量是进程级全局状态</b>：用 <c>try/finally</c> 还原原值；本类整体
    /// 放在 <c>DisableParallelization</c> 的集合里，避免与其它测试并发踩踏。</para></summary>
    [Fact]
    public void 环境变量逃生口开启时不应守护()
    {
        var saved = Environment.GetEnvironmentVariable(NoGuardEnv);
        try
        {
            Environment.SetEnvironmentVariable(NoGuardEnv, "1");

            Assert.False(CrashGuard.ShouldGuard(Array.Empty<string>()),
                "FC_NO_GUARD=1 是无参数启动也能关掉守护的唯一逃生口");
            Assert.False(CrashGuard.ShouldGuard(new[] { "--autodemo", @"C:\videos\a.mp4" }),
                "逃生口优先级高于 --autodemo 例外，否则演示场景无法排查");
        }
        finally
        {
            Environment.SetEnvironmentVariable(NoGuardEnv, saved);
        }
    }

    /// <summary>逃生口只认字面量 <c>"1"</c>：<c>FC_NO_GUARD=0</c> 或 <c>=true</c> 时照常守护。
    ///
    /// <para><b>期望值来源</b>：生产代码里的比较就是 <c>== "1"</c>（<c>CrashGuard.cs:70</c>），
    /// 本用例把它钉住。若实现被放宽成"非空即关"，用户在环境里留一个 <c>FC_NO_GUARD=0</c>
    /// 就会被静默地永久关掉自愈 —— 那是"以为开着、其实关着"的典型事故。</para></summary>
    [Fact]
    public void 逃生口只认字面量1()
    {
        var saved = Environment.GetEnvironmentVariable(NoGuardEnv);
        try
        {
            foreach (var 值 in new[] { "0", "true", "yes", "" })
            {
                Environment.SetEnvironmentVariable(NoGuardEnv, 值);
                Assert.True(CrashGuard.ShouldGuard(Array.Empty<string>()),
                    $"FC_NO_GUARD={值} 不是约定的关闭值（只有 \"1\" 算），仍应守护");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(NoGuardEnv, saved);
        }
    }

    /// <summary>参数数组里的 <c>null</c> 元素被跳过、不抛异常。
    ///
    /// <para><b>为什么必须测</b>：<c>args</c> 来自 <c>Main</c> 的 <c>string[]</c>，
    /// 正常情况不会有 <c>null</c>，但判据一旦在 <c>null</c> 上抛异常，<c>Program.Main</c>
    /// 会在<b>守护判断</b>这一步就炸（那是崩溃自愈自己的入口）。期望值 = <c>true</c>
    /// （无 <c>--</c> 前缀 ⇒ 守护）。</para></summary>
    [Fact]
    public void 空元素被跳过不抛异常()
    {
        Assert.True(在逃生口未开启时判定(new[] { null!, @"C:\videos\a.mp4" }));
        Assert.False(在逃生口未开启时判定(new[] { null!, "--selftest" }));
    }

    /// <summary>只有<b>两个</b>短横线前缀才算模式位：单个 <c>-x</c> 与恰好 <c>--</c> 的边界。
    ///
    /// <para><b>期望值</b>：<c>"-"</c> / <c>"-x"</c> ⇒ <c>true</c>（按判据的定义它们不是模式位，
    /// 会被当作普通参数/路径）；<c>"--"</c> ⇒ <c>false</c>（前缀命中）。本用例钉住的是
    /// <c>StartsWith("--", Ordinal)</c> 这条精确边界，防止退化成 <c>StartsWith("-")</c>
    /// 而把带单横线的路径/参数误判成门禁。</para></summary>
    [Fact]
    public void 模式位前缀边界为两个短横线()
    {
        Assert.True(在逃生口未开启时判定(new[] { "-x" }), "单短横线不是模式位");
        Assert.False(在逃生口未开启时判定(new[] { "--" }), "恰好两个短横线即命中前缀规则");
    }

    /// <summary>跨进程协议常量与文档拼写必须稳定。
    ///
    /// <para><b>为什么必须测</b>：这些字符串有两类外部依赖，改错了都不会编译失败 ——
    /// ① <c>--no-guard</c> / <c>--crash-restore</c> 是<b>用户照文档敲</b>的拼写；
    /// ② <c>--child</c> 是父进程写、子进程读的<b>跨进程线格式</b>（写进
    /// <c>ProcessStartInfo.ArgumentList</c>）。另外钉住一条<b>不变量</b>：子进程标记必须
    /// 自带 <c>--</c> 前缀，否则它落不进"任意 <c>--</c> 不守护"这条规则、守护就会无限递归。</para></summary>
    [Fact]
    public void 常量拼写与子进程标记不变量()
    {
        Assert.Equal("--child", CrashGuard.ChildArg);
        Assert.Equal("--crash-restore", CrashGuard.RestoreArg);
        Assert.Equal("--no-guard", CrashGuard.NoGuardArg);
        Assert.Equal("--guard-selftest", CrashGuard.SelfTestArg);
        Assert.Equal("--autodemo", CrashGuard.AutodemoArg);

        foreach (var 标记 in new[] { CrashGuard.ChildArg, CrashGuard.RestoreArg, CrashGuard.NoGuardArg })
        {
            Assert.StartsWith("--", 标记);
            Assert.False(CrashGuard.ShouldGuard(new[] { 标记 }), $"{标记} 必须被前缀规则拦下");
        }

        // 重启上限必须为正：0 会让守护退化成"一次都不重启"，整个特性变成空操作。
        Assert.True(CrashGuard.MaxConsecutiveRestarts >= 1);
    }
}

/// <summary>把读/写进程级环境变量 <c>FC_NO_GUARD</c> 的用例串成"不与其他集合并行"的一组，
/// 见 <see cref="CrashGuardTests"/> 的类注释。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrashGuardEnvCollection
{
    public const string Name = "3FCompare_CrashGuardEnv";
}
