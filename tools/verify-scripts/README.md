# 复用脚本归档

本目录汇集了从历次一次性验证工程（`.review_pr/*`、`.verify_*`）中**抢救出来**的脚本，
这些工程本体已隔离到 `_quarantine_20260920/`（共约 6.2 GB 的 bin/obj 与日志产物），
但其中的脚本有复用价值，故保留在此并纳入版本控制。

## 子目录来源

| 目录 | 来源 | 内容性质 |
|---|---|---|
| `iconrender/` | `.review_pr/iconrender` | 图标离屏渲染与度量 |
| `pr_wt/` | `.review_pr/pr_wt` | 内核构建变体、回归矩阵、发布脚本 |
| `pubtest/` | `.review_pr/pubtest` | AOT / 抓屏发布验证 |
| `r2/` | `.review_pr/r2` | 早期验证 |
| `verify_halffix/` / `verify_pr_e2e/` / `verify_rpe/` | `.verify_*` | HALF 越界缺陷实证、PR 端到端验证（见 `docs/19`、`docs/23`） |
| `_review_pr/` | `.review_pr` 根目录散落脚本 | 构建、探针、批量运行 |

## ⚠ 使用须知

1. **不等于可直接运行**：多数脚本内含**绝对路径**与**当时的一次性约定**（如特定 obj 目录、
   特定素材路径、特定内核配置）。复用前请先读一遍，按需改路径。
2. **优先用 `tools/` 下的一等脚本**：本仓库的正规工具在 `tools/` 根目录
   （如 `build_kernel_manual.py`、`check_kernel_exports.py`、`crashscope/`、`abtest/`），
   那些是维护中的；本目录只是**归档**，不保证与当前代码同步。
3. **需要 A/B 与统计时**优先用 `tools/abtest/`（`run_batch.sh` 位置平衡批量 + `analyze.py` Fisher 检验）。

## 清理原则（供下次参考）

- 一次性验证工程**先救脚本再清产物**（脚本通常只有几十 KB，产物动辄数百 MB）。
- **用移动隔离代替删除**：建 `_quarantine_<日期>/` 移进去，确认一段时间无碍后再真正删除，
  避免误删后无法恢复。
