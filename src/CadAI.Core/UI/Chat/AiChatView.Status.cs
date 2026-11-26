using System;
using System.Windows;
using System.Threading;
using System.Windows.Controls;

namespace CadAI.Core
{
    /// <summary>
    /// AiChatView 的状态部分：中文文案、状态栏、忙碌开关、Token/耗时指标刷新。
    /// </summary>
    public partial class AiChatView
    {
        private string L(string key)
        {
            switch (key)
            {
                case "Send": return UiLanguage.T("发送", "Send");
                case "History": return UiLanguage.T("历史", "History");
                case "Settings": return UiLanguage.T("设置", "Settings");
                case "SettingsTooltip": return UiLanguage.T("模型地址、密钥、模型名称与运行参数。", "Model endpoint, API key, model name, and runtime parameters.");
                case "SettingsBusy": return UiLanguage.T("请等待当前 AI 任务完成后再打开设置。", "Please wait for the active AI task to finish before opening Settings.");
                case "Remove": return UiLanguage.T("移除", "Remove");
                case "Stop": return UiLanguage.T("停止", "Stop");
                case "StopTooltip": return UiLanguage.T(
                    "终止当前 AI 任务（保留已完成的 " + ChatCoreHostConfig.HostProductNameOrDefault() + " 状态）",
                    "Stop the current AI task (keeps committed " + ChatCoreHostConfig.HostProductNameOrDefault() + " changes)");
                case "HistoryTitle": return UiLanguage.T("历史对话", "Chat History");
                case "Folder": return UiLanguage.T("目录", "Folder");
                case "Idle": return UiLanguage.T("空闲", "Idle");
                case "Connecting": return UiLanguage.T("连接中", "Connecting");
                case "Busy": return UiLanguage.T("执行中", "Running");
                case "Done": return UiLanguage.T("完成", "Done");
                case "Stopped": return UiLanguage.T("已停止", "Stopped");
                case "Failed": return UiLanguage.T("失败", "Failed");
                case "Stopping": return UiLanguage.T("正在停止", "Stopping");
                case "System": return UiLanguage.T("系统", "System");
                case "You": return UiLanguage.T("你", "You");
                case "Error": return UiLanguage.T("错误", "Error");
                case "Thinking": return UiLanguage.T("思考", "Reasoning");
                case "Generating": return UiLanguage.T("正在生成…", "Generating...");
                case "RunningTool": return UiLanguage.T("正在执行工具…", "Running tool...");
                case "Input": return UiLanguage.T("输入", "Input");
                case "Tool": return UiLanguage.T("工具", "Tool");
                case "Preview": return UiLanguage.T("预览", "Preview");
                case "WaitForTask": return UiLanguage.T("请等待当前 AI 任务完成后再修改配置。", "Please wait for the active AI task to finish before changing settings.");
                case "SettingsUpdated": return UiLanguage.T("AI 配置已更新，从下一条消息生效。", "Settings updated and will apply to the next message.");
                case "WelcomeTitle": return UiLanguage.T("使用指南", "Quick Guide");
                case "Welcome": return ResolveWelcomeText();
                case "TaskStopped": return UiLanguage.T("当前 AI 任务已结束。", "The active AI task has stopped.");
                case "Elapsed": return UiLanguage.T("耗时", "Elapsed");
                case "Prompt": return UiLanguage.T("输入", "Prompt");
                case "Completion": return UiLanguage.T("输出", "Completion");
                case "Total": return UiLanguage.T("合计", "Total");
                default: return key;
            }
        }

        /// <summary>
        /// 对话窗口底部的版权信息。与界面语言无关，且按要求只在这里出现一处。
        /// 常量唯一来源是 Branding（Phase 1 从 AboutWindow 上移），聊天窗口直接复用，避免两处各写一份导致年份不一致。
        /// </summary>
        private const string CopyrightNotice = Branding.CopyrightNotice;

        /// <summary>
        /// 欢迎卡正文：英文模式下返回英文版引导，中文模式下优先使用宿主注入文案或内置中文引导。
        /// </summary>
        private static string ResolveWelcomeText()
        {
            if (UiLanguage.IsEnglish)
            {
                return string.Join("\n", new[]
                {
                    "**Welcome to CAD AI Chat.** Inspect, edit, align, annotate, and plot the active AutoCAD drawing using natural language.",
                    "",
                    "**Example prompts**:",
                    "- **Drawing Audit**: `Inspect this drawing for issues and count entities per layer`",
                    "- **Font Repair**: `Find missing SHX/TrueType fonts and replace them with available fonts`",
                    "- **Batch Text Replace**: `Replace all \"DN100\" with \"DN150\" while keeping text height unchanged`",
                    "- **Dimensioning**: `Add a horizontal dimension between (0,0) and (1200,0), 10mm below`",
                    "- **Export PDF**: `Export Layout1 to a high-resolution PDF and save the drawing`",
                    "",
                    "**Configuration & Safety**: Click the **Settings** icon on the left rail (or run `CADCHATCFG`) to configure your model endpoint. Every drawing change runs inside an AutoCAD undo group — press `Ctrl+Z` once to revert."
                });
            }

            try
            {
                string custom = ChatCoreHostConfig.Current.WelcomeText;
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }
            catch { }

            return string.Join("\n", new[]
            {
                "**欢迎使用 CAD 智能对话**，你可以直接通过自然语言对当前 AutoCAD 图纸进行查图、编辑、排版、标注与出图。",
                "",
                "**常用示例**：",
                "- **图纸体检**：`检查当前图纸存在哪些问题，并统计各图层实体数量`",
                "- **字体修复**：`排查当前图纸缺失的字体并自动替换为可用字体`",
                "- **文字批量修改**：`将图中的 \"DN100\" 批量替换为 \"DN150\"，保持字高不变`",
                "- **尺寸标注**：`在 (0,0) 到 (1200,0) 之间添加水平标注，标注线位于下方 10mm`",
                "- **图纸导出**：`将 Layout1 导出为高清 PDF 并保存当前图纸`",
                "",
                "**配置与安全**：点击左侧 **设置** 图标（或命令 `CADCHATCFG`）配置模型服务；所有修改均支持在 AutoCAD 中按 `Ctrl+Z` 一键还原。"
            });
        }

        /// <summary>按当前界面语言刷新静态文案与 Material Symbols 纯图标按钮。</summary>
        private void ApplyStaticTexts()
        {
            if (BrandBadge != null) BrandBadge.ToolTip = UiLanguage.T("CAD 智能对话 (CADAI)", "CAD AI Chat (CADAI)");
            if (HeaderTitleText != null) HeaderTitleText.Text = UiLanguage.T("CAD 智能对话", "CAD AI Chat");
            if (CopyrightText != null)
            {
                CopyrightText.Text = CopyrightNotice;
                CopyrightText.ToolTip = UiLanguage.T("打开 GitHub 仓库", "Open GitHub Repository");
            }
            if (BackToBottomButton != null)
                BackToBottomButton.ToolTip = UiLanguage.T("回到最新消息并恢复自动跟随", "Jump to latest message and resume auto-scroll");
            if (ModelPickerButton != null)
                ModelPickerButton.ToolTip = UiLanguage.T("切换当前对话模型与思考等级", "Switch active model and reasoning effort");
            if (ModelPickerTitleText != null)
                ModelPickerTitleText.Text = UiLanguage.T("选择模型", "Select Model");
            if (ModelPickerConfigButton != null)
                ModelPickerConfigButton.Content = UiLanguage.T("配置…", "Configure...");
            if (ReasoningLevelTitleText != null)
                ReasoningLevelTitleText.Text = UiLanguage.T("思考等级", "Reasoning Effort");

            SetButtonIcon(NewChatButton, MaterialSymbols.Add, UiLanguage.T("新建对话", "New Chat"), 17);
            SetButtonIcon(HistoryButton, MaterialSymbols.History, UiLanguage.T("历史对话", "Chat History"), 17);
            SetButtonIcon(SettingsButton, MaterialSymbols.Settings, UiLanguage.T("AI 设置", "AI Settings"), 17);
            SetButtonIcon(AttachmentButton, MaterialSymbols.AttachFile, UiLanguage.T("添加附件（图片、PDF 或图纸文件）", "Attach files (images, PDF, or drawings)"), 17);
            SetButtonIcon(VoiceButton, MaterialSymbols.Mic, UiLanguage.T("语音输入：点击开始说话，再次点击停止", "Voice input: click to start recording, click again to stop"), 17);
            RefreshReasoningChoices();

            SetButtonIcon(RemoveAttachmentButton, MaterialSymbols.Close, UiLanguage.T("移除全部附件", "Remove all attachments"), 16);
            SetButtonIcon(StopButton, MaterialSymbols.Stop, L("StopTooltip"), 17);
            SetButtonIcon(SendButton, MaterialSymbols.Send, UiLanguage.T("发送消息", "Send message"), 16);
            SetButtonIconText(ResumeQueueButton, MaterialSymbols.PlayArrow, UiLanguage.T("继续执行", "Resume"), UiLanguage.T("继续执行待发送队列", "Resume queued messages"));
            SetButtonIconText(ClearQueueButton, MaterialSymbols.Delete, UiLanguage.T("清空", "Clear"), UiLanguage.T("清空所有排队消息", "Clear all queued messages"));
            if (HistoryTitleTextBlock != null) HistoryTitleTextBlock.Text = L("HistoryTitle");
            SetButtonIconText(HistoryFolderButton, MaterialSymbols.FolderOpen, L("Folder"), UiLanguage.T("打开历史对话目录", "Open chat history folder"));
        }

        private void SetStatus(StatusKind kind, string text = null)
        {
            if (StatusTextBlock == null) return;
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(text) ? StatusKindText(kind) : text;
            if (StatusBadge != null) StatusBadge.Style = UiStyle("StatusBadge." + kind);
        }

        private string StatusKindText(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Connecting: return L("Connecting");
                case StatusKind.Busy: return L("Busy");
                case StatusKind.Done: return L("Done");
                case StatusKind.Stopped: return L("Stopped");
                case StatusKind.Failed: return L("Failed");
                default: return L("Idle");
            }
        }

        private void SetBusy(bool busy, string busyStatus = null)
        {
            if (SendButton != null)
            {
                SendButton.IsEnabled = true;
                SendButton.Visibility = Visibility.Visible;
                if (busy)
                {
                    SetButtonIcon(SendButton, MaterialSymbols.PlaylistAdd, "加入待发送队列", 17);
                    SendButton.Style = (Style)FindResource("Button.Icon.Tonal");
                }
                else
                {
                    SetButtonIcon(SendButton, MaterialSymbols.Send, "发送消息", 16);
                    SendButton.Style = (Style)FindResource("Button.Icon.Primary");
                }
            }
            if (StopButton != null)
            {
                StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                StopButton.IsEnabled = busy;
            }
            NewChatButton.IsEnabled = !busy;
            AttachmentButton.IsEnabled = true;
            ChatReasoningComboBox.IsEnabled = !busy;
            InputBox.IsEnabled = true;
            if (busy) SetStatus(StatusKind.Busy, busyStatus);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _queuePaused = true;
            RefreshQueueUi();
            CancelRunningTask("停止按钮");
        }

        /// <summary>
        /// 中断当前任务。与"停止"按钮走同一套逻辑，供宿主侧调用
        /// （如宿主窗口/面板关闭前中断正在执行的轮次）。
        /// </summary>
        public void CancelRunningTask(string reason)
        {
            CancellationTokenSource cts = _turnCancellation;
            if (cts == null) return;
            _suppressTurnMetrics = true;
            RefreshMetrics();
            try { cts.Cancel(); } catch { }
            SetStatus(StatusKind.Stopped, L("Stopping"));
            try { AddDiagnostic("CANCEL", reason ?? string.Empty, false); } catch { }
        }

        private void AppendUsage(string stage, AgentUsage usage)
        {
            if (usage == null) return;
            InvokeUi(() =>
            {
                _promptTokens += usage.PromptTokens;
                _completionTokens += usage.CompletionTokens;
                _totalTokens += usage.TotalTokens;
                _turnHasEstimatedUsage |= usage.IsEstimated;
                RefreshMetrics();
            });
        }

        /// <summary>把本回合的耗时写到助手回复末尾的指标行上。不显示 Token，全过程仅保留一个总耗时。</summary>
        private void RefreshMetrics()
        {
            TextBlock target = _turnMetricsText;
            if (target == null) return;
            if (_suppressTurnMetrics)
            {
                target.Visibility = Visibility.Collapsed;
                target.Text = string.Empty;
                return;
            }

            EnsureSingleMetricsVisible(target);

            bool running = _turnStopwatch.IsRunning;
            TimeSpan duration = running ? _turnStopwatch.Elapsed : _lastTurnDuration;

            target.Visibility = Visibility.Visible;
            target.Text = L("Elapsed") + " " + FormatHeaderDuration(duration);
            target.ToolTip = L("Elapsed") + " " + FormatDuration(duration);
        }

        private void EnsureSingleMetricsVisible(TextBlock current)
        {
            if (_assistantGroup == null) return;
            foreach (UIElement child in _assistantGroup.Children)
            {
                if (child is StackPanel sp)
                {
                    foreach (UIElement inner in sp.Children)
                    {
                        if (inner is TextBlock tb && !ReferenceEquals(tb, current) && tb.Visibility == Visibility.Visible)
                        {
                            string text = tb.Text ?? string.Empty;
                            if (text.StartsWith(L("Elapsed")) || text.StartsWith("耗时") || text.StartsWith("Elapsed"))
                            {
                                tb.Visibility = Visibility.Collapsed;
                                tb.Text = string.Empty;
                            }
                        }
                    }
                }
            }
        }

        private void StartTurnMetrics()
        {
            _suppressTurnMetrics = false;
            _turnPromptBaseline = _promptTokens;
            _turnCompletionBaseline = _completionTokens;
            _turnTokenBaseline = _totalTokens;
            _turnHasEstimatedUsage = false;
            _turnStopwatch.Restart();
            _turnMetricsTimer.Start();
            RefreshMetrics();
        }

        private void StopTurnMetrics()
        {
            _turnMetricsTimer.Stop();
            _turnStopwatch.Stop();
            _lastTurnDuration = _turnStopwatch.Elapsed;
            _lastTurnPromptTokens = Math.Max(0, _promptTokens - _turnPromptBaseline);
            _lastTurnCompletionTokens = Math.Max(0, _completionTokens - _turnCompletionBaseline);
            _lastTurnTokens = Math.Max(0, _totalTokens - _turnTokenBaseline);
            _lastTurnHasEstimatedUsage = _turnHasEstimatedUsage;
            RefreshMetrics();
        }

        private void TurnMetricsTimer_Tick(object sender, EventArgs e) => RefreshMetrics();

        internal static string FormatHeaderDuration(TimeSpan d) => d.TotalMinutes >= 1 ? ((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00") : d.TotalSeconds.ToString("0.0") + "s";
        internal static string FormatDuration(TimeSpan d) => d.TotalMinutes >= 1 ? d.TotalMinutes.ToString("0.0") + "m" : d.TotalSeconds.ToString("0.0") + "s";
        internal static string FormatTokenCount(int v) => v >= 1000000 ? (v / 1000000d).ToString("0.00") + "M" : v >= 1000 ? (v / 1000d).ToString("0.0") + "K" : v.ToString("N0");
    }
}
