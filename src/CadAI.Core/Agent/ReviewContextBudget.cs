#nullable enable
namespace CadAI.Core
{
    /// <summary>
    /// 自动视觉校验正文的上下文预算。
    ///
    /// 为什么需要它：校验模型每轮会回一篇很长的中文分析（实测一次 **18,403 token**），
    /// 而这段正文被当作 user 消息永久塞回对话，于是后续**每一轮** prefill 都背着它，
    /// 请求越来越慢、上下文越来越早触发压缩。真正要被模型用到的只有两部分：
    /// 开头的结论、结尾的修正动作。
    ///
    /// 因此这里保留"头 + 尾"、只掐掉中间：完整正文仍然原样显示给用户
    /// （ui.AppendContent 走的是未截断的文本），进上下文的才是压缩版。
    /// 纯逻辑、无依赖，便于单元测试覆盖边界。
    /// </summary>
    internal static class ReviewContextBudget
    {
        /// <summary>塞回上下文的最大字符数。中文按 1 字 ≈ 1 token 估算，约 2.4k token。</summary>
        public const int MaxCharacters = 2400;

        /// <summary>保留给开头的比例，其余留给结尾（修正动作通常落在文末）。</summary>
        public const double HeadShare = 0.6;

        /// <summary>
        /// 清洗视觉校验模型返回文本中的免责套话与脱靶代码（如 Python/cadquery/Blender 脚本、
        /// "当前环境未挂载自动建模工具" 等通用免责声明）。这些套话来自多模态模型无工具时的通用预训练记忆，
        /// 塞回上下文或显示在界面上会严重误导主模型和用户。
        /// </summary>
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string cleaned = text!;

            // 1. 去除单行/行内包含类似 "（注：当前环境未挂载自动建模/CAD修正工具...）" 的免责声明
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"[*_]*[（\(]\s*注[：:][^）\)]*(未挂载|无法直接执行|没有挂载|缺少CAD|请在建模脚本)[^）\)]*[）\)]\s*[*_]*",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 2. 去除以 "注：" 开头的独占段落免责声明
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"^\s*[*_]*[（\(]?\s*注[：:][^\n\r]*(未挂载|无法直接执行|没有挂载|缺少CAD|请在建模脚本)[^\n\r]*[）\)]?[*_]*\s*$",
                "",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 3. 去除无 CAD 工具的看图模型常盲目生成的 Python / cadquery / openSCAD 伪代码块
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"```(?:python|cadquery|scad|openscad)[^`]*```",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 4. 收拢因删除段落产生的连续多余空行
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n");

            return cleaned.Trim();
        }

        /// <summary>压缩一段校验正文；未超预算时原样返回（先清洗免责套话，仅去首尾空白）。</summary>
        public static string Compact(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string trimmed = Sanitize(text);
            if (trimmed.Length <= MaxCharacters) return trimmed;

            int headLength = (int)(MaxCharacters * HeadShare);
            int tailLength = MaxCharacters - headLength;
            string head = trimmed.Substring(0, headLength);
            string tail = trimmed.Substring(trimmed.Length - tailLength);
            int omitted = trimmed.Length - headLength - tailLength;

            return head
                   + "\n\n…[视觉校验正文过长，中间省略 " + omitted + " 字；完整内容见上方对话]…\n\n"
                   + tail;
        }
    }
}
