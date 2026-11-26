namespace CadAI.Core
{
    /// <summary>提示词优化时可选注入的当前 AutoCAD 状态摘要。</summary>
    internal sealed class PromptContextSnapshot
    {
        public bool Available { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>AI 提示词优化结果，仅保留预览和替换输入所需的数据。</summary>
    internal sealed class PromptOptimizationResult
    {
        public string Original { get; set; } = string.Empty;
        public string Optimized { get; set; } = string.Empty;
        public string ContextSummary { get; set; } = string.Empty;
    }
}
