using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 智能助手对话排队（Message Queue）UI 契约测试：
/// 守住执行期间可继续输入、排队卡片容器与双按钮布局规范。
/// </summary>
public sealed class MessageQueueContractTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AiChatViewXaml_ContainsMessageQueuePanelAndControls()
    {
        string xamlPath = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Chat", "AiChatView.xaml");
        Assert.True(File.Exists(xamlPath), "AiChatView.xaml 不存在");

        string content = File.ReadAllText(xamlPath);
        Assert.Contains("MessageQueueBorder", content);
        Assert.Contains("QueueTitleText", content);
        Assert.Contains("ClearQueueButton", content);
        Assert.Contains("QueueItemsPanel", content);
    }

    [Fact]
    public void AiChatViewXaml_StopAndSendButtonsCanBeDisplayedTogether()
    {
        string xamlPath = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Chat", "AiChatView.xaml");
        string content = File.ReadAllText(xamlPath);

        // 按钮容器应为横向排列的 StackPanel，允许运行时同时呈现"停止"与"加入队列"按钮
        Assert.Contains("<StackPanel Grid.Column=\"1\" Orientation=\"Horizontal\"", content);
        Assert.Contains("x:Name=\"StopButton\"", content);
        Assert.Contains("x:Name=\"SendButton\"", content);
    }

    [Fact]
    public void QueueSourceFile_ImplementsAllRequiredQueueActions()
    {
        string queueCsPath = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Chat", "AiChatView.Queue.cs");
        Assert.True(File.Exists(queueCsPath), "AiChatView.Queue.cs 不存在");

        string code = File.ReadAllText(queueCsPath);
        Assert.Contains("EnqueueCurrentInput", code);
        Assert.Contains("EditQueuedMessage", code);
        Assert.Contains("RemoveQueuedMessage", code);
        Assert.Contains("MoveQueuedMessage", code);
        Assert.Contains("ClearQueue", code);
        Assert.Contains("CheckAndDispatchNextQueueItemAsync", code);
    }

    [Fact]
    public void QueuedChatMessage_ModelFileExistsAndHasProperties()
    {
        string modelCsPath = RepoPaths.Resolve("src", "CadAI.Core", "UI", "Chat", "QueuedChatMessage.cs");
        Assert.True(File.Exists(modelCsPath), "QueuedChatMessage.cs 不存在");

        string code = File.ReadAllText(modelCsPath);
        Assert.Contains("class QueuedChatMessage", code);
        Assert.Contains("UserText", code);
        Assert.Contains("PathAttachments", code);
        Assert.Contains("ImageAttachments", code);
        Assert.Contains("PreviewText", code);
        Assert.Contains("Cleanup", code);
    }
}
