using System;
using CadAI.Core;
using CadAI.Core.Modal;

namespace CadAI.Host.AutoCAD.Hosting
{
    /// <summary>
    /// AutoCAD 宿主的弹窗决策桥接宿主：把 ChatCore 的 <see cref="ModalDialogBridgeServer"/>
    /// 与 <see cref="ModalDecisionService"/> 组装起来并常驻。
    ///
    /// 生命周期刻意与 AutoCAD 侧一致：**不随会话销毁**。
    /// 销毁后残留的 ModalAgent 上报会无处可去（每次都白等一次短连接），而重建又要处理同管道竞争。
    /// 服务线程是后台线程，进程退出时不会阻塞关闭。
    /// </summary>
    internal static class ModalBridgeHost
    {
        private static ModalDialogBridgeServer _server;

        /// <summary>启动桥接（幂等）。返回是否已就绪。</summary>
        public static bool Start()
        {
            if (_server != null) return true;

            string pipeName = ModalAgentLauncher.PipeName;
            var service = new ModalDecisionService(Log);

            var server = new ModalDialogBridgeServer(
                pipeName,
                request => service.Decide(request),
                Log);

            server.Start();
            _server = server;

            Log("弹窗决策桥接已启动，管道 " + pipeName + "。");
            return true;
        }

        private static void Log(string message)
        {
            try
            {
                AiExceptionGuard.Log("DwgChat.ModalBridge",
                    new InvalidOperationException(message ?? string.Empty));
            }
            catch { }
        }
    }
}
