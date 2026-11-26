using System;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// CADAI 版本与开发者信息。版本号以程序集版本为唯一真源（Directory.Build.props）。
    /// </summary>
    public static class VersionInfo
    {
        public static string Version
        {
            get
            {
                try
                {
                    System.Version v = typeof(VersionInfo).Assembly.GetName().Version;
                    return v == null ? "0.0.0" : v.ToString(3);
                }
                catch { return "0.0.0"; }
            }
        }

        public const string Product = "CADAI";
        public const string Developer = "zly258";
        public const string DeveloperUrl = "https://github.com/zly258";
        public const string RepositoryUrl = "https://github.com/zly258/CADAI";
        public const string SponsorUrl = "https://ifdian.net/order/create?user_id=3db8e630e6dc11f1b37452540025c377";
        public const string License = "GNU General Public License v3.0 (GPL-3.0)";
        public const string Copyright = "Copyright © 2026 zly258 · GPL-3.0";
    }
}
