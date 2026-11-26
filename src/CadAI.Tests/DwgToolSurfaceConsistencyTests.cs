using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// DWG 侧工具面一致性防线：让 skills/dwgai/skill.manifest.json 里手写的工具清单
/// 与源码里 registry.Register(...) 真实注册的工具名不漂移。
///
/// AutoCAD 侧有 ToolSurfaceConsistencyTests 守着 [McpServerTool] 工具面；DWG 侧此前
/// 没有任何守卫，manifest 与实际注册表只能靠人肉同步——一旦漏同步，模型会看到
/// 不存在或缺失的工具。本测试补上这道防线：源码扫描结果 与 manifest toolGroups 必须相等，
/// 且同一工具不允许被重复注册。
/// </summary>
public class DwgToolSurfaceConsistencyTests
{
    private static readonly Regex RegisterPattern = new Regex(
        @"Register(?:Background)?\s*\(\s*""(?<name>cad_[A-Za-z0-9_]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>源码里真实注册的 cad_* 工具名（Mcp.AutoCAD 核心 + Host.AutoCAD 扩展），值为出现次数。</summary>
    private static Dictionary<string, int> ScanRegisteredTools(string repoRoot)
    {
        var roots = new[]
        {
            Path.Combine(repoRoot, "src", "CadAI.Mcp.AutoCAD"),
            Path.Combine(repoRoot, "src", "CadAI.Host.AutoCAD")
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in RegisterPattern.Matches(File.ReadAllText(file)))
                {
                    var name = match.Groups["name"].Value;
                    counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                }
            }
        }
        return counts;
    }

    private static string ManifestPath(string repoRoot)
        => Path.Combine(repoRoot, "skills", "dwgai", "skill.manifest.json");

    /// <summary>manifest 的 routingHints.toolGroups 里声明的全部工具名。</summary>
    private static IReadOnlySet<string> ReadManifestToolGroupNames(string repoRoot)
    {
        var path = ManifestPath(repoRoot);
        Assert.True(File.Exists(path), $"Manifest not found: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var groups = doc.RootElement.GetProperty("routingHints").GetProperty("toolGroups");
        foreach (var group in groups.EnumerateObject())
        {
            foreach (var tool in group.Value.EnumerateArray())
            {
                names.Add(tool.GetString()!);
            }
        }
        return names;
    }

    [Fact]
    public void Manifest_toolGroups_match_registered_tools()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var registered = ScanRegisteredTools(repoRoot);
        var manifest = ReadManifestToolGroupNames(repoRoot);

        var missingInManifest = registered.Keys.Except(manifest).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var staleInManifest = manifest.Except(registered.Keys).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(missingInManifest.Count == 0,
            "Registered cad_* tools missing from skills/dwgai/skill.manifest.json toolGroups: "
            + string.Join(", ", missingInManifest));
        Assert.True(staleInManifest.Count == 0,
            "Manifest lists cad_* tools that are not registered in source (stale entries): "
            + string.Join(", ", staleInManifest));
    }

    [Fact]
    public void No_tool_is_registered_twice()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var registered = ScanRegisteredTools(repoRoot);
        var duplicates = registered
            .Where(kv => kv.Value > 1)
            .Select(kv => $"{kv.Key}(x{kv.Value})")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(duplicates.Count == 0,
            "cad_* tools registered more than once (the model would see a duplicate schema): "
            + string.Join(", ", duplicates));
    }

    [Fact]
    public void NonMutationTools_are_all_registered()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var registered = ScanRegisteredTools(repoRoot).Keys;
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(repoRoot)));
        var nonMutation = doc.RootElement.GetProperty("routingHints").GetProperty("nonMutationTools");

        var stale = new List<string>();
        foreach (var tool in nonMutation.EnumerateArray())
        {
            var name = tool.GetString()!;
            if (!registered.Contains(name)) stale.Add(name);
        }

        Assert.True(stale.Count == 0,
            "nonMutationTools lists tools that are not registered in source: " + string.Join(", ", stale));
    }
}