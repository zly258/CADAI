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
    /// <summary>
    /// 统一的 AutoCAD 智能体会话。建模、装配、工程图、审图与导出共用同一套工具面，
    /// 用户不需要（也不应该）先选"建模模式"或"出图模式"。
    /// </summary>
    internal sealed partial class AgentSession
    {
        private const int MaxContextRecoveryAttempts = 3;
        private const int ToolCallsPerAgentCallBudget = 8;
        private const int MaxReplanAttemptsPerTurn = 2;
        private const int AbsoluteMaxAgentCallsPerTurn = 500;
        private const int AbsoluteMaxToolCallsPerTurn = 2000;
        private const int MaxIdenticalToolPlanExecutions = 1;

        /// <summary>
        /// 本轮是否因为用完对话预算（工具调用上限或续跑段数用尽）而结束，且还有未完成的工作。
        /// 界面据此提示用户"新建对话继续"——批量任务在同一个对话里继续只会让上下文越来越臃肿，
        /// 开一段新对话接着做更快也更稳。
        /// </summary>
        internal bool BudgetExhausted { get; private set; }
        /// <summary>
        /// 一次对话内允许的续跑段数上限。批量任务（多图纸/多文件）经常超出一个对话的容量，
        /// 达到上限后本轮结束并置 <see cref="BudgetExhausted"/>，由界面提示"新建对话继续"，
        /// 而不是继续在同一个对话里堆上下文。
        /// </summary>
        private const int MaxContinuationSegments = 10;
        // 保留最近一次读取的证据（如 BOM 行）。更久远的结果交给整体上下文压缩处理。
        private const int CurrentToolResultCharacters = 64000;

        private OpenAiCompatibleClient _client;
        // Phase 2：工具执行与 schema 全部经宿主注入的 IToolExecutor（AutoCAD 宿主的
        // AutoCADToolExecutor 组合了进程内图纸直连与独立 CadAI.Mcp.AutoCAD 服务器）。
        private readonly IToolExecutor _executor;
        private readonly IReadOnlyList<object> _tools;
        private IReadOnlyList<object> _turnTools;
        private IReadOnlyList<string> _activeToolScopes = new[] { "general" };
        private readonly ConversationHistoryStore _history;
        private readonly List<AgentMessage> _messages = new List<AgentMessage>();
        private readonly JavaScriptSerializer _json = AgentJson.New();
        private int _syntheticCallId;
        private string _taskStateSnapshot = string.Empty;
        private readonly int _maxAgentCalls;
        private readonly int _effectiveMaxAgentCalls;
        private readonly int _hardMaxAgentCalls;
        private AiSettings _settings;
        private readonly string _responseLanguage;
        private bool _englishStatus;
        private readonly int _maxToolCallsPerTurn;
        private readonly bool _autoCompactContext;
        private readonly int _contextWindowTokens;
        private readonly int _contextReserveTokens;
        /// <summary>待展示的活动提示（压缩/分段续跑）。压缩发生在 BeginPhase 之前，延后到下一阶段占位建好时显示。</summary>
        private string _lastActivityHint;

        public AgentSession(AiSettings settings, IToolExecutor executor)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings;
            _responseLanguage = AiSettings.NormalizeResponseLanguage(settings.ResponseLanguage);
            _maxAgentCalls = AiSettings.NormalizeMaxAgentCalls(settings.MaxAgentCalls);
            // 0 或负数表示不限制调用次数，给予宽裕的轮数上限；若配置了正数，则尊重用户设置不再强行压缩到 24
            _effectiveMaxAgentCalls = _maxAgentCalls <= 0
                ? 100
                : _maxAgentCalls;
            _hardMaxAgentCalls = _maxAgentCalls <= 0
                ? AbsoluteMaxAgentCallsPerTurn
                : Math.Min(
                    AbsoluteMaxAgentCallsPerTurn,
                    Math.Max(_effectiveMaxAgentCalls, _effectiveMaxAgentCalls * MaxContinuationSegments));
            _maxToolCallsPerTurn = Math.Min(
                AbsoluteMaxToolCallsPerTurn,
                (int)Math.Min(int.MaxValue, Math.Max(64L, (long)_hardMaxAgentCalls * ToolCallsPerAgentCallBudget)));
            _autoCompactContext = settings.AutoCompactContext;
            // 0 = 按模型名自动推断（1M/200k/128k），显式配置才按配置值。
            _contextWindowTokens = AiSettings.ResolveContextWindowTokens(settings);
            _contextReserveTokens = Math.Max(
                4096,
                AiSettings.NormalizeMaxOutputTokens(settings.MaxOutputTokens) > 0
                    ? AiSettings.NormalizeMaxOutputTokens(settings.MaxOutputTokens) + 2048
                    : 8192);
            _client = new OpenAiCompatibleClient(settings);
            _tools = executor.ToolSchemas();
            _history = new ConversationHistoryStore(settings);
            // P7 保留策略：每段会话开始时把截图目录压到上限内（清理最旧的）。
            // 失败静默——清理绝不能阻断任务。
            try
            {
                int deleted = CaptureRetentionPolicy.Enforce(AiWorkspace.CapturesDirectory);
                if (deleted > 0) _history.Record("CAPTURES PRUNED", "deleted=" + deleted + " old screenshot(s)", false);
            }
            catch { }
            _messages.Add(new AgentMessage
            {
                Role = "system",
                Content = BuildSystemPrompt(settings)
            });
            SaveHistory();
        }

        internal string HistoryFilePath => _history?.FilePath;

        /// <summary>
        /// 设置窗口保存后同步到已打开的会话：替换客户端，让**下一条消息**
        /// 使用新的端点/密钥/模型。正在进行的调用持有旧 client 的引用，
        /// 会按旧配置跑完，不会中途换连接。
        /// </summary>
        public void UpdateSettings(AiSettings settings)
        {
            if (settings == null) return;
            _settings = settings;
            _client = new OpenAiCompatibleClient(settings);
        }

        internal void RestoreHistory(IReadOnlyList<AgentMessage> messages)
        {
            if (_messages.Count > 1) throw new InvalidOperationException("Restore history into a new session only.");
            _messages.AddRange(ConversationHistoryStore.NormalizeRestoredMessages(messages));
            string lastUser = _messages.LastOrDefault(message => message.Role == "user")?.Content as string;
            _activeToolScopes = ResolveToolScopes(lastUser, new[] { "general" });
            _messages.Add(new AgentMessage { Role = "system", Content = "Conversation history was restored. Previous tool results describe past state. Re-read current " + ChatCoreHostConfig.HostProductNameOrDefault() + " state before making further edits; never execute old pending tool calls." });
            SaveHistory();
        }

        public async Task RunTurnAsync(string userText, IAgentUi ui, CancellationToken cancellationToken)
        {
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            if (string.IsNullOrWhiteSpace(userText)) return;

            _executor.BeginTurn();
            try
            {
                if (_settings != null && _messages.Count > 0 && _messages[0].Role == "system") _messages[0].Content = BuildSystemPrompt(_settings);

                // 主动获取当前宿主运行时的图纸/模型上下文（0延迟感知，无需额外 tool_call 探测）
                try
                {
                    // 清理历史中较早轮次注入的旧瞬时宿主上下文，避免跨轮次陈旧状态（如旧图纸名、失效的预选实体）污染模型推理
                    _messages.RemoveAll(m => m != null && m.Role == "system" && m.Content is string s && s.StartsWith("[Active Host Context]", StringComparison.Ordinal));

                    string activeContext = _executor.GetActiveContextSummary();
                    if (!string.IsNullOrWhiteSpace(activeContext))
                    {
                        _messages.Add(new AgentMessage
                        {
                            Role = "system",
                            Content = "[Active Host Context]\n" + activeContext
                        });
                    }
                }
                catch { }

                _englishStatus = _responseLanguage == "en-US" || (_responseLanguage != "zh-CN" && !userText.Any(c => c >= '\u3400' && c <= '\u9fff'));
                _activeToolScopes = ResolveToolScopes(userText, _activeToolScopes);
                _turnTools = SelectToolsForScope(_tools, _activeToolScopes);
                _history.Record(
                    "TOOL SURFACE",
                    "scope=" + string.Join("+", _activeToolScopes) + " · tools=" + _turnTools.Count + "/" + _tools.Count,
                    false);

                _messages.Add(new AgentMessage { Role = "user", Content = userText });
                SaveHistory();

            var executedToolPlans = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long executedToolCalls = 0;
            int replanAttempts = 0;
            int continuationSegment = 1;
            bool finalAnswerOnly = false;

            for (long round = 1; round <= _hardMaxAgentCalls; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (round > 1 &&
                    (round - 1) % _effectiveMaxAgentCalls == 0 &&
                    !finalAnswerOnly)
                {
                    continuationSegment++;
                    ForceCompactForContinuation(continuationSegment);
                    _messages.Add(new AgentMessage
                    {
                        Role = "system",
                        Content =
                            "[Long-task continuation] Continue the same user task from the preserved verified state. " +
                            "Do not repeat completed reads or mutations. Prefer one high-level batch operation over many per-file/per-item calls. " +
                            "Use the compact task state and recent tool results to execute only remaining work."
                    });
                    SaveHistory();
                }

                if (round == _hardMaxAgentCalls && !finalAnswerOnly)
                {
                    finalAnswerOnly = true;
                    _messages.Add(new AgentMessage
                    {
                        Role = "system",
                        Content = "Hard agent safety budget reached after bounded continuation. Do not call tools. Give the user a concise final status: what was completed, what remains, and the safest next action."
                    });
                    SaveHistory();
                }

                CompactIfNeeded();
                ui.BeginPhase();
                // 压缩/分段续跑的提示在 BeginPhase 前生成，占位此刻才存在；
                // 显示后立即清空，避免把上一轮的旧状态带到下一轮。
                if (!string.IsNullOrEmpty(_lastActivityHint))
                {
                    ui.SetActivityHint(_lastActivityHint);
                    _lastActivityHint = null;
                }
                // ConfigureAwait(false) 是刻意设计，不要移除：
                // 1) 聊天 UI 的控件调用内部自带 Dispatcher marshal，专为后台线程设计；
                // 2) 若把延续留在 AutoCAD 主 STA 线程，ExecuteToolCalls 里的同步
                //    stdio 调用（McpProcessClient.CallTool）会阻塞主线程，而 MCP exe
                //    又要通过 COM 回调主线程 → 互等死锁，AutoCAD 整体无响应；
                // 3) 闪退的真正防线在 AutoCADToolExecutor：进程内 COM 工具被
                //    Dispatcher.Invoke marshal 回主线程执行（见该文件注释）。
                AgentCompletion completion = await CompleteAsync(ui, finalAnswerOnly, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                ui.Usage("assistant", completion.Usage);
                _history.RecordUsage("assistant", completion.Usage);
                if (!string.IsNullOrWhiteSpace(completion.ReasoningContent))
                    _history.Record("ASSISTANT REASONING · provider-returned", completion.ReasoningContent, true);

                // 只发工具调用、没有正文的回合，在关闭思考时也要给用户一句可见的状态。
                if (completion.ToolCalls.Count > 0 && !finalAnswerOnly && string.IsNullOrWhiteSpace(completion.Content))
                {
                    completion.Content = _englishStatus
                        ? "Executing " + completion.ToolCalls.Count + " tool operation(s)."
                        : "正在执行 " + completion.ToolCalls.Count + " 项工具操作。";
                    ui.AppendContent(completion.Content);
                }
                _messages.Add(new AgentMessage
                {
                    Role = "assistant",
                    Content = completion.Content ?? string.Empty,
                    ToolCalls = completion.RawToolCalls
                });
                SaveHistory();

                if (completion.ToolCalls.Count == 0)
                {
                    if (!finalAnswerOnly && LooksLikeUninvokedToolIntent(completion.Content) && replanAttempts < MaxReplanAttemptsPerTurn)
                    {
                        replanAttempts++;
                        _messages.Add(new AgentMessage
                        {
                            Role = "system",
                            Content = "[Action Required] You announced tool execution in text but did not emit the actual tool_call. Please immediately invoke the tool function directly to perform the operation."
                        });
                        SaveHistory();
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(completion.Content))
                        await EnsureFinalAnswerAsync(ui, executedToolCalls, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (finalAnswerOnly)
                {
                    ResolveUnexecutedToolCalls(
                        completion,
                        "Tool execution is disabled during the final summary call.");
                    if (string.IsNullOrWhiteSpace(completion.Content))
                        await EnsureFinalAnswerAsync(ui, executedToolCalls, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // 只读调用（query/get/list/info/…）的重复执行不算"无进展"：
                // 大模型常在拿到 ok=true 的写入后再查一次确认结果落盘，
                // 这种校验性重复不该被 no-progress 守卫拦死。
                // 只要本轮计划里混着一个写操作，就仍按原有守卫计数。
                bool allReadOnlyPlan = true;
                foreach (AgentToolCall call in completion.ToolCalls)
                {
                    if (AgentToolResultAnalyzer.IsLikelyMutation(call.Name))
                    {
                        allReadOnlyPlan = false;
                        break;
                    }
                }

                string modelSignature = NormalizedToolSignature(completion);
                executedToolPlans.TryGetValue(modelSignature, out int priorExecutions);
                if (!allReadOnlyPlan && priorExecutions >= MaxIdenticalToolPlanExecutions)
                {
                    ResolveUnexecutedToolCalls(
                        completion,
                        "Skipped because the same tool plan was already executed in this turn.");

                    if (replanAttempts < MaxReplanAttemptsPerTurn)
                    {
                        replanAttempts++;
                        _messages.Add(new AgentMessage
                        {
                            Role = "system",
                            Content =
                                "[No-progress guard] The exact same tool plan has already run. Do not repeat it. " +
                                "Re-read only the minimum fact that can change the decision, then choose a different strategy or a higher-level batch tool. " +
                                "If the previous result already proves success, stop and answer. If no alternative can make progress, report the blocker."
                        });
                        SaveHistory();
                        continue;
                    }

                    finalAnswerOnly = true;
                    _messages.Add(new AgentMessage
                    {
                        Role = "system",
                        Content = "Repeated no-progress plans exceeded the bounded replan allowance. Summarize verified state and the blocking issue; do not call more tools."
                    });
                    SaveHistory();
                    continue;
                }
                executedToolPlans[modelSignature] = priorExecutions + 1;

                long remainingToolCalls = _maxToolCallsPerTurn - executedToolCalls;
                if (remainingToolCalls <= 0 || completion.ToolCalls.Count > remainingToolCalls)
                {
                    ResolveUnexecutedToolCalls(
                        completion,
                        "Skipped because the per-turn tool-call safety budget was reached.");
                    finalAnswerOnly = true;
                    _messages.Add(new AgentMessage
                    {
                        Role = "system",
                        Content = "Tool-call safety budget reached. Summarize the preserved " + ChatCoreHostConfig.HostProductNameOrDefault() + " state and remaining work; do not call more tools."
                    });
                    SaveHistory();
                    continue;
                }

                ToolExecutionSummary execution = ExecuteToolCalls(completion, ui, cancellationToken);
                executedToolCalls += completion.ToolCalls.Count;

                // 模型主动调用截图工具产出的图：**必须**真的喂给视觉模型。
                // 只把 PNG 路径写进工具结果，模型是看不见的——这正是"工具说截图成功、
                // 但模型/用户都看不到图"那个缺陷的根因。放在 Mutated 判断之前：
                // 截图本身不是变更，却最需要被看见。
                if (execution.ToolImagePaths.Count > 0)
                    await AppendToolImageFactsAsync(ui, execution.ToolImagePaths, cancellationToken).ConfigureAwait(false);

                if (!execution.Mutated) continue;

                if (execution.ExternalCheckpointNeeded)
                    AppendExternalCheckpoint(ui, cancellationToken);

                // 自动补截图：仅在宿主显式开启 AutoVisualReviewAfterMutation 且模型本轮未截过图时触发，
                // 避免每次正常建模/修改后都额外阻塞一轮截屏与视觉大模型复核。
                if (ChatCoreHostConfig.Current.AutoVisualReviewAfterMutation &&
                    execution.ExternalVisualMutated &&
                    !execution.VisualReviewAfterMutation &&
                    execution.ImagesCaptured == 0 &&
                    !_activeToolScopes.Contains("metadata", StringComparer.OrdinalIgnoreCase))
                    await AppendExternalVisualVerificationAsync(ui, cancellationToken).ConfigureAwait(false);

                if (execution.DrawingMutated)
                {
                    _messages.Add(new AgentMessage
                    {
                        Role = "system",
                        Content = "[Mutation boundary] The requested drawing mutation completed. Do not run automatic drawing review, layout, dimension cleanup, annotation repair, or any unrelated mutation. Continue only with read-only verification that directly proves the user's requested fields changed."
                    });
                    SaveHistory();
                }
            }

            _history.Record(
                "AGENT BUDGET",
                "Agent call budget ended without a normal final response. " + ChatCoreHostConfig.HostProductNameOrDefault() + " state was preserved.",
                false);
            BudgetExhausted = true;
            }
            finally
            {
                _executor.EndTurn();
            }
        }

        private async Task EnsureFinalAnswerAsync(IAgentUi ui, long executedToolCalls, CancellationToken cancellationToken)
        {
            _messages.Add(new AgentMessage
            {
                Role = "system",
                Content = "The previous response contained no user-facing answer. Return a concise non-empty plain-language summary based only on the recorded tool results. State failures or incomplete work accurately. Do not call tools or output private reasoning."
            });
            ui.BeginPhase();
            AgentCompletion summary = await CompleteAsync(ui, true, cancellationToken).ConfigureAwait(false);
            ui.Usage("assistant", summary.Usage);
            _history.RecordUsage("assistant", summary.Usage);
            _messages.Add(new AgentMessage { Role = "assistant", Content = summary.Content ?? string.Empty, ToolCalls = summary.RawToolCalls });
            ResolveUnexecutedToolCalls(summary, "Tool execution is disabled during final-answer recovery.");
            if (string.IsNullOrWhiteSpace(summary.Content))
            {
                string fallback = _englishStatus
                    ? "The model returned no text summary. Tool calls executed: " + executedToolCalls + ". Expand the tool results to check their outcomes; task completion is not confirmed."
                    : "模型未返回文字总结。本轮已执行 " + executedToolCalls + " 次工具调用，可展开工具结果查看具体状态；暂不能确认任务已全部完成。";
                ui.AppendContent(fallback);
                _messages.Add(new AgentMessage { Role = "assistant", Content = fallback });
            }
            SaveHistory();
        }

        private void ResolveUnexecutedToolCalls(AgentCompletion completion, string reason)
        {
            if (completion == null || completion.ToolCalls.Count == 0) return;
            foreach (AgentToolCall call in completion.ToolCalls)
            {
                string callId = string.IsNullOrWhiteSpace(call.Id) ? NextSyntheticId() : call.Id;
                _messages.Add(new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    Name = call.Name,
                    Content = "{\"ok\":false,\"skipped\":true,\"reason\":" + Quote(reason) + "}"
                });
            }
            SaveHistory();
        }

        /// <summary>
        /// 同工具+同参数+同错误的失败计数（跨本轮全部 turn）。历史教训：
        /// revolve 用同一参数连错 6 次框架不闻不问，模型把整个预算烧在原地。
        /// 第 2 次起注入 no-progress 提示，强制换策略。
        /// </summary>
        private readonly Dictionary<string, int> _identicalFailureCounts = new(StringComparer.Ordinal);

        private ToolExecutionSummary ExecuteToolCalls(AgentCompletion completion, IAgentUi ui, CancellationToken cancellationToken)
        {
            var summary = new ToolExecutionSummary();
            foreach (AgentToolCall call in completion.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string callId = string.IsNullOrWhiteSpace(call.Id) ? NextSyntheticId() : call.Id;
                string cleanArgs = SanitizeToolArgumentsJson(call.ArgumentsJson);
                ui.Tool(ToolActivity.Call(callId, call.Name, cleanArgs));
                _history.Record("TOOL CALL · " + call.Name, cleanArgs, true);

                string resultJson;
                bool failed = false;
                try
                {
                    McpExecutionResult result = _executor.Execute(call.Name, cleanArgs, cancellationToken);
                    resultJson = result?.Json ?? "{}";
                    failed = AgentToolResultAnalyzer.IsFailure(resultJson);

                    bool routed = _executor.IsRoutedTool(call.Name);

                    if (routed)
                    {
                        // 进程内直连（图纸类）：变更标记由执行结果携带，比按名字猜可靠。
                        if (result != null && result.MutatesDrawing)
                        {
                            summary.Mutated = true;
                            if (NeedsDrawingReview(call.Name)) summary.DrawingMutated = true;
                        }
                    }
                    else if (!failed)
                    {
                        if (AgentToolResultAnalyzer.IsLikelyMutation(call.Name))
                        {
                            summary.Mutated = true;
                            // 状态检查点只对外部 MCP 工具登记：进程内工具的变更由结果自证，
                            // 再回读一遍宿主状态是白跑（CAD 侧更是根本没这个概念）。
                            summary.ExternalCheckpointNeeded |= AgentToolResultAnalyzer.RequiresStateCheckpoint(call.Name);
                        }
                    }

                    // ↓↓↓ 以下三项与"工具走进程内还是外部"无关，必须在两个分支之外判定。
                    //
                    // 历史缺陷：它们原先只写在 else（外部工具）分支里，于是 **IsRoutedTool=true 的
                    // 进程内工具永远拿不到**——后果是 CAD 宿主的自动视觉校验从未触发过：
                    // 截图工具名配好了、VisualMutationToolPrefixes 也配好了，但那个分支根本进不来。
                    // 同理 "本轮已截过图" 的计数也永远为 0，框架会重复自动补截图。
                    if (!failed)
                    {
                        bool mutated = routed
                            ? (result != null && result.MutatesDrawing)
                            : AgentToolResultAnalyzer.IsLikelyMutation(call.Name);

                        // 工具产出的图片：优先取执行结果显式声明的，没有就从 JSON 里按
                        // "path 指向图片文件"提取（两种宿主的截图工具形状都覆盖）。
                        foreach (string imagePath in ResolveToolImagePaths(result, resultJson))
                        {
                            if (!summary.ToolImagePaths.Contains(imagePath, StringComparer.OrdinalIgnoreCase))
                                summary.ToolImagePaths.Add(imagePath);
                        }

                        // "复核完成"标记工具名由宿主配置提供：调用过它就不再自动补截图。
                        string reviewTool = ChatCoreHostConfig.VisualReviewToolNameOrDefault();
                        if (!string.IsNullOrWhiteSpace(reviewTool)
                            && call.Name.Equals(reviewTool, StringComparison.OrdinalIgnoreCase)
                            && summary.ExternalVisualMutated)
                            summary.VisualReviewAfterMutation = true;

                        // 截图工具名由宿主配置提供（见 AgentSession.Evidence.VisualCaptureToolName）：
                        // CAD 宿主是 cad_view_capture_screen，写死 cad_view_capture_screen 会让
                        // "本轮已截过图"的判断永远不成立。
                        if (call.Name.Equals(VisualCaptureToolName, StringComparison.OrdinalIgnoreCase))
                            summary.ImagesCaptured++;

                        if (mutated && AgentToolResultAnalyzer.IsVisualMutation(call.Name))
                        {
                            summary.ExternalVisualMutated = true;
                            summary.VisualReviewAfterMutation = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    failed = true;
                    // MCP 服务端把真实原因写在 stderr 上；不带回来时，
                    // 失败的调用在对话里只剩一句没用的概述。
                    string serverLog = _executor.RecentServerLog();
                    resultJson = BuildToolErrorJson(call.Name, ex, serverLog);
                    _history.Record(
                        "TOOL ERROR · " + call.Name,
                        ex + (string.IsNullOrWhiteSpace(serverLog) ? string.Empty : "\n--- MCP stderr ---\n" + serverLog),
                        true);
                }

                ui.Tool(ToolActivity.Result(callId, call.Name, resultJson, failed));
                // 结果里的落盘图片缩略图由工具卡片内嵌展示（ToolCard.Complete），
                // 这里不再铺独立 Preview 块。
                _history.Record((failed ? "TOOL ERROR RESULT · " : "TOOL RESULT · ") + call.Name, resultJson, true);
                _messages.Add(new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    Name = call.Name,
                    Content = AgentContextCompactor.CompactToolResult(resultJson, CurrentToolResultCharacters)
                });

                if (failed)
                {
                    // 同参同错熔断：签名 = 工具名 + 清洗后参数 + 错误摘要。
                    // 第 2 次起在上下文里显式禁止原样重发，并给替代路径。
                    string failureSignature = call.Name + "|" + cleanArgs + "|"
                        + AgentContextCompactor.CompactToolResult(resultJson, 240);
                    _identicalFailureCounts.TryGetValue(failureSignature, out int priorFailures);
                    _identicalFailureCounts[failureSignature] = priorFailures + 1;
                    if (priorFailures + 1 >= 2)
                    {
                        _messages.Add(new AgentMessage
                        {
                            Role = "system",
                            Content = "[No-progress guard] '" + call.Name + "' has now failed " + (priorFailures + 1)
                                + " times with identical arguments and the same error. Never resend this exact call - "
                                + "the result will be identical. Change the approach: different parameters, a "
                                + "higher-level batch tool, or read-only fact-finding to locate the real cause. "
                                + "If no alternative can make progress, report the blocker to the user."
                        });
                    }
                }
                SaveHistory();
            }
            return summary;
        }

    }
}
