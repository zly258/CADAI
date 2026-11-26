using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 批处理服务：**不打开文档**、用侧数据库（Database.ReadDwgFile）直接读写 DWG 文件。
    ///
    /// 为什么走侧数据库而不是逐个打开文档：
    ///   · 打开一张图要切换活动文档 + 恢复视图 + 触发一堆事件，几十张图的批量光切换就占掉大半时间；
    ///   · 侧数据库只是"把 DWG 当数据库文件读进来"，没有 UI、没有事件、不抢主线程，
    ///     快一个数量级，也不打扰用户手头的图（快速模式）。
    ///     DwgTranslate 的批量翻译就是这条路（见 Engine\DwgTranslate.cs:1157），这里把它
    ///     泛化成 AI 可调的通用批处理工具。
    ///
    /// 写操作的安全约定：
    ///   · 默认 dryRun=true 只读不改；改图必须显式传 dryRun=false。
    ///   · 改图前自动做 .bak 备份（backup=true 默认开），失败可回退。
    ///   · 用原文件的 DWG 版本写回，不悄悄升版。
    ///   · 所有工具都是后台工具（RegisterBackground），慢活不压 UI 线程。
    /// </summary>
    internal static class BatchService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.RegisterBackground("cad_batch_read_texts", HandleBatchReadTexts,
                CadSchema.Function("cad_batch_read_texts",
                    "FAST MODE: read all texts (DBText/MText, optional block attributes) from MULTIPLE DWG files WITHOUT opening them. " +
                    "Files are read directly from disk via side databases - AutoCAD stays responsive. Read-only.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files to read.", "string", true),
                        CadSchema.Bool("include_attributes", "Also read block reference attributes (default true)."),
                        CadSchema.Str("layer", "Optional layer filter (exact match)."),
                        CadSchema.Int("limit_per_file", "Max texts returned per file, default 200, cap 1000."))));

            registry.RegisterBackground("cad_batch_replace_texts", HandleBatchReplaceTexts,
                CadSchema.Function("cad_batch_replace_texts",
                    "FAST MODE: find-and-replace text across MULTIPLE DWG files WITHOUT opening them. " +
                    "Matches DBText/MText contents (case-insensitive by default) and rewrites the files. " +
                    "A .bak backup is created next to each modified file unless backup=false. Pass dryRun=false to actually write; default is a dry run that only reports what WOULD change.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files to process.", "string", true),
                        CadSchema.Str("find", "Text to find.", true),
                        CadSchema.Str("replace", "Replacement text (empty string = delete the found text).", true),
                        CadSchema.Bool("case_sensitive", "Default false."),
                        CadSchema.Bool("dryRun", "true (default) = report matches without writing. Pass false to apply."),
                        CadSchema.Bool("backup", "Create .bak next to each modified file (default true)."))));

            registry.RegisterBackground("cad_batch_set_layer_states", HandleBatchLayerStates,
                CadSchema.Function("cad_batch_set_layer_states",
                    "FAST MODE: set layer on/off/freeze/lock/color across MULTIPLE DWG files WITHOUT opening them. " +
                    "Only layers that already exist in each file are touched (missing layers are reported). " +
                    "A .bak backup is created unless backup=false. Pass dryRun=false to actually write.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files to process.", "string", true),
                        CadSchema.Arr("layers", "Layer names to update.", "string", true),
                        CadSchema.Bool("off", "Turn layer off."),
                        CadSchema.Bool("freeze", "Freeze layer."),
                        CadSchema.Bool("lock", "Lock layer."),
                        CadSchema.Str("color", "Optional ACI color name or number, e.g. red / 1 / ByLayer."),
                        CadSchema.Bool("dryRun", "true (default) = report without writing. Pass false to apply."),
                        CadSchema.Bool("backup", "Create .bak backups (default true)."))));

            registry.RegisterBackground("cad_batch_summary", HandleBatchSummary,
                CadSchema.Function("cad_batch_summary",
                    "FAST MODE: collect a summary of MULTIPLE DWG files WITHOUT opening them: entity counts by type, layer count, text count, extents. Read-only.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files.", "string", true),
                        CadSchema.Int("limit_per_file", "Max distinct entity types listed per file, default 20."))));

            registry.RegisterBackground("cad_batch_export_dxf", HandleBatchExportDxf,
                CadSchema.Function("cad_batch_export_dxf",
                    "FAST MODE: export MULTIPLE DWG files to DXF (same base name, .dxf extension) WITHOUT opening them. Existing output files are overwritten.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files to export.", "string", true),
                        CadSchema.Str("output_dir", "Optional output directory. Default: same folder as each DWG."))));
        }

        // ==================================================================== 处理器

        /// <summary>批量读文本：侧数据库只读遍历模型空间。</summary>
        private static McpExecutionResult HandleBatchReadTexts(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            if (files.Count == 0)
                return Fail("cad_batch_read_texts", "file_paths array is required.");
            bool includeAttributes = CadJson.GetBool(args, "include_attributes", true);
            string layer = CadJson.GetString(args, "layer");
            int limit = Math.Max(1, Math.Min(1000, CadJson.GetInt(args, "limit_per_file", 200)));

            var results = new List<object>();
            ForEachSideDatabase(files, "cad_batch_read_texts", results, (filePath, db, errors) =>
            {
                var texts = new List<object>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ReadTextsFrom(tr, (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead),
                        includeAttributes, layer, limit, texts, true);
                    // 布局空间也扫一遍（图签说明常在布局里），但只补空缺名额
                    ObjectId modelId = bt[BlockTableRecord.ModelSpace];
                    foreach (ObjectId layoutId in bt)
                    {
                        if (texts.Count >= limit) break;
                        if (layoutId == modelId) continue;
                        var btr = tr.GetObject(layoutId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr == null || !btr.IsLayout) continue;
                        ReadTextsFrom(tr, btr, includeAttributes, layer, limit - texts.Count, texts, false);
                    }
                    tr.Commit();
                }
                return new Dictionary<string, object> { ["textCount"] = texts.Count, ["texts"] = texts };
            });

            return Ok("cad_batch_read_texts", results);
        }

        private static void ReadTextsFrom(Transaction tr, BlockTableRecord btr, bool includeAttributes,
                                          string layer, int limit, List<object> texts, bool isModel)
        {
            foreach (ObjectId id in btr)
            {
                if (texts.Count >= limit) return;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                if (ent is DBText dt)
                    texts.Add(new Dictionary<string, object> { ["handle"] = ent.Handle.ToString(), ["type"] = "DBText", ["space"] = isModel ? "model" : "layout", ["text"] = dt.TextString, ["layer"] = ent.Layer });
                else if (ent is MText mt)
                    texts.Add(new Dictionary<string, object> { ["handle"] = ent.Handle.ToString(), ["type"] = "MText", ["space"] = isModel ? "model" : "layout", ["text"] = mt.Text, ["layer"] = ent.Layer });
                else if (includeAttributes && ent is BlockReference br)
                {
                    var atts = new List<object>();
                    foreach (ObjectId attId in br.AttributeCollection)
                        if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att)
                            atts.Add(new Dictionary<string, object> { ["tag"] = att.Tag, ["text"] = att.TextString });
                    if (atts.Count > 0)
                        texts.Add(new Dictionary<string, object> { ["handle"] = ent.Handle.ToString(), ["type"] = "BlockReference", ["space"] = isModel ? "model" : "layout", ["blockName"] = SafeName(br), ["attributes"] = atts });
                }
            }
        }

        /// <summary>批量替换文本：侧数据库改 DBText/MText 内容，dryRun 控制是否写回。</summary>
        private static McpExecutionResult HandleBatchReplaceTexts(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            if (files.Count == 0)
                return Fail("cad_batch_replace_texts", "file_paths array is required.");
            string find = CadJson.GetString(args, "find");
            string replace = CadJson.GetString(args, "replace");
            if (find.Length == 0)
                return Fail("cad_batch_replace_texts", "find is required (use empty replace to delete).");
            bool caseSensitive = CadJson.GetBool(args, "case_sensitive", false);
            bool dryRun = CadJson.GetBool(args, "dryRun", true);
            bool backup = CadJson.GetBool(args, "backup", true);

            StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var results = new List<object>();
            ForEachSideDatabase(files, "cad_batch_replace_texts", results, (filePath, db, errors) =>
            {
                int replaced = 0;
                var samples = new List<object>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForWrite) is DBText dt && dt.TextString.IndexOf(find, cmp) >= 0)
                        {
                            if (samples.Count < 5) samples.Add(new Dictionary<string, object> { ["handle"] = dt.Handle.ToString(), ["from"] = dt.TextString, ["to"] = dt.TextString.Replace(find, replace) });
                            dt.TextString = dt.TextString.Replace(find, replace);
                            replaced++;
                        }
                        else if (tr.GetObject(id, OpenMode.ForWrite) is MText mt && mt.Contents.IndexOf(find, cmp) >= 0)
                        {
                            if (samples.Count < 5) samples.Add(new Dictionary<string, object> { ["handle"] = mt.Handle.ToString(), ["from"] = mt.Contents, ["to"] = mt.Contents.Replace(find, replace) });
                            mt.Contents = mt.Contents.Replace(find, replace);
                            replaced++;
                        }
                    }
                    tr.Commit();
                }

                var item = new Dictionary<string, object> { ["replacedCount"] = replaced, ["samples"] = samples };
                if (replaced > 0 && !dryRun)
                {
                    if (backup) item["backupFile"] = Backup(filePath);
                    SaveSideDatabase(db, filePath);
                    item["saved"] = true;
                }
                item["dryRun"] = dryRun;
                return item;
            });

            return Ok("cad_batch_replace_texts", results);
        }

        /// <summary>批量改图层状态：侧数据库改 LayerTableRecord。</summary>
        private static McpExecutionResult HandleBatchLayerStates(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            var layers = GetStringList(args, "layers");
            if (files.Count == 0 || layers.Count == 0)
                return Fail("cad_batch_set_layer_states", "file_paths and layers arrays are required.");

            bool? off = args != null && args.ContainsKey("off") ? CadJson.GetBool(args, "off") : (bool?)null;
            bool? freeze = args != null && args.ContainsKey("freeze") ? CadJson.GetBool(args, "freeze") : (bool?)null;
            bool? lockState = args != null && args.ContainsKey("lock") ? CadJson.GetBool(args, "lock") : (bool?)null;
            string color = CadJson.GetString(args, "color");
            bool dryRun = CadJson.GetBool(args, "dryRun", true);
            bool backup = CadJson.GetBool(args, "backup", true);

            var results = new List<object>();
            ForEachSideDatabase(files, "cad_batch_set_layer_states", results, (filePath, db, errors) =>
            {
                int updated = 0;
                var missing = new List<string>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (string name in layers)
                    {
                        if (!lt.Has(name)) { missing.Add(name); continue; }
                        var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                        if (off.HasValue) ltr.IsOff = off.Value;
                        if (freeze.HasValue) ltr.IsFrozen = freeze.Value;
                        if (lockState.HasValue) ltr.IsLocked = lockState.Value;
                        if (color.Length > 0)
                        {
                            try { ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, ParseAci(color)); }
                            catch { /* 颜色名解析不了就跳过该属性，不影响其它字段 */ }
                        }
                        updated++;
                    }
                    tr.Commit();
                }

                var item = new Dictionary<string, object> { ["updatedLayers"] = updated, ["missingLayers"] = missing };
                if (updated > 0 && !dryRun)
                {
                    if (backup) item["backupFile"] = Backup(filePath);
                    SaveSideDatabase(db, filePath);
                    item["saved"] = true;
                }
                item["dryRun"] = dryRun;
                return item;
            });

            return Ok("cad_batch_set_layer_states", results);
        }

        /// <summary>批量摘要：实体类型计数 / 图层数 / 文本数 / 图形范围。</summary>
        private static McpExecutionResult HandleBatchSummary(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            if (files.Count == 0)
                return Fail("cad_batch_summary", "file_paths array is required.");
            int limit = Math.Max(1, Math.Min(50, CadJson.GetInt(args, "limit_per_file", 20)));

            var results = new List<object>();
            ForEachSideDatabase(files, "cad_batch_summary", results, (filePath, db, errors) =>
            {
                var byType = new Dictionary<string, int>();
                var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int textCount = 0;
                Extents3d? ext = null;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                        string t = ent.GetType().Name;
                        byType[t] = (byType.TryGetValue(t, out int n) ? n : 0) + 1;
                        layers.Add(ent.Layer);
                        if (ent is DBText || ent is MText) textCount++;
                        try
                        {
                            var e = ent.GeometricExtents;
                            if (ext == null) ext = e;
                            else
                            {
                                var mn = ext.Value.MinPoint;
                                var mx = ext.Value.MaxPoint;
                                mn = new Point3d(Math.Min(mn.X, e.MinPoint.X), Math.Min(mn.Y, e.MinPoint.Y), Math.Min(mn.Z, e.MinPoint.Z));
                                mx = new Point3d(Math.Max(mx.X, e.MaxPoint.X), Math.Max(mx.Y, e.MaxPoint.Y), Math.Max(mx.Z, e.MaxPoint.Z));
                                ext = new Extents3d(mn, mx);
                            }
                        }
                        catch { }
                    }
                    tr.Commit();
                }

                var types = new List<object>();
                int i = 0;
                foreach (var pair in byType)
                {
                    if (types.Count >= limit) break;
                    types.Add(new Dictionary<string, object> { ["type"] = pair.Key, ["count"] = pair.Value });
                    i++;
                }

                var item = new Dictionary<string, object>
                {
                    ["entityTypes"] = types,
                    ["layerCount"] = layers.Count,
                    ["textCount"] = textCount
                };
                if (ext.HasValue)
                    item["extents"] = new[]
                    {
                        Math.Round(ext.Value.MinPoint.X, 1), Math.Round(ext.Value.MinPoint.Y, 1),
                        Math.Round(ext.Value.MaxPoint.X, 1), Math.Round(ext.Value.MaxPoint.Y, 1)
                    };
                return item;
            });

            return Ok("cad_batch_summary", results);
        }

        /// <summary>批量导出 DXF：侧数据库 DxfOut，不打开文档。</summary>
        private static McpExecutionResult HandleBatchExportDxf(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            if (files.Count == 0)
                return Fail("cad_batch_export_dxf", "file_paths array is required.");
            string outputDir = CadJson.GetString(args, "output_dir");

            var results = new List<object>();
            foreach (string raw in files)
            {
                string filePath = Environment.ExpandEnvironmentVariables(raw ?? string.Empty).Trim();
                var item = new Dictionary<string, object> { ["file"] = filePath };
                try
                {
                    if (!File.Exists(filePath)) { item["ok"] = false; item["error"] = "文件不存在"; results.Add(item); continue; }

                    string targetDir = outputDir.Length > 0 ? outputDir : Path.GetDirectoryName(filePath);
                    string dxfPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(filePath) + ".dxf");
                    Directory.CreateDirectory(targetDir);

                    using (var db = new Database(false, true))
                    {
                        db.ReadDwgFile(filePath, FileShare.Read, true, null);
                        db.CloseInput(true);
                        db.DxfOut(dxfPath, 0, true);
                    }
                    item["ok"] = true; item["dxf"] = dxfPath;
                }
                catch (Exception ex) { item["ok"] = false; item["error"] = ex.Message; }
                results.Add(item);
            }

            return Ok("cad_batch_export_dxf", results);
        }

        // ==================================================================== 侧数据库骨架

        /// <summary>
        /// 对每个文件：开侧数据库 → 执行 → 汇总。单个文件失败只记错误不中断整批。
        /// 每个文件的结果是 { file, ok, ...item } 或 { file, ok:false, error }。
        /// </summary>
        private static void ForEachSideDatabase(IList<string> files, string toolName, List<object> results,
                                                Func<string, Database, List<string>, Dictionary<string, object>> work)
        {
            foreach (string raw in files)
            {
                string filePath = Environment.ExpandEnvironmentVariables(raw ?? string.Empty).Trim();
                var item = new Dictionary<string, object> { ["file"] = filePath };
                var errors = new List<string>();

                if (!File.Exists(filePath))
                {
                    item["ok"] = false; item["error"] = "文件不存在";
                    results.Add(item); continue;
                }

                Database db = null;
                try
                {
                    // copy=false：不开新内存库；这些工具要么只读、要么显式写回
                    db = new Database(false, true);
                    // FileShare.ReadWrite：允许读"别人开着"的图（快速模式不独占文件）
                    db.ReadDwgFile(filePath, FileShare.ReadWrite, false, null);

                    var payload = work(filePath, db, errors);
                    item["ok"] = true;
                    foreach (var kv in payload) item[kv.Key] = kv.Value;
                    foreach (string e in errors) item["warning"] = e;
                }
                catch (Exception ex)
                {
                    item["ok"] = false;
                    item["error"] = ex.Message;
                }
                finally
                {
                    try { db?.Dispose(); } catch { }
                    db = null;
                }
                results.Add(item);

                // 每处理 20 个文件显式触发一次代际回收，释放非托管句柄与侧数据库游标
                if (results.Count % 20 == 0)
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Optimized);
                        GC.WaitForPendingFinalizers();
                    }
                    catch { }
                }
            }
        }

        /// <summary>把侧数据库写回原文件，保留原 DWG 版本。</summary>
        private static void SaveSideDatabase(Database db, string filePath)
        {
            // 用原文件的 DWG 版本写回，避免批量处理顺手把图纸升版
            db.SaveAs(filePath, db.OriginalFileVersion);
        }

        /// <summary>改图前备份：同名 .bak，已存在则覆盖（本次批量的一致性比历史备份更重要）。</summary>
        private static string Backup(string filePath)
        {
            string bak = filePath + ".bak";
            File.Copy(filePath, bak, true);
            return bak;
        }

        private static string SafeName(BlockReference br)
        {
            try { return br.Name ?? string.Empty; } catch { return string.Empty; }
        }

        /// <summary>颜色参数："red"/"1" 都收（ACI 名单只列最常用九个，其余按数字解析）。</summary>
        private static short ParseAci(string color)
        {
            switch (color.Trim().ToLowerInvariant())
            {
                case "red": return 1;
                case "yellow": return 2;
                case "green": return 3;
                case "cyan": return 4;
                case "blue": return 5;
                case "magenta": return 6;
                case "white": return 7;
                case "bylayer": return 256;
                case "byblock": return 0;
                default: return short.Parse(color.Trim());
            }
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var list = new List<string>();
            if (args != null && args.TryGetValue(key, out var raw) && raw is System.Collections.IEnumerable seq && !(raw is string))
            {
                foreach (var item in seq)
                {
                    string s = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }

        private static McpExecutionResult Ok(string toolName, List<object> results)
        {
            var data = new Dictionary<string, object> { ["ok"] = true, ["fileCount"] = results.Count, ["results"] = results };
            return new McpExecutionResult { ToolName = toolName, Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult Fail(string toolName, string error)
        {
            return new McpExecutionResult { ToolName = toolName, Json = "{\"ok\":false,\"error\":\"" + error + "\"}", MutatesDrawing = false };
        }
    }
}
