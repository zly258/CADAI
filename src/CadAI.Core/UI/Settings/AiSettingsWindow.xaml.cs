using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CadAI.Core
{
    /// <summary>
    /// 现代侧栏式设置窗口：模型配置（支持拉取服务端可用模型列表）、运行参数、语音输入。
    /// 不再预设默认模型或跳过配置，确保用户显式配置目标 API 端点与模型。
    /// </summary>
    public partial class AiSettingsWindow : Window
    {
        private static readonly HttpClient SharedTestHttp = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(1)
        };

        private readonly AiSettings _source;
        private readonly System.Collections.ObjectModel.ObservableCollection<ModalRuleItem> _modalRules =
            new System.Collections.ObjectModel.ObservableCollection<ModalRuleItem>();
        private bool _initializing = true;
        private CancellationTokenSource _testCancellation;

        public AiSettingsWindow(AiSettings settings = null, UnifiedSettingsOptions options = null)
        {
            InitializeComponent();
            WindowPlacement.CenterOnOwnerScreen(this);
            SourceInitialized += (s, e) => TryEnableDarkTitleBar();

            Closed += (s, e) =>
            {
                try { _testCancellation?.Cancel(); } catch { }
            };

            _initializing = true;
            _source = (settings ?? new AiSettings()).Clone();

            Title = string.IsNullOrWhiteSpace(options?.WindowTitle) ? "设置" : options.WindowTitle.Trim();

            // 1. 模型配置（不预设模型名，直接加载用户已保存的值；未配置则为空）
            BaseUrlBox.Text = _source.BaseUrl ?? string.Empty;
            ApiKeyBox.Password = _source.ApiKey ?? string.Empty;
            ModelBox.Text = _source.Model ?? string.Empty;

            if (_source.SavedModelCatalog != null)
            {
                foreach (string m in _source.SavedModelCatalog)
                {
                    if (!string.IsNullOrWhiteSpace(m) && !AvailableModelsList.Items.Contains(m.Trim()))
                        AvailableModelsList.Items.Add(m.Trim());
                }
            }
            if (!string.IsNullOrWhiteSpace(_source.Model))
            {
                string activeModel = _source.Model.Trim();
                if (!AvailableModelsList.Items.Contains(activeModel))
                    AvailableModelsList.Items.Insert(0, activeModel);
                AvailableModelsList.SelectedItem = activeModel;
            }

            // 2. 运行参数（含思考死循环自动重试）
            TemperatureBox.Text = _source.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
            MaxAgentCallsBox.Text = (_source.MaxAgentCalls > 0 ? _source.MaxAgentCalls : AiSettings.DefaultMaxAgentCalls).ToString(CultureInfo.InvariantCulture);
            TimeoutSecondsBox.Text = (_source.RequestTimeoutSeconds > 0 ? _source.RequestTimeoutSeconds : AiSettings.DefaultRequestTimeoutSeconds).ToString(CultureInfo.InvariantCulture);
            AutoCompactCheckBox.IsChecked = _source.AutoCompactContext;
            AutoRetryLoopCheckBox.IsChecked = _source.AutoRetryOnReasoningLoop;
            MaxLoopRetriesBox.Text = Math.Max(0, Math.Min(5, _source.MaxRepetitionRecoveryAttempts)).ToString(CultureInfo.InvariantCulture);
            MaxLoopRetriesBox.IsEnabled = _source.AutoRetryOnReasoningLoop;

            // 3. 语音输入
            SelectComboByTag(VoiceModeBox, AiSettings.NormalizeVoiceInputMode(_source.VoiceInputMode));
            SelectComboByTag(VoiceLanguageBox, AiSettings.NormalizeVoiceLanguage(_source.VoiceLanguage));
            VoiceBaseUrlBox.Text = _source.VoiceBaseUrl ?? string.Empty;
            VoiceApiKeyBox.Password = _source.VoiceApiKey ?? string.Empty;
            VoiceModelBox.Text = _source.VoiceModel ?? string.Empty;

            // 4. 弹窗守护（ModalAgent）
            ModalAgentEnabledCheckBox.IsChecked = _source.ModalAgentEnabled;
            ModalAiBridgeCheckBox.IsChecked = _source.ModalAiBridgeEnabled;
            ModalStallSecondsBox.Text = AiSettings.NormalizeModalStallSeconds(_source.ModalStallSeconds).ToString(CultureInfo.InvariantCulture);
            _modalRules.Clear();
            if (_source.ModalCustomRules != null)
            {
                foreach (ModalRuleItem r in _source.ModalCustomRules)
                {
                    if (r != null) _modalRules.Add(r.Clone());
                }
            }
            ModalRulesList.ItemsSource = _modalRules;

            if (options?.InitialTab == SettingsInitialTab.Voice)
                NavVoiceButton.IsChecked = true;
            else
                NavModelButton.IsChecked = true;

            _initializing = false;
            ApplyLanguage();
            UpdateNavPanels();
            ApplyVoiceModeState();
        }

        /// <summary>按当前全局界面语言（中/英）刷新设置窗口全部文案。</summary>
        private void ApplyLanguage()
        {
            Title = UiLanguage.T("设置", "Settings");
            SidebarTitleText.Text = UiLanguage.T("设置", "Settings");
            NavModelText.Text = UiLanguage.T("模型配置", "Model");
            NavRuntimeText.Text = UiLanguage.T("运行参数", "Runtime");
            NavVoiceText.Text = UiLanguage.T("语音输入", "Voice Input");
            NavModalText.Text = UiLanguage.T("弹窗守护", "Modal Guard");

            ModelSectionTitle.Text = UiLanguage.T("API 连接", "API Connection");
            BaseUrlLabel.Text = UiLanguage.T("API 地址", "API Base URL");
            ApiKeyLabel.Text = UiLanguage.T("API 密钥（可选，本地加密保存）", "API Key (optional, encrypted locally with DPAPI)");
            ModelNameLabel.Text = UiLanguage.T("当前模型名称", "Active Model Name");
            ModelCatalogTitle.Text = UiLanguage.T("可用模型目录", "Available Models");
            ConnectionHint.Text = UiLanguage.T(
                "填写 API 地址后，点击右侧按钮获取模型列表或测试连接",
                "Enter API Base URL, then fetch available models or test connection");
            TestConnectionButton.Content = UiLanguage.T("测试连接", "Test");
            FetchModelsButton.Content = UiLanguage.T("获取可用模型", "Fetch Models");

            RuntimeSectionTitle.Text = UiLanguage.T("运行参数", "Runtime Parameters");
            TemperatureLabel.Text = UiLanguage.T("采样温度（0.0 – 2.0，默认 0.6）", "Temperature (0.0 – 2.0, default 0.6)");
            MaxAgentCallsLabel.Text = UiLanguage.T("最大工具调用步数（5 – 200，默认 50）", "Max Tool Calls per Turn (5 – 200, default 50)");
            TimeoutSecondsLabel.Text = UiLanguage.T("请求超时（秒，15 – 1200，默认 300）", "Request Timeout (seconds, 15 – 1200, default 300)");
            AutoCompactCheckBox.Content = UiLanguage.T(
                "启用长会话上下文自动压缩（超出窗口时自动摘要历史消息）",
                "Auto-compact long conversation context when approaching token limit");
            LoopGuardTitle.Text = UiLanguage.T("防死循环保护", "Reasoning Loop Guard");
            AutoRetryLoopCheckBox.Content = UiLanguage.T(
                "模型思考陷入死循环时自动中断并重试继续",
                "Automatically interrupt and retry when model reasoning enters a repetition loop");
            MaxLoopRetriesLabel.Text = UiLanguage.T("死循环最大自动重试次数（0 – 5，默认 2）", "Max Loop Recovery Retries (0 – 5, default 2)");

            VoiceSectionTitle.Text = UiLanguage.T("语音输入", "Voice Input");
            VoiceModeLabel.Text = UiLanguage.T("识别方式", "Recognition Mode");
            VoiceModeLocalItem.Content = UiLanguage.T("Windows 本地语音识别（离线免配置）", "Windows Local Speech Recognition (Offline)");
            VoiceModeApiItem.Content = UiLanguage.T("语音识别接口（OpenAI / Whisper 兼容）", "Speech API (OpenAI / Whisper Compatible)");
            VoiceLangLabel.Text = UiLanguage.T("识别语言", "Recognition Language");
            VoiceLangZhItem.Content = UiLanguage.T("中文", "Chinese");
            VoiceLangEnItem.Content = UiLanguage.T("英文", "English");
            VoiceLangAutoItem.Content = UiLanguage.T("自动检测", "Auto-detect");
            VoiceBaseUrlLabel.Text = UiLanguage.T("语音接口地址（留空则复用模型 API 地址）", "Voice API Base URL (blank reuses Model API URL)");
            VoiceApiKeyLabel.Text = UiLanguage.T("语音接口密钥（可选）", "Voice API Key (optional)");
            VoiceModelLabel.Text = UiLanguage.T("语音模型名称", "Voice Model Name");

            ModalSectionTitle.Text = UiLanguage.T("弹窗守护（ModalAgent）", "Modal Dialog Guard (ModalAgent)");
            ModalAgentEnabledCheckBox.Content = UiLanguage.T(
                "启用后台弹窗守护（自动处理 AutoCAD 模态阻塞弹窗）",
                "Enable background modal guard (auto-handle blocking AutoCAD dialogs)");
            ModalAiBridgeCheckBox.Content = UiLanguage.T(
                "白名单未命中时交由 AI 安全决策（危险按钮自动拦截）",
                "Delegate unmatched dialogs to AI safety decision (blocks destructive actions)");
            ModalStallLabel.Text = UiLanguage.T("卡死触发检测阈值（秒，5 – 600，默认 45）", "Stall Detection Threshold (seconds, 5 – 600, default 45)");
            ModalRulesTitle.Text = UiLanguage.T("自定义白名单自动应答规则", "Custom Whitelist Auto-Response Rules");
            DeleteModalRuleButton.Content = UiLanguage.T("删除选中", "Delete");
            NewRuleNameLabel.Text = UiLanguage.T("规则名称", "Rule Name");
            NewRuleMarkersLabel.Text = UiLanguage.T("弹窗关键词（逗号分隔）", "Dialog Keywords (comma-separated)");
            NewRuleActionLabel.Text = UiLanguage.T("自动动作", "Action");
            ActionPositiveItem.Content = UiLanguage.T("点击确定/是", "Click OK / Yes");
            ActionNegativeItem.Content = UiLanguage.T("点击取消/否", "Click Cancel / No");
            ActionCloseItem.Content = UiLanguage.T("关闭窗口", "Close Window");
            ActionIgnoreItem.Content = UiLanguage.T("忽略", "Ignore");
            AddModalRuleButton.Content = UiLanguage.T("添加", "Add");

            CancelButton.Content = UiLanguage.T("取消", "Cancel");
            SaveButton.Content = UiLanguage.T("保存", "Save");
        }

        public AiSettings Settings { get; private set; }
        public TranslationModelSettingsDto TranslationSettings { get; private set; }
        public CadTranslationRulesDto CadRules { get; private set; }

        private string SelectedVoiceMode => GetSelectedComboTag(VoiceModeBox, "local");
        private string SelectedVoiceLanguage => GetSelectedComboTag(VoiceLanguageBox, "zh");

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;
            UpdateNavPanels();
        }

        private void UpdateNavPanels()
        {
            if (ModelPanel == null || RuntimePanel == null || VoicePanel == null || ModalPanel == null) return;
            ModelPanel.Visibility = NavModelButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            RuntimePanel.Visibility = NavRuntimeButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            VoicePanel.Visibility = NavVoiceButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ModalPanel.Visibility = NavModalButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoRetryLoop_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || MaxLoopRetriesBox == null) return;
            MaxLoopRetriesBox.IsEnabled = AutoRetryLoopCheckBox.IsChecked == true;
        }

        private void AddModalRule_Click(object sender, RoutedEventArgs e)
        {
            string markers = (NewRuleMarkersBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(markers))
            {
                Warn("请输入至少一个弹窗关键词（多个关键词用逗号分隔）。", NewRuleMarkersBox);
                return;
            }
            string name = (NewRuleNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "自定义规则 " + (_modalRules.Count + 1);
            string action = GetSelectedComboTag(NewRuleActionBox, "ClickPositive");
            _modalRules.Add(new ModalRuleItem
            {
                Name = name,
                Match = "Any",
                Action = action,
                Markers = markers
            });
            NewRuleNameBox.Text = string.Empty;
            NewRuleMarkersBox.Text = string.Empty;
        }

        private void DeleteModalRule_Click(object sender, RoutedEventArgs e)
        {
            if (ModalRulesList.SelectedItem is ModalRuleItem selected)
                _modalRules.Remove(selected);
        }

        private void VoiceMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            ApplyVoiceModeState();
        }

        private void ApplyVoiceModeState()
        {
            bool isApi = string.Equals(SelectedVoiceMode, "api", StringComparison.OrdinalIgnoreCase);
            VoiceBaseUrlBox.IsEnabled = isApi;
            VoiceApiKeyBox.IsEnabled = isApi;
            VoiceModelBox.IsEnabled = isApi;
            TestVoiceButton.Content = isApi
                ? UiLanguage.T("测试语音接口", "Test Speech API")
                : UiLanguage.T("检测本地语音", "Check Local Speech");
            VoiceHint.Text = string.Empty;
        }

        private void AvailableModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            if (AvailableModelsList.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                ModelBox.Text = selected.Trim();
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            await QueryModelsAsync(populateList: false);
        }

        private async void FetchModels_Click(object sender, RoutedEventArgs e)
        {
            await QueryModelsAsync(populateList: true);
        }

        private async Task QueryModelsAsync(bool populateList)
        {
            string url = AiSettings.NormalizeBaseUrl(BaseUrlBox.Text);
            string apiKey = (ApiKeyBox.Password ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(url) || !IsValidEndpoint(url))
            {
                ConnectionHint.Text = "请先输入有效的 http(s) API 地址。";
                BaseUrlBox.Focus();
                return;
            }

            TestConnectionButton.IsEnabled = false;
            FetchModelsButton.IsEnabled = false;
            ConnectionHint.Text = populateList ? "正在获取可用模型列表…" : "正在测试连接…";
            _testCancellation?.Cancel();
            _testCancellation?.Dispose();
            _testCancellation = new CancellationTokenSource();

            try
            {
                var (ok, message, models) = await FetchEndpointModelsAsync(url, apiKey, _testCancellation.Token);
                if (!IsLoaded) return;

                ConnectionHint.Text = message;
                if (ok && populateList && models.Count > 0)
                {
                    _initializing = true;
                    try
                    {
                        string current = (ModelBox.Text ?? string.Empty).Trim();
                        AvailableModelsList.Items.Clear();
                        foreach (string m in models) AvailableModelsList.Items.Add(m);

                        if (!string.IsNullOrWhiteSpace(current) && models.Contains(current, StringComparer.OrdinalIgnoreCase))
                            AvailableModelsList.SelectedItem = models.First(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                        else if (string.IsNullOrWhiteSpace(current))
                        {
                            AvailableModelsList.SelectedIndex = 0;
                            ModelBox.Text = models[0];
                        }
                    }
                    finally { _initializing = false; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Settings.QueryModels", ex);
                if (IsLoaded) ConnectionHint.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                _testCancellation?.Dispose();
                _testCancellation = null;
                if (IsLoaded)
                {
                    TestConnectionButton.IsEnabled = true;
                    FetchModelsButton.IsEnabled = true;
                }
            }
        }

        private async void TestVoice_Click(object sender, RoutedEventArgs e)
        {
            int mics = WaveAudioRecorder.GetInputDeviceCount();
            if (!string.Equals(SelectedVoiceMode, "api", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = WindowsSpeechRecognizer.IsAvailable(out string summary);
                if (mics <= 0)
                    VoiceHint.Text = "未检测到麦克风；" + summary;
                else
                    VoiceHint.Text = (ok ? "就绪（麦克风 " + mics + " 个）：" : "不可用：") + summary;
                return;
            }

            string rawUrl = (VoiceBaseUrlBox.Text ?? string.Empty).Trim();
            string url = string.IsNullOrWhiteSpace(rawUrl)
                ? AiSettings.NormalizeBaseUrl(BaseUrlBox.Text)
                : AiSettings.NormalizeBaseUrl(rawUrl);
            string apiKey = (VoiceApiKeyBox.Password ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(apiKey) && string.IsNullOrWhiteSpace(rawUrl))
                apiKey = (ApiKeyBox.Password ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(url) || !IsValidEndpoint(url))
            {
                VoiceHint.Text = "请先输入有效的 http(s) 语音接口地址。";
                VoiceBaseUrlBox.Focus();
                return;
            }

            TestVoiceButton.IsEnabled = false;
            VoiceHint.Text = "正在测试语音接口…";
            try
            {
                var (_, message, _) = await FetchEndpointModelsAsync(url, apiKey, CancellationToken.None);
                if (IsLoaded)
                    VoiceHint.Text = mics <= 0 ? "未检测到麦克风；" + message : message;
            }
            catch (Exception ex)
            {
                if (IsLoaded) VoiceHint.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                if (IsLoaded) TestVoiceButton.IsEnabled = true;
            }
        }

        private static async Task<(bool Ok, string Message, List<string> Models)> FetchEndpointModelsAsync(
            string baseUrl, string apiKey, CancellationToken cancellationToken)
        {
            string requestUrl = baseUrl.TrimEnd('/') + "/models";
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linkedCts.CancelAfter(TimeSpan.FromSeconds(12));
                using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                    using (HttpResponseMessage response = await SharedTestHttp.SendAsync(request, linkedCts.Token).ConfigureAwait(true))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                        if (!response.IsSuccessStatusCode)
                            return (false, "HTTP " + (int)response.StatusCode + " " + (response.ReasonPhrase ?? string.Empty).Trim() + "：" + Limit(body), new List<string>());

                        List<string> models = ParseModelIds(body);
                        string msg = models.Count > 0
                            ? "连接成功，获取到 " + models.Count + " 个可用模型。"
                            : "连接成功（未返回模型列表，可手动填写模型名）。";
                        return (true, msg, models);
                    }
                }
            }
        }

        private static List<string> ParseModelIds(string json)
        {
            var result = new List<string>();
            try
            {
                object parsed = AgentJson.New().DeserializeObject(json ?? string.Empty);
                if (parsed is IDictionary<string, object> map && map.TryGetValue("data", out object dataObj) && dataObj is System.Collections.IEnumerable list)
                {
                    foreach (object item in list)
                    {
                        if (item is IDictionary<string, object> m && m.TryGetValue("id", out object idObj))
                        {
                            string id = Convert.ToString(idObj)?.Trim();
                            if (!string.IsNullOrWhiteSpace(id) && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                                result.Add(id);
                        }
                    }
                }
                result.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return result;
        }

        private static string Limit(string text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= 180 ? text : text.Substring(0, 180) + "…";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string rawUrl = (BaseUrlBox.Text ?? string.Empty).Trim();
            if (!IsValidEndpoint(rawUrl))
            {
                NavModelButton.IsChecked = true;
                Warn("请配置有效的 http(s) API 地址。", BaseUrlBox);
                return;
            }

            string chatModel = (ModelBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(chatModel))
            {
                NavModelButton.IsChecked = true;
                Warn("请填写或从列表中选择一个模型名称。", ModelBox);
                return;
            }

            if (!double.TryParse((TemperatureBox.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature)
                || temperature < 0.0 || temperature > 2.0)
            {
                NavRuntimeButton.IsChecked = true;
                Warn("请输入 0.0 到 2.0 之间的采样温度。", TemperatureBox);
                return;
            }
            if (!int.TryParse((MaxAgentCallsBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxAgentCalls)
                || maxAgentCalls < 5 || maxAgentCalls > 200)
            {
                NavRuntimeButton.IsChecked = true;
                Warn("请输入 5 到 200 之间的最大工具步数。", MaxAgentCallsBox);
                return;
            }
            if (!int.TryParse((TimeoutSecondsBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeoutSeconds)
                || timeoutSeconds < 15 || timeoutSeconds > 1200)
            {
                NavRuntimeButton.IsChecked = true;
                Warn("请输入 15 到 1200 之间的请求超时秒数。", TimeoutSecondsBox);
                return;
            }

            if (!int.TryParse((MaxLoopRetriesBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxLoopRetries)
                || maxLoopRetries < 0 || maxLoopRetries > 5)
            {
                NavRuntimeButton.IsChecked = true;
                Warn("请输入 0 到 5 之间的死循环最大重试次数。", MaxLoopRetriesBox);
                return;
            }

            if (!int.TryParse((ModalStallSecondsBox.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int modalStallSeconds)
                || modalStallSeconds < 5 || modalStallSeconds > 600)
            {
                NavModalButton.IsChecked = true;
                Warn("请输入 5 到 600 之间的卡死检测阈值（秒）。", ModalStallSecondsBox);
                return;
            }

            string voiceMode = SelectedVoiceMode;
            string rawVoiceUrl = (VoiceBaseUrlBox.Text ?? string.Empty).Trim();
            if (voiceMode == "api" && !string.IsNullOrWhiteSpace(rawVoiceUrl) && !IsValidEndpoint(rawVoiceUrl))
            {
                NavVoiceButton.IsChecked = true;
                Warn("请输入有效的 http(s) 语音接口地址，或留空复用模型地址。", VoiceBaseUrlBox);
                return;
            }

            string chatUrl = AiSettings.NormalizeBaseUrl(rawUrl);
            string chatKey = (ApiKeyBox.Password ?? string.Empty).Trim();
            string provider = AiSettings.NormalizeProvider(_source.Provider, chatUrl);

            AiSettings result = _source.Clone();
            result.UseDefaultModel = false;
            result.Provider = provider;
            result.BaseUrl = chatUrl;
            result.ApiKey = chatKey;
            result.Model = chatModel;
            result.Temperature = temperature;
            result.MaxAgentCalls = maxAgentCalls;
            result.RequestTimeoutSeconds = timeoutSeconds;
            result.AutoCompactContext = AutoCompactCheckBox.IsChecked != false;
            result.AutoRetryOnReasoningLoop = AutoRetryLoopCheckBox.IsChecked != false;
            result.MaxRepetitionRecoveryAttempts = maxLoopRetries;
            result.ModalAgentEnabled = ModalAgentEnabledCheckBox.IsChecked != false;
            result.ModalAiBridgeEnabled = ModalAiBridgeCheckBox.IsChecked != false;
            result.ModalStallSeconds = modalStallSeconds;
            result.ModalCustomRules = _modalRules.Select(r => r.Clone()).ToList();

            var savedModels = new List<string>();
            foreach (object item in AvailableModelsList.Items)
            {
                string s = Convert.ToString(item)?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !savedModels.Contains(s, StringComparer.OrdinalIgnoreCase))
                    savedModels.Add(s);
            }
            if (!savedModels.Contains(chatModel, StringComparer.OrdinalIgnoreCase))
                savedModels.Insert(0, chatModel);
            result.SavedModelCatalog = savedModels;

            result.ResponseLanguage = "zh-CN";
            result.VoiceInputMode = voiceMode;
            result.VoiceLanguage = SelectedVoiceLanguage;
            result.VoiceBaseUrl = string.IsNullOrWhiteSpace(rawVoiceUrl) ? string.Empty : AiSettings.NormalizeBaseUrl(rawVoiceUrl);
            result.VoiceApiKey = (VoiceApiKeyBox.Password ?? string.Empty).Trim();
            result.VoiceModel = (VoiceModelBox.Text ?? string.Empty).Trim();
            result.EnsureProfiles();

            AiModelProfile active =
                result.Models.FirstOrDefault(x => string.Equals(x.Id, result.ActiveModelId, StringComparison.OrdinalIgnoreCase))
                ?? result.Models.First();
            active.Provider = provider;
            active.BaseUrl = chatUrl;
            active.ApiKey = chatKey;
            active.Model = chatModel;
            active.Name = chatModel;
            active.Temperature = result.Temperature;
            result.SetActiveModel(active.Id);

            // 同步更新 ModalAgent 用户自定义规则配置并热生效
            Modal.ModalAgentLauncher.ApplyUserSettings(result);

            Settings = result;
            DialogResult = true;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(Convert.ToString(cbi.Tag), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string GetSelectedComboTag(ComboBox combo, string fallback)
        {
            if (combo?.SelectedItem is ComboBoxItem cbi && cbi.Tag != null)
                return Convert.ToString(cbi.Tag);
            return fallback;
        }

        private void Warn(string text, Control control)
        {
            MessageBox.Show(this, text, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            control?.Focus();
            if (control is TextBox textBox) textBox.SelectAll();
        }

        private static bool IsValidEndpoint(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryEnableDarkTitleBar()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch { }
        }
    }
}
