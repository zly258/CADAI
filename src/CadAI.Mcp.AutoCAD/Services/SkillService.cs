using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 技能包（skills\ 目录）的**按需加载**工具。
    ///
    /// 为什么必须有这一对工具：技能包（SKILL.md + 若干 references）随包部署到了
    /// Contents\skills\ 下，但此前**没有任何代码读它**——ChatCore 里只有整包注入机制，
    /// 而真正按需读取技能文档的工具在 CADAI 侧属于独立 MCP 进程
    /// （cad_skill_list / cad_skill_read），CAD 宿主没有对应实现。
    /// 结果是那 8 份工程规程文档从未进入过模型上下文，等于白写。
    ///
    /// 设计上刻意做成"先列清单、再按需读**一条**"而不是整包注入：
    /// 全部 references 一次性塞进上下文既贵又会稀释指令，还会挤掉真正的图纸事实。
    /// </summary>
    internal static class SkillService
    {
        /// <summary>单个技能文档的大小上限（256 KB），防止误读大文件把上下文冲爆。</summary>
        private const int MaxSkillBytes = 256 * 1024;

        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_skill_list", HandleList,
                CadSchema.Function("cad_skill_list",
                    "List the installed DWG skill packages and every document inside them (id, path, one-line scope). "
                    + "Call this FIRST to see which engineering playbook applies, then load only the ONE document you need with cad_skill_read.",
                    CadSchema.Obj(
                        CadSchema.Str("package", "Optional package name filter (e.g. 'dwgai'). Omit to list all packages."))));

            registry.Register("cad_skill_read", HandleRead,
                CadSchema.Function("cad_skill_read",
                    "Read ONE skill document from an installed package (the entry SKILL.md, or a single reference playbook). "
                    + "Load only the document the current task needs — do not load the whole package.",
                    CadSchema.Obj(
                        CadSchema.Str("package", "Package name or id (e.g. 'dwgai').", true),
                        CadSchema.Str("id", "Document id or path, e.g. 'SKILL.md' or 'references/translation.md'.", true))));
        }

        // ================================================================== 清单

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string filter = CadJson.GetString(args, "package");

            string root = SkillsRoot();
            if (root == null)
                return Failure("cad_skill_list", "技能包目录不存在：skills\\（应位于插件 Contents 目录下）。");

            var packages = new List<object>();
            foreach (string manifestPath in SafeEnumerate(root, "*.manifest.json"))
            {
                Dictionary<string, object> manifest;
                try { manifest = ParseManifest(manifestPath); }
                catch (Exception ex)
                {
                    packages.Add(new Dictionary<string, object>
                    {
                        ["manifest"] = Path.GetFileName(manifestPath),
                        ["error"] = ex.Message
                    });
                    continue;
                }

                string name = Str(manifest, "name") ?? Path.GetFileNameWithoutExtension(manifestPath);
                string id = Str(manifest, "id");

                if (!string.IsNullOrWhiteSpace(filter)
                    && !Contains(name, filter) && !Contains(id, filter))
                    continue;

                packages.Add(BuildPackageView(root, manifestPath, manifest, name, id));
            }

            if (packages.Count == 0)
            {
                return Failure("cad_skill_list", string.IsNullOrWhiteSpace(filter)
                    ? "技能包目录里没有任何 *.manifest.json。"
                    : "没有名为 '" + filter + "' 的技能包。");
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["skillsRoot"] = root,
                ["packages"] = packages,
                ["hint"] = "挑一条与本任务最相关的文档，用 cad_skill_read 读它；不要一次读完。"
            };

            return Ok("cad_skill_list", CadJson.Serialize(data));
        }

        // ================================================================== 读取

        private static McpExecutionResult HandleRead(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string package = CadJson.GetString(args, "package");
            string id = CadJson.GetString(args, "id");

            if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(id))
                return Failure("cad_skill_read", "package 与 id 均为必填。");

            string root = SkillsRoot();
            if (root == null)
                return Failure("cad_skill_read", "技能包目录不存在：skills\\。");

            // 找包
            string packageDir = null;
            Dictionary<string, object> manifest = null;
            string packageName = null;

            foreach (string manifestPath in SafeEnumerate(root, "*.manifest.json"))
            {
                Dictionary<string, object> candidate;
                try { candidate = ParseManifest(manifestPath); } catch { continue; }

                string name = Str(candidate, "name") ?? Path.GetFileNameWithoutExtension(manifestPath);
                string pid = Str(candidate, "id");
                if (!Contains(name, package) && !Equals(pid, package)) continue;

                packageDir = Path.GetDirectoryName(manifestPath);
                manifest = candidate;
                packageName = name;
                break;
            }

            if (packageDir == null)
                return Failure("cad_skill_read", "没有名为 '" + package + "' 的技能包。先用 cad_skill_list 查看可用包。");

            // 解析文档路径
            string relative = ResolveDocumentPath(packageDir, manifest, id.Trim());
            if (relative == null)
                return Failure("cad_skill_read",
                    "技能包 '" + packageName + "' 里没有 id/路径为 '" + id + "' 的文档。先用 cad_skill_list 查看可用条目。");

            // 目录穿越防护：解析后的绝对路径必须仍在包目录内。
            string full = Path.GetFullPath(Path.Combine(packageDir, relative));
            string normalizedRoot = Path.GetFullPath(packageDir) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return Failure("cad_skill_read", "技能路径越出了技能包目录，已拒绝。");

            if (!File.Exists(full))
                return Failure("cad_skill_read", "技能文档不存在：" + relative);

            var info = new FileInfo(full);
            if (info.Length > MaxSkillBytes)
                return Failure("cad_skill_read",
                    "技能文档过大（" + info.Length + " 字节，上限 " + MaxSkillBytes + "），拒绝载入。");

            string content;
            try { content = File.ReadAllText(full, Encoding.UTF8); }
            catch (Exception ex) { return Failure("cad_skill_read", "读取技能文档失败：" + ex.Message); }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["package"] = packageName,
                ["id"] = id,
                ["path"] = relative.Replace('\\', '/'),
                ["bytes"] = info.Length,
                ["content"] = content
            };

            return Ok("cad_skill_read", CadJson.Serialize(data));
        }

        // ================================================================== 工具

        /// <summary>技能包根目录：插件程序集旁的 skills\（AutoCADMcp.dll 与 skills\ 同目录）。</summary>
        private static string SkillsRoot()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(SkillService).Assembly.Location);
                if (string.IsNullOrWhiteSpace(assemblyDir)) return null;
                string root = Path.Combine(assemblyDir, "skills");
                return Directory.Exists(root) ? root : null;
            }
            catch { return null; }
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList(); }
            catch { return new List<string>(); }
        }

        private static Dictionary<string, object> ParseManifest(string path)
        {
            var serializer = CadJson.NewSerializer();
            var parsed = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            return parsed ?? new Dictionary<string, object>();
        }

        private static object BuildPackageView(string root, string manifestPath,
            Dictionary<string, object> manifest, string name, string id)
        {
            // 1) 清单里显式列出的 references（相对包目录）。
            var documents = new List<object>();
            foreach (string reference in Strings(manifest, "references"))
            {
                documents.Add(new Dictionary<string, object>
                {
                    ["id"] = reference.Replace('\\', '/'),
                    ["path"] = reference.Replace('\\', '/'),
                    ["description"] = null
                });
            }

            // 2) 入口文件（SKILL.md）。
            string entry = Str(manifest, "entryPoint");
            if (string.IsNullOrWhiteSpace(entry)) entry = "SKILL.md";
            if (!documents.Any(d => string.Equals(Convert.ToString(((Dictionary<string, object>)d)["path"]),
                    entry.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                documents.Insert(0, new Dictionary<string, object>
                {
                    ["id"] = entry.Replace('\\', '/'),
                    ["path"] = entry.Replace('\\', '/'),
                    ["description"] = Str(manifest, "description")
                });
            }

            // 3) 清单没列全时，把 references\ 下实际存在的 md 也补进来——
            //    否则新加的参考文档会因为忘了登记而永远读不到。
            string packageDir = Path.GetDirectoryName(manifestPath) ?? root;
            var known = new HashSet<string>(
                documents.Select(d => Convert.ToString(((Dictionary<string, object>)d)["path"]) ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (string file in SafeEnumerate(packageDir, "*.md"))
            {
                string relative = RelativeTo(packageDir, file);
                if (known.Contains(relative)) continue;
                known.Add(relative);
                documents.Add(new Dictionary<string, object>
                {
                    ["id"] = relative,
                    ["path"] = relative,
                    ["description"] = "（未在清单中登记，按目录扫描发现）"
                });
            }

            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["id"] = id,
                ["version"] = Str(manifest, "version"),
                ["displayName"] = Str(manifest, "displayName"),
                ["description"] = Str(manifest, "description"),
                ["entryPoint"] = entry.Replace('\\', '/'),
                ["manifest"] = Path.GetFileName(manifestPath),
                ["documents"] = documents
            };
        }

        /// <summary>把 id 解析成包内相对路径；找不到返回 null。</summary>
        private static string ResolveDocumentPath(string packageDir, Dictionary<string, object> manifest, string id)
        {
            string normalized = id.Replace('\\', '/');

            // 1) 清单 references 里精确/文件名匹配
            foreach (string reference in Strings(manifest, "references"))
            {
                string candidate = reference.Replace('\\', '/');
                if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)) return candidate;
                if (string.Equals(Path.GetFileName(candidate), normalized, StringComparison.OrdinalIgnoreCase)) return candidate;
            }

            // 2) 直接把 id 当包内相对路径
            string direct = Path.Combine(packageDir, id);
            if (File.Exists(direct)) return id;

            // 3) 入口文件
            if (string.Equals(normalized, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                string entry = Str(manifest, "entryPoint");
                if (!string.IsNullOrWhiteSpace(entry)) return entry;
            }

            // 4) 退路：按文件名在包内搜一次（限深度，避免在大目录里乱翻）
            foreach (string file in SafeEnumerate(packageDir, "*.md"))
            {
                if (!string.Equals(Path.GetFileName(file), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                return RelativeTo(packageDir, file);
            }

            return null;
        }

        /// <summary>
        /// 取相对路径。net48 没有 Path.GetRelativePath（那是 .NET Standard 2.1+），
        /// 这里只处理"在基准目录之下"这一种情形，取不到就退回文件名。
        /// </summary>
        private static string RelativeTo(string baseDirectory, string fullPath)
        {
            try
            {
                string baseFull = Path.GetFullPath(baseDirectory);
                if (!baseFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    baseFull += Path.DirectorySeparatorChar;

                string full = Path.GetFullPath(fullPath);
                if (full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(baseFull.Length).Replace('\\', '/');

                return Path.GetFileName(full);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        private static string Str(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out object value) || value == null) return null;
            string text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static IEnumerable<string> Strings(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out object value) || value == null) yield break;
            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single)) yield return single;
                yield break;
            }
            if (!(value is System.Collections.IEnumerable sequence)) yield break;

            foreach (object item in sequence)
            {
                string text = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(text)) yield return text;
            }
        }

        private static bool Contains(string haystack, string needle)
            => !string.IsNullOrWhiteSpace(haystack) && !string.IsNullOrWhiteSpace(needle)
               && haystack.IndexOf(needle.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool Equals(string left, string right)
            => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private static McpExecutionResult Ok(string tool, string json)
            => new McpExecutionResult { ToolName = tool, Json = json, MutatesDrawing = false };

        private static McpExecutionResult Failure(string tool, string message)
            => new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
    }
}
