// DXGI 适配器枚举探针（只读，不修改产品代码）。
// 目的：核实本机 DXGI 索引顺序、显存、Vendor/DeviceId、LUID，
// 与内核 EnsureDevice 的 EnumAdapters1 索引空间一致。
#include <dxgi1_6.h>
#include <d3d11.h>
#include <cstdio>

int main() {
    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory))) {
        printf("CreateDXGIFactory1 failed\n");
        return 1;
    }
    for (UINT i = 0;; ++i) {
        IDXGIAdapter1* adapter = nullptr;
        if (factory->EnumAdapters1(i, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        char name[256] = {};
        wcstombs(name, desc.Description, sizeof(name) - 1);
        LUID luid = desc.AdapterLuid;

        // 硬解能力：尝试用该适配器创建 D3D11 设备并查询 VideoDevice
        ID3D11Device* dev = nullptr;
        ID3D11DeviceContext* ctx = nullptr;
        D3D_FEATURE_LEVEL fl{};
        bool hwDecode = false;
        char flStr[32] = "n/a";
        if (SUCCEEDED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx))) {
            flStr[0] = 0;
            snprintf(flStr, sizeof(flStr), "%x", (unsigned)fl);
            ID3D11VideoDevice* vd = nullptr;
            if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D11VideoDevice), (void**)&vd)) && vd) {
                // 统计 H264 的解码 profile 数（>0 说明有硬解能力）
                UINT n = vd->GetVideoDecoderProfileCount();
                hwDecode = n > 0;
                vd->Release();
            }
            ctx->Release();
            dev->Release();
        }
        printf("index=%u  name=\"%s\"  vendor=0x%04X device=0x%04X  "
               "dedicatedVRAM=%llu MB  sharedVRAM=%llu MB  luid=%08X:%08X  "
               "flags=0x%X  d3d11=%s  videoProfiles>0=%s\n",
               i, name, desc.VendorId, desc.DeviceId,
               (unsigned long long)(desc.DedicatedVideoMemory / (1024ull * 1024ull)),
               (unsigned long long)(desc.SharedSystemMemory / (1024ull * 1024ull)),
               (unsigned)luid.HighPart, (unsigned)luid.LowPart,
               desc.Flags, flStr, hwDecode ? "YES" : "NO");
        adapter->Release();
    }
    factory->Release();
    return 0;
}
