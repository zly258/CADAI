using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CadAI.Core
{
    /// <summary>
    /// OpenAI 兼容语音转文字（/audio/transcriptions）客户端。
    /// 支持 Whisper / FunASR / SenseVoice / SiliconFlow / One-API 等兼容接口。
    /// </summary>
    internal sealed class VoiceTranscriptionClient
    {
        private static readonly HttpClient SharedHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        /// <summary>
        /// 将 WAV 音频上传到 /audio/transcriptions 并返回识别文本。
        /// </summary>
        public static async Task<string> TranscribeAsync(
            byte[] wavBytes,
            string baseUrl,
            string apiKey,
            string model,
            string language,
            CancellationToken cancellationToken)
        {
            if (wavBytes == null || wavBytes.Length <= 44)
                return string.Empty;

            string normalizedBase = AiSettings.NormalizeBaseUrl(baseUrl);
            string endpoint = normalizedBase.TrimEnd('/') + "/audio/transcriptions";
            string effectiveModel = string.IsNullOrWhiteSpace(model) ? "whisper-1" : model.Trim();
            string effectiveLanguage = AiSettings.NormalizeVoiceLanguage(language);

            using (var form = new MultipartFormDataContent())
            {
                var audioContent = new ByteArrayContent(wavBytes);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(audioContent, "file", "voice.wav");
                form.Add(new StringContent(effectiveModel), "model");
                if (!string.Equals(effectiveLanguage, "auto", StringComparison.OrdinalIgnoreCase))
                    form.Add(new StringContent(effectiveLanguage), "language");

                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Content = form;
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                    using (HttpResponseMessage response = await SharedHttp.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            string snippet = (body ?? string.Empty).Trim();
                            if (snippet.Length > 200) snippet = snippet.Substring(0, 200) + "…";
                            throw new InvalidOperationException(
                                "语音识别请求失败（HTTP " + (int)response.StatusCode + "）：" + snippet);
                        }

                        return ParseTranscriptionText(body);
                    }
                }
            }
        }

        /// <summary>
        /// 解析语音识别响应：优先读取 JSON 中的 text 字段；非 JSON 时按纯文本返回。
        /// </summary>
        internal static string ParseTranscriptionText(string body)
        {
            string trimmed = (body ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    object parsed = AgentJson.New().DeserializeObject(trimmed);
                    if (parsed is IDictionary<string, object> map)
                    {
                        foreach (var kv in map)
                        {
                            if (string.Equals(kv.Key, "text", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                                return Convert.ToString(kv.Value)?.Trim() ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    // 非标准 JSON 退回原文本
                }
            }

            return trimmed;
        }
    }
}
