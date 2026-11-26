using System.Collections;
using System;
using System.Collections.Generic;
using System.IO;

namespace CadAI.Mcp.AutoCAD.Core
{
    /// <summary>
    /// AutoCAD MCP 工具执行结果 DTO（与 ChatCore.dll 彻底解耦）。
    /// </summary>
    public sealed class McpExecutionResult
    {
        public string ToolName { get; set; }
        public string Json { get; set; }
        public bool MutatesDrawing { get; set; }
        public IReadOnlyList<string> ImagePaths { get; set; }
    }

    /// <summary>
    /// AutoCADMcp 的宿主环境适配层（零 ChatCore.dll 依赖）：
    /// - 独立运行时自动回退到“我的文档\CADAI”下的标准子目录；
    /// - 当在 AutoCAD AI 助手（Host.AutoCAD）内运行时，由宿主通过静态委托注入当前会话的 WorkspaceLayout 路径。
    /// </summary>
    public static class CadEngineHost
    {
        public static Func<string> RootDirectoryProvider { get; set; }
        public static Func<string> CurrentConversationDirectoryProvider { get; set; }
        public static Func<string> ExportsDirectoryProvider { get; set; }
        public static Func<string> CapturesDirectoryProvider { get; set; }

        public static string DefaultWorkspaceRoot
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(docs)) docs = Path.GetTempPath();
                return Path.Combine(docs, "CADAI");
            }
        }

        public static string RootDirectory
        {
            get
            {
                try
                {
                    string custom = RootDirectoryProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(custom)) return custom;
                }
                catch { }
                Directory.CreateDirectory(DefaultWorkspaceRoot);
                return DefaultWorkspaceRoot;
            }
        }

        public static string CurrentConversationDirectory
        {
            get
            {
                try
                {
                    string custom = CurrentConversationDirectoryProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(custom)) return custom;
                }
                catch { }
                return RootDirectory;
            }
        }

        public static string ExportsDirectory
        {
            get
            {
                try
                {
                    string custom = ExportsDirectoryProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(custom)) return custom;
                }
                catch { }
                string dir = Path.Combine(RootDirectory, "Exports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string CapturesDirectory
        {
            get
            {
                try
                {
                    string custom = CapturesDirectoryProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(custom)) return custom;
                }
                catch { }
                string dir = Path.Combine(Path.GetTempPath(), "DwgAiCaptures");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }
}
