using System.Text.RegularExpressions;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 中性化判据扫<b>宿主侧源码</b>（<c>FFF.Player\**\*.vb</c>）：本体不许出现杜比实现细节，
/// 也不许替模块宣称"已合成"。
///
/// <para><b>为什么是测试而不是发布门禁脚本</b>：这条判据证明的是"源码里没有某类字面实现"，
/// 属于可执行的规格，跟"这一版能不能出包"是两件事。放在测试里，改一行 VB 就能在本机立刻
/// 知道踩没踩线；塞进发布脚本则要等到出包那一刻才判红，而且脚本自己还得先被信任。
/// 发布门禁那边要不要再喂一遍源码层，是另一件事，不靠这条测试替代。</para>
///
/// <para><b>为什么现在才补</b>（2026-11-02 审计）：此前的扫描只看 <c>.cpp/.h/.hpp</c>，
/// <c>.vb</c> 从未被扫过，于是宿主侧两处 A 类越界（硬编码边数据类型键 <c>24UI</c>/<c>29UI</c>、
/// 「EL残差已合成」假绿断言）只能靠人工审计才抓到；两处修完之后没有回头的守卫，
/// 任何人再写回来都不会变红。这条测试就是那把锁。</para>
///
/// <para><b>判据为什么是这两个词</b>：<c>EL残差已合成</c> 是"宿主替模块下结论"——配对与否
/// 宿主无从知晓，它只能说"已请求"；<c>边数据契约类型</c>/<c>配置记录契约类型</c> 是"宿主持有
/// 载荷语义的类型键"，类型键归模块—内核，宿主只许透传。两者都是<b>实现</b>层面的越界，
/// 不是措辞偏好。</para>
/// </summary>
public class NeutralitySourceScanTests
{
    /// <summary>宿主侧判红词。与 <c>tools/check_kernel_neutrality.py</c> 的 SOURCE_FATAL
    /// 里那段宿主判据同义——这里刻意重写一遍而不是 import 那个脚本：脚本是出包工具，
    /// 测试不该依赖它的存在（它不在测试路径上，也不该被测试路径绑死）。</summary>
    private static readonly (string What, Regex Pattern)[] HostFatal =
    [
        ("宿主替模块宣称残差已合成（只能说「已请求」）", new Regex(@"EL残差已?合成", RegexOptions.IgnoreCase)),
        ("宿主持有边数据/配置记录契约类型键（只许透传）",
            new Regex(@"边数据契约类型|配置记录契约类型", RegexOptions.IgnoreCase)),
    ];

    [Fact]
    public void 宿主VB源码里没有假绿断言与硬编码契约类型键()
    {
        var root = Path.Combine(RepositoryRoot(), "third_party", "fff_project", "FFF.Player");
        Assert.True(Directory.Exists(root), $"宿主源码目录不存在：{root} ⇒ 本测试没有对象，不能读成 0 命中");

        var scanned = 0;
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.vb", SearchOption.AllDirectories))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }   // 被占用：跳过，不把"读不到"当"干净"
            scanned += lines.Length == 0 ? 0 : 1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsPureComment(lines[i])) continue;   // 注释里的名字不是实现
                foreach (var (what, pattern) in HostFatal)
                {
                    if (pattern.IsMatch(lines[i]))
                        hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  {what}：{lines[i].Trim()}");
                }
            }
        }

        // 扫描量闸门：目录在、文件在读、行在数。一个都没扫到说明定位错了，
        // 那种情况必须判红 —— 否则"没扫"会被读成"通过"（本项目反复踩过的假绿）。
        Assert.True(scanned >= 50, $"只扫到 {scanned} 个 .vb 文件，定位大概落到了别处（宿主本体有 80+ 个源文件）");
        Assert.True(hits.Count == 0, "宿主侧出现中性化越界：\n" + string.Join("\n", hits));
    }

    [Fact]
    public void 扫描器对旧违例样本确实判红()
    {
        // 判据本身也要有判据：上面那条"零命中"只有在扫描器真的会命中时才有意义。
        // 样本取自 2026-11-02 审计抓到的两处真越界（修前的原文形态）。
        string[] samples =
        [
            "            ' 说明：EL残差已合成（旧形态，纯注释 ⇒ 不该判红）",
            "            Dim 文本 As String = \"EL残差已合成\"",
            "            Friend Const 边数据契约类型 As UInteger = 24UI",
            "            Dim 记 As 配置记录契约类型 = Nothing",
        ];

        var flagged = samples
            .Where(line => !IsPureComment(line))
            .Count(line => HostFatal.Any(x => x.Pattern.IsMatch(line)));

        Assert.Equal(3, flagged);   // 4 条样本里 3 条是实现，第 1 条是注释
    }

    /// <summary>VB 的纯注释行：<c>'</c> 开头，或 <c>Rem</c> 开头（大小写不敏感，后接空白或行尾）。</summary>
    private static bool IsPureComment(string line)
    {
        var stripped = line.TrimStart();
        if (stripped.StartsWith('\'')) return true;
        return stripped.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)
            || stripped.Equals("REM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>仓库根：从二进制所在目录上溯，找得到出货树内核源为止。
    /// 找不到就抛 —— 扫描测试不允许静默跳过（跳过等于"看起来通过"）。</summary>
    private static string RepositoryRoot()
    {
        string[] markers =
        [
            @"third_party\fff_project\FFF.Native\3FP\Core\PlayerSession.cpp",
            @"third_party\fff_project\FFF.Player\FFF.Player.vbproj",
        ];
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; dir is not null && hop < 12; hop++, dir = dir.Parent)
        {
            if (markers.All(m => File.Exists(Path.Combine(dir.FullName, m)))) return dir.FullName;
        }
        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 上溯 12 层找不到出货树；中性化扫描宁可判红，不允许跳过。");
    }
}
