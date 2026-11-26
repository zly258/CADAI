using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CadAI.Core
{
    /// <summary>
    /// 将底层异常转化为对话框中展示给用户的报错信息：
    /// 信息密度保持适中——说明故障类型、提炼后的核心原因（不堆砌原始 JSON / HTML / 堆栈）以及一句可执行建议；
    /// 完整异常堆栈仍由调用方写入诊断日志，便于排障。
    /// </summary>
    internal static class AiErrorFormatter
    {
        private const int MaxDetailLength = 120;
        private const int MaxRetryHintLength = 80;

        public static string ToUserMessage(Exception exception, string baseUrl)
        {
            if (exception == null) return "AI 请求失败，请稍后重试。";

            if (Contains<AiRepetitionException>(exception))
            {
                return "模型思考或输出陷入循环，已自动中断并重试但仍未恢复。\n"
                    + "建议：切换模型、换一种问法，或回复“继续”从当前进度接着处理。";
            }

            if (AiRequestPolicy.IsTransportClosed(exception))
            {
                return "AI 服务连接中途断开（上游服务或网关关闭了连接）。\n"
                    + "当前上下文已保留，稍后回复“继续”即可接着处理。";
            }

            if (Contains<SocketException>(exception) || Contains<WebException>(exception) || Contains<HttpRequestException>(exception))
            {
                string detail = ExtractConciseDetail(LastInnerMessage(exception), baseUrl);
                return BuildMessage(
                    "无法连接到 AI 服务。",
                    detail,
                    "请检查服务地址、端口及网络连接是否正常。");
            }

            if (exception is TimeoutException
                || (exception.Message != null && (exception.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                    || exception.Message.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return "AI 服务响应超时（模型加载较慢或当前负载较高）。\n"
                    + "建议：稍后重试，或回复“继续”接着当前进度处理。";
            }

            string message = exception.Message ?? string.Empty;
            if (message.StartsWith("AI 请求失败", StringComparison.OrdinalIgnoreCase))
            {
                int status = ExtractHttpStatusCode(message);
                string detail = ExtractConciseDetail(StripStatusPrefix(message), baseUrl);

                if (message.IndexOf("tool name", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return BuildMessage(
                        "接口拒绝了本次请求（工具名称不符合规范）。",
                        detail,
                        "这是插件侧工具定义问题，与模型配置无关，请联系开发排查。");
                }

                return FormatHttpStatusMessage(status, detail);
            }

            string fallbackDetail = ExtractConciseDetail(message, baseUrl);
            return BuildMessage("AI 执行失败。", fallbackDetail, "可稍后重试，详细信息已记录到诊断日志。");
        }

        /// <summary>生成重试等待提示区的一行精简原因，避免暴露原始 JSON 或冗长英文。</summary>
        public static string ToRetrySummary(string rawMessage, string baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return "上游服务暂时不可用";
            int status = ExtractHttpStatusCode(rawMessage);
            if (status > 0)
            {
                string label = ShortStatusLabel(status);
                return string.IsNullOrEmpty(label) ? $"HTTP {status}" : $"HTTP {status}（{label}）";
            }
            string detail = ExtractConciseDetail(rawMessage, baseUrl, MaxRetryHintLength);
            return string.IsNullOrEmpty(detail) ? "连接异常" : detail;
        }

        private static string FormatHttpStatusMessage(int status, string detail)
        {
            switch (status)
            {
                case 400:
                    return BuildMessage("AI 接口拒绝了请求（HTTP 400）。", detail, "请检查当前模型配置或输入内容，必要时新建对话重试。");
                case 401:
                    return BuildMessage("AI 接口鉴权失败（HTTP 401）。", detail, "请在“设置”中检查 API Key 是否正确或已过期。");
                case 403:
                    return BuildMessage("无权访问当前 AI 服务或模型（HTTP 403）。", detail, "请检查账号权限、模型授权或网络出口限制。");
                case 404:
                    return BuildMessage("AI 接口或模型不存在（HTTP 404）。", detail, "请在“设置”中核对服务地址与模型名称。");
                case 408:
                    return BuildMessage("AI 服务处理超时（HTTP 408）。", detail, "当前请求耗时过长，请稍后重试或拆分任务。");
                case 413:
                    return BuildMessage("请求内容超出服务限制（HTTP 413）。", detail, "请减少附件大小或新建对话后重试。");
                case 429:
                    return BuildMessage("AI 服务请求过于频繁或额度受限（HTTP 429）。", detail, "请稍等片刻后重试，或切换其他可用模型。");
                case 500:
                case 502:
                case 503:
                case 504:
                    return BuildMessage($"AI 服务端暂时异常（HTTP {status}）。", detail, "这通常是上游服务临时故障，稍后回复“继续”即可重试。");
                default:
                    string title = status > 0 ? $"AI 接口请求失败（HTTP {status}）。" : "AI 接口请求失败。";
                    return BuildMessage(title, detail, "请检查模型名称、服务地址配置或稍后重试。");
            }
        }

        private static string ShortStatusLabel(int status)
        {
            switch (status)
            {
                case 400: return "请求参数异常";
                case 401: return "鉴权失败";
                case 403: return "无访问权限";
                case 404: return "接口或模型不存在";
                case 408: return "请求超时";
                case 429: return "请求限流";
                case 500: return "服务内部错误";
                case 502: return "网关错误";
                case 503: return "服务暂不可用";
                case 504: return "网关超时";
                default: return string.Empty;
            }
        }

        private static string BuildMessage(string title, string detail, string suggestion)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return title + "\n" + suggestion;
            return title + "\n原因：" + detail + "\n" + suggestion;
        }

        private static bool Contains<T>(Exception exception) where T : Exception
        {
            for (Exception current = exception; current != null; current = current.InnerException)
                if (current is T) return true;
            return false;
        }

        /// <summary>取最内层异常的消息：连接类错误的真实原因（拒绝/超时/断连）都在最内层。</summary>
        private static string LastInnerMessage(Exception exception)
        {
            string message = exception?.Message ?? string.Empty;
            for (Exception current = exception?.InnerException; current != null; current = current.InnerException)
                if (!string.IsNullOrWhiteSpace(current.Message))
                    message = current.Message;
            return message;
        }

        private static int ExtractHttpStatusCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            Match m = Regex.Match(message, @"AI\s*请求失败\s*(\d{3})", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int code)) return code;
            return 0;
        }

        private static string StripStatusPrefix(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            int colon = message.IndexOf(':');
            if (colon >= 0 && colon + 1 < message.Length)
                return message.Substring(colon + 1).Trim();
            return message;
        }

        /// <summary>
        /// 从原始错误文本（可能是 JSON 错误对象、HTML 网关页或 .NET 异常文案）中提炼一行简明原因。
        /// </summary>
        private static string ExtractConciseDetail(string raw, string baseUrl, int maxLength = MaxDetailLength)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string text = raw.Trim();

            // 1. JSON 错误响应：优先提取 error.message / message / detail / error 字符串字段
            if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
            {
                string extracted = TryExtractJsonMessage(text);
                if (!string.IsNullOrWhiteSpace(extracted))
                    text = extracted;
            }
            // 2. HTML 网关错误页（如 Nginx/Cloudflare 502/504）：优先提取 <title>
            else if (text.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                  || text.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Match titleMatch = Regex.Match(text, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                text = titleMatch.Success ? titleMatch.Groups[1].Value : "上游网关返回错误页面";
            }

            text = Sanitize(text, baseUrl);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > maxLength)
                text = text.Substring(0, maxLength).TrimEnd() + "…";
            return text;
        }

        private static string TryExtractJsonMessage(string json)
        {
            // 按优先级匹配常见 OpenAI / 网关错误字段
            string[] keys = { "message", "detail", "msg", "error_description", "error" };
            foreach (string key in keys)
            {
                Match m = Regex.Match(
                    json,
                    "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                    RegexOptions.IgnoreCase);
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                {
                    string val = m.Groups[1].Value
                        .Replace("\\\"", "\"")
                        .Replace("\\n", " ")
                        .Replace("\\r", " ")
                        .Replace("\\\\", "\\")
                        .Trim();
                    if (val.Length > 0) return val;
                }
            }
            return string.Empty;
        }

        private static string Sanitize(string message, string baseUrl)
        {
            string text = message ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(baseUrl)) text = text.Replace(baseUrl.Trim(), "[服务地址]");
            text = Regex.Replace(text, @"https?://[^\s'<>""]+", "[服务地址]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b", "[地址]");
            return text;
        }
    }
}
