using CadAI.Core.Modal;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 弹窗决策安全闸门的回归防线。
///
/// 这是"要不要在用户的 CAD 会话里点这个按钮"的判定核心：判错就是替用户在图纸上
/// 点了错误的按钮。它此前完全没有测试覆盖（逻辑夹在依赖 ChatCore 的决策服务里，
/// 按测试工程的约定无法链接），现已抽到无依赖的 ModalDecisionSafety 里锁死。
/// </summary>
public class ModalDecisionSafetyTests
{
    [Theory]
    [InlineData("删除")]
    [InlineData("删除(&D)")]
    [InlineData("不保存")]
    [InlineData("放弃更改")]
    [InlineData("Overwrite")]
    [InlineData("Discard changes")]
    [InlineData("Do not save")]   // 带空格的中英混写：只有严格归一化才能命中 donotsave
    public void IsDenied_BlocksDestructiveButtons(string label)
        => Assert.True(ModalDecisionSafety.IsDenied(label));

    [Theory]
    [InlineData("确定")]
    [InlineData("是")]
    [InlineData("接受")]
    [InlineData("取消")]          // "取消/否"是"什么都不做"的分支，必须允许
    [InlineData("否")]
    [InlineData("OK (&O)")]
    [InlineData("Continue")]
    public void IsDenied_AllowsNonDestructiveButtons(string label)
        => Assert.False(ModalDecisionSafety.IsDenied(label));

    [Fact]
    public void IsDenied_TreatsBlankLabelAsDangerous()
    {
        Assert.True(ModalDecisionSafety.IsDenied(null));
        Assert.True(ModalDecisionSafety.IsDenied(string.Empty));
        Assert.True(ModalDecisionSafety.IsDenied("   "));
    }

    [Fact]
    public void MatchButton_PrefersTheExactLabel()
    {
        var buttons = new[] { "接受", "取消" };
        Assert.Equal("接受", ModalDecisionSafety.MatchButton(buttons, "接受"));
        Assert.Equal("取消", ModalDecisionSafety.MatchButton(buttons, "取消"));
    }

    [Theory]
    [InlineData("接受(&A)", "接受")]   // 助记符后缀
    [InlineData("接 受", "接受")]      // 模型插入空格
    [InlineData("接受。", "接受")]     // 标点差异
    public void MatchButton_ToleratesFormattingNoise(string candidate, string expected)
    {
        var buttons = new[] { "接受", "取消" };
        Assert.Equal(expected, ModalDecisionSafety.MatchButton(buttons, candidate));
    }

    [Fact]
    public void MatchButton_RefusesButtonsThatDoNotExist()
    {
        var buttons = new[] { "接受", "取消" };
        // 编造的按钮绝不能"猜一个最近的"——那会在用户的图纸上点错按钮。
        Assert.Null(ModalDecisionSafety.MatchButton(buttons, "保存"));
        Assert.Null(ModalDecisionSafety.MatchButton(buttons, string.Empty));
        Assert.Null(ModalDecisionSafety.MatchButton(Array.Empty<string>(), "接受"));
        Assert.Null(ModalDecisionSafety.MatchButton(null, "接受"));
    }

    [Theory]
    [InlineData("{\"button\":\"接受\",\"reason\":\"发布已完成\"}", "接受")]
    [InlineData("{\"button\": \"确定\"}", "确定")]
    [InlineData("```json\n{\"button\":\"取消\"}\n```", "取消")]
    [InlineData("Answer: {\"button\":\"Continue\"} -- done", "Continue")]
    [InlineData("{\"reason\":\"只有理由没有按钮\"}", "")]
    [InlineData("no json at all", "")]
    public void ExtractButton_OnlyAcceptsStructuredOutput(string raw, string expected)
        => Assert.Equal(expected, ModalDecisionSafety.ExtractButton(raw));

    [Fact]
    public void ExtractButton_NeverGuessesFromFreeText()
    {
        // 只有按钮名、没有结构化键：绝不能把模型的一句解释当成点击指令。
        Assert.Equal(string.Empty, ModalDecisionSafety.ExtractButton("I would click 接受"));
    }

    [Fact]
    public void ExtractReason_PrefersStructuredValueAndTruncatesFallback()
    {
        Assert.Equal("发布已完成",
            ModalDecisionSafety.ExtractReason("{\"button\":\"接受\",\"reason\":\"发布已完成\"}"));

        string raw = new string('x', 400);
        Assert.Equal(160, ModalDecisionSafety.ExtractReason(raw).Length);
        Assert.Equal(string.Empty, ModalDecisionSafety.ExtractReason(null));
    }

    [Theory]
    [InlineData("详细信息", true)]
    [InlineData("Help", true)]      // 归一化后与默认词表 help 对齐
    [InlineData("details", true)]
    [InlineData(">>", true)]
    [InlineData("确定", false)]
    public void IsExpander_MatchesSharedDefaults(string label, bool expected)
        => Assert.Equal(expected, ModalDecisionSafety.IsExpander(label));

    [Fact]
    public void SharedTables_CoverBothLanguages()
    {
        Assert.Contains("删除", ModalDecisionSafety.DefaultDenyWords);
        Assert.Contains("overwrite", ModalDecisionSafety.DefaultDenyWords);
        Assert.Contains("详细信息", ModalDecisionSafety.DefaultExpanderLabels);
        Assert.Contains("help", ModalDecisionSafety.DefaultExpanderLabels);
    }

    // ------------------------------------------------------------------
    // 候选窗口筛子：2026-10-13 AutoCAD 闪退复盘（主框架被当成弹窗）的回归防线。
    // ------------------------------------------------------------------

    [Fact]
    public void DescribeCandidateRejection_RejectsDisabledWindows()
    {
        // 弹窗自己永远可用；被禁用的是它的属主（主框架）。事故中主框架被当成弹窗。
        string? reason = ModalDecisionSafety.DescribeCandidateRejection(
            enabled: false, descendantCount: 3, maxDescendants: 100);

        Assert.NotNull(reason);
        Assert.Contains("禁用", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCandidateRejection_RejectsOversizedWindowTrees()
    {
        // 主框架（Ribbon + 浏览器 + 视口 + 状态栏）有几百上千个子孙窗口。
        string? reason = ModalDecisionSafety.DescribeCandidateRejection(
            enabled: true, descendantCount: 101, maxDescendants: 100);

        Assert.NotNull(reason);
        Assert.Contains("101", reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 40)]
    [InlineData(true, 100)]
    public void DescribeCandidateRejection_AcceptsRealDialogShapes(bool enabled, int descendantCount)
        => Assert.Null(ModalDecisionSafety.DescribeCandidateRejection(enabled, descendantCount, 100));

    [Fact]
    public void DescribeCandidateRejection_ZeroLimitDisablesTheSizeRule()
        => Assert.Null(ModalDecisionSafety.DescribeCandidateRejection(true, 100000, 0));

    [Theory]
    [InlineData("确定", true)]
    [InlineData("打开旧版本 (不允许保存)", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsClickableLabel_RequiresAVisibleCaption(string? label, bool expected)
        => Assert.Equal(expected, ModalDecisionSafety.IsClickableLabel(label));
}
