/*
 * selftest.cpp — 3FC.WgcCapture.dll 自测程序。
 *
 * 目的：复刻 .review_pr/wgc_probe.cpp 的做法，端到端验证本 DLL 的 C ABI 是否可用：
 *   1) 用 LoadLibrary + GetProcAddress **动态解析**全部 6 个导出（与 C# P/Invoke 的解析方式一致，
 *      可顺带验证导出名未被 C++ 修饰）；
 *   2) 建立真实的 D3D11 flip-model swapchain 窗口（严格复刻 FFF.Native VideoRenderer.cpp:2229-2233），
 *      用本 DLL 抓帧，断言像素精确等于清屏色；
 *   3) 覆盖遮挡测试（不透明绿窗完全盖住目标窗）——这是 GDI 回退做不到的；
 *   4) **子窗口**测试（3FCompare 真实形态：视频是父窗口下的子 HWND）；
 *   5) 行序（top-down）验证：往 backbuffer 下部拷一条绿色横带，检查抓回来的 y 范围；
 *   6) 负例：NULL hwnd / 非法 hwnd 的错误码。
 *   7) **静态内容**抓帧（画一次后不再重绘）：断言能成功返回帧且颜色正确，守住
 *      「窗口不重绘时不得退回 TIMEOUT / GDI 兜底线」这条修复。
 *   8) **B1 回归**：会话重建（最小化还原）+ 静态窗口 —— 断言不返回旧会话内容；
 *      「新建会话不得盲等热身期」则改在**持续 Present 的窗口**上断言（静止窗口何时出帧
 *      不受 DLL 控制，用墙钟去卡它只会量到环境抖动，见用例 [9] 注释）。
 *   9) **B4 回归**：同一 handle 的多线程并发抓帧必须全部成功（内部串行化）。
 *
 * 注意：多数用例在抓帧期间**持续 Present**（否则 WGC 通常不产出新帧）。因为 Wgc_CaptureFrame
 * 是阻塞调用，所以 Present 放在独立线程上（这也与真实宿主一致：FFF 内核在自己的线程上 Present）。
 * 只有第 8 个用例刻意不持续 Present——它模拟的正是「暂停态」这一最需要高质量抓屏的场景。
 *
 * ---------------------------------------------------------------------------
 * 取证能力（为定位间歇性失败而加，2026-09-19）
 * ---------------------------------------------------------------------------
 *   --repeat N   在同一进程内把整套用例跑 N 遍（暴露跨用例/跨迭代的状态残留）。
 *                每遍的 stdout 先写入临时文件，失败则改名为 selftest_fail_<时间戳>_iter<N>.log，
 *                成功则删除；同时往 stderr 打一行「通过/失败 + 关键耗时 + OSD 探测」便于统计。
 *   --stop-on-fail  首遍失败即停。
 *   --help
 *
 *   每条断言都打印 `[PASS]/[FAIL] <用例名> :: <期望/实得描述>`，因此失败时能直接看出
 *   「哪一项、实得多少」。VerifyFrame 另外打印全帧主色直方图与左上「OSD 易污染区」统计。
 *
 * 构建：见 build.sh（会自动编译 DLL 与本程序并运行）。
 */

#define WGC_CAPTURE_DYNAMIC  // 只用 GetProcAddress，不声明 dllimport
#define _CRT_SECURE_NO_WARNINGS
#include "wgc_capture.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <fcntl.h>
#include <io.h>
#include <sys/stat.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "dwmapi.lib")

/* ---------------- 动态解析出的函数指针（签名与 wgc_capture.h 严格一致） ---------------- */
typedef int(WGC_CALL* PFN_Wgc_IsSupported)(void);
typedef void*(WGC_CALL* PFN_Wgc_Create)(void);
typedef void(WGC_CALL* PFN_Wgc_Destroy)(void*);
typedef int(WGC_CALL* PFN_Wgc_CaptureFrame)(void*, void*, unsigned char**, int*, int*, int*);
typedef void(WGC_CALL* PFN_Wgc_FreeFrame)(void*, unsigned char*);
typedef void(WGC_CALL* PFN_Wgc_LastError)(void*, char*, int);

static PFN_Wgc_IsSupported pIsSupported = nullptr;
static PFN_Wgc_Create pCreate = nullptr;
static PFN_Wgc_Destroy pDestroy = nullptr;
static PFN_Wgc_CaptureFrame pCaptureFrame = nullptr;
static PFN_Wgc_FreeFrame pFreeFrame = nullptr;
static PFN_Wgc_LastError pLastError = nullptr;

/* ---------------- 测试参数 ---------------- */
static const int kW = 400;
static const int kH = 300;
static const int kMarkerY = 200;   // 绿色横带的起始 y
static const int kMarkerH = 24;    // 横带高度
static const int kStripeX = 100;   // 品红竖带起始 x（用来检测**水平**裁剪偏移）
static const int kStripeW = 24;    // 竖带宽度
static const int kProbeX = 300;    // 采样列：靠右侧，避开本机左上角的 RTSS/Afterburner OSD
static const int kTopY = 60;       // 上部采样行（应=清屏色）
static const int kBandY = kMarkerY + kMarkerH / 2;  // 带内采样行（应=标记色）
static const int kBotY = 280;      // 下部采样行（应=清屏色）

/* ---------------- 全局 D3D 设备 ---------------- */
static ID3D11Device* g_dev = nullptr;
static ID3D11DeviceContext* g_ctx = nullptr;
static ID3D11Texture2D* g_marker = nullptr;  // 绿色标记带纹理
static ID3D11Texture2D* g_stripe = nullptr;  // 品红竖带纹理

/* ---------------- 结果与取证 ---------------- */
static int g_fail = 0;
static int g_pass = 0;
static const char* g_case = "(未命名)";          // 当前用例名（CHECK 会带上）
static std::vector<std::string> g_failures;      // 本遍失败明细（用例名 + 期望/实得 + 位置）
static HMODULE g_dll = nullptr;

// 关键耗时（ms），-1 = 本遍未走到。供 stderr 汇总行做分布统计。
static int g_msFirstCapture = -1;   // [4] 首次抓帧
static int g_msStatic1 = -1;        // [8] 静态窗口第 1 次
static int g_msStatic2 = -1;        // [8] 静态窗口第 2 次
static int g_msB1Rebuild = -1;      // [9] 会话重建（持续 Present 窗口）

#define CHECK(cond, ...)                                                          \
    do {                                                                          \
        char _m[1024];                                                            \
        std::snprintf(_m, sizeof(_m), __VA_ARGS__);                               \
        if (cond) {                                                               \
            ++g_pass;                                                             \
            std::printf("    [PASS] %s :: %s\n", g_case, _m);                     \
        } else {                                                                  \
            ++g_fail;                                                             \
            std::printf("    [FAIL] %s :: %s   (%s:%d)\n", g_case, _m, __FILE__,  \
                        __LINE__);                                                \
            char _l[1200];                                                        \
            std::snprintf(_l, sizeof(_l), "%s :: %s   (%s:%d)", g_case, _m,       \
                          __FILE__, __LINE__);                                    \
            g_failures.push_back(_l);                                             \
        }                                                                         \
    } while (0)

static void Case(const char* name) {
    g_case = name;
    std::printf("\n[%s]\n", name);
}

/* ---------------- OSD / 注入探测（只检测，绝不结束用户进程） ---------------- */
static std::string OsdProbe() {
    // 注意：本 SDK 的 tlhelp32.h 只提供 W 版（无 PROCESSENTRY32A）。
    static const wchar_t* kNames[] = {L"RTSS.exe", L"RTSSHooksLoader64.exe", L"MSIAfterburner.exe",
                                      L"RTSSHooksLoader.exe"};
    constexpr int kN = static_cast<int>(sizeof(kNames) / sizeof(kNames[0]));
    bool found[kN] = {};
    HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W pe{};
        pe.dwSize = sizeof(pe);
        if (::Process32FirstW(snap, &pe)) {
            do {
                for (int i = 0; i < kN; ++i) {
                    if (::_wcsicmp(pe.szExeFile, kNames[i]) == 0) found[i] = true;
                }
            } while (::Process32NextW(snap, &pe));
        }
        ::CloseHandle(snap);
    }
    std::string s;
    for (int i = 0; i < kN; ++i) {
        if (i) s += " ";
        for (const wchar_t* p = kNames[i]; *p; ++p) s += static_cast<char>(*p);  // 名字均为 ASCII
        s += (found[i] ? "=运行" : "=无");
    }
    return s;
}

/* ---------------- 迭代日志捕获（失败时落盘完整输出） ---------------- */
struct LogCapture {
    int saved = -1;

    bool Begin(const std::string& path) {
        std::fflush(stdout);
        saved = ::_dup(1);
        if (saved < 0) return false;
        const int fd = ::_open(path.c_str(), _O_CREAT | _O_TRUNC | _O_WRONLY | _O_TEXT,
                               _S_IREAD | _S_IWRITE);
        if (fd < 0) { ::_close(saved); saved = -1; return false; }
        ::_dup2(fd, 1);
        ::_close(fd);
        return true;
    }
    void End() {
        std::fflush(stdout);
        if (saved >= 0) { ::_dup2(saved, 1); ::_close(saved); saved = -1; }
    }
};

static std::string Timestamp() {
    SYSTEMTIME st{};
    ::GetLocalTime(&st);
    char b[32];
    std::snprintf(b, sizeof(b), "%04d%02d%02d_%02d%02d%02d", st.wYear, st.wMonth, st.wDay, st.wHour,
                  st.wMinute, st.wSecond);
    return b;
}

// 把某一遍的完整输出回显到控制台（stdout 已被重定向到日志文件，这里补一次显示）。
static void EchoFile(const std::string& path) {
    std::FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return;
    char buf[8192];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) std::fwrite(buf, 1, n, stdout);
    std::fclose(f);
    std::fflush(stdout);
}

/* ---------------- 窗口 ---------------- */
static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_ERASEBKGND: return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(h, &ps);
        RECT r;
        GetClientRect(h, &r);
        FillRect(dc, &r, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        EndPaint(h, &ps);
        return 0;
    }
    default: return DefWindowProcA(h, m, w, l);
    }
}

static LRESULT CALLBACK CoverProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(h, &ps);
        RECT r;
        GetClientRect(h, &r);
        HBRUSH br = CreateSolidBrush(RGB(0, 255, 0));
        FillRect(dc, &r, br);
        DeleteObject(br);
        EndPaint(h, &ps);
        return 0;
    }
    default: return DefWindowProcA(h, m, w, l);
    }
}

static void Pump() {
    MSG m;
    while (PeekMessageA(&m, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&m);
        DispatchMessageA(&m);
    }
}

/* ---------------- flip-model 测试窗口 ---------------- */
struct TestWindow {
    HWND hwnd = nullptr;
    IDXGISwapChain1* chain = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    UINT w = 0, h = 0;
    float cr = 1.f, cg = 0.f, cb = 0.f;  // 清屏色
};

// 严格复刻 VideoRenderer.cpp:2229-2233 的 swapchain 配置
static bool CreateFlipWindow(TestWindow& tw, HWND parent, int x, int y, float r, float g, float b,
                             const char* title) {
    static int seq = 0;
    char cls[64];
    std::snprintf(cls, sizeof(cls), "WgcSelfTestWnd%d", ++seq);

    WNDCLASSA wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleA(nullptr);
    wc.lpszClassName = cls;
    wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    RegisterClassA(&wc);

    const DWORD style = parent ? (WS_CHILD | WS_VISIBLE) : (WS_POPUP | WS_VISIBLE);
    tw.hwnd = CreateWindowExA(0, cls, title, style, x, y, kW, kH, parent, nullptr, wc.hInstance,
                              nullptr);
    if (!tw.hwnd) { std::printf("    FATAL: CreateWindowEx 失败\n"); return false; }

    RECT cr{};
    GetClientRect(tw.hwnd, &cr);
    tw.w = static_cast<UINT>(cr.right - cr.left);
    tw.h = static_cast<UINT>(cr.bottom - cr.top);
    tw.cr = r; tw.cg = g; tw.cb = b;
    if (!parent) UpdateWindow(tw.hwnd);

    IDXGIDevice* dxgiDev = nullptr;
    IDXGIAdapter* adapter = nullptr;
    IDXGIFactory2* factory = nullptr;
    if (FAILED(g_dev->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDev))) ||
        FAILED(dxgiDev->GetAdapter(&adapter)) ||
        FAILED(adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory)))) {
        std::printf("    FATAL: 取 IDXGIFactory2 失败\n");
        return false;
    }

    DXGI_SWAP_CHAIN_DESC1 d{};
    d.Width = tw.w;
    d.Height = tw.h;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.BufferCount = 2;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    d.Scaling = DXGI_SCALING_NONE;

    HRESULT hr = factory->CreateSwapChainForHwnd(g_dev, tw.hwnd, &d, nullptr, nullptr, &tw.chain);
    factory->Release();
    adapter->Release();
    dxgiDev->Release();
    if (FAILED(hr)) {
        std::printf("    FATAL: CreateSwapChainForHwnd 0x%08X\n", static_cast<unsigned>(hr));
        return false;
    }

    ID3D11Texture2D* back = nullptr;
    if (FAILED(tw.chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back))) ||
        FAILED(g_dev->CreateRenderTargetView(back, nullptr, &tw.rtv))) {
        std::printf("    FATAL: 取 backbuffer / 建 RTV 失败\n");
        return false;
    }
    back->Release();
    std::printf("    窗口 %-10s hwnd=%p client=%ux%u style=%s\n", title, tw.hwnd, tw.w, tw.h,
                parent ? "WS_CHILD" : "WS_POPUP");
    return true;
}

// 普通窗口（无 swapchain），用作子窗口的父窗口
static HWND CreatePlainWindow(const char* cls, const char* title, DWORD style, int x, int y,
                              int w, int h, HWND parent) {
    WNDCLASSA wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleA(nullptr);
    wc.lpszClassName = cls;
    wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    RegisterClassA(&wc);
    return CreateWindowExA(0, cls, title, style, x, y, w, h, parent, nullptr, wc.hInstance, nullptr);
}

// 一帧：清屏 → 拷入绿色横带 → 拷入品红竖带 → Present
static void RenderOnce(const TestWindow& tw) {
    const float c[4] = {tw.cr, tw.cg, tw.cb, 1.f};
    g_ctx->OMSetRenderTargets(1, &tw.rtv, nullptr);
    g_ctx->ClearRenderTargetView(tw.rtv, c);
    ID3D11Texture2D* back = nullptr;
    if (SUCCEEDED(tw.chain->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                      reinterpret_cast<void**>(&back)))) {
        if (g_marker) g_ctx->CopySubresourceRegion(back, 0, 0, kMarkerY, 0, g_marker, 0, nullptr);
        if (g_stripe) g_ctx->CopySubresourceRegion(back, 0, kStripeX, 0, 0, g_stripe, 0, nullptr);
        back->Release();
    }
    tw.chain->Present(1, 0);
}

/* ---------------- 持续 Present 线程 ---------------- */
static std::atomic<bool> g_presenting{false};
static std::thread g_presentThread;

// 多个窗口由**同一条** Present 线程轮流渲染：D3D11 立即上下文不是线程安全的，
// 多线程各自 RenderOnce 会数据竞争，所以必须串行。
static void StartPresentMany(const std::vector<TestWindow*>& tws) {
    g_presenting = true;
    g_presentThread = std::thread([tws] {
        while (g_presenting.load()) {
            for (auto* tw : tws) RenderOnce(*tw);
            Sleep(16);
        }
    });
}

static void StartPresent(TestWindow& tw) { StartPresentMany({&tw}); }

static void StopPresent() {
    g_presenting = false;
    if (g_presentThread.joinable()) g_presentThread.join();
}

/* ---------------- 抓帧封装 ---------------- */
struct Frame {
    std::vector<unsigned char> px;
    int w = 0, h = 0, stride = 0;
};

static int Capture(void* handle, HWND hwnd, Frame& out, std::string& errOut) {
    unsigned char* bits = nullptr;
    int w = 0, h = 0, st = 0;
    const int rc = pCaptureFrame(handle, hwnd, &bits, &w, &h, &st);
    if (rc != WGC_OK) {
        char buf[1024] = {0};
        pLastError(handle, buf, sizeof(buf));
        errOut = buf;
        return rc;
    }
    out.px.assign(bits, bits + static_cast<size_t>(st) * h);
    out.w = w; out.h = h; out.stride = st;
    pFreeFrame(handle, bits);
    return rc;
}

// 越界安全采样：几何异常（尺寸不是 400x300）时返回全 0，让后续颜色断言自然判红，
// 而不是越界读 vector（旧实现会按 kProbeX/kBotY 直接算偏移，帧偏小即 UB）。
static void PxAt(const Frame& f, int x, int y, unsigned char out[4]) {
    if (x < 0 || y < 0 || x >= f.w || y >= f.h || f.stride < f.w * 4) {
        out[0] = out[1] = out[2] = out[3] = 0;
        return;
    }
    const unsigned char* p = f.px.data() + static_cast<size_t>(y) * f.stride + x * 4;
    out[0] = p[0]; out[1] = p[1]; out[2] = p[2]; out[3] = p[3];  // B,G,R,A
}

static std::string RgbStr(const unsigned char p[4]) {
    char b[64];
    std::snprintf(b, sizeof(b), "RGB(%u,%u,%u)", p[2], p[1], p[0]);
    return b;
}

static bool IsColor(const unsigned char p[4], unsigned char r, unsigned char g, unsigned char bl) {
    return p[2] == r && p[1] == g && p[0] == bl;
}

/* 取证：全帧主色直方图 + 左上「OSD 易污染区」统计。
 * OSD（RTSS/Afterburner）会被注入 swapchain 并叠在帧的左上角，所以失败时这两个量能区分
 * 「整帧内容不对」与「只有左上被 OSD 压住」。 */
static void DumpFrameStats(const char* tag, const Frame& f, unsigned char cr, unsigned char cg,
                           unsigned char cb) {
    std::map<unsigned int, long> hist;
    const int step = 2;
    long sampled = 0;
    for (int y = 0; y < f.h; y += step) {
        const unsigned char* row = f.px.data() + static_cast<size_t>(y) * f.stride;
        for (int x = 0; x < f.w; x += step) {
            const unsigned int c = static_cast<unsigned>(row[x * 4]) |
                                   (static_cast<unsigned>(row[x * 4 + 1]) << 8) |
                                   (static_cast<unsigned>(row[x * 4 + 2]) << 16);
            ++hist[c];
            ++sampled;
        }
    }
    std::vector<std::pair<long, unsigned int>> v;
    v.reserve(hist.size());
    for (auto& kv : hist) v.emplace_back(kv.second, kv.first);
    const size_t topN = (std::min<size_t>)(3, v.size());
    std::partial_sort(v.begin(), v.begin() + topN, v.end(),
                      [](const std::pair<long, unsigned int>& a,
                         const std::pair<long, unsigned int>& b) { return a.first > b.first; });
    std::printf("     [取证] %s 主色(top3): ", tag);
    for (size_t i = 0; i < topN; ++i) {
        const unsigned int c = v[i].second;
        std::printf("RGB(%u,%u,%u)x%ld(%.1f%%) ", (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF,
                    v[i].first, sampled ? 100.0 * v[i].first / sampled : 0.0);
    }
    std::printf("\n");

    // 左上 200x120：OSD 最可能出现的位置。清屏色/绿带/品红带之外的像素即「异常像素」。
    const int ow = (f.w < 200) ? f.w : 200;
    const int oh = (f.h < 120) ? f.h : 120;
    long dirty = 0;
    std::map<unsigned int, long> dirtyHist;
    int dx0 = 1 << 30, dy0 = 1 << 30, dx1 = -1, dy1 = -1;
    for (int y = 0; y < oh; ++y) {
        const unsigned char* row = f.px.data() + static_cast<size_t>(y) * f.stride;
        for (int x = 0; x < ow; ++x) {
            const unsigned char* p = row + x * 4;
            const bool expected = (p[2] == cr && p[1] == cg && p[0] == cb) ||
                                  (p[2] == 0 && p[1] == 255 && p[0] == 0) ||
                                  (p[2] == 255 && p[1] == 0 && p[0] == 255);
            if (!expected) {
                ++dirty;
                const unsigned int c = static_cast<unsigned>(p[0]) |
                                       (static_cast<unsigned>(p[1]) << 8) |
                                       (static_cast<unsigned>(p[2]) << 16);
                ++dirtyHist[c];
                if (x < dx0) dx0 = x;
                if (y < dy0) dy0 = y;
                if (x > dx1) dx1 = x;
                if (y > dy1) dy1 = y;
            }
        }
    }
    std::printf("     [取证] %s 左上 %dx%d 区异常像素 = %ld/%d (%.2f%%) ← OSD 注入判据\n", tag, ow,
                oh, dirty, ow * oh, 100.0 * dirty / (ow * oh));
    if (dirty > 0) {
        std::vector<std::pair<long, unsigned int>> dv;
        for (auto& kv : dirtyHist) dv.emplace_back(kv.second, kv.first);
        const size_t dn = (std::min<size_t>)(3, dv.size());
        std::partial_sort(dv.begin(), dv.begin() + dn, dv.end(),
                          [](const std::pair<long, unsigned int>& a,
                             const std::pair<long, unsigned int>& b) { return a.first > b.first; });
        std::printf("     [取证] %s 异常区 bbox=[%d,%d..%d,%d] 主异常色: ", tag, dx0, dy0, dx1, dy1);
        for (size_t i = 0; i < dn; ++i) {
            const unsigned int c = dv[i].second;
            std::printf("RGB(%u,%u,%u)x%ld ", (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF,
                        dv[i].first);
        }
        std::printf("\n");
    }

    // 粗粒度字符图（8x8 块）：'.'=清屏色 'G'=绿 'M'=品红 '#'=其它 —— 一眼看出污染位置。
    std::printf("     [取证] %s 字符图（8x8 块，'.'清屏 'G'绿 'M'品红 '#'其它）:\n", tag);
    for (int by = 0; by < f.h; by += 8) {
        std::printf("       ");
        for (int bx = 0; bx < f.w; bx += 8) {
            int cntClear = 0, cntGreen = 0, cntMag = 0, cntOther = 0;
            for (int y = by; y < by + 8 && y < f.h; ++y) {
                const unsigned char* row = f.px.data() + static_cast<size_t>(y) * f.stride;
                for (int x = bx; x < bx + 8 && x < f.w; ++x) {
                    const unsigned char* p = row + x * 4;
                    if (p[2] == cr && p[1] == cg && p[0] == cb) ++cntClear;
                    else if (p[2] == 0 && p[1] == 255 && p[0] == 0) ++cntGreen;
                    else if (p[2] == 255 && p[1] == 0 && p[0] == 255) ++cntMag;
                    else ++cntOther;
                }
            }
            if (cntOther > 32) std::printf("#");
            else if (cntMag >= cntClear && cntMag >= cntGreen && cntMag > 16) std::printf("M");
            else if (cntGreen >= cntClear && cntGreen > 16) std::printf("G");
            else if (cntClear >= 32) std::printf(".");
            else std::printf("+");
        }
        std::printf("\n");
    }
}

/* ---------------- 通用断言组：尺寸 / 行序 / 三个采样点 ---------------- */
static void VerifyFrame(const char* tag, const Frame& f, unsigned char cr, unsigned char cg,
                        unsigned char cb) {
    std::printf("  %s: 抓回 %dx%d stride=%d\n", tag, f.w, f.h, f.stride);
    CHECK(f.w == kW && f.h == kH, "%s 尺寸应为 %dx%d，实得 %dx%d", tag, kW, kH, f.w, f.h);
    CHECK(f.stride == f.w * 4, "%s stride 应为 w*4=%d，实得 %d", tag, f.w * 4, f.stride);

    unsigned char top[4], band[4], bot[4];
    PxAt(f, kProbeX, kTopY, top);
    PxAt(f, kProbeX, kBandY, band);
    PxAt(f, kProbeX, kBotY, bot);
    std::printf("     (%d,%d)=%s  (%d,%d)=%s  (%d,%d)=%s\n", kProbeX, kTopY,
                RgbStr(top).c_str(), kProbeX, kBandY, RgbStr(band).c_str(), kProbeX, kBotY,
                RgbStr(bot).c_str());

    // 清屏色判据：上/下各取 **5 个采样点**，要求「≥4/5 精确匹配」。
    //
    // 为什么不是单点：本机 RTSS/Afterburner 的 OSD 被注入到 swapchain 里，实测**每一帧**都在
    // 左上角叠了一段文字（24000 像素的左上 200x120 区里稳定有 ~8500 个非清屏色像素，占 35%，
    // 最宽一行触及 x≈264~271）。固定单点采样一旦被某个字形压住就假红。
    // 取 5 点多数表决后：OSD 压住其中一两点不再误红；而真正的缺陷（整帧黑、抓到别的窗口、
    // 裁剪错位）会让 5 点**全部**不匹配 ⇒ 鉴别力不变。
    // 采样点全部落在右半 / 下部，与左上 OSD 注入区保持距离。
    static const int kUpPts[5][2] = {{300, 60}, {360, 60}, {240, 40}, {200, 80}, {380, 90}};
    static const int kLowPts[5][2] = {{300, 280}, {360, 280}, {240, 260}, {200, 290}, {380, 250}};
    int upHit = 0, lowHit = 0;
    std::string upBad, lowBad;
    for (int i = 0; i < 5; ++i) {
        unsigned char p[4];
        PxAt(f, kUpPts[i][0], kUpPts[i][1], p);
        if (IsColor(p, cr, cg, cb)) {
            ++upHit;
        } else {
            upBad += " (" + std::to_string(kUpPts[i][0]) + "," + std::to_string(kUpPts[i][1]) +
                     ")=" + RgbStr(p);
        }
        PxAt(f, kLowPts[i][0], kLowPts[i][1], p);
        if (IsColor(p, cr, cg, cb)) {
            ++lowHit;
        } else {
            lowBad += " (" + std::to_string(kLowPts[i][0]) + "," + std::to_string(kLowPts[i][1]) +
                      ")=" + RgbStr(p);
        }
    }
    std::printf("     清屏色采样：上部 5 点命中 %d/5%s；下部 5 点命中 %d/5%s\n", upHit,
                upBad.empty() ? "" : ("，未命中:" + upBad).c_str(), lowHit,
                lowBad.empty() ? "" : ("，未命中:" + lowBad).c_str());
    CHECK(upHit >= 4, "%s 上部 5 点应≥4 点=清屏色 RGB(%u,%u,%u)，实得命中 %d/5%s", tag,
          unsigned(cr), unsigned(cg), unsigned(cb), upHit, upBad.c_str());
    CHECK(lowHit >= 4, "%s 下部 5 点应≥4 点=清屏色 RGB(%u,%u,%u)，实得命中 %d/5%s", tag,
          unsigned(cr), unsigned(cg), unsigned(cb), lowHit, lowBad.c_str());

    // 行序（top-down）：绿色标记带必须出现在 y=[kMarkerY, kMarkerY+kMarkerH) 附近
    CHECK(IsColor(band, 0, 255, 0), "%s 标记带 (%d,%d) 应=绿 RGB(0,255,0)，实得 %s", tag, kProbeX,
          kBandY, RgbStr(band).c_str());

    // 扫描该列，量出绿色连续段的 y 范围 —— 若行序反了，这段会跑到 y≈h-224 附近
    int y0 = -1, y1 = -1;
    for (int y = 0; y < f.h; ++y) {
        unsigned char p[4];
        PxAt(f, kProbeX, y, p);
        const bool green = IsColor(p, 0, 255, 0);
        if (green) { if (y0 < 0) y0 = y; y1 = y; }
    }
    std::printf("     绿色带 y 范围: [%d..%d]（期望 [%d..%d]）\n", y0, y1, kMarkerY,
                kMarkerY + kMarkerH - 1);
    CHECK(y0 >= kMarkerY - 2 && y0 <= kMarkerY + 2 && y1 >= kMarkerY + kMarkerH - 3 &&
              y1 <= kMarkerY + kMarkerH + 1,
          "%s 绿色带 y 范围应≈[%d..%d]，实得 [%d..%d]（top-down 行序错误或标记未生效）", tag,
          kMarkerY, kMarkerY + kMarkerH - 1, y0, y1);

    // 扫描多行、取「最长的品红连续段」——用来发现**水平**裁剪偏移。
    // 注意：本机 RTSS / MSI Afterburner 的 OSD 会被注入 swapchain 并叠加在帧的左上角，
    // 固定某一行可能正好被 OSD 文字压住（实测 y=60 时 x[100..112] 被压掉），
    // 因此遍历多行取最长段，才能稳定反映真实的裁剪偏移。
    int bx0 = -1, bx1 = -1, best = 0;
    for (int y = 8; y < f.h; y += 5) {
        int runStart = -1;
        for (int x = 0; x <= f.w; ++x) {
            bool magenta = false;
            if (x < f.w) {
                unsigned char p[4];
                PxAt(f, x, y, p);
                magenta = IsColor(p, 255, 0, 255);
            }
            if (magenta) {
                if (runStart < 0) runStart = x;
            } else if (runStart >= 0) {
                if (x - runStart > best) { best = x - runStart; bx0 = runStart; bx1 = x - 1; }
                runStart = -1;
            }
        }
    }
    std::printf("     品红带 x 范围: [%d..%d]（期望 [%d..%d]，取最长连续段，宽 %d）\n", bx0, bx1,
                kStripeX, kStripeX + kStripeW - 1, best);
    CHECK(bx0 >= kStripeX - 2 && bx0 <= kStripeX + 2 && bx1 >= kStripeX + kStripeW - 3 &&
              bx1 <= kStripeX + kStripeW + 1,
          "%s 品红带 x 范围应≈[%d..%d]，实得 [%d..%d]（水平裁剪偏移错误）", tag, kStripeX,
          kStripeX + kStripeW - 1, bx0, bx1);

    DumpFrameStats(tag, f, cr, cg, cb);
}

/* ---------------- 共享 D3D11 资源（整个进程只建一次） ---------------- */
static bool InitSharedD3D() {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
                                  D3D_FEATURE_LEVEL_10_0};
    D3D_FEATURE_LEVEL got{};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels,
                                 ARRAYSIZE(levels), D3D11_SDK_VERSION, &g_dev, &got, &g_ctx))) {
        std::printf("FATAL: D3D11CreateDevice 失败\n");
        return false;
    }
    std::printf("    D3D11 设备已创建（feature level 0x%04X）\n", static_cast<unsigned>(got));
    {
        std::vector<unsigned int> px(static_cast<size_t>(kW) * kMarkerH, 0xFF00FF00u);  // BGRA 绿
        D3D11_SUBRESOURCE_DATA sd{};
        sd.pSysMem = px.data();
        sd.SysMemPitch = kW * 4;
        D3D11_TEXTURE2D_DESC md{};
        md.Width = kW; md.Height = kMarkerH; md.MipLevels = 1; md.ArraySize = 1;
        md.Format = DXGI_FORMAT_B8G8R8A8_UNORM; md.SampleDesc.Count = 1;
        md.Usage = D3D11_USAGE_DEFAULT;
        if (FAILED(g_dev->CreateTexture2D(&md, &sd, &g_marker))) {
            std::printf("FATAL: 建绿色横带纹理失败\n");
            return false;
        }

        std::vector<unsigned int> sp(static_cast<size_t>(kStripeW) * kH, 0xFFFF00FFu);  // BGRA 品红
        D3D11_SUBRESOURCE_DATA ssd{};
        ssd.pSysMem = sp.data();
        ssd.SysMemPitch = kStripeW * 4;
        D3D11_TEXTURE2D_DESC sd2{};
        sd2.Width = kStripeW; sd2.Height = kH; sd2.MipLevels = 1; sd2.ArraySize = 1;
        sd2.Format = DXGI_FORMAT_B8G8R8A8_UNORM; sd2.SampleDesc.Count = 1;
        sd2.Usage = D3D11_USAGE_DEFAULT;
        if (FAILED(g_dev->CreateTexture2D(&sd2, &ssd, &g_stripe))) {
            std::printf("FATAL: 建品红竖带纹理失败\n");
            return false;
        }
    }
    return true;
}

static void ReleaseSharedD3D() {
    if (g_marker) { g_marker->Release(); g_marker = nullptr; }
    if (g_stripe) { g_stripe->Release(); g_stripe = nullptr; }
    if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
    if (g_dev) { g_dev->Release(); g_dev = nullptr; }
}

/* ---------------- 单遍自测：返回 0 = 正常跑完；2 = FATAL 中止 ---------------- */
static int RunOnce() {
    /* --- 1. 动态解析导出 --- */
    Case("1 动态加载与 6 个导出解析");
    std::printf("    %s\n", "(g_dll 已由 main 加载)");
    struct { const char* name; FARPROC* slot; } exports[] = {
        {"Wgc_IsSupported", reinterpret_cast<FARPROC*>(&pIsSupported)},
        {"Wgc_Create", reinterpret_cast<FARPROC*>(&pCreate)},
        {"Wgc_Destroy", reinterpret_cast<FARPROC*>(&pDestroy)},
        {"Wgc_CaptureFrame", reinterpret_cast<FARPROC*>(&pCaptureFrame)},
        {"Wgc_FreeFrame", reinterpret_cast<FARPROC*>(&pFreeFrame)},
        {"Wgc_LastError", reinterpret_cast<FARPROC*>(&pLastError)},
    };
    int missing = 0;
    for (auto& e : exports) {
        *e.slot = GetProcAddress(g_dll, e.name);
        std::printf("    GetProcAddress(%-18s) -> %s\n", e.name,
                    *e.slot ? "OK（导出名未修饰）" : "**缺失**");
        if (!*e.slot) ++missing;
    }
    CHECK(missing == 0, "6 个导出应全部解析到，实得缺失 %d 个", missing);
    if (missing) return 2;

    /* --- 2. IsSupported --- */
    Case("2 Wgc_IsSupported");
    const int sup = pIsSupported();
    std::printf("    Wgc_IsSupported() = %d\n", sup);
    CHECK(sup == 1, "系统应支持 WGC（Wgc_IsSupported 应=1），实得 %d", sup);

    /* --- 3. 建窗口 --- */
    Case("3 建立 flip-model 窗口");
    std::printf("    FLIP_DISCARD / buf=2 / ALPHA_IGNORE / SCALING_NONE\n");
    TestWindow top;
    if (!CreateFlipWindow(top, nullptr, 120, 120, 1.f, 0.f, 0.f, "top-red")) return 2;
    Pump();
    Sleep(300);
    Pump();

    void* h = pCreate();
    std::printf("    Wgc_Create() -> %p\n", h);
    CHECK(h != nullptr, "Wgc_Create 应返回非空 handle，实得 NULL");
    if (!h) return 2;

    /* --- 4. 无遮挡抓帧 --- */
    Case("4 无遮挡抓帧（清屏色=纯红）");
    {
        Frame f;
        std::string err;
        StartPresent(top);
        const DWORD t0 = GetTickCount();
        const int rc = Capture(h, top.hwnd, f, err);
        g_msFirstCapture = static_cast<int>(GetTickCount() - t0);
        StopPresent();
        std::printf("    Wgc_CaptureFrame() = %d（0=OK）耗时 %d ms %s\n", rc, g_msFirstCapture,
                    err.empty() ? "" : ("  err=" + err).c_str());
        CHECK(rc == WGC_OK, "无遮挡抓帧应成功 rc=0，实得 rc=%d err=%s", rc, err.c_str());
        if (rc == WGC_OK) VerifyFrame("无遮挡", f, 255, 0, 0);
    }

    /* --- 5. 遮挡测试 --- */
    Case("5 遮挡抓帧：不透明绿窗完全盖住目标窗");
    {
        WNDCLASSA cc{};
        cc.lpfnWndProc = CoverProc;
        cc.hInstance = GetModuleHandleA(nullptr);
        cc.lpszClassName = "WgcSelfTestCover";
        RegisterClassA(&cc);
        HWND cover = CreateWindowExA(WS_EX_TOPMOST, "WgcSelfTestCover", "cover",
                                     WS_POPUP | WS_VISIBLE, 100, 100, 700, 600, nullptr, nullptr,
                                     cc.hInstance, nullptr);
        SetWindowPos(cover, HWND_TOPMOST, 100, 100, 700, 600, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        UpdateWindow(cover);
        Pump();
        Sleep(400);
        Pump();
        std::printf("    cover 700x600 @(100,100) 完全盖住目标窗 %dx%d @(120,120)\n", kW, kH);

        Frame f;
        std::string err;
        StartPresent(top);
        const int rc = Capture(h, top.hwnd, f, err);
        StopPresent();
        std::printf("    Wgc_CaptureFrame() = %d %s\n", rc, err.empty() ? "" : ("  err=" + err).c_str());
        CHECK(rc == WGC_OK, "遮挡下抓帧应成功 rc=0（WGC 抓的是窗口自身内容，不受遮挡影响），实得 rc=%d err=%s",
              rc, err.c_str());
        if (rc == WGC_OK) VerifyFrame("有遮挡", f, 255, 0, 0);

        DestroyWindow(cover);
        Pump();
    }

    /* --- 6. 子窗口抓帧（3FCompare 真实形态） --- */
    Case("6 子窗口抓帧（父窗口 + WS_CHILD 子窗口，清屏色=蓝）");
    {
        std::printf("    子窗口放在父窗口客户区 (30,40)，用于验证「捕获顶层祖先 + 裁剪」的偏移是否正确\n");
        // 父窗口带标题栏/边框（非 WS_POPUP），这样「客户区 vs 窗口矩形」的差异会暴露出来
        HWND parent = CreatePlainWindow("WgcSelfTestParent", "parent",
                                        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 560, 120, 700, 520,
                                        nullptr);
        if (!parent) { std::printf("    FATAL: 建父窗口失败\n"); return 2; }
        UpdateWindow(parent);

        TestWindow child;
        if (!CreateFlipWindow(child, parent, 30, 40, 0.f, 0.25f, 1.f, "child-blue")) return 2;

        RECT pc{}, pr{}, cc{};
        GetClientRect(parent, &pc);
        GetWindowRect(parent, &pr);
        GetClientRect(child.hwnd, &cc);
        RECT ext{};
        DwmGetWindowAttribute(parent, DWMWA_EXTENDED_FRAME_BOUNDS, &ext, sizeof(ext));
        POINT tl{0, 0};
        MapWindowPoints(child.hwnd, parent, &tl, 1);
        std::printf("    父窗口: 窗口矩形 %dx%d, 客户区 %dx%d, 扩展帧边界 %dx%d @(%d,%d)\n",
                    int(pr.right - pr.left), int(pr.bottom - pr.top), int(pc.right), int(pc.bottom),
                    int(ext.right - ext.left), int(ext.bottom - ext.top), int(ext.left),
                    int(ext.top));
        std::printf("    子窗口: 客户区 %dx%d, 在父客户区中的原点 = (%d,%d)\n", int(cc.right),
                    int(cc.bottom), int(tl.x), int(tl.y));

        Pump();
        Sleep(300);
        Pump();

        // 先直接抓一次父窗口，确认 WGC 对「带边框窗口」报的尺寸语义（窗口矩形 vs 客户区）
        {
            Frame pf;
            std::string pe;
            StartPresent(child);
            const int prc = Capture(h, parent, pf, pe);
            StopPresent();
            std::printf("    直接抓父窗口: rc=%d 抓回 %dx%d  ← 用于判断内容原点/尺寸语义\n", prc,
                        pf.w, pf.h);
        }

        Frame f;
        std::string err;
        StartPresent(child);
        const int rc = Capture(h, child.hwnd, f, err);
        StopPresent();
        std::printf("    Wgc_CaptureFrame(child.hwnd) = %d %s\n", rc,
                    err.empty() ? "" : ("  err=" + err).c_str());
        CHECK(rc == WGC_OK, "子窗口抓帧应成功 rc=0，实得 rc=%d err=%s", rc, err.c_str());
        if (rc == WGC_OK) VerifyFrame("子窗口", f, 0, 64, 255);

        DestroyWindow(child.hwnd);
        DestroyWindow(parent);
        Pump();
    }

    /* --- 7. 会话复用 + 换窗口重建 --- */
    Case("7 会话复用 / hwnd 变更自动重建");
    {
        Frame f1, f2;
        std::string e1, e2;
        StartPresent(top);
        const int rc1 = Capture(h, top.hwnd, f1, e1);   // 复用第 4 步建立的会话
        const int rc2 = Capture(h, top.hwnd, f2, e2);   // 同一 hwnd 再抓一次
        StopPresent();
        std::printf("    同一 hwnd 连抓两次: rc1=%d rc2=%d（均应 0）\n", rc1, rc2);
        CHECK(rc1 == WGC_OK && rc2 == WGC_OK, "复用会话连抓两次应均成功 rc1=0 rc2=0，实得 rc1=%d rc2=%d",
              rc1, rc2);
    }

    /* --- 8. 静态内容抓帧（画一次后不再重绘） --- */
    Case("8 静态内容抓帧：只渲染一次、之后不再 Present");
    {
        std::printf("    复现「暂停态」原始缺陷：旧实现无条件丢弃前 2 帧，必然 TIMEOUT\n");
        // 独立窗口：画一次后彻底静止 ⇒ WGC 只会产出 1 帧。
        TestWindow still;
        if (!CreateFlipWindow(still, nullptr, 140, 440, 0.f, 1.f, 1.f, "static-cyan")) return 2;
        Pump();
        Sleep(200);
        Pump();

        RenderOnce(still);  // 唯一的一次渲染
        Sleep(150);
        Pump();

        // 用**独立 handle**（即新建会话）确保走到「会话刚建立就要抓静止窗口」这条路径。
        void* hs = pCreate();
        CHECK(hs != nullptr, "Wgc_Create（静态用例）应返回非空 handle，实得 NULL");
        if (hs) {
            Frame f1, f2;
            std::string e1, e2;

            const DWORD t0 = GetTickCount();
            const int rc1 = Capture(hs, still.hwnd, f1, e1);
            g_msStatic1 = static_cast<int>(GetTickCount() - t0);
            std::printf("    第 1 次抓帧: rc=%d 耗时 %d ms %s\n", rc1, g_msStatic1,
                        e1.empty() ? "" : ("err=" + e1).c_str());
            CHECK(rc1 == WGC_OK, "静态窗口抓帧应成功（不得 TIMEOUT），实得 rc=%d err=%s", rc1,
                  e1.c_str());
            if (rc1 == WGC_OK) VerifyFrame("静态-首次", f1, 0, 255, 255);

            // 第 2 次：窗口依旧不重绘 ⇒ 走「返回最后一帧」的降级路径，同样不得 TIMEOUT。
            const DWORD t1 = GetTickCount();
            const int rc2 = Capture(hs, still.hwnd, f2, e2);
            g_msStatic2 = static_cast<int>(GetTickCount() - t1);
            std::printf("    第 2 次抓帧: rc=%d 耗时 %d ms %s\n", rc2, g_msStatic2,
                        e2.empty() ? "" : ("err=" + e2).c_str());
            CHECK(rc2 == WGC_OK, "静态窗口连续抓帧应成功（不得 TIMEOUT），实得 rc=%d err=%s", rc2,
                  e2.c_str());
            if (rc2 == WGC_OK) VerifyFrame("静态-复用", f2, 0, 255, 255);

            pDestroy(hs);
        }
        DestroyWindow(still.hwnd);
        Pump();
    }

    /* --- 9. B1：会话重建 + 静态窗口（不得返回旧会话内容 / 不得盲等热身） --- */
    Case("9 B1：会话重建 + 静态窗口");
    {
        // 独立静态窗口：先画一次红色，之后完全静止。
        TestWindow rebuilt;
        if (!CreateFlipWindow(rebuilt, nullptr, 140, 640, 1.f, 0.f, 0.f, "rebuild-red")) return 2;
        Pump();
        Sleep(200);
        Pump();
        RenderOnce(rebuilt);
        Sleep(200);
        Pump();

        void* hr = pCreate();
        CHECK(hr != nullptr, "Wgc_Create（B1 用例）应返回非空 handle，实得 NULL");
        if (hr) {
            Frame f1;
            std::string e1;
            const DWORD t0 = GetTickCount();
            const int rc1 = Capture(hr, rebuilt.hwnd, f1, e1);
            const DWORD ms1 = GetTickCount() - t0;
            std::printf("    首次抓帧（新建会话 + 静止窗口）: rc=%d 耗时 %lu ms\n", rc1, ms1);
            CHECK(rc1 == WGC_OK, "B1 首次抓帧应成功，实得 rc=%d err=%s", rc1, e1.c_str());
            if (rc1 == WGC_OK) VerifyFrame("B1-首次", f1, 255, 0, 0);
            // ⚠ 此处**刻意不断言耗时**：hr 是刚 Wgc_Create 的 handle，首次抓帧必须先在
            //   EnsureDevice 里惰性创建 D3D11 硬件设备 + WinRT 包装设备。实测这一步耗时呈
            //   长尾分布：0/15/31/47/63/78/157/344/453 ms（进程内已有多个 D3D11 设备时驱动
            //   初始化更慢），与本用例要守的「窗口内容 / 抓帧逻辑」无关。
            //   旧断言 ms1<200 正好卡在该长尾里 ⇒ 实测 27 次中 2 次假红（实得 438ms / 359ms），
            //   而这两次埋点显示「帧到达」耗时恒为 0ms、全部时间花在设备创建上。
            //   「新建会话不得盲等热身期」改在**持续 Present 的窗口**上断言（见本段末尾），
            //   那里「帧何时可得」是确定的，测出来的才是 DLL 自己花掉的时间。

            // 强制重建会话：最小化 -> 抓帧（NOT_CAPTURABLE）-> 还原。
            ShowWindow(rebuilt.hwnd, SW_MINIMIZE);
            Pump();
            Sleep(300);
            Pump();
            unsigned char* b = nullptr;
            int w = 0, hh = 0, st = 0;
            const int rcMin = pCaptureFrame(hr, rebuilt.hwnd, &b, &w, &hh, &st);
            std::printf("    最小化抓帧: rc=%d（期望 %d）\n", rcMin, WGC_ERR_NOT_CAPTURABLE);
            CHECK(rcMin == WGC_ERR_NOT_CAPTURABLE, "最小化应返回 NOT_CAPTURABLE(%d)，实得 %d",
                  WGC_ERR_NOT_CAPTURABLE, rcMin);
            ShowWindow(rebuilt.hwnd, SW_RESTORE);
            Pump();

            // 还原后把内容换成青色（≠ 旧会话的红）再静止 ⇒ 重建出的会话若返回旧会话内容即为 B1。
            rebuilt.cr = 0.f;
            rebuilt.cg = 1.f;
            rebuilt.cb = 1.f;
            RenderOnce(rebuilt);
            Sleep(300);
            Pump();

            Frame f2;
            std::string e2;
            const DWORD t1 = GetTickCount();
            const int rc2 = Capture(hr, rebuilt.hwnd, f2, e2);
            const DWORD ms2 = GetTickCount() - t1;
            std::printf("    重建会话后抓帧: rc=%d 耗时 %lu ms\n", rc2, ms2);
            CHECK(rc2 == WGC_OK, "B1 重建会话后抓帧应成功，实得 rc=%d err=%s", rc2, e2.c_str());
            // ⚠ 这里也**刻意不断言耗时**：本窗口是「静止窗口 + 会话重建」，实测耗时在
            //   15~469 ms 之间大幅抖动（何时重合成静止窗口由 WGC/DWM 决定，DLL 拿到帧就返回，
            //   帧没来就等满 400ms 预算再走缓存）。任何紧阈值都会间歇性假红。
            if (rc2 == WGC_OK) {
                VerifyFrame("B1-重建", f2, 0, 255, 255);
                unsigned char p[4];
                PxAt(f2, kProbeX, kTopY, p);
                CHECK(!IsColor(p, 255, 0, 0),
                      "B1：重建会话后不得返回旧会话内容 RGB(255,0,0)（应为当前内容青 RGB(0,255,255)），实得 %s",
                      RgbStr(p).c_str());
            }

            // ---- 「新建会话不得盲等热身期」的稳健判据（取 N 次重建的最小值）----
            // 旧实现每次建会话都固定等满 300ms 热身期、并盲丢会话前 2 帧 ⇒ 建会话后的首次抓帧
            // 必然 ≥300ms（与窗口内容是否变化无关）。
            //
            // 为什么改成「取多次最小值」而不是单次采样（实测依据）：
            //   会话重建里真正的系统开销是 WGC 的 IGraphicsCaptureItemInterop::CreateForWindow。
            //   用 WGC_TRACE=1 埋点跑 320 次得到它的耗时分布：
            //     多数 0~16ms；长尾 93/172/234/360/375/500ms（约 2%）。
            //   而**本 DLL 自己**的开销（关旧会话 + 建帧池 + CreateCaptureSession + StartCapture）
            //   恒为 0~16ms（320 次里 0ms 占多数，最大 16ms）。
            //   单次采样会把这根属于操作系统的长尾当成 DLL 的问题 ⇒ 实测 320 次独立进程里假红 1 次
            //   （422ms）、同进程 60 遍里假红 1 次（250ms），两次都只是这一条断言。
            //   取 N 次最小值：旧实现「每次建会话固定等满 300ms」会让 N 次全部 ≥300ms
            //   ⇒ 鉴别力 100% 保留；而 CreateForWindow 偶发长尾（p≈2%）需要 N 次**全部**命中
            //   才会误判：N=5 ⇒ 误判概率 ≈ (2%)^5 ≈ 3e-9（单次采样则是 2%，实测约 0.3%/次）。
            //
            // 目标窗口必须是**持续 Present** 的：这样「帧何时可得」是确定的（~16/32ms），
            // 时间窗里只剩「建会话 + 取会话首帧」，不会混进静止窗口何时重合成的环境抖动。
            {
                TestWindow top2;
                const bool ok2 =
                    CreateFlipWindow(top2, nullptr, 700, 120, 0.2f, 0.2f, 0.2f, "top2-gray");
                std::vector<TestWindow*> wins{&top};
                if (ok2) wins.push_back(&top2);

                Frame fl;
                std::string el;
                StartPresentMany(wins);
                int best = -1;
                std::string detail;
                const int trials = ok2 ? 5 : 3;
                for (int trial = 0; trial < trials; ++trial) {
                    // 每次都换 hwnd ⇒ 每次都强制重建会话
                    HWND target = wins[trial % wins.size()]->hwnd;
                    const DWORD tl = GetTickCount();
                    const int rcl = Capture(hr, target, fl, el);
                    const int ms = static_cast<int>(GetTickCount() - tl);
                    char b[128];
                    std::snprintf(b, sizeof(b), "%s%dms", trial ? " " : "", ms);
                    detail += b;
                    std::printf("    会话重建 trial%d（目标 %s）: rc=%d 耗时 %d ms\n", trial + 1,
                                target == top.hwnd ? "top" : "top2", rcl, ms);
                    CHECK(rcl == WGC_OK,
                          "B1 会话重建 trial%d（持续 Present 窗口）应成功，实得 rc=%d err=%s",
                          trial + 1, rcl, el.c_str());
                    if (rcl == WGC_OK && (best < 0 || ms < best)) best = ms;
                }
                StopPresent();
                g_msB1Rebuild = best;
                if (ok2) { DestroyWindow(top2.hwnd); Pump(); }
                std::printf("    会话重建 %d 次耗时（ms）: %s  ⇒ 取最小值 %d ms 判据阈值 250ms\n",
                            trials, detail.c_str(), best);
                CHECK(best >= 0 && best < 250,
                      "B1：新建会话不应盲等热身期（旧实现每次建会话固定等满 300ms 热身期并盲丢"
                      "前 2 帧）——%d 次重建的最小值应 <250ms，实得 %d ms（各次: %s）",
                      trials, best, detail.c_str());
            }
            pDestroy(hr);
        }
        DestroyWindow(rebuilt.hwnd);
        Pump();
    }

    /* --- 10. B4：同一 handle 的并发调用（头文件承诺内部串行化） --- */
    Case("10 B4：同一 handle 的多线程并发调用");
    {
        void* hc = pCreate();
        CHECK(hc != nullptr, "Wgc_Create（并发用例）应返回非空 handle，实得 NULL");
        if (hc) {
            constexpr int kThreads = 4;
            std::atomic<int> okCount{0};
            std::atomic<int> badCount{0};
            std::atomic<bool> go{false};
            std::vector<std::thread> ts;
            for (int i = 0; i < kThreads; ++i) {
                ts.emplace_back([&] {
                    while (!go.load()) {
                    }
                    Frame f;
                    std::string e;
                    const int rc = Capture(hc, top.hwnd, f, e);
                    if (rc == WGC_OK) {
                        ++okCount;
                    } else {
                        ++badCount;
                        std::printf("    [并发] 抓帧失败 rc=%d err=%s\n", rc, e.c_str());
                    }
                });
            }
            StartPresent(top);
            go.store(true);
            for (auto& t : ts) t.join();
            StopPresent();
            std::printf("    %d 线程并发抓同一 handle：成功 %d / 失败 %d（旧实现会丢弃任务并返回 %d）\n",
                        kThreads, okCount.load(), badCount.load(), WGC_ERR_INTERNAL);
            CHECK(okCount.load() == kThreads,
                  "并发抓帧应全部成功（内部串行化）：4 线程应成功 4，实得成功 %d 失败 %d",
                  okCount.load(), badCount.load());
            pDestroy(hc);
        }
    }

    /* --- 11. 负例 --- */
    Case("11 负例：非法参数错误码");
    {
        unsigned char* bits = nullptr;
        int w = 0, hh = 0, st = 0;
        char err[512] = {0};

        int rc = pCaptureFrame(h, nullptr, &bits, &w, &hh, &st);
        pLastError(h, err, sizeof(err));
        std::printf("    hwnd=NULL      -> rc=%d（期望 %d）err=\"%s\"\n", rc, WGC_ERR_INVALID_ARG, err);
        CHECK(rc == WGC_ERR_INVALID_ARG, "hwnd=NULL 应返回 INVALID_ARG(%d)，实得 %d",
              WGC_ERR_INVALID_ARG, rc);
        CHECK(bits == nullptr, "失败时 outBits 应为 NULL，实得 %p", static_cast<void*>(bits));

        rc = pCaptureFrame(h, reinterpret_cast<void*>(static_cast<INT_PTR>(0x1234)), &bits, &w,
                           &hh, &st);
        pLastError(h, err, sizeof(err));
        std::printf("    hwnd=0x1234    -> rc=%d（期望 %d）err=\"%s\"\n", rc,
                    WGC_ERR_NOT_CAPTURABLE, err);
        CHECK(rc == WGC_ERR_NOT_CAPTURABLE, "非法 hwnd 应返回 NOT_CAPTURABLE(%d)，实得 %d",
              WGC_ERR_NOT_CAPTURABLE, rc);

        rc = pCaptureFrame(nullptr, top.hwnd, &bits, &w, &hh, &st);
        std::printf("    handle=NULL    -> rc=%d（期望 %d）\n", rc, WGC_ERR_INVALID_ARG);
        CHECK(rc == WGC_ERR_INVALID_ARG, "handle=NULL 应返回 INVALID_ARG(%d)，实得 %d",
              WGC_ERR_INVALID_ARG, rc);

        pLastError(h, err, sizeof(err));
        std::printf("    最近一次失败信息: \"%s\"\n", err);

        pFreeFrame(h, nullptr);   // 空指针应安全
        pLastError(nullptr, err, sizeof(err));
        pLastError(h, nullptr, 0);
        std::printf("    空指针防护调用未崩溃\n");
    }

    /* --- 11b. #6：目标窗口在会话存活期被销毁（teardown 不得死锁） --- */
    Case("11b #6：目标窗口在会话存活期被销毁");
    {
        void* hd = pCreate();
        CHECK(hd != nullptr, "Wgc_Create（死目标用例）应返回非空 handle，实得 NULL");
        if (hd) {
            TestWindow w1;
            if (!CreateFlipWindow(w1, nullptr, 60, 60, 1.f, 1.f, 0.f, "dead-target")) {
                CHECK(false, "建窗口失败：dead-target");
                return 2;
            }
            Pump();
            StartPresent(w1);
            Frame f1;
            std::string e1;
            const int rc1 = Capture(hd, w1.hwnd, f1, e1);
            StopPresent();
            CHECK(rc1 == WGC_OK, "建会话后抓帧应成功，实得 rc=%d err=%s", rc1, e1.c_str());

            // 会话**存活期间**销毁目标窗口：下一次抓帧必须判定「目标窗口对象已消失」并走
            // 「入 Graveyard」分支，绝不对它做 Close()/Release()（那会永久死锁，docs/41 #6）。
            // 旧实现只靠 IsWindow 判定：HWND 值一旦被系统复用就会误判成「还活着」，
            // 于是去 Close() 一个已死目标 ⇒ worker 线程永久阻塞。
            DestroyWindow(w1.hwnd);
            Pump();
            Sleep(100);
            Pump();

            TestWindow w2;
            if (!CreateFlipWindow(w2, nullptr, 60, 60, 0.f, 1.f, 1.f, "new-target")) {
                CHECK(false, "建窗口失败：new-target");
                return 2;
            }
            Pump();
            StartPresent(w2);
            Frame f2;
            std::string e2;
            const DWORD t0 = GetTickCount();
            const int rc2 = Capture(hd, w2.hwnd, f2, e2);
            const DWORD ms2 = GetTickCount() - t0;
            StopPresent();
            std::printf("    目标窗口已销毁后换新窗口抓帧: rc=%d 耗时 %lu ms\n", rc2, ms2);
            CHECK(rc2 == WGC_OK, "换到新窗口抓帧应成功，实得 rc=%d err=%s", rc2, e2.c_str());
            // 死锁的表现是**永久**阻塞，这里给一个远大于正常耗时（几十 ms）的上限。
            CHECK(ms2 < 3000, "抓帧不得因 teardown 死锁而阻塞，实得 %lu ms", ms2);

            // 会话此时绑在新窗口上：销毁 handle 也应正常返回（旧会话已入 Graveyard，不再触碰）。
            const DWORD t1 = GetTickCount();
            pDestroy(hd);
            const DWORD ms3 = GetTickCount() - t1;
            std::printf("    Wgc_Destroy 耗时 %lu ms\n", ms3);
            CHECK(ms3 < 3000, "Wgc_Destroy 不得因 teardown 死锁而阻塞，实得 %lu ms", ms3);

            DestroyWindow(w2.hwnd);
            Pump();
        }
    }

    /* --- 收尾 --- */
    Case("12 释放");
    pDestroy(h);
    pDestroy(nullptr);
    std::printf("    Wgc_Destroy 完成（含 NULL 防护）\n");

    DestroyWindow(top.hwnd);
    Pump();
    return 0;
}

/* =================================================================================
 * 定向探针（--flaky-probe）—— 主动制造「整帧内容不对」的时序，而不是被动等大样本。
 * =================================================================================
 * 为什么需要它：默认用例 [9] 的「会话重建 trial」循环**只断言 rc==0，不校验内容**，
 * 因此「取到别的窗口 / 黑帧 / 旧内容」这类缺陷在那条路径上是**盲区**。
 *
 * 内容签名判据：清屏色 5 点命中数 + 绿带是否存在 + 品红带是否存在。
 *   - 尺寸/stride 正确但**签名三项全不匹配** = 整帧内容不对（黑帧 / 未合成帧 / 别的窗口）
 *   - 清屏色命中但**绿带/品红带缺失** = 整帧不对（本套件所有窗口都由 RenderOnce 画带）
 *   - 清屏色是**上一个窗口/上一版内容**的颜色 = 内容泄漏
 * ================================================================================= */
struct Sig {
    int clearHit = 0;        // 5 点清屏色命中数
    bool greenBand = false;  // (300,212) == 绿
    bool magenta = false;    // 存在长度 >= 12 的品红连续段
    unsigned char top[4] = {0, 0, 0, 0};  // (300,60) 实得像素（诊断用）
    bool hasBands() const { return greenBand && magenta; }
    std::string Str() const {
        char b[128];
        std::snprintf(b, sizeof(b), "清屏%d/5 绿带%s 品红带%s (300,60)=RGB(%u,%u,%u)", clearHit,
                      greenBand ? "有" : "无", magenta ? "有" : "无", top[2], top[1], top[0]);
        return b;
    }
};

static Sig Signature(const Frame& f, unsigned char cr, unsigned char cg, unsigned char cb) {
    Sig s;
    static const int pts[5][2] = {{300, 60}, {360, 60}, {240, 40}, {200, 80}, {380, 90}};
    for (auto& p : pts) {
        unsigned char c[4];
        PxAt(f, p[0], p[1], c);
        if (IsColor(c, cr, cg, cb)) ++s.clearHit;
    }
    PxAt(f, 300, 60, s.top);
    unsigned char band[4];
    PxAt(f, kProbeX, kBandY, band);
    s.greenBand = IsColor(band, 0, 255, 0);
    int best = 0, run = 0;
    for (int y = 8; y < f.h; y += 5) {
        run = 0;
        for (int x = 0; x < f.w; ++x) {
            unsigned char c[4];
            PxAt(f, x, y, c);
            if (IsColor(c, 255, 0, 255)) { if (++run > best) best = run; }
            else run = 0;
        }
    }
    s.magenta = best >= 12;
    return s;
}

// 探针结果统计
struct ProbeStat {
    int trials = 0, rcBad = 0, sizeBad = 0, wrongContent = 0, blackish = 0;
    int maxMs = 0;
    std::vector<std::string> details;
};

static void ReportProbe(const char* name, const ProbeStat& s) {
    std::printf("  [探针] %-46s 次数=%-3d rc失败=%d 尺寸不符=%d 内容不符=%d(其中无带/黑帧=%d) 最慢=%dms\n",
                name, s.trials, s.rcBad, s.sizeBad, s.wrongContent, s.blackish, s.maxMs);
    for (const auto& d : s.details) std::printf("         ↳ %s\n", d.c_str());
}

// P1：内容变更 + 会话重建（最小化→还原）后**立即**抓帧（不 Sleep）。
//     直接攻击「跨会话 cache / 会话前合成帧」这条兜底链：窗口内容已从 A 变成 B，
//     重建后的会话若返回 A（旧内容）或黑帧，即为内容缺陷。
static void ProbeRebuildAfterChange(int trials, ProbeStat& st) {
    TestWindow w;
    if (!CreateFlipWindow(w, nullptr, 760, 440, 1.f, 0.f, 0.f, "probe1")) return;
    Pump(); Sleep(200); Pump();
    void* hp = pCreate();
    if (!hp) return;

    for (int i = 0; i < trials; ++i) {
        // 逐次交替两套颜色：A 先呈（写进 cache），B 是重建后应立即返回的当前内容。
        static const unsigned char RED[3] = {255, 0, 0};
        static const unsigned char CYAN[3] = {0, 255, 255};
        const bool firstRed = (i % 2) == 0;
        const unsigned char* cA = firstRed ? RED : CYAN;
        const unsigned char* cB = firstRed ? CYAN : RED;

        // 阶段 A：呈色 A 并静止，抓一次把 A 写进 cache
        w.cr = cA[0] / 255.f; w.cg = cA[1] / 255.f; w.cb = cA[2] / 255.f;
        RenderOnce(w);
        Sleep(120);
        Pump();
        Frame f1; std::string e1;
        const int rc1 = Capture(hp, w.hwnd, f1, e1);

        // 阶段 B：强制重建会话（最小化→抓→还原），改内容为 B，**立即**抓帧
        ShowWindow(w.hwnd, SW_MINIMIZE);
        Pump();
        unsigned char* b = nullptr; int ww = 0, hh = 0, stp = 0;
        pCaptureFrame(hp, w.hwnd, &b, &ww, &hh, &stp);  // 期望 NOT_CAPTURABLE
        ShowWindow(w.hwnd, SW_RESTORE);
        Pump();
        w.cr = cB[0] / 255.f; w.cg = cB[1] / 255.f; w.cb = cB[2] / 255.f;
        RenderOnce(w);  // 呈 B，不等待（放大「会话前合成/未合成」窗口）
        Frame f2; std::string e2;
        const DWORD t0 = GetTickCount();
        const int rc2 = Capture(hp, w.hwnd, f2, e2);
        const int ms = static_cast<int>(GetTickCount() - t0);

        ++st.trials;
        if (ms > st.maxMs) st.maxMs = ms;
        if (rc1 != WGC_OK || rc2 != WGC_OK) {
            ++st.rcBad;
            if (st.details.size() < 6)
                st.details.push_back("P1#" + std::to_string(i) + " rc1=" + std::to_string(rc1) +
                                     " rc2=" + std::to_string(rc2) + " err=" + e2);
            continue;
        }
        if (f2.w != kW || f2.h != kH) {
            ++st.sizeBad;
            if (st.details.size() < 6)
                st.details.push_back("P1#" + std::to_string(i) + " 尺寸 " + std::to_string(f2.w) +
                                     "x" + std::to_string(f2.h));
            continue;
        }
        const Sig sig = Signature(f2, cB[0], cB[1], cB[2]);
        const Sig sigOld = Signature(f2, cA[0], cA[1], cA[2]);
        if (sig.clearHit < 4 || !sig.hasBands()) {
            ++st.wrongContent;
            if (!sig.hasBands()) ++st.blackish;
            if (st.details.size() < 6)
                st.details.push_back(
                    "P1#" + std::to_string(i) + " 期望 B=RGB(" + std::to_string(cB[0]) + "," +
                    std::to_string(cB[1]) + "," + std::to_string(cB[2]) + ") 实得 [" + sig.Str() +
                    "] 旧色A命中=" + std::to_string(sigOld.clearHit) + " 耗时=" +
                    std::to_string(ms) + "ms");
        }
    }
    pDestroy(hp);
    DestroyWindow(w.hwnd);
    Pump();
}

// P2：同一 hwnd 反复重建会话（静态窗口，内容不变）——攻击「首帧绕过陈旧判定」。
//     每次重建后抓帧，内容应恒等于该窗口的清屏色；黑帧/无带帧即为缺陷。
static void ProbeRepeatedRebuild(int trials, ProbeStat& st) {
    TestWindow w;
    if (!CreateFlipWindow(w, nullptr, 760, 200, 0.f, 1.f, 0.f, "probe2")) return;
    Pump(); Sleep(200); Pump();
    RenderOnce(w);  // 纯绿清屏色（带仍为绿/品红，注意清屏色与绿带同色，故用下部点判）
    Sleep(120);
    Pump();
    void* hp = pCreate();
    if (!hp) return;

    for (int i = 0; i < trials; ++i) {
        // 每次换 hwnd 值即可强制重建：这里用最小化→还原制造 NOT_CAPTURABLE
        ShowWindow(w.hwnd, SW_MINIMIZE);
        Pump();
        unsigned char* b = nullptr; int ww = 0, hh = 0, stp = 0;
        pCaptureFrame(hp, w.hwnd, &b, &ww, &hh, &stp);
        ShowWindow(w.hwnd, SW_RESTORE);
        Pump();
        Frame f; std::string e;
        const DWORD t0 = GetTickCount();
        const int rc = Capture(hp, w.hwnd, f, e);
        const int ms = static_cast<int>(GetTickCount() - t0);
        ++st.trials;
        if (ms > st.maxMs) st.maxMs = ms;
        if (rc != WGC_OK) {
            ++st.rcBad;
            if (st.details.size() < 6)
                st.details.push_back("P2#" + std::to_string(i) + " rc=" + std::to_string(rc) + " " + e);
            continue;
        }
        if (f.w != kW || f.h != kH) {
            ++st.sizeBad;
            if (st.details.size() < 6)
                st.details.push_back("P2#" + std::to_string(i) + " 尺寸 " + std::to_string(f.w) + "x" +
                                     std::to_string(f.h));
            continue;
        }
        // 清屏色=绿，绿带也是绿 ⇒ 用「品红带存在」+「下部采样点非黑」判内容
        const Sig sig = Signature(f, 0, 255, 0);
        unsigned char low[4];
        PxAt(f, kProbeX, kBotY, low);
        const bool lowOk = IsColor(low, 0, 255, 0);
        if (!sig.magenta || !lowOk) {
            ++st.wrongContent;
            if (!sig.hasBands()) ++st.blackish;
            if (st.details.size() < 6)
                st.details.push_back("P2#" + std::to_string(i) + " [" + sig.Str() + "] 下部=" +
                                     RgbStr(low) + " 耗时=" + std::to_string(ms) + "ms");
        }
    }
    pDestroy(hp);
    DestroyWindow(w.hwnd);
    Pump();
}

// P3：两个**同尺寸、不同清屏色**的窗口交替抓帧（每次换 hwnd ⇒ 每次重建会话），
//     逐次校验内容是否等于**目标窗口**。这条路径在默认用例 [9] 里只断言 rc==0，是盲区。
static void ProbeAlternateWindows(int trials, ProbeStat& st) {
    TestWindow a, b;
    if (!CreateFlipWindow(a, nullptr, 760, 640, 1.f, 0.f, 0.f, "probe3-red")) return;
    if (!CreateFlipWindow(b, nullptr, 760, 640, 0.f, 0.f, 1.f, "probe3-blue")) return;
    // 两个窗口错开位置，避免完全重叠导致的可视性差异
    SetWindowPos(b.hwnd, nullptr, 760, 960, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    Pump(); Sleep(200); Pump();
    std::vector<TestWindow*> wins{&a, &b};
    StartPresentMany(wins);
    void* hp = pCreate();
    if (hp) {
        for (int i = 0; i < trials; ++i) {
            TestWindow* tw = wins[i % wins.size()];
            Frame f; std::string e;
            const DWORD t0 = GetTickCount();
            const int rc = Capture(hp, tw->hwnd, f, e);
            const int ms = static_cast<int>(GetTickCount() - t0);
            ++st.trials;
            if (ms > st.maxMs) st.maxMs = ms;
            if (rc != WGC_OK) {
                ++st.rcBad;
                if (st.details.size() < 6)
                    st.details.push_back("P3#" + std::to_string(i) + " rc=" + std::to_string(rc) + " " + e);
                continue;
            }
            if (f.w != kW || f.h != kH) {
                ++st.sizeBad;
                if (st.details.size() < 6)
                    st.details.push_back("P3#" + std::to_string(i) + " 尺寸 " + std::to_string(f.w) + "x" +
                                         std::to_string(f.h));
                continue;
            }
            const unsigned char er = static_cast<unsigned char>(tw->cr * 255);
            const unsigned char eb = static_cast<unsigned char>(tw->cb * 255);
            const Sig sig = Signature(f, er, 0, eb);
            if (sig.clearHit < 4 || !sig.hasBands()) {
                ++st.wrongContent;
                if (!sig.hasBands()) ++st.blackish;
                TestWindow* other = wins[(i + 1) % wins.size()];
                const Sig sigOther = Signature(f, static_cast<unsigned char>(other->cr * 255), 0,
                                               static_cast<unsigned char>(other->cb * 255));
                if (st.details.size() < 6)
                    st.details.push_back("P3#" + std::to_string(i) + " 目标=" +
                                         (tw == &a ? "红" : "蓝") + " 实得 [" + sig.Str() + "] 另一窗命中=" +
                                         std::to_string(sigOther.clearHit) + " 耗时=" + std::to_string(ms) + "ms");
            }
        }
        pDestroy(hp);
    }
    StopPresent();
    DestroyWindow(a.hwnd);
    DestroyWindow(b.hwnd);
    Pump();
}

// P0：对照探针——不换窗口、不最小化，只反复「建 handle → 抓一次 → 销毁 handle」。
//     用于判定崩溃是「DLL 句柄生命周期」问题还是 P1 特有的窗口时序问题。
static void ProbeHandleChurn(int trials, ProbeStat& st) {
    TestWindow w;
    if (!CreateFlipWindow(w, nullptr, 760, 120, 1.f, 0.f, 0.f, "probe0")) return;
    Pump(); Sleep(200); Pump();
    RenderOnce(w);
    Sleep(120);
    Pump();
    for (int i = 0; i < trials; ++i) {
        void* hp = pCreate();
        Frame f; std::string e;
        const int rc = Capture(hp, w.hwnd, f, e);
        ++st.trials;
        if (rc != WGC_OK) {
            ++st.rcBad;
            if (st.details.size() < 6)
                st.details.push_back("P0#" + std::to_string(i) + " rc=" + std::to_string(rc) + " " + e);
        }
        pDestroy(hp);
    }
    DestroyWindow(w.hwnd);
    Pump();
}

static int RunFlakyProbes(int trials, const std::string& only) {
    const bool all = only.empty();
    std::printf("\n########## 定向探针（--flaky-probe，每项 %d 次，选择=%s） ##########\n", trials,
                all ? "全部" : only.c_str());
    ProbeStat s0, s1, s2, s3;
    // 支持逗号分隔的组合（如 --probe-only P0,P2），用于隔离崩溃触发条件
    auto want = [&](const char* k) {
        return all || only == k || only.find(std::string(k) + ",") != std::string::npos ||
               only.find(std::string(",") + k) != std::string::npos;
    };
    if (want("P0")) { ProbeHandleChurn(trials, s0); ReportProbe("P0 建/销毁 handle 反复（对照）", s0); }
    if (want("P1")) {
        ProbeRebuildAfterChange(trials, s1);
        ReportProbe("P1 内容变更+会话重建后立即抓帧", s1);
    }
    if (want("P2")) {
        ProbeRepeatedRebuild(trials, s2);
        ReportProbe("P2 同 hwnd 反复重建会话（静态）", s2);
    }
    if (want("P3")) {
        ProbeAlternateWindows(trials, s3);
        ReportProbe("P3 两同尺寸窗口交替抓帧（校验内容）", s3);
    }
    const int bad = s0.wrongContent + s0.rcBad + s0.sizeBad + s1.wrongContent + s1.rcBad +
                    s1.sizeBad + s2.wrongContent + s2.rcBad + s2.sizeBad + s3.wrongContent +
                    s3.rcBad + s3.sizeBad;
    std::printf("  [探针汇总] 内容/rc/尺寸异常合计 = %d\n", bad);
    return bad;
}

/* ---------------- 崩溃取证：未处理异常过滤器 ---------------- */
// 探针模式下若发生访问违例，打印异常码 / 故障模块+偏移 / RIP，用于定位崩溃归属。
// （本机没有 cdb.exe，只能靠这种方式拿到「哪个模块的哪一段」。）
#include <psapi.h>
#pragma comment(lib, "psapi.lib")

static LONG WINAPI CrashFilter(EXCEPTION_POINTERS* ep) {
    const DWORD code = ep->ExceptionRecord->ExceptionCode;
    const void* addr = ep->ExceptionRecord->ExceptionAddress;
    char mod[MAX_PATH] = "(未知)";
    uintptr_t base = 0;
    HMODULE mods[128];
    DWORD need = 0;
    if (::EnumProcessModules(::GetCurrentProcess(), mods, sizeof(mods), &need)) {
        const DWORD n = need / sizeof(HMODULE);
        for (DWORD i = 0; i < n && i < 128; ++i) {
            MODULEINFO mi{};
            if (::GetModuleInformation(::GetCurrentProcess(), mods[i], &mi, sizeof(mi))) {
                const uintptr_t b = reinterpret_cast<uintptr_t>(mi.lpBaseOfDll);
                if (reinterpret_cast<uintptr_t>(addr) >= b &&
                    reinterpret_cast<uintptr_t>(addr) < b + mi.SizeOfImage) {
                    base = b;
                    ::GetModuleFileNameA(mods[i], mod, MAX_PATH);
                    break;
                }
            }
        }
    }
    std::fprintf(stderr, "[CRASH] code=0x%08X addr=%p module=%s+0x%llX RIP=%p RSP=%p\n", code, addr,
                 mod, static_cast<unsigned long long>(base ? reinterpret_cast<uintptr_t>(addr) - base : 0),
                 reinterpret_cast<void*>(ep->ContextRecord->Rip),
                 reinterpret_cast<void*>(ep->ContextRecord->Rsp));
    std::fflush(stderr);
    return EXCEPTION_EXECUTE_HANDLER;  // 直接结束，便于脚本读 rc
}

/* ---------------- main ---------------- */
static void PrintUsage() {
    std::printf("用法: selftest.exe [--repeat N] [--stop-on-fail] [--flaky-probe [N]] [--probe-only P1|P2|P3]\n"
                "  --repeat N          在同一进程内把整套用例跑 N 遍（默认 1）\n"
                "  --stop-on-fail      首遍失败即停\n"
                "  --flaky-probe [N]   只跑定向探针（P1/P2/P3），每项 N 次（默认 30）\n"
                "  --probe-only K      只跑指定探针（隔离崩溃/干扰用）\n");
}

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    SetProcessDPIAware();
    ::SetUnhandledExceptionFilter(CrashFilter);

    int repeat = 1;
    bool stopOnFail = false;
    int probeTrials = 0;  // >0 = 只跑定向探针
    std::string probeOnly;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--repeat") == 0 && i + 1 < argc) {
            repeat = std::atoi(argv[++i]);
        } else if (std::strcmp(argv[i], "--probe-only") == 0 && i + 1 < argc) {
            probeOnly = argv[++i];
        } else if (std::strcmp(argv[i], "--flaky-probe") == 0) {
            probeTrials = 30;
            if (i + 1 < argc && argv[i + 1][0] != '-') probeTrials = std::atoi(argv[++i]);
            if (probeTrials < 1) probeTrials = 1;
        } else if (std::strcmp(argv[i], "--stop-on-fail") == 0) {
            stopOnFail = true;
        } else if (std::strcmp(argv[i], "--help") == 0) {
            PrintUsage();
            return 0;
        } else {
            std::fprintf(stderr, "未知参数: %s\n", argv[i]);
            PrintUsage();
            return 2;
        }
    }
    if (repeat < 1) repeat = 1;

    const int pid = static_cast<int>(::GetCurrentProcessId());
    std::printf("=== 3FC.WgcCapture.dll 自测 ===  pid=%d repeat=%d\n", pid, repeat);
    const std::string osd0 = OsdProbe();
    std::printf("OSD/注入探测（只检测，不结束用户进程）: %s\n", osd0.c_str());

    /* --- 动态加载 DLL（整个进程一次） --- */
    char exePath[MAX_PATH]{};
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string dllPath(exePath);
    const size_t slash = dllPath.find_last_of("\\/");
    dllPath = (slash == std::string::npos) ? std::string() : dllPath.substr(0, slash + 1);
    dllPath += "3FC.WgcCapture.dll";
    std::printf("DLL: %s\n", dllPath.c_str());
    g_dll = LoadLibraryA(dllPath.c_str());
    if (!g_dll) {
        std::printf("    FATAL: LoadLibrary 失败, GetLastError=%lu\n", GetLastError());
        return 2;
    }

    /* --- 共享 D3D11 资源（整个进程一次） --- */
    if (!InitSharedD3D()) return 2;

    // 定向探针模式：只跑探针，不跑 76 项默认用例（两者互不影响）。
    if (probeTrials > 0) {
        int missing = 0;
        struct { const char* name; FARPROC* slot; } exports[] = {
            {"Wgc_IsSupported", reinterpret_cast<FARPROC*>(&pIsSupported)},
            {"Wgc_Create", reinterpret_cast<FARPROC*>(&pCreate)},
            {"Wgc_Destroy", reinterpret_cast<FARPROC*>(&pDestroy)},
            {"Wgc_CaptureFrame", reinterpret_cast<FARPROC*>(&pCaptureFrame)},
            {"Wgc_FreeFrame", reinterpret_cast<FARPROC*>(&pFreeFrame)},
            {"Wgc_LastError", reinterpret_cast<FARPROC*>(&pLastError)},
        };
        for (auto& e : exports) {
            *e.slot = GetProcAddress(g_dll, e.name);
            if (!*e.slot) ++missing;
        }
        if (missing) {
            std::printf("FATAL: 探针模式导出解析失败（缺 %d 个）\n", missing);
            return 2;
        }
        // 与默认套件对齐：先调一次 IsSupported（默认套件 [2] 会调）。用于判定崩溃是否与
        // 「是否先做过一次 WinRT 激活」有关。
        std::printf("    Wgc_IsSupported() = %d\n", pIsSupported());
        // 诊断开关：先建一个**永不销毁**的保活 handle，用于判定崩溃是否由
        // 「进程内句柄数归零（最后一个捕获器被销毁）」触发。
        void* keepAlive = nullptr;
        if (::GetEnvironmentVariableA("WGC_PROBE_KEEPALIVE", nullptr, 0) > 0) {
            keepAlive = pCreate();
            std::printf("    [诊断] 保活 handle = %p（不销毁）\n", keepAlive);
        }
        const int bad = RunFlakyProbes(probeTrials, probeOnly);
        (void)keepAlive;
        ReleaseSharedD3D();
        FreeLibrary(g_dll);
        return bad == 0 ? 0 : 1;
    }

    int totalPass = 0, totalFail = 0, failedIters = 0;
    for (int it = 1; it <= repeat; ++it) {
        const int p0 = g_pass, f0 = g_fail;
        g_failures.clear();
        g_msFirstCapture = g_msStatic1 = g_msStatic2 = g_msB1Rebuild = -1;

        const std::string iterLog = "selftest_iter_" + std::to_string(pid) + ".log";
        LogCapture lc;
        const bool captured = lc.Begin(iterLog);
        std::printf("\n########## 第 %d/%d 遍 ##########\n", it, repeat);
        const int rc = RunOnce();
        // FATAL 早退**必须计入失败**（S2 假绿堵口）：RunOnce 里所有「初始化失败」都是裸
        // `return 2`（建窗口失败、创建 swapchain 失败、显存不足……），只要命中一处，本遍用例
        // 一条都没跑完。旧代码在这里只 break、不计数，而最终 `return failedIters == 0 ? 0 : 1`
        // ⇒ 返回 0；构建脚本与门禁只看退出码 ⇒「一条都没跑完」被当成「全部通过」。
        // 这里把它折算成一条失败明细，后续的 df>0 分支会自动保存/回显日志并累加 failedIters。
        if (rc != 0) {
            ++g_fail;
            g_failures.push_back(std::string(g_case) + " :: FATAL 中止：RunOnce 以 rc=" +
                                 std::to_string(rc) + " 提前返回，本遍用例未跑完");
            std::fprintf(stderr,
                         "FATAL：第 %d/%d 遍 RunOnce 以 rc=%d 中止（初始化失败），套件未完成\n", it,
                         repeat, rc);
            std::fflush(stderr);
        }
        const int dp = g_pass - p0, df = g_fail - f0;
        std::printf("\n=== 第 %d 遍结果：%d 项通过, %d 项失败 ===\n", it, dp, df);
        if (df > 0) {
            std::printf("---- 失败明细（%d 条）----\n", df);
            for (const auto& s : g_failures) std::printf("  * %s\n", s.c_str());
        }
        lc.End();

        totalPass += dp;
        totalFail += df;
        const std::string osd = OsdProbe();
        if (df > 0) {
            ++failedIters;
            const std::string failName =
                "selftest_fail_" + Timestamp() + "_iter" + std::to_string(it) + ".log";
            std::remove(failName.c_str());
            if (captured && std::rename(iterLog.c_str(), failName.c_str()) == 0) {
                std::fprintf(stderr, "[iter %d/%d] 失败 %d 项 -> 完整输出已保存 %s\n", it, repeat, df,
                             failName.c_str());
                EchoFile(failName);  // 失败现场立即回显（含逐项 [FAIL] 明细）
            } else {
                std::fprintf(stderr, "[iter %d/%d] 失败 %d 项（日志改名失败，临时文件 %s）\n", it,
                             repeat, df, iterLog.c_str());
                EchoFile(iterLog);
            }
            for (const auto& s : g_failures) std::fprintf(stderr, "    FAIL %s\n", s.c_str());
        } else if (captured) {
            if (repeat == 1) EchoFile(iterLog);  // 单遍运行：完整逐项明细照常显示
            std::remove(iterLog.c_str());
        }

        std::fprintf(stderr,
                     "[iter %d/%d] 通过 %d 失败 %d | 首次抓帧 %dms 静态1 %dms 静态2 %dms B1重建 %dms | %s\n",
                     it, repeat, dp, df, g_msFirstCapture, g_msStatic1, g_msStatic2, g_msB1Rebuild,
                     osd.c_str());
        std::fflush(stderr);

        // FATAL 早退已在上面折算成失败明细（df>0 分支已保存/回显日志并累加 failedIters），
        // 这里只需无条件中止后续迭代：初始化都失败了，后面的遍只会同样失败。
        if (rc != 0) break;
        if (df > 0 && stopOnFail) break;
    }

    std::printf("\n===== 汇总：共 %d 遍，失败遍数 %d，累计通过 %d / 累计失败 %d =====\n", repeat,
                failedIters, totalPass, totalFail);
    std::fprintf(stderr, "===== 汇总：共 %d 遍，失败遍数 %d，累计通过 %d / 累计失败 %d =====\n",
                 repeat, failedIters, totalPass, totalFail);

    ReleaseSharedD3D();
    FreeLibrary(g_dll);
    return failedIters == 0 ? 0 : 1;
}
