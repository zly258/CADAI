using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CadAI.Core
{
    internal enum AiFailureKind { Permanent, Transient, ContextLimit, Canceled }
    internal static class AiRequestPolicy
    {
        public const int MaxAttempts = 4;
        public const double MaxBackoffSeconds = 12.0;

        /// <summary>
        /// 自动视觉校验的最大尝试次数（含首次）。
        /// 刻意远小于 <see cref="MaxAttempts"/>：校验是**增强手段**，不是用户要的结果，
        /// 而它每次要上传约 1.5 MB 截图、单次可占满整个请求超时。历史故障：一次卡住的
        /// 校验按 4 次重试 × 300s 计算，最坏能把主任务拖住 20 分钟，用户只看到转圈。
        /// </summary>
        public const int ReviewMaxAttempts = 2;
        public static AiFailureKind Classify(Exception exception)
        {
            if (exception == null) return AiFailureKind.Permanent;
            // 原地重复是解码退化，不是网络抖动：原样重发只会再循环一遍。
            // 交给模型回退链换模型，或直接报给用户。
            if (FindInner<AiRepetitionException>(exception, out _)) return AiFailureKind.Permanent;
            if (exception is OperationCanceledException || exception is TaskCanceledException) return exception is TaskCanceledException ? AiFailureKind.Transient : AiFailureKind.Canceled;
            if (exception is TimeoutException) return AiFailureKind.Transient;
            if (FindInner<SocketException>(exception, out SocketException socket))
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused: case SocketError.HostNotFound: case SocketError.HostUnreachable: case SocketError.NetworkUnreachable: case SocketError.AddressFamilyNotSupported: case SocketError.ProtocolNotSupported: return AiFailureKind.Permanent;
                    default: return AiFailureKind.Transient;
                }
            }
            if (FindInner<HttpRequestException>(exception, out _)) return AiFailureKind.Transient;
            if (FindInner<WebException>(exception, out WebException web))
            {
                if (web.Status == WebExceptionStatus.ConnectFailure || web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProtocolError && !IsTransientHttpText(web.Message)) return AiFailureKind.Permanent;
                return AiFailureKind.Transient;
            }
            // 连接已建立、响应已开始，随后被对端掐断（流式回答读到一半"连接已关闭"）。
            // .NET Framework 在这里抛的是**裸 IOException**，没有 HttpRequestException/WebException
            // 包装，因此上面三个类型判断都命中不了——不单独认它，整轮对话就会白失败一次。
            // 这类断流是瞬时故障，且此刻尚无工具副作用，重发是安全的。
            if (IsTransportClosed(exception)) return AiFailureKind.Transient;
            string message = exception.Message ?? string.Empty;
            if (AgentContextCompactor.IsContextLimitError(exception)) return AiFailureKind.ContextLimit;
            if (message.IndexOf("AI 请求失败", StringComparison.OrdinalIgnoreCase) >= 0) { int status = ExtractStatusCode(message); if (status >= 500 || status == 408 || status == 429) return AiFailureKind.Transient; if (status >= 400) return AiFailureKind.Permanent; return AiFailureKind.Transient; }
            if (message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("connection reset", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("远程主机强迫关闭", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("基础连接已经关闭", StringComparison.OrdinalIgnoreCase) >= 0) return AiFailureKind.Transient;
            return AiFailureKind.Permanent;
        }
        public static bool IsRetryable(AiFailureKind kind) => kind == AiFailureKind.Transient;
        // 指数退避加 ±25% 抖动：多个客户端同时失败重试时会形成同步重试风暴，
        // 纯确定性 backoff 会把风暴固定在同一些时间点上。
        // net48 没有 Random.Shared，用带锁的静态实例即可（抖动不需要加密强度）。
        private static readonly Random BackoffRandom = new Random();
        public static TimeSpan BackoffFor(int attempt)
        {
            double baseSeconds = Math.Min(MaxBackoffSeconds, Math.Pow(2.0, Math.Max(0, attempt - 1)));
            double jitter;
            lock (BackoffRandom) { jitter = 0.75 + (BackoffRandom.NextDouble() * 0.5); }
            return TimeSpan.FromSeconds(baseSeconds * jitter);
        }
        public static string Describe(AiFailureKind kind) { switch (kind) { case AiFailureKind.Transient: return "AI 服务临时不可用"; case AiFailureKind.ContextLimit: return "上下文超限"; case AiFailureKind.Canceled: return "已取消"; default: return "AI 服务请求失败"; } }
        private static int ExtractStatusCode(string message) { int start = message.IndexOf("失败", StringComparison.Ordinal); if (start < 0) return 0; int digitsStart = -1; for (int i = start; i < message.Length; i++) { if (char.IsDigit(message[i])) { digitsStart = i; break; } if (message[i] == ':') break; } if (digitsStart < 0) return 0; int end = digitsStart; while (end < message.Length && char.IsDigit(message[end])) end++; return int.TryParse(message.Substring(digitsStart, end - digitsStart), out int status) ? status : 0; }
        private static bool IsTransientHttpText(string text) => !string.IsNullOrEmpty(text) && (text.IndexOf("500", StringComparison.Ordinal) >= 0 || text.IndexOf("502", StringComparison.Ordinal) >= 0 || text.IndexOf("503", StringComparison.Ordinal) >= 0 || text.IndexOf("504", StringComparison.Ordinal) >= 0 || text.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// 连接已建立、随后被对端掐断的判据（含 .NET Framework 的本地化文案）。
        ///
        /// 判据落在消息上而不是类型上：同一件事在不同调用路径上有时是裸 IOException
        /// （流式响应体读到一半被 FIN 掉，见 OpenAiCompatibleClient.StreamOnceAsync），
        /// 有时裹在 WebException 里，唯一稳定可认的是底层那句话。
        /// </summary>
        private static readonly string[] TransportClosedMarkers =
        {
            "Unable to read data from the transport connection",
            "Unable to write data to the transport connection",
            "The connection was closed",
            "An existing connection was forcibly closed",
            "无法从传输连接中读取数据",
            "无法将数据写入传输连接",
            "无法写入传输连接",
            "连接已关闭",
            "基础连接已经关闭",
            "远程主机强迫关闭"
        };

        internal static bool IsTransportClosed(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                if (message.Length == 0) continue;
                foreach (string marker in TransportClosedMarkers)
                    if (message.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool FindInner<T>(Exception exception, out T found) where T : Exception { for (Exception current = exception; current != null; current = current.InnerException) if (current is T match) { found = match; return true; } found = null; return false; }
    }
}
