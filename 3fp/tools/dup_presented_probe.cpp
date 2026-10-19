// 过 DWM 那一臂：同一扇窗口、同一片客户区，两个读者各读一次。
//
//   读者 1（内核）：FFF3FP_ReadVideoPixelRegion —— 拿的是我们交给交换链的像素，
//                   在 DWM 合成**之前**。
//   读者 2（桌面复制）：IDXGIOutput5::DuplicateOutput1 —— 拿的是 DWM 合成**之后**、
//                   送显之前的那份桌面图像。
//
// 为什么要这一臂：之前所有"开 Windows HDR 没有改动我们的画面"的判据都来自读者 1，
// 它们能证的只是"没进我们的帧"。驱动/DWM 在合成阶段做的事（Auto HDR、画质增强、
// 色调映射），读者 1 结构上看不到。把同一片像素交给读者 2，再和读者 1 做假设表对撞：
//   呈上的 v 与 复制到的 c 之间，要么 c = k·v（纯线性，合成阶段没插颜色处理），
//   要么 c = PQ(E·v)（DWM 按 nits 标定做了 ST2084 编码），
//   要么两者都不吻合（有按色的处理进来 —— 那才是"用户眼里多了东西"的证据）。
// 残差表由 _3fc_tmp/analyze_dup.py 算，这里只负责如实落盘。
//
// 用法：dup_presented_probe <媒体> [--sr-window WxH] [--dump DIR]
// 构建：见 _3fc_tmp/dup_round.ps1（INCLUDE 需带 SDK 的 winrt 支；链接 dxgi.lib）

#include <windows.h>

#include <d3d11.h>
#include <dxgi1_6.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char* json) {
    if (eventType == FFF3FPEvent::ColorModeChanged || eventType == FFF3FPEvent::DeviceChanged)
        std::printf("  event type=%u: %s\n", static_cast<unsigned>(eventType), json ? json : "(空)");
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD64>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(1);
    }
}
std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    return out;
}
// 半精度→单精度：复制面给 RGBA16F 时要自己解，不引第三方头。
float HalfToFloat(std::uint16_t h) noexcept {
    const std::uint32_t sign = static_cast<std::uint32_t>(h & 0x8000u) << 16;
    std::uint32_t exp = (h >> 10) & 0x1fu;
    std::uint32_t mant = h & 0x3ffu;
    if (exp == 0) {
        if (mant == 0) { const std::uint32_t bits = sign; float f; std::memcpy(&f, &bits, 4); return f; }
        // 非规格化：直接规格化回单精度
        exp = 127 - 15 + 1;
        while ((mant & 0x400u) == 0) { mant <<= 1; --exp; }
        mant &= 0x3ffu;
        const std::uint32_t bits = sign | (exp << 23) | (mant << 13);
        float f; std::memcpy(&f, &bits, 4); return f;
    }
    if (exp == 0x1f) {
        const std::uint32_t bits = sign | 0x7f800000u | (mant << 13);
        float f; std::memcpy(&f, &bits, 4); return f;
    }
    const std::uint32_t bits = sign | ((exp - 15 + 127) << 23) | (mant << 13);
    float f; std::memcpy(&f, &bits, 4); return f;
}
const char* FormatName(const DXGI_FORMAT f) noexcept {
    switch (f) {
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return "RGBA16F(scRGB 线性)";
    case DXGI_FORMAT_R10G10B10A2_UNORM: return "RGB10A2";
    case DXGI_FORMAT_B8G8R8A8_UNORM: return "BGRA8";
    case DXGI_FORMAT_R8G8B8A8_UNORM: return "RGBA8";
    default: return "其它";
    }
}

void DumpBlock(const std::wstring& dir, const wchar_t* name, const std::uint32_t width,
    const std::uint32_t height, const char* format, const std::vector<float>& px) {
    if (dir.empty() || px.empty()) return;
    const auto bin = std::filesystem::path(dir) / name;
    std::ofstream ofs(bin, std::ios::binary | std::ios::trunc);
    ofs.write(reinterpret_cast<const char*>(px.data()),
        static_cast<std::streamsize>(px.size() * sizeof(float)));
    ofs.close();
    std::ofstream mf(std::wstring(bin.wstring()) + L".meta");
    mf << "width=" << width << "\nheight=" << height << "\nformat=" << format
       << "\nlayout=rgba_float32_rowmajor_topdown\nbytes=" << px.size() * sizeof(float) << "\n";
    std::printf("  dumped: %s (%ux%u %s floats=%zu)\n", Utf8(bin.wstring().c_str()).c_str(), width, height,
        format, px.size());
}
// 对照块：另一扇窗口，纯红实底，盖在探针客户区左上角。
// 为什么需要它：本轮拿到的读数是"复制到的 == 呈上的，逐点相同"。如果复制臂其实
// 偷偷读了我们自己的交换链，这条判据同样成立、却一文不值。红块不属于我们的交换链，
// 只属于合成后的桌面 —— 复制里读到红，才证明读者 2 真读了 DWM 之后的桌面。
LRESULT CALLBACK MarkerProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_PAINT) {
        PAINTSTRUCT ps{};
        const auto dc = BeginPaint(h, &ps);
        RECT r{};
        GetClientRect(h, &r);
        const auto red = CreateSolidBrush(RGB(255, 0, 0));
        (void)FillRect(dc, &r, red);
        DeleteObject(red);
        EndPaint(h, &ps);
        return 0;
    }
    if (m == WM_ERASEBKGND) return 1;
    return DefWindowProcW(h, m, w, l);
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { printf("用法: dup_presented_probe <媒体> [--sr-window WxH] [--dump DIR]\n"); return 2; }
    unsigned windowWidth = 1024, windowHeight = 600;
    std::wstring dumpDir;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--sr-window" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%ux%u", &windowWidth, &windowHeight);
        else if (std::wstring(argv[i]) == L"--dump" && i + 1 < argc) dumpDir = argv[++i];
    }
    // 复制面的坐标是物理桌面像素；进程不声明 DPI 感知会拿到缩放后的假坐标。
    // 值取 SDK 权威定义：windef.h:268 `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == ((DPI_AWARENESS_CONTEXT)-4)`。
    // 用 GetProcAddress 而非直接引用符号：符号可见性取决于 NTDDI_VERSION，
    // 编不过会比"这一臂没做"更难归因。
    if (const auto user32 = GetModuleHandleW(L"user32.dll")) {
        using SetCtxFn = BOOL(WINAPI*)(HANDLE);
        if (const auto fn = reinterpret_cast<SetCtxFn>(
                GetProcAddress(user32, "SetProcessDpiAwarenessContext"))) {
            (void)fn(reinterpret_cast<HANDLE>(static_cast<long long>(-4)));
        } else {
            printf("  (没有 SetProcessDpiAwarenessContext，坐标可能按虚像素算)\n");
        }
    }

    const auto path = Utf8(argv[1]);
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFVSRScRgbWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFVSRScRgbWnd", L"dup", WS_OVERLAPPEDWINDOW,
        120, 120, static_cast<int>(windowWidth), static_cast<int>(windowHeight),
        nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::D3D11;
    configuration.colorMode = FFF3FPColorMode::MapToHdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) {
        printf("创建失败\n"); return 1;
    }
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);

    // ---- 桌面复制（独立于内核设备，自己建一台 D3D11 设备）----
    IDXGIFactory1* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    IDXGIOutput* output = nullptr;
    IDXGIOutput5* output5 = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGIOutputDuplication* dup = nullptr;
    RECT desktop{};
    bool dupOk = false;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) {
        printf("无 DXGI 工厂\n");
    } else {
        MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);
        const auto mon = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        if (!GetMonitorInfoW(mon, &mi)) printf("无监视器信息\n");
        for (UINT a = 0; !output && factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; ++a) {
            for (UINT o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; ++o) {
                DXGI_OUTPUT_DESC od{};
                if (SUCCEEDED(output->GetDesc(&od)) && wcscmp(od.DeviceName, mi.szDevice) == 0) {
                    desktop = od.DesktopCoordinates; break;
                }
                output->Release(); output = nullptr;
            }
            if (!output) { adapter->Release(); adapter = nullptr; }
        }
        if (output != nullptr &&
            SUCCEEDED(output->QueryInterface(__uuidof(IDXGIOutput5), reinterpret_cast<void**>(&output5)))) {
            const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1};
            D3D_FEATURE_LEVEL got{};
            HRESULT hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 1, D3D11_SDK_VERSION, &device, &got, &context);
            if (FAILED(hr)) printf("建设备失败 hr=0x%08lx\n", hr);
            else {
                // 优先要 FP16：只有它保留 >1.0 的 scRGB 能量，BGRA8 会把高光削平。
                const DXGI_FORMAT allowed[] = {DXGI_FORMAT_R16G16B16A16_FLOAT,
                    DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM};
                hr = output5->DuplicateOutput1(device, 0, ARRAYSIZE(allowed), allowed, &dup);
                if (FAILED(hr)) printf("DuplicateOutput1 失败 hr=0x%08lx\n", hr);
                else dupOk = true;
            }
        }
    }
    if (!dupOk) { printf("复制通道没建起来 —— 本臂不成立\n"); }
    else {
        // 权威字段名回 dxgi1_2.h 的 DXGI_OUTDUPL_DESC 实测：ModeDesc / Rotation /
        // DesktopImageInSystemMemory —— 没有 ColorSpace，复制面的真实格式只能
        // 从取到的纹理 GetDesc 读（下面每臂都会打印）。
        DXGI_OUTDUPL_DESC dd{};
        dup->GetDesc(&dd);
        printf("复制通道: ModeDesc=%ux%u@%u/%u Hz 旋转=%d 系统内存=%d\n", dd.ModeDesc.Width,
            dd.ModeDesc.Height, dd.ModeDesc.RefreshRate.Numerator,
            dd.ModeDesc.RefreshRate.Denominator, static_cast<int>(dd.Rotation),
            dd.DesktopImageInSystemMemory ? 1 : 0);
        printf("桌面原点=(%d,%d) 尺寸=%dx%d\n", desktop.left, desktop.top,
            desktop.right - desktop.left, desktop.bottom - desktop.top);
    }

    // 抓一份合成后的桌面，裁剪到窗口客户区，转成 float32 RGBA 落盘。
    const auto captureClient = [&](const wchar_t* tag, std::vector<float>& out,
        std::uint32_t& w, std::uint32_t& h, std::string& fmt) -> bool {
        if (!dupOk) return false;
        RECT client{};
        if (!GetClientRect(window, &client)) return false;
        POINT lt{client.left, client.top}, rb{client.right, client.bottom};
        (void)ClientToScreen(window, &lt); (void)ClientToScreen(window, &rb);
        const long x0 = lt.x - desktop.left, y0 = lt.y - desktop.top;
        const long cw = rb.x - lt.x, chh = rb.y - lt.y;
        if (cw <= 0 || chh <= 0) return false;
        bool got = false;
        for (int attempt = 0; attempt < 12 && !got; ++attempt) {
            DXGI_OUTDUPL_FRAME_INFO fi{};
            IDXGIResource* resource = nullptr;
            const HRESULT hr = dup->AcquireNextFrame(1000, &fi, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) { continue; }
            if (FAILED(hr)) { printf("  %s AcquireNextFrame hr=0x%08lx\n", Utf8(tag).c_str(), hr); return false; }
            ID3D11Texture2D* tex = nullptr;
            if (FAILED(resource->QueryInterface(__uuidof(ID3D11Texture2D),
                    reinterpret_cast<void**>(&tex)))) { resource->Release(); dup->ReleaseFrame(); return false; }
            D3D11_TEXTURE2D_DESC td{};
            tex->GetDesc(&td);
            D3D11_TEXTURE2D_DESC sd = td;
            sd.ArraySize = 1; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            sd.Usage = D3D11_USAGE_STAGING; sd.MiscFlags = 0; sd.SampleDesc.Count = 1;
            ID3D11Texture2D* staging = nullptr;
            if (SUCCEEDED(device->CreateTexture2D(&sd, nullptr, &staging))) {
                context->CopyResource(staging, tex);
                D3D11_MAPPED_SUBRESOURCE ms{};
                if (SUCCEEDED(context->Map(staging, 0, D3D11_MAP_READ, 0, &ms))) {
                    const auto bytesPerPixel = td.Format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8u : 4u;
                    w = static_cast<std::uint32_t>(cw); h = static_cast<std::uint32_t>(chh);
                    out.assign(static_cast<std::size_t>(w) * h * 4, 0.0f);
                    fmt = FormatName(td.Format);
                    for (long row = 0; row < chh; ++row) {
                        const auto* src = static_cast<const std::uint8_t*>(ms.pData) +
                            static_cast<std::size_t>(y0 + row) * ms.RowPitch +
                            static_cast<std::size_t>(x0) * bytesPerPixel;
                        auto* dst = out.data() + static_cast<std::size_t>(row) * w * 4;
                        for (long col = 0; col < cw; ++col) {
                            if (td.Format == DXGI_FORMAT_R16G16B16A16_FLOAT) {
                                const auto* px = reinterpret_cast<const std::uint16_t*>(src) + col * 4;
                                dst[col * 4 + 0] = HalfToFloat(px[0]);
                                dst[col * 4 + 1] = HalfToFloat(px[1]);
                                dst[col * 4 + 2] = HalfToFloat(px[2]);
                                dst[col * 4 + 3] = HalfToFloat(px[3]);
                            } else if (td.Format == DXGI_FORMAT_R10G10B10A2_UNORM) {
                                std::uint32_t packed = 0;
                                std::memcpy(&packed, src + static_cast<std::size_t>(col) * 4, 4);
                                dst[col * 4 + 0] = static_cast<float>(packed & 0x3ffu) / 1023.0f;
                                dst[col * 4 + 1] = static_cast<float>((packed >> 10) & 0x3ffu) / 1023.0f;
                                dst[col * 4 + 2] = static_cast<float>((packed >> 20) & 0x3ffu) / 1023.0f;
                                dst[col * 4 + 3] = static_cast<float>((packed >> 30) & 0x3u) / 3.0f;
                            } else {
                                // 8 位：BGRA 或 RGBA 由格式名如实记录，判读侧按名字解释。
                                const auto* px = reinterpret_cast<const std::uint8_t*>(src) + col * 4;
                                const bool rgba = td.Format == DXGI_FORMAT_R8G8B8A8_UNORM;
                                dst[col * 4 + 0] = (rgba ? px[0] : px[2]) / 255.0f;
                                dst[col * 4 + 1] = px[1] / 255.0f;
                                dst[col * 4 + 2] = (rgba ? px[2] : px[0]) / 255.0f;
                                dst[col * 4 + 3] = px[3] / 255.0f;
                            }
                        }
                    }
                    context->Unmap(staging, 0);
                    got = true;
                }
                staging->Release();
            }
            tex->Release(); resource->Release(); dup->ReleaseFrame();
        }
        if (!got) printf("  %s 没取到新帧\n", Utf8(tag).c_str());
        return got;
    };

    const auto presented = [&](std::vector<float>& out, std::uint32_t& w, std::uint32_t& h,
        std::uint32_t& depth) {
        RECT client{};
        if (!GetClientRect(window, &client)) return false;
        w = static_cast<std::uint32_t>(client.right - client.left);
        h = static_cast<std::uint32_t>(client.bottom - client.top);
        out.assign(static_cast<std::size_t>(w) * h * 4, 0.0f);
        depth = 0;
        return FFF3FP_ReadVideoPixelRegion(player, 0, 0, w, h, out.data(),
            static_cast<std::uint32_t>(out.size()), &depth) == FFFResult::Success;
    };

    // 复制通道拍的是**合成后的整块桌面**，任何盖在客户区上的窗口都会一起进画面，
    // 那时"复制 vs 呈上"的对撞量的就不是我的像素了。本探针是自建的一次性窗口，
    // 取景期间置顶、收尾解除（不动用户的任何窗口）。
    (void)ShowWindow(window, SW_SHOW);
    (void)SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    (void)BringWindowToTop(window);

    // 对照块落在客户区左上角 (8,8) 起 40x40；坐标同时写进 dump 目录，判读侧按它挖洞。
    WNDCLASSW mw{};
    mw.lpfnWndProc = MarkerProc;
    mw.hInstance = wc.hInstance;
    mw.lpszClassName = L"FFFVSRMarkerWnd";
    (void)RegisterClassW(&mw);
    RECT probeClient{};
    (void)GetClientRect(window, &probeClient);
    const int markerSize = 40, markerX = 8, markerY = 8;
    POINT markerLt{markerX, markerY};
    (void)ClientToScreen(window, &markerLt);
    const auto marker = CreateWindowExW(WS_EX_TOPMOST, L"FFFVSRMarkerWnd", L"marker", WS_POPUP,
        markerLt.x, markerLt.y, markerSize, markerSize, nullptr, nullptr, wc.hInstance, nullptr);
    (void)ShowWindow(marker, SW_SHOW);
    (void)UpdateWindow(marker);
    (void)SetWindowPos(marker, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
    printf("MARKER 客户区坐标 x=%d y=%d 尺寸=%d（对照块盖在探针画面左上角）\n",
        markerX, markerY, markerSize);
    if (!dumpDir.empty()) {
        std::ofstream mf(std::filesystem::path(dumpDir) / L"marker.txt");
        mf << "x=" << markerX << "\ny=" << markerY << "\nsize=" << markerSize << "\n";
    }

    struct Arm { std::wstring tag; FFF3FPVideoSuperResolution mode; };
    for (const Arm arm : {Arm{L"off", FFF3FPVideoSuperResolution::Off},
                          Arm{L"on", FFF3FPVideoSuperResolution::Auto}}) {
        (void)FFF3FP_SetVideoSuperResolution(player, arm.mode);
        FFF3FP_Play(player);
        Pump(1500);
        FFF3FP_Pause(player);
        Pump(500);
        FFF3FP_Redraw(player);      // 制造一次桌面变化，让复制通道出帧
        // 每次取景前把对照块重新按到最上层：Z 序会被别的窗口翻掉，不会自愈。
        (void)SetWindowPos(marker, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        (void)UpdateWindow(marker);
        Pump(400);
        std::vector<float> ppx, cpx;
        std::uint32_t pw = 0, ph = 0, cw2 = 0, ch2 = 0, pdepth = 0;
        std::string cfmt;
        const auto pOk = presented(ppx, pw, ph, pdepth);
        const auto cOk = captureClient(arm.tag.c_str(), cpx, cw2, ch2, cfmt);
        printf("\n== 臂 %s ==  内核读回=%d(%ux%u outBits=%u) 复制=%d(%ux%u %s)\n",
            Utf8(arm.tag.c_str()), pOk ? 1 : 0, pw, ph, pdepth, cOk ? 1 : 0, cw2, ch2, cfmt.c_str());
        FFF3FPVideoSuperResolutionStatus status{};
        status.size = sizeof(status); status.version = 20;
        FFF3FP_GetVideoSuperResolutionStatus(player, &status);
        printf("  VSR requested=%d active=%d reason=%d src=%ux%u tgt=%ux%u\n",
            static_cast<int>(status.requested), status.active ? 1 : 0,
            static_cast<int>(status.reason), status.sourceWidth, status.sourceHeight,
            status.targetWidth, status.targetHeight);
        // 牙齿自检：同一坐标，复制应当读到红，内核读回应当读到我们自己的内容。
        // 若复制也读到了我们的内容，这一臂就是假的，后面所有"两读者相同"都作废。
        if (cOk && pw == cw2 && pOk) {
            const auto idx = (static_cast<std::size_t>(markerY + markerSize / 2) * cw2 +
                static_cast<std::size_t>(markerX + markerSize / 2)) * 4;
            printf("  对照块中心 复制=(%.4f,%.4f,%.4f) 内核读回=(%.4f,%.4f,%.4f)  期望：复制≈(1,0,0)\n",
                cpx[idx], cpx[idx + 1], cpx[idx + 2], ppx[idx], ppx[idx + 1], ppx[idx + 2]);
        }
        const std::string pfmt = "float32 RGBA ← 内核读回，交换链 outBits=" + std::to_string(pdepth);
        if (pOk && !dumpDir.empty()) DumpBlock(dumpDir, (L"pres_" + arm.tag + L".f32").c_str(), pw, ph, pfmt.c_str(), ppx);
        if (cOk && !dumpDir.empty()) DumpBlock(dumpDir, (L"dup_" + arm.tag + L".f32").c_str(), cw2, ch2, cfmt.c_str(), cpx);
    }

    if (dup) dup->Release();
    if (context) context->Release();
    if (device) device->Release();
    if (output5) output5->Release();
    if (output) output->Release();
    if (adapter) adapter->Release();
    if (factory) factory->Release();
    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (marker != nullptr) DestroyWindow(marker);
    if (window != nullptr) DestroyWindow(window);
    (void)UnregisterClassW(L"FFFVSRMarkerWnd", wc.hInstance);
    return dupOk ? 0 : 6;
}
