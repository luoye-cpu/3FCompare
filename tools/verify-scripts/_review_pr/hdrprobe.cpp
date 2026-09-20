// 本机 HDR 能力探测器 —— 复现内核 OutputSupportsHdr() 的判据
// 1) DisplayConfigGetDeviceInfo(ADVANCED_COLOR_INFO)  -> Windows 是否真的开了 HDR
// 2) IDXGIOutput6::GetDesc1                          -> BitsPerColor / ColorSpace / 亮度
// 3) CheckColorSpaceSupport                          -> 内核真正要用的两个色彩空间
#ifndef WINVER
#define WINVER 0x0A00
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#ifndef NTDDI_VERSION
#define NTDDI_VERSION 0x0A00000C
#endif
#include <windows.h>
#include <dxgi1_6.h>
#include <d3d11.h>
#include <wingdi.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <vector>

#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3d11.lib")

using Microsoft::WRL::ComPtr;

static const char* CsName(DXGI_COLOR_SPACE_TYPE t) {
    switch (t) {
    case DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709:          return "RGB_FULL_G22_NONE_P709 (sRGB)";
    case DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709:          return "RGB_FULL_G10_NONE_P709 (linear scRGB)";
    case DXGI_COLOR_SPACE_RGB_STUDIO_G22_NONE_P709:        return "RGB_STUDIO_G22_NONE_P709";
    case DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020:       return "RGB_FULL_G2084_NONE_P2020 (PQ HDR10)";
    case DXGI_COLOR_SPACE_RGB_STUDIO_G2084_NONE_P2020:     return "RGB_STUDIO_G2084_NONE_P2020 (PQ HDR10 studio)";
    case DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P2020:         return "RGB_FULL_G22_NONE_P2020";
    case DXGI_COLOR_SPACE_YCBCR_FULL_GHLG_TOPLEFT_P2020:   return "YCBCR_FULL_GHLG_TOPLEFT_P2020 (HLG)";
    case DXGI_COLOR_SPACE_YCBCR_STUDIO_GHLG_TOPLEFT_P2020: return "YCBCR_STUDIO_GHLG_TOPLEFT_P2020 (HLG studio)";
    default:                                               return "OTHER";
    }
}

// 该结构体未出现在 Windows SDK 头文件中，按官方文档自行声明
// DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9
#ifndef DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
#define DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO 9
#endif
typedef struct _MY_ADVANCED_COLOR_INFO {
    DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    union {
        struct {
            UINT32 advancedColorSupported : 1;
            UINT32 advancedColorEnabled : 1;
            UINT32 wideColorEnforced : 1;
            UINT32 advancedColorForceDisabled : 1;
            UINT32 reserved : 28;
        };
        UINT32 value;
    };
} MY_ADVANCED_COLOR_INFO;

static std::string WToUtf8(const wchar_t* w) {
    if (!w) return "(null)";
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
    return s;
}

// ---- 1. DisplayConfig Advanced Color ----
static void DumpAdvancedColor() {
    printf("==== [1] DisplayConfig ADVANCED_COLOR_INFO（Windows HDR 开关的真实状态）====\n");
    UINT32 pathCount = 0, modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS) {
        printf("  GetDisplayConfigBufferSizes 失败\n\n"); return;
    }
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount,
                           modes.data(), nullptr) != ERROR_SUCCESS) {
        printf("  QueryDisplayConfig 失败\n\n"); return;
    }
    printf("  活动显示路径数 = %u\n", pathCount);
    for (UINT32 i = 0; i < pathCount; ++i) {
        DISPLAYCONFIG_DEVICE_INFO_HEADER hdr = {};
        hdr.type = (DISPLAYCONFIG_DEVICE_INFO_TYPE)DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        hdr.size = sizeof(MY_ADVANCED_COLOR_INFO);
        hdr.adapterId = paths[i].targetInfo.adapterId;
        hdr.id = paths[i].targetInfo.id;
        MY_ADVANCED_COLOR_INFO aci = {};
        aci.header = hdr;
        LONG rc = DisplayConfigGetDeviceInfo(&aci.header);
        if (rc != ERROR_SUCCESS) {
            printf("  [%u] DisplayConfigGetDeviceInfo 失败 rc=%ld\n", i, rc);
            continue;
        }
        // 取友好名
        DISPLAYCONFIG_TARGET_DEVICE_NAME tdn = {};
        tdn.header.type = (DISPLAYCONFIG_DEVICE_INFO_TYPE)DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        tdn.header.size = sizeof(tdn);
        tdn.header.adapterId = paths[i].targetInfo.adapterId;
        tdn.header.id = paths[i].targetInfo.id;
        std::string name = "(unknown)";
        if (DisplayConfigGetDeviceInfo(&tdn.header) == ERROR_SUCCESS)
            name = WToUtf8(tdn.monitorFriendlyDeviceName);

        printf("  [%u] %s\n", i, name.c_str());
        printf("      advancedColorSupported    = %u   <-- 显示器/OS 是否支持 HDR\n", aci.advancedColorSupported);
        printf("      advancedColorEnabled      = %u   <-- ★ Windows 是否已开启 HDR\n", aci.advancedColorEnabled);
        printf("      wideColorEnforced         = %u\n", aci.wideColorEnforced);
        printf("      advancedColorForceDisabled= %u\n", aci.advancedColorForceDisabled);

        // SDR 白点 / HDR 峰值（Windows 11 22H2+）
        DISPLAYCONFIG_SDR_WHITE_LEVEL wl = {};
        wl.header.type = (DISPLAYCONFIG_DEVICE_INFO_TYPE)DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        wl.header.size = sizeof(wl);
        wl.header.adapterId = paths[i].targetInfo.adapterId;
        wl.header.id = paths[i].targetInfo.id;
        if (DisplayConfigGetDeviceInfo(&wl.header) == ERROR_SUCCESS && wl.SDRWhiteLevel)
            printf("      SDRWhiteLevel             = %u (%.1f nits)\n",
                   wl.SDRWhiteLevel, wl.SDRWhiteLevel / 1000.0 * 80.0);
    }
    printf("\n");
}

// ---- 终局测试：真实创建 16F HDR 交换链 ----
static bool g_testSwapChain = false;

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

static const char* HrTag(HRESULT hr) {
    switch (hr) {
    case S_OK: return "S_OK";
    case DXGI_ERROR_INVALID_CALL: return "DXGI_ERROR_INVALID_CALL";
    case DXGI_ERROR_UNSUPPORTED: return "DXGI_ERROR_UNSUPPORTED";
    case E_INVALIDARG: return "E_INVALIDARG";
    case DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: return "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE";
    default: return "(其他)";
    }
}

static void TryHdrSwapChain(IDXGIAdapter1* adapter, HMONITOR mon) {
    MONITORINFO mi = {}; mi.cbSize = sizeof(mi);
    if (!GetMonitorInfoW(mon, (MONITORINFO*)&mi)) { printf("       [swapchain] GetMonitorInfo 失败\n"); return; }

    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"HdrProbeCls";
    RegisterClassW(&wc);
    HWND hw = CreateWindowExW(0, L"HdrProbeCls", L"hdrprobe", WS_POPUP,
        mi.rcMonitor.left + 40, mi.rcMonitor.top + 40, 240, 160,
        nullptr, nullptr, wc.hInstance, nullptr);
    if (!hw) { printf("       [swapchain] CreateWindow 失败\n"); return; }
    ShowWindow(hw, SW_SHOWNOACTIVATE);

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    if (FAILED(hr)) { printf("       [swapchain] D3D11CreateDevice 失败 hr=0x%08lX\n", hr); DestroyWindow(hw); return; }

    ComPtr<IDXGIDevice> dxdev; dev.As(&dxdev);
    ComPtr<IDXGIAdapter> dxa; dxdev->GetAdapter(&dxa);
    ComPtr<IDXGIFactory2> fac; dxa->GetParent(IID_PPV_ARGS(&fac));
    if (!fac) { printf("       [swapchain] 取 IDXGIFactory2 失败\n"); DestroyWindow(hw); return; }

    struct Case { const char* name; DXGI_FORMAT fmt; DXGI_ALPHA_MODE alpha; };
    Case cases[] = {
        { "R16G16B16A16_FLOAT / IGNORE",      DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_ALPHA_MODE_IGNORE },
        { "R16G16B16A16_FLOAT / PREMULTIPLIED", DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_ALPHA_MODE_PREMULTIPLIED },
        { "R10G10B10A2_UNORM  / IGNORE",      DXGI_FORMAT_R10G10B10A2_UNORM,  DXGI_ALPHA_MODE_IGNORE },
        { "R8G8B8A8_UNORM     / IGNORE",      DXGI_FORMAT_R8G8B8A8_UNORM,     DXGI_ALPHA_MODE_IGNORE },
    };
    for (const auto& c : cases) {
        DXGI_SWAP_CHAIN_DESC1 sd = {};
        sd.Width = 240; sd.Height = 160;
        sd.Format = c.fmt;
        sd.SampleDesc.Count = 1;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.BufferCount = 2;
        sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        sd.AlphaMode = c.alpha;
        ComPtr<IDXGISwapChain1> sc;
        hr = fac->CreateSwapChainForHwnd(dev.Get(), hw, &sd, nullptr, nullptr, &sc);
        printf("       [swapchain] %-38s -> %s (hr=0x%08lX)\n", c.name,
               SUCCEEDED(hr) ? "创建成功" : HrTag(hr), hr);
        if (FAILED(hr)) continue;

        ComPtr<IDXGISwapChain4> sc4;
        if (SUCCEEDED(sc.As(&sc4))) {
            UINT sup = 0;
            if (SUCCEEDED(sc4->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, &sup)))
                printf("                   PQ (G2084/P2020) 支持 = 0x%X %s\n", sup,
                       (sup & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) ? "[可 Present]" : "[不可]");
            sup = 0;
            if (SUCCEEDED(sc4->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, &sup)))
                printf("                   scRGB (G10/P709) 支持 = 0x%X %s\n", sup,
                       (sup & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) ? "[可 Present]" : "[不可]");
        }
        sc.Reset();
    }
    DestroyWindow(hw);
}

// ---- 2/3. DXGI Output6 ----
static void DumpDxgi() {
    printf("==== [2] DXGI IDXGIOutput6::GetDesc1（内核 OutputSupportsHdr 的判据）====\n");
    ComPtr<IDXGIFactory1> f1;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f1)))) { printf("  CreateDXGIFactory1 失败\n\n"); return; }
    ComPtr<IDXGIFactory6> f6;
    f1.As(&f6);

    for (UINT ai = 0;; ++ai) {
        ComPtr<IDXGIAdapter1> adapter;
        if (f6) { if (f6->EnumAdapterByGpuPreference(ai, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)) == DXGI_ERROR_NOT_FOUND) break; }
        else { if (f1->EnumAdapters1(ai, &adapter) == DXGI_ERROR_NOT_FOUND) break; }
        if (!adapter) break;

        DXGI_ADAPTER_DESC1 ad = {};
        adapter->GetDesc1(&ad);
        printf("  适配器[%u] %s  LUID=%08X:%08X  VendorId=0x%04X\n",
               ai, WToUtf8(ad.Description).c_str(),
               (UINT32)ad.AdapterLuid.HighPart, (UINT32)ad.AdapterLuid.LowPart, ad.VendorId);

        for (UINT oi = 0;; ++oi) {
            ComPtr<IDXGIOutput> out;
            if (adapter->EnumOutputs(oi, &out) == DXGI_ERROR_NOT_FOUND) break;
            ComPtr<IDXGIOutput6> out6;
            if (FAILED(out.As(&out6))) { printf("    [%u] 不支持 IDXGIOutput6\n", oi); continue; }

            DXGI_OUTPUT_DESC1 d = {};
            if (FAILED(out6->GetDesc1(&d))) { printf("    [%u] GetDesc1 失败\n", oi); continue; }

            bool bitsOk = d.BitsPerColor >= 10;
            bool csOk = (d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
                         d.ColorSpace == DXGI_COLOR_SPACE_RGB_STUDIO_G2084_NONE_P2020);
            printf("    输出[%u] %s\n", oi, WToUtf8(d.DeviceName).c_str());
            printf("       Monitor(HMONITOR)   = 0x%p\n", (void*)d.Monitor);
            printf("       BitsPerColor        = %u   %s (内核要求 >=10)\n", d.BitsPerColor, bitsOk ? "OK" : "FAIL");
            printf("       ColorSpace          = %s   %s (内核要求 G2084/P2020)\n",
                   CsName(d.ColorSpace), csOk ? "OK" : "FAIL");
            printf("       MinLuminance        = %.4f nits\n", d.MinLuminance);
            printf("       MaxLuminance        = %.4f nits\n", d.MaxLuminance);
            printf("       MaxFullFrameLum     = %.4f nits\n", d.MaxFullFrameLuminance);
            printf("       ==> OutputSupportsHdr 判据 (bits>=10 && cs==PQ) = %s\n",
                   (bitsOk && csOk) ? "TRUE  ← 内核会走 HDR" : "FALSE ← 内核会走 SDR");

            // 是否为主显示器
            MONITORINFOEXW mi = {};
            mi.cbSize = sizeof(mi);
            if (GetMonitorInfoW(d.Monitor, (MONITORINFO*)&mi))
            printf("       Primary             = %s\n",
                   (mi.dwFlags & MONITORINFOF_PRIMARY) ? "YES" : "no");
            if (g_testSwapChain) TryHdrSwapChain(adapter.Get(), d.Monitor);
            printf("\n");
        }
    }
}

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    g_testSwapChain = (argc > 1 && std::string(argv[1]) == "swapchain");
    printf("=== HDR 能力探测 ===\n\n");
    DumpAdvancedColor();
    DumpDxgi();

    HMONITOR mw = MonitorFromWindow(GetDesktopWindow(), MONITOR_DEFAULTTOPRIMARY);
    HMONITOR mp = MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    printf("==== [3] 窗口位置 ====\n");
    printf("  MonitorFromWindow(GetDesktopWindow()) = 0x%p\n", (void*)mw);
    printf("  MonitorFromPoint(0,0)                 = 0x%p\n", (void*)mp);
    return 0;
}
