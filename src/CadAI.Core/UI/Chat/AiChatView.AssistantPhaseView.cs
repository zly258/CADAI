using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CadAI.Core
{
    /// <summary>AiChatView 的嵌套类型：助手回复单个阶段（正文/思考/等待/耗时）的视图状态。</summary>
    public partial class AiChatView
    {
        internal sealed class AssistantPhaseView
        {
            private readonly FrameworkElement _waiting;
            private readonly BusyIndicator _spinner;
            private readonly TextBlock _waitingText;
            private const string DefaultWaitingText = "正在生成…";

            public AssistantPhaseView(StackPanel c, MarkdownTextBlock s, Expander t, TextBlock tt, TextBlock m, FrameworkElement waiting, BusyIndicator spinner, TextBlock waitingText)
            {
                Container = c;
                StreamedText = s;
                Thinking = t;
                ThinkingText = tt;
                Metrics = m;
                _waiting = waiting;
                _spinner = spinner;
                _waitingText = waitingText;
            }

            public StackPanel Container { get; }
            public MarkdownTextBlock StreamedText { get; }
            public Expander Thinking { get; }
            public TextBlock ThinkingText { get; }
            public TextBlock Metrics { get; }
            public StringBuilder Text { get; } = new StringBuilder();
            public StringBuilder Reasoning { get; } = new StringBuilder();
            public bool ReasoningStarted { get; private set; }
            public int ToolCount { get; set; }

            /// <summary>等待占位是否正在显示（流式正文到达后会隐藏）。</summary>
            public bool IsWaiting => _waiting != null && _waiting.Visibility == Visibility.Visible;

            /// <summary>
            /// 只改等待行的文字，不改变可见性、不重启动画。
            /// 供每秒刷新的"已等待 mm:ss"使用——用 SetWaitingText 会把已经被
            /// 流式正文隐藏的占位重新弹出来，压在正文上面。
            /// </summary>
            public void UpdateWaitingText(string text)
            {
                if (_waitingText == null || string.IsNullOrWhiteSpace(text)) return;
                _waitingText.Text = text;
            }

            public void BeginReasoning()
            {
                ReasoningStarted = true;
                Thinking.Visibility = Visibility.Visible;
                Thinking.IsExpanded = true;
                // 推理内容已经可见，占位提示不再需要。
                EndWaiting();
            }

            public void EndReasoning() { if (ReasoningStarted) Thinking.IsExpanded = false; }

            /// <summary>把工具卡片等中间内容插到耗时行之前，保证耗时始终是回复的最后一行。</summary>
            public void InsertBeforeMetrics(UIElement element)
            {
                if (element == null) return;
                int index = Container.Children.IndexOf(Metrics);
                if (index < 0) Container.Children.Add(element);
                else Container.Children.Insert(index, element);
            }

            /// <summary>隐藏等待占位并停止动画；重复调用无副作用。</summary>
            public void EndWaiting()
            {
                _spinner?.Stop();
                if (_waiting != null) _waiting.Visibility = Visibility.Collapsed;
            }

            /// <summary>
            /// 更新等待占位区的一行状态（重试/压缩提示）。空串恢复默认文案；
            /// 占位已被正文隐藏时重新显示——重试期间用户必须看到"还活着"的信号。
            /// 仅 UI 线程调用（经 SetActivityHint 的 InvokeUi 进入）。
            /// </summary>
            public void SetWaitingText(string text)
            {
                if (_waitingText == null) return;
                if (_waiting != null) _waiting.Visibility = Visibility.Visible;
                _spinner?.Start();
                // 界面语言固定中文，默认文案直接写死；无法引用外层 L()（嵌套类限制）。
                _waitingText.Text = string.IsNullOrWhiteSpace(text) ? DefaultWaitingText : text;
            }
        }
    }
}
