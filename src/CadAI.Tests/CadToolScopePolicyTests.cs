using System.Text.RegularExpressions;
using CadAI.Host.AutoCAD.Hosting;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// CAD 侧工具面收窄策略（<see cref="CadToolScopePolicy"/>）的两条底线。
///
/// 背景（2026-10-16）：CadToolScopePolicy 是这一轮新加的能力，它管的是"模型这一轮能看到
/// 哪几个 cad_* 工具"。给它写用例的理由和给路由表写用例一样——**收窄错了不会有任何报错**，
/// 只会让模型"看不到那个工具"，表现为"AI 说它做不到"。所以必须有用例守住：
///   1. 完备性：每个已注册的 cad_* 工具至少要能被**一个具体域**看到。
///      （只落在 all 域不算——all 是"认不出意图时回退全量"的兜底，不是域划分。）
///   2. 回退：认不出意图 / 空文本时回退全量，不许把工具面缩没了。
///
/// 工具名清单直接从源码里的 registry.Register(...) 扫出来，与
/// DwgToolSurfaceConsistencyTests 同一口径——不抄 manifest、不抄手写清单。
/// </summary>
public class CadToolScopePolicyTests
{
    private static readonly Regex RegisterPattern = new(
        @"Register(?:Background)?\s*\(\s*""(?<name>cad_[A-Za-z0-9_]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>源码里真实注册的全部 cad_* 工具名（Mcp.AutoCAD 核心 + Host.AutoCAD 扩展）。</summary>
    private static List<string> RegisteredTools()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "src", "CadAI.Mcp.AutoCAD"),
            Path.Combine(repoRoot, "src", "CadAI.Host.AutoCAD")
        };
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in RegisterPattern.Matches(File.ReadAllText(file)))
                    names.Add(match.Groups["name"].Value);
            }
        }
        Assert.NotEmpty(names);
        return names.ToList();
    }

    /// <summary>与 ChatCore <c>SelectToolsForScope</c> 同语义：任一作用域可见即可（并集）。</summary>
    private static bool Visible(CadToolScopePolicy policy, IReadOnlyList<string> scopes, string toolName)
        => scopes.Any(scope => policy.IncludesTool(scope, toolName));

    [Fact]
    public void Every_registered_tool_is_visible_in_at_least_one_domain_scope()
    {
        var policy = new CadToolScopePolicy();
        var unreachable = RegisteredTools()
            .Where(tool => !CadToolScopePolicy.DomainScopes.Any(scope => policy.IncludesTool(scope, tool)))
            .ToList();

        Assert.True(unreachable.Count == 0,
            "这些 cad_* 工具哪个具体作用域都看不到（等于注册了却永远拿不到，模型会说\"做不到\"）："
            + string.Join(", ", unreachable));
    }

    [Fact]
    public void Unmatched_or_empty_text_falls_back_to_the_full_surface()
    {
        var policy = new CadToolScopePolicy();

        var scopes = policy.ResolveScopes("随便帮我搞一下", Array.Empty<string>());

        Assert.Contains(CadToolScopePolicy.AllScope, scopes);
        // 全量作用域必须真的"全量"：随便挑一个冷门工具都可见。
        Assert.True(policy.IncludesTool(CadToolScopePolicy.AllScope, "cad_batch_export_pdf"));
        Assert.True(policy.IncludesTool(CadToolScopePolicy.AllScope, "cad_xref_update"));
    }

    [Fact]
    public void Short_follow_up_keeps_the_previous_scopes()
    {
        var policy = new CadToolScopePolicy();

        var scopes = policy.ResolveScopes("继续", new[] { "dimension" });

        Assert.Equal(new[] { "dimension" }, scopes);
    }

    [Fact]
    public void Translation_domain_has_left_the_surface()
    {
        // 2026-10-23 产品拆分后 cad_translate_* / cad_glossary_* 已退出对话工具面，
        // translation 域随之删除：任何请求都不应再解析出它，残余关键词只落常规域。
        var policy = new CadToolScopePolicy();

        Assert.DoesNotContain("translation", CadToolScopePolicy.DomainScopes);

        var scopes = policy.ResolveScopes("把这张图纸里的文字翻译成英文", Array.Empty<string>());
        Assert.DoesNotContain("translation", scopes);
        Assert.NotEmpty(scopes);
    }

    [Fact]
    public void Dimension_request_exposes_dimension_tools_only_plus_core()
    {
        var policy = new CadToolScopePolicy();

        var scopes = policy.ResolveScopes("给这几个孔补上尺寸标注", Array.Empty<string>());

        Assert.Contains("dimension", scopes);
        Assert.True(Visible(policy, scopes, "cad_dimension_linear_create"));
        Assert.False(Visible(policy, scopes, "cad_batch_read_texts"));
        Assert.False(Visible(policy, scopes, "cad_table_write"));
    }

    [Fact]
    public void Unknown_scope_does_not_narrow()
    {
        var policy = new CadToolScopePolicy();

        // 未知作用域一律不放行收窄：宁可多给 token，也不让模型"看不到工具"。
        Assert.True(policy.IncludesTool("no-such-scope", "cad_export_dxf"));
        // 具体域仍然只放自己那份。
        Assert.False(policy.IncludesTool("xref", "cad_export_dxf"));
    }
}