namespace _3FCompare.Core.Tests.Infrastructure;

/// <summary>
/// 临时目录夹具：<c>using var dir = new TempDir();</c>，析构时递归删除。
/// </summary>
///
/// <para><b>为什么必须有它</b>：docs/41 §4.5 的 B 类补测（`SettingsStore`、`SessionSnapshot`、
/// `NativeRuntime.Validate`、`AppLog`）都要碰真实文件系统。若各自手写
/// <c>Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())</c>，
/// 会留下三类问题：① 用例失败时目录残留，下次跑互相干扰；② 没人保证清理，
/// 临时目录无限增长；③ 并行测试撞目录名。</para>
///
/// <para><b>为什么不用 <c>DeleteRecursively</c> 之外的花招</b>：删除失败（文件被占用/
/// 只读）时**不抛异常**——清理阶段抛异常会盖掉用例真正的失败原因，让排障方向完全跑偏。
/// 这里只记到 <see cref="CleanupErrors"/>，由需要严格性的用例自行断言。</para>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    /// <summary>清理时遇到的错误（通常为空；非空说明有文件被占用或只读）。</summary>
    public List<string> CleanupErrors { get; } = new();

    public TempDir(string? prefix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "3fc_tests_" + (prefix ?? "") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string File_(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>建一个含指定文本的文件，返回其全路径。</summary>
    public string WriteFile(string name, string content)
    {
        var p = File_(name);
        var dir = System.IO.Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex)
        {
            // 见类注释：清理失败不能掩盖用例的真实失败原因。
            CleanupErrors.Add($"{Path}: {ex.GetType().Name}: {ex.Message}");
        }
        GC.SuppressFinalize(this);
    }

    public override string ToString() => Path;
}
