using System;

namespace CadAI.Core
{
    /// <summary>AiChatView 的嵌套类型：会话诊断日志条目。</summary>
    public partial class AiChatView
    {
        internal sealed class DiagnosticEntry
        {
            public DateTime Time { get; set; }
            public string Title { get; set; }
            public string Body { get; set; }
            public bool Code { get; set; }
        }
    }
}
