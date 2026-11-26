using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CadAI.Core
{
    /// <summary>
    /// 轻量等待指示器：一段 270° 圆弧持续旋转，用给定画刷绘制。
    /// 无第三方依赖，也不需要主题文件；动画只在控件
    /// 已加载并启动时运行，隐藏或脱离可视树的指示器不产生任何开销。
    /// </summary>
    internal sealed class BusyIndicator : Grid
    {
        private readonly Storyboard _spin = new Storyboard();
        private bool _wanted;

        public BusyIndicator(Brush brush, double size = 13, double thickness = 1.5)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Left;

            var arc = new Path
            {
                Data = CreateArc(size, thickness),
                Stroke = brush ?? Brushes.Gray,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = new RotateTransform(0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                SnapsToDevicePixels = true,
                Width = size,
                Height = size
            };
            Children.Add(arc);

            var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, arc);
            Storyboard.SetTargetProperty(
                animation,
                new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _spin.Children.Add(animation);

            Loaded += (sender, args) => { if (_wanted) _spin.Begin(this, true); };
            Unloaded += (sender, args) => _spin.Stop(this);
        }

        /// <summary>开始旋转；在控件加载前调用也是安全的。</summary>
        public void Start()
        {
            _wanted = true;
            if (IsLoaded) _spin.Begin(this, true);
        }

        /// <summary>停止旋转并释放动画时钟。</summary>
        public void Stop()
        {
            _wanted = false;
            _spin.Stop(this);
        }

        private static Geometry CreateArc(double size, double thickness)
        {
            double radius = Math.Max(1.0, (size - thickness) / 2.0);
            var center = new Point(size / 2.0, size / 2.0);
            var figure = new PathFigure
            {
                StartPoint = new Point(center.X, center.Y - radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment(
                new Point(center.X - radius, center.Y),
                new Size(radius, radius),
                0,
                true,
                SweepDirection.Clockwise,
                true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            if (geometry.CanFreeze) geometry.Freeze();
            return geometry;
        }
    }
}
