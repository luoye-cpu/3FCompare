// ============================================================================
// AudioEndpointProbe.cpp —— P0 端点能力探针
// 规格：3fp/docs/138-音频Passthrough与DolbyMAT-Atmos激活实现方案（2026-11-07）.zh.md
//       §5 P0-①（端点 × IEC GUID 矩阵的 IsFormatSupported 实测）、§2.2（GUID 全值）、
//       §2.3（link rate/通道声明）、§2.4.2（能力探测不读系统列表，逐格式实测）。
//
// 做什么：
//   1. 枚举所有活动渲染端点（IMMDeviceEnumerator::EnumAudioEndpoints(eRender, ACTIVE)），
//      标记默认端点（eConsole / eCommunication），读 PKEY_Device_FriendlyName 显示名。
//   2. 每端点 × 每 SubFormat GUID × link 组合，做
//      IAudioClient::IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, ...) 实测：
//      - 默认发 cbSize=22 的普通 WAVEFORMATEXTENSIBLE（mpv/Kodi/LAV/MPC-BE 实战形态，§2.2）；
//      - 失败再试 cbSize=34 的 WAVEFORMATEXTENSIBLE_IEC61937（总 52B）；
//      - PCM 基线（s16/s32f）与 IEC60958 LPCM 一并探测作共享/独占基线参考。
//   3. 输出端点 GetMixFormat 摘要（共享基线参考）。
//   4. 控制台矩阵表 + JSON 落盘（默认 endpoint-capabilities.json，可命令行覆盖；
//      仓库惯例落 3fp/samples/passthrough/endpoint-capabilities.json）。
//
// 不做什么：只读探测——不调 Initialize、不占独占、不改系统状态。
//
// 构建：pwsh -File build.ps1（vswhere 定位 MSVC，cl 直编，无需 MSBuild 工程）。
// 用法：AudioEndpointProbe.exe [输出JSON路径]
//
// 纯 WASAPI/COM，零外部依赖（无 vcpkg / 无 FFmpeg 头）。
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <initguid.h>   // 先于 mmdeviceapi/audioclient/functiondiscoverykeys：本地实例化 IID/CLSID/PKEY，免引 uuid.lib
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mmreg.h>

#include <stdio.h>
#include <string>
#include <vector>
#include <fstream>

#pragma comment(lib, "ole32.lib")

// ---------------------------------------------------------------------------
// IEC 61937 SubFormat GUID（docs/138 §2.2 微软官方全值；后缀一律
// -0010-8000-00aa00389b71，Data3=0x0010，Data4 固定）。
// ---------------------------------------------------------------------------
static GUID MakeIec61937Guid(unsigned long data1, unsigned short data2) {
    GUID g;
    g.Data1 = data1;
    g.Data2 = data2;
    g.Data3 = 0x0010;
    g.Data4[0] = 0x80; g.Data4[1] = 0x00; g.Data4[2] = 0x00; g.Data4[3] = 0xaa;
    g.Data4[4] = 0x00; g.Data4[5] = 0x38; g.Data4[6] = 0x9b; g.Data4[7] = 0x71;
    return g;
}
static const GUID kSubIec60958Lpcm = MakeIec61937Guid(0x00000000u, 0x0000); // IEC60958 LPCM（非 PCM 之外的缺省）
static const GUID kSubPcm          = { 0x00000001u, 0x0000, 0x0010, { 0x80,0x00,0x00,0xaa,0x00,0x38,0x9b,0x71 } }; // KSDATAFORMAT_SUBTYPE_PCM（基线）
static const GUID kSubIeeeFloat    = { 0x00000003u, 0x0000, 0x0010, { 0x80,0x00,0x00,0xaa,0x00,0x38,0x9b,0x71 } }; // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT（基线）
static const GUID kSubAc3          = MakeIec61937Guid(0x00000092u, 0x0000); // AC-3 = WAVE_FORMAT_DOLBY_AC3_SPDIF
static const GUID kSubDts          = MakeIec61937Guid(0x00000008u, 0x0000); // DTS (0x0008)
static const GUID kSubDdp          = MakeIec61937Guid(0x0000000au, 0x0cea); // DD+ / E-AC-3
static const GUID kSubDdpAtmos     = MakeIec61937Guid(0x0000010au, 0x0cea); // DD+ Atmos (JOC) —— 方案点睛 GUID
static const GUID kSubDtsHd        = MakeIec61937Guid(0x0000000bu, 0x0cea); // DTS-HD Type IV
static const GUID kSubDtsXe1       = MakeIec61937Guid(0x0000010bu, 0x0cea); // DTS:X E1
static const GUID kSubDtsXe2       = MakeIec61937Guid(0x0000030bu, 0x0cea); // DTS:X E2
static const GUID kSubMlp          = MakeIec61937Guid(0x0000000cu, 0x0cea); // MAT 1.0 / MLP（TrueHD 打包）
static const GUID kSubMat20        = MakeIec61937Guid(0x0000010cu, 0x0cea); // MAT 2.0（MAT21 支持不隐含 MAT20，分别探测）
static const GUID kSubMat21        = MakeIec61937Guid(0x0000030cu, 0x0cea); // MAT 2.1

// ---------------------------------------------------------------------------
// 矩阵定义：格式 × link 组合（docs/138 §2.3 link rate/通道声明）：
//   AC-3/DTS core → ≤48k/2ch；DD+ → 192000/2ch（44.1 族 176400/2ch）；
//   TrueHD/MAT → 192k（176.4k）/8ch；DTS-HD → 192k，2ch(HRA)/8ch(MA)。
// iec34=true 的格式在 cbSize=22 被拒后追加 cbSize=34（WAVEFORMATEXTENSIBLE_IEC61937）实测，
// 附带 dwEncodedSamplesPerSec/dwEncodedChannelCount/dwAverageBytesPerSec 建议值。
// ---------------------------------------------------------------------------
struct Combo { DWORD rate; int ch; };
struct FmtSpec {
    const char* name;
    GUID        sub;
    int         nCombos;
    Combo       combos[2];
    bool        iec34;
    DWORD       encRate;         // dwEncodedSamplesPerSec
    DWORD       encCh;           // dwEncodedChannelCount
    DWORD       encBytesPerSec;  // dwAverageBytesPerSec
};

static const FmtSpec kFormats[] = {
    // name                              sub             n  combos                            iec34  encRate encCh encBps
    { "IEC60958 LPCM",                   kSubIec60958Lpcm, 2, { {48000,2},{192000,2} },         false, 0,0,0 },
    { "PCM 16bit (baseline)",            kSubPcm,          2, { {48000,2},{48000,8} },          false, 0,0,0 },
    { "FLOAT 32bit (baseline)",          kSubIeeeFloat,    1, { {48000,2} },                    false, 0,0,0 },
    { "AC-3 (0x0092)",                   kSubAc3,          2, { {44100,2},{48000,2} },          true,  48000, 6, 480000 },
    { "DTS (0x0008)",                    kSubDts,          2, { {44100,2},{48000,2} },          true,  48000, 6, 1536000 },
    { "DD+ / E-AC-3 (000a-0cea)",        kSubDdp,          2, { {176400,2},{192000,2} },        true,  48000, 6, 768000 },
    { "DD+ Atmos JOC (010a-0cea)",       kSubDdpAtmos,     2, { {176400,2},{192000,2} },        true,  48000, 6, 768000 },
    { "DTS-HD (000b-0cea)",              kSubDtsHd,        2, { {192000,2},{192000,8} },        true,  48000, 8, 3069000 },
    { "DTS:X E1 (010b-0cea)",            kSubDtsXe1,       2, { {192000,2},{192000,8} },        true,  48000, 8, 3069000 },
    { "DTS:X E2 (030b-0cea)",            kSubDtsXe2,       1, { {192000,8} },                   true,  48000, 8, 3069000 },
    { "MAT 1.0 / MLP (000c-0cea)",       kSubMlp,          2, { {176400,8},{192000,8} },        true,  48000, 8, 2250000 },
    { "MAT 2.0 (010c-0cea)",             kSubMat20,        2, { {192000,2},{192000,8} },        true,  48000, 8, 2250000 },
    { "MAT 2.1 (030c-0cea)",             kSubMat21,        2, { {192000,2},{192000,8} },        true,  48000, 8, 2250000 },
};

// WAVEFORMATEXTENSIBLE_IEC61937（ksmedia.h 有定义；本地重声明避免 SDK 依赖差异）。
// 总 52B = WAVEFORMATEXTENSIBLE(40) + 3×DWORD。
struct WAVEFORMATEXTENSIBLE_IEC61937_L {
    WAVEFORMATEXTENSIBLE FormatExtensible;
    DWORD dwEncodedSamplesPerSec;
    DWORD dwEncodedChannelCount;
    DWORD dwAverageBytesPerSec;
};

// ---------------------------------------------------------------------------
// 报告结构
// ---------------------------------------------------------------------------
struct Attempt {
    std::string fmtName;
    GUID        sub;
    DWORD       rate;
    int         ch;
    DWORD       mask;
    int         cbSize;      // 22 或 34
    HRESULT     hr;
    bool        accepted;    // hr == S_OK
};

struct EndpointReport {
    std::wstring id;
    std::wstring name;
    bool   isDefaultConsole = false;
    bool   isDefaultCommunication = false;
    bool   activateFailed = false;
    HRESULT activateHr = 0;
    bool   hasMix = false;
    std::string mixDesc;              // 人类可读摘要
    long   mixRate = 0; int mixCh = 0; int mixBits = 0; std::string mixSub;
    bool   exclusiveBlockedNote = false; // 全部请求都是 EXCLUSIVE_MODE_NOT_ALLOWED（§2.4.2 整机出局）
    std::vector<Attempt> attempts;
};

// ---------------------------------------------------------------------------
// 小工具
// ---------------------------------------------------------------------------
static std::string Utf8(const std::wstring& w) {
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

static std::string GuidStr(const GUID& g) {
    char b[64];
    sprintf_s(b, "%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        (unsigned long)g.Data1, (unsigned)g.Data2, (unsigned)g.Data3,
        g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3],
        g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    return b;
}

static bool HrName(HRESULT hr, std::string* out) {
    switch (hr) {
    case S_OK:                                        *out = "S_OK"; return true;
    case S_FALSE:                                     *out = "S_FALSE"; return true;
    case AUDCLNT_E_UNSUPPORTED_FORMAT:                *out = "AUDCLNT_E_UNSUPPORTED_FORMAT"; return true;
    case AUDCLNT_E_DEVICE_IN_USE:                     *out = "AUDCLNT_E_DEVICE_IN_USE"; return true;
    case AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED:        *out = "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"; return true;
    case AUDCLNT_E_ENDPOINT_CREATE_FAILED:            *out = "AUDCLNT_E_ENDPOINT_CREATE_FAILED"; return true;
    case AUDCLNT_E_SERVICE_NOT_RUNNING:               *out = "AUDCLNT_E_SERVICE_NOT_RUNNING"; return true;
    case AUDCLNT_E_DEVICE_INVALIDATED:                *out = "AUDCLNT_E_DEVICE_INVALIDATED"; return true;
    case E_INVALIDARG:                                *out = "E_INVALIDARG"; return true;
    case E_POINTER:                                   *out = "E_POINTER"; return true;
    case E_OUTOFMEMORY:                               *out = "E_OUTOFMEMORY"; return true;
    default: break;
    }
    char b[32]; sprintf_s(b, "0x%08lX", (unsigned long)hr);
    *out = b;
    return false;
}

static std::string HrStr(HRESULT hr) {
    std::string s;
    HrName(hr, &s);
    return s;
}

static std::string EscapeJson(const std::string& in) {
    std::string o;
    o.reserve(in.size() + 8);
    for (unsigned char c : in) {
        switch (c) {
        case '"':  o += "\\\""; break;
        case '\\': o += "\\\\"; break;
        case '\b': o += "\\b";  break;
        case '\f': o += "\\f";  break;
        case '\n': o += "\\n";  break;
        case '\r': o += "\\r";  break;
        case '\t': o += "\\t";  break;
        default:
            if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); o += b; }
            else o += (char)c;
        }
    }
    return o;
}

static DWORD MaskForChannels(int ch) {
    switch (ch) {
    case 2: return 0x3;    // KSAUDIO_SPEAKER_STEREO
    case 6: return 0x3F;   // KSAUDIO_SPEAKER_5POINT1_SURROUND
    case 8: return 0x63F;  // KSAUDIO_SPEAKER_7POINT1_SURROUND
    default: {
        DWORD m = 0;
        for (int i = 0; i < ch && i < 18; ++i) m |= (1u << i);
        return m;
    }
    }
}

static void FillExtensible(WAVEFORMATEXTENSIBLE* w, DWORD rate, int ch, int bits, bool isFloat, const GUID& sub) {
    ZeroMemory(w, sizeof(*w));
    w->Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    w->Format.nChannels = (WORD)ch;
    w->Format.nSamplesPerSec = rate;
    w->Format.wBitsPerSample = (WORD)bits;
    w->Format.nBlockAlign = (WORD)(ch * bits / 8);
    w->Format.nAvgBytesPerSec = rate * (DWORD)w->Format.nBlockAlign;
    w->Format.cbSize = 22;
    w->Samples.wValidBitsPerSample = (WORD)bits;
    w->dwChannelMask = MaskForChannels(ch);
    w->SubFormat = sub;
    (void)isFloat;
}

static std::string NowIsoLocal() {
    SYSTEMTIME t; GetLocalTime(&t);
    TIME_ZONE_INFORMATION tz; DWORD r = GetTimeZoneInformation(&tz);
    long biasMin = (r == TIME_ZONE_ID_STANDARD) ? tz.Bias + tz.StandardBias : tz.Bias + tz.DaylightBias;
    char sign = biasMin <= 0 ? '+' : '-';
    long b = biasMin < 0 ? -biasMin : biasMin;
    char s[48];
    sprintf_s(s, "%04u-%02u-%02uT%02u:%02u:%02u%c%02ld:%02ld",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, sign, b / 60, b % 60);
    return s;
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------
int main(int argc, char** argv) {
    SetConsoleOutputCP(65001); // 控制台按 UTF-8 输出
    const char* outPath = (argc > 1) ? argv[1] : "endpoint-capabilities.json";

    printf("AudioEndpointProbe —— P0 端点能力矩阵\n");
    printf("  IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE) 实测；cbSize=22 优先，失败再试 34（docs/138 §2.2/§2.3/§2.4.2）\n");
    printf("  只读探测：不 Initialize、不占设备。\n\n");

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) { printf("CoInitializeEx 失败: 0x%08lX\n", (unsigned long)hr); return 1; }

    IMMDeviceEnumerator* enumr = nullptr;
    // __uuidof：SDK 头用 DECLSPEC_UUID 声明接口，编译期取 GUID，无需 uuid.lib
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&enumr);
    if (FAILED(hr) || !enumr) {
        printf("CoCreateInstance(MMDeviceEnumerator) 失败: %s\n", HrStr(hr).c_str());
        CoUninitialize();
        return 1;
    }

    // 默认端点（eConsole / eCommunication）ID，用于矩阵标记
    std::wstring defConsoleId, defCommId;
    IMMDevice* d = nullptr;
    if (SUCCEEDED(enumr->GetDefaultAudioEndpoint(eRender, eConsole, &d)) && d) {
        LPWSTR id = nullptr;
        if (SUCCEEDED(d->GetId(&id)) && id) { defConsoleId = id; CoTaskMemFree(id); }
        d->Release();
    }
    d = nullptr;
    if (SUCCEEDED(enumr->GetDefaultAudioEndpoint(eRender, eCommunications, &d)) && d) {
        LPWSTR id = nullptr;
        if (SUCCEEDED(d->GetId(&id)) && id) { defCommId = id; CoTaskMemFree(id); }
        d->Release();
    }

    IMMDeviceCollection* coll = nullptr;
    hr = enumr->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &coll);
    if (FAILED(hr) || !coll) {
        printf("EnumAudioEndpoints 失败: %s\n", HrStr(hr).c_str());
        enumr->Release(); CoUninitialize();
        return 1;
    }
    UINT n = 0;
    coll->GetCount(&n);
    printf("活动渲染端点数: %u\n\n", n);

    std::vector<EndpointReport> reports;
    int totalProbes = 0, totalAccepted = 0;

    for (UINT i = 0; i < n; ++i) {
        IMMDevice* dev = nullptr;
        if (FAILED(coll->Item(i, &dev)) || !dev) continue;

        EndpointReport ep;
        LPWSTR idW = nullptr;
        if (SUCCEEDED(dev->GetId(&idW)) && idW) { ep.id = idW; CoTaskMemFree(idW); }
        ep.isDefaultConsole = _wcsicmp(ep.id.c_str(), defConsoleId.c_str()) == 0;
        ep.isDefaultCommunication = _wcsicmp(ep.id.c_str(), defCommId.c_str()) == 0;

        IPropertyStore* ps = nullptr;
        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps)) && ps) {
            PROPVARIANT v; PropVariantInit(&v);
            if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR && v.pwszVal)
                ep.name = v.pwszVal;
            PropVariantClear(&v);
            ps->Release();
        }
        if (ep.name.empty()) ep.name = L"(未知名)";

        // 控制台表头
        printf("=== 端点 [%u/%u]%s%s ===\n", i + 1, n,
            ep.isDefaultConsole ? " [默认·控制台]" : "",
            ep.isDefaultCommunication ? " [默认·通信]" : "");
        printf("  名称: %s\n", Utf8(ep.name).c_str());
        printf("  ID:   %s\n", Utf8(ep.id).c_str());

        IAudioClient* ac = nullptr;
        hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&ac);
        if (FAILED(hr) || !ac) {
            ep.activateFailed = true;
            ep.activateHr = hr;
            printf("  Activate(IAudioClient) 失败: %s —— 跳过矩阵\n\n", HrStr(hr).c_str());
            reports.push_back(ep);
            dev->Release();
            continue;
        }

        // GetMixFormat 摘要（共享基线参考）
        WAVEFORMATEX* mix = nullptr;
        if (SUCCEEDED(ac->GetMixFormat(&mix)) && mix) {
            ep.hasMix = true;
            ep.mixRate = (long)mix->nSamplesPerSec;
            ep.mixCh = mix->nChannels;
            ep.mixBits = mix->wBitsPerSample;
            if (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mix->cbSize >= 22) {
                const WAVEFORMATEXTENSIBLE* mx = (const WAVEFORMATEXTENSIBLE*)mix;
                ep.mixSub = GuidStr(mx->SubFormat);
            } else {
                char b[32]; sprintf_s(b, "tag=0x%04X", mix->wFormatTag); ep.mixSub = b;
            }
            char b[160];
            sprintf_s(b, "%ld Hz / %d ch / %d bit / sub=%s", ep.mixRate, ep.mixCh, ep.mixBits, ep.mixSub.c_str());
            ep.mixDesc = b;
            CoTaskMemFree(mix);
            printf("  MixFormat(共享基线): %s\n", ep.mixDesc.c_str());
        }

        printf("  %-28s %-13s %-36s %-36s\n", "格式", "link", "cbSize=22", "cbSize=34");
        bool allNotAllowed = true;
        for (const FmtSpec& f : kFormats) {
            for (int c = 0; c < f.nCombos; ++c) {
                DWORD rate = f.combos[c].rate;
                int ch = f.combos[c].ch;
                DWORD mask = MaskForChannels(ch);
                int bits = 16;
                GUID sub = f.sub;
                if (f.sub == kSubIeeeFloat) bits = 32;

                Attempt a;
                a.fmtName = f.name; a.sub = f.sub; a.rate = rate; a.ch = ch; a.mask = mask; a.cbSize = 22;

                WAVEFORMATEXTENSIBLE w22;
                FillExtensible(&w22, rate, ch, bits, f.sub == kSubIeeeFloat, sub);
                a.hr = ac->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &w22.Format, nullptr);
                a.accepted = (a.hr == S_OK);
                ep.attempts.push_back(a);
                totalProbes++; if (a.accepted) totalAccepted++;
                allNotAllowed = allNotAllowed && (a.hr == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED);

                Attempt a34 = a;
                a34.cbSize = 34;
                bool tried34 = false;
                if (f.iec34 && a.hr != S_OK) {
                    WAVEFORMATEXTENSIBLE_IEC61937_L w34;
                    FillExtensible(&w34.FormatExtensible, rate, ch, bits, false, sub);
                    w34.FormatExtensible.Format.cbSize = 34;
                    w34.dwEncodedSamplesPerSec = f.encRate;
                    w34.dwEncodedChannelCount = f.encCh;
                    w34.dwAverageBytesPerSec = f.encBytesPerSec;
                    a34.hr = ac->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &w34.FormatExtensible.Format, nullptr);
                    a34.accepted = (a34.hr == S_OK);
                    tried34 = true;
                    ep.attempts.push_back(a34);
                    totalProbes++; if (a34.accepted) totalAccepted++;
                }

                std::string h22 = HrStr(a.hr); if (a.accepted) h22 += " *";
                std::string h34 = tried34 ? HrStr(a34.hr) : "-";
                if (tried34 && a34.accepted) h34 += " *";
                char link[32]; sprintf_s(link, "%lu/%dch", (unsigned long)rate, ch);
                printf("  %-28s %-13s %-36s %-36s\n", f.name, link, h22.c_str(), h34.c_str());
            }
        }
        printf("  （* = 被端点接受；独占探测共 %d 组合）\n\n", (int)ep.attempts.size());
        if (allNotAllowed && !ep.attempts.empty()) {
            printf("  注：该端点对全部 IEC/PCM 独占请求返回 AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED —— 按 §2.4.2 整个设备出局。\n\n");
        }
        ep.exclusiveBlockedNote = allNotAllowed && !ep.attempts.empty();

        ac->Release();
        reports.push_back(ep);
        dev->Release();
    }

    // ---- 控制台汇总 ----
    printf("==== 汇总 ====\n");
    printf("端点: %u，探测组合: %d，接受: %d\n", n, totalProbes, totalAccepted);
    for (const EndpointReport& ep : reports) {
        std::string acc;
        for (const Attempt& a : ep.attempts) {
            if (!a.accepted) continue;
            if (acc.size()) acc += ", ";
            char b[96];
            sprintf_s(b, "%s@%lu/%dch(cb%d)", a.fmtName.c_str(), (unsigned long)a.rate, a.ch, a.cbSize);
            acc += b;
        }
        printf("  %s%s: %s\n", Utf8(ep.name).c_str(),
            ep.isDefaultConsole ? "[默认]" : "", acc.empty() ? "(无接受项)" : acc.c_str());
    }
    printf("\nJSON 落盘: %s\n", outPath);

    // ---- JSON ----
    std::string j;
    j += "{\n";
    j += "  \"schema\": 1,\n";
    j += "  \"tool\": \"AudioEndpointProbe\",\n";
    j += "  \"spec\": \"3fp/docs/138 #5 P0-1 / #2.2 / #2.3\",\n";
    j += "  \"generatedAt\": \"" + NowIsoLocal() + "\",\n";
    j += "  \"shareMode\": \"EXCLUSIVE\",\n";
    j += "  \"method\": \"IAudioClient::IsFormatSupported; cbSize=22 first, retry cbSize=34 (WAVEFORMATEXTENSIBLE_IEC61937) when rejected\",\n";
    j += "  \"endpoints\": [\n";
    for (size_t e = 0; e < reports.size(); ++e) {
        const EndpointReport& ep = reports[e];
        j += "    {\n";
        j += "      \"id\": \"" + EscapeJson(Utf8(ep.id)) + "\",\n";
        j += "      \"name\": \"" + EscapeJson(Utf8(ep.name)) + "\",\n";
        j += std::string("      \"isDefaultConsole\": ") + (ep.isDefaultConsole ? "true" : "false") + ",\n";
        j += std::string("      \"isDefaultCommunication\": ") + (ep.isDefaultCommunication ? "true" : "false") + ",\n";
        if (ep.activateFailed) {
            std::string hn; bool known = HrName(ep.activateHr, &hn);
            j += "      \"activateFailed\": true,\n";
            j += "      \"activateHr\": \"" + hn + (known ? "" : " (" + HrStr(ep.activateHr) + ")") + "\",\n";
        } else {
            j += "      \"activateFailed\": false,\n";
        }
        j += std::string("      \"exclusiveModeBlocked\": ") + (ep.exclusiveBlockedNote ? "true" : "false") + ",\n";
        if (ep.hasMix) {
            j += "      \"mixFormat\": { \"rate\": " + std::to_string(ep.mixRate) +
                 ", \"channels\": " + std::to_string(ep.mixCh) +
                 ", \"bits\": " + std::to_string(ep.mixBits) +
                 ", \"subFormat\": \"" + ep.mixSub + "\" },\n";
        } else {
            j += "      \"mixFormat\": null,\n";
        }
        j += "      \"results\": [\n";
        for (size_t r = 0; r < ep.attempts.size(); ++r) {
            const Attempt& a = ep.attempts[r];
            std::string hn; bool known = HrName(a.hr, &hn);
            char maskBuf[16]; sprintf_s(maskBuf, "0x%04lX", (unsigned long)a.mask);
            j += "        {\n";
            j += "          \"format\": \"" + EscapeJson(a.fmtName) + "\",\n";
            j += "          \"subFormat\": \"" + GuidStr(a.sub) + "\",\n";
            j += "          \"linkRate\": " + std::to_string((unsigned long long)a.rate) + ",\n";
            j += "          \"linkChannels\": " + std::to_string(a.ch) + ",\n";
            j += "          \"channelMask\": \"" + std::string(maskBuf) + "\",\n";
            j += "          \"cbSize\": " + std::to_string(a.cbSize) + ",\n";
            j += "          \"hr\": \"" + EscapeJson(hn) + (known ? "" : " (" + HrStr(a.hr) + ")") + "\",\n";
            j += "          \"hrCode\": " + std::to_string((long long)(int64_t)(int32_t)a.hr) + ",\n";
            j += std::string("          \"accepted\": ") + (a.accepted ? "true" : "false") + "\n";
            j += "        }";
            j += (r + 1 < ep.attempts.size()) ? ",\n" : "\n";
        }
        j += "      ]\n";
        j += "    }";
        j += (e + 1 < reports.size()) ? ",\n" : "\n";
    }
    j += "  ],\n";
    j += "  \"summary\": { \"endpoints\": " + std::to_string((unsigned long long)reports.size()) +
         ", \"probes\": " + std::to_string(totalProbes) +
         ", \"accepted\": " + std::to_string(totalAccepted) + " }\n";
    j += "}\n";

    {
        std::ofstream f(outPath, std::ios::binary | std::ios::trunc);
        if (!f) {
            printf("错误：无法写 JSON 文件 %s\n", outPath);
        } else {
            f.write(j.data(), (std::streamsize)j.size());
            f.close();
            printf("已写入 %zu 字节\n", j.size());
        }
    }

    coll->Release();
    enumr->Release();
    CoUninitialize();
    return 0;
}
