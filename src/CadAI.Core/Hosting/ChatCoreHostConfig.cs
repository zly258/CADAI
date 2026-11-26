using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CadAI.Core
{
    /// <summary>
    /// 宿主相关的语义配置：系统提示词正文 + 代理循环里对工具名的语义分类
    /// （变更/视觉变更/仅图纸变更/未调用工具意图识别）以及界面展示文案。
    /// 默认配置面向 Autodesk AutoCAD（CADAI），支持通过宿主装配或 chatcore.host.json 覆盖。
    /// </summary>
    public sealed class ChatCoreHostConfig
    {
        private const string ConfigFileName = "chatcore.host.json";

        private static ChatCoreHostConfig _current;

        /// <summary>系统提示词正文模板；{WorkspaceRoot} 占位符在运行时替换。</summary>
        public string SystemPromptTemplate { get; set; }

        /// <summary>命中即视为"非变更"的工具前缀。</summary>
        public string[] NonMutationToolPrefixes { get; set; }
        /// <summary>命中即视为"非变更"的工具全名。</summary>
        public string[] NonMutationToolNames { get; set; }
        /// <summary>命中即视为"变更"的工具前缀。</summary>
        public string[] MutationToolPrefixes { get; set; }
        /// <summary>变更工具中会显著改变视觉结果的工具前缀。</summary>
        public string[] VisualMutationToolPrefixes { get; set; }
        /// <summary>只作用于图纸的变更工具前缀。</summary>
        public string[] DrawingOnlyMutationPrefixes { get; set; }
        /// <summary>只作用于图纸的变更工具全名。</summary>
        public string[] DrawingOnlyMutationNames { get; set; }
        /// <summary>识别"模型在正文里宣布要用工具却没发 tool_call"的工具名标记（子串匹配）。</summary>
        public string[] UninvokedIntentToolMarkers { get; set; }

        /// <summary>截图工具名。</summary>
        public string VisualCaptureToolName { get; set; }

        /// <summary>截图工具的参数 JSON。</summary>
        public string VisualCaptureArguments { get; set; }

        /// <summary>是否在截图前先探测文档可渲染内容。</summary>
        public bool VisualReviewGuardEnabled { get; set; }

        /// <summary>探测工具名（仅当 <see cref="VisualReviewGuardEnabled"/> 为真时使用）。</summary>
        public string VisualReviewGuardToolName { get; set; }

        /// <summary>"复核已完成"标记工具名（留空即永不匹配）。</summary>
        public string VisualReviewToolName { get; set; }

        /// <summary>是否在每次修改图纸后自动同步截屏并调用视觉模型复核。默认 false。</summary>
        public bool AutoVisualReviewAfterMutation { get; set; } = false;

        /// <summary>是否启用"变更后自动状态检查点"。AutoCAD 侧默认 false。</summary>
        public bool StateCheckpointEnabled { get; set; }

        /// <summary>状态对比工具名（留空即跳过）。</summary>
        public string StateCheckpointToolName { get; set; }

        /// <summary>豁免状态检查点的工具全名。</summary>
        public string[] NoStateCheckpointToolNames { get; set; }

        /// <summary>命中即视为图纸变更的工具前缀（完成后追加 [Mutation boundary] 提示）。</summary>
        public string[] DrawingReviewMutationPrefixes { get; set; }

        /// <summary>豁免图纸变更边界提示的工具全名。</summary>
        public string[] DrawingReviewExemptNames { get; set; }

        /// <summary>工具面按意图收窄策略（可选；为空时全量暴露）。</summary>
        public IToolScopePolicy ToolScopePolicy { get; set; }

        /// <summary>对话窗口欢迎卡正文（Markdown）。</summary>
        public string WelcomeText { get; set; }

        /// <summary>附件提示词里提到的产品名。</summary>
        public string ProductDisplayName { get; set; }

        /// <summary>宿主产品在界面标题上的展示名。</summary>
        public string UiProductTitle { get; set; }

        /// <summary>用户贴图后视觉模型的领域提取提示。</summary>
        public string ImageFactPromptHint { get; set; }

        /// <summary>宿主软件名（默认 "AutoCAD"）。</summary>
        public string HostProductName { get; set; }

        /// <summary>附件文件选择器的过滤器。</summary>
        public string AttachmentFileFilter { get; set; }

        /// <summary>宿主进程名（不含 .exe，默认 "acad"）。</summary>
        public string HostProcessName { get; set; }

        /// <summary>弹窗截图目录。</summary>
        public string ModalCaptureDirectory { get; set; }

        public static ChatCoreHostConfig Current
        {
            get
            {
                var config = _current;
                if (config != null) return config;
                config = LoadFromDisk() ?? CreateDefaults();
                _current = config;
                return config;
            }
        }

        /// <summary>宿主启动时装入显式配置（优先于磁盘上的 chatcore.host.json）。</summary>
        public static void Install(ChatCoreHostConfig config) => _current = config ?? CreateDefaults();

        private static ChatCoreHostConfig CreateDefaults()
        {
            return new ChatCoreHostConfig
            {
                SystemPromptTemplate = "You are CADAI, an AI assistant for Autodesk AutoCAD. Workspace: {WorkspaceRoot}.",

                NonMutationToolPrefixes = new[]
                {
                    "cad_doc_active", "cad_doc_list",
                    "cad_layout_list",
                    "cad_drawing_statistics", "cad_drawing_health_check",
                    "cad_layer_list",
                    "cad_text_style_list", "cad_text_font_audit",
                    "cad_selection_get",
                    "cad_query_", "cad_get_", "cad_count_",
                    "cad_view_", "cad_text_search", "cad_block_list", "cad_block_get_",
                    "cad_excel_read", "cad_excel_list", "cad_pdf_read", "cad_pdf_get",
                    "cad_skill_"
                },
                NonMutationToolNames = new string[0],

                MutationToolPrefixes = new[]
                {
                    "cad_doc_save",
                    "cad_layout_switch", "cad_layout_create", "cad_layout_rename",
                    "cad_layer_create", "cad_layer_set_",
                    "cad_text_style_set", "cad_text_font_repair",
                    "cad_selection_set",
                    "cad_draw_", "cad_dimension_", "cad_entity_",
                    "cad_text_modify", "cad_text_batch_replace", "cad_text_merge_",
                    "cad_text_align", "cad_text_distribute",
                    "cad_block_set_",
                    "cad_excel_write", "cad_excel_export", "cad_pdf_merge", "cad_pdf_split",
                    "cad_execute_command", "cad_batch_execute", "cad_audit_purge", "cad_export_pdf"
                },

                VisualMutationToolPrefixes = new[]
                {
                    "cad_text_merge_",
                    "cad_text_align",
                    "cad_text_distribute",
                    "cad_text_batch_replace",
                    "cad_entity_delete",
                    "cad_layout_create",
                    "cad_dimension_"
                },

                DrawingOnlyMutationPrefixes = new[] { "cad_" },
                DrawingOnlyMutationNames = new string[0],
                UninvokedIntentToolMarkers = new[] { "cad_" },

                VisualCaptureToolName = "cad_view_capture_screen",
                VisualCaptureArguments = "{\"dpi\":150}",
                AutoVisualReviewAfterMutation = false,
                VisualReviewGuardEnabled = false,
                VisualReviewGuardToolName = null,
                VisualReviewToolName = null,

                WelcomeText = null,
                ProductDisplayName = "CADAI",
                UiProductTitle = "CADAI 智能助手",
                ImageFactPromptHint =
                    "Extract only visual facts relevant to the current AutoCAD drawing task: "
                    + "drawn geometry, dimension values, annotation and note text, layer/linetype cues, "
                    + "title-block and table contents, layout/viewport arrangement, error messages, or visible UI state. ",
                HostProductName = "AutoCAD",
                AttachmentFileFilter = "图纸 / 图片|*.dwg;*.dxf;*.dwt;*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|所有文件|*.*",
                HostProcessName = "acad",
                ModalCaptureDirectory = null,

                StateCheckpointEnabled = false,
                StateCheckpointToolName = null,
                NoStateCheckpointToolNames = new string[0],

                DrawingReviewMutationPrefixes = new[]
                {
                    "cad_draw_", "cad_dimension_", "cad_text_", "cad_entity_",
                    "cad_layout_create", "cad_block_set_", "cad_layer_set_",
                    "cad_text_font_repair", "cad_audit_purge"
                },
                DrawingReviewExemptNames = new[]
                {
                    "cad_doc_save", "cad_export_pdf"
                }
            };
        }

        /// <summary>状态对比工具名（空 = 未提供，检查点会被跳过）。</summary>
        public static string StateCheckpointToolNameOrDefault()
        {
            try
            {
                if (!Current.StateCheckpointEnabled) return null;
                string name = Current.StateCheckpointToolName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>"复核完成"标记工具名（空 = 未提供）。</summary>
        public static string VisualReviewToolNameOrDefault()
        {
            try
            {
                string name = Current.VisualReviewToolName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>该工具是否豁免状态检查点（按全名）。</summary>
        public static bool IsStateCheckpointExempt(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            try
            {
                foreach (string name in Current.NoStateCheckpointToolNames ?? new string[0])
                {
                    if (string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>宿主进程名（空时按 AutoCAD 的 acad 处理）。</summary>
        public static string HostProcessNameOrDefault()
        {
            try
            {
                string name = Current.HostProcessName;
                return string.IsNullOrWhiteSpace(name) ? "acad" : name;
            }
            catch { return "acad"; }
        }

        /// <summary>弹窗截图目录：显式配置优先，否则按当前对话推导。</summary>
        public static string ModalCaptureDirectoryOrDefault()
        {
            try
            {
                string configured = Current.ModalCaptureDirectory;
                if (!string.IsNullOrWhiteSpace(configured)) return configured;
            }
            catch { }

            try
            {
                IHostEnvironment environment = ChatCoreHost.Environment;
                if (environment != null && !string.IsNullOrWhiteSpace(environment.WorkspaceRoot))
                {
                    return AiWorkspace.DialogCapturesDirectory;
                }
            }
            catch { }

            return null;
        }

        /// <summary>宿主软件名（空时按 AutoCAD 处理）。</summary>
        public static string HostProductNameOrDefault()
        {
            try
            {
                string name = Current.HostProductName;
                return string.IsNullOrWhiteSpace(name) ? "AutoCAD" : name;
            }
            catch { return "AutoCAD"; }
        }

        /// <summary>界面标题用产品名（空时回落 Branding.ProductName）。</summary>
        public static string UiProductTitleOrDefault()
        {
            try
            {
                string title = Current.UiProductTitle;
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }
            catch { }
            return Branding.ProductName;
        }

        /// <summary>附件选择器过滤器。</summary>
        public static string AttachmentFileFilterOrDefault()
        {
            const string fallback =
                "图纸 / 图片|*.dwg;*.dxf;*.dwt;*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|所有文件|*.*";
            try
            {
                string filter = Current.AttachmentFileFilter;
                return string.IsNullOrWhiteSpace(filter) ? fallback : filter;
            }
            catch { return fallback; }
        }

        private static ChatCoreHostConfig LoadFromDisk()
        {
            foreach (string directory in CandidateDirectories())
            {
                string path = Path.Combine(directory, ConfigFileName);
                if (!File.Exists(path)) continue;
                try
                {
                    var serializer = AgentJson.New();
                    var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (root == null) continue;
                    ChatCoreHostConfig config = CreateDefaults();

                    if (root.TryGetValue("systemPromptFile", out object promptFile) && promptFile != null)
                    {
                        string promptPath = Environment.ExpandEnvironmentVariables(
                            Convert.ToString(promptFile) ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(promptPath) && File.Exists(promptPath))
                            config.SystemPromptTemplate = File.ReadAllText(promptPath);
                    }

                    if (root.TryGetValue("toolSemantics", out object semanticsRaw) &&
                        semanticsRaw is Dictionary<string, object> semantics)
                    {
                        config.NonMutationToolPrefixes = Strings(semantics, "nonMutationPrefixes", config.NonMutationToolPrefixes);
                        config.NonMutationToolNames = Strings(semantics, "nonMutationNames", config.NonMutationToolNames);
                        config.MutationToolPrefixes = Strings(semantics, "mutationPrefixes", config.MutationToolPrefixes);
                        config.VisualMutationToolPrefixes = Strings(semantics, "visualMutationPrefixes", config.VisualMutationToolPrefixes);
                        config.DrawingOnlyMutationPrefixes = Strings(semantics, "drawingOnlyMutationPrefixes", config.DrawingOnlyMutationPrefixes);
                        config.DrawingOnlyMutationNames = Strings(semantics, "drawingOnlyMutationNames", config.DrawingOnlyMutationNames);
                        config.UninvokedIntentToolMarkers = Strings(semantics, "uninvokedIntentMarkers", config.UninvokedIntentToolMarkers);
                    }

                    if (root.TryGetValue("visualReview", out object visualRaw) &&
                        visualRaw is Dictionary<string, object> visual)
                    {
                        config.VisualCaptureToolName = Scalar(visual, "captureTool", config.VisualCaptureToolName);
                        config.VisualCaptureArguments = Scalar(visual, "captureArguments", config.VisualCaptureArguments);
                        config.VisualReviewGuardToolName = Scalar(visual, "guardTool", config.VisualReviewGuardToolName);
                        if (visual.TryGetValue("autoReviewAfterMutation", out object autoReview) && autoReview != null)
                        {
                            try { config.AutoVisualReviewAfterMutation = Convert.ToBoolean(autoReview); } catch { }
                        }
                        if (visual.TryGetValue("guardEnabled", out object guard) && guard != null)
                        {
                            try { config.VisualReviewGuardEnabled = Convert.ToBoolean(guard); } catch { }
                        }
                    }

                    if (root.TryGetValue("ui", out object uiRaw) && uiRaw is Dictionary<string, object> ui)
                    {
                        config.ProductDisplayName = Scalar(ui, "productName", config.ProductDisplayName);
                        config.HostProductName = Scalar(ui, "hostProductName", config.HostProductName);
                        config.ImageFactPromptHint = Scalar(ui, "imageFactPromptHint", config.ImageFactPromptHint);
                        config.AttachmentFileFilter = Scalar(ui, "attachmentFileFilter", config.AttachmentFileFilter);
                        string welcomeFile = Scalar(ui, "welcomeFile", null);
                        if (!string.IsNullOrWhiteSpace(welcomeFile))
                        {
                            string welcomePath = Environment.ExpandEnvironmentVariables(welcomeFile);
                            if (File.Exists(welcomePath)) config.WelcomeText = File.ReadAllText(welcomePath);
                        }
                        else
                        {
                            config.WelcomeText = Scalar(ui, "welcomeText", config.WelcomeText);
                        }
                    }

                    return config;
                }
                catch
                {
                }
            }
            return null;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory)) yield return assemblyDirectory;
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory)) yield return baseDirectory;
        }

        private static string[] Strings(Dictionary<string, object> root, string key, string[] fallback)
        {
            if (!root.TryGetValue(key, out object value) || !(value is System.Collections.IEnumerable sequence) || value is string)
                return fallback;
            var result = new List<string>();
            foreach (object item in sequence)
            {
                string text = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            return result.Count > 0 ? result.ToArray() : fallback;
        }

        private static string Scalar(Dictionary<string, object> root, string key, string fallback)
        {
            if (!root.TryGetValue(key, out object value) || value == null) return fallback;
            string text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
    }
}
