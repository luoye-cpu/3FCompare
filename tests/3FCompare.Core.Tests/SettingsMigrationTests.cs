using _3FCompare.Core.Settings;

namespace _3FCompare.Core.Tests;

/// <summary>旧版 settings.json 迁移（可用性 P0-2 配套）。
/// 老配置用 -1 哨兵表示"未设置坐标"、用布尔量 WindowMaximized 表示最大化；
/// 新格式改用 null + WindowState int。不迁移会导致两种真实故障：
/// ① -1 在新语义下是合法坐标（副屏在主屏左侧时坐标为负），窗口可能跑到屏幕外；
/// ② WindowMaximized 已被移除且源生成 JSON 会忽略未映射成员，最大化状态会静默丢失。</summary>
public class SettingsMigrationTests
{
    [Fact]
    public void MigrateLegacy_MinusOneSentinel_BecomesNull()
    {
        // 未标注 Version ⇒ 视为老文件（本字段引入之前的旧格式），走 -1 哨兵迁移
        var s = new AppSettings { WindowX = -1, WindowY = -1 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Null(s.WindowX);
        Assert.Null(s.WindowY);
    }

    // ══════ docs/15 §4.2：迁移必须按版本触发，不能无条件跑 ══════

    /// <summary>
    /// 已带版本号的文件：-1 是**合法坐标**（副屏在主屏左侧），迁移不得再清掉它。
    /// 无条件执行迁移的后果：用户把窗口停在 X=-1 的副屏上，每次启动位置都被丢弃。
    /// </summary>
    [Fact]
    public void MigrateLegacy_带版本号时_负一坐标被保留()
    {
        var s = new AppSettings { Version = AppSettings.CurrentVersion, WindowX = -1, WindowY = -1 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Equal(-1, s.WindowX);
        Assert.Equal(-1, s.WindowY);
    }

    /// <summary>老文件迁移后应被盖上当前版本号，避免下次启动重复迁移。</summary>
    [Fact]
    public void MigrateLegacy_老文件迁移后写入当前版本号()
    {
        var s = new AppSettings { Version = 0, WindowX = -1 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Null(s.WindowX);
    }

    [Fact]
    public void MigrateLegacy_NegativeRealCoordinate_IsPreserved()
    {
        // 副显示器位于主屏左侧时坐标为负——这是合法坐标，不能被 -1 哨兵逻辑误伤
        var s = new AppSettings { WindowX = -1920, WindowY = 100 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Equal(-1920, s.WindowX);
        Assert.Equal(100, s.WindowY);
    }

    [Fact]
    public void MigrateLegacy_ZeroCoordinate_IsPreserved()
    {
        // 0 是合法坐标（贴左上），不能当成"未设置"
        var s = new AppSettings { WindowX = 0, WindowY = 0 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Equal(0, s.WindowX);
        Assert.Equal(0, s.WindowY);
    }

    [Fact]
    public void MigrateLegacy_NonPositiveSize_BecomesNull()
    {
        var s = new AppSettings { WindowWidth = 0, WindowHeight = -5 };

        SettingsStore.MigrateLegacy(s, "{}");

        Assert.Null(s.WindowWidth);
        Assert.Null(s.WindowHeight);
    }

    [Fact]
    public void MigrateLegacy_WindowMaximizedTrue_MapsToMaximizedState()
    {
        var s = new AppSettings();

        SettingsStore.MigrateLegacy(s, """{"WindowMaximized":true}""");

        Assert.Equal(2, s.WindowState); // Avalonia WindowState.Maximized
    }

    [Fact]
    public void MigrateLegacy_WindowMaximizedFalse_DoesNotOverrideState()
    {
        var s = new AppSettings { WindowState = 0 };

        SettingsStore.MigrateLegacy(s, """{"WindowMaximized":false}""");

        Assert.Equal(0, s.WindowState);
    }

    [Fact]
    public void MigrateLegacy_ExistingWindowState_WinsOverLegacyFlag()
    {
        // 已迁移过的配置不应被旧的 WindowMaximized 反复覆盖
        var s = new AppSettings { WindowState = 0 };

        SettingsStore.MigrateLegacy(s, """{"WindowMaximized":true}""");

        Assert.Equal(0, s.WindowState);
    }

    [Fact]
    public void MigrateLegacy_MalformedJson_DoesNotThrow()
    {
        var s = new AppSettings { WindowX = 10, WindowY = 20 };

        SettingsStore.MigrateLegacy(s, "not-json-at-all");

        // 迁移失败时其余字段保持原样，不影响启动
        Assert.Equal(10, s.WindowX);
        Assert.Equal(20, s.WindowY);
    }

    [Fact]
    public void MigrateLegacy_TypicalLegacyConfig_IsFullyMigrated()
    {
        // 真实老配置文件形态：-1 坐标 + 1600x900 + WindowMaximized
        var legacy = """
        {"HardwareDecode":true,"WindowX":-1,"WindowY":-1,"WindowWidth":1600,
         "WindowHeight":900,"WindowMaximized":true,"Language":0}
        """;
        var s = new AppSettings
        {
            WindowX = -1, WindowY = -1, WindowWidth = 1600, WindowHeight = 900,
        };

        SettingsStore.MigrateLegacy(s, legacy);

        Assert.Null(s.WindowX);
        Assert.Null(s.WindowY);
        Assert.Equal(1600, s.WindowWidth);
        Assert.Equal(900, s.WindowHeight);
        Assert.Equal(2, s.WindowState);
    }
}
