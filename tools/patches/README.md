# tools/patches — 内核自定义补丁存放目录

本目录存放对 **FFF_Project 内核（`third_party/fff_project`）** 的自研修改补丁。

## 背景

`third_party/fff_project/` 已被 **解除 git submodule 跟踪**（整体不入库）：
- 它包含第三方内核源码 + 构建产物，依赖 `tools/构建全部.ps1` 从上游获取/构建
- `.gitignore` 中已整体忽略 `third_party/fff_project/`

因此，**所有对内核的自研修改必须以 `.patch` 文件形式存放在本目录**，随主仓库版本管理。

## 补丁规范

- **命名**：按用途/功能命名，如 `vrr-swapchain.patch`、`viewport-subregion.patch`
- **格式**：标准 `git diff` / `git apply` 可应用的统一 diff 格式
- **生成**：在 `third_party/fff_project/` 内修改后
  ```powershell
  git -C third_party/fff_project diff > tools/patches/<名称>.patch
  ```
- **应用**：`tools/更新内核.ps1` 会自动按文件名顺序重打本目录所有 `*.patch`

## 工作流

1. 修改 `third_party/fff_project/` 内源码
2. 导出补丁到 `tools/patches/*.patch`
3. 提交补丁（补丁入库，内核源码不入库）
4. 内核升级时运行 `tools/更新内核.ps1`：更新内核 → 自动重打补丁 → 重建
