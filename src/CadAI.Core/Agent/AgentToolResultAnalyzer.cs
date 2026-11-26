using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal static class AgentToolResultAnalyzer
    {
        private static readonly JavaScriptSerializer Json = AgentJson.New();

        public static bool IsFailure(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return false;
            try
            {
                object root = Json.DeserializeObject(result);
                if (ReadDirectBoolean(root, "isError", out bool isError) && isError) return true;
                object payload = ExtractPayload(root);
                if (ReadDirectBoolean(payload, "success", out bool success) && !success) return true;
                if (ReadDirectBoolean(payload, "ok", out bool ok) && !ok) return true;
                if (payload is Dictionary<string, object> direct && direct.TryGetValue("failed", out object count) && Convert.ToInt32(count) > 0) return true;
                if (payload is Dictionary<string, object> map && map.TryGetValue("data", out object data))
                {
                    if (ReadDirectBoolean(data, "ok", out bool dataOk) && !dataOk) return true;
                    if (data is Dictionary<string, object> batch && batch.TryGetValue("failed", out object failed) && Convert.ToInt32(failed) > 0) return true;
                }
            }
            catch { }

            string lower = result.ToLowerInvariant();
            return lower.IndexOf("\"iserror\":true", StringComparison.Ordinal) >= 0
                || lower.IndexOf("mcp 调用失败", StringComparison.Ordinal) >= 0;
        }

        public static bool IsLikelyMutation(string toolName)
        {
            string name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
            if (name.Length == 0) return false;

            // Phase 2c：cad_* 专属的非变更清单由宿主语义配置提供
            // （skills_ 探针、app/context 只读、计划/故障分类工具）。
            // 通用的名称形态启发式（_export/_save 子串、_list/_get 等只读后缀）保持在此。
            ChatCoreHostConfig semantics = ChatCoreHostConfig.Current;
            foreach (string prefix in semantics.NonMutationToolPrefixes)
                if (name.StartsWith(prefix ?? string.Empty, StringComparison.Ordinal)) return false;
            foreach (string exact in semantics.NonMutationToolNames)
                if (name.Equals(exact ?? string.Empty, StringComparison.Ordinal)) return false;
            if (name.IndexOf("_export", StringComparison.Ordinal) >= 0
                || name.IndexOf("_save", StringComparison.Ordinal) >= 0)
                return false;

            string[] readMarkers =
            {
                "_list", "_get", "_info", "_diagnostics", "_status", "_measure", "_review", "_inspect",
                "_find", "_query", "_count", "_bounds", "_references", "_health", "_read", "_recommendation", "_contract"
            };
            if (readMarkers.Any(marker => name.IndexOf(marker, StringComparison.Ordinal) >= 0)) return false;

            // Phase 2c：cad_* 变更清单由宿主语义配置提供（AutoCAD 默认值与拆分前
            // 的硬编码逐字一致，见 ChatCoreHostConfig.MutationToolPrefixes）。
            return semantics.MutationToolPrefixes
                .Any(prefix => name.StartsWith(prefix ?? string.Empty, StringComparison.Ordinal));
        }

        public static bool RequiresStateCheckpoint(string toolName)
        {
            string name = (toolName ?? string.Empty).Trim();

            // 宿主可以整体关掉状态检查点（CAD 没有"模型状态对比"这个概念）。
            try { if (!ChatCoreHostConfig.Current.StateCheckpointEnabled) return false; }
            catch { }

            // 靠属性回读校验的写入（默认是 iProperty）：模型诊断看不到这些值，
            // 而且当前文档是图纸时它必然失败。名单由宿主配置提供。
            if (ChatCoreHostConfig.IsStateCheckpointExempt(name)) return false;

            // 图纸侧的写入（BOM/标题栏/图纸文本/iLogic）验证方式是回读图纸数据，不是模型状态对比。
            // 历史故障：图纸任务里每次写入后都自动跑一次 cad_state_compare，
            // 而它内部要取零件/装配的 ComponentDefinition，在图纸文档上必然抛
            // invalid_state，白白产生一条失败检查点 + 一次无效调用，并让模型误以为出错。
            if (IsDrawingOnlyMutation(name)) return false;

            return IsLikelyMutation(name);
        }

        /// <summary>
        /// 只作用于图纸的变更：它们的正确性由图纸自身的回读校验保证，
        /// 不需要（也不能）跑模型状态检查点。
        /// </summary>
        private static bool IsDrawingOnlyMutation(string name)
        {
            // Phase 2c：仅图纸变更的清单同样来自宿主语义配置。
            ChatCoreHostConfig semantics = ChatCoreHostConfig.Current;
            return semantics.DrawingOnlyMutationPrefixes
                .Any(prefix => name.StartsWith(prefix ?? string.Empty, StringComparison.Ordinal))
                || semantics.DrawingOnlyMutationNames
                    .Any(exact => name.Equals(exact ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }
        public static bool IsVisualMutation(string toolName)
        {
            string name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
            if (!IsLikelyMutation(name)) return false;
            return ChatCoreHostConfig.Current.VisualMutationToolPrefixes
                .Any(prefix => name.StartsWith(prefix ?? string.Empty, StringComparison.Ordinal));
        }

        public static string Signature(string toolName, string argumentsJson)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((toolName ?? string.Empty) + "\n" + (argumentsJson ?? string.Empty)));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private static object ExtractPayload(object root)
        {
            if (root is Dictionary<string, object> dictionary)
            {
                if (dictionary.TryGetValue("structuredContent", out object structured) && structured != null) return structured;
                if (dictionary.TryGetValue("content", out object content) && content is IEnumerable sequence && !(content is string))
                {
                    foreach (object item in sequence)
                    {
                        if (!(item is Dictionary<string, object> part)) continue;
                        if (!part.TryGetValue("text", out object textValue) || textValue == null) continue;
                        string text = Convert.ToString(textValue) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        try { return Json.DeserializeObject(text); }
                        catch { return text; }
                    }
                }
            }
            return root;
        }

        private static bool ReadDirectBoolean(object value, string key, out bool result)
        {
            result = false;
            if (!(value is Dictionary<string, object> dictionary) || !dictionary.TryGetValue(key, out object raw) || raw == null) return false;
            try { result = Convert.ToBoolean(raw); return true; }
            catch { return false; }
        }
    }
}