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
    /// 分析与整理服务（活动图）：
    ///   · cad_measure_area —— 闭合多段线/圆/样条围合面积与周长统计（工程量估算常用）
    ///   · cad_measure_length —— 线性实体长度汇总（按图层分组）
    ///   · cad_layer_isolate —— 只显示指定图层（其余关/锁），整理图面用
    ///   · cad_export_dxf —— 当前图导出 DXF
    ///   · cad_block_insert —— 把外部 DWG 作为块插入当前图（标准图框/图例复用）
    /// </summary>
    internal static class AnalysisService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_measure_area", HandleMeasureArea,
                CadSchema.Function("cad_measure_area",
                    "Measure closed areas: closed polylines, circles, ellipses in ModelSpace. Optionally filter by layer. Returns per-entity area plus total (sum of areas).",
                    CadSchema.Obj(
                        CadSchema.Str("layer", "Optional layer filter (exact match)."),
                        CadSchema.Int("limit", "Max entities listed, default 50, cap 500."))));

            registry.Register("cad_measure_length", HandleMeasureLength,
                CadSchema.Function("cad_measure_length",
                    "Sum lengths of linear entities (Line, Arc, Circle, open/closed Polyline) grouped by layer. Optionally filter by layer.",
                    CadSchema.Obj(
                        CadSchema.Str("layer", "Optional layer filter (exact match)."))));

            registry.Register("cad_layer_isolate", HandleLayerIsolate,
                CadSchema.Function("cad_layer_isolate",
                    "Isolate the given layers: all OTHER layers are turned off (and optionally locked first). Pass empty layers to restore everything back on.",
                    CadSchema.Obj(
                        CadSchema.Arr("layers", "Layer names to keep visible. Empty array = restore all layers on.", "string", false),
                        CadSchema.Bool("lockOthers", "Also lock the hidden layers (default true) so you don't accidentally edit them."))));

            registry.Register("cad_export_dxf", HandleExportDxf,
                CadSchema.Function("cad_export_dxf",
                    "Export the ACTIVE drawing to a DXF file (does not modify the drawing).",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Target .dxf path. Default: same folder/name as the drawing."))));

            registry.Register("cad_block_insert", HandleBlockInsert,
                CadSchema.Function("cad_block_insert",
                    "Insert an external DWG file into the active drawing as a block reference (imports the block definition if needed, like AutoCAD INSERT).",
                    CadSchema.Obj(
                        CadSchema.Str("dwg_path", "Absolute path of the source DWG.", true),
                        CadSchema.Num("x", "Insert X (WCS)."), CadSchema.Num("y", "Insert Y (WCS)."),
                        CadSchema.Num("scale", "Uniform scale, default 1."),
                        CadSchema.Num("rotation", "Rotation in degrees, default 0."),
                        CadSchema.Str("block_name", "Optional block name. Default: the DWG file name without extension."))));
        }

        // ==================================================================== 测量

        private static McpExecutionResult HandleMeasureArea(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string layer = CadJson.GetString(args, "layer");
            int limit = Math.Max(1, Math.Min(500, CadJson.GetInt(args, "limit", 50)));

            var list = new List<object>();
            double total = 0;

            var ms = OpenModelSpace(tr, doc.Database);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                double area = 0, perimeter = 0;
                if (ent is Polyline pl && pl.Closed) { area = pl.Area; perimeter = pl.Length; }
                else if (ent is Circle c) { area = c.Area; perimeter = c.Circumference; }
                else if (ent is Ellipse e && e.Closed) { area = Math.Abs(e.Area); }
                else continue;

                total += area;
                if (list.Count < limit)
                    list.Add(new Dictionary<string, object>
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer,
                        ["area"] = Math.Round(area, 3),
                        ["perimeter"] = Math.Round(perimeter, 3)
                    });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["closedEntityCount"] = list.Count,
                ["totalArea"] = Math.Round(total, 3),
                ["entities"] = list
            };
            return new McpExecutionResult { ToolName = "cad_measure_area", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleMeasureLength(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string layer = CadJson.GetString(args, "layer");

            var byLayer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var ms = OpenModelSpace(tr, doc.Database);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Curve curve)) continue;
                if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(curve.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;

                double len;
                try { double sp = curve.StartParam, ep = curve.EndParam;
                    len = curve.GetGeCurve().GetLength(sp, ep, 1e-6); }
                catch { continue; }   // 未构造完的曲线取长度会抛，跳过

                byLayer[curve.Layer] = (byLayer.TryGetValue(curve.Layer, out double n) ? n : 0) + len;
            }

            var groups = new List<object>();
            double total = 0;
            foreach (var pair in byLayer)
            {
                total += pair.Value;
                groups.Add(new Dictionary<string, object> { ["layer"] = pair.Key, ["length"] = Math.Round(pair.Value, 3) });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["totalLength"] = Math.Round(total, 3),
                ["byLayer"] = groups
            };
            return new McpExecutionResult { ToolName = "cad_measure_length", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        // ==================================================================== 图层隔离

        private static McpExecutionResult HandleLayerIsolate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (args != null && args.TryGetValue("layers", out var raw) && raw is System.Collections.IEnumerable seq && !(raw is string))
            {
                foreach (var item in seq)
                {
                    string s = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(s)) keep.Add(s.Trim());
                }
            }
            bool lockOthers = CadJson.GetBool(args, "lockOthers", true);

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // keep 为空 = 恢复：把所有图层重新打开。非空 = 其它图层 off(+lock)。
            // 图层名先用实体扫出来（LayerTable 本身包含所有层，包括从未用到的；隔离语义只关心"图面上有的"）。
            var touched = new List<string>();
            var missing = new List<string>();
            if (keep.Count > 0)
            {
                foreach (string name in keep)
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(name)) missing.Add(name);
                }
            }

            int affected = 0;
            var ltWrite = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
            foreach (ObjectId ltId in ltWrite)
            {
                var ltr = (LayerTableRecord)tr.GetObject(ltId, OpenMode.ForWrite);
                if (ltr.IsDependent) continue;   // 外部依赖层不碰

                bool shouldShow = keep.Count == 0 || keep.Contains(ltr.Name);
                if (shouldShow)
                {
                    if (ltr.IsOff) { ltr.IsOff = false; affected++; }
                }
                else
                {
                    if (!ltr.IsOff) { ltr.IsOff = true; affected++; }
                    if (lockOthers && !ltr.IsLocked) ltr.IsLocked = true;
                }
                touched.Add(ltr.Name);
            }

            // keep 为空即"恢复"，同时解锁（恢复时锁着的层没有意义）
            if (keep.Count == 0)
            {
                foreach (ObjectId ltId in ltWrite)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(ltId, OpenMode.ForWrite);
                    if (!ltr.IsDependent && ltr.IsLocked) ltr.IsLocked = false;
                }
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["isolated"] = keep.Count > 0,
                ["layersToggled"] = affected,
                ["missingLayers"] = missing
            };
            return new McpExecutionResult { ToolName = "cad_layer_isolate", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        // ==================================================================== DXF / 插块

        private static McpExecutionResult HandleExportDxf(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string target = CadJson.GetString(args, "file_path");
            if (target.Length == 0)
            {
                string dwg = doc.Name;
                if (string.IsNullOrEmpty(dwg) || !File.Exists(dwg))
                    return new McpExecutionResult { ToolName = "cad_export_dxf", Json = "{\"ok\":false,\"error\":\"Drawing is not saved yet; pass file_path explicitly.\"}" };
                target = Path.Combine(Path.GetDirectoryName(dwg), Path.GetFileNameWithoutExtension(dwg) + ".dxf");
            }
            if (!target.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) target += ".dxf";

            try
            {
                doc.Database.DxfOut(target, 0, true);
                var data = new Dictionary<string, object> { ["ok"] = true, ["dxf"] = target };
                return new McpExecutionResult { ToolName = "cad_export_dxf", Json = CadJson.Serialize(data), MutatesDrawing = false };
            }
            catch (Exception ex)
            {
                return new McpExecutionResult { ToolName = "cad_export_dxf", Json = "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}" };
            }
        }

        private static McpExecutionResult HandleBlockInsert(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string dwgPath = Environment.ExpandEnvironmentVariables(CadJson.GetString(args, "dwg_path")).Trim();
            if (dwgPath.Length == 0 || !File.Exists(dwgPath))
                return new McpExecutionResult { ToolName = "cad_block_insert", Json = "{\"ok\":false,\"error\":\"dwg_path must be an existing DWG file.\"}" };

            string blockName = CadJson.GetString(args, "block_name");
            if (blockName.Length == 0) blockName = Path.GetFileNameWithoutExtension(dwgPath);
            double x = CadJson.GetDouble(args, "x");
            double y = CadJson.GetDouble(args, "y");
            double scale = CadJson.GetDouble(args, "scale", 1);
            if (scale == 0) scale = 1;
            double rotationDeg = CadJson.GetDouble(args, "rotation");

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            if (!bt.Has(blockName))
            {
                // 先读外部文件的定义（源库只读，随后插到当前库）
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(dwgPath, FileShare.Read, true, null);
                    db.Insert(blockName, source, false);
                }
            }

            var btWrite = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(btWrite[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var br = new BlockReference(new Point3d(x, y, 0), btWrite[blockName])
            {
                ScaleFactors = new Scale3d(scale),
                Rotation = rotationDeg * Math.PI / 180.0
            };
            var id = ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = id.Handle.ToString(),
                ["blockName"] = blockName,
                ["imported"] = !bt.Has(blockName)
            };
            return new McpExecutionResult { ToolName = "cad_block_insert", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        // ==================================================================== 公共

        private static BlockTableRecord OpenModelSpace(Transaction tr, Database db)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }
    }
}
