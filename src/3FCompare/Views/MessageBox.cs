using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace _3FCompare.Views;

/// <summary>轻量消息框 / 提示对话框（WinForms MessageBox + PromptDialog 对应）。
/// 主按钮返回 true；次按钮（可选自定义文本）返回 false。</summary>
public static class MessageBox
{
    public static Task<bool> Show(Window owner, string title, string message,
        string primaryText = "确定 / OK", string? secondaryText = null)
    {
        var dlg = new Window
        {
            Title = title,
            // 允许调整大小 + 正文可滚动：长文本（内核错误 JSON、异常堆栈）在固定 460×220、
            // 不可缩放的窗口里会被直接裁掉，用户看不到后半段，只能靠日志排查。
            Width = 460, Height = 260,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ThemePalette.SetBrush(dlg, Window.BackgroundProperty, "BgBrush");

        var result = false;
        var primary = new Button { Content = primaryText, Width = 110, Height = 30 };
        primary.Click += (_, _) => { result = true; dlg.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new global::Avalonia.Thickness(16),
        };
        if (secondaryText is not null)
        {
            var secondary = new Button { Content = secondaryText, Width = 110, Height = 30 };
            secondary.Click += (_, _) => dlg.Close();
            buttons.Children.Add(secondary);
        }
        buttons.Children.Add(primary);

        var messageBlock = new TextBlock
        {
            Text = $"⚠ {message}", TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Margin = new global::Avalonia.Thickness(16),
        };

        // 正文套一层 ScrollViewer：文本超出可视高度时可滚动查看，而不是被裁掉
        var bodyScroll = new ScrollViewer { Content = messageBlock };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(bodyScroll);
        dlg.Content = root;

        var tcs = new TaskCompletionSource<bool>();
        dlg.Closed += (_, _) => tcs.TrySetResult(result);
        dlg.ShowDialog(owner);
        return tcs.Task;
    }
}
