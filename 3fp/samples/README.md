# 3fp 样张集

> 图片模式的所有验证都依赖这套样张。**2026-09-24：11 个类别共 17 个文件已生成并核验**（见下表状态列）。
> （口径说明：「类别」= 下表的 11 行，「文件」= 展开后的实际产物数——例如第 7 类一行就含
> WebP/GIF/APNG/AVIF 四个动图文件。两处数字都对，指的是不同东西。）
>
> 生成方式：`bash tools/generate_samples.sh`（ffmpeg 部分）+ `python tools/inject_metadata.py`（元数据注入）。
>
> ⚠ 主仓库 `testmedia/media/` 里现有的 10 个 PNG **全是 UI 截图**（ui_audit / mouse_test / audit_screen 等），
> 没有 EXIF、没有 HDR、没有多页，**不能用作图片素材**。

`media/` 已在 `.gitignore` 中排除（体积大），`tools/` 里的生成脚本入库。

---

## 一、样张清单（11 个类别）

| # | 样张 | 用途 | 期 | 获取方式 | 状态 |
|---|---|---|---|---|---|
| 1 | sRGB PNG（含 alpha 通道） | 基础盘 + alpha 显示 | M1 | 脚本生成 | ✅ `basic/01_*` |
| 2 | Display P3 PNG | 广色域（M3）+ Q10 量化偏色 | M3 | 脚本生成 + **cICP 注入** | ✅ `meta/02_*`（ffmpeg 写不出 cICP，须注入） |
| 3 | JPEG with EXIF Orientation=6 | **Q3 / M2 EXIF 自动旋转** | M2 | 脚本生成 + **APP1 注入** | ✅ `meta/03_*`（Q3 已定案） |
| 4 | PQ AVIF（10bit） | **Q5：HDR 静态图核心样张** | M2 | HDR10 视频抽帧 + libaom | ✅ `hdr/04_*`（Q5 已定案） |
| 5 | PQ HEIC（iPhone 实拍） | Q5 + **Q8 gain map** | M2 | iPhone 实拍（必需素材） | ❌ **本机无 heif muxer，需真实素材** |
| 6 | HLG JXL | Q5 | M2 | zscale PQ→HLG + libjxl | ✅ `hdr/06_*`（Q5 已定案） |
| 7 | 动画 WebP / GIF / APNG / AVIF / JXL 动 / HEIC 动 | M2 默认停首帧 + `loop=0` | M2 | 脚本生成 | ✅ WebP/GIF/APNG（A 类）+ **AVIF（B 类）**；❌ 动画 JXL（无 jxl muxer）/ 动画 HEIC（无 heif muxer） |
| 8 | 多页 TIFF / 多尺寸 ICO | M2 "仅显示第一页/首尺寸"标注 | M2 | 脚本生成 + **Python 合成** | ✅ 3 页 TIFF；⚠ ICO 仅单尺寸（muxer 限制） |
| 9 | >16384 边长 PNG | K4 大图预检 | M1 | 脚本生成 | ✅ `edge/09*`（K4 已实测） |
| 10 | 序列 `img%03d.png` ×10 | K3：序列图应回归定时视频 | M1 | 脚本生成 | ✅ `seq/`；⚠ 经 3FP 打开路径**不可达**（见下） |
| 11 | 16bit PNG / EXR float | K7：RGB/float 色彩与精度 | M2 | 脚本生成 | ✅ `edge/11*`；⚠ 暴露 `bitDepth=32` UB |

**优先级**：先做 1、9、10、11（脚本可生成，解 M1 的阻塞），
再解决 4、5、6（决定 HDR 卖点能否成立，见 `../STATUS.md` 阻塞项 2）。

> ⚠ **#10 的用途在当前宿主下不成立**：ffmpeg 只在**显式模式串**（`img%03d.png`）下才识别 10 帧序列，
> 而内核的路径校验要求"真实存在的常规文件"（上游 `2acd96d` 同样如此）⇒ 序列图打不开，K3 的序列顾虑实为 moot。

> ⚠ **本机 ffmpeg 的三处能力缺口**（实测）：无 `heif`/`heic` muxer、无 `tiff` muxer、无 `jxl` muxer；
> 且 png 编码器无 cICP 选项、mjpeg 编码器不写 EXIF ⇒ 这三项元数据必须后处理注入。

> **动图两类（实测，2026-09-24）**：
> - **A 类（无真实时间轴）**：`07a_anim.webp`(webp_anim) / `07b_anim.gif`(gif) / `07c_anim.png`(apng)——
>   ffprobe 的 `duration`/`nb_frames` 多为 `N/A`（GIF 例外，报 `nb_frames=10`），仅靠帧率循环。
> - **B 类（有真实时间轴）**：**`07d_anim.avif`**——`ftyp major_brand=avis`（AVIF Sequence），
>   ffprobe 报 2 条流：流0 首帧静图（1 帧）+ 流1 动画轨（`nb_frames=10` / `duration=1.0`），
>   经 `mov,mp4,m4a,3gp,3g2,mj2` demuxer + `av1` 解码器读取。
> - **读不了（C 类）**：动画 HEIC —— 本机**无 `heif` demuxer/muxer**；动画 JXL —— **无 `jxl` muxer**
>   （`libjxl_anim` 编码器无任何容器可承载，`image2 -update 1` 产物 libjxl 解不开），
>   但**有** `jpegxl_anim` demuxer + `libjxl_anim` 解码器（无素材可实测）。

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

### 3.1 用本机的完整 ffmpeg CLI（**已推翻旧结论**）

~~本机**没有** `ffmpeg.exe`~~ —— **2026-09-24 实测更正**：本机**有**完整构建，

```
C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\
  ffmpeg.exe / ffprobe.exe / ffplay.exe + avcodec-63.dll 等全部依赖
  （--enable-libjxl --enable-libaom --enable-libwebp --enable-libzimg --enable-lcms2 --enable-libopenjpeg）
```

⇒ **除 §3.2 明确列出的需真实素材项外**，样张均可脚本化生成，无需下载、无需写 P/Invoke 编码工具。
（本机 ffmpeg 缺 `heif`/`tiff`/`jxl` muxer，见上文 ⚠ 提示 ⇒ 第 5、7 类的部分格式**生成不出来**。）
`runtime/` 下那 6 个裸 DLL 仍是内核运行所需的共享运行时（内核按 exe 同目录延迟加载）。

> 📦 **MKS 交付包**：`3fp/samples/mks_samples_20260926.zip`（真实社区 MKS 字幕样本）。
> ⚠ 该 zip **已被 `.gitignore` 排除不入库**（仓库只跟踪 `tools/` 下的生成脚本）——
> 换机器需要按 README 重新获取/生成，别指望从仓库里拿到它。

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
