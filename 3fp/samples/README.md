# 3fp 样张集

> 图片模式的所有验证都依赖这套样张。**当前 `media/` 为空 —— 这是 M0 的第一阻塞项。**
>
> ⚠ 主仓库 `testmedia/media/` 里现有的 10 个 PNG **全是 UI 截图**（ui_audit / mouse_test / audit_screen 等），
> 没有 EXIF、没有 HDR、没有多页，**不能用作图片素材**。

`media/` 已在 `.gitignore` 中排除（体积大），`tools/` 里的生成脚本入库。

---

## 一、样张清单（11 项）

| # | 样张 | 用途 | 期 | 获取方式 | 状态 |
|---|---|---|---|---|---|
| 1 | sRGB PNG（含 alpha 通道） | 基础盘 + alpha 显示 | M1 | 脚本生成 | ☐ |
| 2 | Display P3 PNG | 广色域（M3）+ Q10 量化偏色 | M3 | 生成 / 公开素材 | ☐ |
| 3 | JPEG with EXIF Orientation=6 | **Q3 / M2 EXIF 自动旋转** | M2 | 手机照片或脚本写入 EXIF | ☐ |
| 4 | PQ AVIF（10bit） | **Q5：HDR 静态图核心样张** | M2 | libaom 编码 / 公开素材 | ☐ |
| 5 | PQ HEIC（iPhone 实拍） | Q5 + **Q8 gain map** | M2 | iPhone 实拍（必需素材） | ☐ |
| 6 | HLG JXL | Q5 | M2 | libjxl 编码 | ☐ |
| 7 | 动画 WebP / GIF / APNG / JXL 动 | M2 默认停首帧 + `loop=0` | M2 | 生成 | ☐ |
| 8 | 多页 TIFF / 多尺寸 ICO | M2 "仅显示第一页/首尺寸"标注 | M2 | 生成 | ☐ |
| 9 | >16384 边长 PNG | K4 大图预检 | M1 | 生成 | ☐ |
| 10 | 序列 `img%03d.png` ×10 | K3：序列图应回归定时视频 | M1 | 生成 | ☐ |
| 11 | 16bit PNG / EXR float | K7：RGB/float 色彩与精度 | M2 | 生成 | ☐ |

**优先级**：先做 1、9、10、11（脚本可生成，解 M1 的阻塞），
再解决 4、5、6（决定 HDR 卖点能否成立，见 `../STATUS.md` 阻塞项 2）。

---

## 二、为什么样张卡住了整个项目

| 待验证项 | 决定什么 | 依赖样张 |
|---|---|---|
| Q5 | `color_trc` 是否被填充 ⇒ **HDR 卖点能否成立**（风险 R7/R9） | 4、5、6 |
| Q8 | gain map 是否被 ffmpeg 暴露 ⇒ GainMap 是否彻底定案"不支持" | 5 |
| Q9 | RGB/float 输入的色彩与精度 ⇒ K7 改法 | 11 |
| Q3 | EXIF 方向是否正确应用 ⇒ M2 工作量 | 3 |

样张齐了以后，Q5 与 Q8 **各十分钟**即可定案。

---

## 三、生成方式

### 3.1 用本仓库 `runtime/` 的 ffmpeg DLL 编码

本机**没有** `ffmpeg.exe`，只有 `runtime/` 下的 6 个 DLL（avcodec-63 / avformat-63 / avutil-61 /
swscale-10 / swresample-7 / avfilter-12）。两条路：

1. 下载 BtbN `ffmpeg-master-latest-win64-gpl-shared.zip` 取 CLI（最直接）
2. 扩展 `tools/探测FFmpeg能力.ps1` 的 P/Invoke 方式，写一个编码小工具

### 3.2 需要真实素材、无法生成的

- **#5 iPhone PQ HEIC**：gain map 只有 iPhone 实拍才有，必须找现成素材
- **#4 PQ AVIF / #6 HLG JXL**：可编码，但需要有 HDR 母版内容才有意义

### 3.3 EXIF Orientation（#3）

若拿不到手机照片，可用任意 JPEG 写入 EXIF `Orientation=6`（竖向拍摄）后保存，
ffmpeg 的 `mjpeg` 解码器是否导出 `AV_FRAME_DATA_DISPLAYMATRIX` 正是 Q3 要验的东西。

---

## 四、命名约定

放 `media/` 下，按用途前缀：

```
media/
├── basic/        基础格式（png/jpg/webp/bmp/tiff/gif/ico）
├── hdr/          PQ AVIF / PQ HEIC / HLG JXL / EXR
├── anim/         动画图
├── edge/         多页 TIFF / 多尺寸 ICO / 超大图 / 16bit
├── seq/          img001.png … img010.png
└── meta/         EXIF Orientation 样张 / Display P3
```
