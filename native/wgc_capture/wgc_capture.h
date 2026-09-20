/*
 * wgc_capture.h — 3FCompare 原生 Windows Graphics Capture (WGC) 抓屏 DLL 的公开 C ABI。
 *
 * 为什么是原生 DLL：
 *   宿主 3FCompare 是 Avalonia + .NET 11 且 `PublishAot=true`。WGC 是 WinRT API，
 *   AOT 下需要 CsWinRT 投影并显式保留类型（失败模式只在运行时暴露），且项目没有 D3D11 绑定。
 *   因此改为「原生 DLL 暴露纯 C ABI + C# P/Invoke」——纯 C ABI 对 NativeAOT 完全友好，
 *   无任何托管投影/裁剪风险。
 *
 * 调用约定：
 *   全部导出函数使用 **__cdecl**（宏 WGC_CALL）。本 DLL 只构建 x64，而 x64 上
 *   __cdecl / __stdcall / __fastcall 已统一为同一种调用约定（参数走 RCX/RDX/R8/R9 + 栈），
 *   所以 C# 侧 `[DllImport]` 用默认的 CallingConvention.Winapi 或显式 Cdecl 都能正确调用。
 *   导出名**未修饰**（extern "C" + __cdecl），可直接 GetProcAddress("Wgc_Create")。
 *
 * 内存所有权：
 *   Wgc_CaptureFrame 输出的像素缓冲由本 DLL 用 CoTaskMemAlloc 分配，
 *   调用方**必须**用 Wgc_FreeFrame 释放（不要用 free / Marshal.FreeHGlobal /
 *   Marshal.FreeCoTaskMem，虽然底层同为 CoTaskMemFree，但请走 Wgc_FreeFrame 以保证一致）。
 */

#ifndef WGC_CAPTURE_H
#define WGC_CAPTURE_H

#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------ */
/* 导出/导入与调用约定                                                  */
/* ------------------------------------------------------------------ */

#if defined(_WIN32)
#  if defined(WGC_CAPTURE_BUILD)
#    define WGC_API __declspec(dllexport)   /* 编译本 DLL 时 */
#  elif defined(WGC_CAPTURE_DYNAMIC)
#    define WGC_API                         /* 用 LoadLibrary/GetProcAddress 动态解析时不声明导入 */
#  else
#    define WGC_API __declspec(dllimport)   /* 默认：按导入库链接 */
#  endif
#  define WGC_CALL __cdecl
#else
#  define WGC_API
#  define WGC_CALL
#endif

/* ------------------------------------------------------------------ */
/* 错误码                                                              */
/* ------------------------------------------------------------------ */

/* 全部错误码均为「成功 = 0，失败 = 非 0」，可直接当 HRESULT 之外的简单判定使用。
 * 实现按**失败阶段**返回对应码（与托管侧 CaptureError.Describe 的分支一一对应）。 */
#define WGC_OK                    0  /* 成功 */
#define WGC_ERR_NOT_SUPPORTED     1  /* 系统不支持 WGC（GraphicsCaptureSession::IsSupported() == false） */
#define WGC_ERR_CREATE_FAILED     2  /* 创建捕获器 / 捕获会话失败（D3D11CreateDevice、CreateForWindow、帧池、StartCapture 等） */
#define WGC_ERR_TIMEOUT           3  /* 在超时预算（约 2 秒）内未取到有效帧 */
#define WGC_ERR_NOT_CAPTURABLE    4  /* 窗口不可捕获：hwnd 无效 / 已最小化 / 客户区尺寸为 0 / 裁剪结果为空 */
#define WGC_ERR_TEXTURE_READ      5  /* 从捕获帧取 D3D11 纹理或 CPU 读回（staging/CopyResource/Map）失败 */
#define WGC_ERR_INVALID_ARG       6  /* 传入参数非法（空指针等） */
#define WGC_ERR_INTERNAL          7  /* 其它内部错误（内存分配失败等），细节见 Wgc_LastError */

/* ------------------------------------------------------------------ */
/* 公开函数                                                            */
/* ------------------------------------------------------------------ */

/*
 * 查询当前系统是否支持 Windows Graphics Capture。
 * 等价于 GraphicsCaptureSession::IsSupported()。无需先创建 handle，可从任意线程调用。
 * 返回：1 = 支持；0 = 不支持（或查询本身失败）。
 */
WGC_API int WGC_CALL Wgc_IsSupported(void);

/*
 * 创建捕获器（每路视频窗口一个，内部持有独立的 D3D11 设备与 MTA 工作线程）。
 * 返回：成功返回非空 handle；失败返回 NULL。
 * 注意：必须在同一进程内成对调用 Wgc_Destroy。
 */
WGC_API void* WGC_CALL Wgc_Create(void);

/*
 * 销毁捕获器：关闭捕获会话、释放帧池/纹理/设备并回收工作线程。传入 NULL 安全（空操作）。
 * 调用返回后 handle 立即失效，不得再用于任何其它函数。
 */
WGC_API void WGC_CALL Wgc_Destroy(void* handle);

/*
 * 抓取一帧。
 *
 * 参数：
 *   handle     : Wgc_Create 返回的句柄。
 *   hwnd       : 目标窗口句柄（Win32 HWND，以 void* 传入）。可以是顶层窗口，也可以是子窗口。
 *   outBits    : [出] 像素缓冲首地址，由本 DLL 分配，调用方用 Wgc_FreeFrame 释放。
 *   outWidth   : [出] 像素宽度。
 *   outHeight  : [出] 像素高度。
 *   outStride  : [出] 每行字节数（本实现为紧凑排列，恒等于 outWidth * 4）。
 *
 * 像素格式（重要）：
 *   32 位/像素、**每像素 4 字节**。内存中的字节序固定为 **B, G, R, A**
 *   （等价于小端 DWORD 0xAARRGGBB，即通常说的 BGRA / "RGBA32"）。
 *   这正是 D3D11 捕获面 DXGI_FORMAT_B8G8R8A8_UNORM 的原生字节序，也是
 *   GDI 32bpp BI_RGB DIB、System.Drawing.Format32bppArgb、Avalonia Bgra8888 的字节序，
 *   因此调用方可以零拷贝直接使用，无需通道交换。
 *   ⚠ 契约里的 "RGBA32" 指的是「4 字节/像素的 32 位格式」，**不是** R,G,B,A 字节序。
 *
 * 行序（重要）：
 *   输出为 **自上而下（top-down）**：outBits + y * outStride 就是第 y 行（y=0 是最上面一行）。
 *   ⚠ 这与 GDI DIB 默认的**自下而上（bottom-up）**相反，调用方按 Bitmap 使用时
 *   不需要再做上下翻转（不要再加负高度/翻转逻辑，否则画面会倒过来）。
 *
 * 语义：
 *   - 首次调用会为该 hwnd 建立捕获会话；后续重复调用**复用**同一会话（不重建）。
 *   - 若 hwnd 与上次不同，则自动销毁旧会话并为新 hwnd 重建。
 *   - ⚠ **子窗口支持**：WGC 的 CreateForWindow **不接受子窗口**（WS_CHILD 会直接返回
 *     E_INVALIDARG "Could not capture the given window"，实测）。而 3FCompare 的视频画面
 *     正是父窗口下的子 HWND，因此本 DLL 会自动退化为：
 *       「捕获该 hwnd 的**顶层祖先**（GetAncestor(hwnd, GA_ROOT)）→ 裁剪到 hwnd 的客户区」。
 *     此时 outWidth/outHeight/outStride 描述的是**子窗口客户区**裁剪结果，
 *     调用方无需关心，直接把 hwnd 传进来即可。传入顶层窗口时行为不变（裁剪=全幅）。
 *     副作用：子窗口被同级窗口遮挡时，遮挡物会出现在捕获结果里（因为捕获的是合成后的顶层窗口）。
 *   - **陈旧帧判定**：会话建立时取一个时间锚（StartCapture 之后的 QPC 读数），
 *     帧自带的 Direct3D11CaptureFrame::SystemRelativeTime() 与它同基准，因此
 *     「时间戳 < 锚」的帧被判定为「会话开始前就已合成」，**不会作为可信帧返回**。
 *     本实现**不再**「盲丢新建会话后的前 2 帧」——那会在「会话刚建立 + 窗口静止只出 1 帧」时
 *     把唯一一帧丢掉、再把它当成功帧返回（自相矛盾）。
 *   - **静止内容友好**：若在超时窗口内没等到「新」帧，则返回**跨会话缓存**——上一次成功
 *     返回给调用方的帧（内容本来就没变，它就是当前内容），**不**报超时。缓存只在 hwnd 变化时
 *     失效；同一个 hwnd 重建会话（最小化还原 / 上次 NOT_CAPTURABLE）时保留。
 *     只有「从未拿到任何可信帧」时才返回 WGC_ERR_TIMEOUT。因此「暂停 → 逐帧对齐 → 截图」
 *     这类窗口不重绘的场景不会退回 GDI 兜底线。
 *   - 窗口最小化 / 客户区尺寸为 0 时返回 WGC_ERR_NOT_CAPTURABLE（最小化时的黑帧不会被当成成功）。
 *   - 目标窗口被宿主销毁（DestroyWindow）是安全的：DLL 发现窗口已消失后**放弃**该会话，
 *     不对它做 teardown（对已销毁窗口的捕获会话做 Close()/Release() 会永久死锁在系统捕获
 *     服务里，实测 ≥150s 不返回），因此抓帧调用不会因此阻塞；下一次对有效 hwnd 的调用会
 *     正常建立新会话。
 *   - 单次调用最多等待约 2 秒（WGC_TIMEOUT_MS；已有可兜底帧时约 0.4 秒），不会死等。
 *   - 同一 handle 的并发调用会被内部串行化（第二个调用阻塞到第一个返回）；
 *     不同 handle 之间完全独立、互不干扰。
 *     ⚠ 唯一例外：不得与 Wgc_Destroy 并发——handle 一旦传入 Wgc_Destroy 即失效。
 *
 * 线程/公寓：
 *   本 DLL 内部为每个 handle 维护一条**专属 MTA 工作线程**，所有 WinRT/WGC 调用都在该线程上
 *   执行，因此调用方线程是 MTA / STA / 未初始化 COM 都不影响，也无需消息泵。
 *
 * 返回：WGC_OK(0) 成功；其余见上面错误码表。
 *       失败时 *outBits 为 NULL，*outWidth/*outHeight/*outStride 均为 0。
 */
WGC_API int WGC_CALL Wgc_CaptureFrame(void* handle, void* hwnd,
                                      unsigned char** outBits,
                                      int* outWidth, int* outHeight, int* outStride);

/*
 * 释放 Wgc_CaptureFrame 返回的像素缓冲。传入 NULL 安全（空操作）。
 * handle 参数当前实现不使用（保留以便将来按 handle 做缓冲池化），可为任意值。
 */
WGC_API void WGC_CALL Wgc_FreeFrame(void* handle, unsigned char* bits);

/*
 * 取「最近一次失败的诊断信息」，UTF-8 编码，以 NUL 结尾。
 *   - buffer 由调用方提供；bufferSize 为其字节数（含结尾 NUL 的位置）。
 *   - 若没有错误，或 handle 为 NULL / buffer 非法，则写入空串（buffer[0] = 0）。
 *   - 成功调用 Wgc_CaptureFrame 会清空上一次的错误信息。
 */
WGC_API void WGC_CALL Wgc_LastError(void* handle, char* buffer, int bufferSize);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* WGC_CAPTURE_H */
