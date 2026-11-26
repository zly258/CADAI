#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CadAI.Core
{
    internal static class AgentContextCompactor
    {
        public const int SoftTokenLimit = 40000;
        public const int HardTokenBudget = 24000;
        public const int DefaultKeepRecentMessages = 14;
        public const int MinimumKeepRecentMessages = 6;
        public const int MinimumTargetTokens = 8000;
        private const int MaxSummaryCharacters = 12000;
        private const int ToolResultShrinkThreshold = 1400;
        private const int ToolResultDigestCharacters = 900;
        private const int ToolResultStringLimit = 180;
        private const int MaxShrinkDepth = 3;
        /// <summary>结构化摘取后仍保留的原文末尾长度（见 CompactToolResult）。</summary>
        private const int ToolResultTailCharacters = 320;
        private const string TailSeparator = "\n…[原文末尾]…\n";
        public const string CompactionMarker = "[自动压缩的历史上下文]";

        private static readonly string[] SalientKeys =
        {
            "success", "ok", "message", "data", "pass", "score", "hard_failures", "improved", "rolled_back", "applied",
            "before_score", "after_score", "before_hard_failures", "after_hard_failures", "reason", "error", "error_type", "hint", "note",
            "policy", "strategy", "skipped", "scale_text", "orthographic_scale", "iso_scale", "annotation_reserve", "group_width", "group_height",
            "moved", "max_layer", "total", "count", "selected", "semantic_candidates", "skipped_existing", "safe_area", "metrics", "gates", "violations",
            "added", "per_view", "empty_views", "semantic_bands", "semantic_duplicate_candidates", "duplicate_dimension_candidates", "feature_summary", "features",
            "dimension_kinds", "slots", "placements", "created", "existed", "title_block", "title_block_inserted", "border", "border_inserted", "sheet", "code",
            "size_changed", "background_white", "image_path", "schema", "stateFingerprint", "currentFingerprint", "beforeFingerprint", "changed", "changeCount", "changes",
            "taskKind", "risk", "focus", "recommendedTools", "recommendedSkills", "requiredReads", "acceptanceGates", "stopConditions", "category", "retryable", "nextAction"
        };
        /// <summary>
        /// 身份键：一条工具结果只要带这些键，就说明它指向"某个具体对象"（零件 / 特征 / 边 / 视图 / 文档）。
        /// 这些键一旦被压缩丢掉，模型就再也引用不回同一个对象——"把刚建的那个孔再放大一点"
        /// 这类追问会退化成"重新猜一遍"，是上下文压缩里代价最高的错误，所以单独成表、必须保留。
        /// </summary>
        private static readonly string[] IdentityKeys =
        {
            "id", "name", "displayName", "componentName", "document", "documentName", "path", "fullPath",
            "fullFileName", "partNumber", "revision", "revisionToken", "referenceKey", "handle", "handles",
            "occurrence", "index", "edge_index", "view_index", "sheet_index"
        };

        /// <summary>结构化摘取实际使用的键集合 = 状态键 ∪ 身份键（去重，顺序稳定）。</summary>
        private static readonly string[] DigestKeys = BuildDigestKeys();

        private static string[] BuildDigestKeys()
        {
            var keys = new List<string>(SalientKeys.Length + IdentityKeys.Length);
            keys.AddRange(SalientKeys);
            foreach (string key in IdentityKeys)
                if (!keys.Contains(key)) keys.Add(key);
            return keys.ToArray();
        }
        public static bool TryCompact(List<AgentMessage> messages, bool force, out string report)
            => TryCompact(messages, force, null, DefaultKeepRecentMessages, SoftTokenLimit, force ? HardTokenBudget : SoftTokenLimit, out report);

        public static bool TryCompact(List<AgentMessage> messages, bool force, string taskState, int keepRecent, out string report)
            => TryCompact(messages, force, taskState, keepRecent, SoftTokenLimit, force ? HardTokenBudget : SoftTokenLimit, out report);

        public static bool TryCompact(
            List<AgentMessage> messages,
            bool force,
            string taskState,
            int keepRecent,
            int triggerTokenLimit,
            int targetTokenBudget,
            out string report)
        {
            report = string.Empty;
            if (messages == null || messages.Count == 0) return false;

            //逐条估算一次并缓存，避免在下面的循环里反复全量重扫。
            //历史性能问题：EstimateTokens 会对非字符串内容（ToolCalls）做 JSON 序列化，
            //再逐字符统计；压缩循环每轮都重算一次，上下文大时是纯粹的浪费。
            int before = EstimateMessages(messages, out int[] perMessage);

            int trigger = Math.Max(MinimumTargetTokens, triggerTokenLimit);
            if (!force && before < trigger) return false;

            int target = Math.Max(MinimumTargetTokens, targetTokenBudget);
            int keep = Math.Max(MinimumKeepRecentMessages, keepRecent);
            var steps = new List<string>();

            int current = before;
            int shrunk = ShrinkToolResults(messages, perMessage, ref current);
            if (shrunk > 0) steps.Add("结构化压缩 " + shrunk + " 条工具结果");

            int removed = 0;
            // current 随折叠增量维护，循环里不再整份重扫。
            for (int round = 0; round < 5 && current > target; round++)
            {
                int roundKeep = Math.Max(MinimumKeepRecentMessages, keep - round * 3);
                int dropped = TryDropOlderMessages(messages, roundKeep, taskState, perMessage, ref current);
                if (dropped <= 0) break;
                removed += dropped;
                //折叠后消息下标整体前移，缓存失效；下一轮重新对齐一次即可（只做一次，不是每轮 N 次全扫）。
                perMessage = MeasureMessages(messages);
            }

            if (removed > 0) steps.Add("折叠较早消息 " + removed + " 条为摘要");
            if (steps.Count == 0) return false;

            int after = EstimateMessages(messages, out _);
            report = "上下文自动压缩：约 " + before + " → " + after + " tokens（" +
                     string.Join("；", steps) + "）；目标预算 " + target +
                     "，保留 " + messages.Count + " 条当前消息。完整历史文件不受影响。";
            return true;
        }

        /// <summary>逐条估算并返回每条消息的 token 数，供压缩过程增量维护总量。</summary>
        private static int EstimateMessages(List<AgentMessage> messages, out int[] perMessage)
        {
            perMessage = MeasureMessages(messages);
            int total = 0;
            for (int i = 0; i < perMessage.Length; i++) total += perMessage[i];
            return total;
        }

        private static int[] MeasureMessages(List<AgentMessage> messages)
        {
            if (messages == null) return Array.Empty<int>();
            var result = new int[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                result[i] = EstimateMessage(messages[i]);
            return result;
        }

        private static int EstimateMessage(AgentMessage message)
        {
            if (message == null) return 0;
            return EstimateText(message.Role)
                + EstimateValue(message.Content)
                + EstimateText(message.Name)
                + EstimateText(message.ToolCallId)
                + EstimateValue(message.ToolCalls)
                + 8;
        }

        public static int EstimateToolTokens(IReadOnlyList<object> tools)
        {
            if (tools == null || tools.Count == 0) return 0;
            int total = 0;
            foreach (object tool in tools)
            {
                total += EstimateValue(tool);
                total += 12;
            }
            return total;
        }

        public static string CompactToolResult(string body, int maxCharacters = 1600)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            maxCharacters = Math.Max(300, maxCharacters);
            if (body.Length <= maxCharacters) return body;

            string digest = TryDigestMcpResult(body);
            if (string.IsNullOrWhiteSpace(digest)) digest = TryDigestJson(body);

            if (!string.IsNullOrWhiteSpace(digest))
            {
                // 结构化摘取是"有损"的：只取开头的白名单键，正文尾巴会整段消失。
                // 但工具经常把结论/报错放在结果末尾（"失败原因：..."），丢掉它模型就只能靠猜。
                // 所以再补一小段原文末尾，并从摘要那一侧让出等量预算，总长仍在 maxCharacters 内。
                int tailBudget = maxCharacters - ToolResultTailCharacters - TailSeparator.Length;
                string tail = tailBudget >= 200 ? OriginalTail(body, ToolResultTailCharacters) : string.Empty;
                if (tail.Length > 0)
                    return Cap(digest, tailBudget) + TailSeparator + tail;
                return Cap(digest, maxCharacters);
            }

            digest = body.Replace("\r", " ").Replace("\n", " ");
            return Cap(digest, maxCharacters);
        }

        public static string DescribeCompactionFailure(int messageCount)
            => "会话已超出模型上下文上限，且当前没有更多可安全压缩的历史内容（消息数 " +
               messageCount + "）。当前 AutoCAD 状态和完整历史文件均已保留。";

        public static bool IsContextLimitError(Exception exception)
        {
            if (exception == null) return false;
            string text = exception.ToString().ToLowerInvariant();
            string[] markers =
            {
                "context length", "context_length", "contextlength", "maximum context", "max context",
                "context window", "too many tokens", "token limit", "tokens in the messages",
                "reduce the length of the messages", "reduce the number of tokens", "prompt is too long",
                "prompt too long", "input is too long", "input too long", "request too large",
                "payload too large", "string too long", "exceed context limit", "exceeds the maximum",
                "maximum number of tokens", "上下文长度", "上下文超限", "超出上下文", "上下文窗口",
                "消息过长", "请求过大"
            };
            foreach (string marker in markers)
                if (text.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            return text.IndexOf("上下文", StringComparison.Ordinal) >= 0 &&
                   text.IndexOf("token", StringComparison.Ordinal) >= 0;
        }

        public static int EstimateTokens(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null) return 0;
            int total = 0;
            foreach (AgentMessage message in messages)
            {
                total += EstimateText(message?.Role);
                total += EstimateValue(message?.Content);
                total += EstimateText(message?.Name);
                total += EstimateText(message?.ToolCallId);
                total += EstimateValue(message?.ToolCalls);
                total += 8;
            }
            return total;
        }

        private static int ShrinkToolResults(List<AgentMessage> messages, int[] perMessage, ref int total)
        {
            int count = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                AgentMessage message = messages[i];
                if (message == null || !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(message.Content is string body) || body.Length <= ToolResultShrinkThreshold) continue;
                string digest = CompactToolResult(body, ToolResultDigestCharacters);
                if (digest.Length >= body.Length) continue;

                int before = EstimateMessage(message);
                message.Content = digest;
                int after = EstimateMessage(message);
                //增量维护总量：省掉压缩结束后对整份历史的一次全量重扫。
                if (perMessage != null && i < perMessage.Length) perMessage[i] = after;
                total += after - before;
                count++;
            }
            return count;
        }

        private static int TryDropOlderMessages(List<AgentMessage> messages, int keepRecent, string taskState, int[] perMessage, ref int total)
        {
            if (messages.Count <= 4) return 0;
            int keep = Math.Max(MinimumKeepRecentMessages, keepRecent);
            int cutoff = Math.Max(2, messages.Count - keep);
            while (cutoff > 2 && !IsSafeBoundary(messages[cutoff])) cutoff--;
            if (cutoff <= 2) return 0;

            var older = new List<AgentMessage>();
            for (int i = 1; i < cutoff; i++) older.Add(messages[i]);
            if (older.Count == 0) return 0;

            string priorSummary = ExtractExistingSummary(messages);
            string summary = BuildSummary(older, taskState, priorSummary);
            AgentMessage system = messages[0];
            var recent = new List<AgentMessage>();
            for (int i = cutoff; i < messages.Count; i++) recent.Add(messages[i]);

            // 折叠掉的 token 用缓存逐条扣减（摘要本身的长度下轮重测时自然并入）。
            int reclaimed = 0;
            if (perMessage != null)
            {
                for (int i = 1; i < cutoff && i < perMessage.Length; i++) reclaimed += perMessage[i];
            }
            if (reclaimed == 0)
            {
                foreach (AgentMessage item in older) reclaimed += EstimateMessage(item);
            }
            total -= reclaimed;

            messages.Clear();
            messages.Add(system);
            messages.Add(new AgentMessage { Role = "system", Content = summary });
            messages.AddRange(recent);
            return older.Count;
        }

        private static bool IsSafeBoundary(AgentMessage message)
            => message != null &&
               (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && message.ToolCalls == null));

        private static string ExtractExistingSummary(List<AgentMessage> messages)
        {
            foreach (AgentMessage message in messages)
            {
                if (message != null &&
                    string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) &&
                    message.Content is string text &&
                    text.StartsWith(CompactionMarker, StringComparison.Ordinal))
                    return text.Substring(CompactionMarker.Length).Trim();
            }
            return string.Empty;
        }

        private static string BuildSummary(IReadOnlyList<AgentMessage> older, string taskState, string priorSummary)
        {
            var builder = new StringBuilder();
            builder.AppendLine(CompactionMarker);
            builder.AppendLine("以下摘要来自较早的对话与工具结果，仅用于保持任务连续性。若与当前实际状态冲突，以当前 MCP 查询结果为准。");
            builder.AppendLine();
            builder.AppendLine("[当前任务状态]");
            builder.AppendLine(string.IsNullOrWhiteSpace(taskState) ? "(尚未采集)" : taskState.Trim());

            if (!string.IsNullOrWhiteSpace(priorSummary))
            {
                builder.AppendLine();
                builder.AppendLine("[更早的压缩摘要]");
                builder.AppendLine(Cap(priorSummary, 3000));
            }

            builder.AppendLine();
            builder.AppendLine("[较早对话与工具结果]");
            var lines = new List<string>();
            int used = 0;
            for (int i = older.Count - 1; i >= 1; i--)
            {
                string line = FormatMessage(older[i]);
                if (used + line.Length > MaxSummaryCharacters)
                {
                    lines.Insert(0, "- …更早的记录已省略（" + i + " 条）…");
                    break;
                }
                lines.Insert(0, line);
                used += line.Length;
            }

            if (older.Count > 0 && string.Equals(older[0].Role, "user", StringComparison.OrdinalIgnoreCase))
                lines.Insert(0, FormatMessage(older[0]));

            foreach (string line in lines) builder.AppendLine(line);
            string result = builder.ToString().Trim();
            return result.Length <= MaxSummaryCharacters + 4000
                ? result
                : result.Substring(0, MaxSummaryCharacters + 4000);
        }

        private static string FormatMessage(AgentMessage message)
        {
            string role = (message?.Role ?? "unknown").ToUpperInvariant();
            string name = string.IsNullOrWhiteSpace(message?.Name) ? string.Empty : " " + message.Name;
            string content = message?.Role != null && string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                ? CompactToolResult(ToText(message.Content), ToolResultDigestCharacters)
                : ToCompactText(message?.Content, 400);
            string calls = message?.ToolCalls == null ? string.Empty : " tool_calls=" + ToCompactText(message.ToolCalls, 300);
            return "- " + role + name + ": " + content + calls;
        }

        private static string TryDigestMcpResult(string body)
        {
            try
            {
                if (!(AgentJson.DeserializeObject(body) is Dictionary<string, object> root)) return null;
                object inner = null;
                if (root.TryGetValue("structuredContent", out object structured) && structured != null) inner = structured;
                if (inner == null && root.TryGetValue("content", out object content) && content is IEnumerable sequence && !(content is string))
                {
                    foreach (object item in sequence)
                    {
                        if (!(item is Dictionary<string, object> part)) continue;
                        if (!part.TryGetValue("text", out object textValue) || textValue == null) continue;
                        string text = Convert.ToString(textValue) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        try { inner = AgentJson.DeserializeObject(text); }
                        catch { inner = text; }
                        break;
                    }
                }
                if (inner == null) return null;

                string innerText;
                try { innerText = inner as string ?? AgentJson.Serialize(inner); }
                catch { innerText = Convert.ToString(inner) ?? string.Empty; }

                string innerDigest = TryDigestJson(innerText);
                if (string.IsNullOrWhiteSpace(innerDigest))
                    innerDigest = Cap(innerText.Replace("\r", " ").Replace("\n", " "), ToolResultDigestCharacters);

                bool isError = false;
                if (root.TryGetValue("isError", out object errorValue))
                {
                    try { isError = Convert.ToBoolean(errorValue); } catch { }
                }

                return AgentJson.Serialize(new Dictionary<string, object>
                {
                    ["_mcp_compacted"] = true,
                    ["isError"] = isError,
                    ["result"] = innerDigest
                });
            }
            catch { return null; }
        }

        private static string TryDigestJson(string body)
        {
            try
            {
                if (!(AgentJson.DeserializeObject(body) is Dictionary<string, object> root)) return null;
                var keep = new Dictionary<string, object>();
                foreach (string key in DigestKeys)
                    if (root.TryGetValue(key, out object value)) keep[key] = ShrinkValue(value, 1);
                if (keep.Count == 0) return null;
                keep["_compacted"] = true;
                keep["_original_chars"] = body.Length;
                return AgentJson.Serialize(keep);
            }
            catch { return null; }
        }

        private static object ShrinkValue(object value, int depth)
        {
            if (value == null || depth > MaxShrinkDepth) return value;
            if (value is string text) return Cap(text, ToolResultStringLimit);

            if (value is IDictionary<string, object> map)
            {
                var shrunk = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (shrunk.Count >= 14) break;
                    shrunk[pair.Key] = ShrinkValue(pair.Value, depth + 1);
                }
                return shrunk;
            }

            if (value is IEnumerable sequence)
            {
                var items = new List<object>();
                int total = 0;
                foreach (object item in sequence)
                {
                    total++;
                    if (items.Count < 4) items.Add(ShrinkValue(item, depth + 1));
                }
                return new Dictionary<string, object> { ["_count"] = total, ["_first"] = items };
            }

            return value;
        }

        private static string ToCompactText(object value, int max)
        {
            if (value == null) return string.Empty;
            string text;
            try { text = value as string ?? AgentJson.Serialize(value); }
            catch { text = Convert.ToString(value) ?? string.Empty; }
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return Cap(text, max);
        }

        private static string ToText(object value)
        {
            if (value == null) return string.Empty;
            if (value is string text) return text;
            try { return AgentJson.Serialize(value); }
            catch { return Convert.ToString(value) ?? string.Empty; }
        }

        /// <summary>取原文末尾一小段（从整行开始，避免半行），用于补回被结构化摘取丢掉的结尾。</summary>
        private static string OriginalTail(string body, int max)
        {
            if (string.IsNullOrEmpty(body) || body.Length <= max) return string.Empty;
            string tail = body.Substring(body.Length - max);
            int newline = tail.IndexOf('\n');
            if (newline >= 0) tail = tail.Substring(newline + 1);
            return tail.Trim();
        }
        private static string Cap(string text, int max)
            => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text.Substring(0, max) + "…");

        private static int EstimateValue(object value)
        {
            if (value == null) return 0;
            try { return EstimateText(value as string ?? AgentJson.Serialize(value)); }
            catch { return EstimateText(Convert.ToString(value)); }
        }

        private static int EstimateText(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int ascii = 0, nonAscii = 0;
            foreach (char c in text)
            {
                if (c <= 127) ascii++;
                else nonAscii++;
            }
            return nonAscii + (int)Math.Ceiling(ascii / 4.0);
        }
    }
}
