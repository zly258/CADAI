namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// 可选自动更新检查。当环境变量 CADAI_UPDATE_SHARE 未配置时直接跳过，不进行网络或共享目录探测。
    /// </summary>
    public static class Updater
    {
        public static string SharePath => Defaults.SharePath;

        private static readonly CadAI.Core.UpdateCheckOptions Options = new CadAI.Core.UpdateCheckOptions
        {
            ProductCaption = VersionInfo.Product,
            SharePath = Defaults.SharePath,
            VersionFileName = "CADAI.version.txt",
            SetupExePatterns = new[] { "CADAI-*-setup.exe" },
            RemoteDllName = "CadAI.Host.AutoCAD.dll",
            LocalVersion = () => typeof(Updater).Assembly.GetName().Version,
            ProgressLog = message => StartupLog.Write(message),
            ShowPrompt = (caption, message, directory, reachable) => CadAI.Core.UpdatePrompt.Show(caption, message, directory, reachable),
        };

        /// <summary>启动后台检查；未配置更新目录时直接返回。</summary>
        public static void CheckAsync()
        {
            if (string.IsNullOrWhiteSpace(Options.SharePath)) return;
            CadAI.Core.UpdateChecker.BeginCheckAsync(Options);
        }

        /// <summary>执行一次同步检查。</summary>
        public static string CheckForUpdate(out string updateDirectory)
        {
            if (string.IsNullOrWhiteSpace(Options.SharePath))
            {
                updateDirectory = null;
                return null;
            }
            return CadAI.Core.UpdateChecker.CheckForUpdate(Options, out updateDirectory);
        }
    }
}
