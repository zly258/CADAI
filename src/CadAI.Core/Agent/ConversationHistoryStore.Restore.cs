using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal sealed partial class ConversationHistoryStore
    {
        private static string SnapshotPath(string path) => Path.ChangeExtension(path, "conversation.json");

        internal static IReadOnlyList<AgentMessage> ReadMessages(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Conversation history was not found.", path);
            var json = AgentJson.New();
            string snapshot = SnapshotPath(path);
            if (File.Exists(snapshot))
            {
                try
                {
                    var messages = json.Deserialize<List<AgentMessage>>(File.ReadAllText(snapshot));
                    if (messages != null && messages.Count > 0) return NormalizeRestoredMessages(messages);
                }
                catch { /* 旧版 Markdown 同时也是受损快照的恢复路径。 */ }
            }
            return NormalizeRestoredMessages(ReadMarkdownMessages(File.ReadAllText(path), json));
        }

        private static List<AgentMessage> ReadMarkdownMessages(string markdown, JavaScriptSerializer json)
        {
            var messages = new List<AgentMessage>();
            var body = new StringBuilder();
            string role = null;
            bool conversation = false, fenced = false;
            var pending = new Queue<AgentToolCall>();
            Action flush = () =>
            {
                if (role == null) return;
                string content = body.ToString().Trim();
                object calls = null;
                if (role == "assistant")
                {
                    Match match = Regex.Match(content, @"(?:^|\r?\n)#### Tool calls\r?\n````json\r?\n([\s\S]*?)\r?\n````\s*$");
                    if (match.Success)
                    {
                        try { calls = json.DeserializeObject(match.Groups[1].Value); } catch { }
                        content = content.Substring(0, match.Index).Trim();
                        foreach (AgentToolCall call in ReadToolCalls(calls)) pending.Enqueue(call);
                    }
                }
                var message = new AgentMessage { Role = role, Content = content, ToolCalls = calls };
                if (role == "tool" && pending.Count > 0)
                {
                    AgentToolCall call = pending.Dequeue();
                    message.ToolCallId = call.Id;
                    message.Name = call.Name;
                }
                messages.Add(message);
                body.Clear();
            };
            foreach (string line in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                if (!conversation)
                {
                    if (line == "## Conversation") conversation = true;
                    continue;
                }
                if (!fenced && line == "## Diagnostics") break;
                Match header = !fenced ? Regex.Match(line, @"^### \d+\. (SYSTEM|USER|ASSISTANT|TOOL)\s*$") : Match.Empty;
                if (header.Success)
                {
                    flush();
                    role = header.Groups[1].Value.ToLowerInvariant();
                    continue;
                }
                if (line.StartsWith("````", StringComparison.Ordinal)) fenced = !fenced;
                if (role != null) body.AppendLine(line);
            }
            flush();
            return messages;
        }

        internal static IReadOnlyList<AgentToolCall> ReadToolCalls(object value)
        {
            var calls = new List<AgentToolCall>();
            if (!(value is IEnumerable sequence) || value is string) return calls;
            foreach (object item in sequence)
            {
                if (!(item is Dictionary<string, object> map) || !map.TryGetValue("function", out object raw)
                    || !(raw is Dictionary<string, object> function)) continue;
                string id = map.TryGetValue("id", out object idValue) ? Convert.ToString(idValue) : null;
                string name = function.TryGetValue("name", out object nameValue) ? Convert.ToString(nameValue) : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                string arguments = function.TryGetValue("arguments", out object argumentsValue) ? Convert.ToString(argumentsValue) : "{}";
                calls.Add(new AgentToolCall { Id = id, Name = name, ArgumentsJson = arguments ?? "{}", Raw = map });
            }
            return calls;
        }

        internal static IReadOnlyList<AgentMessage> NormalizeRestoredMessages(IEnumerable<AgentMessage> source)
        {
            var result = new List<AgentMessage>();
            var pending = new Dictionary<string, AgentToolCall>();
            Action finishPending = () =>
            {
                foreach (AgentToolCall call in pending.Values)
                    result.Add(new AgentMessage { Role = "tool", ToolCallId = call.Id, Name = call.Name,
                        Content = "{\"ok\":false,\"skipped\":true,\"reason\":\"No saved result. Restoring history does not execute pending tools.\"}" });
                pending.Clear();
            };
            foreach (AgentMessage message in source ?? Enumerable.Empty<AgentMessage>())
            {
                if (message == null) continue;
                // 使用当天的系统提示词与工具策略，绝不复用旧的已保存指令块。
                if (message.Role == "system") continue;
                if (message.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(message.ToolCallId) && pending.Remove(message.ToolCallId)) result.Add(message);
                    continue;
                }
                if (message.Role != "user" && message.Role != "assistant") continue;
                finishPending();
                var calls = ReadToolCalls(message.ToolCalls);
                result.Add(new AgentMessage { Role = message.Role, Content = message.Content,
                    ToolCalls = calls.Count == 0 ? null : calls.Select(call => call.Raw).ToArray() });
                foreach (AgentToolCall call in calls) pending[call.Id] = call;
            }
            finishPending();
            return result;
        }
    }
}
