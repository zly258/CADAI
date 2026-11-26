using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CadAI.Core
{
    public partial class AiChatView
    {
        private readonly List<QueuedChatMessage> _messageQueue = new List<QueuedChatMessage>();
        private bool _queuePaused = false;
        private bool _isDispatchingQueue = false;

        /// <summary>当前排队消息数量。</summary>
        public int QueuedMessageCount => _messageQueue.Count;

        /// <summary>
        /// 将当前输入框中的文本与挂载的附件打包入队。
        /// </summary>
        private void EnqueueCurrentInput()
        {
            string text = (InputBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) && _pendingImages.Count == 0 && _pendingPaths.Count == 0)
                return;

            var queued = new QueuedChatMessage
            {
                UserText = text
            };
            queued.PathAttachments.AddRange(_pendingPaths);
            _pendingPaths.Clear();

            List<PendingImageAttachment> imgs = TakePendingImages();
            queued.ImageAttachments.AddRange(imgs);

            InputBox.Clear();
            UpdateAttachmentPreviewUi();

            _messageQueue.Add(queued);
            RefreshQueueUi();
            FocusInput();
        }

        /// <summary>
        /// 刷新待发送队列 UI 面板。
        /// </summary>
        private void RefreshQueueUi()
        {
            if (MessageQueueBorder == null || QueueItemsPanel == null) return;

            if (_messageQueue.Count == 0)
            {
                MessageQueueBorder.Visibility = Visibility.Collapsed;
                _queuePaused = false;
                return;
            }

            MessageQueueBorder.Visibility = Visibility.Visible;
            if (QueueTitleText != null)
            {
                QueueTitleText.Text = $"待执行队列 ({_messageQueue.Count})";
            }

            if (QueueStatusHint != null)
            {
                QueueStatusHint.Visibility = _queuePaused ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ResumeQueueButton != null)
            {
                ResumeQueueButton.Visibility = (_queuePaused && !IsTaskRunning) ? Visibility.Visible : Visibility.Collapsed;
            }

            QueueItemsPanel.Children.Clear();
            for (int i = 0; i < _messageQueue.Count; i++)
            {
                QueueItemsPanel.Children.Add(CreateQueueItemRow(_messageQueue[i], i, _messageQueue.Count));
            }
        }

        /// <summary>
        /// 构造单条队列项卡片。采用轻量卡片 + 圆形序号徽章 + 悬停矢量图标操作组。
        /// </summary>
        private UIElement CreateQueueItemRow(QueuedChatMessage item, int index, int total)
        {
            var border = new Border
            {
                Background = (Brush)FindResource("Brush.Panel"),
                BorderBrush = (Brush)FindResource("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)FindResource("Radius.Card"),
                Padding = new Thickness(8, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 4),
                SnapsToDevicePixels = true
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 1. 序号圆形徽章（微型强调色药丸/圆形）
            var badgeBorder = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = (Brush)FindResource("Brush.Accent.Subtle"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = (double)FindResource("FontSize.Micro"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brush.Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeBorder.Child = badgeText;
            Grid.SetColumn(badgeBorder, 0);
            grid.Children.Add(badgeBorder);

            // 2. 文本与附件摘要网格（自适应宽度并在窄空间平滑截断，保障附件角标可见）
            var contentGrid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = item.PreviewText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = (double)FindResource("FontSize.Body"),
                Foreground = (Brush)FindResource("Brush.Text.Primary"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = item.UserText
            };
            Grid.SetColumn(textBlock, 0);
            contentGrid.Children.Add(textBlock);

            if (item.HasAttachments)
            {
                var attachBadge = new Border
                {
                    Background = (Brush)FindResource("Brush.Accent.Subtle"),
                    CornerRadius = (CornerRadius)FindResource("Radius.Small"),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"附件：{item.ImageAttachments.Count} 张图片，{item.PathAttachments.Count} 个文档"
                };
                var attachStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                attachStack.Children.Add(MaterialSymbols.CreateIcon(MaterialSymbols.AttachFile, 12));
                attachStack.Children.Add(new TextBlock
                {
                    Text = item.AttachmentCount.ToString(),
                    FontSize = (double)FindResource("FontSize.Micro"),
                    Foreground = (Brush)FindResource("Brush.Accent"),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(2, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                attachBadge.Child = attachStack;
                Grid.SetColumn(attachBadge, 1);
                contentGrid.Children.Add(attachBadge);
            }
            Grid.SetColumn(contentGrid, 1);
            grid.Children.Add(contentGrid);

            // 3. 微型图标操作组：默认隐藏，鼠标移入整行或获焦时显现
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Hidden
            };

            // 上移按钮
            var upBtn = new Button
            {
                Content = MaterialSymbols.CreateIcon(MaterialSymbols.ArrowUpward, 13),
                Style = (Style)FindResource("Button.Icon.Micro"),
                IsEnabled = index > 0,
                ToolTip = "上移（优先执行）"
            };
            upBtn.Click += (s, e) => MoveQueuedMessage(item, -1);
            actions.Children.Add(upBtn);

            // 下移按钮
            var downBtn = new Button
            {
                Content = MaterialSymbols.CreateIcon(MaterialSymbols.ArrowDownward, 13),
                Style = (Style)FindResource("Button.Icon.Micro"),
                Margin = new Thickness(2, 0, 0, 0),
                IsEnabled = index < total - 1,
                ToolTip = "下移"
            };
            downBtn.Click += (s, e) => MoveQueuedMessage(item, 1);
            actions.Children.Add(downBtn);

            // 编辑按钮
            var editBtn = new Button
            {
                Content = MaterialSymbols.CreateIcon(MaterialSymbols.Edit, 13),
                Style = (Style)FindResource("Button.Icon.Micro"),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "取出到输入框中重新编辑"
            };
            editBtn.Click += (s, e) => EditQueuedMessage(item);
            actions.Children.Add(editBtn);

            // 移除按钮
            var delBtn = new Button
            {
                Content = MaterialSymbols.CreateIcon(MaterialSymbols.Close, 12),
                Style = (Style)FindResource("Button.Icon.Micro.Danger"),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "从队列中移除"
            };
            delBtn.Click += (s, e) => RemoveQueuedMessage(item);
            actions.Children.Add(delBtn);

            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            // 悬停交互：划入行时显现操作按钮并提供柔和高亮底色
            border.MouseEnter += (s, e) =>
            {
                border.Background = (Brush)FindResource("Brush.Control.Hover");
                border.BorderBrush = (Brush)FindResource("Brush.Border.Strong");
                actions.Visibility = Visibility.Visible;
            };
            border.MouseLeave += (s, e) =>
            {
                if (!border.IsKeyboardFocusWithin)
                {
                    border.Background = (Brush)FindResource("Brush.Panel");
                    border.BorderBrush = (Brush)FindResource("Brush.Border");
                    actions.Visibility = Visibility.Hidden;
                }
            };
            border.IsKeyboardFocusWithinChanged += (s, e) =>
            {
                if (border.IsKeyboardFocusWithin)
                {
                    border.Background = (Brush)FindResource("Brush.Control.Hover");
                    border.BorderBrush = (Brush)FindResource("Brush.Border.Strong");
                    actions.Visibility = Visibility.Visible;
                }
                else if (!border.IsMouseOver)
                {
                    border.Background = (Brush)FindResource("Brush.Panel");
                    border.BorderBrush = (Brush)FindResource("Brush.Border");
                    actions.Visibility = Visibility.Hidden;
                }
            };

            border.Child = grid;
            return border;
        }

        /// <summary>调整排队项的顺序（-1 上移，+1 下移）。</summary>
        private void MoveQueuedMessage(QueuedChatMessage item, int offset)
        {
            int idx = _messageQueue.IndexOf(item);
            if (idx < 0) return;
            int target = idx + offset;
            if (target < 0 || target >= _messageQueue.Count) return;
            _messageQueue.RemoveAt(idx);
            _messageQueue.Insert(target, item);
            RefreshQueueUi();
        }

        /// <summary>将排队项取出回填至输入框。</summary>
        private void EditQueuedMessage(QueuedChatMessage item)
        {
            _messageQueue.Remove(item);

            if (string.IsNullOrEmpty(InputBox.Text))
            {
                InputBox.Text = item.UserText;
            }
            else
            {
                InputBox.Text = (InputBox.Text + Environment.NewLine + item.UserText).Trim();
            }
            InputBox.CaretIndex = InputBox.Text.Length;

            foreach (var p in item.PathAttachments)
            {
                if (!_pendingPaths.Contains(p)) _pendingPaths.Add(p);
            }
            foreach (var img in item.ImageAttachments)
            {
                _pendingImages.Add(img);
            }
            item.PathAttachments.Clear();
            item.ImageAttachments.Clear(); // 转移到输入区，不销毁临时文件

            UpdateAttachmentPreviewUi();
            RefreshQueueUi();
            FocusInput();
        }

        /// <summary>移除指定排队项并清理其携带的临时资源。</summary>
        private void RemoveQueuedMessage(QueuedChatMessage item)
        {
            _messageQueue.Remove(item);
            item.Cleanup();
            RefreshQueueUi();
        }

        /// <summary>清空所有排队消息。</summary>
        public void ClearQueue()
        {
            foreach (var q in _messageQueue)
            {
                q.Cleanup();
            }
            _messageQueue.Clear();
            _queuePaused = false;
            RefreshQueueUi();
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            ClearQueue();
        }

        private async void ResumeQueue_Click(object sender, RoutedEventArgs e)
        {
            _queuePaused = false;
            RefreshQueueUi();
            await CheckAndDispatchNextQueueItemAsync();
        }

        /// <summary>
        /// 检查队列并在空闲状态下自动调度下一条消息执行。
        /// </summary>
        internal async Task CheckAndDispatchNextQueueItemAsync()
        {
            if (_isDispatchingQueue) return;
            if (IsTaskRunning) return;
            if (_queuePaused) return;
            if (_messageQueue.Count == 0) return;

            _isDispatchingQueue = true;
            try
            {
                var next = _messageQueue[0];
                _messageQueue.RemoveAt(0);
                RefreshQueueUi();

                await SendQueuedMessageAsync(next);
            }
            finally
            {
                _isDispatchingQueue = false;
            }
        }

        private async Task SendQueuedMessageAsync(QueuedChatMessage queued)
        {
            if (IsTaskRunning) return;
            await ExecuteTurnCoreAsync(queued.UserText, queued.PathAttachments, queued.ImageAttachments);
        }
    }
}
