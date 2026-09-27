using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using _3FCompare.Core.Capture;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.App.Capture;

/// <summary>
/// 对原生库 <c>3FC.WgcCapture.dll</c> 的 P/Invoke 绑定。
///
/// <para><b>为什么用 <see cref="LibraryImportAttribute"/> 而不是 <c>[DllImport]</c></b>：
/// 本工程是 <c>PublishAot=true</c>。LibraryImport 由源生成器在编译期产出封送代码
/// （这里全是 blittable 指针/整数，几乎零封送），对 NativeAOT 的静态解析最友好，
/// 与本项目既有的自定义原生库互操作（<c>Core/Backend/Interop/Fff3FpNative.cs</c>）保持一致。
/// Platform 目录里对 <c>user32/gdi32</c> 这类系统 DLL 仍沿用既有的 <c>[DllImport]</c> 风格
/// （见 <see cref="GdiFrameCapture"/>）。</para>
///
/// <para>契约来源：<c>native/wgc_capture/wgc_capture.h</c>（权威）。全部导出名为未修饰名，
/// 调用约定 __cdecl —— x64 下与 Winapi 等价。</para>
/// </summary>
internal static partial class WgcNative
{
    /// <summary>原生库名（Windows 上会自动补 <c>.dll</c>）。</summary>
    private const string DllName = "3FC.WgcCapture";

    /// <summary>成功（<c>WGC_OK</c>）。</summary>
    internal const int Ok = 0;

    internal const int ErrNotSupported = 1;
    internal const int ErrCreateFailed = 2;
    internal const int ErrTimeout = 3;
    internal const int ErrNotCapturable = 4;
    internal const int ErrTextureRead = 5;
    internal const int ErrInvalidArg = 6;
    internal const int ErrInternal = 7;

    /// <summary>该 handle 的内部工作线程此前已超时被放弃 ⇒ <b>handle 已永久失效</b>，
    /// 此后每一次抓帧都会立即返回本码。调用方<b>必须</b>先 <see cref="Wgc_Destroy"/> 再重新
    /// <see cref="Wgc_Create"/>。与 <see cref="ErrInternal"/> 的区别：7 是"本次失败、handle 仍可用"，
    /// 8 是"handle 报废、必须重建"。契约见 <c>native/wgc_capture/wgc_capture.h</c>。</summary>
    internal const int ErrHandleDead = 8;

    /// <summary>把错误码翻译成可读文本（仅用于日志）。</summary>
    internal static string Describe(int code) => code switch
    {
        Ok => "OK",
        ErrNotSupported => "NOT_SUPPORTED(系统不支持 WGC)",
        ErrCreateFailed => "CREATE_FAILED(创建捕获器/会话失败)",
        ErrTimeout => "TIMEOUT(约 2s 内未取到有效帧)",
        ErrNotCapturable => "NOT_CAPTURABLE(窗口无效/已最小化/客户区为 0)",
        ErrTextureRead => "TEXTURE_READ(取 D3D11 纹理或 CPU 读回失败)",
        ErrInvalidArg => "INVALID_ARG(空指针等非法参数)",
        ErrInternal => "INTERNAL(其它内部错误，细节见 Wgc_LastError)",
        ErrHandleDead => "HANDLE_DEAD(工作线程已超时被放弃，handle 永久失效，须重建)",
        _ => $"UNKNOWN({code})",
    };

    [LibraryImport(DllName)]
    internal static partial int Wgc_IsSupported();

    [LibraryImport(DllName)]
    internal static partial nint Wgc_Create();

    /// <summary>原生声明是 <c>void Wgc_Destroy(void*)</c> —— <b>不返回</b>错误码，故托管侧也声明为 void。</summary>
    [LibraryImport(DllName)]
    internal static partial void Wgc_Destroy(nint handle);

    /// <param name="outBits">RGBA32（字节序 B,G,R,A）、**自上而下**行序的像素缓冲；调用方须用
    /// <see cref="Wgc_FreeFrame"/> 释放。失败时为 NULL。</param>
    [LibraryImport(DllName)]
    internal static partial int Wgc_CaptureFrame(nint handle, nint hwnd,
        out nint outBits, out int outWidth, out int outHeight, out int outStride);

    [LibraryImport(DllName)]
    internal static partial void Wgc_FreeFrame(nint handle, nint bits);

    /// <summary>取最近一次失败的 UTF-8 诊断文本（写入调用方缓冲，NUL 结尾）。</summary>
    [LibraryImport(DllName)]
    internal static unsafe partial void Wgc_LastError(nint handle, byte* buffer, int bufferSize);
}

/// <summary>
/// 抓屏**主线路**：Windows Graphics Capture（经原生库 <c>3FC.WgcCapture.dll</c>）。
///
/// <para>为什么走原生 DLL 而不是托管 WinRT 投影：宿主是 NativeAOT，WinRT 投影有被裁剪的运行时风险，
/// 且项目没有 D3D11 绑定；纯 C ABI 对 AOT 完全友好（docs/27 §一 决策 D1）。</para>
///
/// <para>实测能力（docs/26 §八）：能抓到 D3D11 flip-model swapchain 内容；**被不透明窗口完全遮挡时
/// 结果与无遮挡一致** —— 这正是 GDI 兜底做不到的。</para>
///
/// <para><b>会话生命周期</b>：本类只维护**一个**原生 handle。原生库允许同一 handle 服务不同 hwnd
/// （hwnd 变更时它内部自动销毁旧会话并重建，见 wgc_capture.h），因此单 handle 即可覆盖
/// "2~4 路视频 + 导出时切换目标窗口"的场景，同时把 D3D11 设备数量压在 1 个。
/// 代价是切换目标窗口时要付一次会话重建（原生侧会丢弃重建后的前 2 帧）。</para>
///
/// <para><b>并发</b>：整体加锁。原生侧本就对同一 handle 串行化，这里再串一次是为了让
/// "抓帧 → 取错误文本"成对原子（否则两个线程的 LastError 会互相覆盖），代价可接受。</para>
/// </summary>
internal sealed class WgcFrameCapture : IFrameCapture
{
    /// <summary>与 GDI 线一致的尺寸上界，防止原生侧异常返回导致巨额分配。</summary>
    private const int MaxDimension = 8192;

    private readonly object _gate = new();
    private nint _handle;
    private bool _disposed;

    /// <summary>被**有意跳过**的 <see cref="WgcNative.Wgc_FreeFrame"/> 次数。
    ///
    /// <para>来源见 <see cref="Capture"/> 的 <c>finally</c>：handle 一旦被 <see cref="ResetHandleLocked"/>
    /// 复位，手上这一帧的原生缓冲就<b>不再释放</b> —— 这是**有界**的：只影响"handle 复位那一刻
    /// 仍在手上"的那几帧（正常路径下该分支不可达，计数恒为 0），下次 <see cref="Capture"/> 会重建
    /// handle，之后的帧照常释放。</para>
    ///
    /// <para><b>用途</b>：排查"显存 / 原生缓冲持续增长"时先看这个数字。它在涨 ⇒ 是 handle 复位路径
    /// 被频繁走到（WGC 反复中毒重建），而不是普通抓帧漏放；它恒为 0 ⇒ 与本路径无关，去别处找。</para>
    ///
    /// <para>只在 <see cref="_gate"/> 内读写，无需原子操作。</para></summary>
    private long _skippedFreeFrameCount;

    /// <summary>诊断用只读快照：见 <see cref="_skippedFreeFrameCount"/>（挂调试器/watch 即可看到）。</summary>
    public long SkippedFreeFrameCount
    {
        get { lock (_gate) return _skippedFreeFrameCount; }
    }

    /// <summary>路径标识固定为 WGC。</summary>
    public CaptureRoute Route => CaptureRoute.Wgc;

    /// <summary>最近一次失败的原生错误码（成功时为 <see cref="WgcNative.Ok"/>）。</summary>
    public int LastErrorCode { get; private set; }

    /// <summary>最近一次失败的原生诊断文本（<c>Wgc_LastError</c>，UTF-8）。</summary>
    public string LastErrorMessage { get; private set; } = string.Empty;

    /// <summary>
    /// 抓取一帧。任何失败（含创建句柄失败）都返回 <c>null</c>，由 <see cref="FrameCapture"/> 决定回退。
    /// </summary>
    public Bitmap? Capture(nint hwnd)
    {
        if (hwnd == 0) return null;

        lock (_gate)
        {
            if (_disposed) return null;
            if (!EnsureCreated()) return null;

            nint bits = 0;
            try
            {
                var rc = WgcNative.Wgc_CaptureFrame(_handle, hwnd,
                    out bits, out var width, out var height, out var stride);

                if (rc != WgcNative.Ok)
                {
                    // 必须在 RecordFailure **之后**再复位：RecordFailure 要用当前 handle
                    // 去读 Wgc_LastError，handle 一置 0 就读不到诊断文本了。
                    RecordFailure(rc);

                    // docs/41（W2 复核）：handle 因工作线程超时被放弃后**永久失效**，
                    // 此后每次抓帧都立刻返回 ErrHandleDead。若不重建，WGC 会在**本进程内永久
                    // 降级到 GDI**——即便用户重开媒体（ResetRouting 只清计数）也回不来，
                    // 只能重启应用。也就是说 W2 的"不再冻死"会退化成"静默永久降级"。
                    // 对失效 handle 调 Wgc_Destroy 是安全且有界的（原生侧不 join 已卡死的线程），
                    // 所以这里可以无条件先销毁、下次调用自然重建（Wgc_Create 很便宜）。
                    if (rc == WgcNative.ErrHandleDead)
                    {
                        AppLog.Warn("Capture",
                            $"WGC handle 已失效（{LastErrorMessage}），销毁并重建捕获器");
                        ResetHandleLocked();
                    }
                    return null;
                }

                // 原生侧契约：成功时 stride == width * 4、行序自上而下、字节序 B,G,R,A。
                if (bits == 0 || width <= 0 || height <= 0
                    || width > MaxDimension || height > MaxDimension
                    || stride < (long)width * 4)
                {
                    LastErrorCode = WgcNative.ErrInternal;
                    LastErrorMessage = $"原生返回的帧描述非法（bits=0x{bits:X}, {width}x{height}, stride={stride}）";
                    AppLog.Warn("Capture", $"WGC 返回非法帧描述：{LastErrorMessage}");
                    return null;
                }

                var bmp = CopyToBitmap(bits, width, height, stride);
                if (bmp is null)
                {
                    LastErrorCode = WgcNative.ErrInternal;
                    LastErrorMessage = "像素拷贝到 Bitmap 失败";
                }
                return bmp;
            }
            catch (Exception ex)
            {
                // 只可能是托管侧异常（原生错误都以返回码表达）。不让它冒泡到 UI/抓帧线程。
                LastErrorCode = WgcNative.ErrInternal;
                LastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                AppLog.Error("Capture", ex);
                return null;
            }
            finally
            {
                // 单一释放点：成功与失败都不会漏掉原生缓冲（失败时 bits 按契约为 NULL，此处为安全空操作）。
                //
                // ⚠ handle 已复位（_handle == 0）时**刻意跳过**释放，不要"顺手修成"无条件 Wgc_FreeFrame：
                // handle 是这一帧缓冲的归属凭证 —— 原生头文件明确把它保留给"将来按 handle 做缓冲池化"
                // （现实现恰好不读它）。_handle 为 0 意味着该 handle 已经走过 Wgc_Destroy，
                // 再把一个已销毁的 handle 递回原生侧就是 use-after-free。两害相权：
                // **有意漏掉这一次释放**（有界，见下）远好过 UAF。
                //   有界性：本分支只可能命中"handle 复位那一刻仍在手上"的极少数帧，正常路径下不可达
                //   （复位点都在 bits 尚未持有或已按契约返回 NULL 的路径上）；下次 Capture 会重建 handle，
                //   之后所有帧照常释放。因此**不修**，只计数（_skippedFreeFrameCount / SkippedFreeFrameCount）：
                //   排查显存增长时，这个数字在不在涨，直接区分"复位路径被频繁走到"与"普通抓帧漏放"。
                if (bits != 0)
                {
                    if (_handle == 0)
                    {
                        _skippedFreeFrameCount++;
                        AppLog.Debug("Capture",
                            $"handle 已复位但仍收到帧缓冲，跳过 Wgc_FreeFrame（累计 {_skippedFreeFrameCount} 次，有界泄漏）");
                    }
                    else
                    {
                        try { WgcNative.Wgc_FreeFrame(_handle, bits); }
                        catch (Exception ex) { AppLog.Debug("Capture", $"Wgc_FreeFrame 失败：{ex.Message}"); }
                    }
                }
            }
        }
    }

    /// <summary>释放原生捕获器（关闭会话、回收 MTA 工作线程与 D3D11 设备）。</summary>
    public void Dispose()
    {
        nint handle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
            _handle = 0;
        }

        if (handle == 0) return;
        try { WgcNative.Wgc_Destroy(handle); }
        catch (Exception ex) { AppLog.Debug("Capture", $"Wgc_Destroy 失败：{ex.Message}"); }
    }

    /// <summary>懒创建原生 handle。失败返回 false（调用方按一次 WGC 失败计数）。</summary>
    private bool EnsureCreated()
    {
        if (_handle != 0) return true;

        try
        {
            _handle = WgcNative.Wgc_Create();
        }
        catch (Exception ex)
        {
            _handle = 0;
            LastErrorCode = WgcNative.ErrCreateFailed;
            LastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Debug("Capture", $"Wgc_Create 抛异常：{LastErrorMessage}");
            return false;
        }

        if (_handle == 0)
        {
            LastErrorCode = WgcNative.ErrCreateFailed;
            LastErrorMessage = "Wgc_Create 返回 NULL";
            return false;
        }

        LastErrorCode = WgcNative.Ok;
        LastErrorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// 丢弃当前 handle 并把状态复位到"未创建"（下次 <see cref="Capture"/> 会重新
    /// <see cref="WgcNative.Wgc_Create"/>）。
    /// </summary>
    /// <para>与 <see cref="Dispose"/> 的区别：**不**置 <c>_disposed</c>，因此本对象仍可继续使用
    /// —— 这正是"handle 中毒后自愈"需要的语义；若走 Dispose，WGC 会被永久关闭。
    /// 调用方须已持有 <see cref="_gate"/>。</para>
    private void ResetHandleLocked()
    {
        var handle = _handle;
        _handle = 0;
        if (handle == 0) return;
        try { WgcNative.Wgc_Destroy(handle); }
        catch (Exception ex)
        {
            AppLog.Debug("Capture", $"重置 handle 时 Wgc_Destroy 失败：{ex.Message}");
        }
    }

    private void RecordFailure(int rc)
    {
        LastErrorCode = rc;
        LastErrorMessage = ReadLastError();
    }

    /// <summary>读取 <c>Wgc_LastError</c>（UTF-8，NUL 结尾）。失败时返回空串。</summary>
    private unsafe string ReadLastError()
    {
        if (_handle == 0) return string.Empty;
        try
        {
            Span<byte> buffer = stackalloc byte[512];
            fixed (byte* p = buffer)
            {
                WgcNative.Wgc_LastError(_handle, p, buffer.Length);
            }
            var end = buffer.IndexOf((byte)0);
            return end <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer[..end]);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 把原生缓冲逐行拷进 GDI+ 位图。
    ///
    /// <para><b>行序</b>：原生输出是**自上而下**（wgc_capture.h 明确写死），
    /// 而 <c>new Bitmap(w, h, Format32bppArgb)</c> 经 LockBits 得到的也是自上而下
    /// （Scan0 + y*Stride 就是第 y 行）。两侧同向 ⇒ **直接按行拷贝，不要做任何上下翻转**。
    /// 这与 GDI DIB 的默认自下而上不同，混用会让画面倒过来。</para>
    ///
    /// <para>字节序：原生是 B,G,R,A，正是 Format32bppArgb 在内存中的顺序 ⇒ 无需通道交换。</para>
    /// </summary>
    /// <remarks><c>internal</c>（而非 private）仅为可测试性：这是 WGC 输出进入托管位图的**唯一**转换点，
    /// 行序 / stride 处理错了会静默产出错帧，而它本身不碰原生库，可在单测里用合成缓冲直接验证。</remarks>
    internal static unsafe Bitmap? CopyToBitmap(nint source, int width, int height, int sourceStride)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var src = (byte*)source;
                var dst = (byte*)data.Scan0;
                var rowBytes = (long)width * 4;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * sourceStride, dst + (long)y * data.Stride,
                        data.Stride, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            AppLog.Error("Capture", ex);
            return null;
        }
    }
}
