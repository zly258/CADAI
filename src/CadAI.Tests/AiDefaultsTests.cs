using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// AiDefaults / AiSettings 防回归：
/// 1) "enterprise" 内置档位 Id 必须收口在 AiDefaults.EnterpriseModelId 一处；
/// 2) AiSettings.Clone 必须逐字段拷贝——历史上漏拷 8 个字段，设置窗口一次 Save()
///    就把用户的回退链/卡死阈值静默重置。AiSettings 依赖 System.Web（JavaScriptSerializer），
///     net8 测试工程无法链接编译，这里退而做源码级守卫：
///    Clone 方法体内的属性赋值数量与 AiSettings 的可写属性数量一致。
/// </summary>
public class AiDefaultsTests
{
    private static string FindRepoRoot() => RepoPaths.FindRepoRoot();

    private static string ReadAgentSource(string repoRoot, string fileName)
        => File.ReadAllText(RepoPaths.Resolve("src", "CadAI.Core", "Agent", fileName));

    [Fact]
    public void EnterpriseModelId_is_defined_and_used_everywhere()
    {
        var repoRoot = FindRepoRoot();
        string settings = ReadAgentSource(repoRoot, "AiSettings.cs");
        string defaults = ReadAgentSource(repoRoot, "AiDefaults.cs");

        Assert.Contains("EnterpriseModelId = \"enterprise\"", defaults);

        // AiSettings 里不允许再出现裸的 "enterprise" 字面量（Clone 曾把它硬编码回去）。
        var literals = Regex.Matches(settings, "\"enterprise\"").Count;
        Assert.True(literals == 0,
            "AiSettings.cs contains " + literals + " raw \"enterprise\" literal(s); use AiDefaults.EnterpriseModelId.");
    }

    [Fact]
    public void Clone_copies_every_writable_property()
    {
        var repoRoot = FindRepoRoot();
        string settings = ReadAgentSource(repoRoot, "AiSettings.cs");

        int classStart = settings.IndexOf("public sealed class AiSettings", StringComparison.Ordinal);
        Assert.True(classStart >= 0, "AiSettings class not found.");
        // Clone 之后还有其它成员，取到 Clone 结束（下一个 public 方法定义）即可。
        int cloneStart = settings.IndexOf("public AiSettings Clone()", classStart, StringComparison.Ordinal);
        Assert.True(cloneStart >= 0, "AiSettings.Clone not found.");

        // 可写属性 = 整个类里 "{ get; set; }" 的 public 属性（含 AiModelProfile 之外的全部）。
        var properties = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(settings, @"public\s+[\w<>?.,\[\]]+\s+(?<name>\w+)\s*\{\s*get;\s*set;\s*\}"))
        {
            properties.Add(match.Groups["name"].Value);
        }
        // Models / ActiveModelId 单独断言（Clone 里有特殊处理），不要求逐一相等。
        properties.Remove("Models");
        properties.Remove("ActiveModelId");

        string cloneBody = settings.Substring(cloneStart, settings.IndexOf("}", cloneStart, StringComparison.Ordinal) - cloneStart);
        foreach (string property in properties)
        {
            Assert.True(cloneBody.Contains(property + " = ", StringComparison.Ordinal),
                "AiSettings.Clone does not copy property '" + property + "'. "
                + "Add it to Clone, otherwise saving from the settings window silently resets it.");
        }
        // 这两个字段必须存在且拷贝的是 this 侧的值，而不是字面量。
        Assert.Contains("ActiveModelId = ActiveModelId", cloneBody, StringComparison.Ordinal);
        Assert.Contains("Models = Models?", cloneBody, StringComparison.Ordinal);
    }
}
