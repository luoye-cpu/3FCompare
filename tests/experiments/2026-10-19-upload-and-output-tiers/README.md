# 实验件归处：上传路径与输出档位（2026-10-19）

这一目录是本轮所有测量件的唯一入口。产品侧该不该留、留哪一簇，记在
`docs/交接文档-2026-10-18-上传带宽定因.md` §27 的四档归账表里；这里只放**永不进上游**的那一档。

## 目录

- `scripts/` — 56 件：`gen_*.py` 造臂、`launch_*.bat`/`run_*.bat` 起轮（走 schtasks，探针要交互会话才建得了窗）、
  `parse_*.py`/`analyze_*.py` 判读、`port_*.py` 往出货树移植、`gh_pr_status.py` 问上游 PR 状态。
- `audio-plug/` — 床渲染接缝探针（内核侧实机臂）：`plug_seam_probe.cpp`、`gen_media.py`（造 wav 夹具）、
  `build_probe.bat`、`run_arms.bat`、`run_arms2.bat`。
- `audio-plug/run/`、`logs/`、`backups/` — 一次性产物（DLL/exe/obj/wav/日志/移植前快照），全部被本目录
  `.gitignore` 挡住，可由上面的命令重生成。

## 运行时重产物的根

脚本里写死的根是 `C:\PLAN\3FCompare\_3fc_tmp`（工作区内、已被仓的 `.gitignore` 挡住），子根：
`probe_bin\`（探针 exe）、`player_run\`（部署副本）、`fixtures\`（素材）、`wintmp\`（给子进程重定向
`TMP/TEMP`——沙箱按进程拒写 `%TEMP%`）。**新起的轮请把 `OUT` 指到本目录 `logs\rounds\`**；
旧轮日志留在 `_3fc_tmp` 不动，因为账本按那条路径引用它们，动它等于改读数出处。

## 重跑

```
cd tests\experiments\2026-10-19-upload-and-output-tiers\audio-plug
build_probe.bat          rem 自带 vcvars64；判据 = CL_RC=0 且 PROBE_EXE=present
run_arms.bat             rem 需要 3fp\build\FFF.Native.dll 在场
py gen_media.py          rem 重建 run\*.wav 夹具
```

判读口径写在每个 `parse_*.py` 的文件头（跑前写死，不是看完数再定）。
