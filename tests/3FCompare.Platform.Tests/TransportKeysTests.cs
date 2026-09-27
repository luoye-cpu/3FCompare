using Avalonia.Input;
using _3FCompare.App;
using _3FCompare.Core.Settings;
using _3FCompare.Services;
using Xunit;

namespace _3FCompare.Platform.Tests;

/// <summary><see cref="TransportKeys"/> 的契约：键名字符串 ⇄ <c>(Key, KeyModifiers)</c>、
/// 固定键位表、展示文案、以及 MainWindow 用的分派表。
///
/// <para><b>为什么必须测</b>：这里每一格都会变成"用户按了没反应"：
/// ① <c>TryResolve</c> 放过 <c>Enum.TryParse</c> 的数字串（"12" → (Key)12 是"成功"的）
/// 就会凭空造出一个不存在的键位；放过同值别名（Number1 = D1）就会让两个槽悄悄撞车；
/// ② 固定键位表与 <c>MainWindow.OnKeyDown</c> 的硬编码分支必须是同一份事实，写少了设置页放行一个
/// 永远按不出来的键，写多了用户以为键位能改却报错；
/// ③ 分派表的三条规则（未绑定不进表 / 后者不赢 / 固定键不进表）就是"改键到底生效没有"本身；
/// ④ 修饰键掩码漏掉平台附加位，表现是"存了 Ctrl+Left、按下匹配不上"且毫无线索。</para>
///
/// <para><b>期望值来源</b>：全部人工手推（箭头/符号按规格、修饰键顺序按
/// <c>AppendModifier</c> 的 Ctrl→Shift→Alt→Win 固定序、分派表条数按"6 槽里 Stop 默认未绑定 ⇒ 5 条"），
/// 不从被测代码反推。语言相关用例切 <see cref="LanguageManager"/> 的静态状态，
/// 故整类挂进不并行的 <see cref="LanguageStateCollection"/>（同 LanguageManagerKeysTests）。</para></summary>
[Collection(LanguageStateCollection.Name)]   // 会切语言 ⇒ 必须与既有语言用例串行
public class TransportKeysTests
{
    [Theory]
    [InlineData("Left", Key.Left, KeyModifiers.None)]
    [InlineData("A", Key.A, KeyModifiers.None)]
    [InlineData("space", Key.Space, KeyModifiers.None)]          // 大小写不敏感
    [InlineData("Ctrl+Right", Key.Right, KeyModifiers.Control)]
    [InlineData("ctrl + shift + F12", Key.F12, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("D1", Key.D1, KeyModifiers.None)]
    public void 解析成功并回写同形(string raw, Key key, KeyModifiers mods)
    {
        Assert.True(TransportKeys.TryResolve(raw, out var k, out var m));
        Assert.Equal(key, k);
        Assert.Equal(mods, m);
        // 提交后再解析一次必须得到同一个键（设置页存的就是这个字符串）
        var spec = TransportKeys.ToSpec(k, m);
        Assert.True(TransportKeys.TryResolve(spec, out var k2, out var m2));
        Assert.Equal(k, k2);
        Assert.Equal(m, m2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Banana")]
    [InlineData("12")]           // Enum.TryParse 对数字串会"成功"，必须被名字回写挡掉
    [InlineData("0")]
    [InlineData("None")]
    [InlineData("Number1")]      // 同值别名（= D1）：回写名字不同 ⇒ 拒，源头上消灭"两种写法同一个键"
    [InlineData("Ctrl+Banana")]
    [InlineData("Left+Right")]   // 中间 token 不是修饰键
    public void 解析失败按未绑定处理(string raw)
    {
        Assert.False(TransportKeys.TryResolve(raw, out _, out _));
        Assert.Equal(raw, TransportKeys.Canonical(raw));   // Canonical 不吞坏值
    }

    [Fact]
    public void Canonical把合法值换成规范写法()
    {
        Assert.Equal("Space", TransportKeys.Canonical("space"));
        Assert.Equal("Ctrl+Left", TransportKeys.Canonical("CONTROL + left"));
        Assert.Equal("A", TransportKeys.Canonical("a"));
    }

    [Theory]
    [InlineData(Key.Left, KeyModifiers.None, "←")]
    [InlineData(Key.Right, KeyModifiers.None, "→")]
    [InlineData(Key.Up, KeyModifiers.None, "↑")]
    [InlineData(Key.Down, KeyModifiers.None, "↓")]
    [InlineData(Key.A, KeyModifiers.None, "A")]
    [InlineData(Key.F11, KeyModifiers.None, "F11")]
    [InlineData(Key.Right, KeyModifiers.Control, "Ctrl+→")]
    [InlineData(Key.A, KeyModifiers.Alt | KeyModifiers.Shift, "Shift+Alt+A")]
    public void 展示可读(Key key, KeyModifiers mods, string expected)
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);   // 本用例不含"空格"，两种语言下同形
            Assert.Equal(expected, TransportKeys.Display(key, mods));
        }
        finally { LanguageManager.SetLanguage(saved); }
    }

    [Fact]
    public void 空格按语言展示且未绑定有独立文案()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);
            Assert.Equal("空格", TransportKeys.Display(Key.Space, KeyModifiers.None));
            Assert.Equal("未绑定", TransportKeys.Display(""));
            LanguageManager.SetLanguage(1);
            Assert.Equal("Space", TransportKeys.Display(Key.Space, KeyModifiers.None));
            Assert.Equal("Unbound", TransportKeys.Display(""));
            // 坏值原样露出，不静默当成"未绑定"
            Assert.Equal("Banana", TransportKeys.Display("Banana"));
        }
        finally { LanguageManager.SetLanguage(saved); }
    }

    [Theory]
    [InlineData(Key.Left, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    [InlineData(Key.D, KeyModifiers.None, false)]
    [InlineData(Key.Space, KeyModifiers.None, false)]
    [InlineData(Key.O, KeyModifiers.None, true)]
    [InlineData(Key.B, KeyModifiers.None, false)]   // 「A-B 滑块视图」撤后裸 B 不再被主窗硬编码吃掉
    [InlineData(Key.S, KeyModifiers.None, true)]
    [InlineData(Key.S, KeyModifiers.Control, true)]
    [InlineData(Key.H, KeyModifiers.Control, true)]
    [InlineData(Key.Up, KeyModifiers.None, true)]
    [InlineData(Key.Up, KeyModifiers.Shift, true)]      // 硬编码分支不带修饰键守卫 ⇒ 全组合被占
    [InlineData(Key.F6, KeyModifiers.Control, true)]
    [InlineData(Key.Delete, KeyModifiers.None, true)]
    [InlineData(Key.D1, KeyModifiers.None, true)]
    [InlineData(Key.F11, KeyModifiers.None, true)]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.PageUp, KeyModifiers.None, false)]
    [InlineData(Key.NumPad1, KeyModifiers.None, false)]
    public void 固定键位表与硬编码分支一致(Key key, KeyModifiers mods, bool reserved)
    {
        Assert.Equal(reserved, TransportKeys.IsReserved(key, mods));
    }

    [Fact]
    public void 默认绑定表有5个动作且停止不在表里()
    {
        var map = TransportKeys.BuildMap(new KeyBindingsSettings());
        Assert.Equal(5, map.Count);                                   // Stop 默认未绑定 ⇒ 不参与分派
        Assert.Equal(TransportKeys.Slot.StepSecondBackward, map[(Key.Left, KeyModifiers.None)]);
        Assert.Equal(TransportKeys.Slot.StepSecondForward, map[(Key.Right, KeyModifiers.None)]);
        Assert.Equal(TransportKeys.Slot.StepFrameBackward, map[(Key.A, KeyModifiers.None)]);
        Assert.Equal(TransportKeys.Slot.StepFrameForward, map[(Key.D, KeyModifiers.None)]);
        Assert.Equal(TransportKeys.Slot.PlayPause, map[(Key.Space, KeyModifiers.None)]);
    }

    [Fact]
    public void 未绑定与坏值与固定键都不进分派表()
    {
        var b = new KeyBindingsSettings
        {
            StepSecondBackward = "",                 // 用户清了绑定
            StepSecondForward = "Banana",            // 坏值
            StepFrameBackward = "O",                 // 固定键：硬编码优先 ⇒ 丢弃
            StepFrameForward = "a",                  // 大小写奇怪的合法值
            PlayPause = "Shift+Up",                  // Up 的任意修饰组合都被硬编码占 ⇒ 丢弃
            Stop = "Ctrl+End",
        };
        var map = TransportKeys.BuildMap(b);
        Assert.Equal(2, map.Count);
        Assert.Equal(TransportKeys.Slot.StepFrameForward, map[(Key.A, KeyModifiers.None)]);
        Assert.Equal(TransportKeys.Slot.Stop, map[(Key.End, KeyModifiers.Control)]);
    }

    [Fact]
    public void 附加修饰位不参与匹配()
    {
        // 平台可能塞进来的非相关位必须被掩掉，否则"存 Ctrl+Left、按下算出 Ctrl+Left+附加位 ⇒ 匹配不上"
        var b = new KeyBindingsSettings { StepFrameBackward = "Ctrl+A", StepFrameForward = "A" };
        var map = TransportKeys.BuildMap(b);
        Assert.Equal(TransportKeys.Slot.StepFrameBackward, map[(Key.A, KeyModifiers.Control)]);
        Assert.Equal(TransportKeys.Slot.StepFrameForward, map[(Key.A, KeyModifiers.None)]);
        // 用一个未定义的位当"平台附加位"（比 Enum 里某个具体成员更稳，且不依赖具体平台枚举值）
        var noisy = KeyModifiers.Control | (KeyModifiers)0x4000;
        Assert.Equal(KeyModifiers.Control, TransportKeys.Normalize(noisy));
        Assert.True(map.ContainsKey((Key.A, TransportKeys.Normalize(noisy))));
    }

    [Fact]
    public void CopyInto与Same走同一份槽序()
    {
        var a = new KeyBindingsSettings { Stop = "S", PlayPause = "" };
        var b = new KeyBindingsSettings();
        Assert.False(TransportKeys.Same(a, b));
        TransportKeys.CopyInto(a, b);
        Assert.True(TransportKeys.Same(a, b));
        Assert.Equal("S", b.Stop);
        Assert.Equal("", b.PlayPause);
        Assert.Equal("Left", b.StepSecondBackward);
    }

    [Fact]
    public void 每个槽都有两个语言的标签且格式串占位符齐()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            foreach (var lang in new[] { 0, 1 })
            {
                LanguageManager.SetLanguage(lang);
                foreach (var slot in TransportKeys.Slots)
                {
                    var label = LanguageManager.T(TransportKeys.LabelKey(slot));
                    Assert.NotEqual(TransportKeys.LabelKey(slot), label);   // 缺键会原样返回键名
                    Assert.DoesNotContain("Keys_", label);
                }
                var reserved = LanguageManager.Tf("Keys_ErrReserved", "←");
                Assert.Contains("←", reserved);
                Assert.DoesNotContain("{0}", reserved);
                var dup = LanguageManager.Tf("Keys_ErrDuplicate", "A", "按帧前进");
                Assert.Contains("A", dup);
                Assert.DoesNotContain("{1}", dup);
                Assert.DoesNotContain("{0}", LanguageManager.Tf("Keys_BoundFmt", "空格"));
                Assert.DoesNotContain("{0}", LanguageManager.Tf("Keys_ClearedFmt", "停止"));
                Assert.DoesNotContain("{", LanguageManager.T("Keys_Capturing"));
            }
        }
        finally { LanguageManager.SetLanguage(saved); }
    }
}
