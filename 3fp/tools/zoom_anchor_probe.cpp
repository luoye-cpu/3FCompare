// 验证光标锚定缩放：缩放后，光标下的图像点是否保持不动。
//
// 对照 lakeUI PixelPictureBox 的滚轮逻辑：
//   Dim imgPt = 客户坐标转图片像素(e.Location)
//   _zoomFactor *= factor
//   _scrollX = imgPt.X * _zoomFactor - (e.Location.X - bw)   ' 保持 imgPt 不动
// 3FP 的 FFF3FP_ZoomViewAt 应达到同样效果。
//
// 判据：以某一锚点连续放大三次，锚点处的**图像内容**应基本不变。
//
// 本探针**带对照臂**，顺序是：起始读数 → 对照臂（pan +0.5 必须让该点变化）→
// 复位一致性（回到同一视图读数必须复原）→ 三步锚定缩放。
// 前两条不过就直接判"无牙"而不是判"通过" —— 平色区、整图已入窗（无平移余量）、
// 回读通道读到陈旧帧，三种情况都会让"漂移=0"成为测量假象。
//
// 退出码：0 PASS 锚定成立 / 1 建立或采样失败 / 2 用法 /
//         3 FAIL 锚点漂移超容差 / 4 判据无牙（未判定，不是通过）
//
// 用法：zoom_anchor_probe <媒体> [--zoom Z] [--anchor X,Y]

#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_open = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_open = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
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

/// 取窗口内一个正方形小块的平均色，用于判断锚点内容是否稳定。
/// spread 回填该小块内亮度的极差 —— 极差为 0 说明取到的是平色区，
/// 那样的样本点无论怎么缩放都不会变，判据会在"恒等"上绿灯。
bool SamplePatch(FFF3FPHandle player, const std::uint32_t cx, const std::uint32_t cy,
    const std::uint32_t size, float& r, float& g, float& b, float& spread) {
    std::vector<float> pixels(static_cast<std::size_t>(size) * size * 4, 0.0f);
    std::uint32_t bitDepth = 0;
    if (FFF3FP_ReadVideoPixelRegion(player, cx, cy, size, size, pixels.data(),
            static_cast<std::uint32_t>(pixels.size()), &bitDepth) != FFFResult::Success)
        return false;
    double sr = 0, sg = 0, sb = 0;
    const auto count = static_cast<std::size_t>(size) * size;
    float lowest = 1e30f, highest = -1e30f;
    for (std::size_t i = 0; i < count; ++i) {
        sr += pixels[i * 4]; sg += pixels[i * 4 + 1]; sb += pixels[i * 4 + 2];
        const float luma = 0.2126f * pixels[i * 4] + 0.7152f * pixels[i * 4 + 1] +
            0.0722f * pixels[i * 4 + 2];
        if (luma < lowest) lowest = luma;
        if (luma > highest) highest = luma;
    }
    r = static_cast<float>(sr / count);
    g = static_cast<float>(sg / count);
    b = static_cast<float>(sb / count);
    spread = highest - lowest;
    return true;
}
/// 三通道绝对差之和 —— 全文唯一的"漂移"定义。
float Drift(const float r, const float g, const float b,
    const float r0, const float g0, const float b0) noexcept {
    return std::fabs(r - r0) + std::fabs(g - g0) + std::fabs(b - b0);
}

// 锚点容差沿用本探针既有的 0.08；复位残余取更紧的 0.02，因为它是"同一视图读两次"
// 的自比，理应远小于任何几何位移。
constexpr float anchorTolerance = 0.08f;
constexpr float resetTolerance = 0.02f;
// 锚点小块的"局部平色"上限。实测：块心取到 0.0000，压边界取到 0.68~0.80，
// 中间没有样本，所以这个界只要小于 0.68 都能把两种情况分开，取 0.05 留足余量。
constexpr float flatPatchCap = 0.05f;

} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) { wprintf(L"用法: zoom_anchor_probe <媒体> [--anchor X,Y]\n"); return 2; }
    float anchorX = 0.75f, anchorY = 0.30f;
    for (int i = 2; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--anchor" && i + 1 < argc)
            (void)swscanf_s(argv[++i], L"%f,%f", &anchorX, &anchorY);
    }
    const auto path = Utf8(argv[1]);

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFZoomAnchorWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFZoomAnchorWnd", L"zoom", WS_OVERLAPPEDWINDOW,
        20, 20, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);

    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    configuration.outputWindow = window;
    configuration.decodeMode = FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
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
    FFF3FP_Play(player);
    Pump(1500);
    FFF3FP_Pause(player);
    Pump(600);

    RECT client{};
    GetClientRect(window, &client);
    const std::uint32_t width = static_cast<std::uint32_t>(client.right - client.left);
    const std::uint32_t height = static_cast<std::uint32_t>(client.bottom - client.top);
    const std::uint32_t patch = 12;
    const auto cx = static_cast<std::uint32_t>(anchorX * width) - patch / 2;
    const auto cy = static_cast<std::uint32_t>(anchorY * height) - patch / 2;
    printf("窗口 %ux%u  锚点 (%.2f, %.2f) => 像素 (%u, %u)\n\n", width, height, anchorX, anchorY, cx, cy);

    // 放大到 3x，再以光标为锚连续步进，观察锚点处内容
    FFF3FP_SetViewTransform(player, 3.0f, 0.0f, 0.0f);
    FFF3FP_Redraw(player);
    Pump(800);

    printf("%6s %10s %10s  %s\n", "步", "锚点R", "锚点G", "说明");
    float r0 = 0, g0 = 0, b0 = 0, spread0 = 0;
    if (!SamplePatch(player, cx, cy, patch, r0, g0, b0, spread0)) {
        printf("采样失败\n"); FFF3FP_Destroy(player); return 1;
    }
    printf("%6d %10.4f %10.4f  起始（小块亮度极差=%.4f）\n", 0, r0, g0, spread0);

    // 有效性前置：锚点小块必须**局部平色**，否则这个判据不成立。
    // 小块里若有一条边界，那么即便锚定完全正确，边界到锚点的屏幕距离也会随缩放
    // 成比例外移（12px 小块里的黑白比例改变），平均值必然漂移 —— 那会把正确的
    // 实现读成 FAIL。实测：同一支 DLL、同一张 3000px 大块夹具，锚点落在边界上时
    // 漂移 1.4758，落在块心时 0.0027 —— 差的只是锚点位置。
    // 因此这里判"未判定"而不是"FAIL"：假红和假绿一样不能进门禁。
    if (spread0 > flatPatchCap) {
        printf("\n**判据无牙**：锚点小块亮度极差 %.4f > %.2f，锚点压在边界上，"
            "缩放会改变小块内的黑白比例，本判据在该锚点下不成立。换一个落在大块内部的锚点。\n",
            spread0, flatPatchCap);
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    // ── 对照臂：证明这套量具真的能看见几何变化 ──────────────────────────────
    // 没有对照臂的判据会恒绿：平色区域、无平移余量、回读读的是陈旧帧，
    // 三种情况都会让"锚点漂移=0"变成测量假象而不是结论。
    float passR = 0, passG = 0, passB = 0, passSpread = 0;
    FFF3FP_SetViewTransform(player, 3.0f, 0.5f, 0.0f);
    FFF3FP_Redraw(player);
    Pump(800);
    float cr = 0, cg = 0, cb = 0, cSpread = 0;
    const bool controlOk = SamplePatch(player, cx, cy, patch, cr, cg, cb, cSpread);
    const float controlDrift = controlOk ? Drift(cr, cg, cb, r0, g0, b0) : 0.0f;
    printf("对照臂 平移 panX=+0.5 后该点变化=%.4f（必须 >%.4f 才算有牙）\n",
        controlDrift, anchorTolerance);
    if (!controlOk) {
        printf("\n**判据无牙**：对照臂采样失败，回读通道不通。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }
    if (controlDrift <= anchorTolerance) {
        printf("\n**判据无牙**：pan 0.5 都没能让该点改变，说明当前媒体/窗口下没有平移余量"
            "（整图已入窗）或取到的是平色区。本次不能判定锚定行为，改窗口尺寸或换带细节的媒体再跑。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    // 复位一致性：回到同一视图后读数必须回到起始值，否则这套量具有噪声。
    FFF3FP_SetViewTransform(player, 3.0f, 0.0f, 0.0f);
    FFF3FP_Redraw(player);
    Pump(800);
    const bool resetOk = SamplePatch(player, cx, cy, patch, passR, passG, passB, passSpread);
    const float resetDrift = resetOk ? Drift(passR, passG, passB, r0, g0, b0) : 1.0f;
    printf("复位一致性 回到同一视图后残余差=%.4f（须 <%.4f）\n", resetDrift, resetTolerance);
    if (!resetOk || resetDrift >= resetTolerance) {
        printf("\n**判据无牙**：同一视图两次读数不一致，测量本身不可信。\n");
        FFF3FP_Close(player); FFF3FP_Destroy(player);
        if (window != nullptr) DestroyWindow(window);
        return 4;
    }

    // ── 正判：光标锚定缩放 ──────────────────────────────────────────────────
    float worst = 0.0f;
    bool sampled = true;
    for (int step = 1; step <= 3; ++step) {
        float resulting = 0.0f;
        const auto result = FFF3FP_ZoomViewAt(player, 1.25f, anchorX, anchorY, &resulting);
        FFF3FP_Redraw(player);
        Pump(800);
        float r = 0, g = 0, b = 0, sp = 0;
        if (!SamplePatch(player, cx, cy, patch, r, g, b, sp)) {
            printf("%6d %10s\n", step, "失败");
            sampled = false;
            break;
        }
        const auto drift = Drift(r, g, b, r0, g0, b0);
        if (drift > worst) worst = drift;
        printf("%6d %10.4f %10.4f  result=%d zoom=%.2f 锚点漂移=%.4f %s\n",
            step, r, g, static_cast<int>(result), resulting, drift,
            drift < anchorTolerance ? "锚定良好" : "**锚点漂移明显**");
    }

    const bool verdict = sampled && worst < anchorTolerance;
    printf("\n最大锚点漂移 = %.4f（容差 %.4f）；对照臂 %.4f、复位残余 %.4f\n",
        worst, anchorTolerance, controlDrift, resetDrift);
    printf("结论：%s\n", verdict ? "PASS 光标锚定成立" : "FAIL 锚点漂移超容差");

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    if (!sampled) return 4;
    return verdict ? 0 : 3;
}
