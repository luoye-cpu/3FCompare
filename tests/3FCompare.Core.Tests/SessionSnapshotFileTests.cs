using _3FCompare.Core.Settings;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 会话快照的**文件级**读写（docs/41 §4.5 第 6 项）。
///
/// <para>与既有的 <c>SessionSnapshotSerializationTests</c> 互补：那一组只覆盖
/// <c>ToJson</c>/<c>FromJson</c> 内存往返，而 <c>.3fcs</c> 会话文件的真实失败面在
/// 文件系统一侧——缺文件、内容损坏、原子写残留 tmp。</para>
///
/// <para><b>期望值来源</b>：全部为用例里**写死的字面量**（构造快照时赋的值），
/// 不使用被测实现或其兄弟函数生成期望，避免 docs/41 §4.3 的"同源互证"。</para>
/// </summary>
public class SessionSnapshotFileTests
{
    [Fact]
    public void SaveToFile_LoadFromFile_往返含Items()
    {
        using var dir = new TempDir("session");
        var path = dir.File_("session.3fcs");

        var snap = new SessionSnapshot
        {
            GridLayout = 3,
            Position100ns = 1_234_567_890L,
            LoopEnabled = true,
            LoopStart100ns = 111L,
            LoopEnd100ns = 222L,
        };
        snap.Items.Add(new SessionSnapshot.SessionItem
        {
            Path = @"C:\videos\a.mp4",
            Offset100ns = -5_000_000L,   // 负偏移是合法语义（从路相对 master 提前）
            HardwareDecode = false,
            AdapterIndex = 1,
        });
        snap.Items.Add(new SessionSnapshot.SessionItem
        {
            Path = @"C:\videos\b.mp4",
            Offset100ns = 7L,
            HardwareDecode = true,
            AdapterIndex = -1,
        });

        SessionSnapshot.SaveToFile(path, snap);
        var back = SessionSnapshot.LoadFromFile(path);

        Assert.NotNull(back);
        Assert.Equal(3, back!.GridLayout);
        Assert.Equal(1_234_567_890L, back.Position100ns);
        Assert.True(back.LoopEnabled);
        Assert.Equal(111L, back.LoopStart100ns);
        Assert.Equal(222L, back.LoopEnd100ns);

        // Items 必须逐项回来（这正是"往返含 Items"的要害：丢 Items 的话
        // 会话文件只剩一个时间点，用户重开时 9 路全空）
        Assert.Equal(2, back.Items.Count);
        Assert.Equal(@"C:\videos\a.mp4", back.Items[0].Path);
        Assert.Equal(-5_000_000L, back.Items[0].Offset100ns);
        Assert.False(back.Items[0].HardwareDecode);
        Assert.Equal(1, back.Items[0].AdapterIndex);
        Assert.Equal(@"C:\videos\b.mp4", back.Items[1].Path);
        Assert.Equal(7L, back.Items[1].Offset100ns);
        Assert.True(back.Items[1].HardwareDecode);
        Assert.Equal(-1, back.Items[1].AdapterIndex);
    }

    /// <summary>文件不存在 ⇒ null（不是抛异常、也不是空快照）。
    /// 调用方据此区分"没有会话"与"会话是空的"。</summary>
    [Fact]
    public void LoadFromFile_文件不存在_返回null()
    {
        using var dir = new TempDir("session");

        Assert.Null(SessionSnapshot.LoadFromFile(dir.File_("missing.3fcs")));
    }

    /// <summary>损坏内容 ⇒ null（而非抛出）。两种典型损坏各验一次：
    /// ① 半截 JSON（原子写时代之前的真实故障形态，见 AtomicFile 注释）；
    /// ② 合法 JSON 但结构不对（手改/被其它程序覆写）。</summary>
    [Fact]
    public void LoadFromFile_内容损坏_返回null()
    {
        using var dir = new TempDir("session");

        var truncated = dir.WriteFile("truncated.3fcs", "{\"GridLayout\":2,\"Items\":[{\"Path\":\"C:\\\\a.mp4");
        Assert.Null(SessionSnapshot.LoadFromFile(truncated));

        var wrongShape = dir.WriteFile("wrong-shape.3fcs", "[1,2,3]");
        Assert.Null(SessionSnapshot.LoadFromFile(wrongShape));
    }

    /// <summary>原子写不得留下 tmp 垃圾。
    ///
    /// <para><b>独立推算</b>：<c>AtomicFile</c> 的临时名形如
    /// <c>&lt;目标&gt;.&lt;pid&gt;.&lt;guid&gt;.tmp</c>，成功路径最后一步是
    /// <c>File.Move(tmp, path, overwrite:true)</c> ⇒ 目标目录里**只能**剩下目标文件本身
    /// （<c>SaveToFile</c> 未开 <c>createBackup</c>，故也不应有 <c>.bak</c>）。
    /// 这里用"目录内文件总数 == 1"同时钉住 tmp 与 bak 两种残留。</para></summary>
    [Fact]
    public void SaveToFile_不残留tmp与bak()
    {
        using var dir = new TempDir("session");
        var path = dir.File_("session.3fcs");

        SessionSnapshot.SaveToFile(path, new SessionSnapshot());
        SessionSnapshot.SaveToFile(path, new SessionSnapshot { Position100ns = 42L }); // 覆盖写一次

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak"));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(42L, SessionSnapshot.LoadFromFile(path)!.Position100ns);
    }
}
