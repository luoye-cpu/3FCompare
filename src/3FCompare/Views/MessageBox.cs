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
            Width = 460, Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
        };

        var result = false;
        var primary = new Button { Content = primaryText, Width = 110, Height = 30 };
        primary.Click += (_, _) => { result = true; dlg.Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
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
            Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            Margin = new global::Avalonia.Thickness(16),
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(messageBlock);
        dlg.Content = root;

        var tcs = new TaskCompletionSource<bool>();
        dlg.Closed += (_, _) => tcs.TrySetResult(result);
        dlg.ShowDialog(owner);
        return tcs.Task;
    }
}
