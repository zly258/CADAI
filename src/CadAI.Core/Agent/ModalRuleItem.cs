namespace CadAI.Core
{
    /// <summary>
    /// 用户自定义的模态弹窗自动处置规则项（写入 modal-agent.user.json 并热应用到 ModalAgent）。
    /// </summary>
    public sealed class ModalRuleItem
    {
        public string Name { get; set; } = string.Empty;
        public string Match { get; set; } = "Any";
        public string Action { get; set; } = "ClickPositive";
        public string Markers { get; set; } = string.Empty;

        public ModalRuleItem Clone() => new ModalRuleItem
        {
            Name = Name,
            Match = Match,
            Action = Action,
            Markers = Markers
        };

        /// <summary>界面展示用的动作中文标签。</summary>
        public string ActionDisplay
        {
            get
            {
                switch ((Action ?? string.Empty).Trim())
                {
                    case "ClickNegative": return UiLanguage.T("点击取消/否", "Click Cancel / No");
                    case "Close": return UiLanguage.T("关闭窗口", "Close Window");
                    case "Ignore": return UiLanguage.T("忽略", "Ignore");
                    default: return UiLanguage.T("点击确定/是", "Click OK / Yes");
                }
            }
        }
    }
}
