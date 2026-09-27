using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 崩溃退出码判定（<see cref="CrashExitCode"/>，docs/43 配套）。
///
/// <para><b>为什么这组用例值得写</b>：判定的输入口径在不同宿主间是<b>不一致</b>的——
/// .NET 的 <c>Process.ExitCode</c> 给有符号值，Python 的 <c>returncode</c> 给无符号值，
/// bash 的 <c>$?</c> 则把任何异常终止压成 1。而"这是不是崩溃"决定了守护进程
/// **要不要重启用户界面**：判错一次，表现要么是"崩溃了没人管"，
/// 要么是"用户点了关闭，窗口又自己弹回来"。后者更糟，所以它必须是可断言的生产代码。</para>
///
/// <para><b>期望值来源</b>：全部为写死的十六进制字面量与十进制常量，
/// 不调用被测实现自己生成期望（docs/41 §4.3 的"同源互证"）。</para>
/// </summary>
public class CrashExitCodeTests
{
    // ── 有符号口径：.NET Process.ExitCode ──────────────────────────────
    [Fact]
    public void IsCrash_有符号访问违规_判为崩溃()
    {
        // 0xC0000005 = 3221225477；作为 int 重解释即 -1073741819
        Assert.True(CrashExitCode.IsCrash(-1073741819));
    }

    [Fact]
    public void IsCrash_有符号非法指令_判为崩溃()
    {
        // 0xC000001D = 3221225501；作为 int 重解释即 -1073741795
        Assert.True(CrashExitCode.IsCrash(-1073741795));
    }

    // ── 无符号口径：Python subprocess / 日志对账 ────────────────────────
    [Fact]
    public void IsCrash_无符号访问违规与非法指令_判为崩溃()
    {
        Assert.True(CrashExitCode.IsCrash(0xC0000005u));
        Assert.True(CrashExitCode.IsCrash(0xC000001Du));
    }

    // ── 非崩溃：必须一律 false，否则会误重启 ────────────────────────────
    [Theory]
    [InlineData(0)]          // 正常退出
    [InlineData(1)]          // --multitest 的 DRIFT 断言失败：不是崩溃
    [InlineData(2)]          // 参数错误
    [InlineData(-1)]         // 0xFFFFFFFF
    public void IsCrash_正常与业务退出码_不判为崩溃(int code)
    {
        Assert.False(CrashExitCode.IsCrash(code));
    }

    [Fact]
    public void IsCrash_HRESULT式非崩溃码_不判为崩溃()
    {
        // 0x8000FFFF（E_UNEXPECTED）落在 Severity=1 段：正常程序完全可能主动返回它。
        // 若把 >= 0x80000000 一律当崩溃，用户点关闭就会被重启一次。
        Assert.False(CrashExitCode.IsCrash(0x8000FFFFu));
    }

    [Fact]
    public void IsCrash_CLR托管异常码_不判为崩溃()
    {
        // 0xE0434352 = EXCEPTION_COMPLUS：托管未处理异常。它是**必现**的确定性错误，
        // 重启只会让用户反复看到同一个弹窗；而本机制要防的是外部钩子导致的偶发竞态。
        Assert.False(CrashExitCode.IsCrash(0xE0434352u));
    }

    // ── 边界：区间必须恰好是 [0xC0000000, 0xD0000000) ───────────────────
    [Theory]
    [InlineData(0xBFFFFFFFu, false)]
    [InlineData(0xC0000000u, true)]
    [InlineData(0xCFFFFFFFu, true)]
    [InlineData(0xD0000000u, false)]
    public void IsCrash_区间边界(uint code, bool expected)
    {
        Assert.Equal(expected, CrashExitCode.IsCrash(code));
    }

    // ── 两种口径必须给出同一结论（否则日志与判定会互相打架）─────────────
    [Fact]
    public void IsCrash_有符号与无符号口径一致()
    {
        Assert.Equal(CrashExitCode.IsCrash(0xC0000005u),
                     CrashExitCode.IsCrash(unchecked((int)0xC0000005)));
        Assert.Equal(CrashExitCode.IsCrash(0u),
                     CrashExitCode.IsCrash(0));
    }

    // ── AsUnsigned：用于日志与 WER 对账 ─────────────────────────────────
    [Fact]
    public void AsUnsigned_负值还原为NTSTATUS()
    {
        Assert.Equal(0xC0000005u, CrashExitCode.AsUnsigned(-1073741819));
        Assert.Equal(0u, CrashExitCode.AsUnsigned(0));
    }

    // ── Describe：必须保留十六进制原值（要与 WER 记录逐位对账）──────────
    [Fact]
    public void Describe_已知码_含十六进制与名称()
    {
        var av = CrashExitCode.Describe(-1073741819);
        Assert.Contains("0xC0000005", av);
        Assert.Contains("ACCESS_VIOLATION", av);

        var ill = CrashExitCode.Describe(-1073741795);
        Assert.Contains("0xC000001D", ill);
        Assert.Contains("ILLEGAL_INSTRUCTION", ill);
    }

    [Fact]
    public void Describe_未知崩溃码_仍带十六进制且标明未分类()
    {
        var d = CrashExitCode.Describe(0xC0000096u);
        Assert.Contains("0xC0000096", d);
        Assert.Contains("PRIVILEGED_INSTRUCTION", d);
    }

    [Fact]
    public void Describe_正常退出_不出现崩溃字样()
    {
        var d = CrashExitCode.Describe(0);
        Assert.Contains("0x00000000", d);
        Assert.DoesNotContain("崩溃", d);
    }
}
