using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CadAI.Core
{
    /// <summary>
    /// 对话视图主体。按关注点拆分为多个 partial：
    /// 本文件只保留构造、字段与生命周期入口；
    /// 状态/文案/耗时见 AiChatView.Status.cs，消息卡片与滚动见 AiChatView.Rendering.cs，
    /// 流式合帧见 AiChatView.Streaming.cs，嵌套类型各自独立成文件。
    /// </summary>
    public partial class AiChatView : UserControl, IAgentUi
    {
        internal enum StatusKind { Idle, Connecting, Busy, Done, Stopped, Failed }
        internal enum MessageRole { System, User, Assistant, Error, Welcome }

        private readonly List<DiagnosticEntry> _diagnostics = new List<DiagnosticEntry>();
        private readonly Dictionary<string, ToolCard> _toolCards = new Dictionary<string, ToolCard>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _turnStopwatch = new Stopwatch();
        private readonly DispatcherTimer _turnMetricsTimer;

        // ===== 流式输出合帧 =====
        // 模型每秒可能推几十个 token。若每个 token 都走一次 Dispatcher 并重排整篇
        // FlowDocument，界面会明显卡顿——这不是 C# 慢，而是"每 token 一次全量重排"的
        // 复杂度问题：文本越长，单次重排越贵，总代价接近 O(n²)。
        // 这里把增量先攒进缓冲区，由定时器按固定节奏一次性刷到界面：
        // Dispatcher 编排次数从"每 token 一次"降到"每 60ms 一次"，重排次数与原子的数量脱钩。
        private readonly object _streamBufferGate = new object();
        private readonly StringBuilder _pendingContent = new StringBuilder();
        private readonly StringBuilder _pendingReasoning = new StringBuilder();
        private readonly DispatcherTimer _streamFlushTimer;
        private bool _scrollPending;
        private const int StreamFlushIntervalMs = 60;

        /// <summary>
        /// 自动跟随开关。流式输出时默认为 true（一直贴底）；
        /// 用户一旦主动上滑就置 false 并弹出「回到底部」按钮，
        /// 避免"用户想回看上文、却被新内容反复拽回底部"。
        /// </summary>
        private bool _autoScrollEnabled = true;

        /// <summary>距底部多少像素以内仍算"在底部"（超过则视为用户已上滑）。</summary>
        private const double AutoScrollBottomThreshold = 24.0;

        /// <summary>
        /// 代码触发的滚动会连带触发 ScrollChanged；用它把"程序滚动"与"用户滚动"区分开，
        /// 否则每次自动贴底都会把自己判定成"用户上滑"从而关掉自动跟随。
        /// </summary>
        private bool _scrollDrivenByCode;

        private AiSettings _settings;
        private AgentSession _session;
        internal CancellationTokenSource _turnCancellation;
        private AssistantPhaseView _activePhase;
        private int _promptTokens, _completionTokens, _totalTokens;
        private int _turnPromptBaseline, _turnCompletionBaseline, _turnTokenBaseline;
        private bool _turnHasEstimatedUsage;
        private TimeSpan _lastTurnDuration = TimeSpan.Zero;
        private int _lastTurnPromptTokens, _lastTurnCompletionTokens, _lastTurnTokens;
        private bool _lastTurnHasEstimatedUsage;
        private bool _suppressTurnMetrics = true;
        // 耗时/Token 只显示在助手回复的末尾，不再占用顶栏左上角。
        private TextBlock _turnMetricsText;

        // ===== 等待行（"在等"还是"死了"的唯一凭据）=====
        // Agent 在发起模型请求/视觉校验前后调用 BeginWait/EndWait；计时器每秒把
        // "阶段说明 · 已等待 mm:ss / 上限 mm:ss" 刷到等待占位区。占位区一旦被流式
        // 正文隐藏就停止刷新（否则会把提示重新弹到已输出的正文上面）。
        private readonly DispatcherTimer _waitTimer;
        private DateTime _waitStartedAtUtc;
        private string _waitDescription;
        private TimeSpan _waitTimeout;
        private string _activityHintText;

        // Phase 2：视图不再持有 AutoCAD.Application。宿主能力（工具执行）
        // 经 ChatCoreHost 注入的 IToolExecutor 获取。
        public AiChatView()
        {
            InitializeComponent();
            _settings = AiSettings.Load();
            _turnMetricsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _turnMetricsTimer.Tick += TurnMetricsTimer_Tick;
            // 流式刷新用 Render 优先级（高于 Background）：既要与 UI 交互抢占时有节制，
            // 又要保证 60ms 的节奏不被空闲任务饿死。
            _streamFlushTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(StreamFlushIntervalMs) };
            _streamFlushTimer.Tick += (s, e) => DrainStreamBuffers();
            _waitTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _waitTimer.Tick += (s, e) => RefreshWaitText(false);
            ApplyStaticTexts();
            RefreshMetrics();
            AppendWelcome();
            UiLanguage.LanguageChanged += OnUiLanguageChanged;
        }

        private void OnUiLanguageChanged(object sender, EventArgs e)
        {
            InvokeUi(() =>
            {
                ApplyStaticTexts();
                RefreshMetrics();
                if (_session == null && MessagesPanel != null && MessagesPanel.Children.Count <= 1)
                {
                    MessagesPanel.Children.Clear();
                    AppendWelcome();
                }
            });
        }

        public bool IsTaskRunning => _turnCancellation != null;

        /// <summary>当前会话在磁盘上的 Markdown 历史文件路径；尚未建立会话或空会话时为 null。</summary>
        public string CurrentHistoryFilePath => _session?.HistoryFilePath;

        /// <summary>当完成一轮对话或会话状态更新时触发，供外层宿主（如独立工作台侧边栏）同步历史列表。</summary>
        public event EventHandler ConversationUpdated;

        /// <summary>Ribbon 设置按钮保存后同步到已打开的对话窗口；改动从下一条消息生效。</summary>
        public void ApplySettings(AiSettings settings)
        {
            if (settings == null) return;
            _settings = settings;
            // 旧会话的 client 在构造时固化了端点/密钥；不刷新的话"从下一条消息生效"
            // 只是文案，实际仍打旧地址。正在进行的调用持有旧 client 引用，不受影响。
            try { _session?.UpdateSettings(settings); } catch { }
            SetStatus(StatusKind.Idle, L("SettingsUpdated"));
        }

        internal void FocusInput() { try { InputBox?.Focus(); } catch { } }

        /// <summary>
        /// 宿主窗口或停靠面板关闭时释放视图持有的计时器、取消运行中的任务并释放当前会话。
        /// </summary>
        public void Shutdown()
        {
            UiLanguage.LanguageChanged -= OnUiLanguageChanged;
            StopVoiceInputCleanup();
            try { _streamFlushTimer?.Stop(); } catch { }
            try { _turnMetricsTimer?.Stop(); } catch { }
            try { _waitTimer?.Stop(); } catch { }
            try { _turnCancellation?.Cancel(); } catch { }
            try { _turnCancellation?.Dispose(); } catch { }
            _turnCancellation = null;
            try { _session?.Dispose(); } catch { }
            _session = null;
        }

        private Window OwnerWindow { get { try { return Window.GetWindow(this); } catch { return null; } } }
        internal MessageBoxResult ShowMessage(string text, string caption, MessageBoxButton button, MessageBoxImage icon) { Window owner = OwnerWindow; return owner != null ? MessageBox.Show(owner, text, caption, button, icon) : MessageBox.Show(text, caption, button, icon); }
        internal MessageBoxResult ShowWarning(string text) => ShowMessage(text, ChatCoreHostConfig.UiProductTitleOrDefault(), MessageBoxButton.OK, MessageBoxImage.Warning);
        internal MessageBoxResult ShowInformation(string text) => ShowMessage(text, ChatCoreHostConfig.UiProductTitleOrDefault(), MessageBoxButton.OK, MessageBoxImage.Information);
        internal bool ShowSaveDialog(SaveFileDialog dialog) { Window owner = OwnerWindow; return owner != null ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true; }
        private void AiChatView_Loaded(object sender, RoutedEventArgs e) => FocusInput();

        private Style UiStyle(string key) { try { return TryFindResource(key) as Style; } catch (Exception ex) { AiExceptionGuard.Log("Ui.Style." + key, ex); return null; } }
        private void ApplyStyle(FrameworkElement element, string key) { if (element != null) element.Style = UiStyle(key); }
        private Brush UiBrush(string key) { try { return TryFindResource(key) as Brush; } catch (Exception ex) { AiExceptionGuard.Log("Ui.Brush." + key, ex); return null; } }

        internal Task SendCurrentAsync() => SendUnifiedWithAttachmentsAsync();

        void IAgentUi.BeginPhase() => InvokeUi(CreateAssistantPhaseCore);
        void IAgentUi.ResetPhase(string reason) => InvokeUi(() => DiscardActivePhase(reason));
        void IAgentUi.AppendContent(string delta) => AppendAssistantText(delta);
        void IAgentUi.AppendReasoning(string delta) => AppendReasoning(delta);
        void IAgentUi.Message(string role, string body) => AppendMessage(role, body, false);
        void IAgentUi.Tool(ToolActivity activity) => AppendTool(activity);
        void IAgentUi.Preview(IReadOnlyList<PreviewImage> images) => AppendPreview(images);
        void IAgentUi.Usage(string stage, AgentUsage usage) => AppendUsage(stage, usage);
        void IAgentUi.SetActivityHint(string text) => SetActivityHint(text);
        void IAgentUi.BeginWait(string description, TimeSpan timeout) => BeginWait(description, timeout);
        void IAgentUi.EndWait() => EndWait();

        /// <summary>
        /// 开始一次长等待：显示阶段说明 + 实时计时 + 本次上限。
        /// 刻意**不清空** _activityHintText：压缩/分段续跑的提示正是在 BeginWait 之前
        /// 设置的，清掉它用户就看不到"为什么这次特别慢"。
        /// </summary>
        private void BeginWait(string description, TimeSpan timeout)
        {
            InvokeUi(() =>
            {
                _waitDescription = string.IsNullOrWhiteSpace(description) ? L("Generating") : description;
                _waitTimeout = timeout;
                _waitStartedAtUtc = DateTime.UtcNow;
                RefreshWaitText(true);
                if (!_waitTimer.IsEnabled) _waitTimer.Start();
            });
        }

        /// <summary>等待结束：停表并清空说明，避免上一轮的提示带到下一轮。</summary>
        private void EndWait()
        {
            InvokeUi(() =>
            {
                _waitTimer.Stop();
                _waitDescription = null;
                _activityHintText = null;
                _waitTimeout = TimeSpan.Zero;
            });
        }

        /// <summary>
        /// 刷新等待行。forceVisible=true 时强制显示占位（新一轮请求开始）；
        /// 计时器刷新走 forceVisible=false，占位已被正文隐藏就不再打扰。
        /// </summary>
        private void RefreshWaitText(bool forceVisible)
        {
            if (_waitDescription == null) return;
            AssistantPhaseView phase = _activePhase;
            if (phase == null) return;
            if (!forceVisible && !phase.IsWaiting) return;

            string line = string.IsNullOrEmpty(_activityHintText) ? _waitDescription : _activityHintText;
            line += " · 已等待 " + FormatClock(DateTime.UtcNow - _waitStartedAtUtc);
            if (_waitTimeout > TimeSpan.Zero) line += " / 上限 " + FormatClock(_waitTimeout);
            if (forceVisible) phase.SetWaitingText(line);
            else phase.UpdateWaitingText(line);
        }

        private static string FormatClock(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            return ((int)value.TotalMinutes).ToString("00") + ":" + value.Seconds.ToString("00");
        }

        private void InvokeUi(Action action) { if (Dispatcher.CheckAccess()) action(); else Dispatcher.Invoke(action); }
        private void AddDiagnostic(string title, string body, bool code) => _diagnostics.Add(new DiagnosticEntry { Time = DateTime.Now, Title = title ?? string.Empty, Body = body ?? string.Empty, Code = code });

        internal IReadOnlyList<DiagnosticEntry> Diagnostics => _diagnostics;
        internal int PromptTokens => _promptTokens;
        internal int CompletionTokens => _completionTokens;
        internal int TotalTokens => _totalTokens;
    }
}
