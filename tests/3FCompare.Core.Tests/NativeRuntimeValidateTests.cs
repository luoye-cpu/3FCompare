using _3FCompare.Core.Backend;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// FFmpeg 目录的**校验**与**自动探测**（docs/41 §4.5 第 7 项）。
///
/// <para>与既有的 <c>NativeRuntimeFfmpegDirTests</c> 互补：那一组测的是
/// <c>SetFfmpegDirectory</c>（"设置后是否被采纳"），本组测的是
/// <c>ValidateFfmpegDirectory</c>（给用户的**原因串**）与
/// <c>AutoDetectFfmpegDirectory</c>（候选优先级）。两者失败表现完全不同：
/// 前者静默退回演示模式，后者会让设置窗口弹出一句含糊的提示。</para>
///
/// <para><b>全局状态</b>：本类要改 <c>FFMPEG_DIR</c> 环境变量（进程级），
/// 故与其它改静态量的类同属 <see cref="GlobalStateCollection"/>（禁止并行），
/// 且每条用例 <c>try/finally</c> 无条件还原。</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class NativeRuntimeValidateTests
{
    // ── ValidateFfmpegDirectory：原因串必须能直接显示给用户，故逐字钉死 ──

    [Fact]
    public void ValidateFfmpegDirectory_空或空白_报目录为空()
    {
        Assert.Equal("目录为空", NativeRuntime.ValidateFfmpegDirectory(null));
        Assert.Equal("目录为空", NativeRuntime.ValidateFfmpegDirectory(""));
        Assert.Equal("目录为空", NativeRuntime.ValidateFfmpegDirectory("   "));
    }

    [Fact]
    public void ValidateFfmpegDirectory_目录不存在_报目录不存在()
    {
        using var dir = new TempDir("nrt");

        Assert.Equal("目录不存在", NativeRuntime.ValidateFfmpegDirectory(dir.File_("no-such-dir")));
    }

    /// <summary>目录存在但没有 avcodec ⇒ 必须给出"未找到 avcodec"这一条原因
    /// （用户最常见的错法：选了 FFmpeg 根目录而不是它下面的 bin）。</summary>
    [Fact]
    public void ValidateFfmpegDirectory_无avcodec_报未找到avcodec()
    {
        using var dir = new TempDir("nrt");
        dir.WriteFile("avformat-63.dll", "");   // 有兄弟库但没有核心库
        dir.WriteFile("ffmpeg.exe", "");

        var reason = NativeRuntime.ValidateFfmpegDirectory(dir.Path);

        Assert.NotNull(reason);
        Assert.Contains("avcodec", reason);
        Assert.Contains("未找到", reason);
    }

    [Fact]
    public void ValidateFfmpegDirectory_含avcodec_返回null表示有效()
    {
        using var dir = new TempDir("nrt");
        dir.WriteFile("avcodec-63.dll", "");

        Assert.Null(NativeRuntime.ValidateFfmpegDirectory(dir.Path));
    }

    // ── AutoDetectFfmpegDirectory：候选优先级 ──

    /// <summary>FFMPEG_DIR 是**显式指定**，优先级高于 PATH 与应用目录。
    ///
    /// <para><b>独立推算</b>：用例自己造一个确实含 <c>avcodec-62.dll</c> 的目录并设为
    /// <c>FFMPEG_DIR</c>；探测的第 1 步就是读该变量 ⇒ 只要它指向的目录含 avcodec，
    /// 返回的必须是它本身（与 PATH 上有什么无关）。这条同时钉住"环境变量优先级"这一契约。</para></summary>
    [Fact]
    public void AutoDetectFfmpegDirectory_FFMPEG_DIR优先于其它候选()
    {
        using var dir = new TempDir("nrt");
        var ffmpegDir = Path.Combine(dir.Path, "ffmpeg-full");
        Directory.CreateDirectory(ffmpegDir);
        File.WriteAllText(Path.Combine(ffmpegDir, "avcodec-62.dll"), "");

        var saved = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("FFMPEG_DIR", ffmpegDir);

            var detected = NativeRuntime.AutoDetectFfmpegDirectory();

            Assert.NotNull(detected);
            Assert.Equal(ffmpegDir, detected, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFMPEG_DIR", saved);   // 无条件还原
        }
    }

    /// <summary>FFMPEG_DIR 指向的目录里**没有** avcodec 时，该目录不得被采纳
    /// （否则会把一个装不了内核的目录当成"找到了 FFmpeg"）。
    ///
    /// <para><b>独立推算</b>：该目录只有 readme.txt ⇒ <c>Resolve</c> 找不到
    /// <c>avcodec-*.dll</c> ⇒ 必然返回 null ⇒ 探测结果绝不可能是它
    /// （可能落到 PATH/应用目录的其它候选，或整体为 null，两者都接受）。</para></summary>
    [Fact]
    public void AutoDetectFfmpegDirectory_FFMPEG_DIR无avcodec时_不采纳该目录()
    {
        using var dir = new TempDir("nrt");
        var noAvcodec = Path.Combine(dir.Path, "not-ffmpeg");
        Directory.CreateDirectory(noAvcodec);
        File.WriteAllText(Path.Combine(noAvcodec, "readme.txt"), "");

        var saved = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("FFMPEG_DIR", noAvcodec);

            var detected = NativeRuntime.AutoDetectFfmpegDirectory();

            Assert.False(string.Equals(noAvcodec, detected, StringComparison.OrdinalIgnoreCase),
                $"不含 avcodec 的目录被采纳了：{detected}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFMPEG_DIR", saved);
        }
    }
}
