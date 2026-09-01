using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using global::Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;

namespace _3FCompare.Panels;

/// <summary>书签条目（时间/帧号/备注）。</summary>
public sealed record BookmarkItem(long Position100ns, long Frame, string Note = "");

[JsonSerializable(typeof(List<BookmarkItem>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class BookmarkJsonContext : JsonSerializerContext;

/// <summary>书签面板（WinForms BookmarkPanel 对应）：添加当前帧/列表双击跳转/删除/JSON|CSV 导出。</summary>
public sealed class BookmarkPanel : StackPanel
{
    private readonly ListBox _list = new();
    private readonly TextBox _note = new();
    private readonly Func<(long Position, long Frame)> _currentGetter;

    /// <summary>双击跳转请求。</summary>
    public event Action<long>? JumpRequested;

    public BookmarkPanel(Func<(long Position, long Frame)> currentGetter)
    {
        _currentGetter = currentGetter;
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        _note.PlaceholderText = LanguageManager.T("Bookmark_NotePlaceholder");
        _note.Height = 26;

        _list.MinHeight = 200;
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is BookmarkItem b)
                JumpRequested?.Invoke(b.Position100ns);
        };

        var add = new Button { Content = LanguageManager.T("Bookmark_Add"), Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += OnAddCurrent;
        var export = new Button { Content = LanguageManager.T("Bookmark_Export"), Height = 26 };
        export.Click += async (_, _) => await ExportAsync();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(add);
        row.Children.Add(export);

        Children.Add(new TextBlock
        {
            Text = LanguageManager.T("Bookmark_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
        });
        Children.Add(_note);
        Children.Add(row);
        Children.Add(_list);
    }

    private void OnAddCurrent(object? sender, RoutedEventArgs e)
    {
        var (pos, frame) = _currentGetter();
        Items.Add(new BookmarkItem(pos, frame, _note.Text ?? string.Empty));
        _note.Text = string.Empty;
        RefreshList();
    }

    public List<BookmarkItem> Items { get; } = new();

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var b in Items)
            _list.Items.Add($"{TimeSpan.FromTicks(b.Position100ns):hh\\:mm\\:ss\\.fff}  F{b.Frame:D6}  {b.Note}");
        // 备注：以字符串行显示（等价 WinForms 三列 ListView 的信息量）
    }

    /// <summary>删除当前选中行（Delete 键）。返回是否有删除发生。</summary>
    public bool RemoveSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= Items.Count) return false;
        Items.RemoveAt(_list.SelectedIndex);
        RefreshList();
        return true;
    }

    private async System.Threading.Tasks.Task ExportAsync()
    {
        if (Items.Count == 0) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = LanguageManager.T("Bookmark_Export"),
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new global::Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                new global::Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
            },
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        await System.Threading.Tasks.Task.Run(() =>
        {
            if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                File.WriteAllLines(path, Items.Select(b =>
                    $"\"{TimeSpan.FromTicks(b.Position100ns):hh\\:mm\\:ss\\.fff}\",{b.Frame},\"{(b.Note ?? "").Replace("\"", "\"\"")}\""));
            else
                File.WriteAllText(path, JsonSerializer.Serialize(Items, BookmarkJsonContext.Default.ListBookmarkItem));
        });
    }
}
