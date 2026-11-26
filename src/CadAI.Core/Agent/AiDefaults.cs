using System.Globalization;

namespace CadAI.Core
{
    /// <summary>
    /// 本地对话模型端点默认值的单一来源。
    /// "http://localhost:11434/v1" 曾被复制到多处，而 <see cref="AiSettings.NormalizeProvider"/>
    /// 会从含字面量 ":11434" 的 URL 中嗅探 Ollama 端口。两者都收在这里，改端口只需改一处。
    /// </summary>
    internal static class AiDefaults
    {
        /// <summary>默认本地 Ollama 服务器的主机名。</summary>
        public const string OllamaHost = "localhost";

        /// <summary>默认 Ollama 端口。该字面量只存在于这一处。</summary>
        public const int OllamaPort = 11434;

        /// <summary>不含 OpenAI 兼容路径的服务器根地址，例如 http://localhost:11434。</summary>
        public static readonly string OllamaBaseUrl =
            "http://" + OllamaHost + ":" + OllamaPort.ToString(CultureInfo.InvariantCulture);

        /// <summary>聊天客户端使用的 OpenAI 兼容基础 URL，例如 http://localhost:11434/v1。</summary>
        public static readonly string OllamaOpenAiBaseUrl = OllamaBaseUrl + "/v1";

        /// <summary>本地 Ollama 服务器的默认模型名。</summary>
        public const string OllamaDefaultModel = "qwen2.5:14b";

        /// <summary>
        /// 内置单模型档位的 Id。此前 "enterprise" 字面量散落在 AiSettings 的
        /// 构造、EnsureProfiles、Clone 等 6 处，Clone 还把它硬编码回去，
        /// 会覆盖用户已选择的活动模型。统一收在这里，只改一处。
        /// </summary>
        public const string EnterpriseModelId = "enterprise";

        /// <summary>
        /// provider 为 "Auto" 时用于识别 Ollama 端点的端口令牌：
        /// ':' 加 <see cref="OllamaPort"/>。
        /// </summary>
        public static readonly string OllamaPortToken = ":" + OllamaPort.ToString(CultureInfo.InvariantCulture);
    }
}
