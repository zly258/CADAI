using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace CadAI.Mcp.AutoCAD.Core
{
    /// <summary>
    /// DWG 工具参数/结果的 JSON 入口（零 ChatCore.dll 依赖）。
    ///
    /// 每次调用现取一个 serializer（<see cref="NewSerializer"/>）：JavaScriptSerializer 不保证实例线程安全，
    /// 而工具调用可能来自不同线程；同时统一把 MaxJsonLength 放宽到 int.MaxValue。
    /// </summary>
    internal static class CadJson
    {
        public static JavaScriptSerializer NewSerializer()
            => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static string Serialize(object obj)
        {
            if (obj == null) return "{}";
            try
            {
                return NewSerializer().Serialize(obj);
            }
            catch (Exception ex)
            {
                return "{\"error\":\"Serialization error: " + ex.Message.Replace("\"", "'") + "\"}";
            }
        }

        public static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            try
            {
                return NewSerializer().Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        public static string GetString(Dictionary<string, object> dict, string key, string defaultValue = "")
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
                return Convert.ToString(val);
            return defaultValue;
        }

        public static double GetDouble(Dictionary<string, object> dict, string key, double defaultValue = 0.0)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                if (double.TryParse(Convert.ToString(val), out var d)) return d;
            }
            return defaultValue;
        }

        public static int GetInt(Dictionary<string, object> dict, string key, int defaultValue = 0)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                if (int.TryParse(Convert.ToString(val), out var i)) return i;
            }
            return defaultValue;
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue = false)
        {
            if (dict != null && dict.TryGetValue(key, out var val) && val != null)
            {
                if (bool.TryParse(Convert.ToString(val), out var b)) return b;
            }
            return defaultValue;
        }
    }
}
