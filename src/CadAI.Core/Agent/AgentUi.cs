using System;
using System.Collections.Generic;

namespace CadAI.Core
{
    /// <summary>
    /// Agent 与对话界面之间的唯一契约。取代此前散落的多个 Action 委托，
    /// 让 Agent 只关心“阶段开始了/需要重置/流式内容/工具活动/用量”，不关心 UI 细节。
    /// </summary>
    internal interface IAgentUi
    {
        void BeginPhase();
        void ResetPhase(string reason);
        void AppendContent(string delta);
        void AppendReasoning(string delta);
        void Message(string role, string body);
        void Tool(ToolActivity activity);
        void Preview(IReadOnlyList<PreviewImage> images);
        void Usage(string stage, AgentUsage usage);
        /// <summary>
        /// 更新等待占位区的一行状态（重试/压缩等中间过程）。空串恢复默认文案。
        /// 实现必须线程安全：Agent 在后台线程调用。仅改提示文字，不产生消息记录。
        /// </summary>
        void SetActivityHint(string text);

        /// <summary>
        /// 声明"一次长时间等待开始了"（模型请求 / 自动视觉校验）。
        /// 实现应在等待占位区显示：阶段说明 + **实时累计时长** + 本次上限，
        /// 让用户一眼分清"在等"还是"死了"——历史问题正是长达数分钟的空等
        /// 在界面上只有一个转圈，用户完全不知道系统在做什么。
        /// 与 <see cref="SetActivityHint"/> 配合：hint 覆盖说明文字，计时行始终保留。
        /// 实现必须线程安全：Agent 在后台线程调用。
        /// </summary>
        void BeginWait(string description, TimeSpan timeout);

        /// <summary>
        /// 等待结束：停止计时并清空阶段说明。重复调用无副作用（必须在 finally 里调用）。
        /// </summary>
        void EndWait();
    }

    internal sealed class PreviewImage
    {
        public PreviewImage(string view, string path)
        {
            View = view ?? string.Empty;
            Path = path ?? string.Empty;
        }

        public string View { get; }
        public string Path { get; }
    }
}
