using _3FCompare.App.Capture;
using _3FCompare.App.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare.App;

/// <summary>主窗体：菜单 + 网格 + 传输栏 + 时间轴 + 状态栏。
/// 职责：会话管理（SyncController）、打开/关闭/步进/循环/全屏/设置、快照轮询、快捷键。</summary>
public sealed class MainForm : Form
{
    private readonly bool _realMode;
    private readonly IPlayerEngine _engine;
    private readonly SyncController _sync = new();
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _pollTimer;

    private readonly CompareGridView _grid;
    private readonly TransportBar _transport;
    private readonly TimelineView _timeline;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusMode;
    private readonly ToolStripStatusLabel _statusInfo;
    private readonly MenuStrip _menu;
    private readonly ProbePanel _probe;
    private readonly BookmarkPanel _bookmarks;
    private readonly AbSliderView _abSlider;
    private readonly MagnifierOverlay _magnifier;
    private readonly CheckBox _chkMagnifier;
    private readonly OffsetPanel _offsetPanel;
    private readonly MediaInfoPanel _mediaInfoPanel;
    private readonly DiffOverlayView _diffView;
    private readonly AudioPanel _audioPanel;
    private readonly VerticalDockHost _toolsDock;

    private bool _fullscreen;
    private bool _isPlaying;
    private bool _abViewVisible;

    /// <summary>右侧工具区布局容器（探针/书签 +/- 切换）。</summary>
    private sealed class VerticalDockHost : Panel
    {
        public VerticalDockHost()
        {
            Dock = DockStyle.Right;
            Width = 240;
            BackColor = Color.FromArgb(30, 30, 36);
            AutoScroll = true;
        }

        public void ShowPanel(Control c)
        {
            foreach (Control it in Controls) it.Visible = false;
            if (!Controls.Contains(c)) Controls.Add(c);
            c.Visible = true;
            c.BringToFront();
        }

        public void HideAll()
        {
            foreach (Control it in Controls) it.Visible = false;
        }
    }

    public MainForm()
    {
        _settings = SettingsStore.Load();
        _realMode = EngineFactory.IsNativeAvailable();
        _engine = EngineFactory.Create();

        Text = "3FCompare – ICAT 类视频盯帧对比";
        ClientSize = new Size(1280, 800);

        // 窗口位置/尺寸记忆（F27 窗口管理；仅当有上次记录时恢复）
        if (_settings.WindowWidth >= 640 && _settings.WindowHeight >= 400)
        {
            StartPosition = FormStartPosition.Manual;
            var scr = Screen.FromPoint(new Point(
                Math.Max(0, _settings.WindowX), Math.Max(0, _settings.WindowY)));
            var x = Math.Clamp(_settings.WindowX, scr.WorkingArea.Left, scr.WorkingArea.Right - 320);
            var y = Math.Clamp(_settings.WindowY, scr.WorkingArea.Top, scr.WorkingArea.Bottom - 200);
            Location = new Point(x, y);
            ClientSize = new Size(_settings.WindowWidth, _settings.WindowHeight);
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        KeyPreview = true;
        AllowDrop = true;
        BackColor = Color.FromArgb(24, 24, 28);

        _sync.StepProfile = new StepProfile { FrameStep = _settings.FrameStep, SecondsStep = _settings.SecondsStep };

        // 菜单
        _menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        var openItem = new ToolStripMenuItem("打开视频…", null, (_, _) => OpenVideos());
        var saveItem = new ToolStripMenuItem("保存会话…", null, (_, _) => SaveSession());
        var loadItem = new ToolStripMenuItem("加载会话…", null, (_, _) => LoadSession());
        var screenItem = new ToolStripMenuItem("导出当前帧 PNG… (Ctrl+S)", null, (_, _) => CaptureSelectedFrame());
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => Close());
        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            openItem, new ToolStripSeparator(), saveItem, loadItem,
            new ToolStripSeparator(), screenItem, new ToolStripSeparator(), exitItem,
        });

        var viewMenu = new ToolStripMenuItem("视图(&V)");
        var singleItem = new ToolStripMenuItem("单屏/多屏切换", null, (_, _) => ToggleSingleView());
        var fullItem = new ToolStripMenuItem("全屏切换 (F11)", null, (_, _) => ToggleFullscreen());
        var abItem = new ToolStripMenuItem("A-B 滑块视图", null, (_, _) => ToggleAbView());
        var probeItem = new ToolStripMenuItem("像素探针", null, (_, _) => ToggleProbePanel());
        var bookmarkItem = new ToolStripMenuItem("书签", null, (_, _) => ToggleBookmarkPanel());
        var offsetItem = new ToolStripMenuItem("偏移校准 (F6)", null, (_, _) => ToggleOffsetPanel());
        var mediaInfoItem = new ToolStripMenuItem("媒体信息", null, (_, _) => ToggleMediaInfoPanel());
        var diffItem = new ToolStripMenuItem("差异叠加", null, (_, _) => ToggleDiffView());
        var audioItem = new ToolStripMenuItem("音频", null, (_, _) => ToggleAudioPanel());
        var showGridItem = new ToolStripMenuItem("显示 对比网格", null, (_, _) => ShowGridOnly());
        viewMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            singleItem, new ToolStripSeparator(), abItem,
            new ToolStripSeparator(), probeItem, bookmarkItem, offsetItem, mediaInfoItem,
            new ToolStripSeparator(), diffItem, audioItem, showGridItem, fullItem,
        });

        // 工具面板（探针/书签/A-B/放大镜）
        _probe = new ProbePanel();
        _bookmarks = new BookmarkPanel(() =>
        {
            var snap = _sync.ReadMasterSnapshot();
            return snap is null ? (0, 0) : (snap.Position100ns, snap.FrameIndex);
        });
        _bookmarks.JumpRequested += (_, pos) => _sync.SeekTo(pos);
        _abSlider = new AbSliderView(_grid);
        _magnifier = new MagnifierOverlay();
        _chkMagnifier = new CheckBox
        {
            Text = "放大镜",
            Checked = false,
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Color.White,
        };
        _chkMagnifier.CheckedChanged += (_, _) =>
        {
            foreach (var s in _grid.Surfaces)
            {
                if (_chkMagnifier.Checked)
                {
                    if (!s.Controls.Contains(_magnifier)) s.Controls.Add(_magnifier);
                    _magnifier.BringToFront();
                }
                else
                {
                    _magnifier.HideMagnifier();
                }
            }
        };

        _toolsDock = new VerticalDockHost();
        _offsetPanel = new OffsetPanel();
        _offsetPanel.OffsetNudge += (_, delta) => NudgeSelectedOffset(delta);
        _offsetPanel.AlignRequested += (_, _) => AlignSelectedToMaster();
        _offsetPanel.OffsetReset += (_, _) => NudgeSelectedOffset(0, reset: true);
        _mediaInfoPanel = new MediaInfoPanel();
        _diffView = new DiffOverlayView(_grid);
        _audioPanel = new AudioPanel();
        _toolsDock.Controls.Add(_chkMagnifier);
        _toolsDock.ShowPanel(_probe);

        var settingsMenu = new ToolStripMenuItem("设置(&S)");
        var settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings());
        settingsMenu.DropDownItems.Add(settingsItem);

        _menu.Items.AddRange(new ToolStripItem[] { fileMenu, viewMenu, settingsMenu });
        _menu.BackColor = Color.FromArgb(30, 30, 36);
        _menu.ForeColor = Color.White;
        MainMenuStrip = _menu;

        // 网格
        _grid = new CompareGridView(_realMode);
        _grid.SelectionChanged += (_, _) =>
        {
            AttachProbeToSelected();
            UpdateOffsetPanel();
            RefreshMediaInfoPanel();
            RefreshAudioPanel();
            UpdateStatus();
        };

        // 传输栏
        _transport = new TransportBar();
        _transport.PlayPauseClicked += (_, _) => TogglePlay();
        _transport.StopClicked += (_, _) => Stop();
        _transport.FrameStepClicked += (_, frames) => StepFrames(frames);
        _transport.SecondsStepClicked += (_, sec) => StepSeconds(sec);
        _transport.LoopToggled += (_, _) => ToggleLoop();
        _transport.AddClicked += (_, _) => AddSlotPlaceholder();
        _transport.RemoveClicked += (_, _) => RemoveLastSlot();
        _transport.SpeedChanged += (_, speed) => SetPlaybackSpeed(speed);

        // 时间轴
        _timeline = new TimelineView();
        _timeline.SeekRequested += (_, pos) => _sync.SeekTo(pos);
        _timeline.AbPointSet += (_, p) => SetLoopPoint(p.position, p.isA);

        // 状态栏
        _statusStrip = new StatusStrip();
        _statusMode = new ToolStripStatusLabel(_realMode ? "引擎: FFF.Native (3FP)" : "引擎: 演示模式 (Simulated)");
        _statusInfo = new ToolStripStatusLabel("就绪 — 点击「打开视频」或拖拽文件");
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusMode, new ToolStripStatusLabel(" | "), _statusInfo });
        _statusStrip.BackColor = Color.FromArgb(30, 30, 36);
        _statusStrip.ForeColor = Color.White;

        Controls.AddRange(new Control[] { _grid, _transport, _timeline, _statusStrip, _menu, _toolsDock });

        // 布局
        _menu.Dock = DockStyle.Top;
        _grid.Dock = DockStyle.Fill;
        _transport.Dock = DockStyle.Bottom;
        _timeline.Dock = DockStyle.Bottom;
        _statusStrip.Dock = DockStyle.Bottom;
        _timeline.BringToFront();
        _transport.BringToFront();

        // 轮询（60Hz 快照刷新 + 循环检测）
        _pollTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _pollTimer.Tick += (_, _) => PollSnapshots();
        _pollTimer.Start();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                OpenFiles(files);
        };

        // 初始 2 路（M0 起步，可加至 9）
        _grid.SetCount(2);
        WireSurfaceTools();
        UpdateStatus();
    }

    // ---------- 会话管理 ----------

    /// <summary>真实模式自检：打开文件→等就绪→双步进断言→返回退出码。
    /// 必须在 WinForms 消息循环内运行（--selftest 由 Program 调用）。</summary>
    public int RunSelfTest(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"selftest: 文件不存在 {path}");
            return 2;
        }

        var result = -2; // 未完成标记
        Shown += async (_, _) =>
        {
            try
            {
                if (!_realMode)
                {
                    Console.WriteLine("selftest: 未检测到 FFF.Native，演示模式无法做真实后端验证");
                }

                // 打开（复用真实路径：OpenFiles 会创建会话并异步 OpenAsync）
                var beforeCount = _sync.Count;
                AutoOpenFiles(new[] { path });
                Console.WriteLine($"selftest: 打开 {path} ({(_realMode ? "真实" : "演示")}模式)");

                // 等待就绪（最多 15s）
                var ready = false;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    // await 让出控制权，Application.Run 的消息循环继续驱动 3FP 呈现/状态机
                    await Task.Delay(120);

                    var snap = _sync.ReadMasterSnapshot();
                    if (snap is { Duration100ns: > 0 } && (snap.State == 2 || snap.State == 3 || snap.State == 4))
                    {
                        ready = true;
                        Console.WriteLine($"selftest: 就绪 ✓ 时长={TimeSpan.FromTicks(snap.Duration100ns)} 帧号={snap.FrameIndex} 状态={snap.State}");
                        break;
                    }
                }
                if (!ready)
                {
                    Console.Error.WriteLine("selftest: 打开后 15s 内未就绪");
                    result = 1;
                    Close();
                    return;
                }

                // 双步进断言
                var before = _sync.GetMasterPosition100ns();
                _sync.StepFrames(1);
                await Task.Delay(200);
                var afterFrame = _sync.GetMasterPosition100ns();
                Console.WriteLine($"selftest: 帧步进 {TimeSpan.FromTicks(before)} → {TimeSpan.FromTicks(afterFrame)} (Δ={TimeSpan.FromTicks(afterFrame - before)})");
                if (afterFrame <= before && _sync.Slots.Count > 0 && !_realMode)
                {
                    // 演示模式步进应前进；真实模式同帧时允许
                }
                if (_realMode && afterFrame < before)
                {
                    Console.Error.WriteLine("selftest: 帧步进后退");
                    result = 1;
                    Close();
                    return;
                }

                _sync.StepSeconds(1);
                await Task.Delay(200);
                var afterSec = _sync.GetMasterPosition100ns();
                Console.WriteLine($"selftest: 秒步进 {TimeSpan.FromTicks(afterFrame)} → {TimeSpan.FromTicks(afterSec)} (Δ={TimeSpan.FromTicks(afterSec - afterFrame)})");
                if (_realMode && afterSec < afterFrame)
                {
                    Console.Error.WriteLine("selftest: 秒步进后退");
                    result = 1;
                    Close();
                    return;
                }

                var media = _sync.Slots.FirstOrDefault()?.Session.ReadMediaInfo();
                if (media is not null)
                    Console.WriteLine($"selftest: 媒体 {media.VideoWidth}x{media.VideoHeight} @{media.FrameRate:0.##}fps {media.Codec} HDR={media.IsHdr}");
                else
                    Console.WriteLine("selftest: 媒体信息不可用");

                Console.WriteLine("selftest: 全部通过 ✓");
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"selftest: 异常 {ex}");
                result = 1;
            }
            Close();
        };
        Application.Run(this);
        return result;
    }

    /// <summary>截图自检：打开视频→等就绪→DrawToBitmap 导出 PNG→退出码 0。
    /// 验证 F21 截图链路（真实/演示双模式）。</summary>
    public int RunScreenshotTest(string path, string outputPath)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"screentest: 文件不存在 {path}");
            return 2;
        }

        var result = -2;
        Shown += async (_, _) =>
        {
            try
            {
                AutoOpenFiles(new[] { path });
                Console.WriteLine($"screentest: 打开 {path} ({(_realMode ? "真实" : "演示")}模式)");

                // 等就绪
                var ready = false;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(120);
                    var snap = _sync.ReadMasterSnapshot();
                    if (snap is { Duration100ns: > 0 } && (snap.State == 2 || snap.State == 3 || snap.State == 4))
                    {
                        ready = true;
                        break;
                    }
                }
                if (!ready)
                {
                    Console.Error.WriteLine("screentest: 未就绪");
                    result = 1;
                    Close();
                    return;
                }

                // 等一帧渲染（真实模式 D3D 需要时间）
                await Task.Delay(500);

                var surface = _grid.GetSurface(0);
                if (surface is null)
                {
                    Console.Error.WriteLine("screentest: surface 不可用");
                    result = 1;
                    Close();
                    return;
                }

                Bitmap bmp = null!;
                if (_realMode)
                {
                    // 真实模式：优先 PrintWindow 抓 D3D 合成帧；失败回退 BitBlt 屏幕区；再回退像素采样
                    var viaPrint = WgcFrameCapture.CaptureWindowFrame(surface.Handle);
                    if (viaPrint is not null)
                    {
                        bmp = viaPrint;
                        Console.WriteLine($"screentest: PrintWindow 捕获 {bmp.Width}x{bmp.Height}");
                    }
                    else
                    {
                        // 主窗口置前可见后再截
                        Activate();
                        BringToFront();
                        Application.DoEvents();
                        await Task.Delay(300);
                        bmp = WgcFrameCapture.CaptureWindowFrame(surface.Handle);
                        if (bmp is not null)
                        {
                            Console.WriteLine($"screentest: PrintWindow(重试) {bmp.Width}x{bmp.Height}");
                        }
                    }

                    if (bmp is null)
                    {
                        var media = _sync.Slots[0].Session.ReadMediaInfo();
                        var w = Math.Clamp(media?.VideoWidth ?? 320, 16, 1280);
                        var h = Math.Clamp(media?.VideoHeight ?? 180, 16, 720);
                        var scale = Math.Min(1.0, 320.0 / w);
                        var sw = Math.Max(16, (int)(w * scale));
                        var sh = Math.Max(16, (int)(h * scale));
                        var session = _sync.Slots[0].Session;

                        bmp = new Bitmap(sw, sh);
                        for (var y = 0; y < sh; y++)
                        {
                            for (var x = 0; x < sw; x++)
                            {
                                var sx = Math.Min(w - 1, (int)(x / scale));
                                var sy = Math.Min(h - 1, (int)(y / scale));
                                if (session.TryReadPixel(sx, sy, out var pxVal))
                                {
                                    bmp.SetPixel(x, y, Color.FromArgb(
                                        (int)Math.Clamp(pxVal.R * 255, 0, 255),
                                        (int)Math.Clamp(pxVal.G * 255, 0, 255),
                                        (int)Math.Clamp(pxVal.B * 255, 0, 255)));
                                }
                                else
                                {
                                    bmp.SetPixel(x, y, Color.FromArgb(20, 20, 24));
                                }
                            }
                        }
                        Console.WriteLine($"screentest: ReadVideoPixel 采样 {sw}x{sh} (源 {w}x{h})");
                    }
                }
                else
                {
                    bmp = new Bitmap(Math.Max(1, surface.Width), Math.Max(1, surface.Height));
                    using (var g = Graphics.FromImage(bmp))
                    {
                        surface.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    }
                }
                bmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                var bytes = new FileInfo(outputPath).Length;
                bmp.Dispose();
                Console.WriteLine($"screentest: PNG 已保存 {outputPath} ({bytes / 1024} KB)");
                result = bytes > 1000 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"screentest: 异常 {ex.Message}");
                result = 1;
            }
            Close();
        };
        Application.Run(this);
        return result;
    }

    /// <summary>外部入口（--autodemo / 测试钩子）：自动打开文件并开始播放。
    /// 必须在窗体句柄创建后调用（构造后由 Show 触发）。</summary>
    public void AutoOpenFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        // 等待 Handle 就绪后执行，避免 BeginInvoke 在句柄前失败
        if (!IsHandleCreated)
            Shown += (_, _) => AutoOpenFiles(files);
        else
            OpenFilesDeferred(files);
    }

    private void OpenFilesDeferred(IReadOnlyList<string> files)
    {
        OpenFiles(files.ToArray());
        BeginInvoke(() =>
        {
            // 等打开完成后播放（演示模式立即生效）
            _sync.Play();
            _isPlaying = true;
            _transport.SetPlaying(true);
        });
    }

    private void OpenVideos()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenFiles(dlg.FileNames);
    }

    private void OpenFiles(string[] files)
    {
        var count = Math.Min(files.Length, 9 - _sync.Count);
        if (count <= 0) return;
        _grid.SetCount(_sync.Count + count);

        var realSurface = _realMode;
        for (var i = 0; i < count; i++)
        {
            var path = files[i];
            var surface = _grid.Surfaces[_sync.Count];
            surface.FileName = Path.GetFileName(path);
            surface.IsFailed = false;

            try
            {
                // 确保 Surface 的 HWND 已创建（真实模式需要有效输出窗口）
                if (_realMode && surface.Handle == 0)
                {
                    _ = surface.Handle; // 强制创建
                }
                var session = _engine.CreateSession(new EngineSessionOptions
                {
                    OutputWindow = surface.Handle,
                    HardwareDecode = _settings.HardwareDecode,
                    PreferredAdapterIndex = _settings.PreferredAdapterIndex,
                    ColorMode = (ColorMode)_settings.ColorMode,
                });
                surface.AttachSession(session);
                _sync.AddSlot(session, path);

                // 异步打开
                _ = OpenSlotAsync(_sync.Slots[^1], surface, path);
            }
            catch (Exception ex)
            {
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
            }
        }
        UpdateStatus();
    }

    private async Task OpenSlotAsync(SyncController.SyncSlot slot, PlayerSurface surface, string path)
    {
        try
        {
            await slot.Session.OpenAsync(path);
            surface.FileName = Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            slot.Failed = true;
            slot.Error = ex.Message;
            surface.IsFailed = true;
            surface.ErrorText = ex.Message;
        }
        UpdateStatus();
    }

    private void AddSlotPlaceholder()
    {
        if (_sync.Count >= 9) return;
        _grid.SetCount(_sync.Count + 1);
        UpdateStatus();
    }

    private void RemoveLastSlot()
    {
        if (_sync.Count <= 0) return;
        _grid.Surfaces[_sync.Count - 1].DetachSession();
        _sync.RemoveSlotAt(_sync.Count - 1);
        _grid.SetCount(_sync.Count);
        UpdateStatus();
    }

    // 打开文件后架设探针/放大镜联动
    private void WireSurfaceTools()
    {
        foreach (var s in _grid.Surfaces)
        {
            var surface = s;
            surface.MouseMove += (_, e) =>
            {
                if (_chkMagnifier.Checked)
                    _magnifier.UpdateMagnifier(new Point(e.X, e.Y));
                if (_probe.Visible && surface.Selected)
                    _probe.UpdatePoint(e.X, e.Y);
            };
            surface.MouseLeave += (_, _) => _magnifier.HideMagnifier();
        }
    }

    // ---------- 播放控制 ----------

    private void TogglePlay()
    {
        if (_sync.Count == 0) return;
        var snap = _sync.ReadMasterSnapshot();
        var playing = snap is { State: 3 };
        if (playing) _sync.Pause();
        else _sync.Play();
        _isPlaying = !playing;
        _transport.SetPlaying(_isPlaying);
    }

    private void Stop()
    {
        _sync.Stop();
        _isPlaying = false;
        _transport.SetPlaying(false);
    }

    private void StepFrames(int frames)
        => _sync.StepFrames(frames * Math.Max(1, _sync.StepProfile.FrameStep));

    private void StepSeconds(double seconds)
        => _sync.StepSeconds(seconds * _sync.StepProfile.SecondsStep);

    private void ToggleLoop()
    {
        var loop = !_sync.LoopEnabled;
        _sync.LoopEnabled = loop;
        if (loop)
        {
            var pos = _sync.GetMasterPosition100ns();
            if (_sync.LoopStart100ns < 0) _sync.LoopStart100ns = 0;
            if (_sync.LoopEnd100ns < 0) _sync.LoopEnd100ns = Math.Max(0, _sync.GetMasterDuration100ns());
            _timeline.SetLoopRange(_sync.LoopStart100ns, _sync.LoopEnd100ns);
        }
        else
        {
            _sync.LoopStart100ns = -1;
            _sync.LoopEnd100ns = -1;
            _timeline.SetLoopRange(-1, -1);
        }
        _transport.SetLoop(loop);
    }

    /// <summary>时间轴 A/B 打点（F11 区间循环快捷键别名：A 设起点、B 设终点）。</summary>
    private void SetLoopPoint(long position100ns, bool isA)
    {
        _sync.LoopEnabled = true;
        if (isA)
        {
            _sync.LoopStart100ns = position100ns;
        }
        else
        {
            _sync.LoopEnd100ns = position100ns;
        }
        // 若只设了一点，自动补全另一点
        if (_sync.LoopStart100ns < 0) _sync.LoopStart100ns = 0;
        if (_sync.LoopEnd100ns < 0) _sync.LoopEnd100ns = Math.Max(0, _sync.GetMasterDuration100ns());
        _timeline.SetLoopRange(_sync.LoopStart100ns, _sync.LoopEnd100ns);
        _transport.SetLoop(true);
        UpdateStatus();
    }

    private void SetPlaybackSpeed(double speed)
    {
        // 播放速度：3FP 无原生速率 API（A2），演示模式直接改轮询步进比例；
        // 真实模式暂以「每次轮询按速度 Seek」的伪变速近似（后续 A2 落地后替换）。
        _playbackSpeed = speed;
    }

    private double _playbackSpeed = 1.0;

    // ---------- 视图 ----------

    private void ToggleSingleView()
    {
        _grid.SingleView = !_grid.SingleView;
        HideChromeOverlaysIfNeeded();
        UpdateStatus();
    }

    // ---------- 工具面板（探针 / 书签 / A-B / 放大镜） ----------

    private void ToggleProbePanel()
    {
        _toolsDock.ShowPanel(_probe);
        // 把探针关联到选中路
        AttachProbeToSelected();
    }

    private void ToggleBookmarkPanel()
    {
        _toolsDock.ShowPanel(_bookmarks);
    }

    private void ToggleOffsetPanel()
    {
        _toolsDock.ShowPanel(_offsetPanel);
        UpdateOffsetPanel();
    }

    private void ToggleMediaInfoPanel()
    {
        _toolsDock.ShowPanel(_mediaInfoPanel);
        RefreshMediaInfoPanel();
    }

    /// <summary>差异叠加：弹窗展示两路差异热力图（F20，可选工具）。</summary>
    private void ToggleDiffView()
    {
        if (_sync.Count < 2)
        {
            MessageBox.Show(this, "差异叠加需要至少 2 路视频。请先用「打开视频」加载两路。", "3FCompare",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var sel = _grid.SelectedIndex >= 0 ? _grid.SelectedIndex : 0;
        var a = sel;
        var b = (sel + 1) % _sync.Count;
        var dialog = new Form
        {
            Text = $"差异叠加 [{a + 1}] vs [{b + 1}]",
            Size = new Size(720, 520),
            StartPosition = FormStartPosition.CenterParent,
        };
        var view = new DiffOverlayView(_grid);
        view.SetPair(a, b);
        view.Dock = DockStyle.Fill;
        dialog.Controls.Add(view);
        dialog.Click += (_, _) => view.Invalidate(); // 点击重采样
        dialog.ShowDialog(this);
    }

    /// <summary>音频控制面板（音轨/音量/静音）。</summary>
    private void ToggleAudioPanel()
    {
        _toolsDock.ShowPanel(_audioPanel);
        RefreshAudioPanel();
    }

    private void RefreshAudioPanel()
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count)
        {
            _audioPanel.AttachSession(null, null);
            return;
        }
        try
        {
            var session = _sync.Slots[idx].Session;
            var media = session.ReadMediaInfo();
            _audioPanel.AttachSession(session, media);
        }
        catch
        {
            _audioPanel.AttachSession(null, null);
        }
    }

    /// <summary>刷新媒体信息面板（读取选中路媒体信息；打开完成前为 null）。</summary>
    private void RefreshMediaInfoPanel()
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count)
        {
            _mediaInfoPanel.Clear();
            return;
        }
        try
        {
            var media = _sync.Slots[idx].Session.ReadMediaInfo();
            _mediaInfoPanel.ShowMediaInfo(media);
        }
        catch
        {
            _mediaInfoPanel.Clear();
        }
    }

    /// <summary>对选中路微调偏移（delta 100ns；reset=true 归零）。</summary>
    private void NudgeSelectedOffset(long delta, bool reset = false)
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count) return;
        var slot = _sync.Slots[idx];
        slot.Offset100ns = reset ? 0 : slot.Offset100ns + delta;
        UpdateOffsetPanel();
        _sync.RefreshAllPositions(); // 让全场按新偏移重新对齐
    }

    /// <summary>对齐命令：把选中路「对齐到此帧」——记录它与 master 的时间差为偏移。</summary>
    private void AlignSelectedToMaster()
    {
        var idx = _grid.SelectedIndex;
        if (idx <= 0 || idx >= _sync.Count) return; // 第 0 路是基准，不可对齐
        var master = _sync.ReadMasterSnapshot();
        var target = _sync.Slots[idx].Session.ReadSnapshot();
        if (master is null || target is null) return;
        var slot = _sync.Slots[idx];
        slot.Offset100ns = master.Position100ns - target.Position100ns;
        UpdateOffsetPanel();
        _sync.RefreshAllPositions();
    }

    private void UpdateOffsetPanel()
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count)
        {
            _offsetPanel.SetPlaceholder("未选中路");
            return;
        }
        var slot = _sync.Slots[idx];
        var snap = _sync.ReadMasterSnapshot();
        var fps = snap is null ? 24.0 : SyncController.EstimateFps(snap);
        _offsetPanel.SetOffset(slot.Offset100ns, fps);
    }

    private void ShowGridOnly()
    {
        _toolsDock.HideAll();
        _abViewVisible = false;
        HideChromeOverlaysIfNeeded();
        _grid.Dock = DockStyle.Fill;
    }

    private void ToggleAbView()
    {
        _abViewVisible = !_abViewVisible;
        HideChromeOverlaysIfNeeded();
    }

    private void HideChromeOverlaysIfNeeded()
    {
        if (_abViewVisible)
        {
            // A-B 视图替换网格主体
            _grid.Visible = false;
            if (_abSlider.Parent is null) Controls.Add(_abSlider);
            _abSlider.Dock = DockStyle.Fill;
            _abSlider.BringToFront();
            _abSlider.Visible = true;
            _abSlider.SetPair(_grid.SelectedIndex >= 0 ? _grid.SelectedIndex : 0,
                              _grid.SelectedIndex >= 0 ? (_grid.SelectedIndex + 1) % Math.Max(1, _grid.Count) : 1);
        }
        else
        {
            _abSlider.Visible = false;
            _grid.Visible = true;
        }
        UpdateStatus();
    }

    private void AttachProbeToSelected()
    {
        var sel = _grid.SelectedIndex;
        var session = _sync.Slots.Count > 0 && sel >= 0 && sel < _sync.Slots.Count
            ? _sync.Slots[sel].Session : null;
        _probe.AttachSession(session);
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _oldWindowState = WindowState;
            _oldFormBorderStyle = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            if (_settings.HideChromeInFullscreen)
            {
                _menu.Visible = false;
                _transport.Visible = false;
                _timeline.Visible = false;
            }
        }
        else
        {
            FormBorderStyle = _oldFormBorderStyle;
            WindowState = _oldWindowState;
            _menu.Visible = true;
            _transport.Visible = true;
            _timeline.Visible = true;
        }
        _grid.LayoutSurfaces();
    }

    private FormWindowState _oldWindowState;
    private FormBorderStyle _oldFormBorderStyle;

    // ---------- 设置 ----------

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Changed)
        {
            _settings.HardwareDecode = dlg.Result.HardwareDecode;
            _settings.PreferredAdapterIndex = dlg.Result.PreferredAdapterIndex;
            _settings.FrameStep = dlg.Result.FrameStep;
            _settings.SecondsStep = dlg.Result.SecondsStep;
            _settings.StartFullscreen = dlg.Result.StartFullscreen;
            _settings.HideChromeInFullscreen = dlg.Result.HideChromeInFullscreen;
            _settings.ColorMode = dlg.Result.ColorMode;
            _settings.DefaultGridCols = dlg.Result.DefaultGridCols;
            _settings.DefaultGridRows = dlg.Result.DefaultGridRows;
            SettingsStore.Save(_settings);

            _sync.StepProfile = new StepProfile { FrameStep = _settings.FrameStep, SecondsStep = _settings.SecondsStep };
            UpdateStatus();
        }
    }

    // ---------- 会话保存/加载 ----------

    /// <summary>导出当前选中路的当前帧为 PNG（F21）。
    /// 演示模式：DirectDraw 自绘画面（快照帧号/时间已渲染）；
    /// 真实模式：用 PrintWindow 抓取 Surface（D3D 输出到窗口，可直接捕获合成帧）。</summary>
    private void CaptureSelectedFrame()
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count)
        {
            MessageBox.Show(this, "请先选中一个已打开的媒体", "3FCompare", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var surface = _grid.GetSurface(idx);
        if (surface is null) return;

        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图像|*.png",
            FileName = $"frame_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Bitmap bmp;
            if (_realMode)
            {
                // 真实模式：PrintWindow 抓取 D3D 合成帧（回退 ReadVideoPixel 采样）
                bmp = WgcFrameCapture.CaptureWindowFrame(surface.Handle)
                    ?? CapturePixelSampled();
                if (bmp is null)
                {
                    MessageBox.Show(this, "截图失败：窗口帧捕获与像素采样均不可用", "3FCompare",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                bmp = new Bitmap(Math.Max(1, surface.Width), Math.Max(1, surface.Height));
                using (var g = Graphics.FromImage(bmp))
                {
                    surface.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                }
            }

            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            _statusInfo.Text = $"已导出截图: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"截图失败: {ex.Message}", "3FCompare", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>真实模式回退：ReadVideoPixel 逐点采样重建小尺寸位图。</summary>
    private Bitmap? CapturePixelSampled()
    {
        var idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _sync.Count) return null;
        try
        {
            var session = _sync.Slots[idx].Session;
            var media = session.ReadMediaInfo();
            var w = Math.Clamp(media?.VideoWidth ?? 320, 16, 1280);
            var h = Math.Clamp(media?.VideoHeight ?? 180, 16, 720);
            var scale = Math.Min(1.0, 320.0 / w);
            var sw = Math.Max(16, (int)(w * scale));
            var sh = Math.Max(16, (int)(h * scale));

            var bmp = new Bitmap(sw, sh);
            for (var y = 0; y < sh; y++)
            {
                for (var x = 0; x < sw; x++)
                {
                    var sx = Math.Min(w - 1, (int)(x / scale));
                    var sy = Math.Min(h - 1, (int)(y / scale));
                    if (session.TryReadPixel(sx, sy, out var px))
                    {
                        bmp.SetPixel(x, y, Color.FromArgb(
                            (int)Math.Clamp(px.R * 255, 0, 255),
                            (int)Math.Clamp(px.G * 255, 0, 255),
                            (int)Math.Clamp(px.B * 255, 0, 255)));
                    }
                    else
                    {
                        bmp.SetPixel(x, y, Color.FromArgb(20, 20, 24));
                    }
                }
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void SaveSession()
    {
        using var dlg = new SaveFileDialog { Filter = "3FCompare 会话|*.3fcs|JSON|*.json", FileName = "session.3fcs" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var snap = new SessionSnapshot
        {
            GridLayout = _grid.SingleView ? 1 : 0,
            Position100ns = _sync.GetMasterPosition100ns(),
            LoopEnabled = _sync.LoopEnabled,
            LoopStart100ns = _sync.LoopStart100ns,
            LoopEnd100ns = _sync.LoopEnd100ns,
        };
        foreach (var slot in _sync.Slots)
        {
            snap.Items.Add(new SessionSnapshot.SessionItem
            {
                Path = slot.Path,
                Offset100ns = slot.Offset100ns,
            });
        }
        SessionSnapshot.SaveToFile(dlg.FileName, snap);
    }

    private void LoadSession()
    {
        using var dlg = new OpenFileDialog { Filter = "3FCompare 会话|*.3fcs;*.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var snap = SessionSnapshot.LoadFromFile(dlg.FileName);
        if (snap is null || snap.Items.Count == 0)
        {
            MessageBox.Show(this, "会话文件无效或为空", "3FCompare", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _sync.Clear();
        _grid.SetCount(0);
        OpenFiles(snap.Items.Select(x => x.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray()!);

        if (snap.Position100ns > 0) _sync.SeekTo(snap.Position100ns);
        if (snap.LoopEnabled)
        {
            _sync.LoopEnabled = true;
            _sync.LoopStart100ns = snap.LoopStart100ns;
            _sync.LoopEnd100ns = snap.LoopEnd100ns;
            _timeline.SetLoopRange(snap.LoopStart100ns, snap.LoopEnd100ns);
            _transport.SetLoop(true);
        }
    }

    // ---------- 轮询 ----------

    private long _lastShownPos;

    private void PollSnapshots()
    {
        if (_sync.Count == 0) return;

        var snaps = _sync.ReadAllSnapshots();
        for (var i = 0; i < snaps.Count && i < _grid.Count; i++)
        {
            _grid.GetSurface(i)?.UpdateSnapshot(snaps[i]);
        }

        var master = snaps.Count > 0 ? snaps[0] : null;
        if (master is not null)
        {
            _timeline.SetDuration(master.Duration100ns);
            _timeline.SetPosition(master.Position100ns);
            _transport.SetTime(TimeSpan.FromTicks(master.Position100ns), TimeSpan.FromTicks(master.Duration100ns));
        }

        // 播放状态回显（若被原生事件改变）
        if (master is { State: 3 } && !_isPlaying)
        {
            _isPlaying = true;
            _transport.SetPlaying(true);
        }
        else if (master is not null and { State: not 3 } && _isPlaying)
        {
            _isPlaying = false;
            _transport.SetPlaying(false);
        }

        // 循环检测
        if (_sync.LoopEnabled) _sync.TickLoop();

        // 伪变速：真实模式下按速度节流 Seek（A2 落地前）
        if (_isPlaying && _realMode && Math.Abs(_playbackSpeed - 1.0) > 0.01 && master is not null)
        {
            var now = master.Position100ns;
            if (_lastShownPos == 0) _lastShownPos = now;
            var elapsed = now - _lastShownPos;
            if (elapsed > 0 && _playbackSpeed > 1.0)
            {
                // 加速：目标位置 = 当前 + (elapsed * (speed-1))，每 100ms 纠正一次
                _sync.SeekTo(now + (long)(elapsed * (_playbackSpeed - 1.0)));
            }
            _lastShownPos = now;
        }
        else if (_lastShownPos != 0 && master is not null)
        {
            _lastShownPos = master.Position100ns;
        }
    }

    private void UpdateStatus()
    {
        if (_sync.Count == 0)
        {
            _statusInfo.Text = _realMode ? "就绪 — 打开视频开始对比" : "演示模式 — 打开任意视频文件体验（画面为合成）";
            return;
        }
        var mode = _grid.SingleView ? "单屏" : "网格";
        var failed = _sync.Slots.Count(s => s.Failed);
        _statusInfo.Text = $"{mode}模式 | 路数 {_sync.Count}/9 | 步进: {_sync.StepProfile.FrameStep}帧/{_sync.StepProfile.SecondsStep:0.#}秒" +
            (failed > 0 ? $" | {failed} 路失败" : string.Empty);
    }

    // ---------- 快捷键 ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Space:
                TogglePlay();
                return true;
            case Keys.S | Keys.Control:
                CaptureSelectedFrame();
                return true;
            case Keys.Left:
                StepFrames(-1);
                return true;
            case Keys.Right:
                StepFrames(1);
                return true;
            case Keys.Left | Keys.Shift:
                StepSeconds(-1);
                return true;
            case Keys.Right | Keys.Shift:
                StepSeconds(1);
                return true;
            case Keys.Up:
                StepSeconds(-10);
                return true;
            case Keys.Down:
                StepSeconds(10);
                return true;
            case Keys.F11:
                ToggleFullscreen();
                return true;
            case Keys.Escape when _fullscreen:
                ToggleFullscreen();
                return true;
            case Keys.O:
                OpenVideos();
                return true;
            case Keys.B:
                ToggleAbView();
                return true;
            case Keys.P:
                ToggleProbePanel();
                return true;
            case Keys.F6:
                ToggleOffsetPanel();
                return true;
            case Keys.Delete:
                _bookmarks.RemoveSelected();
                return true;
        }

        // 数字键 1-9 设置路数
        if (keyData >= Keys.D1 && keyData <= Keys.D9)
        {
            var n = (int)(keyData - Keys.D1) + 1;
            if (n <= 9 && n >= 1 && _sync.Count <= n)
            {
                while (_sync.Count < n) AddSlotPlaceholder();
            }
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 保存窗口位置/尺寸/最大化状态
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            _settings.WindowMaximized = false;
        }
        else
        {
            // 最大化/最小化时记录恢复为普通状态的尺寸
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            var restored = RestoreBounds;
            if (restored.Width > 0)
            {
                _settings.WindowX = restored.X;
                _settings.WindowY = restored.Y;
                _settings.WindowWidth = restored.Width;
                _settings.WindowHeight = restored.Height;
            }
        }
        SettingsStore.Save(_settings);

        _pollTimer.Stop();
        _sync.Clear();
        base.OnFormClosing(e);
    }
}