using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace _3FCompare.Core.Settings;

/// <summary>
/// 原子文件写入：临时文件 → fsync → <see cref="File.Move(string, string, bool)"/> 替换。
///
/// 为什么不能用 <c>File.WriteAllText</c>：它会**先截断**目标文件再写。
/// 磁盘满 / 只读 / 文件被占用 / 写入途中进程被杀，都会让配置文件变成
/// 0 字节或半截 JSON；下次读取抛 <c>JsonException</c>，而调用方普遍用
/// <c>catch { return new AppSettings(); }</c> 兜住 ⇒ **静默回退默认值**，
/// 窗口几何 / 侧栏宽度 / FFmpeg 目录 / 语言全部丢失且用户毫无感知（docs/15 §4.1）。
///
/// 先写临时文件则不同：写失败时目标文件分毫未动，下次读到的仍是上一份完整配置。
/// </summary>
public static class AtomicFile
{
    /// <summary>临时文件后缀（<see cref="PurgeStaleTempFiles"/> 据此识别残留）。</summary>
    public const string TempFileExtension = ".tmp";

    /// <summary>备份文件后缀：目标文件损坏时读取方可回退到它（B2）。</summary>
    public const string BackupFileExtension = ".bak";

    /// <summary>超过这个年龄的临时文件一律视为残留——避免 pid 被系统复用后
    /// 误判"属主进程还活着"，也让拿不到进程信息时仍有个兜底判据。</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(24);

    /// <summary>同进程内按**目标路径**串行化写入。
    ///
    /// 为什么必须加：tmp 名原先只带 <c>Environment.ProcessId</c>，同进程里两个线程
    /// （例如"侧栏拖拽"与"窗口几何变化"各触发一次 Save）会算出一模一样的 tmp 名。
    /// 后到的那个 <c>FileStream</c> 打开失败（<c>FileShare.None</c>），
    /// 它的 catch 分支执行 <c>File.Delete(tmp)</c> —— **把先到者正在写的文件删掉**，
    /// 于是先到者的 <c>File.Move</c> 失败，配置反而没存上（B3）。
    ///
    /// 键用规范化后的全路径（Windows 下忽略大小写），否则 "a.json" 与 "A.json"
    /// 会各锁一把、互相踩踏。字典条目数量等于不同目标文件的数量，实际只有个位数。</summary>
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原子写入文本（UTF-8 无 BOM）。成功时目标文件要么保留旧内容，要么整体是新内容。</summary>
    /// <param name="path">目标文件完整路径。</param>
    /// <param name="contents">要写入的内容。</param>
    /// <param name="writeBom">是否写 UTF-8 BOM（默认否，与 File.WriteAllText 一致）。</param>
    /// <param name="createBackup">
    /// 覆盖前是否先复制一份 <c>path + ".bak"</c>。
    ///
    /// 为什么默认关闭：① 备份让每次写入多一次全量拷贝，会话快照这类可能几 MB 的文件
    /// 不该无条件买单；② 备份文件会多占一个目录项，而本方法被多处（含单测）当作
    /// "写完后目录里只剩目标文件"来用。
    ///
    /// 需要它的场景是**读回来可能解析失败**的配置类文件（<c>settings.json</c>）——
    /// 损坏一次就回退出厂值、旧内容再也要不回来，代价远高于一次拷贝（B2）。
    /// 所以由 <c>SettingsStore.Save</c> 显式打开。
    /// </param>
    public static void WriteAllText(string path, string contents, bool writeBom = false, bool createBackup = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // 同一路径串行；不同路径之间不互斥，不损失并发度
        var gate = PathLocks.GetOrAdd(NormalizeKey(path), static _ => new object());
        lock (gate)
        {
            WriteAllTextCore(path, contents, writeBom, createBackup);
        }
    }

    private static void WriteAllTextCore(string path, string contents, bool writeBom, bool createBackup)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        Directory.CreateDirectory(dir);   // 目录不存在时先建（幂等）

        // 临时文件名带 pid **再加 Guid**：只带 pid 时同进程并发会撞名（见 PathLocks 注释）。
        // Guid 保证即使锁被绕过（例如跨 AppDomain）也不会互相删除对方的文件。
        var tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                       + "." + Guid.NewGuid().ToString("N") + TempFileExtension;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (writeBom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);   // 真正落盘，避免"写了但没落盘就断电"
            }

            // 覆盖前留一份上一版（best-effort）：Move 一成功旧内容就没了，
            // 而"新内容本身是坏的"是真实存在的——序列化被中途打断、位翻转、
            // 半截写盘。没有备份的话，下次 Load 失败就直接回退出厂设置（B2）。
            if (createBackup)
            {
                try
                {
                    if (File.Exists(path))
                        File.Copy(path, path + BackupFileExtension, overwrite: true);
                }
                catch
                {
                    // 备份失败绝不阻断主写入：能存上新配置比留住旧备份重要
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 清理残留的临时文件，别把用户的配置目录弄脏
            //（tmp 名带 Guid，删的一定是自己那一份，不会误伤并发的另一方）
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;   // 交给调用方记录；关键是目标文件此时**未被破坏**
        }
    }

    private static string NormalizeKey(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }   // 非法路径：退回原串，照样能互斥
    }

    /// <summary>
    /// 清理目录里残留的原子写临时文件（B4）。
    ///
    /// <c>FileStream</c> 创建成功到 <c>File.Move</c> 之间进程被杀（崩溃 / 强杀 / 断电），
    /// tmp 就永久留在配置目录里：既占地方，又让用户以为配置目录出bug了。
    /// 判定"残留"= 命名符合 <c>&lt;目标&gt;.&lt;pid&gt;.&lt;guid&gt;.tmp</c>
    /// 且 pid 对应的进程已不存在（或文件已存在超过 24 小时）。
    /// </summary>
    /// <param name="directory">要清理的目录。</param>
    /// <returns>实际删除的文件数。全程 best-effort，任何失败都被吞掉。</returns>
    public static int PurgeStaleTempFiles(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return 0;
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*" + TempFileExtension))
            {
                if (!IsStaleTempFile(file)) continue;
                try { File.Delete(file); removed++; }
                catch { /* 正被占用/只读：下次启动再试 */ }
            }
        }
        catch
        {
            // 清理是顺手做的，失败绝不影响启动
        }
        return removed;
    }

    private static bool IsStaleTempFile(string file)
    {
        // 形如 "settings.json.1234.<guid>.tmp"：去掉后缀后取倒数两段 = guid、pid
        var name = Path.GetFileName(file);
        if (name.Length <= TempFileExtension.Length) return false;

        var stem = name.Substring(0, name.Length - TempFileExtension.Length);
        var parts = stem.Split('.');
        if (parts.Length < 3) return false;   // 不是本类写出的 tmp，别乱删用户文件

        if (!int.TryParse(parts[parts.Length - 2], NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var pid))
            return false;

        // 自己正在写的那份绝对不能删
        if (pid == Environment.ProcessId) return false;

        if (IsOlderThan(file, StaleTempAge)) return true;
        return !IsProcessAlive(pid);
    }

    private static bool IsOlderThan(string file, TimeSpan age)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age; }
        catch { return false; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            if (pid <= 0) return false;
            // GetProcessById 在进程不存在时抛 ArgumentException；
            // 拿到了就说明 pid 还活着（可能是别的进程占用了这个 pid，故再叠一层 24h 年龄判据）
            using var p = Process.GetProcessById(pid);
            return p is not null;
        }
        catch { return false; }
    }
}
