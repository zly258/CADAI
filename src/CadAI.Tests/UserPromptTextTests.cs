using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 对话气泡只显示用户真正输入的内容：附件清单、PDF 抽取结果、图片视觉事实包装
/// 都是发给模型的上下文，不能在界面（或历史标题）里露出来。
/// </summary>
public sealed class UserPromptTextTests
{
    [Fact]
    public void ToDisplay_KeepsPlainUserText()
    {
        Assert.Equal("根据pdf图纸创建三维模型", UserPromptText.ToDisplay("根据pdf图纸创建三维模型"));
    }

    [Fact]
    public void ToDisplay_StripsAttachmentBlockAndPdfExtraction()
    {
        string prompt =
            "根据pdf图纸创建三维模型\r\n" +
            "\r\n" +
            "[用户附件]\r\n" +
            "用户已把下列文件作为本轮输入。把它们当作任务证据……\r\n" +
            "- E:\\设备基础.pdf (143.4 KB)\r\n" +
            "\r\n" +
            "[PDF 文本抽取: E:\\设备基础.pdf]\r\n" +
            "页数估计: 1\r\n" +
            "未抽取到可读文本。……";

        Assert.Equal("根据pdf图纸创建三维模型", UserPromptText.ToDisplay(prompt));
    }

    [Fact]
    public void ToDisplay_ReturnsEmptyForAttachmentOnlyMessage()
    {
        string prompt = "\r\n[用户附件]\r\n说明\r\n- D:\\图纸\\支架.dwg (2.1 MB)\r\n";

        Assert.Equal(string.Empty, UserPromptText.ToDisplay(prompt));
    }

    [Fact]
    public void ToDisplay_StripsImageFactWrapper()
    {
        string prompt =
            "[Original user request — use this section to determine response language]\n" +
            "把这个截图里的零件建模\n" +
            "\n" +
            "[Internal image visual facts — do not use this section to choose response language]\n" +
            "图中是一个法兰盘，外径 120。";

        Assert.Equal("把这个截图里的零件建模", UserPromptText.ToDisplay(prompt));
    }

    [Fact]
    public void ToDisplay_HandlesImagePlusPdfInOneMessage()
    {
        string prompt =
            "[Original user request — use this section to determine response language]\n" +
            "看图并建模\r\n\r\n[用户附件]\r\n说明\r\n- E:\\a.pdf (1 KB)\r\n\r\n[PDF 文本抽取: E:\\a.pdf]\r\n无\r\n" +
            "\n[Internal image visual facts — do not use this section to choose response language]\n事实";

        Assert.Equal("看图并建模", UserPromptText.ToDisplay(prompt));
    }

    [Fact]
    public void AttachmentNames_ParsesFileNamesFromListLines()
    {
        string prompt =
            "建模\r\n\r\n[用户附件]\r\n说明\r\n- E:\\设备基础.pdf (143.4 KB)\r\n- D:\\图纸\\支架.dwg (2.1 MB)\r\n" +
            "\r\n[PDF 文本抽取: E:\\设备基础.pdf]\r\n页数估计: 1\r\n";

        var names = UserPromptText.AttachmentNames(prompt);

        Assert.Equal(2, names.Count);
        Assert.Equal("设备基础.pdf", names[0]);
        Assert.Equal("支架.dwg", names[1]);
    }

    [Fact]
    public void AttachmentNames_ReturnsEmptyWhenNoAttachmentBlock()
    {
        Assert.Empty(UserPromptText.AttachmentNames("普通消息，没有附件"));
        Assert.Empty(UserPromptText.AttachmentNames(string.Empty));
    }
}
