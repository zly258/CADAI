using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class EntityService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_count_entities_by_type", HandleCountByType,
                CadSchema.Function("cad_count_entities_by_type",
                    "Count all entities in the current drawing grouped by their type (Line, Circle, DBText, MText, BlockReference, etc.).",
                    CadSchema.Obj()));

            registry.Register("cad_query_entities", HandleQueryEntities,
                CadSchema.Function("cad_query_entities",
                    "Query entities matching filters such as type, layer, and optional 2D bounding box (min_x, min_y, max_x, max_y).",
                    CadSchema.Obj(
                        CadSchema.Str("type", "Entity type name (e.g. 'DBText', 'MText', 'Line', 'Circle', 'Dimension', 'BlockReference')."),
                        CadSchema.Str("layer", "Filter by layer name."),
                        CadSchema.Num("min_x", "Optional spatial filter minimum X."),
                        CadSchema.Num("min_y", "Optional spatial filter minimum Y."),
                        CadSchema.Num("max_x", "Optional spatial filter maximum X."),
                        CadSchema.Num("max_y", "Optional spatial filter maximum Y."),
                        CadSchema.Int("limit", "Maximum number of results to return (default 50, max 200)."))));

            registry.Register("cad_get_entity_info", HandleGetEntityInfo,
                CadSchema.Function("cad_get_entity_info",
                    "Get detailed geometric and attribute properties of an entity by its hex Handle (e.g. '1A3F').",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Entity Handle in hex format.", true))));

            registry.Register("cad_entity_delete", HandleDelete,
                CadSchema.Function("cad_entity_delete",
                    "Delete one or more entities by their hex Handle list.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to delete.", "string", true))));

            registry.Register("cad_entity_set_properties", HandleSetProperties,
                CadSchema.Function("cad_entity_set_properties",
                    "Modify properties (layer, color_index, linetype, lineweight, linetype_scale) of entities specified by Handles.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles.", "string", true),
                        CadSchema.Str("layer", "Target layer name."),
                        CadSchema.Int("color_index", "Target color index (1-255)."),
                        CadSchema.Str("linetype", "Target linetype name (e.g. 'CENTER', 'DASHED', 'Continuous')."),
                        CadSchema.Num("lineweight", "Lineweight in mm (e.g. 0.25, 0.5) or integer hundredths of mm (e.g. 25, 50)."),
                        CadSchema.Num("linetype_scale", "Linetype scale multiplier (e.g. 1.0, 10.0)."))));

            registry.Register("cad_entity_transform", HandleTransform,
                CadSchema.Function("cad_entity_transform",
                    "Apply geometric transformation (move/translate, rotate, scale) to entities specified by Handles.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to transform.", "string", true),
                        CadSchema.Str("operation", "Transform operation: 'move', 'rotate', or 'scale'.", true),
                        CadSchema.Num("dx", "Displacement delta X (for 'move').", false),
                        CadSchema.Num("dy", "Displacement delta Y (for 'move').", false),
                        CadSchema.Num("dz", "Displacement delta Z (for 'move', default 0).", false),
                        CadSchema.Num("angle_degrees", "Counter-clockwise rotation angle in degrees (for 'rotate').", false),
                        CadSchema.Num("scale_factor", "Scale factor, e.g. 2.0 or 0.5 (for 'scale').", false),
                        CadSchema.Num("base_x", "Base point X for rotate or scale (default 0).", false),
                        CadSchema.Num("base_y", "Base point Y for rotate or scale (default 0).", false),
                        CadSchema.Num("base_z", "Base point Z for rotate or scale (default 0).", false))));

            registry.Register("cad_entity_copy", HandleCopy,
                CadSchema.Function("cad_entity_copy",
                    "Clone one or more entities and place copies shifted by (dx, dy, dz).",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to copy.", "string", true),
                        CadSchema.Num("dx", "Displacement X per copy.", true),
                        CadSchema.Num("dy", "Displacement Y per copy.", true),
                        CadSchema.Num("dz", "Displacement Z (default 0)."),
                        CadSchema.Int("count", "Number of incremental copies (default 1)."))));

            registry.Register("cad_entity_mirror", HandleMirror,
                CadSchema.Function("cad_entity_mirror",
                    "Mirror entities across a line defined by (x1, y1)-(x2, y2). Optionally erases the source entities.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to mirror.", "string", true),
                        CadSchema.Num("x1", "Mirror line first point X.", true),
                        CadSchema.Num("y1", "Mirror line first point Y.", true),
                        CadSchema.Num("x2", "Mirror line second point X.", true),
                        CadSchema.Num("y2", "Mirror line second point Y.", true),
                        CadSchema.Bool("erase_source", "Erase original entities after mirroring (default false)."))));

            registry.Register("cad_entity_offset", HandleOffset,
                CadSchema.Function("cad_entity_offset",
                    "Offset curve entities (Line, Arc, Circle, Polyline, Spline) by signed distance, optionally oriented toward a side point (side_x, side_y).",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of curve hex Handles to offset.", "string", true),
                        CadSchema.Num("distance", "Offset distance.", true),
                        CadSchema.Num("side_x", "Optional point X choosing which side to offset toward."),
                        CadSchema.Num("side_y", "Optional point Y choosing which side to offset toward."))));

            registry.Register("cad_entity_array_rectangular", HandleArrayRectangular,
                CadSchema.Function("cad_entity_array_rectangular",
                    "Create a rectangular array of clones from source handles.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of source hex Handles.", "string", true),
                        CadSchema.Int("rows", "Number of rows (>= 1).", true),
                        CadSchema.Int("columns", "Number of columns (>= 1).", true),
                        CadSchema.Num("row_spacing", "Row spacing along +Y.", true),
                        CadSchema.Num("column_spacing", "Column spacing along +X.", true))));

            registry.Register("cad_entity_array_polar", HandleArrayPolar,
                CadSchema.Function("cad_entity_array_polar",
                    "Create a circular/polar array of clones around (center_x, center_y).",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of source hex Handles.", "string", true),
                        CadSchema.Num("center_x", "Array center X.", true),
                        CadSchema.Num("center_y", "Array center Y.", true),
                        CadSchema.Int("count", "Total number of items including original (>= 2).", true),
                        CadSchema.Num("total_angle_degrees", "Total fill angle in degrees (default 360)."),
                        CadSchema.Bool("rotate_items", "Rotate each item as it is arrayed (default true)."))));

            registry.Register("cad_entity_fillet_chamfer", HandleFilletChamfer,
                CadSchema.Function("cad_entity_fillet_chamfer",
                    "Create a 2D fillet arc or straight chamfer bevel connecting two non-parallel lines (by hex handles).",
                    CadSchema.Obj(
                        CadSchema.Str("handle1", "First Line hex Handle.", true),
                        CadSchema.Str("handle2", "Second Line hex Handle.", true),
                        CadSchema.Str("mode", "'fillet' or 'chamfer' (default 'fillet')."),
                        CadSchema.Num("radius", "Fillet radius (for mode='fillet')."),
                        CadSchema.Num("distance1", "First setback distance (for mode='chamfer')."),
                        CadSchema.Num("distance2", "Second setback distance (defaults to distance1)."),
                        CadSchema.Bool("trim", "Trim/extend both lines cleanly to the fillet/chamfer endpoints (default true)."))));

            registry.Register("cad_entity_explode", HandleExplode,
                CadSchema.Function("cad_entity_explode",
                    "Explode compound entities (BlockReference, Polyline, Dimension) into constituent entities.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of hex Handles to explode.", "string", true),
                        CadSchema.Bool("erase_source", "Erase source entities after exploding (default true)."))));
        }

        private static McpExecutionResult HandleCountByType(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                string typeName = ent.GetType().Name;
                counts[typeName] = counts.ContainsKey(typeName) ? counts[typeName] + 1 : 1;
                total++;
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["totalEntities"] = total,
                ["typeCounts"] = counts
            };

            return new McpExecutionResult
            {
                ToolName = "cad_count_entities_by_type",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleQueryEntities(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string filterType = CadJson.GetString(args, "type");
            string filterLayer = CadJson.GetString(args, "layer");
            bool hasBounds = TryParseSpatialBounds(args, out double minX, out double minY, out double maxX, out double maxY);
            int limit = Math.Max(1, Math.Min(200, CadJson.GetInt(args, "limit", 50)));

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var list = new List<object>();

            foreach (ObjectId id in ms)
            {
                if (list.Count >= limit) break;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                if (!string.IsNullOrWhiteSpace(filterType) && !string.Equals(ent.GetType().Name, filterType, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(filterLayer) && !string.Equals(ent.Layer, filterLayer, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (hasBounds && !EntityOverlapsBounds(ent, minX, minY, maxX, maxY))
                    continue;

                var item = new Dictionary<string, object>
                {
                    ["handle"] = ent.Handle.ToString(),
                    ["type"] = ent.GetType().Name,
                    ["layer"] = ent.Layer,
                    ["color"] = ent.Color.ColorIndex
                };

                // 针对文字补充预览内容
                if (ent is DBText dt) item["text"] = dt.TextString;
                else if (ent is MText mt) item["text"] = mt.Text;

                list.Add(item);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["count"] = list.Count,
                ["entities"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_query_entities",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleGetEntityInfo(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hexHandle = CadJson.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hexHandle))
                return new McpExecutionResult { ToolName = "cad_get_entity_info", Json = "{\"ok\":false,\"error\":\"handle is required.\"}" };

            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long handleVal))
                return new McpExecutionResult { ToolName = "cad_get_entity_info", Json = "{\"ok\":false,\"error\":\"Invalid hex handle.\"}" };

            var db = doc.Database;
            var handle = new Handle(handleVal);
            if (!db.TryGetObjectId(handle, out ObjectId id) || id.IsNull)
                return new McpExecutionResult { ToolName = "cad_get_entity_info", Json = "{\"ok\":false,\"error\":\"Entity not found for handle: " + hexHandle + "\"}" };

            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null)
                return new McpExecutionResult { ToolName = "cad_get_entity_info", Json = "{\"ok\":false,\"error\":\"Object is not a drawing Entity.\"}" };

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = hexHandle,
                ["type"] = ent.GetType().Name,
                ["layer"] = ent.Layer,
                ["color"] = ent.Color.ColorIndex,
                ["lineweight"] = ent.LineWeight.ToString(),
                ["visible"] = ent.Visible
            };

            try
            {
                var ext = ent.GeometricExtents;
                data["bounds"] = new Dictionary<string, object>
                {
                    ["min"] = new[] { Math.Round(ext.MinPoint.X, 3), Math.Round(ext.MinPoint.Y, 3), Math.Round(ext.MinPoint.Z, 3) },
                    ["max"] = new[] { Math.Round(ext.MaxPoint.X, 3), Math.Round(ext.MaxPoint.Y, 3), Math.Round(ext.MaxPoint.Z, 3) }
                };
            }
            catch { }

            if (ent is Line line)
            {
                data["start"] = new[] { Math.Round(line.StartPoint.X, 3), Math.Round(line.StartPoint.Y, 3), Math.Round(line.StartPoint.Z, 3) };
                data["end"] = new[] { Math.Round(line.EndPoint.X, 3), Math.Round(line.EndPoint.Y, 3), Math.Round(line.EndPoint.Z, 3) };
                data["length"] = Math.Round(line.Length, 3);
            }
            else if (ent is Circle circle)
            {
                data["center"] = new[] { Math.Round(circle.Center.X, 3), Math.Round(circle.Center.Y, 3), Math.Round(circle.Center.Z, 3) };
                data["radius"] = Math.Round(circle.Radius, 3);
                data["area"] = Math.Round(circle.Area, 3);
            }
            else if (ent is DBText dt)
            {
                data["text"] = dt.TextString;
                data["height"] = Math.Round(dt.Height, 3);
                data["rotation"] = Math.Round(dt.Rotation * 180 / Math.PI, 2);
                data["position"] = new[] { Math.Round(dt.Position.X, 3), Math.Round(dt.Position.Y, 3), Math.Round(dt.Position.Z, 3) };
            }
            else if (ent is MText mt)
            {
                data["text"] = mt.Text;
                data["contents"] = mt.Contents;
                data["height"] = Math.Round(mt.TextHeight, 3);
                data["rotation"] = Math.Round(mt.Rotation * 180 / Math.PI, 2);
                data["location"] = new[] { Math.Round(mt.Location.X, 3), Math.Round(mt.Location.Y, 3), Math.Round(mt.Location.Z, 3) };
            }

            return new McpExecutionResult
            {
                ToolName = "cad_get_entity_info",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleDelete(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_entity_delete", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            var db = doc.Database;
            int deleted = 0;
            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased)
                        {
                            ent.Erase();
                            deleted++;
                        }
                    }
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_delete",
                Json = "{\"ok\":true,\"deletedCount\":" + deleted + "}",
                MutatesDrawing = deleted > 0
            };
        }

        private static McpExecutionResult HandleSetProperties(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_entity_set_properties", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            string layer = CadJson.GetString(args, "layer");
            int colorIndex = CadJson.GetInt(args, "color_index", -1);
            string linetype = CadJson.GetString(args, "linetype");
            bool hasLinetypeScale = args.ContainsKey("linetype_scale");
            double linetypeScale = CadJson.GetDouble(args, "linetype_scale", 1.0);
            bool hasLineweight = args.ContainsKey("lineweight");
            LineWeight parsedWeight = LineWeight.ByLayer;
            if (hasLineweight)
            {
                double rawLw = CadJson.GetDouble(args, "lineweight", -1.0);
                int code = rawLw > 0 && rawLw < 5.0 ? (int)Math.Round(rawLw * 100.0) : (int)Math.Round(rawLw);
                if (Enum.IsDefined(typeof(LineWeight), code))
                    parsedWeight = (LineWeight)code;
                else
                    hasLineweight = false;
            }

            var db = doc.Database;
            if (!LayerService.EnsureLayerExists(tr, db, layer, out string layerError))
            {
                var error = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = layerError,
                    ["error_type"] = "layer_not_found"
                };
                return new McpExecutionResult
                {
                    ToolName = "cad_entity_set_properties",
                    Json = CadJson.Serialize(error),
                    MutatesDrawing = false
                };
            }

            string resolvedLinetype = null;
            if (!string.IsNullOrWhiteSpace(linetype))
            {
                resolvedLinetype = GeometryService.ResolveOrLoadLinetype(db, tr, linetype);
                if (string.IsNullOrWhiteSpace(resolvedLinetype))
                {
                    return new McpExecutionResult
                    {
                        ToolName = "cad_entity_set_properties",
                        Json = CadJson.Serialize(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = "Linetype not found or could not be loaded: " + linetype
                        }),
                        MutatesDrawing = false
                    };
                }
            }

            int updated = 0;
            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased)
                        {
                            if (!string.IsNullOrWhiteSpace(layer)) ent.Layer = layer;
                            if (colorIndex > 0) ent.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
                            if (!string.IsNullOrWhiteSpace(resolvedLinetype)) ent.Linetype = resolvedLinetype;
                            if (hasLineweight) ent.LineWeight = parsedWeight;
                            if (hasLinetypeScale && linetypeScale > 0) ent.LinetypeScale = linetypeScale;
                            updated++;
                        }
                    }
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_set_properties",
                Json = "{\"ok\":true,\"updatedCount\":" + updated + "}",
                MutatesDrawing = updated > 0
            };
        }

        private static McpExecutionResult HandleTransform(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_entity_transform", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            string op = CadJson.GetString(args, "operation").ToLowerInvariant();
            Matrix3d matrix;

            double bx = CadJson.GetDouble(args, "base_x", 0.0);
            double by = CadJson.GetDouble(args, "base_y", 0.0);
            double bz = CadJson.GetDouble(args, "base_z", 0.0);
            var basePoint = new Point3d(bx, by, bz);

            if (op == "move" || op == "translate")
            {
                double dx = CadJson.GetDouble(args, "dx", 0.0);
                double dy = CadJson.GetDouble(args, "dy", 0.0);
                double dz = CadJson.GetDouble(args, "dz", 0.0);
                matrix = Matrix3d.Displacement(new Vector3d(dx, dy, dz));
            }
            else if (op == "rotate")
            {
                double angleDeg = CadJson.GetDouble(args, "angle_degrees", 0.0);
                double angleRad = angleDeg * Math.PI / 180.0;
                matrix = Matrix3d.Rotation(angleRad, Vector3d.ZAxis, basePoint);
            }
            else if (op == "scale")
            {
                double factor = CadJson.GetDouble(args, "scale_factor", 1.0);
                if (Math.Abs(factor) < 1e-6)
                    return new McpExecutionResult { ToolName = "cad_entity_transform", Json = "{\"ok\":false,\"error\":\"scale_factor cannot be 0.\"}" };
                matrix = Matrix3d.Scaling(factor, basePoint);
            }
            else
            {
                return new McpExecutionResult { ToolName = "cad_entity_transform", Json = "{\"ok\":false,\"error\":\"Unsupported operation '" + op + "'. Use 'move', 'rotate', or 'scale'.\"}" };
            }

            var db = doc.Database;
            int transformed = 0;
            var transformedHandles = new List<string>();

            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased)
                        {
                            ent.TransformBy(matrix);
                            transformed++;
                            transformedHandles.Add(hex);
                        }
                    }
                }
            }

            var res = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["operation"] = op,
                ["transformedCount"] = transformed,
                ["handles"] = transformedHandles
            };

            return new McpExecutionResult
            {
                ToolName = "cad_entity_transform",
                Json = CadJson.Serialize(res),
                MutatesDrawing = transformed > 0
            };
        }

        private static McpExecutionResult HandleCopy(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_copy", "At least one valid entity handle is required.");

            double dx = CadJson.GetDouble(args, "dx");
            double dy = CadJson.GetDouble(args, "dy");
            double dz = CadJson.GetDouble(args, "dz", 0.0);
            int count = Math.Max(1, Math.Min(100, CadJson.GetInt(args, "count", 1)));

            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var createdHandles = new List<string>();
            for (int k = 1; k <= count; k++)
            {
                Matrix3d mat = Matrix3d.Displacement(new Vector3d(dx * k, dy * k, dz * k));
                foreach (Entity src in sources)
                {
                    var clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    clone.TransformBy(mat);
                    space.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    createdHandles.Add(clone.Handle.ToString());
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_copy",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = createdHandles.Count > 0,
                    ["copiedCount"] = createdHandles.Count,
                    ["handles"] = createdHandles
                }),
                MutatesDrawing = createdHandles.Count > 0
            };
        }

        private static McpExecutionResult HandleMirror(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            bool eraseSource = CadJson.GetBool(args, "erase_source", false);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, eraseSource ? OpenMode.ForWrite : OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_mirror", "At least one valid entity handle is required.");

            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            var p1 = new Point3d(x1, y1, 0);
            var p2 = new Point3d(x2, y2, 0);
            if (p1.DistanceTo(p2) < 1e-6)
                return FailEntity("cad_entity_mirror", "Mirror axis points cannot coincide.");

            Matrix3d mirrorMat = Matrix3d.Mirroring(new Line3d(p1, p2));
            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var resultHandles = new List<string>();

            foreach (Entity src in sources)
            {
                if (eraseSource)
                {
                    src.TransformBy(mirrorMat);
                    resultHandles.Add(src.Handle.ToString());
                }
                else
                {
                    var clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    clone.TransformBy(mirrorMat);
                    space.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    resultHandles.Add(clone.Handle.ToString());
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_mirror",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = resultHandles.Count > 0,
                    ["mirroredCount"] = resultHandles.Count,
                    ["handles"] = resultHandles
                }),
                MutatesDrawing = resultHandles.Count > 0
            };
        }

        private static McpExecutionResult HandleOffset(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_offset", "At least one valid curve handle is required.");

            double dist = CadJson.GetDouble(args, "distance");
            if (Math.Abs(dist) < 1e-6)
                return FailEntity("cad_entity_offset", "Offset distance cannot be 0.");

            bool hasSide = args.ContainsKey("side_x") && args.ContainsKey("side_y");
            Point3d sidePt = hasSide ? new Point3d(CadJson.GetDouble(args, "side_x"), CadJson.GetDouble(args, "side_y"), 0) : Point3d.Origin;

            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var createdHandles = new List<string>();

            foreach (Entity ent in sources)
            {
                if (!(ent is Curve curve)) continue;
                double signedDist = dist;
                if (hasSide)
                {
                    signedDist = PickOffsetSignTowardSide(curve, Math.Abs(dist), sidePt);
                }

                try
                {
                    DBObjectCollection offsets = curve.GetOffsetCurves(signedDist);
                    foreach (DBObject obj in offsets)
                    {
                        if (obj is Entity offsetEnt)
                        {
                            space.AppendEntity(offsetEnt);
                            tr.AddNewlyCreatedDBObject(offsetEnt, true);
                            createdHandles.Add(offsetEnt.Handle.ToString());
                        }
                    }
                }
                catch { }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_offset",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = createdHandles.Count > 0,
                    ["offsetCount"] = createdHandles.Count,
                    ["handles"] = createdHandles
                }),
                MutatesDrawing = createdHandles.Count > 0
            };
        }

        private static double PickOffsetSignTowardSide(Curve curve, double absDist, Point3d sidePt)
        {
            double BestDist(double candidateDist)
            {
                try
                {
                    DBObjectCollection res = curve.GetOffsetCurves(candidateDist);
                    double minD = double.MaxValue;
                    foreach (DBObject o in res)
                    {
                        if (o is Curve c)
                        {
                            double midParam = (c.StartParam + c.EndParam) * 0.5;
                            double d = c.GetPointAtParameter(midParam).DistanceTo(sidePt);
                            if (d < minD) minD = d;
                        }
                        o.Dispose();
                    }
                    return minD;
                }
                catch { return double.MaxValue; }
            }

            double dPos = BestDist(absDist);
            double dNeg = BestDist(-absDist);
            return dNeg < dPos ? -absDist : absDist;
        }

        private static McpExecutionResult HandleArrayRectangular(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_array_rectangular", "At least one valid entity handle is required.");

            int rows = Math.Max(1, Math.Min(50, CadJson.GetInt(args, "rows", 1)));
            int cols = Math.Max(1, Math.Min(50, CadJson.GetInt(args, "columns", 1)));
            double rowSpacing = CadJson.GetDouble(args, "row_spacing");
            double colSpacing = CadJson.GetDouble(args, "column_spacing");
            if (rows * cols > 500)
                return FailEntity("cad_entity_array_rectangular", "Total grid cells rows * columns exceeds limit of 500.");

            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var createdHandles = new List<string>();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (r == 0 && c == 0) continue;
                    Matrix3d mat = Matrix3d.Displacement(new Vector3d(c * colSpacing, r * rowSpacing, 0));
                    foreach (Entity src in sources)
                    {
                        var clone = src.Clone() as Entity;
                        if (clone == null) continue;
                        clone.TransformBy(mat);
                        space.AppendEntity(clone);
                        tr.AddNewlyCreatedDBObject(clone, true);
                        createdHandles.Add(clone.Handle.ToString());
                    }
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_array_rectangular",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["createdCount"] = createdHandles.Count,
                    ["handles"] = createdHandles
                }),
                MutatesDrawing = createdHandles.Count > 0
            };
        }

        private static McpExecutionResult HandleArrayPolar(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_array_polar", "At least one valid entity handle is required.");

            double cx = CadJson.GetDouble(args, "center_x");
            double cy = CadJson.GetDouble(args, "center_y");
            int count = Math.Max(2, Math.Min(200, CadJson.GetInt(args, "count", 2)));
            double totalDeg = CadJson.GetDouble(args, "total_angle_degrees", 360.0);
            bool rotateItems = CadJson.GetBool(args, "rotate_items", true);

            var center = new Point3d(cx, cy, 0);
            bool fullCircle = Math.Abs(Math.Abs(totalDeg) - 360.0) < 1e-4;
            double stepRad = (totalDeg * Math.PI / 180.0) / (fullCircle ? count : (count - 1));

            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var createdHandles = new List<string>();
            for (int i = 1; i < count; i++)
            {
                double theta = stepRad * i;
                Matrix3d mat = Matrix3d.Rotation(theta, Vector3d.ZAxis, center);
                foreach (Entity src in sources)
                {
                    var clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    if (rotateItems)
                    {
                        clone.TransformBy(mat);
                    }
                    else
                    {
                        var ext = clone.GeometricExtents;
                        Point3d mid = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * 0.5, (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5, 0);
                        Point3d target = mid.TransformBy(mat);
                        clone.TransformBy(Matrix3d.Displacement(target - mid));
                    }
                    space.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    createdHandles.Add(clone.Handle.ToString());
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_array_polar",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = createdHandles.Count > 0,
                    ["createdCount"] = createdHandles.Count,
                    ["handles"] = createdHandles
                }),
                MutatesDrawing = createdHandles.Count > 0
            };
        }

        private static McpExecutionResult HandleFilletChamfer(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string h1 = CadJson.GetString(args, "handle1");
            string h2 = CadJson.GetString(args, "handle2");
            string mode = CadJson.GetString(args, "mode", "fillet").Trim().ToLowerInvariant();
            double radius = CadJson.GetDouble(args, "radius", 0.0);
            double dist1 = CadJson.GetDouble(args, "distance1", 0.0);
            double dist2 = CadJson.GetDouble(args, "distance2", dist1);
            bool trim = CadJson.GetBool(args, "trim", true);

            var db = doc.Database;
            if (!TryResolveEntity(db, tr, h1, OpenMode.ForWrite, out Entity e1) || !(e1 is Line l1) ||
                !TryResolveEntity(db, tr, h2, OpenMode.ForWrite, out Entity e2) || !(e2 is Line l2))
            {
                return FailEntity("cad_entity_fillet_chamfer", "handle1 and handle2 must reference existing Line entities.");
            }

            var ptA1 = new Point2d(l1.StartPoint.X, l1.StartPoint.Y);
            var ptA2 = new Point2d(l1.EndPoint.X, l1.EndPoint.Y);
            var ptB1 = new Point2d(l2.StartPoint.X, l2.StartPoint.Y);
            var ptB2 = new Point2d(l2.EndPoint.X, l2.EndPoint.Y);

            if (!TryIntersectLines2D(ptA1, ptA2, ptB1, ptB2, out Point2d inter))
                return FailEntity("cad_entity_fillet_chamfer", "Lines are parallel or collinear and have no unique intersection.");

            // 选择远离交点的保留端作为方向参考
            bool l1KeepStart = ptA1.GetDistanceTo(inter) >= ptA2.GetDistanceTo(inter);
            Point2d keep1 = l1KeepStart ? ptA1 : ptA2;
            Vector2d u1 = (keep1 - inter).GetNormal();

            bool l2KeepStart = ptB1.GetDistanceTo(inter) >= ptB2.GetDistanceTo(inter);
            Point2d keep2 = l2KeepStart ? ptB1 : ptB2;
            Vector2d u2 = (keep2 - inter).GetNormal();

            double setback1, setback2;
            if (mode == "chamfer")
            {
                if (dist1 < 0 || dist2 < 0)
                    return FailEntity("cad_entity_fillet_chamfer", "Chamfer distances cannot be negative.");
                setback1 = dist1;
                setback2 = dist2;
            }
            else
            {
                if (radius < 0)
                    return FailEntity("cad_entity_fillet_chamfer", "Fillet radius cannot be negative.");
                double cosTheta = Math.Max(-0.9999, Math.Min(0.9999, u1.DotProduct(u2)));
                double halfAngle = Math.Acos(cosTheta) * 0.5;
                double tanHalf = Math.Tan(halfAngle);
                double setback = radius <= 1e-6 || tanHalf < 1e-6 ? 0.0 : radius / tanHalf;
                setback1 = setback;
                setback2 = setback;
            }

            Point2d cut1 = inter + u1 * setback1;
            Point2d cut2 = inter + u2 * setback2;

            if (trim)
            {
                if (l1KeepStart) l1.EndPoint = new Point3d(cut1.X, cut1.Y, l1.EndPoint.Z);
                else l1.StartPoint = new Point3d(cut1.X, cut1.Y, l1.StartPoint.Z);

                if (l2KeepStart) l2.EndPoint = new Point3d(cut2.X, cut2.Y, l2.EndPoint.Z);
                else l2.StartPoint = new Point3d(cut2.X, cut2.Y, l2.StartPoint.Z);
            }

            string createdHandle = null;
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            if (mode == "chamfer" && (setback1 > 1e-6 || setback2 > 1e-6))
            {
                var bevel = new Line(new Point3d(cut1.X, cut1.Y, 0), new Point3d(cut2.X, cut2.Y, 0));
                bevel.SetDatabaseDefaults(db);
                bevel.Layer = l1.Layer;
                space.AppendEntity(bevel);
                tr.AddNewlyCreatedDBObject(bevel, true);
                createdHandle = bevel.Handle.ToString();
            }
            else if (mode != "chamfer" && radius > 1e-6)
            {
                double cross = u1.X * u2.Y - u1.Y * u2.X;
                Vector2d bisector = (u1 + u2).GetNormal();
                double sinHalf = Math.Sqrt(Math.Max(1e-8, 0.5 * (1.0 - Math.Max(-0.9999, Math.Min(0.9999, u1.DotProduct(u2))))));
                Point2d center = inter + bisector * (radius / sinHalf);

                double a1 = Math.Atan2(cut1.Y - center.Y, cut1.X - center.X);
                double a2 = Math.Atan2(cut2.Y - center.Y, cut2.X - center.X);
                double startA = cross >= 0 ? a1 : a2;
                double endA = cross >= 0 ? a2 : a1;
                var arc = new Arc(new Point3d(center.X, center.Y, 0), Vector3d.ZAxis, radius, startA, endA);
                arc.SetDatabaseDefaults(db);
                arc.Layer = l1.Layer;
                space.AppendEntity(arc);
                tr.AddNewlyCreatedDBObject(arc, true);
                createdHandle = arc.Handle.ToString();
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_fillet_chamfer",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["mode"] = mode,
                    ["connectorHandle"] = createdHandle ?? string.Empty
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleExplode(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            bool eraseSource = CadJson.GetBool(args, "erase_source", true);
            List<Entity> sources = ResolveEntities(args, doc.Database, tr, eraseSource ? OpenMode.ForWrite : OpenMode.ForRead);
            if (sources.Count == 0)
                return FailEntity("cad_entity_explode", "At least one valid entity handle is required.");

            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var createdHandles = new List<string>();
            int explodedEntities = 0;

            foreach (Entity src in sources)
            {
                var pieces = new DBObjectCollection();
                try { src.Explode(pieces); }
                catch { continue; }
                if (pieces.Count == 0) continue;

                foreach (DBObject obj in pieces)
                {
                    if (obj is Entity piece)
                    {
                        space.AppendEntity(piece);
                        tr.AddNewlyCreatedDBObject(piece, true);
                        createdHandles.Add(piece.Handle.ToString());
                    }
                }
                if (eraseSource) src.Erase();
                explodedEntities++;
            }

            return new McpExecutionResult
            {
                ToolName = "cad_entity_explode",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = createdHandles.Count > 0,
                    ["explodedSourceCount"] = explodedEntities,
                    ["createdCount"] = createdHandles.Count,
                    ["handles"] = createdHandles
                }),
                MutatesDrawing = createdHandles.Count > 0
            };
        }

        internal static bool TryParseSpatialBounds(
            Dictionary<string, object> args,
            out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0.0;
            bool any = args.ContainsKey("min_x") || args.ContainsKey("min_y")
                || args.ContainsKey("max_x") || args.ContainsKey("max_y");
            if (!any) return false;

            minX = args.ContainsKey("min_x") ? CadJson.GetDouble(args, "min_x") : double.NegativeInfinity;
            minY = args.ContainsKey("min_y") ? CadJson.GetDouble(args, "min_y") : double.NegativeInfinity;
            maxX = args.ContainsKey("max_x") ? CadJson.GetDouble(args, "max_x") : double.PositiveInfinity;
            maxY = args.ContainsKey("max_y") ? CadJson.GetDouble(args, "max_y") : double.PositiveInfinity;
            return true;
        }

        internal static bool EntityOverlapsBounds(
            Entity ent, double minX, double minY, double maxX, double maxY)
        {
            try
            {
                Extents3d ext = ent.GeometricExtents;
                return ext.MaxPoint.X >= minX && ext.MinPoint.X <= maxX
                    && ext.MaxPoint.Y >= minY && ext.MinPoint.Y <= maxY;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveEntity(Database db, Transaction tr, string hex, OpenMode mode, out Entity entity)
        {
            entity = null;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal)) return false;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull) return false;
            entity = tr.GetObject(id, mode) as Entity;
            return entity != null && !entity.IsErased;
        }

        private static List<Entity> ResolveEntities(Dictionary<string, object> args, Database db, Transaction tr, OpenMode mode)
        {
            var result = new List<Entity>();
            if (!args.TryGetValue("handles", out object raw) || !(raw is System.Collections.IEnumerable seq))
                return result;

            foreach (object item in seq)
            {
                if (TryResolveEntity(db, tr, Convert.ToString(item), mode, out Entity ent))
                    result.Add(ent);
            }
            return result;
        }

        private static bool TryIntersectLines2D(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d inter)
        {
            inter = Point2d.Origin;
            double dx1 = a2.X - a1.X, dy1 = a2.Y - a1.Y;
            double dx2 = b2.X - b1.X, dy2 = b2.Y - b1.Y;
            double denom = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(denom) < 1e-8) return false;

            double t = ((b1.X - a1.X) * dy2 - (b1.Y - a1.Y) * dx2) / denom;
            inter = new Point2d(a1.X + t * dx1, a1.Y + t * dy1);
            return true;
        }

        private static McpExecutionResult FailEntity(string tool, string message)
            => new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
    }
}
