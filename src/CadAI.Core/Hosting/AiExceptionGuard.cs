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

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (ClipboardAccess.IsClipboardBusy(e.Exception))
            {
                Log("DispatcherClipboardBusy", e.Exception);
                e.Handled = true;
                return;
            }

            Log("DispatcherUnhandledException", e.Exception);
            try
            {
                string prefix = ChatCoreHostConfig.UiProductTitleOrDefault() + " 捕获到异常，已记录日志。";
                string unknown = "未知错误";
                MessageBox.Show(prefix + "\n\n" + (e.Exception?.Message ?? unknown) + "\n\n" + LogPath, ChatCoreHostConfig.UiProductTitleOrDefault(), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            e.Handled = true;
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) => Log("AppDomainUnhandledException", e.ExceptionObject as Exception);
        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) { Log("UnobservedTaskException", e.Exception); try { e.SetObserved(); } catch { } }
    }
}
