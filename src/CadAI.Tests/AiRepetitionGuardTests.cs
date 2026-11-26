using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

public class AiRepetitionGuardTests
{
    private const string Paragraph =
        "Let me check the current model state again before applying the next change to the assembly. ";

    [Fact]
    public void NormalReasoning_DoesNotTrigger()
    {
        var detector = new RepetitionDetector();

        string text =
            "The user wants a bracket with four mounting holes. " +
            "I should read the current parameters first, then create the sketch on the base face, " +
            "extrude it by twelve millimeters, and finally cut the holes with a matching pattern. " +
            "Before that, verify the active document is an assembly so the part can be placed correctly.";

        Assert.False(detector.Append(text));
    }

    [Fact]
    public void RepeatedParagraph_Triggers()
    {
        var detector = new RepetitionDetector();

        Assert.False(detector.Append(Paragraph));
        Assert.False(detector.Append(Paragraph));
        Assert.True(detector.Append(Paragraph));
    }

    [Fact]
    public void TwoOccurrences_DoNotTrigger()
    {
        var detector = new RepetitionDetector();

        Assert.False(detector.Append(Paragraph));
        Assert.False(detector.Append(Paragraph));
    }

    [Fact]
    public void RepeatedShortPhrase_Triggers()
    {
        var detector = new RepetitionDetector();
        const string phrase = "让我再检查一下模型状态。";

        bool triggered = false;
        for (int i = 0; i < 30 && !triggered; i++)
            triggered = detector.Append(phrase);

        Assert.True(triggered);
    }

    [Fact]
    public void ChunkBoundaries_StillDetectRepetition()
    {
        var detector = new RepetitionDetector();
        string text = string.Concat(Paragraph, Paragraph, Paragraph);

        bool triggered = false;
        foreach (char c in text)
        {
            if (detector.Append(c.ToString()))
            {
                triggered = true;
                break;
            }
        }

        Assert.True(triggered);
    }

    [Fact]
    public void LongUniqueOutput_DoesNotTrigger()
    {
        var detector = new RepetitionDetector();

        for (int i = 0; i < 4000; i++)
        {
            // 每一段都由序号推导、互不相同，64 字符窗口不会重复。
            string chunk = i.ToString("D8")
                + "-" + ((uint)i * 2654435761u).ToString("X8")
                + "-" + ((ulong)i * 40503UL * 40503UL).ToString("X16")
                + "-" + ((i & 0xFFFF) ^ 0x5A5A).ToString("X4") + "\n";
            Assert.False(detector.Append(chunk));
        }
    }

    [Fact]
    public void MarkdownDividerAndBoxLines_DoNotTrigger()
    {
        var detector = new RepetitionDetector();

        // Markdown 分隔线、ASCII 表格边框与大量空格均属于低熵格式字符，绝不能误判为死循环
        string divider = new string('-', 120) + "\n"
            + new string('─', 120) + "\n"
            + "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n"
            + new string(' ', 100) + "\n";

        Assert.False(detector.Append(divider));
    }

    [Fact]
    public void ScatteredIdenticalLines_DoNotTrigger()
    {
        var detector = new RepetitionDetector();

        // 同一句 CAD 图层/规范说明在长思考中相隔 3000+ 字符出现 3 次，属于正常回溯而非原地死循环
        string fillerA = string.Join("", System.Linq.Enumerable.Range(0, 120).Select(i => $"StepA-{i:D3}-coord({i * 10},{i * 20}); "));
        string fillerB = string.Join("", System.Linq.Enumerable.Range(0, 120).Select(i => $"StepB-{i:D3}-layer(L{i})-dim({i * 5}); "));

        Assert.False(detector.Append(Paragraph));
        Assert.False(detector.Append(fillerA));
        Assert.False(detector.Append(Paragraph));
        Assert.False(detector.Append(fillerB));
        Assert.False(detector.Append(Paragraph));
    }
}
