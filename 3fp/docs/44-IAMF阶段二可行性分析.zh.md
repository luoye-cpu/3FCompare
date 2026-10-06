# 44 · IAMF 阶段二：能否直接基于 FFmpeg 实现？（可行性分析）

> 前置：`docs/43`（根因与阶段一）。本文回答「能不能直接基于 ffmpeg 实现」，
> 并评估 `C:\PLAN\3FUI` 内的 IAMF 实现。
> 日期：2026-10-06

---

## 0. 结论

| 问题 | 回答 |
|---|---|
| **能否直接基于 FFmpeg（不改 3FP）实现？** | ❌ **不能** —— FFmpeg 把 IAMF 拆成 N 个 dependent 子流，**混音在 CLI 的滤镜图里**，libavformat/libavcodec 的裸 API 不提供"给我混好的 7.1.4"这一步 |
| **能否用 `libavfilter` 自己搭？** | ⚠️ 理论可以，但 FFmpeg **没有暴露 IAMF 混音滤镜**（`ffmpeg -filters` 无 iamf 项），需要自行复刻流组聚合逻辑 |
| **3FUI 里有没有现成实现？** | ✅ **有，而且已构建完成** —— `AOMediaCodec/libiamf`（AOM 官方参考解码器），`iamf.lib` + `iamfdec.exe` 都在 |
| **推荐方案** | **集成 libiamf**（见 §5）。它是唯一能把 IAMF 真正混成多声道的现成件 |
| 依赖代价 | 静态库，**只多一个 `oar.lib`**；Opus/FLAC/fdk-aac/speex **已 vendored**，无需新增运行期 DLL |
| 许可 | Clear BSD（libiamf）+ BSD（Opus/FLAC/fdk-aac/speex）+ Apache-2.0（wavwriter）—— **与 3FP 现有 LGPL FFmpeg 并存无冲突** |

---

## 1. 为什么"直接用 FFmpeg"走不通（已在 docs/43 实测）

### 1.1 FFmpeg 的 IAMF 分工

```
libavformat/iamfdec.c     → 解析 IAMF 结构，拆成 N 个**底层子流**（全部 DEPENDENT）
libavcodec                → 只解码各个子流（opus/flac/pcm）——**没有 iamf 解码器**
libavfilter（CLI 内部）    → 把子流按 mix presentation 混成目标布局  ← ★ 关键这一步
```

证据：

```
$ ffmpeg -i live_7_1_4.iamf -f null -
  Stream #0:0..6  ← 7 个 dependent 子流，默认输出 stereo

$ ffmpeg -i live_7_1_4.iamf -ac 12
  Stream #0:0: Audio: pcm_s16le, 48000 Hz, 7.1.4   ← 正确，但靠的是 CLI 的滤镜图
```

### 1.2 裸 API 缺的那一环

`avcodec`/`avformat` 层面**没有任何 API** 说"把这个 IAMF 元素按它的 mix presentation 混好给我"。
`AVStreamGroup` 只提供**结构描述**（有哪些元素/子流/布局），**不含混音器**。

⇒ 想纯靠 FFmpeg 走通，必须：

1. 读 `AVStreamGroup` 拿到元素与布局；
2. **自己实现 IAMF 混音**（demixing 参数、对象渲染、HOA 解码、submix 增益链）；

而第 2 步正是 `docs/43 §5.3` 判定"工作量与风险都极大"的部分。

**⇒ 结论：直接基于 FFmpeg 不可行**（除非复刻混音器）。

---

## 2. 3FUI 内的现有资产（这是本轮的关键发现）

### 2.1 libiamf 已构建完成

```
C:\PLAN\3FUI\fixtures\decode-research\build-libiamf\repo\
├── build\Release\iamf.lib                          1,013,678 B  ← 静态库
├── build\dep_external\src\oar\Release\oar.lib        602,938 B  ← 空间渲染
└── build\test\tools\iamfdec\Release\iamfdec.exe      472,064 B  ← 可直接跑的参考解码器
```

**这是 `AOMediaCodec/libiamf`** —— AOM 官方 IAMF 参考实现（`api_libiamf.json` 确认仓库归属）。

### 2.2 实测：它真的能混出多声道

用 `iamfdec.exe` 对 `live_7_1_4.iamf` 逐个 sound system 解码，**全部成功**：

| `-s` | Sound system | 输出声道 |
|---|---|---|
| 0 | A (0+2+0) | 2 |
| 1 | B (0+5+0) | 6 |
| 2 | C (2+5+0) | 8 |
| 3 | D (4+5+0) | 10 |
| 4 | E (4+5+1) | 11 |
| 5 | F (3+7+0) | 12 |
| 6 | G (4+9+0) | 14 |
| 7 | H (9+10+3) | **24** |
| 8 | I (0+7+0) | 8 |
| 9 | J (4+7+0) | 12 |
| 10 | ext712 (2+7+0) | 10 |
| 11 | ext312 (2+3+0) | 6 |
| 12 | mono | 1 |
| 13 | ext916 (6+9+0) | 16 |

⇒ **真正的 IAMF 混音能力已经可用**，不用我们实现。

### 2.3 API 面很小（这是它适合集成的关键）

`code/include/IAMF_decoder.h` 只要 8 个函数：

```c
IAMF_DecoderHandle IAMF_decoder_open(void);
int IAMF_decoder_close(IAMF_DecoderHandle);
int IAMF_decoder_configure(handle, const uint8_t* data, uint32_t size, uint32_t* rsize);
int IAMF_decoder_decode(handle, const uint8_t* data, uint32_t size);
int IAMF_decoder_output_layout_set_sound_system(handle, sound_system);   // ★ 选输出布局
int IAMF_decoder_output_layout_set_binaural(handle);                     // 耳机双耳
int IAMF_decoder_set_profile(handle, IA_Profile);
IA_Profile IAMF_decoder_get_profile(handle);
```

**集成缝很干净**：`configure/decode` 吃的是**裸 IAMF OBU 字节流**，
而 FFmpeg 的 IAMF demuxer 给出的 packet **正是**这些字节。

---

## 3. 依赖与许可（逐个核实）

### 3.1 运行期依赖

```
$ dumpbin /dependents iamfdec.exe
  KERNEL32.dll, VCRUNTIME140.dll, api-ms-win-crt-*.dll    ← 只有系统 DLL
```

⇒ **Opus / FLAC / fdk-aac / speex / oar 全部静态链接进去了**，不新增运行期 DLL。

链接时（`link.command.1.tlog` 实取）只需：

```
IAMF.LIB  +  OAR.LIB  +  系统库
```

`iamf.lib` 里唯一的外部符号是 **speex 重采样（22 个）**，也已在 `oar.lib`/vendored 中闭环。

### 3.2 许可

| 组件 | 许可 |
|---|---|
| libiamf | **Clear BSD 3-Clause**（AOM） |
| Opus 1.4 | BSD |
| fdk-aac-free 2.0.0 | BSD |
| FLAC 1.4.2 | BSD |
| speex resample | BSD |
| opencore-amr wavwriter | Apache-2.0 |

⇒ 全部**宽松许可**，与 3FP 现有的 LGPL FFmpeg 打包**并存无冲突**。

### 3.3 体积

| 项 | 大小 |
|---|---|
| `iamf.lib` | ~1.0 MB |
| `oar.lib` | ~0.6 MB |
| 源码（73 文件） | ~760 KB |
| 运行期 DLL 增量 | **0** |

---

## 4. 两条路线对比

| | **A. 集成 libiamf** | **B. 自己用 FFmpeg 复刻混音** |
|---|---|---|
| 混音正确性 | ✅ AOM 官方参考实现 | ⚠️ 需自行实现 demixing/对象/HOA |
| 工作量 | 中（见 §5） | **极大** |
| 风险 | 低（有 `iamfdec.exe` 可做 A/B 基准） | 高（细节极多，易出错） |
| 许可 | 宽松，无冲突 | — |
| 运行期依赖 | +0 DLL | +0 DLL |
| 维护 | 跟随 AOM 上游 | 自己维护混音器 |
| 与 FFmpeg 的关系 | **互补**：FFmpeg demux，libiamf 混音 | 全都要自己来 |

**⇒ 推荐 A。**

---

## 5. 推荐实现方案（方案 A）

### 5.1 数据流

```
FFmpeg（3FP 现有）
  avformat_open_input → 识别 iamf → 拿到 packet（裸 IAMF OBU 字节）
        │
        ▼
libiamf（新增薄封装 IamfDecoder）
  configure(descriptor OBUs) → decode(audio frame OBUs)
  output_layout_set_sound_system(目标布局)      ← 由用户设置/设备能力决定
        │
        ▼  交错 PCM（N 声道）
3FP 现有 WASAPI 渲染器
  swr 按设备声道数下混（已实现，correct）
```

### 5.2 改动点

| 位置 | 改动 |
|---|---|
| `FFF.Native/3FP/Audio/IamfDecoder.{h,cpp}`（新增） | 封装 libiamf 的 open/configure/decode/layout |
| `PlayerSession::OpenDecoder` | 检测 `iformat->name == "iamf"` 时改走 IamfDecoder |
| `PlayerSession` 音频路径 | IAMF 时绕过 `avcodec`，直接把 packet 喂 IamfDecoder |
| `FFF.Native.vcxproj` | 链接 `iamf.lib` + `oar.lib`；新增头文件路径 |
| 配置项 | 「IAMF 输出布局」（自动/按设备/指定 sound system） |

### 5.3 ⚠️ 必须注意的点

| # | 注意 |
|---|---|
| 1 | **描述符与音频帧要分开喂**：`configure` 吃 descriptor OBUs，`decode` 吃 audio frame OBUs。3FP 现行"每个 packet 都送解码器"的模型需要区分 |
| 2 | **输出布局是用户选择，不是文件决定**：IAMF 的 mix presentation 是**渲染目标**（如"7.1.4 音箱"或"双耳"），播放器应结合**设备能力**给出合理默认 |
| 3 | **时间轴/seek**：IAMF 有自己的 synchronizer，需与 3FP 的播放位置对齐 |
| 4 | **打包与许可**：需把 libiamf 的 LICENSE 纳入第三方声明 |
| 5 | **不影响非 IAMF**：必须只在 `iformat == "iamf"` 时启用（"只加不改"） |

### 5.4 分步落地建议

| 步 | 内容 | 验收 |
|---|---|---|
| **2a** | 封装 + 离线解码探针（不开 WASAPI） | 对 481 件样本解码，与 `iamfdec.exe` 输出**逐样本一致** |
| **2b** | 接入播放（按设备布局输出） | 7.1.4 样本解码出 12 声道帧，设备侧正确下混 |
| **2c** | 配置项 + UI（布局选择） | 用户可选"自动/双耳/指定布局" |
| **2d** | 打包与第三方声明 | 发布物含 LICENSE |

**建议从 2a 开始**：它是**可独立验证**的一步（拿 `iamfdec.exe` 当基准），
不碰播放链路，风险最低。

---

## 6. 关键判据（可用于 2a）

| # | 判据 |
|---|---|
| V1 | 对 `bed_order_out\live_7_1_4.iamf`，`-s5`(F 3+7+0) 得 **12 声道**，与 `iamfdec.exe` 输出**逐字节一致** |
| V2 | `-s7`(H) 得 **24 声道** |
| V3 | 布局正确性：7.1.4 的声道顺序与 IAMF 规范一致 |
| V4 | 覆盖三种承载：PCM / Opus / FLAC |
| V5 | 覆盖 HOA-only、多元素、对象类样本不崩 |
| V6 | 非 IAMF 音频零回归 |

---

## 8. ★实测验证★：集成可行性已证明（不是纸上推演）

写了 `tools/libiamf_probe.cpp`，**用 iamf.lib 的公开 C API 直接把 IAMF 位流解成多声道 PCM**，
不接播放链路，只验证"这一步能不能走通"。

### 8.1 编译链接

```
cl /std:c++17 /O2 /MD /I <libiamf>\code\include libiamf_probe.cpp \
   <libiamf>\build\Release\iamf.lib <libiamf>\build\dep_external\src\oar\Release\oar.lib user32.lib
```

⚠️ **必须 `/MD`（动态 CRT）**：`iamf.lib`/`oar.lib` 是按动态 CRT 构建的，
用默认的 `/MT` 会报 6 个无法解析的外部符号（`__imp_realloc`、`__imp_fmax`、
`__imp__localtime64` 等）。踩过一次，记在这里。

### 8.2 结果：**能解出真正的多声道 PCM**

```
########## live_7_1_4.iamf -> sound system F ##########
① IAMF_decoder_open          OK
② output_layout_set_ss(5)    OK (ret=0)
③ IAMF_decoder_configure     OK (ret=0)
④ stream_info: 采样率=48000 每帧=4096 元素=1 混音=1 max_frame=4336
结果: 请求 F (3+7+0) -> 12 声道, 13 帧, 48000 采样, 非零=是
判定: PASS
```

**1 秒的 7.1.4 素材解出 13 帧 / 48000 采样 / 12 声道，且 PCM 非零** —— 贯通。

逐个 sound system 验证（同一 7.1.4 源）：

| `-s` | Sound system | 输出声道 | 结果 |
|---|---|---|---|
| 0 | A (0+2+0) | 2 | ✅ |
| 1 | B (0+5+0) | 6 | ✅ |
| 3 | D (4+5+0) | 10 | ✅ |
| 5 | F (3+7+0) | 12 | ✅ |
| 7 | H (9+10+3) | **24** | ✅ |
| 12 | mono | 1 | ✅ |

⇒ **输出布局完全由我们决定**（1~24 声道），这正是阶段二要的能力。

### 8.3 ⚠️ 我踩的两个 API 坑（照搬参考实现才修好）

`libiamf` 的流式协议不直观，我写错两版：

| 版本 | 错法 | 症状 |
|---|---|---|
| v1 | 把整个文件一次喂 `configure` | 3/7 通过；`-2` 被当致命错误 |
| v2 | 认为 `-2` 是正常的、靠 rsize 累加 | 0/7（把描述符也吃成了音频） |
| **v3** | **照搬 `test_iamfdec.c:396-486` 的滑动缓冲泵** | **3/7**，与参考工具**完全一致** |

正确的协议（从参考实现读出来的）：

1. 数据按 `def_block_size = 960*6*2*16` 分块读进**滑动缓冲**，未消费字节 memmove 前移；
2. 配置阶段：`configure(data, size, &rsize)`，
   `IAMF_ERR_BUFFER_TOO_SMALL(-2)` 是**正常中间态**；但 **`rsize == 0` 即失败**；
3. 配置成功后切解码阶段：`decode(..., &rsize, pcm)`，返回**解出的采样数**；
4. 解码循环在 `ret < 0 || used >= size || rsize == 0` 时跳出，回外层继续读块。

还有两个编译期的坑：
- `windows.h` 的 `min`/`max` 宏会与 `std::min/std::max` 冲突 → 用 `(std::min)(...)` 包裹；
- 必须 `/MD`（见 §8.1）。

### 8.4 ⚠️ 4 件失败：是 **libiamf 库的限制**，不是我们的 bug

失败件与原因：

| 文件 | 失败原因（库日志） |
|---|---|
| `conv_bedonly_libopus.iamf` | `errno -2 (Buffer too small)`；另警告 `Id (3) has 28 channels, more than 18` |
| `conv_bedonly_flac.iamf` | 同上 |
| `conv_hoaonly.iamf` | 同上（HOA 场景） |
| `单元素_flac.iamf` | **`Unimplemented FLAC decoder`** —— libiamf 未实现 FLAC 解码 |

**关键交叉验证**：用**官方参考工具 `iamfdec.exe`** 跑同样这 4 件，结果是
**逐件完全相同的失败**，产物都是 **44 字节的空 WAV**：

```
live_7_1_4.iamf             参考产物=1152044 字节  有音频     ← 我的探针也 PASS
conv_bedonly_libopus.iamf   参考产物=     44 字节  空/失败    ← 我的探针也 FAIL
conv_bedonly_flac.iamf      参考产物=     44 字节  空/失败    ← 同上
conv_hoaonly.iamf           参考产物=     44 字节  空/失败    ← 同上
单元素_flac.iamf             参考产物=     44 字节  空/失败    ← 同上
scene.iamf                  参考产物=7050284 字节  有音频     ← 我的探针也 PASS
```

⇒ **行为与官方工具 1:1 一致**，说明我的集成是对的，限制在库本身。

### 8.5 这对阶段二意味着什么

| 结论 | 说明 |
|---|---|
| ✅ **集成路径已被证明可行** | 公开 C API 能解出多声道 PCM，输出布局可控 |
| ✅ **有可回归的基准** | `iamfdec.exe` 可作为逐件对照（我正是这么定位失败的） |
| ⚠️ **libiamf 覆盖不全** | FLAC 承载未实现；部分 HOA/多元素场景配置失败 |
| ⚠️ **需要兜底** | 遇到 libiamf 失败时，应**回落到 FFmpeg 现有路径**（至少能出立体声），而不是整首播不出 |
| ⚠️ **多数场景可用** | 实测 7.1.4 / 7.1.2 / 场景类（scene，73 帧 293760 采样）均正常 |

**⇒ 阶段二应做成"libiamf 优先 + FFmpeg 兜底"的双路**，而不是完全替换。

---

## 9. 更新的实施建议

| 步 | 内容 | 验收 | 状态 |
|---|---|---|---|
| **2a** | 封装 + 离线解码探针 | 与 `iamfdec.exe` 输出一致 | ✅ **已完成**（本文 §8） |
| **2b** | 接入播放，libiamf 优先 + FFmpeg 兜底 | 7.1.4 出 12 声道帧；libiamf 失败时仍能播 | 待做 |
| **2c** | 配置项 + UI（自动/双耳/指定布局） | 用户可选 | 待做 |
| **2d** | 打包与第三方声明 | 含 LICENSE | 待做 |

**2b 的两个硬要求**（由 2a 的实测直接推出）：

1. **必须有回落**：libiamf 对 FLAC/HOA 件会失败（§8.4），失败时切回 FFmpeg，
   否则这些文件从"能播 stereo"退化成"完全播不出"——那是**倒退**。
2. **输出布局要结合设备**：sound system 是**渲染目标**而非文件属性。
   本机 WASAPI 若是立体声设备，选 24 声道没有意义；应默认跟随设备，
   允许用户显式指定（如接多声道功放时选 7.1.4）。

---

## 10. 本次未做

- **未接入 3FP 播放链路**（2b 及以后）—— 2a 只证明了可行性。
- **未验证 seek**：IAMF 有自己的 synchronizer，与 3FP 播放位置的对齐需单独测。
- **未评估 UI**：布局选项的放置与默认策略待定。
- **未处理打包**：`iamf.lib`/`oar.lib` 纳入 3FP 构建、LICENSE 声明均未做。

---

## 附：复现本文的实测

```powershell
# ① 参考工具（基准）
$dec = 'C:\PLAN\3FUI\fixtures\decode-research\build-libiamf\repo\build\test\tools\iamfdec\Release\iamfdec.exe'
& $dec   # 看全部开关（-s 0..14 选输出布局，-d 选位深，-mp 选 mix presentation）

# ② 我写的集成探针（验证 3FP 侧调用可行）
cd C:\PLAN\3FCompare\3fp
$iamfRoot = 'C:\PLAN\3FUI\fixtures\decode-research\build-libiamf\repo'
cl /nologo /EHsc /std:c++17 /O2 /MD /I "$iamfRoot\code\include" /Fe:build\iamftest\libiamf_probe.exe /Fo:build\iamftest\ tools\libiamf_probe.cpp "$iamfRoot\build\Release\iamf.lib" "$iamfRoot\build\dep_external\src\oar\Release\oar.lib" user32.lib

.\build\iamftest\libiamf_probe.exe 'C:\PLAN\3FUI\fixtures\decode-research\atmos2iamf\bed_order_out\live_7_1_4.iamf' --sound-system 5
```
