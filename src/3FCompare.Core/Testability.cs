using System.Runtime.CompilerServices;

// docs/41 §4.5 配套基建：让 Core.Tests 能直接测 internal 成员。
//
// 为什么用 InternalsVisibleTo 而不是把被测成员改成 public：
// public 等于把"内部实现细节"升格成公共 API 的一部分——此后改名/改签名都成了破坏性变更，
// 而它们本不该有任何外部消费者。典型例子是 Fff3FpEngine.ParseMediaInfoJson：
// 它是"3FP 返回的 JSON → EngineMediaInfo"的**唯一**解析入口（媒体信息面板的唯一数据来源），
// 却因为 private 而完全无测试覆盖（docs/41 §4.4 A 类）。改为 internal + 本特性即可覆盖，
// 且不会对外暴露。
[assembly: InternalsVisibleTo("3FCompare.Core.Tests")]

namespace _3FCompare.Core;

/// <summary>
/// 时间源（可注入）。存在的唯一理由：让依赖"当前时间"的逻辑能被**确定性**测试。
/// </summary>
/// <para>此前 `SyncControllerIntegrationTests` 用 `Thread.Sleep(1050)` 等真实挂钟
/// （docs/41 §4.3 列为质量问题）——既慢又 flaky（机器卡顿时判红）。
/// 注入 <see cref="IClock"/> 后，测试可以手动推进时间，把"等 1 秒"变成"跳 1 秒"。</para>
/// <para><b>边界</b>：本接口只用于"读取当前时刻"，不提供定时器/延迟——
/// 定时器另有 `System.Threading.Timer` 等机制，混进来会让 seam 变得难以替身。</para>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

// ⚠ 曾经的 SystemClock（真实挂钟）已删除（docs/45 P2）：全仓零引用，且它的存在会误导——
// 生产默认走的是 Environment.TickCount64（**单调**、不受系统时间调整影响，见 SyncController
// 的 _nowMs 注释），不是挂钟。留一个"生产用时间源"却从不注入，等于给读者一份错的地图：
// 真正需要注入的是测试侧的 ManualClock。若将来确有按挂钟计时的逻辑，再按该语义引入，
// 不要复活一个"通用但没人用"的实现。
