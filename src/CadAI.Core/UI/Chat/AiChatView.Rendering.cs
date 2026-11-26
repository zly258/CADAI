using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CadAI.Core
{
    /// <summary>
    /// AiChatView 的呈现部分：消息/工具/预览卡片的创建、角色解析与滚动处理。
    /// </summary>
    public partial class AiChatView
    {
        private void AppendMessage(string role, string body, bool user)
        {
            InvokeUi(() =>
            {
                if (!user && string.Equals(role, "AI", StringComparison.OrdinalIgnoreCase))
                {
                    AppendAssistantText(body);
                    return;
                }
                FinalizeActivePhase();
                _assistantGroup = null;
                string safe = body ?? string.Empty;
                MessageRole kind = ResolveMessageRole(role, user);
                AddDiagnostic(user ? "USER" : (role ?? "SYSTEM").ToUpperInvariant(), safe, false);
                AddMessageCard(CreateMessageCard(kind, role, safe));
                ScrollToBottom();
            });
        }

        // 长会话内存护栏：视觉树只保留最近 MaxRenderedMessages 张消息卡，
        // 更早的从面板移除（Markdown 流式卡与工具卡也按普通子项计数）。
        // 完整会话仍保存在历史 .md 与快照里，不受影响。
        private const int MaxRenderedMessages = 200;

        private void AddMessageCard(UIElement card)
        {
            MessagesPanel.Children.Add(card);
            while (MessagesPanel.Children.Count > MaxRenderedMessages)
                MessagesPanel.Children.RemoveAt(0);
        }

        private static MessageRole ResolveMessageRole(string role, bool user)
        {
            if (user) return MessageRole.User;
            string value = (role ?? string.Empty).Trim();
            if (value.IndexOf("错误", StringComparison.Ordinal) >= 0 || value.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return MessageRole.Error;
            if (string.Equals(value, "AI", StringComparison.OrdinalIgnoreCase)) return MessageRole.Assistant;
            if (string.Equals(value, "welcome", StringComparison.OrdinalIgnoreCase)) return MessageRole.Welcome;
            return MessageRole.System;
        }

        private Border CreateMessageCard(MessageRole kind, string role, string body)
        {
            var content = new StackPanel();
            content.Children.Add(CreateRoleLabel(role, kind));
            // AI 回复按 Markdown 渲染；欢迎卡也用 Markdown（内含小标题与示例清单）；
            // 用户输入与系统/错误提示保持纯文本，避免把日志里的符号当语法解析。
            bool allowMarkdown = kind == MessageRole.Assistant || kind == MessageRole.Welcome;
            content.Children.Add(CreateBodyText(body, allowMarkdown));
            return CreateMessageCard(kind, content);
        }

        private Border CreateMessageCard(MessageRole kind, UIElement content)
        {
            var card = new Border { Child = content, Margin = kind == MessageRole.User ? new Thickness(48, 0, 0, 8) : new Thickness(0, 0, 0, 8) };
            ApplyStyle(card, "Card.Message." + kind);
            return card;
        }

        private Border CreateRoleLabel(string text, MessageRole kind)
        {
            string labelText = kind == MessageRole.User ? L("You")
                : kind == MessageRole.Assistant ? "AI"
                : kind == MessageRole.Error ? L("Error")
                : kind == MessageRole.Welcome ? L("WelcomeTitle")
                : L("System");
            // 胶囊标签：Border 提供 CornerRadius（TextBlock 画不了圆角），
            // 样式键仍是 Text.Role.*，只是 TargetType 从 TextBlock 换成了 Border；
            // 前景经 TextElement.Foreground 继承进内部文字。
            var roleText = new TextBlock { Text = labelText };
            ApplyStyle(roleText, "Text.RoleText");
            var label = new Border { Child = roleText };
            ApplyStyle(label, "Text.Role." + kind);
            return label;
        }

        /// <summary>
        /// 新建对话时的欢迎卡。用独立角色（<see cref="MessageRole.Welcome"/>）而不是普通 System，
        /// 以便套用带强调色左边框的卡片样式——用户反馈"新建对话默认显示的提示字太弱"。
        /// </summary>
        internal void AppendWelcome()
        {
            InvokeUi(() =>
            {
                FinalizeActivePhase();
                _assistantGroup = null;
                AddMessageCard(CreateMessageCard(MessageRole.Welcome, "welcome", L("Welcome")));
                _autoScrollEnabled = true;
                UpdateBackToBottomButton();
                ScrollToBottom();
            });
        }

        /// <summary>
        /// 创建消息正文控件。<paramref name="allowMarkdown"/> 为 true 时交给第三方 Markdown 组件渲染，
        /// 否则按纯文本呈现（用户输入、诊断日志等不该被解析的内容）。
        /// </summary>
        private MarkdownTextBlock CreateBodyText(string text, bool allowMarkdown = false)
        {
            // 赋值顺序就是渲染顺序，别调换：Markdown 的 setter 会**立刻**渲染首帧，
            // 所以字体 / 字号 / 前景色必须写在它前面。旧实现把 Markdown 写在最前，
            // 首帧只能按控件默认字号（12）画一遍，紧接着 FontSize 被赋成主题的 Body 字号再重排一次
            // —— 这就是用户看到的"先小后大"。
            var block = new MarkdownTextBlock
            {
                PlainTextMode = !allowMarkdown,
                FontFamily = TryFindResource("Font.Ui") as FontFamily ?? FontFamily,
                FontSize = ResolveBodyFontSize(),
                Foreground = UiBrush("Brush.Text.Primary") ?? Foreground,
                Markdown = text ?? string.Empty
            };
            return block;
        }

        /// <summary>
        /// 正文主题字号。视图资源里取不到（例如视图尚未 Loaded、控件还没挂进树）时退到静态主题字典，
        /// 保证任何时刻拿到的都是 Typography.xaml 里那一个值。
        /// </summary>
        private double ResolveBodyFontSize()
        {
            object size = TryFindResource("FontSize.Body") ?? ThemeResources.Find("FontSize.Body");
            if (size is double bodySize && bodySize > 0) return bodySize;
            return 12;
        }

        /// <summary>缩略图角标 / 附件名等比 Caption 更小的字号，取 Typography 的 FontSize.Micro。</summary>
        private double ResolveMicroFontSize()
        {
            object size = TryFindResource("FontSize.Micro") ?? ThemeResources.Find("FontSize.Micro");
            if (size is double microSize && microSize > 0) return microSize;
            return 10;
        }

        private void AppendTool(ToolActivity activity)
        {
            if (activity == null) return;
            InvokeUi(() =>
            {
                AddDiagnostic((activity.IsResult ? "TOOL RESULT · " : "TOOL CALL · ") + activity.Name, activity.Json, true);
                if (!activity.IsResult)
                {
                    ToolCard card = CreateToolCard(activity);
                    _toolCards[activity.CallId ?? Guid.NewGuid().ToString("N")] = card;
                    AddToolToAssistantPhase(card.Root);

                }
                else
                {
                    if (!_toolCards.TryGetValue(activity.CallId ?? string.Empty, out ToolCard card))
                    {
                        card = CreateToolCard(new ToolActivity { CallId = activity.CallId, Name = activity.Name, Json = "{}" });
                        AddToolToAssistantPhase(card.Root);
                    }
                    card.Complete(Truncate(activity.Json), activity.IsError, L("Failed"), activity.Json);

                    // 建模截图是"结果本身"的证据，不能只折叠在工具卡里。
                    //
                    // 2026-10-14 用户反馈："建模的效果很差……截图应该在对话中显示"。
                    // 此前截图只以 176px 缩略图嵌在**折叠**的工具卡内部（当时的反馈是"截图太占版面"），
                    // 于是用户根本看不到模型长什么样，只能靠文字判断，
                    // 把"我看不到"误判成了"建得差"。
                    //
                    // 折中：只对**宿主配置的截图工具**（模型可视化本身）额外铺一个常显预览块；
                    // 其余工具（查询/日志/参数）保持折叠，不把对话撑长。
                    // 工具名走 ChatCoreHostConfig：CAD 宿主是 cad_view_capture_screen，
                    // 写死 cad_view_capture_screen 会让 AutoCAD 的截图永远拿不到常显块。
                    if (!activity.IsError && IsVisualCaptureTool(activity.Name))
                    {
                        System.Collections.Generic.List<PreviewImage> previews = TryExtractPreviewImages(activity.Json);
                        if (previews != null && previews.Count > 0) AppendPreview(previews);
                    }
                }
                ScrollToBottom();
            });
        }

        private void AddToolToAssistantPhase(Border card)
        {
            if (_activePhase == null) CreateAssistantPhaseCore();
            _activePhase.EndReasoning();
            _activePhase.EndWaiting();
            _activePhase.ToolCount++;
            _activePhase.InsertBeforeMetrics(card);
        }

        private ToolCard CreateToolCard(ToolActivity call)
        {
            var body = new StackPanel();
            var inputLabel = new TextBlock { Text = L("Input"), Margin = new Thickness(0, 0, 0, 4) };
            ApplyStyle(inputLabel, "Text.Caption");
            body.Children.Add(inputLabel);
            var input = new TextBox { Text = Truncate(call.Json) };
            ApplyStyle(input, "TextBox.Code");
            body.Children.Add(input);
            var output = new TextBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            ApplyStyle(output, "TextBox.Code");
            body.Children.Add(output);
            var header = new TextBlock
            {
                Text = L("Tool") + " · " + (call.Name ?? string.Empty),
                Margin = new Thickness(0, 0, 52, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var expander = new Expander { Header = header, Content = body, IsExpanded = false };
            ApplyStyle(expander, "Expander.Subtle");

            // 悬浮纯文字复制按钮（平时隐藏，鼠标移入工具卡片时显示；点击复制工具名称/输入/输出详情）
            var copyBtn = new Button
            {
                Content = "复制",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                Visibility = Visibility.Collapsed,
                ToolTip = "复制工具详情（名称、输入、输出）",
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false
            };
            ApplyStyle(copyBtn, "Button.Text.Small");

            var grid = new Grid();
            grid.Children.Add(expander);
            grid.Children.Add(copyBtn);

            var root = new Border { Child = grid, Margin = new Thickness(0, 6, 0, 6) };
            ApplyStyle(root, "Card.Tool");

            root.MouseEnter += (s, e) => copyBtn.Visibility = Visibility.Visible;
            root.MouseLeave += (s, e) => copyBtn.Visibility = Visibility.Collapsed;

            var card = new ToolCard(this, root, header, body, output, call.Name, call.Json);

            copyBtn.Click += async (s, e) =>
            {
                e.Handled = true; // 阻止冒泡避免触发 Expander 的折叠展开
                string text = card.BuildCopyDetails();
                ClipboardAccess.SetTextInBackground(text, "Chat.ToolCopy");
                copyBtn.Content = "已复制";
                await System.Threading.Tasks.Task.Delay(1500);
                copyBtn.Content = "复制";
            };

            return card;
        }

        private string Truncate(string value) { string text = value ?? string.Empty; return text.Length > 5000 ? text.Substring(0, 5000) + ("\n… UI 已截断") : text; }

        private void AppendPreview(IReadOnlyList<PreviewImage> images)
        {
            if (images == null || images.Count == 0) return;
            InvokeUi(() =>
            {
                FinalizeActivePhase();
                var container = new StackPanel();
                container.Children.Add(CreateRoleLabel(L("Preview"), MessageRole.Assistant));

                var panel = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                int added = 0;
                foreach (PreviewImage preview in images)
                {
                    if (preview == null || string.IsNullOrWhiteSpace(preview.Path) || !File.Exists(preview.Path))
                        continue;

                    BitmapSource source = LoadPreviewBitmap(preview.Path);
                    if (source == null) continue;

                    var tile = new StackPanel
                    {
                        Width = 196,
                        Margin = new Thickness(0, 0, 8, 8)
                    };
                    var border = new Border
                    {
                        BorderBrush = UiBrush("Brush.Border") ?? Brushes.LightGray,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(2),
                        Background = UiBrush("Brush.Panel") ?? Brushes.White
                    };
                    border.Child = new Image
                    {
                        Source = source,
                        Width = 188,
                        Height = 124,
                        Stretch = Stretch.Uniform,
                        ToolTip = preview.Path
                    };
                    tile.Children.Add(border);

                    var caption = new TextBlock
                    {
                        Text = FormatPreviewViewName(preview.View),
                        Margin = new Thickness(2, 3, 0, 0)
                    };
                    ApplyStyle(caption, "Text.Caption");
                    tile.Children.Add(caption);
                    panel.Children.Add(tile);
                    added++;
                }

                if (added == 0) return;
                container.Children.Add(panel);
                if (_assistantGroup == null) CreateAssistantPhaseCore();
                _assistantGroup.Children.Add(container);
                ScrollToBottom();
            });
        }

        private static BitmapSource LoadPreviewBitmap(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                image.EndInit();
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                // 预览图加载失败时 UI 只会静默缺图，必须留痕才能排查。
                AiExceptionGuard.Log("Chat.PreviewBitmap", ex);
                return null;
            }
        }

        private string FormatPreviewViewName(string view)
        {
            string value = (view ?? string.Empty).Trim();
            if (value.Length == 0) return "Preview";
            switch (value.ToLowerInvariant())
            {
                case "front": return "前视";
                case "back": return "后视";
                case "left": return "左视";
                case "right": return "右视";
                case "top": return "俯视";
                case "bottom": return "仰视";
                case "iso_top_right": return "轴测 · 右上";
                case "iso_top_left": return "轴测 · 左上";
                case "iso_bottom_right": return "轴测 · 右下";
                case "iso_bottom_left": return "轴测 · 左下";
                case "active": return "当前视图";
                default: return value;
            }
        }

        /// <summary>
        /// 从工具结果 JSON 里提取落盘图片（view/path 对），供工具卡片内嵌缩略图。
        /// 与 AgentSession.ExtractPreviewImages 识别同一种形状：MCP content[].text
        /// 包裹层与嵌套的 images/entries 数组都递归展开；失败返回空列表，只影响展示。
        /// </summary>
        private static System.Collections.Generic.List<PreviewImage> TryExtractPreviewImages(string json)
        {
            var images = new System.Collections.Generic.List<PreviewImage>();
            if (string.IsNullOrWhiteSpace(json)) return images;
            try
            {
                CollectPreviewImages(AgentJson.New().DeserializeObject(json), images, 0);
            }
            catch { }
            return images;
        }

        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
        };

        /// <summary>该工具名是否是宿主配置的截图工具（决定要不要铺常显预览块）。</summary>
        private static bool IsVisualCaptureTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            string configured;
            try { configured = ChatCoreHostConfig.Current.VisualCaptureToolName; }
            catch { configured = null; }
            if (string.IsNullOrWhiteSpace(configured)) configured = "cad_view_capture_screen";
            return string.Equals(toolName, configured, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该路径是否指向一个图片文件（按扩展名判定，不查磁盘——展示层不碰 IO）。</summary>
        private static bool LooksLikeImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            foreach (string extension in ImageExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>图片条目的显示标签：优先 view/angle/label，都没有就留空（界面会只显示缩略图）。</summary>
        private static string PreviewLabel(System.Collections.Generic.Dictionary<string, object> map, string path)
        {
            foreach (string key in new[] { "view", "angle", "label", "name" })
            {
                if (!map.TryGetValue(key, out object value)) continue;
                string text = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            try { return System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static void CollectPreviewImages(object value, System.Collections.Generic.List<PreviewImage> images, int depth)
        {
            if (value == null || images == null || depth > 10 || images.Count >= 12) return;

            if (value is System.Collections.Generic.Dictionary<string, object> map)
            {
                // 只要条目里有指向**图片文件**的 path 就收下，view/angle 仅作为标签。
                //
                // 历史问题：这里原先要求同时存在 view（或 angle）+ path，那是
                // cad_view_capture_screen 的条目形状。CAD 宿主的截图工具返回
                // {"images":[{"ok":true,"path":"..."}]}——没有 view/angle 字段，
                // 于是截图既不进模型（自动视觉校验那条路另说）也不进界面，
                // 用户看到的是"工具说截图成功，但对话框里什么都没有"。
                // 按扩展名判定而不是按字段名判定，两个宿主就都能用。
                if (map.TryGetValue("path", out object pathValue))
                {
                    string path = Convert.ToString(pathValue) ?? string.Empty;
                    if (LooksLikeImagePath(path))
                        images.Add(new PreviewImage(PreviewLabel(map, path), path));
                }

                foreach (System.Collections.Generic.KeyValuePair<string, object> item in map)
                    CollectPreviewImages(item.Value, images, depth + 1);
                return;
            }

            if (value is string text)
            {
                string trimmed = text.Trim();
                if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
                    (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)))
                {
                    try { CollectPreviewImages(AgentJson.New().DeserializeObject(trimmed), images, depth + 1); }
                    catch { }
                }
                return;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (object item in sequence)
                    CollectPreviewImages(item, images, depth + 1);
            }
        }

        /// <summary>
        /// 滚到底部。合帧处理：同一轮 Dispatcher 周期内只真正执行一次 ScrollToEnd。
        ///
        /// ScrollToEnd 会强制整篇 FlowDocumentScrollViewer 重新测量排版，
        /// 文本越长越贵。流式输出时若每个增量都滚一次，总代价接近 O(n²)——
        /// 这正是"回答越长越卡"的主因。合并后滚动次数只与时间有关，与 token 数无关。
        ///
        /// 2026-10-14：增加自动跟随开关。用户主动上滑后 <see cref="_autoScrollEnabled"/> 为 false，
        /// 此时不再强制贴底，避免把正在回看上文的人反复拽回底部。
        /// </summary>
        private void ScrollToBottom()
        {
            if (!_autoScrollEnabled) return;
            if (_scrollPending) return;
            _scrollPending = true;
            ConversationScroll.Dispatcher.BeginInvoke(new Action(() =>
            {
                _scrollPending = false;
                try
                {
                    _scrollDrivenByCode = true;
                    ConversationScroll.ScrollToEnd();
                }
                catch { }
                finally
                {
                    // 滚动事件是异步派发的，延后一拍再复位标记。
                    ConversationScroll.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _scrollDrivenByCode = false;
                        UpdateBackToBottomButton();
                    }), DispatcherPriority.Background);
                }
            }), DispatcherPriority.Background);
        }

        /// <summary>重新开启自动跟随并滚到底部（新消息发出、或用户点「回到底部」时调用）。</summary>
        internal void ResumeAutoScroll()
        {
            _autoScrollEnabled = true;
            UpdateBackToBottomButton();
            if (_scrollPending) return;
            _scrollPending = true;
            ConversationScroll.Dispatcher.BeginInvoke(new Action(() =>
            {
                _scrollPending = false;
                try
                {
                    _scrollDrivenByCode = true;
                    ConversationScroll.ScrollToEnd();
                }
                catch { }
                finally
                {
                    ConversationScroll.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _scrollDrivenByCode = false;
                        UpdateBackToBottomButton();
                    }), DispatcherPriority.Background);
                }
            }), DispatcherPriority.Background);
        }

        /// <summary>当前是否停在底部附近。</summary>
        private bool IsNearBottom()
        {
            try
            {
                double remaining = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset;
                return remaining <= AutoScrollBottomThreshold;
            }
            catch { return true; }
        }

        /// <summary>按自动跟随状态与是否在底部，决定「回到底部」按钮的可见性。</summary>
        private void UpdateBackToBottomButton()
        {
            if (BackToBottomButton == null) return;
            try
            {
                bool shouldShow = !_autoScrollEnabled && !IsNearBottom();
                BackToBottomButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>
        /// 滚动位置变化。区分"程序滚动"与"用户滚动"：
        /// 程序滚动（自动贴底）不改变开关；用户滚离底部则关闭自动跟随并显示按钮。
        /// </summary>
        private void ConversationScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollDrivenByCode) return;

            // 只关心纵向位移；布局变化（内容变长）不算用户操作。
            if (Math.Abs(e.VerticalChange) < 0.5) return;

            if (IsNearBottom())
            {
                // 用户自己滚回底部 → 恢复自动跟随。
                _autoScrollEnabled = true;
            }
            else if (e.VerticalChange < 0)
            {
                // 向上滚（内容下移）→ 暂停自动跟随。
                _autoScrollEnabled = false;
            }

            UpdateBackToBottomButton();
        }

        private void BackToBottom_Click(object sender, RoutedEventArgs e)
        {
            ResumeAutoScroll();
        }

        /// <summary>
        /// 对话区滚轮修复。症状：鼠标停在消息区滚轮无效。
        /// 原因：消息气泡里含 MarkdownTextBlock / Expander 等可聚焦子元素，滚轮落在它们身上时被
        /// 子元素先处理并标记 Handled，ScrollViewer 收不到；StackPanel 内容也不会把滚动交回父容器。
        /// 处理：在 Preview 阶段（tunneling，先于子元素）直接驱动 ScrollViewer 并标记 Handled，
        /// 阻止子元素重复响应。触控板的小幅 delta 按像素走，鼠标滚轮按固定行数走。
        /// </summary>
        private void ConversationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ConversationScroll == null || e == null || e.Delta == 0) return;

            // 触控板发小幅 delta，按像素平移；鼠标滚轮一格固定滚动若干像素，手感一致且不依赖行高。
            double step = Math.Abs(e.Delta) < MouseWheelLineThreshold
                ? e.Delta
                : Math.Sign(e.Delta) * MouseWheelPixelsPerNotch;

            double next = ConversationScroll.VerticalOffset - step;
            //夹在合法范围内，防止空白处继续滚导致偏移越界后回弹。
            next = Math.Max(0, Math.Min(next, ConversationScroll.ScrollableHeight));

            // 向上滚（delta > 0）视为用户主动回看：立刻暂停自动跟随，不等 ScrollChanged。
            if (e.Delta > 0 && next < ConversationScroll.ScrollableHeight - AutoScrollBottomThreshold)
                _autoScrollEnabled = false;

            _scrollDrivenByCode = true;
            ConversationScroll.ScrollToVerticalOffset(next);
            ConversationScroll.Dispatcher.BeginInvoke(new Action(() =>
            {
                _scrollDrivenByCode = false;
                UpdateBackToBottomButton();
            }), DispatcherPriority.Background);

            e.Handled = true;
        }

        private const int MouseWheelLineThreshold = 120;
        private const double MouseWheelPixelsPerNotch = 120;
    }
}
