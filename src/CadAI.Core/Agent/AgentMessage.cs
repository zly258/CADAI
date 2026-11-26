#nullable disable

namespace CadAI.Core
{
    /// <summary>
    /// 发送给模型的单条对话消息。独立成文件，便于上下文压缩逻辑被离线单元测试覆盖。
    /// </summary>
    internal sealed class AgentMessage
    {
        public string Role { get; set; }
        public object Content { get; set; }
        public string Name { get; set; }
        public string ToolCallId { get; set; }
        public object ToolCalls { get; set; }
    }
}
