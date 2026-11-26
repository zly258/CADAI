using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using Matrix = System.Drawing.Drawing2D.Matrix;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CadAI.Core
{
    /// <summary>
    /// Ribbon 按钮图标种类：两个 CAD 宿主共用的一套 Google Material Symbols 官方图标。
    /// 枚举值是**并集**——每种图形只画一次，谁的按钮用哪一枚由宿主自己决定。
    /// </summary>
    public enum RibbonIconKind
    {
        /// <summary>AI 助手（机器人）。</summary>
        Assistant,
        /// <summary>AI 助手（AutoCAD 侧的命名，图形与 Assistant 相同）。</summary>
        AiChat,
        /// <summary>翻译（中英互译）。</summary>
        Translation,
        /// <summary>批量翻译（叠放文档）。</summary>
        Batch,
        /// <summary>文本翻译（带文字行的文档）。</summary>
        Text,
        /// <summary>翻译范围（漏斗）。</summary>
        Scope,
        /// <summary>设置（齿轮）。</summary>
        Settings,
        /// <summary>术语库（摊开的书）。</summary>
        Glossary,
        /// <summary>关于（信息圆环）。</summary>
        About,
        /// <summary>出图标注（打印机，表达"把工程图出成图纸"）。</summary>
        Generate,
        /// <summary>框选翻译（选框四角括号）。</summary>
        Selection
    }

    /// <summary>
    /// Ribbon 图标渲染器：直接使用 **Google Material Symbols 官方图标**（Outlined 字重，24 px 网格），
    /// AutoCAD 与 AutoCAD 两个宿主共用。图标路径取自
    /// <c>google/material-design-icons</c>，Apache License 2.0，可自由商用；
    /// 归属与许可全文见仓库根目录 <c>THIRD-PARTY-NOTICES.md</c>。
    ///
    /// 渲染策略：
    ///  1. **矢量路径 + 单色填充**。官方 SVG 的 <c>d</c> 属性原样存在
    ///     <see cref="MaterialSymbols"/>，GDI+ 侧由 <see cref="SvgPathParser"/> 解析成
    ///     <see cref="GraphicsPath"/> 后填充，所以任意尺寸都清晰，不依赖图片资源或图标字体。
    ///  2. **跟随 CAD 主题换色**。亮色主题用深灰、暗色主题用浅灰，只用同一个前景色——
    ///     Material Symbols 本身就是单色图标，靠形状而不靠颜色区分语义。
    ///  3. **4 倍超采样再缩到目标尺寸**。16 px 小按钮上的细笔画（齿轮、信息圆环内圈）
    ///     直接按 16 px 填充会糊，先按 64 px 画再双三次缩下来。
    ///
    /// 渲染只依赖 System.Drawing，不碰 COM、不碰 WPF；两个宿主各自套一层
    /// （IPictureDisp / ImageSource）并负责判断当前是亮色还是暗色主题。
    /// </summary>
    public static class RibbonIconRenderer
    {
        /// <summary>按目标尺寸渲染图标（调用方负责把 Bitmap 转成宿主需要的图像类型）。</summary>
        public static Bitmap Render(int size, RibbonIconKind kind, bool darkTheme)
        {
            int target = Math.Max(16, size);
            using (Bitmap highResolution = DrawIcon(target * Supersample, kind, darkTheme))
            {
                return Downscale(highResolution, target);
            }
        }

        /// <summary>按目标尺寸渲染图标并直接转换为冻结的 WPF ImageSource（供 AutoCAD 等 WPF Ribbon 宿主直接复用）。</summary>
        public static ImageSource RenderImageSource(int size, RibbonIconKind kind, bool darkTheme)
        {
            using (Bitmap bitmap = Render(size, kind, darkTheme))
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = ms;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private const int Supersample = 4;

        /// <summary>Material Symbols 的官方 viewBox 边长（<c>0 -960 960 960</c>）。</summary>
        private const float SymbolGrid = 960f;

        /// <summary>青翠绿松石渐变徽章（与传统蓝底机器人图标明显区分）。</summary>
        private static readonly Color BadgeTealTop = Color.FromArgb(0x14, 0xB8, 0xA6);
        private static readonly Color BadgeTealBottom = Color.FromArgb(0x0F, 0x76, 0x6E);
        private static readonly Color BadgeStroke = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        private static readonly Color BadgeForeground = Color.FromArgb(0xFF, 0xFF, 0xFF);

        private static Bitmap DrawIcon(int size, RibbonIconKind kind, bool darkTheme)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 青翠渐变更大圆角方徽章 + 微亮内描边
                float margin = size * 0.05f;
                var badgeRect = new RectangleF(margin, margin, size - margin * 2f, size - margin * 2f);
                float radius = size * 0.28f;
                using (GraphicsPath badgePath = CreateRoundedRectangle(badgeRect, radius))
                using (var bgBrush = new LinearGradientBrush(badgeRect, BadgeTealTop, BadgeTealBottom, LinearGradientMode.ForwardDiagonal))
                using (var borderPen = new System.Drawing.Pen(BadgeStroke, Math.Max(1f, size * 0.03f)))
                {
                    g.FillPath(bgBrush, badgePath);
                    g.DrawPath(borderPen, badgePath);
                }

                string name = IconName(kind);
                using (GraphicsPath path = SvgPathParser.Parse(MaterialSymbols.PathData(name)))
                using (var brush = new SolidBrush(BadgeForeground))
                using (var transform = new Matrix())
                {
                    // 图标按 70% 比例居中排布在蓝色圆角底块内，留出舒适的四周呼吸空间。
                    // 官方坐标系 y 轴向上为负：先等比缩放，再平移到画布居中位置。
                    float iconBox = size * 0.70f;
                    float offset = (size - iconBox) * 0.5f;
                    float scale = iconBox / SymbolGrid;
                    transform.Scale(scale, scale, MatrixOrder.Append);
                    transform.Translate(offset, offset + SymbolGrid * scale, MatrixOrder.Append);

                    Matrix previous = g.Transform;
                    g.Transform = transform;
                    try
                    {
                        g.FillPath(brush, path);
                    }
                    finally
                    {
                        g.Transform = previous;
                    }
                }
            }

            return bitmap;
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>图标语义 → Material Symbols 官方名称。</summary>
        private static string IconName(RibbonIconKind kind)
        {
            switch (kind)
            {
                case RibbonIconKind.Translation: return MaterialSymbols.Translation;
                case RibbonIconKind.Batch: return MaterialSymbols.Batch;
                case RibbonIconKind.Text: return MaterialSymbols.Text;
                case RibbonIconKind.Scope: return MaterialSymbols.Scope;
                case RibbonIconKind.Settings: return MaterialSymbols.Settings;
                case RibbonIconKind.Glossary: return MaterialSymbols.Glossary;
                case RibbonIconKind.About: return MaterialSymbols.About;
                case RibbonIconKind.Generate: return MaterialSymbols.Generate;
                case RibbonIconKind.Selection: return MaterialSymbols.Selection;
                case RibbonIconKind.Assistant:
                case RibbonIconKind.AiChat:
                default:
                    return MaterialSymbols.AutoAwesome;
            }
        }

        /// <summary>4 倍尺寸的原图缩到目标尺寸，HighQualityBicubic 保住细笔画。</summary>
        private static Bitmap Downscale(Bitmap high, int target)
        {
            var bitmap = new Bitmap(target, target, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(high, new Rectangle(0, 0, target, target));
            }
            return bitmap;
        }
    }
}
