using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 技能入口 Markdown 的 YAML front-matter 读取。
///
/// 背景：技能包是用户/作者手写的，front-matter 写坏一个字符不该让整个档位从下拉框里
/// 消失。这里锁住"宽容解析"的边界——尤其折叠块（description: &gt;）与引号值，
/// 它们决定下拉框 tooltip 与 cad_skill_list 的一行说明能不能读出来。
/// </summary>
public sealed class SkillFrontMatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 只有正文，没有 front-matter")]
    [InlineData("---\nname: 未闭合")]
    public void TryRead_ReturnsFalseWithoutACompleteBlock(string? markdown)
    {
        Assert.False(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Null(name);
        Assert.Null(description);
    }

    [Fact]
    public void TryRead_ReadsPlainNameAndDescription()
    {
        const string markdown = "---\nname: 板类\ndescription: 板、衬板、垫板的建模与出图。\n---\n\n# 板类\n";

        Assert.True(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Equal("板类", name);
        Assert.Equal("板、衬板、垫板的建模与出图。", description);
    }

    [Fact]
    public void TryRead_FoldsMultilineDescriptionIntoOneLine()
    {
        const string markdown = "---\nname: 轴类\ndescription: >\n  轴、销、辊筒的建模与出图。\n  第二行。\n---\n";

        Assert.True(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Equal("轴类", name);
        Assert.Equal("轴、销、辊筒的建模与出图。 第二行。", description);
    }

    [Fact]
    public void TryRead_AcceptsAliasKeysAndQuotedValues()
    {
        const string markdown = "---\ntitle: \"一般装配\"\nsummary: '焊接组件的装配与出图。'\n---\n";

        Assert.True(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Equal("一般装配", name);
        Assert.Equal("焊接组件的装配与出图。", description);
    }

    [Fact]
    public void TryRead_SkipsLeadingBlankLinesAndComments()
    {
        const string markdown = "\n\n# 注释\n---\ndisplayName: 板类\nwhen: 做板时加载。\n---\n";

        Assert.True(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Equal("板类", name);
        Assert.Equal("做板时加载。", description);
    }

    [Fact]
    public void TryRead_HandlesBomAndCrlf()
    {
        const string markdown = "\uFEFF---\r\nname: 板类\r\ndescription: 板件。\r\n---\r\n";

        Assert.True(SkillFrontMatter.TryRead(markdown, out string? name, out string? description));
        Assert.Equal("板类", name);
        Assert.Equal("板件。", description);
    }

    /// <summary>
    /// 交付契约：AutoCAD 技能包（dwgai）的入口 SKILL.md 必须存在且具备可读的 front-matter。
    /// </summary>
    [Fact]
    public void DwgAiSkillPackage_HasReadableFrontMatter()
    {
        string entry = TestPaths.RepoFile("skills", "dwgai", "SKILL.md");
        Assert.True(File.Exists(entry), $"缺少技能包入口：{entry}");

        Assert.True(SkillFrontMatter.TryRead(File.ReadAllText(entry), out string? name, out string? description));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(string.IsNullOrWhiteSpace(description));
    }
}