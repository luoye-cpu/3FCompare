// P2 验证：比较三种"CPU 数据 -> GPU 纹理"路径在大帧下的成本。
//
// 背景：8K 软解每帧要送 ~99.6 MB（Y 66.4 + UV 33.2）进 GPU，实测带宽仅 3.6-4.4 GB/s，
// 而 1080p 能到 6.9 GB/s。同一 API 越往上越慢，说明瓶颈不只是 PCIe 带宽。
//
// 三种候选：
//   A) UpdateSubresource 直接写 DEFAULT 纹理（当前 >=4K 走这条）
//   B) Map(WRITE_DISCARD) 写 DYNAMIC 纹理（当前 <4K 走这条）
//   C) 写 STAGING 纹理 + CopyResource 到 DEFAULT（GPU 侧拷贝，CPU 写 staging）
//
// 目的：判断 P2（双缓冲/重叠）是否值得做，以及 C 是否优于 A。
//
// 用法：upload_paths [--width W] [--height H] [--bitdepth 8|10] [--frames N] [--rounds R]

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

ComPtr<ID3D11Texture2D> Make(ID3D11Device* device, const UINT w, const UINT h,
    const DXGI_FORMAT f, const D3D11_USAGE usage) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w; d.Height = h; d.MipLevels = d.ArraySize = 1;
    d.Format = f; d.SampleDesc.Count = 1;
    d.Usage = usage;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (usage == D3D11_USAGE_DYNAMIC) d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (usage == D3D11_USAGE_STAGING) d.BindFlags = 0, d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    ComPtr<ID3D11Texture2D> t;
    if (FAILED(device->CreateTexture2D(&d, nullptr, &t))) return nullptr;
    return t;
}

/// 返回每帧毫秒；method: 0=A, 1=B, 2=C
double TimePath(ID3D11DeviceContext* ctx, ID3D11Texture2D* dst, ID3D11Texture2D* staging,
    const std::vector<std::uint8_t>& src, const UINT w, const UINT h, const UINT bps,
    const int frames, const int method) {
    const auto rowBytes = static_cast<std::size_t>(w) * bps;
    const auto started = std::chrono::steady_clock::now();
    for (int i = 0; i < frames; ++i) {
        if (method == 0) {
            ctx->UpdateSubresource(dst, 0, nullptr, src.data(), static_cast<UINT>(rowBytes), 0);
        } else if (method == 1) {
            D3D11_MAPPED_SUBRESOURCE m{};
            if (FAILED(ctx->Map(dst, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) return -1;
            for (UINT r = 0; r < h; ++r)
                std::memcpy(static_cast<std::uint8_t*>(m.pData) + static_cast<std::size_t>(r) * m.RowPitch,
                    src.data() + static_cast<std::size_t>(r) * rowBytes, rowBytes);
            ctx->Unmap(dst, 0);
        } else {
            // C: CPU -> staging 纹理（Map/写），再由 GPU 拷到目标
            D3D11_MAPPED_SUBRESOURCE m{};
            if (FAILED(ctx->Map(staging, 0, D3D11_MAP_WRITE, 0, &m))) return -1;
            for (UINT r = 0; r < h; ++r)
                std::memcpy(static_cast<std::uint8_t*>(m.pData) + static_cast<std::size_t>(r) * m.RowPitch,
                    src.data() + static_cast<std::size_t>(r) * rowBytes, rowBytes);
            ctx->Unmap(staging, 0);
            ctx->CopyResource(dst, staging);
        }
    }
    ctx->Flush();
    return std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count() / frames;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    UINT width = 7680, height = 4320;
    bool tenBit = true;
    int frames = 5, rounds = 9;
    for (int i = 1; i < argc; ++i) {
        const std::wstring k = argv[i];
        if (k == L"--width" && i + 1 < argc) width = static_cast<UINT>(_wtoi(argv[++i]));
        else if (k == L"--height" && i + 1 < argc) height = static_cast<UINT>(_wtoi(argv[++i]));
        else if (k == L"--bitdepth" && i + 1 < argc) tenBit = _wtoi(argv[++i]) > 8;
        else if (k == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (k == L"--rounds" && i + 1 < argc) rounds = _wtoi(argv[++i]);
    }
    const auto bps = tenBit ? 2u : 1u;
    const auto format = tenBit ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &ctx))) { printf("无 D3D11 设备\n"); return 3; }
    ComPtr<IDXGIDevice> dxgiDevice; device.As(&dxgiDevice);
    ComPtr<IDXGIAdapter> adapter; dxgiDevice->GetAdapter(&adapter);
    DXGI_ADAPTER_DESC desc{}; adapter->GetDesc(&desc);

    const auto def = Make(device.Get(), width, height, format, D3D11_USAGE_DEFAULT);
    const auto dyn = Make(device.Get(), width, height, format, D3D11_USAGE_DYNAMIC);
    const auto stg = Make(device.Get(), width, height, format, D3D11_USAGE_STAGING);
    if (!def || !dyn || !stg) { printf("纹理创建失败\n"); return 4; }

    std::vector<std::uint8_t> src(static_cast<std::size_t>(width) * height * bps, 0x33);
    const auto mb = src.size() / 1048576.0;
    printf("GPU: %ls\n尺寸 %ux%u  位深 %d  平面 %.1f MB  每轮 %d 帧  共 %d 轮\n\n",
        desc.Description, width, height, tenBit ? 10 : 8, mb, frames, rounds);

    // 预热
    TimePath(ctx.Get(), def.Get(), stg.Get(), src, width, height, bps, 2, 0);
    TimePath(ctx.Get(), dyn.Get(), stg.Get(), src, width, height, bps, 2, 1);
    TimePath(ctx.Get(), def.Get(), stg.Get(), src, width, height, bps, 2, 2);

    std::vector<double> a, b, c;
    for (int r = 0; r < rounds; ++r) {
        // 每轮轮换顺序，消除先后偏差
        const auto order = r % 3;
        for (int k = 0; k < 3; ++k) {
            const auto which = (order + k) % 3;
            const auto ms = TimePath(ctx.Get(), which == 1 ? dyn.Get() : def.Get(), stg.Get(),
                src, width, height, bps, frames, which);
            if (ms < 0) { printf("path %d 失败\n", which); return 5; }
            (which == 0 ? a : (which == 1 ? b : c)).push_back(ms);
        }
    }

    const auto ma = Median(a), mbb = Median(b), mc = Median(c);
    printf("  A) UpdateSubresource -> DEFAULT   %7.3f ms  (%5.1f GB/s)\n", ma, mb / ma);
    printf("  B) Map(DISCARD)      -> DYNAMIC   %7.3f ms  (%5.1f GB/s)\n", mbb, mb / mbb);
    printf("  C) staging + CopyResource         %7.3f ms  (%5.1f GB/s)\n", mc, mb / mc);
    printf("\n  最快: %s\n", ma <= mbb && ma <= mc ? "A" : (mbb <= mc ? "B" : "C"));
    printf("  C/A 比 = %.2fx\n", mc / ma);
    return 0;
}
