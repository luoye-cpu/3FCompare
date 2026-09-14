using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Drawing.Imaging;
using _3FCompare.App;
using _3FCompare.Core.Backend;

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
        // 路径②（兜底）：GDI 抓屏 —— 仅在引擎不支持回读时使用，会在状态栏标注来源
        System.Drawing.Bitmap? bmp = null;
        var source = "native";
        try
        {
            if (session is not null)
                bmp = CaptureNativeFrame(session);
            if (bmp is null && surface.Hwnd != 0)
            {
                bmp = _3FCompare.App.Capture.ScreenFrameCapture.CaptureWindowFrame(surface.Hwnd);
                source = "screen";
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
                StatusInfo.Text = source == "native"
                    ? $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)} ({size}, 内核回读)"
                    : $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)} ({size}, 抓屏回退)";
            }
        }
        catch (Exception ex)
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                $"{LanguageManager.T("Msg_CaptureFail")}: {ex.Message}", LanguageManager.T("Settings_Ok"));
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

            // 无 RTInfo（演示模式 / 旧内核）时退回"整面即视频"的假定
            if (!session.ReadRenderTargetInfo(out var rt) || rt.DestWidth == 0 || rt.DestHeight == 0)
            {
                rt = new RenderTargetInfo((uint)media.VideoWidth, (uint)media.VideoHeight,
                    (uint)media.VideoWidth, (uint)media.VideoHeight,
                    0, 0, (uint)media.VideoWidth, (uint)media.VideoHeight, 8, false);
            }

            // 交换链尺寸是回读的硬性上界：放大/平移时 Dest 矩形可能超出，必须裁剪，
            // 否则会请求越界区域（内核返回失败或垃圾数据）。
            var swapW = rt.SwapWidth > 0 ? (int)rt.SwapWidth : int.MaxValue;
            var swapH = rt.SwapHeight > 0 ? (int)rt.SwapHeight : int.MaxValue;
            var x0 = Math.Clamp((int)rt.DestX, 0, Math.Max(0, swapW - 1));
            var y0 = Math.Clamp((int)rt.DestY, 0, Math.Max(0, swapH - 1));
            var w = Math.Min((int)rt.DestWidth, Math.Max(0, swapW - x0));
            var h = Math.Min((int)rt.DestHeight, Math.Max(0, swapH - y0));
            if (w <= 0 || h <= 0) return null;

            var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var tileRows = Math.Clamp(TilePixelBudget / Math.Max(1, w), 1, h);
            var buffer = new float[w * tileRows * 4];

            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var dst = (byte*)data.Scan0;
                for (var y = 0; y < h; y += tileRows)
                {
                    var rows = Math.Min(tileRows, h - y);
                    if (!session.TryReadPixelRegion(x0, y0 + y, w, rows, buffer, out _))
                    {
                        bmp.UnlockBits(data);
                        bmp.Dispose();
                        return null;
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
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
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

        // 已有色彩信息（sRGB / gAMA / iCCP / cHRM）时不动，避免冲突
        if (PngHasColorChunk(png))
        {
            File.WriteAllBytes(path, png);
            return;
        }

        using var fs = File.Create(path);
        fs.Write(png, 0, Math.Min(8, png.Length));   // PNG 签名
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);
            var total = 12 + len;
            if (len < 0 || pos + total > png.Length) break;
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            fs.Write(png, pos, total);
            pos += total;
            // sRGB 必须紧跟在 IHDR 之后（PNG 规范要求位于第一个 IDAT 之前）
            if (type == "IHDR") WriteChunk(fs, "sRGB", new byte[] { 0 }); // 0 = Perceptual
            if (type == "IEND") break;
        }
        if (pos < png.Length) fs.Write(png, pos, png.Length - pos); // 结构异常时兜底补齐
    }

    private static bool PngHasColorChunk(byte[] png)
    {
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var len = ReadBigEndianInt32(png, pos);
            var total = 12 + len;
            if (len < 0 || pos + total > png.Length) break;
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type is "sRGB" or "gAMA" or "iCCP" or "cHRM") return true;
            pos += total;
            if (type == "IEND") break;
        }
        return false;
    }

    private static int ReadBigEndianInt32(byte[] b, int offset)
        => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var lenBytes = BitConverter.GetBytes(data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
        s.Write(lenBytes, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        // CRC 覆盖 type + data
        var crcInput = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
        var crcBytes = BitConverter.GetBytes(Crc32(crcInput));
        if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
        s.Write(crcBytes, 0, 4);
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data)
    {
        _crcTable ??= BuildCrcTable();
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
