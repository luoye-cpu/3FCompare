namespace _3FCompare.Platform.Tests;

/// <summary>
/// 会话文件路径安全闸门（docs/41 #18 的调用点断言）。
///
/// <para><b>为什么单独钉调用点</b>：<c>PathSafety.IsUncPath</c> 自己测对了，不等于
/// <c>LoadSessionSnapshot</c> 用上了它。会话文件（<c>.3fcs</c>）是外部输入，
/// 是本缺陷真正的攻击面 —— 打开别人给的会话文件时，一条 <c>//server/share/x</c>
/// 就会让 Windows 发起 SMB 请求并自动完成 NTLM 认证，把本机凭据送到对方服务器。
/// 所以这里断言的是<b>生产代码的过滤函数</b>（<c>MainWindow.FilterSessionPaths</c>，
/// 经 InternalsVisibleTo 直接调用），不是测试里重写的一份等价逻辑。</para>
/// </summary>
public class SessionPathFilterTests
{
    [Theory]
    [InlineData(@"\\server\share\x.mp4")]   // 经典 UNC
    [InlineData("//server/share/x.mp4")]    // ⚠ 本缺陷核心：不以 \\ 开头，旧判据放行
    [InlineData("//?/C:/x.mp4")]            // 设备命名空间
    public void UNC路径必须被拒绝(string unc)
    {
        var (accepted, _, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new[] { unc });
        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    /// <summary>拒绝理由必须可操作：点明 UNC 与凭据外泄，而不是笼统"未通过校验"。</summary>
    [Fact]
    public void UNC拒绝理由说明凭据外泄风险()
    {
        var (_, _, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new[] { "//server/share/x.mp4" });
        Assert.Contains("UNC", rejected[0]);
        Assert.Contains("凭据", rejected[0]);
        Assert.Contains("//server/share/x.mp4", rejected[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空路径必须被拒绝(string bad)
    {
        var (accepted, _, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new[] { bad });
        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    /// <summary>相对路径**不再一律拒绝**，而是归一化成绝对路径后接受（docs/41 #18 复核）。
    /// <para>原因：#18 要堵的是 UNC 绕过，不是相对路径。一律拒绝会静默丢掉用户会话里的整条路，
    /// 代价大于收益。安全性不降：当前工作目录若本身在网络共享上，归一化结果就是 UNC
    /// ⇒ 会被 <c>IsUncPath</c> 拦下。</para></summary>
    [Theory]
    [InlineData("relative/x.mp4")]
    [InlineData("rel.mp4")]
    public void 相对路径归一化后被接受(string rel)
    {
        var (accepted, _, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new[] { rel });
        Assert.Equal(new[] { System.IO.Path.GetFullPath(rel) }, accepted);
        Assert.Empty(rejected);
    }

    /// <summary>判定的串与下游使用的串必须是同一个：交出的必须是**绝对路径**，
    /// 否则"相对路径在别处被按另一个 CWD 解析"这条缝仍然存在。</summary>
    [Fact]
    public void 相对路径交出的是归一化后的绝对路径()
    {
        var (accepted, _, _) = global::_3FCompare.MainWindow.FilterSessionPaths(new[] { "rel.mp4" });
        Assert.Single(accepted);
        Assert.True(System.IO.Path.IsPathRooted(accepted[0]), $"交出的不是绝对路径：{accepted[0]}");
    }

    [Fact]
    public void 空引用必须被拒绝()
    {
        var (accepted, _, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new string?[] { null });
        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    [Fact]
    public void 本地绝对路径被接受()
    {
        var (accepted, index, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(
            new[] { @"C:\videos\a.mp4" });
        Assert.Equal(new[] { @"C:\videos\a.mp4" }, accepted);
        Assert.Equal(new[] { 0 }, index);
        Assert.Empty(rejected);
    }

    /// <summary>跳过若干条之后，<c>AcceptedIndex</c> 必须仍指向<b>原始</b>下标 ——
    /// 调用方用它给各路对位偏移量，错位会把偏移张冠李戴到别的路（docs/15 §3.1）。</summary>
    [Fact]
    public void 跳过路径后索引仍对应原始位置()
    {
        var (accepted, index, rejected) = global::_3FCompare.MainWindow.FilterSessionPaths(new string?[]
        {
            @"C:\videos\a.mp4",   // 0 接受
            "//server/share/x",   // 1 拒（UNC）
            null,                 // 2 拒（空）
            @"C:\videos\b.mp4",   // 3 接受
            "rel.mp4",            // 4 接受（归一化后）
        });
        Assert.Equal(new[] { @"C:\videos\a.mp4", @"C:\videos\b.mp4",
                             System.IO.Path.GetFullPath("rel.mp4") }, accepted);
        Assert.Equal(new[] { 0, 3, 4 }, index);
        Assert.Equal(2, rejected.Count);
    }
}
