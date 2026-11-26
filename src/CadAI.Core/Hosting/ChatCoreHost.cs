using System;

namespace CadAI.Core
{
    /// <summary>
    /// ChatCore 的宿主装配点。宿主（CadAI.Host.AutoCAD 的 CADAIAddInServer.Activate）在启动时安装一次：
    ///   - IHostEnvironment：工作区 / 历史 / 日志 / MCP 可执行文件等路径；
    ///   - IToolExecutor 工厂：每个会话一个执行器（进程内图纸直连 + stdio MCP）；
    ///   - 可选的运行时配置路径覆盖（宿主 runtime.json：cadai.runtime.json / cadai.runtime.json）。
    /// ChatCore 内部一律经由本类取宿主能力，不再出现任何 Autodesk AutoCAD 类型。
    /// 注：宿主模态框自动处理已整体移除（外部独立 hook 程序负责）。
    /// </summary>
    public static class ChatCoreHost
    {
        public static IHostEnvironment Environment { get; private set; }
        public static Func<IToolExecutor> ToolExecutorFactory { get; private set; }
        public static string RuntimeConfigPathOverride { get; private set; }

        /// <summary>由宿主在 Add-in 激活时装入一次；重复安装以最后一次为准。</summary>
        public static void Install(
            IHostEnvironment environment,
            Func<IToolExecutor> toolExecutorFactory,
            string runtimeConfigPathOverride = null)
        {
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            ToolExecutorFactory = toolExecutorFactory ?? throw new ArgumentNullException(nameof(toolExecutorFactory));
            RuntimeConfigPathOverride = string.IsNullOrWhiteSpace(runtimeConfigPathOverride)
                ? null
                : runtimeConfigPathOverride;
        }

        public static IHostEnvironment RequireEnvironment()
        {
            var environment = Environment;
            if (environment == null)
                throw new InvalidOperationException(
                    "ChatCore host was not initialized: IHostEnvironment is not installed. " +
                    "The host must call ChatCoreHost.Install during startup before opening any chat surface.");
            return environment;
        }

        public static IToolExecutor CreateToolExecutor()
        {
            var factory = ToolExecutorFactory;
            if (factory == null)
                throw new InvalidOperationException(
                    "CADAI ChatCore host was not initialized: ToolExecutorFactory is not installed. " +
                    "The host must call ChatCoreHost.Install during startup before opening any chat surface.");
            return factory();
        }
    }
}
