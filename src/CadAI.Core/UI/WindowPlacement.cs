using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CadAI.Core
{
    /// <summary>
    /// 顶层窗口的居中摆放。
    ///
    /// 历史行为：这些窗口都用 <c>WindowStartupLocation="CenterOwner"</c>，而设置/图纸翻译/
    /// 文本翻译/术语词库的 Owner 在多数入口是**对话窗口**。于是它们跟着对话窗口走——
    /// 用户把对话窗口拖到屏幕一角后，随后打开的窗口全挤在那一角，看起来完全没有居中。
    ///
    /// 现在的规则：统一居中到 Owner 所在**显示器的工作区**（Owner 为空时用主显示器）。
    /// 用 Owner 的显示器而不是"鼠标所在显示器"：多屏时鼠标可能停在另一块屏上，
    /// 而 AutoCAD 与对话窗口在哪块屏，用户就在看哪块屏。
    /// </summary>
    internal static class WindowPlacement
    {
        /// <summary>
        /// 让窗口在首次显示时居中到 Owner 所在显示器的工作区。
        /// 构造后、Show/ShowDialog 前调用；Owner 需在 Show 前设置好
        /// （Owner 为空时退回主显示器，不会抛异常）。
        /// </summary>
        public static void CenterOnOwnerScreen(Window window)
        {
            if (window == null) return;
            // SourceInitialized：句柄已建、WPF 已算出最终尺寸（SizeToContent 也算完了），
            // 这时定位不会闪一下。Loaded 再算一次兜底——个别路径在这一步才拿到最终高度；
            // 两次结果相同，重复执行无副作用。
            window.SourceInitialized += (sender, args) => Center(window);
            window.Loaded += (sender, args) => Center(window);
        }

        private static void Center(Window window)
        {
            try
            {
                IntPtr owner = new WindowInteropHelper(window).Owner;
                System.Drawing.Rectangle work = (owner != IntPtr.Zero
                        ? System.Windows.Forms.Screen.FromHandle(owner)
                        : System.Windows.Forms.Screen.PrimaryScreen)
                    .WorkingArea;

                // Screen 给的是物理像素，WPF 的 Left/Top 是逻辑单位：按窗口自身的呈现源
                // 换算，多屏不同缩放（150%）下也不会把窗口算到屏幕外。
                Matrix fromDevice = Matrix.Identity;
                PresentationSource source = PresentationSource.FromVisual(window);
                if (source != null && source.CompositionTarget != null)
                    fromDevice = source.CompositionTarget.TransformFromDevice;

                Point topLeft = fromDevice.Transform(new Point(work.Left, work.Top));
                Point bottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));
                double areaWidth = bottomRight.X - topLeft.X;
                double areaHeight = bottomRight.Y - topLeft.Y;
                if (areaWidth <= 0 || areaHeight <= 0) return;

                // 声明尺寸超过工作区时（小屏 / 125%-150% 缩放），先把窗口压到能放下再居中：
                // 只夹 Left/Top 会把底部的操作按钮（发送 / 保存）整体顶出屏幕外，
                // 而主聊天窗没有滚动条，用户没有手段把底部够回来。
                double declaredWidth = ResolveSize(window.ActualWidth, window.Width);
                double declaredHeight = ResolveSize(window.ActualHeight, window.Height);
                if (declaredWidth > 0 && areaWidth < declaredWidth)
                {
                    window.MaxWidth = areaWidth;
                    window.Width = areaWidth;
                }
                if (declaredHeight > 0 && areaHeight < declaredHeight)
                {
                    window.MaxHeight = areaHeight;
                    window.Height = areaHeight;
                }

                double width = ResolveSize(window.ActualWidth, window.Width);
                double height = ResolveSize(window.ActualHeight, window.Height);
                if (width <= 0 || height <= 0) return;

                // 夹在工作区内：窗口比工作区还大时贴左上角，而不是把标题栏顶到屏幕外。
                window.Left = Math.Max(topLeft.X, topLeft.X + (areaWidth - width) / 2);
                window.Top = Math.Max(topLeft.Y, topLeft.Y + (areaHeight - height) / 2);
            }
            catch
            {
                // 居中只是观感：任何失败都保持窗口原有位置，绝不因此影响功能。
            }
        }

        private static double ResolveSize(double actual, double declared)
        {
            if (IsUsable(actual)) return actual;
            return IsUsable(declared) ? declared : 0;
        }

        private static bool IsUsable(double value)
            => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
