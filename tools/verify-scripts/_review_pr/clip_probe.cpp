// 判别实验：SetWindowRgn 能否裁剪 D3D11 flip-model swapchain 的内容？
//
// 复刻内核的真实 swapchain 配置（VideoRenderer.cpp:2229-2233, 4752）：
//   IDXGIFactory2::CreateSwapChainForHwnd
//   BufferCount=2, DXGI_SWAP_EFFECT_FLIP_DISCARD,
//   AlphaMode=DXGI_ALPHA_MODE_IGNORE, Scaling=DXGI_SCALING_NONE, Present(1, 0)
//
// ⚠ 抓取方式很关键：flip-model swapchain 的内容不进 GDI 重定向表面，
//   用 BitBlt/GetDC 抓屏**抓不到**（实测 7 点只有中心偶尔命中）。
//   因此这里改用 Desktop Duplication API，直接取 DWM 合成后的画面。
//
// 判据：父窗口 = 绿色；子窗口（WS_CHILD）= flip swapchain 清屏红色。
//   裁剪成"中间一半"后，区域外露出绿 => 生效；仍红 => 无效。
#include <windows.h>

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdio>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

using Microsoft::WRL::ComPtr;

namespace {

ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_context = nullptr;
IDXGISwapChain1* g_chain = nullptr;
ID3D11RenderTargetView* g_rtv = nullptr;

// Desktop Duplication
IDXGIOutputDuplication* g_dup = nullptr;
ID3D11Texture2D* g_staging = nullptr;
int g_originX = 0, g_originY = 0;

LRESULT CALLBACK ParentProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_PAINT) {
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
    if (m == WM_DESTROY) PostQuitMessage(0);
    return DefWindowProcA(h, m, w, l);
}

LRESULT CALLBACK ChildProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_PAINT) {
        PAINTSTRUCT ps;
        BeginPaint(h, &ps);
        EndPaint(h, &ps);
        return 0;
    }
    return DefWindowProcA(h, m, w, l);
}

void Pump() {
    MSG m;
    while (PeekMessageA(&m, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&m);
        DispatchMessageA(&m);
    }
}

struct Pix { int r, g, b; };
bool IsRed(const Pix& p) { return p.r > 140 && p.g < 110 && p.b < 110; }
bool IsGreen(const Pix& p) { return p.g > 140 && p.r < 110 && p.b < 110; }
const char* Tag(const Pix& p) { return IsRed(p) ? "RED" : (IsGreen(p) ? "GREEN" : "other"); }

bool InitDuplication(HWND window) {
    ComPtr<IDXGIDevice2> dxgiDevice;
    if (FAILED(g_device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))) return false;
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgiDevice->GetAdapter(&adapter))) return false;

    // 找到窗口所在显示器的 output，记录其桌面原点
    HMONITOR mon = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXA mi{};
    mi.cbSize = sizeof(mi);
    GetMonitorInfoA(mon, &mi);
    g_originX = mi.rcMonitor.left;
    g_originY = mi.rcMonitor.top;

    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIOutput> out;
        if (adapter->EnumOutputs(i, &out) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_OUTPUT_DESC od{};
        if (FAILED(out->GetDesc(&od))) continue;
        if (od.Monitor != mon) continue;
        ComPtr<IDXGIOutput1> out1;
        if (FAILED(out.As(&out1))) return false;
        if (FAILED(out1->DuplicateOutput(g_device, &g_dup))) return false;
        printf("duplication      : %ls @ (%d,%d)\n", od.DeviceName, g_originX, g_originY);
        return true;
    }
    return false;
}

// 抓一帧桌面，返回是否成功；成功后可用 ReadPixel 索引
bool CaptureDesktop() {
    if (g_dup == nullptr) return false;
    // 先释放上一帧
    ComPtr<IDXGIResource> res;
    DXGI_OUTDUPL_FRAME_INFO fi{};
    HRESULT hr = g_dup->AcquireNextFrame(1000, &fi, &res);
    if (FAILED(hr)) return false;
    ComPtr<ID3D11Texture2D> tex;
    if (FAILED(res.As(&tex))) return false;

    D3D11_TEXTURE2D_DESC d{};
    tex->GetDesc(&d);
    if (g_staging == nullptr) {
        D3D11_TEXTURE2D_DESC s = d;
        s.Usage = D3D11_USAGE_STAGING;
        s.BindFlags = 0;
        s.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        s.MiscFlags = 0;
        if (FAILED(g_device->CreateTexture2D(&s, nullptr, &g_staging))) return false;
    }
    g_context->CopyResource(g_staging, tex.Get());
    g_dup->ReleaseFrame();
    return true;
}

Pix ReadPixel(int screenX, int screenY) {
    if (g_staging == nullptr) return {-1, -1, -1};
    D3D11_MAPPED_SUBRESOURCE ms{};
    if (FAILED(g_context->Map(g_staging, 0, D3D11_MAP_READ, 0, &ms))) return {-1, -1, -1};
    const int x = screenX - g_originX;
    const int y = screenY - g_originY;
    Pix out{-1, -1, -1};
    if (x >= 0 && y >= 0) {
        const auto* p = static_cast<const unsigned char*>(ms.pData) +
            static_cast<size_t>(y) * ms.RowPitch + static_cast<size_t>(x) * 4;
        out = {p[2], p[1], p[0]}; // BGRA -> RGB
    }
    g_context->Unmap(g_staging, 0);
    return out;
}

// Desktop Duplication 只有桌面变化时才产生新帧，且 AcquireNextFrame 返回的是
// "自上一帧以来的变化"。要拿到最新合成结果必须连续推进几帧。
// 采样时必须**在同一帧**上读取所有点，否则各点来自不同时刻、不可比。
bool CaptureLatest() {
    for (int i = 0; i < 5; ++i) {
        if (!CaptureDesktop()) { Sleep(50); continue; }
    }
    return g_staging != nullptr;
}

Pix SampleAt(int x, int y) {
    if (!CaptureLatest()) return {-1, -1, -1};
    return ReadPixel(x, y);
}

} // namespace

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    WNDCLASSA pc{};
    pc.lpfnWndProc = ParentProc;
    pc.hInstance = GetModuleHandleA(nullptr);
    pc.lpszClassName = "ClipProbeParent";
    RegisterClassA(&pc);
    WNDCLASSA cc{};
    cc.lpfnWndProc = ChildProc;
    cc.hInstance = pc.hInstance;
    cc.lpszClassName = "ClipProbeChild";
    RegisterClassA(&cc);

    HWND parent = CreateWindowExA(0, "ClipProbeParent", "clip probe",
        WS_POPUP | WS_VISIBLE, 200, 200, 420, 340, nullptr, nullptr, pc.hInstance, nullptr);
    if (parent == nullptr) { printf("FATAL: parent\n"); return 2; }
    RECT pr;
    GetClientRect(parent, &pr);
    const int cw = pr.right - pr.left, ch = pr.bottom - pr.top;
    HWND child = CreateWindowExA(0, "ClipProbeChild", nullptr, WS_CHILD | WS_VISIBLE,
        0, 0, cw, ch, parent, nullptr, pc.hInstance, nullptr);
    if (child == nullptr) { printf("FATAL: child\n"); return 2; }
    SetWindowPos(parent, HWND_TOPMOST, 200, 200, 420, 340, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    UpdateWindow(parent);
    Pump();
    Sleep(900);
    Pump();

    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL_9_3};
    D3D_FEATURE_LEVEL got{};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &g_device, &got, &g_context))) {
        printf("FATAL: D3D11CreateDevice\n");
        return 2;
    }
    ComPtr<IDXGIDevice2> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory2> factory;
    g_device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    dxgiDevice->GetAdapter(&adapter);
    DXGI_ADAPTER_DESC ad{};
    adapter->GetDesc(&ad);
    adapter->GetParent(IID_PPV_ARGS(&factory));

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(cw);
    desc.Height = static_cast<UINT>(ch);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    desc.Scaling = DXGI_SCALING_NONE;
    if (FAILED(factory->CreateSwapChainForHwnd(g_device, child, &desc, nullptr, nullptr, &g_chain))) {
        printf("FATAL: CreateSwapChainForHwnd\n");
        return 2;
    }
    ComPtr<ID3D11Texture2D> back;
    g_chain->GetBuffer(0, IID_PPV_ARGS(&back));
    g_device->CreateRenderTargetView(back.Get(), nullptr, &g_rtv);

    printf("=== SetWindowRgn vs D3D11 flip-model swapchain ===\n");
    printf("adapter            : vendor=0x%04x device=0x%04x\n", ad.VendorId, ad.DeviceId);
    printf("swapchain          : %dx%d FLIP_DISCARD buf=2 alpha=IGNORE scaling=NONE\n", cw, ch);
    if (!InitDuplication(child)) { printf("FATAL: desktop duplication unavailable\n"); return 2; }

    auto present = [&]() {
        const float red[4] = {1, 0, 0, 1};
        g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_context->ClearRenderTargetView(g_rtv, red);
        g_chain->Present(1, 0);
        Pump();
        Sleep(400);
        Pump();
    };

    RECT cr;
    GetWindowRect(child, &cr);
    const int rx = cr.left, ry = cr.top, rw = cr.right - cr.left, rh = cr.bottom - cr.top;
    printf("child  rect        : (%d,%d) %dx%d\n", rx, ry, rw, rh);

    // 采样点内缩 24px：避开 Win11 窗口圆角造成的边缘混色。
    const int m = 24;
    struct Sample { const char* name; int x, y; };
    const Sample samples[] = {
        {"中心    ", rx + rw / 2, ry + rh / 2},
        {"左上    ", rx + m, ry + m},
        {"右上    ", rx + rw - m, ry + m},
        {"左下    ", rx + m, ry + rh - m},
        {"右下    ", rx + rw - m, ry + rh - m},
        {"上边中  ", rx + rw / 2, ry + m},
        {"左边中  ", rx + m, ry + rh / 2},
    };
    constexpr int kN = sizeof(samples) / sizeof(samples[0]);

    // 同一帧上读取所有采样点
    auto readAll = [&](Pix out[kN]) {
        if (!CaptureLatest()) return false;
        for (int i = 0; i < kN; ++i) out[i] = ReadPixel(samples[i].x, samples[i].y);
        return true;
    };
    auto dump = [&](const char* title, const Pix v[kN]) {
        printf("\n%s\n", title);
        for (int i = 0; i < kN; ++i)
            printf("    %s (%4d,%4d) = (%3d,%3d,%3d) %s\n", samples[i].name,
                samples[i].x, samples[i].y, v[i].r, v[i].g, v[i].b, Tag(v[i]));
    };

    present();
    Pix base[kN];
    if (!readAll(base)) { printf("FATAL: capture failed\n"); return 2; }
    dump("[A] 无裁剪区域（对照：全部应为 RED）", base);
    int redCount = 0;
    for (int i = 0; i < kN; ++i) if (IsRed(base[i])) ++redCount;
    const bool baselineOk = redCount == kN;
    printf("    基线：%d/%d RED -> %s\n", redCount, kN, baselineOk ? "OK" : "不成立");
    if (!baselineOk) {
        printf("⚠ 抓屏方式仍不可靠，判定无效\n");
        return 1;
    }

    const int qx = rw / 4, qy = rh / 4, qw = rw / 2, qh = rh / 2;
    HRGN rgn = CreateRectRgn(qx, qy, qx + qw, qy + qh);
    const int setOk = SetWindowRgn(child, rgn, TRUE);
    printf("\n[B] SetWindowRgn 中间矩形 (%d,%d) %dx%d -> %s\n", qx, qy, qw, qh,
        setOk ? "已设置" : "失败");
    RedrawWindow(child, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    present();
    Pix after[kN];
    if (!readAll(after)) { printf("FATAL: capture failed\n"); return 2; }
    dump("[B] 已设置区域（期望：中心 RED，其余 GREEN）", after);

    const Pix c1 = after[0];
    const Pix o1 = after[1];
    int greenCount = 0;
    for (int i = 1; i < kN; ++i) if (IsGreen(after[i])) ++greenCount;

    printf("\n=== 判定 ===\n");
    if (IsRed(c1) && greenCount == kN - 1) {
        printf("✅ 裁剪生效（区域内 RED，区域外 %d 点全 GREEN）\n", greenCount);
        printf("   => SetWindowRgn 可裁剪 flip-model swapchain，方案 D2 成立\n");
    } else if (IsRed(c1) && IsRed(o1)) {
        printf("❌ 裁剪无效：区域外仍是 RED\n");
        printf("   => DWM 绕过窗口区域；D2 改用「子窗口偏移 + 父窗口 WS_CLIPCHILDREN」\n");
    } else if (IsGreen(c1)) {
        printf("❌ 设置区域后 swapchain 内容整体消失（连区域内也 GREEN）\n");
        printf("   => 窗口区域使 flip-model 呈现失效，D2 不可行\n");
    } else {
        printf("⚠ 不确定：中心=%s 左上角=%s 绿点=%d\n", Tag(c1), Tag(o1), greenCount);
    }
    return 0;
}
