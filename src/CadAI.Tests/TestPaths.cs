namespace CadAI.Tests;

/// <summary>
/// 测试用的仓库路径定位。测试输出目录在 .build 下，源码文件不在旁边，
/// 因此统一从 AppContext.BaseDirectory 向上找 CADAI.sln 作为锚点。
/// </summary>
internal static class TestPaths
{
    private static string? _repoRoot;

    internal static string RepoRoot()
    {
        if (_repoRoot != null) return _repoRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CADAI.sln")))
            {
                _repoRoot = dir.FullName;
                return _repoRoot;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (CADAI.sln not found above the test output directory).");
    }

    /// <summary>拼一个仓库内文件的绝对路径。</summary>
    internal static string RepoFile(params string[] parts)
    {
        string path = RepoRoot();
        foreach (string part in parts) path = Path.Combine(path, part);
        return path;
    }
}
