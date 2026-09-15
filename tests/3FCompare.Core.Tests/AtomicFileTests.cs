using _3FCompare.Core.Settings;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 原子写回归（docs/15 §4.1）。
///
/// 原先 SettingsStore / SessionSnapshot 都用 <c>File.WriteAllText</c>，它会**先截断**
/// 目标文件：写失败（磁盘满 / 只读 / 被占用 / 进程被杀）后配置文件变成半截 JSON，
/// 下次读取抛 JsonException 又被 catch 吞掉 ⇒ 静默回退默认，用户毫无感知。
///
/// 说明：原子写的核心收益在**失败路径**（目标文件分毫未动），而失败路径在本机
/// 难以稳定模拟（只读目录在 Windows/CI 上行为不一致）。这里守住的是：
/// ① 正常路径语义不变；② 失败时必须**抛异常**而不是静默留下半截文件；
/// ③ 临时文件不得残留。原理见 AtomicFile 的注释。
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _root;

    public AtomicFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "3fcompare_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteAllText_写入后内容正确且不残留临时文件()
    {
        var path = Path.Combine(_root, "settings.json");
        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        // 临时文件必须已被移走/清理，否则会把用户配置目录弄脏
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_覆盖已有文件时整体替换为新内容()
    {
        var path = Path.Combine(_root, "settings.json");
        AtomicFile.WriteAllText(path, "OLD-CONTENT");
        AtomicFile.WriteAllText(path, "NEW-CONTENT");

        Assert.Equal("NEW-CONTENT", File.ReadAllText(path));
        Assert.Equal(1, Directory.GetFiles(_root).Length); // 只有目标文件，没有 tmp
    }

    [Fact]
    public void WriteAllText_目标目录不存在时自动创建()
    {
        var path = Path.Combine(_root, "sub", "dir", "settings.json");
        AtomicFile.WriteAllText(path, "X");

        Assert.True(File.Exists(path));
        Assert.Equal("X", File.ReadAllText(path));
    }

    /// <summary>失败必须冒泡：调用方要能写日志，而不是静默吞掉留下半截文件。</summary>
    [Fact]
    public void WriteAllText_失败时抛异常()
    {
        // 把一个已存在的目录当文件写 —— File.Move 到目录必然失败
        var dirAsFile = Path.Combine(_root, "not-a-file");
        Directory.CreateDirectory(dirAsFile);

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(dirAsFile, "X"));
        // 失败后不应留下 .tmp 垃圾
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void WriteAllText_UTF8无BOM()
    {
        var path = Path.Combine(_root, "u8.json");
        AtomicFile.WriteAllText(path, "A");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(1, bytes.Length);          // 无 BOM 时只有 'A'
        Assert.Equal((byte)'A', bytes[0]);
    }
}
