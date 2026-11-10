# P1「4 路崩溃」· vtable 热替换假说 —— 取证结论

日期：2026-09-20  ·  产物目录：`.review_pr/vtable_probe/`
脚本：`probe.py`（调试附加 + 冻结现场 + 内存扫描）、`analyze.py`（汇总）

**结论：假说不成立。** 对象 vtable 指针未被换到 RTSS 副本，Present 槽也未被改写。

---

## 样本集（4 个独立崩溃）

| # | 来源 | 故障地址 | AV | `[chain+0]` | vtable 与磁盘比对 |
|---|---|---|---|---|---|
| A | 本会话 run1（pid 55240） | `dxgi+0x19530` | read `0xFFFF…FF` | `dxgi+0xCD688` | 逐槽一致（0 差异） |
| B | 上会话 `crashdumps/live_scene.json` | `dxgi+0x19530` | read `0xFFFF…FF` | `dxgi+0xCD688` | — |
| C | minidump `…1789867297.dmp` | `dxgi+0x19530` | read `0xFFFF…FF` | `dxgi+0xCD688` | vtable 页未入 dump；崩溃点代码与磁盘一致 |
| D | minidump `…1789867314.dmp` | `dxgi+0x19530` | read `0xFFFF…FF` | `dxgi+0xCD688` | 同上 |

四个样本 `[chain+0]` **完全相同**，无「换表」迹象。
（本次批跑的另 3 次运行未复现崩溃；RTSS/RTSSHooksLoader64/MSIAfterburner 全程在场。）

> 磁盘比对口径已校验（非"空读→0 差异"假阳性）：
> `dxgi+0xCD688` 落在 `.rdata`，磁盘值 `0x180033D00/0x1800356E0` 经重定位 delta `0x7FF844F00000`
> 得 `0x7FF9C4F33D00/0x7FF9C4F356E0`，与运行时槽 0/槽 1 逐值吻合。

---

## ① `[chain+0]` 落点 —— 最关键判据

样本 A 实测（`VirtualQueryEx`）：

```
[chain+0] = 0x00007FF9C4FCD688 = dxgi.dll+0xCD688
  state = MEM_COMMIT      type = MEM_IMAGE
  protect = 0x2 (PAGE_READONLY)   writable = False
  allocation_base = 0x00007FF9C4F00000  (= dxgi.dll 映像基址)
```

⇒ **落在 dxgi.dll 模块的只读 `.rdata` 内，不是可写私有内存。**

同时：
- 该 vtable 与**磁盘映像**（按重定位 delta `0x7FF844F00000` 校正）**逐槽一致，差异 0 处**。
- 前 40 槽**全部**落在 dxgi 可执行节；**没有任何一槽**指向 `RTSSHooks64.dll` 或私有/RWX 内存。
- 上下文自洽：
  - `[rsp+0]` = `FFF.Native.dll+0x5CCBE` = `call r9` 的**下一条**；
  - `eflags` ZF=1，正是调用点 `xor r8d, r8d` 的结果；
  - 反汇编 `FFF.Native.dll`（运行时构建）：
    ```
    +0x5CC88  48 8b 06        mov rax, [rsi]      ; rax = [object] = vtable
    +0x5CC8B  4c 8b 48 40     mov r9, [rax+0x40]  ; r9 = vtable 槽 8
    +0x5CC91  48 8b ce        mov rcx, rsi        ; this
    +0x5CCB4  45 33 c0        xor r8d, r8d        ; Flags = 0
    +0x5CCB7  ba 01 00 00 00  mov edx, 1          ; SyncInterval = 1
    +0x5CCBC  41 ff d1        call r9             ; Present(1,0)
    ```
    ⇒ 是**编译器生成的普通虚调用**，经槽 8（offset 0x40），目标为真表内的真函数。

## ② 是否存在被改写的 vtable 副本

在**崩溃冻结现场**扫描进程内存 **1725 MB**（`probe.py::scan_vtable_copies`），判据「连续 ≥5 个 8 字节槽都指向 dxgi 可执行节」（阈值取 5，以免漏掉"只改了 Present 槽"的副本）：

| 命中位置 | 内存类型/保护 | 可写 | 判定 |
|---|---|---|---|
| 26 处，全部在 `0x7FF9C4FCB000 … 0x7FF9C5019050` | `MEM_IMAGE` / `0x2` | False | dxgi.dll 自身只读镜像 = 其它 COM 类的真表 |

**可写内存 / 私有内存中命中 0 处。** 未发现任何 slot[Present] 指向 RTSS 或私有地址的副本。

## ③ 多次崩溃对比

- `[chain+0]`：4/4 全部为 `dxgi+0xCD688`，**每次都相同**（非"每次不同"）。
- 故障地址：4/4 为 `dxgi+0x19530`；AV 参数：4/4 为 `read 0xFFFFFFFFFFFFFFFF`。
- 返回地址：两份 dump 的 `[rsp+0]` 均为 `FFF.Native.dll+0x5CCBE`（= `call r9` 的下一条），上下文自洽。
- 崩溃点**代码**是否被内联改写：从两份 minidump 读 `dxgi+0x19530` 起 32 字节，与磁盘映像**逐字节一致**（Present 序言 `mov [rsp+0x10],rbx; mov [rsp+0x18],rsi; push rbp; push rdi; push r14; lea rbp,[rsp-0x70]; sub rsp,0x170; …`），**未被 inline hook**。
- 崩溃现场 vtable 内容：样本 A 可查（逐槽等于磁盘）；样本 C/D 的 vtable 所在页未写入 dump，故仅核对到 `rax` 值本身。

## ④ Present1 槽位（静态评估）

| 槽 | offset | 值 | 模块 | 判定 |
|---|---|---|---|---|
| 8 | `0x40` | `dxgi+0x19530` | dxgi.dll | 被调用者（Present），真函数 |
| 22 | `0xB0` | `dxgi+0x199D0` | dxgi.dll | Present1，与 Present 同族相邻（`0x18D70/0x19530/0x199D0`），真函数 |

两槽**都未被改写**，都指向 dxgi 自己的代码。

---

## ⑤ 判定与规避方向

**假说：不成立。** 核心断言（"对象 vtable 被换到 RTSS 的副本 / Present 槽被改成 RTSS 地址"）被四条独立证据否定：
只读镜像内 + 与磁盘逐槽一致 + 全部槽指向 dxgi + 可写内存无副本。

**但 RTSS 仍是触发者**，只是手法不是"换 vtable"。剩余实测矛盾：
故障指令是**写栈** `mov [rsp+0x10], rbx`（`rsp` 落在 `MEM_COMMIT/PAGE_READWRITE` 的合法栈页，目标地址可写），
异常却报**读** `0xFFFFFFFFFFFFFFFF` —— **记录与指令不自洽**；而上下文其余部分（返回地址、eflags）完全自洽。
这指向**带外干预**（代码页保护状态抖动 / 驱动级注入异常），而非"取到失效槽位"。

规避方向（按可行性排序）：

1. **降低 presenter 并发**：路数强相关已被既有事实确认（4 路 ≈ 330 线程）。把 4 路 Present 收敛到单线程 / 串行化 Present 入口，可直接压掉竞态窗口。注意：既有"全局 Present 锁无效（p=1.0000）"说明锁的粒度不对——锁的是我们自己的调用，而非外部写窗口，需改为**跨线程串行化整个 Present 入口**才有意义。
2. **改走 `Present1`**：两槽静态都干净，成本最低可试。**但收益不确定**——本次未发现 Present 被 inline hook（代码逐字节等于磁盘），故 Present1 是否真能绕开尚无证据支撑；若根因是 RTSS 针对 `Present` 这一**函数**的带外处理，则换函数可能有效。
3. **环境规避**：退出 RTSS 已验证 90%→0%。可加"检测到 `RTSSHooks64.dll` 在场则禁用多路 Present / 提示用户"。
4. **不建议**做 vtable 层面的"防换表"加固——本次未观测到换表。

---

## 复现命令

```bash
python .review_pr/vtable_probe/probe.py --runs N --seconds 30 \
       --out .review_pr/vtable_probe/scenes.json
python .review_pr/vtable_probe/analyze.py .review_pr/vtable_probe/scenes.json
```

## 原始产物

- `scenes.json` / `run1.log` —— 本会话 run1 完整现场（含 26 处扫描命中明细）
- `scenes_r2_5.json` / `run2_5.log` —— 附加 3 次运行（未复现）
- `summary_rows.json` —— `analyze.py` 汇总表
- `probe.py` / `analyze.py` —— 脚本
