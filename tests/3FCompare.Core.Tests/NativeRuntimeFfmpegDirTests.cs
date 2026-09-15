using _3FCompare.Core.Backend;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// FFmpeg 目录校验回归（docs/15 §5.2）。
///
/// 配置写在 exe 同目录（支撑便携部署），等同"可被投放"：
/// 一个 settings.json 指定任意目录 ⇒ SetDllDirectoryW ⇒ 内核 Delay-Load
/// 加载该目录的 avcodec-*.dll。所以必须先校验再用。
///
/// 注意：这里**没有**把配置迁到 %APPDATA% —— 便携部署正是依赖"配置与 exe 同目录"，
/// 迁移会破坏这个设计。加固方式是校验内容而不是搬家。
/// </summary>
public class NativeRuntimeFfmpegDirTests : IDisposable
{
    private readonly string _root;
    private readonly string? _saved;

    public NativeRuntimeFfmpegDirTests()
    {
        _saved = NativeRuntime.FfmpegDirectory;
        _root = Path.Combine(Path.GetTempPath(), "3fcompare_ffmpeg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        NativeRuntime.SetFfmpegDirectory(_saved);   // 还原，避免影响其它测试
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    // UNC：Windows 访问时会发起 NTLM 认证，外泄本机凭据
    [InlineData(@"\\evil-server\share")]
    [InlineData(@"\\?\C:\anything")]
    // 相对路径：按当前工作目录解析，等于让配置决定加载哪个目录的 DLL
    [InlineData("relative\\path")]
    [InlineData("ffmpeg")]
    public void SetFfmpegDirectory_拒绝UNC与相对路径(string bad)
    {
        NativeRuntime.SetFfmpegDirectory(bad);
        Assert.Null(NativeRuntime.FfmpegDirectory);
    }

    [Fact]
    public void SetFfmpegDirectory_拒绝不存在的目录()
    {
        var missing = Path.Combine(_root, "no-such-dir");
        NativeRuntime.SetFfmpegDirectory(missing);
        Assert.Null(NativeRuntime.FfmpegDirectory);
    }

    /// <summary>目录里没有 avcodec 就说明配错了；这条同时挡住"指向任意目录加载同名 DLL"。</summary>
    [Fact]
    public void SetFfmpegDirectory_拒绝没有avcodec的目录()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "not-ffmpeg.dll"), "x");

        NativeRuntime.SetFfmpegDirectory(empty);

        Assert.Null(NativeRuntime.FfmpegDirectory);
    }

    [Fact]
    public void SetFfmpegDirectory_接受含avcodec的目录()
    {
        var good = Path.Combine(_root, "good");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "avcodec-63.dll"), "");

        NativeRuntime.SetFfmpegDirectory(good);

        Assert.Equal(good, NativeRuntime.FfmpegDirectory);
    }

    [Fact]
    public void SetFfmpegDirectory_空白等同于清除()
    {
        NativeRuntime.SetFfmpegDirectory("   ");
        Assert.Null(NativeRuntime.FfmpegDirectory);
    }
}
