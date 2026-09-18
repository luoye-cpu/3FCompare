# 19 · `ReadPixelRegion` 16F/HDR 越界读取缺陷：A/B 实证

日期：2026-09-18
结论（先说结论）：**缺陷真实存在，已用修正前/修正后内核在同一 HDR/16 位场景下受控对照证实**，
不依赖代码推理。修正前内核在 16F 路径上 38 个采样点**全部不一致**（最大差 2.669922），
修正后**全部逐位一致**（0.000000）。

---

## 1. 缺陷本体

`FFF.Native/3FP/Render/VideoRenderer.cpp` — `PlayerVideoRenderer::ReadPixelRegion`，

`R16G16B16A16_FLOAT` 分支（修正前）：

```cpp
const auto* rgba = reinterpret_cast<const float*>(rowPtr);
std::memcpy(out, rgba, static_cast<std::size_t>(copyWidth) * 4u * sizeof(float));
out += static_cast<std::size_t>(copyWidth) * 4u;
```

`R16G16B16A16_FLOAT` 每通道是 **HALF（2 字节）**，4 通道 = **8 字节/像素**，
而代码按 **16 字节/像素** 读。两个后果：

1. **数值全错** —— HALF 位模式被当作 IEEE float 解释；
2. **越界读取一倍内存** —— 按 `copyWidth * 16` 字节读，实际只有 `copyWidth * 8` 有效。

单点 API `ReadPixel` 本就正确（`HALF*` + `XMConvertHalfToFloat`），因此
「批量 API 与单点 API 不一致」是该缺陷的直接观测口。

修复提交：集成分支 `ba6d875`、PR 分支 `e4fc454`。

> ### 范围澄清（重要，别误读本报告）
>
> **缺陷在「回读转换」，不在「输出位深选择」。16 位 scRGB 输出是正确且应当保留的策略，
> 本报告不对其提出任何改动。**
>
> - `hdr → 16`（`OutputBitDepthForSource`）是**有意为之**：HDR 用 8 位会在 0–1000+ nits
>   的动态范围上只分到 256 级，暗部/天空渐变必然出现明显**色带**；而 DWM 内部组合管线
>   本身就是 FP16 高位深，8 位交换链等于在最后一步又做一次量化，与系统管线不契合。
> - 着色器按 `nits / 80.0` 输出线性 scRGB（FP16），正是为了让显示器/系统的色调映射器
>   拿到全精度线性光。
> - 需要修的**只有** `ReadPixelRegion` 把 HALF 当 float 读的那一次转换——
>   即"回读时按 `DXGI_FORMAT` 推导每像素字节数"，而不是让它别用 16 位。
> - 文末 §8 建议的"防御性断言"同样只针对回读函数的字节数推导，**不涉及输出格式策略**。

## 2. 为什么必须做实证

- 本机默认走 **10 位**（`outputBitDepth=10 hdr=0`，`R10G10B10A2_UNORM`），
  **16F 分支根本不被执行**。注意这不是"HDR 回退"——`OutputBitDepthForSource`
  的规则是 `hdr → 16`、`sourceBitDepth > 8 → 10`，本机是 **SDR 路径下的 10-bit 源**
  （HEVC HDR10 解码后 10 bit）正常选中 10 位；
- 该缺陷最初是**代码审查**发现的，实测覆盖率为 0；
- 源码注释指出 `FFF_TEST_HDR=1` 会强制 16 位交换链 —— 这是唯一可触发路径。

## 3. 产品可达性：不是"测试专用路径"

内核 `VideoRenderer.cpp:90` 的位深选择 + `:2273` 的交换链格式：

```cpp
// HDR output uses a 16-bit scRGB floating-point swap chain (linear
// Rec.709 primaries, 1.0 = 80 nits) ...
if (hdr) return 16;                       // 位深
...
const auto formatBits = hdr ? std::max(16u, outputBits) : outputBits;   // 格式
```

着色器侧是同一份契约（`:585`）：`// scRGB swap-chain contract: linear Rec.709
primaries, 1.0 = 80 nits` → `rec709Nits / 80.0`。

⇒ **只要走 HDR 输出，交换链必然是 `R16G16B16A16_FLOAT`（16 位 scRGB）**。
这是 Windows Advanced Color 的标准 HDR 组合格式（linear Rec.709、1.0 = 80 nits，
DWM 直接合成），**不是偏门路径**——任何在 Windows 显示设置里开了 HDR 的用户都在这条路上。

`hdr` 为真的条件（内核 `SetColorMode`）：

1. 色彩模式为 `MapToHdr` —— **产品在传输栏就有 Auto/SDR/HDR 下拉**
   （`TransportBar.axaml` + `MainWindow.OnColorModeChanged` → `Session.SetColorMode`）。
   - **Auto**：跟随 Windows 的 HDR 开关。`DisplayCapabilities.Supported` 取自
     **当前输出色彩空间**（`ColorSpace >= 12` = G2084/P2020），用户关掉系统 HDR 就变 false
     （见 `DisplayCapabilities.cs` 注释）⇒ 开了系统 HDR 的机器上，Auto 就会走 16F。
   - **手动选 HDR**：不经显示器能力门控（`ToneMappingParameters.Calculate` 只在算
     `sdrPeakNits` 时区分能力），由内核自行 fallback。
2. 片源本身是 HDR（`hdrProcessor_.IsHdrSource()`，否则 `actualMode_` 回退 SDR）。

批量回读 API 的产品调用方是 **`MainWindow.Capture.cs` 的截图/导出**
（`CaptureNativeFrame`，失败才回退抓屏）。

⇒ **真实触发路径**：用户播 HDR 片源 + 传输栏选 HDR（或 Auto 判定为 HDR）→ 导出/截图
→ 走 `ReadPixelRegion` → 拿到错误像素。

⚠ 危害等级：**静默数据损坏**。它不报错、不崩溃（小区域越界通常仍落在映射缓冲区同一行内），
只是导出 PNG 的像素值是错的。对一个**用于画面对比**的工具来说，这比崩溃更危险——
用户会基于错误的对比图下结论。

## 4. 实验设计（单变量 + 2×2 + 交替顺序）

### 4.1 两个内核来自**明确的 git 版本**，不是"某份旧 DLL"

用 `git archive` 从子模块导出两份源码快照，避免依赖他人工作区产物：

| 变体 | git 版本 | 产物 | 大小 | sha256(前16) | API |
|---|---|---|---|---|---|
| `fixed` | `ba6d875`（含修复） | `out_fixed/FFF.Native.dll` | 758,784 | `6b576029785ebf3bd311` | 15 |
| `prefix` | `ba6d875^`（修复前） | `out_prefix/FFF.Native.dll` | 757,760 | `1b34595e9c434a9959b7` | 15 |

`diff -rq` 确认两份快照**只有 1 个文件、1 处 hunk 不同**（即上面的 `VideoRenderer.cpp`
16F 分支）。两者用**同一脚本、同一编译参数**在**各自独立 obj 目录**构建（不碰共享
`FFF.Native/obj/x64/Release`，以免与并行进行的其他构建互相污染）。

> ⚠ 未采用 `.verify_pr_e2e/kernel_pre_fix.dll`（756,224 B）：它与独立构建的 `ba6d875^`
> （757,760 B）体积不符，来源无法确认，不能充当"修正前"基线。

### 4.2 唯一变量 = exe 同目录下的 `FFF.Native.dll`

同一份 `pixel_equiv.exe`（自行从 `test_pixel_equiv.cpp` 编译，不复用他人物件）、
同一素材、同一环境变量、同一台机器。

### 4.3 2×2 设计

| | 修正前 `prefix` | 修正后 `fixed` |
|---|---|---|
| **10 位**（`R10G10B10A2_UNORM`，不设 HDR，`outputBitDepth=10 hdr=0`） | 阴性对照 | 阴性对照 |
| **16 位**（`R16G16B16A16_FLOAT`，`FFF_TEST_HDR=1`，`outputBitDepth=16 hdr=1`） | **实验组** | **实验组** |

每格跑 **2 次、交替顺序**（fixed→prefix，再 prefix→fixed），排除顺序与热态影响。

### 4.4 有效性前置条件（否则结论不可信）

- 播放中帧会推进，两次调用会跨帧 ⇒ 先 `Pause` 冻结；
- 「间隔 300 ms 两次单点读数差 = 0」作为**帧冻结对照** —— 全部 6 次运行均为
  `diff=0.000000 -> FROZEN (valid)`；
- 采样点选有内容的锚点（纯黑区即使行列映射错了也会"通过"）；测试自带
  `layout pixels with signal` 计数来坐实这一点。

## 5. 结果

### 5.1 16 位路径（`outputBitDepth=16 hdr=1`）——决定性对照

| 指标 | 修正前 | 修正后 |
|---|---|---|
| 单点坐标一致数 | **0 / 6** | **6 / 6** |
| 4×4 区块像素一致数 | **0 / 32** | **32 / 32** |
| 最差 `maxDiff`（单坐标） | **2.664062** | 0.000000 |
| 最差 `maxDiff`（4×4） | **2.669922** | 0.000000 |
| 区块中有信号的像素 | **0 / 32** | 32 / 32 |
| 退出码 / 判定 | `1` / **NOT CONSISTENT** | `0` / **CONSISTENT** |

两轮（含反向顺序）**逐项完全复现**，无一次例外。

### 5.2 10 位路径（`outputBitDepth=10 hdr=0`）——变体内阴性对照

| 指标 | 修正前 | 修正后 |
|---|---|---|
| 一致数 | 38 / 38 | 38 / 38 |
| 最差 `maxDiff` | 0.000000 | 0.000000 |
| 判定 | CONSISTENT | CONSISTENT |

⇒ **修正前内核并不是"整体失效"**：它在 10 位路径上依然完全正确。
失败严格局限于「修正前 × 16F」，这正是缺陷定位所指的分支。

## 6. 缺陷签名（修正前的实际读数）

单点 API 返回真实 HDR 值（PQ/线性空间，可 >1），批量 API 返回常数式垃圾：

```
#4 (960,180)  region R=0.0000 G=0.0078 B=0.0000 A=0.0000
              pixel R=0.8979 G=0.4021 B=2.6641 A=1.0000     maxDiff=2.664062
```

特征：批量 API 的值塌缩到 ~0 的常数（HALF 位模式当 float 解释后的极小值/非规格化数），
与坐标几乎无关；差值上限 ≈ 真实通道值本身（蓝通道 2.6641 ⇒ 差 2.664）。
⇒ 这不是精度误差或舍入差异，是**语义性错误**。

## 7. 回归工具与复现

已固化的工具：**`tools/verify_readpixel_equivalence/`**（`run.sh` + 测试源码 + README）。

```bash
bash tools/verify_readpixel_equivalence/run.sh <FFF.Native.dll>
```

一次跑完 10 位与 16 位两条路径，并打印各自的 `outputBitDepth`（可确认 16F 分支真的被覆盖）。
**已双向验证**：修正后内核 PASS（两条路径都 CONSISTENT）；修正前内核 FAIL，且仅 16 位路径失败
（10 位仍 PASS）⇒ 工具既能报出缺陷，也能精确定位分支。

一次性 A/B 产物目录：`C:\PLAN\3FCompare\.verify_halffix\`

```
build_variant.sh <variant> <git-rev>   # 从明确 git 版本独立构建内核
run_ab.sh <rounds>                     # 交替顺序 A/B（FFF_TEST_HDR=1）
```

- 源码快照：`src_fixed/`、`src_prefix/`（`git archive` 导出）
- 运行目录：`run_fixed/`、`run_prefix/`（各自带对应内核 + FFmpeg DLL）
- 日志：`r_fixed_0/1.log`、`r_prefix_0/1.log`（16 位）、`s_fixed.log`、`s_prefix.log`（10 位）
- 素材：`testmedia/media/real/real_4k_hevc_hdr10_60m.mp4`

## 8. 结论与建议

1. **缺陷已被实测证实**：`ReadPixelRegion` 在 `R16G16B16A16_FLOAT` 下返回错误数值，
   且按每像素 16 字节读取（实际 8 字节）⇒ 越界一倍。修复提交 `ba6d875` / `e4fc454` 正确。
2. **越界为何没崩**：小区域（1×1、4×4）读取的越界部分通常仍落在映射缓冲区同一行内，
   故表现为"错值"而非 AV；**区域靠行尾或宽度较大时越界量成倍增长，风险更高**。
   因此这是"静默错误 + 潜在越界"，比崩溃更危险。
3. **覆盖率缺口已补上，但尚未接入门禁**：默认 10 位路径覆盖不到 16F 分支。
   回归工具已建好并双向验证（`tools/verify_readpixel_equivalence/`），
   **但目前只能手动执行**——建议并入发布门禁，否则下次改 `ReadPixelRegion`/
   交换链格式选择时仍可能静默回归。
4. **上游**：该修复已包含在拟推上游的补丁中（PR 分支 `e4fc454`）；本节实测数据属内部证据，
   按约定**不写入 PR 正文**，PR 只说明代码改动本身。
