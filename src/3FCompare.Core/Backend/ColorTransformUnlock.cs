using System.Threading;
using System.Threading.Tasks;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Core.Backend;

/// <summary>
/// 「模块在盘上但没解锁」时，宿主 UI 收解锁码的**唯一收口**。
///
/// <para><b>为什么必须有这一层</b>：Core 不认识 Avalonia，而"弹一个输入框"只能是 UI 的事；
/// 同时这件事又必须由"装载失败"那一侧发起。于是把请求做成可注入的委托：
/// 谁有 UI 谁注册，没注册就等于无人值守（自测 / 无头进程）⇒ <b>恒不提示</b>。</para>
///
/// <para><b>三条口径</b>：
/// <b>① 每进程至多一次</b>——9 路会话会各自走到"未解锁"，没有闩就会弹 9 个框；
/// <b>② 没人注册就永远不提示</b>——自测与无人值守的依赖，也是"没 DLL 不提示"的另一半保险；
/// <b>③ 解锁码本身绝不进日志</b>——只记"请求了 / 成功了 / 拒绝了"。</para>
///
/// <para>委托的返回值是"最终是否解锁成功"：输码、校验、重试、放弃这一整套闭环都在 UI 里
/// （UI 才知道怎么把"码不对"说给人听），Core 只关心结果，成功后重新装载即可。</para>
/// </summary>
public static class ColorTransformUnlock
{
    private static Func<Task<bool>>? _requester;
    private static int _requested;

    /// <summary>注册请求器（UI 启动时调一次）。传 null 即注销（回到"无人值守"）。</summary>
    public static void RegisterRequester(Func<Task<bool>>? requester) =>
        Interlocked.Exchange(ref _requester, requester);

    /// <summary>本进程是否已经请求过（诊断用：自测里断言"缺席时不该请求"）。</summary>
    public static bool HasRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>请求一次解锁。<b>每进程至多真的请求一次</b>；重复调用返回 false。
    /// 无人注册 / 请求器抛异常 ⇒ false（静默，不留痕到用户面前）。</summary>
    public static async Task<bool> RequestOnceAsync()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0) return false;   // 已经请求过

        var requester = Volatile.Read(ref _requester);
        if (requester is null)
        {
            // 无人值守（自测 / 服务 / 还没挂 UI）：这是设计态，不是故障。
            AppLog.Debug("ColorTransform", "模块未解锁，但宿主没有解锁入口（无人值守）⇒ 保持静默");
            return false;
        }

        try
        {
            var unlocked = await requester().ConfigureAwait(false);
            AppLog.Info("ColorTransform", unlocked
                ? "解锁码已被模块接受"
                : "解锁未完成（用户取消或码不对）⇒ 本次按基础层出画");
            return unlocked;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ColorTransform", $"解锁请求器抛异常（已忽略）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>仅供测试：把闩与请求器复位（生产路径永不调用）。</summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref _requested, 0);
        Interlocked.Exchange(ref _requester, null);
    }
}
