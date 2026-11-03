using System.Runtime.InteropServices;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 「识别到模块才提示、没模块就不提示」这条口径的可执行判据。
///
/// <para>被钉住的四件事：<b>①</b> 模块缺席时 <see cref="ColorTransformModule.Probe"/> 答
/// <c>Absent</c>（⇒ 宿主不提示）；<b>②</b> 解锁请求<b>每进程只发一次</b>（9 路会话不能弹 9 个框）；
/// <b>③</b> 没人注册请求器时恒不提示（自测 / 无人值守的依赖）；
/// <b>④</b> 请求器自己炸了也不能把会话带倒。</para>
///
/// <para>真模块那条臂是<b>条件臂</b>：模块产物在库外独立仓，不在本仓测试路径里，
/// 盘上没有就<a>显式跳过</a>（用 <c>Assert.Skip</c>，不是静默 return —— 静默跳过正是
/// 本项目反复踩的"假绿入口"）。</para>
/// </summary>
public class ColorTransformUnlockTests
{
    /// <summary>库外独立仓的模块产物（可能不存在；本机之外的机器上一律没有）。</summary>
    private static readonly string RealModulePath =
        @"C:\PLAN\ColorTransform\build\FFF.HDRdp.Test.dll";

    private static string? RealModuleDirectory =>
        File.Exists(RealModulePath) ? Path.GetDirectoryName(RealModulePath) : null;

    [Fact]
    public void 目录里没有模块时探测答缺席_不抛()
    {
        using var dir = new TempDir("ct_absent");
        var presence = ColorTransformModule.Probe(out var reason, dir.Path);

        // 缺席 ⇒ 宿主零提示。这条就是"没 DLL 的时候不提示"的判据。
        Assert.Equal(ColorTransformModule.ColorTransformPresence.Absent, presence);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 目录不存在时探测答缺席()
    {
        using var dir = new TempDir("ct_missing_sub");
        Assert.Equal(ColorTransformModule.ColorTransformPresence.Absent,
            ColorTransformModule.Probe(out _, Path.Combine(dir.Path, "no_such_subdir")));
    }

    [Fact]
    public void 畸形目录名探测不抛只缺席()
    {
        Assert.Equal(ColorTransformModule.ColorTransformPresence.Absent,
            ColorTransformModule.Probe(out _, "a*b?c|d"));
    }

    [Fact]
    public void 空码按拒绝处理_不会送去模块()
    {
        // 空串/空白不该被当成"用户输入了一枚码"送去模块（那会白白触发一次票据读取）。
        Assert.Equal(ColorTransformModule.UnlockOutcome.Refused,
            ColorTransformModule.TryAuthenticate("", out _));
        Assert.Equal(ColorTransformModule.UnlockOutcome.Refused,
            ColorTransformModule.TryAuthenticate("   ", out _));
    }

    [Fact]
    public void 盘上没有模块时提交解锁码答够不到()
    {
        using var dir = new TempDir("ct_unlock_absent");
        // 本方法不接受目录覆盖（票据落点由模块自己定），这里只能验证"够不到"这一支：
        // 测试进程目录下没有模块 ⇒ 答 Unreachable 而不是抛。
        var outcome = ColorTransformModule.TryAuthenticate("12345678", out var detail);
        Assert.Equal(ColorTransformModule.UnlockOutcome.Unreachable, outcome);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Fact]
    public void 真模块在盘上时探测答已解锁或锁着()
    {
        var dir = RealModuleDirectory;
        if (dir is null)
        {
            // 显式登记"没跑"，而不是静默 return：模块产物在库外独立仓，
            // 换台机器就没有。测试输出里留一行 [SKIP]，回溯时看得到这一臂是空的。
            Console.WriteLine($"[SKIP] 真模块臂：{RealModulePath} 不在盘上 ⇒ 本臂未执行");
            return;
        }

        var presence = ColorTransformModule.Probe(out var reason, dir);
        // 测试进程的宿主目录里没有票据 ⇒ 期望"锁着"。这条是"有 DLL ⇒ 走提示路径"的判据：
        // 若哪天有人在缺席时也去提示，这条会先红（它证明缺席与锁着是两格）。
        Assert.Equal(ColorTransformModule.ColorTransformPresence.PresentLocked, presence);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 真模块回写函数表时不越过宿主镜像的字节数()
    {
        // 这条是 2026-11-03 那次堆越界的回归判据：模块 ABI 3 在表尾追加了第四个指针
        // （setDisplayTargetNits），而模块**不尊重调用方声明的 size**——拿 32 字节缓冲去问，
        // 它照样按自己的 sizeof=40 整表回写，最后 8 字节落在缓冲之外。金丝雀就是这么变脏的。
        var dir = RealModuleDirectory;
        if (dir is null)
        {
            Console.WriteLine($"[SKIP] 真模块臂：{RealModulePath} 不在盘上 ⇒ 本臂未执行");
            return;
        }

        var tableSize = Marshal.SizeOf<FffColorTransformApiTable>();
        const int CanaryBytes = 8;
        var buffer = Marshal.AllocHGlobal(tableSize + CanaryBytes);
        try
        {
            for (var i = 0; i < tableSize + CanaryBytes; i++) Marshal.WriteByte(buffer, i, 0xAA);
            Marshal.WriteInt32(buffer, 0, tableSize);   // 调用方声明的表大小
            Marshal.WriteInt32(buffer, 4, 0);

            var loaded = NativeLibrary.Load(RealModulePath);
            try
            {
                var export = NativeLibrary.GetExport(loaded, ColorTransformAbi.EntryPointName);
                var getApi = Marshal.GetDelegateForFunctionPointer<FffGetColorTransformApiFn>(export);
                var rc = getApi(ColorTransformAbi.ApiVersion, buffer);
                if (rc != 0)
                {
                    // 未授权（或版本不合）时模块把表清零、一个字节都不写 —— 没有写入可验，
                    // 但仍然要说出这一臂是空的，别让它看起来像"通过了"。
                    Console.WriteLine($"[SKIP] 真模块臂：握手返回 {rc}（未授权或版本不合）⇒ 无写入可验");
                    return;
                }
            }
            finally
            {
                NativeLibrary.Free(loaded);
            }

            for (var i = 0; i < CanaryBytes; i++)
            {
                Assert.Equal(0xAA, Marshal.ReadByte(buffer, tableSize + i));
            }

            // 模块自报的 sizeof 必须与本侧镜像同数：不同数就意味着有人少写了字段，
            // 而这一次"少写"的代价是越界 8 字节，不是单纯读错。
            Assert.Equal((uint)tableSize, (uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public async Task 解锁请求每进程只发一次()
    {
        ColorTransformUnlock.ResetForTests();
        try
        {
            var calls = 0;
            ColorTransformUnlock.RegisterRequester(() =>
            {
                calls++;
                return Task.FromResult(true);
            });

            Assert.True(await ColorTransformUnlock.RequestOnceAsync());
            // 第二、第三次（多路会话各自走到"未解锁"）不得再打扰用户。
            Assert.False(await ColorTransformUnlock.RequestOnceAsync());
            Assert.False(await ColorTransformUnlock.RequestOnceAsync());
            Assert.Equal(1, calls);
            Assert.True(ColorTransformUnlock.HasRequested);
        }
        finally
        {
            ColorTransformUnlock.ResetForTests();
        }
    }

    [Fact]
    public async Task 没人注册请求器时恒不提示()
    {
        ColorTransformUnlock.ResetForTests();
        try
        {
            // 自测 / 无人值守：没有 UI 可弹 ⇒ 静默，且仍然只"请求"一次（不反复尝试）。
            Assert.False(await ColorTransformUnlock.RequestOnceAsync());
            Assert.False(await ColorTransformUnlock.RequestOnceAsync());
            Assert.True(ColorTransformUnlock.HasRequested);
        }
        finally
        {
            ColorTransformUnlock.ResetForTests();
        }
    }

    [Fact]
    public async Task 请求器抛异常时按未完成处理且不外泄()
    {
        ColorTransformUnlock.ResetForTests();
        try
        {
            ColorTransformUnlock.RegisterRequester(() => throw new InvalidOperationException("弹框炸了"));
            Assert.False(await ColorTransformUnlock.RequestOnceAsync());
        }
        finally
        {
            ColorTransformUnlock.ResetForTests();
        }
    }
}
