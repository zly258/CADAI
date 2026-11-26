using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CadAI.Core
{
    /// <summary>AiChatView 的嵌套类型：工具调用卡片（标题 + JSON 输出）。</summary>
    public partial class AiChatView
    {
        private sealed class ToolCard
        {
            private readonly TextBlock _header;
            private readonly TextBox _output;
            private readonly StackPanel _body;
            // 宿主视图实例：复用它的 UiBrush/ApplyStyle/L/FormatPreviewViewName/LoadPreviewBitmap。
            private readonly AiChatView _view;
            // 从工具调用发出到结果返回的实际执行耗时，完成时追加到标题上。
            private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

            private readonly string _toolName;
            private readonly string _inputJson;
            private string _outputJson = string.Empty;
            private bool _isCompleted;
            private bool _isError;

            public ToolCard(AiChatView view, Border root, TextBlock header, StackPanel body, TextBox output, string toolName = null, string inputJson = null)
            {
                _view = view; Root = root; _header = header; _body = body; _output = output;
                _toolName = toolName ?? string.Empty;
                _inputJson = inputJson ?? string.Empty;
            }
            public Border Root { get; }
            public void Complete(string json, bool error, string failedText, string fullJson = null)
            {
                _stopwatch.Stop();
                _isCompleted = true;
                _isError = error;
                _outputJson = fullJson ?? json ?? string.Empty;
                _output.Text = json ?? string.Empty;
                _output.Visibility = Visibility.Visible;
                if (error) _header.Text += " · " + failedText;
                _header.Text += " · " + FormatHeaderDuration(_stopwatch.Elapsed);
                if (!error) EmbedResultThumbnails(json);
            }

            public string BuildCopyDetails()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"工具名称: {_toolName}");
                string status = !_isCompleted ? "执行中" : (_isError ? "失败" : "成功");
                sb.AppendLine($"执行状态: {status}");
                if (_isCompleted)
                    sb.AppendLine($"执行耗时: {FormatHeaderDuration(_stopwatch.Elapsed)}");
                sb.AppendLine();
                sb.AppendLine("【输入参数】");
                sb.AppendLine(string.IsNullOrWhiteSpace(_inputJson) ? "{}" : _inputJson);
                sb.AppendLine();
                sb.AppendLine("【输出结果】");
                if (!_isCompleted)
                    sb.AppendLine("(尚未返回)");
                else
                    sb.AppendLine(string.IsNullOrWhiteSpace(_outputJson) ? "{}" : _outputJson);
                return sb.ToString();
            }

            /// <summary>
            /// 工具结果里带落盘图片（cad_view_capture_screen / cad_review_*）时，
            /// 把缩略图折叠进卡片内部：只有展开这张卡才看得到，不再铺一个独立的
            /// "Preview" 块把对话撑长（2026-10-14 用户反馈：截图展示太占版面）。
            /// 解析失败只影响展示，不影响功能。
            /// </summary>
            private void EmbedResultThumbnails(string json)
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                System.Collections.Generic.List<PreviewImage> images = TryExtractPreviewImages(json);
                if (images == null || images.Count == 0) return;

                _view.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var panel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                        int added = 0;
                        foreach (PreviewImage preview in images)
                        {
                            if (preview == null || string.IsNullOrWhiteSpace(preview.Path) || !System.IO.File.Exists(preview.Path)) continue;
                            BitmapSource source = AiChatView.LoadPreviewBitmap(preview.Path);
                            if (source == null) continue;

                            var tile = new StackPanel { Width = 176, Margin = new Thickness(0, 0, 8, 8) };
                            var border = new Border
                            {
                                BorderBrush = _view.UiBrush("Brush.Border") ?? Brushes.LightGray,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(2),
                                Background = _view.UiBrush("Brush.Panel") ?? Brushes.White
                            };
                            border.Child = new Image
                            {
                                Source = source,
                                Width = 168,
                                Height = 112,
                                Stretch = Stretch.Uniform,
                                ToolTip = preview.Path
                            };
                            tile.Children.Add(border);
                            var caption = new TextBlock { Text = _view.FormatPreviewViewName(preview.View), Margin = new Thickness(2, 3, 0, 0) };
                            _view.ApplyStyle(caption, "Text.Caption");
                            tile.Children.Add(caption);
                            panel.Children.Add(tile);
                            added++;
                        }
                        if (added > 0)
                        {
                            var label = new TextBlock { Text = _view.L("Preview"), Margin = new Thickness(0, 6, 0, 4) };
                            _view.ApplyStyle(label, "Text.Caption");
                            _body.Children.Add(label);
                            _body.Children.Add(panel);
                            // 卡片保持默认折叠：点开才见缩略图，对话流保持紧凑。
                        }
                    }
                    catch (System.Exception ex)
                    {
                        AiExceptionGuard.Log("Chat.ToolCardThumbnails", ex);
                    }
                });
            }
        }
    }
}
