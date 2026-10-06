// 上传路径微基准：直接对比 UpdateSubresource 与 Map(WRITE_DISCARD) 的**拷贝成本**。
//
// 为什么需要它：端到端 FPS 在所有可得的素材上都是**解码/源帧率受限**
// （720p 源 24fps -> 23.7；4K/8K 60fps 源 -> 58~59），渲染路径不是瓶颈，
// 因此 FPS 无法反映上传优化。要量化"NV12 也走 WRITE_DISCARD"这类改动，
// 必须直接测上传本身。
//
// 本探针用真实的 D3D11 设备，对同等尺寸的纹理分别执行：
//   A) UpdateSubresource（DEFAULT 纹理）
//   B) Map(WRITE_DISCARD) + 逐行 memcpy（DYNAMIC 纹理）
// 各若干轮，报告每帧耗时中位数与差异。
//
// 用法：upload_microbench [--width W] [--height H] [--bitdepth 8|10] [--frames N] [--runs R]

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
struct Timing { double updateMs = 0; double mapMs = 0; bool ok = false; };

double Median(std::vector<double> v) {
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    const auto mid = v.size() / 2;
    return v.size() % 2 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
}

/// NV12 尺寸：luma WxH，chroma (W/2)x(H/2) 交错为一张 (W/2)x(H/2) 的 RG 平面。
struct Layout {
    UINT lumaWidth = 0, lumaHeight = 0;
    UINT chromaWidth = 0, chromaHeight = 0;
    UINT bytesPerSample = 1;
};

Layout MakeLayout(const UINT width, const UINT height, const bool tenBit) {
    Layout l;
    l.lumaWidth = width; l.lumaHeight = height;
    l.chromaWidth = (width + 1) / 2; l.chromaHeight = (height + 1) / 2;
    l.bytesPerSample = tenBit ? 2u : 1u;
    return l;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);

    UINT width = 3840, height = 2160;
    bool tenBit = false;
    int frames = 200;
    int runs = 5;
    for (int i = 1; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--width" && i + 1 < argc) width = static_cast<UINT>(_wtoi(argv[++i]));
        else if (key == L"--height" && i + 1 < argc) height = static_cast<UINT>(_wtoi(argv[++i]));
        else if (key == L"--bitdepth" && i + 1 < argc) tenBit = _wtoi(argv[++i]) > 8;
        else if (key == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (key == L"--runs" && i + 1 < argc) runs = _wtoi(argv[++i]);
    }
    const auto layout = MakeLayout(width, height, tenBit);
    const auto bps = layout.bytesPerSample;
    // NV12 用 R8_UNORM / R8G8_UNORM（10bit 用 16 位变体），与内核 CreateTexture2D 一致
    const auto lumaFormat = tenBit ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
    const auto chromaFormat = tenBit ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R8G8_UNORM;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL level{};
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, &level, &context))) {
        printf("无法创建 D3D11 设备\n"); return 3;
    }
    ComPtr<IDXGIDevice> dxgiDevice;
    device.As(&dxgiDevice);
    ComPtr<IDXGIAdapter> adapter;
    dxgiDevice->GetAdapter(&adapter);
    DXGI_ADAPTER_DESC desc{};
    adapter->GetDesc(&desc);
    printf("GPU: %ls\n", desc.Description);

    // 源数据（模拟解码器给出的系统内存平面）
    std::vector<std::uint8_t> luma(static_cast<std::size_t>(layout.lumaWidth) * layout.lumaHeight * bps, 0x40);
    std::vector<std::uint8_t> chroma(static_cast<std::size_t>(layout.chromaWidth) * layout.chromaHeight * bps * 2u, 0x80);

    const char* lumaFormatName = tenBit ? "R16_UNORM" : "R8_UNORM";
    printf("尺寸: %ux%u  位深: %d  亮度平面 %.1f MB  色度平面 %.1f MB  格式 %s\n",
        width, height, tenBit ? 10 : 8,
        luma.size() / 1048576.0, chroma.size() / 1048576.0, lumaFormatName);

    std::vector<double> updateTimes, mapTimes;

    for (int run = 0; run < runs; ++run) {
        // ── A) UpdateSubresource 到 DEFAULT 纹理 ──
        {
            D3D11_TEXTURE2D_DESC d{};
            d.Width = layout.lumaWidth; d.Height = layout.lumaHeight;
            d.MipLevels = d.ArraySize = 1; d.Format = lumaFormat; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            ComPtr<ID3D11Texture2D> tex;
            if (FAILED(device->CreateTexture2D(&d, nullptr, &tex))) { printf("luma DEFAULT 创建失败\n"); return 4; }
            const auto started = std::chrono::steady_clock::now();
            for (int f = 0; f < frames; ++f)
                context->UpdateSubresource(tex.Get(), 0, nullptr, luma.data(), layout.lumaWidth * bps, 0);
            updateTimes.push_back(std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count() / frames);
        }
        // ── B) Map(WRITE_DISCARD) 到 DYNAMIC 纹理 ──
        {
            D3D11_TEXTURE2D_DESC d{};
            d.Width = layout.lumaWidth; d.Height = layout.lumaHeight;
            d.MipLevels = d.ArraySize = 1; d.Format = lumaFormat; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DYNAMIC; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            ComPtr<ID3D11Texture2D> tex;
            if (FAILED(device->CreateTexture2D(&d, nullptr, &tex))) { printf("luma DYNAMIC 创建失败\n"); return 4; }
            const auto started = std::chrono::steady_clock::now();
            for (int f = 0; f < frames; ++f) {
                D3D11_MAPPED_SUBRESOURCE mapped{};
                if (FAILED(context->Map(tex.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
                    printf("Map 失败\n"); return 5;
                }
                const auto rowBytes = static_cast<std::size_t>(layout.lumaWidth) * bps;
                if (f == 0 && run == 0)
                    printf("    [诊断] rowBytes=%zu  RowPitch=%u  差=%d  行数=%u\n",
                        rowBytes, mapped.RowPitch, static_cast<int>(mapped.RowPitch) - static_cast<int>(rowBytes),
                        layout.lumaHeight);
                for (UINT row = 0; row < layout.lumaHeight; ++row)
                    std::memcpy(static_cast<std::uint8_t*>(mapped.pData) +
                        static_cast<std::size_t>(row) * mapped.RowPitch,
                        luma.data() + static_cast<std::size_t>(row) * rowBytes, rowBytes);
                context->Unmap(tex.Get(), 0);
            }
            mapTimes.push_back(std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count() / frames);
        }
    }

    const auto updateMs = Median(updateTimes);
    const auto mapMs = Median(mapTimes);
    const auto lumaMB = luma.size() / 1048576.0;
    printf("\n每帧上传（亮度平面 %.1f MB）:\n", lumaMB);
    printf("  UpdateSubresource        %7.3f ms/帧   %6.1f GB/s\n", updateMs, lumaMB / updateMs);
    printf("  Map(WRITE_DISCARD)       %7.3f ms/帧   %6.1f GB/s\n", mapMs, lumaMB / mapMs);
    printf("  差异: %+.3f ms/帧 (%+.1f%%)\n", mapMs - updateMs,
        updateMs > 0 ? (mapMs - updateMs) / updateMs * 100.0 : 0.0);
    return 0;
}
