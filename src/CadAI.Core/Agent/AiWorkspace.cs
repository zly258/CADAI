using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// AI 助手生成/中间文件的唯一可写工作区。
    /// 用户显式提供的绝对输出路径仍可指向别处，
    /// 但助手自己绝不擅自创建临时/输出目录。
    ///
    /// 2026-10-14：新增「按对话分目录」。此前所有产物平铺在 Workspace 根目录，
    /// 实测几十个 .ipt/.idw/.pdf 混在一起，无法按对话追溯。现在每个会话有固定结构的目录：
    ///
    /// <code>
    /// Workspace/
    ///   Conversations/
    ///     &lt;yyyyMMdd-HHmmss&gt;-&lt;id8&gt;/
    ///       Conversation/   会话记录（.md / .conversation.json）
    ///       Models/         .ipt / .iam
    ///       Drawings/       .idw / .dwg
    ///       Exports/        .pdf / .step / .dxf
    ///       Captures/       截图
    ///       Logs/           引擎日志
    ///       Attachments/    用户附件
    ///       meta.json       会话元信息
    ///   Shared/             跨会话共享（模板、临时）
    ///   History/            旧版会话记录目录（保留只读，新会话不再写入）
    /// </code>
    ///
    /// MCP 进程通过 <see cref="CurrentPointerFileName"/> 指针文件得知"当前对话目录"，
    /// 从而把 workspace 相对路径解析到会话目录内（见 CadAI.Mcp.AutoCAD 的 WorkspacePaths）。
    /// </summary>
    internal static class AiWorkspace
    {
        /// <summary>当前对话指针文件名（放在 Conversations/ 下，内容是目录名）。</summary>
        public const string CurrentPointerFileName = ".current";

        // 会话内的标准子目录名：集中成常量，避免各工程各写一遍字符串。
        public const string ConversationFolder = "Conversation";
        public const string ModelsFolder = "Models";
        public const string DrawingsFolder = "Drawings";
        public const string ExportsFolder = "Exports";
        public const string CapturesFolder = "Captures";
        public const string ReviewFolder = "Review";
        public const string LogsFolder = "Logs";
        public const string AttachmentsFolder = "Attachments";
        public const string DialogsFolder = "Dialogs";
        public const string SharedFolder = "Shared";

        /// <summary>会话目录下的固定子目录（两个产品共用同一套布局）。</summary>
        public static readonly string[] ConversationSubdirectories =
        {
            ConversationFolder, ModelsFolder, DrawingsFolder, ExportsFolder,
            CapturesFolder, ReviewFolder, LogsFolder, AttachmentsFolder
        };

        public static string RootDirectory => EnsureDirectory(ChatCoreHost.RequireEnvironment().WorkspaceRoot);

        /// <summary>
        /// 旧版平铺历史目录。只在「按对话分目录」关闭或创建失败时作为回退使用，
        /// 正常路径不写入、也不再提前创建（避免工作区根目录留下空 History/）。
        /// </summary>
        public static string HistoryDirectory => ChatCoreHost.RequireEnvironment().HistoryDirectory;

        public static string AttachmentsDirectory => ConversationScopedDirectory(AttachmentsFolder);
        public static string CapturesDirectory => ConversationScopedDirectory(CapturesFolder);
        public static string ReviewDirectory => ConversationScopedDirectory(ReviewFolder);
        public static string ModelsDirectory => ConversationScopedDirectory(ModelsFolder);
        public static string DrawingsDirectory => ConversationScopedDirectory(DrawingsFolder);

        /// <summary>
        /// 生成物目录。统一收敛到会话的 <c>Exports/</c>：此前根目录还有一个 <c>Output/</c>，
        /// 与 <c>Exports/</c> 语义重复，用户看到两处都在出文件。
        /// </summary>
        public static string ExportsDirectory => ConversationScopedDirectory(ExportsFolder);

        /// <summary>弹窗截图目录（当前会话的 Captures\Dialogs）。</summary>
        public static string DialogCapturesDirectory
            => EnsureDirectory(Path.Combine(ConversationScopedDirectory(CapturesFolder), DialogsFolder));

        /// <summary>所有会话目录的父目录。</summary>
        public static string ConversationsDirectory => EnsureDirectory(Path.Combine(RootDirectory, "Conversations"));

        /// <summary>跨会话共享目录。</summary>
        public static string SharedDirectory => EnsureDirectory(Path.Combine(RootDirectory, SharedFolder));

        /// <summary>
        /// 按对话分目录的总开关。默认开启；置 false 时退回旧的平铺布局（兼容老用户与排障）。
        /// </summary>
        public static bool UseConversationLayout { get; set; } = true;

        /// <summary>读取当前对话目录名（指针文件内容）；没有则返回空串。</summary>
        public static string CurrentConversationName
        {
            get
            {
                try
                {
                    string pointer = Path.Combine(ConversationsDirectory, CurrentPointerFileName);
                    if (!File.Exists(pointer)) return string.Empty;
                    return File.ReadAllText(pointer, Encoding.UTF8).Trim();
                }
                catch { return string.Empty; }
            }
        }

        /// <summary>当前对话目录的完整路径；未开始会话时返回 Workspace 根目录。</summary>
        public static string CurrentConversationDirectory
        {
            get
            {
                string name = CurrentConversationName;
                if (string.IsNullOrWhiteSpace(name)) return RootDirectory;
                return EnsureDirectory(Path.Combine(ConversationsDirectory, name));
            }
        }

        /// <summary>
        /// 开始一段新对话：创建固定结构的会话目录、写入元信息、更新当前指针。
        /// 返回会话目录完整路径。任何失败都退回 Workspace 根目录，绝不阻断对话。
        /// </summary>
        public static string BeginConversation(string conversationId, DateTime startedAt, string title)
        {
            if (!UseConversationLayout) return RootDirectory;

            try
            {
                string safeId = SanitizeSegment(conversationId);
                if (string.IsNullOrWhiteSpace(safeId)) safeId = Guid.NewGuid().ToString("N").Substring(0, 8);
                string name = startedAt.ToString("yyyyMMdd-HHmmss") + "-" + safeId;
                string directory = EnsureDirectory(Path.Combine(ConversationsDirectory, name));

                foreach (string sub in ConversationSubdirectories)
                    EnsureDirectory(Path.Combine(directory, sub));

                WriteMeta(directory, name, conversationId, startedAt, title);
                WriteCurrentPointer(name);
                return directory;
            }
            catch
            {
                return RootDirectory;
            }
        }

        /// <summary>把当前指针指向指定会话目录（切换历史会话时用）。</summary>
        public static void SetCurrentConversation(string directoryName)
        {
            try { WriteCurrentPointer(directoryName); } catch { }
        }

        /// <summary>
        /// 解析"属于当前对话"的输出目录。有会话时落到 <c>Conversations\&lt;会话&gt;\&lt;sub&gt;</c>；
        /// 没有会话（极早期启动/旧布局关闭）时落到 <c>Shared\&lt;sub&gt;</c>，
        /// **绝不**再往工作区根目录平铺 Captures/Output/Review。
        /// </summary>
        public static string ConversationScopedDirectory(string sub)
        {
            string name = UseConversationLayout ? CurrentConversationName : string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
                return EnsureDirectory(Path.Combine(CurrentConversationDirectory, sub));

            return EnsureDirectory(Path.Combine(SharedDirectory, sub));
        }

        private static void WriteCurrentPointer(string name)
        {
            string pointer = Path.Combine(ConversationsDirectory, CurrentPointerFileName);
            // 原子写：先写临时文件再替换，避免 MCP 侧读到半个文件名。
            string temp = pointer + ".tmp";
            File.WriteAllText(temp, name ?? string.Empty, new UTF8Encoding(false));
            if (File.Exists(pointer))
            {
                try { File.Replace(temp, pointer, null); return; } catch { }
                try { File.Delete(pointer); } catch { }
            }
            File.Move(temp, pointer);
        }

        private static void WriteMeta(string directory, string name, string conversationId, DateTime startedAt, string title)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"schemaVersion\": 1,");
                sb.AppendLine("  \"conversation\": " + Json(name) + ",");
                sb.AppendLine("  \"id\": " + Json(conversationId ?? string.Empty) + ",");
                sb.AppendLine("  \"startedAt\": " + Json(startedAt.ToString("s")) + ",");
                sb.AppendLine("  \"title\": " + Json(title ?? string.Empty) + ",");
                sb.AppendLine("  \"layout\": \"ByConversation\",");
                sb.AppendLine("  \"directories\": ["
                    + string.Join(", ", Array.ConvertAll(ConversationSubdirectories, Json)) + "]");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(directory, "meta.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string Json(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            }
            string text = sb.ToString();
            return text.Length > 16 ? text.Substring(0, 16) : text;
        }

        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            string value = Environment.ExpandEnvironmentVariables(path.Trim());
            return Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(RootDirectory, value));
        }

        /// <summary>
        /// 把 workspace 相对路径解析到**当前对话目录**（未开始会话时退回根目录）。
        /// 绝对路径原样返回。
        /// </summary>
        public static string ResolvePathInConversation(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            string value = Environment.ExpandEnvironmentVariables(path.Trim());
            if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
            if (!UseConversationLayout) return Path.GetFullPath(Path.Combine(RootDirectory, value));

            string baseDirectory = CurrentConversationDirectory;
            return Path.GetFullPath(Path.Combine(baseDirectory, value));
        }

        /// <summary>
        /// 在当前对话的标准子目录里找已存在的文件（只读）。相对路径依次尝试
        /// 会话根 → Models → Drawings → Exports → Captures → Attachments；找不到返回 null。
        /// </summary>
        public static string FindExistingInConversation(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string value = Environment.ExpandEnvironmentVariables(path.Trim());
            if (Path.IsPathRooted(value)) return File.Exists(value) ? Path.GetFullPath(value) : null;

            string relative = value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            foreach (string sub in ConversationSubdirectories)
            {
                if (relative.StartsWith(sub + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative.Substring(sub.Length + 1);
                    break;
                }
            }

            var candidates = new List<string>
            {
                Path.Combine(CurrentConversationDirectory, relative)
            };
            foreach (string sub in ConversationSubdirectories)
                candidates.Add(Path.Combine(CurrentConversationDirectory, sub, relative));

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 解析"要写出的文件"：相对路径落到当前对话的指定子目录（Models/Drawings/Exports/Captures/Review）。
        /// 绝对路径原样返回。与 <see cref="ResolvePathInConversation"/> 的区别是：
        /// 它先锚定子目录，避免相对路径被写到会话根或进程工作目录。
        /// </summary>
        public static string ResolveOutputPath(string path, string subdirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));

            string value = Environment.ExpandEnvironmentVariables(path.Trim());
            if (Path.IsPathRooted(value)) return Path.GetFullPath(value);

            string sub = (subdirectory ?? string.Empty).Trim();
            string relative = value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (sub.Length > 0 &&
                relative.StartsWith(sub + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative.Substring(sub.Length + 1);
            }

            string directory = ConversationScopedDirectory(sub);
            return Path.GetFullPath(Path.Combine(directory, relative));
        }

        private static string EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
