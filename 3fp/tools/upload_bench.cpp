// 上传/渲染路径基准：用 headless（outputWindow = nullptr）隔离被测代码，
// 避免 vsync 掩盖改动效果。
//
// 为什么必须 headless：正常播放时 Present 等待是 vsync 主导的（实测组内波动
// 2.1~5.9 ms，大于组间差异），无法用来裁决小的调度/上传改动。传 nullptr 窗口后，
// 渲染器仍走完 **上传 + EnsurePipeline + DrawWithShader**，但跳过 Present
// （见 VideoRenderer::Render 的 `if (window_ == nullptr)` 早退）——正好隔离出
// PR #15 与本次优化所触及的那段代码。
//
// ⚠ 为什么每个样本要用**独立进程**：同一进程内反复 Create/Destroy D3D11 解码器
// 会从第 2 轮起全部失败（实测）。而独立进程极稳（8K 硬解 57.0 / 57.0 / 57.5 fps）。
// 因此 runs>1 时自调用，每个样本一个子进程，父进程汇总取中位数。
//
// 用法：
//   upload_bench <媒体> --decode cpu|gpu --seconds N --runs R     （父模式，汇总）
//   upload_bench <媒体> --decode cpu|gpu --seconds N --one       （子模式，单样本）

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "3FP/Api/FFF.Player.Api.h"

namespace {
bool g_openCompleted = false;
void EventCallback(void*, const FFF3FPEvent eventType, const char*) {
    if (eventType == FFF3FPEvent::OpenCompleted) g_openCompleted = true;
}
void Pump(const int ms) {
    const auto end = GetTickCount64() + static_cast<DWORD>(ms);
    while (GetTickCount64() < end) {
        MSG m{};
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        Sleep(1);
    }
}

struct Sample {
    double frames = 0;
    double softwareConvertMs = 0;
    double uploadMs = 0;
    double deviceLockMs = 0;
    double hardwareTransferMs = 0;
    double presentMs = 0;
    double dropped = 0;
    bool ok = false;
};

Sample RunOnce(const std::string& path, const bool gpu, const int seconds) {
    Sample out;
    FFF3FPConfiguration configuration{};
    configuration.size = sizeof(configuration);
    configuration.version = FFF3FP_GetApiVersion();
    // ★ 关键：不传窗口 -> headless，隔离上传/渲染路径，不受 vsync 影响
    configuration.outputWindow = nullptr;
    configuration.decodeMode = gpu ? FFF3FPDecodeMode::D3D11 : FFF3FPDecodeMode::Cpu;
    configuration.colorMode = FFF3FPColorMode::MapToSdr;
    configuration.sdrPeakNits = 100.0f;
    configuration.sdrPaperWhiteNits = 203.0f;
    configuration.eventCallback = EventCallback;
    configuration.preferredAdapterIndex = -1;

    FFF3FPHandle player = nullptr;
    if (FFF3FP_Create(&configuration, &player) != FFFResult::Success || player == nullptr) return out;
    if (FFF3FP_Open(player, path.c_str()) != FFFResult::Success) {
        FFF3FP_Destroy(player);
        return out;
    }
    for (int i = 0; i < 300 && !g_openCompleted; ++i) Pump(50);

    FFF3FPSnapshot before{};
    before.size = sizeof(before);
    before.version = 20;
    FFF3FP_GetSnapshot(player, &before);

    FFF3FP_Play(player);
    Pump(seconds * 1000);

    FFF3FPSnapshot after{};
    after.size = sizeof(after);
    after.version = 20;
    FFF3FP_GetSnapshot(player, &after);

    out.frames = static_cast<double>(after.presentedVideoFrames - before.presentedVideoFrames);
    out.softwareConvertMs = static_cast<double>(after.softwareConvert100ns - before.softwareConvert100ns) / 10000.0;
    out.uploadMs = static_cast<double>(after.videoUpload100ns - before.videoUpload100ns) / 10000.0;
    out.deviceLockMs = static_cast<double>(after.deviceLockWait100ns - before.deviceLockWait100ns) / 10000.0;
    out.hardwareTransferMs = static_cast<double>(after.hardwareTransfer100ns - before.hardwareTransfer100ns) / 10000.0;
    out.presentMs = static_cast<double>(after.presentWait100ns - before.presentWait100ns) / 10000.0;
    out.dropped = static_cast<double>(after.droppedVideoFrames - before.droppedVideoFrames);
    out.ok = out.frames > 0;

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    return out;
}

double Median(std::vector<double> values) {
    if (values.empty()) return 0;
    std::sort(values.begin(), values.end());
    const auto mid = values.size() / 2;
    return values.size() % 2 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
}

std::string Utf8(const wchar_t* wide) {
    if (wide == nullptr || *wide == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
    if (size > 1) WideCharToMultiByte(CP_UTF8, 0, wide, -1, out.data(), size, nullptr, nullptr);
    return out;
}

int RunOne(const std::string& path, const bool gpu, const int seconds) {
    const auto s = RunOnce(path, gpu, seconds);
    if (!s.ok) { printf("SAMPLE fail\n"); return 1; }
    printf("SAMPLE %.4f %.4f %.4f %.4f %.4f %.4f %.4f\n",
        s.frames, s.softwareConvertMs, s.deviceLockMs, s.hardwareTransferMs,
        s.presentMs, s.dropped, s.uploadMs);
    return 0;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) {
        wprintf(L"用法: upload_bench <媒体> [--decode cpu|gpu] [--seconds N] [--runs R] [--one]\n");
        return 2;
    }
    bool gpu = false;
    bool oneShot = false;
    int seconds = 3;
    int runs = 5;
    for (int i = 2; i < argc; ++i) {
        const std::wstring key = argv[i];
        if (key == L"--decode" && i + 1 < argc) gpu = std::wstring(argv[++i]) == L"gpu";
        else if (key == L"--seconds" && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (key == L"--runs" && i + 1 < argc) runs = _wtoi(argv[++i]);
        else if (key == L"--one") oneShot = true;
    }
    if (seconds < 1) seconds = 3;
    if (runs < 1) runs = 1;

    const std::string path = Utf8(argv[1]);
    if (oneShot || runs == 1) return RunOne(path, gpu, seconds);

    std::vector<double> fps, convert, lock, transfer, present, dropped, upload;
    const std::wstring self = argv[0];
    const std::wstring widePath(argv[1]);
    for (int i = 0; i < runs; ++i) {
        std::wstring command = L"\"" + self + L"\" \"" + widePath +
            L"\" --seconds " + std::to_wstring(seconds) + L" --one";
        if (gpu) command += L" --decode gpu";

        SECURITY_ATTRIBUTES attributes{};
        attributes.nLength = sizeof(attributes);
        attributes.bInheritHandle = TRUE;
        HANDLE readEnd = nullptr, writeEnd = nullptr;
        if (!CreatePipe(&readEnd, &writeEnd, &attributes, 0)) continue;
        SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);

        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdOutput = writeEnd;
        startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        PROCESS_INFORMATION process{};
        std::vector<wchar_t> mutableCommand(command.begin(), command.end());
        mutableCommand.push_back(L'\0');
        const auto started = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr,
            TRUE, 0, nullptr, nullptr, &startup, &process);
        CloseHandle(writeEnd);
        std::string output;
        if (started) {
            char buffer[512]{};
            DWORD read = 0;
            while (ReadFile(readEnd, buffer, sizeof(buffer) - 1, &read, nullptr) && read > 0) {
                buffer[read] = '\0';
                output += buffer;
            }
            WaitForSingleObject(process.hProcess, 120000);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        CloseHandle(readEnd);

        const auto at = output.find("SAMPLE ");
        if (at == std::string::npos) { printf("  样本 %d 失败\n", i + 1); continue; }
        double f = 0, c = 0, l = 0, t = 0, p = 0, d = 0, u = 0;
        if (sscanf_s(output.c_str() + at, "SAMPLE %lf %lf %lf %lf %lf %lf %lf", &f, &c, &l, &t, &p, &d, &u) != 7) {
            printf("  样本 %d 解析失败\n", i + 1);
            continue;
        }
        fps.push_back(f / seconds);
        convert.push_back(c);
        lock.push_back(l);
        transfer.push_back(t);
        present.push_back(p);
        dropped.push_back(d);
    }
    if (fps.empty()) { printf("全部样本失败\n"); return 1; }

    printf("media=%s decode=%s seconds=%d samples=%zu\n",
        path.c_str(), gpu ? "gpu" : "cpu", seconds, fps.size());
    printf("FPS_median=%.2f  FPS_min=%.2f  FPS_max=%.2f\n",
        Median(fps), *std::min_element(fps.begin(), fps.end()),
        *std::max_element(fps.begin(), fps.end()));
    printf("sws_ms_median=%.1f  upload_ms_median=%.1f  lock_ms_median=%.1f  hwtransfer_ms_median=%.1f  present_ms_median=%.1f  dropped_median=%.1f\n",
        Median(convert), Median(upload), Median(lock), Median(transfer), Median(present), Median(dropped));
    return 0;
}
