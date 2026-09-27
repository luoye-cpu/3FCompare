using System;
using System.IO;
using _3FCompare.App;                 // LanguageManager 在这个命名空间下
using _3FCompare.Core.Settings;
using _3FCompare.Services;
using Xunit;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 出货 <c>使用说明.txt</c> 里那段"快捷键"清单的防漂移闸门。
///
/// <para><b>为什么要有它</b>：那段文字是 <c>pack.ps1</c> 里的硬编码字符串，随包发出去、还进 SHA256SUMS，
/// 却和代码里的键位默认值各写一份。2026-09-27 实测它已经在说反话：写 <c>←→ 帧步进</c> /
/// <c>Shift+←→ 秒步进</c>，而代码默认是 <c>←/→</c>=按秒、<c>A/D</c>=按帧；还把需求文档里的
/// **功能编号 F25** 当成按键印进了包。手抄的东西没有闸门就一定会漂 —— 这里把"清单"与
/// <see cref="TransportKeys.DefaultOf"/> 钉在一起，改默认值时这条会红着提醒去改文案。</para>
///
/// <para><b>fail-closed</b>：找不到仓库根或找不到那段文字 ⇒ 判红并打印找过哪里，
/// 绝不"取不到就算过"。</para>
/// </summary>
[Collection(LanguageStateCollection.Name)]   // 会切语言 ⇒ 与其余语言用例串行
public class ShippedShortcutsNoteTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pack.ps1")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src", "3FCompare")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上 10 层都没找到含 pack.ps1 + src/3FCompare 的仓库根");
    }

    /// <summary>切出 pack.ps1 里"⌨️ 快捷键"到"❓ 常见问题"之间的那段。</summary>
    private static string ReadShortcutBlock()
    {
        var path = Path.Combine(FindRepoRoot(), "pack.ps1");
        var raw = File.ReadAllText(path);
        var start = raw.IndexOf("⌨️ 快捷键", StringComparison.Ordinal);
        var end = raw.IndexOf("❓ 常见问题", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{path} 里找不到「⌨️ 快捷键」段");
        if (end <= start) throw new InvalidOperationException($"{path} 里快捷键段没有结尾锚点");
        return raw.Substring(start, end - start);
    }

    /// <summary>逐行核对：每条动作那一行的**键位列**必须等于代码默认值拼出来的串。
    ///
    /// <para><b>为什么不是"整段 Contains(键名)"</b>：第一版就是那么写的，被变异证伪 ——
    /// 默认键是 "A"、"D"、"←" 这类单字符，整段文案里到处都有字母 A，把 "A / D" 改成 "Z / X"
    /// 断言照样绿。这是"恒真断言"的教科书形态：必须把比较收窄到**那一行的键位列**。</para></summary>
    [Fact]
    public void 出货说明的传输键位与代码默认值一致()
    {
        var saved = LanguageManager.CurrentLanguage;
        LanguageManager.SetLanguage(0);   // 中文表：Display() 的箭头符号取自 Keys_* 串
        try
        {
            var block = ReadShortcutBlock();
            var lines = block.Split('\n');
            var b = new AppSettings();
            string Def(TransportKeys.Slot s) =>
                TransportKeys.Display(TransportKeys.Get(s, b.KeyBindings));

            // 标签必须取"只在表格行里出现"的完整串：第一版用「播放/暂停」结果撞上了
            // 上面那行说明文字，键位列被读成了「※ 前四行动作（」。
            var checks = new (string Label, string Expect)[]
            {
                ("逐帧后退/前进", Def(TransportKeys.Slot.StepFrameBackward) + " / " +
                             Def(TransportKeys.Slot.StepFrameForward)),
                ("按秒快退/快进", Def(TransportKeys.Slot.StepSecondBackward) + " / " +
                             Def(TransportKeys.Slot.StepSecondForward)),
                ("播放/暂停 Play/Pause", Def(TransportKeys.Slot.PlayPause)),
            };
            foreach (var (label, expect) in checks)
            {
                var line = System.Array.Find(lines,
                    l => l.Contains(label, System.StringComparison.Ordinal));
                Assert.True(line is not null, $"快捷键段里找不到「{label}」这一行");
                // 键位列 = 行首到动作标签之前；标签之后的括号说明不参与比较
                var keyCol = line!.Substring(0, line.IndexOf(label, System.StringComparison.Ordinal));
                Assert.True(keyCol.Contains(expect, System.StringComparison.Ordinal),
                    $"「{label}」行的键位列是「{keyCol.Trim()}」，与代码默认「{expect}」不符 —— " +
                    "要么默认键改了没同步出货文案，要么文案被改错了");
            }
        }
        finally { LanguageManager.SetLanguage(saved); }
    }

    [Fact]
    public void 出货说明不得残留说反话的旧键位与把功能编号当按键()
    {
        var block = ReadShortcutBlock();
        // 这三条正是 2026-09-27 那次实测到的假话：←→ 被写成帧步进、Shift+← 被写成秒步进、
        // F25（docs/01 的功能编号）被印成按键。
        Assert.DoesNotContain("←→               帧步进", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Shift+←→", block, StringComparison.Ordinal);
        Assert.DoesNotContain("F25", block, StringComparison.Ordinal);
        // 三种视图模式的键是这一轮加的，缺了就是文案又漂回旧版本
        Assert.Contains("G / V / S", block, StringComparison.Ordinal);
    }
}
