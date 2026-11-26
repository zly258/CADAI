using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CadAI.ModalAgent
{
    /// <summary>上报给宿主的弹窗快照。</summary>
    internal sealed class DialogRequest
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>窗口句柄的字符串键（"0x…"），宿主据此做决策去重，避免两层同时决定同一个弹窗。</summary>
        public string Hwnd { get; set; } = string.Empty;

        public string Process { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<string> Buttons { get; set; } = new();
        public string Screenshot { get; set; } = string.Empty;
    }

    /// <summary>宿主返回的决策。</summary>
    internal sealed class DialogDecision
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>要点击的按钮文本；空 = 不动作（保持弹窗交给人工）。</summary>
        public string Button { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        /// <summary>决策来源：ai / rule / none / error，仅用于记录。</summary>
        public string Source { get; set; } = string.Empty;
    }

    /// <summary>
    /// 单向事件消息：本进程用规则处理掉一个弹窗后告知宿主。
    /// 宿主只登记（批量翻译报告等要展示"期间处理了哪些弹窗"），不回复。
    /// </summary>
    internal sealed class DialogEventMessage
    {
        public string Kind { get; set; } = "event";

        /// <summary>处理层：规则 / AI。</summary>
        public string Layer { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        public List<string> Buttons { get; set; } = new();

        /// <summary>实际动作（被点击的按钮文本）；空 = 没有动作。</summary>
        public string Action { get; set; } = string.Empty;

        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// 弹窗桥接客户端：把弹窗快照发给宿主（Add-in），等它回一个决策。
    ///
    /// 设计要点：
    ///   * 一次请求一条短连接，绝不长驻——宿主重启/没启动时 Connect 会在很短超时内失败，
    ///     直接回落成"无决策"，不会拖慢扫描线程；
    ///   * 读响应带超时（同步 ReadLine 放到线程池任务上等），超时即放弃并丢弃连接；
    ///   * 任何异常都返回 null，调用方据此保持"不动作"，因此桥接永远不可能比旧版更激进。
    /// </summary>
    internal static class DialogBridgeClient
    {
        /// <summary>连接宿主的超时：宿主没在听时必须立刻回落，不能占用扫描线程。</summary>
        private const int ConnectTimeoutMs = 800;

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static DialogDecision? Request(string pipeName, DialogRequest request, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || request == null) return null;
            int waitMs = Math.Max(1000, Math.Min(120000, timeoutMs));

            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(ConnectTimeoutMs);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

                writer.WriteLine(JsonSerializer.Serialize(request, WriteOptions));

                // 同步 ReadLine 无法取消，放到线程池上等超时；超时后丢弃连接即可，
                // 那个后台读任务会随连接释放而结束。
                Task<string?> readTask = Task.Run(() => reader.ReadLine());
                if (!readTask.Wait(waitMs)) return null;

                string? line = readTask.Result;
                if (string.IsNullOrWhiteSpace(line)) return null;

                DialogDecision? decision = JsonSerializer.Deserialize<DialogDecision>(line, ReadOptions);
                if (decision == null) return null;

                // 回包必须对应当前请求，防止管道串包导致点到别的弹窗上。
                if (!string.IsNullOrEmpty(decision.Id)
                    && !string.Equals(decision.Id, request.Id, StringComparison.OrdinalIgnoreCase))
                    return null;

                return decision;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 单向发送一条事件（宿主只登记、不回包）。失败静默：事件记录是增强手段，
        /// 绝不能因为宿主没在听就影响弹窗处理本身。
        /// </summary>
        public static void Notify(string pipeName, DialogEventMessage message)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || message == null) return;

            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(ConnectTimeoutMs);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(message, WriteOptions));
            }
            catch
            {
                // 宿主没在跑/管道忙：丢弃这条事件即可。
            }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }
    }
}
