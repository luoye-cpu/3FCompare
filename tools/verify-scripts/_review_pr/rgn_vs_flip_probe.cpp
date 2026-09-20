// rgn_vs_flip_probe.cpp
//
// 问题：SetWindowRgn 能否真的裁掉 D3D11 flip-model swapchain 窗口的画面？
//
// 之前的验证（clip_probe2.cpp）只证明了「设置区域后 Present 仍 S_OK、设备不丢」，
// 没有证明画面真的被裁掉——因为 BitBlt/GDI 抓屏对 flip-model 不可靠。
// 本探针用 WGC（已验证能正确捕获 flip-model 内容、且不受遮挡影响）做观测。
//
// 设计（关键：捕获对象不是被设区域的那个窗口，而是它的父窗口）：
//   父窗口（顶层 WS_POPUP，客户区 720x580，背景纯绿）
//     └── 子窗口（WS_CHILD @(160,140) 400x300），绑定 flip-model swapchain，持续 Present 纯红
//   用 WGC 捕获【父窗口】→ 采样：
//     [A] 对照：不设区域            → 子窗口整块应为红
//     [B] 实验：SetWindowRgn(子窗口, 中间矩形 60,60-340,240)
//              → 区域内应仍为红；区域外（子窗口范围内）若变绿 ⇒ 裁剪生效 ✅
//                                                   若仍为红 ⇒ 裁剪无效 ❌（DWM 绕过了窗口区域）
//     [C] 还原：SetWindowRgn(子窗口, NULL) → 应恢复全红（证明 B 的变化确实来自区域）
//
// 采样点全部避开子窗口左上角（本机 RTSS/MSI Afterburner 的 OSD 会注入 swapchain 污染那一带，
// 见 docs/26 §八），只取「中心/中右/中下/右下」与「底部带/右侧带」。多点 + 多数表决。
//
// 复用 FFF.Native 的真实 swapchain 配置（VideoRenderer.cpp:2229-2233）：
//   CreateSwapChainForHwnd + FLIP_DISCARD + BufferCount=2 + ALPHA_MODE_IGNORE + SCALING_NONE

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <d3d11.h>
#include <dxgi1_2.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <Windows.Graphics.Capture.Interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "windowsapp.lib")

namespace {

// ---------------- 布局常量（父窗口客户区坐标系） ----------------
constexpr int kParentW = 720;
constexpr int kParentH = 580;
constexpr int kChildX = 160;
constexpr int kChildY = 140;
constexpr int kChildW = 400;
constexpr int kChildH = 300;

// 实验用的窗口区域（子窗口自身坐标系）：中间矩形，四边各内缩 60px
constexpr int kRgnL = 60, kRgnT = 60, kRgnR = 340, kRgnB = 240;

constexpr unsigned char kRedR = 255, kRedG = 0, kRedB = 0;
constexpr unsigned char kGrnR = 0, kGrnG = 255, kGrnB = 0;

HWND g_parent = nullptr;
HWND g_child = nullptr;

ID3D11Device* g_dev = nullptr;
ID3D11DeviceContext* g_ctx = nullptr;
IDXGISwapChain1* g_chain = nullptr;
ID3D11RenderTargetView* g_rtv = nullptr;

long g_presentCalls = 0;
long g_presentFails = 0;
HRESULT g_lastPresentHr = S_OK;

// ---------------- 窗口过程 ----------------

LRESULT CALLBACK ParentProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_ERASEBKGND:
    case WM_PAINT: {
        // 整块客户区涂纯绿。刻意不用 WS_CLIPCHILDREN：
        // 子窗口区域缩小后要能立刻露出这层绿色。
        HDC dc;
        PAINTSTRUCT ps;
        bool inPaint = (m == WM_PAINT);
        if (inPaint) {
            dc = BeginPaint(h, &ps);
        } else {
            dc = reinterpret_cast<HDC>(w);
        }
        RECT r;
        GetClientRect(h, &r);
        HBRUSH br = CreateSolidBrush(RGB(kGrnR, kGrnG, kGrnB));
        FillRect(dc, &r, br);
        DeleteObject(br);
        if (inPaint) EndPaint(h, &ps);
        return inPaint ? 0 : 1;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcA(h, m, w, l);
    }
}

LRESULT CALLBACK ChildProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(h, &ps);
        RECT r;
        GetClientRect(h, &r);
        FillRect(dc, &r, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        EndPaint(h, &ps);
        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcA(h, m, w, l);
    }
}

void Pump() {
    MSG m;
    while (PeekMessageA(&m, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&m);
        DispatchMessageA(&m);
    }
}

void PresentRed() {
    const float c[4] = {1.f, 0.f, 0.f, 1.f};
    g_ctx->OMSetRenderTargets(1, &g_rtv, nullptr);
    g_ctx->ClearRenderTargetView(g_rtv, c);
    const HRESULT hr = g_chain->Present(1, 0);
    ++g_presentCalls;
    if (FAILED(hr)) ++g_presentFails;
    g_lastPresentHr = hr;
}

// 持续出帧 + 抽消息，维持指定时长
void Spin(int ms) {
    const DWORD t0 = GetTickCount();
    while (static_cast<int>(GetTickCount() - t0) < ms) {
        PresentRed();
        Pump();
        Sleep(1);
    }
    Pump();
}

// ---------------- WGC 会话（对父窗口，全程复用同一个会话） ----------------

struct WgcSession {
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool{nullptr};
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session{nullptr};
    ID3D11Texture2D* staging = nullptr;
    UINT sw = 0, sh = 0;

    void Start(HWND hwnd,
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice const& rtDev) {
        using namespace winrt::Windows::Graphics::Capture;
        auto interop =
            winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(
            hwnd, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)));
        const auto size = item.Size();
        printf("capture item     : %dx%d\n", size.Width, size.Height);
        pool = Direct3D11CaptureFramePool::Create(
            rtDev, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, size);
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled(false);
        try {
            session.IsBorderRequired(false);
            printf("border required  : off\n");
        } catch (winrt::hresult_error const&) {
            printf("border required  : on（无法关闭）\n");
        }
        session.StartCapture();
    }

    // 取「最新」一帧（把帧池里的陈旧帧全部排干，只留最后一个）
    bool Latest(std::vector<unsigned char>& bits, int& outW, int& outH) {
        auto frame = pool.TryGetNextFrame();
        if (!frame) return false;
        for (;;) {
            auto next = pool.TryGetNextFrame();
            if (!next) break;
            frame = next;
        }

        auto surf = frame.Surface();
        winrt::com_ptr<ID3D11Texture2D> tex;
        auto access =
            surf.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), tex.put_void()));

        D3D11_TEXTURE2D_DESC d{};
        tex->GetDesc(&d);
        if (staging == nullptr || sw != d.Width || sh != d.Height) {
            if (staging != nullptr) { staging->Release(); staging = nullptr; }
            D3D11_TEXTURE2D_DESC s = d;
            s.Usage = D3D11_USAGE_STAGING;
            s.BindFlags = 0;
            s.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            s.MiscFlags = 0;
            winrt::check_hresult(g_dev->CreateTexture2D(&s, nullptr, &staging));
            sw = d.Width;
            sh = d.Height;
        }
        g_ctx->CopyResource(staging, tex.get());

        D3D11_MAPPED_SUBRESOURCE ms{};
        winrt::check_hresult(g_ctx->Map(staging, 0, D3D11_MAP_READ, 0, &ms));
        outW = static_cast<int>(d.Width);
        outH = static_cast<int>(d.Height);
        bits.resize(static_cast<size_t>(outW) * outH * 4);
        for (int y = 0; y < outH; ++y) {
            std::memcpy(&bits[static_cast<size_t>(y) * outW * 4],
                static_cast<const unsigned char*>(ms.pData) +
                    static_cast<size_t>(y) * ms.RowPitch,
                static_cast<size_t>(outW) * 4);
        }
        g_ctx->Unmap(staging, 0);
        frame.Close();
        return true;
    }
};

// ---------------- 采样 ----------------

struct Pix { unsigned char b, g, r, a; };

struct Shot {
    std::vector<unsigned char> bits;
    int w = 0, h = 0;

    // 输入是「父窗口客户区」坐标，按捕获尺寸自动缩放
    Pix At(int px, int py) const {
        int x = static_cast<int>(px * static_cast<double>(w) / kParentW);
        int y = static_cast<int>(py * static_cast<double>(h) / kParentH);
        if (x < 0) x = 0; if (x >= w) x = w - 1;
        if (y < 0) y = 0; if (y >= h) y = h - 1;
        const unsigned char* p = &bits[(static_cast<size_t>(y) * w + x) * 4];
        return Pix{p[0], p[1], p[2], p[3]};
    }

    // 返回矩形内「精确等于某色」的比例
    double CoverRect(int px0, int py0, int px1, int py1,
                     unsigned char R, unsigned char G, unsigned char B) const {
        int x0 = static_cast<int>(px0 * static_cast<double>(w) / kParentW);
        int x1 = static_cast<int>(px1 * static_cast<double>(w) / kParentW);
        int y0 = static_cast<int>(py0 * static_cast<double>(h) / kParentH);
        int y1 = static_cast<int>(py1 * static_cast<double>(h) / kParentH);
        if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
        if (x1 > w) x1 = w; if (y1 > h) y1 = h;
        long long hit = 0, tot = 0;
        for (int y = y0; y < y1; ++y) {
            const unsigned char* row = &bits[(static_cast<size_t>(y) * w) * 4];
            for (int x = x0; x < x1; ++x) {
                const unsigned char* p = row + static_cast<size_t>(x) * 4;
                if (p[2] == R && p[1] == G && p[0] == B) ++hit;
                ++tot;
            }
        }
        return tot ? static_cast<double>(hit) / static_cast<double>(tot) : 0.0;
    }
};

const char* Classify(const Pix& p) {
    if (p.r >= 200 && p.g <= 60 && p.b <= 60) return "RED  ";
    if (p.g >= 200 && p.r <= 60 && p.b <= 60) return "GREEN";
    return "OTHER";
}

char ClassifyChar(const Pix& p) {
    if (p.r >= 200 && p.g <= 60 && p.b <= 60) return '#';   // 子窗口红色画面
    if (p.g >= 200 && p.r <= 60 && p.b <= 60) return 'G';   // 父窗口绿色背景
    if (p.r < 40 && p.g < 40 && p.b < 40) return '.';       // 黑（叠加层 / 未绘制）
    return '?';
}

// 存 32 位 BMP（top-down），便于人眼核对
void SaveBmp(const char* path, const std::vector<unsigned char>& bits, int w, int h) {
    const UINT pixBytes = static_cast<UINT>(w) * h * 4;
    const UINT headerBytes = 14 + 40;
    BITMAPFILEHEADER fh{};
    fh.bfType = 0x4D42;
    fh.bfOffBits = headerBytes;
    fh.bfSize = headerBytes + pixBytes;
    BITMAPINFOHEADER ih{};
    ih.biSize = sizeof(ih);
    ih.biWidth = w;
    ih.biHeight = -h;
    ih.biPlanes = 1;
    ih.biBitCount = 32;
    ih.biCompression = BI_RGB;
    ih.biSizeImage = pixBytes;
    FILE* f = nullptr;
    if (fopen_s(&f, path, "wb") != 0 || f == nullptr) return;
    fwrite(&fh, sizeof(fh), 1, f);
    fwrite(&ih, sizeof(ih), 1, f);
    fwrite(bits.data(), 4, static_cast<size_t>(w) * h, f);
    fclose(f);
}

struct Pt {
    std::string name;
    int x, y;    // 父窗口客户区坐标
    int kind;    // 0 = 父窗口区域(子窗口外) 1 = 子窗口内·区域内 2 = 子窗口内·区域外
};

std::vector<Pt> BuildPoints() {
    std::vector<Pt> v;
    auto add = [&](const char* n, int rx, int ry, int kind) {
        v.push_back({n, kChildX + rx, kChildY + ry, kind});
    };
    // 父窗口自己的区域（子窗口之外），恒定应为绿 —— 用来证明「绿色通道」是通的
    v.push_back({"父窗口(子窗外)左上", 40, 40, 0});
    v.push_back({"父窗口(子窗外)右下", kParentW - 40, kParentH - 40, 0});
    // 区域内（子窗口坐标 60..340 x 60..240）：全部取中心及右下，避开左上 OSD
    add("区域内 中心       ", 200, 150, 1);
    add("区域内 中右       ", 300, 150, 1);
    add("区域内 中下       ", 200, 220, 1);
    add("区域内 右下       ", 300, 220, 1);
    // 区域外（子窗口范围内）：只取底部带与右侧带，避开左上 OSD
    add("区域外 左下       ", 50, 275, 2);
    add("区域外 底部中     ", 200, 275, 2);
    add("区域外 底部右     ", 330, 275, 2);
    add("区域外 右侧中     ", 375, 150, 2);
    add("区域外 右侧下     ", 375, 220, 2);
    return v;
}

struct PhaseResult {
    int redInRegion = 0, totInRegion = 0;
    int grnOutRegion = 0, redOutRegion = 0, otherOutRegion = 0;
    int grnParent = 0, totParent = 0;
    double bandBottomRed = 0, bandRightRed = 0;
    double interiorRed = 0;
    bool got = false;
};

void RunPhase(const char* tag, const char* title, WgcSession& cap, const std::vector<Pt>& pts,
              PhaseResult& out) {
    printf("\n--- %s ---\n", title);
    printf("present          : calls=%ld fails=%ld lastHR=0x%08lX\n",
        g_presentCalls, g_presentFails, static_cast<unsigned long>(g_lastPresentHr));

    // 排干旧帧，拿到区域生效后的最新帧
    std::vector<unsigned char> tmp;
    int tw = 0, th = 0;
    for (int i = 0; i < 3; ++i) {
        Spin(120);
        if (!cap.Latest(tmp, tw, th)) { printf("WGC 未取到帧\n"); return; }
    }

    Shot s;
    s.bits = std::move(tmp);
    s.w = tw;
    s.h = th;
    out.got = true;

    printf("frame            : %dx%d\n", s.w, s.h);
    printf("  %-20s %-16s %-10s %s\n", "采样点", "父窗口客户区坐标", "RGB", "判定");
    for (const auto& p : pts) {
        const Pix c = s.At(p.x, p.y);
        const char* cls = Classify(c);
        if (p.kind == 0) { ++out.totParent; if (std::strcmp(cls, "GREEN") == 0) ++out.grnParent; }
        if (p.kind == 1) { ++out.totInRegion; if (std::strcmp(cls, "RED  ") == 0) ++out.redInRegion; }
        if (p.kind == 2) {
            if (std::strcmp(cls, "GREEN") == 0) ++out.grnOutRegion;
            else if (std::strcmp(cls, "RED  ") == 0) ++out.redOutRegion;
            else ++out.otherOutRegion;
        }
        printf("  %-20s (%3d,%3d)        (%3u,%3u,%3u) %s\n", p.name.c_str(), p.x, p.y,
            c.r, c.g, c.b, cls);
    }

    // 区间覆盖率（子窗口坐标 → 父窗口坐标）
    out.interiorRed = s.CoverRect(kChildX + 70, kChildY + 70, kChildX + 330, kChildY + 230,
                                  kRedR, kRedG, kRedB);
    out.bandBottomRed = s.CoverRect(kChildX + 10, kChildY + 250, kChildX + 390, kChildY + 295,
                                    kRedR, kRedG, kRedB);
    out.bandRightRed = s.CoverRect(kChildX + 350, kChildY + 65, kChildX + 395, kChildY + 240,
                                   kRedR, kRedG, kRedB);

    printf("  覆盖率(红)      : 区域内=%6.2f%%  区域外·底部带=%6.2f%%  区域外·右侧带=%6.2f%%\n",
        out.interiorRed * 100, out.bandBottomRed * 100, out.bandRightRed * 100);
    printf("  点统计          : 区域内红=%d/%d  区域外绿=%d 红=%d 其它=%d  父窗口绿=%d/%d\n",
        out.redInRegion, out.totInRegion, out.grnOutRegion, out.redOutRegion,
        out.otherOutRegion, out.grnParent, out.totParent);

    // 子窗口范围 ASCII 图（# 红 / G 绿 / . 黑 / ? 其它），直观看出裁剪边界
    printf("  子窗口区 ASCII 图（# 子窗口红画面 / G 父窗口绿 / . 黑 / ? 其它）：\n");
    {
        const int cols = 40, rows = 15;
        for (int ry = 0; ry < rows; ++ry) {
            printf("    ");
            for (int rx = 0; rx < cols; ++rx) {
                const int cx = rx * kChildW / cols + kChildW / (2 * cols);
                const int cy = ry * kChildH / rows + kChildH / (2 * rows);
                putchar(ClassifyChar(s.At(kChildX + cx, kChildY + cy)));
            }
            putchar('\n');
        }
    }

    // 垂直扫描：子窗口 x=300，y=20..290 步长 10 —— 直接量出红/绿交界（裁剪边界）
    printf("  垂直扫描 子窗口x=300 (y: 20->290, 步长10；交界落在相邻两点之间):\n    ");
    {
        const int x = kChildX + 300;
        char prev = 0;
        int prevY = 0;
        for (int cy = 20; cy <= 290; cy += 10) {
            const Pix p = s.At(x, kChildY + cy);
            const char c = ClassifyChar(p);
            printf("y%d=%c(%u,%u,%u) ", cy, c, p.r, p.g, p.b);
            if (prev != 0 && prev != c) printf("[交界在 %d..%d] ", prevY, cy);
            prev = c;
            prevY = cy;
        }
        printf("\n");
    }

    // 水平扫描：子窗口 y=220，x=20..380 步长 10
    printf("  水平扫描 子窗口y=220 (x: 20->380, 步长10):\n    ");
    {
        const int y = kChildY + 220;
        char prev = 0;
        int prevX = 0;
        for (int cx = 20; cx <= 380; cx += 10) {
            const Pix p = s.At(kChildX + cx, y);
            const char c = ClassifyChar(p);
            printf("x%d=%c(%u,%u,%u) ", cx, c, p.r, p.g, p.b);
            if (prev != 0 && prev != c) printf("[交界在 %d..%d] ", prevX, cx);
            prev = c;
            prevX = cx;
        }
        printf("\n");
    }

    char path[128];
    sprintf_s(path, "rgn_phase_%s.bmp", tag);
    SaveBmp(path, s.bits, s.w, s.h);
    printf("  已存图          : %s\n", path);

    // 定位捕获坐标系：跨「子窗口左边缘 / 上边缘」扫描（绿→红交界应落在子窗口坐标 0）。
    // 与上面的区域边界对照，可区分「区域设偏了」与「WGC 采集整体偏移」。
    printf("  跨子窗口左边缘 (子窗口y=230, x=-40..40, 步长5):\n    ");
    for (int cx = -40; cx <= 40; cx += 5) {
        const Pix p = s.At(kChildX + cx, kChildY + 230);
        printf("x%d=%c(%u,%u,%u) ", cx, ClassifyChar(p), p.r, p.g, p.b);
    }
    printf("\n  跨子窗口上边缘 (子窗口x=300, y=-40..40, 步长5):\n    ");
    for (int cy = -40; cy <= 40; cy += 5) {
        const Pix p = s.At(kChildX + 300, kChildY + cy);
        printf("y%d=%c(%u,%u,%u) ", cy, ClassifyChar(p), p.r, p.g, p.b);
    }
    printf("\n");
}

} // namespace

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    printf("=== SetWindowRgn vs D3D11 flip-model swapchain ===\n");

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (winrt::hresult_error const& e) {
        printf("FATAL: init_apartment 0x%08X\n", static_cast<unsigned>(e.code()));
        return 2;
    }
    printf("WGC IsSupported  : %s\n",
        winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported() ? "true"
                                                                                 : "false");

    // ---- 父窗口（顶层，无边框，客户区 720x580） ----
    WNDCLASSA wcp{};
    wcp.lpfnWndProc = ParentProc;
    wcp.hInstance = GetModuleHandleA(nullptr);
    wcp.lpszClassName = "RgnProbeParent";
    wcp.hbrBackground = nullptr;   // 自己涂绿
    RegisterClassA(&wcp);

    g_parent = CreateWindowExA(0, "RgnProbeParent", "rgn probe parent", WS_POPUP | WS_VISIBLE,
        60, 40, kParentW, kParentH, nullptr, nullptr, wcp.hInstance, nullptr);
    if (g_parent == nullptr) { printf("FATAL: 父窗口创建失败\n"); return 2; }
    RECT pr{};
    GetClientRect(g_parent, &pr);
    printf("parent           : hwnd=%p client=%dx%d @(60,40)\n", g_parent,
        static_cast<int>(pr.right), static_cast<int>(pr.bottom));
    InvalidateRect(g_parent, nullptr, TRUE);
    UpdateWindow(g_parent);
    Pump();

    // ---- 子窗口（WS_CHILD，400x300） ----
    WNDCLASSA wcc{};
    wcc.lpfnWndProc = ChildProc;
    wcc.hInstance = wcp.hInstance;
    wcc.lpszClassName = "RgnProbeChild";
    wcc.hbrBackground = nullptr;
    RegisterClassA(&wcc);

    g_child = CreateWindowExA(0, "RgnProbeChild", "rgn probe child",
        WS_CHILD | WS_VISIBLE, kChildX, kChildY, kChildW, kChildH, g_parent, nullptr,
        wcc.hInstance, nullptr);
    if (g_child == nullptr) { printf("FATAL: 子窗口创建失败\n"); return 2; }
    RECT cr{};
    GetClientRect(g_child, &cr);
    printf("child            : hwnd=%p client=%dx%d parent-rel(%d,%d)\n", g_child,
        static_cast<int>(cr.right), static_cast<int>(cr.bottom), kChildX, kChildY);
    UpdateWindow(g_child);
    Pump();

    // ---- D3D11 设备 + flip-model swapchain（严格复刻 VideoRenderer.cpp:2229-2233） ----
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0};
    D3D_FEATURE_LEVEL got{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels,
        ARRAYSIZE(levels), D3D11_SDK_VERSION, &g_dev, &got, &g_ctx);
    if (FAILED(hr)) { printf("FATAL: D3D11CreateDevice 0x%08X\n", hr); return 2; }

    winrt::com_ptr<IDXGIDevice> dxgiDevice;
    winrt::com_ptr<IDXGIAdapter> adapter;
    winrt::com_ptr<IDXGIFactory2> factory;
    winrt::check_hresult(g_dev->QueryInterface(IID_PPV_ARGS(dxgiDevice.put())));
    winrt::check_hresult(dxgiDevice->GetAdapter(adapter.put()));
    winrt::check_hresult(adapter->GetParent(IID_PPV_ARGS(factory.put())));

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(cr.right - cr.left);
    desc.Height = static_cast<UINT>(cr.bottom - cr.top);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    desc.Scaling = DXGI_SCALING_NONE;
    hr = factory->CreateSwapChainForHwnd(g_dev, g_child, &desc, nullptr, nullptr, &g_chain);
    if (FAILED(hr)) { printf("FATAL: CreateSwapChainForHwnd(child) 0x%08X\n", hr); return 2; }
    printf("swapchain        : %ux%u FLIP_DISCARD buf=2 alpha=IGNORE scaling=NONE (on CHILD hwnd)\n",
        desc.Width, desc.Height);

    winrt::com_ptr<ID3D11Texture2D> back;
    winrt::check_hresult(g_chain->GetBuffer(0, IID_PPV_ARGS(back.put())));
    winrt::check_hresult(g_dev->CreateRenderTargetView(back.get(), nullptr, &g_rtv));

    // ---- WGC 会话：捕获【父窗口】 ----
    winrt::com_ptr<::IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()));
    auto rtDev = inspectable.as<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();

    WgcSession cap;
    try {
        cap.Start(g_parent, rtDev);
    } catch (winrt::hresult_error const& e) {
        printf("FATAL: WGC CreateForWindow(parent) 0x%08X\n", static_cast<unsigned>(e.code()));
        return 2;
    }

    const std::vector<Pt> pts = BuildPoints();
    printf("采样点数         : %zu（全部避开子窗口左上角 OSD 污染区）\n", pts.size());
    printf("子窗口区域(实验) : 子窗口坐标 (%d,%d)-(%d,%d)\n", kRgnL, kRgnT, kRgnR, kRgnB);

    Spin(900);

    PhaseResult a, b, c;

    // ================= [A] 对照：不设区域 =================
    RunPhase("A", "[A] 对照：子窗口未设置任何窗口区域", cap, pts, a);

    // ================= [B] 实验：SetWindowRgn(子窗口, 中间矩形) =================
    printf("\n--- [B] 实验：SetWindowRgn(子窗口, 中间矩形) ---\n");
    {
        HRGN rgn = CreateRectRgn(kRgnL, kRgnT, kRgnR, kRgnB);
        const int ok = SetWindowRgn(g_child, rgn, TRUE);   // TRUE: 立即重绘
        printf("SetWindowRgn 返回 : %d (非0=成功；rgn 所有权已交给系统)\n", ok);
        printf("GetLastError      : %lu\n", GetLastError());

        HRGN q = CreateRectRgn(0, 0, 0, 0);
        const int type = GetWindowRgn(g_child, q);
        RECT rb{};
        if (type != ERROR) GetRgnBox(q, &rb);
        printf("GetWindowRgn 校验 : type=%d box=(%ld,%ld)-(%ld,%ld)\n", type,
            rb.left, rb.top, rb.right, rb.bottom);
        DeleteObject(q);
    }
    InvalidateRect(g_parent, nullptr, TRUE);
    RedrawWindow(g_parent, nullptr, nullptr,
        RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW);
    Pump();
    RunPhase("B", "[B] 实验：已设置窗口区域（子窗口坐标 60,60-340,240）", cap, pts, b);

    // ================= [C] 还原：清掉区域 =================
    printf("\n--- [C] 还原：SetWindowRgn(子窗口, NULL) ---\n");
    {
        const int ok = SetWindowRgn(g_child, nullptr, TRUE);
        printf("SetWindowRgn(NULL) 返回 : %d\n", ok);
    }
    InvalidateRect(g_parent, nullptr, TRUE);
    RedrawWindow(g_parent, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW);
    Pump();
    RunPhase("C", "[C] 还原：窗口区域已清除", cap, pts, c);

    // ================= 判定 =================
    printf("\n=== 判定 ===\n");
    printf("A 对照   : 区域内红 %d/%d  区域外绿 %d/5  区域外红 %d/5  | 覆盖: 区域内%.1f%% 底带%.1f%% 右带%.1f%%\n",
        a.redInRegion, a.totInRegion, a.grnOutRegion, a.redOutRegion,
        a.interiorRed * 100, a.bandBottomRed * 100, a.bandRightRed * 100);
    printf("B 实验   : 区域内红 %d/%d  区域外绿 %d/5  区域外红 %d/5  | 覆盖: 区域内%.1f%% 底带%.1f%% 右带%.1f%%\n",
        b.redInRegion, b.totInRegion, b.grnOutRegion, b.redOutRegion,
        b.interiorRed * 100, b.bandBottomRed * 100, b.bandRightRed * 100);
    printf("C 还原   : 区域内红 %d/%d  区域外绿 %d/5  区域外红 %d/5  | 覆盖: 区域内%.1f%% 底带%.1f%% 右带%.1f%%\n",
        c.redInRegion, c.totInRegion, c.grnOutRegion, c.redOutRegion,
        c.interiorRed * 100, c.bandBottomRed * 100, c.bandRightRed * 100);
    printf("present 全程: calls=%ld fails=%ld lastHR=0x%08lX\n", g_presentCalls, g_presentFails,
        static_cast<unsigned long>(g_lastPresentHr));

    // 判据说明：
    //   区域外（子窗口范围内、区域外）是本实验的唯一有效判据 —— 变绿=裁剪生效。
    //   区域内不作「100% 红」要求：本机第三方 OSD 会在 swapchain 中心/左上注入，
    //   故只要求区域内仍以红为主（≥3/4 点 + 覆盖率 > 50%），用来排除「整个画面全丢」。
    const bool okA = a.got && a.redOutRegion >= 4 && a.bandBottomRed > 0.9 &&
                     a.bandRightRed > 0.9 && a.redInRegion >= 3;
    const bool okC = c.got && c.redOutRegion >= 4 && c.bandBottomRed > 0.9 &&
                     c.bandRightRed > 0.9 && c.redInRegion >= 3;
    const bool clipB = b.got && b.grnOutRegion >= 4 && b.redOutRegion == 0 &&
                       b.bandBottomRed < 0.05 && b.bandRightRed < 0.05 && b.redInRegion >= 3;
    const bool stillRedB = b.got && b.redOutRegion >= 4 && b.bandBottomRed > 0.95 &&
                           b.bandRightRed > 0.95;

    if (!okA) {
        printf("结论：无法判定 ❌ —— 对照组（未设区域）就没能观察到「子窗口整块为红」，\n");
        printf("      说明 WGC 抓父窗口没能拿到子窗口的 flip-model 内容，实验设计需调整。\n");
    } else if (clipB) {
        printf("结论：SetWindowRgn 对 flip-model 画面【生效】✅\n");
        printf("      依据1：实验组区域外 5 点中 %d 点变绿、区域外覆盖红率底带 %.1f%%/右带 %.1f%% ≈ 0；\n",
            b.grnOutRegion, b.bandBottomRed * 100, b.bandRightRed * 100);
        printf("      依据2：同一帧里区域内仍是红（覆盖 %.1f%%），说明不是整个 swapchain 丢失；\n",
            b.interiorRed * 100);
        printf("      依据3：清掉区域后 [C] 区域外恢复为红（%d/5 红），变化确由区域引起；\n",
            c.redOutRegion);
        printf("      依据4：全过程 Present 无失败（fails=%ld），无需重建 swapchain。\n",
            g_presentFails);
    } else if (stillRedB) {
        printf("结论：SetWindowRgn 对 flip-model 画面【不生效】❌\n");
        printf("      依据：设置区域后，区域外 5 点仍全部为红、区域外覆盖红率 %.1f%%/%.1f%% ≈ 100%%；\n",
            b.bandBottomRed * 100, b.bandRightRed * 100);
        printf("      即 DWM 合成 flip-model 交换链时绕过了窗口区域，画面未被裁掉。\n");
    } else {
        printf("结论：结果【不确定】⚠ —— 区域外既非稳定绿也非稳定红（绿=%d 红=%d 其它=%d），\n",
            b.grnOutRegion, b.redOutRegion, b.otherOutRegion);
        printf("      可能是部分裁剪 / 采样受污染 / 帧未及时更新，见上方原始采样值。\n");
    }

    if (cap.staging != nullptr) cap.staging->Release();
    try { cap.session.Close(); } catch (...) {}
    try { cap.pool.Close(); } catch (...) {}
    if (g_rtv) g_rtv->Release();
    if (g_chain) g_chain->Release();
    if (g_ctx) g_ctx->Release();
    if (g_dev) g_dev->Release();
    if (g_child) DestroyWindow(g_child);
    if (g_parent) DestroyWindow(g_parent);
    return 0;
}
