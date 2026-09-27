using System.Text;
using Avalonia.Input;
using _3FCompare.App;
using _3FCompare.Core.Settings;

namespace _3FCompare.Services;

/// <summary>播放器可自定义快捷键的 UI 侧原语：<b>解析</b>设置里的键名字符串、<b>展示</b>成可读键名、
/// 提供<b>固定键位表</b>（哪些键被硬编码分支占着、不可绑定）、构建 MainWindow 用的<b>分派表</b>。
///
/// <para><b>为什么单独一个文件</b>：MainWindow（按键分派）与 SettingsWindow（改键界面）都要用这三件事，
/// 而"这个键到底归谁"必须是同一个答案。两份实现必然漂移，漂移的表现就是
/// "设置页说这个键空闲、按下去却触发了别的动作"（或反过来：明明能绑却报冲突）。</para>
///
/// <para><b>与 Core 的分工</b>：<see cref="KeyBindingsSettings"/> 存的是键名字符串
/// （Core 不引用 Avalonia，没有 <c>Key</c> 枚举可用），并在那里做加载级的规范形与字符串级冲突消解；
/// 本类负责把字符串变成 <c>(Key, KeyModifiers)</c>。字符串合法但枚举名不认识
/// （手改配置文件写了 <c>"Banana"</c>）时按<b>未绑定</b>处理并写日志，绝不抛。</para></summary>
public static class TransportKeys
{
    /// <summary>可绑定的播放动作。
    ///
    /// <para><b>声明顺序必须与 <see cref="KeyBindingsSettings"/> 的属性顺序一致</b>，它同时决定：
    /// ① 设置页的行序（照底栏从左到右：按秒退 / 按帧退 / 播放暂停 / 按帧进 / 按秒进 的槽序排列，
    /// 与 Core 侧的槽序一一对应）；② 同一个键被绑给两个动作时谁赢（UI 与 Core 都按此序裁决）。</para>
    /// <para>新增动作请<b>加在末尾</b>，插在中间会静默改变既有键位的冲突结果。</para></summary>
    public enum Slot
    {
        /// <summary>按秒后退（跳 <see cref="AppSettings.SecondsStep"/> 秒）。</summary>
        StepSecondBackward,
        /// <summary>按秒前进。</summary>
        StepSecondForward,
        /// <summary>按帧后退（跳 <see cref="AppSettings.FrameStep"/> 帧）。</summary>
        StepFrameBackward,
        /// <summary>按帧前进。</summary>
        StepFrameForward,
        /// <summary>播放/暂停切换。</summary>
        PlayPause,
        /// <summary>停止（底栏按钮已删除 ⇒ 默认未绑定，只能由用户在设置页显式绑键）。</summary>
        Stop,
    }

    /// <summary>全部槽位（= 设置页行序 = 冲突优先级）。刻意手写而不是 <c>Enum.GetValues</c>：
    /// 反射在 NativeAOT 下不保证可用，而这里就 6 项。</summary>
    public static readonly Slot[] Slots =
    {
        Slot.StepSecondBackward, Slot.StepSecondForward, Slot.StepFrameBackward,
        Slot.StepFrameForward, Slot.PlayPause, Slot.Stop,
    };

    /// <summary>参与键位匹配的修饰键位。<b>刻意做一次掩码</b>：某些平台/键盘状态会往
    /// <see cref="KeyEventArgs.KeyModifiers"/> 里塞 Function、左右侧之类的附加位，
    /// 不掩码就会表现为"存的是 Ctrl+Left、按下算成 Ctrl+Left+附加位 ⇒ 匹配不上"，
    /// 用户看到的是"键设了却没反应"，且没有任何线索。</summary>
    public const KeyModifiers RelevantModifiers =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;

    /// <summary>把修饰键位收敛到 <see cref="RelevantModifiers"/>（写入表与查表两侧必须走同一个函数）。</summary>
    public static KeyModifiers Normalize(KeyModifiers mods) => mods & RelevantModifiers;

    /// <summary>读某一槽的键名字符串。</summary>
    public static string Get(Slot slot, KeyBindingsSettings s) => slot switch
    {
        Slot.StepSecondBackward => s.StepSecondBackward,
        Slot.StepSecondForward => s.StepSecondForward,
        Slot.StepFrameBackward => s.StepFrameBackward,
        Slot.StepFrameForward => s.StepFrameForward,
        Slot.PlayPause => s.PlayPause,
        Slot.Stop => s.Stop,
        _ => s.PlayPause,   // 不可达：Slot 只有上面 6 个成员
    };

    /// <summary>写某一槽的键名字符串。</summary>
    public static void Set(Slot slot, KeyBindingsSettings s, string value)
    {
        switch (slot)
        {
            case Slot.StepSecondBackward: s.StepSecondBackward = value; break;
            case Slot.StepSecondForward: s.StepSecondForward = value; break;
            case Slot.StepFrameBackward: s.StepFrameBackward = value; break;
            case Slot.StepFrameForward: s.StepFrameForward = value; break;
            case Slot.PlayPause: s.PlayPause = value; break;
            case Slot.Stop: s.Stop = value; break;
        }
    }

    /// <summary>某一槽的出厂默认键名（唯一来源在 Core 的常量里，这里只做映射，
    /// 免得"恢复默认"按钮和设置模型各存一份而对不上）。</summary>
    public static string DefaultOf(Slot slot) => slot switch
    {
        Slot.StepSecondBackward => KeyBindingsSettings.DefaultStepSecondBackward,
        Slot.StepSecondForward => KeyBindingsSettings.DefaultStepSecondForward,
        Slot.StepFrameBackward => KeyBindingsSettings.DefaultStepFrameBackward,
        Slot.StepFrameForward => KeyBindingsSettings.DefaultStepFrameForward,
        Slot.PlayPause => KeyBindingsSettings.DefaultPlayPause,
        Slot.Stop => KeyBindingsSettings.DefaultStop,
        _ => KeyBindingsSettings.DefaultPlayPause,
    };

    /// <summary>某一槽的本地化键名（两张语言表都要有对应条目，
    /// <c>LanguageManagerKeysTests</c> 会钉住键集一致）。</summary>
    public static string LabelKey(Slot slot) => "Keys_" + slot;

    /// <summary>键名字符串 → <c>(Key, KeyModifiers)</c>。空串/空白/解析失败一律返回 false
    /// （调用方按"未绑定"处理）。</summary>
    public static bool TryResolve(string? raw, out Key key, out KeyModifiers mods)
    {
        key = Key.None;
        mods = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        // 最后一个 token 是键名，前面全是修饰键；任一个修饰键拼错就整体判失败
        // （不做"丢掉修饰键、保留裸键"的降级 —— 那会让用户以为绑的是 Ctrl+A，实际占用了裸 A）
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryModifier(parts[i], out var m)) return false;
            mods |= m;
        }

        // ⚠ Enum.TryParse 对**数字串**也会成功（"12" → (Key)12，一个未定义的枚举值），
        // 所以还要用"名字回写一致"钉住它确实是合法键名。不用 Enum.IsDefined：
        // 那是反射，NativeAOT 下不保证可用（同 AppSettings.Normalize 的注释）。
        // 顺带的效果：别名写法（如 "Number1" 这种与 D1 同值的旧名）会被拒掉，
        // 于是"两个不同写法解析成同一个键"这类冲突在源头上就不存在。
        if (!Enum.TryParse(parts[^1], ignoreCase: true, out key)) return false;
        if (!string.Equals(key.ToString(), parts[^1], StringComparison.OrdinalIgnoreCase)) return false;
        if (key == Key.None) return false;

        mods = Normalize(mods);
        return true;
    }

    private static bool TryModifier(string token, out KeyModifiers mods)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl" or "control": mods = KeyModifiers.Control; return true;
            case "shift": mods = KeyModifiers.Shift; return true;
            case "alt" or "menu": mods = KeyModifiers.Alt; return true;
            case "win" or "meta" or "cmd": mods = KeyModifiers.Meta; return true;
            default: mods = KeyModifiers.None; return false;
        }
    }

    /// <summary>把一次实际按键写成设置里的键名字符串（<see cref="TryResolve"/> 的逆操作）。</summary>
    public static string ToSpec(Key key, KeyModifiers mods)
    {
        var sb = new StringBuilder();
        AppendModifier(sb, mods, KeyModifiers.Control, "Ctrl");
        AppendModifier(sb, mods, KeyModifiers.Shift, "Shift");
        AppendModifier(sb, mods, KeyModifiers.Alt, "Alt");
        AppendModifier(sb, mods, KeyModifiers.Meta, "Win");
        sb.Append(key);
        return sb.ToString();
    }

    /// <summary>把已有字符串换成规范写法（<c>"space"</c> → <c>"Space"</c>、
    /// <c>"ctrl + left"</c> → <c>"Ctrl+Left"</c>）；解析不出来的原样返回。
    /// <para>设置页用它初始化每一行的值：行里存的字符串与"按一次键写出来的字符串"
    /// 必须是同一形态，否则只是打开设置又点确定就会被判成"改动过"，白写一次盘
    /// （而用户以为是自己改坏了配置）。</para></summary>
    public static string Canonical(string raw)
        => TryResolve(raw, out var key, out var mods) ? ToSpec(key, mods) : raw;

    private static void AppendModifier(StringBuilder sb, KeyModifiers mods, KeyModifiers flag, string name)
    {
        if (!Normalize(mods).HasFlag(flag)) return;
        sb.Append(name).Append('+');
    }

    /// <summary>某一槽当前绑定的展示文本（"已设置且解析失败"时原样显示字符串本身 ——
    /// 让用户看得见配置里写了个什么东西，而不是静默显示"未绑定"）。</summary>
    public static string Display(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return LanguageManager.T("Keys_Unbound");
        return TryResolve(raw, out var key, out var mods) ? Display(key, mods) : raw;
    }

    /// <summary>键位展示：方向键用箭头符号、空格按语言给词、其余取枚举名（字母/数字/F11 原样）。</summary>
    public static string Display(Key key, KeyModifiers mods)
    {
        var sb = new StringBuilder();
        AppendModifier(sb, mods, KeyModifiers.Control, "Ctrl");
        AppendModifier(sb, mods, KeyModifiers.Shift, "Shift");
        AppendModifier(sb, mods, KeyModifiers.Alt, "Alt");
        AppendModifier(sb, mods, KeyModifiers.Meta, "Win");
        sb.Append(KeyName(key));
        return sb.ToString();
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Space => LanguageManager.T("Keys_Space"),
        _ => key.ToString(),
    };

    // ──────── 固定键位表（不可绑定的那些） ────────

    /// <summary>带修饰键守卫的硬编码组合：只有**这一组合**被占，裸键仍可绑。</summary>
    private static readonly HashSet<(Key, KeyModifiers)> ReservedExact = new()
    {
        (Key.O, KeyModifiers.None),          // 打开视频
        (Key.O, KeyModifiers.Control),       // 打开视频（菜单标注的同一条）
        (Key.S, KeyModifiers.Control),       // 导出当前帧
        // 裸 B 已随「A-B 滑块视图」一起退场（MainWindow.OnKeyDown 里那条分支没了 ⇒ 本表按"同一份事实"
        // 的约定必须跟着撤）。它仍会被时间轴的 A/B 打点消费，但那与裸 A 完全同形——A 也一直可绑。
        (Key.C, KeyModifiers.None),          // 对比布局循环（A/B 可拖动内的变体细调）
        (Key.S, KeyModifiers.None),          // 左右拉动对比
        (Key.V, KeyModifiers.None),          // A/B 可拖动对比
        (Key.G, KeyModifiers.None),          // 标准模式
        (Key.T, KeyModifiers.None),          // 折叠时间轴
        (Key.T, KeyModifiers.Shift),         // 折叠状态栏
        (Key.P, KeyModifiers.None),          // 像素探针
        (Key.R, KeyModifiers.None),          // 重置视图
        (Key.H, KeyModifiers.Control),       // 侧栏三态
        (Key.F11, KeyModifiers.None),        // 全屏
        (Key.Escape, KeyModifiers.None),     // 退出全屏；且设置页用它取消捕获
    };

    /// <summary>硬编码分支写成 <c>mods.HasFlag(KeyModifiers.Control)</c> 的那些键：
    /// 只要按下的修饰键里**含** Ctrl（哪怕再多按了 Alt），分支就会命中 ⇒ 该键的
    /// 一切含 Ctrl 的组合都算被占，绑过去永远不会触发。</summary>
    private static readonly HashSet<Key> ReservedUnderControl = new()
    {
        Key.S,    // 导出当前帧
        Key.H,    // 侧栏三态
    };

    /// <summary>不带修饰键守卫的硬编码分支：该键的**所有**修饰组合都已被占。
    /// （<c>case Key.Up:</c> 这种写法不区分 Ctrl/Shift，绑给它任何一个组合都会被硬编码吃掉。）</summary>
    private static readonly HashSet<Key> ReservedAnyModifiers = new()
    {
        Key.Up, Key.Down,                     // ±10 秒（规格要求保留，不动）
        Key.F6,                               // 偏移校准
        Key.Delete,                           // 删书签
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, // 加 lane
    };

    /// <summary>这个键位是否被<b>非设置来源</b>的硬编码分支占用（因而不可绑定）。
    /// 判定必须与 MainWindow.OnKeyDown 的硬编码分支保持同一份事实 —— 见本文件顶部"为什么单独一个文件"。</summary>
    public static bool IsReserved(Key key, KeyModifiers mods)
    {
        var m = Normalize(mods);
        return ReservedAnyModifiers.Contains(key)
            || ReservedExact.Contains((key, m))
            || (m.HasFlag(KeyModifiers.Control) && ReservedUnderControl.Contains(key));
    }

    // ──────── 分派表 ────────

    /// <summary>把 6 个槽解析成 MainWindow 用的分派表。三条规则：
    /// <list type="number">
    /// <item><description><b>未绑定不参与分派</b>：空串、或解析不出枚举的字符串都不进表，
    /// 并各写一条日志（"按了没反应"必须能归因）；</description></item>
    /// <item><description><b>同键后者不赢</b>：<c>TryAdd</c> 失败即丢弃并写 Warn。
    /// <see cref="KeyBindingsSettings.Normalize"/> 已在加载时按槽序消解过一次<b>字符串级</b>冲突，
    /// 这里兜的是残余的"写法不同但解析成同一个键"的情况；</description></item>
    /// <item><description><b>与硬编码冲突的不进表</b>：MainWindow 只在 switch 的 default 臂查本表，
    /// 所以硬编码优先是<b>结构性</b>的；这里再挡一道，只为把"为什么你这个绑定没生效"写进日志。</description></item>
    /// </list></summary>
    public static Dictionary<(Key, KeyModifiers), Slot> BuildMap(KeyBindingsSettings? bindings)
    {
        // 参数刻意可空：只有"手改配置文件写成 null 且没跑过 Normalize"这一条路能给出 null，
        // 为它抛异常不划算（表现为启动即崩），回落到全默认绑定即可。
        var s = bindings ?? new KeyBindingsSettings();
        var map = new Dictionary<(Key, KeyModifiers), Slot>();
        foreach (var slot in Slots)
        {
            var raw = Get(slot, s);
            if (string.IsNullOrEmpty(raw)) continue;              // 未绑定：静默跳过是正当的（用户自己清的）
            if (!TryResolve(raw, out var key, out var mods))
            {
                _3FCompare.Core.Diagnostics.AppLog.Warn("KeyBindings",
                    $"{slot} 的键位 \"{raw}\" 不是可识别的键名，按未绑定处理");
                continue;
            }
            if (IsReserved(key, mods))
            {
                _3FCompare.Core.Diagnostics.AppLog.Warn("KeyBindings",
                    $"{slot} 绑定的 {Display(key, mods)} 属于固定功能键，硬编码优先 ⇒ 本次绑定不生效");
                continue;
            }
            if (!map.TryAdd((key, mods), slot))
            {
                _3FCompare.Core.Diagnostics.AppLog.Warn("KeyBindings",
                    $"{slot} 与更早声明的槽撞在 {Display(key, mods)} 上 ⇒ 后加载的不生效（先声明者赢）");
            }
        }
        return map;
    }

    /// <summary>逐槽把 <paramref name="from"/> 的键名抄进 <paramref name="to"/>。
    /// <para>必须逐槽赋值而不是整块引用赋值：<c>AppSettings.KeyBindings</c> 是引用类型，
    /// 共享实例会让"取消设置"或后续改动串到主窗口的 _settings 上（本仓库在
    /// <c>SettingsWindow._orig</c> 的注释里已经为同样的坑留过警告）。</para></summary>
    public static void CopyInto(KeyBindingsSettings from, KeyBindingsSettings to)
    {
        foreach (var slot in Slots) Set(slot, to, Get(slot, from));
    }

    /// <summary>两套绑定是否逐槽相同（设置窗口据此判断"要不要落盘"，
    /// 漏判的表现就是改了快捷键点确定却没保存）。</summary>
    public static bool Same(KeyBindingsSettings a, KeyBindingsSettings b)
    {
        foreach (var slot in Slots)
            if (!string.Equals(Get(slot, a), Get(slot, b), StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>整套绑定的日志摘要（改键生效时打一条，便于事后回答"当时按的到底是什么键"）。</summary>
    public static string Summary(KeyBindingsSettings s)
        => string.Join(' ', Slots.Select(slot => $"{slot}={Get(slot, s)}"));
}
