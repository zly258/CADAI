using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CadAI.Core
{
    public static class AiExceptionGuard
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private static readonly object Sync = new object();
        private static readonly HashSet<Dispatcher> Dispatchers = new HashSet<Dispatcher>();
        private static bool _processHooked;
        private static int _dialogOpen;
        private static DateTime _lastDialogUtc = DateTime.MinValue;
        private static DateTime _lastSuspendedLogUtc = DateTime.MinValue;

        // Phase 2：日志目录来自宿主环境（IHostEnvironment.LogDirectory，经 ChatCoreHost 注入）；
        // AutoCAD 宿主提供的默认值是 %LOCALAPPDATA%\CADAI\AutoCAD\logs。
        public static string LogPath => Path.Combine(ChatCoreHost.RequireEnvironment().LogDirectory, "unhandled-exception.txt");

        public static void Install(Dispatcher dispatcher)
        {
            Dispatcher target;
            try { target = dispatcher ?? Dispatcher.CurrentDispatcher; } catch { return; }
            if (target == null) return;
            lock (Sync)
            {
                if (Dispatchers.Add(target)) target.UnhandledException += OnDispatcherUnhandledException;
                if (_processHooked) return;
                _processHooked = true;
            }
            try { AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException; } catch { }
            try { TaskScheduler.UnobservedTaskException += OnUnobservedTaskException; } catch { }
        }

        public static void Log(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                // 剪贴板后台线程等也会写日志，加锁避免并发 AppendAllText 抛 IOException。
                lock (Sync)
                {
                    string path = LogPath;
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes) File.Delete(path);
                    var sb = new StringBuilder();
                    sb.Append("===== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" | ").Append(source ?? "unknown").AppendLine(" =====");
                    sb.AppendLine(ex.ToString()).AppendLine();
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
                }
            }
            catch { }
        }

        /// <summary>
        /// 判定是否为 AutoCAD 宿主 WPF 窗口在输入法组合/布局期间触发的经典
        /// “Dispatcher processing has been suspended” 重入异常。
        /// 此类异常绝不能在当前 WndProc 内同步弹 MessageBox，否则模态消息循环会无限重入并刷屏报错。
        /// </summary>
        internal static bool IsDispatcherSuspendedReentrancy(Exception ex)
        {
            if (!(ex is InvalidOperationException)) return false;
            string stack = ex.StackTrace ?? string.Empty;
            string msg = ex.Message ?? string.Empty;
            if (stack.IndexOf("Dispatcher.WndProcHook", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return msg.IndexOf("Dispatcher processing has been suspended", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("调度程序进程已挂起", StringComparison.Ordinal) >= 0;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (ClipboardAccess.IsClipboardBusy(e.Exception))
            {
                Log("DispatcherClipboardBusy", e.Exception);
                e.Handled = true;
                return;
            }

            // AutoCAD 原生消息泵 + 中文输入法在 WPF TextBox 输入时偶发 Dispatcher 挂起期间重入 WndProcHook：
            // 仅限频记录日志并静默消化，绝不弹出模态框（弹框本身会立刻放大为无限异常风暴）。
            if (IsDispatcherSuspendedReentrancy(e.Exception))
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastSuspendedLogUtc).TotalSeconds >= 30)
                {
                    _lastSuspendedLogUtc = now;
                    Log("DispatcherSuspendedReentrancy", e.Exception);
                }
                e.Handled = true;
                return;
            }

            Log("DispatcherUnhandledException", e.Exception);
            e.Handled = true;

            // 其他未处理异常：防重入 + 10 秒冷却 + 延迟到 ApplicationIdle 再提示，避免在挂起状态弹框死循环。
            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - _lastDialogUtc).TotalSeconds < 10) return;
            if (System.Threading.Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0) return;
            _lastDialogUtc = utcNow;

            string message = e.Exception?.Message ?? UiLanguage.T("未知错误", "Unknown error");
            Dispatcher dispatcher = e.Dispatcher;
            if (dispatcher == null)
            {
                System.Threading.Interlocked.Exchange(ref _dialogOpen, 0);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        string title = UiLanguage.T("CAD 智能对话", "CAD AI Chat");
                        string prefix = UiLanguage.T(
                            title + " 捕获到异常，已记录日志。",
                            title + " caught an error and saved details to the log.");
                        MessageBox.Show(prefix + "\n\n" + message + "\n\n" + LogPath, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _dialogOpen, 0);
                    }
                }));
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _dialogOpen, 0);
            }
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) => Log("AppDomainUnhandledException", e.ExceptionObject as Exception);
        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) { Log("UnobservedTaskException", e.Exception); try { e.SetObserved(); } catch { } }
    }
}
