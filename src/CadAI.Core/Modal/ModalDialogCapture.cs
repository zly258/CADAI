using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// 弹窗截图：把宿主弹窗抓成 PNG，交给视觉模型判断该点哪个按钮。
    ///
    /// 为什么 Add-in 侧要自己抓一份，而不是复用 ModalAgent 的截图：卡死看门狗要处理的
    /// 恰恰是"ModalAgent 没在跑"的场景，那时候没有任何别的进程能提供图片。
    ///
    /// 与 ModalAgent 的 DialogCapture 是同一套做法（那边是 net8 的独立进程，不能共享
    /// 程序集，只能各留一份实现）；这里同样只用只读的公开 Win32 调用，任何失败都返回 null。
    /// </summary>
    public static class ModalDialogCapture
    {
        private const uint PwRenderFullContent = 0x00000002;
        private const int Srccopy = 0x00CC0020;
        private const int MaxDimension = 4096;

        /// <summary>抓取指定窗口并保存为 PNG；失败返回 null（不抛异常）。</summary>
        public static string TryCapture(IntPtr hwnd, string directory)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
                if (string.IsNullOrWhiteSpace(directory)) return null;
                if (!GetWindowRect(hwnd, out Rect rect)) return null;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return null;

                using (var bitmap = new Bitmap(width, height))
                {
                    bool captured = false;
                    try
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            IntPtr hdc = graphics.GetHdc();
                            try { captured = PrintWindow(hwnd, hdc, PwRenderFullContent); }
                            finally { graphics.ReleaseHdc(hdc); }
                        }
                    }
                    catch { captured = false; }

                    // 兜底：PrintWindow 对个别自绘/异形弹窗返回 false 或画出纯色图，
                    // 此时直接从屏幕 DC 拷贝该矩形（模态弹窗必然在最前面）。
                    if (!captured || IsUniform(bitmap))
                    {
                        try
                        {
                            using (var graphics = Graphics.FromImage(bitmap))
                            {
                                IntPtr screenDc = GetDC(IntPtr.Zero);
                                if (screenDc == IntPtr.Zero) return null;
                                IntPtr targetDc = graphics.GetHdc();
                                try { BitBlt(targetDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, Srccopy); }
                                finally { graphics.ReleaseHdc(targetDc); }
                                ReleaseDC(IntPtr.Zero, screenDc);
                            }
                        }
                        catch { /* 兜底也失败就交出 PrintWindow 的结果（哪怕纯色）。 */ }
                    }

                    System.IO.Directory.CreateDirectory(directory);
                    string path = System.IO.Path.Combine(
                        directory,
                        "dialog-watchdog-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    return path;
                }
            }
            catch
            {
                // 截图是增强手段：失败绝不能影响弹窗处置主流程。
                return null;
            }
        }

        /// <summary>稀疏采样判断是否"一片纯色"（说明什么都没渲染出来，需要走屏幕兜底）。</summary>
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
                        if (bitmap.GetPixel(x, y).ToArgb() != first.ToArgb()) return false;
                    }
                }
                return true;
            }
            catch
            {
                // 判断失败按"不是纯色"处理：宁可多截一张，也不要因为判断逻辑丢图。
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
