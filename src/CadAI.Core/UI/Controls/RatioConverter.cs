using System;
using System.Globalization;
using System.Windows.Data;

namespace CadAI.Core
{
    /// <summary>
    /// 把祖先元素的宽度乘以一个比例（ConverterParameter，默认 0.8）。
    /// 用户消息气泡用它把 MaxWidth 绑到对话区宽度的 82%：窄窗里不再"占满整行没有靠右感"，
    /// 宽屏上也不需要为新窗口宽度不断调整硬编码值。
    /// </summary>
    public sealed class RatioConverter : IValueConverter
    {
        public static readonly RatioConverter Instance = new RatioConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = value as double? ?? 0;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return 560.0;

            double ratio = 0.8;
            if (parameter is string text && !string.IsNullOrWhiteSpace(text))
            {
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio);
            }
            if (ratio <= 0 || ratio > 1) ratio = 0.8;
            return Math.Max(120, width * ratio);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
