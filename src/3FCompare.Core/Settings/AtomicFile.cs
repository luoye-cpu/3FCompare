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
    /// <summary>原子写入文本（UTF-8 无 BOM）。成功时目标文件要么保留旧内容，要么整体是新内容。</summary>
    /// <param name="path">目标文件完整路径。</param>
    /// <param name="contents">要写入的内容。</param>
    /// <param name="writeBom">是否写 UTF-8 BOM（默认否，与 File.WriteAllText 一致）。</param>
    public static void WriteAllText(string path, string contents, bool writeBom = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        Directory.CreateDirectory(dir);   // 目录不存在时先建（幂等）

        // 临时文件带进程 ID，避免多实例并发写同一配置时互相踩踏
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (writeBom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);   // 真正落盘，避免"写了但没落盘就断电"
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 清理残留的临时文件，别把用户的配置目录弄脏
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;   // 交给调用方记录；关键是目标文件此时**未被破坏**
        }
    }
}
