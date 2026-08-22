using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Core.Backend;

namespace _3FCompare.Panels;

/// <summary>像素探针面板（WinForms ProbePanel 对应）：读取选中表面悬停像素的
/// 颜色管理前码值（float RGBA + 8bit），支持复制 JSON。</summary>
public sealed class ProbePanel : StackPanel
{
    private readonly TextBlock _coord, _value;
    private IPlayerSession? _session;
    private int _lastX, _lastY;
    private PixelSample _last;
    private bool _hasSample;

    public ProbePanel()
    {
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        _coord = Mk(12);
        _value = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Brush("#FFFFC840"), TextWrapping = TextWrapping.Wrap,
        };
        var hint = Mk(11);
        hint.Foreground = Brush("#8C8C96");
        hint.Text = LanguageManager.T("Probe_Hint");

        var copy = new Button { Content = "⧉ JSON", Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => CopyToClipboard();

        var header = new TextBlock
        {
            Text = LanguageManager.T("Probe_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = Brush("#FFFFFFFF"),
        };
        Children.Add(header);
        Children.Add(_coord);
        Children.Add(_value);
        Children.Add(hint);
        Children.Add(copy);

        LanguageManager.LanguageChanged += (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(RefreshCoord);
        RefreshCoord();
    }

    private void RefreshCoord() =>
        _coord.Text = _hasSample ? $"X:{_lastX} Y:{_lastY} {_last.BitDepth}bit" : LanguageManager.T("Probe_Coord");

    private static TextBlock Mk(double size) => new()
    {
        FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#FFC8C8D2"),
    };

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        _hasSample = false;
        RefreshCoord();
        _value.Text = string.Empty;
    }

    /// <summary>读取 (x,y) 像素（颜色管理前码值）。失败显示读取失败。</summary>
    public void UpdatePoint(int x, int y)
    {
        if (_session is null) return;
        try
        {
            if (_session.TryReadPixel(x, y, out var s))
            {
                _last = s;
                _lastX = x; _lastY = y;
                _hasSample = true;
                RefreshCoord();
                _value.Text =
                    $"R {s.R:0.000000}  G {s.G:0.000000}\nB {s.B:0.000000}  A {s.A:0.000000}\n" +
                    $"{LanguageManager.T("Probe_Bits")}: {To8Bit(s.R)},{To8Bit(s.G)},{To8Bit(s.B)}";
                return;
            }
        }
        catch { /* 会话未就绪 */ }
        _value.Text = LanguageManager.T("Probe_ReadFail");
    }

    private static int To8Bit(float v) => Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    public void CopyToClipboard()
    {
        if (!_hasSample) return;
        // 手工拼接（NativeAOT：匿名类型反射序列化不可用）
        var json = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\n  \"x\": {_lastX},\n  \"y\": {_lastY},\n  \"r\": {_last.R:0.######},\n  \"g\": {_last.G:0.######}," +
            $"\n  \"b\": {_last.B:0.######},\n  \"a\": {_last.A:0.######},\n  \"bitDepth\": {_last.BitDepth}\n}}");
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(json);
    }
}
