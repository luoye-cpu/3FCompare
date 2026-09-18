# ReadPixelRegion / ReadPixel 一致性校验（含 16F 路径）

校验内核的批量回读 API `FFF3FP_ReadVideoPixelRegion` 与单点 API `FFF3FP_ReadVideoPixel`
在同一坐标上是否一致，**同时覆盖 10 位与 16 位两条交换链路径**。

## 为什么必须有这个工具

`ReadPixelRegion` 曾把 `R16G16B16A16_FLOAT` 当作 float（16 字节/像素）读取，而它每通道
实际是 **HALF（2 字节，共 8 字节/像素）** ⇒ 数值全错 + 越界读取一倍。

这个缺陷**在默认路径下测不出来**：HDR 素材在本机会回退到 10 位
（`outputBitDepth=10 hdr=0`），16F 分支根本不执行。必须靠 `FFF_TEST_HDR=1`
强制 16 位交换链才能覆盖。⇒ **没有这个工具，该分支的回归覆盖率为 0。**

缺陷与 A/B 实证过程见 `docs/19-ReadPixelRegion-HALF越界缺陷实证.zh.md`。

## 用法

```bash
# 校验某个内核（要求 FFF.Native.lib 与 DLL 同目录）
bash tools/verify_readpixel_equivalence/run.sh <FFF.Native.dll> [FFmpeg DLL 目录]
```

- FFmpeg 目录默认 `<repo>/runtime`
- `RPE_MEDIA` 可换媒体，但**必须是 HDR 源**——非 HDR 源内核不会建 HDR 交换链，
  16 位用例会退化成 10 位（脚本会打印 `outputBitDepth`，据此可发现）
- `WORK` 可改工作目录（默认 `<repo>/.verify_rpe`）
- 退出码：`0` 两条路径都一致 / `1` 出现不一致 / `2` 环境或参数错误

输出示例（修正后内核）：

```
-- 10-bit path (default) --
  sdr        exit=0   outputBitDepth=10  VERDICT: CONSISTENT
-- 16-bit path (FFF_TEST_HDR=1) --
  hdr        exit=0   outputBitDepth=16  VERDICT: CONSISTENT
RESULT: PASS
```

## 已做的双向验证（改脚本后务必重做）

| 内核 | 10 位 | 16 位 | 总判定 |
|---|---|---|---|
| `ba6d875`（含修复） | CONSISTENT | CONSISTENT | **PASS** |
| `ba6d875^`（修复前） | CONSISTENT | **NOT CONSISTENT**（0/38，maxDiff 2.669922） | **FAIL** |

⇒ 工具能报出缺陷，且能精确定位到 16F 分支（10 位仍为 PASS）。

## 踩过的坑

1. **`cl.exe` 会把 MSYS 路径 `/c/...` 当成命令行选项** ⇒ 传给 MSVC 的路径必须用
   `cygpath -w` 转成 Windows 形式（否则 `D8003 缺少源文件名`）。
2. **内核（FFmpeg）只认 Windows 路径**：媒体路径传 `/c/...` 会在 `FFF3FP_Open`
   报 `InvalidArgument` / "The path must identify an existing regular local file"。
3. **FFmpeg DLL 必须与 exe 同目录**：延迟加载走 `LoadLibrary`，只加 PATH 不可靠
   （且 MSYS 的正斜杠 PATH 项 Windows 解析不了）⇒ 在 open 阶段直接失败（exit=127）。
4. **一致性比较前必须冻结画面**：先 `Pause`，并用「间隔 300 ms 两次单点读数差 = 0」
   坐实静止，否则两次调用会跨帧，"一致/不一致"都不可信。测试程序已内置该对照。
5. **采样点要有内容**：纯黑区即使行列映射错了也会"通过"。程序输出
   `layout pixels with signal`，为 0 说明这一轮没有判别力。

## 何时该跑

- 改动 `VideoRenderer.cpp` 的 `ReadPixelRegion` / `ReadPixel` / 交换链格式选择后
- 内核基线升级后
- 建议并入发布门禁（当前**尚未接入**，需手动执行）
