# 141 · HLG映射scRGB过曝：OOTF亮度域修复（2026-11-11）

## 背景与素材

用户报告 HLG→scRGB 映射存在过曝，指定用 YouTube `Qs2-klYtq5Y` 的 HLG 交付做分析。经本地 SOCKS 代理（`socks5h://127.0.0.1:10808`）下载两个最小 HDR 交付格式（存 `_3fc_tmp/hlg_audit/`）：

| 文件 | 编码 | 规格 | 体积 | ffprobe（流级） |
|---|---|---|---|---|
| `source_av1.mp4` | AV1 10bit | 256×144p60 | 26.7 MiB | arib-std-b67 / bt2020nc / bt2020 / tv |
| `source_vp9.webm` | VP9 Profile 2 | 256×144p60 | 41.4 MiB | arib-std-b67 / bt2020nc / bt2020 / tv |

**注意：两个文件的帧级 `color_trc` 均为空**（`ffprobe -show_frames` 无该字段）——HLG 判定完全依赖流级 `codecpar.color_trc=18` 回退（`HdrProcessor::ConfigureStream`）。真实 VP9/AV1 HLG 交付里这是常态。

## 审查：管线核对清单（确认正确的环节）

limited→full 量程展开（`VideoRenderer.cpp` `directYuv` 臂）、BT.2020 系数 0.2627/0.0593、`yuv420p10le` sampleScale=65535/1023 与 P010 的 65535/65472、HLG 恒走自绘 shader（`RequiresMetadataAwareShader` 排除 VP 快路径）、2020→709 线性矩阵、scRGB 臂 ÷80（1.0=80nits）、HLG 0.75→203.15nits 参考白、`HdrStaticMetadata` 无 side data 时的 BT.2020/D65 默认值。

## 根因：OOTF 被逐通道施加

`HlgOne/HlgToNits`（shader 与 C++ 参考实现两份同源）在逆 OETF 之后做 `1000·pow(scene,1.2)`，**对 R/G/B 各自做幂**。BT.2100 参考 OOTF 定义在场景**亮度**上：`Yd = α·Ys^(γ−1)·Yc`，γ=1.2、α=Lw=1000，`Yc = dot(E,(0.2627,0.6780,0.0593))`，所得**单一系数同乘三通道**。

逐通道写法对灰阶完全等效（`Yc=E` 时退化一致），对彩色像素等效于每通道乘 `Yc^(−0.2)`：

| 内容 | 参考亮度 | 旧实现亮度 | 倍数 |
|---|---|---|---|
| 中性灰 0.75 | 203.2 nits | 203.2 nits | 1.00 |
| 纯红 v=1 | 765.4 nits（刺激） | 1000 nits | ×1.31 |
| 纯绿 v=1 | 925.2 | 1000 | ×1.08 |
| 纯蓝 v=1 | 568.3 | 1000 | **×1.76** |
| 深饱和暗色（Yc=0.026） | — | — | ×2.07 |

真实素材逐像素验证（shader 全链路数学 vs BT.2100 参考，含 2020→709 与 ÷80）：

| 帧 | 中位 | P95 | P99 | 最大 |
|---|---|---|---|---|
| 30s（偏中性） | ×1.002 | ×1.006 | ×1.015 | ×1.759 |
| 60s | ×1.000 | ×1.016 | ×1.023 | ×1.759 |
| **90s（日落）** | ×1.014 | ×1.130 | ×1.192 | ×1.223 |

90s 最亮太阳像素 信号(0.99,0.34,0.08)：旧 scRGB (19.29,−1.17,−0.23) → 新 (14.96,−0.72,−0.18)，+29%；0.64% 像素过亮超 ×1.2。中性画面无感、饱和高光系统性偏亮 20%~76% 且额外过饱和——进 DWM rolloff 后即"高光提早滚白"的过曝观感。这也是 docs/60 实测未抓到它的原因：中性灰基准本来就对，旧实测证伪的是"HLG 按 PQ 解码"旧缺陷。

### 对先前 P2 结论的更正

审查初期口头结论「HLG 无 mastering 元数据时下发全零色度 HDR10 块」**证伪**：`HdrStaticMetadata` 的成员默认值本来就是 BT.2020 原色 + D65 白点（`HdrProcessor.h:29-45`），Apply* 系列只在 side data 有效时覆写。真正残留的只有 `minimumLuminanceNits` 默认 0 → `MinMasteringLuminance=0`（CTA-861/DXGI 视 0 为无效值）。

## 改动（两树各四处 + 字节码重生成）

开发树 `3fp/kernel` 与出货树 `third_party/fff_project` 逐字同步（两树 HLGL 区块/`HdrProcessor.cpp` 编辑前逐字一致，编辑后 `ShaderBytecode.h`/`SdrShaderBytecode.h` 哈希一致）：

1. **HLSL**（`VideoRenderer.cpp` `PixelShaderSource` 内）：`HlgOne` 拆为 `HlgInvOetf`（标量，逐通道）+ `HlgToNits(float3)`（`yc=dot(e,2020权重)`，`1000·pow(yc,0.2)·e`）。灰阶行为逐位不变。
2. **C++ 参考实现**：标量 `HlgToNits(float)` 改名 `HlgInvOetf`；`struct Float3` 定义点新增 `Float3 HlgToNits(Float3)`（同公式）；`EvaluateVideoColorTransform` 消费点由逐通道构造改为 `nits = HlgToNits(rgb)`。
3. **`BuildDxgiHdr10Metadata`**：`MinMasteringLuminance` 在源无 mastering side data（默认 0）时下限到 0.0001 nits（1 个 DXGI 单位，与 `BuildTrueHdrMetadata` 口径一致）。
4. **`ProcessFrame` 帧级 trc 双向重分类**：原来只有 `format==Sdr` 时才接受帧级 trc 升级；容器错标（如 HLG 标成 2084）会把 HdrProcessor 与 shader（`ResolveDecodeTransfer` 已跟随帧级）永久撕裂。现在在 `!dynamicMetadata` 且 format ∈ {Sdr,Hlg,Hdr10} 时按帧级 trc 双向重分类（Vivid/HDR10+ 属主的状态不动；真实 Vivid/HDR10+ 流帧级 trc 为 UNSPECIFIED，FATE 素材已验证，不受影响）。
5. **字节码**：`3fp/kernel/tools/generate_shader_bytecode.py`（fxc 10.0.28000.0，ps_5_0/vs_5_0）对两树各跑默认+`--sdr-specializations` 两遍，四个头重写；两树 `ShaderBytecode.h`/`SdrShaderBytecode.h` 哈希一致。**`SdrShaderBytecode.h` 逐字节未变**——SDR 布局特化把 HLG 分支死码消除了，反证 SDR 链不受影响。`check_shader_cbuffer_layout.py` 两树全绿（17/17 含正控制，矩阵 9/9）。

## 回归判据与新探针

新探针 `3fp/tools/hlg_ootf_probe.cpp`（已挂 `rebuild_probes.ps1`）：经 `FFF3FP_EvaluateColorTransform`（MapToHdr + source2020=1，与生产 scRGB 臂同合同）锚定五点，容差 ±0.02：

| 输入 | 期望 scRGB | 旧实现值 |
|---|---|---|
| 灰 0.75 | (2.5394, 2.5394, 2.5394) | 相同（锚定回归安全） |
| 红 1.0 | (15.8869, −1.1916, −0.1737) | (20.7561, −1.5569, −0.2269) |
| 绿 1.0 | (−6.7962, 13.1023, −1.1632) | (−7.3455, 14.1613, −1.2572) |
| 蓝 1.0 | (−0.5175, −0.0593, 7.9478) | (−0.9106, −0.1044, 13.9841) |
| 晚霞 (0.99,0.34,0.08) | (14.9616, −0.7174, −0.1803) | (19.2927, −1.1738, −0.2290) |

实测：KF 内核 DLL **5/5 PASS**；TP 树源码独立构建（无 IAMF 形态，见下）DLL 同样 **5/5 PASS**。`rebuild_probes.ps1` 34 探针 OK（hlg_ootf_probe 首编缺 `<iterator>` 修复后通过）。

## 产物

`FFF.Native.dll`（KF 构建，含 IAMF）sha16 **`983451b73d370877`**（6520832 B），四份一致：`3fp/kernel/FFF.Native/x64/Release`、`3fp/build`、宿主 `FFF.Player/bin/Release/net10.0-windows`、`third_party/fff_project/FFF.Native/x64/Release`（出货镜像位）。零 commit / 零 push。

**出货镜像位说明**：TP 的 vcxproj 其 IAMF 链接段缺 `fdk-aac.lib`（KF vcxproj 已补，TP 未跟——既有缺口，非本次引入），故 TP 树用 `HaveLibiamf=false` 可独立构建（源码级验证即用它，sha16 `54f876f9f262c63e`，1.05 MiB，探针同过），但强制 IAMF 形态在 TP 本地链接失败；出货二进制按惯例取 KF 构建镜像。TP 的 fdk-aac 链接补齐留给下次 TP PR。

## 残留与后续

- 实机 GUI 回看 90s 日落帧（对比修复前后高光）未做，需要时可用 FFF.Player 实机驱动工具链复验。
- 容器错标传输函数的文件（如 HLG 标 2084）现在能被帧级证据纠回；对帧级也错标/缺失的极端文件仍无法自动识别，遇可疑素材先抽帧人工核对。
- 探针判据已覆盖 C++ 参考实现；生产 shader 与 C++ 的同源性靠同一公式文本 + 字节码重生成保证（SDR 特化死码消除已反证编译穿透）。
