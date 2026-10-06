# sdr-scrgb-kernel — 内核改动整文件覆盖包

`3fp/kernel/` 被 `.gitignore` 忽略（本地构建树，不入库），因此「SDR 10bit / 广色域
scRGB 呈现」特性的内核改动以**整文件覆盖包**形式随仓库交付。

## 文件映射（`__` = 原相对路径分隔符）

| 包内文件 | 覆盖目标（相对 `3fp/kernel/FFF.Native/`） |
|---|---|
| `3FP__Api__FFF.Player.Api.h` | `3FP/Api/FFF.Player.Api.h` |
| `3FP__Api__PlayerApi.cpp` | `3FP/Api/PlayerApi.cpp` |
| `FFF.Native.rc` | `FFF.Native.rc` |
| `3FP__Core__PlayerSession.cpp` | `3FP/Core/PlayerSession.cpp` |
| `3FP__Render__VideoRenderer.h` | `3FP/Render/VideoRenderer.h` |
| `3FP__Render__VideoRenderer.cpp` | `3FP/Render/VideoRenderer.cpp` |
| `3FP__Hdr__HdrProcessor.h` | `3FP/Hdr/HdrProcessor.h` |
| `3FP__Render__ShaderBytecode.h` | `3FP/Render/ShaderBytecode.h` |

⚠ `ShaderBytecode.h` 是 fxc 预编译 DXBC（含 P3 三态支持），**必须**用包内版本覆盖，
不要用本机 HLSL 重新生成后再覆盖回旧字节码。

## 应用方式

```powershell
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Api__FFF.Player.Api.h'      '3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h'      -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Api__PlayerApi.cpp'         '3fp\kernel\FFF.Native\3FP\Api\PlayerApi.cpp'         -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\FFF.Native.rc'                   '3fp\kernel\FFF.Native\FFF.Native.rc'                 -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Core__PlayerSession.cpp'    '3fp\kernel\FFF.Native\3FP\Core\PlayerSession.cpp'    -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Render__VideoRenderer.h'    '3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.h'    -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Render__VideoRenderer.cpp'  '3fp\kernel\FFF.Native\3FP\Render\VideoRenderer.cpp'  -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Hdr__HdrProcessor.h'        '3fp\kernel\FFF.Native\3FP\Hdr\HdrProcessor.h'        -Force
Copy-Item '3fp\patches\sdr-scrgb-kernel\3FP__Render__ShaderBytecode.h'   '3fp\kernel\FFF.Native\3FP\Render\ShaderBytecode.h'   -Force
python tools/build_kernel_3fp.py        # 期望：0 失败，产物 ≈785408 B
```

## 注意

1. **构建源必须是 `3fp/kernel/FFF.Native`**（`build_kernel_3fp.py`）。`third_party/fff_project`
   是提上游 PR 的 git 仓库，**缺 `IsWideGamutSource` 广色域门禁**，其产物不能当应用内核。
2. API 16 与宿主 `Fff3FpEngine.ConfigVersion = 16` 严格相等，混配会全部会话创建失败。
3. 改动摘要见 `3fp/docs/27` §11.1；验证判据见 §7.3 与 §11.4/§12.5。
4. 本包对应 2026-09-29 代码审查后的版本（含 gamut 优先级、首帧门控时序、rc 字符串、
   P3 探针钳位对齐、SDR 白沿用等修复）——覆盖旧包时整包替换，不要混用。
