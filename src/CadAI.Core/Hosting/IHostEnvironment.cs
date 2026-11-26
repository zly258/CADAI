namespace CadAI.Core
{
    /// <summary>
    /// 宿主环境：ChatCore 内所有路径类默认值都从这里取，不再自行推导宿主位置。
    /// 每个宿主提供一份实现（AutoCAD 宿主在 CadAI.Host.AutoCAD 的 <c>AutoCADHostEnvironment</c>，
    /// AutoCAD 宿主在 DwgChat 的 <c>CadHostEnvironment</c>）。
    ///
    /// 契约边界：本接口只描述**路径**。工具怎么执行（进程内直连还是 stdio MCP）由
    /// <see cref="IToolExecutor"/> 的实现决定，ChatCore 对此不做假设。
    /// </summary>
    public interface IHostEnvironment
    {
        /// <summary>生成/中间文件的唯一可写工作区根目录。</summary>
        string WorkspaceRoot { get; }

        /// <summary>对话历史落盘目录（默认 <see cref="WorkspaceRoot"/> 下的 History）。</summary>
        string HistoryDirectory { get; }

        /// <summary>异常守卫日志目录。宿主应给一个属于自己产品的目录，避免多个产品共写一份日志。</summary>
        string LogDirectory { get; }

        /// <summary>
        /// 产品专属的用户数据目录（Roaming AppData 下），存放 <c>ai-settings.json</c> /
        /// <c>user-preferences.json</c> 等每用户配置（<c>AiSettings.SettingsPath</c> 从这里取）。
        ///
        /// **必须产品专属**：两个助手（AutoCAD / AutoCAD）各占一份，禁止共用——历史缺陷是两者都写
        /// <c>%APPDATA%\CADAI\</c>，在任一产品里改设置会串到另一个产品。实现时用
        /// <see cref="ProductDataPaths"/> 的常量拼接，不要再手写字面路径。
        /// </summary>
        string UserDataDirectory { get; }

        /// <summary>
        /// 独立 MCP 服务器可执行文件的优先路径。
        ///
        /// **ChatCore 自己从不读这个值**——它不知道 stdio MCP 的存在。这个属性是宿主给自己人看的
        /// 提示位：宿主的 <c>IToolExecutor</c> 实现（例如 CadAI.Host.AutoCAD 的 McpProcessClient）
        /// 经 <c>ChatCoreHost.Environment</c> 取它，用来决定"要不要拉起外部 MCP 子进程、从哪拉起"。
        /// 空/null 表示由宿主自行探测。只做进程内直连的宿主（例如 AutoCAD 侧的 DwgChat）返回 null 即可。
        /// </summary>
        string McpExecutablePath { get; }
    }
}
