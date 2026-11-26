using System.Collections.Generic;

namespace CadAI.Core
{
    /// <summary>
    /// 工具面按意图收窄的策略。**可选**：宿主不提供时 ChatCore 不做任何收窄，
    /// 把宿主注册的全部工具原样交给模型。
    ///
    /// 为什么抽成接口，而不是像以前那样把某家的路由表直接编进 ChatCore：
    /// 原先 ChatCore 用 &lt;Compile Include="..\CadAI.Mcp.AutoCAD\Core\ScopeRoutingTable.cs"&gt;
    /// 把 AutoCAD 的"作用域 → 工具名"表链进自己的程序集，于是：
    ///   1) ChatCore 的编译依赖另一个产品的源码树（引用它就要先把那边的目录摆好）；
    ///   2) 那套作用域关键词（"图纸""标注""翻译""导出"…）是 AutoCAD 的工具面语言，
    ///      拿它去过滤别的宿主的工具名，行为是"碰巧对"——一个都匹配不上时
    ///      SelectToolsForScope 回退成全量，看起来正常；一旦某个工具名碰巧落在表里，
    ///      就会静默隐藏掉大部分工具（AutoCAD 自己就踩过这个坑：出图引擎的 20 个工具
    ///      两处都没登记，schema 发下去了、模型一次都调不到）。
    ///
    /// 现在 ChatCore 只认这个接口；各宿主自己提供实现（AutoCAD 侧就是薄薄一层适配，
    /// 把共享的 ScopeRoutingTable 接上来）。
    /// </summary>
    public interface IToolScopePolicy
    {
        /// <summary>
        /// 把用户文本映射到本轮应暴露的作用域（多个域取并集）。
        /// <paramref name="current"/> 是上一轮的作用域，用于让"再来一次"这类简短追问不收窄工具面。
        /// </summary>
        IReadOnlyList<string> ResolveScopes(string userText, IReadOnlyList<string> current);

        /// <summary>该工具是否应在指定作用域下暴露。</summary>
        bool IncludesTool(string scope, string toolName);
    }
}
