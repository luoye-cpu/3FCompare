# PR: SDR 10bit / 广色域内容优先经 16bit scRGB 呈现（内核 API 15→16）

解决两个问题：**① SDR 10bit / 广色域内容被色彩压缩**（gamma R10G10B10A2 链 + shader 把
BT.2020 折叠回 BT.709）；**② 亮度与以前的 SDR 链不一致**（scRGB 是绝对亮度契约，1.0=80nits，
纸白必须有人拍板——现在由 Windows「SDR 内容亮度」保证）。

## 用户可见行为

| 设置 | 内容 | 以前 | 现在 |
|---|---|---|---|
| 自动（默认） | 10bit SDR / 广色域 SDR | gamma 链，BT.2020 被折叠进 BT.709 | scRGB 线性链，色域不折叠，白点 = 系统 SDR 白 |
| 从不 | 任意 SDR | — | 完全回到改动前行为 |

8bit SDR / HDR / RawHdrAsSdr / SDR 桌面（AC 未激活）**零变化**。

## 内核侧（API 15→16）

- `FFF3FPConfiguration` 尾部追加 `sdrScRgbMode`（>1 → InvalidArgument）；`FFF3FPColorMode`
  枚举、`FFF3FP_SetColorMode` 签名、`FFF3FPSnapshot` **均不变**。
- 三处复制粘贴的色彩门禁收敛为 `WantsScRgbPresentationPath()`，追加「SDR 且位深>8」；
  显示器门控仍为 `OutputSupportsHdr()`。
- SDR 源走 scRGB 改发 `DXGI_HDR_METADATA_TYPE_NONE`（此前发 2020 primaries + 100 nits 假元数据）。
- **纸白 = 系统 SDR 白**：`EffectivePaperWhiteNits()` 读 `AdvancedColorInfo::SdrWhiteLevelInNits`
  （DISPLAYCONFIG 第二读数），自动跟随 HDR 校准滑杆；读不到回退 80 nits；HDR 源仍用 200。
  画面 / 字幕 / 封面 tint 共用同一解析值。
- **P3 修正**：gamut 三态（709/2020/P3）+ P3→709 线性矩阵；`ShaderBytecode.h` 由 fxc 重生成
  （生产 shader 是预编译 DXBC，只改 HLSL 不生效）。
- DISPLAYCONFIG 第二读数兼容两种 SDK 形态（10.0.28000 单参 header.size / 老版双参数）。

## 宿主侧

`AppSettings.SdrScRgbMode`（默认 1=自动，Normalize 钳位）→ `EngineSessionOptions` → 内核；
设置窗口「色彩」节新增下拉（两处深拷贝 + changed 链同步）；中英文案；会话级生效。

## 行为不变性声明

枚举 / 导出签名 / Snapshot 布局不变；8bit SDR、SDR 桌面、RawHdrAsSdr、HDR 源行为零变化。
内核与宿主必须同发（版本严格相等校验）。

## 验证

内核 23 编译单元 0 错（782336 B）；宿主 0 错；单测 728 通过 / 0 失败；`GetApiVersion()=16`；
ABI 冒烟 sizeof=80、sdrScRgbMode 0/1 接受、2 拒绝。实机（Advanced Color 桌面）：
10bit BT.709 SDR → `outBits=16`（基线 10）；8bit → 8；Display P3 → 16；V7 定案探针
（`sdr_white_probe`）：scRGB 1.0 输出 → 合成后读回 1.0 ⇒ 无二次映射。

## 如何验证

```
python tools/build_kernel_3fp.py            # 构建 3fp/kernel（应用真正跑的内核树）
python tools/build_sdr_scrgb.py --sync      # 一键：fxc 重生成 shader 字节码 + 编译 + 同步
python tools/check_kernel_config_abi.py     # ABI 冒烟
dotnet test tests/3FCompare.Core.Tests --nologo -v q
3fp\build\verify_image_mode <10bitSDR样本> --hdr --sdr-scrgb   # 判据：[after-play] outBits=16
```

## 不在本 PR 范围

SDR 桌面强走 scRGB（实验路径已有 `ForceHdrOutput` 管道）；TransportBar 增档位；
SDR-scRGB 独立纸白刻度（当前锚系统值，待观感反馈）。

---

## 附：内核交付方式

`3fp/kernel/` 被 `.gitignore` 忽略（本地构建树），内核改动以**整文件覆盖包**交付：
`3fp/patches/sdr-scrgb-kernel/`（8 个文件，文件名 `__` 对应原相对路径，
覆盖到 `3fp/kernel/FFF.Native/` 对应位置后用 `build_kernel_3fp.py` 重建）。
生成/更新命令见 `3fp/patches/sdr-scrgb-kernel-README.md`。
