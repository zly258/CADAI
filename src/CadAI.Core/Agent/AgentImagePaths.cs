using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CadAI.Core
{
    /// <summary>
    /// 从工具结果里找出"落盘的图片"。
    ///
    /// 为什么要有它：工具结果对模型而言只有文本，模型**看不见**图片。
    /// 截图类工具把 PNG 路径写进 JSON 后，模型拿到的只是一串路径字符串，
    /// 于是"截了图"对模型等于没发生——它会以为没有可看的证据，或者干脆凭空描述画面。
    /// 这个提取器把路径捞出来，交给视觉通道真正喂进去。
    ///
    /// 判定口径刻意是**按扩展名**而不是按字段名：
    ///  - AutoCAD 的截图条目形如 {"view":"iso","path":"...png"}；
    ///  - CAD 的截图条目形如 {"images":[{"ok":true,"path":"...png"}]}。
    /// 要求特定字段名会把其中一种漏掉（这正是之前 CAD 截图进不了界面的原因）。
    /// </summary>
    internal static class AgentImagePaths
    {
        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
        };

        /// <summary>单次工具结果最多提取多少张图，避免一个工具返回几百条路径。</summary>
        private const int MaxImages = 12;

        /// <summary>结果 JSON 里是否出现了图片路径的迹象（用于跳过昂贵的解析）。</summary>
        public static bool MightContainImages(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            foreach (string extension in ImageExtensions)
            {
                if (json.IndexOf(extension, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>提取图片路径（按出现顺序去重，只保留磁盘上确实存在的文件）。</summary>
        public static List<string> Extract(string json)
        {
            var result = new List<string>();
            if (!MightContainImages(json)) return result;

            try
            {
                Collect(AgentJson.New().DeserializeObject(json), result, 0);
            }
            catch
            {
                // 解析失败只影响"能不能看图"，绝不影响工具本身的结果。
            }

            return result;
        }

        private static void Collect(object value, List<string> into, int depth)
        {
            if (value == null || into.Count >= MaxImages || depth > 10) return;

            if (value is Dictionary<string, object> map)
            {
                // 只要某个键的值是"指向图片文件的路径"就收下，键名不限。
                foreach (KeyValuePair<string, object> entry in map)
                {
                    if (into.Count >= MaxImages) return;
                    Collect(entry.Value, into, depth + 1);
                }
                return;
            }

            if (value is System.Collections.IEnumerable sequence && !(value is string))
            {
                foreach (object item in sequence)
                {
                    if (into.Count >= MaxImages) return;
                    Collect(item, into, depth + 1);
                }
                return;
            }

            if (!(value is string text)) return;

            string path = text.Trim();

            // 关键修复：MCP 协议标准返回形如 {"content":[{"type":"text","text":"{\"FilePath\":\"...png\"}"}]}，
            // 嵌套在 content[].text 内部的 JSON 字符串必须二次解包，否则图片绝对路径会被漏掉导致模型无法视觉看图。
            if ((path.StartsWith("{") || path.StartsWith("[")) && MightContainImages(path))
            {
                try
                {
                    object nested = AgentJson.New().DeserializeObject(path);
                    if (nested != null)
                    {
                        Collect(nested, into, depth + 1);
                        return;
                    }
                }
                catch { }
            }

            if (!LooksLikeImagePath(path)) return;
            if (into.Contains(path, StringComparer.OrdinalIgnoreCase)) return;

            // 只收磁盘上真实存在的文件：不存在的路径喂给视觉模型只会变成一次失败请求。
            try { if (!File.Exists(path)) return; }
            catch { return; }

            into.Add(path);
        }

        private static bool LooksLikeImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) return false;
            foreach (string extension in ImageExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
