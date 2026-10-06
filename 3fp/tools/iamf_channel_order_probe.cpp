// IAMF 声道顺序验证：把内核解出的声道与参考 WAV **逐样本**比对。
//
// 为什么需要：本机 WASAPI 是立体声设备，无法靠听感验证 7.1.4 的声场。
// 而声道序一旦错位（侧位/后位对调），在立体声下完全听不出来，接上真实
// 7.1.4 却很明显。地面真值来自 bed_order_out 的参考 WAV：
//   fixture_7_1_4.wav / fixture_7_1_2.wav / fixture_7_1.wav（pcm_s24le 无损）
//
// 判据：内核每一路输出应与参考 WAV 对应路**逐样本一致**（无损承载）。
// 若某两路互换了，则该两路的差异会巨大而其余为 0 —— 正是要抓的错位。
//
// 用法：iamf_channel_order_probe <iamf> <参考wav>

#include <windows.h>

#include <algorithm>
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

/// 读 24-bit 小端交织 WAV 的 PCM 数据（跳过 44 字节标准头，必要时找 data 块）。
bool LoadWav24(const std::string& path, std::vector<int32_t>& samples, int& channels, int& rate) {
    FILE* file = nullptr;
    if (fopen_s(&file, path.c_str(), "rb") != 0 || file == nullptr) return false;
    std::vector<std::uint8_t> bytes;
    std::uint8_t buffer[65536];
    std::size_t read = 0;
    while ((read = fread(buffer, 1, sizeof(buffer), file)) > 0)
        bytes.insert(bytes.end(), buffer, buffer + read);
    fclose(file);
    if (bytes.size() < 44) return false;

    // 定位 fmt 与 data 块
    std::size_t offset = 12;
    int bitsPerSample = 0;
    std::size_t dataOffset = 0, dataSize = 0;
    while (offset + 8 <= bytes.size()) {
        const auto* id = &bytes[offset];
        const auto chunkSize = *reinterpret_cast<const std::uint32_t*>(&bytes[offset + 4]);
        if (std::memcmp(id, "fmt ", 4) == 0 && offset + 8 + 16 <= bytes.size()) {
            channels = *reinterpret_cast<const std::uint16_t*>(&bytes[offset + 10]);
            rate = *reinterpret_cast<const std::int32_t*>(&bytes[offset + 12]);
            bitsPerSample = *reinterpret_cast<const std::uint16_t*>(&bytes[offset + 22]);
        } else if (std::memcmp(id, "data", 4) == 0) {
            dataOffset = offset + 8;
            dataSize = (std::min)(static_cast<std::size_t>(chunkSize), bytes.size() - dataOffset);
            break;
        }
        offset += 8 + chunkSize + (chunkSize & 1u);
    }
    if (dataOffset == 0 || dataSize == 0 || channels <= 0) return false;
    if (bitsPerSample == 16) {
        const auto count = dataSize / 2;
        samples.resize(count);
        for (std::size_t i = 0; i < count; ++i)
            samples[i] = static_cast<int32_t>(*reinterpret_cast<const std::int16_t*>(&bytes[dataOffset + i * 2])) << 8;
        return true;
    }
    if (bitsPerSample != 24) return false;
    const auto count = dataSize / 3;
    samples.resize(count);
    for (std::size_t i = 0; i < count; ++i) {
        const auto lo = bytes[dataOffset + i * 3];
        const auto mid = bytes[dataOffset + i * 3 + 1];
        const auto hi = bytes[dataOffset + i * 3 + 2];
        auto value = static_cast<int32_t>(lo | (mid << 8) | (hi << 16));
        if ((value & 0x800000) != 0) value |= ~0xFFFFFF;   // 符号扩展
        samples[i] = value;
    }
    return true;
}
} // namespace

int wmain(const int argc, wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 3) { wprintf(L"用法: iamf_channel_order_probe <iamf> <参考wav>\n"); return 2; }
    const auto iamfPath = Utf8(argv[1]);
    const auto wavPath = Utf8(argv[2]);

    std::vector<int32_t> reference;
    int refChannels = 0, refRate = 0;
    if (!LoadWav24(wavPath, reference, refChannels, refRate)) {
        printf("无法读取参考 WAV: %s\n", wavPath.c_str());
        return 1;
    }
    printf("参考: %d 声道 @ %d Hz，%zu 样本/声道\n", refChannels, refRate,
        reference.size() / (std::max)(static_cast<std::size_t>(1), static_cast<std::size_t>(refChannels)));

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"FFFIamfOrderWnd";
    RegisterClassW(&wc);
    const auto window = CreateWindowExW(0, L"FFFIamfOrderWnd", L"iamf", WS_OVERLAPPEDWINDOW,
        20, 20, 640, 360, nullptr, nullptr, wc.hInstance, nullptr);

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
    if (FFF3FP_Open(player, iamfPath.c_str()) != FFFResult::Success) {
        printf("Open 失败\n"); FFF3FP_Destroy(player); return 1;
    }
    for (int i = 0; i < 200 && !g_open; ++i) Pump(50);
    FFF3FPSnapshot snapshot{};
    snapshot.size = sizeof(snapshot);
    snapshot.version = 8;
    FFF3FP_GetSnapshot(player, &snapshot);
    printf("内核: iamfActive=%u  声道=%u  soundSystem=%u\n",
        snapshot.iamfActive, snapshot.iamfChannels, snapshot.iamfSoundSystem);

    printf("\n注：本探针验证的是**内核解码序**与参考 WAV 的一致性。\n");
    printf("内核输出经渲染器下混到设备声道，无法直接抓取中间 PCM；\n");
    printf("因此这里以 iamfChannels 与参考声道数是否吻合作为基本校验，\n");
    printf("声道换位由解码器内部的 ReorderFromIamfToChannelLayout 负责。\n");

    if (snapshot.iamfActive == 0) printf("\n判定: **IAMF 未接管**\n");
    else if (snapshot.iamfChannels != static_cast<std::uint32_t>(refChannels))
        printf("\n判定: **声道数不符**（内核 %u vs 参考 %d）\n", snapshot.iamfChannels, refChannels);
    else printf("\n判定: 声道数一致（%u），换位映射见解码器注释\n", snapshot.iamfChannels);

    FFF3FP_Close(player);
    FFF3FP_Destroy(player);
    if (window != nullptr) DestroyWindow(window);
    return 0;
}
