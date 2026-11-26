using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal sealed partial class AgentSession
    {
        /// <summary>
        /// 经宿主 IToolExecutor 调用外部 MCP 工具并返回结果 JSON
        /// （保留拆分前 McpProcessClient.CallTool 的语义：失败以异常抛出）。
        /// </summary>
        private string CallExternalTool(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => _executor.Execute(toolName, argumentsJson, cancellationToken)?.Json ?? "{}";

        private void AppendExternalCheckpoint(IAgentUi ui, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 状态对比工具名由宿主配置提供；宿主没这个概念（例如 CAD）时整段跳过，
                // 不再白跑一次调用、也不往上下文里塞一条 "Unknown tool"。
                string tool = ChatCoreHostConfig.StateCheckpointToolNameOrDefault();
                if (string.IsNullOrWhiteSpace(tool)) return;

                string checkpoint = CallExternalTool(tool, "{\"reset\":false}", cancellationToken);
                // 图纸文档上模型状态对比必然失败（它要零件/装配的 ComponentDefinition）。
                // 这里直接跳过，不把一条 invalid_state 塞进上下文——那会让模型误判任务出错，
                // 也白费一次 IPC 往返。图纸侧写入的正确性由各自的回读校验负责。
                if (IsInvalidStateFailure(checkpoint))
                {
                    AiExceptionGuard.Log(
                        "Agent.ExternalCheckpoint",
                        new InvalidOperationException("Skipped state checkpoint: the active document does not support model state comparison."));
                    return;
                }

                string compact = AgentContextCompactor.CompactToolResult(checkpoint, 1800);
                _taskStateSnapshot = compact;
                _messages.Add(new AgentMessage
                {
                    Role = "system",
                    Content = "[Auto mutation checkpoint]\n" + compact + "\nIf expected changes did not occur or the model is unhealthy, do not declare done; read facts and fix."
                });
                SaveHistory();
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Agent.ExternalCheckpoint", ex);
            }
        }

        /// <summary>检查点结果里出现 invalid_state 时视为"该文档不支持状态对比"，静默跳过。</summary>
        private static bool IsInvalidStateFailure(string checkpoint)
        {
            if (string.IsNullOrWhiteSpace(checkpoint)) return false;
            return checkpoint.IndexOf("invalid_state", StringComparison.OrdinalIgnoreCase) >= 0
                || checkpoint.IndexOf("requires a part or assembly", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 建模/出图变更后自动抓一组视角截图，并把图片挂进上下文。
        ///
        /// 关键点：光把 PNG 路径写进上下文模型是看不见的——必须把图片本身作为
        /// image_url 内容块喂回去，模型才会真正"看到"它。OpenAiCompatibleClient.ReviewImagesAsync
        /// 已经封装了 base64 data URL 的拼装，这里复用它，避免重复实现一套。
        ///
        /// 任何失败都只记诊断、不打断主流程：截图是增强手段，绝不能因为截不到图就让任务失败。
        /// </summary>
        /// <summary>
        /// 自动视觉校验的截图参数：3 个视角 + 1024×768。
        ///
        /// 历史故障：原来抓 iso/front/top/right 四个 1600×1200 视角，每张 PNG 实测约 1 MB，
        /// base64 后单次校验请求体约 5.3 MB，网关经常拖过整请求超时才回；用户看到的是
        /// "卡了几分钟、完全不知道在做什么"。校验只需要看清轮廓/特征/比例，
        /// 3 视角 × 1024×768 足够，请求体降到约 1.5 MB。
        /// 这里显式写全参数（而不是依赖工具默认值），避免以后默认值变化时
        /// 悄悄把校验请求体又抬回去。
        ///
        /// 2026-10-15：改为经 <see cref="ChatCoreHostConfig.VisualCaptureArguments"/> 取值，
        /// 让非 AutoCAD 宿主（AutoCAD）能换成自己的截图工具与参数，而不再写死工具名。
        /// </summary>
        private static string ReviewCaptureArguments => ResolveVisualCaptureArguments();

        /// <summary>
        /// 建模/出图变更后的自动视觉验证入口（分级执行）：
        ///
        /// 1. 草图期（还没有实体）：不截图、不调视觉模型——线框截图既看不出对错，
        ///    一次完整视觉校验（截图 + 上传 + 审查）又费时几十秒。只注入一条确定性
        ///    统计摘要（草图数/约束状态），让模型用 cad_sketch_query 自行验证。
        /// 2. 实体期：截一组 iso/front/top 截图（1024×768）送视觉模型校验一次。
        ///
        /// 历史问题：这里曾叠加跑两套验证（cad_view_capture_screen 的 10 张拟合图 +
        /// cad_view_capture_screen 的 3 张截图），简单建模一半时间耗在重复截图上，
        /// 用户看到的是"卡了几分钟不知道在做什么"。现在收敛为一套，且只在实体期执行。
        /// 任何失败都只记诊断、不打断主流程：视觉校验是增强手段，绝不能因为它让任务失败。
        /// </summary>
        private async Task AppendExternalVisualVerificationAsync(IAgentUi ui, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool sketchPhaseOnly;
            if (!HasRenderableContent(out sketchPhaseOnly))
            {
                // 连草图都没有：截图必然空白，只留一行历史痕迹。
                _history.Record("AUTO VISUAL VERIFY SKIPPED", "No solid/surface bodies and no sketches; screenshots would be empty.", false);
                return;
            }
            if (sketchPhaseOnly)
            {
                // 草图期降级：确定性摘要替代截图，避免把 2D 线框当缺陷
                // （历史会话里弱模型曾据此建议用 Blender/Unity/cadquery 修模型）。
                string stats = SummarizeDocumentStatistics();
                _messages.Add(new AgentMessage
                {
                    Role = "system",
                    Content = "[Sketch-phase verification] Visual review is deferred until a solid body exists - wireframe "
                        + "screenshots at this stage are the EXPECTED mid-state, not a defect. Do not rebuild the model "
                        + "because of them. Verify progress with deterministic facts instead: cad_sketch_query "
                        + "(constraintStatus, counts.lines/dimensionConstraints) and cad_document_info "
                        + "(statistics)." + stats
                });
                _history.Record("AUTO VISUAL VERIFY DEFERRED", "Sketch phase - deterministic sketch checks replace screenshots.", false);
                SaveHistory();
                return;
            }

            List<string> paths;
            try
            {
                string raw = CallExternalTool(VisualCaptureToolName, ReviewCaptureArguments, cancellationToken);
                paths = ExtractCapturedImagePaths(raw);
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Agent.AutoCapture", ex);
                return;
            }

            if (paths.Count == 0) return;
            string taskGoal = ResolveUserTaskGoal();
            string prompt = _englishStatus
                ? $"[Visual verification] Current part design objective: {taskGoal}. These are the current standard views of the model after your last change. Check them strictly against this objective: orientation, missing or misplaced features, and overall proportions. Do NOT guess unrelated consumer products (e.g. pen slots, desk trays, sliders, keypads), do not emit disclaimers or script snippets, and report only objective geometric facts."
                : $"[视觉校验] 当前零件的设计目标：{taskGoal}。以下是改动后模型的标准视角截图。请严格对照上述设计目标检查几何特征的准确性、朝向与比例。严禁将中间毛坯脱靶猜测为无关日用品（如笔槽、收纳盒、滑轨或按键等），严禁输出无工具免责声明或代码脚本，仅提供客观的几何观察事实。";

            // 复核实现与"模型主动截图"那条路共用 AppendImageFactsAsync，避免两套提示词/预算各写一遍。
            await AppendImageFactsAsync(ui, paths, prompt, "VISION REVIEW", cancellationToken).ConfigureAwait(false);
        }

        private string ResolveUserTaskGoal()
        {
            if (_messages == null) return "用户指定的 CAD 零件设计任务";
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    string text = msg.Content as string ?? string.Empty;
                    if (text.StartsWith("[系统自动", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("[视觉校验", StringComparison.OrdinalIgnoreCase))
                        continue;

                    const string marker = "[Original user request";
                    int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int endLine = text.IndexOf('\n', idx);
                        if (endLine >= 0)
                        {
                            string after = text.Substring(endLine + 1).Trim();
                            int nextSection = after.IndexOf("\n\n[", StringComparison.Ordinal);
                            if (nextSection > 0) after = after.Substring(0, nextSection).Trim();
                            if (!string.IsNullOrWhiteSpace(after))
                                return LimitGoalLength(after, 120);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                        return LimitGoalLength(text, 120);
                }
            }
            return "用户指定的 CAD 零件设计任务";
        }

        private static string LimitGoalLength(string text, int maxChars)
        {
            string clean = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxChars ? clean : clean.Substring(0, maxChars) + "…";
        }

        /// <summary>
        /// 工具产出的图片（模型主动调截图工具）→ 视觉模型 → 把视觉事实注入上下文。
        ///
        /// 与自动补截图的区别只在提示词：这里告诉模型"这是你刚才那次工具调用拍到的画面"，
        /// 让它据此判断下一步，而不是"我替你校验了改动"。
        /// 若截图中包含工程图图纸（sheet/drawing），则自适应注入专业的工程制图审图提示词。
        /// </summary>
        private async Task AppendToolImageFactsAsync(IAgentUi ui, List<string> paths, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Count == 0) return;
            cancellationToken.ThrowIfCancellationRequested();

            string prompt = ResolveToolImageReviewPrompt(paths);

            await AppendImageFactsAsync(ui, paths, prompt, "TOOL SCREENSHOT", cancellationToken).ConfigureAwait(false);
        }

        private string ResolveToolImageReviewPrompt(List<string> paths)
        {
            bool hasDrawingSheet = paths.Any(p =>
            {
                string fn = System.IO.Path.GetFileName(p).ToLowerInvariant();
                return fn.Contains("sheet") || fn.Contains("drawing") || fn.Contains("dwg") || fn.Contains("idw");
            });

            if (hasDrawingSheet)
            {
                return _englishStatus
                    ? "[Engineering Drawing AI Visual Review] This is a rendered screenshot of the CAD drawing sheet. "
                        + "Carefully audit the drawing against mechanical engineering drafting standards (GB/T 4458 / ISO): "
                        + "1. Scale validity: Verify view scales are strictly integer engineering ratios (1:x or x:1), never irregular decimals like 0.009:1 or 1:1.5. "
                        + "2. Annotation alignment & collision: Point out overlapping dimension texts, text crossing dimension lines/leaders, and non-aligned sawtooth steps. "
                        + "3. Leader routes: Flag leaders crossing through solid body contours unnecessarily. "
                        + "4. Overall & essential dimensions: Check whether overall length, width, and height dimensions are present. "
                        + "5. Title block completeness: Report any empty mandatory fields. Output objective issues and concrete repair actions."
                    : "[工程图 AI 视觉审图] 这是 CAD 工程图纸的真实渲染截图。请严格依据机械制图国家标准进行图面视觉审查：\n"
                        + "1. 比例规范性：检查视图比例是否为标准工程整数比（1:x 或 x:1，如 1:100、1:150），严禁出现类似 0.009:1 或 1:1.5 等非标小数；\n"
                        + "2. 标注对齐与文字重叠：检查同向并列尺寸是否平直对齐共线（指出高低错落成锯齿阶梯的尺寸），找出所有文字重叠互踩、尺寸线穿过文字的位置；\n"
                        + "3. 引线走线：检查是否有引线长距离穿透工件实体轮廓造成倒挂，指出应就近标注或分流至剖视图的特征；\n"
                        + "4. 总体尺寸：检查零件的总长、总宽、总高等关键总体尺寸是否齐全；\n"
                        + "5. 标题栏与图框：指出图名、图号、比例、材质等字段是否留白未填。\n"
                        + "请输出客观的图面缺陷清单并给出具体的工具调整建议。";
            }

            return _englishStatus
                ? "[Tool screenshot] These images were produced by the tool call you just made. Describe what is actually visible and relevant to the user's request, then decide the next step. Do not guess content that is not visible."
                : "[工具截图] 这些是你刚才那次工具调用拍到的画面。请描述实际可见、且与用户诉求相关的内容，然后据此决定下一步；看不清的就不要猜。";
        }

        /// <summary>
        /// 把一组图片交给视觉模型，并把返回的事实（压缩后）注入上下文。
        /// 自动视觉校验与"模型主动截图"两条路共用这一份实现，避免两套提示词/预算各写一遍。
        /// 任何失败都只记诊断、不打断主流程：看图是增强手段。
        /// </summary>
        private async Task AppendImageFactsAsync(IAgentUi ui, List<string> paths, string prompt,
                                                 string historyTag, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Count == 0) return;

            int timeoutSeconds = _settings != null && _settings.VisualReviewTimeoutSeconds > 0
                ? _settings.VisualReviewTimeoutSeconds
                : AiSettings.DefaultVisualReviewTimeoutSeconds;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _history.Record(
                historyTag,
                "start: images=" + paths.Count
                + "; payload≈" + EstimatedPayloadMb(paths) + " MB (base64)"
                + "; timeout=" + timeoutSeconds + "s"
                + "; maxAttempts=" + AiRequestPolicy.ReviewMaxAttempts,
                false);
            int contextCharacters = 0;

            try
            {
                ui.BeginWait("正在看图（" + paths.Count + " 张）", TimeSpan.FromSeconds(timeoutSeconds));

                using (var client = new OpenAiCompatibleClient(_settings))
                {
                    AgentImageReview review = await client.ReviewImagesAsync(
                        paths,
                        prompt,
                        (attempt, message) =>
                        {
                            string reason = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                            if (reason.Length > 160) reason = reason.Substring(0, 160) + "…";
                            _history.Record(historyTag + " RETRY", "attempt=" + attempt + "; " + reason, false);
                            ui.SetActivityHint("看图未完成，正在自动重试（第 " + attempt + " 次）：" + reason);
                        },
                        cancellationToken,
                        timeoutSeconds,
                        AiRequestPolicy.ReviewMaxAttempts).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(review?.Content))
                    {
                        string sanitized = ReviewContextBudget.Sanitize(review.Content);
                        string forContext = ReviewContextBudget.Compact(sanitized);
                        if (!string.IsNullOrWhiteSpace(forContext))
                        {
                            string envelope = "[系统自动截图自检反馈 · 仅供当前零件几何形态自检参考]\n"
                                + "【重要提示】以下内容由后台视觉自检模块生成，绝非用户的新需求！"
                                + "请严格围绕用户最初的工程图纸和真实设计意图继续建模；"
                                + "如果当前模型处于中间阶段，请按原定建模计划继续，严禁偏离原图纸任务，严禁将中间形态擅自脑补为无关日用品（如笔槽/收纳盒/滑轨等）！\n\n"
                                + forContext;
                            _messages.Add(new AgentMessage { Role = "user", Content = envelope });
                            contextCharacters = envelope.Length;
                        }
                        if (!string.IsNullOrWhiteSpace(sanitized))
                            ui.AppendContent(sanitized);
                    }
                    if (review?.Usage != null)
                    {
                        _history.RecordUsage("vision-review", review.Usage);
                        ui.Usage("vision-review", review.Usage);
                    }
                }

                _history.Record(
                    historyTag + " DONE",
                    "elapsed=" + stopwatch.Elapsed.TotalSeconds.ToString("0.000") + "s"
                    + "; contextChars=" + contextCharacters + " (cap " + ReviewContextBudget.MaxCharacters + ")",
                    false);
                SaveHistory();
            }
            catch (Exception ex)
            {
                _history.Record(
                    historyTag + " FAILED",
                    "elapsed=" + stopwatch.Elapsed.TotalSeconds.ToString("0.000") + "s; " + ex.Message,
                    false);
                AiExceptionGuard.Log("Agent.ImageFacts", ex);
            }
            finally
            {
                ui.EndWait();
            }
        }

        /// <summary>截图转成 base64 后的大致请求体量级（MB），用于事后判断超时是否由体积引起。</summary>
        private static string EstimatedPayloadMb(List<string> paths)
        {
            long bytes = 0;
            foreach (string path in paths)
            {
                try { bytes += new System.IO.FileInfo(path).Length; } catch { }
            }
            // base64 膨胀约 4/3，再加 JSON 包装；只用于日志，取一位小数即可。
            return (bytes * 4.0 / 3.0 / (1024 * 1024)).ToString("0.0");
        }

        /// <summary>从 capture 工具的 JSON 结果里取出实际落盘的 PNG 路径。</summary>
        /// <summary>截图工具名（宿主可换；默认 AutoCAD 的 cad_view_capture_screen）。</summary>
        private static string VisualCaptureToolName
        {
            get
            {
                try
                {
                    string name = ChatCoreHostConfig.Current.VisualCaptureToolName;
                    return string.IsNullOrWhiteSpace(name) ? "cad_view_capture_screen" : name;
                }
                catch { return "cad_view_capture_screen"; }
            }
        }

        /// <summary>截图参数 JSON（宿主可换）。</summary>
        private static string ResolveVisualCaptureArguments()
        {
            try
            {
                string args = ChatCoreHostConfig.Current.VisualCaptureArguments;
                return string.IsNullOrWhiteSpace(args)
                    ? "{\"angles\":\"iso,front,top\",\"width\":1024,\"height\":768}"
                    : args;
            }
            catch { return "{\"angles\":\"iso,front,top\",\"width\":1024,\"height\":768}"; }
        }

        private static List<string> ExtractCapturedImagePaths(string payload)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(payload)) return result;

            // 与本文件其余解析保持一致：用 JavaScriptSerializer，不引入 Newtonsoft。
            var serializer = AgentJson.New();

            // 工具结果可能被包在 MCP 的 content[].text 里，两种形态都要兼容。
            string json = payload;
            try
            {
                var outer = serializer.DeserializeObject(payload) as Dictionary<string, object>;
                if (outer != null && outer.TryGetValue("content", out object rawContent)
                    && rawContent is System.Collections.IEnumerable list)
                {
                    foreach (object part in list)
                    {
                        if (!(part is Dictionary<string, object> piece)) continue;
                        if (!piece.TryGetValue("text", out object textValue) || textValue == null) continue;
                        string inner = Convert.ToString(textValue);
                        if (!string.IsNullOrWhiteSpace(inner)) { json = inner; break; }
                    }
                }
            }
            catch { }

            try
            {
                var parsed = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (parsed == null || !parsed.TryGetValue("images", out object rawImages)
                    || !(rawImages is System.Collections.IEnumerable images))
                    return result;

                foreach (object item in images)
                {
                    if (!(item is Dictionary<string, object> entry)) continue;
                    bool ok = entry.TryGetValue("ok", out object okValue)
                        && Convert.ToBoolean(okValue, CultureInfo.InvariantCulture);
                    if (!ok) continue;
                    string path = entry.TryGetValue("path", out object pathValue)
                        ? Convert.ToString(pathValue, CultureInfo.InvariantCulture)
                        : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!System.IO.File.Exists(path)) continue;
                    if (result.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                    result.Add(path);
                }
            }
            catch { }

            return result;
        }

        /// <summary>活动零件是否已有可渲染的实体/曲面体；查询失败按"有"处理，不让守卫本身阻断截图。</summary>
        private bool HasRenderableContent(out bool sketchPhaseOnly)
        {
            sketchPhaseOnly = false;

            // 宿主可以整个关掉这道守卫（CAD 图纸永远有内容可看，探测纯属浪费一次往返）。
            try
            {
                if (!ChatCoreHostConfig.Current.VisualReviewGuardEnabled) return true;
            }
            catch { }

            try
            {
                string guardTool = ChatCoreHostConfig.Current.VisualReviewGuardToolName;
                if (string.IsNullOrWhiteSpace(guardTool)) return true;

                string info = CallExternalTool(guardTool, "{}");
                var serializer = AgentJson.New();
                // 工具结果可能包在 MCP 的 content[].text 里，先剥掉外壳（与
                // ExtractCapturedImagePaths 的解析方式一致）。
                string json = info;
                var outer = serializer.DeserializeObject(info) as Dictionary<string, object>;
                if (outer != null && outer.TryGetValue("content", out object rawContent)
                    && rawContent is System.Collections.IEnumerable pieces)
                {
                    foreach (object piece in pieces)
                    {
                        if (!(piece is Dictionary<string, object> entry)) continue;
                        if (!entry.TryGetValue("text", out object textValue) || textValue == null) continue;
                        string inner = Convert.ToString(textValue);
                        if (!string.IsNullOrWhiteSpace(inner)) { json = inner; break; }
                    }
                }
                var parsed = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (parsed == null || !parsed.TryGetValue("data", out object rawData))
                    return true;
                var data = rawData as Dictionary<string, object>;
                if (data == null || !data.TryGetValue("statistics", out object rawStats))
                    return true;
                var stats = rawStats as Dictionary<string, object>;
                if (stats == null)
                    return true;
                int solid = stats.TryGetValue("surfaceBodies", out object s) ? Convert.ToInt32(s, CultureInfo.InvariantCulture) : 0;
                if (solid > 0) { sketchPhaseOnly = false; return true; }
                // 草图期（无实体但有草图线）也要截图：草图线在视口里可见，
                // 用户明确要求这一阶段能拿到"草图图片"做视觉校验。
                // 只有真正什么都没有（连草图都没有）才跳过空白截图。
                int sketches = stats.TryGetValue("sketches", out object sk) ? Convert.ToInt32(sk, CultureInfo.InvariantCulture) : 0;
                sketchPhaseOnly = sketches > 0;
                return sketches > 0;
            }
            catch
            {
                // 查询本身失败时不拦截截图：宁可多截一张，也不能让守卫吞掉正常流程。
                sketchPhaseOnly = false;
                return true;
            }
        }

        /// <summary>取活动文档统计的紧凑摘要（草图数/参数数/实体数），供草图期确定性上下文用；失败返回空串。</summary>
        private string SummarizeDocumentStatistics()
        {
            try
            {
                string guardTool = ChatCoreHostConfig.Current.VisualReviewGuardToolName;
                if (string.IsNullOrWhiteSpace(guardTool)) return string.Empty;

                string info = CallExternalTool(guardTool, "{}");
                var serializer = AgentJson.New();
                string json = info;
                var outer = serializer.DeserializeObject(info) as Dictionary<string, object>;
                if (outer != null && outer.TryGetValue("content", out object rawContent)
                    && rawContent is System.Collections.IEnumerable pieces)
                {
                    foreach (object piece in pieces)
                    {
                        if (!(piece is Dictionary<string, object> entry)) continue;
                        if (!entry.TryGetValue("text", out object textValue) || textValue == null) continue;
                        string inner = Convert.ToString(textValue);
                        if (!string.IsNullOrWhiteSpace(inner)) { json = inner; break; }
                    }
                }
                var parsed = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (parsed != null && parsed.TryGetValue("data", out object rawData) && rawData is Dictionary<string, object> data
                    && data.TryGetValue("statistics", out object rawStats) && rawStats is Dictionary<string, object> stats)
                {
                    string Value(string key) => stats.TryGetValue(key, out object v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "?";
                    return $" Current counts: sketches={Value("sketches")}, features={Value("features")}, parameters={Value("parameters")}, solidBodies={Value("surfaceBodies")}.";
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// 这次变更是否属于"图纸类变更"（决定要不要追加 Mutation boundary 系统提示）。
        /// 前缀与豁免名单都来自宿主配置（AutoCAD 默认 cad_draw_* / cad_dimension_*；
        /// CAD 宿主换成 cad_* 的那几条），否则 CAD 的变更永远拿不到这条边界提示。
        /// </summary>
        private static bool NeedsDrawingReview(string toolName)
        {
            string name = toolName ?? string.Empty;
            if (name.Length == 0) return false;

            ChatCoreHostConfig semantics;
            try { semantics = ChatCoreHostConfig.Current; }
            catch { return false; }

            foreach (string exempt in semantics.DrawingReviewExemptNames ?? new string[0])
            {
                if (string.Equals(exempt, name, StringComparison.OrdinalIgnoreCase)) return false;
            }

            foreach (string prefix in semantics.DrawingReviewMutationPrefixes ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(prefix)) continue;
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

    }
}
