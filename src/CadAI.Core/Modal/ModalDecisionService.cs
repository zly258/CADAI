using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

using CadAI.Core;
using CadAI.Core.Modal;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// 弹窗 AI 决策：把 ModalAgent 上报的弹窗（文本 + 截图）交给模型，换回一个"点哪个按钮"的结论。
    ///
    /// 位置很关键：本方法由 <see cref="ModalDialogBridgeServer"/> 的后台线程调用。
    /// 宿主被模态框阻塞时，AutoCAD 主 STA 线程正停在弹窗消息循环里，卡住的那次工具调用
    /// 还在等它返回——所以决策链路必须完全绕开 UI 线程与 AutoCAD API，否则就是死锁。
    ///
    /// 安全策略（默认全保守，任何一步失败都退回"不动作"）：
    ///   1) 模型只能从弹窗上真实存在的按钮里选，编造的按钮一律丢弃；
    ///   2) 命中危险语义（删除/覆盖/放弃/不保存…）的按钮一律拒绝，绝不代用户做破坏性选择；
    ///   3) 请求带超时，超时/异常 = 无决策，弹窗保持原样交给人工；
    ///   4) 每次决策都写审计（含模型原始输出），现场可追溯。
    /// </summary>
    public sealed class ModalDecisionService
    {
        /// <summary>截图上限：超过这个大小就不走视觉，避免把大图 base64 塞进请求。</summary>
        private const long MaxScreenshotBytes = 8L * 1024 * 1024;

        private readonly Action<string> _log;

        /// <summary>
        /// 当前正在执行的工具名。由执行器在调用前后维护，作为弹窗决策的上下文——
        /// "正在导出 DWF 时弹出的发布警告"和"用户手动点保存弹出的框"处置方式完全不同。
        ///
        /// 采用进程级同步栈：工具在 UI/执行线程上被调用，而弹窗决策由 ModalDialogBridgeServer
        /// 在独立的命名管道后台线程上监听并执行 Decide。如果使用 ThreadStatic，
        /// 桥接线程看到的栈永远为空（报 tool=(empty)）。进程级栈 + 锁既支持多执行器嵌套，
        /// 又能确保跨管道监听线程准确获取当前阻塞的工具名。
        /// </summary>
        private static readonly object _toolLock = new object();
        private static readonly Stack<string> _toolStack = new Stack<string>();

        public static string CurrentToolName
        {
            get
            {
                lock (_toolLock)
                {
                    return _toolStack.Count > 0 ? _toolStack.Peek() : string.Empty;
                }
            }
        }

        public static void PushTool(string toolName)
        {
            lock (_toolLock)
            {
                _toolStack.Push(toolName ?? string.Empty);
            }
        }

        public static void PopTool()
        {
            lock (_toolLock)
            {
                if (_toolStack.Count > 0) _toolStack.Pop();
            }
        }

        /// <summary>
        /// 判定"最近有自动化工具在跑"的时间窗。不能写成"当下正好在调用中"：弹窗可能出现在
        /// 一次工具调用返回之后、下一次调用之前（AutoCAD 的延迟提示），窗口放宽才覆盖得到。
        /// </summary>
        private static readonly TimeSpan AutomationWindow = TimeSpan.FromMinutes(10);

        private static long _lastToolTicksUtc;

        /// <summary>由工具执行器在每次调用**开始**时打点（弹窗是在调用期间弹出来的）。</summary>
        public static void MarkToolActivity()
            => System.Threading.Interlocked.Exchange(ref _lastToolTicksUtc, DateTime.UtcNow.Ticks);

        private static bool HasRecentAutomation()
        {
            long ticks = System.Threading.Interlocked.Read(ref _lastToolTicksUtc);
            if (ticks <= 0) return false;
            try { return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= AutomationWindow; }
            catch { return false; }
        }

        /// <summary>
        /// 决策记忆时长：这么久之后还开着的弹窗，允许看门狗再问一次。
        /// 取值与 ModalAgent 侧"未获决策后的冷却"同量级，避免两层互相踩着重问。
        /// </summary>
        private static readonly TimeSpan DecisionMemory = TimeSpan.FromMinutes(5);

        private static readonly object DecisionSync = new object();
        private static readonly Dictionary<string, DateTime> RecentlyDecided =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 窗口句柄的字符串键。Add-in 侧（看门狗探测）与 ModalAgent 侧（上报）必须使用
        /// 完全相同的格式，否则决策记忆对不上，两层就会同时决定同一个弹窗。
        /// </summary>
        public static string HwndKey(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X");

        /// <summary>这个窗口最近是否已经走过一次决策（不论结论是点还是不点）。</summary>
        public static bool WasRecentlyDecided(string hwndKey)
        {
            if (string.IsNullOrWhiteSpace(hwndKey)) return false;
            lock (DecisionSync)
            {
                if (!RecentlyDecided.TryGetValue(hwndKey, out DateTime at)) return false;
                return DateTime.UtcNow - at <= DecisionMemory;
            }
        }

        private static void RememberDecision(string hwndKey)
        {
            if (string.IsNullOrWhiteSpace(hwndKey)) return;
            lock (DecisionSync)
            {
                RecentlyDecided[hwndKey] = DateTime.UtcNow;
                if (RecentlyDecided.Count <= 128) return;

                DateTime cutoff = DateTime.UtcNow - DecisionMemory;
                foreach (string key in new List<string>(RecentlyDecided.Keys))
                {
                    if (RecentlyDecided[key] < cutoff) RecentlyDecided.Remove(key);
                }
            }
        }

        public ModalDecisionService(Action<string> log)
        {
            _log = log ?? (message => { });
        }

        public ModalDialogDecision Decide(ModalDialogRequest request)
        {
            var none = new ModalDialogDecision { Button = string.Empty, Source = "none" };
            if (request == null) return none;
            none.Id = request.Id;

            // 记住这次询问：卡死看门狗据此判断"这个窗口常驻通道已经在管了"，
            // 避免两层同时决定同一个弹窗该点哪个按钮。
            RememberDecision(request.Hwnd);

            if (request.Buttons == null || request.Buttons.Count == 0)
            {
                none.Reason = "弹窗没有可选按钮";
                return none;
            }

            var stopwatch = Stopwatch.StartNew();
            string raw = string.Empty;
            string mode = "text";

            // 只代答"自动化引起的弹窗"。用户自己操作弹出的窗口（许可证、手动保存、
            // 手动打开旧版本图纸…）不该由 AI 替他点——那不是他的意思。
            // 这里回 skipped（而不是 none）让 ModalAgent 用短冷却，自动化一开始就能再问。
            if (!HasRecentAutomation())
            {
                none.Reason = "近期没有自动化工具调用，判定为用户手动弹窗，不代答";
                none.Source = "skipped";
                Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                return none;
            }

            try
            {
                AiSettings settings = AiSettings.Load();
                if (settings == null || string.IsNullOrWhiteSpace(settings.Model))
                {
                    none.Reason = "未配置模型";
                    Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                    return none;
                }

                string userPrompt = BuildUserPrompt(request);
                bool useImage = ShouldUseImage(request);

                // 决策超时按用户设置走（默认 12s）。它必须留在 ModalAgent 的决策等待窗口
                // （modal-agent.json 的 DecisionTimeoutMs，默认 20s）之内，否则宿主还没答完、
                // 对方已经放弃并保持弹窗不动作，等于白问一次。
                int aiTimeoutSeconds = AiSettings.NormalizeModalDecisionTimeoutSeconds(settings.ModalDecisionTimeoutSeconds);

                raw = AskModel(settings, userPrompt, request.Screenshot, useImage, aiTimeoutSeconds, out mode);

                // 视觉不可用（模型不支持图片、或截图那一问失败）时不直接放弃：
                // 退回纯文本再问一次，标题 + 按钮列表通常已经够判断。
                // 第二次只用剩余预算，保证两次加起来仍在 ModalAgent 的等待窗口内。
                if (useImage && string.IsNullOrWhiteSpace(ModalDecisionSafety.ExtractButton(raw)))
                {
                    int remaining = aiTimeoutSeconds - (int)stopwatch.Elapsed.TotalSeconds;
                    if (remaining >= 3)
                    {
                        string fallbackMode;
                        raw = AskModel(settings, userPrompt, request.Screenshot, false, remaining, out fallbackMode);
                        mode = fallbackMode;
                    }
                }
            }
            catch (Exception ex)
            {
                none.Reason = "模型请求失败：" + ex.Message;
                _log("[ModalDecision] 决策失败：" + ex.Message);
                Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                return none;
            }

            string candidate = ModalDecisionSafety.ExtractButton(raw);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                none.Reason = "模型未给出按钮";
                Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                return none;
            }

            string matched = ModalDecisionSafety.MatchButton(request.Buttons, candidate);
            if (matched == null)
            {
                none.Reason = "模型给出的按钮不在弹窗中：" + candidate;
                Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                return none;
            }

            if (ModalDecisionSafety.IsDenied(matched))
            {
                none.Reason = "命中危险语义，按策略不点击：" + matched;
                Audit(request, mode, raw, none, stopwatch.ElapsedMilliseconds);
                return none;
            }

            var decision = new ModalDialogDecision
            {
                Id = request.Id,
                Button = matched,
                Reason = ModalDecisionSafety.ExtractReason(raw),
                Source = mode == "vision" ? "ai-vision" : "ai"
            };
            Audit(request, mode, raw, decision, stopwatch.ElapsedMilliseconds);
            return decision;
        }

        /// <summary>
        /// 问一次模型。所有异常都收敛成空串（调用方据此回落），并回传实际使用的模式。
        ///
        /// 同步阻塞在当前线程（桥接服务线程）：该线程是普通后台线程，没有
        /// SynchronizationContext，客户端内部又是 ConfigureAwait(false)，
        /// 因此 GetAwaiter().GetResult() 在这里不存在死锁风险。
        /// </summary>
        private string AskModel(AiSettings settings, string userPrompt, string screenshot, bool useImage, int timeoutSeconds, out string mode)
        {
            mode = useImage ? "vision" : "text";
            int seconds = Math.Max(3, timeoutSeconds);
            try
            {
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
                using (var client = new OpenAiCompatibleClient(settings))
                {
                    if (useImage)
                    {
                        // 视觉兜底：正文读不到（树控件/WPF/自绘弹窗）时才付这份成本。
                        AgentImageReview review = client.ReviewImagesAsync(
                            new[] { screenshot }, userPrompt, null, cancellation.Token).GetAwaiter().GetResult();
                        return review?.Content ?? string.Empty;
                    }
                    return client.CompleteOnceAsync(
                        SystemPrompt, userPrompt, null, cancellation.Token, seconds).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _log("[ModalDecision] 模型请求失败（" + mode + "）：" + ex.Message);
                mode = mode + "-error";
                return string.Empty;
            }
        }

        private const string SystemPrompt =
            "You answer a single modal dialog inside the host desktop application so that a blocked automation task can continue. "
            + "The host cannot proceed until this dialog is dismissed. "
            + "Choose exactly one button from the list given by the user. "
            + "Reply with strict JSON only, no prose and no code fence: "
            + "{\"button\":\"<exact button text>\",\"reason\":\"<one short sentence>\"}. "
            + "If no listed button is safe, reply {\"button\":\"\",\"reason\":\"<why>\"}.";

        private static string BuildUserPrompt(ModalDialogRequest request)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Dialog title: " + Safe(request.Title));
            builder.AppendLine("Dialog message: " + Safe(request.Body));
            builder.AppendLine("Available buttons: " + string.Join(" | ", request.Buttons ?? new List<string>()));
            builder.AppendLine("Host process: " + Safe(request.Process));
            builder.AppendLine("Automation tool currently blocked: " + Safe(CurrentToolName));
            builder.AppendLine();
            builder.AppendLine("Pick the button that unblocks the host without destroying, discarding, overwriting or replacing user data.");
            builder.AppendLine("Never pick a button that deletes, removes, clears, overwrites, replaces or discards data.");
            builder.AppendLine("Use the exact button text from the list. Reply with JSON only.");
            return builder.ToString();
        }

        private static bool ShouldUseImage(ModalDialogRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Screenshot)) return false;
                if (!File.Exists(request.Screenshot)) return false;
                var info = new FileInfo(request.Screenshot);
                if (info.Length <= 0 || info.Length > MaxScreenshotBytes) return false;

                // 正文为空或极短时，文本判断基本没有依据（正文在树控件里的弹窗正是这种情况），
                // 这时才把截图交给视觉模型。正文足够时优先走文本：更快、更确定、成本更低。
                return string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim().Length <= 8;
            }
            catch { return false; }
        }

        private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Replace("\r", " ").Replace("\n", " ");

        /// <summary>审计：弹窗内容 + 模型原始输出 + 最终动作。现场排查只靠这个文件。</summary>
        private void Audit(ModalDialogRequest request, string mode, string raw, ModalDialogDecision decision, long elapsedMs)
        {
            try
            {
                string line = string.Join(" | ", new[]
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    "mode=" + mode,
                    "tool=" + (string.IsNullOrWhiteSpace(CurrentToolName) ? "-" : CurrentToolName),
                    "title=" + Safe(request.Title),
                    "buttons=[" + string.Join(" / ", request.Buttons ?? new List<string>()) + "]",
                    "raw=" + (string.IsNullOrWhiteSpace(raw) ? "-" : raw.Replace("\r", " ").Replace("\n", " ")),
                    "action=" + (string.IsNullOrWhiteSpace(decision.Button) ? "none" : decision.Button),
                    "reason=" + Safe(decision.Reason),
                    "ms=" + elapsedMs
                });

                string directory = ResolveLogDirectory();
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "modal-decisions.log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 2 * 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }

                // 供工具结果附带：让模型知道"期间不是卡住了，而是自动处理了这些弹窗"。
                ModalActivityLog.Add(string.IsNullOrWhiteSpace(decision.Button)
                    ? "弹窗未处置（" + Safe(request.Title) + "）：" + Safe(decision.Reason)
                    : "自动点击「" + decision.Button + "」（" + Safe(request.Title) + "）：" + Safe(decision.Reason));
            }
            catch (Exception ex)
            {
                _log("[ModalDecision] 审计写入失败：" + ex.Message);
            }
        }

        private static string ResolveLogDirectory()
        {
            try
            {
                IHostEnvironment environment = ChatCoreHost.Environment;
                if (environment != null && !string.IsNullOrWhiteSpace(environment.LogDirectory)) return environment.LogDirectory;
            }
            catch { }
            try
            {
                // 宿主未注入环境时的兜底：跟随品牌名，而不是写死 CADAI——
                // 换宿主复用时日志自然落到对应产品目录。
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Branding.ProductName, "logs");
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 弹窗活动环形缓冲：弹窗处理发生在某个工具调用期间（甚至那次调用正被它阻塞），
    /// 所以只能在调用返回后把摘要附到结果里，让模型知道这一轮发生过什么。
    /// </summary>
    internal static class ModalActivityLog
    {
        private const int Capacity = 20;

        private static readonly object Sync = new object();
        private static readonly List<string> Recent = new List<string>();

        public static void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (Sync)
            {
                Recent.Add(line);
                while (Recent.Count > Capacity) Recent.RemoveAt(0);
            }
        }

        /// <summary>取走并清空当前摘要；没有活动时返回空串。</summary>
        public static string Drain()
        {
            lock (Sync)
            {
                if (Recent.Count == 0) return string.Empty;
                string summary = string.Join("；", Recent);
                Recent.Clear();
                return summary;
            }
        }
    }
}
