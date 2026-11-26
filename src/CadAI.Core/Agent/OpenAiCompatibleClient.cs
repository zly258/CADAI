#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace CadAI.Core
{
    internal sealed class AgentToolCall { public string Id { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string ArgumentsJson { get; set; } = string.Empty; public object Raw { get; set; } = null!; }
    internal sealed class AgentUsage { public int PromptTokens { get; set; } public int CompletionTokens { get; set; } public int TotalTokens { get; set; } public bool IsEstimated { get; set; } public TimeSpan Duration { get; set; } }
    internal sealed class AgentCompletion { public string Content { get; set; } = string.Empty; public string ReasoningContent { get; set; } = string.Empty; public List<AgentToolCall> ToolCalls { get; } = new List<AgentToolCall>(); public object? RawToolCalls { get; set; } public AgentUsage Usage { get; set; } = new AgentUsage(); }
    internal sealed class AgentImageReview { public string Content { get; set; } = string.Empty; public AgentUsage Usage { get; set; } = new AgentUsage(); }
    internal sealed class StreamingCallbacks { public Action<string> OnContentDelta { get; set; } = null!; public Action<string> OnReasoningDelta { get; set; } = null!; public Action OnStreamReset { get; set; } = null!; public Action<int, string> OnRetry { get; set; } = null!; }

    internal sealed class OpenAiCompatibleClient : IDisposable
    {
        private readonly AiSettings _settings;
        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json = AgentJson.New();

        public OpenAiCompatibleClient(AiSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _settings.Temperature = AiSettings.NormalizeTemperature(_settings.Temperature);
            _settings.TopP = AiSettings.NormalizeTopP(_settings.TopP);
            _settings.MaxOutputTokens = AiSettings.NormalizeMaxOutputTokens(_settings.MaxOutputTokens);
            _settings.RequestTimeoutSeconds = AiSettings.NormalizeRequestTimeoutSeconds(_settings.RequestTimeoutSeconds);
            // 全局 Timeout 在 .NET Framework 上会覆盖"整个响应体读取"——对流式长回答
            // 是错误语义（读到 300s 被拦腰掐断）。改为 Infinite，由每条请求自行用
            // linked CTS 控制超时：流式=首字节超时（见 StreamOnceAsync），非流式=整请求超时（见 PostAsync）。
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
        }

        public async Task<AgentCompletion> CompleteStreamingAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<object> tools, StreamingCallbacks callbacks, CancellationToken cancellationToken)
        {
            callbacks = callbacks ?? new StreamingCallbacks();

            // 2026-10-14：模型回退链。
            // 起因：上游中途对当前出口返回 400「User location is not supported」，同批模型随后全部 503；
            // 产品此前一次上游错误就让整轮对话失败，用户看到的是"产品坏了"。
            // 现在：主模型不可用（永久错误，或瞬时错误重试耗尽）时，按配置顺序换备用模型重发同一请求。
            // 换模型发生在**本请求尚未产生任何工具副作用**的位置，因此重发是安全的。
            List<string> chain = BuildModelChain();
            Exception? last = null;

            for (int modelIndex = 0; modelIndex < chain.Count; modelIndex++)
            {
                string model = chain[modelIndex];
                bool isLastModel = modelIndex == chain.Count - 1;

                for (int attempt = 1; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return await StreamOnceAsync(messages, tools, callbacks, cancellationToken, model).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ShouldRetry(ex, attempt))
                    {
                        last = ex;
                        callbacks.OnStreamReset?.Invoke();
                        callbacks.OnRetry?.Invoke(attempt, ex.Message);
                        await Task.Delay(AiRequestPolicy.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 永久错误，或瞬时错误已重试耗尽 → 这个模型不再可用。
                        last = ex;
                        break;
                    }
                }

                if (isLastModel) break;

                string next = chain[modelIndex + 1];
                callbacks.OnStreamReset?.Invoke();
                callbacks.OnRetry?.Invoke(0, $"模型 {model} 不可用，改用备用模型 {next} 重试。原因：{Shorten(last?.Message)}");
            }

            throw last ?? new InvalidOperationException("AI 请求失败：没有可用模型。");
        }

        /// <summary>构造模型尝试顺序：主模型在前，备用模型（若启用）在后。</summary>
        private List<string> BuildModelChain()
        {
            var chain = new List<string> { _settings.Model };
            if (_settings.AutoFallbackOnUpstreamError)
            {
                foreach (string fallback in _settings.ResolveFallbackModels())
                    chain.Add(fallback);
            }
            return chain;
        }

        private static string Shorten(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "未知错误";
            string text = message!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 120 ? text : text.Substring(0, 120) + "…";
        }

        // 多张图片视觉审查，将多个 image_url 批量传递给视觉模型。
        //
        // timeoutSeconds / maxAttempts 由调用方按"这是增强手段"来收紧：
        // 视觉校验要上传数 MB 截图，默认沿用对话的 300s × 4 次重试会让一次卡住的
        // 校验把主任务拖住十几分钟，而它并不是用户要的结果。
        public async Task<AgentImageReview> ReviewImagesAsync(IReadOnlyList<string> imagePaths, string prompt, Action<int, string> onRetry, CancellationToken cancellationToken, int timeoutSeconds = 0, int maxAttempts = 0)
        {
            var validPaths = imagePaths?.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
            if (string.IsNullOrWhiteSpace(_settings.Model) || validPaths == null || validPaths.Count == 0)
                return new AgentImageReview { Content = string.Empty, Usage = new AgentUsage { IsEstimated = true } };

            var contentItems = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt }
            };
            foreach (string path in validPaths)
            {
                var (mimeType, base64) = PrepareImageDataUrl(path);
                if (string.IsNullOrEmpty(base64)) continue;
                string dataUrl = $"data:{mimeType};base64,{base64}";
                contentItems.Add(new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = dataUrl }
                });
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = _settings.Model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = contentItems.ToArray()
                    }
                }
            };
            ApplyGenerationOptions(payload);

            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    Dictionary<string, object> root = await PostAsync(payload, cancellationToken, timeoutSeconds).ConfigureAwait(false);
                    stopwatch.Stop();
                    Dictionary<string, object> choice = FirstDictionary(GetValue(root, "choices"));
                    Dictionary<string, object> message = AsDictionary(GetValue(choice, "message"));
                    string text = Convert.ToString(GetValue(message, "content") ?? string.Empty);
                    AgentUsage usage = ReadUsage(root) ?? EstimateUsage(prompt, text);
                    usage.Duration = stopwatch.Elapsed;
                    return new AgentImageReview { Content = text, Usage = usage };
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ShouldRetry(ex, attempt, maxAttempts))
                {
                    onRetry?.Invoke(attempt, ex.Message);
                    await Task.Delay(AiRequestPolicy.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 为大模型视觉接口（Vision API）准备图像数据与 MIME 类型。
        /// 1. 自动校正 MIME 类型（image/png, image/jpeg, image/webp）；
        /// 2. 拦截超大图（如高分辨率扫描图纸可达 20000x14000 像素，导致上游模型报 400 拒收或反解 DOS 炸弹）；
        ///    超限时按比例降采样到安全视口分辨率（2560px），既保留工程细节又保证模型成功接收；
        /// 3. 非标格式（BMP/TIFF 等）自动转码为标准 PNG。
        /// </summary>
        private static (string mimeType, string base64) PrepareImageDataUrl(string path)
        {
            const int maxVisionDimension = 2560;
            const long maxDirectFileSize = 8 * 1024 * 1024; // 8MB

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return (string.Empty, string.Empty);

                string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                bool isStandardWebImage = ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";

                int pixelWidth = 0;
                int pixelHeight = 0;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var decoder = BitmapDecoder.Create(
                            fs,
                            BitmapCreateOptions.DelayCreation,
                            BitmapCacheOption.None);
                        if (decoder.Frames.Count > 0)
                        {
                            pixelWidth = decoder.Frames[0].PixelWidth;
                            pixelHeight = decoder.Frames[0].PixelHeight;
                        }
                    }
                }
                catch
                {
                    // 仅读头失败时后续尝试全量解码
                }

                int maxSide = Math.Max(pixelWidth, pixelHeight);
                if (isStandardWebImage && info.Length <= maxDirectFileSize && maxSide > 0 && maxSide <= maxVisionDimension)
                {
                    string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                                : ext == ".webp" ? "image/webp"
                                : "image/png";
                    return (mime, Convert.ToBase64String(File.ReadAllBytes(path)));
                }

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                if (maxSide > maxVisionDimension)
                {
                    if (pixelWidth >= pixelHeight)
                        bi.DecodePixelWidth = maxVisionDimension;
                    else
                        bi.DecodePixelHeight = maxVisionDimension;
                }
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                if (bi.CanFreeze) bi.Freeze();

                using (var ms = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bi));
                    encoder.Save(ms);
                    return ("image/png", Convert.ToBase64String(ms.ToArray()));
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("OpenAiClient.PrepareImage", ex);
                try
                {
                    string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                    string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                                : ext == ".webp" ? "image/webp"
                                : "image/png";
                    return (mime, Convert.ToBase64String(File.ReadAllBytes(path)));
                }
                catch { return (string.Empty, string.Empty); }
            }
        }

        /// <summary>
        /// 非流式单轮补全：图纸翻译等专项任务使用；modelOverride 为空时回落到对话模型。
        /// 不带工具、不带历史，重试策略与流式补全保持一致。
        /// timeoutSeconds 给定时用它覆盖全局请求超时（翻译批量要短超时，避免单条卡死整批）。
        /// </summary>
        public async Task<string> CompleteOnceAsync(string systemPrompt, string userPrompt, string modelOverride, CancellationToken cancellationToken, int timeoutSeconds = 0)
        {
            string model = string.IsNullOrWhiteSpace(modelOverride) ? _settings.Model : modelOverride.Trim();

            if (timeoutSeconds > 0)
            {
                using (var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timed.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        return await CompleteOnceCoreAsync(systemPrompt, userPrompt, model, timed.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("翻译请求超时（" + timeoutSeconds + " 秒）。");
                    }
                }
            }
            return await CompleteOnceCoreAsync(systemPrompt, userPrompt, model, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> CompleteOnceCoreAsync(string systemPrompt, string userPrompt, string model, CancellationToken cancellationToken)
        {
            // 翻译等专项请求：确定性任务，温度固定 0（覆盖用户对话温度）。
            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["temperature"] = 0.0,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt ?? string.Empty },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = userPrompt ?? string.Empty }
                }
            };
            ApplyGenerationOptions(payload);
            payload.Remove("reasoning_effort");
            payload["temperature"] = 0.0;

            // 默认关闭思考：单轮补全服务于翻译/弹窗决策这类确定性任务，只要结果不要推理。
            // 不同网关的字段名不同，三个都带上；若网关不认识这些字段（400/thinking 相关报错），
            // 剥除后立即重试一次。开关取对话设置的同名语义（翻译宿主经 TranslationSettings
            // 的客户端包装自行翻译，翻译思考开关在宿主侧把该值写进克隆设置）。
            bool thinkingOff = _settings.DisableThinkingOnCompletion;
            if (thinkingOff)
            {
                payload["enable_thinking"] = false;
                payload["think"] = false;
                payload["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };
            }
            bool thinkingFieldsStripped = false;

            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Dictionary<string, object> root = await PostAsync(payload, cancellationToken).ConfigureAwait(false);
                    Dictionary<string, object> choice = FirstDictionary(GetValue(root, "choices"));
                    Dictionary<string, object> message = AsDictionary(GetValue(choice, "message"));
                    return Convert.ToString(GetValue(message, "content") ?? string.Empty);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                           && thinkingOff && !thinkingFieldsStripped
                                           && IsThinkingRejection(ex))
                {
                    // 网关不支持思考开关字段（如 hy-mt2 返回 400 "does not support thinking"）：
                    // 剥除这些字段后立即重试，不消耗普通重试次数。
                    thinkingFieldsStripped = true;
                    payload.Remove("enable_thinking");
                    payload.Remove("think");
                    payload.Remove("chat_template_kwargs");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ShouldRetry(ex, attempt))
                {
                    await Task.Delay(AiRequestPolicy.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>识别「网关不支持思考开关字段」类错误（HTTP 400 + thinking 相关文案）。</summary>
        private static bool IsThinkingRejection(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                if (message.IndexOf("400", StringComparison.Ordinal) >= 0 &&
                    (message.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0
                     || message.IndexOf("chat_template", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
                if (message.IndexOf("does not support", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    message.IndexOf("think", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool ShouldRetry(Exception exception, int attempt, int maxAttempts = 0)
        {
            int limit = maxAttempts > 0 ? maxAttempts : AiRequestPolicy.MaxAttempts;
            return attempt < limit && AiRequestPolicy.IsRetryable(AiRequestPolicy.Classify(exception));
        }

        private async Task<AgentCompletion> StreamOnceAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<object> tools, StreamingCallbacks callbacks, CancellationToken cancellationToken, string? modelOverride = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(modelOverride) ? _settings.Model : modelOverride!,
                ["messages"] = messages.Select(ToApiMessage).ToArray(),
                ["stream"] = true
            };
            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = tools.ToArray();
                payload["tool_choice"] = "auto";
            }
            ApplyGenerationOptions(payload);
            if (ShouldRequestStreamUsage(_settings.Provider))
                payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };

            string requestJson = _json.Serialize(payload);
            var stopwatch = Stopwatch.StartNew();
            AgentUsage? usage = null;
            var result = new AgentCompletion();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var contentLoop = new RepetitionDetector();
            var reasoningLoop = new RepetitionDetector();
            var toolParts = new Dictionary<int, ToolCallAccumulator>();

            using (var request = new HttpRequestMessage(HttpMethod.Post, GetChatCompletionsEndpoint()))
            {
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                // 流式请求不能用全局 HttpClient.Timeout（覆盖整个响应体读取）：长回答
                // 超过 300s 会被 TaskCanceledException 从中间掐断，内容截断后还被当成
                // 瞬时错误整段重发。这里用 linked CTS 做"首字节超时"——拿到响应头后
                // 立即解除，正文读取只受用户 cancellationToken 约束。
                TimeSpan firstByteTimeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds);
                using (var firstByteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    firstByteCts.CancelAfter(firstByteTimeout);
                    try
                    {
                        using (HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstByteCts.Token).ConfigureAwait(false))
                        {
                            // 响应头已到：解除首字节超时，正文流不再受它约束。
                            firstByteCts.CancelAfter(Timeout.InfiniteTimeSpan);
                            // net48 的语义：SendAsync 完成后，它的 token 对后续读取不再生效。
                            // 用户点「停止」时主动 Dispose 响应，让 ReadLineAsync 立刻抛异常退出，
                            // 否则半开连接会让整轮永久挂死。
                            using (cancellationToken.Register(() =>
                            {
                                try { response.Dispose(); } catch { }
                            }))
                            {
                            if (!response.IsSuccessStatusCode)
                            {
                                string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                throw new InvalidOperationException($"AI 请求失败 {(int)response.StatusCode}: {error}");
                            }
                            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                        // 不用 `while (!reader.EndOfStream) { await ReadLineAsync(); }`：
                        // EndOfStream 会对缓冲做一次同步探测读，等于每行都多一次无谓的同步 IO；
                        // 直接读到 null 为止既少一次判断，也让循环语义与流结束一一对应。
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) break;
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            string data = line.Substring(5).Trim();
                            if (data.Length == 0) continue;
                            if (data == "[DONE]") break;
                            Dictionary<string, object>? root;
                            try { root = _json.Deserialize<Dictionary<string, object>>(data); } catch { continue; }
                            if (root == null) continue;
                            AgentUsage? reported = ReadUsage(root);
                            if (reported != null) usage = reported;
                            Dictionary<string, object> choice = FirstDictionary(GetValue(root, "choices"));
                            Dictionary<string, object> delta = AsDictionary(GetValue(choice, "delta"));
                            if (delta.Count == 0) delta = AsDictionary(GetValue(choice, "message"));
                            if (delta.Count == 0) continue;
                            bool loopGuardEnabled = _settings.AutoRetryOnReasoningLoop;
                            string text = ReadContent(GetValue(delta, "content"));
                            if (!string.IsNullOrEmpty(text))
                            {
                                // 退化重复发生在单次流里，轮次预算和 no-progress 守卫都看不到；
                                // 仅在启用防死循环保护时检测，一旦判定局部周期重复即中止交由上层纠偏重试。
                                if (loopGuardEnabled && contentLoop.Append(text))
                                    throw new AiRepetitionException("模型回复内容陷入原地重复。");
                                content.Append(text);
                                callbacks.OnContentDelta?.Invoke(text);
                            }
                            string think = ReadReasoning(delta);
                            if (!string.IsNullOrEmpty(think))
                            {
                                if (loopGuardEnabled && reasoningLoop.Append(think))
                                    throw new AiRepetitionException("模型思考内容陷入原地重复。");
                                reasoning.Append(think);
                                // 软死循环兜底：复杂 CAD 几何推演在深度思考模式下可达数万字，
                                // 仅当思考超 80,000 字符仍未产出任何正文或工具调用时才中断纠偏。
                                if (loopGuardEnabled && reasoning.Length > 80000 && content.Length == 0 && toolParts.Count == 0)
                                    throw new AiRepetitionException("模型思考过程过长且未收敛，疑似陷入死循环。");
                                callbacks.OnReasoningDelta?.Invoke(think);
                            }
                            foreach (Dictionary<string, object> call in Dictionaries(GetValue(delta, "tool_calls")))
                            {
                                int index = ConvertToInt(GetValue(call, "index"), toolParts.Count);
                                if (!toolParts.TryGetValue(index, out ToolCallAccumulator acc)) { acc = new ToolCallAccumulator(); toolParts[index] = acc; }
                                string id = Convert.ToString(GetValue(call, "id") ?? string.Empty);
                                if (!string.IsNullOrWhiteSpace(id)) acc.Id = id;
                                Dictionary<string, object> function = AsDictionary(GetValue(call, "function"));
                                string name = Convert.ToString(GetValue(function, "name") ?? string.Empty);
                                string args = Convert.ToString(GetValue(function, "arguments") ?? string.Empty);
                                if (!string.IsNullOrEmpty(name)) acc.Name.Append(name);
                                if (!string.IsNullOrEmpty(args)) acc.Arguments.Append(args);
                            }
                        }
                            }
                        }
                    }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 只有首字节超时（非用户取消）会走到这里：转成可重试的超时异常。
                        throw new TimeoutException("AI 服务在 " + (int)firstByteTimeout.TotalSeconds + " 秒内未返回响应头。");
                    }
                    catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                    {
                        // 主动 Dispose 响应会让 ReadLineAsync 抛 ObjectDisposedException/IOException；
                        // 统一转成 OperationCanceledException，让上层按"用户取消"处理而不是重试。
                        throw new OperationCanceledException("AI 流式响应已被取消。", ex, cancellationToken);
                    }
                }
            }

            result.Content = content.ToString();
            result.ReasoningContent = reasoning.ToString();
            var rawCalls = new List<object>();
            var outputForEstimate = new StringBuilder(result.Content).Append(result.ReasoningContent);
            foreach (KeyValuePair<int, ToolCallAccumulator> pair in toolParts.OrderBy(p => p.Key))
            {
                ToolCallAccumulator acc = pair.Value;
                string id = string.IsNullOrWhiteSpace(acc.Id) ? Guid.NewGuid().ToString("N") : acc.Id;
                string name = acc.Name.ToString();
                string args = acc.Arguments.Length == 0 ? "{}" : acc.Arguments.ToString();
                var raw = new Dictionary<string, object> { ["id"] = id, ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = name, ["arguments"] = args } };
                rawCalls.Add(raw);
                result.ToolCalls.Add(new AgentToolCall { Id = id, Name = name, ArgumentsJson = args, Raw = raw });
                outputForEstimate.Append(name).Append(args);
            }
            result.RawToolCalls = rawCalls.Count == 0 ? null : rawCalls.ToArray();
            stopwatch.Stop();
            result.Usage = usage ?? EstimateUsage(requestJson, outputForEstimate.ToString());
            result.Usage.Duration = stopwatch.Elapsed;
            return result;
        }

        private async Task<Dictionary<string, object>> PostAsync(Dictionary<string, object> payload, CancellationToken cancellationToken, int timeoutSeconds = 0)
        {
            string json = _json.Serialize(payload);
            // 非流式请求：全局 Timeout 已改为 Infinite，这里用 linked CTS 恢复整请求超时语义。
            // timeoutSeconds 允许调用方覆盖（视觉校验用更短的窗口，见 ReviewImagesAsync）。
            int effectiveTimeout = timeoutSeconds > 0 ? timeoutSeconds : _settings.RequestTimeoutSeconds;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = await _http.PostAsync(GetChatCompletionsEndpoint(), content, timeoutCts.Token).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AI 请求失败 {(int)response.StatusCode}: {body}");
                    return _json.Deserialize<Dictionary<string, object>>(body) ?? throw new InvalidOperationException("AI 返回了空 JSON。");
                }
            }
        }

        private string GetChatCompletionsEndpoint()
        {
            string baseUrl = (_settings.BaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("AI 地址未配置。");
            baseUrl = baseUrl.TrimEnd('/');
            return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
        }

        private static AgentUsage? ReadUsage(Dictionary<string, object> root)
        {
            Dictionary<string, object> usage = AsDictionary(GetValue(root, "usage"));
            if (usage.Count == 0) return null;
            int prompt = ReadInt(usage, "prompt_tokens", "input_tokens");
            int completion = ReadInt(usage, "completion_tokens", "output_tokens");
            int total = ReadInt(usage, "total_tokens");
            if (total <= 0) total = prompt + completion;
            if (prompt <= 0 && completion <= 0 && total <= 0) return null;
            return new AgentUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = total, IsEstimated = false };
        }

        private void ApplyGenerationOptions(Dictionary<string, object> payload)
        {
            if (payload == null) return;

            payload["top_p"] = AiSettings.NormalizeTopP(_settings.TopP);

            int maxOutputTokens = AiSettings.NormalizeMaxOutputTokens(_settings.MaxOutputTokens);
            if (maxOutputTokens > 0)
                payload["max_tokens"] = maxOutputTokens;

            ApplyCustomParameters(payload);
            ApplyReasoningOptions(payload);
        }

        private void ApplyCustomParameters(Dictionary<string, object> payload)
        {
            string json = AiSettings.NormalizeCustomParametersJson(_settings.CustomParametersJson);
            if (json == "{}") return;

            try
            {
                object raw = _json.DeserializeObject(json);
                if (!(raw is Dictionary<string, object> values)) return;

                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "model", "messages", "tools", "tool_choice", "stream", "stream_options",
                    "temperature", "top_p", "max_tokens", "max_completion_tokens",
                    "enable_thinking", "reasoning_effort"
                };

                foreach (KeyValuePair<string, object> pair in values)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || reserved.Contains(pair.Key)) continue;
                    payload[pair.Key] = pair.Value;
                }
            }
            catch
            {
                // 这段 JSON 由设置界面校验。历史遗留的格式错误直接忽略，
                // 否则用户还没重新打开设置，所有 AI 请求就已经失败了。
            }
        }

        private void ApplyReasoningOptions(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            string effort = AiSettings.NormalizeReasoningEffort(_settings.ReasoningEffort);
            if (_settings.ReasoningEnabled)
            {
                if (effort != "auto" && !string.Equals(_settings.Provider, "Alibaba Cloud", StringComparison.OrdinalIgnoreCase))
                    payload["reasoning_effort"] = effort;
                if (string.Equals(_settings.Provider, "Alibaba Cloud", StringComparison.OrdinalIgnoreCase))
                    payload["enable_thinking"] = true;
                return;
            }
            string provider = (_settings.Provider ?? string.Empty).Trim();
            if (provider.Equals("Alibaba Cloud", StringComparison.OrdinalIgnoreCase))
                payload["enable_thinking"] = false;
            else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
                     provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                payload["reasoning_effort"] = "none";
        }

        private static bool ShouldRequestStreamUsage(string provider)
        {
            string value = (provider ?? string.Empty).Trim();
            return value.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Alibaba Cloud", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
        }

        internal static AgentUsage EstimateUsage(string input, string output)
        {
            int prompt = EstimateTokens(input); int completion = EstimateTokens(output);
            return new AgentUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion, IsEstimated = true };
        }

        internal static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int ascii = 0, nonAscii = 0;
            foreach (char c in text) { if (c <= 127) ascii++; else nonAscii++; }
            return nonAscii + (int)Math.Ceiling(ascii / 4.0);
        }

        private static int ReadInt(Dictionary<string, object> dictionary, params string[] keys) { foreach (string key in keys) { try { object? value = GetValue(dictionary, key); if (value != null) return Convert.ToInt32(value); } catch { } } return 0; }
        internal static string ReadContent(object? value)
        {
            if (value is string text) return text;
            var content = new StringBuilder();
            foreach (Dictionary<string, object> part in Dictionaries(value))
            {
                object? raw = GetValue(part, "text");
                if (raw is string segment) content.Append(segment);
                else if (raw is Dictionary<string, object> nested && GetValue(nested, "value") is string nestedText)
                    content.Append(nestedText);
            }
            return content.ToString();
        }

        private static string ReadReasoning(Dictionary<string, object> delta) { foreach (string key in new[] { "reasoning_content", "reasoning", "thinking" }) { string value = Convert.ToString(GetValue(delta, key) ?? string.Empty); if (!string.IsNullOrEmpty(value)) return value; } return string.Empty; }
        private static Dictionary<string, object> ToApiMessage(AgentMessage message) { var result = new Dictionary<string, object> { ["role"] = message.Role, ["content"] = message.Content ?? string.Empty }; if (!string.IsNullOrWhiteSpace(message.Name)) result["name"] = message.Name; if (!string.IsNullOrWhiteSpace(message.ToolCallId)) result["tool_call_id"] = message.ToolCallId; if (message.ToolCalls != null) result["tool_calls"] = message.ToolCalls; return result; }
        private static int ConvertToInt(object? value, int fallback) { try { return Convert.ToInt32(value); } catch { return fallback; } }
        private static object? GetValue(Dictionary<string, object>? dictionary, string key) { if (dictionary == null || string.IsNullOrEmpty(key)) return null; dictionary.TryGetValue(key, out object? value); return value; }
        private static Dictionary<string, object> AsDictionary(object? value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
        private static Dictionary<string, object> FirstDictionary(object? value) => Dictionaries(value).FirstOrDefault() ?? new Dictionary<string, object>();
        private static IEnumerable<Dictionary<string, object>> Dictionaries(object? value) { if (value == null) yield break; if (value is Dictionary<string, object> one) { yield return one; yield break; } if (value is IEnumerable sequence && !(value is string)) foreach (object item in sequence) if (item is Dictionary<string, object> dictionary) yield return dictionary; }
        public void Dispose() => _http.Dispose();
        private sealed class ToolCallAccumulator { public string Id { get; set; } = string.Empty; public StringBuilder Name { get; } = new StringBuilder(); public StringBuilder Arguments { get; } = new StringBuilder(); }
    }
}
