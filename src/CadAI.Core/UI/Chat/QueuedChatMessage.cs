using System;
using System.Collections.Generic;

namespace CadAI.Core
{
    /// <summary>
    /// 对话排队消息实体：保存用户在任务执行期间输入的文本与关联附件。
    /// </summary>
    public sealed class QueuedChatMessage
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string UserText { get; set; } = string.Empty;
        public List<string> PathAttachments { get; } = new List<string>();
        internal List<AiChatView.PendingImageAttachment> ImageAttachments { get; } = new List<AiChatView.PendingImageAttachment>();
        public DateTime CreatedAt { get; } = DateTime.Now;

        public string PreviewText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(UserText))
                {
                    if (HasAttachments)
                        return $"[附件: {AttachmentCount} 个文件]";
                    return "(空消息)";
                }
                string single = UserText.Replace("\r", " ").Replace("\n", " ").Trim();
                return single.Length > 60 ? single.Substring(0, 60) + "…" : single;
            }
        }

        public int AttachmentCount => PathAttachments.Count + ImageAttachments.Count;
        public bool HasAttachments => AttachmentCount > 0;

        /// <summary>清理该条排队消息携带的临时文件（如剪贴板临时图片）。</summary>
        public void Cleanup()
        {
            foreach (var img in ImageAttachments)
            {
                try { img.DeleteFile(); } catch { }
            }
            ImageAttachments.Clear();
            PathAttachments.Clear();
        }
    }
}
