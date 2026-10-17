using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.Core.Backend;
using _3FCompare.Localization;
using _3FCompare.Themes;

namespace _3FCompare.Views;

/// <summary>
/// 解锁码输入对话框：**只有盘上真的有那枚色彩变换模块、且它还没解锁**时才会出现。
///
/// <para><b>为什么单独一个框、而不是复用 <see cref="MessageBox"/></b>：那个只有"是/否"两个按钮，
/// 没有输入位；而"输码 → 校验 → 错了再输"是一段闭环，得放在有 UI 的这一侧——
/// Core 侧只关心"最后成没成"（见 <c>ColorTransformUnlock</c> 的注释）。</para>
///
/// <para><b>三条纪律</b>：
/// <b>① 码不进日志</b>——这里不记、Core 侧也不记，只记"接受/拒绝"；
/// <b>② 文案中性</b>——不出现任何厂家商标字样（与全仓 HDRdp 中性化同一条线），
/// 也不引用模块自带的说明文本（<c>StatusText</c>），那文本的措辞不由本仓控制；
/// <b>③ 错了可以说，但不能无限弹</b>——连续错 <see cref="MaxAttempts"/> 次即放弃，
/// 放弃与取消都回到"本次按基础层出画"，不再打扰。</para>
/// </summary>
public static class UnlockCodeDialog
{
    /// <summary>连续输错的上限。超过即放弃（回落到基础层出画，本次进程不再提示）。</summary>
    public const int MaxAttempts = 5;

    /// <summary>显示并跑完整套闭环，返回"最终是否解锁成功"。</summary>
    public static async Task<bool> ShowAsync(Window owner)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var wrong = attempt > 1;
            var code = await PromptOnceAsync(owner, wrong, attempt).ConfigureAwait(true);
            if (code is null) return false;              // 取消 / 关闭窗口

            var outcome = ColorTransformModule.TryAuthenticate(code, out var detail);
            switch (outcome)
            {
                case ColorTransformModule.UnlockOutcome.Accepted:
                    return true;

                case ColorTransformModule.UnlockOutcome.AcceptedNotPersisted:
                    // 码是对的，但票据写不进目标目录（常见于装在不可写目录里）：
                    // 本次进程能用，重启后要重输。这句必须说清，否则用户以为"没成功"。
                    await MessageBox.Show(owner,
                        LanguageManager.T("Unlock_Title"),
                        LanguageManager.T("Unlock_NotPersisted"),
                        LanguageManager.T("Settings_Ok")).ConfigureAwait(true);
                    return true;

                case ColorTransformModule.UnlockOutcome.Refused:
                    // 码不对（或模块返回了看不懂的值）：换文案再给一次机会。
                    if (!string.IsNullOrEmpty(detail))
                        System.Console.Error.WriteLine($"[Unlock] 拒绝原因：{detail}");
                    continue;

                default:
                    // 够不到模块 / 不含解锁入口：这不是"码对不对"的问题，
                    // 再弹下去只是让用户白试，直接放弃并回到静默。
                    if (!string.IsNullOrEmpty(detail))
                        System.Console.Error.WriteLine($"[Unlock] 无法提交：{detail}");
                    return false;
            }
        }
        return false;
    }

    /// <summary>弹一次窗口，返回用户输入的码；取消/关闭返回 null。</summary>
    private static Task<string?> PromptOnceAsync(Window owner, bool showWrongHint, int attempt)
    {
        var dlg = new Window
        {
            Title = LanguageManager.T("Unlock_Title"),
            Width = 480, Height = 250,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ThemePalette.SetBrush(dlg, Window.BackgroundProperty, "BgBrush");

        var body = new TextBlock
        {
            Text = showWrongHint
                ? LanguageManager.Tf("Unlock_WrongCode", attempt, MaxAttempts)
                : LanguageManager.T("Unlock_Body"),
            TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Margin = new Avalonia.Thickness(16, 16, 16, 8),
        };

        var label = new TextBlock
        {
            Text = LanguageManager.T("Unlock_CodeLabel"), FontSize = 12,
            Margin = new Avalonia.Thickness(16, 0, 16, 4),
        };
        var input = new TextBox
        {
            FontSize = 14,
            Margin = new Avalonia.Thickness(16, 0, 16, 12),
            // 长度只是输入框的护栏，不代表校验规则：怎么算一枚有效的码由模块自己定。
            MaxLength = 32,
            PlaceholderText = "00000000",
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(16),
        };
        var cancel = new Button { Content = LanguageManager.T("Unlock_Cancel"), Width = 110, Height = 30 };
        var ok = new Button { Content = LanguageManager.T("Unlock_Confirm"), Width = 110, Height = 30 };

        string? result = null;
        void Submit()
        {
            var text = input.Text?.Trim() ?? string.Empty;
            if (text.Length == 0) { dlg.Close(); return; }   // 空输入＝取消
            result = text;
            dlg.Close();
        }
        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => dlg.Close();
        // Enter 提交：码是一串数字，输完直接回车是最自然的动作。
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) Submit();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var form = new StackPanel { Orientation = Orientation.Vertical };
        form.Children.Add(label);
        form.Children.Add(input);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var scroll = new ScrollViewer { Content = body };
        DockPanel.SetDock(scroll, Dock.Top);
        root.Children.Add(scroll);
        root.Children.Add(form);
        dlg.Content = root;

        var tcs = new TaskCompletionSource<string?>();
        dlg.Closed += (_, _) => tcs.TrySetResult(result);
        dlg.ShowDialog(owner);
        return tcs.Task;
    }
}
