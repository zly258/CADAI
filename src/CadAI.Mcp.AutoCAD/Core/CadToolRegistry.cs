using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadAI.Mcp.AutoCAD.Core
{
    internal sealed class CadToolRegistry
    {
        private readonly Dictionary<string, Func<string, Document, Transaction, McpExecutionResult>> _handlers =
            new Dictionary<string, Func<string, Document, Transaction, McpExecutionResult>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// "后台工具"：不在 UI 线程上执行、也不套外层文档锁与事务，由处理器自己按阶段调度。
        ///
        /// 为什么需要：整图翻译这类工具慢在 HTTP（几十秒到几分钟），把它压在 AutoCAD 主线程上
        /// 会让整个 CAD 冻结且无法取消。这类工具的正确形态是
        /// 「主线程读一小段 → 后台做慢活 → 主线程写一小段」，所以它不能被执行器统一包在
        /// 一个 UI 线程事务里。
        /// </summary>
        private readonly Dictionary<string, Func<string, McpExecutionResult>> _backgroundHandlers =
            new Dictionary<string, Func<string, McpExecutionResult>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<object> _schemas = new List<object>();

        /// <summary>已登记的工具名。重复 Register 时只覆盖处理器、不重复追加 schema——
        /// 工具清单是发给模型的，出现同名两条会让模型困惑，也白白吃掉上下文。</summary>
        private readonly HashSet<string> _registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 把一段短操作切回 UI 线程执行。由宿主在装配时注入（<c>dispatcher.Invoke</c>）。
        /// 未注入时直接就地执行——那样后台工具在需要碰 Database 的地方会失败，
        /// 但不会静默走错线程。
        /// </summary>
        public Action<Action> UiInvoke { get; set; }

        public void Register(string name, Func<string, Document, Transaction, McpExecutionResult> handler, object schema)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            _handlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
            _backgroundHandlers.Remove(name);
            AddSchema(name, schema);
        }

        /// <summary>登记一个后台工具（自己调度线程与事务，见 <see cref="_backgroundHandlers"/>）。</summary>
        public void RegisterBackground(string name, Func<string, McpExecutionResult> handler, object schema)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            _backgroundHandlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
            _handlers.Remove(name);
            AddSchema(name, schema);
        }

        private void AddSchema(string name, object schema)
        {
            if (schema != null && _registered.Add(name)) _schemas.Add(schema);
        }

        /// <summary>该工具名是否已登记（前台或后台）。宿主扩展点用它做幂等保护。</summary>
        public bool Contains(string name)
            => !string.IsNullOrWhiteSpace(name)
               && (_handlers.ContainsKey(name) || _backgroundHandlers.ContainsKey(name));

        /// <summary>是否是后台工具（执行器据此决定要不要切 UI 线程 / 套事务）。</summary>
        public bool IsBackground(string name)
            => !string.IsNullOrWhiteSpace(name) && _backgroundHandlers.ContainsKey(name);

        public int Count => _handlers.Count + _backgroundHandlers.Count;

        public McpExecutionResult Execute(string name, string argsJson, Document doc, Transaction tr)
        {
            if (_handlers.TryGetValue(name, out var handler))
            {
                return handler(argsJson, doc, tr);
            }

            return Unknown(name);
        }

        /// <summary>执行一个后台工具（调用方负责线程与超时）。</summary>
        public McpExecutionResult ExecuteBackground(string name, string argsJson)
        {
            if (_backgroundHandlers.TryGetValue(name, out var handler))
            {
                return handler(argsJson);
            }

            return Unknown(name);
        }

        private static McpExecutionResult Unknown(string name) => new McpExecutionResult
        {
            ToolName = name,
            Json = "{\"error\":\"Unknown tool: " + name + "\"}",
            MutatesDrawing = false
        };

        public IReadOnlyList<object> GetSchemas() => _schemas;
    }
}
