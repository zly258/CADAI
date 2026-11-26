namespace CadAI.Core
{
    /// <summary>统一设置窗口打开时默认激活的页签。</summary>
    public enum SettingsInitialTab
    {
        Chat = 0,
        Translation = 1,
        Voice = 2,
        CadRules = 3
    }

    /// <summary>
    /// 翻译模型设置 DTO：由宿主传入统一设置窗口，保持 ChatCore 对翻译工程零依赖。
    /// </summary>
    public sealed class TranslationModelSettingsDto
    {
        public bool UseDefaultModel { get; set; } = true;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        /// <summary>部署侧默认端点（用于勾选「使用默认」时的连接测试与回填）。</summary>
        public string DefaultBaseUrl { get; set; } = string.Empty;
        public string DefaultApiKey { get; set; } = string.Empty;
        public string DefaultModel { get; set; } = string.Empty;

        public TranslationModelSettingsDto Clone() => new TranslationModelSettingsDto
        {
            UseDefaultModel = UseDefaultModel,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            DefaultBaseUrl = DefaultBaseUrl,
            DefaultApiKey = DefaultApiKey,
            DefaultModel = DefaultModel
        };
    }

    /// <summary>
    /// AutoCAD 专属定向翻译规则 DTO（标题栏属性标签映射）。
    /// </summary>
    public sealed class CadTranslationRulesDto
    {
        public string TitleBlockSourceTag { get; set; } = "DESCRIPTION_2";
        public string TitleBlockTargetTag { get; set; } = "DESCRIPTION_5";

        public CadTranslationRulesDto Clone() => new CadTranslationRulesDto
        {
            TitleBlockSourceTag = TitleBlockSourceTag,
            TitleBlockTargetTag = TitleBlockTargetTag
        };
    }

    /// <summary>
    /// 统一设置窗口的宿主扩展配置。
    /// </summary>
    public sealed class UnifiedSettingsOptions
    {
        public string WindowTitle { get; set; } = "设置";
        public SettingsInitialTab InitialTab { get; set; } = SettingsInitialTab.Chat;
        public TranslationModelSettingsDto Translation { get; set; }
        public CadTranslationRulesDto CadRules { get; set; }
    }
}
