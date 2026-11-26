using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CadAI.Core
{
    /// <summary>
    /// AiChatView 的流式输出部分：助手回复阶段的创建/收尾与增量合帧缓冲。
    /// </summary>
    public partial class AiChatView
    {
        private StackPanel _assistantGroup;

        private void CreateAssistantPhaseCore()
        {
            if (_turnMetricsText != null)
            {
                _turnMetricsText.Visibility = Visibility.Collapsed;
                _turnMetricsText.Text = string.Empty;
                _turnMetricsText = null;
            }
            FinalizeActivePhase();
            if (_assistantGroup == null)
            {
                _assistantGroup = new StackPanel();
                _assistantGroup.Children.Add(CreateRoleLabel("AI", MessageRole.Assistant));
                AddMessageCard(CreateMessageCard(MessageRole.Assistant, _assistantGroup));
            }
            var container = new StackPanel();

            var thinkingText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyStyle(thinkingText, "Text.Code");
            var thinking = new Expander { Header = L("Thinking"), Content = thinkingText, IsExpanded = false, Visibility = Visibility.Collapsed };
            ApplyStyle(thinking, "Expander.Subtle");
            container.Children.Add(thinking);

            // 等待占位：转圈 + 文案。收到首个文本/推理增量后立即隐藏，避免出现空白气泡。
            var waiting = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 3) };
            var spinner = new BusyIndicator(UiBrush("Brush.Text.Secondary"));
            waiting.Children.Add(spinner);
            var waitingText = new TextBlock
            {
                Text = L("Generating"),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplyStyle(waitingText, "Text.Caption");
            waiting.Children.Add(waitingText);
            container.Children.Add(waiting);

            var streamed = CreateBodyText(string.Empty, true);
            container.Children.Add(streamed);

            var metrics = new TextBlock { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            ApplyStyle(metrics, "Text.Caption");
            container.Children.Add(metrics);

            _assistantGroup.Children.Add(container);
            _activePhase = new AssistantPhaseView(container, streamed, thinking, thinkingText, metrics, waiting, spinner, waitingText);
            _turnMetricsText = metrics;
            spinner.Start();
            ScrollToBottom();
        }

        private void DiscardActivePhase(string reason)
        {
            if (_activePhase != null)
            {
                _activePhase.EndWaiting();
                if (ReferenceEquals(_turnMetricsText, _activePhase.Metrics)) _turnMetricsText = null;
                try { _assistantGroup?.Children.Remove(_activePhase.Container); } catch { }
                _activePhase = null;
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                AppendMessage("system", reason, false);
            }
            else
            {
                // 无 reason 的 ResetPhase 来自流式重试：旧阶段已丢弃，但回合仍在进行。
                // 立即重建等待占位，保证转圈与耗时计时器在重试期间不消失；
                // 耗时继续走 StartTurnMetrics 启动的同一个秒表，数值不回跳。
                CreateAssistantPhaseCore();
            }
        }

        /// <summary>
        /// 接收模型的内容增量。运行在后台线程，且调用频率极高，
        /// 因此这里只做一次入缓冲，绝不逐个 token 走 Dispatcher / 重排 Markdown。
        /// </summary>
        private void AppendAssistantText(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            lock (_streamBufferGate) _pendingContent.Append(delta);
            ScheduleStreamFlush();
        }

        /// <summary>接收思考过程的增量，与正文同样走合帧缓冲。</summary>
        private void AppendReasoning(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            lock (_streamBufferGate) _pendingReasoning.Append(delta);
            ScheduleStreamFlush();
        }

        /// <summary>启动合帧定时器（线程安全，可重复调用）。</summary>
        private void ScheduleStreamFlush()
        {
            // DispatcherTimer 只能在 UI 线程启动：从后台线程用 BeginInvoke 升优先级排队，
            // 且只在未启动时启动，避免每次增量都塞一条调度消息。
            if (Dispatcher.CheckAccess())
            {
                if (!_streamFlushTimer.IsEnabled) _streamFlushTimer.Start();
                return;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_streamFlushTimer.IsEnabled) _streamFlushTimer.Start();
            }), DispatcherPriority.Render);
        }

        /// <summary>把缓冲区内容一次性刷到界面。只在 UI 线程执行。</summary>
        private void DrainStreamBuffers()
        {
            string content, reasoning;
            lock (_streamBufferGate)
            {
                if (_pendingContent.Length == 0 && _pendingReasoning.Length == 0)
                {
                    _streamFlushTimer.Stop();
                    return;
                }
                content = _pendingContent.ToString();
                reasoning = _pendingReasoning.ToString();
                _pendingContent.Clear();
                _pendingReasoning.Clear();
            }

            if (_activePhase == null) CreateAssistantPhaseCore();

            if (reasoning.Length > 0)
            {
                _activePhase.BeginReasoning();
                _activePhase.Reasoning.Append(reasoning);
                _activePhase.ThinkingText.Text = _activePhase.Reasoning.ToString();
            }

            if (content.Length > 0)
            {
                _activePhase.EndReasoning();
                _activePhase.EndWaiting();
                _activePhase.Text.Append(content);
                // Markdown 控件自身还有 180ms 的渲染去抖，两处叠加后每帧只重排一次。
                _activePhase.StreamedText.Markdown = _activePhase.Text.ToString();
            }

            ScrollToBottom();
        }

        /// <summary>
        /// 更新等待占位区的一行状态（重试/压缩提示）。Agent 在后台线程调用；
        /// 阶段尚未创建时先记下文字，CreateAssistantPhaseCore 随后建占位时仍显示旧提示没有意义，
        /// 因此无阶段时直接丢弃（BeginPhase 紧随其后会用默认文案重建）。
        ///
        /// 长等待进行中（BeginWait..EndWait）时，hint 只作为等待行的说明文字，
        /// 由 RefreshWaitText 把"已等待/上限"一起渲染，不会被覆盖掉计时。
        /// </summary>
        private void SetActivityHint(string text)
        {
            InvokeUi(() =>
            {
                _activityHintText = string.IsNullOrWhiteSpace(text) ? null : text;
                if (_waitDescription != null)
                {
                    RefreshWaitText(false);
                    return;
                }
                _activePhase?.SetWaitingText(text);
            });
        }

        /// <summary>立即清空缓冲并渲染（回合结束、停止、窗口关闭时调用）。</summary>
        private void FlushStreamBuffersNow()
        {
            try { _streamFlushTimer.Stop(); } catch { }
            DrainStreamBuffers();
        }

        private void FinalizeActivePhase()
        {
            // 先把缓冲区里还没上屏的增量刷出去，否则最后几十毫秒的内容会丢。
            FlushStreamBuffersNow();
            if (_activePhase == null) return;
            AssistantPhaseView phase = _activePhase;
            _activePhase = null;
            phase.EndReasoning();
            phase.EndWaiting();
            phase.StreamedText.Flush();
            string text = phase.Text.ToString(), reasoning = phase.Reasoning.ToString();
            if (!string.IsNullOrWhiteSpace(reasoning)) AddDiagnostic("ASSISTANT REASONING", reasoning, true);
            if (!string.IsNullOrWhiteSpace(text)) AddDiagnostic("ASSISTANT", text, false);
            // 只产生工具调用的回合会留下空卡片：直接移除，避免出现空白气泡。
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(reasoning) && phase.ToolCount == 0)
            {
                try { _assistantGroup?.Children.Remove(phase.Container); } catch { }
                if (ReferenceEquals(_turnMetricsText, phase.Metrics)) _turnMetricsText = null;
                return;
            }

            // 耗时落在本回合回复的末尾，回合结束后仍继续接收最终数值。
            _turnMetricsText = phase.Metrics;
            RefreshMetrics();
        }
    }
}
