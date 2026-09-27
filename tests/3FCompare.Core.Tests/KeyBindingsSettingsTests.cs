using System.Text.Json;
using _3FCompare.Core.Settings;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="KeyBindingsSettings.Normalize"/> 的契约（可自定义快捷键的设置层）。
///
/// <para><b>为什么必须测</b>：这一层是"老配置文件 + 手改过的配置文件"进程序的唯一入口。
/// 三条各自都有实打实的代价：① 老文件缺 <c>KeyBindings</c> 段必须落到出厂键位，漏了的话
/// 升级完 ←/→/A/D/空格 全部失灵；② <b>空串（用户主动解绑）不能被默认值覆盖</b>，
/// 否则"清掉某个键"会在下次启动自己长回来；③ 同一个键绑给两个动作时必须<b>先声明者赢</b>，
/// 否则后加载的赢 = 谁写文件谁抢键，且没有任何提示。</para>
///
/// <para><b>期望值来源（独立推算，不读被测代码的输出）</b>：默认键位按产品规格手推 ——
/// 底栏从左到右是「按秒退 / 按帧退 / 播放暂停 / 按帧进 / 按秒进」，对应
/// <c>Left / A / Space / D / Right</c>，「停止」<b>没有</b>界面按钮故默认<b>不占键</b>（空串）。
/// 冲突用例的期望值是按"槽序 = 声明顺序，先声明者赢、撞车者回落自己的默认值、
/// 默认值也被占则不绑"这条规则逐槽手推出来的。</para>
///
/// <para><b>为什么全程内存序列化而不碰 <see cref="SettingsStore"/>.Save/Load</b>：那两个方法写
/// exe 同目录的 settings.json，会与并行跑的 SettingsStoreTests 抢同一个文件（随机判红）。
/// 走 <see cref="JsonAotContext"/> 反而是更贴近生产的那条路 —— NativeAOT 下用的就是它。</para></summary>
public class KeyBindingsSettingsTests
{
    private static AppSettings Parse(string json)
    {
        var s = JsonSerializer.Deserialize(json, JsonAotContext.Default.AppSettings)!;
        s.Normalize();
        return s;
    }

    [Fact]
    public void 空对象落到默认键位()
    {
        var s = Parse("{}");
        Assert.Equal("Left", s.KeyBindings.StepSecondBackward);
        Assert.Equal("Right", s.KeyBindings.StepSecondForward);
        Assert.Equal("A", s.KeyBindings.StepFrameBackward);
        Assert.Equal("D", s.KeyBindings.StepFrameForward);
        Assert.Equal("Space", s.KeyBindings.PlayPause);
        Assert.Equal("", s.KeyBindings.Stop);
    }

    [Fact]
    public void 老配置文件没有这一段也能加载()
    {
        // 真实老文件形态：没有 Version、没有 KeyBindings
        var s = Parse("""{"FrameStep": 3, "SecondsStep": 2.5, "HardwareDecode": false}""");
        Assert.Equal(3, s.FrameStep);
        Assert.Equal(2.5, s.SecondsStep);
        Assert.Equal("Left", s.KeyBindings.StepSecondBackward);
        Assert.Equal("", s.KeyBindings.Stop);
    }

    [Fact]
    public void 显式null段与显式null槽回落默认()
    {
        var s = Parse("""{"KeyBindings": null}""");
        Assert.Equal("Space", s.KeyBindings.PlayPause);
        s = Parse("""{"KeyBindings": {"PlayPause": null, "Stop": "x"}}""");
        Assert.Equal("Space", s.KeyBindings.PlayPause);
        Assert.Equal("x", s.KeyBindings.Stop);
    }

    [Fact]
    public void 清除绑定写空串不会被默认值覆盖()
    {
        var s = Parse("""{"KeyBindings": {"StepSecondBackward": "", "PlayPause": "  "}}""");
        Assert.Equal("", s.KeyBindings.StepSecondBackward);
        Assert.Equal("", s.KeyBindings.PlayPause);   // 纯空白 = 用户想清掉，不是"没写"
        Assert.Equal("Right", s.KeyBindings.StepSecondForward);
    }

    [Fact]
    public void 同键冲突时先声明者赢_后者回落默认()
    {
        // Stop（默认空）抢 StepSecondBackward（默认 Left）→ 后者槽序靠前 = 赢，Stop 回落到自己的默认 ""
        var s = Parse("""{"KeyBindings": {"StepSecondBackward": "Left", "Stop": "Left"}}""");
        Assert.Equal("Left", s.KeyBindings.StepSecondBackward);
        Assert.Equal("", s.KeyBindings.Stop);

        // 帧进（默认 D）被帧退抢成 A → 帧进回落默认 D；帧退保持 A
        s = Parse("""{"KeyBindings": {"StepFrameBackward": "A", "StepFrameForward": "A"}}""");
        Assert.Equal("A", s.KeyBindings.StepFrameBackward);
        Assert.Equal("D", s.KeyBindings.StepFrameForward);

        // 撞车后回落到自己的默认值（默认值没被占时不降级成"未绑定"）
        s = Parse("""{"KeyBindings": {"StepSecondBackward": "Q", "StepSecondForward": "Q", "StepFrameBackward": "Q"}}""");
        Assert.Equal("Q", s.KeyBindings.StepSecondBackward);
        Assert.Equal("Right", s.KeyBindings.StepSecondForward);
        Assert.Equal("A", s.KeyBindings.StepFrameBackward);
    }

    [Fact]
    public void 连默认值也被占时改为未绑定()
    {
        // 帧退（默认 A）被秒退抢走同一个键，且它的默认值 A 也已在表里 ⇒ 只能不绑
        var s = Parse("""{"KeyBindings": {"StepSecondBackward": "A", "StepFrameBackward": "A", "StepFrameForward": "A"}}""");
        Assert.Equal("A", s.KeyBindings.StepSecondBackward);   // 槽序第一 = 赢
        Assert.Equal("", s.KeyBindings.StepFrameBackward);     // 回落默认 A 也被占 → 未绑定
        Assert.Equal("D", s.KeyBindings.StepFrameForward);     // 回落默认 D 是空的 → 用 D
    }

    [Fact]
    public void 组合键规范形与大小写不影响冲突判定()
    {
        var s = Parse("""{"KeyBindings": {"StepSecondBackward": "ctrl + left", "StepFrameForward": "CTRL+LEFT"}}""");
        // 修饰键被规范成 Ctrl、空格去掉；键名 token 原样保留（Core 没有 Key 枚举，改不动大小写），
        // 所以这里看到的是 "Ctrl+left" 而不是 "Ctrl+Left" —— UI 层展示与落盘前会再走一遍 Canonical。
        Assert.Equal("Ctrl+left", s.KeyBindings.StepSecondBackward);
        Assert.Equal("D", s.KeyBindings.StepFrameForward);   // 大小写不同的同键仍被判为冲突并回落

        // 多个槽同时未绑定不算冲突
        s = Parse("""{"KeyBindings": {"Stop": "", "PlayPause": "", "StepFrameBackward": ""}}""");
        Assert.Equal("", s.KeyBindings.Stop);
        Assert.Equal("", s.KeyBindings.PlayPause);
        Assert.Equal("", s.KeyBindings.StepFrameBackward);
    }

    [Fact]
    public void 存盘再读回保持键位()
    {
        var s = new AppSettings();
        s.KeyBindings.Stop = "S";           // 手改出来的合法值（S 在 Core 层不算冲突）
        s.KeyBindings.PlayPause = "Space";
        // 刻意走内存里的序列化而不是 SettingsStore.Save/Load：后者写 exe 同目录的 settings.json，
        // 会和并行跑的 SettingsStoreTests 抢同一个文件（表现为随机判红）。
        // 序列化必须走 JsonAotContext —— 这才是 NativeAOT 下真正用的那条路。
        var json = JsonSerializer.Serialize(s, JsonAotContext.Default.AppSettings);
        Assert.Contains("KeyBindings", json);
        var back = Parse(json);
        Assert.Equal("S", back.KeyBindings.Stop);
        Assert.Equal("Space", back.KeyBindings.PlayPause);
        Assert.Equal("Left", back.KeyBindings.StepSecondBackward);
    }
}
