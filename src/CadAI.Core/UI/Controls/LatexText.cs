using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CadAI.Core
{
    /// <summary>
    /// LaTeX 公式 → Unicode 文本的轻量转换器。
    ///
    /// 背景：本项目用 MdXaml 渲染 Markdown，而 MdXaml 1.27 **不暴露 Markdig pipeline**，
    /// 无法挂载 <c>Markdig.Extensions.Mathematics</c>；引入 WpfMath 一类二维排版库又会带来
    /// 新的依赖与体积。因此这里走"预处理"路线：在交给 MdXaml 之前把 <c>$...$</c> / <c>$$...$$</c>
    /// 里的 LaTeX 转成 Unicode 文本，让它作为普通文本被正常排版。
    ///
    /// 设计约束（按重要性排序）：
    /// 1. **绝不丢内容**：任何无法识别的命令都原样保留（去掉反斜杠），不静默删除；
    /// 2. **绝不抛异常**：出错就返回原文，渲染链路不受影响；
    /// 3. **不碰代码**：围栏代码块与行内代码里的 <c>$</c> 不做转换（那里的 $ 是字面量）。
    ///
    /// 能力边界（如实说明）：支持上下标、希腊字母、常用运算符、分式（转成 a/b 形式）、
    /// 根号、文本命令。**不做二维排版**——复杂矩阵、多层嵌套分式不会得到 LaTeX 级别的排版，
    /// 但内容可读、不丢失。
    /// </summary>
    internal static class LatexText
    {
        // ---------------------------------------------------------------- 符号表

        private static readonly Dictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 希腊字母（小写）
            ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
            ["epsilon"] = "ε", ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
            ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
            ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
            ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["varsigma"] = "ς",
            ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["varphi"] = "φ",
            ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
            // 希腊字母（大写）
            ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ",
            ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Upsilon"] = "Υ",
            ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
            // 运算符
            ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["ast"] = "∗",
            ["pm"] = "±", ["mp"] = "∓", ["oplus"] = "⊕", ["otimes"] = "⊗",
            ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥",
            ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["sim"] = "~",
            ["simeq"] = "≃", ["equiv"] = "≡", ["cong"] = "≅", ["propto"] = "∝",
            ["ll"] = "≪", ["gg"] = "≫", ["infty"] = "∞",
            ["sum"] = "Σ", ["prod"] = "Π", ["int"] = "∫", ["oint"] = "∮",
            ["partial"] = "∂", ["nabla"] = "∇", ["sqrt"] = "√",
            ["rightarrow"] = "→", ["to"] = "→", ["leftarrow"] = "←",
            ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
            ["uparrow"] = "↑", ["downarrow"] = "↓",
            // 集合与逻辑
            ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆",
            ["supset"] = "⊃", ["supseteq"] = "⊇", ["cup"] = "∪", ["cap"] = "∩",
            ["emptyset"] = "∅", ["varnothing"] = "∅",
            ["forall"] = "∀", ["exists"] = "∃", ["neg"] = "¬", ["land"] = "∧", ["lor"] = "∨",
            // 几何与工程常用
            ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥", ["triangle"] = "△",
            ["circ"] = "°", ["degree"] = "°", ["prime"] = "′", ["dprime"] = "″",
            ["ldots"] = "…", ["cdots"] = "⋯", ["dots"] = "…",
            ["quad"] = " ", ["qquad"] = "  ", [","] = " ", [";"] = " ", [":"] = " ", ["!"] = "",
            // 常用文本/函数名（原样保留为普通字母）
            ["text"] = "", ["mathrm"] = "", ["mathbf"] = "", ["mathit"] = "", ["operatorname"] = "",
            ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["log"] = "log", ["ln"] = "ln",
            ["exp"] = "exp", ["max"] = "max", ["min"] = "min", ["lim"] = "lim",
        };

        private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ";
        private const string SuperscriptSource = "0123456789+-=()ni";
        private const string Subscripts = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₒₓₕₖₗₘₙₚₛₜ";
        private const string SubscriptSource = "0123456789+-=()aeoxhklmnpst";

        // ---------------------------------------------------------------- 入口

        /// <summary>
        /// 把 Markdown 里的 LaTeX 公式转成 Unicode 文本。代码块/行内代码内不处理。
        /// 任何异常都返回原文。
        /// </summary>
        public static string Normalize(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;
            // 快速短路：没有公式定界符就原样返回，避免无谓扫描。
            if (markdown.IndexOf('$') < 0 && markdown.IndexOf("\\(", StringComparison.Ordinal) < 0)
                return markdown;

            try
            {
                var output = new StringBuilder(markdown.Length + 32);
                int index = 0;
                int length = markdown.Length;

                while (index < length)
                {
                    // 1) 围栏代码块 ```...``` 原样拷贝
                    if (IsFence(markdown, index))
                    {
                        int end = FindFenceEnd(markdown, index);
                        output.Append(markdown, index, end - index);
                        index = end;
                        continue;
                    }

                    char c = markdown[index];

                    // 2) 行内代码 `...` 原样拷贝
                    if (c == '`')
                    {
                        int end = FindInlineCodeEnd(markdown, index);
                        output.Append(markdown, index, end - index);
                        index = end;
                        continue;
                    }

                    // 3) 块级公式 $$...$$
                    if (c == '$' && index + 1 < length && markdown[index + 1] == '$')
                    {
                        int end = markdown.IndexOf("$$", index + 2, StringComparison.Ordinal);
                        if (end > 0)
                        {
                            string body = markdown.Substring(index + 2, end - index - 2);
                            // 块级公式独立成段，用加粗让它与正文区分（MdXaml 无数学块样式）。
                            output.Append("\n\n**").Append(Convert(body)).Append("**\n\n");
                            index = end + 2;
                            continue;
                        }
                    }

                    // 4) 行内公式 $...$（要求 $ 后紧跟非空白，且闭合 $ 前为非空白，避免误伤金额）
                    if (c == '$' && IsInlineMathStart(markdown, index))
                    {
                        int end = FindInlineMathEnd(markdown, index + 1);
                        if (end > 0)
                        {
                            string body = markdown.Substring(index + 1, end - index - 1);
                            output.Append(Convert(body));
                            index = end + 1;
                            continue;
                        }
                    }

                    // 5) \(...\) 与 \[...\]
                    if (c == '\\' && index + 1 < length && (markdown[index + 1] == '(' || markdown[index + 1] == '['))
                    {
                        bool display = markdown[index + 1] == '[';
                        string closer = display ? "\\]" : "\\)";
                        int end = markdown.IndexOf(closer, index + 2, StringComparison.Ordinal);
                        if (end > 0)
                        {
                            string body = markdown.Substring(index + 2, end - index - 2);
                            output.Append(display ? "\n\n**" + Convert(body) + "**\n\n" : Convert(body));
                            index = end + 2;
                            continue;
                        }
                    }

                    output.Append(c);
                    index++;
                }

                return output.ToString();
            }
            catch
            {
                // 转换失败绝不影响渲染：退回原文。
                return markdown;
            }
        }

        // ---------------------------------------------------------------- 扫描辅助

        private static bool IsFence(string text, int index)
        {
            return index + 2 < text.Length && text[index] == '`' && text[index + 1] == '`' && text[index + 2] == '`';
        }

        private static int FindFenceEnd(string text, int index)
        {
            int lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0) return text.Length;
            int close = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (close < 0) return text.Length;
            int after = close + 3;
            // 吃掉闭合围栏所在行的剩余字符
            int nl = text.IndexOf('\n', after);
            return nl < 0 ? text.Length : nl + 1;
        }

        private static int FindInlineCodeEnd(string text, int index)
        {
            int end = text.IndexOf('`', index + 1);
            return end < 0 ? index + 1 : end + 1;
        }

        private static bool IsInlineMathStart(string text, int index)
        {
            if (index + 2 >= text.Length) return false;
            char next = text[index + 1];
            if (char.IsWhiteSpace(next)) return false;      // "$ 100" 不是公式
            if (next == '$') return false;                   // 交给块级分支
            // 行内公式必须能找到闭合 $，且中间不含换行
            int end = FindInlineMathEnd(text, index + 1);
            return end > index + 1;
        }

        private static int FindInlineMathEnd(string text, int index)
        {
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') return -1;
                if (c == '\\') { i++; continue; }            // 跳过转义字符
                if (c == '$')
                {
                    if (i == index) return -1;               // 空公式
                    if (char.IsWhiteSpace(text[i - 1])) return -1;  // 闭合 $ 前是空白 → 不是公式
                    return i;
                }
            }
            return -1;
        }

        // ---------------------------------------------------------------- 转换核心

        /// <summary>把一段 LaTeX 正文转成 Unicode 文本。</summary>
        internal static string Convert(string latex)
        {
            if (string.IsNullOrEmpty(latex)) return string.Empty;

            string s = latex;

            // 归一化换行（块级公式里的 \\ 是换行）
            s = s.Replace("\\\\", "\n");

            // \frac{a}{b} → (a)/(b)，可嵌套（最多 4 层，避免病态输入打爆栈）
            for (int pass = 0; pass < 4; pass++)
            {
                string replaced = Regex.Replace(s, @"\\frac\s*\{([^{}]*)\}\s*\{([^{}]*)\}", m =>
                {
                    string num = m.Groups[1].Value.Trim();
                    string den = m.Groups[2].Value.Trim();
                    // 单字符分子分母直接写 a/b；否则加括号避免歧义
                    string n = num.Length <= 1 ? num : "(" + num + ")";
                    string d = den.Length <= 1 ? den : "(" + den + ")";
                    return n + "/" + d;
                });
                if (replaced == s) break;
                s = replaced;
            }

            // \sqrt{x} → √(x)
            s = Regex.Replace(s, @"\\sqrt\s*\{([^{}]*)\}", m => "√(" + m.Groups[1].Value.Trim() + ")");
            s = Regex.Replace(s, @"\\sqrt\s*\[([^\]]*)\]\s*\{([^{}]*)\}", m => "√[" + m.Groups[1].Value.Trim() + "](" + m.Groups[2].Value.Trim() + ")");

            // \text{...} / \mathrm{...} 等：保留内容
            s = Regex.Replace(s, @"\\(?:text|mathrm|mathbf|mathit|operatorname|mbox)\s*\{([^{}]*)\}", m => m.Groups[1].Value);

            // 上标：^{...} 或 ^x
            s = Regex.Replace(s, @"\^\s*\{([^{}]*)\}", m => ToSuperscript(m.Groups[1].Value));
            s = Regex.Replace(s, @"\^\s*([0-9A-Za-z+\-=()])", m => ToSuperscript(m.Groups[1].Value));

            // 下标：_{...} 或 _x
            s = Regex.Replace(s, @"_\s*\{([^{}]*)\}", m => ToSubscript(m.Groups[1].Value));
            s = Regex.Replace(s, @"_\s*([0-9A-Za-z+\-=()])", m => ToSubscript(m.Groups[1].Value));

            // \left( \right) 等定界符
            s = Regex.Replace(s, @"\\left|\\right", string.Empty);
            s = Regex.Replace(s, @"\\[{}]", string.Empty);
            s = s.Replace("\\{", "{").Replace("\\}", "}");

            // 命令替换：\name → 符号；未识别的命令去掉反斜杠但保留名字（不丢内容）
            s = Regex.Replace(s, @"\\([A-Za-z]+)", m =>
            {
                string name = m.Groups[1].Value;
                return Symbols.TryGetValue(name, out string value) ? value : name;
            });
            // 单字符命令：\, \; \: \! 等
            s = Regex.Replace(s, @"\\(.)", m =>
            {
                string name = m.Groups[1].Value;
                return Symbols.TryGetValue(name, out string value) ? value : name;
            });

            // 清理：去掉多余的分组花括号（保内容），压缩空白
            s = s.Replace("{", string.Empty).Replace("}", string.Empty);
            s = Regex.Replace(s, @"[ \t]{2,}", " ");
            s = s.Trim();

            return s;
        }

        private static string ToSuperscript(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                int at = SuperscriptSource.IndexOf(c);
                sb.Append(at >= 0 ? Superscripts[at] : c);
            }
            string mapped = sb.ToString();
            // 有字符没映射成功（仍是原字符且不是本来就该原样显示的）→ 用 ^(...) 形式
            return mapped == value && !IsPlainWord(value) ? "^(" + value + ")" : mapped;
        }

        private static string ToSubscript(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                int at = SubscriptSource.IndexOf(c);
                sb.Append(at >= 0 ? Subscripts[at] : c);
            }
            string mapped = sb.ToString();
            return mapped == value && !IsPlainWord(value) ? "_(" + value + ")" : mapped;
        }

        private static bool IsPlainWord(string value)
        {
            foreach (char c in value) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }
    }
}
