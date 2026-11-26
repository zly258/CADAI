using System.Text;
using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 上下文压缩（<see cref="AgentContextCompactor"/>）的两条硬要求。
///
/// 背景（2026-10-16 审计）：压缩器对超长工具结果只做"白名单键的结构化摘取"，
/// 白名单里全是 <c>success</c>/<c>score</c>/<c>error</c> 这类**状态键**，一个**身份键**都没有。
/// 于是压缩完之后"这个零件/这个孔/这条边"的 id、name、索引全没了——模型下一轮想说
/// "把刚建的那个孔再放大一点"时已经指不回任何对象，只能重新猜一遍。
/// 同时在末尾补充的结论/报错也会被整段丢掉（很多工具把结果写在最后）。
///
/// 这里锁住两件事：身份键必须原样留下；结构化摘取后必须附一段原文末尾。
/// </summary>
public class AgentContextCompactorTests
{
    /// <summary>构造一条"有身份、也有大块无用载荷"的典型工具结果。</summary>
    private static string BuildToolResult()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"success\":true,");
        builder.Append("\"id\":\"PART-001\",");
        builder.Append("\"name\":\"MainShaft\",");
        builder.Append("\"displayName\":\"MainShaft:1\",");
        builder.Append("\"partNumber\":\"WSD-2026-001\",");
        builder.Append("\"revision\":\"B\",");
        builder.Append("\"edge_index\":7,");
        builder.Append("\"view_index\":2,");
        builder.Append("\"filler\":\"");
        builder.Append(new string('x', 4000));
        builder.Append("\",");
        builder.Append("\"trailing\":\"末尾补充：刀路干涉，改用 R2 刀尖\"");
        builder.Append('}');
        return builder.ToString();
    }

    [Fact]
    public void CompactToolResult_KeepsIdentityKeys_AndStaysWithinBudget()
    {
        string body = BuildToolResult();

        string digest = AgentContextCompactor.CompactToolResult(body, 900);

        Assert.True(digest.Length < body.Length, "超长结果必须被压缩");
        Assert.True(digest.Length <= 900, $"压缩结果不得超过预算，实际 {digest.Length}");
        Assert.Contains("\"_compacted\":true", digest);
        // 身份键：少一个，模型就再也引用不回这个对象。
        Assert.Contains("PART-001", digest);
        Assert.Contains("MainShaft", digest);
        Assert.Contains("WSD-2026-001", digest);
        Assert.Contains("\"revision\":\"B\"", digest);
        Assert.Contains("\"edge_index\":7", digest);
        Assert.Contains("\"view_index\":2", digest);
    }

    [Fact]
    public void CompactToolResult_AppendsOriginalTail_SoTrailingConclusionSurvives()
    {
        string body = BuildToolResult();

        string digest = AgentContextCompactor.CompactToolResult(body, 900);

        // trailing 不在白名单里，结构化摘取拿不到它；它出现在结果里就说明"原文末尾"这条兜底生效了。
        Assert.Contains("末尾补充：刀路干涉", digest);
    }

    [Fact]
    public void CompactToolResult_LeavesShortBodiesUntouched()
    {
        const string body = "{\"success\":true,\"id\":\"PART-001\"}";
        Assert.Equal(body, AgentContextCompactor.CompactToolResult(body, 900));
    }

    [Fact]
    public void TryCompact_KeepsIdentityFieldsInRecentToolResults()
    {
        var messages = new List<AgentMessage>
        {
            new AgentMessage { Role = "system", Content = "你是 CAD 助手。" }
        };
        for (int i = 0; i < 20; i++)
            messages.Add(new AgentMessage { Role = "user", Content = "第 " + i + " 轮：" + new string('字', 600) });
        messages.Add(new AgentMessage { Role = "assistant", Content = "已检查。" });
        messages.Add(new AgentMessage { Role = "user", Content = "把刚建的那个孔再放大一点。" });
        messages.Add(new AgentMessage { Role = "tool", ToolCallId = "call_1", Content = BuildToolResult() });

        bool compacted = AgentContextCompactor.TryCompact(
            messages, force: true, taskState: string.Empty, keepRecent: 6,
            triggerTokenLimit: AgentContextCompactor.SoftTokenLimit,
            targetTokenBudget: AgentContextCompactor.MinimumTargetTokens,
            out string report);

        Assert.True(compacted, "强制压缩应当至少压缩一条超长工具结果");
        Assert.False(string.IsNullOrWhiteSpace(report));
        string combined = string.Join("\n", messages.ConvertAll(m => m.Content as string ?? string.Empty));
        Assert.Contains("PART-001", combined);
        Assert.Contains("WSD-2026-001", combined);
    }
}