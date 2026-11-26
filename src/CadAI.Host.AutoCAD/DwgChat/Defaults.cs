using System;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// 智能助手的可选部署常量。开源默认不连接任何内网共享目录；
    /// 如团队内部自建更新源，可通过环境变量 CADAI_UPDATE_SHARE 指定目录。
    /// </summary>
    internal static class Defaults
    {
        /// <summary>可选更新共享目录（默认为空，不触发内网探测）。</summary>
        public static string SharePath
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable("CADAI_UPDATE_SHARE");
                    return string.IsNullOrWhiteSpace(env) ? string.Empty : env.Trim();
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }
    }
}
