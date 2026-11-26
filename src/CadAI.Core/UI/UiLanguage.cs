using System;

namespace CadAI.Core
{
    /// <summary>
    /// 全局界面语言管理服务（支持简体中文 zh-CN 与英文 en-US，切换后立即广播通知各窗口与 Ribbon 刷新）。
    /// </summary>
    public static class UiLanguage
    {
        public const string ZhCn = "zh-CN";
        public const string EnUs = "en-US";

        private static string _current = ZhCn;

        /// <summary>当前生效的界面语言代码（zh-CN 或 en-US）。</summary>
        public static string Current => _current;

        /// <summary>当前界面是否为英文模式。</summary>
        public static bool IsEnglish => string.Equals(_current, EnUs, StringComparison.OrdinalIgnoreCase);

        /// <summary>界面语言切换事件（各已打开窗口与 Ribbon 订阅此事件以实时刷新文案）。</summary>
        public static event EventHandler LanguageChanged;

        /// <summary>规范化语言代码：以 en 开头归一为 en-US，其余归一为 zh-CN。</summary>
        public static string Normalize(string code)
        {
            string trimmed = (code ?? string.Empty).Trim();
            if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return EnUs;
            return ZhCn;
        }

        /// <summary>设置界面语言并在发生变化时通知订阅方。</summary>
        public static void Set(string code)
        {
            string normalized = Normalize(code);
            if (string.Equals(_current, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _current = normalized;
            try
            {
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                // 避免单个监听者异常阻断其他界面刷新
            }
        }

        /// <summary>在中英文之间一键切换并返回切换后的语言代码。</summary>
        public static string Toggle()
        {
            string next = IsEnglish ? ZhCn : EnUs;
            Set(next);
            return next;
        }

        /// <summary>按当前语言返回对应文案。</summary>
        public static string T(string zh, string en) => IsEnglish ? en : zh;
    }
}
