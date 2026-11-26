using System;
using System.Collections.Generic;
using System.IO;

namespace CadAI.Core
{
    /// <summary>
    /// 用户消息的"显示文本"还原。
    ///
    /// 发给模型、同时存进历史的 user 正文并不等于用户敲的那句话：带附件的消息会拼上
    /// [用户附件] 清单与 PDF 文本抽取结果，图片消息会被包进 [Original user request …]
    /// 段落并在后面追加 [Internal image visual facts …]。这些都是模型上下文，
    /// 对话气泡和历史标题都不该显示它们。
    /// </summary>
    internal static class UserPromptText
    {
        private const string AttachmentBlockMarker = "[用户附件]";
        private const string OriginalRequestMarker = "[Original user request";
        private const string ImageFactsMarker = "[Internal image visual facts";

        /// <summary>去掉注入段，只留用户真正输入的内容。</summary>
        public static string ToDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string result = text;

            // 图片消息：原文被包在 "[Original user request …]" 段里，视觉事实接在后面。
            int original = result.IndexOf(OriginalRequestMarker, StringComparison.Ordinal);
            if (original >= 0)
            {
                int lineEnd = result.IndexOf('\n', original);
                if (lineEnd >= 0)
                {
                    result = result.Substring(lineEnd + 1);
                    int facts = result.IndexOf(ImageFactsMarker, StringComparison.Ordinal);
                    if (facts >= 0) result = result.Substring(0, facts);
                }
            }

            // 文件附件：原文之后是 "[用户附件]" 说明 + 文件清单 + "[PDF 文本抽取 …]"。
            int attachment = result.IndexOf(AttachmentBlockMarker, StringComparison.Ordinal);
            if (attachment >= 0) result = result.Substring(0, attachment);

            return result.Trim();
        }

        /// <summary>从原文的附件清单里取文件名（历史记录没有单独字段，只能从清单行解析）。</summary>
        public static IReadOnlyList<string> AttachmentNames(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(text)) return names;
            int start = text.IndexOf(AttachmentBlockMarker, StringComparison.Ordinal);
            if (start < 0) return names;

            foreach (string line in text.Substring(start).Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    // 清单行格式："- <路径> (<大小>)"，大小后缀可选。
                    string path = trimmed.Substring(2).Trim();
                    int sizeMark = path.LastIndexOf(" (", StringComparison.Ordinal);
                    if (sizeMark > 0 && path.EndsWith(")", StringComparison.Ordinal)) path = path.Substring(0, sizeMark);
                    string name = Path.GetFileName(path);
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                else if (names.Count > 0)
                {
                    break;   // 清单结束，后面是 PDF 抽取段
                }
            }
            return names;
        }
    }
}
