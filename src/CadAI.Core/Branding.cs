namespace CadAI.Core
{
    /// <summary>
    /// 产品品牌常量的唯一来源。Phase 1 从 AboutWindow 上移到 ChatCore：
    /// 聊天窗口（已迁入 ChatCore）直接复用，宿主工程经 InternalsVisibleTo 访问；
    /// AboutWindow 仍以同名常量转发，既有调用点（AboutWindow.ProductName）不受影响。
    /// </summary>
    public static class Branding
    {
        /// <summary>
        /// 产品在界面上对外展示的名称：窗口标题、关于窗口、消息框标题都用它。
        /// 内部字符串 ID（清单 ClassId/ClientId、安装目录）不受影响。
        /// </summary>
        public const string ProductName = "CADAI";

        /// <summary>Ribbon 选项卡/面板的短名称。</summary>
        public const string RibbonTabName = "CADAI";

        /// <summary>开源仓库地址。</summary>
        public const string RepositoryUrl = "https://github.com/zly258/CADAI";

        /// <summary>对话窗口底部展示链接。</summary>
        public const string CopyrightNotice = RepositoryUrl;
    }
}
