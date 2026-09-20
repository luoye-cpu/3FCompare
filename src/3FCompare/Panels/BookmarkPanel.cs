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
using _3FCompare.Controls;

namespace _3FCompare.Panels;

/// <summary>书签条目（时间/帧号/备注）。</summary>
public sealed record BookmarkItem(long Position100ns, long Frame, string Note = "");

/// <summary>书签在列表里的显示行：把 <see cref="BookmarkItem"/> 包一层，
/// 让 <c>ListBox.SelectedItem</c> 能直接拿回数据对象。
/// <para>为什么包一层而不是给 <see cref="BookmarkItem"/> 加 ToString：
/// BookmarkItem 是 JSON 导出的序列化模型，给它加显示用 ToString 会让
/// "数据模型"和"列表呈现"耦合；且 record 自动生成的 ToString 若被覆盖，
/// 调试日志里就再也看不到字段值。包装类只在面板内部使用，影响面为零。</para>
/// <para>ListBox 无 ItemTemplate 时对非控件项走 <c>FuncDataTemplate.Default</c>，
/// 即显示 <c>ToString()</c>，因此文本格式与改动前完全一致。</para></summary>
internal sealed class BookmarkRow
{
    public BookmarkRow(BookmarkItem item) => Item = item;

    /// <summary>行对应的书签数据。</summary>
    public BookmarkItem Item { get; }

    /// <summary>行文本：<c>hh:mm:ss.fff␠␠F######␠␠备注</c>（与改动前的字符串行逐字符一致）。</summary>
    public override string ToString() =>
        $"{TimeSpan.FromTicks(Item.Position100ns):hh\\:mm\\:ss\\.fff}  F{Item.Frame:D6}  {Item.Note}";
}

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
        // 显式给 ItemTemplate：不依赖"无模板时回退到 ToString()"这一默认行为，
        // 行文本格式由我们自己控制（且换 Avalonia 版本也不会悄悄变样）。
        _list.ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<BookmarkRow>(
            (row, _) => new TextBlock { Text = row.ToString() }, false);
        _list.DoubleTapped += (_, _) =>
        {
            // 列表里装的是 **BookmarkRow**（书签数据的显示包装），不是字符串：
            // 过去装的是格式化字符串，而这里判的是 "SelectedItem is BookmarkItem"，
            // 模式匹配恒不成立 ⇒ JumpRequested 永不触发、双击跳转完全失效且不报错
            // —— 用户只会以为"这个功能没做"。用显示行对象后 SelectedItem 与数据一一对应。
            if (_list.SelectedItem is BookmarkRow row)
                JumpRequested?.Invoke(row.Item.Position100ns);
        };

        // 图标 + 文本（docs/31 阶段 4 第二批）：文案里的 ＋ / ⇩ 字形在缺字字体下渲染成豆腐块，
        // 改为 AppIcons 的矢量几何。文案保持可见 ⇒ 图标不是唯一语义来源（两处按钮都不需要额外 ToolTip）。
        var add = new Button
        {
            Content = IconText("Bookmark_Add", "Add"),
            Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        add.Click += OnAddCurrent;
        var export = new Button { Content = IconText("Bookmark_Export", "Export"), Height = 26 };
        export.Click += async (_, _) => await ExportAsync();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(add);
        row.Children.Add(export);

        Children.Add(new TextBlock
        {
            Text = LanguageManager.T("Bookmark_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
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
        // Items.Clear() 会把 SelectedIndex 重置为 -1 ⇒ 每次添加/删除后选中态丢失
        // （连续删书签时删完一条就得重新点一次）。先记下旧索引，重建后按新 Count 钳制恢复：
        // 删掉的是最后一条时索引会落到新的末行，而不是跳回 -1。
        var prev = _list.SelectedIndex;

        _list.Items.Clear();
        foreach (var b in Items)
            _list.Items.Add(new BookmarkRow(b));
        // 备注：以字符串行显示（等价 WinForms 三列 ListView 的信息量）——
        // 行文本由 BookmarkRow.ToString() 提供，格式与改动前逐字符一致。

        _list.SelectedIndex = prev < 0 || Items.Count == 0 ? -1 : Math.Clamp(prev, 0, Items.Count - 1);
    }

    /// <summary>删除当前选中行（Delete 键）。返回是否有删除发生。</summary>
    public bool RemoveSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= Items.Count) return false;
        Items.RemoveAt(_list.SelectedIndex);
        RefreshList();
        return true;
    }

    /// <summary>按钮内容 = 矢量图标 + 本地化文案（docs/31 阶段 4 第二批）。
    /// <para>图标取 13px（与 ProbePanel 的复制按钮同级，26px 高的按钮里不撑边）；
    /// 文案前景用 <c>TextPrimaryBrush</c>，与按钮默认前景一致、深浅主题下都可读。</para></summary>
    private static StackPanel IconText(string textKey, string iconKey) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            AppIcons.Create(iconKey, 13),
            new TextBlock { Text = LanguageManager.T(textKey), VerticalAlignment = VerticalAlignment.Center }
                .Themed("TextPrimaryBrush"),
        },
    };

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
