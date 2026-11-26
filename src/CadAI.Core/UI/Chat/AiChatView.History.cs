using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace CadAI.Core
{
    public partial class AiChatView
    {
        private Window _historyDialog;
        private IReadOnlyList<AgentMessage> _pendingHistoryMessages;
        private StackPanel HistoryItemsPanel = new StackPanel();
        private TextBlock HistoryTitleTextBlock = new TextBlock();
        private Button HistoryFolderButton = new Button();
        private CheckBox _selectAllHistoryCheckBox;
        private Button _batchDeleteButton;
        private readonly HashSet<string> _selectedHistoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HistoryItem> _currentHistoryItems = new List<HistoryItem>();
        private bool _isUpdatingBatchState;

        private void History_Click(object sender, RoutedEventArgs e)
        {
            if (_historyDialog != null) { _historyDialog.Activate(); return; }
            _selectedHistoryPaths.Clear();

            // 每次打开都重建复用控件：上次关闭时它们仍挂在旧对话框的可视树里，
            // 直接复用会抛"指定的元素已经是另一个元素的逻辑子元素"。
            HistoryItemsPanel = new StackPanel();
            HistoryFolderButton = new Button();
            HistoryTitleTextBlock = new TextBlock();

            var panel = new DockPanel
            {
                Margin = TryFindResource("Layout.WindowPadding") is Thickness windowPadding ? windowPadding : new Thickness(8)
            };

            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(toolbar, Dock.Top);

            _selectAllHistoryCheckBox = new CheckBox
            {
                Content = "全选",
                IsThreeState = true,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            };
            ApplyStyle(_selectAllHistoryCheckBox, "CheckBox.Base");
            _selectAllHistoryCheckBox.Click += SelectAllHistory_Click;

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(rightActions, Dock.Right);

            _batchDeleteButton = new Button
            {
                Content = "批量删除",
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false
            };
            // 危险操作走 Danger 样式：红边红字、悬停淡红底，禁用态自动转灰。
            // 之前是 Secondary + 本地 Foreground=Danger，悬停/禁用的前景全被本地值锁死。
            ApplyStyle(_batchDeleteButton, "Button.Danger.Small");
            _batchDeleteButton.Click += BatchDeleteHistory_Click;

            HistoryFolderButton.Content = L("Folder");
            HistoryFolderButton.Click -= HistoryFolder_Click;
            HistoryFolderButton.Click += HistoryFolder_Click;
            ApplyStyle(HistoryFolderButton, "Button.Secondary.Small");

            rightActions.Children.Add(_batchDeleteButton);
            rightActions.Children.Add(HistoryFolderButton);

            toolbar.Children.Add(rightActions);
            toolbar.Children.Add(_selectAllHistoryCheckBox);

            panel.Children.Add(toolbar);
            panel.Children.Add(new ScrollViewer { Content = HistoryItemsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            RefreshHistoryPanel();

            _historyDialog = new Window
            {
                Title = L("HistoryTitle"),
                Width = 680,
                Height = 540,
                MinWidth = 500,
                MinHeight = 320,
                Content = panel,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Resources = Resources,
                Style = UiStyle("Window.Base")
            };
            if (OwnerWindow != null) _historyDialog.Owner = OwnerWindow;
            try { _historyDialog.ShowDialog(); }
            finally { panel.Children.Clear(); _historyDialog.Content = null; _historyDialog = null; }
        }

        private void HistoryFolder_Click(object sender, RoutedEventArgs e) => RunHistoryAction("打开历史目录", OpenHistoryFolder);

        private void RefreshHistoryPanel()
        {
            HistoryItemsPanel.Children.Clear();
            _currentHistoryItems.Clear();
            HistoryItem[] items;
            try
            {
                var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. 新布局：Conversations/<session>/Conversation/session-*.md
                try
                {
                    string convDir = AiWorkspace.ConversationsDirectory;
                    if (Directory.Exists(convDir))
                    {
                        foreach (string file in Directory.GetFiles(convDir, "session-*.md", SearchOption.AllDirectories))
                            scannedFiles.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("History.ScanConversations", ex);
                }

                // 2. 旧布局：History/yyyyMMdd/session-*.md
                try
                {
                    Directory.CreateDirectory(ConversationHistoryStore.RootDirectory);
                    if (Directory.Exists(ConversationHistoryStore.RootDirectory))
                    {
                        foreach (string file in Directory.GetFiles(ConversationHistoryStore.RootDirectory, "session-*.md", SearchOption.AllDirectories))
                            scannedFiles.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("History.ScanRoot", ex);
                }

                items = scannedFiles
                    .Select(TryCreateHistoryItem)
                    .Where(item => item != null)
                    .OrderByDescending(item => item.Modified)
                    .Take(120)
                    .ToArray();
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("History.Scan", ex);
                items = new HistoryItem[0];
            }

            _currentHistoryItems.AddRange(items);
            // 移除不再存在的文件路径
            _selectedHistoryPaths.RemoveWhere(p => !items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)));

            if (items.Length == 0)
            {
                var empty = new TextBlock
                {
                    Text = "暂无历史对话",
                    Margin = new Thickness(5, 12, 5, 12),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                ApplyStyle(empty, "Text.Caption");
                HistoryItemsPanel.Children.Add(empty);
                UpdateBatchToolbarState();
                return;
            }

            foreach (HistoryItem item in items)
                HistoryItemsPanel.Children.Add(CreateHistoryRow(item));

            UpdateBatchToolbarState();
        }

        private void UpdateBatchToolbarState()
        {
            if (_selectAllHistoryCheckBox == null || _batchDeleteButton == null) return;
            _isUpdatingBatchState = true;
            try
            {
                int total = _currentHistoryItems.Count;
                int selected = _selectedHistoryPaths.Count;

                _selectAllHistoryCheckBox.IsEnabled = total > 0;
                if (total == 0) _selectAllHistoryCheckBox.IsChecked = false;
                else if (selected == total) _selectAllHistoryCheckBox.IsChecked = true;
                else if (selected == 0) _selectAllHistoryCheckBox.IsChecked = false;
                else _selectAllHistoryCheckBox.IsChecked = null;

                _batchDeleteButton.IsEnabled = selected > 0;
                _batchDeleteButton.Content = selected > 0
                    ? ($"批量删除 ({selected})")
                    : ("批量删除");
            }
            finally
            {
                _isUpdatingBatchState = false;
            }
        }

        private void SelectAllHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingBatchState) return;
            bool checkAll = _selectAllHistoryCheckBox.IsChecked == true;
            _selectedHistoryPaths.Clear();
            if (checkAll)
            {
                foreach (HistoryItem item in _currentHistoryItems)
                    _selectedHistoryPaths.Add(item.Path);
            }
            RefreshHistoryPanel();
        }

        private void BatchDeleteHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedHistoryPaths.Count == 0) return;
            int count = _selectedHistoryPaths.Count;
            bool containsActive = _selectedHistoryPaths.Any(IsActiveSessionFile);
            string prompt = $"确定删除选中的 {count} 个历史对话吗？\n此操作不可恢复。" + (containsActive ? "\n\n注意：选中的项包含当前会话正在写入的记录。" : string.Empty);

            MessageBoxResult result = ShowMessage(prompt, ChatCoreHostConfig.UiProductTitleOrDefault(), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            string[] pathsToDelete = _selectedHistoryPaths.ToArray();
            int deleted = 0;
            foreach (string path in pathsToDelete)
            {
                try
                {
                    ConversationHistoryStore.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("History.BatchDelete", ex);
                }
            }

            _selectedHistoryPaths.Clear();
            SetStatus(StatusKind.Done, $"已删除 {deleted} 个历史对话");
            RefreshHistoryPanel();
        }

        private UIElement CreateHistoryRow(HistoryItem item)
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var itemCheck = new CheckBox
            {
                IsChecked = _selectedHistoryPaths.Contains(item.Path),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            ApplyStyle(itemCheck, "CheckBox.Base");
            itemCheck.Click += (s, e) =>
            {
                if (itemCheck.IsChecked == true) _selectedHistoryPaths.Add(item.Path);
                else _selectedHistoryPaths.Remove(item.Path);
                UpdateBatchToolbarState();
            };
            Grid.SetColumn(itemCheck, 0);
            Grid.SetRow(itemCheck, 0);
            Grid.SetRowSpan(itemCheck, 2);
            root.Children.Add(itemCheck);

            var titleText = new TextBlock
            {
                Text = item.Title,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new Button
            {
                Content = titleText,
                ToolTip = string.IsNullOrWhiteSpace(item.Title)
                    ? ("打开历史记录")
                    : (("打开历史记录：") + item.Title)
            };
            ApplyStyle(title, "Button.Title");
            title.Click += (s, e) => RunHistoryAction("打开", () => OpenHistoryItem(item));

            // 就地重命名：标题位置直接换成输入框，不再弹出对话框。
            var editor = new TextBox
            {
                Visibility = Visibility.Collapsed,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(6, 0, 6, 0)
            };
            ApplyStyle(editor, "TextBox.Base");

            var titleHost = new Grid();
            titleHost.Children.Add(title);
            titleHost.Children.Add(editor);
            Grid.SetColumn(titleHost, 1);
            Grid.SetRow(titleHost, 0);
            root.Children.Add(titleHost);

            bool editing = false;
            Action endEdit = () =>
            {
                if (!editing) return;
                editing = false;
                editor.Visibility = Visibility.Collapsed;
                title.Visibility = Visibility.Visible;
            };
            Action beginEdit = () =>
            {
                if (editing) return;
                editing = true;
                editor.Text = item.Title ?? string.Empty;
                title.Visibility = Visibility.Collapsed;
                editor.Visibility = Visibility.Visible;
                editor.Focus();
                editor.SelectAll();
            };
            Action commit = () =>
            {
                if (!editing) return;
                string name = ConversationHistoryStore.NormalizeTitle(editor.Text);
                string current = ConversationHistoryStore.NormalizeTitle(item.Title);
                endEdit();
                if (string.IsNullOrWhiteSpace(name) || string.Equals(name, current, StringComparison.Ordinal)) return;
                RunHistoryAction("重命名", () => { ConversationHistoryStore.Rename(item.Path, name); RefreshHistoryPanel(); });
            };

            editor.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; endEdit(); }
            };
            editor.LostKeyboardFocus += (s, e) => commit();

            var time = new TextBlock { Text = item.Modified.ToString("MM-dd HH:mm:ss"), Margin = new Thickness(6, 2, 0, 1) };
            ApplyStyle(time, "Text.Caption");
            Grid.SetColumn(time, 1);
            Grid.SetRow(time, 1);
            root.Children.Add(time);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            actions.Children.Add(HistoryActionButton("重命名", beginEdit));
            actions.Children.Add(HistoryActionButton("导出", () => ExportHistoryItem(item)));
            actions.Children.Add(HistoryActionButton("删除", () => DeleteHistoryItem(item), true));
            Grid.SetColumn(actions, 2);
            Grid.SetRow(actions, 0);
            Grid.SetRowSpan(actions, 2);
            root.Children.Add(actions);

            var row = new Border { Child = root, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
            ApplyStyle(row, "Card.Info");
            return row;
        }
        private Button HistoryActionButton(string text,Action action,bool destructive=false){var button=new Button{Content=text,Margin=new Thickness(2,0,0,0)};ApplyStyle(button,"Button.Text.Small");if(destructive)button.Foreground=UiBrush("Brush.Danger");button.Click+=(s,e)=>RunHistoryAction(text,action);return button;}
        private void RunHistoryAction(string actionName,Action action){try{action();}catch(Exception ex){AiExceptionGuard.Log("HistoryAction."+actionName,ex);ShowWarning(actionName+("失败：")+ex.Message);}}
        private void ExportHistoryItem(HistoryItem item)
        {
            if (item == null || !File.Exists(item.Path)) return;
            var dialog = new SaveFileDialog
            {
                Title = "导出历史对话",
                Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".md",
                AddExtension = true,
                FileName = SanitizeFileName(item.Title) + ".md"
            };
            if (!ShowSaveDialog(dialog)) return;
            string source = item.Path, target = dialog.FileName;
            // 大历史的全文扫描与图片复制是秒级操作，且旧实现用
            // StringBuilder.Replace 对每个图片匹配做整文替换（O(匹配数×全文长度)），
            // 在 UI 线程上跑会卡死界面——挪到后台线程执行。
            SetBusy(true, "正在导出历史对话…");
            Task.Run(() =>
            {
                try
                {
                    ExportHistoryWithImages(source, target);
                    InvokeUi(() =>
                    {
                        SetBusy(false);
                        SetStatus(StatusKind.Done, "已导出历史（含图片）");
                    });
                }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("History.Export", ex);
                    InvokeUi(() =>
                    {
                        SetBusy(false);
                        SetStatus(StatusKind.Failed);
                        ShowWarning("导出失败：" + ex.Message);
                    });
                }
            });
        }

        /// <summary>
        /// 导出对话并附带图片：历史 .md 里工具结果只记录了截图的绝对路径
        /// （model_capture / review 证据 PNG），直接复制文件会导致导出后
        /// "图片只剩路径看不到图"。导出时把 md 里引用到的本地图片复制到
        /// 伴随目录「<导出名>.files\」，并把路径改写为相对引用——用任何
        /// Markdown 查看器打开都能直接看到图。
        /// 逐行流式处理：旧实现先全文正则、再对每个图片匹配做整文 StringBuilder.Replace
        /// （O(匹配数×全文长度)），大历史在 UI 线程上直接把界面卡死——本方法的调用方已挪到后台线程。
        /// </summary>
        private static void ExportHistoryWithImages(string sourcePath, string targetPath)
        {
            File.Copy(sourcePath, targetPath, true);

            try
            {
                string filesDirectory = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(targetPath) ?? string.Empty,
                    System.IO.Path.GetFileNameWithoutExtension(targetPath) + ".files");
                string relativePrefix = "./" + System.IO.Path.GetFileNameWithoutExtension(targetPath) + ".files/";
                var pattern = new System.Text.RegularExpressions.Regex(
                    @"[A-Za-z]:[\\/](?:[^""\r\n<>|?*]+[\\/])*[^""\r\n<>|?*]+\.(?:png|jpg|jpeg|bmp|tif|tiff)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var output = new List<string>();
                int order = 0;
                bool replacedAny = false;

                // 逐行读写：路径不会跨行，行内替换即可；超长行（base64/大 JSON）跳过正则原样保留。
                foreach (string line in File.ReadLines(targetPath))
                {
                    if (line.Length > 8192 || !pattern.IsMatch(line))
                    {
                        output.Add(line);
                        continue;
                    }
                    string rewritten = pattern.Replace(line, match =>
                    {
                        // JSON 转义会把反斜杠写成两个字符，先还原成真实路径。
                        string absolute = match.Value.Replace("\\\\", "\\").Replace("//", "/");
                        if (!File.Exists(absolute)) return match.Value;
                        if (!copied.TryGetValue(absolute, out string relative))
                        {
                            order++;
                            Directory.CreateDirectory(filesDirectory);
                            string fileName = order.ToString("D2") + "_" + System.IO.Path.GetFileName(absolute);
                            string destination = System.IO.Path.Combine(filesDirectory, fileName);
                            try { File.Copy(absolute, destination, true); }
                            catch { return match.Value; }
                            copied[absolute] = relative = relativePrefix + fileName;
                        }
                        replacedAny = true;
                        return relative;
                    });
                    output.Add(rewritten);
                }

                if (replacedAny)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath) ?? ".");
                    File.WriteAllLines(targetPath, output, new UTF8Encoding(true));
                }
            }
            catch (Exception ex)
            {
                // 附加图片失败不影响导出本体：md 文件已复制成功。
                AiExceptionGuard.Log("History.ExportImages", ex);
            }
        }
        private void DeleteHistoryItem(HistoryItem item){if(!File.Exists(item.Path)){RefreshHistoryPanel();return;}bool isActiveSession=IsActiveSessionFile(item.Path);MessageBoxResult result=ShowMessage(("确定删除历史对话“"+item.Title+"”吗？\n此操作不可恢复。"+(isActiveSession?"\n\n注意：这是当前会话正在写入的记录，继续对话会重新生成它。":string.Empty)),ChatCoreHostConfig.UiProductTitleOrDefault(),MessageBoxButton.YesNo,MessageBoxImage.Warning);if(result!=MessageBoxResult.Yes)return;ConversationHistoryStore.Delete(item.Path);SetStatus(StatusKind.Done,isActiveSession?("已删除当前会话记录"):("已删除历史"));RefreshHistoryPanel();}
        private bool IsActiveSessionFile(string path){try{string active=_session?.HistoryFilePath;if(string.IsNullOrWhiteSpace(active))return false;return string.Equals(System.IO.Path.GetFullPath(active),System.IO.Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase);}catch{return false;}}
        private static HistoryItem TryCreateHistoryItem(string path){try{return new HistoryItem(path);}catch(Exception ex){AiExceptionGuard.Log("History.Item",ex);return null;}}
        private void OpenHistoryItem(HistoryItem item)
        {
            if (item == null) return;
            if (IsTaskRunning) { ShowInformation(L("WaitForTask")); return; }
            LoadHistoryConversation(item.Path);
            _historyDialog?.Close();
            FocusInput();
        }

        /// <summary>从指定的历史会话文件加载并恢复上下文与消息列表。</summary>
        public void LoadHistoryConversation(string path)
        {
            IReadOnlyList<AgentMessage> messages = ConversationHistoryStore.ReadMessages(path);
            if (!messages.Any(message => message.Role == "user" || message.Role == "assistant"))
                throw new InvalidOperationException("此文件没有可恢复的对话内容。");
            NewChat_Click(null, null);
            MessagesPanel.Children.Clear();
            _diagnostics.Clear();
            _assistantGroup = null;
            _pendingHistoryMessages = messages;
            foreach (AgentMessage message in messages)
            {
                string content = message.Content as string ?? OpenAiCompatibleClient.ReadContent(message.Content);
                if (message.Role == "user") AppendUserMessage(content, null);
                else if (message.Role == "assistant")
                {
                    if (!string.IsNullOrWhiteSpace(content)) AppendAssistantText(content);
                    foreach (AgentToolCall call in ConversationHistoryStore.ReadToolCalls(message.ToolCalls))
                        AppendTool(ToolActivity.Call(call.Id, call.Name, call.ArgumentsJson));
                    // 恢复出来的助手消息是一个已完成阶段；下一条回复仍归入此分组。
                    FinalizeActivePhase();
                }
                else if (message.Role == "tool")
                    AppendTool(ToolActivity.Result(message.ToolCallId, message.Name, content, AgentToolResultAnalyzer.IsFailure(content)));
            }
            FinalizeActivePhase();
            SetStatus(StatusKind.Idle, "历史已加载，可继续对话");
            ScrollToBottom();
        }
        private static void OpenHistoryFolder()
        {
            string targetDir = (AiWorkspace.UseConversationLayout && Directory.Exists(AiWorkspace.ConversationsDirectory))
                ? AiWorkspace.ConversationsDirectory
                : ConversationHistoryStore.RootDirectory;
            Directory.CreateDirectory(targetDir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + targetDir + "\"", UseShellExecute = true });
        }
        private static string SanitizeFileName(string value){string text=string.IsNullOrWhiteSpace(value)?Branding.ProductName+"-History":value;foreach(char c in Path.GetInvalidFileNameChars())text=text.Replace(c,'_');return text;}
        private sealed class HistoryItem{public HistoryItem(string path){Path=path;Modified=File.GetLastWriteTime(path);string title=ConversationHistoryStore.ReadTitle(path);Title=string.IsNullOrWhiteSpace(title)?System.IO.Path.GetFileNameWithoutExtension(path):title;}public string Path{get;}public DateTime Modified{get;}public string Title{get;}}
    }
}
