using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Drawing.Imaging;
using _3FCompare.App;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;
using _3FCompare.Diagnostics;

namespace _3FCompare;

/// <summary>帧导出（抓帧）相关逻辑。
/// <para>从 MainWindow.axaml.cs 拆出（P0-2 修复 + P1-1 拆分）。</para>
/// <para><b>P0-2 背景</b>：旧实现把 GDI BitBlt 抓屏作为首选路径，抓到的是 DWM 合成之后的画面
/// （已经过色彩管理与色调映射），且窗口被遮挡时会抓到遮挡物；退路又固定降采样到 320px 宽。
/// 这对"画质对比"工具是不可接受的——用户导出的对比素材既不原生也不准确。
/// 现在改为<b>内核原生回读优先</b>（与像素探针同一个数据源：颜色管理前的原生缓冲），
/// 抓屏仅作为引擎不支持时的兜底。</para></summary>
public partial class MainWindow : Window
{
    /// <summary>单次回读的像素预算。内核回读接口返回的是 RGBA 归一化浮点（每像素 4 float = 16B），
    /// 4K 整帧一次性读回需要约 133MB；分块可以把单次分配压到 ~24MB 量级，避免 LOH 抖动。</summary>
    private const int TilePixelBudget = 1_500_000;

    private async void OnExportFrame(object? sender, RoutedEventArgs e)
    {
        // D1：async void 顶层边界。文件选择器（SavedFilePicker）与 MessageBox.ShowDialog
        // 都可能抛，而方法体内原有的两段 try/catch 只覆盖"抓帧"与"写盘"，
        // 覆盖不到选择器与提示框本身 ⇒ 漏网异常直接冒泡到 Dispatcher ⇒ 进程闪退。
        // 成功路径与既有的两段内部分支完全不变。
        try
        {
            var surface = Grid.GetSurface(Math.Max(0, Grid.SelectedIndex));
            if (surface is null || _sync.Count == 0)
            {
                await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                    LanguageManager.T("Msg_SelectMedia"), LanguageManager.T("Settings_Ok"));
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageManager.T("Menu_ExportFrame"),
                SuggestedFileName = $"frame_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;

            var session = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex)?.Session;

            // 路径①（首选）：内核原生回读 —— 颜色管理前的原生缓冲，任意分辨率
            // 路径②（兜底）：抓屏双线（WGC 主 + GDI 备，docs/27 §三）—— 仅在引擎不支持回读时使用，
            //   会在状态栏标注实际来源。两条抓屏线的可信度不等价（WGC 能抓到 flip-model 内容且被遮挡
            //   时结果正确；GDI 对 flip-model 不可靠、被遮挡时会抓到遮挡物，docs/26 §3.2/§八），
            //   所以这里必须区分标注，不能笼统写"抓屏回退"。
            System.Drawing.Bitmap? bmp = null;
            var source = "native";
            try
            {
                if (session is not null)
                    bmp = CaptureNativeFrame(session);
                if (bmp is null && surface.Hwnd != 0)
                {
                    var captured = _3FCompare.App.Capture.FrameCapture.CaptureWindowFrame(surface.Hwnd);
                    bmp = captured?.Bitmap;
                    source = captured?.Route == _3FCompare.Core.Capture.CaptureRoute.Wgc ? "wgc" : "screen";
                    // 组件日志：抓屏请求与其实际选路（WGC / GDI 回退 / 无结果）。
                    // 抓屏要读子 HWND 的合成结果，是"呈现线程 + 窗口生命周期"的交叉点。
                    ComponentLog.Log(Comp.Capture, "WindowFrame", Grid.SelectedIndex,
                        $"hwnd=0x{surface.Hwnd:X} route={source} ok={(bmp is not null)}");
                }
            }
            catch (Exception ex)
            {
                await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                    $"{LanguageManager.T("Msg_CaptureFail")}: {ex.Message}", LanguageManager.T("Settings_Ok"));
                return;
            }

            if (bmp is null)
            {
                await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                    LanguageManager.T("Msg_CaptureUnavailable"), LanguageManager.T("Settings_Ok"));
                return;
            }

            try
            {
                using (bmp)
                {
                    var size = $"{bmp.Width}×{bmp.Height}";
                    WritePngWithSrgbChunk(bmp, path);
                    // 明确标注来源与分辨率：抓屏结果与内核回读不等价，用户做对比报告时需要知道。
                    // 注意：内核 ReadVideoPixelRegion 回读的是"已呈现帧"，故分辨率 = 呈现分辨率
                    // （随窗口/显示器大小变化），不是源视频分辨率——用词避免误导为"原生分辨率"。
                    StatusInfo.Text = source switch
                    {
                        "native" => $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)} ({size}, 内核回读)",
                        "wgc" => $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)} ({size}, WGC 抓屏)",
                        _ => $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)} ({size}, GDI 抓屏回退)",
                    };
                }
            }
            catch (Exception ex)
            {
                await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                    $"{LanguageManager.T("Msg_CaptureFail")}: {ex.Message}", LanguageManager.T("Settings_Ok"));
            }
        }
        catch (Exception fatal)
        {
            // 变量名刻意不叫 ex：方法体内两个内层 catch 已用 ex（CS0136 禁止在外层
            // 作用域重名），改名后仍指同一个"漏到顶层的异常"。
            await ReportErrorAsync("Menu.ExportFrame",
                Loc("导出当前帧失败。", "Failed to export the current frame."), fatal);
        }
    }

    /// <summary>从内核后台缓冲整帧回读当前帧（分块，支持 4K/8K）。
    /// <para>内核回读的坐标域是<b>后台缓冲</b>而非视频源，视频内容只占据 destination 矩形
    /// （letterbox 之外是背景），因此必须先取 RenderTargetInfo 定位目标矩形。
    /// 返回 null 表示引擎不支持本次回读（调用方应回退抓屏）。</para></summary>
    private unsafe System.Drawing.Bitmap? CaptureNativeFrame(IPlayerSession? session)
    {
        if (session is null) return null;
        try
        {
            var media = session.ReadMediaInfo();
            if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0) return null;

            // "无 RTInfo 退回整面 / Dest 超 swap 按 swap 裁剪"的算术已下沉到
            // Core.Backend.NativeFrameReadback（纯函数，可单测，docs/41 §4.5 第 12 项）：
            // 原先是内联在这里的 8 行，而本方法挂在 Window 子类上，单测够不着。
            var rtAvailable = session.ReadRenderTargetInfo(out var rt);
            var rect = _3FCompare.Core.Backend.NativeFrameReadback.ResolveRect(
                rtAvailable, rt, media.VideoWidth, media.VideoHeight);
            if (rect is not { } readRect) return null;
            var (x0, y0, w, h) = (readRect.X, readRect.Y, readRect.Width, readRect.Height);

            var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var tileRows = Math.Clamp(TilePixelBudget / Math.Max(1, w), 1, h);
            var buffer = new float[w * tileRows * 4];

            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            // 解锁与释放的职责必须收敛到 finally：此前失败分支先 UnlockBits + Dispose，
            // finally 又对已释放的 Bitmap 再 UnlockBits 一次 → 每次回读失败都抛异常
            // （被外层 catch 吞掉，结果"碰巧正确"，但异常路径常态化）。
            var readOk = false;
            try
            {
                var dst = (byte*)data.Scan0;
                for (var y = 0; y < h; y += tileRows)
                {
                    var rows = Math.Min(tileRows, h - y);
                    if (!session.TryReadPixelRegion(x0, y0 + y, w, rows, buffer, out _))
                    {
                        return null;   // 解锁/释放交给 finally
                    }
                    for (var r = 0; r < rows; r++)
                    {
                        var row = dst + (long)(y + r) * data.Stride;
                        var si = r * w * 4;
                        for (var x = 0; x < w; x++)
                        {
                            var i = si + x * 4;
                            // 内核返回 0..1 归一化的 RGBA 浮点（与像素探针同一语义：
                            // 颜色管理前的原生缓冲）。Format32bppArgb 在内存中是 BGRA 顺序。
                            row[x * 4 + 0] = (byte)To8(buffer[i + 2]); // B
                            row[x * 4 + 1] = (byte)To8(buffer[i + 1]); // G
                            row[x * 4 + 2] = (byte)To8(buffer[i + 0]); // R
                            row[x * 4 + 3] = (byte)To8(buffer[i + 3]); // A
                        }
                    }
                }
                readOk = true;
                return bmp;
            }
            finally
            {
                // 先解锁（此时 Bitmap 仍存活），再按成败决定是否释放；
                // 内层 try/finally 保证即使 UnlockBits 抛异常也不会漏 Dispose。
                try { bmp.UnlockBits(data); }
                finally { if (!readOk) bmp.Dispose(); }
            }
        }
        catch
        {
            return null; // 回读失败 → 交给调用方回退抓屏
        }
    }

    /// <summary>保存 PNG 并补写 sRGB 色彩标记。
    /// <para>System.Drawing 存出的 PNG 不带任何色彩空间信息，其它软件打开时只能靠猜测，
    /// 广色域/HDR 内容尤其容易偏色。sRGB chunk 明确声明"这是 sRGB 编码数据"。</para></summary>
    private static void WritePngWithSrgbChunk(System.Drawing.Bitmap bmp, string path)
    {
        byte[] png;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }
        // chunk 遍历 / CRC32 / sRGB 插入已下沉到 Core.Imaging.PngChunk（可单测）。
        // 原先这些纯计算写在 UI 层，测试工程够不着，写错只会安静地产出损坏的 PNG
        // （docs/14 §4.2）。
        File.WriteAllBytes(path, PngChunk.InsertSrgbAfterIhdr(png));
    }
}
