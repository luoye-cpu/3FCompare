// dxgi_display_probe.cpp —— 只读地摊开"这台机器现在处在什么显示状态"。
//
// 为什么单独造这个探针：上一轮量的是**应用后台缓冲**，它早于 DWM/驱动合成，回答不了
// "用户把 HDR 打开之后显示端是什么"。这里读的全是系统/驱动侧事实：
//   · DISPLAYCONFIG 高级色彩（AdvancedColorEnabled = Windows 的 HDR/高级色彩是否开着）
//   · SDR 白电平（DWM 用多少尼特当 SDR 白 —— 双重映射分析里的关键量）
//   · DXGI_OUTPUT_DESC1 的当前色彩空间、每通道位深、亮度与原色（PQ 桌面还是 sRGB 桌面）
//   · IDXGIOutput5::CheckOverlaySupport：NV12/P010 能否走硬件 overlay(MPO)
//     —— 驱动把一面当"视频"从而叠 RTX Video 增强，靠的就是这条视频面通道。
// 全部只读：不改显示设置、不动注册表。不用 WRL/ComPtr（这台机器的 SDK 头里没有 wrl/client.h）。
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <cstdio>
#include <vector>

template <typename Packet>
static bool QueryPacket(Packet& packet) noexcept {
    if constexpr (requires { packet.header.size; }) {
        packet.header.size = sizeof(Packet);
        return DisplayConfigGetDeviceInfo(&packet.header) == ERROR_SUCCESS;
    } else {
        return DisplayConfigGetDeviceInfo(&packet.header, sizeof(Packet)) == ERROR_SUCCESS;
    }
}

static const char* ColorSpaceName(const DXGI_COLOR_SPACE_TYPE value) noexcept {
    switch (value) {
    case DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709: return "sRGB(709,G2.2) = 桌面 SDR";
    case DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709: return "scRGB FP16(G10) = 高级色彩";
    case DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020: return "HDR10/PQ BT.2020";
    case DXGI_COLOR_SPACE_RGB_STUDIO_G2084_NONE_P2020: return "HDR10 studio/PQ";
    case DXGI_COLOR_SPACE_RGB_STUDIO_G22_NONE_P709: return "studio 709 G2.2";
    default: return "其它/未列出";
    }
}

static void ProbeOverlay(IDXGIOutput* output) {
    IDXGIOutput5* output5 = nullptr;
    if (FAILED(output->QueryInterface(IID_PPV_ARGS(&output5)))) return;
    const DXGI_FORMAT probes[] = { DXGI_FORMAT_NV12, DXGI_FORMAT_P010,
        DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM };
    for (const auto format : probes) {
        UINT support = 0;
        const auto rc = output5->CheckOverlaySupport(format, output /* pTarget：文档允许 NULL，
            // 但本机四个格式全给 E_INVALIDARG(0x80070057) ⇒ NULL 这条调用形状不可用，
            // 不能据此判"没有 MPO"；改成传真实输出再量。 */, &support);
        std::printf("    CheckOverlaySupport(%-9s) rc=0x%08X flags=0x%X%s%s\n",
            format == DXGI_FORMAT_NV12 ? "NV12" : format == DXGI_FORMAT_P010 ? "P010" :
            format == DXGI_FORMAT_R16G16B16A16_FLOAT ? "RGBA16F" : "BGRA8",
            static_cast<UINT>(rc), support,
            (support & DXGI_OVERLAY_SUPPORT_FLAG_DIRECT) ? " DIRECT" : "",
            (support & DXGI_OVERLAY_SUPPORT_FLAG_SCALING) ? " SCALING" : "");
    }
    output5->Release();
}

int main() {
    UINT32 pathCount = 0, modeCount = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS &&
        GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, &pathCount, &modeCount) != ERROR_SUCCESS) {
        std::printf("GetDisplayConfigBufferSizes 失败\n"); return 1;
    }
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount,
                           modes.data(), nullptr) != ERROR_SUCCESS) {
        pathCount = static_cast<UINT32>(paths.size()); modeCount = static_cast<UINT32>(modes.size());
        if (QueryDisplayConfig(QDC_DATABASE_CURRENT, &pathCount, paths.data(), &modeCount,
                               modes.data(), nullptr) != ERROR_SUCCESS) {
            std::printf("QueryDisplayConfig 失败\n"); return 1;
        }
    }
    std::printf("== DISPLAYCONFIG：%u 条路径\n", pathCount);
    for (UINT32 i = 0; i < pathCount; ++i) {
        if ((paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
        DISPLAYCONFIG_TARGET_DEVICE_NAME target{};
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.adapterId = paths[i].targetInfo.adapterId;
        target.header.id = paths[i].targetInfo.id;
        if (QueryPacket(target))
            std::printf("  目标: 友好名 %S  路径 %S\n",
                target.monitorFriendlyDeviceName, target.monitorDevicePath);
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color{};
        color.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        color.header.adapterId = paths[i].targetInfo.adapterId;
        color.header.id = paths[i].targetInfo.id;
        if (QueryPacket(color))
            // 字段名按本机 SDK：wingdi.h:3167-3186（这一版里没有亮度字段，
            // 亮度只能从 DXGI_OUTPUT_DESC1 与 SDR 白电平两处拿）。
            std::printf("  高级色彩: supported=%u **enabled=%u** wideColorEnforced=%u "
                "forceDisabled=%u | colorEncoding=%d 每通道 %u 位\n",
                color.advancedColorSupported, color.advancedColorEnabled,
                color.wideColorEnforced, color.advancedColorForceDisabled,
                static_cast<int>(color.colorEncoding), color.bitsPerColorChannel);
        else std::printf("  高级色彩: 读不到\n");
        DISPLAYCONFIG_SDR_WHITE_LEVEL sdr{};
        sdr.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        sdr.header.adapterId = paths[i].targetInfo.adapterId;
        sdr.header.id = paths[i].targetInfo.id;
        if (QueryPacket(sdr) && sdr.SDRWhiteLevel)
            std::printf("  SDR 白电平: 原始 %u => %.1f nits（%.3f × 80 参考）\n", sdr.SDRWhiteLevel,
                static_cast<float>(sdr.SDRWhiteLevel) / 1000.0f * 80.0f,
                static_cast<float>(sdr.SDRWhiteLevel) / 1000.0f);
        else std::printf("  SDR 白电平: 未提供（通常=桌面处于 SDR）\n");
    }

    IDXGIFactory6* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) { std::printf("无 DXGI\n"); return 1; }
    for (UINT adapterIndex = 0; ; ++adapterIndex) {
        IDXGIAdapter1* adapter = nullptr;
        if (factory->EnumAdapters1(adapterIndex, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 description{};
        adapter->GetDesc1(&description);
        std::printf("== 适配器 #%u：%S vendor=0x%04X 专用显存 %llu MB\n", adapterIndex,
            description.Description, description.VendorId,
            static_cast<unsigned long long>(description.DedicatedVideoMemory / (1024ull * 1024ull)));
        for (UINT outputIndex = 0; ; ++outputIndex) {
            IDXGIOutput* output = nullptr;
            if (adapter->EnumOutputs(outputIndex, &output) == DXGI_ERROR_NOT_FOUND) break;
            IDXGIOutput6* output6 = nullptr;
            if (SUCCEEDED(output->QueryInterface(IID_PPV_ARGS(&output6)))) {
                DXGI_OUTPUT_DESC1 desc1{};
                if (SUCCEEDED(output6->GetDesc1(&desc1))) {
                    // 字段名一律按权威头文件 dxgi1_6.h:384-399 写：BitsPerColor /
                    // MaxFullFrameLuminance / RedPrimary[2]，没有 HDR10/AdvancedColor 布尔
                    // （那两个是我上一版凭记忆编的，编译直接 C2039 拒绝）。
                    std::printf("  输出 %S: 当前色彩空间 %u [%s] 每通道 %u 位\n",
                        desc1.DeviceName, static_cast<UINT>(desc1.ColorSpace),
                        ColorSpaceName(desc1.ColorSpace), desc1.BitsPerColor);
                    std::printf("    亮度: 最大 %.1f 满帧 %.1f 最小 %.4f\n",
                        desc1.MaxLuminance, desc1.MaxFullFrameLuminance, desc1.MinLuminance);
                    std::printf("    原色 R(%.4f,%.4f) G(%.4f,%.4f) B(%.4f,%.4f) 白(%.4f,%.4f)\n",
                        desc1.RedPrimary[0], desc1.RedPrimary[1],
                        desc1.GreenPrimary[0], desc1.GreenPrimary[1],
                        desc1.BluePrimary[0], desc1.BluePrimary[1],
                        desc1.WhitePoint[0], desc1.WhitePoint[1]);
                }
                output6->Release();
            }
            ProbeOverlay(output);
            output->Release();
        }
        adapter->Release();
    }
    factory->Release();
    return 0;
}
