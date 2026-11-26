using System.Text.RegularExpressions;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// pack URI 契约：<c>pack://application:,,,/程序集名;component/路径</c> 里的程序集名必须真实存在，
/// 路径也必须真能在该工程里找到对应文件（含工程内物理文件或 <c>&lt;Page Include="..." Link="..." /&gt;</c> 链接文件）。
/// </summary>
public sealed class PackUriAssemblyTests
{
    private static readonly Regex PackUriPattern = new(
        "pack://application:,,,/(?<asm>[^;/;,]+);component/(?<path>[^\" )]+)",
        RegexOptions.Compiled);

    private static readonly Regex PageLinkPattern = new(
        "<Page\\s+Include=\"(?<inc>[^\"]+)\"\\s+Link=\"(?<link>[^\"]+)\"",
        RegexOptions.Compiled);

    [Fact]
    public void PackUris_ReferenceExistingAssemblyAndFile()
    {
        Dictionary<string, string> projects = LoadProjects();
        Assert.Contains("CadAI.Core", projects.Keys);
        Assert.Contains("CadAI.Host.AutoCAD", projects.Keys);

        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match match in PackUriPattern.Matches(text))
            {
                string assembly = match.Groups["asm"].Value;
                string relative = match.Groups["path"].Value;
                string where = file.Substring(RepoPaths.FindRepoRoot().Length + 1);

                if (!projects.ContainsKey(assembly))
                {
                    offenders.Add(where + " -> 程序集 " + assembly + " 不存在（仓库里没有任何工程产出它）");
                    continue;
                }

                if (!ProjectContainsResource(projects[assembly], relative))
                {
                    offenders.Add(where + " -> " + assembly + ";component/" + relative + " 在工程 " + projects[assembly] + " 里找不到该文件");
                }
            }
        }

        Assert.True(offenders.Count == 0, "XAML/CS 里的 pack URI 失效（程序集名或路径不对）:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool ProjectContainsResource(string projectDir, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(projectDir, normalized)))
            return true;

        foreach (string csproj in Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            string csprojText = File.ReadAllText(csproj);
            foreach (Match match in PageLinkPattern.Matches(csprojText))
            {
                string link = match.Groups["link"].Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (string.Equals(link, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    string inc = match.Groups["inc"].Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    string target = Path.GetFullPath(Path.Combine(projectDir, inc));
                    if (File.Exists(target))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>程序集名 → 工程目录：<c>&lt;AssemblyName&gt;</c> 优先，缺省用 csproj 文件名。</summary>
    private static Dictionary<string, string> LoadProjects()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string csproj in Directory.EnumerateFiles(RepoPaths.Resolve("src"), "*.csproj", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(csproj))
            {
                continue;
            }

            Match name = Regex.Match(File.ReadAllText(csproj), "<AssemblyName>([^<]+)</AssemblyName>");
            string assembly = name.Success ? name.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(csproj);
            map[assembly] = Path.GetDirectoryName(csproj)!;
        }

        return map;
    }

    private static IEnumerable<string> SourceFiles()
    {
        string src = RepoPaths.Resolve("src");
        string tests = Path.Combine(src, "CadAI.Tests");

        foreach (string xaml in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
        {
            if (!IsBuildOutput(xaml))
            {
                yield return xaml;
            }
        }

        foreach (string cs in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (!IsBuildOutput(cs) && !cs.StartsWith(tests, StringComparison.OrdinalIgnoreCase))
            {
                yield return cs;
            }
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains(@"\obj\") || path.Contains(@"\bin\") || path.Contains(@"\output\");
}
