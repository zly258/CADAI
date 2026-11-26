namespace CadAI.Core
{
    /// <summary>
    /// 工作区布局的公开契约。两个产品（AutoCAD / AutoCAD）共用同一套「按对话分目录」结构：
    ///
    /// <code>
    /// &lt;WorkspaceRoot&gt;\
    ///   Conversations\
    ///     &lt;yyyyMMdd-HHmmss&gt;-&lt;id8&gt;\
    ///       Conversation\   会话记录（.md / .conversation.json）
    ///       Models\         .ipt / .iam
    ///       Drawings\       .idw / .dwg / .dxf
    ///       Exports\        .pdf / .dwf / .step / 其他生成物
    ///       Captures\       截图（含 Dialogs\）
    ///       Review\         复核证据
    ///       Logs\           引擎日志
    ///       Attachments\    用户附件
    ///       meta.json
    ///   Shared\            跨会话共享（术语库 / 模板 / 无会话时的兜底输出）
    /// </code>
    ///
    /// 根目录下**不再**出现 Captures / Output / Review / History 这些平铺目录。
    /// 宿主与进程内工具（AutoCADMcp）通过本类取路径，不要自行拼 <c>WorkspaceRoot + "Captures"</c>。
    /// </summary>
    public static class WorkspaceLayout
    {
        public static string RootDirectory => AiWorkspace.RootDirectory;
        public static string ConversationsDirectory => AiWorkspace.ConversationsDirectory;
        public static string CurrentConversationName => AiWorkspace.CurrentConversationName;
        public static string CurrentConversationDirectory => AiWorkspace.CurrentConversationDirectory;
        public static string SharedDirectory => AiWorkspace.SharedDirectory;

        /// <summary>当前对话的截图目录（含 Dialogs 子目录）。</summary>
        public static string CapturesDirectory => AiWorkspace.CapturesDirectory;

        /// <summary>当前对话的弹窗截图目录。</summary>
        public static string DialogCapturesDirectory => AiWorkspace.DialogCapturesDirectory;

        /// <summary>当前对话的生成物目录（会话内 Exports\）。</summary>
        public static string ExportsDirectory => AiWorkspace.ExportsDirectory;

        /// <summary>当前对话的模型目录（.ipt/.iam）。</summary>
        public static string ModelsDirectory => AiWorkspace.ModelsDirectory;

        /// <summary>当前对话的图纸目录（.idw/.dwg/.dxf）。</summary>
        public static string DrawingsDirectory => AiWorkspace.DrawingsDirectory;

        /// <summary>当前对话的复核证据目录。</summary>
        public static string ReviewDirectory => AiWorkspace.ReviewDirectory;

        public static string AttachmentsDirectory => AiWorkspace.AttachmentsDirectory;

        /// <summary>取当前对话下指定子目录（不存在则创建）。</summary>
        public static string Subdirectory(string name) => AiWorkspace.ConversationScopedDirectory(name);
    }
}
