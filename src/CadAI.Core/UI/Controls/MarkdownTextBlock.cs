using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MdEngine = MdXaml.Markdown;

namespace CadAI.Core
{
    /// <summary>
    /// 承载可选中 Markdown 的宿主控件，底层用 <see cref="FlowDocumentScrollViewer"/>。
    ///
    /// Markdown 的解析与排版交给第三方组件 MdXaml，本类只做两件事：把控件自身的字体 / 字号 /
    /// 前景色注入 MdXaml 的样式链，以及把生成结果重新贴合工业风浅色主题。这样对话回复可以保留
    /// 真实结构（标题、列表、表格、代码块、链接），同时不会引入第三方组件自带的配色。
    /// 拖选复制与流式渲染节流（180 ms）保持原有行为。
    /// </summary>
    internal sealed class MarkdownTextBlock : ContentControl
    {
        /// <summary>最小行高，避免小字号下行距过挤。</summary>
        private const double MinLineHeight = 18;

        /// <summary>各级标题相对正文字号的增量，沿用原有工业风层级（标题只用字号区分，不加粗）。</summary>
        private static readonly double[] HeadingOffset = { 5, 4, 3, 2, 1, 0 };

        // 主题资源不可用时的兜底配色。取色顺序是「控件资源作用域 → ThemeResources 静态主题字典」，
        // 只有两者都失败（几乎只在单元测试 / 设计器里发生）才会用到这些字面量；
        // 值仍与 Colors.xaml 的同名键保持一致，改主题色时同步改这里：
        // FallbackText→Color.TextPrimary、FallbackSecondary→Color.TextSecondary、
        // FallbackBorder→Color.Border、FallbackCodeBackground/MutedPanel→Color.CodeBackground/Color.MutedPanel、
        // FallbackAccent→Color.Accent。
        private static readonly Brush FallbackText = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5));
        private static readonly Brush FallbackSecondary = new SolidColorBrush(Color.FromRgb(0xB0, 0xB7, 0xC3));
        private static readonly Brush FallbackBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4F, 0x5A));
        private static readonly Brush FallbackCodeBackground = new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x26));
        private static readonly Brush FallbackMutedPanel = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x42));
        private static readonly Brush FallbackAccent = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));

        private readonly FlowDocumentScrollViewer _viewer;
        private readonly DispatcherTimer _renderTimer;
        private readonly MenuItem _copyMenuItem;
        private readonly MenuItem _selectAllMenuItem;

        private string _markdown = string.Empty;
        private string _lastRenderedMarkdown;

        /// <summary>当前主题快照（同时持有按该快照构建的 MdXaml 引擎）。</summary>
        private MarkdownTheme _theme;

        /// <summary>主题快照的版本号；重建后会变化，用于判断是否需要重新渲染相同文本。</summary>
        private int _themeVersion;

        /// <summary>最近一次真正渲染时使用的主题版本。</summary>
        private int _renderedThemeVersion = -1;

        public MarkdownTextBlock()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Top;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            MinWidth = 0;

            _viewer = new FlowDocumentScrollViewer
            {
                IsSelectionEnabled = true,
                IsToolBarVisible = false,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var menu = new ContextMenu();
            _copyMenuItem = new MenuItem { Header = "复制" };
            _copyMenuItem.Click += (sender, args) => CopySelection();
            _viewer.CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Copy,
                CopyCommand_Executed,
                CopyCommand_CanExecute));
            // 在命令隧道阶段拦截 Copy：WPF 默认的 TextEditor 复制会把选区序列化成
            // Xaml / XamlPackage 等多种格式，长回复下光是生成这些格式就要数秒。
            // 统一改走纯文本后台复制，Ctrl+C 与右键菜单行为一致。
            CommandManager.AddPreviewExecutedHandler(_viewer, PreviewCopyExecuted);
            _selectAllMenuItem = new MenuItem { Header = "全选" };
            _selectAllMenuItem.Click += (sender, args) =>
            {
                if (_viewer.Document != null && _viewer.Selection != null)
                    _viewer.Selection.Select(_viewer.Document.ContentStart, _viewer.Document.ContentEnd);
            };
            menu.Items.Add(_copyMenuItem);
            menu.Items.Add(_selectAllMenuItem);
            _viewer.ContextMenu = menu;

            Content = _viewer;
            _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _renderTimer.Tick += RenderTimer_Tick;
            Unloaded += MarkdownTextBlock_Unloaded;
            // 主题资源只有在控件进入可视化树之后才解析得到，加载完成后补一次渲染以套用真实配色。
            Loaded += MarkdownTextBlock_Loaded;
        }

        private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _viewer.Selection != null && !_viewer.Selection.IsEmpty;
            e.Handled = true;
        }

        private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            CopySelection();
            e.Handled = true;
        }

        private void PreviewCopyExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Copy) return;
            CopySelection();
            e.Handled = true;
        }

        private void CopySelection()
        {
            if (_viewer.Selection == null || _viewer.Selection.IsEmpty) return;
            string text = _viewer.Selection.Text ?? string.Empty;
            // 剪贴板被占用时 SetText 会内部重试约 1 秒；后台线程写入，避免复制卡住界面。
            ClipboardAccess.SetTextInBackground(text, "Markdown.CopyClipboard");
        }

        /// <summary>置为 true 时不做 Markdown 解析，直接以纯文本呈现（用于日志、诊断等机器可读输出）。</summary>
        public bool PlainTextMode { get; set; }

        public string Markdown
        {
            get => _markdown;
            set
            {
                _markdown = value ?? string.Empty;
                ScheduleRender();
            }
        }

        public void Flush()
        {
            _renderTimer?.Stop();
            Render();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            // 字体 / 字号 / 前景色变化会影响 MdXaml 的样式链，需要按新主题重排已有内容。
            if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == ForegroundProperty)
                ScheduleRender();
        }

        private void ScheduleRender()
        {
            if (_renderTimer == null) return;

            // 首次内容立即渲染，不走去抖：流式输出时用户感知的"开始出字"延迟
            // 主要来自这段等待，先渲染一次让第一个字马上可见。
            // 之后的增量仍按定时器合帧，避免长文本反复全量重排。
            if (_lastRenderedMarkdown == null && !string.IsNullOrEmpty(_markdown))
            {
                Render();
                return;
            }

            if (!_renderTimer.IsEnabled) _renderTimer.Start();
        }

        private void RenderTimer_Tick(object sender, EventArgs e)
        {
            _renderTimer.Stop();
            Render();
        }

        private void MarkdownTextBlock_Loaded(object sender, RoutedEventArgs e) => ScheduleRender();

        private void MarkdownTextBlock_Unloaded(object sender, RoutedEventArgs e) => _renderTimer.Stop();

        private void Render()
        {
            // 替换 FlowDocument 会让 TextPointer 与选区失效。流式输出期间
            // 要保住用户正在拖选的选区，等选区清空后再渲染最新 Markdown。
            if (_viewer.Selection != null && !_viewer.Selection.IsEmpty)
            {
                ScheduleRender();
                return;
            }

            string next = _markdown ?? string.Empty;
            MarkdownTheme theme = ResolveTheme();
            if (_viewer.Document != null
                && string.Equals(next, _lastRenderedMarkdown, StringComparison.Ordinal)
                && _renderedThemeVersion == _themeVersion)
                return;

            _viewer.Document = PlainTextMode
                ? BuildPlainDocument(next, theme)
                : BuildMarkdownDocument(next, theme);
            _lastRenderedMarkdown = next;
            _renderedThemeVersion = _themeVersion;
        }

        /// <summary>
        /// 解析当前生效的字体与配色。结果被缓存；任一输入变化时重建主题快照并递增版本号，
        /// 从而让已经渲染过的文本按新主题重新排版。
        /// </summary>
        private MarkdownTheme ResolveTheme()
        {
            // 字体族 / 字号 / 配色一律**优先取主题**，而且必须做到"控件还没挂进可视化树也能取到"。
            //
            // 2026-10-14 用户反馈："打开先显示的文字比较小，然后改成了大文字"。
            //
            // 成因：关于窗口的简介、对话窗口的欢迎卡，都是在**构造阶段**就把 Markdown 赋给本控件；
            // Markdown 的 setter 会立刻渲染首帧，而那一刻控件还没挂进窗口，资源作用域里什么都没有
            // （主题是按窗口/按视图挂载的，宿主没有应用级字典）。于是首帧只能用 WPF 默认字号 12
            // 与系统字体画一遍；等控件进入可视化树、主题可达，再按 FontSize.Body 重排一次
            // —— 这就是肉眼可见的"先小后大"。
            //
            // 修法：取值的顺序是「本控件的资源作用域（窗口/视图可覆盖主题）→ 静态主题字典
            // （ThemeResources，见 UI/Themes/ThemeResources.cs）」，首帧就能拿到最终值。
            // 刻意**不**用"未 Loaded 就不渲染"那种时序控制——那条路会改变渲染时机，
            // 曾导致窗口起不来（已回退），风险远大于收益。
            var family = ThemeValue("Font.Ui") as FontFamily ?? this.FontFamily;
            if (family == null) family = SystemFonts.MessageFontFamily;

            double size = 0;
            if (ThemeValue("FontSize.Body") is double themedSize && themedSize > 0) size = themedSize;
            if (size <= 0) size = FontSize > 0 ? FontSize : 12;
            Brush foreground = ResolveForeground();
            Brush secondary = ResolveBrush("Brush.Text.Secondary", FallbackSecondary);
            Brush border = ResolveBrush("Brush.Border", FallbackBorder);
            Brush codeBackground = ResolveBrush("Brush.Code.Background", FallbackCodeBackground);
            Brush mutedPanel = ResolveBrush("Brush.MutedPanel", FallbackMutedPanel);
            Brush accent = ResolveBrush("Brush.Accent", FallbackAccent);

            MarkdownTheme current = _theme;
            if (current != null
                && SameFamily(current.Family, family)
                && Math.Abs(current.Size - size) < 0.01
                && SameBrush(current.Foreground, foreground)
                && SameBrush(current.Secondary, secondary)
                && SameBrush(current.Border, border)
                && SameBrush(current.CodeBackground, codeBackground)
                && SameBrush(current.MutedPanel, mutedPanel)
                && SameBrush(current.Accent, accent))
                return current;

            var rebuilt = new MarkdownTheme
            {
                Family = family,
                Size = size,
                Foreground = foreground,
                Secondary = secondary,
                Border = border,
                CodeBackground = codeBackground,
                MutedPanel = mutedPanel,
                Accent = accent
            };
            rebuilt.Engine = CreateEngine(rebuilt);
            _theme = rebuilt;
            _themeVersion++;
            return rebuilt;
        }

        /// <summary>
        /// 正文颜色。注意 <see cref="Control.Foreground"/> 的默认值是**纯黑**
        /// （<c>SystemColors.ControlTextBrush</c>）而不是 null：控件还没挂进窗口时读到的就是它。
        /// 直接拿去画首帧，等窗口样式（Window.Base 的 Brush.Text.Primary）生效后就会再重排成
        /// #1F2933 —— 又是一次肉眼可见的闪动（实测真实 AboutWindow：主题版本 1 → 2）。
        ///
        /// 所以用 <see cref="DependencyPropertyHelper"/> 区分"这是元数据默认值"还是"真的有人设过 /
        /// 真的继承到了"：只有后者才用控件自己的 Foreground，默认值一律走主题。
        /// </summary>
        private Brush ResolveForeground()
        {
            try
            {
                ValueSource source = DependencyPropertyHelper.GetValueSource(this, ForegroundProperty);
                if (source.BaseValueSource != BaseValueSource.Default)
                    return Foreground ?? FallbackText;
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Markdown.ForegroundSource", error);
            }
            return ThemeValue("Brush.Text.Primary") as Brush ?? FallbackText;
        }

        /// <summary>
        /// 取一个主题值：先查本控件所在的资源作用域（窗口/视图可以覆盖主题），查不到再退到
        /// 静态主题字典。后者保证控件**尚未挂进可视化树**时也能拿到最终字号 / 字体 / 配色，
        /// 首帧即终帧，不会出现"先小后大"。
        /// </summary>
        private object ThemeValue(string key)
        {
            try
            {
                object fromScope = TryFindResource(key);
                if (fromScope != null) return fromScope;
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Markdown.Theme." + key, error);
            }
            return ThemeResources.Find(key);
        }

        /// <summary>
        /// 字体族按名称比较：控件挂进树前拿到的是静态字典里的实例、挂进树后是窗口字典里的实例，
        /// 名称相同就该视为"主题没变"，否则会白白重排一次。
        /// </summary>
        private static bool SameFamily(FontFamily left, FontFamily right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 画刷按颜色比较。Brush 不重写 Equals（等于引用比较），而"挂进树前"用的是静态字典里的画刷、
        /// "挂进树后"用的是窗口字典里的同色画刷；若按引用比较会被判成"主题变了"，
        /// 于是替换一次 FlowDocument——虽然字号颜色都一样，但那一下仍然是一次闪动。
        /// </summary>
        private static bool SameBrush(Brush left, Brush right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left is SolidColorBrush leftSolid && right is SolidColorBrush rightSolid)
                return leftSolid.Color == rightSolid.Color;
            return Equals(left, right);
        }

        /// <summary>从主题中取画刷，取不到时使用本地兜底色，避免渲染中断。</summary>
        private Brush ResolveBrush(string key, Brush fallback) => ThemeValue(key) as Brush ?? fallback;

        #region MdXaml 引擎与样式

        /// <summary>
        /// 用主题值构建 MdXaml 引擎。MdXaml 只有在宿主提供了样式名对应的 <see cref="Style"/> 时才
        /// 会排版对应元素，因此这里显式补齐文档、标题、段落、引用与行内代码的样式。
        /// </summary>
        private static MdEngine CreateEngine(MarkdownTheme theme)
        {
            var engine = new MdEngine
            {
                // MdXaml 会为代码块内嵌一个可聚焦的编辑器，气泡里不需要它自带的右键菜单。
                DisabledContextMenu = true,
                DocumentStyle = BuildDocumentStyle(theme),
                NormalParagraphStyle = BuildParagraphStyle(theme),
                BlockquoteStyle = BuildBlockquoteStyle(theme),
                CodeStyle = BuildInlineCodeStyle(theme)
            };

            Style[] headings =
            {
                BuildHeadingStyle(0, theme),
                BuildHeadingStyle(1, theme),
                BuildHeadingStyle(2, theme),
                BuildHeadingStyle(3, theme),
                BuildHeadingStyle(4, theme),
                BuildHeadingStyle(5, theme)
            };
            engine.Heading1Style = headings[0];
            engine.Heading2Style = headings[1];
            engine.Heading3Style = headings[2];
            engine.Heading4Style = headings[3];
            engine.Heading5Style = headings[4];
            engine.Heading6Style = headings[5];
            return engine;
        }

        private static Style BuildDocumentStyle(MarkdownTheme theme)
        {
            var style = new Style(typeof(FlowDocument));
            style.Setters.Add(new Setter(FlowDocument.FontFamilyProperty, theme.Family));
            style.Setters.Add(new Setter(FlowDocument.FontSizeProperty, theme.Size));
            style.Setters.Add(new Setter(FlowDocument.ForegroundProperty, theme.Foreground));
            style.Setters.Add(new Setter(FlowDocument.PagePaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FlowDocument.ColumnWidthProperty, double.PositiveInfinity));
            return style;
        }

        private static Style BuildHeadingStyle(int index, MarkdownTheme theme)
        {
            double size = theme.Size + HeadingOffset[index];
            var style = new Style(typeof(Paragraph));
            style.Setters.Add(new Setter(TextElement.FontSizeProperty, size));
            // 工业风：标题只靠字号区分层级，不人为加粗。
            style.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.Normal));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, theme.Foreground));
            style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 5, 0, 5)));
            style.Setters.Add(new Setter(Block.LineHeightProperty, Math.Max(MinLineHeight, size + 6)));
            style.Setters.Add(new Setter(Block.TextAlignmentProperty, TextAlignment.Left));
            return style;
        }

        private static Style BuildParagraphStyle(MarkdownTheme theme)
        {
            var style = new Style(typeof(Paragraph));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, theme.Foreground));
            style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 0, 0, 6)));
            style.Setters.Add(new Setter(Block.LineHeightProperty, Math.Max(MinLineHeight, theme.Size + 6)));
            style.Setters.Add(new Setter(Block.TextAlignmentProperty, TextAlignment.Left));
            return style;
        }

        private static Style BuildBlockquoteStyle(MarkdownTheme theme)
        {
            var style = new Style(typeof(Section));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, theme.Secondary));
            style.Setters.Add(new Setter(Block.BorderBrushProperty, theme.Border));
            style.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
            style.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(9, 1, 0, 1)));
            style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 3, 0, 7)));
            return style;
        }

        private static Style BuildInlineCodeStyle(MarkdownTheme theme)
        {
            var style = new Style(typeof(Run));
            // 统一 UI 字体；行内代码通过背景色区分，而不是等宽字体。
            style.Setters.Add(new Setter(TextElement.FontFamilyProperty, theme.Family));
            style.Setters.Add(new Setter(TextElement.BackgroundProperty, theme.CodeBackground));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, theme.Foreground));
            return style;
        }

        #endregion

        #region 渲染

        private FlowDocument BuildPlainDocument(string content, MarkdownTheme theme)
        {
            var document = CreateEmptyDocument(theme);
            string clean = StripMarkdown(content);
            document.Blocks.Add(new Paragraph(new Run(clean))
            {
                Margin = new Thickness(0),
                LineHeight = Math.Max(MinLineHeight, theme.Size + 6)
            });
            return document;
        }

        private FlowDocument BuildMarkdownDocument(string markdown, MarkdownTheme theme)
        {
            FlowDocument document;
            try
            {
                // LaTeX → Unicode 预处理：MdXaml 1.27 不暴露 Markdig pipeline，
                // 无法挂 Mathematics 扩展，因此在交给它之前先把 $...$ / $$...$$ 转成文本。
                // 该转换保证不丢内容、不抛异常（见 LatexText 的说明）。
                string prepared = LatexText.Normalize(markdown ?? string.Empty);
                document = theme.Engine.Transform(prepared);
            }
            catch (Exception error)
            {
                // 第三方解析器异常不应打断对话：退回纯文本呈现并记录日志。
                AiExceptionGuard.Log("Markdown.Transform", error);
                return BuildPlainDocument(markdown, theme);
            }

            if (document == null) return BuildPlainDocument(markdown, theme);

            document.PagePadding = new Thickness(0);
            document.ColumnWidth = double.PositiveInfinity;
            document.FontFamily = theme.Family;
            document.FontSize = theme.Size;
            document.Foreground = theme.Foreground;
            ApplyTheme(document.Blocks, theme);
            if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            return document;
        }

        private static FlowDocument CreateEmptyDocument(MarkdownTheme theme)
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                FontFamily = theme.Family,
                FontSize = theme.Size,
                Foreground = theme.Foreground
            };
        }

        #endregion

        #region 主题化（把 MdXaml 的输出重新贴合工业风浅色主题）

        /// <summary>
        /// MdXaml 只负责结构排版，表格边框、分隔线、代码块底色等观感仍由本工程决定，
        /// 这里对生成的块集合做一次深度重写。
        ///
        /// 只有真正发生替换（围栏代码块、分隔线）时才重排集合：清空 <see cref="ListItem"/> 的
        /// 块会让 WPF 把它从所属列表中摘除，正在枚举的上级集合会因此失效。
        /// </summary>
        private static void ApplyTheme(BlockCollection blocks, MarkdownTheme theme)
        {
            if (blocks == null || blocks.Count == 0) return;

            // 先取快照：重写过程中可能替换块，不能在枚举集合的同时改动它。
            var snapshot = new List<Block>(blocks.Count);
            foreach (Block block in blocks) snapshot.Add(block);

            var replacements = new List<Block>(snapshot.Count);
            bool replaced = false;
            foreach (Block block in snapshot)
            {
                Block themed = ThemeBlock(block, theme);
                replacements.Add(themed);
                if (!ReferenceEquals(themed, block)) replaced = true;
            }

            if (!replaced) return;

            blocks.Clear();
            foreach (Block block in replacements) blocks.Add(block);
        }

        private static Block ThemeBlock(Block block, MarkdownTheme theme)
        {
            switch (block)
            {
                case BlockUIContainer container:
                    if (container.Child is Separator) return BuildSeparator(theme);
                    if (TryConstrainContainerImage(container.Child)) return container;
                    string code = TryReadCodeBlock(container);
                    return code != null ? BuildCodeBlock(code, theme) : (Block)container;

                case Table table:
                    ThemeTable(table, theme);
                    return table;

                case Section section:
                    ApplyTheme(section.Blocks, theme);
                    return section;

                case List list:
                    // 逐项重写时同样可能触碰列表项自身的块集合，先固定列表项快照。
                    var items = new List<ListItem>(list.ListItems.Count);
                    foreach (ListItem item in list.ListItems) items.Add(item);
                    foreach (ListItem item in items) ApplyTheme(item.Blocks, theme);
                    return list;

                case Paragraph paragraph:
                    ThemeParagraph(paragraph, theme);
                    return paragraph;

                default:
                    return block;
            }
        }

        private static void ThemeParagraph(Paragraph paragraph, MarkdownTheme theme)
        {
            // MdXaml 对没有命中样式的段落使用 NaN 边距，这里统一收敛，避免行距塌陷或异常留白。
            if (HasAutoMargin(paragraph.Margin)) paragraph.Margin = new Thickness(0, 0, 0, 6);
            if (double.IsNaN(paragraph.LineHeight))
                paragraph.LineHeight = Math.Max(MinLineHeight, theme.Size + 6);
            ThemeInlines(paragraph.Inlines, theme);
        }

        private static bool HasAutoMargin(Thickness margin)
        {
            return double.IsNaN(margin.Left) || double.IsNaN(margin.Top)
                || double.IsNaN(margin.Right) || double.IsNaN(margin.Bottom);
        }

        private static void ThemeInlines(InlineCollection inlines, MarkdownTheme theme)
        {
            if (inlines == null || inlines.Count == 0) return;

            var snapshot = new List<Inline>(inlines.Count);
            foreach (Inline inline in inlines) snapshot.Add(inline);

            var replacements = new List<Inline>(snapshot.Count);
            bool replaced = false;

            foreach (Inline inline in snapshot)
            {
                if (inline is Hyperlink link)
                {
                    link.Foreground = theme.Accent;
                    link.TextDecorations = TextDecorations.Underline;
                    link.Cursor = Cursors.Hand;
                    ThemeInlines(link.Inlines, theme);
                    replacements.Add(link);
                }
                else if (inline is Span span)
                {
                    ThemeInlines(span.Inlines, theme);
                    replacements.Add(span);
                }
                else if (inline is InlineUIContainer uiContainer)
                {
                    TryConstrainContainerImage(uiContainer.Child);
                    replacements.Add(uiContainer);
                }
                else if (inline is Run run && ContainsHtmlBreak(run.Text))
                {
                    replaced = true;
                    ExpandRunBreaks(run, replacements);
                }
                else if (inline is Run normalRun)
                {
                    if (ContainsHtmlEntities(normalRun.Text))
                        normalRun.Text = NormalizeHtmlEntities(normalRun.Text);
                    replacements.Add(normalRun);
                }
                else
                {
                    replacements.Add(inline);
                }
            }

            if (!replaced) return;

            inlines.Clear();
            foreach (Inline inline in replacements) inlines.Add(inline);
        }

        private static void ExpandRunBreaks(Run sourceRun, List<Inline> targetList)
        {
            string text = NormalizeHtmlEntities(sourceRun.Text);
            string[] parts = Regex.Split(text, @"<br\s*/?>", RegexOptions.IgnoreCase);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) targetList.Add(new LineBreak());
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    var run = new Run(parts[i]);
                    CopyRunProperties(sourceRun, run);
                    targetList.Add(run);
                }
            }
        }

        private static void CopyRunProperties(Run source, Run target)
        {
            if (source == null || target == null) return;
            target.FontFamily = source.FontFamily;
            target.FontSize = source.FontSize;
            target.FontWeight = source.FontWeight;
            target.FontStyle = source.FontStyle;
            target.Foreground = source.Foreground;
            target.Background = source.Background;
            target.TextDecorations = source.TextDecorations;
        }

        private static bool ContainsHtmlBreak(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("&nbsp;", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("&lt;", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("&gt;", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("&amp;", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text
                .Replace("&nbsp;", " ")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        private static bool TryConstrainContainerImage(UIElement element)
        {
            if (element == null) return false;
            if (element is Image image)
            {
                ConstrainImage(image);
                return true;
            }
            if (element is Border border && border.Child is Image borderImg)
            {
                ConstrainImage(borderImg);
                return true;
            }
            if (element is Panel panel)
            {
                bool found = false;
                foreach (UIElement child in panel.Children)
                {
                    if (TryConstrainContainerImage(child)) found = true;
                }
                return found;
            }
            return false;
        }

        private static void ConstrainImage(Image image)
        {
            if (image == null) return;
            image.Stretch = Stretch.Uniform;
            image.StretchDirection = StretchDirection.DownOnly;
            image.MaxWidth = 420;
            image.MaxHeight = 260;
            image.HorizontalAlignment = HorizontalAlignment.Left;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        }

        private static void ThemeTable(Table table, MarkdownTheme theme)
        {
            table.CellSpacing = 0;
            table.Margin = new Thickness(0, 2, 0, 8);
            table.BorderBrush = theme.Border;
            table.BorderThickness = new Thickness(1);

            // MdXaml 的行分组方式不固定，这里先拉平成完整行序列，表头与末行才能稳定判定。
            var rows = new List<TableRow>();
            foreach (TableRowGroup group in table.RowGroups)
                foreach (TableRow row in group.Rows) rows.Add(row);

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                TableRow row = rows[rowIndex];
                bool header = rowIndex == 0;
                bool lastRow = rowIndex == rows.Count - 1;
                if (header) row.Background = theme.MutedPanel;

                for (int cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
                {
                    TableCell cell = row.Cells[cellIndex];
                    cell.Padding = new Thickness(7, 5, 7, 5);
                    cell.BorderBrush = theme.Border;
                    cell.BorderThickness = new Thickness(
                        0,
                        0,
                        cellIndex == row.Cells.Count - 1 ? 0 : 1,
                        lastRow ? 0 : 1);
                    ApplyTheme(cell.Blocks, theme);
                }
            }
        }

        private static Block BuildSeparator(MarkdownTheme theme)
        {
            return new BlockUIContainer(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 7, 0, 10),
                Background = theme.Border,
                SnapsToDevicePixels = true
            });
        }

        private static Paragraph BuildCodeBlock(string code, MarkdownTheme theme)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 8),
                Padding = new Thickness(8, 6, 8, 6),
                Background = theme.CodeBackground,
                BorderBrush = theme.Border,
                BorderThickness = new Thickness(1),
                // 统一 UI 字体；代码块通过背景 / 内边距 / 边框区分，而不是等宽字体。
                FontFamily = theme.Family,
                FontSize = theme.Size,
                LineHeight = Math.Max(MinLineHeight, theme.Size + 5),
                TextAlignment = TextAlignment.Left
            };
            paragraph.Inlines.Add(new Run(code ?? string.Empty));
            return paragraph;
        }

        /// <summary>
        /// MdXaml 把围栏代码块渲染成内嵌的 AvalonEdit 编辑器。聊天气泡不需要一个可聚焦的编辑器，
        /// 这里取出源码文本，改用与整机风格一致的主题化代码段呈现；识别失败时保留原样。
        /// </summary>
        private static string TryReadCodeBlock(BlockUIContainer container)
        {
            // MdXaml 的代码块是内嵌 AvalonEdit TextEditor。Add-in 已引用该程序集，
            // 直接强类型取 Text 即可，不必用反射按名字找属性。
            if (container.Child is not ICSharpCode.AvalonEdit.TextEditor editor)
                return null;

            try
            {
                return editor.Text;
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Markdown.CodeBlock", error);
                return null;
            }
        }

        #endregion

        /// <summary>
        /// 清洗文本中的常见 Markdown 格式标记，转换为整洁的纯文本呈现，避免在纯文本模式下泄露 Markdown 符号。
        /// </summary>
        internal static string StripMarkdown(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return string.Empty;

            string text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

            // 去掉代码块围栏行 ```[lang]
            text = Regex.Replace(text, @"^```[^\n]*\n?", "", RegexOptions.Multiline);

            // 去掉标题前缀行 #、## 等
            text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);

            // 去掉粗体和斜体标记
            text = Regex.Replace(text, @"\*\*\*(.*?)\*\*\*", "$1");
            text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
            text = Regex.Replace(text, @"\*(.*?)\*", "$1");

            // 去掉删除线
            text = Regex.Replace(text, @"~~(.*?)~~", "$1");

            // 去掉行内代码反引号
            text = Regex.Replace(text, @"`([^`]+)`", "$1");

            // 去掉超链接语法 [title](url) -> title
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");

            // 去掉引用块符号 >
            text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);

            // 将无序列表符号规范化为整洁的圆点
            text = Regex.Replace(text, @"^[*-]\s+", "• ", RegexOptions.Multiline);

            return text;
        }

        /// <summary>一次渲染使用的字体与配色快照，以及按该快照构建的 MdXaml 引擎。</summary>
        private sealed class MarkdownTheme
        {
            public FontFamily Family;
            public double Size;
            public Brush Foreground;
            public Brush Secondary;
            public Brush Border;
            public Brush CodeBackground;
            public Brush MutedPanel;
            public Brush Accent;
            public MdEngine Engine;
        }
    }
}
