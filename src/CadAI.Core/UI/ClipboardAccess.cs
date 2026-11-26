using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CadAI.Core
{
    /// <summary>
    /// 集中的 WPF 剪贴板访问。当 AutoCAD、其他应用或剪贴板工具占用剪贴板时，
    /// Windows 可能瞬时拒绝 OpenClipboard。对短暂争用进行重试，
    /// 绝不让 CLIPBRD_E_CANT_OPEN 泄漏到插件 UI 调度器中。
    /// </summary>
    internal static class ClipboardAccess
    {
        internal const int ClipboardCantOpenHResult = unchecked((int)0x800401D0);
        private const int RetryCount = 3;
        private const int RetryDelayMilliseconds = 15;

        /// <summary>
        /// 在后台 STA 线程写剪贴板。剪贴板被占用时 WPF 的 SetText 内部会重试约 1 秒才失败，
        /// 在 UI 线程调用会让每次复制卡顿数秒；挪到后台线程后界面立即响应，
        /// 写入与失败日志都在后台完成。调用方无需等待结果。
        /// </summary>
        public static void SetTextInBackground(string text, string logSource)
        {
            if (string.IsNullOrEmpty(text)) return;
            var worker = new Thread(() =>
            {
                try
                {
                    if (!TrySetText(text, out Exception error) && error != null)
                        AiExceptionGuard.Log(logSource, error);
                }
                catch (Exception error)
                {
                    AiExceptionGuard.Log(logSource, error);
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        public static bool TrySetText(string text, out Exception error)
        {
            error = null;
            if (string.IsNullOrEmpty(text)) return false;

            for (int attempt = 0; attempt < RetryCount; attempt++)
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex;
                    if (!IsClipboardBusy(ex) || attempt == RetryCount - 1) return false;
                    Thread.Sleep(RetryDelayMilliseconds * (attempt + 1));
                }
            }
            return false;
        }

        public static bool TryGetImage(out BitmapSource source, out Exception error)
        {
            source = null;
            error = null;

            for (int attempt = 0; attempt < RetryCount; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsImage()) return false;
                    BitmapSource value = Clipboard.GetImage();
                    if (value == null) return false;
                    if (value.CanFreeze) value.Freeze();
                    source = value;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex;
                    if (!IsClipboardBusy(ex) || attempt == RetryCount - 1) return false;
                    Thread.Sleep(RetryDelayMilliseconds * (attempt + 1));
                }
            }
            return false;
        }

        public static bool TryGetFileDropList(out System.Collections.Specialized.StringCollection files, out Exception error)
        {
            files = null;
            error = null;

            for (int attempt = 0; attempt < RetryCount; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsFileDropList()) return false;
                    System.Collections.Specialized.StringCollection list = Clipboard.GetFileDropList();
                    if (list == null || list.Count == 0) return false;
                    files = list;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex;
                    if (!IsClipboardBusy(ex) || attempt == RetryCount - 1) return false;
                    Thread.Sleep(RetryDelayMilliseconds * (attempt + 1));
                }
            }
            return false;
        }

        public static bool IsClipboardBusy(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
                if (current.HResult == ClipboardCantOpenHResult) return true;
            return false;
        }
    }
}
