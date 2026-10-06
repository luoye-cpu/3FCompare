# JXR 满血启用方案（FFmpeg `libjxr` 修复计划）

- 日期：2026-09-26
- 目标：**等上游 FFmpeg/jxrlib 修好 GUID 表后，换 DLL 即可"满血"使用 JXR**；本方案先完成 **3FP 侧全部前置工作**（白名单已放开），使"上游一修好就能用"成立。
- 前置阅读：`docs/15-JXR解码可行性分析（2026-09-26）.zh.md`（阻断点取证）

---

## 1. 当前状态一句话

| 层 | 状态 | 说明 |
|---|---|---|
| **3FP 内核/宿主白名单** | ✅ **本轮已放开** | `播放列表.图片扩展名` / `媒体扩展名` 已含 `.jxr/.wdp/.hdp`（见 §3） |
| **FFmpeg `libjxr` 解码能力** | ❌ **上游缺陷** | GUID 映射表**只覆盖 8bpp Gray**；彩色格式全部 `Unsupported JPEG XR pixel format GUID` |
| **端到端（新 FFmpeg）** | 🟡 **管线通、解码断** | `.jxr` 现可被选中并进入图片管线；`gray` 格式 JXR **已实机首帧呈现成功**（`EXIT=0`）；彩色 JXR 止于解码器 |
| **端到端（随包旧 FFmpeg）** | ❌ 无 libjxr | 只能走到 `Invalid data found`（打开即败） |

> ⭐ **结论**：3FP 侧已 100% 就绪。**唯一的等待项是上游 FFmpeg 的 `libjxr` 封装**。

---

## 2. 阻断点精确定位（上游侧）

### 2.1 症状

```
[libjxr @ ...] Unsupported JPEG XR pixel format GUID
Error submitting packet to decoder: Function not implemented
```

### 2.2 根因

FFmpeg 的 `libavcodec/libjxr.c`（封装 jxrlib 的 `PKImageDecode`）在 **解码路径**里把 JXR 头部的 **pixel format GUID** 映射到 jxrlib 的 `PIXEL_FORMAT` 枚举。当前实现**只映射了 `GUID_WICPixelFormat8bppGray` 一项**，其余全部落到 `Unsupported ... GUID` 分支。

**决定性证据**（`docs/15` §2.2）：用新 FFmpeg **自己的编码器**写 8 种 pix_fmt，再用**同一个解码器**读回 —— **编码 8/8 成功，解码只认 1/8**：

| pix_fmt | 嵌入 GUID | 解码 |
|---|---|---|
| `gray` | `6fddc324-…-c908` (8bppGray) | ✅ |
| `gray16le` | `…-c90b` (16bppGray) | ❌ |
| `rgb24` | `…-c90d` (24bppRGB) | ❌ |
| `bgr24` | `…-c90c` (24bppBGR) | ❌ |
| `rgba` | `f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9` (32bppRGBA) | ❌ |
| `bgra` | `…-c90f` (32bppBGRA) | ❌ |
| `rgb48le` | `…-c915` (48bppRGB) | ❌ |
| `rgba64le` | `…-c916` (64bppRGBA) | ❌ |

⇒ 编码器**写得出来**（说明它自己知道这些格式），解码器**读不回来**。**这是封装层映射表覆盖不全，不是 jxrlib 本身的问题。**

### 2.3 真实样本为何更特殊

Windows HDR Calibration 的 `.jxr` 用 `IMAGE_HEADER.headerSize=8`（**不内嵌 GUID**），其后的 1 字节像素格式码是 **`0x0B`(11) / `0x0C`(12)** —— **超出 jxrlib 经典 `PIXEL_FORMAT` 枚举范围 0–10**（`JXRGlue.h`）：

```
0 32bppRGB  1 32bppRGBA  2 32bppPRGBA  3 24bppRGB   4 48bppRGB
5 32bppBGR  6 32bppBGRA  7 32bppPBGRA  8 8bppGray   9 16bppGray  10 32bppCMYK
```

⇒ 上游要修**两条路径**：① 内嵌 GUID 的映射（补全彩色 GUID）；② **无 GUID 时的短码映射**（补 11/12 等扩展码）。

---

## 3. 已完成（3FP 侧）

### 3.1 白名单放开（`docs/15` §5 指出的第二道门）

**文件：`3fp/kernel/FFF.Player/Core/播放列表.vb`**

```vb
' 媒体扩展名（:26）
".ico", ".avif", ".heic", ".heif", ".jxr", ".wdp", ".hdp"
' 图片扩展名（:36）
".ico", ".avif", ".heic", ".heif", ".jxr", ".wdp", ".hdp"
```

⚠ **不变式**：`媒体扩展名 ⊇ 图片扩展名`（已加回归断言，见 §3.3）。

### 3.2 有意**不**改的地方：文件关联清单

`文件关联管理器.vb` 的图片清单**刻意不加** `.jxr` —— 该清单的承诺是**"双击即看"**，入选判据是「随包内核实测能呈现≥1帧」（`FFF.Player.Tests/Program.vb:632` 注释断言 + `tools/cover_probe.sh` 逐条实测背书）。JXR 目前 `presented=0`，加进去会**破坏该项目既有承诺**。

> ⇒ **上游修好后的收尾动作之一**：把 `.jxr/.wdp/.hdp` 加进 `文件关联管理器.vb` 的 `不常见图片` 组（见 §6 检查清单）。

### 3.3 新增回归断言（防白名单被手滑改坏）

**新增开关 `--image-extension-whitelist-regression`**（`FFF.Player.Tests/Program.vb`）：

- ① `.jxr/.wdp/.hdp` 必须在**图片模式**清单里；
- ② 必须在**媒体**清单里（维护「媒体 ⊇ 图片」不变式）；
- ③ 大小写不敏感（`SAMPLE.JXR` / `Sample.Wdp`）；
- ④ **反向**：`sample.psd` / `sample.txt` 不得被误判为图片；
- ⑤ 既有 14 个图片格式**一个都不能掉**。

**⭐ 反向验证已做**：临时摘掉 `.jxr` ⇒ `测试失败：图片模式清单缺少 .jxr`（**EXIT=1**）；恢复 ⇒ 判绿（EXIT=0）。**断言有牙齿。**

已加入 `3fp/build/flags1.txt`（全量扫描清单）。

### 3.4 实机端到端证据（新 FFmpeg + 白名单已开）

| 样本 | 结果 |
|---|---|
| `fh2_fp16.jxr`（真实彩色） | ❌ `测试失败：Decoder rejected packet: Function not implemented`（**止于解码器，符合预期**） |
| `rt_gray.jxr`（gray 格式 JXR） | ✅ **`GPU 配置下图片打开即显示首帧回归通过`（EXIT=0）** |

> ⭐ **`gray` JXR 在真实宿主里成功呈现首帧**，证明：**3FP 侧管线完全可用**，彩色 JXR 的唯一障碍就是上游 GUID 表。

### 3.5 回归

`tools/run_host_tests.sh` ⇒ **13 通过 / 9 失败（共 22）**，与基线**逐条同形**（9 项全为环境固有：8× Temp 目录冲突 + 1× `Panel`→`ModernPanel`），**新增开关 PASS，零新回归**。

---

## 4. 上游修复方案（三条路线）

### 路线 A：等上游 FFmpeg 合入修复 ⭐ 推荐（零维护）

- **依赖**：FFmpeg 上游有人在 `libavcodec/libjxr.c` 补全 GUID 映射。
- **现状**：FFmpeg 官方 `libjxr` 封装支持长期偏弱；需关注上游 commit。
- **动作**：无。定期用 §5 的**验证脚本**测一次新构建即可。

### 路线 B：本地给 FFmpeg 打补丁后自行重编 `avcodec-63.dll`

- **改动点**：`libavcodec/libjxr.c` 两处映射函数（GUID→`PIXEL_FORMAT` 的查表 + 无 GUID 短码分支）。
- **补丁要点**（按 `docs/15` 证据，至少需覆盖）：

  | WIC GUID | jxrlib PIXEL_FORMAT | pix_fmt |
  |---|---|---|
  | `…-c908` | 8bppGray | gray ✅ 已有 |
  | `…-c90b` | 16bppGray | gray16le |
  | `…-c90c` | 24bppBGR | bgr24 |
  | `…-c90d` | 24bppRGB | rgb24 |
  | `…-c90f` | 32bppBGRA | bgra |
  | `f5c7ad2d-…` | 32bppRGBA | rgba |
  | `…-c915` | 48bppRGB | rgb48le |
  | `…-c916` | 64bppRGBA | rgba64le |
  | 短码 `0x0B` | （待定，扩展格式） | — |
  | 短码 `0x0C` | （待定，扩展格式） | — |

- **代价**：需维护 FFmpeg 分支 + 交叉编译链（clang/llvm-mingw + 6 个依赖 DLL），与"直接用预编译 full build"策略冲突。
- **适用**：用户对 JXR 有硬需求且等不及上游。

### 路线 C：改走 Windows 原生 WIC 解码后端

- Windows `IWICImagingFactory` 原生支持 JXR（**且支持 HDR 扩展格式**）。
- **代价**：需在 3FP 里新增一条 WIC 解码路径，与现有"单 FFmpeg 链路"架构冲突，改动最大。
- **适用**：若上游长期不修，且需要**完整 HDR JXR** 支持。

> ⚠ **不建议**"只放开文件关联白名单"这种捷径：解码器没好之前，只会把"格式不支持"变成"支持但双击打不开"的坏体验。

---

## 5. 上游更新后的验证脚本（一键判"是否已满血"）

**已实现为工具 `3fp/tools/verify_jxr.sh`**（可直接跑）。用法：

```bash
bash 3fp/tools/verify_jxr.sh <ffmpeg.exe 路径>
# 不带参数则用随包旧 FFmpeg 做对照
```

判据分三层（**全绿才算满血**）：

| 层 | 检查 | 含义 |
|---|---|---|
| ① 依赖层 | `-decoders` 里有 `libjxr` | 解码器是否编入 |
| ② 解码层 | 8 种 pix_fmt 自产往返**全部** DEC-OK | GUID 映射表是否补全 |
| ③ 真实层 | 4 个真实样本**全部** OPEN-OK | Windows HDR Calibration 的 JXR |

实测结果（本轮，2026-09-26）：

| FFmpeg | ① | ② | ③ | 结论 |
|---|---|---|---|---|
| 随包旧（`bin/Debug/…/ffmpeg.exe`） | ❌ | ❌ | ❌ | 无 libjxr |
| `publish/PLAN/ffmpeg-full/`（新） | ✅ | ❌（1/8） | ❌（0/4） | 有 libjxr，GUID 表不全 |

> ⚠⚠ **判据必须看 stderr，不能看退出码！**
> `ffmpeg -frames:v 1 -f null -` 在**解码失败**时**仍返回 exit 0**（把 `jxrlib JPEG XR decode failed` 写到 stderr，输出流为空但未"出错退出"）。
> 实测踩坑：脚本初版只看退出码 ⇒ **8 种全报 DEC-OK 的假绿**，与 `docs/15` 的手工结论矛盾 ⇒ 改成「`-v error` 下 stderr 为空」后恢复正常（1/8）。**这是"判据不具特异性"的典型（元发现 2）**。

---

## 6. 上游修好后的**收尾检查清单**（换 DLL 即满血）

按顺序执行：

- [ ] **1. 换入新 FFmpeg 依赖**
  - 三件套 `avcodec-63.dll` / `avformat-63.dll` / `avutil-61.dll`（**版本号可能变**，以新构建为准）
  - ⚠ **clang 交叉编译产物需一并带 6 个运行期 DLL**：`libc++.dll` / `libomp.dll` / `libunwind.dll` / `libwinpthread-1.dll` / `libshaderc_shared.dll` / `libSPIRV-Tools-shared.dll`
  - 落点：`3fp/build/`、`x64/Release/`、发布目录、`FFF.Player.Tests/bin/Release/.../`（**全部镜像都要换，用 `cmp` 逐字节校验**）

- [ ] **2. 跑 §5 验证脚本** ⇒ 期望「② 全部 DEC-OK + ③ 全部 OPEN-OK」

- [ ] **3. 端到端首帧呈现**（用随包内核，非外部 CLI）
  ```bash
  cd 3fp/kernel/FFF.Player.Tests/bin/Release/net10.0-windows10.0.26100.0
  ./FFF.Player.Tests.exe --static-image-open-regression 'C:\PLAN\3FCompare\3fp\samples\media\jxr\fh2_fp16.jxr'
  # 期望 EXIT=0（首帧呈现）
  ```

- [ ] **4. 补文件关联白名单**（§3.2 有意留的那步）
  - `文件关联管理器.vb` 的 `不常见图片` 组加 `.jxr/.wdp/.hdp`
  - 用 `tools/cover_probe.sh --current` 复验（须 `presented>=1`）

- [ ] **5. 验证 HDR 路径**（JXR 常携带 HDR/广色域）
  - `verify_image_mode.exe` 看 `outBits`：期望 HDR JXR ⇒ `16`(scRGB) / SDR JXR ⇒ `8`
  - 参考 `docs/13` 的 HDR 验证矩阵

- [ ] **6. 全格式回归**
  - `tools/verify_animated.sh`、`tools/verify_all_samples.sh`、`tools/run_host_tests.sh`
  - 确认无回归（基线：宿主 13 通过 / 9 环境固有失败）

- [ ] **7. 同步文档与记忆**
  - `docs/15` 结论改为「✅ 已跑通」、`docs/16`（本文件）标注完成
  - `STATUS.md` + 记忆

---

## 7. 本轮产物与现场

### 7.1 改动文件（内核仓，未提交）

| 文件 | 改动 |
|---|---|
| `FFF.Player/Core/播放列表.vb` | 两个扩展名清单各加 `.jxr/.wdp/.hdp` |
| `FFF.Player.Tests/Program.vb` | 新增 `--image-extension-whitelist-regression` 开关 + `测试图片扩展名白名单()` |
| `3fp/build/flags1.txt` | 加入新开关（供全量扫描） |

**未改动**：`文件关联管理器.vb`（有意保留，见 §3.2）

### 7.2 验证结果

- 宿主编译：**0 错误 0 警告**
- 新断言：判绿 `EXIT=0`；**反向验证判红** `EXIT=1`（摘掉 `.jxr`）
- 全量回归：**13 通过 / 9 失败**（环境固有，与基线同形）
- 端到端：`gray` JXR **首帧呈现成功**；彩色 JXR 止于上游解码器（符合预期）

### 7.3 现场复原

- `3fp/build/` 与 `FFF.Player.Tests/bin/Release/.../` 的 FFmpeg DLL 已从备份还原，三份 `cmp` 均 **IDENTICAL**，6 个额外依赖已删。
- **零 git 写操作**。

### 7.4 踩坑

1. ⚠ **宿主测试输出目录（`bin/Release/.../`）有独立的一份 FFmpeg DLL**，只换 `3fp/build/` 不会影响宿主端到端 ⇒ **两处都要换**。
2. ⚠ **`文件关联管理器.vb` 有"双击即看"承诺**（`presented≥1` 判据）⇒ `.jxr` **不能**直接加，需上游修好后才加（§6 第 4 步）。
3. ⚠ 用 Python 批量删 `播放列表.vb` 里的扩展名做变异时，**必须 `cp` 备份再用 `cp` 还原**（不要用编辑工具逐行改，易错位）。
