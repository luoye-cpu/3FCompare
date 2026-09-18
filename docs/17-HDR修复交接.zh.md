# 17 - HDR 修复交接（供接手 agent 使用）

> 撰写日期：2026-09-16。上游 issue #7（多路播放 DXGI 竞态崩溃）已修复并提交 PR，本文档交接的是**下一个独立事项：HDR 亮度探测的实机验证与收尾**。
> 背景：`CODE-REVIEW-v0.2.5.md` 判定 No-Go 的两项"静默功能失效"之一即为本项（另一项书签双击跳转已修复）。

## 一、问题是什么

`DisplayCapabilities.ReadForMonitor`（HDR 能力/峰值亮度探测）曾因 COM vtable 槽位错误**静默全失效**：`IDXGIOutput6::GetDesc1` 被调到错误槽位，返回"S 成功"但只写了结构体前 4 字节，导致 `desc.Monitor` 恒为 0、HMONITOR 匹配恒失败，HDR 能力与峰值亮度全部走默认值，且不报任何错——用户只会觉得"HDR 模式不好用"。

## 二、代码现状（2026-09-16 已核实，均无需再改代码）

| 审查报告条目 | 现状 | 位置 |
|---|---|---|
| `SltGetDesc1` vtable 槽位错（27 vs 26） | ✅ 已定为 **27**，附实测逐字段比对说明（26 是 DuplicateOutput1，调它会崩） | `src/3FCompare.Core/Display/DxgiOutputInfo.cs:101` |
| `ColorSpace>=3` 注释与 `>=12` 实现不一致 | ✅ 注释已重写为 `>=12`（G2084/P2020）并说明语义：跟随系统 HDR 开关而非硬件上限，正是业务所需 | `src/3FCompare.Core/Display/DisplayCapabilities.cs:49-70` |
| 枚举触顶静默漏检 | ✅ 上限已从 8 提到 32 | `DxgiOutputInfo.cs:109-110` |
| 失败日志 | ✅ 已有明确日志：`未找到匹配 HMONITOR 0x… 的 DXGI 输出，HDR 亮度回退默认值` | `DxgiOutputInfo.cs:283` |

**关键结论：代码侧修复已全部完成，唯一悬项 = HDR 显示器实机端到端验证**（当前机器无 HDR 屏，无法确认 `MaxLuminance` 真实读到非默认值）。

## 三、接手后的验证步骤（需 HDR 显示器实机）

1. **环境确认**：Windows 设置 → 系统 → 显示器 → 开启 HDR；记录显示器型号与额定峰值亮度。
2. **应用日志取证**：启动 3FCompare，检查日志中是否**不再**出现 `未找到匹配 HMONITOR … 回退默认值`；如仍出现，按 `DxgiOutputInfo.cs:217` 注释的排查路径走（枚举顺序/虚拟输出）。
3. **数值核对**：让应用读出 `MaxLuminance`，与 Windows "HDR/SDR 亮度平衡"页或 EDID 的额定值比对（误差应在厂商标称范围内）。
4. **端到端行为**：打开 4K HEVC HDR10 素材（`testmedia/media/test_4k_hdr_80M.mp4` / `test_8k_av1_200M.mp4`），确认 `ColorModeHelper.Resolve` 自动选中 HDR 输出模式（而非手动指定）；再关掉系统 HDR 开关重启应用，确认自动回落 SDR（验证 `ColorSpace>=12` 语义正确）。
5. **回归**：`dotnet test tests/3FCompare.Core.Tests`（现有 175 用例应全绿，其中含 ToneMappingParametersTests 等 HDR 相关单测）；`--selftest` 用 HDR10 素材跑一遍（历史基线见 `HANDOFF-v0.2.5` §测试记录）。

## 四、验收标准

- HDR 屏上：日志无回退告警 + `MaxLuminance` 与标称一致 + HDR 素材自动进 HDR 模式。
- 关闭系统 HDR：自动回落 SDR。
- 全部通过后：更新 `CODE-REVIEW-v0.2.5.md` D.1 段落为"已实机确认"，并将 §二 的表格结论同步到该文档条目 5。

## 五、注意事项（沿用项目长期约定）

- 工作区有未提交改动时，**等用户指示**才动 git；禁止 `git stash`（曾有提交丢失事故）。
- 提交规范：标题 `[vX.Y.Z] <type>: <摘要>`，每版本 3~4 条主题化提交，英文提交信息。
- `构建全部.ps1` 是内核构建唯一入口；当前内核基线 `3fcompare-kernel-2026.9.17.1`（`3ac124a`，上游 PR #8 合并后）。
  ⚠ 沙箱会拦截 `MSBuild.exe`（LOLBin 规则），重建内核需走手工 `cl.exe`/`link.exe`/`rc.exe`（见 `docs/18` §1.2）。
- 上游 PR #8（issue #7）：**已合并**（`ea3ce05` / `824093d`）。但 2026-09-17 复测**崩溃未消失**：
  12 次 `--multitest` 崩溃 6 次（50%），与修复前无差异；`RequestRecoveryIfDeviceLost()` 与
  `ClearSurface()` 两条路径仍只在 `deviceMutex_` 下访问交换链。详见 `docs/18`。
  ⇒ 本条"修复后值得复测"已执行，结论为**未通过**，HDR 项不受影响（相互独立）。
