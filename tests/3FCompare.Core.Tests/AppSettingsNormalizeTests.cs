using _3FCompare.Core.Settings;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 设置值收敛回归（docs/15 §4.3）。
///
/// 畸形 JSON 会被 SettingsStore.Load 的 catch 整体兜住（回退默认），
/// 但**合法 JSON 里的越界值**不会被拦：ColorMode=99 会直接转成枚举喂给内核、
/// FrameStep=int.MaxValue 会让步长变成荒谬值。所以读取后必须收敛一次。
/// </summary>
public class AppSettingsNormalizeTests
{
    [Fact]
    public void Normalize_越界值被收敛到合法区间()
    {
        var s = new AppSettings
        {
            ColorMode = (ColorModeSetting)99,
            FrameStep = int.MaxValue,
            SecondsStep = 1e9,
            DefaultGridCols = 50,
            DefaultGridRows = 0,
            PreferredAdapterIndex = 9999,
            SidebarWidth = -5,
            WindowState = 3,            // FullScreen：恢复它会让用户莫名全屏
        };

        s.Normalize();

        Assert.Equal(ColorModeSetting.Auto, s.ColorMode);
        Assert.Equal(999, s.FrameStep);
        Assert.Equal(3, s.DefaultGridCols);
        Assert.Equal(1, s.DefaultGridRows);
        Assert.Equal(15, s.PreferredAdapterIndex);
        Assert.Null(s.SidebarWidth);
        Assert.Null(s.WindowState);
    }

    [Fact]
    public void Normalize_下界同样被收敛()
    {
        var s = new AppSettings
        {
            FrameStep = int.MinValue,
            SecondsStep = -3,
            PreferredAdapterIndex = -999,   // -1 才是"系统默认"
        };

        s.Normalize();

        Assert.Equal(1, s.FrameStep);
        Assert.Equal(-1, s.PreferredAdapterIndex);
    }

    /// <summary>收敛不能误伤合法值——尤其是用户特意设的"非默认"配置。</summary>
    [Fact]
    public void Normalize_合法值原样保留()
    {
        var s = new AppSettings
        {
            ColorMode = ColorModeSetting.MapToHdr,
            FrameStep = 3,
            SecondsStep = 5.0,
            DefaultGridCols = 3,
            DefaultGridRows = 3,
            PreferredAdapterIndex = 2,
            SidebarWidth = 300,
            WindowState = 2,            // Maximized：允许恢复
        };

        s.Normalize();

        Assert.Equal(ColorModeSetting.MapToHdr, s.ColorMode);
        Assert.Equal(3, s.FrameStep);
        Assert.Equal(5.0, s.SecondsStep);
        Assert.Equal(3, s.DefaultGridCols);
        Assert.Equal(3, s.DefaultGridRows);
        Assert.Equal(2, s.PreferredAdapterIndex);
        Assert.Equal(300, s.SidebarWidth);
        Assert.Equal(2, s.WindowState);
    }

    /// <summary>Minimized(1) 无意义，同样不恢复。</summary>
    [Fact]
    public void Normalize_Minimized状态被丢弃()
    {
        var s = new AppSettings { WindowState = 1 };
        s.Normalize();
        Assert.Null(s.WindowState);
    }
}
