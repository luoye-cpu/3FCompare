# tools/patches — 内核自定义补丁存档目录

> **状态说明（2026-09-14 修订）**
>
> 本目录现在是 **历史留档**，不是构建流程的一环。
>
> 内核基线自 2026-09-11 起固化为本地归档分支 `3fcompare/zoom-viewport-cover`
> （tag `3fcompare-kernel-2026.9.11.1`，SHA 见 `tools/构建全部.ps1` 的 `$KernelBaselineSha`）。
> 该分支以**提交**而非补丁文件的形式承载了 3FCompare 扩展，因此：
>
> - `tools/构建全部.ps1` 在 HEAD 等于钉死基线时 **跳过** 本目录（扩展已内置）；
> - 本目录 9 个补丁的上下文相对该基线 **已漂移**：实测正向、反向均不可应用
>   （`git apply --check` 与 `git apply -R --check` 全部失败）。它们保留下来是作为
>   重移植时的参考实现，不再是"跑一遍就能应用"的东西。
>
> 上游升级的正确流程见 `third_party/fff_project/PATCHES.md`（人工重移植 + 打新归档 tag），
> 体检信息用 `tools/更新内核.ps1` 查询。

## 背景

`third_party/fff_project/` 已被 **解除 git submodule 跟踪**（整体不入库）：

- 它包含第三方内核源码 + 构建产物，依赖 `tools/构建全部.ps1` 从上游获取/构建
- `.gitignore` 中已整体忽略 `third_party/fff_project/`

⚠️ **可复现性风险**：归档分支与 tag 目前**只存在于本机**，远端 `Lake1059/FFF_Project`
没有 `3fcompare-kernel-*` tag。新机器裸克隆无法复现基线——`tools/构建全部.ps1`
会**报错中止**（不会静默退回到上游默认分支）。修复办法：

```bash
git -C third_party/fff_project push origin 3fcompare/zoom-viewport-cover
git -C third_party/fff_project push origin 3fcompare-kernel-2026.9.11.1
```

## 补丁规范（新增补丁时仍适用）

- **命名**：按用途/功能命名，如 `vrr-swapchain.patch`、`viewport-subregion.patch`
- **格式**：标准 `git diff` / `git apply` 可应用的统一 diff 格式
- **生成**：在 `third_party/fff_project/` 内修改后
  ```powershell
  git -C third_party/fff_project diff > tools/patches/<名称>.patch
  ```

## 应用（幂等）

`tools/构建全部.ps1` 是补丁应用的**唯一**入口，判定顺序：

1. `git apply -R --check` 成功 → 判定为「已应用」→ **跳过**（幂等的关键）
2. 否则 `git apply --ignore-whitespace` → 失败再退 `git apply --3way`
3. 仍失败 → **throw 阻断构建**，并提示用 `-SkipPatches` 明确放行

不再依赖「补丁 mtime 与标记文件比较」的时间戳启发式：
旧实现一旦有任一补丁失败就不刷新标记，下次重跑会把全部补丁再打一遍，
已应用的必然再失败 —— 形成永久卡死。

## 工作流

1. 修改 `third_party/fff_project/` 内源码
2. 提交到 `3fcompare/zoom-viewport-cover` 分支（补丁文件仅作留档时可选导出）
3. 更新 `third_party/fff_project/PATCHES.md` 的分类清单
4. 打新归档 tag，并同步 `tools/构建全部.ps1` 的 `$KernelBaselineSha`
5. 重建：`tools/构建全部.ps1`
