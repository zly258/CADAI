using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CadAI.Core
{
    public partial class AiChatView
    {
        // 待发送的图片列表，支持多图水平排列
        private readonly List<PendingImageAttachment> _pendingImages = new List<PendingImageAttachment>();
        // 单次对话最多附加的图片数量
        private const int MaxPendingImages = 9;

        internal bool TryPasteImageFromClipboard()
        {
            try
            {
                if (_pendingImages.Count >= MaxPendingImages)
                {
                    AppendMessage("info", $"最多支持添加 {MaxPendingImages} 张图片。", false);
                    return false;
                }

                // 1. 优先尝试从剪贴板读取位图对象
                if (ClipboardAccess.TryGetImage(out BitmapSource source, out Exception clipboardError))
                {
                    AddNormalizedImage(source);
                    UpdateAttachmentPreviewUi();
                    return true;
                }

                if (clipboardError != null)
                {
                    AiExceptionGuard.Log("Chat.PasteImageClipboard", clipboardError);
                    AppendMessage("error", "Windows 剪贴板正被占用，请稍后再粘贴。", false);
                    return false;
                }

                // 2. 尝试从剪贴板读取复制的文件列表（例如在资源管理器复制图片/PDF后粘贴）。
                // 图片直接进入视觉通道；PDF/AutoCAD 文件作为路径附件进入建模上下文。
                if (ClipboardAccess.TryGetFileDropList(out System.Collections.Specialized.StringCollection files, out Exception _) && files != null && files.Count > 0)
                {
                    int addedImageCount = 0;
                    var addedPaths = new List<string>();
                    foreach (string file in files)
                    {
                        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                        if (IsSupportedImageFile(file))
                        {
                            if (_pendingImages.Count < MaxPendingImages && TryAddImageFromFile(file)) addedImageCount++;
                        }
                        else if (IsSupportedPathAttachment(file))
                        {
                            addedPaths.Add(file);
                        }
                    }
                    if (addedPaths.Count > 0) AddPendingPaths(addedPaths);
                    if (addedImageCount > 0 || addedPaths.Count > 0)
                    {
                        UpdateAttachmentPreviewUi();
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.PasteImage", ex);
                AppendMessage("error", ("粘贴图片失败：") + ex.Message, false);
                return false;
            }
        }

        private void AddNormalizedImage(BitmapSource source)
        {
            BitmapSource normalized = NormalizeClipboardImage(source);
            string path = Path.Combine(
                AiWorkspace.AttachmentsDirectory,
                "paste_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");

            double scale = Math.Min(1.0, 160.0 / Math.Max(normalized.PixelWidth, normalized.PixelHeight));
            BitmapSource preview = scale < 0.999
                ? new TransformedBitmap(normalized, new ScaleTransform(scale, scale))
                : normalized;
            if (preview.CanFreeze) preview.Freeze();

            using (var stream = File.Create(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(normalized));
                encoder.Save(stream);
            }
            _pendingImages.Add(new PendingImageAttachment(path, preview, isTemporaryFile: true));
        }

        internal bool TryAddImageFromFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
                if (_pendingImages.Count >= MaxPendingImages) return false;
                if (!IsSupportedImageFile(filePath)) return false;

                // 极速缩略图解码：只解码 160px 宽度，耗时 <2ms，避免全分辨率大图卡死 UI 主线程
                BitmapSource thumbnail = LoadThumbnailBitmap(filePath, 160);
                if (thumbnail == null) return false;

                // 直接使用磁盘已有图片文件，不重复做慢速 PNG 压缩转存；isTemporaryFile=false 避免清除附件时误删用户原图
                _pendingImages.Add(new PendingImageAttachment(filePath, thumbnail, isTemporaryFile: false));
                return true;
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.AddImageFile", ex);
                return false;
            }
        }

        private static BitmapSource LoadThumbnailBitmap(string path, int decodeWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.DecodePixelWidth = decodeWidth;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                image.EndInit();
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.ThumbnailBitmap", ex);
                return null;
            }
        }

        private static bool IsSupportedImageFile(string filePath)
        {
            string ext = Path.GetExtension(filePath) ?? string.Empty;
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedPathAttachment(string filePath)
        {
            string ext = Path.GetExtension(filePath) ?? string.Empty;
            return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".idw", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".iam", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".step", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".stp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPdfAttachment(string filePath)
            => string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

        private static string FormatAttachmentLine(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return "- " + path + (info.Exists ? " (" + FormatFileSize(info.Length) + ")" : "");
            }
            catch
            {
                return "- " + path;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            return (kb / 1024.0).ToString("0.##") + " MB";
        }

        private static BitmapSource NormalizeClipboardImage(BitmapSource source)
        {
            const double maxSide = 1600.0;
            double scale = Math.Min(1.0, maxSide / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource result = source;
            if (scale < 0.999) result = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            if (result.CanFreeze) result.Freeze();
            return result;
        }

        private void RemoveSingleImage(PendingImageAttachment attachment)
        {
            if (attachment == null) return;
            _pendingImages.Remove(attachment);
            attachment.DeleteFile();
            UpdateAttachmentPreviewUi();
            FocusInput();
        }

        private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
        {
            ClearPendingAttachments();
            FocusInput();
        }

        /// <summary>移除单个路径附件（文件卡的 ✕）。移除后刷新预览并收回焦点。</summary>
        private void RemoveSinglePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _pendingPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            UpdateAttachmentPreviewUi();
            FocusInput();
        }

        private void ClearPendingAttachments()
        {
            _pendingPaths.Clear();
            foreach (var img in _pendingImages)
            {
                img?.DeleteFile();
            }
            _pendingImages.Clear();
            UpdateAttachmentPreviewUi();
        }

        private void ClearPendingImage() => ClearPendingAttachments();

        private List<PendingImageAttachment> TakePendingImages()
        {
            var list = new List<PendingImageAttachment>(_pendingImages);
            _pendingImages.Clear();
            UpdateAttachmentPreviewUi();
            return list;
        }

        private void UpdateAttachmentPreviewUi()
        {
            int imgCount = _pendingImages.Count;
            int pathCount = _pendingPaths.Count;

            if (imgCount == 0 && pathCount == 0)
            {
                if (AttachmentPreviewBorder != null) AttachmentPreviewBorder.Visibility = Visibility.Collapsed;
                if (ImagePreviewsScroll != null) ImagePreviewsScroll.Visibility = Visibility.Collapsed;
                if (ImagePreviewsPanel != null) ImagePreviewsPanel.Children.Clear();
                return;
            }

            if (AttachmentPreviewBorder != null) AttachmentPreviewBorder.Visibility = Visibility.Visible;

            if (ImagePreviewsPanel != null && ImagePreviewsScroll != null)
            {
                ImagePreviewsPanel.Children.Clear();
                if (imgCount + pathCount > 0)
                {
                    ImagePreviewsScroll.Visibility = Visibility.Visible;
                    // 统一的小方形卡片流：图片卡显示缩略图，文件卡显示扩展名角标；
                    // 每张卡右上角独立"✕"移除，与常见 AI 对话一致。
                    foreach (var att in _pendingImages)
                        ImagePreviewsPanel.Children.Add(CreateImagePreviewCard(att));
                    foreach (string path in _pendingPaths)
                        ImagePreviewsPanel.Children.Add(CreateFilePreviewCard(path));
                }
                else
                {
                    ImagePreviewsScroll.Visibility = Visibility.Collapsed;
                }
            }
        }

        private FrameworkElement CreateImagePreviewCard(PendingImageAttachment att)
        {
            var border = CreateAttachmentCardShell(att.Preview != null
                ? $"{Path.GetFileName(att.FilePath)} · {att.Preview.PixelWidth}×{att.Preview.PixelHeight}"
                : Path.GetFileName(att.FilePath));

            var grid = CreateAttachmentCardContent();
            grid.Children.Add(new Image
            {
                Source = att.Preview,
                // 铺满小方块：缩略图要一眼看出是什么，留白比裁掉两侧更伤辨识度。
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true
            });
            grid.Children.Add(CreateAttachmentRemoveButton(() => RemoveSingleImage(att), "移除此图片"));
            border.Child = grid;
            return border;
        }

        /// <summary>文件附件卡：扩展名角标 + 文件名（超长截断），独立移除按钮。尺寸与图片卡一致。</summary>
        private FrameworkElement CreateFilePreviewCard(string path)
        {
            string fileName = Path.GetFileName(path) ?? path;
            string ext = (Path.GetExtension(path) ?? string.Empty).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "文件";

            var border = CreateAttachmentCardShell(path);

            var grid = CreateAttachmentCardContent();
            grid.Background = UiBrush("Brush.Panel");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var badge = new Border
            {
                Background = UiBrush("Brush.MutedPanel"),
                BorderBrush = UiBrush("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = ext,
                FontSize = ResolveMicroFontSize(),
                FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush("Brush.Text.Secondary")
            };
            stack.Children.Add(badge);
            stack.Children.Add(new TextBlock
            {
                Text = fileName,
                FontSize = ResolveMicroFontSize(),
                Margin = new Thickness(2, 4, 2, 0),
                MaxWidth = AttachmentCardSize - 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = UiBrush("Brush.Text.Secondary")
            });
            grid.Children.Add(stack);
            grid.Children.Add(CreateAttachmentRemoveButton(() => RemoveSinglePath(path), "移除此文件"));
            border.Child = grid;
            return border;
        }

        /// <summary>附件卡边长。用户反馈 40px 的缩略图太小、看不清内容也点不准，放大到 60。</summary>
        private const double AttachmentCardSize = 60;

        /// <summary>
        /// 附件卡圆角。取 12 而不是卡片容器的 4：一是缩略图更像"图片"而不是小方块，
        /// 二是移除钮（16px、内缩 4px）的圆角弧心正好与卡片圆角弧心重合，按钮整个落在圆角以内。
        /// </summary>
        private const double AttachmentCardRadius = 12;

        /// <summary>
        /// 附件卡内容层：自带圆角矩形 Clip。Border 的 CornerRadius 是否裁切子元素在各版本 WPF 上
        /// 并不一致，靠它不如自己裁——缩略图的圆角必须是确定的（那正是"图片卡"观感的来源）。
        /// 右上角的移除钮内缩 4px，落在圆角以内，不会被这里裁掉。
        /// </summary>
        private static Grid CreateAttachmentCardContent()
        {
            double inner = AttachmentCardSize - 2;   // 扣掉左右、上下各 1px 边框
            return new Grid
            {
                Clip = new RectangleGeometry(new Rect(0, 0, inner, inner), AttachmentCardRadius, AttachmentCardRadius)
            };
        }

        /// <summary>附件卡统一外壳：60×60 方形、圆角 12、细边框，缩略图按圆角裁切。</summary>
        private Border CreateAttachmentCardShell(string tooltip)
        {
            return new Border
            {
                Width = AttachmentCardSize,
                Height = AttachmentCardSize,
                Margin = new Thickness(0, 0, 8, 0),
                BorderBrush = UiBrush("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AttachmentCardRadius),
                Background = UiBrush("Brush.WindowBackground"),
                ClipToBounds = true,
                SnapsToDevicePixels = true,
                ToolTip = tooltip
            };
        }

        /// <summary>
        /// 附件卡右上角的移除钮。外观全部在主题里（Buttons.xaml 的 Button.RemoveChip）：
        /// 16px 圆形浮层、半透明白底 + 深色 Material「close」图标、悬停转红底白图标。
        /// </summary>
        private Button CreateAttachmentRemoveButton(Action onRemove, string tooltip)
        {
            var button = new Button
            {
                Content = MaterialSymbols.CreateIcon(MaterialSymbols.Close, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                // 内缩 4：既离开缩略图边缘，又整体落在卡片的 12px 圆角以内，不被圆角裁切。
                Margin = new Thickness(0, 4, 4, 0),
                ToolTip = tooltip
            };
            ApplyStyle(button, "Button.RemoveChip");
            button.Click += (s, e) => onRemove();
            return button;
        }

        /// <summary>用户气泡折叠阈值：超过任一条就走"预览 + 折叠全文"。
        /// 触发场景：PDF 抽取文本整段拼进消息（一次可塞 2 万字符）、粘贴长日志等。</summary>
        private const int UserBubbleCollapseChars = 800;
        private const int UserBubbleCollapseLines = 15;
        private const int UserBubblePreviewLines = 12;

        /// <summary>把超长用户文本切成"预览 + 折叠全文"。不超阈值时原样返回 null 折叠区。</summary>
        private FrameworkElement CreateUserBubbleBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            bool tooLong = text.Length > UserBubbleCollapseChars
                || CountLines(text) > UserBubbleCollapseLines;
            if (!tooLong) return CreateBodyText(text);

            string preview = BuildUserBubblePreview(text);
            var panel = new StackPanel();
            panel.Children.Add(CreateBodyText(preview));

            string header = $"显示全部（共 {text.Length:N0} 字符）";
            var expander = new Expander
            {
                Header = new TextBlock { Text = header },
                Content = CreateBodyText(text),
                IsExpanded = false,
                Margin = new Thickness(0, 4, 0, 0)
            };
            ApplyStyle(expander, "Expander.Subtle");
            expander.Expanded += (s, e) => ScrollToBottom();
            expander.Collapsed += (s, e) => ScrollToBottom();
            panel.Children.Add(expander);
            return panel;
        }

        private static int CountLines(string text)
        {
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lines++;
            return lines;
        }

        private static string BuildUserBubblePreview(string text)
        {
            // 按行取前 N 行，同时限最大字符数，两道保险防"单行超长"。
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int take = Math.Min(lines.Length, UserBubblePreviewLines);
            var previewBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < take; i++)
            {
                if (previewBuilder.Length > 0) previewBuilder.Append('\n');
                string line = lines[i];
                if (line.Length > 200) line = line.Substring(0, 200) + "…";
                previewBuilder.Append(line);
            }
            int hiddenLines = lines.Length - take;
            previewBuilder.Append('\n').Append("…[已折叠")
                .Append(hiddenLines > 0 ? $"，还有 {hiddenLines} 行" : string.Empty)
                .Append("，点击下方「显示全部」查看原文]");
            return previewBuilder.ToString();
        }

        /// <summary>
        /// 用户气泡。text 是**存进历史、发给模型的原文**——它可能带着给模型看的注入段
        /// （[用户附件] 清单、PDF 抽取结果、图片视觉事实包装），这些都不该出现在界面上：
        /// 气泡只显示用户真正敲的那句话 + 附件名。
        /// </summary>
        private void AppendUserMessage(string text, IReadOnlyList<BitmapSource> images,
            IReadOnlyList<string> attachmentPaths = null, string diagnosticText = null)
        {
            InvokeUi(() =>
            {
                FinalizeActivePhase();
                _assistantGroup = null;
                string raw = text ?? string.Empty;
                string display = UserPromptText.ToDisplay(raw);
                int imgCount = images?.Count ?? 0;
                IReadOnlyList<string> names = attachmentPaths != null && attachmentPaths.Count > 0
                    ? attachmentPaths.Select(p => Path.GetFileName(p) ?? p)
                        .Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                    : UserPromptText.AttachmentNames(raw);
                AddDiagnostic("USER", (diagnosticText ?? raw) + (imgCount == 0 ? string.Empty : $"\n[{imgCount} image attachment(s)]"), false);
                var content = new StackPanel();
                content.Children.Add(CreateRoleLabel("user", MessageRole.User));

                if (imgCount > 0)
                {
                    var imagesPanel = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 4, 0, string.IsNullOrWhiteSpace(display) ? 0 : 8)
                    };
                    foreach (var img in images)
                    {
                        var card = new Border
                        {
                            BorderBrush = (Brush)FindResource("Brush.Border"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Margin = new Thickness(0, 0, 8, 6),
                            Background = (Brush)FindResource("Brush.Panel"),
                            Padding = new Thickness(2)
                        };
                        card.Child = new Image
                        {
                            Source = img,
                            MaxWidth = imgCount == 1 ? 360 : 200,
                            MaxHeight = imgCount == 1 ? 220 : 140,
                            Stretch = Stretch.Uniform,
                            ToolTip = $"{img.PixelWidth} × {img.PixelHeight}"
                        };
                        imagesPanel.Children.Add(card);
                    }
                    content.Children.Add(imagesPanel);
                }

                if (names.Count > 0) content.Children.Add(CreateAttachmentNamesPanel(names));

                if (!string.IsNullOrWhiteSpace(display))
                {
                    var body = CreateUserBubbleBody(display);
                    if (body != null) content.Children.Add(body);
                }
                AddMessageCard(CreateMessageCard(MessageRole.User, content));
                _autoScrollEnabled = true;
                UpdateBackToBottomButton();
                ScrollToBottom();
            });
        }

        private FrameworkElement CreateAttachmentNamesPanel(IReadOnlyList<string> names)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            foreach (string name in names) panel.Children.Add(CreateAttachmentNameChip(name));
            return panel;
        }

        /// <summary>附件名胶囊：扩展名角标 + 文件名（超长截断），与输入区文件卡同一套视觉语言。</summary>
        private FrameworkElement CreateAttachmentNameChip(string fileName)
        {
            string ext = (Path.GetExtension(fileName) ?? string.Empty).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "文件";

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border
            {
                Background = UiBrush("Brush.MutedPanel"),
                BorderBrush = UiBrush("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = ext,
                FontSize = ResolveMicroFontSize(),
                FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush("Brush.Text.Secondary")
            };
            row.Children.Add(badge);
            row.Children.Add(new TextBlock
            {
                Text = fileName,
                Margin = new Thickness(6, 0, 0, 0),
                MaxWidth = 240,
                FontSize = ResolveMicroFontSize(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiBrush("Brush.Text.Secondary")
            });

            return new Border
            {
                Background = UiBrush("Brush.Panel"),
                BorderBrush = UiBrush("Brush.Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = fileName,
                Child = row
            };
        }

        private static string BuildPromptWithPathAttachments(string userText, IReadOnlyList<string> paths)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(userText)) builder.AppendLine(userText.Trim());
            builder.AppendLine();
            builder.AppendLine("[用户附件]");
            builder.AppendLine("用户已把下列文件作为本轮输入。把它们当作任务证据，不要只把路径当普通文本；如果是 PDF 图纸，先使用下面抽取到的可读文字/尺寸线索，再结合用户请求建模或出图；如果抽取不到文字，明确说明需要可读截图/OCR 或人工补充关键尺寸，不能猜尺寸。");
            foreach (string path in paths)
                builder.AppendLine(FormatAttachmentLine(path));

            foreach (string pdf in paths.Where(IsPdfAttachment))
            {
                PdfTextExtractionResult extraction = PdfTextExtractor.Extract(pdf);
                builder.AppendLine();
                builder.AppendLine("[PDF 文本抽取: " + pdf + "]");
                if (extraction.PageCount > 0) builder.AppendLine("页数估计: " + extraction.PageCount);
                if (!string.IsNullOrWhiteSpace(extraction.Warning)) builder.AppendLine("提示: " + extraction.Warning);
                if (!string.IsNullOrWhiteSpace(extraction.Text))
                {
                    builder.AppendLine("```text");
                    builder.AppendLine(extraction.Text);
                    builder.AppendLine("```");
                }
                else
                {
                    builder.AppendLine("未抽取到可读文本。该 PDF 可能是扫描件或图片型图纸；不要从空白文本中猜测尺寸。");
                }
            }
            return builder.ToString().Trim();
        }

        internal async Task SendUnifiedWithAttachmentsAsync()
        {
            if (IsTaskRunning)
            {
                EnqueueCurrentInput();
                return;
            }

            string userText = (InputBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(userText) && _pendingImages.Count == 0 && _pendingPaths.Count == 0) return;

            var pathAttachments = new List<string>(_pendingPaths);
            _pendingPaths.Clear();
            List<PendingImageAttachment> attachments = TakePendingImages();
            InputBox.Clear();
            UpdateAttachmentPreviewUi();

            await ExecuteTurnCoreAsync(userText, pathAttachments, attachments);
        }

        private async Task ExecuteTurnCoreAsync(string userText, List<string> pathAttachments, List<PendingImageAttachment> attachments)
        {
            if (_turnCancellation != null) return;

            pathAttachments = pathAttachments ?? new List<string>();
            attachments = attachments ?? new List<PendingImageAttachment>();

            string text = pathAttachments.Count > 0
                ? BuildPromptWithPathAttachments(userText, pathAttachments)
                : userText;

            AppendUserMessage(userText, attachments.Select(a => a.Preview).ToList(), pathAttachments, text);
            ResumeAutoScroll();
            SetBusy(true, L("Busy"));
            _toolCards.Clear();
            _turnCancellation = new CancellationTokenSource();
            StartTurnMetrics();

            try
            {
                string taskText = await BuildPromptWithImagesAsync(text, attachments, _turnCancellation.Token);
                if (_session == null)
                {
                    SetStatus(StatusKind.Connecting);
                    _session = new AgentSession(_settings, ChatCoreHost.CreateToolExecutor());
                    SetStatus(StatusKind.Busy, L("Busy"));
                }
                if (_pendingHistoryMessages != null)
                {
                    _session.RestoreHistory(_pendingHistoryMessages);
                    _pendingHistoryMessages = null;
                }
                await _session.RunTurnAsync(taskText, this, _turnCancellation.Token);
                FinalizeActivePhase();
                SetStatus(StatusKind.Done);
                try { ConversationUpdated?.Invoke(this, EventArgs.Empty); } catch { }

                if (_session.BudgetExhausted)
                {
                    AppendMessage(
                        "system",
                        "本段对话已用尽工具调用预算。点击「新建」后发送剩余任务即可在新对话中继续——原上下文已存入历史记录。",
                        false);
                    _queuePaused = true;
                }
            }
            catch (OperationCanceledException)
            {
                FinalizeActivePhase();
                AppendMessage("system", "已停止。当前 " + ChatCoreHostConfig.HostProductNameOrDefault() + " 状态和上下文保留。", false);
                SetStatus(StatusKind.Stopped);
                _queuePaused = true;
            }
            catch (Exception ex) when (_turnCancellation != null && _turnCancellation.IsCancellationRequested)
            {
                FinalizeActivePhase();
                AddDiagnostic("STOP TEARDOWN", ex.ToString(), true);
                AppendMessage("system", "已停止。当前 " + ChatCoreHostConfig.HostProductNameOrDefault() + " 状态和上下文保留。", false);
                SetStatus(StatusKind.Stopped);
                _queuePaused = true;
            }
            catch (Exception ex)
            {
                FinalizeActivePhase();
                AddDiagnostic("ERROR DETAIL", ex.ToString(), true);
                AiExceptionGuard.Log("Chat.Turn", ex);
                AppendMessage("错误", AiErrorFormatter.ToUserMessage(ex, _settings?.BaseUrl), false);
                SetStatus(StatusKind.Failed);
                _queuePaused = true;
            }
            finally
            {
                StopTurnMetrics();
                _turnCancellation?.Dispose();
                _turnCancellation = null;
                SetBusy(false);
                FocusInput();

                // 正常完成（未暂停且队列非空）时自动继续分发下一个排队项
                if (!_queuePaused && _messageQueue.Count > 0)
                {
                    _ = Dispatcher.InvokeAsync(CheckAndDispatchNextQueueItemAsync);
                }
                else
                {
                    RefreshQueueUi();
                }
            }
        }

        private async Task<string> BuildPromptWithImagesAsync(string text, IReadOnlyList<PendingImageAttachment> attachments, CancellationToken ct)
        {
            if (attachments == null || attachments.Count == 0) return text;
            int count = attachments.Count;
            // 这段文本既显示给用户、也作为 user 消息发给模型，所以宿主名必须走配置。
            // 写死 "AutoCAD 任务" 会让 AutoCAD 用户看到（并让模型以为）一个错误的宿主。
            string hostName = ChatCoreHostConfig.HostProductNameOrDefault();
            string userText = string.IsNullOrWhiteSpace(text)
                ? (count == 1
                    ? "请分析这张图片，并结合当前 " + hostName + " 任务继续处理，只使用可靠的视觉事实。"
                    : $"请按顺序分析这 {count} 张图片，并结合当前 {hostName} 任务继续处理，只使用可靠的视觉事实。")
                : text;
            try
            {
                using (var client = new OpenAiCompatibleClient(_settings))
                {
                    string languageRule = ImageFactLanguageRule(_settings?.ResponseLanguage, userText);
                    string productName = ResolveProductDisplayName();
                    string visionPrompt = (count == 1
                        ? "Analyze the image pasted into the " + productName + " chat. "
                        : $"Analyze the {count} images pasted into the {productName} chat in sequential order (Image 1 to Image {count}). ") +
                        ResolveImageFactPromptHint() +
                        "Do not guess unreadable content. " + languageRule + "\nOriginal user request:\n" + userText;

                    var imagePaths = attachments.Select(a => a.FilePath).ToList();
                    AgentImageReview review = await client.ReviewImagesAsync(imagePaths, visionPrompt, (attempt, message) => { }, ct);
                    AppendUsage("pasted-image", review.Usage);
                    if (!string.IsNullOrWhiteSpace(review.Content))
                        return "[Original user request — use this section to determine response language]\n" + userText +
                               "\n\n[Internal image visual facts — do not use this section to choose response language]\n" + review.Content;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AiExceptionGuard.Log("Chat.PastedImageVision", ex);
                AppendMessage("warning", "图片视觉提取未成功：" + ex.Message, false);
            }
            return "[Original user request — use this section to determine response language]\n" + userText +
                   "\n\n[Internal image visual facts]\nNo reliable visual facts were returned. Do not guess the image contents.";
        }

        private static string ImageFactLanguageRule(string responseLanguage, string originalUserText)
        {
            // 回答语言只有中文/英文两档（Normalize 已把历史 auto 归一到中文）。
            string language = AiSettings.NormalizeResponseLanguage(responseLanguage);
            if (string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase))
                return "Return the extracted visual facts in English.";
            return "Return the extracted visual facts in Simplified Chinese.";
        }

        /// <summary>附件提示词里的产品名（宿主可换；默认 CADAI）。</summary>
        private static string ResolveProductDisplayName()
        {
            try
            {
                string name = ChatCoreHostConfig.Current.ProductDisplayName;
                return string.IsNullOrWhiteSpace(name) ? "CADAI" : name;
            }
            catch { return "CADAI"; }
        }

        /// <summary>"只提取哪些视觉事实"的领域提示（宿主可换；默认 AutoCAD 口径）。</summary>
        private static string ResolveImageFactPromptHint()
        {
            try
            {
                string hint = ChatCoreHostConfig.Current.ImageFactPromptHint;
                if (!string.IsNullOrWhiteSpace(hint)) return hint;
            }
            catch { }

            return "Extract only visual facts relevant to the current AutoCAD task: "
                + "model geometry, drawing views, dimensions/annotations, assembly relationships, error messages, or visible UI state. ";
        }

        internal sealed class PendingImageAttachment
        {
            public PendingImageAttachment(string filePath, BitmapSource preview, bool isTemporaryFile = true)
            {
                FilePath = filePath;
                Preview = preview;
                IsTemporaryFile = isTemporaryFile;
            }

            public string FilePath { get; }
            public BitmapSource Preview { get; }
            public bool IsTemporaryFile { get; }

            public void DeleteFile()
            {
                // 仅删除剪贴板粘贴生成的临时文件，绝不能删除用户从本地磁盘添加的原图！
                if (!IsTemporaryFile) return;
                try
                {
                    if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
                        File.Delete(FilePath);
                }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("Chat.DeletePastedImage", ex);
                }
            }
        }
    }
}
