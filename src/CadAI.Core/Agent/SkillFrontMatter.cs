#nullable enable
using System;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// 技能包入口 Markdown 的 YAML front-matter 读取器：**只认 name / description 两个键**。
    ///
    /// 为什么单独成文件：技能包解析 <c>skill.manifest.json</c> 用的是
    /// .NET Framework 的 <c>System.Web.Script.Serialization</c>，net8 的单测工程编译不了它，
    /// 于是这段纯字符串解析被拆出来——它没有平台依赖，可以整文件链接进测试工程单独验证。
    ///
    /// 解析刻意做得**极度宽容**：技能包是用户/作者手写的，front-matter 写坏一个字符不该让
    /// 整个档位从下拉框里消失。任何异常/不识别都退化成"没读到"，由调用方退回目录名兜底。
    ///
    /// 支持的写法：
    ///   ---
    ///   name: 板类
    ///   description: >        ← 折叠块（多行拼成一行）
    ///     板、衬板、垫板的建模与出图。
    ///     第二行。
    ///   ---
    /// 也接受 <c>title</c> / <c>displayName</c> 当 name，<c>summary</c> / <c>when</c> 当 description，
    /// 以及单引号/双引号包裹的值。
    /// </summary>
    internal static class SkillFrontMatter
    {
        private const string Fence = "---";

        /// <summary>读取 front-matter 的 name 与 description；没有 front-matter 时返回 false。</summary>
        public static bool TryRead(string? markdown, out string? name, out string? description)
        {
            name = null;
            description = null;
            if (string.IsNullOrEmpty(markdown)) return false;

            string[] lines = SplitLines(markdown!);
            int start = 0;
            // 允许 BOM / 空行 / 注释在最前面（这些都不算 front-matter 的开场）。
            while (start < lines.Length && (IsBlank(lines[start]) || IsComment(lines[start]))) start++;
            if (start >= lines.Length || !IsFence(lines[start])) return false;

            int end = -1;
            for (int i = start + 1; i < lines.Length; i++)
            {
                if (IsFence(lines[i])) { end = i; break; }
            }
            if (end < 0) return false;

            ParseBlock(lines, start + 1, end, ref name, ref description);
            name = Clean(name);
            description = Clean(description);
            return !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(description);
        }

        private static void ParseBlock(string[] lines, int from, int to, ref string? name, ref string? description)
        {
            int i = from;
            while (i < to)
            {
                string raw = lines[i];
                if (IsBlank(raw) || raw.TrimStart().StartsWith("#", StringComparison.Ordinal)) { i++; continue; }

                int colon = raw.IndexOf(':');
                if (colon <= 0) { i++; continue; }

                string key = raw.Substring(0, colon).Trim();
                string inline = raw.Substring(colon + 1).Trim();

                // 折叠/字面块：把后续更深缩进的行收进来。
                if (inline == ">" || inline == "|" || inline == ">-" || inline == "|-")
                {
                    var block = new StringBuilder();
                    i++;
                    while (i < to)
                    {
                        string next = lines[i];
                        if (!IsBlank(next) && !char.IsWhiteSpace(next[0])) break;
                        if (block.Length > 0) block.Append(inline.StartsWith("|", StringComparison.Ordinal) ? "\n" : " ");
                        block.Append(next.Trim());
                        i++;
                    }
                    Assign(key, block.ToString(), ref name, ref description);
                    continue;
                }

                Assign(key, inline, ref name, ref description);
                i++;
            }
        }

        private static void Assign(string key, string value, ref string? name, ref string? description)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (name == null && IsNameKey(key)) { name = value; return; }
            if (description == null && IsDescriptionKey(key)) description = value;
        }

        private static bool IsNameKey(string key)
            => key.Equals("name", StringComparison.OrdinalIgnoreCase)
            || key.Equals("title", StringComparison.OrdinalIgnoreCase)
            || key.Equals("displayName", StringComparison.OrdinalIgnoreCase);

        private static bool IsDescriptionKey(string key)
            => key.Equals("description", StringComparison.OrdinalIgnoreCase)
            || key.Equals("summary", StringComparison.OrdinalIgnoreCase)
            || key.Equals("when", StringComparison.OrdinalIgnoreCase);

        private static bool IsFence(string line) => line.TrimEnd().TrimStart().TrimStart('\uFEFF').StartsWith(Fence, StringComparison.Ordinal);

        private static bool IsComment(string line) => line.TrimStart().StartsWith("#", StringComparison.Ordinal);

        private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

        /// <summary>去掉首尾空白与成对引号；把多行压成一行（下拉框 tooltip 用）。</summary>
        private static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value!.Trim();
            if (text.Length >= 2 &&
                ((text[0] == '"' && text[text.Length - 1] == '"') || (text[0] == '\'' && text[text.Length - 1] == '\'')))
            {
                text = text.Substring(1, text.Length - 2).Trim();
            }
            return text.Length == 0 ? null : text.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
        }

        private static string[] SplitLines(string text)
        {
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}