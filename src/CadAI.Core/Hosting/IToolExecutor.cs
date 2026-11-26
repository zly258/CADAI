using System;
using System.Collections.Generic;
using System.Threading;

namespace CadAI.Core
{
    /// <summary>
    /// 宿主提供的工具执行面：把对话核心需要的全部工具能力收敛到一个接口后面。
    ///
    /// **传输层由宿主决定，ChatCore 不做任何假设。**
    /// 两种已验证的形态：
    ///   - 进程内直连：工具在宿主进程里执行（AutoCAD 侧的 DwgChat 就是这种，
    ///     所有工具都返回 <see cref="IsRoutedTool"/>=true）；
    ///   - 进程内 + stdio 混合：宿主的执行器自己拉起外部 MCP 子进程并转发未命中路由的调用
    ///     （CadAI.Host.AutoCAD 的 AutoCADToolExecutor + McpProcessClient 是这种）。
    ///
    /// ChatCore 里**没有** MCP 协议客户端：它只调 <see cref="Execute"/>。
    /// 因此"ChatCore 不支持 stdio MCP"不是缺陷，而是刻意的分层——
    /// 需要 stdio 的宿主自己实现 <see cref="IToolExecutor"/> 的那一半。
    /// </summary>
    public interface IToolExecutor : IDisposable
    {
        /// <summary>
        /// 合并后的 OpenAI function 工具清单（进程内注册表 + 外部 MCP 服务器公布的工具）。
        /// 每项形如 { type=function, function={ name, description, parameters } }。
        /// </summary>
        IReadOnlyList<object> ToolSchemas();

        /// <summary>
        /// 该工具名是否由宿主**进程内直连**执行（不走外部 MCP 服务器）。
        ///
        /// 这个标记影响两处判定，别随便返回 true：
        ///   - 进程内工具：变更标记取自执行结果的 <see cref="McpExecutionResult.MutatesDrawing"/>，
        ///     并且不登记"状态检查点"（进程内工具的状态由结果自证）；
        ///   - 外部工具：变更/视觉变更/检查点按工具名与宿主语义配置推断。
        /// </summary>
        bool IsRoutedTool(string toolName);

        /// <summary>
        /// 执行一个工具调用并返回结果。失败时抛异常，由 AgentSession 统一转成结构化错误 JSON。
        /// 结果里除 JSON 外还可带 <see cref="McpExecutionResult.ImagePaths"/>——
        /// 只把图片路径写进 JSON 的话模型是看不见图的。
        /// </summary>
        /// <param name="cancellationToken">
        /// 用户在界面上按「停止」时传播进来的取消令牌。宿主必须把它当作"别再等下去"的信号：
        /// 中止还在排队的派发、结束带超时的等待，并且**不要把它吞掉**——让
        /// <see cref="OperationCanceledException"/> 冒到 AgentSession，那一轮才会真正停下。
        /// 已经落到 CAD 主线程 / AutoCAD COM 线程上执行的那一段无法中断（COM 本来就不能打断），
        /// 宿主至少要让框架不再为它无限期等待；把这条语义写进宿主自己的注释里，不要假装能回滚。
        /// </param>
        McpExecutionResult Execute(string toolName, string argumentsJson, CancellationToken cancellationToken);

        /// <summary>
        /// 外部 MCP 服务器最近的 stderr 日志尾部；失败诊断时附进对话，
        /// 因为 MCP SDK 会把工具异常替换成一句通用消息。
        /// 纯进程内执行的宿主返回空串即可。
        /// </summary>
        string RecentServerLog();

        /// <summary>
        /// 一轮对话开始前由 AgentSession 调用，宿主可在此开启事务撤销标记组（如 AutoCAD StartUndoMark）。
        /// </summary>
        void BeginTurn();

        /// <summary>
        /// 一轮对话结束后由 AgentSession 统一调用，宿主在此闭合撤销标记组。
        /// </summary>
        void EndTurn();

        /// <summary>
        /// 提取当前宿主运行时的轻量上下文摘要（如活动图纸名、当前空间、光标预选实体，耗时 < 5ms）。
        /// 非空时会在用户提问前注入为系统上下文，避免 AI 产生额外的探查往返。
        /// </summary>
        string GetActiveContextSummary();
    }
}
