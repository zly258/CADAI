using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CadAI.Core
{
    public partial class AiChatView
    {
        private bool _updatingReasoning;
        private readonly List<string> _pendingPaths = new List<string>();

        /// <summary>对话区的按钮统一为文字按钮：只设置文案与无障碍名称，不再插入图标。</summary>
        private static void SetButtonText(Button button, string text, string label)
        {
            if (button == null) return;
            button.Content = text;
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        }

        /// <summary>纯图标按钮：单色 MaterialIcon 居中，保留悬停提示与无障碍名称。</summary>
        private static void SetButtonIcon(Button button, string symbol, string label, double size = 16)
        {
            if (button == null) return;
            button.Content = new MaterialIcon
            {
                Symbol = symbol,
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        }

        /// <summary>点击底部 GitHub 链接在默认浏览器打开仓库。</summary>
        private void CopyrightText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Branding.RepositoryUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        /// <summary>
        /// 图标 + 文字按钮：图标继承按钮前景色（悬停/禁用自动换色），文字与无障碍名称照旧。
        /// 顶栏与底栏动作加上图标后，与 Ribbon / 关于页的视觉语言一致，窄窗口下也更好扫读。
        /// </summary>
        private static void SetButtonIconText(Button button, string symbol, string text, string label)
        {
            if (button == null) return;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new MaterialIcon
            {
                Symbol = symbol,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            button.Content = panel;
            button.ToolTip = label;
            AutomationProperties.SetName(button, label);
        }

        private void RefreshReasoningChoices()
        {
            _updatingReasoning = true;
            try
            {
                ChatReasoningComboBox.Items.Clear();
                // 四档：关闭 / 低等 / 中等 / 高等。
                string[] keys = { "off", "low", "medium", "high" };
                string[] labels = UiLanguage.IsEnglish
                    ? new[] { "Off", "Low", "Medium", "High" }
                    : new[] { "关闭", "低等", "中等", "高等" };
                for (int i = 0; i < keys.Length; i++) ChatReasoningComboBox.Items.Add(new ComboBoxItem { Tag = keys[i], Content = labels[i] });

                string effort = AiSettings.NormalizeReasoningEffort(_settings.ReasoningEffort);
                int index = Array.IndexOf(keys, effort);
                // 历史值可能还是已下线的 "auto"（旧「自动」档）：开启思考时就地归一到「低等」，
                // 否则下拉框显示与实际下发档位不一致（auto 不发送 reasoning_effort，交给服务端默认）。
                if (_settings.ReasoningEnabled && index < 0)
                {
                    index = 1;
                    _settings.ReasoningEffort = "low";
                }

                ChatReasoningComboBox.SelectedIndex = _settings.ReasoningEnabled ? index : 0;
                ChatReasoningComboBox.ToolTip = UiLanguage.T(
                    "思考等级：关闭 / 低等 / 中等 / 高等（需要模型服务支持）",
                    "Reasoning effort: Off / Low / Medium / High (requires model support)");
                RefreshModelPickerUi();
            }
            finally { _updatingReasoning = false; }
        }

        private void RefreshModelPickerUi()
        {
            if (ModelPickerLabel == null) return;
            string activeModel = (_settings?.Model ?? string.Empty).Trim();
            ModelPickerLabel.Text = string.IsNullOrWhiteSpace(activeModel)
                ? UiLanguage.T("点击配置模型", "Configure model")
                : activeModel;

            if (ReasoningMiniBadge != null && ReasoningMiniText != null)
            {
                if (_settings != null && _settings.ReasoningEnabled)
                {
                    string effort = AiSettings.NormalizeReasoningEffort(_settings.ReasoningEffort);
                    ReasoningMiniText.Text = effort == "high"
                        ? UiLanguage.T("高思考", "High")
                        : effort == "medium"
                            ? UiLanguage.T("中思考", "Med")
                            : UiLanguage.T("低思考", "Low");
                    ReasoningMiniBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    ReasoningMiniBadge.Visibility = Visibility.Collapsed;
                }
            }

            if (ModelQuickList != null)
            {
                bool prev = _updatingReasoning;
                _updatingReasoning = true;
                try
                {
                    ModelQuickList.Items.Clear();
                    var models = new List<string>();
                    if (_settings?.SavedModelCatalog != null)
                    {
                        foreach (string m in _settings.SavedModelCatalog)
                        {
                            if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
                                models.Add(m.Trim());
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(activeModel) && !models.Contains(activeModel, StringComparer.OrdinalIgnoreCase))
                        models.Insert(0, activeModel);

                    foreach (string m in models) ModelQuickList.Items.Add(m);
                    if (!string.IsNullOrWhiteSpace(activeModel))
                        ModelQuickList.SelectedItem = models.FirstOrDefault(x => string.Equals(x, activeModel, StringComparison.OrdinalIgnoreCase));
                }
                finally { _updatingReasoning = prev; }
            }
        }

        private void ModelPickerButton_Click(object sender, RoutedEventArgs e)
        {
            if (ModelPickerPopup == null) return;
            RefreshModelPickerUi();
            ModelPickerPopup.IsOpen = !ModelPickerPopup.IsOpen;
        }

        private void ModelQuickList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingReasoning || _settings == null || IsTaskRunning) return;
            if (ModelQuickList.SelectedItem is string chosen && !string.IsNullOrWhiteSpace(chosen))
            {
                _settings.Model = chosen.Trim();
                AiModelProfile active = _settings.Models?.FirstOrDefault(x =>
                    string.Equals(x.Id, _settings.ActiveModelId, StringComparison.OrdinalIgnoreCase));
                if (active != null)
                {
                    active.Model = _settings.Model;
                    active.Name = _settings.Model;
                }
                _settings.Save();
                RefreshModelPickerUi();
                if (ModelPickerPopup != null) ModelPickerPopup.IsOpen = false;
            }
        }

        private void ModelPickerOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (ModelPickerPopup != null) ModelPickerPopup.IsOpen = false;
            Settings_Click(sender, e);
        }

        private void ChatReasoning_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingReasoning || _settings == null || IsTaskRunning) return;
            string choice = Convert.ToString((ChatReasoningComboBox.SelectedItem as ComboBoxItem)?.Tag);
            _settings.ReasoningEnabled = choice != "off";
            _settings.ReasoningEffort = AiSettings.NormalizeReasoningEffort(choice);
            _settings.Save();
            RefreshModelPickerUi();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = _settings ?? AiSettings.Load();
                var dialog = new AiSettingsWindow(settings)
                {
                    Owner = Window.GetWindow(this)
                };
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        dialog.Settings.Save();
                    }
                    catch (Exception saveEx)
                    {
                        AiExceptionGuard.Log("AiChatView.Settings.Save", saveEx);
                        MessageBox.Show(
                            Window.GetWindow(this),
                            "设置已在当前会话生效，但保存到配置文件失败：" + saveEx.Message,
                            "设置保存提示",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    ApplySettings(dialog.Settings);
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("AiChatView.Settings", ex);
            }
        }

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            StartNewConversation();
        }

        /// <summary>开启一段全新的对话流，清空消息列表与当前活跃会话状态。</summary>
        public void StartNewConversation()
        {
            if (IsTaskRunning) return;
            StartFreshConversation();
            FocusInput();
        }

        /// <summary>
        /// 开一段全新对话（清空消息、诊断、工具卡片与用量统计）。
        /// 批量任务的对话不够用时也走这里——继续在旧对话里硬撑只会让上下文越来越臃肿。
        /// </summary>
        private void StartFreshConversation()
        {
            FinalizeActivePhase();
            _session?.Dispose();
            _session = null;
            _diagnostics.Clear();
            _toolCards.Clear();
            _pendingHistoryMessages = null;
            MessagesPanel.Children.Clear();
            _assistantGroup = null;
            ClearPendingImage();
            ClearQueue();
            InputBox.Clear();
            _promptTokens = _completionTokens = _totalTokens = 0;
            _lastTurnPromptTokens = _lastTurnCompletionTokens = _lastTurnTokens = 0;
            _lastTurnDuration = TimeSpan.Zero;
            _lastTurnHasEstimatedUsage = false;
            _suppressTurnMetrics = true;
            RefreshMetrics();
            SetStatus(StatusKind.Idle);
            AppendWelcome();
        }

        /// <summary>点击附件按钮直接弹文件选择框（不再套一层菜单——常用路径就该一步到位）。</summary>
        private void Attachment_Click(object sender, RoutedEventArgs e) => SelectAttachments();

        private void SelectAttachments()
        {
            // 过滤器按宿主配置：AutoCAD 用户在"附件"里看到 *.ipt/*.iam 没有意义，
            // AutoCAD 用户看到 dwg 优先的清单同样别扭。默认值仍是原来的 AutoCAD 清单。
            var dialog = new OpenFileDialog { Multiselect = true, Filter = ChatCoreHostConfig.AttachmentFileFilterOrDefault() };
            if ((OwnerWindow != null ? dialog.ShowDialog(OwnerWindow) : dialog.ShowDialog()) != true) return;

            var otherFiles = new List<string>();

            foreach (string file in dialog.FileNames)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                if (IsSupportedImageFile(file))
                {
                    if (!TryAddImageFromFile(file)) otherFiles.Add(file);
                }
                else
                {
                    otherFiles.Add(file);
                }
            }

            if (otherFiles.Count > 0)
            {
                AddPendingPaths(otherFiles);
            }
            else
            {
                UpdateAttachmentPreviewUi();
            }
        }

        private void AddPendingPaths(IEnumerable<string> paths)
        {
            foreach (string path in paths) if (!_pendingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _pendingPaths.Add(path);
            UpdateAttachmentPreviewUi();
        }
    }
}
