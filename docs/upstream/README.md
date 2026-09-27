# docs/upstream —— 上游协作留档

本目录存放 3FCompare 与上游 `Lake1059/FFF_Project`（3FP）协作的**历史留档**
（issue 草稿、PR 正文、补丁、复测记录）。

## ⚠ 3FP「图片模式」相关文档已迁出

2026-09-24 起，3FP 图片查看功能拥有**独立工作区**，相关文档已迁至：

| 原位置（已移除） | 现位置 |
|---|---|
| `docs/upstream/3FP图片查看功能-可行性分析.zh.md` | **`3fp/docs/01-可行性分析.zh.md`** |
| `docs/upstream/3FP图片查看-实现规划.zh.md` | **`3fp/docs/02-实现规划.zh.md`** |

新增文档：

- `3fp/README.md` —— 工作区说明与三条铁律
- `3fp/STATUS.md` —— **交接状态（接手请先读这个）**
- `3fp/docs/00-图片模式设计规格.zh.md` —— 图片模式 / 视频模式隔离设计
- `3fp/kernel/` —— 独立内核工作副本（分支 `feature/image-mode`）

迁移原因：图片模式的试验性改动不能直接落在 `third_party/fff_project/`（3FC 的构建基线，
SHA 钉死且拒绝 dirty 工作区），故另建独立副本承载改动。

本目录其余文件（PR #9 提交文本、issue7 复测、历史 patch）**保持原位不动**。
