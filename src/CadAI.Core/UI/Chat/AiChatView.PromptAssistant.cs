using System.Windows;

namespace CadAI.Core
{
    /// <summary>
    /// 输入框上方的辅助逻辑宿主。原先的「提示词模板」「优化提示词」入口已按需求移除；
    /// 宿主模态框自动处理（发送期间挂模态守卫）也已整体移除，由外部独立 hook 程序负责。
    /// 2026-10-23 拆分产品：助手与翻译助手成为两个独立插件，不再做任务互锁，
    /// 原先的 SetExternalTaskLock（翻译跑批时禁发消息）随之移除。
    /// 这里仅保留加载初始化。
    /// </summary>
    public partial class AiChatView
    {
        private bool _promptAssistantInitialized;

        static AiChatView()
        {
            EventManager.RegisterClassHandler(typeof(AiChatView), FrameworkElement.LoadedEvent, new RoutedEventHandler(PromptAssistantLoaded), true);
        }

        private static void PromptAssistantLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is AiChatView view) view.EnsurePromptAssistantUi();
        }

        private void EnsurePromptAssistantUi()
        {
            if (_promptAssistantInitialized || InputBox == null || SendButton == null) return;
            _promptAssistantInitialized = true;
        }
    }
}
