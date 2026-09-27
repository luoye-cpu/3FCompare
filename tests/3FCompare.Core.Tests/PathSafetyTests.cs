using _3FCompare.Core;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// UNC 判据回归（docs/41 #18）。
///
/// 缺陷形态：原判据是 <c>path.StartsWith(@"\\")</c>，但 <c>Path.IsPathRooted("//server/share/x")</c>
/// 返回 <b>true</b> 而该串<b>不以</b> <c>\\</c> 开头 ⇒ 校验被 <c>//</c> 写法整体绕过。
/// 后果是打开外部 .3fcs 会话文件时按 UNC 路径触发 SMB 出网请求，Windows 自动发起 NTLM
/// 认证 ⇒ 本机凭据被送到对方服务器。这是真实的安全问题，不是"写法不统一"。
///
/// 因此这里的断言全部针对"归一化后的形式"，并且异常路径必须是 fail-closed（判为不可信）。
/// </summary>
public class PathSafetyTests
{
    [Theory]
    // 经典 UNC 写法
    [InlineData(@"\\server\share\x")]
    // ⚠ 本缺陷的核心：// 与 \\ 在 .NET 里都算 rooted，只有归一化后形式才唯一
    [InlineData("//server/share/x")]
    [InlineData("//server/share")]
    // 设备命名空间（\\?\ 与 //?/ 归一后同为 \\?\…）：会跳过常规路径规范化，同样按不可信拒绝
    [InlineData(@"\\?\C:\a\b")]
    [InlineData("//?/C:/x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    // 空 / 空白：无可信内容 ⇒ fail-closed
    [InlineData("")]
    [InlineData("   ")]
    // 含非法字符（内嵌 NUL）：GetFullPath 抛异常 ⇒ 必须按不可信处理，绝不能吞掉后当合法本地路径
    [InlineData("C:\\a\\\0b")]
    public void IsUncPath_不可信路径判为真(string path)
        => Assert.True(PathSafety.IsUncPath(path));

    [Theory]
    [InlineData(@"C:\a\b")]
    [InlineData(@"C:\")]
    [InlineData("C:/a/b")]
    [InlineData(@"D:\videos\a.mp4")]
    public void IsUncPath_本地绝对路径判为假(string path)
        => Assert.False(PathSafety.IsUncPath(path));

    /// <summary>相对路径归一化后是本地绝对路径 ⇒ 本判据返回 false。
    /// "拒绝相对路径"是调用点 <c>Path.IsPathRooted</c> 的职责，两者语义不重叠 ——
    /// 若这里也返回 true，调用方给出的原因会错报成"UNC 路径"。</summary>
    [Theory]
    [InlineData("relative/x")]
    [InlineData("relative\\x")]
    [InlineData("a.mp4")]
    public void IsUncPath_相对路径判为假(string path)
        => Assert.False(PathSafety.IsUncPath(path));

    [Fact]
    public void IsUncPath_空引用判为真()
        => Assert.True(PathSafety.IsUncPath(null));

    /// <summary>钉死缺陷本身：<c>//server/share/x</c> 既是 rooted、又不以 <c>\\</c> 开头。
    /// 这两条同时成立 ⇒ 任何"先判 rooted、再查 \\ 前缀"的实现都会被绕过。
    /// 该用例在把 IsUncPath 改回 StartsWith(@"\\") 时会判红（见反向验证）。</summary>
    [Fact]
    public void 缺陷复现_斜杠形式UNC既rooted又不以反斜杠开头()
    {
        const string bypass = "//server/share/x";
        Assert.True(Path.IsPathRooted(bypass));                       // 旧实现的第 1 道闸门放行
        Assert.False(bypass.StartsWith(@"\\", StringComparison.Ordinal)); // 旧实现的第 2 道闸门也放行
        Assert.True(PathSafety.IsUncPath(bypass));                    // 新判据必须拦住
    }
}
