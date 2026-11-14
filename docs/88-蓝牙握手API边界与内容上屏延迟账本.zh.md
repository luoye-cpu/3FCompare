# 88 · 蓝牙握手 API 边界与「内容上传 → 上屏」延迟账本

**简体中文** · 2026-11-14 · 3FCompare / 3FP 音频与呈现链路

> 结论先行：
> 1. **profile 拿得到，codec 拿不到。** Windows 用户态没有"当前 A2DP 编解码器"的公开 API；
>    `configured-codec-SIOP` 只发给内核态音频驱动（`IOCTL_SBAUD_GET_SIOP` 明确限定 kernel mode）。
>    LE Audio 的 `BluetoothLEAudio_CodecConfiguration` 同样是 ACX 驱动侧属性。
> 2. **能判"现在握手的是 A2DP / HFP / LE Audio"**，而且比"枚举所有端点"更准的做法是：
>    默认呈现端点 + 父服务 GUID + **设备格式（辅助）/ 输入端点是否有活动会话**。
>    Win11 会把 48 kHz 应用音频重采样到 HFP，所以 `GetMixFormat` 不能单独当判据。
> 3. **内容上传到上屏不是 0**：4K/144Hz 典型 **10–25 ms**，4K/60Hz 典型 **25–50 ms**，
>    9 路同播或上传错过 2 ms 提前窗时可到 **50–80 ms**。
> 4. 因此 `SetAudioDelay(-L_BT)` 是**错的**；正确补偿是 **`d = L_disp - L_BT`**。
>    60Hz 屏上按 `-L_BT` 补，会把音频多提前 20–40 ms（声音反过来领先画面）。

---

## 1. 蓝牙"握手模式"到底能拉到什么

| 信息 | 用户态可得 | 通道 | 备注 |
| --- | --- | --- | --- |
| 当前 profile（A2DP / HFP / LE Audio） | ✅ | SetupAPI：音频端点的父设备服务 GUID；`DEVPKEY_Bluetooth_ServiceGUID` / `DEVPKEY_Device_Service` | 本仓库 `tools/探测蓝牙音频延迟.ps1` 已实现 |
| 设备是否在位 / 是否默认端点 | ✅ | `IMMDeviceEnumerator`、`IMMNotificationClient`、端点 `DeviceState` | 判"当前在用"必须走这条，不是枚举所有端点 |
| 当前是 A2DP 还是 HFP 在出声 | ✅（组合判据） | 默认呈现端点 + 父服务 GUID + `PKEY_AudioEngine_DeviceFormat`（辅助）+ 输入端点是否有活动会话 | 父 GUID 只说明"支持 A2DP"；HFP 的真正触发是麦克风/Communications 流 |
| A2DP codec（SBC/AAC/aptX） | ❌ | 仅内核：`SIDEBANDAUDIO_PARAM_A2DP_CONFIGURED_CODEC`，经 `IOCTL_SBAUD_GET_SIOP` | Microsoft 文档明确 "This IOCTL can only be called from kernel mode" |
| aptX Adaptive 延迟档（LL/普通） | ❌ | 仅内核：`SIDEBANDAUDIO_PARAM_A2DP_CODEC_LATENCY_MODE` | 同上；仅 aptX Adaptive 使用 |
| LE Audio codec 配置 | ❌ | 仅驱动：ACX 属性 `BluetoothLEAudio_CodecConfiguration` | LC3 是默认；用户态无公开读取口 |
| AVDTP Delay Reporting | ❌ | Windows 蓝牙栈不消费 | 前一轮已确认 |
| 设备支持的 codec 列表 | ❌ | A2DP SDP 记录不公开给应用 | 只能维护设备库或实测 |

### 1.1 为什么 `configured-codec` 拿不到

Microsoft 的 A2DP sideband offload 文档把 codec 信息定义成 **SIOP**（sideband I/O parameter）：

- `codecs-SIOP`：音频驱动声明支持的 codec 列表；
- `configured-codec-SIOP`：音频驱动**读取**"当前配置的 codec"；
- 载体是 `IOCTL_SBAUD_GET_SIOP` / `IOCTL_SBAUD_GET_SIOP_UPDATE`，文档写明 **kernel mode only**。

这条路径服务的是音频 DSP/驱动（本机恰好是 Intel Smart Sound for Bluetooth Audio），
不是应用层。要绕过它只能自带内核驱动（签名、安装、维护、蓝屏风险），对一个"延迟估计开关"完全不划算。

LE Audio 同理：`BluetoothLEAudio_CodecCapabilities` / `CodecConfiguration` 是
ACX streaming driver 与 Windows LE Audio profile 之间的属性，不是 WinRT 面。

### 1.2 能推断到什么程度

Windows 的 A2DP 选择顺序是固定的（从高到低，取双方都支持的第一个）：

| 顺序 | codec | 生效条件 |
| --- | --- | --- |
| 1 | aptX Adaptive | Windows 11 24H2+，且蓝牙 radio 为兼容的 Qualcomm |
| 2 | AAC | Windows 11 21H2+ |
| 3 | aptX Classic | Windows 10 1507+ / Win11 21H2+ |
| 4 | SBC | 始终 |

所以"profile + radio 厂商 + 设备支持表"能给出**有置信度的推断**，但不是读取：

- 本机：Intel AX211（非 Qualcomm）+ Intel SST A2DP offload + 测试蓝牙耳机（支持 AAC/SBC/LDAC，不支持 aptX）
  ⇒ Windows 取不到 aptX Adaptive，LDAC 又不在 Microsoft 栈 ⇒ **几乎可以断定是 AAC**；
  延迟按 AAC 区间 **150–280 ms**，取中位 200–220 ms。
- 未知设备：按 SBC 上界 **300 ms** 兜底，UI 标"估计"。
- Qualcomm radio + aptX Adaptive 设备：可能落到 50–120 ms，但只有 24H2+ 才成立。

**不要把推断写成"读到"。** 开关的状态行应显示 `A2DP · codec 未公开（推断 AAC）`，
并把置信度（高/中/低）一起暴露。

---

## 2. 判定"当前握手"的推荐算法

现有脚本枚举了所有 Render 端点，能分类但不能回答"现在用的是哪一个"。建议改成：

```
1. GetDefaultAudioEndpoint(eRender, eConsole)      → 媒体输出
   GetDefaultAudioEndpoint(eRender, eCommunications) → 通话输出
2. 端点 ID → ContainerId → SetupAPI 父设备 → Service GUID
     110B = A2DP；111E/1112 = HFP；BthLEAudio/BTHLE = LE Audio
3. 读 PKEY_AudioEngine_DeviceFormat（WAVEFORMATEX，辅助判据）
     16 kHz 单声道 → 很可能 HFP；48 kHz 立体声 → 倾向 A2DP
     但 Win11 会把 48 kHz 应用音频重采样到 HFP，mix format 可能一直是 48k，
     所以这条只能当"辅助"，不能单独定论。
4. HFP 的真正判据：
     该设备的 Capture 端点上有 Active 的音频会话（麦克风被打开），或
     本应用自己创建了 Category=Communications 的输出流。
   用 IAudioSessionManager2 枚举 Capture 端点的会话状态即可，不需要打开麦克风。
5. 端点 DeviceState != ACTIVE，或不是默认端点 → "未在使用"
6. 订阅 IMMNotificationClient：
     OnDefaultDeviceChanged / OnDeviceStateChanged / OnPropertyValueChanged
   并每 500 ms 轮询一次 Capture 会话状态；去抖 300–500 ms。
```

要点：

- **Win11 的端点统一**：设备支持 HFP 时只建一个输出 + 一个输入端点；此时父服务 GUID
  不足以区分 A2DP/HFP，必须看**输入端点是否有活动会话**（或本应用是否建了
  Communications 流），设备格式只能辅助。`GetMixFormat` 尤其不可靠：Win11 可以把
  48 kHz 应用音频重采样到 HFP，端点仍报 48 kHz。
- **麦克风一开就全局切 HFP**：这是 Windows 的行为，不是本应用能控制的；开关要能
  在 300–500 ms 内改口，并把 `L_BT` 从 150–300 切到 100–250。
- **LE Audio**：按 `BthLEAudio` / `BTHLE` 父实例判；本机没有 LE Audio 设备，
  这一支尚未真机验证（脚本同）。

---

## 3. 内容上传 → 最终上屏：钱花在哪

### 3.1 阶段拆分

> 口径：这里的"内容上传"按渲染语义指**解码帧 → GPU 纹理**（`videoUpload100ns`），
> 不含 FFmpeg 解码/文件加载；后者属于启动首帧的 ~75 ms（§3.2），不是稳态。

```
L_disp = T_sched + T_upload + T_draw + T_present + T_dwm + T_scanout + T_response
```

| 阶段 | 代码/证据 | 典型 | 说明 |
| --- | --- | --- | --- |
| `T_sched` | `PlayerSession.cpp:6412`：`position <= now + 20'000`（**2 ms** 提前窗） | −2–0 ms | 帧最多提前 2 ms 提交；上传/绘制超过 2 ms 就错过目标 vblank |
| `T_upload` | `videoUpload100ns`（`Api:312`；Map wait / copy 拆分 `:350-364`） | GPU 拷贝 0.3–1.5 ms；CPU 4K 2–6 ms | 4K NV12 约 12.4 MB；Map 可能等上一帧 GPU |
| `T_draw` | `mainDraw100ns`（`Api:433`） | 0.5–3 ms | 4K 输出 + 缩放/色调映射；9 路时争用 |
| `T_present` | `presentWait100ns`（`Api:245`） | 0–1 个刷新周期 | 144 Hz 实测均值 **0.46 ms**（60 fps 内容，见下） |
| `T_dwm` | `VideoRenderer.cpp:8072` 实测 | 1 个刷新周期，错过再加 1 | 窗口化 flip 链由 DWM 合成；子 HWND 网格拿不到 independent flip |
| `T_scanout` | 显示器时序 | 0–1 个刷新周期，平均半个 | 取决于像素所在行 |
| `T_response` | 面板规格 | 1–8 ms | OLED 通常更快 |

### 3.2 本机实测锚点（2026-11-14）

`3fp/build/display_grid_probe.exe`（144 Hz 面板，60 fps 内容，臂 A = 产品同形）：

- Present 等待均值 **0.46 ms**；
- 4 秒内 `PresentRefreshCount` 推进 569，声明 144 Hz 应有 ≈576（差 1.2%）；
- **同一 vblank 内被覆盖的帧 = 36**；
- 每帧停留 2–5 个刷新周期（2 刷新 103 次、3 刷新 62 次、5 刷新 33 次）。

⇒ 在"内容比面板慢"的场景，`Present(1,0)` 并不阻塞，**Present 等待不是主项**；
真正的延迟在 DWM 合成 + 扫描 + 面板响应。

另一个锚点：`VideoRenderer.cpp:8072-8073` 的注释记录固定刷新桌面
`Present(0,0)` 仍被 DWM 按 vblank 合成，基线 **13.9–17.1 ms**，
低延迟窗口反而打出 23/46/66 ms 尖峰。这证明 DWM 这一帧是实打实存在的。

启动首帧（`3fp/docs/106`，1080p 静图）：内核链 T1→T2 ≈ **86–94 ms**，
其中可归因 upload 1.96 ms + softwareConvert 4.49 ms + presentWait 4.49 ms，
其余 ~75 ms 是 FFmpeg 首映射/解码/设备/交换链。**这是冷启动，不是稳态。**

### 3.3 预算表

| 场景 | 上传 | 绘制 | Present | DWM | 扫描 | 响应 | 典型合计 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 4K / 144 Hz / GPU 拷贝 | 0.3–1 | 1–3 | 0–7 | 7–14 | 0–7 | 1–5 | **10–25 ms** |
| 4K / 60 Hz / GPU 拷贝 | 0.3–1 | 1–3 | 0–17 | 17–33 | 0–17 | 1–5 | **25–50 ms** |
| 4K / 144 Hz / CPU 上传 | 2–6 | 1–3 | 0–7 | 7–14 | 0–7 | 1–5 | **18–40 ms** |
| 9 路 4K / 144 Hz | 争用 +1–2 帧 | 争用 +1–2 帧 | 0–14 | 7–21 | 0–7 | 1–5 | **35–80 ms** |

表中"典型"取中位而非上界；上界出现在上传/绘制错过 2 ms 提前窗、或 DWM 被
其他子窗口/覆盖层拖住时。9 路场景因为每个 session 都是独立子 HWND + 独立 flip 链，
DWM 必须逐窗口合成，拿不到 independent flip，这一帧省不掉。

### 3.4 怎么测才可信

| 方法 | 量到什么 | 边界 |
| --- | --- | --- |
| `presentWait100ns / swapChainPresents` | Present 调用内部阻塞 | **不含 DWM/扫描/响应**；不能单独当上屏延迟 |
| `PresentRefreshCount` + `SyncRefreshCount/SyncQPCTime` | 图像真正落到的 vblank | 精确到刷新周期；需要把 landed refresh 换算成 QPC |
| PresentMon `msUntilDisplayed` | Present → 显示器（含 DWM） | 不含面板扫描/像素响应；适合做回归 |
| 光电二极管/高速相机 | Present → 光子 | 唯一能覆盖扫描 + 响应的方法；需要硬件 |

建议给内核补一个导出的 `presentToLanded100ns`：
在 `PresentCurrentFrame` 记 `submitQpc`，在 `UpdatePresentGrid` 用
`landedQpc = syncQpc + (landedRefresh - syncRefresh) * period` 求得落点，
差值即"提交 → 真正上屏"，比 `presentWait100ns` 完整得多。这个量与
`videoUpload100ns`、`mainDraw100ns` 相加，就是软件侧能测到的 `L_disp` 上界；
剩下的扫描 + 响应仍需硬件测。

---

## 4. 对预播放开关的直接修正

上一版 doc 86 的补偿式是 `SetAudioDelay(-L_BT)`，默认"画面在 Present 那一刻就发光"。
实测 `L_disp` 不是 0，所以：

```
d = L_disp - L_BT
```

- `L_disp` 10–25 ms（144 Hz）/ 25–50 ms（60 Hz）；
- `L_BT` 150–300 ms（A2DP，**耳机侧**：编码 + 链路 + 接收缓冲，不含主机 60 ms）；
- 例：`L_BT = 200 ms`、`L_disp = 30 ms` ⇒ `d = -170 ms`，不是 `-200 ms`；
- 若屏是 60 Hz 且 `L_disp = 50 ms`、BT 是 150 ms ⇒ `d = -100 ms`；
- 若 `L_disp > L_BT`（有线/高刷 + HFP 低延迟），`d` 为正，音频要**更晚**，不是提前。

因此开关必须把 `L_disp` 作为补偿模型的一等项，而不是"主机缓冲可忽略"。
这也意味着：

- 只做蓝牙表值补偿，高刷屏上会**多补 10–40 ms**（声音领先画面）；
- 只做 `-L_BT` 而不管 `L_disp`，在 60 Hz 上会多补 25–50 ms；
- 校准流程应把"画面-声音"作为一个整体测，不能只测音频回环。

---

## 5. 落点（按性价比）

| 阶段 | 内容 | 改动面 |
| --- | --- | --- |
| A0 | 把探测脚本从"枚举端点"改成"默认端点 + 输入端点活动会话 + 父 GUID" | `tools/探测蓝牙音频延迟.ps1`，0.5 d |
| A1 | 托管层加 `BluetoothAudioProbe`（SetupAPI + MMDevice + 通知） | 新服务 + AudioPanel 状态行，1–2 d |
| A2 | 接出 `SetAudioDelay`，按 `d = L_disp - L_BT` 应用 | Interop + `IPlayerEngine` + UI，0.5–1 d |
| A3 | 导出 `presentToLanded100ns`，把 `L_disp` 纳入诊断 | `VideoRenderer` + snapshot + 托管映射，1 d |
| A4 | 实测校准（非蓝牙麦克风 + GCC-PHAT） | 设置页 + 持久化，2 d |

**不要把 A1 做成"读 codec"**：那条路只有内核驱动，做了也发不出去。

---

## 6. 验收

1. 默认端点切 A2DP↔HFP 时，状态行 ≤ 500 ms 内改口；
2. 设备未连接/非默认时显示"未在使用"，不显示延迟数字；
3. `PresentMon msUntilDisplayed` 与 `PresentRefreshCount` 换算的落点差 ≤ 1 个刷新周期；
4. 开关开/关各 5 次，`|画面-声音|` 中位数 ≤ 40 ms；`d` 的符号与实测方向一致；
5. 9 路 4K 下 `presentToLanded100ns` 的 P95 不超过 3 个刷新周期（144 Hz ≈ 21 ms）。

---

## 6.1 已落地实现（2026-11-14）

设置页新增「蓝牙低延迟」一节，开关默认关闭；勾选且默认输出是**已连接**的蓝牙端点时，
按 profile 区间中位自动下发有符号音频偏移：

| 层 | 文件 | 行为 |
| --- | --- | --- |
| 策略 | `src/3FCompare.Core/Settings/BluetoothLatencyPolicy.cs` | `d = L_disp - L_BT - trim`；`L_disp = clamp(2×1000/refresh + 5, 10, 60)` ms；profile 区间取中位；越界钳到内核 ±10 min |
| 设置 | `src/3FCompare.Core/Settings/AppSettings.cs` | `BluetoothLowLatencyMode`（bool，默认 false）、`BluetoothExtraLeadMs`（int，默认 0，钳 [-100,100]）；两个字段同时进 `_orig` / `Result` / `CopySettings` |
| 探测 | `src/3FCompare/Services/BluetoothAudioDetector.cs` | 默认 `eRender/eConsole` 端点（`CoCreateInstance` + 裸 vtable，不用 `[ComImport]`）→ MMDevices `ContainerID` blob 尾部 16B → `BTHENUM`/`BTHLE` 服务节点；读 `Service` / `HardwareID` 服务 UUID / `FriendlyName` / MAC；非蓝牙、未连接、COM 失败都返回可展示的 Detail |
| 互操作 | `Fff3FpNative.cs` / `IPlayerEngine.cs` / `Fff3FpEngine.cs` | `FFF3FP_SetAudioDelay(nint, long)`；`IPlayerSession.SetAudioDelay`；演示/测试替身补空实现 |
| 编排 | `src/3FCompare/Services/PlaybackCoordinator.cs` | 建会话/打开就绪后补放；镜像会话 100ms 轮询就绪后补放；已有会话在设置保存/端点变化时重下发；值为 0 且原本就是 0 时**不调用**（避免无意义 re-seek） |
| 运行时 | `src/3FCompare/MainWindow.axaml.cs` | 启动算一次；500ms 检查默认端点/profile/连接状态；只有 key 或值变化才调 `SetAudioDelay`；设置保存后立即重算 |
| 稳定性 | `src/3FCompare/MainWindow.axaml.cs` | `_coordinator.StateChanged` 在 `_sync.AddSlot` 后立刻回调 UI，此时会话仍是 Opening，`ReadSnapshot` 抛 `InvalidArgument` 会沿回调冒到打开批次、把该路判失败；现在对 `EngineException`/`ObjectDisposedException` 降级到 master/24fps，不再打断打开 |
| UI | `src/3FCompare/Views/SettingsWindow.cs` | 开关 + 「额外提前量」NumericUpDown + 状态行 + 「重新检测」；状态行 500ms 刷新；显示设备、profile、区间、`L_disp`、刷新率与实际下发值 |

实测（本机，2026-11-14）：

- 默认输出为显示器音频 `DM72u`（端点 GUID 略）→ 探测判非蓝牙，补偿 0；
- 强制探测已知 A2DP 端点（本机端点 GUID 略）→ `HUAWEI 蓝牙耳机（测试）`、
  `Service=BthA2dp`、`150–300 ms`、MAC `AA:BB:CC:DD:EE:FF`、`EndpointState=4`（未连接）→ 补偿 0；
- 144Hz 下连接后预期 `L_disp≈18.9 ms`、A2DP 中位 225ms ⇒ `d≈-206 ms`。

验证边界：

- `dotnet build src/3FCompare/3FCompare.csproj` 通过（仅既有 AVLN3001 警告）；
- `BluetoothLatencyPolicyTests` 9 条通过（含 60/144Hz、LE Audio、未知 profile、越界）；
- `LanguageManagerKeysTests` / `LocalizationTests` 12 条通过（中英两张表键集合一致）。

未完成/未验证：

- 设备当前未连接，无法实测真实 A2DP/HFP 切换与听感；`EndpointState=4` 时开关按设计不补偿。
- 当前 debug 输出里的 `FFF.Native.dll` 使用 `FFF3FPSnapshot` v23 / 608 B，而托管侧仍是
  v21 / 512 B，`FFF3FP_GetSnapshot` 因此每次返回 `InvalidArgument`，会话永远等不到
  `RouteReady`，所以**未完成实机开播听感验收**。这是内核/托管快照结构版本问题，
  不是本功能引入；需先同步快照结构（并重建内核）后复验。
- `SetAudioDelay` 是**有符号音频时间线偏移**，不是重新预缓冲：负值让音频内容提前，
  启动瞬间会跳过与提前量等长的开头音频；连续播放段内的音画同步是正确的。

---

## 7. 参考

- Microsoft Learn, *Audio Sideband A2DP Offload*（`configured-codec-SIOP`、`codec-latency-mode-SIOP`）
  https://learn.microsoft.com/windows-hardware/drivers/audio/audio-sideband-a2dp-offload
- Microsoft Learn, *SIDEBANDAUDIO_PARAMS_A2DP*（`CONFIGURED_CODEC` / `CODEC_LATENCY_MODE`）
  https://learn.microsoft.com/windows-hardware/drivers/ddi/a2dpsidebandaudio/ne-a2dpsidebandaudio
- Microsoft Learn, *IOCTL_SBAUD_GET_SIOP_UPDATE*（kernel mode only）
  https://learn.microsoft.com/windows-hardware/drivers/ddi/sidebandaudio/ni-sidebandaudio-ioctl_sbaud_get_siop_update
- Microsoft Learn, *Bluetooth Classic Audio*（A2DP codec 顺序、Win11 端点统一、HFP 触发条件）
  https://learn.microsoft.com/windows-hardware/drivers/bluetooth/bluetooth-classic-audio
- Microsoft Learn, *Bluetooth Low Energy Audio*（`BluetoothLEAudio_CodecConfiguration`）
  https://learn.microsoft.com/windows-hardware/drivers/bluetooth/bluetooth-low-energy-audio
- PresentMon README（`msUntilDisplayed` 定义）
  https://github.com/GameTechDev/PresentMon
- 仓库内：`3fp/tools/display_grid_probe.cpp`、`3fp/docs/106`、`VideoRenderer.cpp:8072`、
  `PlayerSession.cpp:6412`、`Api/FFF.Player.Api.h:245/312/433`
