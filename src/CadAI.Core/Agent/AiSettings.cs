using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using CadAI.Core;

namespace CadAI.Core
{
    public sealed class AiModelProfile
    {
        public string Id { get; set; } = AiDefaults.EnterpriseModelId;
        public string Name { get; set; } = "Enterprise model";
        public string Provider { get; set; } = "Ollama";
        public string BaseUrl { get; set; } = AiDefaults.OllamaOpenAiBaseUrl;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = AiDefaults.OllamaDefaultModel;
        public double Temperature { get; set; } = 0.6;

        public AiModelProfile Clone() => new AiModelProfile
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            Temperature = Temperature
        };
    }

    /// <summary>
    /// 部署侧统一管理的内容只有模型地址与模型名称，来自宿主注入的 runtime.json（AutoCAD: cadai.runtime.json；AutoCAD: cadai.runtime.json）。
    /// 其余配置各不相同，属于每个用户的设置（落 <see cref="IHostEnvironment.UserDataDirectory"/> 下的 ai-settings.json，两个助手各占一份）。
    /// 翻译助手的配置独立在 <c>TranslationSettings</c>（translation-settings.json），与本类无关。
    /// </summary>
    internal sealed class AiRuntimeConfiguration
    {
        public string BaseUrl { get; set; } = AiDefaults.OllamaOpenAiBaseUrl;
        public string Model { get; set; } = AiDefaults.OllamaDefaultModel;
    }

    internal sealed class AiUserPreferences
    {
        public bool ReasoningEnabled { get; set; } = false;
        public string ReasoningEffort { get; set; } = "auto";
    }

    public sealed class AiSettings
    {
        public const int DefaultMaxAgentCalls = 50;
        public const int DefaultRequestTimeoutSeconds = 300;

        /// <summary>默认卡死判定阈值（秒）：明显大于 ModalAgent 的 20s 决策窗口，且远小于 180s 僵死超时。</summary>
        public const int DefaultModalStallSeconds = 45;

        /// <summary>默认弹窗决策模型请求超时（秒）。</summary>
        public const int DefaultModalDecisionTimeoutSeconds = 12;

        /// <summary>
        /// 默认工具调用超时（秒）：宿主等待单次 MCP 工具返回的上限。
        /// 超过即返回一条可读的超时诊断，而不是让对话界面一直转圈。
        /// </summary>
        public const int DefaultToolCallTimeoutSeconds = 300;

        /// <summary>默认后台工具（整图翻译等）调用超时（分钟）。比前台宽：逐条串行调模型本来就慢。</summary>
        public const int DefaultBackgroundToolTimeoutMinutes = 20;

        /// <summary>
        /// 默认视觉校验请求超时（秒）。刻意远低于对话请求的 300s：
        /// 校验要上传约 1.5 MB 截图，网关一旦卡住就是整整一个超时窗口的空等，
        /// 而它是增强手段，宁可快速放弃也不该拖住主任务。
        /// </summary>
        public const int DefaultVisualReviewTimeoutSeconds = 120;

        // 只有模型端点与模型名来自部署侧的 runtime.json；其余设置都是按用户保存于 ai-settings.json 的值。
        public string Provider { get; set; } = "OpenAICompatible";
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        // 开源版不预设默认模型：由用户在设置中显式配置 API 地址、密钥与模型。
        public bool UseDefaultModel { get; set; } = false;
        // 单轮补全（翻译/弹窗决策等确定性任务）默认关闭思考：网关不支持思考参数时自动剥除重试。
        // 隐藏设置，按需手改 ai-settings.json。
        public bool DisableThinkingOnCompletion { get; set; } = true;
        public double Temperature { get; set; } = 0.6;
        public double TopP { get; set; } = 1.0;
        public int MaxOutputTokens { get; set; }
        public string ResponseLanguage { get; set; } = "zh-CN";
        /// <summary>界面显示语言（zh-CN 简体中文 / en-US 英文）。</summary>
        public string UiLanguage { get; set; } = CadAI.Core.UiLanguage.ZhCn;
        public int MaxAgentCalls { get; set; } = DefaultMaxAgentCalls;
        public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

        // 弹窗处置的两个阈值（隐藏设置，按需手改 ai-settings.json）：
        //   ModalStallSeconds —— 工具调用多久没返回就判定"卡住"，触发宿主模态框检测；
        //     必须明显大于 ModalAgent 的决策等待窗口（modal-agent.json 的 DecisionTimeoutMs，
        //     默认 20s）以先给常驻通道机会，又明显小于 MCP 客户端判僵死的 180s。
        //   ModalDecisionTimeoutSeconds —— 弹窗决策单次模型请求的超时，必须小于
        //     ModalAgent 的等待窗口，否则宿主还没答完对方已经放弃。
        public int ModalStallSeconds { get; set; } = DefaultModalStallSeconds;
        public int ModalDecisionTimeoutSeconds { get; set; } = DefaultModalDecisionTimeoutSeconds;

        /// <summary>
        /// 单次工具调用超时（秒）。AutoCAD 侧由 CadToolExecutor 使用；AutoCAD 侧 MCP 进程
        /// 自带 180s 读超时。设成 0 表示使用默认值。
        /// </summary>
        public int ToolCallTimeoutSeconds { get; set; } = DefaultToolCallTimeoutSeconds;

        /// <summary>后台工具（整图翻译等）调用超时（分钟）。0 = 默认值。</summary>
        public int BackgroundToolTimeoutMinutes { get; set; } = DefaultBackgroundToolTimeoutMinutes;
        // 自动视觉校验的单次请求超时（隐藏设置，按需手改 ai-settings.json）：
        // 与对话请求的 RequestTimeoutSeconds 解耦，避免校验把主任务拖住几分钟。
        public int VisualReviewTimeoutSeconds { get; set; } = DefaultVisualReviewTimeoutSeconds;
        public bool AutoCompactContext { get; set; } = true;
        public bool AutoRetryOnReasoningLoop { get; set; } = true;
        public int MaxRepetitionRecoveryAttempts { get; set; } = 2;
        public bool ModalAgentEnabled { get; set; } = true;
        public bool ModalAiBridgeEnabled { get; set; } = true;
        public List<ModalRuleItem> ModalCustomRules { get; set; } = new List<ModalRuleItem>();
        public List<string> SavedModelCatalog { get; set; } = new List<string>();

        /// <summary>
        /// 上下文窗口（tokens）。<b>0 = 自动</b>：按当前模型名推断（见 <see cref="ResolveContextWindowTokens"/>）。
        /// 显式填正数则严格按该值压缩。
        ///
        /// 历史默认是写死的 65536，对 1M 上下文的模型意味着"才聊到 6% 就压缩"——
        /// 用户看到的是对话刚开始就出现"[自动压缩的历史上下文]"，模型反复丢失任务状态。
        /// </summary>
        public int ContextWindowTokens { get; set; } = 0;
        public int DimensionPrecision { get; set; }

        /// <summary>
        /// 出图默认模板（.idw）的显式路径。
        /// **模板不能写死**：不同厂标/标准（GB、ISO、ASME）的图框与标题栏都不一样，
        /// 交付件里也不该预设某一个模板。为空时按以下顺序探测：
        /// &lt;Add-in&gt;\templates\*.idw → AutoCAD 自带 Templates\&lt;语言&gt;\Standard.idw。
        /// </summary>
        public string DrawingTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// 图框（border）定义名。为空时：文档里只有一个定义就用它；多于一个则**不猜**，
        /// 报错并列出全部可选名，让调用方显式传 name 或先在这里配默认。
        /// </summary>
        public string DrawingBorderDefinitionName { get; set; } = string.Empty;

        /// <summary>标题栏（title block）定义名。规则同 <see cref="DrawingBorderDefinitionName"/>。</summary>
        public string DrawingTitleBlockDefinitionName { get; set; } = string.Empty;

        /// <summary>
        /// 出图图幅：<c>auto</c>（按模型范围自动推荐）/ A0 / A1 / A2 / A3 / A4。
        /// 空或 auto 时由 <c>DrawingSheetPlan</c> 依据模型三维包络与视图数推荐最合适的一档。
        /// </summary>
        public string DrawingSheetSize { get; set; } = "auto";

        /// <summary>
        /// 出图图版朝向：<c>auto</c>（按长宽比自动选横/竖版）/ <c>landscape</c>（横版）/ <c>portrait</c>（竖版）。
        /// </summary>
        public string DrawingOrientation { get; set; } = "auto";

        /// <summary>
        /// 出图视图集合：<c>auto</c>（默认四视图：主 + 俯 + 侧 + 轴测）/ <c>three</c>（三视图）/
        /// <c>single</c>（仅主视图）。
        /// </summary>
        public string DrawingViewMode { get; set; } = "auto";

        /// <summary>自动推荐图幅时允许使用的最大图幅（默认 A0），避免超大构件把图幅撑到 A0 之外。</summary>
        public string DrawingMaxSheetSize { get; set; } = "A0";

        public string CustomParametersJson { get; set; } = "{}";
        public string AdditionalInstructions { get; set; } = string.Empty;

        // 推理控制也会显示在聊天头部，便于快速访问。
        public bool ReasoningEnabled { get; set; } = false;
        public string ReasoningEffort { get; set; } = "auto";

        // ────────────────────────────────────────────────────────────────────
        // 语音输入配置（支持 Windows 本地语音识别与 OpenAI 兼容 /audio/transcriptions 接口）
        // ────────────────────────────────────────────────────────────────────

        /// <summary>语音识别模式：local（Windows 本地识别，离线可用）或 api（OpenAI 兼容语音转文字接口）。</summary>
        public string VoiceInputMode { get; set; } = "local";

        /// <summary>语音识别接口地址（留空时在 api 模式下回退到当前对话模型服务地址）。</summary>
        public string VoiceBaseUrl { get; set; } = string.Empty;

        /// <summary>语音识别接口密钥（可选，落盘走 DPAPI 加密）。</summary>
        public string VoiceApiKey { get; set; } = string.Empty;

        /// <summary>语音识别模型名称（默认 whisper-1，支持 FunASR / SenseVoice / Whisper 等兼容网关）。</summary>
        public string VoiceModel { get; set; } = "whisper-1";

        /// <summary>语音识别语言：zh（中文）/ en（英文）/ auto（自动）。</summary>
        public string VoiceLanguage { get; set; } = "zh";

        // ────────────────────────────────────────────────────────────────────
        // 模型可用性（2026-10-14 新增）
        //
        // 起因：实测上游中途对当前出口返回 HTTP 400「User location is not supported」，
        // 随后同批模型全部 503。产品此前**没有回退机制**，一次上游抖动就让整轮对话失败，
        // 用户看到的是"产品坏了"。外部模型可用性必须当作故障域来设计。
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 备用模型名（逗号分隔）。主模型返回**上游类错误**时按顺序尝试，第一个成功即采用。
        /// 空 = 不启用回退。
        /// </summary>
        public string FallbackModels { get; set; } = string.Empty;

        /// <summary>上游错误时是否自动回退到 <see cref="FallbackModels"/>。</summary>
        public bool AutoFallbackOnUpstreamError { get; set; } = true;

        /// <summary>解析后的备用模型列表（去空白、去重、排除与主模型同名的项）。</summary>
        public IReadOnlyList<string> ResolveFallbackModels()
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(FallbackModels)) return result;
            foreach (string raw in FallbackModels.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;
                if (string.Equals(name, Model, StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(name);
            }
            return result;
        }

        /// <summary>候选模型列表（供独立工作台快速下拉切换）。</summary>
        public List<string> AvailableModels { get; set; } = new List<string>();

        /// <summary>获取去重后的候选模型清单（主模型优先，紧跟配置候选与备选模型）。</summary>
        public IReadOnlyList<string> GetCandidateModels()
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model)) list.Add(Model.Trim());
            if (AvailableModels != null)
            {
                foreach (string m in AvailableModels)
                {
                    string name = (m ?? string.Empty).Trim();
                    if (name.Length > 0 && !list.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                        list.Add(name);
                }
            }
            foreach (string fb in ResolveFallbackModels())
            {
                if (!list.Exists(x => string.Equals(x, fb, StringComparison.OrdinalIgnoreCase)))
                    list.Add(fb);
            }
            if (list.Count == 0) list.Add("deepseek-chat");
            return list;
        }

        // ────────────────────────────────────────────────────────────────────
        // 工作区布局（2026-10-14 新增）
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 工作区布局：<c>ByConversation</c>（默认，按对话分目录）或 <c>Flat</c>（旧的平铺布局）。
        /// 排障或需要与历史产物路径保持一致时可切回 Flat。
        /// </summary>
        public string WorkspaceLayout { get; set; } = "ByConversation";

        /// <summary>是否为每个会话保留产物目录（关闭后只保留会话记录）。</summary>
        public bool KeepConversationArtifacts { get; set; } = true;

        // ────────────────────────────────────────────────────────────────────
        // 对话界面偏好（2026-10-14 新增）
        // ────────────────────────────────────────────────────────────────────

        /// <summary>流式输出时自动跟随到底部。用户主动上滑会临时暂停，发新消息后恢复。</summary>
        public bool AutoScrollFollowStreaming { get; set; } = true;

        // 兼容层：企业运行时只拥有唯一一个配置档。
        public List<AiModelProfile> Models { get; set; } = new List<AiModelProfile>();
        public string ActiveModelId { get; set; } = AiDefaults.EnterpriseModelId;

        public static string RuntimeConfigPath
        {
            get
            {
                // Phase 2：宿主在启动时（任何 AiSettings.Load 之前）注入运行时配置路径，
                // 指向宿主程序集所在目录——与拆分前从程序集位置推导的结果一致，
                // 但路径不再隐式依赖 ChatCore.dll 的位置。
                string hostOverride = ChatCoreHost.RuntimeConfigPathOverride;

                // CADAI_RUNTIME_CONFIG 是 AutoCAD 的历史变量，只对 AutoCAD 生效：
                // AutoCAD 若误读 AutoCAD 的 runtime.json，会把对话/翻译端点串到另一个产品。
                bool isAutoCADHost;
                try
                {
                    isAutoCADHost = string.Equals(
                        ChatCoreHostConfig.HostProductNameOrDefault(),
                        "AutoCAD",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { isAutoCADHost = true; }

                if (isAutoCADHost)
                {
                    string overridden = Environment.GetEnvironmentVariable("CADAI_RUNTIME_CONFIG");
                    if (!string.IsNullOrWhiteSpace(overridden))
                    {
                        try { return Path.GetFullPath(overridden.Trim()); } catch { }
                    }
                }

                if (!string.IsNullOrWhiteSpace(hostOverride)) return hostOverride;

                string assemblyPath = typeof(AiSettings).Assembly.Location;
                string directory = Path.GetDirectoryName(assemblyPath);
                if (string.IsNullOrWhiteSpace(directory))
                    directory = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(directory, "cadai.runtime.json");
            }
        }

        /// <summary>
        /// 产品专属的用户数据目录：优先取宿主装进来的 <see cref="IHostEnvironment.UserDataDirectory"/>；
        /// 宿主尚未安装（设计器、离线单测）时退到 AutoCAD 助手默认目录。
        /// **两个助手各占一份**——AutoCAD 落 <c>%APPDATA%\CADAI\AutoCAD</c>，AutoCAD 落
        /// <c>%APPDATA%\CADAI\AutoCAD</c>，不再共用同一份设置。
        /// </summary>
        internal static string ResolveUserDataDirectory()
        {
            IHostEnvironment environment = ChatCoreHost.Environment;
            string directory = environment == null ? null : environment.UserDataDirectory;
            if (string.IsNullOrWhiteSpace(directory)) directory = ProductDataPaths.AutoCadUserData;
            return directory;
        }

        public static string SettingsPath
        {
            get
            {
                string directory = ResolveUserDataDirectory();
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "ai-settings.json");
            }
        }

        private static string ReasoningPreferencesPath => Path.Combine(
            ResolveUserDataDirectory(),
            "user-preferences.json");

        public AiModelProfile ActiveProfile
        {
            get
            {
                EnsureProfiles();
                return Models[0];
            }
        }

        public static AiSettings Load() => LoadFromFiles(RuntimeConfigPath, SettingsPath);

        internal static AiSettings LoadFromFiles(string runtimePath, string userSettingsPath)
        {
            AiRuntimeConfiguration runtime = LoadRuntimeConfiguration(runtimePath);
            AiSettings settings = new AiSettings();

            try
            {
                if (File.Exists(userSettingsPath))
                {
                    AiSettings saved = AgentJson.New().Deserialize<AiSettings>(File.ReadAllText(userSettingsPath));
                    if (saved != null)
                    {
                        settings = saved;
                        // 密钥在磁盘上是 DPAPI 密文（带 dpapi: 前缀）；旧版明文原样兼容读取。
                        // Deserialize 直接映射字符串属性，这里从原始文件把两个密钥解密回来。
                        Dictionary<string, object> raw = AgentJson.New()
                            .DeserializeObject(File.ReadAllText(userSettingsPath)) as Dictionary<string, object>;
                        if (raw != null)
                        {
                            settings.ApiKey = SecretProtector.ReadSecret(raw, "ApiKey");
                            settings.VoiceApiKey = SecretProtector.ReadSecret(raw, "VoiceApiKey");
                        }
                    }
                }
                else
                {
                    // 保留临时简化设置界面启用期间所做的推理选择。
                    AiUserPreferences preferences = LoadUserPreferences();
                    settings.ReasoningEnabled = preferences.ReasoningEnabled;
                    settings.ReasoningEffort = NormalizeReasoningEffort(preferences.ReasoningEffort);
                }
            }
            catch
            {
                // 用户设置文件无法读取时保留部署的运行时默认值。
            }

            settings.ApplyManagedConnection(runtime);
            settings.Normalize();
            settings.EnsureProfiles();
            return settings;
        }

        public void Save()
        {
            EnsureProfiles();
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? ".");
            // 原子落盘：先写临时文件再替换，写中途崩溃不会损毁既有设置。
            string target = SettingsPath;
            string temp = target + ".tmp";
            File.WriteAllText(temp, ToUserSettingsJson());
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }

        internal string ToUserSettingsJson()
        {
            var serializer = AgentJson.New();
            var values = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(this));
            foreach (string key in new[] { "Models", "ActiveModelId", "ActiveProfile" }) values.Remove(key);

            // 密钥不落明文：DPAPI（CurrentUser）加密后写入，加载时解密。
            // 加密在移除默认组键之前做——UseDefaultModel 分组即使被移除也不影响密钥格式。
            if (values.ContainsKey("ApiKey"))
                values["ApiKey"] = SecretProtector.Protect(ApiKey);
            if (values.ContainsKey("VoiceApiKey"))
                values["VoiceApiKey"] = SecretProtector.Protect(VoiceApiKey);

            // 使用默认模型时不保留地址、密钥与模型名称。
            if (UseDefaultModel)
                foreach (string key in new[] { "BaseUrl", "Model", "ApiKey" }) values.Remove(key);

            return serializer.Serialize(values);
        }

        internal void ApplyManagedConnection() => ApplyManagedConnection(LoadRuntimeConfiguration());

        private void ApplyManagedConnection(AiRuntimeConfiguration runtime)
        {
            runtime = runtime ?? new AiRuntimeConfiguration();
            if (UseDefaultModel || string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(Model))
            {
                UseDefaultModel = true;
                BaseUrl = NormalizeBaseUrl(runtime.BaseUrl);
                Model = string.IsNullOrWhiteSpace(runtime.Model) ? AiDefaults.OllamaDefaultModel : runtime.Model.Trim();
            }
            else
            {
                BaseUrl = NormalizeBaseUrl(BaseUrl);
                Model = Model.Trim();
            }

            Provider = NormalizeProvider(Provider, BaseUrl);
            EnsureProfiles();
        }

        public bool SetActiveModel(string id)
        {
            EnsureProfiles();
            ActiveModelId = AiDefaults.EnterpriseModelId;
            return false;
        }

        public void EnsureProfiles()
        {
            Models = new List<AiModelProfile>
            {
                new AiModelProfile
                {
                    Id = AiDefaults.EnterpriseModelId,
                    Name = "Enterprise model",
                    Provider = Provider,
                    BaseUrl = BaseUrl,
                    ApiKey = ApiKey,
                    Model = Model,
                    Temperature = Temperature
                }
            };
            ActiveModelId = AiDefaults.EnterpriseModelId;
        }

        public AiSettings Clone()
        {
            return new AiSettings
            {
                Provider = Provider,
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Model = Model,
                UseDefaultModel = UseDefaultModel,
                DisableThinkingOnCompletion = DisableThinkingOnCompletion,
                Temperature = Temperature,
                TopP = TopP,
                MaxOutputTokens = MaxOutputTokens,
                ReasoningEnabled = ReasoningEnabled,
                ReasoningEffort = ReasoningEffort,
                VoiceInputMode = VoiceInputMode,
                VoiceBaseUrl = VoiceBaseUrl,
                VoiceApiKey = VoiceApiKey,
                VoiceModel = VoiceModel,
                VoiceLanguage = VoiceLanguage,
                ResponseLanguage = ResponseLanguage,
                UiLanguage = UiLanguage,
                MaxAgentCalls = MaxAgentCalls,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                VisualReviewTimeoutSeconds = VisualReviewTimeoutSeconds,
                ModalStallSeconds = ModalStallSeconds,
                ModalDecisionTimeoutSeconds = ModalDecisionTimeoutSeconds,
                ToolCallTimeoutSeconds = ToolCallTimeoutSeconds,
                BackgroundToolTimeoutMinutes = BackgroundToolTimeoutMinutes,
                AutoCompactContext = AutoCompactContext,
                AutoRetryOnReasoningLoop = AutoRetryOnReasoningLoop,
                MaxRepetitionRecoveryAttempts = MaxRepetitionRecoveryAttempts,
                ModalAgentEnabled = ModalAgentEnabled,
                ModalAiBridgeEnabled = ModalAiBridgeEnabled,
                ModalCustomRules = (ModalCustomRules ?? new List<ModalRuleItem>()).Select(r => r.Clone()).ToList(),
                SavedModelCatalog = (SavedModelCatalog ?? new List<string>()).ToList(),
                ContextWindowTokens = ContextWindowTokens,
                DimensionPrecision = DimensionPrecision,
                DrawingTemplatePath = DrawingTemplatePath,
                DrawingBorderDefinitionName = DrawingBorderDefinitionName,
                DrawingTitleBlockDefinitionName = DrawingTitleBlockDefinitionName,
                DrawingSheetSize = DrawingSheetSize,
                DrawingOrientation = DrawingOrientation,
                DrawingViewMode = DrawingViewMode,
                DrawingMaxSheetSize = DrawingMaxSheetSize,
                CustomParametersJson = CustomParametersJson,
                AdditionalInstructions = AdditionalInstructions,
                FallbackModels = FallbackModels,
                AutoFallbackOnUpstreamError = AutoFallbackOnUpstreamError,
                AvailableModels = AvailableModels == null
                    ? new List<string>()
                    : new List<string>(AvailableModels),
                WorkspaceLayout = WorkspaceLayout,
                KeepConversationArtifacts = KeepConversationArtifacts,
                AutoScrollFollowStreaming = AutoScrollFollowStreaming,
                // 复制用户当前选择，而不是硬编码回内置档位——Clone 是设置窗口的
                // 工作副本，任何一次 Save() 都不应该把活动模型重置。
                ActiveModelId = ActiveModelId,
                Models = Models?.Select(x => x.Clone()).ToList() ?? new List<AiModelProfile>()
            };
        }

        // 部署侧运行时配置只读宿主注入的 runtime.json（AutoCAD/AutoCAD 各自的 cadai.runtime.json）：本地文件读取很快，不再需要缓存或后台预热。
        private static AiRuntimeConfiguration LoadRuntimeConfiguration(string runtimePath = null)
        {
            return TryLoadRuntimeConfigurationFile(runtimePath ?? RuntimeConfigPath)
                ?? new AiRuntimeConfiguration();
        }

        /// <summary>
        /// 支持两种结构（键名大小写不敏感）：
        /// 分组（新）：{ "chat": { "baseUrl", "model" } }
        /// 扁平（旧，兼容）：{ "BaseUrl", "Model" }
        /// 分组结构优先。翻译助手的端点配置独立在它自己的 runtime JSON（TranslationSettings 读取），
        /// 与本文件无关。
        /// </summary>
        private static AiRuntimeConfiguration TryLoadRuntimeConfigurationFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                object deserialized = AgentJson.New().DeserializeObject(File.ReadAllText(path));
                Dictionary<string, object> map = deserialized as Dictionary<string, object>;
                if (map == null) return null;

                var values = new Dictionary<string, object>(map, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> chatGroup = ReadGroup(values, "chat");

                // 对话端点：分组结构的 chat 组优先，回落到旧扁平键（BaseUrl/Model）。
                string baseUrl = ReadString(chatGroup, "baseUrl")
                    ?? ReadString(values, "baseUrl");
                string model = ReadString(chatGroup, "model")
                    ?? ReadString(values, "model");
                if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return null;

                return new AiRuntimeConfiguration
                {
                    BaseUrl = baseUrl.Trim(),
                    Model = model.Trim()
                };
            }
            catch (Exception ex)
            {
                // JSON 损坏/权限不足视为不可用，交给默认配置兜底，但要留痕：
                // 否则用户只会看到连到错误的默认端点，没有任何线索。
                AiExceptionGuard.Log("AiSettings.LoadRuntimeConfiguration", ex);
                return null;
            }
        }

        /// <summary>读取顶层分组（如 chat/translation），缺失或不是对象时返回 null。</summary>
        private static Dictionary<string, object> ReadGroup(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out object groupValue)) return null;
            return groupValue as Dictionary<string, object>;
        }

        /// <summary>读取组内字符串键（大小写不敏感），缺失或空串返回 null。</summary>
        private static string ReadString(Dictionary<string, object> group, string key)
        {
            if (group == null) return null;
            if (!group.TryGetValue(key, out object value)) return null;
            string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static AiUserPreferences LoadUserPreferences()
        {
            try
            {
                if (!File.Exists(ReasoningPreferencesPath))
                    return new AiUserPreferences();

                return AgentJson.New().Deserialize<AiUserPreferences>(
                    File.ReadAllText(ReasoningPreferencesPath)) ?? new AiUserPreferences();
            }
            catch
            {
                return new AiUserPreferences();
            }
        }

        private void Normalize()
        {
            Provider = NormalizeProvider(Provider, BaseUrl);
            BaseUrl = NormalizeBaseUrl(BaseUrl);
            ApiKey = (ApiKey ?? string.Empty).Trim();
            Model = (Model ?? string.Empty).Trim();
            Temperature = NormalizeTemperature(Temperature);
            TopP = NormalizeTopP(TopP);
            MaxOutputTokens = NormalizeMaxOutputTokens(MaxOutputTokens);
            MaxAgentCalls = NormalizeMaxAgentCalls(MaxAgentCalls);
            RequestTimeoutSeconds = NormalizeRequestTimeoutSeconds(RequestTimeoutSeconds);
            ModalStallSeconds = NormalizeModalStallSeconds(ModalStallSeconds);
            MaxRepetitionRecoveryAttempts = Math.Max(0, Math.Min(5, MaxRepetitionRecoveryAttempts));
            if (ModalCustomRules == null) ModalCustomRules = new List<ModalRuleItem>();
            if (SavedModelCatalog == null) SavedModelCatalog = new List<string>();
            ModalDecisionTimeoutSeconds = NormalizeModalDecisionTimeoutSeconds(ModalDecisionTimeoutSeconds);
            ToolCallTimeoutSeconds = NormalizeToolCallTimeoutSeconds(ToolCallTimeoutSeconds);
            BackgroundToolTimeoutMinutes = NormalizeBackgroundToolTimeoutMinutes(BackgroundToolTimeoutMinutes);
            VisualReviewTimeoutSeconds = NormalizeVisualReviewTimeoutSeconds(VisualReviewTimeoutSeconds);
            ContextWindowTokens = NormalizeContextWindowTokens(ContextWindowTokens);
            DimensionPrecision = NormalizeDimensionPrecision(DimensionPrecision);
            DrawingTemplatePath = (DrawingTemplatePath ?? string.Empty).Trim();
            DrawingBorderDefinitionName = (DrawingBorderDefinitionName ?? string.Empty).Trim();
            DrawingTitleBlockDefinitionName = (DrawingTitleBlockDefinitionName ?? string.Empty).Trim();
            DrawingSheetSize = NormalizeDrawingSheetSize(DrawingSheetSize);
            DrawingOrientation = NormalizeDrawingOrientation(DrawingOrientation);
            DrawingViewMode = NormalizeDrawingViewMode(DrawingViewMode);
            DrawingMaxSheetSize = NormalizeDrawingMaxSheetSize(DrawingMaxSheetSize);
            ResponseLanguage = NormalizeResponseLanguage(ResponseLanguage);
            UiLanguage = CadAI.Core.UiLanguage.Normalize(UiLanguage);
            ReasoningEffort = NormalizeReasoningEffort(ReasoningEffort);
            VoiceInputMode = NormalizeVoiceInputMode(VoiceInputMode);
            VoiceBaseUrl = string.IsNullOrWhiteSpace(VoiceBaseUrl) ? string.Empty : NormalizeBaseUrl(VoiceBaseUrl);
            VoiceApiKey = (VoiceApiKey ?? string.Empty).Trim();
            VoiceModel = string.IsNullOrWhiteSpace(VoiceModel) ? "whisper-1" : VoiceModel.Trim();
            VoiceLanguage = NormalizeVoiceLanguage(VoiceLanguage);
            CustomParametersJson = NormalizeCustomParametersJson(CustomParametersJson);
            AdditionalInstructions = (AdditionalInstructions ?? string.Empty).Trim();
        }

        public static double NormalizeTemperature(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? 0.6 : Math.Max(0.0, Math.Min(2.0, value));

        public static double NormalizeTopP(double value)
            => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? 1.0 : Math.Max(0.01, Math.Min(1.0, value));

        public static int NormalizeMaxOutputTokens(int value)
            => value <= 0 ? 0 : Math.Max(256, Math.Min(131072, value));

        public static int NormalizeMaxAgentCalls(int value) => Math.Max(0, value);

        public static string NormalizeReasoningEffort(string value)
        {
            string effort = (value ?? "auto").Trim().ToLowerInvariant();
            return new[] { "auto", "low", "medium", "high" }.Contains(effort) ? effort : "auto";
        }

        public static int NormalizeRequestTimeoutSeconds(int value)
            => value <= 0 ? DefaultRequestTimeoutSeconds : Math.Max(30, Math.Min(1800, value));

        /// <summary>卡死判定阈值：≥5s（否则正常慢调用会被误判），≤600s（否则比僵死超时还晚）。</summary>
        public static int NormalizeModalStallSeconds(int value)
            => value <= 0 ? DefaultModalStallSeconds : Math.Max(5, Math.Min(600, value));

        /// <summary>
        /// 弹窗决策单次模型请求超时：≥3s，≤60s。
        /// 上限刻意压得比对话请求低：它必须留在 ModalAgent 的决策等待窗口之内。
        /// </summary>
        public static int NormalizeModalDecisionTimeoutSeconds(int value)
            => value <= 0 ? DefaultModalDecisionTimeoutSeconds : Math.Max(3, Math.Min(60, value));

        /// <summary>
        /// 视觉校验单次请求超时：≥30s（低于此值大图上传必然误判超时），≤600s。
        /// </summary>
        public static int NormalizeVisualReviewTimeoutSeconds(int value)
            => value <= 0 ? DefaultVisualReviewTimeoutSeconds : Math.Max(30, Math.Min(600, value));

        /// <summary>工具调用超时：≥10s（再短会误杀正常建模），≤3600s（再长就等于不超时）。</summary>
        public static int NormalizeToolCallTimeoutSeconds(int value)
            => value <= 0 ? DefaultToolCallTimeoutSeconds : Math.Max(10, Math.Min(3600, value));

        /// <summary>后台工具超时：≥1 分钟，≤240 分钟。</summary>
        public static int NormalizeBackgroundToolTimeoutMinutes(int value)
            => value <= 0 ? DefaultBackgroundToolTimeoutMinutes : Math.Max(1, Math.Min(240, value));

        public static int NormalizeContextWindowTokens(int value)
            => value <= 0 ? 0 : Math.Max(8192, Math.Min(2000000, value));

        /// <summary>
        /// 实际用于压缩判定的上下文窗口：用户显式配置优先，否则按模型名推断。
        /// 压缩只在"下一次请求确实装不下"时触发，不再用一个与模型无关的固定阈值。
        /// </summary>
        public static int ResolveContextWindowTokens(AiSettings settings)
        {
            if (settings == null) return InferContextWindowTokens(string.Empty);
            int configured = NormalizeContextWindowTokens(settings.ContextWindowTokens);
            return configured > 0 ? configured : InferContextWindowTokens(settings.Model);
        }

        /// <summary>
        /// 按模型名推断上下文窗口（保守下限）。模型名千变万化，这里只识别常见家族；
        /// 推断值偏小时压缩会略早发生，但不会失败；推断偏大时网关会返回 context-limit 错误，
        /// AgentSession 的恢复分支会压缩后重发，所以两边的兜底都在。
        /// </summary>
        public static int InferContextWindowTokens(string model)
        {
            string name = (model ?? string.Empty).Trim().ToLowerInvariant();
            if (name.Length == 0) return 128000;

            if (name.Contains("gemini")) return 1000000;
            if (name.Contains("gpt-4.1") || name.Contains("gpt-5") || name.Contains("gpt-4.5")) return 1000000;
            if (name.Contains("claude")) return 200000;
            if (name.Contains("kimi") || name.Contains("moonshot")) return 200000;
            if (name.Contains("deepseek") || name.Contains("qwen") || name.Contains("glm")
                || name.Contains("llama") || name.Contains("mistral") || name.Contains("doubao")
                || name.Contains("hunyuan") || name.Contains("ernie")) return 128000;

            return 128000;
        }

        public static int NormalizeDimensionPrecision(int value) => Math.Max(0, Math.Min(8, value));

        /// <summary>出图图幅：只认 auto 或 A0-A4，其余一律归一到 auto。</summary>
        public static string NormalizeDrawingSheetSize(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant();
            switch (text)
            {
                case "A0":
                case "A1":
                case "A2":
                case "A3":
                case "A4":
                    return text;
                default:
                    return "auto";
            }
        }

        /// <summary>出图朝向：只认 auto / landscape / portrait，其余归一到 auto。</summary>
        public static string NormalizeDrawingOrientation(string value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "landscape":
                case "horizontal":
                case "横版":
                    return "landscape";
                case "portrait":
                case "vertical":
                case "竖版":
                    return "portrait";
                default:
                    return "auto";
            }
        }

        /// <summary>出图视图集合：只认 auto / three / single，其余归一到 auto（四视图）。</summary>
        public static string NormalizeDrawingViewMode(string value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "three":
                case "3":
                case "ortho":
                    return "three";
                case "single":
                case "front":
                case "1":
                    return "single";
                default:
                    return "auto";
            }
        }

        /// <summary>最大图幅：只认 A0-A4，其余归一到 A0。</summary>
        public static string NormalizeDrawingMaxSheetSize(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant();
            switch (text)
            {
                case "A1":
                case "A2":
                case "A3":
                case "A4":
                    return text;
                default:
                    return "A0";
            }
        }

        /// <summary>回答语言只有中文/英文两档；历史 auto 值就地归一到中文。</summary>
        public static string NormalizeResponseLanguage(string value)
        {
            string language = (value ?? string.Empty).Trim();
            if (language.Equals("en-US", StringComparison.OrdinalIgnoreCase)) return "en-US";
            return "zh-CN";
        }

        /// <summary>语音识别模式：只认 local / api，其余归一到 local。</summary>
        public static string NormalizeVoiceInputMode(string value)
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "api" || mode == "whisper" || mode == "remote" || mode == "openai")
                return "api";
            return "local";
        }

        /// <summary>语音识别语言：只认 zh / en / auto，其余归一到 zh。</summary>
        public static string NormalizeVoiceLanguage(string value)
        {
            string lang = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (lang == "en" || lang == "en-us" || lang == "english") return "en";
            if (lang == "auto") return "auto";
            return "zh";
        }

        public static string NormalizeCustomParametersJson(string value)
            => string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();

        public static string NormalizeProvider(string provider, string baseUrl)
        {
            if (!string.IsNullOrWhiteSpace(provider) &&
                !string.Equals(provider.Trim(), "Auto", StringComparison.OrdinalIgnoreCase))
                return provider.Trim();

            string url = baseUrl ?? string.Empty;
            if (url.IndexOf(AiDefaults.OllamaPortToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Ollama";
            if (url.IndexOf("dashscope", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("aliyun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("maas.", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Alibaba Cloud";
            if (url.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return "OpenAI";
            return "OpenAI Compatible";
        }

        public static string NormalizeBaseUrl(string value)
        {
            string url = string.IsNullOrWhiteSpace(value)
                ? AiDefaults.OllamaBaseUrl
                : value.Trim().TrimEnd('/');

            if (url.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                return url.Substring(0, url.Length - "/chat/completions".Length);
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return url.Substring(0, url.Length - "/chat/completions".Length);
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return url;
            return url + "/v1";
        }
    }
}
