namespace CadAI.Tests;

/// <summary>
/// 少数测试需要读源码文件（校验"手写清单 vs 源码声明"的一致性）。
/// 统一在这里定位仓库根，避免每个测试类各写一份向上查找的逻辑。
/// </summary>
internal static class RepoPaths
{
    /// <summary>从测试输出目录向上定位仓库根（以 CADAI.sln 为锚点）。</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CADAI.sln")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("Could not locate the repository root (CADAI.sln not found above the test output directory).");
    }

    /// <summary>按仓库根解析相对路径。</summary>
    public static string Resolve(params string[] relativeParts)
        => Path.Combine(new[] { FindRepoRoot() }.Concat(relativeParts).ToArray());
}
