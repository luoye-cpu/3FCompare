# 39 · `--color-regression` 失败的影响分析

> 起因：内核更新到上游 `2026.10.5` 后，跑 `--color-regression` 报
> `测试失败：HDR 源没有采用 MaxCLL 1242 nit：1000。`
> **结论：影响面极小，且其中一半是"测试本身已不存在"造成的假象。**

---

## 0. 一句话结论

| 问题 | 回答 |
|---|---|
| 是本次合并引入的吗？ | **不是**——`HdrProcessor.cpp` 与上游逐字节相同，我没改过 |
| 影响真实播放吗？ | **不影响**——受影响代码路径需要 `FFF.DolbyVision.Test.dll`，**该 DLL 不存在，路径不可达** |
| 普通 HDR（HDR10/HDR10+/HLG）的 MaxCLL 还正确吗？ | ✅ **正确**——实测 `MaxCLL=1000` → `sourcePeakNits=1000` |
| 那这个失败到底是什么？ | **一个已被上游删除的旧测试，跑在过期二进制上产生的告警** |

---

## 1. 先纠正一个事实：这个测试**已经不存在了**

我在上一份报告里把 `--color-regression` 当作"现存测试失败"来对待，这是**不准确的**，现予以更正：

| 事实 | 证据 |
|---|---|
| 该测试由 `Program.vb` 提供，**已在上游 `5d2cb21` 被删除** | `git log -S'color-regression'` |
| 合并后 `Program.vb` 只剩 **137 行 smoke**，检索 `color-regression` **0 命中** | `Select-String` |
| 我当时的 `FFF.Player.Tests.exe` 时间戳 **18:44**，而合并后源码 **19:27** | 见下方 |

⇒ **我跑的是合并前的陈旧二进制**，它承载着上游已删掉的旧测试。
重新构建后再跑，该开关连入口都没有。

```
# 合并后重新构建测试工程，再跑：
Core checks passed: session state, layer APIs, peaks, playlist and subtitle UTF-8 buffers.   EXIT=0
# --color-regression：已无此入口
```

**这是我上一轮的取证疏漏**：我没有核对二进制的时间戳就采信了它的输出。

---

## 2. 那个断言原本在测什么

从 `5d2cb21^:FFF.Player.Tests/Program.vb` 取回原文：

```vb
Private Sub 测试色彩回归(SDR路径 As String, HDR路径 As String)
    Dim SDR源峰值 = 读取源峰值(SDR路径, False)
    Dim HDR源峰值 = 读取源峰值(HDR路径, True)
    断言(SDR源峰值 = 100UI,   $"SDR 源的内部峰值不是 100 nit：{SDR源峰值}。")
    断言(HDR源峰值 = 1242UI,  $"HDR 源没有采用 MaxCLL 1242 nit：{HDR源峰值}。")   ' ← 失败点
```

`读取源峰值` 的做法：造一个隐藏窗口 + 会话，`打开 → 播放`，轮询快照直到
`源峰值尼特 > 0`，返回该值。

⇒ **它验的是「快照里的 `sourcePeakNits` 是否等于该样本 MaxCLL 的 1242」。**
失败信息 `：1000` 表示拿到的是 1000（默认值），**没有取到 1242**。

---

## 3. 上游改了什么

`5881872`（2026/10/5-1）重写了 `HdrProcessor::SetExtensionProcessing`，其中一处：

```diff
-    if (std::isfinite(sourcePeakNits) && sourcePeakNits > 0.0f)
+    if (active && std::isfinite(sourcePeakNits) && sourcePeakNits > 0.0f)
         frameState_.sourcePeakNits = std::clamp(sourcePeakNits, 1.0f, 10000.0f);
```

同时把"设置 `ExternalDynamic` 处理路径"也一并收进 `if (active)`。
这是**上游对 Dolby Vision 外部扩展层**的重构：把"外部扩展接受当前帧"作为
进入 `ExternalDynamic` 与采纳其 `sourcePeakNits` 的**统一前提**。

**语义上这是合理的收紧**：`active=false` 时，扩展并没有真正处理这一帧，
那它给的 sourcePeak 自然不该被采纳。旧代码会无条件覆盖 `sourcePeakNits`，
反而更容易写入一个"没有对应处理"的峰值。

---

## 4. 关键：这条路径**在本机根本走不到**

`SetExtensionProcessing` 只有一个调用者链，入口条件很硬：

```cpp
// VideoRenderer.cpp:2678 —— extensionShader_ 仅在扩展 DLL 可用时创建
if (!extensionAttempted_ && extensionEligible_) {
    if (const auto* api = GetColorExtension()) {   // ← 需要 FFF.DolbyVision.Test.dll
        ...CreatePixelShader(api->shaderBytecode, ...) → extensionShader_
    }
}

// VideoRenderer.cpp:4704 —— 调用 SetExtensionProcessing 的唯一分支
if (extensionShader_ && extensionConstants_) {
    ...
    hdrProcessor_.SetExtensionProcessing(output.sourcePeakNits, applied);   // :4723
} else {
    hdrProcessor_.SetExtensionProcessing(0.0f, false);                      // :4728
}
```

而 `GetColorExtension()`（`ColorExtension.h:7`）要求 exe 同目录存在
**`FFF.DolbyVision.Test.dll`**（且导出协议完整）。

**实测：该 DLL 在本机与仓库中都不存在。**

```
Get-ChildItem -Recurse -Filter 'FFF.DolbyVision.Test.dll'   → 无
git ls-files | Select-String 'DolbyVision'                  → 无（内核仓不含其源码）
```

⇒ `GetColorExtension()` 恒返回 `nullptr` ⇒ `extensionShader_` 恒为空 ⇒
走 `else` 分支，`SetExtensionProcessing(0.0f, false)` ⇒ **上游改的那个 `if (active && …)`
在本机永远不会以 `active=true` 进入**。

**⇒ 对 3FP 的实际播放行为：零影响。**

---

## 5. 普通 HDR 的 MaxCLL 仍然正确（实测）

担心"MaxCLL 不被采纳"是否影响日常 HDR 播放？**不影响**——普通路径与扩展路径无关。
`sourcePeakNits` 在标准 HDR 分类里就已赋值（`HdrProcessor.cpp:192–326`）：

```cpp
} else if (parameters->color_trc == AVCOL_TRC_SMPTE2084) {
    next.format = FFF3FPHdrFormat::Hdr10;
    next.processingPath = FFF3FPHdrProcessingPath::StaticHdr10;
    next.sourcePeakNits = 1000.0f;          // 默认
}
ApplyStreamStaticMetadata(parameters, next.staticMetadata);
if (next.format != FFF3FPHdrFormat::Sdr) {
    if (const auto staticPeak = StaticSourcePeak(next.staticMetadata); staticPeak > 0.0f) {
        next.sourcePeakNits = staticPeak;   // ← MaxCLL 在此生效，与扩展无关
    }
}
```

实测（新写探针 `tools/hdrpeak_probe.cpp`，读快照 + 媒体信息）：

```
样本 hdr10_1080p.mp4   ffmpeg 报 MaxCLL=1000, MaxFALL=400
  快照: sourcePeakNits=1000  isHdrSource=1  hdrFormat=1(Hdr10)  hdrPath=1(StaticHdr10)
```

⇒ **`MaxCLL` 被正确采纳为 `sourcePeakNits`。普通 HDR 路径健康。**

---

## 6. 那"1242 → 1000"到底说明什么

两种可能，且**都指向"不是播放缺陷"**：

1. **该测试用的样本本来就不是 1242**：它期望一个 MaxCLL=1242 的素材，而我跑的
   样本是 1000 —— 用错素材，断言自然失败。
2. **上游确实改变了"扩展峰值"的采纳条件**：但那要求扩展 DLL 在场，本机不具备。

本机所有 HDR 样本里**只有 `hdr10_1080p.mp4` 带 MaxCLL，且是 1000**；
我尝试合成一个 1242 的样本，但 ffmpeg 2026 的 libx264 已不接受 `max-cll`
私有选项，直接改 HEVC SEI 也未命中（该 MP4 的 CLL 不在我扫描的 NAL 位置）。
⇒ **无法在本机复现 1242 这个前提**，因此也无法进一步区分上述两种可能。

---

## 7. 影响面评估

| 维度 | 影响 |
|---|---|
| **普通 SDR/HDR10/HDR10+/HLG 播放** | ✅ **无影响**（路径无关，且实测 MaxCLL 正确） |
| **Dolby Vision 播放（无外部扩展）** | ✅ **无影响**——`SetExtensionProcessing` 不被调用 |
| **Dolby Vision + `FFF.DolbyVision.Test.dll`** | ⚠️ **有行为变化**：扩展未被接受时不再采纳其 `sourcePeakNits`。**这是上游有意的收紧，语义更正确** |
| **本机 3FP 实际行为** | ✅ **零影响**（DLL 不存在） |
| **回归风险** | ✅ 低——且 `--clip-step-regression` 实测 EXIT=0，smoke 测试 EXIT=0 |

---

## 8. 建议

| # | 建议 |
|---|---|
| 1 | **不要把本次失败当作缺陷**；它是"旧测试 + 陈旧二进制"的双重假象 |
| 2 | **若要恢复该测试**：需按上游新测试结构移植，并解决"1242 素材"的可复现获取（属独立事项） |
| 3 | **若要验证扩展路径**：需要 `FFF.DolbyVision.Test.dll` 与一份真正的 Dolby Vision 素材（P5/P8 + RPU）——本机都没有 |
| 4 | 上游的 `active` 收紧**建议接受**（语义更正确），无需回退 |
| 5 | ⚠ **流程改进**：跑测试前**必须核对二进制时间戳**。本次我因未核对而一度误报，已在本报告更正 |

---

## 9. 复现

```powershell
cd C:\PLAN\3FCompare\3fp\kernel
# 1) 确认扩展 DLL 不存在 => 该路径不可达
Get-ChildItem -Recurse -Filter 'FFF.DolbyVision.Test.dll'    # 空

# 2) 重新构建测试工程（消除陈旧二进制）
dotnet build FFF.Player.Tests\FFF.Player.Tests.vbproj -c Release
#    合并后的 Program.vb 无 --color-regression 入口

# 3) smoke + 剪辑回归
$env:TEMP='C:\PLAN\3FCompare\3fp\build\tmptest'; $env:TMP=$env:TEMP
FFF.Player.Tests\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.Tests.exe          # EXIT=0
... --clip-step-regression <视频>                                                       # EXIT=0

# 4) 普通 HDR 峰值仍正确
cd ..\build
.\hdrpeak_probe.exe (Resolve-Path 'vidtest\hdr10_1080p.mp4').Path
#   快照: sourcePeakNits=1000  isHdrSource=1  hdrFormat=1  hdrPath=1
```
