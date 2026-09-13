# 3FCompare 项目长期约定

## 实机测试素材（用户明确要求）

**所有实机测试必须基于真实的高规格视频，禁止用 ffmpeg 合成的测试图案素材作为验证依据。**

- ❌ 禁用：`testmedia/media/small.mp4`(255KB)、`test.mp4`(16KB) —— ffmpeg testsrc 合成，画面为色块+计时器，无真实编码复杂度。
- ⚠️ 次选：`testmedia/media/test_4k_60M.mp4` 等 —— 码率/分辨率达标，但内容仍是合成图案。
- ✅ 采用：`testmedia/media/real/` —— 真实拍摄内容的高规格片源，见 `testmedia/media/real/SOURCES.md`。
  来源：Jellyfin 官方测试库 `https://repo.jellyfin.org/test-videos/`（4K/8K、H.264/HEVC/AV1、8/10bit、HDR10、杜比视界，按码率分档）。
- 该目录体积大，已加入 `.gitignore`，不入库。

## 环境约束

- **`dotnet restore` 在本机全域失败**：报 `Value cannot be null. (Parameter 'path1')`（NuGet.targets 796,5）。已证明是环境问题（全新空白工程同样失败）。所有 build/test 必须加 `--no-restore`。
- **禁止 `git stash`**：曾因 stash 被 SIGTERM 中断导致 `.git/objects/pack` 丢失，约 28 个未推送提交永久丢失。需要临时切换代码时，复制文件备份后 `git checkout`，不要用 stash。
- `msbuild` / `cmd.exe` / PowerShell `Add-Type` 被安全策略拦截；PowerShell 调 Bash 也被拦。用 Bash 工具即可。

## 原生内核交互约定

- `KernelLogBridge` 把**托管委托**交给 FFF.Native 内核。内核解码/播放线程生命周期长于托管侧，
  **进程退出前必须 `KernelLogBridge.Uninstall()`**，否则 CLR 停机后内核线程反向 P/Invoke 会触发
  `coreclr/vm/ceemain.cpp:1750` 断言（"Attempt to execute managed code after the .NET runtime thread
  state has been destroyed."）并以 127 退出 —— 表现为"测试全部通过却报崩溃"。
- 已设 `AppDomain.CurrentDomain.ProcessExit` 兜底钩子 + 自测统一退出口 `MainWindow.ExitSelfTest(code)`。
- multitest 期间仍存在**播放期**原生崩溃（SIGILL `0xC000001D` / SIGSEGV），与硬件解码相关，属既有缺陷，非可用性改动引入。
