using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// WPF 窗口的公共小工具：把窗口挂到 AutoCAD 主窗口下（Owner）。
    /// 不挂 Owner 的窗口在 AutoCAD 最小化/切换时会失去跟随，还会在任务栏多出一项。
    /// 模态窗口必须 Topmost 避免被 AutoCAD 主窗口重新激活时盖住导致界面假死，
    /// 并统一挂载 Esc 键退出。
    /// </summary>
    internal static class WindowService
    {
        /// <summary>在 Loaded 时把窗口 Owner 设为 AutoCAD 主窗口；失败静默（不影响窗口本身）。</summary>
        public static void OwnToAcad(Window window, bool modal = true)
        {
            try
            {
                IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(window).Owner = hwnd;
            }
            catch (Exception) { }

            // 统一增加 Esc 键关闭窗口能力（避免失去焦点或被遮挡时无法退出）
            window.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    try { window.Close(); } catch { }
                }
            };

            if (modal)
            {
                // 模态窗口置顶，避免被 AutoCAD 视口或其它窗口挡住导致整个 CAD 界面不可操作假死
                window.Topmost = true;
            }
        }
    }
}
