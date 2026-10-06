// 严格对比 UpdateSubresource 与 Map(WRITE_DISCARD) —— 消除顺序偏差与缓存效应。
//
// 动机：upload_microbench 显示 Map 明显更慢，但两次运行间 UpdateSubresource 自身
// 也从 2.38 -> 0.87 ms/帧，说明**顺序与预热**影响很大。要下结论必须：
//   1. 两种方法**交替**执行（A/B/A/B…），消除漂移；
//   2. 每轮先预热，再计时；
//   3. 报告**分布**（中位数 + 四分位），不只看单值；
//   4. 两种方法用**同尺寸、同格式**的纹理，且都写满整个平面。
//
// 用法：upload_ab [--width W] [--height H] [--bitdepth 8|10] [--frames N] [--rounds R]

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
double Median(std::vector<double> v) {
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    const auto mid = v.size() / 2;
    return v.size() % 2 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
}
double Percentile(std::vector<double> v, const double p) {
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    const auto index = static_cast<std::size_t>(p * (v.size() - 1) + 0.5);
    return v[(std::min)(index, v.size() - 1)];
}
void Report(const char* label, std::vector<double> samples, const double mb) {
    const auto median = Median(samples);
    printf("  %-24s 中位 %7.3f ms/帧  Q1 %7.3f  Q3 %7.3f  (%6.1f GB/s)\n",
        label, median, Percentile(samples, 0.25), Percentile(samples, 0.75),
        median > 0 ? mb / median : 0.0);
}

/// 用给定方法把 src 写进 tex，重复 frames 次，返回平均每帧毫秒。
double TimeUpload(ID3D11DeviceContext* context, ID3D11Texture2D* tex,
    const std::vector<std::uint8_t>& src, const UINT width, const UINT height,
    const UINT bps, const int frames, const bool useMap) {
    const auto rowBytes = static_cast<std::size_t>(width) * bps;
    const auto started = std::chrono::steady_clock::now();
    for (int f = 0; f < frames; ++f) {
        if (useMap) {
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (FAILED(context->Map(tex, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return -1;
            for (UINT row = 0; row < height; ++row)
                std::memcpy(static_cast<std::uint8_t*>(mapped.pData) +
                    static_cast<std::size_t>(row) * mapped.RowPitch,
                    src.data() + static_cast<std::size_t>(row) * rowBytes, rowBytes);
            context->Unmap(tex, 0);
        } else {
            context->UpdateSubresource(tex, 0, nullptr, src.data(),
                static_cast<UINT>(rowBytes), 0);
        }
    }
    return std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count() / frames;
}

ComPtr<ID3D11Texture2D> MakeTexture(ID3D11Device* device, const UINT width, const UINT height,
    const DXGI_FORMAT format, const bool dynamic) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = width; d.Height = height;
    d.MipLevels = d.ArraySize = 1; d.Format = format; d.SampleDesc.Count = 1;
    d.Usage = dynamic ? D3D11_USAGE_DYNAMIC : D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (dynamic) d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    ComPtr<ID3D11Texture2D> tex;
    if (FAILED(device->CreateTexture2D(&d, nullptr, &tex))) return nullptr;
    return tex;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    UINT width = 3840, height = 2160;
    bool tenBit = false;
    int frames = 20;
    int rounds = 15;
    for (int i = 1; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--width" && i + 1 < argc) width = static_cast<UINT>(_wtoi(argv[++i]));
        else if (key == L"--height" && i + 1 < argc) height = static_cast<UINT>(_wtoi(argv[++i]));
        else if (key == L"--bitdepth" && i + 1 < argc) tenBit = _wtoi(argv[++i]) > 8;
        else if (key == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (key == L"--rounds" && i + 1 < argc) rounds = _wtoi(argv[++i]);
    }
    const auto bps = tenBit ? 2u : 1u;
    const auto format = tenBit ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &context))) {
        printf("无法创建 D3D11 设备\n"); return 3;
    }
    ComPtr<IDXGIDevice> dxgiDevice;
    device.As(&dxgiDevice);
    ComPtr<IDXGIAdapter> adapter;
    dxgiDevice->GetAdapter(&adapter);
    DXGI_ADAPTER_DESC desc{};
    adapter->GetDesc(&desc);

    const auto luma = MakeTexture(device.Get(), width, height, format, false);
    const auto lumaDynamic = MakeTexture(device.Get(), width, height, format, true);
    if (!luma || !lumaDynamic) { printf("纹理创建失败\n"); return 4; }

    std::vector<std::uint8_t> src(static_cast<std::size_t>(width) * height * bps, 0x5A);
    const auto mb = src.size() / 1048576.0;

    printf("GPU: %ls\n", desc.Description);
    printf("尺寸 %ux%u  位深 %d  平面 %.1f MB  每轮 %d 帧  共 %d 轮（交替）\n\n",
        width, height, tenBit ? 10 : 8, mb, frames, rounds);

    // 预热：让两条路径的驱动侧分配/页表都就位
    TimeUpload(context.Get(), luma.Get(), src, width, height, bps, 3, false);
    TimeUpload(context.Get(), lumaDynamic.Get(), src, width, height, bps, 3, true);

    std::vector<double> updateTimes, mapTimes;
    for (int r = 0; r < rounds; ++r) {
        // 交替顺序，且每轮翻转先后：消除"总是后跑的更慢/更快"的偏差
        if (r % 2 == 0) {
            updateTimes.push_back(TimeUpload(context.Get(), luma.Get(), src, width, height, bps, frames, false));
            mapTimes.push_back(TimeUpload(context.Get(), lumaDynamic.Get(), src, width, height, bps, frames, true));
        } else {
            mapTimes.push_back(TimeUpload(context.Get(), lumaDynamic.Get(), src, width, height, bps, frames, true));
            updateTimes.push_back(TimeUpload(context.Get(), luma.Get(), src, width, height, bps, frames, false));
        }
    }

    Report("UpdateSubresource", updateTimes, mb);
    Report("Map(WRITE_DISCARD)", mapTimes, mb);
    const auto u = Median(updateTimes), m = Median(mapTimes);
    printf("\n  中位数之比: Map / Update = %.2fx  (%s)\n", m / u, m > u ? "Map 更慢" : "Map 更快");
    printf("  每帧差值:   %+.3f ms\n", m - u);
    return 0;
}
