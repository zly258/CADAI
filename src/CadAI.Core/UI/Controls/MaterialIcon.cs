using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CadAI.Core
{
    /// <summary>
    /// 单色 Material Symbols 图标控件。
    ///
    /// 不在 XAML 里直接用 <c>Path Stretch="Uniform"</c>：那会把每个图标自身的几何包围盒
    /// 拉满可用尺寸，而官方图标在 960 网格里的留白各不相同（例如 <c>close</c> 比 <c>print</c>
    /// 占得更窄），同一个 Width 下视觉大小会明显不一致。这里统一按官方 viewBox
    /// （<c>0 -960 960 960</c>）缩放，保留图标设计时的内外边距，与 Ribbon 的 GDI+ 渲染
    /// (<see cref="RibbonIconRenderer"/>) 完全一致。
    ///
    /// 颜色取 <see cref="Control.Foreground"/>（继承属性），按钮悬停/禁用/危险态的换色
    /// 会自动传导过来，不需要额外绑定。
    /// </summary>
    public sealed class MaterialIcon : Control
    {
        /// <summary>Material Symbols 官方名称，例如 <c>close</c>、<c>arrow_downward</c>。</summary>
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(string), typeof(MaterialIcon),
                new FrameworkPropertyMetadata(MaterialSymbols.Close,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        static MaterialIcon()
        {
            // 只负责画图标：不参与焦点、不拦鼠标，点击继续落到外层按钮上。
            FocusableProperty.OverrideMetadata(typeof(MaterialIcon),
                new FrameworkPropertyMetadata(false));
            IsHitTestVisibleProperty.OverrideMetadata(typeof(MaterialIcon),
                new FrameworkPropertyMetadata(false));
        }

        public string Symbol
        {
            get { return (string)GetValue(SymbolProperty); }
            set { SetValue(SymbolProperty, value); }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            Geometry geometry = MaterialSymbols.GetGeometry(Symbol);
            double scale = size / 960.0;
            double offsetX = (ActualWidth - size) / 2.0;
            double offsetY = (ActualHeight - size) / 2.0;

            drawingContext.PushTransform(new MatrixTransform(
                scale, 0, 0, scale,
                offsetX,
                offsetY + 960.0 * scale));
            drawingContext.DrawGeometry(Foreground, null, geometry);
            drawingContext.Pop();
        }
    }
}
