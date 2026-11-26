using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 整图结构化导出：把当前图纸的图元（含几何）一次性导成 JSON，供「提取全部信息 →
    /// 交给另一个宿主（如 AutoCAD）建模 / 离线分析」这类跨宿主工作流使用。
    ///
    /// 与 cad_query_entities 的分工：后者是「按类型/图层取一小撮、默认 50 上限」的局部探查；
    /// 本工具面向「全量」，提供 offset/limit 分页、space（模型 / 图纸空间）选择与更完整的几何，
    /// 并在响应里返回 total / returned / truncated / nextOffset，让模型清楚还有多少没取。
    /// 只读，不改图（MutatesDrawing=false）。
    ///
    /// 跨宿主交接：<c>write_to_file=true</c> 时把**本页**结果同时写进跨宿主交换目录
    /// （见 <see cref="ExchangeStore"/>），并在响应里返回绝对路径 <c>path</c>，
    /// 供 AutoCAD 侧按路径读取（<c>cad_plan_from_drawing_facts</c> 的 <c>source_path</c>）。
    /// 大图务必配合 <c>limit=5000</c>（上限）分页写全，避免只交接了第一页。
    /// </summary>
    internal static class DrawingExportService
    {
        private const int DefaultLimit = 500;
        private const int MaxLimit = 5000;

        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_export_drawing_json", HandleExport,
                CadSchema.Function("cad_export_drawing_json",
                    "Export the ACTIVE drawing as structured JSON: every entity (with geometry) in the chosen space(s), paged by offset/limit. "
                    + "Use this to extract the WHOLE drawing for cross-host modelling or offline analysis; use cad_query_entities for a small targeted query instead.",
                    CadSchema.Obj(
                        CadSchema.Str("space", "Which space to export: 'model' (default), 'paper', or 'all'."),
                        CadSchema.Str("type", "Optional entity type filter, e.g. 'Line', 'DBText', 'BlockReference'."),
                        CadSchema.Str("layer", "Optional layer name filter."),
                        CadSchema.Int("offset", "Zero-based index of the first entity to return (default 0)."),
                        CadSchema.Int("limit", "Maximum entities to return (default 500, max 5000)."),
                        CadSchema.Bool("include_bounds", "Include each entity's bounding box (default true)."),
                        CadSchema.Bool("write_to_file", "Also write THIS page to the cross-host exchange directory and return its absolute path in 'path' (default false)."),
                        CadSchema.Str("file_name", "Base name for write_to_file (default 'drawing'; a timestamp is appended)."))));
        }

        private static McpExecutionResult HandleExport(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string space = (CadJson.GetString(args, "space") ?? "").Trim().ToLowerInvariant();
            if (space != "model" && space != "paper" && space != "all") space = "model";
            string filterType = CadJson.GetString(args, "type");
            string filterLayer = CadJson.GetString(args, "layer");
            int offset = Math.Max(0, CadJson.GetInt(args, "offset", 0));
            int limit = Math.Max(1, Math.Min(MaxLimit, CadJson.GetInt(args, "limit", DefaultLimit)));
            bool includeBounds = CadJson.GetBool(args, "include_bounds", true);
            bool writeToFile = CadJson.GetBool(args, "write_to_file", false);
            string fileName = (CadJson.GetString(args, "file_name") ?? "").Trim();

            var db = doc.Database;
            List<KeyValuePair<string, BlockTableRecord>> records = CollectRecords(db, tr, space);
            if (records == null)
                return Failure("cannot read block tables for the requested space.");

            var entities = new List<object>();
            int matched = 0;
            foreach (var record in records)
            {
                foreach (ObjectId id in record.Value)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;

                    if (!string.IsNullOrWhiteSpace(filterType)
                        && !string.Equals(ent.GetType().Name, filterType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrWhiteSpace(filterLayer)
                        && !string.Equals(ent.Layer, filterLayer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int index = matched++;
                    if (index < offset) continue;
                    if (entities.Count >= limit) continue; // keep counting so 'total' is the true match count

                    entities.Add(BuildDetail(ent, record.Key, includeBounds, tr));
                }
            }

            int nextOffset = offset + entities.Count;
            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["space"] = space,
                ["offset"] = offset,
                ["limit"] = limit,
                ["total"] = matched,
                ["returned"] = entities.Count,
                ["truncated"] = nextOffset < matched,
                ["nextOffset"] = nextOffset < matched ? (object)nextOffset : null,
                ["entities"] = entities
            };
            // 落盘写的是不含 path 的这份正文（避免文件里引用自己），
            // 而返回给模型的 JSON 必须带上 path：此前先序列化、后补 data["path"]，
            // 结果工具返回的 JSON 里根本没有 path——模型没法告诉用户文件写到了哪，
            // 上下文压缩后连这一条线索也丢了。
            string payload = CadJson.Serialize(data);
            if (writeToFile)
            {
                try
                {
                    data["path"] = ExchangeStore.WriteJson(
                        string.IsNullOrWhiteSpace(fileName) ? "drawing" : fileName, payload);
                }
                catch (Exception ex)
                {
                    // 落盘失败必须让模型看见：它决定这次跨宿主交接到底成没成。
                    data["path"] = null;
                    data["write_error"] = ex.Message;
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_export_drawing_json",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        /// <summary>收集待导出的块表记录（模型空间 + 各布局图纸空间），带空间名。</summary>
        private static List<KeyValuePair<string, BlockTableRecord>> CollectRecords(Database db, Transaction tr, string space)
        {
            var records = new List<KeyValuePair<string, BlockTableRecord>>();
            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (space == "model" || space == "all")
                {
                    records.Add(new KeyValuePair<string, BlockTableRecord>(
                        "Model", (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead)));
                }
                if (space == "paper" || space == "all")
                {
                    var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in layoutDict)
                    {
                        var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                        if (layout == null) continue;
                        if (string.Equals(layout.LayoutName, "Model", StringComparison.OrdinalIgnoreCase)) continue;
                        var btr = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr != null) records.Add(new KeyValuePair<string, BlockTableRecord>(layout.LayoutName, btr));
                    }
                }
            }
            catch { return null; }
            return records;
        }

        /// <summary>单图元详情：通用属性 + 该类型的几何（字段命名与 cad_get_entity_info 保持一致的风格）。</summary>
        private static Dictionary<string, object> BuildDetail(Entity ent, string space, bool includeBounds, Transaction tr)
        {
            var item = new Dictionary<string, object>
            {
                ["handle"] = ent.Handle.ToString(),
                ["type"] = ent.GetType().Name,
                ["space"] = space,
                ["layer"] = ent.Layer,
                ["linetype"] = ent.Linetype,
                ["color"] = ent.Color.ColorIndex,
                ["lineweight"] = ent.LineWeight.ToString(),
                ["visible"] = ent.Visible
            };

            if (includeBounds)
            {
                try
                {
                    var ext = ent.GeometricExtents;
                    item["bounds"] = new Dictionary<string, object>
                    {
                        ["min"] = new[] { Round(ext.MinPoint.X), Round(ext.MinPoint.Y), Round(ext.MinPoint.Z) },
                        ["max"] = new[] { Round(ext.MaxPoint.X), Round(ext.MaxPoint.Y), Round(ext.MaxPoint.Z) }
                    };
                }
                catch { }
            }

            if (ent is Line line)
            {
                item["start"] = new[] { Round(line.StartPoint.X), Round(line.StartPoint.Y), Round(line.StartPoint.Z) };
                item["end"] = new[] { Round(line.EndPoint.X), Round(line.EndPoint.Y), Round(line.EndPoint.Z) };
                item["length"] = Round(line.Length);
            }
            else if (ent is Circle circle)
            {
                item["center"] = new[] { Round(circle.Center.X), Round(circle.Center.Y), Round(circle.Center.Z) };
                item["radius"] = Round(circle.Radius);
                item["area"] = Round(circle.Area);
            }
            else if (ent is Arc arc)
            {
                item["center"] = new[] { Round(arc.Center.X), Round(arc.Center.Y), Round(arc.Center.Z) };
                item["radius"] = Round(arc.Radius);
                item["start_angle_deg"] = Round(arc.StartAngle * 180.0 / Math.PI, 2);
                item["end_angle_deg"] = Round(arc.EndAngle * 180.0 / Math.PI, 2);
            }
            else if (ent is Polyline pline)
            {
                item["closed"] = pline.Closed;
                item["vertex_count"] = pline.NumberOfVertices;
                item["length"] = Round(pline.Length);
                var verts = new List<object>();
                for (int i = 0; i < pline.NumberOfVertices; i++)
                {
                    var pt = pline.GetPoint3dAt(i);
                    verts.Add(new[] { Round(pt.X), Round(pt.Y), Round(pt.Z) });
                }
                item["vertices"] = verts;
            }
            else if (ent is Polyline3d p3d)
            {
                var verts = new List<object>();
                foreach (ObjectId vertexId in p3d)
                {
                    var vertex = tr.GetObject(vertexId, OpenMode.ForRead) as PolylineVertex3d;
                    if (vertex != null) verts.Add(new[] { Round(vertex.Position.X), Round(vertex.Position.Y), Round(vertex.Position.Z) });
                }
                item["vertex_count"] = verts.Count;
                item["vertices"] = verts;
            }
            else if (ent is DBText dt)
            {
                item["text"] = dt.TextString;
                item["height"] = Round(dt.Height);
                item["rotation_deg"] = Round(dt.Rotation * 180.0 / Math.PI, 2);
                item["position"] = new[] { Round(dt.Position.X), Round(dt.Position.Y), Round(dt.Position.Z) };
            }
            else if (ent is MText mt)
            {
                item["text"] = mt.Text;
                item["height"] = Round(mt.TextHeight);
                item["rotation_deg"] = Round(mt.Rotation * 180.0 / Math.PI, 2);
                item["location"] = new[] { Round(mt.Location.X), Round(mt.Location.Y), Round(mt.Location.Z) };
            }
            else if (ent is AttributeReference att)
            {
                item["tag"] = att.Tag;
                item["text"] = att.TextString;
            }
            else if (ent is BlockReference br)
            {
                item["block_name"] = br.Name;
                item["position"] = new[] { Round(br.Position.X), Round(br.Position.Y), Round(br.Position.Z) };
                item["rotation_deg"] = Round(br.Rotation * 180.0 / Math.PI, 2);
                item["scale"] = new[] { Round(br.ScaleFactors.X), Round(br.ScaleFactors.Y), Round(br.ScaleFactors.Z) };
            }
            else if (ent is Dimension dim)
            {
                try { item["measurement"] = Round(dim.Measurement); } catch { }
                try { if (!string.IsNullOrEmpty(dim.DimensionText)) item["dimension_text"] = dim.DimensionText; } catch { }
            }

            return item;
        }

        private static double Round(double value) => Math.Round(value, 3);

        private static double Round(double value, int digits) => Math.Round(value, digits);

        private static McpExecutionResult Failure(string message)
            => new McpExecutionResult
            {
                ToolName = "cad_export_drawing_json",
                Json = CadJson.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = message })
            };
    }
}