using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal sealed partial class AgentSession
    {
        /// <summary>
        /// 把用户文本映射为一组工具 scope。命中多个域时返回多个 scope，工具面取**并集**。
        ///
        /// 规则本身不在 ChatCore 里：由宿主经 <see cref="IToolScopePolicy"/> 提供。
        /// 宿主不给策略时返回空列表，<see cref="SelectToolsForScope"/> 据此不做任何收窄
        /// （把全部工具交给模型）——这比"用一个别的宿主的表去猜"安全得多。
        ///
        /// 历史问题（保留记录）：scope 只看最新一条用户消息时，任务中途需要另一个域的工具
        /// 模型根本看不到，只能报错或让用户换句话再说一遍（例如"先建模，然后出图"里的出图部分）。
        /// 这条约束由宿主实现负责满足（<c>current</c> 参数就是为此传进去的）。
        /// </summary>
        private static IReadOnlyList<string> ResolveToolScopes(string userText, IReadOnlyList<string> current)
        {
            IToolScopePolicy policy;
            try { policy = ChatCoreHostConfig.Current.ToolScopePolicy; }
            catch { policy = null; }

            if (policy == null) return new string[0];
            return policy.ResolveScopes(userText, current) ?? new string[0];
        }

        private static IReadOnlyList<object> SelectToolsForScope(IReadOnlyList<object> tools, IReadOnlyList<string> scopes)
        {
            if (tools == null || tools.Count == 0)
                return tools;

            IToolScopePolicy policy;
            try { policy = ChatCoreHostConfig.Current.ToolScopePolicy; }
            catch { policy = null; }

            // 宿主没有提供收窄策略：全量工具面。这是 CAD 等"工具本来就不多、
            // 也没有成熟分组语义"的宿主的正确默认。
            if (policy == null) return tools;

            if (scopes == null || scopes.Count == 0)
                scopes = new[] { "general" };

            var selected = new List<object>();
            foreach (object tool in tools)
            {
                string name = ToolName(tool);
                if (scopes.Any(scope => policy.IncludesTool(scope, name))) selected.Add(tool);
            }
            return selected.Count == 0 ? tools : selected;
        }

        private static string ToolName(object tool)
        {
            if (!(tool is Dictionary<string, object> outer) || !outer.TryGetValue("function", out object raw) || !(raw is Dictionary<string, object> function)) return string.Empty;
            return function.TryGetValue("name", out object value) ? Convert.ToString(value) ?? string.Empty : string.Empty;
        }

        private static string BuildSystemPrompt(AiSettings settings)
        {
            settings = settings ?? new AiSettings();
            string reasoningRule = settings.ReasoningEnabled
                ? string.Empty
                : "\nUse direct, concise responses. Do not emit chain-of-thought; keep planning minimal and action-oriented.";
            string languageRule = BuildResponseLanguageRule(settings.ResponseLanguage);
            // Markdown 输出规则：界面用第三方 Markdown 组件渲染回复，因此鼓励结构化排版，而不是一整段纯文本。
            const string formattingRule = "\nFormat user-facing answers in Markdown. Use '##' or '###' headings to group steps or results, '-' or numbered lists for sequences, fenced code blocks with a language tag for scripts or snippets, and Markdown tables only when comparing several items. Keep formatting purposeful: no decorative emphasis, no heading level deeper than '###', and never wrap an entire answer in a code block. Put host API names, file paths, parameter names and exact strings in inline code spans (`...`), and keep their characters byte-exact inside those spans.";
            string additional = string.IsNullOrWhiteSpace(settings.AdditionalInstructions)
                ? string.Empty
                : "\nAdditional enterprise system instructions:\n" + LimitInstruction(settings.AdditionalInstructions, 4000);

            // 语言指令放在第一行：小参数量模型会模仿系统提示词的语言，
            // 放在结尾的中文指令权重不足，曾导致整段英文提示词把回答带成英文。
            // 注意：文本编辑协议（id/expected/expectedRevision/dryRun）的细节已由
            // McpToolRegistry 的工具描述逐字承载，这里只保留一句指引，避免每轮 prefill 重复 ~600 token。
            // Phase 2c：系统提示词正文改为宿主配置（ChatCoreHostConfig）提供。
            // AutoCAD 宿主默认使用嵌入式资源 Hosting/SystemPrompt.AutoCAD.txt，
            // 正文与拆分前的硬编码逐字一致；{WorkspaceRoot} 占位符在运行时替换。
            // 动态部分（语言规则 / Markdown 排版规则 / 推理规则 / 附加企业指令）仍在此拼接。
            string promptBody = (ChatCoreHostConfig.Current.SystemPromptTemplate ?? string.Empty)
                .Replace("{WorkspaceRoot}", AiWorkspace.RootDirectory);
            return languageRule.TrimStart('\n') + "\n" +
                   promptBody +
                   languageRule + formattingRule + reasoningRule + additional;
        }

        private static string BuildResponseLanguageRule(string value)
        {
            // 回答语言只有中文/英文两档（Normalize 已把历史 auto 归一到中文）。
            string language = AiSettings.NormalizeResponseLanguage(value);
            if (string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase))
                return "\nLANGUAGE RULE (highest priority): Write EVERY user-facing sentence in English, including progress notes between tool calls. Only tool names, code, file paths, and API identifiers may stay unchanged.";
            return "\nLANGUAGE RULE (highest priority): 你面向用户输出的每一句话都必须用简体中文书写，包括工具调用之间的进度说明、错误说明和最终总结；不要因为系统提示词是英文就用英文回答。只有工具名、代码、文件路径和 API 标识符可以保留英文。";
        }

        private static string LimitInstruction(string value, int max)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }

        /// <summary>
        /// 预处理大模型输出的工具调用参数 JSON。
        /// 当参数中包含文件/目录路径时，若大模型在中英文边界自动插入了多余空格导致路径在磁盘上不存在，
        /// 自动将空格消除并修复为磁盘上实际存在的真实路径。
        /// </summary>
        private static string SanitizeToolArgumentsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            try
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(json, @"[A-Za-z]:\\\\?[^""\r\n]+");
                if (matches.Count == 0) return json;

                string result = json;
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string rawPath = m.Value.Replace(@"\\", @"\");
                    if (!System.IO.Directory.Exists(rawPath) && !System.IO.File.Exists(rawPath))
                    {
                        string stripped = System.Text.RegularExpressions.Regex.Replace(rawPath, @"(?<=[\u4e00-\u9fa5])\s+(?=[a-zA-Z0-9])|(?<=[a-zA-Z0-9])\s+(?=[\u4e00-\u9fa5])", "");
                        if (System.IO.Directory.Exists(stripped) || System.IO.File.Exists(stripped))
                        {
                            string replacement = m.Value.Contains(@"\\") ? stripped.Replace(@"\", @"\\") : stripped;
                            result = result.Replace(m.Value, replacement);
                        }
                    }
                }
                return result;
            }
            catch
            {
                return json;
            }
        }

        private static string BuildToolErrorJson(string toolName, Exception exception, string serverLog = null)
        {
            string json = "{\"ok\":false,\"tool\":" + Quote(toolName ?? string.Empty) +
                          ",\"error_type\":\"mcp_tool_error\",\"recoverable\":true,\"error\":" +
                          Quote(exception?.Message ?? "unknown error");
            if (!string.IsNullOrWhiteSpace(serverLog))
                json += ",\"server_log\":" + Quote(serverLog);
            return json + "}";
        }
        private string NextSyntheticId() => "sys-" + (++_syntheticCallId).ToString("D4");

        /// <summary>
        /// 取一次工具调用产出的图片路径。
        /// 优先用执行结果显式声明的 <see cref="McpExecutionResult.ImagePaths"/>（宿主说了算，最准）；
        /// 没声明时从结果 JSON 里按"值指向图片文件"兜底提取——这样即使宿主没适配新字段，
        /// 截图工具也能被模型看见。
        /// </summary>
        private static IReadOnlyList<string> ResolveToolImagePaths(McpExecutionResult result, string resultJson)
        {
            if (result != null && result.ImagePaths != null && result.ImagePaths.Count > 0)
                return result.ImagePaths;

            return AgentImagePaths.Extract(resultJson);
        }
        private void SaveHistory() => _history.Save(_messages);

        private static string Signature(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private static string NormalizedToolSignature(AgentCompletion completion)
        {
            if (completion == null || completion.ToolCalls.Count == 0) return Signature(completion?.Content ?? string.Empty);
            List<string> parts = completion.ToolCalls
                .Select(call => (call.Name ?? string.Empty) + ":" + (call.ArgumentsJson ?? string.Empty))
                .OrderBy(part => part, StringComparer.Ordinal)
                .ToList();
            return Signature(string.Join(";", parts));
        }

        private static bool LooksLikeUninvokedToolIntent(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            string text = content.ToLowerInvariant();
            // 1. 工具名标记由宿主语义配置提供（AutoCAD: cad_，AutoCAD: cad_）。
            foreach (string marker in ChatCoreHostConfig.Current.UninvokedIntentToolMarkers)
            {
                if (string.IsNullOrWhiteSpace(marker)) continue;
                if (!text.Contains(marker.ToLowerInvariant())) continue;
                if (text.Contains("使用") || text.Contains("调用") || text.Contains("执行") || text.Contains("替换") ||
                    text.Contains("use") || text.Contains("call") || text.Contains("invoke") || text.Contains("replace") || text.Contains("apply"))
                    return true;
            }

            // 2. 任务未完成便提前结束：模型文字中明确表达"接下来/继续进行/开始测试/进行下一组"但本回合漏发了 tool_call。
            if (text.Contains("接下来继续") || text.Contains("继续执行") || text.Contains("接下来进行") ||
                text.Contains("接下来测试") || text.Contains("开始测试第") || text.Contains("准备执行") ||
                text.Contains("进行下一组") || text.Contains("继续进行") || text.Contains("proceed with") ||
                text.Contains("continue with the next"))
            {
                return true;
            }

            return false;
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        public void Dispose()
        {
            // 会话结束前把防抖中的历史记录强制落盘，防止最后 2 秒内的记录丢失。
            try { _history?.Flush(); } catch { }
            try { _client.Dispose(); } catch { }
            try { _executor?.Dispose(); } catch { }
        }

        private sealed class ToolExecutionSummary
        {
            public bool Mutated { get; set; }
            public bool DrawingMutated { get; set; }
            public bool ExternalCheckpointNeeded { get; set; }
            public bool ExternalVisualMutated { get; set; }
            public bool VisualReviewAfterMutation { get; set; }

            /// <summary>
            /// 本轮计划里模型是否已经自己调用过截图工具。已经截过就不再自动补图，
            /// 避免同一轮里既让模型主动截图、框架又截一次，重复发多组图片浪费上下文。
            /// </summary>
            public int ImagesCaptured { get; set; }

            /// <summary>
            /// 本轮工具调用产出的图片路径（去重）。非空时会把它们交给视觉模型，
            /// 让"工具截了图"这件事对模型真正可见。
            /// </summary>
            public List<string> ToolImagePaths { get; } = new List<string>();
        }

    }
}
