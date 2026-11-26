using System;
using System.Collections.Generic;
using CadAI.Core;

namespace CadAI.Host.AutoCAD.Hosting
{
    /// <summary>
    /// AutoCAD 宿主的工具面收窄策略（对应 AutoCAD 侧的
    /// <c>AutoCADToolScopePolicy</c>）。插件通过
    /// <see cref="ChatCoreHostConfig.ToolScopePolicy"/> 注入，ChatCore 只认接口。
    ///
    /// 背景（2026-10-16）：CAD 侧此前**没有**策略，80+ 个 <c>cad_*</c> 工具每一轮都常驻上下文。
    /// 当时的理由写在 AgentSession 的注释里——"CAD 工具本来就不多"。实际代价是：
    ///   - 每轮请求都要为全量工具 schema 付预填充 token（实测占提示词的大头）；
    ///   - 模型面对大量选项更容易挑错工具（"批量换文字"时去调 <c>cad_batch_set_layer_states</c>）。
    /// 之所以不加策略就"看起来没事"，是因为不给策略等于不做任何收窄——不会出错，只是慢和散。
    ///
    /// 设计（刻意与 AutoCAD 保持一致的三条）：
    ///   1. **域名取并集**：命中多个域时全部生效（"批量替换文字并补标注"要同时拿到 batch 与
    ///      dimension）；不再"首个命中即返回"。
    ///   2. **无命中回退全量**：认不出意图时返回 <see cref="AllScope"/>，把全部工具交给模型。
    ///      宁可多给 token，也不让用户换句话再说一遍（AutoCAD 侧用的是"回落 core 工具"，
    ///      因为它的 core 面本来就宽；CAD 的域划分更细，回落 core 太容易漏工具）。
    ///   3. **核心工具常开**：任何具体域都附带 <see cref="CoreTools"/>（文档/选择/实体查询/视图/技能），
    ///      避免"说了翻译就连查一下图纸都做不到"。
    ///
    /// 规则的形态：<c>"cad_layer_*"</c> 是前缀族，<c>"cad_export_pdf"</c> 是整名。
    /// 域划分与 <c>skills/dwgai/skill.manifest.json</c> 的 <c>routingHints.toolGroups</c> 对齐，
    /// 完备性由 <c>CadToolScopePolicyTests</c> 守着：**每个已注册的 <c>cad_*</c> 工具
    /// 至少要能被一个具体域看到**（只被 all 域看到不算——那等于没分域）。
    /// </summary>
    internal sealed class CadToolScopePolicy : IToolScopePolicy
    {
        /// <summary>
        /// 无命中时的全量作用域。见类型注释第 2 条：认不出意图就不收窄。
        /// </summary>
        internal const string AllScope = "all";

        /// <summary>
        /// 任何具体域都附带的核心工具（常开）。取的都是"看一眼/定位一下"这类前置动作；
        /// 它们在任何任务里都可能被需要，塞进每个域比让模型失败一次再换句话便宜得多。
        /// </summary>
        internal static readonly string[] CoreTools =
        {
            "cad_skill_list", "cad_skill_read",
            "cad_doc_active_info", "cad_doc_list",
            "cad_selection_get",
            "cad_query_entities", "cad_get_entity_info", "cad_count_entities_by_type",
            "cad_view_zoom_extents", "cad_view_capture_screen",
            "cad_drawing_statistics", "cad_drawing_health_check"
        };

        /// <summary>
        /// 域名 → 规则表。域划分镜像 <c>skills/dwgai/skill.manifest.json</c> 的
        /// <c>routingHints.toolGroups</c>（那边由 DwgToolSurfaceConsistencyTests 守着与注册表一致）。
        /// 规则以 <c>*</c> 结尾为前缀族，否则为整名。
        /// 注意：translation 域已随 2026-10-23 产品拆分删除——<c>cad_translate_*</c>/<c>cad_glossary_*</c>
        /// 工具退出对话工具面，翻译类请求由模型自身能力完成（收不到任何域时回退全量，见类型注释第 2 条）。
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] ScopeRules =
        {
            Pair("skill", "cad_skill_*"),
            Pair("document", "cad_doc_*"),
            Pair("layout", "cad_layout_*"),
            Pair("statistics", "cad_drawing_statistics", "cad_drawing_health_check"),
            Pair("layer", "cad_layer_list", "cad_layer_create", "cad_layer_set_state"),
            Pair("style", "cad_text_style_*", "cad_text_font_*"),
            Pair("selection", "cad_selection_*"),
            Pair("entity", "cad_entity_*", "cad_query_entities", "cad_get_entity_info", "cad_count_entities_by_type"),
            Pair("geometry", "cad_draw_*"),
            Pair("dimension", "cad_dimension_*", "cad_leader_*"),
            Pair("text",
                "cad_text_search", "cad_text_modify", "cad_text_batch_replace",
                "cad_text_merge_to_mtext", "cad_text_align", "cad_text_distribute"),
            Pair("block", "cad_block_*"),
            Pair("view", "cad_view_*"),
            Pair("delivery", "cad_execute_command", "cad_audit_purge", "cad_export_pdf"),
            Pair("batch", "cad_batch_*", "cad_compare_drawings"),
            Pair("table", "cad_table_*", "cad_virtual_table_detect"),
            Pair("xref", "cad_xref_*"),
            Pair("analysis",
                "cad_measure_*", "cad_layer_isolate", "cad_export_dxf",
                "cad_block_insert", "cad_export_drawing_json"),
            Pair("office", "cad_excel_*", "cad_pdf_*")
        };

        /// <summary>
        /// 关键词 → 域名。**全部命中都生效（并集）**，所以表里的顺序只影响可读性，不影响结果。
        /// 中文与英文关键词并列：宿主不知道用户会用哪种语言。
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] TextRouting =
        {
            Pair("skill", "技能", "有什么工具", "工具清单", "skill"),
            Pair("document", "保存", "打开文档", "当前文档", "文档信息", "save", "document"),
            Pair("layout", "布局", "视口", "打印", "出图配置", "layout", "viewport", "plot"),
            Pair("statistics", "统计", "审图", "图纸健康", "健康检查", "statistics", "health"),
            Pair("layer", "图层", "线宽", "线型", "layer", "linetype"),
            Pair("style", "文字样式", "字体", "text style", "font"),
            Pair("selection", "选择", "选中", "选择集", "selection", "select"),
            Pair("entity", "实体", "对象属性", "删除实体", "复制", "镜像", "偏移", "阵列", "倒角", "圆角", "分解", "炸开", "entity", "object", "copy", "mirror", "offset", "array", "fillet", "chamfer", "explode"),
            Pair("geometry", "画线", "画圆", "绘制", "多段线", "直线", "圆弧", "矩形", "椭圆", "样条", "中心线", "draw", "circle", "polyline", "arc", "rectangle", "ellipse", "spline", "centerline"),
            Pair("dimension", "标注", "尺寸", "直径", "半径", "引线", "角度", "坐标标注", "连续标注", "链式", "dimension", "leader", "angular", "ordinate", "chain"),
            Pair("text", "文字", "文本", "注释", "note", "text"),
            Pair("block", "图块", "块属性", "插入块", "block", "attribute"),
            Pair("view", "缩放", "视图", "视口", "zoom", "viewport"),
            Pair("delivery", "命令", "清理", "核查", "purge", "audit", "command"),
            Pair("batch", "批量", "批处理", "多步", "事务", "对比", "batch", "compare"),
            Pair("table", "表格", "明细表", "table"),
            Pair("xref", "外部参照", "参照", "xref", "attach"),
            Pair("analysis", "测量", "面积", "长度", "measure", "area", "length"),
            Pair("office", "excel", "xlsx", "pdf", "清单", "工作表", "图册", "合并")
        };

        /// <summary>具体域名（不含 <see cref="AllScope"/>）：自测用它逐个域做完备性检查。</summary>
        internal static IReadOnlyList<string> DomainScopes
        {
            get
            {
                var scopes = new List<string>(ScopeRules.Length);
                foreach (var rule in ScopeRules) scopes.Add(rule.Key);
                return scopes;
            }
        }

        public IReadOnlyList<string> ResolveScopes(string userText, IReadOnlyList<string> current)
        {
            string text = (userText ?? string.Empty).Trim().ToLowerInvariant();

            var scopes = new List<string>();
            if (text.Length > 0)
            {
                foreach (var rule in TextRouting)
                {
                    if (!ContainsAny(text, rule.Value)) continue;
                    if (!scopes.Contains(rule.Key)) scopes.Add(rule.Key);
                }
            }

            if (scopes.Count == 0)
            {
                // 简短追问（"再来一次"）沿用上一轮：不回退会让上一轮好不容易收窄的面又摊开。
                if (current != null && current.Count > 0) return current;
                return new[] { AllScope };
            }
            return scopes;
        }

        public bool IncludesTool(string scope, string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            // 无命中：全量。
            if (string.Equals(scope, AllScope, StringComparison.OrdinalIgnoreCase)) return true;

            if (IsCore(toolName)) return true;

            foreach (var rule in ScopeRules)
            {
                if (!string.Equals(rule.Key, scope, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string pattern in rule.Value)
                    if (Matches(pattern, toolName)) return true;
                return false;
            }

            // 未知作用域：不收窄（宁可多给，不可漏给）。ChatCore 在收窄结果为空时也会回退全量，
            // 这里显式返回 true 只是把语义写清楚。
            return true;
        }

        private static bool IsCore(string toolName)
        {
            foreach (string name in CoreTools)
                if (string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>前缀族（<c>*</c> 结尾）按前缀匹配，其余按整名（大小写不敏感）。</summary>
        internal static bool Matches(string pattern, string toolName)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                string prefix = pattern.Substring(0, pattern.Length - 1);
                return toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            foreach (string keyword in keywords)
                if (!string.IsNullOrEmpty(keyword) && text.Contains(keyword.ToLowerInvariant())) return true;
            return false;
        }

        private static KeyValuePair<string, string[]> Pair(string scope, params string[] patterns)
            => new KeyValuePair<string, string[]>(scope, patterns);
    }
}