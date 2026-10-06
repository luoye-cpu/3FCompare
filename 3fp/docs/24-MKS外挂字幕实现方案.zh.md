# MKS 外挂字幕实现方案（2026-09-26）

> 前置分析见 `docs/23`（现状、实测、缺口）。本文只讲**怎么做**：改哪些文件、什么形状、怎么验收。
> 所有"必须做"的结论都有实测支撑；标注 🔴 的是实测撞出来的坑。

---

## 0. 一句话方案

**内核加一个「按路径探测字幕流」的 API + 修掉「容器内字体附件不加载」的缺陷；宿主把 `.mks` 加进白名单，按探测结果走「容器 + 流索引」分派（复用现成的内嵌字幕那条路）。**

数据流：

```
拖入/同名扫描 .mks
   → 宿主: 外部字幕自动加载器（白名单命中 .mks）
   → 内核: FFF3FP_ProbeSubtitleStreams(路径)        ← 新增，拿 [{index, codec, language, title}]
   → 宿主: 选轨（默认 = default/forced 或第一条；用户在字幕菜单里按语言切）
   → 内核: 位图编码(PGS/DVB/XSUB) → FFF3FP_OpenBitmapSubtitle(路径, 流索引)
           其余                   → FFF3FP_OpenAssSubtitle(路径, 字体目录, 流索引)
```

**解码/渲染/时间轴内核都不用动**（`docs/23` §4 已实测：ass / subrip / PGS 全部能解能画）。

---

## 1. 内核改动（两项）

### 1.1 新增 `FFF3FP_ProbeSubtitleStreams`（必需）

`FFF.Native/3FP/Api/FFF.Player.Api.h`（挨着 `FFF3FP_GetMediaInfo` 放）：

```cpp
// 按路径探测容器内的字幕流，不需要打开播放会话。
// outputUtf8 是与 FFF3FP_GetMediaInfo 的 streams 数组**同构**的 JSON（见下）。
FFF3FP_API FFFResult FFF3FP_ProbeSubtitleStreams(const char* localPathUtf8,
    char* outputUtf8, std::uint32_t outputSize, std::uint32_t* requiredSize) noexcept;
```

**为什么用 JSON 而不是结构体**：宿主 `媒体流信息`（`FFF.Player/Core/播放模型.vb:472`）已经用
`<JsonPropertyName("index"|"type"|"codec"|"default"|"forced"|"metadata"…)>` 反序列化这一套，
**同构输出可以直接复用 `媒体流信息` 与现有解析**，顺带白拿 `metadata` 字典里的 `language`。
（实测：官方 7 轨样本的语言标签 `chi/eng/fre/ger/ita/jpn/spa` 就在流 metadata 里。）

实现要点：
- 放新文件 `3FP/Subtitle/SubtitleProbe.{h,cpp}`；导出在 `PlayerApi.cpp` 加一行转发（同 `GetMediaInfo` 的形状，用 `CopyUtf8`）
- 复用 `SharedFileInput::Open` + `avformat_open_input` + `avformat_find_stream_info`（**不看扩展名**）
- 只收 `codec_type == AVMEDIA_TYPE_SUBTITLE` 的流；每流输出 `index/codec/codecLongName/default/forced/metadata`
- 失败语义：文件打不开 → `FfmpegFailure(-5)`；无字幕流 → `Success` 但输出空数组（**不要**返回错误，宿主好判断）
- ⚠ 必须 `noexcept` 且不抛（与其它导出一致）

### 1.2 修掉「容器内字体附件不加载」（必需，否则动漫字幕字体全错）

🔴 **实测证据**（`docs/23` §4b / 本轮对照实验，三组同批）：

| 场景 | 渲染结果 |
|---|---|
| ASS 引用 `Coolvetica`，MKS **无**字体附件 | `368x61`, opaque `8083`（回退 Arial） |
| ASS 引用 `Coolvetica`，MKS **自带**该字体附件 | `368x61`, opaque `8083` **← 与上一行逐位相同** |
| 同一 ASS + **外部字体目录** `--fontdir fonts\` | `330x59`, opaque `8261` **← 明显不同，字体生效** |

⇒ 内核**只认 `fontDirectories` 外部目录**，完全不读容器里的 attachment。
`AssSubtitleRenderer::AddMediaFonts()`（`AssSubtitleRenderer.cpp:534`）只遍历 `fontDirectories_`，
而 `ReadContainerTrack()` 从未碰过 `AV_DISPOSITION_ATTACHED_PIC` / attachment 流。

**这不是我们自己发现的孤例**——Jellyfin issue **#15222「ASS in external MKS renders incorrectly due to missing fonts」**
（至今 open，2026-06 仍在讨论，用户反馈"字体像随机挑的"）正是同一个根因。**外挂 MKS 的 ASS 八成带字体附件**，不修这项目就等于残的。

修法（内核，`ReadContainerTrack` 内，`ass_new_track` 之前）：

```cpp
// Matroska attachment 的数据在 codecpar->extradata 里，文件名在 metadata["filename"]
for (unsigned i = 0; i < format->nb_streams; ++i) {
    const auto* s = format->streams[i];
    if (s->codecpar->codec_type != AVMEDIA_TYPE_ATTACHMENT) continue;
    if (s->codecpar->extradata == nullptr || s->codecpar->extradata_size == 0) continue;
    if (!IsFontMime(s)) continue;                       // application/x-truetype-font, font/ttf, ...
    ass_add_font(library_, FileNameOf(s),
                 reinterpret_cast<const char*>(s->codecpar->extradata),
                 s->codecpar->extradata_size);
}
```

⚠ `ass_add_font` 必须在 **track 解析之前**注册（渲染时 libass 才查得到）。
验收判据就是上面那张表：**带附件的那一行必须变成 `330x59 / 8261`**。

### 1.3 版本号与导出纪律

- 只**新增函数**、不改任何结构体 ⇒ **API 版本保持 15**（`PlayerApi.cpp` 的 `PlayerApiVersion` / rc 的 VER_API / 宿主 `ConfigVersion` 三处都不动）
- 🔴 **宿主必须运行时解析这个新符号**（`GetProcAddress`），**不能静态导入**：
  否则装了旧内核的用户会直接因"找不到入口点"起不来 —— 与 `verify_image_mode.cpp` 对
  `FFF3FP_GetImageInfo` 的处理是同一个坑（基线内核没这个导出）。
  宿主侧做法：`播放器原生接口.vb` 里加一个 `ResolveProbeSubtitleStreams()`，解析失败则按
  "无探测能力"降级（.mks 不自动加载，但也不崩）。

---

## 2. 宿主改动（VB.NET）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Core/外部字幕自动加载器.vb` `候选项` | 加 `(".mks", 外部字幕格式.MKS)`。**别复用 SRT/ASS/SSA/SUP 的语义**——mks 是容器不是编码 |
| 2 | 同上 `加载字幕` | 对 MKS 走**容器路径**：`ProbeSubtitleStreams` → 按 `流.编码` 分派（位图编码 → `SUP字幕帧生成器(路径, 流.索引)`；其余 → `ASS特效字幕帧生成器(路径, 视频路径, 流.索引)`），即**复用 `加载内嵌字幕` 已有的分派逻辑**（:187） |
| 3 | 同上 `扫描同名字幕` | 白名单放开后自动支持同名 .mks（含 `媒体名.语言.mks` 命名） |
| 4 | `Core/播放器控制器.vb:331/346/1373`、`Form1.vb:463` | 拖放与菜单的"是否字幕文件"判定自动跟随白名单；`加载字幕Async` 需多传**视频路径**（字体私域目录按视频算，不是按 mks） |
| 5 | `播放器流选择器.vb` / 字幕菜单 | 多轨 UI：展示 `language` + `title`，切换 = 用新流索引重新 `加载内嵌字幕`（现有"内嵌字幕换轨"逻辑可直接复用，它是原子的） |

🔴 **三处必须注意的坑**（都是实测撞的）：

1. **流索引绝不能传 -1**。同一个 `-1`，位图路径是"自动选最佳字幕流"
   （`BitmapSubtitleDecoder.cpp:45` 的 `av_find_best_stream`）碰巧能用，
   ASS/文本路径是"纯文本直读"**必失败**（`AssSubtitleRenderer.cpp:276`）。
   沿用 mka 那边的习惯（mka 允许 -1）会得到"PGS 能用、ASS 用不了"的怪现象。
2. **越界索引两条路径报错不一致**：位图 `InvalidArgument(-1)`，ASS `NativeFailure(-4)`。
   探测 API 返回的索引才是可信来源，别靠试。
3. **SRT 走 libass 与宿主自研解析器样式不同**：外部 `.srt` 今天是 `SRT字幕解析器` + `设置.创建SRT字幕样式()`；
   MKS 里的 subrip 会走 libass + 内置 `DefaultHeader`（`AssSubtitleRenderer.cpp:432`）。
   字体/大小/描边与用户设置不一致 —— 要么接受，要么把 SRT 样式同步进 DefaultHeader（另议）。

---

## 3. 验收判据（必须带反向验证）

样本：`3fp/samples/media/mks/`（自造 3 个）+ `official/`（官方素材重封装 4 个，含 PGS、7 语言轨、带字体附件版）。
探针：`3fp/tools/mks_subtitle_probe.cpp`、`mks_open_probe.cpp`。

| # | 判据 | 反向验证（摘掉后必须变红） |
|---|---|---|
| 1 | `ProbeSubtitleStreams` 对 `multi_sub_official.mks` 返回 7 条流，language = chi/eng/fre/ger/ita/jpn/spa | 传一个 .mkv（无字幕流）应返回空数组而非错误 |
| 2 | 选中流 3（ger）→ 渲染出字，且与流 1（eng）**内容不同** | 故意传流 1 → 结果应等于 eng 那条 |
| 3 | `pgs_official.mks`（PGS）走位图路径，`opaque > 0`，`start=1.001s` | 传错索引（如 5）→ `InvalidArgument` |
| 4 | **字体**：带附件 MKS 的渲染结果 == 外部字体目录的结果（`330x59`） | 摘掉 §1.2 的 `ass_add_font` → 应回到 `368x61` |
| 5 | 拖入 .mks 能加载；同名 .mks 自动加载 | 把扩展名改回白名单外 → 应被拒且不崩 |
| 6 | 旧内核（无新导出）下：宿主**不崩**，只是不自动加载 .mks | 用 `GetProcAddress` 失败路径验证 |

⚠ 判据 1/2/3 都要 `opaque > 0`（真画出像素），只看"返回 Success"会假绿——
`docs/23` §4 的反向对照（-1 必失败）就是防止这种假绿的。

---

## 4. 社区现状（本轮补充搜索）

- **官方/大型样本库确实没有现成 `.mks`**（已核实 5 处）：Matroska 官方 test suite（8 个全 .mkv）、
  `samples.ffmpeg.org` 的 `Matroska/` 与 `Matroska/subtitles/`、`streams.videolan.org` 的 `mkv/` 与 `samples/Matroska/subtitles/`。
- **但 MKS 在社区是真实在用的**：Jellyfin **支持**外挂 MKS（issue #12236/#12718 实现"字幕提取"），
  并且确实踩了字体坑（#15222，open）。⇒ 这个需求不是小众幻想。
- 唯一的公开真实样本（Jellyfin #15222 附件，带字体的 ASS）**本机下不了**：
  `github.com:443` 连接超时（`api.github.com` 通、`github.com` 不通）。
  ⇒ 我们用官方素材重封装补位（`-map 0:s -map 0:t -c copy`，字体附件也带上），已能覆盖同一场景。

---

## 5. 落地建议（分两个 PR，独立可验）

**PR-1（内核，无 UI）**：`FFF3FP_ProbeSubtitleStreams` + 容器内字体附件 `ass_add_font`。
- 纯内核、可用现成 C 探针端到端验证（判据 1/2/3/4），不依赖宿主能否编译
- 字体那项对**内嵌 ASS 字幕同样受益**（不只是外挂 MKS）⇒ 独立价值

**PR-2（宿主）**：白名单 + 容器分派 + 多轨 UI + 运行时解析降级。
- 依赖 PR-1；需要宿主可编译（LakeUI 已就绪）

规模判断：PR-1 约 1 个新文件 + 3 处改动；PR-2 约 5 个文件。
**远小于图片模式 PR（20 文件 +1728/−63）**。

---

## 6. 未决

1. 是否现在开工？（建议先做 PR-1，可独立验证，风险最低）
2. ~~MKS 时间轴是否需要"字幕延迟/偏移"~~ → **已由真实样本实测回答：需要**。见 §7。
3. 位图轨 `end100ns` 恒 0（ASS 也一样）：清除靠 `flags=Clear`。是否需要在探测结果里给出时长？（暂不需要）

---

## 7. 真实样本实测后的补充（2026-09-26 晚，详见 `docs/25`）

已通过本机 VPN 代理（`127.0.0.1:10808`）拿到**唯一公开的真实社区 .mks**（Jellyfin #15222 附件，
3,554,862 B / md5 `014babfc…`：1 条 ASS + **9 个字体附件占 95.6%**），并在其上复验了本方案。

### 7.1 内核改动 2（字体附件）从「建议」升为「必做」

四格对照 + 时间轴对齐后，**"带 9 个字体附件"与"0 附件"渲染结果逐位相同**
（196x40/2826、267x40/4022、319x41/4849），而传入外部字体目录则变为
174x37/2281、230x40/3194、286x40/4040（窄 10~14%）。
⇒ 附件 100% 未被加载，缺陷在真实文件上坐实（此前只有自制对照对）。

### 7.2 新增：探测 API 应返回 `startTimeSeconds`

真实 MKS 的容器 `start_time` **可能非 0**（本样本 **+141.81 s**，且 ASS 文本首事件写的是
`0:00:00.00`，两者矛盾；Matroska 以容器块时间戳为准，内核行为与之一致，t≤120 全 `Clear`）。
正常字幕 `start_time` 落在 0~1 s，故这是真实世界的异常值。

外挂 MKS 无参考流 ⇒ 无从自动纠正。**建议**：内核保持"用容器绝对时间戳"（与内嵌一致、零歧义），
但 `FFF3FP_ProbeSubtitleStreams` 的 JSON **顶层增加 `startTimeSeconds`**，宿主据此显示并复用
现有偏移能力（对标 `SetExternalAudioOffset`）让用户修正。

### 7.3 其余结论不变

播放器 `Open(真实 .mks)` 仍是 `-7 NotSupported`、`GetMediaInfo` 空 ⇒ §1 的探测缺口原样成立；
ASS 流 0 打开成功并渲染出字 ⇒ 解码/渲染零改动。方案主体无需修改。
