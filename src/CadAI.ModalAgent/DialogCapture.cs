using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CadAI.ModalAgent
{
    /// <summary>
    /// 弹窗截图：把目标窗口的内容抓成 PNG，供宿主交给视觉模型判断该点哪个按钮。
    ///
    /// 为什么需要截图：纯文本枚举有几类硬伤——树控件（SysTreeView32）里的正文读不到、
    /// WPF 弹窗整窗只有一个 HWND（按钮不是子窗口）、自绘控件根本没有可读文本。
    /// 跨进程读树控件文本需要 VirtualAllocEx/ReadProcessMemory，那是改宿主内存，
    /// 违反本工具"不注入、不改宿主内存"的稳定性边界；截图 + 视觉模型正好绕开这一整类问题。
    ///
    /// 稳定性：只用只读的公开 Win32 调用（GetWindowRect / PrintWindow / BitBlt），
    /// 全部异常与失败都收敛成 null 返回，绝不向调用方抛出。
    /// </summary>
    internal static class DialogCapture
    {
        /// <summary>PrintWindow 标志：让 DWM 合成窗口（含大部分现代弹窗）也渲染进位图。</summary>
        private const uint PwRenderFullContent = 0x00000002;

        private const int Srccopy = 0x00CC0020;

        /// <summary>单张截图的上限尺寸，防止异常窗口尺寸导致超大位图分配。</summary>
        private const int MaxDimension = 4096;

        /// <summary>
        /// 抓取窗口内容并保存为 PNG。失败返回 null（目录不可写、窗口已消失、尺寸异常等）。
        /// </summary>
        /// <param name="hwnd">目标窗口。</param>
        /// <param name="directory">输出目录，不存在时自动创建。</param>
        /// <param name="filePrefix">文件名前缀（不含扩展名）。</param>
        public static string? TryCapture(IntPtr hwnd, string directory, string filePrefix)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
                if (string.IsNullOrWhiteSpace(directory)) return null;
                if (!GetWindowRect(hwnd, out Rect rect)) return null;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return null;

                using var bitmap = new Bitmap(width, height);
                bool captured = false;

                // 首选 PrintWindow：抓到的是窗口自身内容，不受遮挡影响。
                try
                {
                    using var graphics = Graphics.FromImage(bitmap);
                    IntPtr hdc = graphics.GetHdc();
                    try { captured = PrintWindow(hwnd, hdc, PwRenderFullContent); }
                    finally { graphics.ReleaseHdc(hdc); }
                }
                catch { captured = false; }

                // 兜底：PrintWindow 对个别自绘/异形弹窗返回 false，此时从屏幕 DC 直接拷贝
                // 该窗口矩形。模态弹窗必然在最前面，所以这份拷贝是它自己的内容。
                if (!captured || IsUniform(bitmap))
                {
                    try
                    {
                        using var graphics = Graphics.FromImage(bitmap);
                        IntPtr screenDc = GetDC(IntPtr.Zero);
                        if (screenDc == IntPtr.Zero) return null;
                        IntPtr targetDc = graphics.GetHdc();
                        try { BitBlt(targetDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, Srccopy); }
                        finally { graphics.ReleaseHdc(targetDc); }
                        ReleaseDC(IntPtr.Zero, screenDc);
                    }
                    catch { /* 兜底也失败就交出 PrintWindow 的结果（哪怕是纯色图）。 */ }
                }

                Directory.CreateDirectory(directory);
                string path = Path.Combine(
                    directory,
                    filePrefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                bitmap.Save(path, ImageFormat.Png);
                return path;
            }
            catch
            {
                // 截图是增强手段：失败绝不能影响弹窗应答主流程。
                return null;
            }
        }

        /// <summary>
        /// 稀疏采样判断位图是否"一片纯色"。纯色说明 PrintWindow 实际上什么都没渲染出来，
        /// 这种图喂给视觉模型只会误导，因此触发屏幕 DC 兜底。
        /// </summary>
        private static bool IsUniform(Bitmap bitmap)
        {
            try
            {
                int stepX = Math.Max(1, bitmap.Width / 32);
                int stepY = Math.Max(1, bitmap.Height / 32);
                Color first = bitmap.GetPixel(0, 0);
                for (int y = 0; y < bitmap.Height; y += stepY)
                {
                    for (int x = 0; x < bitmap.Width; x += stepX)
                    {
                        Color current = bitmap.GetPixel(x, y);
                        if (current.ToArgb() != first.ToArgb()) return false;
                    }
                }
                return true;
            }
            catch
            {
                // 判断失败时按"不是纯色"处理：宁可多截一张，也不要因为判断逻辑丢图。
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr srcDc, int srcX, int srcY, int rop);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
    }
}
