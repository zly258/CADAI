using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 自动视觉校验正文的上下文预算。
/// 背景：校验模型一次能回 1.8 万 token 的分析，而这段正文被永久塞回对话，
/// 后续每一轮 prefill 都背着它。这里锁住"保留头尾、掐中间"的边界行为——
/// 掐错方向会把结论或修正动作丢掉，比不截断更糟。
/// </summary>
public sealed class ReviewContextBudgetTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compact_ReturnsEmptyForBlankInput(string? text)
        => Assert.Equal(string.Empty, ReviewContextBudget.Compact(text));

    [Fact]
    public void Compact_TrimsSurroundingWhitespace()
        => Assert.Equal("结论", ReviewContextBudget.Compact("  结论  "));

    [Fact]
    public void Compact_KeepsTextAtTheBudgetUnchanged()
    {
        string text = new string('a', ReviewContextBudget.MaxCharacters);

        Assert.Equal(text, ReviewContextBudget.Compact(text));
    }

    [Fact]
    public void Compact_KeepsHeadAndTailAndReportsTheOmittedCount()
    {
        // 头部全是 H、尾部全是 T：掐中间不能把开头结论或结尾修正动作掐掉。
        string text = new string('H', 3000) + new string('M', 5000) + new string('T', 3000);
        string compact = ReviewContextBudget.Compact(text);

        int headLength = (int)(ReviewContextBudget.MaxCharacters * ReviewContextBudget.HeadShare);
        int tailLength = ReviewContextBudget.MaxCharacters - headLength;

        Assert.StartsWith(new string('H', headLength), compact, StringComparison.Ordinal);
        Assert.EndsWith(new string('T', tailLength), compact, StringComparison.Ordinal);
        // 11000 - 1440 - 960
        Assert.Contains("省略 8600 字", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_AlwaysShrinksOverBudgetText()
    {
        string text = new string('x', ReviewContextBudget.MaxCharacters * 5);

        Assert.True(ReviewContextBudget.Compact(text).Length < text.Length);
    }

    [Fact]
    public void Sanitize_RemovesDisclaimersAndPythonCode()
    {
        string dirty = @"### 1. 检查发现的问题
* 模型尺寸正常。

*(注：当前环境未挂载自动建模/CAD修正工具。如需继续修改，请确认上述各槽位的设计目标尺寸与宽度是否需对齐，我将为您提供具体的尺寸参数或修正脚本。)*

```python
import cadquery as cq
result = cq.Workplane('XY').box(200, 25, 4)
```

### 2. 结论
正常完成。";

        string clean = ReviewContextBudget.Sanitize(dirty);

        Assert.DoesNotContain("未挂载", clean);
        Assert.DoesNotContain("cadquery", clean);
        Assert.Contains("模型尺寸正常", clean);
        Assert.Contains("正常完成", clean);
    }
}
