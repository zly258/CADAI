using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using CadAI.Core;

namespace CadAI.Host.AutoCAD.Hosting
{
    internal sealed class CadHostEnvironment : IHostEnvironment
    {
        public string WorkspaceRoot
        {
            get
            {
                // 固定为产品专属工作区，绝不擅自污染用户图纸所在目录（避免生成 Conversations、Captures 等）。
                string dir = ProductDataPaths.DefaultAutoCadWorkspace;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public string HistoryDirectory
        {
            get
            {
                // 与 AutoCAD 端统一：旧版平铺历史固定在工作区根下，且**不提前创建**。
                // 正常路径按对话分目录落在 Conversations\<会话>\Conversation\。
                return Path.Combine(WorkspaceRoot, "History");
            }
        }

        public string LogDirectory
        {
            get
            {
                var path = ProductDataPaths.AutoCadLogs;
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        // 每用户配置（ai-settings.json / user-preferences.json）落点：产品专属目录，不与 AutoCAD 共用。
        public string UserDataDirectory
        {
            get
            {
                var path = ProductDataPaths.AutoCadUserData;
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        public string McpExecutablePath => null;
    }
}
