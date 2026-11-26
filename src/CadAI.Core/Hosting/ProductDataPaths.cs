using System;
using System.IO;

namespace CadAI.Core
{
    /// <summary>
    /// CADAI 在用户数据目录（Roaming / Local AppData / 文档）下的统一落点约定：
    /// <code>
    /// %APPDATA%\CADAI\AutoCAD\            ai-settings.json / user-preferences.json
    /// %APPDATA%\CADAI\Exchange\           跨工具交换目录
    /// %LOCALAPPDATA%\CADAI\AutoCAD\Logs\  运行日志
    /// 我的文档\CADAI\                     工作区（Conversations/ + Shared/）
    /// </code>
    /// </summary>
    public static class ProductDataPaths
    {
        /// <summary>CADAI 在 AppData 下的统一根目录名。</summary>
        public const string BrandRootFolder = "CADAI";

        /// <summary>AutoCAD 助手在 <c>%APPDATA%\CADAI\</c> 下的子目录名。</summary>
        public const string AutoCadProductFolder = "AutoCAD";

        /// <summary>AutoCAD 助手在“我的文档”下的工作区目录名。</summary>
        public const string AutoCadWorkspaceFolderName = "CADAI";

        /// <summary>AutoCAD 工作区默认绝对路径（<c>我的文档\CADAI</c>）。</summary>
        public static string DefaultAutoCadWorkspace => UnderDocuments(AutoCadWorkspaceFolderName);

        /// <summary>AutoCAD 助手的用户数据目录绝对路径（<c>%APPDATA%\CADAI\AutoCAD</c>）。</summary>
        public static string AutoCadUserData => UnderRoaming(BrandRootFolder, AutoCadProductFolder);

        /// <summary>AutoCAD 助手日志目录（<c>%LOCALAPPDATA%\CADAI\AutoCAD\Logs</c>）。</summary>
        public static string AutoCadLogs => UnderLocalAppData(BrandRootFolder, AutoCadProductFolder, "Logs");

        /// <summary>把相对片段拼到 Roaming AppData 根下，返回绝对路径。</summary>
        public static string UnderRoaming(params string[] segments)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            if (segments == null || segments.Length == 0) return root ?? string.Empty;

            var parts = new System.Collections.Generic.List<string> { root ?? string.Empty };
            for (int i = 0; i < segments.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(segments[i]))
                    parts.Add(segments[i]);
            }
            return Path.Combine(parts.ToArray());
        }

        /// <summary>把相对片段拼到 Local AppData 根下，返回绝对路径。</summary>
        public static string UnderLocalAppData(params string[] segments)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            if (segments == null || segments.Length == 0) return root ?? string.Empty;

            var parts = new System.Collections.Generic.List<string> { root ?? string.Empty };
            for (int i = 0; i < segments.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(segments[i]))
                    parts.Add(segments[i]);
            }
            return Path.Combine(parts.ToArray());
        }

        /// <summary>把相对片段拼到“我的文档”根下，返回绝对路径。</summary>
        public static string UnderDocuments(params string[] segments)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            if (segments == null || segments.Length == 0) return root ?? string.Empty;

            var parts = new System.Collections.Generic.List<string> { root ?? string.Empty };
            for (int i = 0; i < segments.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(segments[i]))
                    parts.Add(segments[i]);
            }
            return Path.Combine(parts.ToArray());
        }
    }
}
