# AudioEndpointProbe —— P0 端点能力探针

规格出处：`3fp/docs/138-音频Passthrough与DolbyMAT-Atmos激活实现方案（2026-11-07）.zh.md`
§5 P0-①（端点 × IEC GUID 矩阵的 IsFormatSupported 实测）、§2.2（SubFormat GUID 全值）、
§2.3（link rate/通道声明）、§2.4.2（能力探测方法论）。

## 用途

回答一个问题：**本机的每个渲染端点，分别接受哪些 IEC 61937 bitstream 格式？**
这是 passthrough 仲裁（ATMOS→DDP 降级等）与回退链的实测依据，也是 P0 验收读数之一。

只读探测：不调 `Initialize`、不占独占、不改系统状态，随时可跑。

## 构建

pwsh（自解析工具链：vswhere 定位 MSVC → vcvars64 → cl 直编；无需 MSBuild 工程、无需 vcpkg/FFmpeg 头）：

```powershell
pwsh -File 3fp\tools\AudioEndpointProbe\build.ps1          # 仅编译
pwsh -File 3fp\tools\AudioEndpointProbe\build.ps1 -Run     # 编译并直接跑（JSON 落 3fp\samples\passthrough\）
```

纯 WASAPI/COM（`ole32.lib`），源码 `AudioEndpointProbe.cpp` 单文件。
编译细节：`/utf-8`；接口 GUID 用 `__uuidof` 取（新 SDK 头是 `DECLSPEC_UUID` 声明，非 `DEFINE_GUID`）；
`initguid.h` 前置实例化 `PKEY_Device_FriendlyName`；cl 的 `/Fo` 参数**不能以 `\` 结尾**
（尾反斜杠转义引号会吞掉后续源文件参数 → D8003，此坑已写进 build.ps1 注释）。

## 运行

```powershell
3fp\tools\AudioEndpointProbe\AudioEndpointProbe.exe [输出JSON路径]
```

- 不带参数：JSON 写到当前目录 `endpoint-capabilities.json`。
- 仓库惯例：`AudioEndpointProbe.exe 3fp\samples\passthrough\endpoint-capabilities.json`。

## 探测矩阵

对每个活动渲染端点 × 下列格式 × link 组合，调
`IAudioClient::IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, ...)`：

| 格式（GUID 前 4 字节） | link 组合 |
|---|---|
| IEC60958 LPCM (00000000-0000-0010) | 48000/2、192000/2 |
| PCM 16bit 基线 (00000001-…-9b71) | 48000/2、48000/8 |
| FLOAT 32bit 基线 (00000003-…) | 48000/2 |
| AC-3 (00000092-0000-0010) | 44100/2、48000/2 |
| DTS (00000008-0000-0010) | 44100/2、48000/2 |
| DD+/E-AC-3 (0000000a-0cea-0010) | 176400/2、192000/2 |
| DD+ Atmos JOC (0000010a-0cea-0010) | 176400/2、192000/2 |
| DTS-HD (0000000b-0cea-0010) | 192000/2(HRA)、192000/8(MA) |
| DTS:X E1 (0000010b-0cea-0010) | 192000/2、192000/8 |
| DTS:X E2 (0000030b-0cea-0010) | 192000/8 |
| MAT 1.0/MLP (0000000c-0cea-0010) | 176400/8、192000/8 |
| MAT 2.0 (0000010c-0cea-0010) | 192000/2、192000/8 |
| MAT 2.1 (0000030c-0cea-0010) | 192000/2、192000/8 |

通道掩码：2ch=0x3（立体声）、8ch=0x63F（7.1 surround）。link 组合依据 §2.3
（AC-3/DTS core ≤48k/2ch；DD+ 48k 族 192000/2ch、44.1 族 176400/2ch；TrueHD/MAT 192k/8ch；DTS-HD 192k 2ch(HRA)/8ch(MA)）。

每个组合**先发 cbSize=22 的普通 `WAVEFORMATEXTENSIBLE`**（mpv/Kodi/LAV/MPC-BE 实战形态），
被拒再发 **cbSize=34 的 `WAVEFORMATEXTENSIBLE_IEC61937`**（总 52B，附
dwEncodedSamplesPerSec/dwEncodedChannelCount/dwAverageBytesPerSec 建议值）——§2.2 的"默认 22，失败再试 34"。

## 判读

| 输出 | 含义 |
|---|---|
| `S_OK` | 端点接受该格式（控制台以 `*` 标记） |
| `S_FALSE` | 返回最接近匹配（独占语义下**不算**接受） |
| `AUDCLNT_E_UNSUPPORTED_FORMAT` (0x88890008) | 端点/驱动拒绝 |
| `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` (0x8889000E) | 独占被禁——若全部请求皆此码，端点整体出局（§2.4.2），JSON 置 `exclusiveModeBlocked:true` |
| `AUDCLNT_E_DEVICE_IN_USE` (0x8889000A) | 设备被占 |

控制台每端点另打印 `GetMixFormat` 摘要（共享模式基线参考：率/声道/位深/SubFormat）。

## JSON 字段说明

顶层 `schema/tool/generatedAt/shareMode/method/summary`，`endpoints[]`：

| 字段 | 说明 |
|---|---|
| `id` / `name` | 设备 ID（IMMDevice::GetId）/ 显示名（PKEY_Device_FriendlyName） |
| `isDefaultConsole` / `isDefaultCommunication` | 默认端点标记（eConsole / eCommunications） |
| `activateFailed` + `activateHr` | IAudioClient 激活失败时的错误码 |
| `exclusiveModeBlocked` | 全部独占请求都被 EXCLUSIVE_MODE_NOT_ALLOWED 拒绝 |
| `mixFormat` | 共享基线（rate/channels/bits/subFormat），可能为 null |
| `results[]` | 每次探测一行：`format`（可读名）、`subFormat`（完整 GUID）、`linkRate`、`linkChannels`、`channelMask`、`cbSize`（22/34）、`hr`（符号名或 0x 码）、`hrCode`（十进制）、`accepted` |

编码 UTF-8（无 BOM）。矩阵定义（GUID/link 组合/34 结构建议值）在源码 `kFormats[]` 一处维护。

## 已知实测结论（2026-11-07 本机）

6 端点（VB-Cable A / Steam Streaming Speakers / VoiceMeeter ×3 / Realtek 默认）×258 组合：
**全部 IEC 61937 格式被拒（AUDCLNT_E_UNSUPPORTED_FORMAT）**，PCM 16bit 48k 基线全部接受。
本机没有 HDMI/eARC 或 S/PDIF 物理端点——Atmos/DTS:X 激活的实机验证挂账待硬件（§7.8），
在目标电视/功放机器上跑本探针即为矩阵读数的复测方式。
