using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class SelectionService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_selection_get", HandleGetSelection,
                CadSchema.Function("cad_selection_get",
                    "Get all entities currently selected (picked/highlighted) by the user in AutoCAD with their Handles and types.",
                    CadSchema.Obj()));

            registry.Register("cad_selection_set", HandleSetSelection,
                CadSchema.Function("cad_selection_set",
                    "Set the active selection set in AutoCAD by Handle list so the target entities are highlighted on screen.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to select.", "string", true))));

            registry.Register("cad_selection_filter", HandleFilterSelection,
                CadSchema.Function("cad_selection_filter",
                    "Get the current selection with rich per-entity detail (text content, block name, geometry measures), filtered by type/layer/text substring. " +
                    "Use this to inspect WHAT the user selected (e.g. read all texts in the picked area, list block attributes).",
                    CadSchema.Obj(
                        CadSchema.Str("type", "Optional entity type filter, e.g. DBText, MText, BlockReference, Line, Circle."),
                        CadSchema.Str("layer", "Optional layer name filter (exact match)."),
                        CadSchema.Str("textContains", "Optional case-insensitive substring the entity text/attribute must contain."),
                        CadSchema.Str("blockName", "Optional block name filter for BlockReference entities."),
                        CadSchema.Int("limit", "Max entities returned, default 200, cap 2000."))));

            registry.Register("cad_selection_select_by_filter", HandleSelectByFilter,
                CadSchema.Function("cad_selection_select_by_filter",
                    "Highlight (select) all entities in ModelSpace matching the filter conditions, replacing the current selection. " +
                    "Returns the matched Handles so subsequent per-entity tools can use them.",
                    CadSchema.Obj(
                        CadSchema.Str("type", "Optional entity type filter, e.g. DBText, MText, BlockReference."),
                        CadSchema.Str("layer", "Optional layer name filter (exact match)."),
                        CadSchema.Str("textContains", "Optional case-insensitive substring the entity text must contain."),
                        CadSchema.Str("blockName", "Optional block name filter for BlockReference entities."),
                        CadSchema.Bool("addToSelection", "true = add to current selection instead of replacing it."))));

            registry.Register("cad_selection_window", HandleWindowSelection,
                CadSchema.Function("cad_selection_window",
                    "Select all entities inside a rectangular window (ModelSpace WCS coordinates) and return them. " +
                    "Set select=false to only list matches without changing the on-screen selection.",
                    CadSchema.Obj(
                        CadSchema.Num("minX", "Window min X."), CadSchema.Num("minY", "Window min Y."),
                        CadSchema.Num("maxX", "Window max X."), CadSchema.Num("maxY", "Window max Y."),
                        CadSchema.Bool("select", "true (default) = highlight the matched entities on screen."),
                        CadSchema.Int("limit", "Max entities returned, default 500, cap 2000."))));
        }

        private static McpExecutionResult HandleGetSelection(string argsJson, Document doc, Transaction tr)
        {
            var ed = doc.Editor;
            var psr = ed.SelectImplied();

            var list = new List<object>();

            if (psr.Status == PromptStatus.OK && psr.Value != null)
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null || so.ObjectId.IsNull) continue;
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var item = new Dictionary<string, object>
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer
                    };

                    if (ent is DBText dt) item["text"] = dt.TextString;
                    else if (ent is MText mt) item["text"] = mt.Text;

                    list.Add(item);
                }
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["selectedCount"] = list.Count,
                ["entities"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_selection_get",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleSetSelection(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_selection_set", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            var db = doc.Database;
            var idList = new List<ObjectId>();

            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        idList.Add(id);
                    }
                }
            }

            doc.Editor.SetImpliedSelection(idList.ToArray());

            return new McpExecutionResult
            {
                ToolName = "cad_selection_set",
                Json = "{\"ok\":true,\"selectedCount\":" + idList.Count + "}",
                MutatesDrawing = false
            };
        }

        // ==================================================================== 过滤 / 富详情

        /// <summary>
        /// 判断实体是否命中过滤条件。类型 / 图层精确匹配，文本与块名不区分大小写的包含匹配。
        /// 条件全空 = 不过滤。选择集工具共用，保证 cad_selection_filter 与
        /// cad_selection_select_by_filter 的语义一致。
        /// </summary>
        private static bool MatchesFilter(Entity ent, string type, string layer, string textContains, string blockName)
        {
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(ent.GetType().Name, type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(blockName))
            {
                if (!(ent is BlockReference br) ||
                    !string.Equals(BestBlockName(br), blockName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(textContains))
            {
                string text = ReadAllText(ent);
                if (text == null || text.IndexOf(textContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        /// <summary>块参照的有效名：优先 AttributeTag 可读的 EffectiveName（匿名块也解析出真名）。</summary>
        private static string BestBlockName(BlockReference br)
        {
            try { return br.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>实体的全部可读文本：DBText/MText 正文 + 块参照的属性表。</summary>
        private static string ReadAllText(Entity ent)
        {
            if (ent is DBText dt) return dt.TextString;
            if (ent is MText mt) return mt.Text;
            if (ent is BlockReference br)
            {
                var parts = new List<string>();
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (tr_cache != null && tr_cache.GetObject(attId, OpenMode.ForRead) is AttributeReference att)
                        parts.Add(att.Tag + "=" + att.TextString);
                }
                return string.Join(" | ", parts);
            }
            return null;
        }

        /// <summary>ReadAllText 需要事务读属性；由入口先把当前事务挂进来。</summary>
        [ThreadStatic] private static Transaction tr_cache;

        /// <summary>实体的富详情：几何测量 + 文本 + 块属性摘要。过滤读取与窗口选取共用。</summary>
        private static Dictionary<string, object> Describe(Entity ent)
        {
            var item = new Dictionary<string, object>
            {
                ["handle"] = ent.Handle.ToString(),
                ["type"] = ent.GetType().Name,
                ["layer"] = ent.Layer
            };

            if (ent is DBText dt)
            {
                item["text"] = dt.TextString;
                item["height"] = Math.Round(dt.Height, 3);
                item["position"] = new[] { Math.Round(dt.Position.X, 2), Math.Round(dt.Position.Y, 2) };
            }
            else if (ent is MText mt)
            {
                item["text"] = mt.Text;
                item["position"] = new[] { Math.Round(mt.Location.X, 2), Math.Round(mt.Location.Y, 2) };
            }
            else if (ent is BlockReference br)
            {
                item["blockName"] = BestBlockName(br);
                item["position"] = new[] { Math.Round(br.Position.X, 2), Math.Round(br.Position.Y, 2) };
                var atts = new List<object>();
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (tr_cache != null && tr_cache.GetObject(attId, OpenMode.ForRead) is AttributeReference att)
                        atts.Add(new Dictionary<string, object> { ["tag"] = att.Tag, ["text"] = att.TextString });
                }
                if (atts.Count > 0) item["attributes"] = atts;
            }
            else if (ent is Line line)
            {
                item["length"] = Math.Round(line.Length, 3);
                item["start"] = new[] { Math.Round(line.StartPoint.X, 2), Math.Round(line.StartPoint.Y, 2) };
                item["end"] = new[] { Math.Round(line.EndPoint.X, 2), Math.Round(line.EndPoint.Y, 2) };
            }
            else if (ent is Circle c)
            {
                item["center"] = new[] { Math.Round(c.Center.X, 2), Math.Round(c.Center.Y, 2) };
                item["radius"] = Math.Round(c.Radius, 3);
            }
            else if (ent is Polyline pl)
            {
                item["vertices"] = pl.NumberOfVertices;
                try { item["length"] = Math.Round(pl.Length, 3); } catch { }
            }
            else if (ent is Arc a)
            {
                item["radius"] = Math.Round(a.Radius, 3);
                item["length"] = Math.Round(a.Length, 3);
            }

            try
            {
                var ext = ent.GeometricExtents;
                item["bounds"] = new[]
                {
                    Math.Round(ext.MinPoint.X, 2), Math.Round(ext.MinPoint.Y, 2),
                    Math.Round(ext.MaxPoint.X, 2), Math.Round(ext.MaxPoint.Y, 2)
                };
            }
            catch { }

            return item;
        }

        /// <summary>遍历模型空间，返回命中过滤条件的实体（上限 limit）。</summary>
        private static List<Entity> CollectMatching(Transaction tr, Database db, string type, string layer,
                                                    string textContains, string blockName, int limit)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var result = new List<Entity>();
            foreach (ObjectId id in ms)
            {
                if (result.Count >= limit) break;
                if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && MatchesFilter(ent, type, layer, textContains, blockName))
                    result.Add(ent);
            }
            return result;
        }

        /// <summary>读取用户当前选中的实体（富详情），可按条件过滤。</summary>
        private static McpExecutionResult HandleFilterSelection(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string type = CadJson.GetString(args, "type");
            string layer = CadJson.GetString(args, "layer");
            string textContains = CadJson.GetString(args, "textContains");
            string blockName = CadJson.GetString(args, "blockName");
            int limit = Math.Max(1, Math.Min(2000, CadJson.GetInt(args, "limit", 200)));

            tr_cache = tr;
            try
            {
                var ed = doc.Editor;
                var psr = ed.SelectImplied();

                var list = new List<object>();
                if (psr.Status == PromptStatus.OK && psr.Value != null)
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        if (so == null || so.ObjectId.IsNull) continue;
                        if (!(tr.GetObject(so.ObjectId, OpenMode.ForRead) is Entity ent)) continue;
                        if (!MatchesFilter(ent, type, layer, textContains, blockName)) continue;
                        if (list.Count >= limit) break;
                        list.Add(Describe(ent));
                    }
                }

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["selectedCount"] = list.Count,
                    ["entities"] = list
                };
                return new McpExecutionResult
                {
                    ToolName = "cad_selection_filter",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false
                };
            }
            finally { tr_cache = null; }
        }

        /// <summary>按过滤条件高亮模型空间实体（可追加），返回命中的 Handle 列表。</summary>
        private static McpExecutionResult HandleSelectByFilter(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string type = CadJson.GetString(args, "type");
            string layer = CadJson.GetString(args, "layer");
            string textContains = CadJson.GetString(args, "textContains");
            string blockName = CadJson.GetString(args, "blockName");
            bool addTo = CadJson.GetBool(args, "addToSelection", false);

            tr_cache = tr;
            try
            {
                var matched = CollectMatching(tr, doc.Database, type, layer, textContains, blockName, 2000);

                var ids = new List<ObjectId>();
                if (addTo)
                {
                    var psr = doc.Editor.SelectImplied();
                    if (psr.Status == PromptStatus.OK && psr.Value != null)
                    {
                        foreach (SelectedObject so in psr.Value)
                        {
                            if (so != null && !so.ObjectId.IsNull) ids.Add(so.ObjectId);
                        }
                    }
                }
                foreach (var ent in matched) ids.Add(ent.ObjectId);
                doc.Editor.SetImpliedSelection(ids.ToArray());

                var handles = new List<string>();
                foreach (var ent in matched) handles.Add(ent.Handle.ToString());

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["matchedCount"] = handles.Count,
                    ["selectedCount"] = ids.Count,
                    ["handles"] = handles
                };
                return new McpExecutionResult
                {
                    ToolName = "cad_selection_select_by_filter",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false
                };
            }
            finally { tr_cache = null; }
        }

        /// <summary>按矩形窗口（模型空间 WCS 坐标）选取实体并返回富详情。</summary>
        private static McpExecutionResult HandleWindowSelection(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            int limit = Math.Max(1, Math.Min(2000, CadJson.GetInt(args, "limit", 500)));
            bool select = CadJson.GetBool(args, "select", true);

            var pMin = new Point3d(CadJson.GetDouble(args, "minX"), CadJson.GetDouble(args, "minY"), 0);
            var pMax = new Point3d(CadJson.GetDouble(args, "maxX"), CadJson.GetDouble(args, "maxY"), 0);

            tr_cache = tr;
            try
            {
                var ed = doc.Editor;
                var psr = ed.SelectWindow(pMin, pMax);
                var list = new List<object>();
                var ids = new List<ObjectId>();

                if (psr.Status == PromptStatus.OK && psr.Value != null)
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        if (so == null || so.ObjectId.IsNull) continue;
                        if (!(tr.GetObject(so.ObjectId, OpenMode.ForRead) is Entity ent)) continue;
                        if (list.Count >= limit) break;
                        list.Add(Describe(ent));
                        ids.Add(so.ObjectId);
                    }
                }

                if (select) ed.SetImpliedSelection(ids.ToArray());

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["matchedCount"] = list.Count,
                    ["entities"] = list
                };
                return new McpExecutionResult
                {
                    ToolName = "cad_selection_window",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false
                };
            }
            finally { tr_cache = null; }
        }
    }
}
