using System.Text.Json;
using System.Text.Json.Serialization;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>书签条目（F22）。</summary>
public sealed record BookmarkItem
{
    public long Position100ns { get; init; }
    public long FrameIndex { get; init; }
    public required string Note { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;

    public string PositionText => TimeSpan.FromTicks(Position100ns).ToString(@"hh\:mm\:ss\.fff");
}

/// <summary>App 层 JSON 源生成上下文（AOT 兼容，顶层声明）。</summary>
[JsonSerializable(typeof(BookmarkItem[]))]
internal sealed partial class BookmarkJsonContext : JsonSerializerContext;

/// <summary>书签面板（F22）：记录当前帧号/时间/备注，列表展示，可导出 JSON/CSV。</summary>
public sealed class BookmarkPanel : Panel
{
    private readonly ListView _list;
    private readonly TextBox _noteBox;
    private readonly Button _btnAdd;
    private readonly Button _btnExport;
    private readonly Label _title;
    private readonly Func<(long position, long frame)>? _currentPos;
    private readonly List<BookmarkItem> _items = new();

    public IReadOnlyList<BookmarkItem> GetAllItems() => _items.ToArray();

    public event EventHandler? ItemsChanged;

    /// <summary>书签跳转请求（参数 = 目标位置 100ns）。</summary>
    public event EventHandler<long>? JumpRequested;

    public BookmarkPanel(Func<(long position, long frame)>? currentPosGetter)
    {
        _currentPos = currentPosGetter;
        Dock = DockStyle.Right;
        Width = 240;
        BackColor = AppTheme.Colors.PanelBackground;

        _title = new Label
        {
            Text = LanguageManager.T("Bookmark_Title"),
            Dock = DockStyle.Top,
            Height = 28,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _noteBox = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = AppTheme.Fonts.BodyFont,
            PlaceholderText = LanguageManager.T("Bookmark_NotePlaceholder"),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _btnAdd = new Button
        {
            Dock = DockStyle.Fill,
            Height = 28,
            Text = LanguageManager.T("Bookmark_Add"),
            FlatStyle = FlatStyle.Flat,
            BackColor = AppTheme.Colors.ButtonActive,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        _btnAdd.Click += (_, _) => AddCurrent();

        _btnExport = new Button
        {
            Dock = DockStyle.Fill,
            Height = 28,
            Text = LanguageManager.T("Bookmark_Export"),
            FlatStyle = FlatStyle.Flat,
            BackColor = AppTheme.Colors.ButtonSecondary,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        _btnExport.Click += (_, _) => Export();

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = AppTheme.Colors.ControlBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            BorderStyle = BorderStyle.None,
        };
        _list.Columns.Add(LanguageManager.T("Bookmark_Col_Time"), 110);
        _list.Columns.Add(LanguageManager.T("Bookmark_Col_Frame"), 70);
        _list.Columns.Add(LanguageManager.T("Bookmark_Col_Note"), 80);

        // 双击书签跳转
        _list.MouseDoubleClick += (_, e) =>
        {
            var item = _list.GetItemAt(e.X, e.Y);
            if (item is null || item.Index < 0 || item.Index >= _items.Count) return;
            JumpRequested?.Invoke(this, _items[item.Index].Position100ns);
        };

        // 添加/导出按钮并排：TableLayoutPanel 两列等分，高 DPI 下自动缩放
        var rightBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppTheme.Colors.PanelBackground,
        };
        rightBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        rightBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        _btnExport.Dock = DockStyle.Fill;
        _btnExport.Margin = new Padding(0, 1, 2, 1);
        _btnAdd.Dock = DockStyle.Fill;
        _btnAdd.Margin = new Padding(2, 1, 0, 1);
        rightBar.Controls.Add(_btnExport, 0, 0);
        rightBar.Controls.Add(_btnAdd, 1, 0);

        Controls.AddRange(new Control[] { _list, rightBar, _noteBox, _title });
    }

    /// <summary>语言切换后刷新静态文本。</summary>
    public void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Bookmark_Title");
        _noteBox.PlaceholderText = LanguageManager.T("Bookmark_NotePlaceholder");
        _btnAdd.Text = LanguageManager.T("Bookmark_Add");
        _btnExport.Text = LanguageManager.T("Bookmark_Export");
        _list.Columns[0].Text = LanguageManager.T("Bookmark_Col_Time");
        _list.Columns[1].Text = LanguageManager.T("Bookmark_Col_Frame");
        _list.Columns[2].Text = LanguageManager.T("Bookmark_Col_Note");
    }

    private void AddCurrent()
    {
        if (_currentPos is null) return;
        var (pos, frame) = _currentPos();
        var item = new BookmarkItem { Position100ns = pos, FrameIndex = frame, Note = _noteBox.Text.Trim() };
        _items.Add(item);
        _list.Items.Add(new ListViewItem(new[] { item.PositionText, frame.ToString(), item.Note }));
        _noteBox.Clear();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSelected()
    {
        if (_list.SelectedIndices.Count == 0) return;
        var idx = _list.SelectedIndices[0];
        _items.RemoveAt(idx);
        _list.Items.RemoveAt(idx);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = LanguageManager.T("Filter_Bookmark"),
            FileName = "bookmarks",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        if (dlg.FilterIndex == 1)
        {
            var json = JsonSerializer.Serialize(_items, BookmarkJsonContext.Default.BookmarkItemArray);
            File.WriteAllText(dlg.FileName, json);
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("time,frame,note");
            foreach (var it in _items)
            {
                var note = it.Note.Replace("\"", "'");
                sb.AppendLine($"\"{it.PositionText}\",{it.FrameIndex},\"{note}\"");
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
        }
    }
}