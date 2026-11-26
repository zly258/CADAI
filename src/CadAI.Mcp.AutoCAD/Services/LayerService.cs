using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class LayerService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_layer_list", HandleList,
                CadSchema.Function("cad_layer_list",
                    "List all layers in the current drawing with their name, color index, off/frozen/locked status.",
                    CadSchema.Obj()));

            registry.Register("cad_layer_create", HandleCreate,
                CadSchema.Function("cad_layer_create",
                    "Create a new layer with optional color index (1-255).",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Layer name to create.", true),
                        CadSchema.Int("color_index", "AutoCAD Color Index (ACI) 1-255 (e.g. 1=red, 2=yellow, 3=green, 4=cyan, 7=white). Default is 7."),
                        CadSchema.Str("linetype", "Optional linetype name (e.g. 'CENTER', 'DASHED'); loaded from acad.lin if missing."),
                        CadSchema.Int("lineweight", "Optional lineweight in 1/100 mm (e.g. 25 = 0.25 mm)."),
                        CadSchema.Str("plot_style", "Optional plot style name (e.g. 'Normal')."))));

            registry.Register("cad_layer_set_state", HandleSetState,
                CadSchema.Function("cad_layer_set_state",
                    "Change layer states such as off, frozen, or locked.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Target layer name.", true),
                        CadSchema.Bool("is_off", "Turn layer off (hide entities)."),
                        CadSchema.Bool("is_frozen", "Freeze layer (do not regenerate or display)."),
                        CadSchema.Bool("is_locked", "Lock layer (cannot be edited)."))));
        }

        /// <summary>
        /// 图层存在性预检。不存在的图层名直接赋给 Entity.Layer 会抛 eSetLayerFailed，
        /// 整个事务 Abort，模型只看到模糊的"执行错误"。这里提前给出可执行的错误。
        /// </summary>
        internal static bool EnsureLayerExists(
            Transaction tr, Database db, string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) return true;

            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return true;

            error = "Layer '" + name + "' does not exist. Create it first with cad_layer_create.";
            return false;
        }

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)        {
            var db = doc.Database;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var list = new List<object>();

            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = ltr.Name,
                    ["color"] = ltr.Color.ColorIndex,
                    ["isOff"] = ltr.IsOff,
                    ["isFrozen"] = ltr.IsFrozen,
                    ["isLocked"] = ltr.IsLocked,
                    ["isPlottable"] = ltr.IsPlottable
                });
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["total"] = list.Count,
                ["layers"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_layer_list",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            int colorIndex = CadJson.GetInt(args, "color_index", 7);

            if (string.IsNullOrWhiteSpace(name))
            {
                return new McpExecutionResult { ToolName = "cad_layer_create", Json = "{\"ok\":false,\"error\":\"Layer name cannot be empty.\"}" };
            }

            var db = doc.Database;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            if (lt.Has(name))
            {
                return new McpExecutionResult
                {
                    ToolName = "cad_layer_create",
                    Json = "{\"ok\":true,\"message\":\"图层已存在: " + name + "\",\"created\":false}",
                    MutatesDrawing = false
                };
            }

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord();
            ltr.Name = name;
            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Math.Max(1, Math.Min(255, colorIndex)));

            string linetype = CadJson.GetString(args, "linetype");
            if (!string.IsNullOrWhiteSpace(linetype))
            {
                var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (!ltt.Has(linetype))
                {
                    try { db.LoadLineTypeFile(linetype, "acad.lin"); } catch { }
                    ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                }
                if (!ltt.Has(linetype))
                {
                    return new McpExecutionResult
                    {
                        ToolName = "cad_layer_create",
                        Json = "{\"ok\":false,\"error\":\"线型不存在: " + linetype + "\"}",
                        MutatesDrawing = false
                    };
                }
                ltr.LinetypeObjectId = ltt[linetype];
            }

            if (args.ContainsKey("lineweight"))
            {
                int lineweight = CadJson.GetInt(args, "lineweight", -3);
                if (Enum.IsDefined(typeof(LineWeight), lineweight)) ltr.LineWeight = (LineWeight)lineweight;
            }

            string plotStyle = CadJson.GetString(args, "plot_style");
            if (!string.IsNullOrWhiteSpace(plotStyle)) ltr.PlotStyleName = plotStyle;

            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);

            return new McpExecutionResult
            {
                ToolName = "cad_layer_create",
                Json = "{\"ok\":true,\"message\":\"成功创建图层: " + name + "\",\"created\":true}",
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleSetState(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name))
                return new McpExecutionResult { ToolName = "cad_layer_set_state", Json = "{\"ok\":false,\"error\":\"name is required.\"}" };

            var db = doc.Database;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
                return new McpExecutionResult { ToolName = "cad_layer_set_state", Json = "{\"ok\":false,\"error\":\"图层不存在: " + name + "\"}" };

            var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
            if (args.ContainsKey("is_off")) ltr.IsOff = CadJson.GetBool(args, "is_off");
            if (args.ContainsKey("is_locked")) ltr.IsLocked = CadJson.GetBool(args, "is_locked");
            if (args.ContainsKey("is_frozen") && ltr.ObjectId != db.Clayer) ltr.IsFrozen = CadJson.GetBool(args, "is_frozen");

            return new McpExecutionResult
            {
                ToolName = "cad_layer_set_state",
                Json = "{\"ok\":true,\"message\":\"图层状态已更新: " + name + "\"}",
                MutatesDrawing = true
            };
        }
    }
}
