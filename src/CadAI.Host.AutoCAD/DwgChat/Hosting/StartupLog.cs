using System;
using System.IO;
using System.Text;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// 智能助手插件日志（滚动文件，best-effort，写失败一律忽略，绝不影响插件本身）。
    ///
    /// 落 <c>%LocalAppData%\CADAI\AutoCAD\startup.log</c>：插件加载记录。
    /// AutoCAD 的 Autoloader 是**静默失败**的，bundle 没被加载时既没有对话框也没有命令行提示；
    /// 文件里有当次启动的记录 = DLL 确实被加载了。超过上限就重建，避免无限增长。
    /// （翻译运行日志 translate.log 在翻译助手工程；2026-10-23 拆分产品后各记各的。）
    /// </summary>
    internal static class StartupLog
    {
        /// <summary>超过这个大小就重建文件，避免无限增长。</summary>
        private const long MaxBytes = 64 * 1024;

        /// <summary>启动记录：追加一行到 startup.log。</summary>
        public static void Write(string message)
        {
            Append(message);
        }

        /// <summary>
        /// 进程内写锁：并发落日志时 File.AppendAllText 并发写同一文件会抛 IOException 被 catch 吞掉——
        /// 丢的恰是排障最需要的记录。
        /// </summary>
        private static readonly object WriteLock = new object();

        private static void Append(string message)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"CADAI\AutoCAD",
                    "startup.log");
                lock (WriteLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));

                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes) File.Delete(path);

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // 日志写不了就算了，不能因为它影响插件
            }
        }
    }
}
