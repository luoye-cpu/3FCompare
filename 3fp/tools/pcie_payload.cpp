// 8K 软解加速候选评估（非 GPU 解码）
//
// 背景：8K P010 软解每帧要经 PCIe 上行约 99.6 MB，实测 4.2 GB/s
// => 23.7 ms/帧，是当前 20 fps 的主要限制。硬解可绕过（解码器直接产出 GPU 纹理），
// 但硬解并非总可用（编码不支持、驱动限制、多实例上限）。本探针评估
// **不解码但用 GPU 加速** 的几条路，量化各自能省多少传输量。
//
// 候选：
//   A. 10bit 打包传输（P010 的 10 位存在 16 位容器里，37.5% 是填充）
//      -> 先 CPU 打包成 10bit 紧凑格式再上传，GPU 侧解包
//   B. 降色深传输（YUV420 8bit 而非 10bit）=> 直接减半，但有损
//   C. 只传显示需要的内容（窗口 2560x1440，源 7680x4320 => 9 倍差异）
//      -> 需要 CPU 先缩放到窗口尺寸，属于 CPU 缩放（非 GPU 加速）
//   D. 分块传输（tile）+ 只传变化块（视频不适用：每帧全变）
//
// 本探针只算**传输量**，不做实际实现 —— 用于判断哪条路值得投入。
//
// 用法：pcie_payload [--width W] [--height H] [--window WxH]

#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>

namespace {
struct Layout {
    const char* name;
    double bytes;       // 每帧传输字节
    bool lossless;
    const char* note;
};

/// 各种"上传什么"的传输量（8K 为例）
void Analyze(const std::uint32_t w, const std::uint32_t h,
             const std::uint32_t windowWidth, const std::uint32_t windowHeight) {
    const double pixels = static_cast<double>(w) * h;
    const double chromaPixels = static_cast<double>((w + 1) / 2) * ((h + 1) / 2);

    const double p010 = pixels * 2.0 + chromaPixels * 4.0;          // Y 16bit + UV 16bitx2
    const double packed10 = (pixels * 10.0 + chromaPixels * 20.0) / 8.0;  // 10bit 紧凑
    const double nv12 = pixels + chromaPixels * 2.0;                 // 8bit
    const double windowScale = static_cast<double>(windowWidth) * windowHeight;
    // 缩放到窗口后按 NV12 传（需 CPU 缩放）
    const double windowNv12 = windowScale + windowScale / 2.0;

    const Layout options[] = {
        {"P010 原样（现状）",        p010,      true,  "Y16+UV16，37.5% 是填充位"},
        {"10bit 紧凑打包",           packed10,  true,  "CPU 打包 10bit，GPU 解包"},
        {"NV12 8bit（降色深）",      nv12,      false, "HDR 不可接受，SDR 可接受"},
        {"CPU 缩放到窗口 + NV12",    windowNv12, false, "CPU 先缩放，省大量传输"},
    };

    printf("源 %ux%u（%.1f Mpx），窗口 %ux%u\n\n", w, h, pixels / 1e6, windowWidth, windowHeight);
    printf("%-26s %10s %10s %8s  %s\n", "方案", "MB/帧", "相对", "无损", "说明");
    for (const auto& o : options) {
        const auto mb = o.bytes / 1048576.0;
        printf("%-26s %10.1f %9.2fx %8s  %s\n", o.name, mb, p010 / o.bytes,
            o.lossless ? "是" : "**否**", o.note);
    }

    // 按实测 4.2 GB/s 估算帧时间与上限
    constexpr double bandwidthGBs = 4.2;
    printf("\n按实测 %.1f GB/s 估算：\n", bandwidthGBs);
    printf("%-26s %10s %12s\n", "方案", "帧时间", "理论上限");
    for (const auto& o : options) {
        const auto ms = o.bytes / (bandwidthGBs * 1e9) * 1000.0;
        printf("%-26s %8.1f ms %10.1f fps\n", o.name, ms, 1000.0 / ms);
    }
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::uint32_t w = 7680, h = 4320, ww = 2560, wh = 1440;
    for (int i = 1; i < argc; ++i) {
        const std::wstring k = argv[i];
        if (k == L"--width" && i + 1 < argc) w = static_cast<std::uint32_t>(_wtoi(argv[++i]));
        else if (k == L"--height" && i + 1 < argc) h = static_cast<std::uint32_t>(_wtoi(argv[++i]));
        else if (k == L"--window" && i + 1 < argc) {
            const std::wstring v = argv[++i];
            const auto x = v.find(L'x');
            if (x != std::wstring::npos) {
                ww = static_cast<std::uint32_t>(_wtoi(v.substr(0, x).c_str()));
                wh = static_cast<std::uint32_t>(_wtoi(v.substr(x + 1).c_str()));
            }
        }
    }
    Analyze(w, h, ww, wh);
    return 0;
}
