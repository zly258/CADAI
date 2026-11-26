using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 图纸综合统计与健康体检。
    ///
    /// 为什么需要：让模型「先看清全貌再动手」的成本必须远低于它自己逐个工具试错。
    /// 一次统计调用就能回答「这图有多少张布局、多少实体、都压在哪些图层上、文字用了哪些样式」；
    /// 一次体检就能回答「哪里会出问题」（空文字、零长度线、重复文字、缺字体、空图层、外部参照异常）。
    /// </summary>
    internal static class StatisticsService
    {
        private const int TopLayerLimit = 30;
        private const int SampleHandleLimit = 20;
        private const int DuplicateScanLimit = 20000;

        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_drawing_statistics", HandleStatistics,
                CadSchema.Function("cad_drawing_statistics",
                    "Produce a whole-drawing summary: layout list, entity totals by type, per-layer entity counts, text counts by style, block reference usage and external reference status.",
                    CadSchema.Obj()));

            registry.Register("cad_drawing_health_check", HandleHealthCheck,
                CadSchema.Function("cad_drawing_health_check",
                    "Run a non-destructive drawing health check and report issues: empty text, zero-length geometry, duplicated text, missing SHX fonts, unused layers, unreferenced blocks and broken external references.",
                    CadSchema.Obj(
                        CadSchema.Bool("include_duplicates", "Scan for duplicated text at identical positions (default true; skipped for very large drawings)."))));
        }

        // ================================================================== 综合统计

        private static McpExecutionResult HandleStatistics(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;

            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var blockRefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var textByStyle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int modelCount = 0, paperCount = 0;
            int dbTextCount = 0, mTextCount = 0, dimCount = 0, attrRefCount = 0;

            // 样式 id → 名称，供文字统计用
            var styleNames = new Dictionary<ObjectId, string>();
            var styleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in styleTable)
            {
                try { styleNames[id] = ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name; }
                catch { }
            }

            CadSpaces.ForEachEntity(db, tr, (space, ent) =>
            {
                if (space.IsModel) modelCount++; else paperCount++;

                string typeName = ent.GetType().Name;
                byType[typeName] = byType.TryGetValue(typeName, out int t) ? t + 1 : 1;

                string layer = string.IsNullOrWhiteSpace(ent.Layer) ? "(none)" : ent.Layer;
                byLayer[layer] = byLayer.TryGetValue(layer, out int l) ? l + 1 : 1;

                if (ent is DBText dt)
                {
                    dbTextCount++;
                    BumpText(styleNames, textByStyle, dt.TextStyleId);
                }
                else if (ent is MText mt)
                {
                    mTextCount++;
                    BumpText(styleNames, textByStyle, mt.TextStyleId);
                }
                else if (ent is Dimension) dimCount++;
                else if (ent is AttributeReference ar)
                {
                    attrRefCount++;
                    BumpText(styleNames, textByStyle, ar.TextStyleId);
                }
                else if (ent is BlockReference br)
                {
                    string blockName = SafeBlockName(tr, br);
                    if (blockName != null)
                        blockRefs[blockName] = blockRefs.TryGetValue(blockName, out int b) ? b + 1 : 1;
                }
            });

            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var layerList = new List<object>();
            foreach (ObjectId id in layers)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                layerList.Add(new Dictionary<string, object>
                {
                    ["name"] = ltr.Name,
                    ["isOff"] = ltr.IsOff,
                    ["isFrozen"] = ltr.IsFrozen,
                    ["isLocked"] = ltr.IsLocked,
                    ["isPlottable"] = ltr.IsPlottable,
                    ["entityCount"] = byLayer.TryGetValue(ltr.Name, out int n) ? n : 0
                });
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            int blockDefinitionCount = 0;
            foreach (ObjectId id in blockTable)
            {
                try
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout) continue;
                    blockDefinitionCount++;
                }
                catch { }
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["fileName"] = Path.GetFileName(doc.Name),
                ["currentLayout"] = CadSpaces.CurrentLayoutName(doc),
                ["tileMode"] = db.TileMode,
                ["units"] = db.Insunits.ToString(),
                ["layoutCount"] = CadSpaces.Enumerate(db, tr).Count,
                ["entityTotals"] = new Dictionary<string, object>
                {
                    ["modelSpace"] = modelCount,
                    ["paperSpace"] = paperCount,
                    ["all"] = modelCount + paperCount
                },
                ["byType"] = SortDescending(byType),
                ["textSummary"] = new Dictionary<string, object>
                {
                    ["dbText"] = dbTextCount,
                    ["mText"] = mTextCount,
                    ["dimensions"] = dimCount,
                    ["blockAttributes"] = attrRefCount,
                    ["total"] = dbTextCount + mTextCount + dimCount + attrRefCount,
                    ["byTextStyle"] = SortDescending(textByStyle)
                },
                ["layerCount"] = layerList.Count,
                ["layerUsage"] = SortDescending(byLayer).Take(TopLayerLimit).ToList(),
                ["layers"] = layerList,
                ["blockDefinitionCount"] = blockDefinitionCount,
                ["blockReferences"] = SortDescending(blockRefs).Take(TopLayerLimit).ToList(),
                ["externalReferences"] = CollectXrefs(db, tr)
            };

            return new McpExecutionResult
            {
                ToolName = "cad_drawing_statistics",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        // ================================================================== 健康体检

        private static McpExecutionResult HandleHealthCheck(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            bool includeDuplicates = CadJson.GetBool(args, "include_duplicates", true);

            var db = doc.Database;
            var issues = new List<object>();

            int emptyTextCount = 0;
            int zeroGeometryCount = 0;
            int totalEntities = 0;
            var emptyTextSamples = new List<string>();
            var zeroGeomSamples = new List<string>();
            var usedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int duplicateCount = 0;
            var duplicateSamples = new List<object>();
            bool duplicateScanSkipped = false;

            CadSpaces.ForEachEntity(db, tr, (space, ent) =>
            {
                totalEntities++;
                usedLayers.Add(ent.Layer ?? string.Empty);

                // 1) 空文字 / 纯空白文字
                string text = null;
                if (ent is DBText dt) text = dt.TextString;
                else if (ent is MText mt) text = mt.Text;
                else if (ent is AttributeReference ar) text = ar.TextString;

                if (text != null && string.IsNullOrWhiteSpace(text))
                {
                    emptyTextCount++;
                    if (emptyTextSamples.Count < SampleHandleLimit)
                        emptyTextSamples.Add(space.Name + ":" + ent.Handle);
                }

                // 2) 零几何实体（零长度线、零半径圆、单点多段线）
                bool zeroGeometry = false;
                if (ent is Line line) zeroGeometry = line.Length < 1e-6;
                else if (ent is Circle circle) zeroGeometry = circle.Radius < 1e-6;
                else if (ent is Arc arc) zeroGeometry = arc.Radius < 1e-6 || Math.Abs(arc.Length) < 1e-6;
                else if (ent is Polyline pl) zeroGeometry = pl.NumberOfVertices == 0;

                if (zeroGeometry)
                {
                    zeroGeometryCount++;
                    if (zeroGeomSamples.Count < SampleHandleLimit)
                        zeroGeomSamples.Add(space.Name + ":" + ent.Handle + " (" + ent.GetType().Name + ")");
                }

                // 3) 同位置同内容的重复文字
                if (includeDuplicates && text != null && !string.IsNullOrWhiteSpace(text) && textKeys.Count < DuplicateScanLimit)
                {
                    if (ent is DBText dt2)
                    {
                        string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0:F3}|{1:F3}|{2}|{3}", dt2.Position.X, dt2.Position.Y, dt2.Layer ?? string.Empty, text.Trim());
                        if (!textKeys.TryGetValue(key, out var bucket))
                        {
                            bucket = new List<string>();
                            textKeys[key] = bucket;
                        }
                        bucket.Add(ent.Handle.ToString());
                    }
                }
            });

            if (includeDuplicates)
            {
                if (textKeys.Count >= DuplicateScanLimit) duplicateScanSkipped = true;
                foreach (var pair in textKeys)
                {
                    if (pair.Value.Count < 2) continue;
                    duplicateCount += pair.Value.Count - 1;
                    if (duplicateSamples.Count < SampleHandleLimit)
                    {
                        duplicateSamples.Add(new Dictionary<string, object>
                        {
                            ["handles"] = pair.Value,
                            ["count"] = pair.Value.Count
                        });
                    }
                }
            }

            // 4) 缺失 SHX 字体
            var missingFonts = TextStyleService.CollectMissingFonts(db, tr);

            // 5) 空图层（定义了但没有任何实体引用）
            var emptyLayers = new List<object>();
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in layerTable)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (usedLayers.Contains(ltr.Name)) continue;
                // 0 层与 Defpoints 是 AutoCAD 的功能层，不算冗余。
                if (ltr.Name == "0" || ltr.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
                emptyLayers.Add(new Dictionary<string, object>
                {
                    ["name"] = ltr.Name,
                    ["isOff"] = ltr.IsOff,
                    ["isFrozen"] = ltr.IsFrozen,
                    ["isLocked"] = ltr.IsLocked
                });
            }

            // 6) 未被任何块引用引用的块定义
            var unusedBlocks = CollectUnusedBlocks(db, tr);

            // 7) 外部参照状态
            var xrefs = CollectXrefs(db, tr);
            var brokenXrefs = xrefs.Where(x =>
            {
                var dict = x as Dictionary<string, object>;
                return dict != null && dict.ContainsKey("isResolved") && !(bool)dict["isResolved"];
            }).ToList();

            // ---------------- 汇总 ----------------
            if (emptyTextCount > 0)
                issues.Add(Issue("warning", "emptyText",
                    emptyTextCount + " 个文字实体内容为空（图面上看不见但会参与统计与翻译）。",
                    emptyTextSamples));

            if (zeroGeometryCount > 0)
                issues.Add(Issue("warning", "zeroGeometry",
                    zeroGeometryCount + " 个实体几何为零（零长度线 / 零半径圆等），通常是误操作残留。",
                    zeroGeomSamples));

            if (duplicateCount > 0)
                issues.Add(Issue("info", "duplicateText",
                    duplicateCount + " 个文字与同图层其他文字位置、内容完全重合（可能是重复标注）。",
                    duplicateSamples));

            if (missingFonts.Count > 0)
            {
                issues.Add(Issue("error", "missingShxFont",
                    missingFonts.Count + " 个文字样式引用了本机不存在的 SHX 字体，打开图纸时会弹「缺少字体」对话框。",
                    missingFonts.Select(m => ((Dictionary<string, object>)m)["style"]).ToList()));
            }

            if (emptyLayers.Count > 0)
                issues.Add(Issue("info", "unusedLayer",
                    emptyLayers.Count + " 个图层没有任何实体，可考虑清理。",
                    emptyLayers.Take(SampleHandleLimit).Select(l => ((Dictionary<string, object>)l)["name"]).ToList()));

            if (unusedBlocks.Count > 0)
                issues.Add(Issue("info", "unusedBlock",
                    unusedBlocks.Count + " 个块定义未被引用，可考虑清理以减小文件体积。",
                    unusedBlocks.Take(SampleHandleLimit).ToList()));

            if (brokenXrefs.Count > 0)
                issues.Add(Issue("error", "brokenXref",
                    brokenXrefs.Count + " 个外部参照未找到源文件，图面会缺失参照内容。",
                    brokenXrefs.Select(x => ((Dictionary<string, object>)x)["name"]).ToList()));

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["healthy"] = issues.Count == 0,
                ["totalEntities"] = totalEntities,
                ["issueCount"] = issues.Count,
                ["issues"] = issues,
                ["details"] = new Dictionary<string, object>
                {
                    ["emptyTextCount"] = emptyTextCount,
                    ["zeroGeometryCount"] = zeroGeometryCount,
                    ["duplicateTextCount"] = duplicateCount,
                    ["duplicateScanSkipped"] = duplicateScanSkipped,
                    ["missingFontStyles"] = missingFonts,
                    ["unusedLayers"] = emptyLayers,
                    ["unusedBlocks"] = unusedBlocks,
                    ["externalReferences"] = xrefs
                }
            };

            return new McpExecutionResult
            {
                ToolName = "cad_drawing_health_check",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        // ================================================================== 工具

        private static Dictionary<string, object> Issue(string severity, string code, string message, object samples)
        {
            return new Dictionary<string, object>
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
                ["samples"] = samples
            };
        }

        private static void BumpText(Dictionary<ObjectId, string> styleNames,
                                     Dictionary<string, int> textByStyle, ObjectId styleId)
        {
            if (styleId.IsNull) return;
            if (!styleNames.TryGetValue(styleId, out string name)) return;
            textByStyle[name] = textByStyle.TryGetValue(name, out int n) ? n + 1 : 1;
        }

        private static string SafeBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                if (br.IsDynamicBlock)
                {
                    var btr = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                    return btr.Name;
                }
                var record = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                return record.Name;
            }
            catch
            {
                return null;
            }
        }

        private static List<object> CollectXrefs(Database db, Transaction tr)
        {
            var result = new List<object>();
            try
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in blockTable)
                {
                    BlockTableRecord btr;
                    try { btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead); }
                    catch { continue; }
                    if (!btr.IsFromExternalReference && !btr.IsFromOverlayReference) continue;

                    var item = new Dictionary<string, object>
                    {
                        ["name"] = btr.Name,
                        ["path"] = btr.PathName,
                        ["isOverlay"] = btr.IsFromOverlayReference,
                        ["isResolved"] = btr.XrefStatus == XrefStatus.Resolved,
                        ["status"] = btr.XrefStatus.ToString()
                    };
                    result.Add(item);
                }
            }
            catch { }
            return result;
        }

        private static List<string> CollectUnusedBlocks(Database db, Transaction tr)
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<ObjectId>();

            // 块定义内部也会嵌套引用别的块定义，因此必须把整张块表都扫一遍，
            // 否则「只被另一个块引用」的块会被误报成未使用。visited 防自引用死循环。
            void Scan(BlockTableRecord record)
            {
                if (record == null) return;
                if (!visited.Add(record.ObjectId)) return;

                foreach (ObjectId entId in record)
                {
                    BlockReference br;
                    try { br = tr.GetObject(entId, OpenMode.ForRead) as BlockReference; }
                    catch { continue; }
                    if (br == null) continue;

                    string name = SafeBlockName(tr, br);
                    if (name != null) referenced.Add(name);

                    try
                    {
                        var nested = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        Scan(nested);
                    }
                    catch { }
                }
            }

            foreach (var space in CadSpaces.Enumerate(db, tr)) Scan(space.Record);

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var definitions = new List<BlockTableRecord>();
            foreach (ObjectId id in blockTable)
            {
                BlockTableRecord btr;
                try { btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead); }
                catch { continue; }
                definitions.Add(btr);
                Scan(btr);
            }

            var unused = new List<string>();
            foreach (var btr in definitions)
            {
                if (btr.IsLayout) continue;
                if (btr.IsAnonymous) continue;                 // 匿名块由 AutoCAD 自己管理
                if (btr.IsFromExternalReference || btr.IsFromOverlayReference) continue;
                if (btr.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                if (referenced.Contains(btr.Name)) continue;
                unused.Add(btr.Name);
            }

            return unused;
        }

        private static List<object> SortDescending(Dictionary<string, int> source)
        {
            return source
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => (object)new Dictionary<string, object>
                {
                    ["name"] = pair.Key,
                    ["count"] = pair.Value
                })
                .ToList();
        }
    }
}
