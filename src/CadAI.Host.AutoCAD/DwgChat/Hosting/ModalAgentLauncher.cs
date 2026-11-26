using CadAI.Core.Modal;
using System;

namespace CadAI.Host.AutoCAD.Hosting
{
    /// <summary>
    /// ModalAgent（进程外模态弹窗自动应答看门狗）在 AutoCAD 宿主中的启动/管理：
    /// 薄包装层，直接复用 ChatCore.Modal.ModalAgentLauncher。
    /// 固定 identity="DwgAI"，统一支持 --pipe、--capture-dir、宿主专属规则与多源路径解析。
    /// </summary>
    internal static class ModalAgentLauncher
    {
        public const string DefaultIdentity = "DwgAI";

        static ModalAgentLauncher()
        {
            EnsureConfigured();
        }

        internal static void EnsureConfigured()
        {
            try
            {
                CadAI.Core.Modal.ModalAgentLauncher.Configure(identity: DefaultIdentity);
            }
            catch (Exception ex)
            {
                CadAI.Core.AiExceptionGuard.Log("DwgChat.ModalAgentLauncher.Configure", ex);
            }
        }

        public static string PipeName => CadAI.Core.Modal.ModalAgentLauncher.PipeName;

        public static bool IsRunning() => CadAI.Core.Modal.ModalAgentLauncher.IsRunning();

        public static bool Start(string targetProcessName = "acad", bool debug = false)
        {
            EnsureConfigured();
            return CadAI.Core.Modal.ModalAgentLauncher.Start(targetProcessName, debug);
        }

        public static void Stop() => CadAI.Core.Modal.ModalAgentLauncher.Stop();
    }
}
