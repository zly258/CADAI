using System;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using CadAI.Core;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// Ribbon 图标工厂（AutoCAD 侧）。
    ///
    /// 图形本身由 <see cref="ChatCore.RibbonIconRenderer"/> 渲染——与 AutoCAD 插件用的是同一套
    /// Google Material Symbols 官方图标（Apache 2.0，单色填充 + 跟随主题换色）。本类只做两件 AutoCAD 专属的事：
    ///  1. 判定当前主题（COLORTHEME：0=暗、1=亮）；
    ///  2. 把 Bitmap 包成 WPF 的 ImageSource（Ribbon 按钮吃 WPF 位图）。
    ///
    /// 图标语义统一由 <see cref="RibbonIconKind"/> 决定，本工程不再自定义枚举。
    /// </summary>
    internal static class RibbonIconFactory
    {
        /// <summary>
        /// 当前 AutoCAD 是否处于暗色主题（COLORTHEME：0=暗、1=亮）。
        /// 取不到系统变量时按**亮色**处理：与插件窗口的浅色观感一致。
        /// </summary>
        public static bool IsDarkTheme()
        {
            try
            {
                object value = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("COLORTHEME");
                if (value == null) return false;
                return Convert.ToInt32(value) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>画一个指定尺寸的图标（按当前主题取色）。size 传 32（大按钮）或 16（小按钮）。</summary>
        public static ImageSource Create(int size, RibbonIconKind kind)
        {
            return Create(size, kind, IsDarkTheme());
        }

        /// <summary>画一个指定尺寸的图标，显式指定主题（供主题切换时重画）。</summary>
        public static ImageSource Create(int size, RibbonIconKind kind, bool darkTheme)
            => RibbonIconRenderer.RenderImageSource(size, kind, darkTheme);
    }
}