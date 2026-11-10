# 138 · 音频 Passthrough 与 Dolby Atmos / DTS:X 激活实现方案（2026-11-07，v2）

> 任务来源：给外部模块（`C:\PLAN\ColorTransform`，FFF.HDRdp.Test.dll）加入 **Bitstream / Passthrough** 与 **Dolby MAT / Atmos for Home Theater** 能力，让电视/功放正确激活相关内容。
> 本方案由三路输入合成：①内核音频路径只读审计（同日）；②GitHub 开源 Passthrough 实现调研（mpv/Kodi/LAV/MPC-BE/FFmpeg）；③Dolby 传输机制调研（MAT 2.0 / Atmos HT / 激活条件 / 开源实现存在性）。
> 关联：docs/91/92/93（中性化判据）、docs/100（音频管线）、docs/105 §14（出货形态）、docs/139（欠账清理，同日落）。
> **变更记录**：v2（2026-11-07）——用户拍板「实现基于目前 DLL」：帧解析/IEC 61937 打包/Dolby 仲裁**全部进现有模块**，内核只留中性缝（原 v1 的「内核复用 FFmpeg spdifenc」方案 A 作废，裁决记录保留在 §4.0）。判词 2、§2.4、§4.0-4.2、§5、§6、§7.9 已同步修订。

---

## 0. 判词（四条，先说结论）

1. **「电视点亮 Atmos/DTS:X」的物理条件只有一条：HDMI/eARC 上出现带 Atmos/DTS:X 元数据的码流。**
   真相表（详见 §2.1）：E-AC-3+JOC 码流、TrueHD+Atmos 码流、Dolby MAT 2.0 流都会点亮；**裸 LPCM 7.1.4 不点亮**（回落 Dolby Surround/多声道）；7.1.4 LPCM 只有经 Windows「Dolby Atmos for Home Theater」端点被 Dolby 组件转成 MAT 后才点亮。
   ⇒ 现状（FFmpeg 把 EAC3 JOC 解码成声道床再折叠成 PCM）**在物理上不可能让电视亮 Atmos**，与实现质量无关。要亮，必须 passthrough 或走 HT 端点。

2. **主线 = bitstream passthrough（路径①）。2026-11-07 用户拍板：实现全部基于现有 DLL——帧解析、IEC 61937 打包、Dolby 仲裁都进 FFF.HDRdp.Test.dll，内核只留中性缝（轨接管、WASAPI 非 PCM 独占、burst 直写、回退）。**
   IEC 61937 burst 封装是公开标准（FFmpeg `spdifenc` / Kodi `AEPackIEC61937` / LAV `Bitstream.cpp` 全部开源实现），模块自含重写的成本与对策见 §7.9；FFmpeg spdif muxer 从「被复用」降级为「对照基准」（P0 生成 golden vector，模块输出逐字节 diff 验证）。落位模块的法律性质不变（§6）。
   模块新增三块能力：①自含帧解析+打包器（AC-3/DTS/EAC3/TrueHD-MAT1.0/DTS-HD，**零 FFmpeg 依赖、导入表零新增**）；②Dolby 语义仲裁：JOC/Atmos 检测、SubFormat GUID（含 **`0000010a` DDP_ATMOS** 这个 mpv/Kodi/LAV 都没用过的专用 GUID）；③Dolby HT 端点识别——与模块已有 DV 分类知识同性质。

3. **自研 MAT 2.0 编码不可行（技术+法律双重否定），方案中彻底放弃，只留接口预留位。**
   调研实证：GitHub 上不存在任何 MAT 2.0 编码器；连 DD+ JOC 元数据层 PoC（raress96/dolby-atmos-encoder）都被 **EMDF `emdf_protection` 认证码**（ETSI TS 102 366 §H.2.2：算法"implementation dependent and is not defined"，256 种 CRC-8 多项式暴力扫描 0 命中）挡在认证硬件之外；开源 Atmos 编码全部依赖 Dolby 官方收费的 DEE 二进制。
   「Dolby MAT / Atmos for Home Theater」的可交付形态因此是**路径②**：播放器把 7.1.4 PCM 写进 Dolby HT 端点，**MAT 编码由 Dolby Access 的组件完成**（官方机制：HT 静态床 7.1.4 + 20 动态对象配额），自研侧零 Dolby 许可义务。

4. **五期落地，P2 即达成主目标。**
   P1（模块 AC-3/DTS 打包器 + 内核缝）即可用 golden vector 逐字节闭环验证；P2（E-AC-3 JOC + ATMOS GUID 仲裁）即实现「电视亮 Atmos」；P3 补 TrueHD(MAT 1.0 打包)/DTS-HD；P4 补 Dolby HT 协同模式；P5 收尾 ABI/UI/文档。

---

## 1. 目标与验收

| # | 目标 | 验收读数 |
|---|---|---|
| G1 | E-AC-3 JOC(Atmos) 文件在 HDMI 电视上点亮 Atmos | 实机：TV/AVR 面板显示 Dolby Atmos；无硬件时以端点接受 `0000010a` GUID + 码流逐字节完整性代替 |
| G2 | AC-3 / DTS / TrueHD / DTS-HD 同样点亮各自格式 | 同上，对应 GUID |
| G3 | 全自动回退：端点不支持 / 独占被占 / 打包失败 → 无感回到现有解码折叠路 | 回退事件 JSON（仿 exclusiveFallback，PlayerSession.cpp:2483-2485）；播放不中断 |
| G4 | 视频路径逐字节不变（铁律 2） | 图片/视频回归对照（仅音频代码分片） |
| G5 | Dolby HT 模式协同：HT 开启时播放器不与 Dolby 组件抢独占，改投 7.1.4 PCM（床）照样点亮 | HT 开/关双臂实测 |
| G6 | 中性化判据不回退 + 模块门禁不回退 | `tools/check_kernel_neutrality.py` 全绿；模块导入表 ≤ 现有 15 项（含 dbghelp，来源待查，见 §7.6）；模块 selftest 全过 |

---

## 2. 事实底座（三份调研的浓缩）

### 2.1 激活真相表（信号 → 电视反应）

| 送入电视/功放的信号 | 亮 Atmos？ | 对象级声场？ | 证据 |
|---|---|---|---|
| E-AC-3 + JOC 码流 passthrough | **是** | 有（终端解码对象） | 微软官方 GUID `DOLBY_DIGITAL_PLUS_ATMOS`="Dolby Atmos encoded with Dolby Digital Plus" |
| TrueHD + Atmos 码流 passthrough | **是** | 有 | FFmpeg profiles "Dolby TrueHD + Dolby Atmos" |
| Dolby MAT 2.0 流（PC HT 模式输出） | **是** | 有（床+≤20 动态对象） | 微软 Spatial Sound 文档 + AtmosSpatialTest |
| 裸 LPCM 7.1.4（不经 HT 端点） | **否**（回落 Dolby Surround） | 无 | 社区共识 + raress96 认证硬件实测侧证；AVR 手册原页未抓到（§7.3） |
| 立体声/5.1 PCM 经 HT 端点 | **是**（被转成 MAT） | 无对象（upmix） | 微软 Q&A："Dolby Atmos is in use" with stereo titles |

### 2.2 WASAPI SubFormat GUID 全值（微软官方文档，后缀均为 `-0010-8000-00aa00389b71`）

| 格式 | GUID 前 4 字节（完整前段） | 说明 |
|---|---|---|
| IEC60958 LPCM | `00000000-0000-0010` | 非 PCM 之外的缺省 |
| AC-3 | `00000092-0000-0010` | = WAVE_FORMAT_DOLBY_AC3_SPDIF (0x0092) |
| DTS | `00000008-0000-0010` | = 0x0008 tag |
| **DD+（E-AC-3）** | `0000000a-0cea-0010` | 普通 DD+ |
| **DD+ Atmos (JOC)** | `0000010a-0cea-0010` | **本方案的点睛 GUID**，开源三玩家均未使用 |
| DTS-HD | `0000000b-0cea-0010` | Type IV |
| DTS:X E1 / E2 | `0000010b-0cea` / `0000030b-0cea` | DTS:X |
| MAT 1.0 (MLP/TrueHD) | `0000000c-0cea-0010` | TrueHD 打包即走此 GUID |
| MAT 2.0 / 2.1 | `0000010c-0cea` / `0000030c-0cea` | MAT21 支持不隐含 MAT20，需分别探测 |
| WMA Pro / DST | `00000164-0000-0010` / `0000000d-0cea` | 备用 |

结构：`WAVEFORMATEXTENSIBLE_IEC61937`（cbSize=34，总 52B，含 dwEncodedSamplesPerSec/dwEncodedChannelCount/dwAverageBytesPerSec）。**实战（mpv/Kodi/LAV/MPC-BE）全部发 cbSize=22 的普通 WAVEFORMATEXTENSIBLE**（Kodi 注释：完整结构在老驱动上出问题）——本方案照抄：默认 22，失败再试 34。

### 2.3 IEC 61937 burst 常量表（FFmpeg spdifenc/spdif.h + Kodi AEPackIEC61937 双源一致；v2 起此表是**模块打包器的实现规格**）

| type | 格式 | burst 周期 | Pd 语义 | 要点 |
|---|---|---|---|---|
| 0x01 | AC-3 | 6144B (1536<<2) | bits=帧字节×8 | Pc bit8-12=bsmod |
| 0x0B/0x0C/0x0D | DTS 512/1024/2048 样本 | 2048/4096/8192B (blocks<<7) | bits=core×8 | 恰好装满可省 8B 头；LE 源需 bswap |
| 0x11 | DTS-HD | period×4，period∈{512,1024,2048,4096,8192,16384}→subtype 0..5 | bits，**对齐 (Pd&0xF)==0x8** | 帧头 `01 00 00 00 00 00 00 00 FE FE`+u16 size+完整帧(含 core)；MA 装不下剥 core（FFmpeg 60s 后重试 / LAV 永久） |
| 0x15 | E-AC-3 | 24576B（6 块@192k link） | **bytes**（非 bits！） | bsid>10 且 fscod≠3 时按 numblkscod 累积 {6,3,2,1} 帧凑满 6 块；**JOC 无特判**（依赖子流在帧内） |
| 0x16 | TrueHD (MAT 1.0) | 61440B，数据 61424B | =61424 | MAT codes：start 20B@0 / middle 12B@30708 / end 16B@61408；**动态 padding**（恒定 2560B 会被高码率 sync frame 撑爆）；48k 族 link 768kHz（24 AU/帧）、44.1 族 705.6kHz；必须等 major sync（`AV_RB32(data+4)==0xf8726fba`） |
| 0x03 | pause | 当前格式周期 | 32 | payload 首 u16=gap=rate×ms/1000（Kodi PackPause） |

**link rate/通道声明**（WAVEFORMATEX 按 link 率填，不是内容率）：DD+ 48k 内容→**192000**/2ch/5.1 mask（44.1 族→176400；32k 内容 HDMI 不支持）；TrueHD→192k 或 176.4k/**8ch**/7.1 mask；DTS-HD→192k，2ch(HRA)/8ch(MA)；AC-3/DTS core→≤48k/2ch。

### 2.4 主流实现的共性设计（mpv/LAV/Kodi/MPC-BE 的最大公约数）

1. **两派打包策略**：mpv（`audio/decode/ad_spdif.c` 伪解码器）与 LAV（`Bitstream.cpp`）在**解码层内**复用 FFmpeg spdif muxer（`avformat_write_header`+`av_write_frame` 到自定义 AVIO，输出"伪装成 s16 PCM"的 burst）；Kodi 在引擎层 `AEBitstreamPacker` 打包、sink 只收 RAW。~~v1 原取 mpv/LAV 派（复用 libavformat）~~ **v2 拍板后改为模块自含打包，FFmpeg spdif muxer 只作对照基准**（§4.0/§7.9）；但两派暴露的机制事实（burst 参数、协商顺序、时钟）全部照单吸收。
2. **能力探测**：不读系统列表，直接 `IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, ...)` 逐格式试（Kodi 在枚举期缓存结果）；`EXCLUSIVE_MODE_NOT_ALLOWED` → 整个设备出局。
3. **协商顺序**：原样请求 → 特定回退（mpv 谎报 AC3 subtype；LAV DTS-HD→core）→ 放弃 bitstream 转解码（`BitstreamFallbackToPCM`）。RAW/IEC 永不自动改参数。
4. **独占打开**：全部事件驱动（EVENTCALLBACK，Kodi/MPC-BE 加 NOPERSIST）；`hnsBufferDuration==hnsPeriodicity≠0`；period 取 device default（Kodi 放大到整数倍、目标 ≥50ms）；`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` → GetBufferSize 重算后重开（Kodi 还会重新 Activate）；被占（DEVICE_IN_USE）→ 1ms 起指数退避至 1s。
5. **送包节奏**：设备事件节拍驱动、每次填满整块（GetBuffer(bufferFrameCount)）；欠载立即补（mpv 补两轮）或攒齐整块再发（Kodi aux buffer）。
6. **时钟**：`IAudioClock::GetPosition/GetFrequency` → delay=(已提交样本−设备位置)/link_rate；QPC 校正调用延迟；>10s 异常值丢弃。**PTS 在打包层保留**（mpv 直通；LAV 用 m_rtBitstreamCache 补偿 muxer 缓存一帧延迟 + jitter 自纠）。
7. **热切换**：IMMNotificationClient 全套回调，尤其 `PKEY_AudioEngine_DeviceFormat` 属性变化（电视/功放改格式时触发）→ 整体重开。

### 2.5 坑点清单（实现期逐条对照）

- **JOC 完整性**：DD+ Atmos 的 dependent substream 必须与独立子流同帧进 burst；MKV Block 可能拆/并帧 → 进打包器前必须完整帧（内核 av_parser 归整，§4.1）。
- **TrueHD**：major sync 前丢包；动态 padding；一个 AVPacket 至多交付一个 burst；44.1 族换 176.4k link。
- **DTS-HD**：帧必须含 core；period 合法性检查；Pd 特殊对齐；MA 装不下剥 core。
- **字节序**：WASAPI 缓冲按 s16le 样本字填（逻辑 BE 字 Pa=0xF872 存成 72 F8）；LE DTS 源要 bswap；打包器交付前统一按 s16le 摆好。
- **时钟漂移**：GetPosition 延迟用 QPC 差值校正；全部 delay 用 link rate。
- **互斥**：bitstream 与音量/单声道/变速/HRTF 床渲染天然互斥（PCM 变换不适用）——UI 强制关、内核断言。
- **spdifdec 不能回读** TrueHD/DTS-HD/MAT——自产流的回读验证只能对 AC-3/EAC3/DTS 用；TrueHD/DTS-HD 用 golden vector 结构断言。

---

## 3. 现状审计（集成点，全部来自同日只读审计，行号以 KF=3fp/kernel 为准）

### 3.1 可直接复用的既有机制

| 机制 | 位置 | 对 passthrough 的意义 |
|---|---|---|
| 独占模式骨架 | WasapiRenderer 构造带 exclusive 参数（.h:102-104）；`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` 对齐重试（cpp:1127-1138）；独占失败自动回退共享（PlayerSession.cpp:2205-2212） | 差"非 PCM 格式探测"一步 |
| 设备热切换 | 500ms 默认端点轮询（cpp:1238-1254）+ IsAudioDeviceLost→RequestRestart→RecoverAudioDevice（cpp:134-139/973-978，PlayerSession.cpp:2196-2233） | 端点变更重开已通，补 IMMNotificationClient 属增强项 |
| 时钟 | updateMediaClock（cpp:1193-1221）：GetPosition/GetFrequency→100ns 换算+QPC 插值 | 61937 载波恒定率，逻辑保留，delay 换 link rate |
| **IAMF 同构旁路** | TryOpenIamfAudioDecoder（PlayerSession.cpp:5266-5301）→ PumpIamfAudio（5306-5365）→ FallBackFromIamf（5370-5382，重开 FFmpeg）；接入点 DoOpen 4152-4163、DoSelectStream 7709-7729；快照置位 5294-5299 | **passthrough 完全照此复刻**：TryOpen 接管整条轨 → 包循环按 codec_id 拦截 → 失败 FallBack 重开 FFmpeg |
| 追加导出惯例 | PlayerApi.cpp:471-493（SetAudioRenderPlug 两导出），PlayerApiVersion 恒 20；快照尾部追加纪律（Api.h:281-311）+ 偏移钉（451-484，audioFailures:584=最后钉，sizeof==592） | 新导出与新快照字段（offset 592 起）有既定模式 |
| 宿主开关模式 | 播放器会话.vb:731-748（设置音频效果→SetAudioRenderPlug/SetMonoAudio）；exclusiveFallback JSON（PlayerSession.cpp:2483-2485）；信息层行（播放器信息图层呈现器.vb:399-420） | UI/事件/显示三件套照抄 |

### 3.2 缺口（要新建的）

1. **独占格式探测只试 PCM**（cpp:1069-1106：7 档采样率 × 4 种 PCM/浮点）——需扩展 IEC61937 SubFormat 枚举。
2. **包循环无 codec 分流**：AC3/EAC3/DTS/TRUEHD 枚举全树零出现（"TrueHD" 命中全是 NVIDIA TrueHDR）；解码在 OpenDecoder(4161) 一把梭。
3. **bitstream 路由**：Enqueue 全链（预滚动 641-677、swr 678-680、TempoAudio 6773、ApplyGain 785-834、双耳缝 631/694）均为 PCM 语义。
4. **端点能力上报**：无 IAudioClient2/3、无 PKEY_、无注册表 MMDevices 读取；GetMediaInfo 音频段（8224-8268）无任何 Atmos/直通字段。
5. **EnsureResampler 忽略端点 dwChannelMask**（cpp:280 用 av_channel_layout_default(outputChannels_)；mask 在 148-152 已读未用）——Dolby HT 路由（7.1.4 PCM）需要它。

### 3.3 两树差异提醒

KF 领先 TP 的音频项＝mono 辅助、ASR AudioTap、音频延迟/变速、快照尾部 9 字段；**AudioRenderPlug.h 两树逐字节相同**。passthrough 全部落在 KF 开发；`third_party\fff_project` 是出货基线**不动**（铁律 1），成熟后走上游 PR。

---

## 4. 架构设计（v2：全部实现基于现有 DLL）

### 4.0 职责划分决策（2026-11-07 用户拍板：实现基于目前 DLL）

> **拍板记录**：用户明确要求「实现是基于目前 DLL 上进行实现」——帧解析、打包、仲裁全部落在现有外部模块（FFF.HDRdp.Test.dll），**不新建 DLL、不在内核新增打包实现**；内核只留一条中性缝。v1 的方案 A（内核复用 FFmpeg spdifenc）据此作废，原裁决保留如下供追溯。

| 方案 | 内容 | 裁决 |
|---|---|---|
| A（v1 推荐，**已作废**） | 内核：FFmpeg spdif muxer 打包 + WASAPI 非 PCM 独占 + 旁路回退。模块：仅 Dolby 语义仲裁 | 代码量最小（复用 libavformat），但用户拍板否定 |
| **B（v2 采纳）** | **模块**：自含帧解析 + IEC 61937 打包 + Dolby 仲裁（零 FFmpeg）。**内核**：中性缝（轨接管/回退、WASAPI 非 PCM 独占、burst 直写、快照/导出） | 满足拍板；非公开能力统一进模块的交付形态；成本与对策见 §7.9 |
| C（v1 备选，继续否决） | 全部进内核，模块不动 | 不满足用户要求；且 ATMOS GUID 仲裁/HT 识别属杜比语义，放模块更一致 |

| 职责 | 承载方 | 内容 |
|---|---|---|
| 帧边界保证 | **内核**（`av_parser_parse2`——FFmpeg 已在内核链接，属通用管线既有件，非"打包实现"） | MKV 拆帧/并帧归整为完整 access unit 后再交模块（模块不收半帧） |
| 帧解析 + IEC 61937 打包 | **模块**（新增 `PassthroughFrameParser` + `PassthroughPacker`，自含 §2.3 常量表，零外部依赖） | AC-3/DTS/EAC3 burst、TrueHD MAT 1.0（动态 padding）、DTS-HD period/剥核、字节序 |
| Dolby 语义仲裁 | **模块** | JOC/Atmos 检测、SubFormat GUID（含 `0000010a` ATMOS）、HT 端点识别、MAT 2.0 预留位 |
| 中性缝 | **内核**（新契约头 `FFF.Player.AudioPassthrough.h`，纯 POD、禁 FFmpeg） | 轨接管/回退状态机（IAMF 同构）、WASAPI 非 PCM 独占打开（按模块决策构造 WAVEFORMATEX）、Bitstream 路由、快照/导出/事件 |

**边界线（写进契约头注释）**：内核做「字节搬运 + 设备协商」，不理解任何编码语义（codec 以内核自有枚举值传递）；模块做「帧语义 + 杜比语义」。模块缺席/未授权 → 内核整体回退解码路径（不做通用 GUID 降级打包——内核没有打包器；这比 v1 的降级更简单，回退语义也更干净：模块在=直通，模块不在=今日行为）。

### 4.1 内核：中性缝（探测 → 接管 → 路由 → 回退）

**P1 新文件**：`3FP/Audio/AudioPassthrough.{h,cpp}`（轨接管状态机，IAMF 旁路同构）+ 契约头 `3FP/Api/FFF.Player.AudioPassthrough.h`。内核**不含任何打包代码**：完整帧进模块，burst 回内核。

```
demuxer(AVPacket, codec_id=ac3/dts/eac3/truehd)
  └─ av_parser_parse2 归整完整帧（MKV 拆/并帧防御；通用管线件，非打包）
  └─ TryOpenAudioPassthrough(track)                     ← 仿 5266-5301
       ├─ 模块 describe()：codec→GUID/link rate/burstBytes/ATMOS 标志——见 4.2
       ├─ 端点能力：IsFormatSupported(EXCLUSIVE, 按模块决策构造的 WAVEFORMATEX)
       │            （扩展 cpp:1069-1106；ATMOS 变体被拒 → 模块 re-describe 退通用 GUID 再试）
       ├─ 成功：接管整条轨，audioDecoder_=nullptr（同 IAMF 4159-4160），
       │        snapshot.passthroughActive/Format 置位（仿 5294-5299）
       └─ 失败：Emit {"type":"audio","passthrough":true,"fallback":true,...}（仿 5286-5289）
包循环（5195-5244 与 6094-6104 两处拦截，codec_id 分支在 decodeAudioPacket 前）
  └─ 模块 pack(frame) → 0..1 个 burst（攒帧状态在模块 ctx；burstSize=0=攒帧中）
       └─ renderer->EnqueueBitstream(burst, linkRate, mediaDuration100ns, pts)
            └─ WasapiRenderer 新路由 Bitstream（仿 BedRoute h:162-173）：
                跳过 swr/gain/tempo/plug；GetBuffer 直写 burst
失败/格式变更 → FallBackFromPassthrough()（仿 5370-5382：模块 reset + 释放接管态 + 重开 FFmpeg 解码）
```

关键实现细节（逐条对应 §2.5 坑点）：
- **格式协商**：WAVEFORMATEX 由内核按模块决策构造（SubFormat GUID + link rate + channels，cbSize=22 默认、失败再试 34）；独占 buffer==periodicity；period 取 device default；`NOT_ALIGNED` 重算重开（骨架已有）；被占退避重试（§2.4.4）。
- **计量**：burst→媒体时长由模块在 pack() 输出（mediaDuration100ns：AC3 帧 1536/rate；EAC3 攒满 6 块=24576B@192k；DTS blocks<<7；TrueHD 61440B@768k），内核只透传——预滚动/不连续修复不适用（审计 §3.2.3）。
- **互斥断言**：passthrough 生效期间 `SetMonoAudio/SetPlaybackRate/SetAudioRenderPlug` 请求被拒或排队（返回明确错误码）；UI 侧联动禁用（4.4）。
- **seek**：调用模块 reset() 清攒帧/MAT 环 + 内核清 burst 队列，从 seek 目标后第一个完整帧重新开始；TrueHD 重新等 major sync。
- **设备热切换**：复用 RecoverAudioDevice；重开后端点能力可能变化 → 重新走 TryOpen 探测（降级路径：bitstream→解码）。
- **GetMediaInfo**：音频段追加 `passthrough:{supported:[...],trackAtmos:bool}`（codec profile 用 `AV_PROFILE_EAC3_DDP_ATMOS`/`AV_PROFILE_TRUEHD_ATMOS`，Kodi StreamUtils.cpp 同源判定）。

### 4.2 外部模块：承载全部实现（解析器 + 打包器 + Dolby 仲裁）

**新契约头** `3FP/Api/FFF.Player.AudioPassthrough.h`（纯 POD、禁 FFmpeg、version=1，随 contract 快照进模块仓——沿用 `tools\ContractClosure.ps1`/`Refresh-Contract.ps1` 流程，构建哈希门禁自动覆盖）：

```cpp
struct FFF3FPAudioPassthroughTrackInfo {   // 内核 → 模块：这条轨是什么（describe 一次）
    std::uint32_t size, version;
    std::uint32_t codecId;                 // 内核自有枚举（POD 化，不引 libavcodec）
    std::uint32_t sampleRate;              // 内容率
    std::uint32_t channelCount;
    const std::uint8_t* firstFrame;        // 首个完整帧字节（供 sync/JOC 检测）
    std::uint32_t firstFrameSize;
    const char*    endpointHint;           // 端点显示名/特征（HT 识别用，可空）
};
struct FFF3FPAudioPassthroughFormat {      // 模块 → 内核：怎么送（describe 输出）
    std::uint32_t size, version;
    std::uint32_t subFormat[4];            // 仲裁的 IEC61937 SubFormat GUID（含 ATMOS 变体）
    std::uint32_t linkSampleRate;          // 192000/176400/48000…
    std::uint32_t linkChannels;            // 2 / 8
    std::uint32_t burstBytes;              // 该格式的 burst 周期（6144/8192/24576/61440…）
    std::uint32_t flags;                   // bit0=atmosDetected, bit1=htEndpointPreferred, ...
    const char*   displayName;             // UTF-8，供信息层（"Dolby Digital Plus JOC (Atmos)"）
};
struct FFF3FPAudioPassthroughApi {         // 模块 → 内核：函数表（形状仿 FFF3FPAudioRenderPlug）
    std::uint32_t size, version;           // FFF3FP_AUDIO_PASSTHROUGH_VERSION = 1
    void* context;                         // 模块自有状态（攒帧/MAT 环/判决缓存），内核不解释
    // 一次/轨：校验 + JOC 检测 + GUID 仲裁。负值=拒绝 → 内核回退解码
    std::int32_t(*describe)(void* context,
        const FFF3FPAudioPassthroughTrackInfo* info,
        FFF3FPAudioPassthroughFormat* outFormat) noexcept;
    // 每完整帧一次：塞入攒帧器，产出 0..1 个 IEC burst（字节序已按 s16le 摆好）。
    // burstSize=0 表示攒帧中；mediaDuration100ns=本次消耗的媒体时长（含攒帧折算）。
    // 不得分配、不得阻塞（跑在 session worker 的 Enqueue 路径）。
    std::int32_t(*pack)(void* context, const std::uint8_t* frame, std::uint32_t frameSize,
        std::uint8_t* outBurst, std::uint32_t burstCapacity,
        std::uint32_t* burstSize, std::int64_t* mediaDuration100ns) noexcept;
    void(*reset)(void* context) noexcept;  // seek/换轨：清攒帧与 MAT 环
    void(*destroy)(void* context) noexcept;
};
FFF3FP_API FFFResult FFF_GetAudioPassthroughApi(std::uint32_t version,
    FFF3FPAudioPassthroughApi* api) noexcept;        // 模块导出（第三张函数表，未授权→拒发）
FFF3FP_API FFFResult FFF3FP_SetAudioPassthroughApi(FFF3FPHandle player,
    const FFF3FPAudioPassthroughApi* api) noexcept;  // 内核导出：宿主钉入（形状仿 SetAudioRenderPlug）
FFF3FP_API FFFResult FFF3FP_SetAudioPassthroughMode(FFF3FPHandle player,
    std::uint32_t mode) noexcept;                    // 0=自动 1=强制 2=关；追加导出不 bump 版本
```

**模块内新增源文件**（`C:\PLAN\ColorTransform\src\`，全部零外部依赖、导入表零新增）：
1. `PassthroughFrameParser.{h,cpp}`——sync 探测与帧界：AC-3/E-AC-3（0x0B77、bsid、frame size code），DTS（四种 syncword + 14bit 变体 bswap 判定、帧大小直接取自头），TrueHD（major sync `0xf8726fba`@+4、ratebits→samples/帧）；顺带产出 **JOC/Atmos 位**（E-AC-3 addbsi `flag_ec3_extension_type_a` + `complexity_index_type_a`，ETSI TS 102 366 v1.4.1 + FFmpeg ac3_parser.c 同源逻辑；TrueHD major sync 内 Atmos 判定）。
2. `PassthroughPacker.{h,cpp}`——IEC 61937 burst 构建（按 §2.3 常量表逐条实现）：Pa/Pb/Pc/Pd 头、AC-3/DTS 直通 burst、E-AC-3 {6,3,2,1} 攒帧到 6 块（Pd=bytes）、TrueHD MAT 1.0（mat_start/middle/end + **动态 padding**、61440B 环、按 s16le 字节序交付）、DTS-HD（period 计算、(Pd&0xF)==8 对齐、超载剥 core 策略）。状态机用固定 arena（仿 BedBinauralRenderer 的零分配纪律），outBurst 由内核提供缓冲。
3. `PassthroughApi.cpp`——函数表装配 + describe 仲裁 + 授权门（未授权/缺席 → 拒发函数表，内核整体回退解码，语义与 FFF_GetAudioRenderApi 同构）。
4. **Dolby 仲裁**（并入 describe）：检测到 JOC → `0000010a`(DDP_ATMOS)，端点拒绝再退 `0000000a`(DDP)；TrueHD → `0000000c`(MLP)；DTS-HD/X 同表。**这是相对开源三玩家的真实增量**（它们都无 ATMOS GUID 逻辑）。
5. **Dolby HT 端点识别**（P4）：内核把端点显示名/格式特征经 TrackInfo.endpointHint 传入；匹配「Dolby Atmos for Home Theater」（Dolby Access 注入的空间音效端点）→ `htEndpointPreferred`，内核改走 4.3 而非抢独占。
6. **（预留）MAT 2.0 槽位**：`flags` 留位 + 契约头注释写明结论（§6：自研编码不可行；何时才可能启用——获得 Dolby DEE 类许可时）。不做任何 MAT 2.0 打包代码。

**selftest**：模块 `tests\selftest.cpp`（现 528 checks）新增 golden-vector 差分用例（见 §5 P0），挂进 `build.bat` 四步门禁。

### 4.3 Dolby HT 协同模式（路径②，P4）

- **机制**（微软官方）：HT 模式是端点空间音效格式；应用喂 PCM，**MAT 编码由 Dolby 组件完成**；静态床 7.1.4 + 20 动态对象配额；非感知应用混入后照样点亮（upmix，无对象）。
- **播放器侧实现**：
  1. `EnsureResampler` 尊重端点 `dwChannelMask`（修 cpp:280；mask 已读未用，148-152）——这样 7.1.4 源能以原生 mask 折叠进 HT 端点而非被 default 布局重排；
  2. 模块仲裁返回 `htEndpointPreferred` 时：**放弃独占直通**（HT 端点被 Dolby APO 管线占用，独占 IEC 请求大概率失败且互相干扰），改走现有共享模式 PCM 折叠（多声道设备路径，审计 §3.2 的"多声道设备→Native 折叠"已存在）；
  3. 快照新增 `audioPassthroughMode`：`0=解码折叠 / 1=IEC直通(模块打包) / 2=HT端点(MAT由系统生成)`，信息层分别显示「直通：Dolby Digital Plus JOC」「输出：Dolby Atmos for Home Theater（系统 MAT）」。
- **升级变体（远期，不排期）**：`ISpatialAudioClient` 动态对象路线——把 JOC 对象映射为 ≤20 个 ISAC 对象，可保留真对象声场且编码仍归 Dolby 组件；需要 OAMD 解析（参考 Cavern，**注意其非商用许可**，只读思路不抄码）。

### 4.4 ABI 与宿主

| 层 | 改动 | 依据模式 |
|---|---|---|
| `FFF.Player.Api.h` | 快照尾部追加 `audioPassthroughActive`(u32) / `audioPassthroughMode`(u32) / `audioPassthroughFormat`(i32，内核格式枚举)——offset 592 起，更新 sizeof pin 457 与新增 offsetof pin；PlayerApiVersion **保持 20** | 尾部追加纪律（281-311）+ pin 块（451-484） |
| `PlayerApi.cpp` | 新增导出 `FFF3FP_SetAudioPassthroughApi` / `FFF3FP_SetAudioPassthroughMode`；追加导出不 bump 版本 | 471-493 模式 |
| `PlayerSession` | 偏好成员随 RecreateAudioRenderer（2096-2141）透传；TryOpen/FallBack 状态机；回退 JSON | IAMF + exclusiveFallback（2465-2493） |
| 宿主 VB | 播放器原生接口.vb 镜像尾部字段 + P/Invoke（1394-1422 模式）；色彩变换模块接口.vb 增音频直通表入口（`FFF_GetAudioPassthroughApi`，:70/:260/:1145 模式）；播放模型.vb 配置属性（92-102）；**独立开关**「音频源码直通：自动/开/关」（与独占模式联动：开→自动请求独占；共享模式下无效并在信息层提示）；信息图层呈现器新行（399-420 模式）；Form媒体信息 徽章 | 现有接线表（审计 §5） |
| 模块仓 | contract\ 快照刷新（新头）、`ContractClosure.ps1` 纳入新头、src 三新文件、selftest 增补、README §四导入表复查 | 现有构建门禁 |

### 4.5 回退链全景（每级都 emits JSON 事件，用户可感知原因）

```
请求直通（模式=自动或强制）
 ├─ 模块缺席/未授权/拒发函数表 → 直接走解码（今日行为）──────────┐
 ├─ 模块 describe() 拒绝（不认识的 codec/坏帧） → 同上 ──────────┤
 ├─ 端点 IsFormatSupported(EXCLUSIVE, 决策 GUID) 失败            │
 │    └─ 模块 re-describe 降 GUID（ATMOS→DDP；MAT21→MAT20→MLP）─┤
 ├─ HT 端点在场（htEndpointPreferred）→ 路径②：共享 7.1.4 PCM ───┤
 ├─ 独占打开被占/被禁 → 指数退避→共享解码 ──────────────────────┤
 ├─ 打包层失败（pack 负值/坏帧）→ FallBackFromPassthrough ───────┤
 └─ 全部失败 → 现有解码+矩阵折叠（今日行为，保底）◄──────────────┘
```

---

## 5. 分期计划（每期带验收读数）

| 期 | 内容 | 验收 |
|---|---|---|
| **P0** 探针、素材与 golden vector | ①`3fp/tools/` 端点能力探针（端点 × IEC GUID 矩阵的 IsFormatSupported 实测）；②素材获取：E-AC-3 JOC 与 TrueHD 样本（仓内现无；途径=自有片源/用户样张；ffprobe 验 profile）；③**golden vector 生成**：用 ffmpeg-full `-f spdif` 对样本各编码段产出参照 burst 文件（模块打包器的唯一真源对照）；④模块契约头骨架 + `ContractClosure.ps1` 纳入 | 端点能力矩阵落档；golden vector 集入库（samples/media，不入 git）；契约快照刷新通过 |
| **P1** 模块 AC-3/DTS 打包器 + 内核缝 | 模块 `PassthroughFrameParser`（AC-3/DTS 部分）+ `PassthroughPacker`（0x01/0x0B-0x0D）+ `PassthroughApi` 函数表；内核 AudioPassthrough 状态机 + WasapiRenderer Bitstream 路由 + 快照字段 + 独占非 PCM 探测 + 宿主开关 | **模块 selftest golden diff 逐字节一致**（AC-3/DTS core）；端点实开/热切换/seek/回退 JSON 事件齐；`check_kernel_neutrality.py` 绿；模块导入表零新增 |
| **P2** EAC3 JOC + ATMOS 仲裁 | 模块 EAC3 攒帧（{6,3,2,1}）+ JOC 检测 + describe 仲裁（ATMOS→DDP 降级）+ 信息层/媒体信息 | JOC 完整性断言（依赖子流同帧）；模块在场/缺席双臂对照；**实机 TV 亮 Atmos**（有硬件即验，无硬件以 ATMOS GUID 接受 + golden diff 为准） |
| **P3** TrueHD MAT 1.0 + DTS-HD | 模块 TrueHD（major sync 门控、动态 padding、61440B 环、字节序）+ DTS-HD（period/Pd 对齐/剥 core） | golden diff（TrueHD 段 spdifdec 不能回读，用 burst 结构断言 + golden 对照）；DTS-HD→core 回退事件 |
| **P4** Dolby HT 协同 | EnsureResampler 尊重端点 mask + HT 端点识别（endpointHint）+ passthroughMode 三态 | HT 开/关双臂实测：HT 开→TV 亮 Atmos（床）；HT 关→直通路径接管 |
| **P5** 收尾 | GetMediaInfo/徽章/文档（docs/140 落地记录）/contract 与模块构建门禁对账 | 发布3FP.ps1 门禁绿；模块导入表 ≤ 现有 15 项；selftest 全过 |

依赖：P0→P1→P2→P3 串行；P4 依赖 P2 的缝；P5 独立。**每期不跑全量测试**（按全局偏好，只跑音频分片回归 + golden diff + 模块 selftest）。

---

## 6. 法律姿态（与中性化判据对表）

| 内容 | 判定 | 依据 |
|---|---|---|
| IEC 61937 burst 打包（AC3/DTS/EAC3/TrueHD-MAT1.0） | **公开标准，免授权**。v2 落位模块——这是交付形态决策，法律性质不变；docs/93 v3 判据约束的是**内核本体**（判据脚本 `check_kernel_neutrality.py` 只扫内核树），模块本就是自有的非公开交付件 | FFmpeg/LAV/Kodi/mpv 全开源实现；93 v3 判据原文 |
| 码流 passthrough 本身 | **无需 Dolby 许可**（转发用户片源自带的授权码流，业界通行：Kodi/mpv/Plex） | 调研 §D① |
| JOC/Atmos 位检测（ETSI TS 102 366） | 公开规范（ETSI 免费下载），实现落模块（与 DV 分类知识同仓） | ETSI TS 102 366 v1.4.1 |
| MAT 2.0 编码 | **不可行且不尝试**：技术（EMDF 认证码 + 无规范全文）+ 法律（Dolby 专利池/商标/认证；唯一合法途径 DEE 授权）双重否定 | raress96 实证 + 调研 §C/§D③ |
| Dolby HT 路由（路径②） | 编码由 Dolby 已授权 Windows 组件完成，**自研侧零许可义务**（最干净的一条路） | 微软官方机制文档 |
| DDP_ATMOS/DTS:X GUID 的使用 | 仅是格式枚举值（微软公开文档），无额外义务 | MS 官方 GUID 表 |
| 商标 | UI 文案避免暗示"本产品经 Dolby 认证"；沿用模块现有中性化措辞（「Dolby Digital Plus JOC」为格式描述性使用） | docs/98 中性化原则 |

---

## 7. 风险与未判定（别当已判）

1. **ATMOS GUID (`0000010a`) 的驱动行为差异**：微软文档定义了它，但各 HDMI 驱动（NVIDIA/Intel/Realtek）对它的暴露与路由不一致——P0 能力矩阵必须实测本机+目标电视驱动；mpv/Kodi/LAV 不用它本身说明通用性待验（这恰是模块仲裁的价值点，也是风险点）。
2. **IEC 61937-14 part 号**：MAT 与 IEC 61937-14 的对应关系是业界通行说法，本次未能核实 IEC 原文（正文付费+检索失败）——文档层面标注「未核实」，不影响实现（实现以微软 GUID 表为准）。
3. **「裸 LPCM 不点亮 Atmos」**：依据社区共识 + raress96 认证硬件侧证，AVR/电视手册原页未取到——G5 验收以实机为准。
4. **HT 端点共享模式的确切混音格式值**未逐字节核实（P4 实测项）。
5. **独占与 Dolby APO 的互斥细节**：HT 模式下端点是否完全拒绝 IEC 独占（驱动相关）——P4 双臂实测项，回退链已兜底。
6. **模块导入表门禁**：现 15 项含 **dbghelp**（来源待查，疑为诊断/堆栈）；新增 passthrough 代码必须纯软件位解析/字节变换，禁止再引入依赖；P2 落地时顺带查清 dbghelp 来源并补进 README §四。
7. **Swift_TrueHD**（自称纯 Swift TrueHD+Atmos 编码器）真实性未核实，仅存档不采信。
8. **素材获取**是 P0 的外部堵点：仓内无 JOC/TrueHD 样本；若无实机 HDMI 电视，G1/G5 的最终验收降级为「端点接受 + 码流完整性 + golden diff」，实机项挂账待硬件。
9. **打包器自含重写成本**（v2 拍板后的新增项）：≈1.2k-1.8k 行 C++（解析+打包+攒帧/MAT 状态机），失去 FFmpeg muxer 的"免费正确性"——**对策**：P0 用 ffmpeg-full 预生成 golden vector 集（各编码多段 + 边界帧 + 坏帧），模块 selftest 逐字节 diff 并挂进 `build.bat` 四步门禁；任何行为疑义以 FFmpeg spdifenc 源码为唯一真源逐条对照；TrueHD 动态 padding 与 DTS-HD period 是最易错的两处（§2.5），各配专项用例。

---

## 8. 素材与验证工具

| 工具/素材 | 位置/来源 | 用途 |
|---|---|---|
| ffmpeg/ffprobe CLI（git-2026-10-11，含 spdif muxer/demuxer、eac3 编码器、AV_PROFILE_EAC3_DDP_ATMOS 探测） | `publish\PLAN\ffmpeg-full\` | golden vector 生成基准、素材 profile 验证 |
| golden vector 集（v2 新增） | P0 产出，`3fp/samples/`（不入 git） | 模块打包器逐字节 diff 的唯一真源 |
| 端点能力探针 | `3fp/tools/`（P0 新增） | 本机端点 × GUID 矩阵实测（P0 验收读数） |
| 模块 selftest | `C:\PLAN\ColorTransform\tests\selftest.cpp`（现 528 checks） | golden diff 挂载点，随 build.bat 门禁跑 |
| AtmosSpatialTest | https://github.com/emufanhui/AtmosSpatialTest | Dolby MAT 测试信号发生器（HT/TV 侧验证参考） |
| raress96/dolby-atmos-encoder | https://github.com/raress96/dolby-atmos-encoder | 仅作「开源编码不可行」证据与 OAMD/JOC 解析参考（Rust，不引入） |
| E-AC-3 JOC / TrueHD 样本 | 待获取（P0 外部堵点） | G1/G2 验收 |

---

## 9. 调研来源（关键 URL）

- 微软《Representing Formats for IEC 61937 Transmissions》（GUID 全值）：https://learn.microsoft.com/en-us/windows/win32/coreaudio/representing-formats-for-iec-61937-transmissions
- 微软《Spatial Sound for app developers》（HT 机制/7.1.4+20 对象）：https://learn.microsoft.com/en-us/windows/win32/coreaudio/spatial-sound
- 微软《WAVEFORMATEXTENSIBLE_IEC61937》结构：https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksmedia/ns-ksmedia-waveformatextensible_iec61937
- 微软《Subformat GUIDs for Compressed Audio Formats》：https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/subformat-guids-for-compressed-audio-formats
- FFmpeg libavformat（spdif.h / spdifenc.c / spdifdec.c）、libavcodec（ac3_parser.c / profiles.c）：https://github.com/FFmpeg/FFmpeg
- mpv（ad_spdif.c / ao_wasapi*.c）：https://github.com/mpv-player/mpv
- Kodi AudioEngine（AESinkWASAPI.cpp / AEPackIEC61937 / AEBitstreamPacker / StreamUtils.cpp）：https://github.com/xbmc/xbmc
- LAV Filters（Bitstream.cpp / BitstreamMAT.cpp / BitstreamParser.cpp）：https://github.com/Nevcairiel/LAVFilters
- MPC-BE（AudioSwitcher.cpp / MpcAudioRenderer.cpp）：https://github.com/Aleksoid1978/MPC-BE
- raress96/dolby-atmos-encoder（开源 DD+JOC 编码不可行实证）：https://github.com/raress96/dolby-atmos-encoder
- AtmosSpatialTest（MAT 测试信号发生器）：https://github.com/emufanhui/AtmosSpatialTest
- Dolby DEE 官方仓库（只有插件 SDK，无编码核心）：https://github.com/DolbyLaboratories/dolby-encoding-engine
