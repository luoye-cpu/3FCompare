# HDR10+ (ST 2094-40) 测试素材与元数据注入 API

> 背景：项目此前只有 FFmpeg 官方 FATE 测试向量
> （`testmedia/media/hdrmeta/hdr10_plus_h265_sample.hevc`），它是**合成梯度图**，
> 曲线近恒等，几乎不锻炼动态映射路径。本目录是从**真实 4K HDR10 片源**生成、
> 携带真实 ST 2094-40 SEI 的素材，用于端到端验证。
>
> 本文两大部分：**§一–§七 测试素材**（怎么来的、怎么复跑），
> **§八 注入 API**（对外接口的用法与语义）。实现规划见 `3fp/docs/58`。

---

## 一、为什么需要自己生成

| 途径 | 结果 |
|---|---|
| FFmpeg FATE 向量 | ✅ 有 HDR10+，但曲线近恒等（`knee=0`、9 锚点近似线性）——**测不出映射是否真的生效** |
| Jellyfin 官方测试库 | ❌ 只有 SDR / HDR10 / Dolby Vision，**无 HDR10+** |
| 4kmedia.org | ❌ 全站搜索 HDR10+ 只返回 HDR10/HLG/DV，**无真 HDR10+** |
| hdr10plus.org 官网 | ❌ 无公开样张下载（内容在商业流媒体侧） |
| `x265 --dhdr10-info` | ❌ 本机 x265 4.2 报 `--dhdr10-info disabled. Enable HDR10_PLUS in cmake.` |
| **FFmpeg `hdr10plus` 滤镜** | ✅ **可行** —— 从画面统计生成真 ST 2094-40 元数据 |

⇒ 用 FFmpeg 的 `hdr10plus` 滤镜（"Produce HDR10+ (SMPTE ST 2094-40) dynamic
metadata from the picture"）从真实片源画面**逐帧测量**并生成元数据。

---

## 二、生成方法（可复现）

源素材：`testmedia/media/real/real_4k_hevc_hdr10_60m.mp4`
（4K HEVC 10bit，PQ/`smpte2084`，BT.2020，30s，Jellyfin 官方测试库）

```bash
FF=publish/PLAN/ffmpeg-full/ffmpeg.exe

# curve=rolloff：斜率 1 到目标点后把场景峰值拟合上去（真实的非恒等曲线）
$FF -hide_banner -ss 0 -t 10 -i testmedia/media/real/real_4k_hevc_hdr10_60m.mp4 \
  -vf "scale=1920:1080,hdr10plus=peak=1000:curve=rolloff:fbp=1" \
  -c:v libx265 -preset fast -crf 18 -pix_fmt yuv420p10le \
  -x265-params "hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:log-level=error" \
  -an -f mp4 -y testmedia/media/hdr10plus/real_hdr10plus_rolloff.mp4
```

其余两份同法，仅改 `hdr10plus` 参数（见下表）。

**`hdr10plus` 滤镜关键参数**：

| 参数 | 含义 |
|---|---|
| `peak` | targeted system display 峰值亮度（cd/m²），写入 `targeted_system_display_maximum_luminance` |
| `curve` | `off`=只写统计不写曲线块；`preserve`=恒等曲线；`rolloff`=斜率 1 到目标点再拟合场景峰值 |
| `fbp` | 逐帧真实测量 `fraction_bright_pixels`（而非写 0 标记） |
| `hold` | 新 knee 与旧值差异小于 N 个 1/4095 码时沿用旧值 |

> ⚠ **写文件务必用临时名 + 成功后改名**。中途被中断的编码会留下一个"看起来完整"的
> 截断 mp4（本次 `preserve` 第一版就是这样：524 KB 且无 `moov` atom），
> 后续若是直接读它会得到"文件打不开"而非"内容不对"，排查方向会被带偏。

---

## 三、素材清单

| 文件 | 时长 | 大小 | SEI 数 | `curve` | `peak` | 内核实测 |
|---|---|---|---|---|---|---|
| `real_hdr10plus_rolloff.mp4` | 10s | 6.8 MB | 600 | `rolloff` | 1000 | `degrade=0`、曲线生效、峰值随场景变化 |
| `real_hdr10plus_preserve.mp4` | 6s | 3.2 MB | 360 | `preserve`（恒等） | 1000 | `degrade=0` |
| `real_hdr10plus_nocurve.mp4` | 6s | 3.2 MB | 360 | `off`（无曲线块） | 2000 | **`degrade=4`**（`ToneMappingAbsent`） |

三者覆盖三条不同代码路径：**非恒等曲线 / 恒等曲线 / 无曲线**。
第三份是"元数据缺曲线块"这一形状的**负向用例**——内核如实上报 `degrade=4`
而不是假装成功，这正是设计意图。

---

## 四、实测证据

### 4.1 逐帧动态（这是 FATE 向量给不出的）

`hdr10plus_kernel_probe` 对 `real_hdr10plus_rolloff.mp4` 的读数：

```
t= 400ms: peak=432 targetPeak=1000 outBits=10
t= 800ms: peak=377 targetPeak=1000 outBits=10
t=1200ms: peak=347 targetPeak=1000 outBits=10
t=1600ms: peak=314 targetPeak=1000 outBits=10
t=2000ms: peak=322 targetPeak=1000 outBits=10
t=2400ms: peak=325 targetPeak=1000 outBits=10
t=2800ms: peak=347 targetPeak=1000 outBits=10
t=3200ms: peak=350 targetPeak=1000 outBits=10
```

- `peak` **随场景起伏**（432→377→347→314→322→325→347→350），不是常数 ⇒ 动态映射真的在工作
- `serial` 持续递增（4→29→53→…→172），证明逐帧元数据在**被消费**
- `targeted=1000` 与生成时的 `peak=1000` 一致
- `degrade=0`：无降级

### 4.2 完整探针输出

`kernel_probe_rolloff.txt` 为原始输出留档。

---

## 五、如何复跑

```bash
# 1) 内核 + 探针（探针必须对 third_party 头文件编译，见 docs/58 §11.6）
py tools/build_kernel_3fp.py            # 注意：这个脚本构建的是 3fp/kernel 树
# 本目录素材对应的探针在 3fp/build/hdr10plus-run/（对 third_party 头文件编译）

# 2) 端到端读数
3fp/build/hdr10plus-run/hdr10plus_kernel_probe.exe \
  testmedia/media/hdr10plus/real_hdr10plus_rolloff.mp4

# 3) 元数据注入 API 验证
3fp/build/hdr10plus-run/hdr10plus_inject_test.exe \
  testmedia/media/hdr10plus/real_hdr10plus_rolloff.mp4

# 4) 注入→像素验证
3fp/build/hdr10plus-run/hdr10plus_inject_pixel_probe.exe \
  testmedia/media/hdr10plus/real_hdr10plus_rolloff.mp4
```

---

## 六、已知边界

1. **生成时长有限**（6–10s）。长片段的场景切换、seek 行为、`hold` 上界（60 帧）
   仍未在真实影视素材上验证。
2. **均为单窗口**（`num_windows=1`）。**多窗口（`num_windows=3`）仍未验证** ——
   FFmpeg 的 `hdr10plus` 滤镜只产出单窗口统计。多窗口需要专门的编码工具
   （x265 with HDR10+，或 Dolby/Ateme 等商业工具）。过渡带宽度 `kDynamicWindowBlend=0.05`
   因此仍只有代码路径覆盖，无观感确认。
3. 素材由**画面统计合成**元数据，不是片方原始调色元数据。对"解析与映射是否正确"
   完全有效；对"是否还原了某部影片的创作意图"不适用。
4. 本目录素材**不入库**（体积，`.gitignore:76` 忽略 `testmedia/media/`），
   生成方法在 §二 可复现；本文（`docs/65`）在库内。

---

## 七、注入 API 解决的正是 §六 的边界

上面 4 条边界里有 2 条（多窗口、任意场景）**无法靠"找更多素材"解决**——真实 HDR10+
素材稀缺且多为单窗口。§八 的注入 API 让宿主**直接给出元数据**，
于是"多窗口"和"任意曲线"都变成可测的，不必等素材。

---

## 八、ST 2094-40 动态元数据注入 API

### 8.1 为什么需要这个接口

| 场景 | 没有注入 API | 有注入 API |
|---|---|---|
| 验证多窗口（`num_windows=3`）映射 | 必须找到多窗口素材（稀缺/需商业工具） | 直接构造 3 窗口表 |
| 验证任意曲线形状 | 受限于素材自带的曲线 | 任意 knee/锚点 |
| 回归测试（固定输入→固定输出） | 素材一变基线就失效 | 表固定，像素可比 |
| 覆盖色/校验、调色验证 | 不可做 | 可做 |
| 复现用户报告的问题 | 需要用户提供素材 | 让用户提供元数据即可 |

⇒ 它是**测试与集成的必要接口**，不是 debug 后门。缺了它，多窗口路径在可预见期内
无法被验证。

### 8.2 三个导出

```c
// 替换注入表。entries == NULL 且 count == 0 表示清空。内核复制数组，调用方保留所有权。
FFFResult FFF3FP_SetHdrDynamicMetadata(FFF3FPHandle player,
    const FFF3FPHdrDynamicMetadataEntry* entries, uint32_t count);

// 丢弃注入表，回到"只用比特流"的行为。
FFFResult FFF3FP_ClearHdrDynamicMetadata(FFF3FPHandle player);

// 查询最近一帧由谁驱动（见 FFF3FPHdrMetadataSource）。
FFFResult FFF3FP_GetHdrMetadataSource(FFF3FPHandle player, uint32_t* source);
```

### 8.3 数据结构与单位

```c
struct FFF3FPHdrDynamicMetadataEntry {
    uint32_t size;        // sizeof(...)
    uint32_t version;     // 必须为 1
    int64_t  frameIndex;  // 起始帧（含），见 8.4 匹配规则

    float maxScl[3];            // 线性化 RGB 最大值，0..1（nits = 值 × 10000）
    float averageMaxRgb;        // 线性化 RGB 均值，0..1
    float fractionBrightPixels; // 0..1，仅信息性

    uint32_t toneMappingFlag;   // 0 = 只有峰值，无曲线
    float kneePointX;           // 0..1；0 表示"无线性段"
    float kneePointY;           // 0..1
    uint32_t numCurveAnchors;   // 0..15
    float curveAnchors[15];     // **递增**，0..1

    float targetedDisplayNits;  // 内容的目标显示峰值（cd/m²），0 = 由 maxScl 推导
};
```

⚠ **单位与解析侧完全一致**，两处易错点已在解析层踩过（见 `3fp/docs/58` §11）：
- `maxScl` 是**已线性化**的量，`nits = 值 × 10000`，**不要**再套 PQ 反 EOTF
- `targetedDisplayNits` **直接就是 cd/m²**，**不要**再乘 10000

### 8.4 匹配规则

取 **`frameIndex <= 当前帧号` 中 `frameIndex` 最大的那条**；若没有，
取表中第一条。⇒ 宿主可以只给出**场景切换点**的稀疏列表，不必逐帧填。

### 8.5 校验与失败语义（关键设计）

**整张表先全部校验通过才替换现表**。任一条非法 ⇒ 返回 `InvalidArgument`，
**原表继续生效**。理由：半应用的表会让画面"部分正确"，比直接拒绝更难排查。

被拒绝的情形（均有测试覆盖）：

| 情形 | 判据 |
|---|---|
| 版本不符 | `version != 1` |
| 结构体过小 | `size < sizeof(entry)` |
| 数值越界 | `maxScl`/`averageMaxRgb`/锚点 ∉ [0,1]，或非有限值 |
| 锚点非递增 | `curveAnchors[i] < curveAnchors[i-1]` |
| knee 在恒等线上方 | `kneePointY > kneePointX`（会提亮暗部，无母版意图如此） |
| 锚点数超规范上限 | `numCurveAnchors > 15` |
| 表过大 | `count > 4096` |

### 8.6 优先级与可逆性

- **注入 > 比特流**。宿主显式要求，且测试需要覆盖真实 SEI。
- **可逆**：清空后行为与"该功能不存在"完全一致。
- 未注入时渲染路径与改动前**逐位相同**（Vivid 回归已复验）。

### 8.7 用法示例

```c
/* 两个场景：第 0 帧起用暗场景曲线，第 60 帧起用亮场景曲线 */
FFF3FPHdrDynamicMetadataEntry table[2];
memset(table, 0, sizeof(table));
for (int i = 0; i < 2; ++i) {
    table[i].size = sizeof(table[i]);
    table[i].version = 1;
}
table[0].frameIndex = 0;
table[0].maxScl[0] = table[0].maxScl[1] = table[0].maxScl[2] = 0.10f;
table[0].toneMappingFlag = 1;
table[0].kneePointX = 0.40f;  table[0].kneePointY = 0.10f;
table[0].numCurveAnchors = 5;
table[0].curveAnchors[0] = 0.20f; /* ... 递增至 0.92f */
table[0].targetedDisplayNits = 200.0f;
/* table[1] 同构，frameIndex = 60 */

FFF3FP_SetHdrDynamicMetadata(player, table, 2);
/* ... 播放 ... */
FFF3FP_ClearHdrDynamicMetadata(player);
```

### 8.8 验证证据

| 探针 | 检查 | 结果 |
|---|---|---|
| `3fp/tools/hdr10plus_inject_test.cpp` | 17 项：参数校验拒绝、接受、清空、场景列表匹配、**注入覆盖真实 SEI** | **17/17 通过** |
| `3fp/tools/hdr10plus_inject_pixel_probe.cpp` | 注入是否**真的到达像素**（不只是上报值） | 同一时间点 比特流→注入→清空 得 `peak` 344→500→297，RGB 明显不同；清空后 `serial` 从 57 续到 83 |

> 第二个探针是必要的：API 测试只证明"被接受"，不证明"被应用"。
> 它跑在**真带 HDR10+ SEI** 的文件上，因此"注入赢过比特流"是有意义的结论。

