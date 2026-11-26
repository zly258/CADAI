#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace System.Web.Script.Serialization
{
    /// <summary>
    /// .NET 8 兼容垫片：复刻 .NET Framework <c>System.Web.Script.Serialization.JavaScriptSerializer</c>
    /// 的常用序列化与反序列化语义（大小写不敏感、宽松数字读取、object 还原为 Dictionary/object[]/基元类型），
    /// 让同一份插件源码无需改写即可同时编译 <c>net48</c>（2019-2024）与 <c>net8.0-windows</c>（2025-2026）。
    /// </summary>
    internal sealed class JavaScriptSerializer
    {
        private static readonly JsonSerializerOptions SharedOptions = CreateOptions();

        public int MaxJsonLength { get; set; } = int.MaxValue;

        public int RecursionLimit { get; set; } = 100;

        public string Serialize(object obj)
        {
            if (obj == null) return "null";
            return JsonSerializer.Serialize(obj, obj.GetType(), SharedOptions);
        }

        public object DeserializeObject(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var docOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = Math.Max(64, RecursionLimit)
            };
            using (JsonDocument doc = JsonDocument.Parse(input, docOptions))
            {
                return ConvertElement(doc.RootElement);
            }
        }

        public T Deserialize<T>(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return default;

            Type targetType = typeof(T);
            if (targetType == typeof(object)
                || targetType == typeof(Dictionary<string, object>)
                || targetType == typeof(IDictionary<string, object>))
            {
                return (T)DeserializeObject(input);
            }

            return JsonSerializer.Deserialize<T>(input, SharedOptions);
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                MaxDepth = 128
            };
            options.Converters.Add(new ObjectToInferredTypesConverter());
            return options;
        }

        internal static object ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty prop in element.EnumerateObject())
                        dict[prop.Name] = ConvertElement(prop.Value);
                    return dict;

                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(ConvertElement(item));
                    return list.ToArray();

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int i32)) return i32;
                    if (element.TryGetInt64(out long i64)) return i64;
                    if (element.TryGetDecimal(out decimal dec)) return dec;
                    return element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return null;
            }
        }

        /// <summary>
        /// 当强类型 DTO 包含 <see cref="object"/> 属性（如 AgentMessage.Content / ToolCalls）时，
        /// 将 JSON 还原为 Dictionary/object[]/基元类型，而不是 System.Text.Json 默认的 JsonElement。
        /// </summary>
        private sealed class ObjectToInferredTypesConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using (JsonDocument doc = JsonDocument.ParseValue(ref reader))
                {
                    return ConvertElement(doc.RootElement);
                }
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value == null)
                {
                    writer.WriteNullValue();
                    return;
                }
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }
        }
    }
}
#endif
