// 按 IDXGIFactory1::EnumAdapters1 的原生顺序列出适配器
// 用于评估 preferredAdapterIndex 默认值 0 的风险：0 号到底是不是"系统默认/高性能"卡
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <dxgi1_6.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <vector>

#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3d11.lib")

using Microsoft::WRL::ComPtr;

static std::string W2U(const wchar_t* w) {
    if (!w) return "(null)";
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
    return s;
}

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);
    ComPtr<IDXGIFactory1> f1;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f1)))) { printf("CreateDXGIFactory1 失败\n"); return 1; }

    printf("=== IDXGIFactory1::EnumAdapters1 原生顺序（内核 preferredAdapterIndex 用的就是这个顺序）===\n");
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIAdapter1> a;
        if (f1->EnumAdapters1(i, &a) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 d{};
        a->GetDesc1(&d);
        printf("[%u] %-42s VendorId=0x%04X DeviceId=0x%04X  LUID=%08X:%08X  DedicatedVRAM=%llu MB\n",
               i, W2U(d.Description).c_str(), d.VendorId, d.DeviceId,
               (UINT32)d.AdapterLuid.HighPart, (UINT32)d.AdapterLuid.LowPart,
               (unsigned long long)(d.DedicatedVideoMemory / 1024 / 1024));
        // 该卡驱动哪些显示器
        for (UINT o = 0;; ++o) {
            ComPtr<IDXGIOutput> out;
            if (a->EnumOutputs(o, &out) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC od{};
            if (SUCCEEDED(out->GetDesc(&od)))
                printf("      输出[%u] %s  Monitor=0x%p  %s\n", o, W2U(od.DeviceName).c_str(),
                       (void*)od.Monitor, (od.AttachedToDesktop ? "已连接桌面" : "未连接"));
        }
    }

    // 对照：按 GPU 高性能偏好枚举
    ComPtr<IDXGIFactory6> f6;
    if (SUCCEEDED(f1.As(&f6))) {
        printf("\n=== 对照：EnumAdapterByGpuPreference(HIGH_PERFORMANCE) 顺序 ===\n");
        for (UINT i = 0;; ++i) {
            ComPtr<IDXGIAdapter1> a;
            if (f6->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&a)) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 d{};
            a->GetDesc1(&d);
            printf("[%u] %s  LUID=%08X:%08X\n", i, W2U(d.Description).c_str(),
                   (UINT32)d.AdapterLuid.HighPart, (UINT32)d.AdapterLuid.LowPart);
        }
    }

    // 系统默认卡：D3D11CreateDevice(nullptr, HARDWARE)
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    if (SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) {
        ComPtr<IDXGIDevice> dx; dev.As(&dx);
        ComPtr<IDXGIAdapter> ad; dx->GetAdapter(&ad);
        DXGI_ADAPTER_DESC d{};
        ad->GetDesc(&d);
        printf("\n=== 系统默认卡 D3D11CreateDevice(nullptr, HARDWARE) ===\n    %s  LUID=%08X:%08X\n",
               W2U(d.Description).c_str(), (UINT32)d.AdapterLuid.HighPart, (UINT32)d.AdapterLuid.LowPart);
    }
    return 0;
}
