# 3fp 补丁归档

> 导出给**上游** `Lake1059/FFF_Project` 的补丁放在这里（入库跟踪）。
> 本机对上游**只有读权限**（`tools/构建全部.ps1:403-404`），只能走 issue / PR。

---

## 命名约定

```
NNNN-<简短说明>.patch        内核补丁（FFF.Native）
NNNN-<简短说明>.player.patch 宿主补丁（FFF.Player）
NNNN-<简短说明>.md           该补丁的说明/PR 正文草稿
```

从 `3fp/kernel/` 导出：

```powershell
git -C 3fp/kernel diff <base>..feature/image-mode -- FFF.Native > 3fp/patches/0001-xxx.patch
git -C 3fp/kernel diff <base>..feature/image-mode -- FFF.Player > 3fp/patches/0001-xxx.player.patch
```

---

## 提交批次（对应 `../docs/02-实现规划.zh.md` §8）

| 批次 | 内容 | 改 ABI | 状态 |
|---|---|---|---|
| PR-A | K1 + K2 + K4（纯行为修正） | 否 | ☐ 未开始 |
| PR-B | K3（静态图判定改 `nb_frames<=1`） | 否 | ☐ 未开始 |
| PR-C | 版本 15→16 + 快照 8→9 + 3 个新 API + 外壳适配 | **是** | ☐ 未开始 |
| PR-D | 外壳图片浏览功能 | 依赖 C | ☐ 未开始 |

⚠ **PR-C 必须内核与外壳同一 PR**（PR #9 的教训：单独合内核会让播放器打不开任何文件）。

---

## 与 `tools/patches/` 的区别

| 目录 | 归属 | 用途 |
|---|---|---|
| `tools/patches/`（主仓库） | 3FCompare | 3FC 对内核的**历史**扩展归档，**不自动重放**，仅留档 |
| `3fp/patches/`（本区域） | 3FP 上游 | 图片模式改动的**待提交**补丁 |

两者**不要混用**。
