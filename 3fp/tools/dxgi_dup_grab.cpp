// One-shot desktop-duplication grab: captures the COMPOSED desktop image of one
// output (hardware overlays included, which GDI BitBlt misses).
//   argv[1] = output index (within the first adapter's output list)
//   argv[2] = raw output file (BGRA, top-down)
// Prints "OK <w> <h> <originX> <originY>" on success. Retries AcquireNextFrame
// for up to ~2 s, because a static desktop produces no new frames.

#include <windows.h>
#include <dxgi1_2.h>
#include <d3d11.h>
#include <cstdio>
#include <cstdlib>

int main(int argc, char** argv)
{
    if (argc < 3) { std::printf("usage: dxgi_dup_grab <outIndex> <rawfile>\n"); return 2; }
    const int outIndex = std::atoi(argv[1]);
    const char* path = argv[2];

    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory))) {
        std::printf("no factory\n"); return 1;
    }
    IDXGIAdapter1* adapter = nullptr;
    if (factory->EnumAdapters1(0, &adapter) == DXGI_ERROR_NOT_FOUND) {
        std::printf("no adapter\n"); return 1;
    }
    IDXGIOutput* output = nullptr;
    if (adapter->EnumOutputs(outIndex, &output) == DXGI_ERROR_NOT_FOUND) {
        std::printf("no output %d\n", outIndex); return 1;
    }
    DXGI_OUTPUT_DESC desc{};
    output->GetDesc(&desc);
    const int ox = desc.DesktopCoordinates.left, oy = desc.DesktopCoordinates.top;

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    D3D_FEATURE_LEVEL fl{};
    if (FAILED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &device, &fl, &context))) {
        std::printf("no device\n"); return 1;
    }
    IDXGIOutput1* output1 = nullptr;
    if (FAILED(output->QueryInterface(__uuidof(IDXGIOutput1), (void**)&output1))) {
        std::printf("no output1\n"); return 1;
    }
    IDXGIOutputDuplication* dup = nullptr;
    if (FAILED(output1->DuplicateOutput(device, &dup))) {
        std::printf("duplicate failed\n"); return 1;
    }
    DXGI_OUTDUPL_FRAME_INFO info{};
    IDXGIResource* resource = nullptr;
    HRESULT hr = E_FAIL;
    for (int attempt = 0; attempt < 40; ++attempt) {
        hr = dup->AcquireNextFrame(50, &info, &resource);
        if (SUCCEEDED(hr) && info.LastPresentTime.QuadPart != 0) break;
        if (SUCCEEDED(hr)) { resource->Release(); resource = nullptr; dup->ReleaseFrame(); }
    }
    if (FAILED(hr) || resource == nullptr) {
        std::printf("no frame (hr=0x%08lX)\n", (unsigned long)hr); return 1;
    }
    ID3D11Texture2D* tex = nullptr;
    resource->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex);
    D3D11_TEXTURE2D_DESC td{};
    tex->GetDesc(&td);
    D3D11_TEXTURE2D_DESC stageDesc = td;
    stageDesc.Usage = D3D11_USAGE_STAGING;
    stageDesc.BindFlags = 0;
    stageDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stageDesc.MiscFlags = 0;
    ID3D11Texture2D* stage = nullptr;
    if (FAILED(device->CreateTexture2D(&stageDesc, nullptr, &stage))) {
        std::printf("stage failed\n"); return 1;
    }
    context->CopyResource(stage, tex);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(stage, 0, D3D11_MAP_READ, 0, &mapped))) {
        std::printf("map failed\n"); return 1;
    }
    FILE* f = std::fopen(path, "wb");
    if (f == nullptr) { std::printf("open out failed\n"); return 1; }
    for (UINT row = 0; row < td.Height; ++row)
        std::fwrite((const char*)mapped.pData + (std::size_t)row * mapped.RowPitch,
            (std::size_t)td.Width * 4, 1, f);
    std::fclose(f);
    std::printf("OK %u %u %d %d\n", td.Width, td.Height, ox, oy);
    context->Unmap(stage, 0);
    tex->Release(); stage->Release(); resource->Release();
    dup->ReleaseFrame();
    dup->Release(); output1->Release(); output->Release();
    adapter->Release(); factory->Release();
    context->Release(); device->Release();
    return 0;
}
