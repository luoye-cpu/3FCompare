// 判别实验（不依赖抓屏）：SetWindowRgn 之后，flip-model swapchain 的 Present 是否还能成功？
//
// 上一版用抓屏判定，但本环境屏幕上有其他覆盖窗口干扰采样（左上角稳定读到非预期值），
// 无法给出确定结论。这里改用**不依赖画面**的判据：
//   - Present() 的 HRESULT
//   - 设备是否丢失（DXGI_ERROR_DEVICE_REMOVED / D3DERR_DEVICELOST）
//   - 窗口区域是否真的生效（GetWindowRgn）
//
// 复刻内核参数：CreateSwapChainForHwnd + FLIP_DISCARD + buf=2 + ALPHA_IGNORE + SCALING_NONE
#include <windows.h>

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdio>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

using Microsoft::WRL::ComPtr;

namespace {
ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_context = nullptr;
IDXGISwapChain1* g_chain = nullptr;
ID3D11RenderTargetView* g_rtv = nullptr;

LRESULT CALLBACK Wnd(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_DESTROY) PostQuitMessage(0);
    return DefWindowProcA(h, m, w, l);
}
void Pump() {
    MSG m;
    while (PeekMessageA(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageA(&m); }
}
const char* HrTag(HRESULT hr) {
    switch (hr) {
        case S_OK: return "S_OK";
        case DXGI_ERROR_DEVICE_REMOVED: return "DXGI_ERROR_DEVICE_REMOVED";
        case DXGI_ERROR_DEVICE_RESET: return "DXGI_ERROR_DEVICE_RESET";
        case DXGI_ERROR_INVALID_CALL: return "DXGI_ERROR_INVALID_CALL";
        case DXGI_ERROR_ACCESS_LOST: return "DXGI_ERROR_ACCESS_LOST";
        default: return "other";
    }
}
} // namespace

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    WNDCLASSA wc{};
    wc.lpfnWndProc = Wnd;
    wc.hInstance = GetModuleHandleA(nullptr);
    wc.lpszClassName = "ClipProbe2";
    RegisterClassA(&wc);
    HWND w = CreateWindowExA(0, "ClipProbe2", "probe2", WS_POPUP | WS_VISIBLE,
        300, 300, 400, 300, nullptr, nullptr, wc.hInstance, nullptr);
    if (w == nullptr) { printf("FATAL: window\n"); return 2; }
    Pump();
    Sleep(500);
    Pump();

    D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL_9_3};
    D3D_FEATURE_LEVEL got{};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels),
            D3D11_SDK_VERSION, &g_device, &got, &g_context))) {
        printf("FATAL: device\n");
        return 2;
    }
    ComPtr<IDXGIDevice2> dd;
    ComPtr<IDXGIAdapter> ad;
    ComPtr<IDXGIFactory2> fac;
    g_device->QueryInterface(IID_PPV_ARGS(&dd));
    dd->GetAdapter(&ad);
    ad->GetParent(IID_PPV_ARGS(&fac));

    DXGI_SWAP_CHAIN_DESC1 d{};
    d.Width = 400;
    d.Height = 300;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.BufferCount = 2;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    d.Scaling = DXGI_SCALING_NONE;
    if (FAILED(fac->CreateSwapChainForHwnd(g_device, w, &d, nullptr, nullptr, &g_chain))) {
        printf("FATAL: swapchain\n");
        return 2;
    }
    ComPtr<ID3D11Texture2D> back;
    g_chain->GetBuffer(0, IID_PPV_ARGS(&back));
    g_device->CreateRenderTargetView(back.Get(), nullptr, &g_rtv);

    printf("=== SetWindowRgn -> Present HRESULT（不依赖抓屏）===\n");
    printf("swapchain: 400x300 FLIP_DISCARD buf=2 ALPHA_IGNORE SCALING_NONE\n\n");

    auto present = [&](const char* tag) {
        const float c[4] = {1, 0, 0, 1};
        g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_context->ClearRenderTargetView(g_rtv, c);
        HRESULT hr = g_chain->Present(1, 0);
        Pump();
        Sleep(200);
        Pump();
        HRESULT devHr = g_device->GetDeviceRemovedReason();
        printf("  %-22s Present=0x%08lx (%s)  DeviceRemoved=0x%08lx\n",
            tag, hr, HrTag(hr), devHr);
        return hr;
    };

    present("设置区域前");

    HRGN rgn = CreateRectRgn(100, 75, 300, 225);
    const int ok = SetWindowRgn(w, rgn, TRUE);
    printf("\n  SetWindowRgn(100,75)-(300,225) -> %s\n", ok ? "已设置" : "失败");
    HRGN verify = CreateRectRgn(0, 0, 0, 0);
    const int rgot = GetWindowRgn(w, verify);
    RECT vr{};
    GetRgnBox(verify, &vr);
    printf("  GetWindowRgn 回读 -> %d, rect=(%ld,%ld)-(%ld,%ld)\n",
        rgot, vr.left, vr.top, vr.right, vr.bottom);
    DeleteObject(verify);

    present("设置区域后（不重建）");

    printf("\n  再试：设置区域后 ResizeBuffers 再 Present\n");
    HRESULT rhr = g_chain->ResizeBuffers(2, 400, 300, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
    printf("    ResizeBuffers=0x%08lx\n", rhr);
    if (SUCCEEDED(rhr)) {
        ComPtr<ID3D11Texture2D> b2;
        g_chain->GetBuffer(0, IID_PPV_ARGS(&b2));
        if (g_rtv) g_rtv->Release();
        g_device->CreateRenderTargetView(b2.Get(), nullptr, &g_rtv);
    }
    present("设置区域后（重建）");

    printf("\n  清除区域后：\n");
    SetWindowRgn(w, nullptr, TRUE);
    present("清除区域后");

    printf("\n=== 判定 ===\n");
    printf("若「设置区域后」Present 失败或 DeviceRemoved 非 0 ⇒ 窗口区域破坏 flip-model 呈现，D2 不可行。\n");
    printf("若全部 S_OK ⇒ Present 层面无碍，但仍需真机目视确认画面是否真的被裁剪。\n");
    return 0;
}
