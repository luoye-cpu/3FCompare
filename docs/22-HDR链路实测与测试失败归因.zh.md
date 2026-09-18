# 22 · HDR 链路实测与「HDR 测试失败」真实归因（2026-09-18）

对象：PR `upstream/pr-b-adapter`（API 14→15 + `preferredAdapterIndex`），基于上游 `ea3ce05`。
上游播放器环境：`.review_pr/player_env/`（LakeUI + `FFF.Player` + `FFF.Player.Tests`）。

---

## 结论（先说结果）

**1. 本机确实具备完整 HDR 链路。此前"本机 Windows HDR 未开"的判定是错误的，予以撤回。**
**2. HDR 链路实测通过**——`--hdr-switch-regression` 的第一阶段（真正的 HDR 切换验证）全部通过。
**3. 两项 HDR 测试失败，原因都与 HDR 无关、与 PR 无关**：

| 测试 | 真实原因 | 性质 |
|---|---|---|
| `--hdr-switch-regression` | 第二阶段依赖 VB 应用框架注入命令行文件，测试 EXE 入口下永不触发 ⇒ 会话从未创建 ⇒ 15s 超时 | **上游测试设计缺陷** |
| `--color-regression` | 测试硬编码期望 HDR 源峰值 = **1242 nit**，本机素材实测 **1000 nit** | **素材常量不匹配** |

⇒ **不构成 PR 阻塞项。**

---

## 一、本机 HDR 能力实测（推翻旧结论）

工具：`.review_pr/hdrprobe/hdrprobe.exe`（自写，直接复现内核 `OutputSupportsHdr()` 判据）。

```
输出[0] \\.\DISPLAY5（主显示器，NVIDIA GeForce RTX 5080）
  BitsPerColor   = 10                             ✅ 内核要求 >=10
  ColorSpace     = RGB_FULL_G2084_NONE_P2020 (PQ) ✅ 内核要求 G2084/P2020
  MinLuminance   = 0.0000 nits
  MaxLuminance   = 417.7118 nits
  MaxFullFrameLum= 417.7118 nits
  ==> OutputSupportsHdr() 判据 = TRUE   ← 内核会走 HDR
```

关键点：`DXGI_OUTPUT_DESC1.ColorSpace` 只有在 Windows 设置中**真正开启"使用 HDR"**后才会变为
`G2084/P2020`（PQ）。它等于 PQ 本身就是"HDR 已开启"的直接证据。

其他适配器（非主显示器路径）：RTX 4060 Laptop ×2、Intel UHD、Microsoft Basic Render。

### 内核侧判据对照

`OutputSupportsHdr()` 位于 `VideoRenderer.cpp:2027-2114`，判据正是
`IDXGIOutput6::GetDesc1` 的 `BitsPerColor >= 10 && ColorSpace ∈ {RGB_FULL_G2084_NONE_P2020, RGB_STUDIO_G2084_NONE_P2020}`
（`:2071-2084`），按 HMONITOR 缓存、有效期 750ms。
⇒ **两项均满足，内核不应降级到 SDR。**

---

## 二、HDR 链路实测通过（正面证据）

`--hdr-switch-regression` 第一阶段 `测试播放中HDR交换链切换`（`Program.vb:1370`）**全部通过**：

| 断言 | 结果 |
|---|---|
| `会话.当前快照.是HDR源`（素材识别为 HDR10） | ✅ |
| 切 `峰值映射HDR` 后 `实际色彩模式 = 峰值映射HDR` 且 `视频输出位深度 >= 10` 且 Present/帧递增 | ✅ |
| 2 秒持续呈现：Present +149（要求 ≥60）、视频帧 +127（要求 ≥24） | ✅ |
| 字幕/弹幕/信息图层在 HDR 下持续合成（≥60 图层呈现） | ✅ |
| 切回 `映射到SDR` 后继续呈现 | ✅ |

实测输出：
```
HDR 切换：视频帧 3→129→130，交换链 Present 4→153→155。
```

⇒ **在本机开启 HDR 的显示器上，完成了 SDR→HDR 切换、稳定呈现、再切回 SDR 的全流程。**

> ⚠️ **2026-09-18 更正**：本次实测所用内核经核实为**基线 v14**（`FFF3FP_GetApiVersion()` = 14，
> 750,592 B = `.review_pr/out_v14/`），**不是 PR 内核 v15**（756,736 B = `.review_pr/out_pr/`）。
> 隔离播放器环境当时停在 `base` 态（`toggle_player.py` 上一轮 A/B 后未切回 `pr`）。
> ⇒ 上述结论应表述为「**本机 HDR 链路可用、HDR 切换实测通过**」，**不能**说成"PR 内核已验证 HDR"。
> 不过这不影响本文的核心结论：两项失败的原因分别是上游测试缺陷与素材常量不匹配，
> **均与 HDR 能力无关**；且本 PR 未改动任何 HDR 逻辑（未触碰 `OutputSupportsHdr`/位深选择/色彩管线），
> 故 HDR 行为在 v14 与 v15 之间**不存在差异路径**。
> 若要在 PR 中声称"HDR 已在 PR 内核上验证"，需把 `out_pr` 内核 + `toggle_player.py pr` 重新部署后复跑。

---

## 三、`--hdr-switch-regression` 失败归因：上游测试缺陷

### 现象

```
测试失败：等待完整播放器打开 HDR 样本超时：状态 ，色彩 /，视频帧/Present /。
```

三个字段**全部为空**。

### 证据链

1. **空字段 ⇒ 快照为 `Nothing`**
   `等待控制器快照` 打印的是 `快照?.状态` / `快照?.请求色彩模式` / `快照?.已呈现视频帧数`。
   只要快照非 Nothing，状态至少会打印"打开中/就绪/正在播放"等字样。全空 ⇒ **`快照 Is Nothing`**。

2. **`安全读取快照()` 返回 Nothing ⇒ 会话不存在**
   `FFF.Player/Core/播放器控制器.vb:235`：
   ```vb
   Public Function 安全读取快照() As 播放器快照
       Try
           Return 会话?.当前快照     ' 会话 为 Nothing 时整体返回 Nothing
   ```
   ⇒ **`会话` 从未创建，媒体从未打开**。

3. **已排除"核心文件缺失"**
   `Form1_Load`（`Form1.vb:57`）若核心文件缺失，会在 `:85` 提前 `Return`，
   则 `画面控件`（`:86`）与 `播放控制器`（`:88`）均为 Nothing，
   测试断言（`Program.vb:1478`）会抛 **"无法取得完整播放器 HDR 回归所需的控制器、信息图层或画面控件。"**
   实际并未抛出 ⇒ `核心文件检查通过 = True`，DLL 依赖齐全。

4. **Form1 拿不到媒体路径（根因）**
   `Form1_Shown`（`Form1.vb:307`）：
   ```vb
   Dim 启动文件 = My.Application.取出待处理启动文件()
   Dim 请求文件 = If(String.IsNullOrEmpty(待打开外部文件), 启动文件, 待打开外部文件)
   If Not String.IsNullOrEmpty(请求文件) Then BeginInvoke(Sub() 打开外部文件(请求文件))
   ```
   两个来源都是空的。其中 `取出待处理启动文件()` 的值只在
   `ApplicationEvents.vb:32 MyApplication_Startup → 记录初次启动命令行(e.CommandLine)` 中赋值。

   **而该事件只在 VB 应用程序框架启动时触发。** 测试的入口是 `FFF.Player.Tests.exe`，
   FFF.Player 的 `MyApplication.Run()` 从未执行 ⇒ `待处理启动文件` 恒为空。

   佐证：测试工程里唯一出现 `记录初次启动命令行` 的地方是 `Program.vb:564`，
   那是**针对该函数自身的单元测试**（`testApplication.记录初次启动命令行({"不存在的文件", executablePath})`），
   并没有为 HDR 测试向 `My.Application` 注入路径。

### 定性

- 失败发生在**打开媒体之前**，与色彩模式、HDR 能力、交换链**完全无关**。
- 该缺陷只取决于**进程入口**，与内核版本无关 ⇒ 在 PR 内核与基线 v14 上**同样必然失败**（与 `docs/21` §七记录的"两版都超时"一致）。
- 上游若要修，应让测试把媒体路径**直接传给 Form1**（如新增 `窗口.打开命令行文件({视频路径})`），
  而不是依赖 VB 应用框架。

---

## 四、`--color-regression` 失败归因：素材常量不匹配

实测输出：
```
BT.2390：knee 24.40 nit；暗部 12.20 nit 保持；源峰值 1242 nit → SDR 1.0000。
测试失败：HDR 源没有采用 MaxCLL 1242 nit：1000。
```

- 断言位于 `Program.vb:4272`：`HDR源峰值 = 1242UI`，失败信息为
  `"HDR 源没有采用 MaxCLL 1242 nit：{峰值}"`。
- **1242 nit 是上游自有样本的固有值**，被硬编码进测试。本机素材 `real_4k_hevc_hdr10_60m.mp4`
  实测为 **1000 nit**（内核在无 MaxCLL 元数据时亦按母版/回退 1000 nit），必然不相等。
- 该测试**只检查 `请求色彩模式` 与 `控制器.色彩模式` 以及源峰值，不检查 `实际色彩模式`**
  ⇒ **完全不需要真实 HDR 显示器**。此前把它归为"环境限制"同样是错的。

⇒ 换用任何非上游原样本的 HDR 片，该测试都会失败。

---

## 五、对 PR 提交的影响

1. **`docs/21` §七「HDR 两项失败是环境限制（本机 Windows HDR 未开）」予以撤回。** 该结论错误。
2. PR 描述中**可以更正面的表述**：HDR 切换已在开启 HDR 的显示器上实测通过
   （SDR→HDR→SDR 全流程，含位深度 ≥10 与持续呈现）。
3. **不建议**声称覆盖了 `--hdr-switch-regression` / `--color-regression` 两项。
   若与上游沟通，可补充说明：
   - `--hdr-switch-regression` 第二阶段在 `FFF.Player.Tests.exe` 入口下必然超时（路径注入机制失效）；
   - `--color-regression` 的 1242 nit 应改为可配置或随样本提供。
4. 本轮**未重新构建基线 v14 内核做同批次 A/B**。理由：两条失败原因均由代码结构决定、
   与内核版本无关（进程入口问题 / 常量不匹配），且 `docs/21` 已记录两版失败信息逐字一致。
5. ⚠️ 复核测试环境内核版本的方法（便宜且可靠，建议每次跑测试前都做）：
   ```bash
   python -c "import ctypes;print(ctypes.CDLL(r'<测试bin>/FFF.Native.dll').FFF3FP_GetApiVersion())"
   ```
   并与 `toggle_player.py` 的 pr/base 状态（`grep -c 15UI 播放器会话.vb`）**一起核对**——
   两者必须匹配，否则测的既不是纯 PR 也不是纯基线。

---

## 附：复现命令

```bash
# 1) HDR 能力探测（不创建窗口）
.review_pr/hdrprobe/hdrprobe.exe
# 附加 swapchain 参数可真实创建 R16G16B16A16_FLOAT 交换链并查询 PQ/scRGB 支持
.review_pr/hdrprobe/hdrprobe.exe swapchain

# 2) HDR 切换回归（第一阶段通过，第二阶段超时）
cd .review_pr/player_env/fff/FFF.Player.Tests/bin/Release/net10.0-windows10.0.26100.0
./FFF.Player.Tests.exe --hdr-switch-regression "C:/PLAN/3FCompare/testmedia/media/real/real_4k_hevc_hdr10_60m.mp4"

# 3) 色彩回归（MaxCLL 常量不匹配）
./FFF.Player.Tests.exe --color-regression \
  "C:/PLAN/3FCompare/testmedia/media/real/real_4k_h264_60m.mp4" \
  "C:/PLAN/3FCompare/testmedia/media/real/real_4k_hevc_hdr10_60m.mp4"
```

### 探针编译要点

- `cl.exe` 只认 Windows 风格路径，MSYS 的 `/c/...` 会被当成选项。
- `DISPLAYCONFIG_ADVANCED_COLOR_INFO` **不在 Windows SDK 头文件中**，需自行声明
  （`DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9`）。
- `CheckColorSpaceSupport` 是 `IDXGISwapChain3/4` 的方法，**不是** `IDXGIOutput6` 的。
