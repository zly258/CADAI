using System.Collections.Generic;

namespace CadAI.Core
{
    /// <summary>
    /// 插件进程内窄版 MCP 桥接返回的结果。
    /// 此 DTO 由元数据、明细栏/BOM、标题栏、图纸与导出等路由共享。
    /// </summary>
    public sealed class McpExecutionResult
    {
        public string ToolName { get; set; }
        public string Json { get; set; }
        public bool MutatesDrawing { get; set; }

        /// <summary>
        /// 本次调用落盘的图片路径（PNG 等）。非空时 AgentSession 会把它们交给视觉模型，
        /// 并把提取到的视觉事实注入上下文。
        ///
        /// 为什么必须有这个字段：工具结果只有文本，模型**看不见**图片。
        /// 截图工具把 PNG 路径写进 JSON，模型能读到的只是一串路径字符串——
        /// 于是"截了图"这件事对模型等于没发生。宿主可以在这里显式声明；
        /// 不声明时 AgentSession 会按"path 指向图片文件"从结果 JSON 里兜底提取。
        /// </summary>
        public IReadOnlyList<string> ImagePaths { get; set; }
    }
}
