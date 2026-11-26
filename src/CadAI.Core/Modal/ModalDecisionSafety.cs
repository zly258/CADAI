#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// 弹窗决策的纯逻辑与共享默认值：无 COM、无 JSON 库、无宿主依赖。
    ///
    /// 为什么单独一个文件，而不是留在 ModalDecisionService / WindowResponder 里：
    ///   1) 这是"要不要在用户的 CAD 会话里点这个按钮"的判定核心，出事代价最高，
    ///      必须能被单元测试覆盖——而测试工程只链接"无第三方、无 COM 依赖"的源文件
    ///      （见 CadAI.Mcp.AutoCAD.Tests.csproj 的说明）。夹在依赖 ChatCore / AutoCAD
    ///      互操作的类里就永远测不到。
    ///   2) 危险词表与展开按钮表必须只有**一个**来源。本文件住在 ChatCore，同时被 CadAI.Host.AutoCAD
    ///      （AI 决策闸门）与 ModalAgent（规则匹配、按钮筛选）链接编译，
    ///      不再各存一份。历史上两份规则表就漂移过：DWF 发布警告规则只写在
    ///      C# 默认值里，交付的 json 里没有。
    ///
    /// 文件头显式 <c>#nullable enable</c>：ModalAgent 工程开了可空上下文、Add-in 没开，
    /// 同一份源码要同时满足两者（否则 Add-in 侧会报 CS8632 警告）。
    ///
    /// 命名空间为 ChatCore.Modal：这些判定与宿主无关，理应住在共享层。
    /// 引用方按 ChatCore.Modal 引用。
    /// </summary>
    internal static class ModalDecisionSafety
    {
        /// <summary>
        /// AI 决策的危险按钮拒绝词（去空白/标点/大小写后做包含匹配）。
        /// 只对 AI/看门狗决策生效，不影响 ModalAgent 里人工配置的 Rules——
        /// 白名单规则是人工授权的确定行为，AI 决策不是。
        /// </summary>
        internal static readonly string[] DefaultDenyWords =
        {
            "删除", "移除", "清除", "覆盖", "替换", "放弃", "不保存", "重置",
            "delete", "remove", "clear", "overwrite", "replace", "discard", "donotsave"
        };

        /// <summary>
        /// 展开型按钮文本（详细信息/帮助等）：不算业务选项分支，
        /// 既不参与"肯定/否定按钮"判定，也不上报给 AI。
        /// </summary>
        internal static readonly string[] DefaultExpanderLabels =
        {
            "详细信息", "详细", "更多", "高级", "帮助",
            "details", "more", "advanced", "help"
        };

        /// <summary>
        /// 按钮文本归一化：去掉 &amp; 助记符、尾部 (X) 快捷键括号、（推荐）后缀，
        /// 再统一小写。ModalAgent 与 Add-in 必须用同一套，否则两边匹配到的"同一个按钮"
        /// 不是同一个。
        /// </summary>
        internal static string NormalizeButtonText(string? text)
        {
            string value = (text ?? string.Empty).Replace("&", string.Empty).Trim();
            value = Regex.Replace(value, @"[（(][a-zA-Z][）)]$", string.Empty);
            return value.Replace("（推荐）", string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>去掉空白/标点与大小写差异，用于标题/正文标记匹配。</summary>
        internal static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var builder = new StringBuilder(value!.Length);
            foreach (char c in value.ToLowerInvariant())
            {
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c)) builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>
        /// 把给定的按钮文本对齐到弹窗上真实存在的按钮。四段递进匹配：
        /// 按钮归一化精确 → 严格归一化精确（去空白/标点）→ 严格归一化包含 → 按钮归一化包含。
        /// 全部失败返回 null——绝不"猜一个最近的"，猜错就是在用户的图纸上点了错误的按钮。
        /// </summary>
        internal static string? MatchButton(IReadOnlyList<string>? buttons, string? candidate)
        {
            if (buttons == null || buttons.Count == 0) return null;
            string wantedLabel = NormalizeButtonText(candidate);
            string wantedStrict = Normalize(candidate);
            if (wantedLabel.Length == 0 && wantedStrict.Length == 0) return null;

            // 1) 按钮归一化精确匹配（最常见：模型原样复述按钮文本）。
            foreach (string button in buttons)
            {
                if (wantedLabel.Length > 0 && string.Equals(NormalizeButtonText(button), wantedLabel, StringComparison.OrdinalIgnoreCase))
                    return button;
            }

            // 2) 严格归一化精确匹配（容忍模型插入的空格/标点差异）。
            foreach (string button in buttons)
            {
                if (wantedStrict.Length > 0 && Normalize(button) == wantedStrict) return button;
            }

            // 3) 严格归一化双向包含（模型补了括号说明之类）。
            foreach (string button in buttons)
            {
                string current = Normalize(button);
                if (current.Length > 0 && wantedStrict.Length > 0
                    && (current.Contains(wantedStrict) || wantedStrict.Contains(current))) return button;
            }

            // 4) 按钮归一化双向包含（兜底）。
            foreach (string button in buttons)
            {
                string current = NormalizeButtonText(button);
                if (current.Length > 0 && wantedLabel.Length > 0
                    && (current.Contains(wantedLabel) || wantedLabel.Contains(current))) return button;
            }
            return null;
        }

        /// <summary>按钮是否命中危险语义（用内置默认词表）。</summary>
        internal static bool IsDenied(string? label) => IsDenied(label, DefaultDenyWords);

        /// <summary>按钮是否命中给定危险语义词表。空标签视为危险（拒绝）。</summary>
        internal static bool IsDenied(string? label, IReadOnlyList<string>? denyWords)
        {
            // 危险词比较必须用"去标点"归一化，而不是按钮归一化：
            // 「Do not save」这类带空格的写法只有去掉空格才能命中 donotsave。
            string normalized = Normalize(label);
            if (normalized.Length == 0) return true;
            if (denyWords == null) return false;

            foreach (string word in denyWords)
            {
                string marker = Normalize(word);
                if (marker.Length > 0 && normalized.Contains(marker)) return true;
            }
            return false;
        }

        /// <summary>按钮是否为展开型（>>、&lt;&lt; 或命中展开按钮词表）。</summary>
        internal static bool IsExpander(string? label) => IsExpander(label, DefaultExpanderLabels);
        /// <summary>
        /// 候选窗口的第一道筛子（纯逻辑）：这个窗口有没有可能**就是弹窗本身**。
        /// 返回拒绝原因；null = 通过。
        ///
        /// 两条判据都来自 2026-10-13 的 AutoCAD 闪退复盘。当时 ModalAgent 把 AutoCAD
        /// 主框架当成了弹窗：ReadDialogBody 用 EnumChildWindows 递归读的是主窗口**整棵子树**，
        /// 于是某个子弹窗的正文成了主窗口的"正文"，命中「已知无害弹窗→确定」后盲点了一个
        /// 无标题按钮 —— 弹窗被错误关掉，AutoCAD 随后在 Close Doc 时 0xC0000005 闪退。
        ///   1) 被禁用的窗口不是弹窗：弹窗自己永远可用，被它禁用的是它的属主（主框架）。
        ///   2) 子孙窗口过多的窗口不是弹窗：主框架有几百上千个，真弹窗只有几十个。
        /// </summary>
        internal static string? DescribeCandidateRejection(bool enabled, int descendantCount, int maxDescendants)
        {
            if (!enabled) return "窗口当前被禁用（它是模态框的属主，不是弹窗本身）";
            if (maxDescendants > 0 && descendantCount > maxDescendants)
                return "子孙窗口 " + descendantCount + " 个，超过弹窗规模上限 " + maxDescendants;
            return null;
        }

        /// <summary>
        /// 按钮能否作为点击目标：必须有可见标题。
        /// 无标题按钮属于工具条/框架上的匿名控件——事故中被点中的正是主窗口上的空标题按钮，
        /// 因此"没标题就不点"是一条硬约束，而不是偏好。
        /// </summary>
        internal static bool IsClickableLabel(string? label) => !string.IsNullOrWhiteSpace(label);

        internal static bool IsExpander(string? label, IReadOnlyList<string>? expanderLabels)
        {
            if (string.IsNullOrEmpty(label)) return false;
            if (label!.Contains(">>") || label.Contains("<<")) return true;
            if (expanderLabels == null) return false;

            string current = Normalize(label);
            foreach (string expander in expanderLabels)
            {
                if (Normalize(expander) == current) return true;
            }
            return false;
        }

        /// <summary>
        /// 从模型输出里取按钮文本：先按 JSON 键抽取，退回严格正则；**不做自由文本猜测**。
        /// 这里刻意不引 JSON 库（net48 用 JavaScriptSerializer、net8 用 System.Text.Json，
        /// 共用文件不能二选一），只认 <c>"button":"…"</c> 这一种结构化写法。
        /// </summary>
        internal static string ExtractButton(string? raw) => ExtractStringValue(raw, "button");

        /// <summary>从模型输出里取理由文本；取不到就回退整段（截断），仅用于审计展示。</summary>
        internal static string ExtractReason(string? raw)
        {
            string reason = ExtractStringValue(raw, "reason");
            if (reason.Length > 0) return reason;
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string text = raw!.Trim();
            return text.Length > 160 ? text.Substring(0, 160) : text;
        }

        private static string ExtractStringValue(string? raw, string key)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // 必须带引号的键名：避免正文里出现 "button" 这个普通单词时误判。
            Match match = Regex.Match(raw!, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            return Unescape(match.Groups[1].Value);
        }

        /// <summary>只处理 JSON 字符串里最常见的三种转义；模型输出不会更复杂。</summary>
        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\r", " ");
        }
    }
}
