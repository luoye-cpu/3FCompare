using _3FCompare.Core.Settings;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 崩溃自愈快照的生命周期（<see cref="SessionAutosave"/>，docs/43 配套）。
///
/// <para><b>为什么必须放进 <see cref="GlobalStateCollection"/></b>：快照路径是<b>进程级静态</b>
/// 状态（<c>OverridePath</c>）。xunit 默认按测试类并行，两个类同时改它会出现
/// "单跑绿、全跑红"。集合内串行 + 每个用例 try/finally 还原，两道都要有。</para>
///
/// <para><b>为什么每个用例都还原路径</b>：不还原的话，第一个用例留下的临时路径会
/// 泄漏到后续用例，让它们把快照写到已经不存在的目录里——表现为一批莫名其妙的失败。</para>
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class SessionAutosaveTests : IDisposable
{
    private readonly TempDir _dir = new("autosave");

    public SessionAutosaveTests()
    {
        SessionAutosave.ResetForTests(_dir.File_(SessionAutosave.FileName));
    }

    public void Dispose()
    {
        SessionAutosave.ResetForTests(null);
        _dir.Dispose();
    }

    private static SessionSnapshot Sample(int routes = 2) => new()
    {
        GridLayout = 2,
        Position100ns = 4_500_000_000L,
        Items = Enumerable.Range(0, routes)
            .Select(i => new SessionSnapshot.SessionItem
            {
                Path = $@"C:\videos\clip{i}.mp4",
                Offset100ns = i * 1_000_000L,
            })
            .ToList(),
    };

    [Fact]
    public void 写入后_可读回且字段一致()
    {
        SessionAutosave.Write(Sample());

        var back = SessionAutosave.Read();

        Assert.NotNull(back);
        Assert.Equal(2, back!.Items.Count);
        Assert.Equal(@"C:\videos\clip0.mp4", back.Items[0].Path);
        Assert.Equal(@"C:\videos\clip1.mp4", back.Items[1].Path);
        Assert.Equal(2, back.GridLayout);
        Assert.Equal(4_500_000_000L, back.Position100ns);
    }

    [Fact]
    public void 从未写入_读取返回空()
    {
        Assert.False(SessionAutosave.HasRecoverable());
        Assert.Null(SessionAutosave.Read());
    }

    [Fact]
    public void 文件不存在但被标记可恢复_读取不抛()
    {
        // fail-closed：任何异常都只能表现为"没能恢复"，绝不能冒到调用方
        Assert.Null(SessionAutosave.Read());
    }

    [Fact]
    public void 内容损坏_读取返回空而不是抛()
    {
        File.WriteAllText(SessionAutosave.FilePath, "{ 这不是 JSON");

        Assert.Null(SessionAutosave.Read());
    }

    [Fact]
    public void 空会话_读取返回空()
    {
        // 路数为 0 的快照恢复出来是"提示已恢复但界面全空"，比不恢复更糟
        SessionAutosave.Write(new SessionSnapshot());

        Assert.Null(SessionAutosave.Read());
    }

    [Fact]
    public void 正常退出清除后_不再可恢复()
    {
        SessionAutosave.Write(Sample());
        Assert.True(SessionAutosave.HasRecoverable());

        SessionAutosave.Clear();

        Assert.False(SessionAutosave.HasRecoverable());
        Assert.False(File.Exists(SessionAutosave.FilePath));
    }

    [Fact]
    public void 过期快照_不参与恢复()
    {
        SessionAutosave.Write(Sample());
        // 回拨 25 小时（MaxAge = 24h）：用户早已不记得崩溃这回事，
        // 突然弹出前天的 8 路 4K 只会造成困惑
        File.SetLastWriteTimeUtc(SessionAutosave.FilePath,
            DateTime.UtcNow - TimeSpan.FromHours(25));

        Assert.False(SessionAutosave.HasRecoverable());
        Assert.Null(SessionAutosave.Read());
    }

    [Fact]
    public void 隔离后_文件改名且不再可恢复()
    {
        SessionAutosave.Write(Sample());

        Assert.True(SessionAutosave.Quarantine());

        // 隔离是**改名**而不是删除：反复崩溃多半是"打开这组文件就必崩"，
        // 留着会自激循环；但内容仍要能取回，所以保留成 .bad
        Assert.False(File.Exists(SessionAutosave.FilePath));
        Assert.True(File.Exists(SessionAutosave.FilePath + SessionAutosave.QuarantineExtension));
        Assert.False(SessionAutosave.HasRecoverable());
    }

    [Fact]
    public void 无快照时隔离_返回假且不抛()
    {
        Assert.False(SessionAutosave.Quarantine());
    }

    [Fact]
    public void 内容未变化时_跳过物理写入()
    {
        var snap = Sample();
        SessionAutosave.Write(snap);

        // 从外部把文件改成别的内容：若下一次 Write 真的落盘，它会被覆盖回快照 JSON；
        // 若正确地跳过了写入，被篡改的内容会原样留下。
        //（不能靠"临时文件有没有残留"来判断——AtomicFile 无论如何都会 Move 掉 tmp，
        //  那样写出来的断言永远绿，等于没验证。）
        const string tampered = "\"tampered\"";
        File.WriteAllText(SessionAutosave.FilePath, tampered);
        SessionAutosave.Write(snap);

        Assert.Equal(tampered, File.ReadAllText(SessionAutosave.FilePath));
    }

    [Fact]
    public void 内容变化后_确实落盘()
    {
        // 上一条用例的反面：位置推进后必须真的写下去，否则恢复出来的永远是打开瞬间
        SessionAutosave.Write(Sample());
        var advanced = Sample();
        advanced.Position100ns = 9_000_000_000L;
        SessionAutosave.Write(advanced);

        var back = SessionAutosave.Read();
        Assert.NotNull(back);
        Assert.Equal(9_000_000_000L, back!.Position100ns);
    }
}
