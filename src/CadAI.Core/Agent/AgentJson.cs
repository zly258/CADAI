#nullable disable

#if NETFRAMEWORK || CORE_JS_COMPAT
using System.Web.Script.Serialization;
#else
using System.Collections.Generic;
using System.Text.Json;
#endif

namespace CadAI.Core
{
    /// <summary>
    /// net48 侧（Add-in / 插件宿主）统一的 JSON 序列化入口。
    ///
    /// **为什么不用第三方库（Newtonsoft.Json / System.Text.Json）**——这是反复评估后的结论：
    ///  1. **宿主冲突**：插件跑在 AutoCAD / AutoCAD 进程里。这些宿主自己就带 Newtonsoft.Json
    ///     （版本各年不同），插件再放一份同名程序集进去就是典型的加载冲突（FileLoadException /
    ///     MissingMethodException），排查成本极高。System.Text.Json 在 net48 上还要拖
    ///     System.Memory / System.Buffers / System.Runtime.CompilerServices.Unsafe / Encodings.Web
    ///     等一串依赖，风险面更大。工程软件"稳定 &gt; 快"，不值得为这点序列化性能赌宿主加载。
    ///  2. **性能不是瓶颈**：调用方言的耗时在 LLM 推理与 CAD COM 调用上，JSON 只是搬运几十 KB
    ///     的工具参数/结果，换库带来的收益在整体里量不出来。
    ///  3. **行为回归**：JavaScriptSerializer 反序列化**大小写不敏感**、多余键自动忽略、按属性名
    ///     原样写键。换库后这些都要逐个核对（现有多处 DTO 注释就依赖这些行为），收益远小于风险。
    /// 结论：net48 侧继续用 BCL 自带的 JavaScriptSerializer；.NET 8 的 MCP 服务端用 System.Text.Json。
    /// 两端的 JSON 实现由各自目标框架决定，**刻意保持独立**，这里只把 net48 侧的配置收敛到一处。
    ///
    /// <see cref="New"/> 返回配置好的新实例：JavaScriptSerializer 本身不保证线程安全，
    /// 因此通过 <c>ThreadLocal</c> 为每个线程独立持有一份实例，杜绝并发竞争；
    /// 并统一共享"最大长度放宽到 int.MaxValue"这一策略——
    /// 默认 2 MB 上限会直接抛 InvalidOperationException（大图纸导出、长对话历史、批量 BOM 都会超），
    /// 这类失败非常隐蔽（CadJson 会把它包成 {"error": ...}），必须提前掐掉。
    /// </summary>
    public static class AgentJson
    {
#if NETFRAMEWORK || CORE_JS_COMPAT
        internal static JavaScriptSerializer New() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>按 net48 侧策略序列化（复用当前线程独立的 JSON 序列化器，见 <see cref="New"/>）。</summary>
        public static string Serialize(object value) => PerThreadSerializer.Value.Serialize(value);

        /// <summary>反序列化：对象 → <c>Dictionary&lt;string, object&gt;</c>，数组 → <c>List&lt;object&gt;</c>。</summary>
        public static object DeserializeObject(string json) => PerThreadSerializer.Value.DeserializeObject(json);

        // 线程局部实例：各线程互不干扰，兼顾线程安全（JavaScriptSerializer 实例非线程安全）
        // 与避免频繁 new/GC 开销，同时统一应用 MaxJsonLength = int.MaxValue 策略。
        private static readonly System.Threading.ThreadLocal<JavaScriptSerializer> PerThreadSerializer =
            new System.Threading.ThreadLocal<JavaScriptSerializer>(New);
#else
        // net8 分支：**只**会在测试工程以链接方式编译本文件时用到（仓库单测不引用 net48 程序集，
        // 见 CadAI.Tests.csproj 的说明）。这里用 System.Text.Json 复刻
        // JavaScriptSerializer 的形状语义——对象 → Dictionary<string, object>、
        // 数组 → List<object>——好让 AgentContextCompactor 的压缩/摘要逻辑在离线单测里
        // 跑到与 net48 同一套形状，而不是在测试里换一种 JSON 结构各测各的。
        public static string Serialize(object value)
            => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

        public static object DeserializeObject(string json)
            => Convert(JsonDocument.Parse(json).RootElement);

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions();

        private static object Convert(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject())
                        map[property.Name] = Convert(property.Value);
                    return map;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray()) list.Add(Convert(item));
                    return list;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long integer) ? integer : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
#endif
    }
}