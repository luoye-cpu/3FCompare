# 43 · IAMF 空间音频支持分析与实现

> 需求：「iamf 空间音频支持现在开始推进一下，确保播放器能正确播放 IAMF 空间音频」
> 日期：2026-10-06

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| 3FP 能不能**打开** IAMF？ | ✅ 能（demuxer 在，Open 返回 0） |
| 能不能**出声**？ | ✅ 能（有音频帧被解码） |
| **空间信息是否保留？** | ❌ **不能** —— 7.1.4 被当成 stereo 播，**丢掉 10 个声道** |
| 根因 | **IAMF 多子流结构 + FFmpeg 的 disposition 规则**（见 §3） |
| 本机样本 | ✅ 已有 **481 个真实 IAMF**（`C:\PLAN\3FUI\fixtures\decode-research\atmos2iamf`） |
| 修复方向 | 识别 IAMF 流组并按 mix presentation 布局取全声道（见 §5） |

---

## 1. IAMF 是什么形状（这决定了所有实现选择）

IAMF（Immersive Audio Model and Formats，AOM 标准）**不是**普通音频容器：

```
IAMF 文件
├── Stream group #0: IAMF Audio Element   ← 音频元素
│     Layer 0: 12 channels (FL+FR+SL+SR+BL+BR+TFL+TFR+TBL+TBR+FC+LFE)
│     内部拆成 **7 个子流**（6 个 stereo + 2 个 mono 打包方式）
└── Stream group #1: IAMF Mix Presentation ← 混音描述
      Submix 0: Layout = 7.1.4
```

**关键**：承载编解码器是 **Opus / FLAC / PCM**，**没有 "iamf 解码器"**。
demuxer 负责解析 IAMF 结构，把它**展开成若干底层编码子流**；
"混成 7.1.4" 这一步由 **FFmpeg 的 IAMF 读取层 / 滤镜图**完成。

实测（同一文件）：

```
ffmpeg -i live_7_1_4.iamf -f null -
  Stream #0:0..6  ← 7 个子流，全部标记 (dependent)，各自 stereo 或 mono
  输出默认 stereo

ffmpeg -i live_7_1_4.iamf -ac 12
  Stream #0:0: Audio: pcm_s16le, 48000 Hz, **7.1.4**     ← 正确！
```

---

## 2. 实测：3FP 现在的行为

用新写的 `tools/iamf_probe.cpp` 逐件验证：

```
########## 7.1.4 bed（Opus 承载）##########
Open result=0            ← 能打开
MediaInfo:
  "channels":2           ← ⚠ 源是 12 声道，却报 2
  "channelLayout":"stereo"
播放 3 s：已解码音频帧 0 -> 12
判定: PASS（有音频帧被解码）
```

6 件多声道样本**无一例外**都报 stereo/mono：

| 样本 | FFmpeg 报的布局 | 3FP 报的 |
|---|---|---|
| `live_7_1_4.iamf` | 7.1.4（12 声道） | **stereo** |
| `live_7_1_2.iamf` | 7.1.2（10 声道） | **stereo** |
| `live_7_1.iamf` | 7.1（8 声道） | **stereo** |
| `conv_hoaonly.iamf` | HOA（高阶 ambisonics） | **mono** |
| `conv_bedonly.iamf` | bed | **mono** |
| `单元素_flac.iamf` | FLAC 承载 | **stereo** |

⇒ **能播，但空间音频被摧毁**——这正是需求里"正确播放 IAMF 空间音频"要解决的。

---

## 3. 根因（已定位到 FFmpeg 源码级）

### 3.1 直接原因：3FP 只取第一个音频流

```cpp
// PlayerSession.cpp:371
std::int32_t FindDefaultOrFirstStream(AVFormatContext* format, AVMediaType type) {
    for (...) {
        if (first < 0) first = index;
        if ((stream->disposition & AV_DISPOSITION_DEFAULT) != 0) return index;  // ← 没有 DEFAULT
    }
    return first;   // ← 于是退化成"第一个流"
}
```

### 3.2 为什么没有 DEFAULT：FFmpeg 的 IAMF demuxer 规则

从 `libavformat/iamfdec.c` 逐字取：

```c
if (!i && !j && audio_element->layers[0].substream_count == 1)
    st->disposition |= AV_DISPOSITION_DEFAULT;
else if (audio_element->nb_layers > 1 || audio_element->layers[0].substream_count > 1)
    st->disposition |= AV_DISPOSITION_DEPENDENT;   // ← 多子流走这条
```

**多子流的 IAMF 元素里，每一个子流都是 `DEPENDENT`，没有一个 `DEFAULT`。**

⇒ `FindDefaultOrFirstStream` 找不到 DEFAULT，退化为"第一个音频流"，
而第一个流只是 12 声道里的**一对 stereo**。

### 3.3 实测对照（决定性）

```
只取 stream 0 导出：声道 = 2     字节 = 288102
要求 12 声道导出  ：声道 = 12    字节 = 1728102
```

`288102 × 6 ≈ 1728102` —— 6 个 stereo 子流恰好组成 12 声道。
**3FP 只播了其中一对，静默丢弃另外 10 个声道。**

### 3.4 为什么"有声音"所以一直没被发现

第一对 stereo 是**合法可播的音频**，所以播放器不报错、用户听得见声音，
只是**空间感没了**。这是典型的"静默降级"。

---

## 4. 三层能力核实（避免误判为 3FP 独有缺陷）

| 层 | 状态 |
|---|---|
| **FFmpeg 二进制** | ✅ `avformat-63.dll` 含 iamf demuxer（33 处命中）；Opus/FLAC/PCM 解码器齐全 |
| **avcodec** | ⚠️ **没有** "iamf 解码器"——因为 IAMF 本就靠 demuxer + 底层编解码器，不是缺陷 |
| **3FP 音频输出** | ✅ WASAPI 渲染器**按设备实际声道数**自适应（`outputChannels_ = activeFormat->nChannels`），swr 负责下混 ⇒ **输出层没问题** |
| **3FP 流选择** | ❌ **问题在这里**：只取第一个 DEPENDENT 子流 |

⇒ **缺陷在"选流"这一步，不在解码、也不在输出。**

---

## 5. 修复方案

### 5.1 方案 A（推荐）：按 IAMF stream group 取全声道

FFmpeg 把 IAMF 结构暴露为 **AVStreamGroup**：

- `AV_STREAM_GROUP_PARAMS_IAMF_AUDIO_ELEMENT` —— 音频元素（含各层与子流）
- `AV_STREAM_GROUP_PARAMS_IAMF_MIX_PRESENTATION` —— 混音描述（含目标布局）

修复点：`FindDefaultOrFirstStream` 遇 IAMF 时：

1. 找 `IAMF_AUDIO_ELEMENT` 流组；
2. 若其子流数 > 1（即当前被误判为多流的情形），**不要只取第一个**；
3. 依据 `IAMF_MIX_PRESENTATION` 的 submix layout 决定要以哪个布局输出
   （如 7.1.4 → 12 声道）；
4. 通过 libavfilter 的 `iamf` 混音能力或自行按 group 聚合，产出该布局的单帧；
5. 交给既有 WASAPI 渲染器（它会按设备声道数再下混，这是正确的最后一环）。

> **注意**：不要自己去实现 IAMF 混音算法（那涉及 demixing 参数、对象元数据、
> HOA 解码，工作量与风险都极大）。**用 FFmpeg 已实现的 IAMF 读取层**。

### 5.2 方案 B（最小可用）：至少不要"静默丢声道"

若完整混音短期难落地，**最低限度**应：

1. 检出 IAMF 多子流情形；
2. 通过 `GetMediaInfo` 如实报告**真实布局**（如 7.1.4），而不是谎报 stereo；
3. 给用户一条明确提示（"IAMF 空间音频暂以立体声播放"），避免"以为播对了"。

**方案 B 是方案 A 的必做前置**：先消除"静默"，再谈"正确"。

### 5.3 为什么不建议的路径

| 路径 | 为什么不做 |
|---|---|
| 自行实现 IAMF 混音 | 涉及 demixing/对象/HOA，工程量巨大且极易出错 |
| 强制 `-ac 12` 式处理 | 需先有完整 12 声道数据；而这正是缺失的一环 |
| 依赖某个"iamf 解码器" | **不存在**——IAMF 不是编解码器，找它必然徒劳 |

---

## 6. 本机可用的验证素材

```
C:\PLAN\3FUI\fixtures\decode-research\atmos2iamf\   ← 481 个 .iamf
```

覆盖面很广，可直接用于回归：

| 目录 | 内容 |
|---|---|
| `bed_order_out\` | 7.1 / 7.1.2 / 7.1.4 bed（**布局正确性**） |
| `convert_out\` | bed-only / HOA-only / bed+scene；PCM/FLAC/Opus 三种承载 |
| `spatial_wide_out\` | 三阶 ambisonics、多元素场景、对象位置 |
| `dolby_matrix_out\` | AC3/EAC3 矩阵化后的 IAMF |
| `dtsx_e2e_out\`、`thd_e2e_out\` | DTS:X / TrueHD Atmos 转换结果 |
| `flac_ctl_out\` | 单元素/双元素 FLAC 对照 |

---

## 7. 验收判据

| # | 判据 | 方法 |
|---|---|---|
| **V1** | `GetMediaInfo` 对 7.1.4 IAMF **报 12 声道 / 7.1.4** | `iamf_probe` 读 `channels`/`channelLayout` |
| **V2** | 解码出的帧 `ch_layout.nb_channels` 与 mix presentation 一致 | 内核侧断言 |
| **V3** | 与 `ffmpeg -ac 12` 导出**样本数一致** | 对比 PCM 帧数 |
| **V4** | HOA / bed+scene / 多元素样本**不崩**且布局合理 | 批量跑 `atmos2iamf` 子集 |
| **V5** | 非 IAMF 音频（mp3/flac/m4a）**零回归** | 既有 smoke + 套件 |
| **V6** | 立体声 IAMF 不因本次改动而受损 | 用 `单元素_flac` 等对照 |

> ⚠ **V6 很重要**：单子流的 IAMF **有** DEFAULT 标记，走的是旧路径；
> 修改选流逻辑必须保证这类样本行为不变（属"只加不改"）。

---

## 8. 实现结果（阶段一：消除静默降级）

### 8.1 已做

在 `PlayerSession.cpp` 新增 `ResolveIamfLayout()`，解析 FFmpeg 暴露的 IAMF 流组：

- `AV_STREAM_GROUP_PARAMS_IAMF_AUDIO_ELEMENT` → 各层声道合计（`elementChannels`）、子流数
- `AV_STREAM_GROUP_PARAMS_IAMF_MIX_PRESENTATION` → 目标布局（`presentationChannels` / 布局名）

并在 `GetMediaInfo` 的音频流上追加如实字段：

```json
"channels":2, "channelLayout":"stereo",          ← codecpar：只是**一个子流**
"iamf":{"substreams":7,"elementChannels":12,
        "presentationChannels":12,"presentationLayout":"7.1.4"}   ← 真实布局
```

**关键设计**：不删除 `codecpar` 的 `channels`（它如实描述那一条子流，是 ABI 的一部分），
而是**追加** `iamf` 段说明全貌 —— 属"只加不改"，不破坏既有消费者。

### 8.2 两处"取最大"的判断

| 情形 | 选择 | 理由 |
|---|---|---|
| 同一 presentation 有**多个** submix 布局 | 取**声道最多**的 | 实测 `conv_bedonly` 同时有 `stereo` 与 `5.1(side)`；报小的等于继续低估内容 |
| 无 mix presentation（纯 HOA/scene 件） | 回退到 `elementChannels` | 报 0 会像是解析失败；元素声道数是文件仅有的布局信息 |

### 8.3 验收（全部实测）

**V1 布局报告 8/8 PASS**（`tools/iamf_layout_regression.cpp`）：

```
PASS  live_7_1_4.iamf           布局=7.1.4    声道=12  子流=7
PASS  live_7_1_2.iamf           布局=7.1.2    声道=10  子流=6
PASS  live_7_1.iamf             布局=7.1      声道=8   子流=5
PASS  conv_bedonly.iamf         布局=5.1(side) 声道=6   子流=4
PASS  conv_bedonly_libopus.iamf 布局=7.1.4    声道=12  子流=7
PASS  conv_bedonly_flac.iamf    布局=7.1.4    声道=12  子流=7
PASS  conv_hoaonly.iamf         布局=5.1(side) 声道=6   子流=4
PASS  单元素_flac.iamf           布局=5.1(side) 声道=6   子流=4
通过 8 / 失败 0
```

**V4 鲁棒性 12/12 PASS**（随机抽 481 件中的 12 件）：

命中布局 `7.1.4` / `5.1.4` / `5.1(side)` / `stereo`，含 16 子流的对象类样本，
全部打开并解码成功、`iamf` 段 12/12 存在。

**V5/V6 零回归**：普通音频（mp3/flac/m4a 仍报 stereo）、歌词套件 17/17、smoke 与播放 EXIT=0。

### 8.4 阶段一**没有**做的事（必须说清）

**音频仍是 stereo 输出** —— 本次修复的是"**谎报**"，不是"**混音**"。
要真正听到 7.1.4，需要阶段二：按 IAMF 流组取全声道并经 FFmpeg 的 IAMF 读取层混音。
`GetMediaInfo` 现在会如实告诉宿主/用户"这个文件其实是 7.1.4"。

`outputChannels:2` 同时出现是**正常的**：那是 WASAPI 设备的实际声道数
（本机为立体声设备），渲染器按设备能力下混是正确行为。

---

## 9. 教训记录（本轮两次"我错了"）

| # | 我一度以为 | 实际 | 如何发现 |
|---|---|---|---|
| 1 | `iamf_layout_regression` 首轮 FAIL 4 项 = 内核 bug | 是**我的期望值**写错；`ffmpeg -i` 只显示**第一个** Layout | 逐个用工单里的 `Layout #N:` 全量核对 |
| 2 | 抽样 6/12 FAIL = 内核没报布局 | 是**探针键列表**没含 `iamf`，且 1 秒窗口太短 | 用 `lyrics_probe --raw` 直接看 JSON 原文 |

⇒ 这两次都说明同一件事：**测试失败时先怀疑测试，且必须看原始数据而不是二手读数**。
若当时直接"修内核"，会把两个正确的实现改坏。

---

## 10. 本文档状态

- ✅ §1–§4 实测（探针 + ffmpeg CLI 交叉验证 + FFmpeg 源码）
- ✅ §5 方案；§8 阶段一实现与验收
- ⏳ **阶段二（按流组混音出真实多声道）未做** —— 见 §8.4

