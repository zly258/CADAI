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
        private int ResolveMaxRepetitionRecoveryAttempts()
        {
            if (_settings == null) return 2;
            if (!_settings.AutoRetryOnReasoningLoop) return 0;
            return Math.Max(0, Math.Min(5, _settings.MaxRepetitionRecoveryAttempts));
        }

        private async Task<AgentCompletion> CompleteAsync(IAgentUi ui, bool finalAnswerOnly, CancellationToken cancellationToken)
        {
            IReadOnlyList<object> tools = finalAnswerOnly ? null : (_turnTools ?? _tools);
            var callbacks = new StreamingCallbacks
            {
                OnContentDelta = ui.AppendContent,
                OnReasoningDelta = delta => { if (_settings?.ReasoningEnabled == true) ui.AppendReasoning(delta); },
                OnStreamReset = () => ui.ResetPhase(string.Empty),
                OnRetry = (attempt, message) =>
                {
                    // 重试期间 UI 只有转圈没有文字，用户无从判断"是在重试还是死了"。
                    // 把尝试次数与精简原因写到等待占位区（一行提示，不产生消息记录）。
                    string reason = AiErrorFormatter.ToRetrySummary(message, _settings?.BaseUrl);
                    string prefix = attempt > 0
                        ? "连接中断，正在自动重试（第 " + attempt + " 次）："
                        : "正在切换备用模型继续：";
                    ui.SetActivityHint(prefix + reason);
                }
            };

            int keepRecent = AgentContextCompactor.DefaultKeepRecentMessages;
            int contextRecovery = 0;
            int repetitionRecovery = 0;
            AgentMessage repetitionGuidance = null;
            // 整个"请求 + 重试 + 上下文压缩恢复"过程都算一次等待：用户看到的应该是一条
            // 持续走秒的行，而不是几百秒的纯转圈（历史问题：300s 首字节超时 + 重试
            // 期间界面没有任何"还在动"的凭据）。
            TimeSpan requestTimeout = TimeSpan.FromSeconds(
                _settings != null && _settings.RequestTimeoutSeconds > 0
                    ? _settings.RequestTimeoutSeconds
                    : AiSettings.DefaultRequestTimeoutSeconds);
            ui.BeginWait("等待模型响应", requestTimeout);
            try
            {
                while (true)
                {
                    IReadOnlyList<AgentMessage> requestMessages = _messages;
                    if (repetitionGuidance != null)
                    {
                        var augmented = new List<AgentMessage>(_messages.Count + 1);
                        augmented.AddRange(_messages);
                        augmented.Add(repetitionGuidance);
                        requestMessages = augmented;
                    }

                    try
                    {
                        return await _client.CompleteStreamingAsync(requestMessages, tools, callbacks, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AiRepetitionException ex) when (!cancellationToken.IsCancellationRequested && repetitionRecovery < ResolveMaxRepetitionRecoveryAttempts())
                    {
                        int maxRetries = ResolveMaxRepetitionRecoveryAttempts();
                        // 思考/输出陷入死循环：流式端已自动中断，这里自动清理残缺片段、注入临时纠偏引导并重试继续。
                        repetitionRecovery++;
                        _history.Record(
                            "REPETITION RECOVERY",
                            $"attempt={repetitionRecovery}/{maxRetries}; reason={ex.Message}",
                            false);
                        ui.ResetPhase(string.Empty);
                        ui.SetActivityHint(
                            $"检测到模型思考陷入循环，已自动中断，正在自动重试继续（第 {repetitionRecovery}/{maxRetries} 次）…");
                        repetitionGuidance = new AgentMessage
                        {
                            Role = "system",
                            Content = repetitionRecovery == 1
                                ? "[Reasoning loop recovery] Your previous response got stuck in a repetitive thinking loop and was automatically interrupted. Switch to a direct, concise approach and continue the task immediately. Do not repeat earlier thoughts; directly emit the next needed tool call or concise final answer."
                                : "[Reasoning loop recovery - final retry] Stop extended deliberation immediately. Take the single most direct next tool call or provide a concise summary of verified progress."
                        };
                        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        !cancellationToken.IsCancellationRequested &&
                        _autoCompactContext &&
                        AgentContextCompactor.IsContextLimitError(ex))
                    {
                        contextRecovery++;
                        int currentTokens = AgentContextCompactor.EstimateTokens(_messages);
                        int baseTarget = _contextWindowTokens > 0
                            ? Math.Max(
                                AgentContextCompactor.MinimumTargetTokens,
                                _contextWindowTokens - (_contextReserveTokens * 2))
                            : Math.Max(
                                AgentContextCompactor.MinimumTargetTokens,
                                (int)Math.Ceiling(currentTokens * 0.70));
                        int recoveryTarget = Math.Max(
                            AgentContextCompactor.MinimumTargetTokens,
                            baseTarget - (contextRecovery - 1) * Math.Max(2000, baseTarget / 6));
                        if (contextRecovery > MaxContextRecoveryAttempts ||
                            !AgentContextCompactor.TryCompact(
                                _messages,
                                true,
                                _taskStateSnapshot,
                                keepRecent,
                                AgentContextCompactor.SoftTokenLimit,
                                recoveryTarget,
                                out string report))
                            throw;

                        _history.Record("CONTEXT COMPACTION", report, false);
                        SaveHistory();
                        ui.ResetPhase(string.Empty);
                        ui.SetActivityHint("上下文超出模型上限，已自动压缩（" + report + "），正在重发请求…");
                        keepRecent = Math.Max(AgentContextCompactor.MinimumKeepRecentMessages, keepRecent - 4);
                    }
                }
            }
            finally
            {
                ui.EndWait();
            }
        }

        private void ForceCompactForContinuation(int segment)
        {
            if (!_autoCompactContext) return;

            IReadOnlyList<object> activeTools = _turnTools ?? _tools;
            int toolTokens = AgentContextCompactor.EstimateToolTokens(activeTools);
            int currentTokens = AgentContextCompactor.EstimateTokens(_messages);
            int trigger = Math.Max(
                AgentContextCompactor.MinimumTargetTokens,
                _contextWindowTokens - _contextReserveTokens);

            // 分段续跑只是提醒模型"接着干"，不等于上下文装不下。
            // 只有下一次请求按估算确实超出窗口预留时才压缩；否则保持原样，
            // 避免"每 N 轮续跑"变成"每 N 轮强制压缩"——模型会反复丢掉已验证状态。
            if (currentTokens + toolTokens < trigger) return;

            int target = _contextWindowTokens > 0
                ? Math.Max(
                    AgentContextCompactor.MinimumTargetTokens,
                    _contextWindowTokens - (_contextReserveTokens * 2) - toolTokens)
                : Math.Max(
                    AgentContextCompactor.MinimumTargetTokens,
                    (int)Math.Ceiling(currentTokens * 0.58));

            if (AgentContextCompactor.TryCompact(
                    _messages,
                    true,
                    _taskStateSnapshot,
                    Math.Max(
                        AgentContextCompactor.MinimumKeepRecentMessages,
                        AgentContextCompactor.DefaultKeepRecentMessages - 4),
                    AgentContextCompactor.SoftTokenLimit,
                    target,
                    out string report))
            {
                _history.Record(
                    "LONG TASK CONTINUATION",
                    "segment=" + segment + "; " + report,
                    false);
                SaveHistory();
                _lastActivityHint = "长任务分段继续，已压缩历史（" + report + "）";
            }
        }

        private void CompactIfNeeded()
        {
            if (!_autoCompactContext || _contextWindowTokens <= 0) return;

            IReadOnlyList<object> activeTools = _turnTools ?? _tools;
            int toolTokens = AgentContextCompactor.EstimateToolTokens(activeTools);
            int messageTokens = AgentContextCompactor.EstimateTokens(_messages);
            int estimatedRequestTokens = messageTokens + toolTokens;
            int trigger = Math.Max(
                AgentContextCompactor.MinimumTargetTokens,
                _contextWindowTokens - _contextReserveTokens);

            // 上下文健康时不要提前压缩。只有下一次请求按估算装不下预留的回复空间时才压缩。
            if (estimatedRequestTokens < trigger) return;

            int target = Math.Max(
                AgentContextCompactor.MinimumTargetTokens,
                _contextWindowTokens - (_contextReserveTokens * 2) - toolTokens);

            if (AgentContextCompactor.TryCompact(
                    _messages,
                    false,
                    _taskStateSnapshot,
                    AgentContextCompactor.DefaultKeepRecentMessages,
                    Math.Max(
                        AgentContextCompactor.MinimumTargetTokens,
                        trigger - toolTokens),
                    target,
                    out string report))
            {
                // 压缩在请求间隙同步执行，大历史时耗时数秒且 UI 无任何输出，
                // 用户会误以为卡死。给等待占位区一行提示（下一轮 BeginPhase 会重建占位）。
                _history.Record(
                    "CONTEXT COMPACTION",
                    report + " toolSchemas≈" + toolTokens + " tokens; contextLimit=" + _contextWindowTokens + ".",
                    false);
                SaveHistory();
                _lastActivityHint = "正在压缩上下文…（" + report + "）";
            }
        }

    }
}
