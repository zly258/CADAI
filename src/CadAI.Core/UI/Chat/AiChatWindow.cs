using System;
using System.Windows;

namespace CadAI.Core
{
    public sealed class AiChatWindow : Window
    {
        // Phase 2：不再传入 AutoCAD.Application；宿主能力（工具执行、模态守卫）经 ChatCoreHost 注入。
        public AiChatWindow()
        {
            Title = UiLanguage.T("CAD 智能对话", "CAD AI Chat");
            Width = 520;
            Height = 760;
            MinWidth = 420;
            MinHeight = 520;
            // 与其余窗口一致：挂主题字典 + Window.Base（背景/前景/全局字体/文本渲染模式）。
            // 之前这里不挂，AiChatView 只能自己写 FontFamily 兜底——那是 LayoutCheck 的唯一 ERROR。
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CadAI.Core;component/UI/Themes/Theme.xaml")
            });
            Style = TryFindResource("Window.Base") as Style;
            // 居中到 AutoCAD 主窗口所在显示器的工作区（Owner 在 Show 前由宿主注入）。
            // 不用 CenterOwner：AutoCAD 非最大化时，CenterOwner 会把窗口摆到
            // AutoCAD 窗口中心而不是屏幕中心。
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowPlacement.CenterOnOwnerScreen(this);
            ShowInTaskbar = true;
            ResizeMode = ResizeMode.CanResize;
            View = new AiChatView();
            Content = View;
            AiExceptionGuard.Install(View.Dispatcher);
            Closing += Window_Closing;
            Closed += Window_Closed;
            UiLanguage.LanguageChanged += OnLanguageChanged;
            SourceInitialized += (s, e) => TryEnableDarkTitleBar();
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Title = UiLanguage.T("CAD 智能对话", "CAD AI Chat");
                }));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryEnableDarkTitleBar()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = 1;
                // Windows 11 / Windows 10 20H1+ (20) 与早期 Windows 10 (19) 暗色标题栏属性
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch { }
        }

        public AiChatView View { get; }

        /// <summary>
        /// 关闭确认：AI 正在执行任务时关窗会**直接中断**当前这一轮，
        /// 已经建好的特征留在 AutoCAD 里、但对话里的结论与产物清单就丢了。
        /// 用户按 X 时往往以为只是"把窗口收起来"，所以这里必须问一句。
        ///
        /// 选"是"＝中断任务并关窗（会取消 CancellationToken，工具链在安全点退出）；
        /// 选"否"＝留在窗口里继续。
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!View.IsTaskRunning) return;

            MessageBoxResult answer;
            try
            {
                answer = MessageBox.Show(
                    this,
                    UiLanguage.T(
                        "AI 正在执行任务，关闭窗口会中断当前操作。\n\n" +
                        "已建好的模型/图纸会保留在 " + ChatCoreHostConfig.HostProductNameOrDefault() + " 里，但本轮对话的结论与产物清单不会再更新。\n\n" +
                        "确定要关闭吗？",
                        "An AI task is currently running. Closing this window will cancel the active turn.\n\n" +
                        "Changes already committed in " + ChatCoreHostConfig.HostProductNameOrDefault() + " will be preserved, but the final summary will not be updated.\n\n" +
                        "Close anyway?"),
                    UiLanguage.T("CAD 智能对话", "CAD AI Chat"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
            }
            catch
            {
                return;   // 弹不出对话框就不要拦，避免用户关不掉窗口
            }

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            try { View.CancelRunningTask("关闭窗口"); } catch { }
        }

        public void AttachOwner(IntPtr owner)
        {
            if (owner == IntPtr.Zero) return;
            try { new System.Windows.Interop.WindowInteropHelper(this).Owner = owner; }
            catch { }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            UiLanguage.LanguageChanged -= OnLanguageChanged;
            try { View.Shutdown(); } catch { }
        }
    }
}
