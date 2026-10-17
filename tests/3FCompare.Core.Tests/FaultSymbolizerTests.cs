using _3FCompare.Core.Diagnostics;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="FaultSymbolizer"/> 的边界判据。期望值全部**手算**，不引用实现里的常数。
///
/// <para>为什么要单钉这个纯函数：调用它的那半边（VEH + 写文件）在 App 工程里、没有 xunit 容器，
/// 而崩溃取证最容易坏的地方恰恰是"地址算到哪算哪"——半开区间写反、或者把堆/栈这类
/// 非镜像内存也算成模块，读数都会变得"看起来很合理"，等发现时已经查完一整轮了。</para></summary>
public class FaultSymbolizerTests
{
    private const string A = "a.dll";
    private const string B = "b.dll";

    // ─────────────── 命中与归属 ───────────────

    [Fact]
    public void 首字节命中_偏移为零()
    {
        var s = new FaultSymbolizer(new[] { new ModuleRange(A, 0x1000, 0x200) });
        Assert.Equal("a.dll+0x0", s.Resolve(0x1000));
    }

    [Fact]
    public void 末字节命中_偏移等于大小减一()
    {
        var s = new FaultSymbolizer(new[] { new ModuleRange(A, 0x1000, 0x200) });
        Assert.Equal("a.dll+0x1ff", s.Resolve(0x11ff));
    }

    /// <summary>区间是 <c>[Base, Base+Size)</c>：<c>Base+Size</c> 已经属于后面那段内存，
    /// 不能算进本模块。写成 <c>&lt;=</c> 的话，下一个模块的第一条指令会被记成上一个模块的尾巴
    /// ——正是"把别人的崩溃算到自己头上"的那种错。</summary>
    [Fact]
    public void 恰好越过末边界_不属于本模块()
    {
        var s = new FaultSymbolizer(new[] { new ModuleRange(A, 0x1000, 0x200) });
        Assert.StartsWith("outside@", s.Resolve(0x1200));
    }

    [Fact]
    public void 相邻模块_起点归属后一个()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange(A, 0x1000, 0x100),      // 0x1000..0x10ff
            new ModuleRange(B, 0x1100, 0x100),      // 0x1100..0x11ff
        });
        Assert.Equal("a.dll+0xff", s.Resolve(0x10ff));
        Assert.Equal("b.dll+0x0", s.Resolve(0x1100));
    }

    [Fact]
    public void 空洞里的地址_明确报outside()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange(A, 0x1000, 0x10),
            new ModuleRange(B, 0x9000, 0x10),
        });
        // 0x2000 落在两个模块之间：不能"就近吸附"到 a.dll
        Assert.Equal("outside@0x0000000000002000", s.Resolve(0x2000));
    }

    /// <summary>乱序输入也必须解析正确——构造时排过序。真实调用方（进程模块枚举）给的顺序
    /// 不保证按基址递增，这条如果实现漏了排序就会红。</summary>
    [Fact]
    public void 乱序输入_按基址排序后仍可解析()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange(B, 0x5000, 0x100),
            new ModuleRange(A, 0x1000, 0x100),
        });
        Assert.Equal("a.dll+0x40", s.Resolve(0x1040));
        Assert.Equal("b.dll+0x40", s.Resolve(0x5040));
    }

    [Fact]
    public void 二分查得到深表里的中段与两端()
    {
        var list = new System.Collections.Generic.List<ModuleRange>();
        for (var i = 0; i < 64; i++)
            list.Add(new ModuleRange($"m{i}.dll", 0x1000UL + (ulong)i * 0x1000, 0x1000));
        var s = new FaultSymbolizer(list);
        Assert.Equal("m0.dll+0x0", s.Resolve(0x1000));
        Assert.Equal("m31.dll+0xff8", s.Resolve(0x1000 + 31 * 0x1000 + 0xff8));
        Assert.Equal("m63.dll+0xfff", s.Resolve(0x1000 + 63 * 0x1000 + 0xfff));
        Assert.StartsWith("outside@", s.Resolve(0x1000 + 64 * 0x1000));   // 尾后一个字节
        Assert.StartsWith("outside@", s.Resolve(0x0fff));                 // 首前一个字节
    }

    // ─────────────── 输入卫生 ───────────────

    [Fact]
    public void 空表_一切地址都是outside_且计数为零()
    {
        var s = new FaultSymbolizer(System.Array.Empty<ModuleRange>());
        Assert.Equal(0, s.Count);
        Assert.Equal("outside@0x00007ff800000000", s.Resolve(0x7ff8_0000_0000UL));
    }

    /// <summary>大小为 0 的条目不可能包住任何地址，必须被丢弃；留着它只会让
    /// <see cref="FaultSymbolizer.Count"/> 虚报"仪器看得见多少模块"。</summary>
    [Fact]
    public void 零大小条目被丢弃()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange(A, 0x1000, 0),
            new ModuleRange(B, 0x2000, 0x10),
        });
        Assert.Equal(1, s.Count);
        Assert.Equal("b.dll+0x0", s.Resolve(0x2000));
    }

    [Fact]
    public void 空模块名条目被丢弃()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange("", 0x1000, 0x10),
            new ModuleRange(B, 0x2000, 0x10),
        });
        Assert.Equal(1, s.Count);
        Assert.Equal("b.dll+0x5", s.Resolve(0x2005));
    }

    [Fact]
    public void 构造不接受null表()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new FaultSymbolizer(null!));
    }

    // ─────────────── 读数形态 ───────────────

    [Fact]
    public void 偏移用小写十六进制且不补零()
    {
        // 镜像给足 0x20000：0xabcd 必须仍在区间内，否则这条测的是 outside 分支而不是格式化。
        var s = new FaultSymbolizer(new[] { new ModuleRange(A, 0x10000, 0x20000) });
        Assert.Equal("a.dll+0x1a", s.Resolve(0x1001a));
        Assert.Equal("a.dll+0xabcd", s.Resolve(0x10000 + 0xabcd));
    }

    [Fact]
    public void outside地址补齐十六位便于与WER对表()
    {
        // WER 的 Sig 字段里地址就是 16 位十六进制；留痕与它对表时不该一边补零一边不补。
        var s = new FaultSymbolizer(System.Array.Empty<ModuleRange>());
        Assert.Equal("outside@0x0000000000000001", s.Resolve(1));
    }

    /// <summary>同基址（同一模块被重复枚举）保留先出现的那份：不合并、不猜哪个对，
    /// 但必须保证 <see cref="FaultSymbolizer.Count"/> 不虚增到让读数失去意义。</summary>
    [Fact]
    public void 同基址重复条目_保留第一份()
    {
        var s = new FaultSymbolizer(new[]
        {
            new ModuleRange(A, 0x1000, 0x100),
            new ModuleRange(B, 0x1000, 0x900),
        });
        Assert.Equal("a.dll+0x10", s.Resolve(0x1010));
    }

    // ─────────────── 缓存表漏模块时的映射名归名（#41）───────────────
    // 现场起因：2026-10-04 04:41 的留痕里表是崩溃前 2 秒重建的（modules=52），
    // 却仍然没有 dxgi ⇒ `ip=outside@…`，而同一段 `region=` 明写 `type=MEM_IMAGE
    // allocBase=0x7ffe320e0000`。地址真的在某个 dll 里而仪器说"outside"，是这台仪器
    // 最容易把人带偏的一种读数，所以归名那条 fallback 必须有边界判据。

    [Fact]
    public void 映射名_设备路径给出小写叶子与偏移()
    {
        // 手算：0x7FFEC71A35A3 − 0x7FFEC7170000 = 0x335A3
        var got = FaultSymbolizer.TryFormatImageRange(
            @"\Device\HarddiskVolume3\Windows\SYSTEM32\dxgi.dll", 0x7FFEC7170000, 0x7FFEC71A35A3);
        Assert.Equal("dxgi.dll+0x335a3", got);
    }

    [Fact]
    public void 映射名_地址就是分配基址_偏移为零()
    {
        var got = FaultSymbolizer.TryFormatImageRange(
            @"C:\Windows\System32\dxgi.dll", 0x7FFEC7170000, 0x7FFEC7170000);
        Assert.Equal("dxgi.dll+0x0", got);
    }

    /// <summary>地址**低于**分配基址绝不写成一个巨大的回绕偏移：宁可不归名。
    /// 没有这一条，任何把减法写成无回绕检查的实现都会产出一个"看着像模块+偏移"的假读数，
    /// 而这恰恰是这台仪器被造出来要避免的那类错。</summary>
    [Fact]
    public void 映射名_地址低于分配基址_不归名()
    {
        Assert.Null(FaultSymbolizer.TryFormatImageRange(
            @"C:\Windows\System32\dxgi.dll", 0x7FFEC7170000, 0x7FFEC716FFF0));
    }

    [Fact]
    public void 映射名_路径缺失或全空白_不归名()
    {
        Assert.Null(FaultSymbolizer.TryFormatImageRange(null, 0x1000, 0x1010));
        Assert.Null(FaultSymbolizer.TryFormatImageRange("", 0x1000, 0x1010));
        Assert.Null(FaultSymbolizer.TryFormatImageRange("   ", 0x1000, 0x1010));
    }

    [Fact]
    public void 映射名_分配基址为零_不归名()
    {
        // VirtualQuery 对 MEM_FREE 段的 AllocationBase 就是 0（今天 6/7 份留痕正是这种），
        // 这时任何"算出来的偏移"都是无意义的。
        Assert.Null(FaultSymbolizer.TryFormatImageRange(
            @"C:\Windows\System32\dxgi.dll", 0, 0x7FFEC71A35A3));
    }

    [Fact]
    public void 映射名_结尾是分隔符_没有文件名_不归名()
    {
        Assert.Null(FaultSymbolizer.TryFormatImageRange(
            @"C:\Windows\System32\dxgi.dll\", 0x1000, 0x1100));
    }

    /// <summary>表里的模块名都是小写叶子，映射名可能回 <c>DXGI.dll</c> ⇒ 统一一次，
    /// 免得同一份留痕里出现两个写法被后人当成两个模块。</summary>
    [Fact]
    public void 映射名_大写叶子_与表里那份同名()
    {
        Assert.Equal("dxgi.dll+0x0", FaultSymbolizer.TryFormatImageRange(
            @"C:\Windows\System32\DXGI.dll", 0x1000, 0x1000));
    }

    /// <summary>两个来源的**版式必须可互换**：表命中的 <c>Resolve</c> 与映射名那条要给出同一串，
    /// 否则离线比对（拿 10-03 的归档留痕比今天的新留痕）会先把"来源不同"当成"模块不同"。</summary>
    [Fact]
    public void 映射名版式与表命中一致()
    {
        var s = new FaultSymbolizer(new[] { new ModuleRange(A, 0x1000, 0x200) });
        var viaTable = s.Resolve(0x11ff);
        var viaMapping = FaultSymbolizer.TryFormatImageRange(@"C:\x\a.dll", 0x1000, 0x11ff);
        Assert.Equal(viaTable, viaMapping);
        Assert.Equal("a.dll+0x1ff", viaMapping);
    }
}
