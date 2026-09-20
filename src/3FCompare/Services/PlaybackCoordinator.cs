using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare.Services;

/// <summary>多路打开编排器（WinForms MainForm.OpenFiles / OpenSlotAsync /
/// WaitForOpenCompletionAsync / TrySettle / HandleEngineEvent 的移植）。
///
/// 关键时序（真实模式）：FFF3FP_Open 仅把 DoOpen 入队即返回 Success，OpenAsync 完成
/// 时后端仍是 Opening，此阶段 Play() 得 InvalidState；必须轮询快照至
/// Ready/Playing/Paused（≤15s）再由 TrySettle 统一启动播放（首帧渲染契约）。</summary>
public sealed class PlaybackCoordinator
{
    // ---- 打开等待的时间参数（F1）----
    // deadline 是"总预算"的唯一基准：事件等待只占开头 OpenEventWindow 一小段，
    // 其余时间留给兜底轮询。旧实现让事件等待吃掉整整 15s，返回时 deadline 早已过期，
    // 轮询循环一次都不执行 ⇒ 后端其实 2s 就 Ready 也会被误判超时（注释承诺的兜底形同虚设）。

    /// <summary>等待后端就绪的总预算（与原 15s 语义一致）。</summary>
    private static readonly TimeSpan OpenTotalBudget = TimeSpan.FromSeconds(15);

    /// <summary>OpenCompleted 事件的专属等待窗口；超时即让位给轮询（不占用总预算）。</summary>
    private static readonly TimeSpan OpenEventWindow = TimeSpan.FromSeconds(3);

    /// <summary>兜底轮询间隔（事件丢失时靠它把"早就绪但无事件"的路救回来）。</summary>
    private static readonly TimeSpan OpenPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IPlayerEngine _engine;
    private readonly SyncController _sync;
    private readonly AppSettings _settings;

    /// <summary>最近一次打开被取消/降级的原因（UI 状态栏读取；null = 无待显示错误）。
    /// OpenFiles 是 async void，异常无人接住会击穿进程，因此一切失败都走状态通知而非 throw。</summary>
    public string? LastOpenError { get; private set; }

    /// <summary>UI 展示完打开错误后调用（N1 消费链：展示 → 立即清除）。</summary>
    public void ConsumeLastOpenError() => LastOpenError = null;
    private readonly bool _realMode;
    private readonly Func<int, PlayerSurface?> _surfaceAt;

    /// <summary>窗口已关闭标志。volatile：等待循环（可能落在线程池续体）与 UI 线程都要读它。</summary>
    private volatile bool _closed;

    /// <summary>打开取消源（F3）：关窗时 Cancel()，让事件等待与 100ms 轮询立即结束，
    /// 不再把 surface / slot / session 多持有十几秒。</summary>
    private readonly CancellationTokenSource _cts = new();

    // ★ 实验开关（FC_SPREAD_GPUS）：把不同播放路分配到不同 GPU。
    // 动机：多路 4K/8K 时解码器报告过载；且崩溃根因是"跨渲染器并发 Present"，
    // 若各路落在不同物理 GPU 上，其 DXGI Present 路径彼此独立，可能顺带规避该冲突。
    // 内核 A11 的 preferredAdapterIndex 同时决定**解码与渲染**用哪张卡
    // （FFmpeg 的 D3D11VA 上下文复用同一个 ID3D11Device），所以每路完全独立、无需跨卡传输。
    // ⚠ 默认关闭，行为与原来完全一致；设为逗号分隔的 DXGI 索引即可开启，如 FC_SPREAD_GPUS=0,2
    // ⚠ 变量名不能以数字开头：实测 Windows 上 "3FC_..." 这类名字传不进子进程（读到 null）。
    private int _spreadCursor;
    private int[]? _spreadGpus;
    /// <summary>AUTO 模式标志：只有 AUTO/EVEN 才启用分散；手动列表是用户显式指定。</summary>
    private bool _spreadAuto;
    /// <summary>是否按能力加权（AUTO=true）。EVEN=false 表示均分轮转。
    /// ⚠ 权衡：加权能让强卡多干（吞吐高），但会把多路压到同一张卡，
    ///   同卡内并发 Present 更多——而我们已证实崩溃正源于此，故加权可能加剧崩溃；
    ///   均分则相反，更稳但吞吐低。</summary>
    private bool _spreadWeighted;
    /// <summary>初始化分散状态。⚠ 必须是独立方法：能力探测在**首个会话创建之前**就要知道
    /// 候选与模式，而原来这段逻辑懒在 ResolveAdapterIndex() 里，导致探测判断时
    /// `_spreadAuto` 还是 false（探测永远不触发）。</summary>
    private void EnsureSpreadState()
    {
        if (_spreadGpus != null) return;
        {
            var raw = Environment.GetEnvironmentVariable("FC_SPREAD_GPUS");
            if (string.IsNullOrWhiteSpace(raw))
            {
                _spreadGpus = Array.Empty<int>();
            }
            else if (raw.Trim().Equals("AUTO", StringComparison.OrdinalIgnoreCase) ||
                     raw.Trim().Equals("EVEN", StringComparison.OrdinalIgnoreCase))
            {
                _spreadAuto = true;
                _spreadWeighted = !raw.Trim().Equals("EVEN", StringComparison.OrdinalIgnoreCase);
                // 自动：取所有"有专用显存"的适配器 —— 这样能自然排除 Microsoft Basic
                // Render Driver（纯软件、无硬解，分给它只会更慢）。
                // GpuEnumeration 走 DXGI，与内核 EnsureDevice 的 EnumAdapters1 同一索引空间。
                try
                {
                    var all = GpuEnumeration.Enumerate()
                        .Where(a => a.Index >= 0 && a.DedicatedMemoryBytes > 0)
                        .ToList();
                    // 同一张物理卡在 DXGI 里可能出现多个条目（实测本机 index 2 与 3 的 DeviceId
                    // 相同）⇒ 必须用 **LUID** 去重，否则会"看似分散、实则挤在同一张卡上"。
                    // ⚠ 不能用 Description：GpuEnumeration 会给它拼接显存与 "#索引" 后缀，
                    //   同一张卡的两个条目加工后必然不同字符串，去重会失效（已踩）。
                    var seen = new HashSet<long>();
                    all = all.Where(a => seen.Add(a.AdapterLuid)).ToList();
                    // 所有"有专用显存"的卡都参与轮转，N 卡 / i 卡混合使用是可以的
                    // （用户确认：本地多显卡时一起调用 N/i 卡不是问题，本意就是让它们分担负载）。
                    // 唯一要排除的是 Microsoft Basic Render Driver：它专用显存为 0、
                    // 纯软件无硬解，上面的 > 0 条件已经把它滤掉。
                    _spreadGpus = all.OrderBy(a => a.Index).Select(a => a.Index).ToArray();
                }
                catch
                {
                    _spreadGpus = Array.Empty<int>();   // 枚举失败就退回原行为
                }
            }
            else
            {
                _spreadGpus = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
                     .Where(v => v >= 0).ToArray();
            }
        }
    }

    private int ResolveAdapterIndex()
    {
        EnsureSpreadState();
        if (_spreadGpus!.Length == 0) return _settings.PreferredAdapterIndex;
        // 有按能力测算好的分配表就优先用（AUTO 模式）；否则按候选列表轮转（手动指定模式）。
        var pool = _spreadPlan is { Length: > 0 } ? _spreadPlan : _spreadGpus;
        var idx = pool[_spreadCursor % pool.Length];
        _spreadCursor++;
        return idx;
    }

    /// <summary>按能力测算得到的分配表（每个元素是一路要用的适配器索引）。
    /// 与"无脑轮转"的区别：先在多张卡上实测解码吞吐，再按吞吐比例分配名额。</summary>
    private int[]? _spreadPlan;
    private const int ProbeMs = 1500;

    /// <summary>在多张候选卡上**实测**解码能力，再按能力加权生成分配表。
    /// <para>为什么不能无脑轮转：不同卡的硬解能力、驱动版本、当前负载都不同；
    /// 集显与独显差距可能达数倍，平均分配会让强卡吃不饱、弱卡成为瓶颈。</para>
    /// <para>探测方法：对每张卡开一个 headless 会话（OutputWindow=0，
    /// 内核在无窗口时 Present 直接计数返回，不产生真实窗口），播放 ProbeMs 毫秒后
    /// 读 `PresentedVideoFrames` 作为吞吐指标。硬解失败会回退软解，吞吐自然低，
    /// 因此"不支持硬解的卡"会被自动排到后面而不必显式判断。</para></summary>
    private int[] BuildCapabilityPlan(int routeCount)
    {
        var candidates = _spreadGpus ?? Array.Empty<int>();
        if (candidates.Length <= 1 || routeCount <= 1) return candidates;

        var scored = new List<(int Index, double Score)>();
        foreach (var idx in candidates)
        {
            var score = CapabilityScore(idx);
            scored.Add((idx, score));
        }
        // 全部为 0（探测失败/无法解码）⇒ 不做任何改动，退回原行为
        if (scored.All(s => s.Score <= 0)) return candidates;

        // 吞吐为 0 的卡不参与（探测失败或根本解不了这个视频）
        var usable = scored.Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
        if (usable.Count == 1) return new[] { usable[0].Index };

        var total = usable.Sum(s => s.Score);
        var plan = new List<int>(routeCount);
        foreach (var (idx, score) in usable)
        {
            var quota = Math.Max(1, (int)Math.Round(score / total * routeCount));
            for (var k = 0; k < quota && plan.Count < routeCount; k++) plan.Add(idx);
        }
        // 余下名额按能力强弱依次补齐（例如 4 路分给 3 张卡时，最强卡会多拿一路）
        for (var i = 0; plan.Count < routeCount; i++) plan.Add(usable[i % usable.Count].Index);

        AppLog.Info("Coordinator",
            "多卡能力探测: " + string.Join(", ",
                usable.Select(s => $"#{s.Index}={s.Score:F0}f")) +
            $" → 分配 {string.Join(",", plan)}");
        return plan.ToArray();
    }

    // ---------------------------------------------------------------------
    // 解码单元数据（"一个单元一分"）
    //
    // 为什么不用显存：解码能力由 GPU 内部的**固定功能解码单元**决定（NVIDIA NVDEC /
    // Intel Quick Sync / AMD VCN），与显存大小没有必然关系。用显存评分会让大显存的卡
    // 分到超出其解码单元数目的路，结果只是排队，还叠加了同卡并发 Present 的崩溃风险。
    //
    // 数据来源：厂商公开规格（NVIDIA Video Codec SDK 的编解码支持矩阵、Intel 各代
    // Quick Sync、AMD VCN）。⚠ 只收录**已核实**的型号；未收录者走 DefaultDecodeUnits。
    // 宁可保守也不要编造——收录错误比不收录更糟。
    //
    // 现状：消费级 GeForce / 集显普遍只有 1 个解码单元，因此评分多为 1 ⇒ 分配退化为均分，
    // 这正是"按解码单元"应有的结果（每卡各扛一路，而不是强卡扛多路）。
    // 多解码单元主要见于专业卡/数据中心卡，遇到时在此登记即可自动加权。
    // ---------------------------------------------------------------------
    // ---------------------------------------------------------------------
    // 各厂商对"硬件解码单元"的官方叫法不同，术语要对齐：
    //   · NVIDIA —— **NVDEC**（Video Codec SDK 支持矩阵直接给出 Total NVDEC 数量）
    //   · Intel  —— **Multi-Format Codec Engines（MFC，多格式编解码引擎 / 媒体引擎）**
    //               见 Intel ARK 产品规格页的 "Multi-Format Codec Engines" 字段。
    //               Intel 的 oneVPL「Media Capabilities Supported by Intel Hardware」只给
    //               codec/位深/分辨率矩阵，**不给**引擎数量；数量要查 ARK。
    //   · AMD    —— **VCN**（Video Core Next），官方同样不逐卡给数量。
    //
    // 数据来源（2026-09-17 查证）：
    //   · NVIDIA: developer.nvidia.com/video-encode-and-decode-gpu-support-matrix-new
    //   · Intel : ark.intel.com 各产品页的 "Multi-Format Codec Engines"（如 Arc A770=2）
    // ---------------------------------------------------------------------
    private const int DefaultDecodeUnits = 1;

    // ① 精确匹配：已核实的 deviceId（宁缺毋滥，未收录者落到 ② 或默认值）
    private static readonly Dictionary<(uint Vendor, uint Device), int> KnownDecodeUnits = new()
    {
        // —— NVIDIA：NVDEC（逐型号，照录官方支持矩阵 Total NVDEC）——
        [(0x10DE, 0x2C02)] = 2,   // GeForce RTX 5080（Blackwell，官方 **2**×NVDEC）
        [(0x10DE, 0x28E0)] = 1,   // GeForce RTX 4060 Laptop（Ada，官方 1×NVDEC）
        // —— Intel 集显：Multi-Format Codec Engines（MFC），全部逐个查 Intel ARK 核实 ——
        //    来源：intel.com 处理器 ARK 规格页 GPU Specifications 区块的该字段。
        //    12 代：0x4680(UHD 770 桌面)=2、0x46A6(Iris Xe 移动)=2
        [(0x8086, 0x4680)] = 2,   // UHD 770（i9-12900K/i7-12700K/i5-12600K，均已核实）
        [(0x8086, 0x46A6)] = 2,   // Iris Xe（i7-12700H/i5-1240P，均已核实）
        //    13/14 代桌面：0xA780(UHD 770)=2（6 颗 K 系列全部一致）
        [(0x8086, 0xA780)] = 2,   // UHD 770（i9-13900K/i7-13700K/i5-13600K/
                                  //          i9-14900K/i7-14700K/i5-14600K，均已核实）
        //    13/14 代移动：
        [(0x8086, 0xA7A0)] = 2,   // Iris Xe（i5-1340P，已核实）
        [(0x8086, 0xA7A1)] = 2,   // Iris Xe（i7-1365U，已核实）
        [(0x8086, 0xA782)] = 1,   // UHD 730（i5-14400，已核实；同为 14 代桌面却是 1，勿外推）
        //    ⚠ 0xA788 存在**官方数据冲突**：i9-13900HX=2、i7-14700HX=2，但 i7-13650HX=1。
        //      同 Device ID 不同值 ⇒ 只能取多数（2）；若你用的是 13650HX 类低阶 HX，
        //      请用 FC_GPU_UNITS 显式指定为 1。
        [(0x8086, 0xA788)] = 2,   // UHD 13th/14th Gen（13900HX/14700HX=2；13650HX 例外=1）
        //    ⚠ Core Ultra（Meteor/Arrow/Lunar Lake）的 ARK 页**没有** MFC 字段，
        //      官方只给 Xe-cores 与 codec 支持 ⇒ 无法从 ARK 取数，一律走默认值 1，
        //      请务必用 FC_GPU_UNITS 按实测指定。
        //
        // ==================== TODO：两处数据缺口（暂定默认 1） ====================
        // ① Intel Core Ultra —— 已穷尽四条官方途径确认"官方未公开"：
        //    ARK 无字段 / oneVPL 无数量无 API / 官方 Datasheet（EDC·CDRD 文档 792044，
        //    328 页全文检索）只给 codec 矩阵不给数量 / 支持文章 000098345 当前版本已删除
        //    MFX 行（旧版曾载 Xe-HPM、Xe-LPM+ 均=2，但无法在线验证）。
        //    已知 Device ID（备登记用）：0x7D55 = Meteor Lake、0x7D67 = Arrow Lake、
        //    0x64A0 = Lunar Lake。将来取得官方数值后，在此按 deviceId 登记即可。
        // ② AMD（vendor 0x1002）—— 官网规格页只有 codec 的 Yes/No
        //    （4K H264 Decode / H265 Decode / AV1 Decode），无 VCN 或引擎数量字段。
        //    已知 Device ID 一概未登记，全部走默认 1。
        // ⚠ 两者当前均为"保守均分"语义（等同 FC_SPREAD_GPUS=EVEN），不会分配错误，
        //    只是无法按能力加权。用户在发现官方数据或实测出并发能力后，
        //    可用 FC_GPU_UNITS="index:数量" 覆盖，或补全此表。
        // ======================================================================
        // 以下 4 项均已逐个查 Intel ARK 核实（字段 "Multi-Format Codec Engines"），全部 = 2
        [(0x8086, 0x56A0)] = 2,   // Intel Arc A770（已核实）
        [(0x8086, 0x56A1)] = 2,   // Intel Arc A750（已核实）
        [(0x8086, 0x56A5)] = 2,   // Intel Arc A380（已核实）
        [(0x8086, 0xE20B)] = 2,   // Intel Arc B580（已核实，Xe2/Battlemage）
        // 注：本机 i7-13650HX 的 ARK 标注 Device ID 0xA788、MFC=1，而 DXGI 运行时报 0xA78B；
        //     两者并非同一 ID，故 **0xA78B 未直接核实**，不登记，走默认值 1（可用 FC_GPU_UNITS 覆盖）。
    };

    // ② 按型号名匹配系列，覆盖未收录 deviceId 的卡。⚠ 顺序敏感：具体型号在前，系列在后。
    // 顺序敏感原则：**专业卡必须排在消费级之前**。
    // 反例（已踩）："RTX 4000 Ada" 含有子串 "RTX 40"，若消费级规则 ("RTX 40",1) 在先，
    // 专业卡会被误判成 1（实际官方为 2）。故按：数据中心 → 专业卡 → 消费级 → Intel → 兜底。
    private static readonly (string Key, int Units, string Note)[] DecodeUnitRules =
    {
        // ① 数据中心（官方明确，远多于消费级）
        ("H100",   7, "H100: 官方 7×NVDEC"),
        ("A100",   5, "A100: 官方 5×NVDEC"),
        ("L40",    3, "L40/L40S: 官方 3×NVDEC"),
        ("L4",     4, "L4: 官方 4×NVDEC"),
        ("T4",     2, "Tesla T4: 官方 2×NVDEC"),

        // ② 专业卡 / 工作站（官方 Professional 表逐型号给出，分档 1–4）
        ("RTX PRO 6000", 4, "Blackwell 专业: 官方 4×NVDEC"),
        ("RTX PRO 5000", 3, "Blackwell 专业: 官方 3×NVDEC"),
        ("RTX PRO 4500", 2, "Blackwell 专业: 官方 2×NVDEC（⚠ Server Edition 为 3，名称无法区分）"),
        ("RTX PRO 4000", 2, "Blackwell 专业: 官方 2×NVDEC（含 SFF）"),
        ("RTX PRO 2000", 1, "Blackwell 专业: 官方 1×NVDEC"),
        ("RTX 6000 Ada", 3, "Ada 专业: 官方 3×NVDEC"),
        ("RTX 5000 Ada", 2, "Ada 专业: 官方 2×NVDEC"),
        ("RTX 4500 Ada", 2, "Ada 专业: 官方 2×NVDEC"),
        ("RTX 4000 Ada", 2, "Ada 专业: 官方 2×NVDEC（含 SFF）"),
        ("RTX 2000 Ada", 1, "Ada 专业: 官方 1×NVDEC"),
        ("RTX A6000", 2, "Ampere 专业: 官方 2×NVDEC"),
        ("RTX A5500", 2, "Ampere 专业: 官方 2×NVDEC"),
        ("RTX A5000", 2, "Ampere 专业: 官方 2×NVDEC"),
        ("RTX A4500", 1, "Ampere 专业: 官方 1×NVDEC"),
        ("RTX A4000", 1, "Ampere 专业: 官方 1×NVDEC"),
        ("RTX A2000", 1, "Ampere 专业: 官方 1×NVDEC"),
        ("RTX A",    1, "Ampere 专业兜底: 官方 1×NVDEC"),
        ("Quadro",   1, "Quadro（Turing/Volta/Pascal）: 官方 1×NVDEC"),

        // ③ 消费级 GeForce
        ("RTX 5090", 2, "Blackwell: 官方 2×NVDEC"),
        ("RTX 5080", 2, "Blackwell: 官方 2×NVDEC"),
        ("RTX 5070", 1, "Blackwell: 官方 1×NVDEC（含 Ti）"),
        ("RTX 5060", 1, "Blackwell: 官方 1×NVDEC（含 Ti）"),
        ("RTX 5050", 1, "Blackwell: 官方 1×NVDEC"),
        ("RTX 50",   1, "Blackwell 其余: 官方 1×NVDEC"),
        ("RTX 40",   1, "Ada 消费级全系: 官方 1×NVDEC（含 4090/4080）"),
        ("RTX 30",   1, "Ampere 消费级全系: 官方 1×NVDEC（含 3090/3080）"),
        ("RTX 20",   1, "Turing: 官方 1×NVDEC（部分 0）"),
        ("GTX 16",   1, "Turing: 官方 1×NVDEC"),
        ("GTX 10",   1, "Pascal: 官方 1×NVDEC"),
        ("GTX 9",    1, "Maxwell 2nd: 官方 1×NVDEC"),

        // ④ Intel 独显（A770/A750/A380/B580 已逐个查 ARK 核实，全部 = 2）
        ("Arc A",  2, "Intel Arc A 系列: 2×MFC（A770/A750/A380 已核实）"),
        ("Arc B",  2, "Intel Arc B 系列: 2×MFC（B580 已核实）"),
        ("Arc",    2, "Intel Arc 独显兜底: 2×MFC"),
    };

    // ③ 用户手动指定（最高优先级）：FC_GPU_UNITS="0:2,1:1,2:0"
    //    格式 = adapterIndex:单元数，逗号分隔；单元数填 0 表示**排除**该卡。
    //    用途：厂商文档没给出数量（Intel/AMD）、或你的实测与官方口径不符时自行校准。
    private Dictionary<int, int>? _unitsOverride;
    private void EnsureUnitOverrides()
    {
        if (_unitsOverride != null) return;
        _unitsOverride = new Dictionary<int, int>();
        var raw = Environment.GetEnvironmentVariable("FC_GPU_UNITS");
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 &&
                int.TryParse(kv[0].Trim(), out var idx) &&
                int.TryParse(kv[1].Trim(), out var units) && units >= 0)
            {
                _unitsOverride[idx] = units;
            }
        }
        if (_unitsOverride.Count > 0)
            AppLog.Info("Coordinator", "用户指定的解码单元: " +
                string.Join(", ", _unitsOverride.Select(kv => $"#{kv.Key}={kv.Value}")));
    }

    private static int DecodeUnitCount(AdapterInfo info)
    {
        if (KnownDecodeUnits.TryGetValue((info.VendorId, info.DeviceId), out var exact)) return exact;
        var name = info.Description ?? "";
        foreach (var (key, units, _) in DecodeUnitRules)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return units;
        return DefaultDecodeUnits;
    }

    /// <summary>按**硬件解码单元数**评分（一个单元一分），不按显存。
    /// 优先级：用户指定 &gt; 已核实 deviceId &gt; 型号系列规则 &gt; 默认 1。</summary>
    private double CapabilityScore(int adapterIndex)
    {
        EnsureUnitOverrides();
        if (_unitsOverride!.TryGetValue(adapterIndex, out var manual)) return manual;
        var info = GpuEnumeration.Enumerate().FirstOrDefault(a => a.Index == adapterIndex);
        return info is null ? DefaultDecodeUnits : DecodeUnitCount(info);
    }

    /// <summary>在飞批次表（F2/F4）。批次的配额与回调都挂在各自的 OpenBatch 上，
    /// 某批出错只结清自己那一份；整表仅在登记/注销时加锁，批次内部一律用 Interlocked 收支。</summary>
    private readonly List<OpenBatch> _batches = new();
    private readonly object _batchLock = new();

    /// <summary>一次 <see cref="OpenFiles"/> 调用的记账单元（F2）。
    /// <para><b>为什么必须按批次记账</b>：旧实现用全局单一的 <c>_pendingAutoPlay</c> +
    /// <c>_onAllOpenedCallbacks</c>，两批打开交叉时（会话恢复在飞 → 用户再拖入文件），
    /// 任一"批"的错误分支都会 <c>Exchange(0)</c> / <c>Clear()</c> 掉全局账，
    /// 把另一批的配额与恢复回调（Seek / 循环区间）一起抹掉 ⇒ 另一批配额永远回不到 0，
    /// 自动播放与会话恢复 Seek 永不执行（表现为"文件都打开了但就是不动"）。</para></summary>
    private sealed class OpenBatch
    {
        /// <summary>本批尚未归还配额的路数（0 = 本批不参与自动播放）。一律走 Interlocked 收支。</summary>
        public int Remaining;

        /// <summary>本批全部完成后的回调（会话恢复 Seek / 循环区间）。
        /// <para>用 Interlocked.Exchange 一次性取走：既保证只执行一次，
        /// 又免去了旧实现那个无锁 <c>Queue&lt;Action&gt;</c>（F4）——
        /// 旧队列依赖"所有续体都回到 Avalonia 同步上下文"的隐式假设，换个续体模型就会静默错乱。</para></summary>
        public Action? OnAllOpened;
    }

    /// <summary>归还一份配额。返回 true 表示配额已归零（即"最后一路也还了"）。
    /// <para>配额已为 0 时什么都不做并返回 false（调用方据此不触发后续动作）。
    /// CAS 循环保证并发下不会把配额减成负数。</para></summary>
    private static bool TryConsumeQuota(OpenBatch batch)
    {
        while (true)
        {
            var cur = Volatile.Read(ref batch.Remaining);
            if (cur <= 0) return false;
            if (Interlocked.CompareExchange(ref batch.Remaining, cur - 1, cur) == cur) return cur - 1 == 0;
        }
    }

    // ---- 批次登记 / 结清 ----

    private void RegisterBatch(OpenBatch batch)
    {
        lock (_batchLock) { _batches.Add(batch); }
    }

    private void UnregisterBatch(OpenBatch batch)
    {
        lock (_batchLock) { _batches.Remove(batch); }
    }

    /// <summary>结清并丢弃批次：<b>不</b>触发 Play、<b>不</b>跑回调。
    /// 用于"本批已无路可播 / 窗口已关闭"——旧实现正是在这些分支上误伤了其它批次。</summary>
    private void DiscardBatch(OpenBatch batch)
    {
        Interlocked.Exchange(ref batch.OnAllOpened, null);
        Interlocked.Exchange(ref batch.Remaining, 0);
        UnregisterBatch(batch);
    }

    /// <summary>本批最后一路完成：先跑恢复回调，再统一 Play（跳过 Failed 槽）。</summary>
    private void CompleteBatch(OpenBatch batch)
    {
        UnregisterBatch(batch);
        if (_closed) { Interlocked.Exchange(ref batch.OnAllOpened, null); return; }
        // 组件日志：本批全部路"打开阶段结束"（成功或失败都算）。这是全部打开路径的
        // 唯一就绪汇聚点 —— 之后的 Play/Seek 与 presenter 线程启动都以它为界。
        _3FCompare.Diagnostics.ComponentLog.Log(
            _3FCompare.Diagnostics.Comp.Engine, "SessionReady", -1,
            $"routes={_sync.Count} failed={_sync.Slots.Count(s => s.Failed)} src=coordinator");
        // 先取走再调用：回调内部若再次触发结清，不会重复执行
        Interlocked.Exchange(ref batch.OnAllOpened, null)?.Invoke();
        _sync.Play();
    }

    /// <summary>某一路完成（成功或失败）时归还配额；归零即结算本批次。</summary>
    private void TrySettle(OpenBatch batch)
    {
        // 未归零说明还有路没就绪，等最后一路来触发
        if (!TryConsumeQuota(batch)) return;
        CompleteBatch(batch);
    }

    /// <summary>取取消令牌（F3）。<c>Close()</c> 会 Cancel + Dispose，而并发路径可能同时读 Token
    /// ⇒ 已释放时直接返回一个"已取消"的令牌，让等待立即结束（走到这里 _closed 必然已为 true）。</summary>
    private CancellationToken CancelToken
    {
        get
        {
            try { return _cts.Token; }
            catch (ObjectDisposedException) { return new CancellationToken(true); }
        }
    }

    public PlaybackCoordinator(IPlayerEngine engine, SyncController sync, AppSettings settings,
        Func<int, PlayerSurface?> surfaceAt)
    {
        _engine = engine;
        _sync = sync;
        _settings = settings;
        _surfaceAt = surfaceAt;
        _realMode = engine is Fff3FpEngine;
    }

    public bool RealMode => _realMode;
    public SyncController Sync => _sync;

    /// <summary>窗口已关闭（自动化流程终止标志）。</summary>
    public bool IsClosed => _closed;

    /// <summary>状态变化通知（失败路变化/事件到达等，UI 据此刷新状态栏）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>打开文件（≤9 路总量钳制）。autoPlay：全部就绪后统一 Play；
    /// onAllOpened：播放前回调（会话恢复 Seek 等）。</summary>
    public async void OpenFiles(IReadOnlyList<string> files, bool autoPlay = false, Action? onAllOpened = null)
    {
        // 批次在入口处建好：即便 OpenFilesCore 内部抛异常，下面的 catch 也只结清本批次，
        // 不会像旧实现那样把全局配额/回调一次性清零（F2）。
        var batch = new OpenBatch();
        // async void 的异常无人接住会击穿进程：全部逻辑下沉到 OpenFilesCore（async Task），
        // 这里只做兜底——任何漏网异常转为状态通知。
        try
        {
            await OpenFilesCore(batch, files, autoPlay, onAllOpened);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Coordinator", $"OpenFiles 兜底捕获: {ex.GetType().Name}: {ex.Message}");
            LastOpenError = $"打开失败：{ex.Message}";
            DiscardBatch(batch);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task OpenFilesCore(OpenBatch batch, IReadOnlyList<string> files, bool autoPlay, Action? onAllOpened)
    {
        if (_closed) return;
        var count = Math.Min(files.Count, 9 - _sync.Count);
        if (count <= 0)
        {
            // 已达 9 路上限：不得同步触发 onAllOpened（会话未就绪时 Play 无意义）
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // 配额在循环前一次性记满，之后每一路无论成败都必须归还一份，否则本批永远结不清。
        if (autoPlay) Volatile.Write(ref batch.Remaining, count);
        batch.OnAllOpened = onAllOpened;
        RegisterBatch(batch);

        // 组件日志：会话打开请求（全部打开路径的唯一汇聚点：拖放 / 选择文件 / 会话重载 /
        // 崩溃重建 / 自测）。这是"presenter 线程将被创建"的时刻，与随后的 Surface/HwndCreate
        // 一起构成时间线骨架。
        _3FCompare.Diagnostics.ComponentLog.Log(
            _3FCompare.Diagnostics.Comp.Engine, "SessionOpen", -1,
            $"files={count} existing={_sync.Count} autoPlay={autoPlay} src=coordinator");

        // S4：打开媒体是本进程里最自然的"抓屏环境重来一次"的时机 —— 此刻目标窗口是全新的，
        // 用户也期望看到最新内容。清掉此前累积的 WGC 连续失败计数与降级标记，让新会话重新先试
        // WGC；否则 3 次瞬时失败（驱动重置/锁屏/窗口被临时占用）会让本进程余下的导出帧与
        // 缩略图永远走不可靠的 GDI（docs/26 §3.2），直到重启应用。
        _3FCompare.App.Capture.FrameCapture.ResetRouting();

        // 多卡分散：AUTO 模式先在各张卡上实测解码能力，再按能力分配名额；
        // 手动列表（FC_SPREAD_GPUS=0,2）是用户显式指定，不再探测，保持轮转。
        EnsureSpreadState();
        if (_spreadAuto && _spreadWeighted && count > 1 && _spreadGpus is { Length: > 1 })
        {
            _spreadPlan = BuildCapabilityPlan(count);
        }

        for (var i = 0; i < count; i++)
        {
            if (_closed) { DiscardBatch(batch); return; }
            var path = files[i];
            var surface = _surfaceAt(_sync.Count);
            if (surface is null)
            {
                // 不可静默跳过：会打破 pending 配额与回调队列的收支平衡。
                // 也不要 throw——OpenFiles 是 async void，异常无人接住会击穿进程；
                // 降级为状态通知（LastOpenError + StateChanged）。
                LastOpenError = $"第 {_sync.Count + 1} 路没有可用的播放面板（surface 为空），已取消本次打开";
                AppLog.Warn("Coordinator", LastOpenError);
                // 只结清本批次：会话恢复在飞时用户再拖文件走到这里，
                // 旧实现会把恢复批次的配额与 Seek 回调一并清掉（F2）。
                DiscardBatch(batch);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            surface.FileName = Path.GetFileName(path);
            surface.IsFailed = false;
            surface.ErrorText = string.Empty;

            try
            {
                // 真实模式需要子 HWND 作为输出窗口：等待 NativeControlHost 附件创建
                nint hwnd = 0;
                if (_realMode)
                {
                    hwnd = await surface.EnsureHwndAsync();
                    if (hwnd == nint.Zero)
                        throw new InvalidOperationException("输出窗口 HWND 未创建");
                }

                // 解析 Auto 色彩模式：根据显示器能力自动选择 HDR/SDR
                var resolvedColorMode = _3FCompare.Core.Settings.ColorModeHelper.Resolve(
                    _settings.ColorMode,
                    hwnd != 0 ? _3FCompare.Core.Display.DisplayCapabilities.ReadForWindow(hwnd) : null);
                var session = _engine.CreateSession(new EngineSessionOptions
                {
                    OutputWindow = hwnd,
                    HardwareDecode = _settings.HardwareDecode,
                    PreferredAdapterIndex = ResolveAdapterIndex(),
                    ColorMode = resolvedColorMode,
                    TearingPresent = _settings.VrrTearingPresent,
                });
                surface.AttachSession(session);
                _sync.AddSlot(session, path);

                _ = OpenSlotAsync(_sync.Slots[^1], surface, path, batch);
            }
            catch (Exception ex)
            {
                // P0-3 修复：在此处抛出的路（CreateSession 失败 / HWND 未创建）不会进入 OpenSlotAsync，
                // 也就不会调 TrySettle 归还配额。而配额是在循环前一次性记账的，
                // 漏还就会让配额永远回不到 0 → 自动播放与 onAllOpened（会话恢复 Seek / 循环区间）永不执行，
                // 表现为"文件都打开了但就是不动"。
                //
                // 归零说明没有任何一路能走异步完成路径（可能全部失败）：
                // 只结清本批次（清掉悬挂的回调，避免它污染下一次打开）；不在此触发 Play（无路可播）。
                if (TryConsumeQuota(batch)) DiscardBatch(batch);
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
            }
        }

        // autoPlay=false 的批次没有配额可归还，永远不会"完成"：直接注销，
        // 免得它带着一个永远等不到调用的回调常驻在批次表里。
        if (Volatile.Read(ref batch.Remaining) <= 0) UnregisterBatch(batch);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenSlotAsync(SyncController.SyncSlot slot, PlayerSurface surface, string path, OpenBatch batch)
    {
        try
        {
            // 引擎事件（原生工作线程）→ UI 线程
            slot.Session.EngineEvent += (_, evt) =>
            {
                if (_closed) return;
                try { Dispatcher.UIThread.Post(() => HandleEngineEvent(slot, surface, evt)); }
                catch { /* 窗口已关闭 */ }
            };

            await slot.Session.OpenAsync(path);
            if (!_closed)
            {
                surface.FileName = Path.GetFileName(path);
                if (_realMode)
                    await WaitForOpenCompletionAsync(slot, surface);
                // 每次 await 之后都要复查关闭信号（F3）：等待期间窗口可能已经关闭，
                // 此时不应再 Play，也不能让配额悬挂着。
                if (_closed) { DiscardBatch(batch); return; }
                // 播放中拖入新视频：同步到主时间轴当前位置
                if (!slot.Failed && _sync.Count > 1)
                {
                    var masterPos = _sync.GetMasterPosition100ns();
                    if (masterPos > 0)
                        slot.Session.Seek(masterPos + slot.Offset100ns);
                }
                TrySettle(batch);
            }
            else
            {
                DiscardBatch(batch);
            }
        }
        catch (Exception ex)
        {
            // 旧实现这里只改状态不打日志，排查时全靠猜（F4）
            if (!_closed)
                AppLog.Warn("Coordinator", $"[{Path.GetFileName(path)}] 打开失败: {ex.GetType().Name}: {ex.Message}");
            slot.Failed = true;
            slot.Error = ex.Message;
            if (!_closed)
            {
                surface.IsFailed = true;
                surface.ErrorText = ex.Message;
                TrySettle(batch); // 失败路也计入完成，避免卡住
            }
            else
            {
                DiscardBatch(batch);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>真实模式等待 3FP 后端真正就绪；失败/超时(15s)也视为完成（标记失败）。</summary>
    private async Task WaitForOpenCompletionAsync(SyncController.SyncSlot slot, PlayerSurface surface)
    {
        // 就绪通知改事件驱动（08 计划 §4.2）：内核 OpenCompleted 到达即提前结束等待。
        // deadline 是整个等待的"唯一时间基准"，事件窗口只占开头一段 —— 见文件头的 F1 说明。
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEngineEvent(object? _, EngineEvent evt)
        {
            if (evt.Type == EngineEventType.OpenCompleted) readyTcs.TrySetResult(true);
        }
        slot.Session.EngineEvent += OnEngineEvent;

        var token = CancelToken;
        var deadline = DateTime.UtcNow + OpenTotalBudget;
        try
        {
            var eventWindow = Task.Delay(OpenEventWindow, token);
            // WhenAny 不会把内部任务的异常/取消抛出来，所以取消要下面自己判一次。
            await Task.WhenAny(readyTcs.Task, eventWindow);
            if (token.IsCancellationRequested) return;
        }
        finally
        {
            slot.Session.EngineEvent -= OnEngineEvent;
        }

        // 兜底/确认轮询：事件先行时下一轮几乎立即命中 Ready；
        // 事件丢失时按剩余预算继续轮询 —— 后端 2s 就 Ready 的情况能被救回（F1）。
        try
        {
            while (DateTime.UtcNow < deadline && !_closed && !token.IsCancellationRequested)
            {
                try
                {
                    var snap = slot.Session.ReadSnapshot();
                    if (IsReadyState(snap.State))
                    {
                        // 组件日志：单路就绪（真实模式下唯一的逐路就绪观测点）。
                        // 与 SessionOpen/RouteCountChanged 一起可还原"哪一路何时可用"。
                        _3FCompare.Diagnostics.ComponentLog.Log(
                            _3FCompare.Diagnostics.Comp.Engine, "RouteReady", surface.Index,
                            $"state={snap.State}");
                        return;
                    }
                    if (snap.State == PlayerState.Failed)
                    {
                        slot.Failed = true;
                        slot.Error = "内核打开失败";
                        surface.IsFailed = true;
                        surface.ErrorText = slot.Error;
                        return;
                    }
                }
                catch
                {
                    // 快照读取失败：继续等
                }
                await Task.Delay(OpenPollInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 关窗/取消：直接结束，不判定失败
            return;
        }

        if (!_closed)
        {
            slot.Failed = true;
            slot.Error = "打开超时（后端未就绪）";
            surface.IsFailed = true;
            surface.ErrorText = slot.Error;
        }
    }

    private void HandleEngineEvent(SyncController.SyncSlot slot, PlayerSurface surface, EngineEvent evt)
    {
        switch (evt.Type)
        {
            case EngineEventType.Error:
                // 用 System.Text.Json 解析 state 字段，避免字符串包含匹配的脆弱性
                var isFailure = false;
                const int FailureState = (int)PlayerState.Failed; // 内核 Failed=6
                if (!string.IsNullOrEmpty(evt.DetailJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(evt.DetailJson);
                        if (doc.RootElement.TryGetProperty("state", out var stateEl) &&
                            stateEl.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            stateEl.GetInt32() == FailureState)
                            isFailure = true;
                        else if (doc.RootElement.TryGetProperty("reason", out var reasonEl) &&
                                 reasonEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                                 (reasonEl.GetString()?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true))
                            isFailure = true;
                    }
                    catch
                    {
                        // JSON 解析失败时回退到字符串匹配
                        isFailure = evt.DetailJson.Contains($"\"state\":{FailureState}", StringComparison.OrdinalIgnoreCase) ||
                                    evt.DetailJson.Contains("fail", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (isFailure)
                {
                    slot.Failed = true;
                    slot.Error = $"内核错误: {evt.DetailJson}";
                    surface.IsFailed = true;
                    surface.ErrorText = slot.Error;
                }
                break;
            case EngineEventType.PlaybackEnded:
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case EngineEventType.DeviceChanged:
                // 显卡/显示器变更：不自动重建（易误触发），提示用户手动恢复。
                // 但抓屏线路要重来一次（S4）：GPU 变更后原有的 WGC 会话与失败计数都已失去意义，
                // 继续沿用降级标记会让用户在"手动重试"后仍然拿到 GDI 的错内容。
                _3FCompare.App.Capture.FrameCapture.ResetRouting();
                // 组件日志：设备/显示器变化。它会让抓屏线路重来一次，也常伴随
                // 交换链/设备重建 —— 是"崩溃前发生了什么"的高价值锚点。
                _3FCompare.Diagnostics.ComponentLog.Log(
                    _3FCompare.Diagnostics.Comp.Engine, "DeviceChanged", -1,
                    $"{Path.GetFileName(slot.Path)} {evt.DetailJson}");
                AppLog.Warn("Coordinator", $"[{Path.GetFileName(slot.Path)}] 显卡/显示器变更: {evt.DetailJson}");
                surface.ErrorText = "检测到显卡/显示器变更，如画面异常请右键该路重试";
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case EngineEventType.ColorModeChanged:
                // HDR/SDR 模式切换：刷新诊断面板（快照会带出新的 colorMode）
                // 组件日志：色彩模式切换会重建呈现资源，同样是高风险结构变化
                _3FCompare.Diagnostics.ComponentLog.Log(
                    _3FCompare.Diagnostics.Comp.Engine, "ColorModeChanged", -1,
                    $"{Path.GetFileName(slot.Path)} {evt.DetailJson}");
                AppLog.Info("Coordinator", $"[{Path.GetFileName(slot.Path)}] 色彩模式切换: {evt.DetailJson}");
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public static bool IsReadyState(PlayerState state)
        => state is PlayerState.Ready or PlayerState.Playing or PlayerState.Paused;

    public void Close()
    {
        _closed = true;
        // 先取消在飞的等待（Task.Delay 都带令牌），再释放 CTS 的定时器注册，避免泄漏（F3）。
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放（重复 Close） */ }
        _cts.Dispose();
        lock (_batchLock)
        {
            foreach (var b in _batches)
            {
                Interlocked.Exchange(ref b.OnAllOpened, null);
                Interlocked.Exchange(ref b.Remaining, 0);
            }
            _batches.Clear();
        }
    }
}
