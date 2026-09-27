using _3FCompare.Core.Settings;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 设置的持久化路径与损坏回退（docs/41 §4.5 第 10 项）。
///
/// <para><b>为什么这组必须碰真实文件系统</b>：被测的正是"配置写到哪儿"与
/// "写坏了读哪儿"——<c>SettingsStore</c> 没有可注入的 baseDir seam，
/// 配置路径由 <c>AppContext.BaseDirectory</c> 硬决定（便携部署是明确设计目标）。
/// 因此本组直接在**测试输出目录**上做真写读，并在夹具里保存/还原现场，
/// 绝不把测试输出目录里原本存在的 settings.json 覆盖掉。</para>
///
/// <para><b>⚠ 未覆盖项</b>："exe 目录不可写 ⇒ 回退 %LOCALAPPDATA%"这一分支
/// **本组无法可靠模拟**（见类末注释），已在交付说明中如实标注，未新建生产 seam。</para>
/// </summary>
public class SettingsStoreTests : IDisposable
{
    // 便携路径：与 exe 同目录（AppContext.BaseDirectory 就是测试输出目录）
    private static readonly string 便携路径 = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly string 备份路径 = 便携路径 + AtomicFile.BackupFileExtension;

    private readonly bool _便携原有;
    private readonly string? _便携原文;
    private readonly bool _备份原有;
    private readonly string? _备份原文;

    public SettingsStoreTests()
    {
        _便携原有 = File.Exists(便携路径);
        _便携原文 = _便携原有 ? File.ReadAllText(便携路径) : null;
        _备份原有 = File.Exists(备份路径);
        _备份原文 = _备份原有 ? File.ReadAllText(备份路径) : null;
    }

    public void Dispose()
    {
        还原(便携路径, _便携原有, _便携原文);
        还原(备份路径, _备份原有, _备份原文);
        GC.SuppressFinalize(this);
    }

    private static void 还原(string path, bool existed, string? content)
    {
        try
        {
            if (existed) File.WriteAllText(path, content ?? "");
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 清理失败不掩盖用例失败原因 */ }
    }

    /// <summary>本用例独立实现的"目录可写"探针（**不复用** SettingsStore 的私有探针，
    /// 也不复用 NativeRuntime 的那个）：真的建一个文件再删掉。
    /// 用途是**前置条件**，不是期望值。</summary>
    private static bool 目录可写(string dir)
    {
        var probe = Path.Combine(dir, "3fc_settings_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                                           FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch { return false; }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
    }

    private static void 断言便携分支可用()
        => Assert.True(目录可写(AppContext.BaseDirectory),
            "测试输出目录不可写 ⇒ 本用例针对的是'便携路径分支'，此时无法验证（非产品缺陷）");

    [Fact]
    public void Save后Load_往返一致()
    {
        断言便携分支可用();

        var settings = new AppSettings
        {
            WindowWidth = 1234,
            WindowHeight = 567,
            SidebarWidth = 300,
            FrameStep = 7,
            Language = 1,
        };

        Assert.True(SettingsStore.Save(settings));

        var back = SettingsStore.Load();

        // 期望值 = 上面写死的字面量（独立于实现推算）
        Assert.Equal(1234, back.WindowWidth);
        Assert.Equal(567, back.WindowHeight);
        Assert.Equal(300, back.SidebarWidth);
        Assert.Equal(7, back.FrameStep);
        Assert.Equal(1, back.Language);
        // Save 会盖上当前结构版本（契约：读回时据此判断是否需要迁移）
        Assert.Equal(AppSettings.CurrentVersion, back.Version);
    }

    /// <summary>exe 目录可写 ⇒ 配置落在 exe 同目录（便携部署的**核心契约**：
    /// 若无条件搬到 %APPDATA%，U 盘版每换一台机器就丢配置）。</summary>
    [Fact]
    public void exe目录可写时_配置落在exe同目录()
    {
        断言便携分支可用();

        Assert.True(SettingsStore.Save(new AppSettings { WindowWidth = 4321 }));

        Assert.True(File.Exists(便携路径), $"未在 exe 同目录找到 settings.json：{便携路径}");
        Assert.Contains("4321", File.ReadAllText(便携路径));
    }

    /// <summary>主文件损坏 ⇒ 回退读 .bak（而不是直接回退出厂设置）。
    ///
    /// <para><b>独立推算</b>：第一次 Save 写下 A 版（<c>WindowWidth=1111</c>）；
    /// 第二次 Save 覆盖前会先把当前主文件复制成 .bak（即 A 版），再把主文件换成 B 版。
    /// 随后把主文件写成半截 JSON ⇒ 主文件解析失败 ⇒ 只能从 .bak 读到 A 版。
    /// 出厂默认的 <c>WindowWidth</c> 是 <c>null</c>，与 1111 天然可区分，
    /// 故"读到 1111"不可能由"回退默认"造成。</para></summary>
    [Fact]
    public void 主文件损坏时_从bak恢复()
    {
        断言便携分支可用();

        Assert.True(SettingsStore.Save(new AppSettings { WindowWidth = 1111, FrameStep = 3 }));
        Assert.True(SettingsStore.Save(new AppSettings { WindowWidth = 2222, FrameStep = 9 }));

        Assert.True(File.Exists(备份路径), "第二次 Save 应留下上一版 .bak");
        Assert.Contains("1111", File.ReadAllText(备份路径));
        Assert.Contains("2222", File.ReadAllText(便携路径));

        File.WriteAllText(便携路径, """{"WindowWidth": 半截""");   // 半截 JSON

        var back = SettingsStore.Load();

        Assert.Equal(1111, back.WindowWidth);   // 来自 .bak（A 版），不是 2222、也不是默认 null
        Assert.Equal(3, back.FrameStep);
    }

    /// <summary>保存失败必须留下可读的原因，而不是静默吞掉。
    ///
    /// <para><b>故障注入</b>：把主文件用 <c>FileShare.None</c> 独占打开——这模拟的是
    /// 真实世界最常见的一种失败（杀软/同步盘/另一实例正持有配置文件）。
    /// 原子写的最后一步 <c>File.Move(tmp, path, overwrite:true)</c> 无法替换被独占的目标，
    /// 于是 Save 必须返回 false 且把异常写进 <c>LastSaveError</c>。</para>
    ///
    /// <para><b>对照</b>：释放占用后再存一次必须成功、且 <c>LastSaveError</c> 被清回 null
    /// （契约：null = 还没失败过 / 最后一次成功）——否则上面那条"非空"可能是恒真。</para></summary>
    [Fact]
    public void 保存失败时_返回false并设置LastSaveError()
    {
        断言便携分支可用();

        Assert.True(SettingsStore.Save(new AppSettings()));   // 先成功一次 ⇒ LastSaveError 归 null
        Assert.Null(SettingsStore.LastSaveError);

        using (new FileStream(便携路径, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ok = SettingsStore.Save(new AppSettings { WindowWidth = 999 });

            Assert.False(ok);
            Assert.NotNull(SettingsStore.LastSaveError);
            Assert.NotEqual("", SettingsStore.LastSaveError);
        }

        Assert.True(SettingsStore.Save(new AppSettings { WindowWidth = 888 }));
        Assert.Null(SettingsStore.LastSaveError);
    }

    // ─────────────────────────────────────────────────────────────
    // 未覆盖："exe 目录不可写 ⇒ 回退 %LOCALAPPDATA%\3FCompare"
    //
    // 原因：ResolveConfigPath 的候选目录来自 AppContext.BaseDirectory（硬编码），
    // 且 _resolvedConfigPath 是进程级缓存，全无注入点。要覆盖它只能
    // ① 把测试输出目录改成只读（Windows 下受 ACL 继承/UAC 虚拟化影响，行为不稳定，
    //    且会连累同进程的其它测试）；或 ② 新增一个生产 seam（可注入 baseDir）。
    // 按工单要求"不要新建生产 seam 只为测试"，这里选择如实标注为未覆盖。
    //
    // 间接可覆盖的部分：回退**目标目录**的常量（LocalApplicationData + "3FCompare"）
    // 已由 AppSettings 的注释与代码固定；"可写性探针"这一判据本身已在本文件
    // 用独立实现（目录可写）间接验证过其可靠性。
    // ─────────────────────────────────────────────────────────────
}
