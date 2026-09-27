using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace _3FCompare.Core.Backend;

/// <summary>原生运行时路径管理：手动指定 FFmpeg DLL 目录、自释放嵌入的 FFF.Native.dll。
/// 将指定目录加入 DLL 搜索路径（通过 SetDllDirectory），不再复制 DLL 到应用目录。</summary>
public static partial class NativeRuntime
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    /// <summary>当前生效的 FFmpeg 目录（null = 未手动指定，走自动检测）。</summary>
    public static string? FfmpegDirectory { get; private set; }

    /// <summary>从嵌入资源释放 FFF.Native.dll 到应用目录（如果尚未存在或内容不同）。
    /// 应当只在 MainForm 启动时调用一次，在 EngineFactory 探测之前。</summary>
    public static void ExtractEmbeddedDll(Func<string, byte[]?> resourceLoader)
    {
        var targetDir = AppContext.BaseDirectory;
        if (!Directory.Exists(targetDir)) return;

        var targetPath = Path.Combine(targetDir, "FFF.Native.dll");
        try
        {
            var data = resourceLoader("FFF.Native.dll");
            if (data is null)
            {
                // 无可嵌入资源：保持现有 DLL，但必须留痕——静默跳过会让
                // "内核更新不生效"这类问题无从排查（如 csproj 内嵌路径失效）。
                if (File.Exists(targetPath))
                    Diagnostics.AppLog.Warn("NativeRuntime", "嵌入资源中找不到 FFF.Native.dll，保留磁盘上的现有版本");
                return;
            }

            if (File.Exists(targetPath))
            {
                // ⚠ 磁盘上已存在时**绝不覆盖**（2026-09-16 事故）：
                // 内核由 `构建全部.ps1` 单独构建并落盘，实际构建 SHA 记在 `.3fc_kernel_sha`，
                // 与托管侧的发布批次是**两件事**。内嵌资源只是"没有内核时的兜底"，
                // 它通常是**更旧**的基线。一旦按"哈希不同就覆盖"，就会把用户磁盘上
                // 刚构建好的新版内核降级成旧基线 —— 实测把 API 15 覆盖成 API 14，
                // 而托管 ConfigVersion=15，FFF3FP_Create 对版本严格相等 ⇒
                // 所有会话创建失败，表现为"未就绪 / 找不到入口点 FFF3FP_SetLogCallback"。
                // 因此这里只留痕（便于排查"内核没更新"），写盘仅限缺失时。
                var diskHash = TryComputeSha256(targetPath);
                Diagnostics.AppLog.Info("NativeRuntime",
                    diskHash is not null && diskHash.AsSpan().SequenceEqual(SHA256.HashData(data))
                        ? "FFF.Native.dll 与内嵌资源一致，保留磁盘版本（不写盘）"
                        : "FFF.Native.dll 与内嵌资源不一致；按约定保留磁盘版本（内核由构建脚本管理，" +
                          "内嵌资源仅作缺失兜底）。若需更新内核请重跑 构建全部.ps1");
                return;
            }

            File.WriteAllBytes(targetPath, data);
            Diagnostics.AppLog.Info("NativeRuntime", $"已释放 FFF.Native.dll ({data.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("NativeRuntime", $"释放 FFF.Native.dll 失败: {ex.Message}");
        }
    }

    /// <summary>设置 FFmpeg DLL 搜索目录（null/空白 = 清除手动设置，恢复自动检测）。
    /// 将指定目录加入 DLL 搜索路径，使内核 Delay-Load 可命中（不再复制 DLL 到应用目录）。</summary>
    public static void SetFfmpegDirectory(string? directory)
    {
        var trimmed = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        if (trimmed is not null && !IsAcceptableFfmpegDirectory(trimmed, out var reason))
        {
            // 配置写在 exe 同目录以支撑便携部署，等同"可被投放"，所以要校验后再用：
            // 否则一个 settings.json 就能让内核去加载任意目录里的同名 DLL（docs/15 §5.2）。
            Diagnostics.AppLog.Warn("NativeRuntime", $"忽略不可用的 FFmpeg 目录 “{trimmed}”：{reason}");
            trimmed = null;
        }
        FfmpegDirectory = trimmed;
        // 传 null 会恢复默认搜索顺序，所以"被拒绝"时必须显式清掉上一次的设置
        ApplyDllDirectory(FfmpegDirectory);
        // 目录变了 ⇒ 上一次"内核是否可用"的结论失效。不重置的话用户改完目录
        // 会一直看到重启前的探测结果（EngineFactory.IsNativeAvailable 是进程级缓存）。
        EngineFactory.ResetNativeProbe();
    }

    /// <summary>把目录写入 DLL 搜索路径，并检查返回值。
    /// 此前从不检查 SetDllDirectoryW 的成败：一旦它失败（路径过长、权限、路径格式），
    /// 表现是"配了目录却加载不到 avcodec"，而日志里什么都没有，只能靠猜。</summary>
    private static void ApplyDllDirectory(string? directory)
    {
        if (SetDllDirectoryW(directory)) return;
        var error = Marshal.GetLastWin32Error();
        Diagnostics.AppLog.Warn("NativeRuntime",
            $"SetDllDirectoryW 失败（路径='{directory ?? "(null)"}'），Win32 错误码={error}；" +
            "FFmpeg 的 Delay-Load 可能解析不到 avcodec");
    }

    /// <summary>FFmpeg 目录是否可接受。判据见下，任一不满足即拒绝。</summary>
    private static bool IsAcceptableFfmpegDirectory(string dir, out string reason)
        // 用户显式指定的目录：安全闸门只警告不拒绝（见 PassesSecurityGate 注释）
        => IsAcceptableFfmpegDirectory(dir, autoDetect: false, out reason);

    /// <param name="dir">候选目录。</param>
    /// <param name="autoDetect">true = PATH 自动检测（安全闸门不通过即拒绝）；
    /// false = 用户显式指定（安全闸门不通过只记日志，仍采纳）。</param>
    /// <param name="reason">不可用时的人类可读原因；可用时为 ""。</param>
    private static bool IsAcceptableFfmpegDirectory(string dir, bool autoDetect, out string reason)
    {
        // ① 空/空白 与 UNC/设备路径：**两种不同的拒绝原因，提示必须分开给**。
        //    空白串没有网络语义，若并进下面那条 UNC 文案，用户会去查网络/共享，
        //    而真实原因是 settings.json 里这一项是空的（排查方向被带偏）。
        //    这只是把提示拆细：空白在共用判据里同样返回 true（fail-closed），
        //    先判空白不改变任何拒绝/采纳结论，安全面既不削弱也不扩大。
        if (string.IsNullOrWhiteSpace(dir))
        {
            reason = "未配置 FFmpeg 目录（路径为空或只有空白字符）";
            return false;
        }
        //    UNC：Windows 访问网络路径时会自动发起 NTLM 认证，把本机凭据送出去。
        //    必须走 PathSafety.IsUncPath 而不是 dir.StartsWith(@"\\")：
        //    "//server/share" 也是 rooted 但不以 "\\" 开头，只查前缀会被它整体绕过；
        //    共用判据内部先 GetFullPath 归一化（// 与 \\ 归一后形式唯一）再判，
        //    并把非法字符/超长这类归一化失败按"不可信"拒绝（fail-closed）。
        //    注：这里的目录来自 settings.json（与 exe 同目录，等同可被投放），
        //    与会话文件里的视频路径是**不同的可信边界**，所以只共用判据、不共用提示与策略。
        if (PathSafety.IsUncPath(dir))
        {
            reason = "不接受 UNC/设备路径或无法归一化的路径（访问网络路径会自动发起 NTLM 认证，泄露本机凭据）";
            return false;
        }
        // ② 相对路径：会按当前工作目录解析，等于让配置决定加载哪个目录的 DLL
        if (!Path.IsPathRooted(dir))
        {
            reason = "必须是绝对路径";
            return false;
        }
        if (!Directory.Exists(dir))
        {
            reason = "目录不存在";
            return false;
        }
        // ③ 目录里必须有 avcodec 核心 DLL。
        //    设置这个目录的唯一目的就是让内核 Delay-Load 命中 avcodec；
        //    没有它说明配错了，同时也能挡住"指向任意目录去加载同名 DLL"。
        try
        {
            if (!System.IO.Directory.EnumerateFiles(dir, "avcodec-*.dll").Any())
            {
                reason = "目录中没有 avcodec-*.dll";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"无法列举目录：{ex.Message}";
            return false;
        }
        // ④ 安全闸门：可写性 / 签名（详见 PassesSecurityGate）
        if (!PassesSecurityGate(dir, autoDetect, out var gateReason))
        {
            reason = gateReason;
            return false;
        }
        if (gateReason.Length > 0)
        {
            // 用户显式指定：记日志但仍采纳。硬拒会破坏"我自己编译/自签名的 FFmpeg"
            // 这类合法部署，这类场景应由用户自行判断风险。
            Diagnostics.AppLog.Warn("NativeRuntime",
                $"采纳存在风险的 FFmpeg 目录 “{dir}”：{gateReason}（用户显式指定，未阻断）");
        }
        reason = "";
        return true;
    }

    /// <summary>安全闸门（在既有"绝对路径/非 UNC/存在/含 avcodec"基础之上<b>追加</b>，不削弱既有检查）。
    /// <para>威胁模型：SetDllDirectory 会让内核 Delay-Load <b>优先</b>命中该目录里的
    /// <c>avcodec-*.dll</c>。PATH 上任何一个当前用户可写的目录被投放伪造 DLL ⇒ 任意代码执行。</para>
    /// <para>autoDetect=true（PATH 自动检测）：任一项不通过即拒绝，调用方继续找下一个候选目录。
    /// autoDetect=false（用户显式指定）：只写日志，不改判定结果。</para></summary>
    private static bool PassesSecurityGate(string dir, bool autoDetect, out string reason)
    {
        var problems = new List<string>();

        // ① 可写性：能往里放文件 = 能替换 avcodec。
        if (IsDirectoryWritable(dir))
            problems.Add("目录对当前用户可写（可被投放伪造 DLL）");

        // ② Authenticode 签名：正规发行的 FFmpeg DLL 是签名的；
        //    未通过校验说明来源不明（自制构建通常无签名，所以只对自动检测硬拒）。
        string? avcodec = null;
        try
        {
            avcodec = Directory.GetFiles(dir, "avcodec-*.dll").FirstOrDefault()
                      ?? Directory.GetFiles(dir, "avcodec*.dll").FirstOrDefault();
        }
        catch (Exception ex)
        {
            problems.Add($"无法列举 avcodec DLL：{ex.Message}");
        }
        if (avcodec is not null)
        {
            try
            {
                if (new FileInfo(avcodec).Length <= 0)
                    problems.Add("avcodec DLL 是空文件");
                else if (!IsAuthenticodeSigned(avcodec))
                    problems.Add($"“{Path.GetFileName(avcodec)}” 未通过 Authenticode 签名校验");
            }
            catch (Exception ex)
            {
                problems.Add($"无法校验 avcodec DLL：{ex.Message}");
            }
        }

        if (problems.Count == 0)
        {
            reason = "";
            return true;
        }
        reason = string.Join("；", problems);
        return !autoDetect;
    }

    /// <summary>目录是否对当前用户可写（能投放伪造 DLL 的前提条件）。
    /// 用"真的去创建一个临时文件"探测，而不是解析 ACL：ACL 要考虑继承、组、完整性级别、
    /// UAC 虚拟化、拒绝项优先级等，判断极易做错；而"能否落一个文件"就是攻击面本身。
    /// 文件用 DeleteOnClose 打开，using 结束即消失，不留垃圾。</summary>
    private static bool IsDirectoryWritable(string dir)
    {
        var probe = Path.Combine(dir, $".3fcompare-write-probe-{Environment.ProcessId}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            // 访问被拒 / 目录只读 / 路径非法 —— 都视为"写不进去"，即低风险
            return false;
        }
    }

    // ==================================================================
    // Authenticode 签名校验（wintrust.dll）
    // ==================================================================
    // 只校验"文件是否带有效签名"，不弹 UI、不联网（WTD_UI_NONE + WTD_REVOKE_NONE）。
    // 结构体全部用 nint/uint 手工布局 ⇒ 完全 blittable，既能被 LibraryImport 源生成
    // 直接支持（AOT 友好），也避免 string 字段在不同 CharSet 下的布局歧义。

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 = {00AAC56B-CD44-11D0-8CC2-00C04FC295EE}。</summary>
    // 必须 unchecked：0xCD44 = 52548 超出 short.MaxValue，而项目开了整数溢出检查
    // （CheckForOverflowUnderflow），裸写 (short)0xcd44 会直接 CS0221 编译失败。
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new(0x00aac56b, unchecked((short)0xcd44), unchecked((short)0x11d0),
            0x8c, 0xc2, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);

    private const uint WtdUiNone = 2;      // WTD_UI_NONE：绝不弹窗（本调用在启动路径上）
    private const uint WtdRevokeNone = 0;  // WTD_REVOKE_NONE：不做吊销检查，避免联网等待
    private const uint WtdChoiceFile = 1;  // WTD_CHOICE_FILE：校验一个磁盘文件
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        public nint FilePath;     // PCWSTR（Unicode）
        public nint HFile;        // 必须为 0（用路径而非句柄）
        public nint KnownSubject; // 必须为 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public nint PolicyCallbackData;
        public nint SIPClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint Union;        // 联合体首字段：此处指向 WinTrustFileInfo
        public uint StateAction;
        public nint StateData;    // 由 WinVerifyTrust 写出，必须用 CLOSE 动作释放
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, in Guid actionId, in WinTrustData data);

    /// <summary>文件是否通过 Authenticode 签名校验。
    /// 校验失败<b>不等于</b>文件有害（自制/自签名构建通常无签名），所以调用方按
    /// "显式指定 vs 自动检测"分别处理，这里只给事实。任何异常都按"未通过"处理并留日志。</summary>
    private static bool IsAuthenticodeSigned(string filePath)
    {
        var fileInfoPtr = nint.Zero;
        var pathPtr = nint.Zero;
        try
        {
            var fileInfoSize = Marshal.SizeOf<WinTrustFileInfo>();
            pathPtr = Marshal.StringToHGlobalUni(filePath);
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)fileInfoSize,
                FilePath = pathPtr,
                HFile = 0,
                KnownSubject = 0,
            };
            fileInfoPtr = Marshal.AllocHGlobal(fileInfoSize);
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = 0,
                SIPClientData = 0,
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                Union = fileInfoPtr,
                StateAction = WtdStateActionVerify,
                StateData = 0,
                UrlReference = 0,
                ProvFlags = 0,
                UiContext = 0,
                SignatureSettings = 0,
            };

            var rc = WinVerifyTrust(0, in WinTrustActionGenericVerifyV2, in data);
            // 必须成对：VERIFY 之后用 CLOSE 释放内核为我们分配的 state data，
            // 否则每探测一次泄漏一份内核资源（长期运行的设置页反复点"测试"就会累积）。
            if (data.StateData != 0)
            {
                var close = data;
                close.StateAction = WtdStateActionClose;
                WinVerifyTrust(0, in WinTrustActionGenericVerifyV2, in close);
            }
            return rc == 0;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("NativeRuntime",
                $"Authenticode 校验异常（{Path.GetFileName(filePath)}）: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            // StructureToPtr 只拷了指针值，释放内存即可（WinTrustFileInfo 无内部分配）
            if (fileInfoPtr != nint.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            if (pathPtr != nint.Zero) Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>计算文件 SHA-256；读取失败时返回 null（调用方按"不一致"处理 ⇒ 覆盖）。</summary>
    private static byte[]? TryComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("NativeRuntime",
                $"计算 {Path.GetFileName(path)} 的 SHA-256 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>自动探测 FFmpeg 目录：FFMPEG_DIR 环境变量 → PATH 逐项（含 bin/bin64）→
    /// 运行时来源目录（手动指定 > 同目录 > ffmpeg-full/ > ffmpeg/）。
    /// 返回含 avcodec 核心 DLL 的绝对路径；未找到返回 null。</summary>
    public static string? AutoDetectFfmpegDirectory()
    {
        try
        {
            // 1) FFMPEG_DIR 环境变量（显式指定）
            //    视为"用户显式指定"：环境变量是用户主动设的，安全闸门只警告不拒绝，
            //    否则在自建工具链（无签名 DLL）环境下会静默降级到演示模式。
            var envDir = Environment.GetEnvironmentVariable("FFMPEG_DIR");
            if (!string.IsNullOrWhiteSpace(envDir) && Resolve(envDir, autoDetect: false) is { } fromEnv)
                return fromEnv;

            // 2) PATH 逐项探测（含自身 / bin / bin64 三种形态）
            //    这是唯一的自动路径：PATH 上任何用户可写目录都可能被投放伪造 DLL，
            //    因此这里启用严格闸门（autoDetect: true），不通过就换下一个候选目录。
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Resolve(raw.Trim(), autoDetect: true) is { } fromPath)
                        return fromPath;
                }
            }

            // 3) 运行时来源目录（手动指定 > 同目录 > ffmpeg-full/ > ffmpeg/）
            if (ResolveFfmpegSourceDirectory() is { } source)
                return source;
        }
        catch
        {
            // 探测失败不抛出，保持 null
        }
        return null;
    }

    /// <summary>本地辅助：候选目录及其 bin/bin64 子目录中是否含 avcodec DLL。
    /// autoDetect=true 时额外要求通过安全闸门，否则跳过该候选（继续找下一个）。</summary>
    private static string? Resolve(string? candidate, bool autoDetect)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var baseDir = Path.GetFullPath(candidate.Trim());
            var candidates = new[] { baseDir, Path.Combine(baseDir, "bin"), Path.Combine(baseDir, "bin64") };
            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir)) continue;
                var dll = Directory.GetFiles(dir, "avcodec-*.dll").FirstOrDefault()
                    ?? Directory.GetFiles(dir, "avcodec*.dll").FirstOrDefault();
                if (dll is null) continue;
                if (autoDetect && !PassesSecurityGate(dir, autoDetect: true, out var why))
                {
                    // 不 return：换下一个候选目录（bin/bin64 或下一个 PATH 项），
                    // 让"PATH 上有一个脏目录"不至于让整个自动检测失效。
                    Diagnostics.AppLog.Warn("NativeRuntime", $"自动检测跳过 FFmpeg 目录 “{dir}”：{why}");
                    continue;
                }
                return Path.GetFullPath(dir);
            }
        }
        catch { /* 候选无效跳过 */ }
        return null;
    }

    /// <summary>检测运行时来源目录是否已具备 FFmpeg 核心 DLL（avcodec-*.dll）。
    /// 3FP 内核通过 Delay-Load 从 DLL 搜索路径（应用目录 + SetDllDirectory 目录 + 系统路径）解析 FFmpeg。
    /// 用于引擎可用性探测：仅有 FFF.Native 而没有 FFmpeg 时不能使用真实模式
    /// （否则打开视频时 FFmpeg Delay-Load 失败会导致原生崩溃）。
    /// 优先级：手动指定 > 同目录 DLL > ffmpeg-full/ 子目录 > ffmpeg/ 子目录（旧版）。
    /// </summary>
    public static bool IsFfmpegAvailable()
        => ResolveFfmpegSourceDirectory() is not null;

    /// <summary>按优先级解析 FFmpeg 来源目录：
    /// ① 手动指定的 FfmpegDirectory（仅当其含 avcodec）→ ② exe 同目录 → ③ ffmpeg-full/ 子目录
    /// → ④ ffmpeg/ 子目录（旧完整版运行目录）。
    /// 命中子目录时同时 SetDllDirectoryW 注册搜索路径（否则 FFF.Native 的 Delay-Load
    /// 解析不到 avcodec → 打开媒体时原生崩溃 0xC0005FFE）。返回实际目录绝对路径或 null。</summary>
    private static string? ResolveFfmpegSourceDirectory()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            if (!Directory.Exists(appDir)) return null;

            // ① 手动指定（优先级最高）
            if (FfmpegDirectory is not null && HasAvcodec(FfmpegDirectory))
            {
                Diagnostics.AppLog.Debug("NativeRuntime", $"IsFfmpegAvailable: found in FfmpegDirectory='{FfmpegDirectory}'");
                return FfmpegDirectory;
            }
            // ② exe 同目录
            if (HasAvcodec(appDir))
            {
                Diagnostics.AppLog.Debug("NativeRuntime", "IsFfmpegAvailable: found in appDir");
                return appDir;
            }
            // ③ ffmpeg-full/ 子目录（发布完整版运行时目录）
            if (TryRegisterSubDir(appDir, "ffmpeg-full", out var ffmpegFullDir))
            {
                Diagnostics.AppLog.Debug("NativeRuntime", $"IsFfmpegAvailable: found in ffmpeg-full/ subdir, registered search path = {ffmpegFullDir}");
                return ffmpegFullDir;
            }
            // ④ ffmpeg/ 子目录（旧完整版运行目录，兼容）
            if (TryRegisterSubDir(appDir, "ffmpeg", out var ffmpegSubDir))
            {
                Diagnostics.AppLog.Debug("NativeRuntime", $"IsFfmpegAvailable: found in ffmpeg/ subdir, registered search path = {ffmpegSubDir}");
                return ffmpegSubDir;
            }
            Diagnostics.AppLog.Warn("NativeRuntime", $"IsFfmpegAvailable: NOT found (FfmpegDirectory='{FfmpegDirectory}')");
            return null;
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("NativeRuntime", $"IsFfmpegAvailable exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>目录中是否含 avcodec 核心 DLL。</summary>
    private static bool HasAvcodec(string dir)
        => Directory.Exists(dir) &&
           (Directory.GetFiles(dir, "avcodec-*.dll").Length > 0
            || Directory.GetFiles(dir, "avcodec*.dll").Length > 0);

    /// <summary>子目录存在且含 avcodec 时，把该子目录加入 DLL 搜索路径（SetDllDirectory），
    /// 返回 true 并输出子目录绝对路径；否则返回 false。</summary>
    private static bool TryRegisterSubDir(string appDir, string subDir, out string fullDir)
    {
        fullDir = Path.Combine(appDir, subDir);
        if (!Directory.Exists(fullDir)) return false;
        if (!HasAvcodec(fullDir)) return false;
        ApplyDllDirectory(fullDir);
        return true;
    }

    /// <summary>验证目录是否包含 FFmpeg 核心 DLL（avcodec-*.dll）。</summary>
    public static string? ValidateFfmpegDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "目录为空";
        var dir = directory.Trim();
        if (!Directory.Exists(dir)) return "目录不存在";
        var match = Directory.GetFiles(dir, "avcodec-*.dll").FirstOrDefault()
            ?? Directory.GetFiles(dir, "avcodec*.dll").FirstOrDefault();
        if (match is null)
            return "目录中未找到 avcodec DLL（请选择包含 bin 的 FFmpeg 目录）";
        return null; // 有效
    }

    /// <summary>探测：设置目录后是否能让 FFF.Native 加载成功。</summary>
    public static bool IsNativeAvailableWithDirectory(string? directory)
    {
        var existing = FfmpegDirectory;
        try
        {
            SetFfmpegDirectory(directory);
            return EngineFactory.IsNativeAvailable();
        }
        finally
        {
            SetFfmpegDirectory(existing);
        }
    }
    // P2 清理：原 CopyDlls(sourceDir) 是 private 且全仓库无调用者（实际走 SetDllDirectory），
    // 已删除。它会把用户目录的 av*/sw*/ass* DLL 无条件覆盖到应用目录 —— 留着就是个定时炸弹。
}
