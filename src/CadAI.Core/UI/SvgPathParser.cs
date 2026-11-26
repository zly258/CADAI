using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace CadAI.Core
{
    /// <summary>
    /// 极简 SVG path 解析器：把 Material Symbols 的 <c>d</c> 属性转成 GDI+ <see cref="GraphicsPath"/>。
    ///
    /// Ribbon 图标必须由 System.Drawing 渲染（AutoCAD 要 IPictureDisp，AutoCAD 要 WPF 位图，
    /// 两边都从同一张 Bitmap 来），而 System.Drawing 自身不认 SVG path。这里只实现图标用得到的
    /// 完整命令集：M/L/H/V/C/S/Q/T/A/Z（含相对命令与小写重复参数），填充规则固定为 nonzero
    /// （<see cref="FillMode.Winding"/>）——Material Symbols 的镂空（齿轮中心、信息圆环内圈）
    /// 依赖子路径绕向，用 alternate 会把洞填实。
    ///
    /// 坐标保持官方 CSS 像素网格（viewBox 0 -960 960 960，y 轴向上为负），缩放与翻转由调用方
    /// 通过 <see cref="Graphics.Transform"/> 统一完成。
    /// </summary>
    internal static class SvgPathParser
    {
        private const double Pi = Math.PI;

        public static GraphicsPath Parse(string data)
        {
            var path = new GraphicsPath(FillMode.Winding);
            if (string.IsNullOrWhiteSpace(data)) return path;

            int index = 0;
            char command = '\0';
            char previousCommand = '\0';
            double currentX = 0, currentY = 0;
            double startX = 0, startY = 0;
            double lastCubicX = 0, lastCubicY = 0;
            double lastQuadX = 0, lastQuadY = 0;
            bool figureOpen = false;

            while (true)
            {
                SkipSeparators(data, ref index);
                if (index >= data.Length) break;

                if (char.IsLetter(data[index]))
                {
                    command = data[index];
                    index++;
                }
                else if (command == '\0')
                {
                    throw new FormatException("SVG path 必须以命令字母开头。");
                }
                else if (char.ToUpperInvariant(command) == 'M')
                {
                    // SVG 规定 moveto 后的额外坐标对按 lineto 处理。
                    command = char.IsLower(command) ? 'l' : 'L';
                }

                if (char.ToUpperInvariant(command) == 'Z')
                {
                    if (figureOpen)
                    {
                        path.CloseFigure();
                        figureOpen = false;
                    }
                    currentX = startX;
                    currentY = startY;
                    previousCommand = command;
                    continue;
                }

                bool relative = char.IsLower(command);
                double originX = relative ? currentX : 0;
                double originY = relative ? currentY : 0;

                switch (char.ToUpperInvariant(command))
                {
                    case 'M':
                    {
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.StartFigure();
                        figureOpen = true;
                        currentX = startX = x;
                        currentY = startY = y;
                        // 同一命令字母后若还有坐标对，按 lineto 继续。
                        command = relative ? 'l' : 'L';
                        break;
                    }

                    case 'L':
                    {
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddLine((float)currentX, (float)currentY, (float)x, (float)y);
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'H':
                    {
                        double x = ReadNumber(data, ref index) + originX;
                        path.AddLine((float)currentX, (float)currentY, (float)x, (float)currentY);
                        currentX = x;
                        break;
                    }

                    case 'V':
                    {
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddLine((float)currentX, (float)currentY, (float)currentX, (float)y);
                        currentY = y;
                        break;
                    }

                    case 'C':
                    {
                        double x1 = ReadNumber(data, ref index) + originX;
                        double y1 = ReadNumber(data, ref index) + originY;
                        double x2 = ReadNumber(data, ref index) + originX;
                        double y2 = ReadNumber(data, ref index) + originY;
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddBezier((float)currentX, (float)currentY,
                            (float)x1, (float)y1, (float)x2, (float)y2, (float)x, (float)y);
                        lastCubicX = x2;
                        lastCubicY = y2;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'S':
                    {
                        bool smooth = previousCommand == 'C' || previousCommand == 'c'
                            || previousCommand == 'S' || previousCommand == 's';
                        double x1 = smooth ? 2 * currentX - lastCubicX : currentX;
                        double y1 = smooth ? 2 * currentY - lastCubicY : currentY;
                        double x2 = ReadNumber(data, ref index) + originX;
                        double y2 = ReadNumber(data, ref index) + originY;
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddBezier((float)currentX, (float)currentY,
                            (float)x1, (float)y1, (float)x2, (float)y2, (float)x, (float)y);
                        lastCubicX = x2;
                        lastCubicY = y2;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'Q':
                    {
                        double x1 = ReadNumber(data, ref index) + originX;
                        double y1 = ReadNumber(data, ref index) + originY;
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddBezier((float)currentX, (float)currentY,
                            (float)(currentX + 2.0 / 3.0 * (x1 - currentX)),
                            (float)(currentY + 2.0 / 3.0 * (y1 - currentY)),
                            (float)(x + 2.0 / 3.0 * (x1 - x)),
                            (float)(y + 2.0 / 3.0 * (y1 - y)),
                            (float)x, (float)y);
                        lastQuadX = x1;
                        lastQuadY = y1;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'T':
                    {
                        bool smooth = previousCommand == 'Q' || previousCommand == 'q'
                            || previousCommand == 'T' || previousCommand == 't';
                        double x1 = smooth ? 2 * currentX - lastQuadX : currentX;
                        double y1 = smooth ? 2 * currentY - lastQuadY : currentY;
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        path.AddBezier((float)currentX, (float)currentY,
                            (float)(currentX + 2.0 / 3.0 * (x1 - currentX)),
                            (float)(currentY + 2.0 / 3.0 * (y1 - currentY)),
                            (float)(x + 2.0 / 3.0 * (x1 - x)),
                            (float)(y + 2.0 / 3.0 * (y1 - y)),
                            (float)x, (float)y);
                        lastQuadX = x1;
                        lastQuadY = y1;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'A':
                    {
                        double rx = ReadNumber(data, ref index);
                        double ry = ReadNumber(data, ref index);
                        double rotation = ReadNumber(data, ref index);
                        bool largeArc = ReadFlag(data, ref index);
                        bool sweep = ReadFlag(data, ref index);
                        double x = ReadNumber(data, ref index) + originX;
                        double y = ReadNumber(data, ref index) + originY;
                        AddArc(path, currentX, currentY, rx, ry, rotation, largeArc, sweep, x, y);
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    default:
                        throw new FormatException("不支持的 SVG path 命令：" + command);
                }

                previousCommand = command;
            }

            return path;
        }

        private static void SkipSeparators(string data, ref int index)
        {
            while (index < data.Length)
            {
                char c = data[index];
                if (char.IsWhiteSpace(c) || c == ',') index++;
                else break;
            }
        }

        private static double ReadNumber(string data, ref int index)
        {
            SkipSeparators(data, ref index);
            int start = index;
            if (index < data.Length && (data[index] == '+' || data[index] == '-')) index++;

            bool digits = false;
            while (index < data.Length && char.IsDigit(data[index])) { index++; digits = true; }

            if (index < data.Length && data[index] == '.')
            {
                index++;
                while (index < data.Length && char.IsDigit(data[index])) { index++; digits = true; }
            }

            if (digits && index < data.Length && (data[index] == 'e' || data[index] == 'E'))
            {
                int exponent = index;
                index++;
                if (index < data.Length && (data[index] == '+' || data[index] == '-')) index++;
                if (index < data.Length && char.IsDigit(data[index]))
                {
                    while (index < data.Length && char.IsDigit(data[index])) index++;
                }
                else
                {
                    index = exponent;
                }
            }

            if (!digits) throw new FormatException("SVG path 中的数字格式非法。");
            return double.Parse(data.Substring(start, index - start), CultureInfo.InvariantCulture);
        }

        private static bool ReadFlag(string data, ref int index)
        {
            SkipSeparators(data, ref index);
            if (index >= data.Length) throw new FormatException("SVG arc 缺少标志位。");
            char c = data[index++];
            if (c == '0') return false;
            if (c == '1') return true;
            throw new FormatException("SVG arc 标志位只能是 0 或 1。");
        }

        /// <summary>
        /// SVG 椭圆弧 → 三次贝塞尔。端点参数转中心参数用 W3C 实现说明里的公式，
        /// 每段不超过 90°，用 4/3·tan(θ/4) 近似。
        /// </summary>
        private static void AddArc(GraphicsPath path, double x1, double y1,
            double rx, double ry, double rotationDegrees, bool largeArc, bool sweep,
            double x2, double y2)
        {
            if (Math.Abs(x1 - x2) < 1e-9 && Math.Abs(y1 - y2) < 1e-9) return;

            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx < 1e-9 || ry < 1e-9)
            {
                path.AddLine((float)x1, (float)y1, (float)x2, (float)y2);
                return;
            }

            double phi = rotationDegrees * Pi / 180.0;
            double cosPhi = Math.Cos(phi);
            double sinPhi = Math.Sin(phi);

            double dx = (x1 - x2) / 2.0;
            double dy = (y1 - y2) / 2.0;
            double x1p = cosPhi * dx + sinPhi * dy;
            double y1p = -sinPhi * dx + cosPhi * dy;

            double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lambda > 1.0)
            {
                double scale = Math.Sqrt(lambda);
                rx *= scale;
                ry *= scale;
            }

            double rx2 = rx * rx;
            double ry2 = ry * ry;
            double x1p2 = x1p * x1p;
            double y1p2 = y1p * y1p;
            double denominator = rx2 * y1p2 + ry2 * x1p2;
            double numerator = rx2 * ry2 - denominator;
            double factor = denominator <= 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
            if (largeArc == sweep) factor = -factor;

            double cxp = factor * rx * y1p / ry;
            double cyp = -factor * ry * x1p / rx;
            double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2.0;
            double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2.0;

            double startAngle = VectorAngle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double deltaAngle = VectorAngle(
                (x1p - cxp) / rx, (y1p - cyp) / ry,
                (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && deltaAngle > 0) deltaAngle -= 2 * Pi;
            if (sweep && deltaAngle < 0) deltaAngle += 2 * Pi;

            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(deltaAngle) / (Pi / 2)));
            double delta = deltaAngle / segments;

            double t = 4.0 / 3.0 * Math.Tan(delta / 4.0);
            double angle = startAngle;
            double currentX = x1;
            double currentY = y1;

            for (int i = 0; i < segments; i++)
            {
                double cosA = Math.Cos(angle);
                double sinA = Math.Sin(angle);
                double next = angle + delta;
                double cosB = Math.Cos(next);
                double sinB = Math.Sin(next);

                double endX = cx + cosPhi * rx * cosB - sinPhi * ry * sinB;
                double endY = cy + sinPhi * rx * cosB + cosPhi * ry * sinB;

                double control1X = currentX + t * (-cosPhi * rx * sinA - sinPhi * ry * cosA);
                double control1Y = currentY + t * (-sinPhi * rx * sinA + cosPhi * ry * cosA);
                double control2X = endX - t * (-cosPhi * rx * sinB - sinPhi * ry * cosB);
                double control2Y = endY - t * (-sinPhi * rx * sinB + cosPhi * ry * cosB);

                path.AddBezier((float)currentX, (float)currentY,
                    (float)control1X, (float)control1Y,
                    (float)control2X, (float)control2Y,
                    (float)endX, (float)endY);

                currentX = endX;
                currentY = endY;
                angle = next;
            }
        }

        private static double VectorAngle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double length = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            if (length < 1e-12) return 0;
            double angle = Math.Acos(Math.Max(-1, Math.Min(1, dot / length)));
            return ux * vy - uy * vx < 0 ? -angle : angle;
        }
    }
}
