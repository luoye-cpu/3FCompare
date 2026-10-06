/*
 * sdr_white_probe.cpp — V7 定案探针：DWM 对显式 scRGB 窗口会不会再套一次 SDR 白映射？
 *
 * 背景（3fp/docs/27 §12.5）：3FCompare 把 SDR 源送上 16bit scRGB 链（G10 契约），
 * 纸白取 Windows「SDR 内容亮度」（SdrWhiteLevelInNits），输出值 = SdrWhite/80。
 * 前提：DWM 对显式 scRGB 窗口按绝对亮度处理（1.0 = 80 nits），不再套一次映射。
 *
 * 做法：
 *   1. FP16 scRGB 交换链窗口（SetColorSpace1(G10)），清屏为已知线性值 1.0（= 80 nits）。
 *   2. Windows.Graphics.Capture（Float16 帧池）捕获自己的窗口，读回同一线性值。
 *   3. HDR 桌面判读：读回 ≈ 1.0 ⇒ 无二次映射（当前实现正确）；
 *      读回 ≈ SdrWhite/80 ⇒ 存在二次映射，纸白应改回 80。
 *      SDR 桌面读回 ≈ 1.0 属预期，不能定案。
 *
 * 构建：python tools/build_kernel_3fp.py --white-probe
 * 运行：3fp\build\sdr_white_probe.exe
 */

#include <windows.h>
#include <objbase.h>
#include <roapi.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dxgi1_6.h>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.Display.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.display.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

// IDirect3DDxgiInterfaceAccess 是 interop 头里的全局命名空间类型（不在 winrt:: 下）。
using ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

namespace {

constexpr float kClearValue = 1.0f;   // scRGB 1.0 = 80 nits
constexpr int kClientW = 400;
constexpr int kClientH = 300;

template <typename T>
struct ComGuard {
    T* ptr = nullptr;
    ComGuard() = default;
    ~ComGuard() { if (ptr) ptr->Release(); }
    T** put() { return &ptr; }
    T* get() const { return ptr; }
    T* operator->() const { return ptr; }
};

/* IEEE 754 half → float（FP16 staging Map 出来的是半精度）。 */
float HalfToFloat(std::uint16_t h) noexcept {
    const std::uint32_t sign = static_cast<std::uint32_t>(h & 0x8000u) << 16;
    const std::uint32_t exp10 = (h & 0x7C00u) >> 10;
    const std::uint32_t mant = h & 0x03FFu;
    std::uint32_t bits;
    if (exp10 == 0) {
        if (mant == 0) {
            bits = sign;
        } else {
            int shift = 0;
            std::uint32_t m = mant;
            while ((m & 0x0400u) == 0) { m <<= 1; ++shift; }
            bits = sign | (static_cast<std::uint32_t>(127 - 14 - shift) << 23) |
                   ((m & 0x03FFu) << 13);
        }
    } else if (exp10 == 0x1Fu) {
        bits = sign | 0x7F800000u | (mant << 13);
    } else {
        bits = sign | ((exp10 - 15 + 127) << 23) | (mant << 13);
    }
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM w, LPARAM l) {
    return DefWindowProcW(window, message, w, l);
}

/* SDK 形态兼容：28000 起 size 在 header、API 单参数；老 SDK 双参数。同内核包装。 */
template <typename Packet>
bool QueryDisplayConfigInfo(Packet& packet) noexcept {
    if constexpr (requires { packet.header.size; }) {
        packet.header.size = sizeof(Packet);
        return DisplayConfigGetDeviceInfo(&packet.header) == ERROR_SUCCESS;
    } else {
        return DisplayConfigGetDeviceInfo(&packet.header, sizeof(Packet)) == ERROR_SUCCESS;
    }
}

/* WinRT 路径（内核 EffectivePaperWhiteNits 实际用的读数来源）。 */
float ReadSdrWhiteLevelWinRT(HWND window, int* advancedColorKind) noexcept {
    *advancedColorKind = -1;
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    if (monitor == nullptr) return 0.0f;
    const auto initializeResult = RoInitialize(RO_INIT_MULTITHREADED);
    const bool shouldUninitialize = SUCCEEDED(initializeResult);
    float nits = 0.0f;
    do {
        HSTRING_HEADER classHeader{};
        HSTRING className = nullptr;
        if (FAILED(WindowsCreateStringReference(
                RuntimeClass_Windows_Graphics_Display_DisplayInformation,
                ARRAYSIZE(RuntimeClass_Windows_Graphics_Display_DisplayInformation) - 1,
                &classHeader, &className)) || className == nullptr) break;
        ComGuard<IDisplayInformationStaticsInterop> statics;
        if (FAILED(RoGetActivationFactory(className, IID_PPV_ARGS(statics.put())))) break;
        ComGuard<ABI::Windows::Graphics::Display::IDisplayInformation5> information;
        if (FAILED(statics->GetForMonitor(monitor, IID_PPV_ARGS(information.put())))) break;
        ComGuard<ABI::Windows::Graphics::Display::IAdvancedColorInfo> color;
        if (FAILED(information->GetAdvancedColorInfo(color.put())) || color.get() == nullptr) break;
        ABI::Windows::Graphics::Display::AdvancedColorKind kind{};
        if (SUCCEEDED(color->get_CurrentAdvancedColorKind(&kind)))
            *advancedColorKind = static_cast<int>(kind);
        float value = 0.0f;
        if (SUCCEEDED(color->get_SdrWhiteLevelInNits(&value)) && std::isfinite(value) &&
            value > 0.0f)
            nits = value;
    } while (false);
    if (shouldUninitialize) RoUninitialize();
    return nits;
}

/* 系统「SDR 内容亮度」：与内核 ReadSdrWhiteLevelDisplayConfig 同一来源。 */
float ReadSdrWhiteLevelNits(HWND window) noexcept {
    const auto monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
    if (monitor == nullptr) return 0.0f;
    MONITORINFOEXW monitorInfo{};
    monitorInfo.cbSize = sizeof(monitorInfo);
    if (!GetMonitorInfoW(monitor, &monitorInfo)) return 0.0f;
    UINT32 pathCount = 0;
    UINT32 modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, &pathCount, &modeCount) != ERROR_SUCCESS)
        return 0.0f;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_DATABASE_CURRENT, &pathCount, paths.data(), &modeCount,
                           modes.data(), nullptr) != ERROR_SUCCESS)
        return 0.0f;
    for (UINT32 index = 0; index < pathCount; ++index) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.adapterId = paths[index].sourceInfo.adapterId;
        source.header.id = paths[index].sourceInfo.id;
        if (!QueryDisplayConfigInfo(source)) continue;
        if (_wcsicmp(source.viewGdiDeviceName, monitorInfo.szDevice) != 0) continue;
        DISPLAYCONFIG_SDR_WHITE_LEVEL sdr{};
        sdr.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        sdr.header.adapterId = paths[index].sourceInfo.adapterId;
        sdr.header.id = paths[index].targetInfo.id;
        if (!QueryDisplayConfigInfo(sdr)) return 0.0f;
        return static_cast<float>(sdr.SDRWhiteLevel) / 1000.0f * 80.0f;
    }
    return 0.0f;
}

/* 建窗口 + FP16 scRGB 交换链，清成 kClearValue 并 Present。成功返回窗口句柄与设备。 */
HWND ShowKnownWhite(ID3D11Device** outDevice, IDXGISwapChain1** outSwapChain) {
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"SdrWhiteProbeClass";
    if (!RegisterClassW(&windowClass)) return nullptr;
    RECT rectangle{0, 0, kClientW, kClientH};
    AdjustWindowRect(&rectangle, WS_OVERLAPPEDWINDOW, FALSE);
    HWND window = CreateWindowExW(0, L"SdrWhiteProbeClass", L"scRGB 1.0 (80 nits)",
                                  WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60,
                                  rectangle.right - rectangle.left,
                                  rectangle.bottom - rectangle.top,
                                  nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (window == nullptr) return nullptr;
    ComGuard<ID3D11Device> device;
    ComGuard<ID3D11DeviceContext> context;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                 D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                                 D3D11_SDK_VERSION, device.put(), nullptr, context.put()))) {
        DestroyWindow(window);
        return nullptr;
    }
    ComGuard<IDXGIDevice> dxgiDevice;
    ComGuard<IDXGIAdapter> adapter;
    ComGuard<IDXGIFactory2> factory;
    if (FAILED(device.get()->QueryInterface(IID_PPV_ARGS(dxgiDevice.put()))) ||
        FAILED(dxgiDevice.get()->GetAdapter(adapter.put())) ||
        FAILED(adapter.get()->GetParent(IID_PPV_ARGS(factory.put())))) {
        DestroyWindow(window);
        return nullptr;
    }
    DXGI_SWAP_CHAIN_DESC1 swapDesc{};
    swapDesc.Width = kClientW;
    swapDesc.Height = kClientH;
    swapDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;   // scRGB FP16
    swapDesc.SampleDesc.Count = 1;
    swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapDesc.BufferCount = 2;
    swapDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    swapDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    ComGuard<IDXGISwapChain1> swapChain;
    if (FAILED(factory.get()->CreateSwapChainForHwnd(device.get(), window, &swapDesc, nullptr,
                                                     nullptr, swapChain.put()))) {
        DestroyWindow(window);
        return nullptr;
    }
    // 显式 scRGB 契约：不设置 DWM 会按 sRGB gamma 解释线性数据（TrueToneCap 同款教训）。
    ComGuard<IDXGISwapChain3> swapChain3;
    if (FAILED(swapChain.get()->QueryInterface(IID_PPV_ARGS(swapChain3.put()))) ||
        FAILED(swapChain3.get()->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709))) {
        DestroyWindow(window);
        return nullptr;
    }
    ComGuard<ID3D11Texture2D> backBuffer;
    if (FAILED(swapChain.get()->GetBuffer(0, IID_PPV_ARGS(backBuffer.put())))) {
        DestroyWindow(window);
        return nullptr;
    }
    D3D11_RENDER_TARGET_VIEW_DESC rtvDesc{};
    rtvDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    rtvDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ComGuard<ID3D11RenderTargetView> rtv;
    if (FAILED(device.get()->CreateRenderTargetView(backBuffer.get(), &rtvDesc, rtv.put()))) {
        DestroyWindow(window);
        return nullptr;
    }
    const float clearColor[4] = {kClearValue, kClearValue, kClearValue, 1.0f};
    // FLIP 模型至少 Present 两次，保证第一张缓冲确实上屏。
    for (int i = 0; i < 2; ++i) {
        context.get()->ClearRenderTargetView(rtv.get(), clearColor);
        swapChain.get()->Present(1, 0);
    }
    Sleep(600);   // 等合成器上屏
    *outDevice = device.ptr;
    *outSwapChain = swapChain.ptr;
    device.ptr = nullptr;      // 所有权移交调用方
    context.ptr = nullptr;
    swapChain.ptr = nullptr;
    return window;
}

}  // namespace

int main() {
    // 崩溃诊断：无缓冲输出，崩溃前的进度标记不会随缓冲丢失。
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    std::printf("=== SDR white double-mapping probe (V7) ===\n");
    std::printf("clear value : %.3f (scRGB linear = %.0f nits)\n", kClearValue,
                kClearValue * 80.0f);
    if (!winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported()) {
        std::printf("WGC not supported on this system\n");
        return 2;
    }
    std::printf("step: WGC supported\n");
    ComGuard<ID3D11Device> device;
    ComGuard<IDXGISwapChain1> swapChain;
    HWND window = ShowKnownWhite(device.put(), swapChain.put());
    if (window == nullptr) {
        std::printf("setup failed\n");
        return 2;
    }
    const float sdrWhiteNits = ReadSdrWhiteLevelNits(window);
    int advancedColorKind = -1;
    const float sdrWhiteWinRT = ReadSdrWhiteLevelWinRT(window, &advancedColorKind);
    std::printf("sdrWhite(DISPLAYCONFIG): %.0f nits\n", sdrWhiteNits);
    std::printf("sdrWhite(WinRT)        : %.0f nits, AdvancedColorKind=%d "
                "(1=Standard 2=WideColorGamut 3=HighDynamicRange)\n",
                sdrWhiteWinRT, advancedColorKind);
    std::printf("step: display/swapchain ready\n");

    winrt::com_ptr<IDXGIDevice> dxgiDevice;
    winrt::check_hresult(device.get()->QueryInterface(winrt::guid_of<IDXGIDevice>(),
                                                      dxgiDevice.put_void()));
    winrt::com_ptr<::IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(
        dxgiDevice.get(), inspectable.put()));
    auto captureDevice = inspectable.as<
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();

    auto interop = winrt::get_activation_factory<
        winrt::Windows::Graphics::Capture::GraphicsCaptureItem,
        IGraphicsCaptureItemInterop>();
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
    winrt::check_hresult(interop->CreateForWindow(
        window,
        winrt::guid_of<winrt::Windows::Graphics::Capture::GraphicsCaptureItem>(),
        winrt::put_abi(item)));

    auto pool = winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(
        captureDevice,
        winrt::Windows::Graphics::DirectX::DirectXPixelFormat::R16G16B16A16Float, 2,
        winrt::Windows::Graphics::SizeInt32{kClientW, kClientH});
    auto session = pool.CreateCaptureSession(item);
    try { session.IsCursorCaptureEnabled(false); } catch (...) {}
    session.StartCapture();
    std::printf("step: capture session started\n");

    winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame frame{nullptr};
    for (int attempt = 0; attempt < 60 && frame == nullptr; ++attempt) {
        frame = pool.TryGetNextFrame();
        if (frame == nullptr) Sleep(50);
    }
    if (frame == nullptr) {
        std::printf("capture timeout: no frame within 3s\n");
        return 2;
    }

    auto access = frame.Surface().as<IDirect3DDxgiInterfaceAccess>();
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    D3D11_TEXTURE2D_DESC stagingDesc = desc;
    stagingDesc.ArraySize = 1;
    stagingDesc.MipLevels = 1;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;
    winrt::com_ptr<ID3D11Texture2D> staging;
    winrt::check_hresult(device.get()->CreateTexture2D(&stagingDesc, nullptr, staging.put()));
    winrt::com_ptr<ID3D11DeviceContext> context;
    device.get()->GetImmediateContext(context.put());
    context->CopyResource(staging.get(), texture.get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    winrt::check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));

    const int centerX = static_cast<int>(desc.Width) / 2;
    const int centerY = static_cast<int>(desc.Height) / 2;
    auto* row = static_cast<const std::uint16_t*>(mapped.pData) +
        static_cast<size_t>(centerY) * (mapped.RowPitch / 2) + static_cast<size_t>(centerX) * 4;
    const float r = HalfToFloat(row[0]);
    const float g = HalfToFloat(row[1]);
    const float b = HalfToFloat(row[2]);
    context->Unmap(staging.get(), 0);

    std::printf("captured    : R=%.4f G=%.4f B=%.4f (linear; G=%.0f nits after DWM)\n",
                r, g, b, g * 80.0f);
    const float reference = sdrWhiteNits > 0.0f ? sdrWhiteNits / 80.0f : 1.0f;
    const bool nearOne = std::fabs(g - 1.0f) < 0.05f;
    const bool nearWhite = sdrWhiteNits > 0.0f && std::fabs(g - reference) < 0.05f;
    // 只有 AdvancedColorKind == HighDynamicRange(3) 才允许下结论：SDR/WCG 桌面上
    // 合成器不做 SDR 白提升，清屏 1.0 读回必然 ≈1.0，会伪装成「无二次映射」。
    if (advancedColorKind != 3) {
        std::printf("verdict     : inconclusive - AdvancedColorKind=%d is not HDR; "
                    "enable HDR (Advanced Color) for the V7 verdict.\n", advancedColorKind);
    } else if (nearOne && !nearWhite) {
        std::printf("verdict     : no double mapping (captured == 1.0). "
                    "Current paper-white (SdrWhite) is CORRECT.\n");
    } else if (nearWhite) {
        std::printf("verdict     : double mapping present (captured == SdrWhite/80). "
                    "Paper white must go back to 80 nits output (=1.0).\n");
    } else {
        std::printf("verdict     : inconclusive (captured matches neither 1.0 nor "
                    "SdrWhite/80 = %.4f).\n", reference);
    }
    // 不显式 Close、不走正常退出：free-threaded 帧池回调线程与主线程的 WinRT
    // teardown 存在竞态（实测 0xC0000005，put_abi 泄漏也拦不住——静态析构仍会碰
    // 它们）。结论已完整输出，ExitProcess 跳过全部析构（native/wgc_capture 的
    // Graveyard「故意泄漏」是同一问题的同一类处理）。
    std::fflush(stdout);
    ExitProcess(0);
}
