using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 文字样式（TextStyle）管理与 SHX 字体体检。
    ///
    /// 为什么单独成域：AutoCAD 打开外来图纸时最常见的阻塞就是「缺少 SHX 字体」——
    /// 大字体（BigFont）指向的 hztxt.shx / gbcbig.shx 在别的机器上不存在，
    /// AutoCAD 会弹模态框要求手工替换，批量任务直接卡死。这里把「哪些样式缺字、
    /// 各自被多少实体引用」查清楚，并支持按样式一键替换为可用的兜底字体。
    /// </summary>
    internal static class TextStyleService
    {
        /// <summary>常见的兜底 SHX：单线西文字体 + 中文大字体。</summary>
        private const string DefaultFallbackFont = "simplex.shx";
        private const string DefaultFallbackBigFont = "hztxt.shx";

        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_text_style_list", HandleList,
                CadSchema.Function("cad_text_style_list",
                    "List every text style in the drawing with its SHX/TrueType font file, big font, width factor, fixed height and how many entities use it.",
                    CadSchema.Obj()));

            registry.Register("cad_text_style_set", HandleSet,
                CadSchema.Function("cad_text_style_set",
                    "Modify a text style's font file, big font file, width factor or fixed text height.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Text style name to modify.", true),
                        CadSchema.Str("font_file", "New font file (e.g. 'simplex.shx' or 'Arial')."),
                        CadSchema.Str("big_font_file", "New big font file for CJK (e.g. 'hztxt.shx'). Pass empty string to clear."),
                        CadSchema.Num("width_factor", "Width factor, e.g. 0.8."),
                        CadSchema.Num("text_size", "Fixed text height in mm; 0 means not fixed."))));

            registry.Register("cad_text_style_create", HandleCreate,
                CadSchema.Function("cad_text_style_create",
                    "Create a new text style with an optional SHX/TrueType font, big font, width factor and fixed text height.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "New text style name.", true),
                        CadSchema.Str("font_file", "Main font file (e.g. 'simplex.shx' or 'Arial')."),
                        CadSchema.Str("big_font_file", "Big font for CJK (e.g. 'hztxt.shx')."),
                        CadSchema.Num("width_factor", "Width factor, e.g. 0.8."),
                        CadSchema.Num("text_size", "Fixed text height in mm; 0 means not fixed."))));

            registry.Register("cad_text_font_audit", HandleAudit,
                CadSchema.Function("cad_text_font_audit",
                    "Audit the drawing for missing SHX font files (the usual cause of the AutoCAD 'missing SHX font' dialog) and report which text styles and entities are affected.",
                    CadSchema.Obj()));

            registry.Register("cad_text_font_repair", HandleRepair,
                CadSchema.Function("cad_text_font_repair",
                    "Replace missing SHX font files in text styles with known-good fallback fonts so the drawing opens without the missing-font dialog.",
                    CadSchema.Obj(
                        CadSchema.Str("fallback_font", "Replacement SHX for missing main fonts (default 'simplex.shx')."),
                        CadSchema.Str("fallback_big_font", "Replacement SHX for missing big fonts (default 'hztxt.shx')."),
                        CadSchema.Bool("dry_run", "When true (default), only report what would change."))));
        }

        // ------------------------------------------------------------------ 列表

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var usage = CountUsageByStyle(db, tr);

            var list = new List<object>();
            foreach (ObjectId id in table)
            {
                var tsr = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                bool isShx = LooksLikeShx(tsr);
                string resolved = isShx ? ResolveShx(tsr.FileName) : null;
                string resolvedBig = isShx ? ResolveShx(tsr.BigFontFileName) : null;

                var item = new Dictionary<string, object>
                {
                    ["name"] = tsr.Name,
                    ["handle"] = tsr.Handle.ToString(),
                    ["fontFile"] = tsr.FileName,
                    ["bigFontFile"] = tsr.BigFontFileName,
                    ["isShx"] = isShx,
                    ["widthFactor"] = Math.Round(tsr.XScale, 4),
                    ["textSize"] = Math.Round(tsr.TextSize, 4),
                    ["entityCount"] = usage.TryGetValue(tsr.Name, out int n) ? n : 0,
                    ["isCurrent"] = string.Equals(tsr.Name, db.Textstyle.ToString(), StringComparison.OrdinalIgnoreCase)
                };

                if (isShx)
                {
                    item["fontResolved"] = resolved != null;
                    if (resolved != null) item["fontPath"] = resolved;
                    if (!string.IsNullOrWhiteSpace(tsr.BigFontFileName))
                    {
                        item["bigFontResolved"] = resolvedBig != null;
                        if (resolvedBig != null) item["bigFontPath"] = resolvedBig;
                    }
                }

                list.Add(item);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["total"] = list.Count,
                ["currentStyle"] = db.Textstyle.ToString(),
                ["supportPaths"] = SupportPaths(),
                ["styles"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_text_style_list",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        // ------------------------------------------------------------------ 修改

        private static McpExecutionResult HandleSet(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Fail("cad_text_style_set", "name is required.");

            var table = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
            if (!table.Has(name)) return Fail("cad_text_style_set", "文字样式不存在: " + name);

            var tsr = (TextStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);

            var changed = new Dictionary<string, object>();
            bool hasFont = args.ContainsKey("font_file");
            bool hasBigFont = args.ContainsKey("big_font_file");

            if (hasFont)
            {
                string font = CadJson.GetString(args, "font_file");
                if (!string.IsNullOrWhiteSpace(font)) { tsr.FileName = font.Trim(); changed["fontFile"] = font.Trim(); }
            }
            if (hasBigFont)
            {
                string big = CadJson.GetString(args, "big_font_file");
                tsr.BigFontFileName = (big ?? string.Empty).Trim();
                changed["bigFontFile"] = tsr.BigFontFileName;
            }
            if (args.ContainsKey("width_factor"))
            {
                double w = CadJson.GetDouble(args, "width_factor", tsr.XScale);
                if (w > 0) { tsr.XScale = w; changed["widthFactor"] = Math.Round(w, 4); }
            }
            if (args.ContainsKey("text_size"))
            {
                double h = CadJson.GetDouble(args, "text_size", tsr.TextSize);
                if (h >= 0) { tsr.TextSize = h; changed["textSize"] = Math.Round(h, 4); }
            }

            // 样式表记录被改动后需要通知数据库重生成，否则图面仍是旧字形。
            TryRegen(doc);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["name"] = tsr.Name,
                ["changed"] = changed
            };

            return new McpExecutionResult
            {
                ToolName = "cad_text_style_set",
                Json = CadJson.Serialize(data),
                MutatesDrawing = true
            };
        }

        // ------------------------------------------------------------------ 字体体检

        private static McpExecutionResult HandleAudit(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            var problems = CollectMissingFonts(db, tr);

            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var ttfStyles = new List<object>();
            foreach (ObjectId id in table)
            {
                var tsr = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (LooksLikeShx(tsr)) continue;
                ttfStyles.Add(new Dictionary<string, object>
                {
                    ["name"] = tsr.Name,
                    ["fontFile"] = tsr.FileName,
                    ["note"] = "TrueType 字体按字体族名引用，无法仅凭文件名判定缺失，未做校验。"
                });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["hasProblems"] = problems.Count > 0,
                ["problemCount"] = problems.Count,
                ["problems"] = problems,
                ["trueTypeStyles"] = ttfStyles,
                ["supportPaths"] = SupportPaths(),
                ["suggestedFallbackFont"] = DefaultFallbackFont,
                ["suggestedFallbackBigFont"] = DefaultFallbackBigFont
            };

            return new McpExecutionResult
            {
                ToolName = "cad_text_font_audit",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        /// <summary>
        /// 收集「引用了 SHX 字体但本机找不到文件」的文字样式。
        /// 供字体体检工具与图纸健康体检共用，避免两处判定口径漂移。
        /// </summary>
        internal static List<Dictionary<string, object>> CollectMissingFonts(Database db, Transaction tr)
        {
            var problems = new List<Dictionary<string, object>>();
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var usage = CountUsageByStyle(db, tr);

            foreach (ObjectId id in table)
            {
                var tsr = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!LooksLikeShx(tsr)) continue;

                bool mainMissing = !string.IsNullOrWhiteSpace(tsr.FileName) && ResolveShx(tsr.FileName) == null;
                bool bigMissing = !string.IsNullOrWhiteSpace(tsr.BigFontFileName) && ResolveShx(tsr.BigFontFileName) == null;
                if (!mainMissing && !bigMissing) continue;

                problems.Add(new Dictionary<string, object>
                {
                    ["style"] = tsr.Name,
                    ["handle"] = tsr.Handle.ToString(),
                    ["entityCount"] = usage.TryGetValue(tsr.Name, out int n) ? n : 0,
                    ["missingMainFont"] = mainMissing ? tsr.FileName : null,
                    ["missingBigFont"] = bigMissing ? tsr.BigFontFileName : null
                });
            }

            return problems;
        }

        private static McpExecutionResult HandleRepair(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string fallbackFont = CadJson.GetString(args, "fallback_font", DefaultFallbackFont);
            string fallbackBig = CadJson.GetString(args, "fallback_big_font", DefaultFallbackBigFont);
            bool dryRun = CadJson.GetBool(args, "dry_run", true);

            if (string.IsNullOrWhiteSpace(fallbackFont)) fallbackFont = DefaultFallbackFont;
            if (string.IsNullOrWhiteSpace(fallbackBig)) fallbackBig = DefaultFallbackBigFont;

            // 兜底字体自己也找不到就没意义了：先把话说清楚，别把图纸改坏。
            bool fallbackOk = ResolveShx(fallbackFont) != null;
            bool fallbackBigOk = ResolveShx(fallbackBig) != null;
            if (!fallbackOk && !fallbackBigOk)
            {
                return Fail("cad_text_font_repair",
                    "兜底字体在本机也找不到（" + fallbackFont + " / " + fallbackBig + "），未做任何修改。");
            }

            var db = doc.Database;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

            var changes = new List<object>();
            foreach (ObjectId id in table)
            {
                var tsr = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!LooksLikeShx(tsr)) continue;

                bool mainMissing = !string.IsNullOrWhiteSpace(tsr.FileName) && ResolveShx(tsr.FileName) == null;
                bool bigMissing = !string.IsNullOrWhiteSpace(tsr.BigFontFileName) && ResolveShx(tsr.BigFontFileName) == null;
                if (!mainMissing && !bigMissing) continue;

                var entry = new Dictionary<string, object>
                {
                    ["style"] = tsr.Name,
                    ["oldFontFile"] = mainMissing ? tsr.FileName : null,
                    ["oldBigFontFile"] = bigMissing ? tsr.BigFontFileName : null
                };

                if (!dryRun)
                {
                    var writable = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    if (mainMissing && fallbackOk)
                    {
                        writable.FileName = fallbackFont;
                        entry["newFontFile"] = fallbackFont;
                    }
                    if (bigMissing && fallbackBigOk)
                    {
                        writable.BigFontFileName = fallbackBig;
                        entry["newBigFontFile"] = fallbackBig;
                    }
                }
                else
                {
                    if (mainMissing && fallbackOk) entry["wouldSetFontFile"] = fallbackFont;
                    if (bigMissing && fallbackBigOk) entry["wouldSetBigFontFile"] = fallbackBig;
                }

                changes.Add(entry);
            }

            if (!dryRun && changes.Count > 0)
                TryRegen(doc);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["dryRun"] = dryRun,
                ["repairedCount"] = dryRun ? 0 : changes.Count,
                ["affectedCount"] = changes.Count,
                ["changes"] = changes
            };

            return new McpExecutionResult
            {
                ToolName = "cad_text_font_repair",
                Json = CadJson.Serialize(data),
                MutatesDrawing = !dryRun && changes.Count > 0
            };
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>该样式用的是 SHX（形）字体还是 TrueType 字体。</summary>
        private static bool LooksLikeShx(TextStyleTableRecord tsr)
        {
            try
            {
                if (tsr.IsShapeFile) return true;
                string file = tsr.FileName ?? string.Empty;
                return file.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在 AutoCAD 支持路径（ACADPREFIX）与当前图纸目录下查找 SHX 文件。
        /// 找到返回完整路径，找不到返回 null。空文件名视为「没设」，返回 null 但不代表缺失。
        /// </summary>
        private static string ResolveShx(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            string name = fileName.Trim();
            var candidates = new List<string> { name };
            if (!name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                candidates.Add(name + ".shx");

            // 绝对路径直接判断
            if (Path.IsPathRooted(name))
            {
                foreach (var candidate in candidates)
                {
                    try { if (File.Exists(candidate)) return candidate; } catch { }
                }
                return null;
            }

            var roots = SupportPaths();
            foreach (var root in roots)
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        string full = Path.Combine(root, candidate);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static readonly List<string> _supportPathCache = new List<string>();
        private static bool _supportPathLoaded;

        /// <summary>AutoCAD 支持文件搜索路径（ACADPREFIX 是分号分隔的列表）。</summary>
        private static List<string> SupportPaths()
        {
            if (_supportPathLoaded) return _supportPathCache;
            _supportPathLoaded = true;

            try
            {
                var raw = Convert.ToString(Application.GetSystemVariable("ACADPREFIX")) ?? string.Empty;
                foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string path = part.Trim();
                    if (path.Length == 0) continue;
                    try
                    {
                        path = Path.GetFullPath(path);
                        if (Directory.Exists(path)) _supportPathCache.Add(path);
                    }
                    catch { }
                }
            }
            catch { }

            return _supportPathCache;
        }

        /// <summary>统计每个文字样式被多少实体引用（跨模型空间与全部图纸空间，含块定义内实体）。</summary>
        private static Dictionary<string, int> CountUsageByStyle(Database db, Transaction tr)
        {
            var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<ObjectId, string>();
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                try { names[id] = ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name; }
                catch { }
            }

            void Bump(ObjectId styleId)
            {
                if (styleId.IsNull) return;
                if (!names.TryGetValue(styleId, out string styleName)) return;
                usage[styleName] = usage.TryGetValue(styleName, out int n) ? n + 1 : 1;
            }

            // 标注样式也持有文字样式引用：先把「标注样式 → 文字样式」映射建好。
            var dimStyleTextStyle = new Dictionary<ObjectId, ObjectId>();
            var dimTable = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in dimTable)
            {
                try
                {
                    var dsr = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    dimStyleTextStyle[id] = dsr.Dimtxsty;
                }
                catch { }
            }

            // 布局空间里的实体
            CadSpaces.ForEachEntity(db, tr, (space, ent) =>
            {
                if (ent is DBText dt) Bump(dt.TextStyleId);
                else if (ent is MText mt) Bump(mt.TextStyleId);
                else if (ent is AttributeReference ar) Bump(ar.TextStyleId);
                else if (ent is Dimension dim)
                {
                    if (dimStyleTextStyle.TryGetValue(dim.DimensionStyle, out ObjectId textStyleId))
                        Bump(textStyleId);
                }
            });

            // 块定义内的属性定义（标题栏 / 材料表模板）
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in blockTable)
            {
                BlockTableRecord btr;
                try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
                catch { continue; }
                foreach (ObjectId entId in btr)
                {
                    try
                    {
                        if (tr.GetObject(entId, OpenMode.ForRead) is AttributeDefinition ad) Bump(ad.TextStyleId);
                    }
                    catch { }
                }
            }

            return usage;
        }

        private static McpExecutionResult Fail(string tool, string message)
        {
            return new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + message.Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
        }

        /// <summary>重生成视图以刷新字形。失败不影响工具结果（下次视图变化自然会刷新）。</summary>
        private static void TryRegen(Document doc)
        {
            try { doc.Editor.Regen(); }
            catch { }
        }

        private static McpExecutionResult HandleCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Fail("cad_text_style_create", "name is required.");

            var db = doc.Database;
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (table.Has(name)) return Fail("cad_text_style_create", "文字样式已存在: " + name);

            var record = new TextStyleTableRecord { Name = name };
            string font = CadJson.GetString(args, "font_file");
            if (!string.IsNullOrWhiteSpace(font)) record.FileName = font;
            string bigFont = CadJson.GetString(args, "big_font_file");
            if (!string.IsNullOrWhiteSpace(bigFont)) record.BigFontFileName = bigFont;
            if (args.ContainsKey("width_factor")) record.XScale = Math.Max(0.01, CadJson.GetDouble(args, "width_factor", 1.0));
            if (args.ContainsKey("text_size")) record.TextSize = Math.Max(0.0, CadJson.GetDouble(args, "text_size", 0.0));

            table.UpgradeOpen();
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);

            var data = new Dictionary<string, object> { ["ok"] = true, ["name"] = name, ["created"] = true };
            return new McpExecutionResult { ToolName = "cad_text_style_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

    }
}
