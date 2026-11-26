using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class GeometryService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_draw_line", HandleDrawLine,
                CadSchema.Function("cad_draw_line",
                    "Draw a straight line between two 3D points (x1, y1, z1) and (x2, y2, z2).",
                    CadSchema.Obj(
                        CadSchema.Num("x1", "Start point X.", true),
                        CadSchema.Num("y1", "Start point Y.", true),
                        CadSchema.Num("z1", "Start point Z (default 0)."),
                        CadSchema.Num("x2", "End point X.", true),
                        CadSchema.Num("y2", "End point Y.", true),
                        CadSchema.Num("z2", "End point Z (default 0)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_circle", HandleDrawCircle,
                CadSchema.Function("cad_draw_circle",
                    "Draw a circle with center (cx, cy, cz) and radius.",
                    CadSchema.Obj(
                        CadSchema.Num("cx", "Center point X.", true),
                        CadSchema.Num("cy", "Center point Y.", true),
                        CadSchema.Num("cz", "Center point Z (default 0)."),
                        CadSchema.Num("radius", "Radius of the circle.", true),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_polyline", HandleDrawPolyline,
                CadSchema.Function("cad_draw_polyline",
                    "Draw a lightweight 2D polyline through a list of 2D points [[x1, y1], [x2, y2], ...].",
                    CadSchema.Obj(
                        CadSchema.Arr("points", "Array of [x, y] coordinates, e.g. [[0,0], [100,0], [100,50]].", "array", true),
                        CadSchema.Bool("closed", "Whether the polyline should be closed (default false)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_text", HandleDrawText,
                CadSchema.Function("cad_draw_text",
                    "Insert text at position (x, y, z). Supports single-line (DBText) or multiline (MText).",
                    CadSchema.Obj(
                        CadSchema.Str("text", "Text content to insert.", true),
                        CadSchema.Num("x", "Insertion point X.", true),
                        CadSchema.Num("y", "Insertion point Y.", true),
                        CadSchema.Num("z", "Insertion point Z (default 0)."),
                        CadSchema.Num("height", "Text height in mm (default 3.5)."),
                        CadSchema.Bool("is_mtext", "True for MText, false for DBText (default true)."),
                        CadSchema.Str("style", "Optional text style name."),
                        CadSchema.Num("rotation", "Rotation in degrees (default 0)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_hatch", HandleDrawHatch,
                CadSchema.Function("cad_draw_hatch",
                    "Create an associative hatch on a closed boundary entity (polyline, circle, ellipse or closed spline).",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Hex handle of the closed boundary entity.", true),
                        CadSchema.Str("pattern", "Predefined hatch pattern name (default 'ANSI31')."),
                        CadSchema.Num("scale", "Pattern scale (default 1.0)."),
                        CadSchema.Num("angle", "Pattern angle in degrees (default 0)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_arc", HandleDrawArc,
                CadSchema.Function("cad_draw_arc",
                    "Draw a circular arc by center (cx, cy, cz), radius, start_angle_degrees, and end_angle_degrees (CCW from +X).",
                    CadSchema.Obj(
                        CadSchema.Num("cx", "Center point X.", true),
                        CadSchema.Num("cy", "Center point Y.", true),
                        CadSchema.Num("cz", "Center point Z (default 0)."),
                        CadSchema.Num("radius", "Arc radius.", true),
                        CadSchema.Num("start_angle_degrees", "Start angle in degrees.", true),
                        CadSchema.Num("end_angle_degrees", "End angle in degrees.", true),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_rectangle", HandleDrawRectangle,
                CadSchema.Function("cad_draw_rectangle",
                    "Draw a closed rectangular 2D polyline either from opposite corners (x1, y1)-(x2, y2) or center+size (cx, cy, width, height).",
                    CadSchema.Obj(
                        CadSchema.Num("x1", "First corner X."),
                        CadSchema.Num("y1", "First corner Y."),
                        CadSchema.Num("x2", "Opposite corner X."),
                        CadSchema.Num("y2", "Opposite corner Y."),
                        CadSchema.Num("cx", "Center X (when using width/height)."),
                        CadSchema.Num("cy", "Center Y (when using width/height)."),
                        CadSchema.Num("width", "Rectangle width (used with cx, cy, height)."),
                        CadSchema.Num("height", "Rectangle height (used with cx, cy, width)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_ellipse", HandleDrawEllipse,
                CadSchema.Function("cad_draw_ellipse",
                    "Draw a full ellipse or elliptical arc by center, major_radius, minor_radius, and optional rotation.",
                    CadSchema.Obj(
                        CadSchema.Num("cx", "Center point X.", true),
                        CadSchema.Num("cy", "Center point Y.", true),
                        CadSchema.Num("cz", "Center point Z (default 0)."),
                        CadSchema.Num("major_radius", "Semi-major axis length.", true),
                        CadSchema.Num("minor_radius", "Semi-minor axis length.", true),
                        CadSchema.Num("rotation_degrees", "Major axis rotation angle in degrees (default 0)."),
                        CadSchema.Num("start_angle_degrees", "Start parameter angle in degrees (default 0)."),
                        CadSchema.Num("end_angle_degrees", "End parameter angle in degrees (default 360)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_spline", HandleDrawSpline,
                CadSchema.Function("cad_draw_spline",
                    "Draw a smooth spline through a list of 2D fit points [[x1, y1], [x2, y2], ...].",
                    CadSchema.Obj(
                        CadSchema.Arr("points", "Array of [x, y] fit points (at least 2).", "array", true),
                        CadSchema.Bool("closed", "Whether to close the spline smoothly (default false)."),
                        CadSchema.Int("degree", "Spline degree (default 3)."),
                        CadSchema.Str("layer", "Optional target layer name."))));

            registry.Register("cad_draw_centerline", HandleDrawCenterline,
                CadSchema.Function("cad_draw_centerline",
                    "Draw an engineering centerline with dashed CENTER linetype and optional endpoint extension.",
                    CadSchema.Obj(
                        CadSchema.Num("x1", "Start point X.", true),
                        CadSchema.Num("y1", "Start point Y.", true),
                        CadSchema.Num("x2", "End point X.", true),
                        CadSchema.Num("y2", "End point Y.", true),
                        CadSchema.Num("extension", "Optional extension beyond both endpoints in mm (default 0)."),
                        CadSchema.Str("linetype", "Linetype name (default 'CENTER')."),
                        CadSchema.Int("color_index", "Optional ACI color index (default 1 = red)."),
                        CadSchema.Str("layer", "Optional target layer name."))));
        }

        private static BlockTableRecord GetCurrentSpace(Document doc, Transaction tr)
        {
            var db = doc.Database;
            return (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        }

        /// <summary>
        /// 图层预检：不存在的图层名直接赋值会抛 eSetLayerFailed 并回滚整个事务。
        /// 提前返回结构化错误，模型能自己决定先建层再重试。
        /// </summary>
        private static McpExecutionResult ValidateLayer(
            Document doc, Transaction tr, string layer, string toolName)
        {
            if (string.IsNullOrWhiteSpace(layer)) return null;
            if (LayerService.EnsureLayerExists(tr, doc.Database, layer, out string error)) return null;

            var data = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = error,
                ["error_type"] = "layer_not_found"
            };
            return new McpExecutionResult
            {
                ToolName = toolName,
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleDrawLine(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double z1 = CadJson.GetDouble(args, "z1", 0.0);
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            double z2 = CadJson.GetDouble(args, "z2", 0.0);
            string layer = CadJson.GetString(args, "layer");

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_line");
            if (layerError != null) return layerError;

            var line = new Line(new Point3d(x1, y1, z1), new Point3d(x2, y2, z2));
            line.SetDatabaseDefaults(doc.Database);
            if (!string.IsNullOrWhiteSpace(layer)) line.Layer = layer;

            var space = GetCurrentSpace(doc, tr);
            ObjectId id = space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = line.Handle.ToString(),
                ["length"] = Math.Round(line.Length, 3)
            };

            return new McpExecutionResult { ToolName = "cad_draw_line", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleDrawCircle(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double cx = CadJson.GetDouble(args, "cx");
            double cy = CadJson.GetDouble(args, "cy");
            double cz = CadJson.GetDouble(args, "cz", 0.0);
            double radius = CadJson.GetDouble(args, "radius");
            string layer = CadJson.GetString(args, "layer");

            if (radius <= 0)
                return new McpExecutionResult { ToolName = "cad_draw_circle", Json = "{\"ok\":false,\"error\":\"Radius must be > 0.\"}" };

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_circle");
            if (layerError != null) return layerError;

            var circle = new Circle(new Point3d(cx, cy, cz), Vector3d.ZAxis, radius);
            circle.SetDatabaseDefaults(doc.Database);
            if (!string.IsNullOrWhiteSpace(layer)) circle.Layer = layer;

            var space = GetCurrentSpace(doc, tr);
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = circle.Handle.ToString(),
                ["radius"] = radius,
                ["area"] = Math.Round(circle.Area, 3)
            };

            return new McpExecutionResult { ToolName = "cad_draw_circle", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleDrawPolyline(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("points", out var ptsRaw) || !(ptsRaw is System.Collections.IEnumerable ptList))
                return new McpExecutionResult { ToolName = "cad_draw_polyline", Json = "{\"ok\":false,\"error\":\"points array is required.\"}" };

            bool closed = CadJson.GetBool(args, "closed", false);
            string layer = CadJson.GetString(args, "layer");

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_polyline");
            if (layerError != null) return layerError;

            var pline = new Polyline();
            pline.SetDatabaseDefaults(doc.Database);
            if (!string.IsNullOrWhiteSpace(layer)) pline.Layer = layer;

            int idx = 0;
            foreach (var item in ptList)
            {
                if (item is System.Collections.IList coord && coord.Count >= 2)
                {
                    double x = Convert.ToDouble(coord[0]);
                    double y = Convert.ToDouble(coord[1]);
                    pline.AddVertexAt(idx++, new Point2d(x, y), 0, 0, 0);
                }
            }

            if (pline.NumberOfVertices < 2)
                return new McpExecutionResult { ToolName = "cad_draw_polyline", Json = "{\"ok\":false,\"error\":\"Polyline requires at least 2 valid points.\"}" };

            pline.Closed = closed;
            var space = GetCurrentSpace(doc, tr);
            space.AppendEntity(pline);
            tr.AddNewlyCreatedDBObject(pline, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = pline.Handle.ToString(),
                ["vertexCount"] = pline.NumberOfVertices,
                ["length"] = Math.Round(pline.Length, 3)
            };

            return new McpExecutionResult { ToolName = "cad_draw_polyline", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleDrawText(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string text = CadJson.GetString(args, "text");
            double x = CadJson.GetDouble(args, "x");
            double y = CadJson.GetDouble(args, "y");
            double z = CadJson.GetDouble(args, "z", 0.0);
            double height = CadJson.GetDouble(args, "height", 3.5);
            bool isMText = CadJson.GetBool(args, "is_mtext", true);
            string styleName = CadJson.GetString(args, "style");
            double rotation = CadJson.GetDouble(args, "rotation", 0.0) * Math.PI / 180.0;
            string layer = CadJson.GetString(args, "layer");

            if (string.IsNullOrEmpty(text))
                return new McpExecutionResult { ToolName = "cad_draw_text", Json = "{\"ok\":false,\"error\":\"Text cannot be empty.\"}" };

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_text");
            if (layerError != null) return layerError;

            ObjectId styleId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(styleName))
            {
                var styles = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                if (!styles.Has(styleName))
                    return new McpExecutionResult { ToolName = "cad_draw_text", Json = "{\"ok\":false,\"error\":\"文字样式不存在: " + styleName + "\"}" };
                styleId = styles[styleName];
            }

            var space = GetCurrentSpace(doc, tr);
            string handleStr = "";

            if (isMText)
            {
                var mt = new MText();
                mt.SetDatabaseDefaults(doc.Database);
                mt.Location = new Point3d(x, y, z);
                mt.TextHeight = Math.Max(0.1, height);
                mt.Contents = text;
                if (!styleId.IsNull) mt.TextStyleId = styleId;
                mt.Rotation = rotation;
                if (!string.IsNullOrWhiteSpace(layer)) mt.Layer = layer;
                space.AppendEntity(mt);
                tr.AddNewlyCreatedDBObject(mt, true);
                handleStr = mt.Handle.ToString();
            }
            else
            {
                var dt = new DBText();
                dt.SetDatabaseDefaults(doc.Database);
                dt.Position = new Point3d(x, y, z);
                dt.Height = Math.Max(0.1, height);
                dt.TextString = text;
                if (!styleId.IsNull) dt.TextStyleId = styleId;
                dt.Rotation = rotation;
                if (!string.IsNullOrWhiteSpace(layer)) dt.Layer = layer;
                space.AppendEntity(dt);
                tr.AddNewlyCreatedDBObject(dt, true);
                handleStr = dt.Handle.ToString();
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = handleStr,
                ["isMText"] = isMText
            };

            return new McpExecutionResult { ToolName = "cad_draw_text", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleDrawHatch(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hex = CadJson.GetString(args, "handle");
            if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                return new McpExecutionResult { ToolName = "cad_draw_hatch", Json = "{\"ok\":false,\"error\":\"无效的十六进制句柄。\"}" };

            var db = doc.Database;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                return new McpExecutionResult { ToolName = "cad_draw_hatch", Json = "{\"ok\":false,\"error\":\"未找到边界图元。\"}" };

            var boundary = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (boundary == null)
                return new McpExecutionResult { ToolName = "cad_draw_hatch", Json = "{\"ok\":false,\"error\":\"该句柄不是图元。\"}" };

            string pattern = CadJson.GetString(args, "pattern");
            if (string.IsNullOrWhiteSpace(pattern)) pattern = "ANSI31";
            double scale = CadJson.GetDouble(args, "scale", 1.0);
            double angle = CadJson.GetDouble(args, "angle", 0.0) * Math.PI / 180.0;
            string layer = CadJson.GetString(args, "layer");

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_hatch");
            if (layerError != null) return layerError;

            var space = GetCurrentSpace(doc, tr);
            var hatch = new Hatch();
            space.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);
            hatch.SetDatabaseDefaults(db);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            hatch.Associative = true;
            hatch.PatternScale = Math.Max(0.0001, scale);
            hatch.PatternAngle = angle;
            if (!string.IsNullOrWhiteSpace(layer)) hatch.Layer = layer;
            hatch.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { id });
            hatch.EvaluateHatch(true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = hatch.Handle.ToString(),
                ["pattern"] = pattern,
                ["boundary"] = hex
            };
            return new McpExecutionResult { ToolName = "cad_draw_hatch", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleDrawArc(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double cx = CadJson.GetDouble(args, "cx");
            double cy = CadJson.GetDouble(args, "cy");
            double cz = CadJson.GetDouble(args, "cz", 0.0);
            double r = CadJson.GetDouble(args, "radius");
            double startDeg = CadJson.GetDouble(args, "start_angle_degrees");
            double endDeg = CadJson.GetDouble(args, "end_angle_degrees");
            string layer = CadJson.GetString(args, "layer");

            if (r <= 0.0)
                return new McpExecutionResult { ToolName = "cad_draw_arc", Json = "{\"ok\":false,\"error\":\"Arc radius must be > 0.\"}" };

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_arc");
            if (layerError != null) return layerError;

            var db = doc.Database;
            var space = GetCurrentSpace(doc, tr);
            var arc = new Arc(
                new Point3d(cx, cy, cz),
                Vector3d.ZAxis,
                r,
                startDeg * Math.PI / 180.0,
                endDeg * Math.PI / 180.0);
            arc.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(layer)) arc.Layer = layer;

            space.AppendEntity(arc);
            tr.AddNewlyCreatedDBObject(arc, true);

            return new McpExecutionResult
            {
                ToolName = "cad_draw_arc",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = arc.Handle.ToString(),
                    ["radius"] = r,
                    ["length"] = Math.Round(arc.Length, 4)
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleDrawRectangle(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string layer = CadJson.GetString(args, "layer");
            double minX, minY, maxX, maxY;

            if (args.ContainsKey("width") && args.ContainsKey("height"))
            {
                double cx = CadJson.GetDouble(args, "cx");
                double cy = CadJson.GetDouble(args, "cy");
                double w = CadJson.GetDouble(args, "width");
                double h = CadJson.GetDouble(args, "height");
                if (w <= 0 || h <= 0)
                    return new McpExecutionResult { ToolName = "cad_draw_rectangle", Json = "{\"ok\":false,\"error\":\"width and height must be > 0.\"}" };
                minX = cx - w * 0.5; maxX = cx + w * 0.5;
                minY = cy - h * 0.5; maxY = cy + h * 0.5;
            }
            else if (args.ContainsKey("x1") && args.ContainsKey("y1") && args.ContainsKey("x2") && args.ContainsKey("y2"))
            {
                double x1 = CadJson.GetDouble(args, "x1");
                double y1 = CadJson.GetDouble(args, "y1");
                double x2 = CadJson.GetDouble(args, "x2");
                double y2 = CadJson.GetDouble(args, "y2");
                minX = Math.Min(x1, x2); maxX = Math.Max(x1, x2);
                minY = Math.Min(y1, y2); maxY = Math.Max(y1, y2);
                if (maxX - minX <= 1e-6 || maxY - minY <= 1e-6)
                    return new McpExecutionResult { ToolName = "cad_draw_rectangle", Json = "{\"ok\":false,\"error\":\"Rectangle corners must define non-zero width and height.\"}" };
            }
            else
            {
                return new McpExecutionResult { ToolName = "cad_draw_rectangle", Json = "{\"ok\":false,\"error\":\"Specify either (x1, y1, x2, y2) or (cx, cy, width, height).\"}" };
            }

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_rectangle");
            if (layerError != null) return layerError;

            var db = doc.Database;
            var space = GetCurrentSpace(doc, tr);
            var pline = new Polyline(4);
            pline.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            pline.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            pline.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            pline.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
            pline.Closed = true;
            pline.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(layer)) pline.Layer = layer;

            space.AppendEntity(pline);
            tr.AddNewlyCreatedDBObject(pline, true);

            return new McpExecutionResult
            {
                ToolName = "cad_draw_rectangle",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = pline.Handle.ToString(),
                    ["width"] = Math.Round(maxX - minX, 4),
                    ["height"] = Math.Round(maxY - minY, 4),
                    ["area"] = Math.Round(pline.Area, 4)
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleDrawEllipse(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double cx = CadJson.GetDouble(args, "cx");
            double cy = CadJson.GetDouble(args, "cy");
            double cz = CadJson.GetDouble(args, "cz", 0.0);
            double majorR = CadJson.GetDouble(args, "major_radius");
            double minorR = CadJson.GetDouble(args, "minor_radius");
            double rotDeg = CadJson.GetDouble(args, "rotation_degrees", 0.0);
            double startDeg = CadJson.GetDouble(args, "start_angle_degrees", 0.0);
            double endDeg = CadJson.GetDouble(args, "end_angle_degrees", 360.0);
            string layer = CadJson.GetString(args, "layer");

            if (majorR <= 0.0 || minorR <= 0.0)
                return new McpExecutionResult { ToolName = "cad_draw_ellipse", Json = "{\"ok\":false,\"error\":\"major_radius and minor_radius must be > 0.\"}" };

            // AutoCAD Ellipse 要求 radiusRatio <= 1.0，若 minor > major 则交换长短轴并旋转 90°
            if (minorR > majorR)
            {
                double tmp = majorR;
                majorR = minorR;
                minorR = tmp;
                rotDeg += 90.0;
            }

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_ellipse");
            if (layerError != null) return layerError;

            double rotRad = rotDeg * Math.PI / 180.0;
            var majorVec = new Vector3d(majorR * Math.Cos(rotRad), majorR * Math.Sin(rotRad), 0.0);
            double ratio = Math.Min(1.0, Math.Max(1e-6, minorR / majorR));
            double startRad = startDeg * Math.PI / 180.0;
            double endRad = endDeg * Math.PI / 180.0;

            var db = doc.Database;
            var space = GetCurrentSpace(doc, tr);
            var ellipse = new Ellipse(new Point3d(cx, cy, cz), Vector3d.ZAxis, majorVec, ratio, startRad, endRad);
            ellipse.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(layer)) ellipse.Layer = layer;

            space.AppendEntity(ellipse);
            tr.AddNewlyCreatedDBObject(ellipse, true);

            return new McpExecutionResult
            {
                ToolName = "cad_draw_ellipse",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = ellipse.Handle.ToString(),
                    ["major_radius"] = majorR,
                    ["minor_radius"] = minorR
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleDrawSpline(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("points", out object rawPts) || !(rawPts is System.Collections.IEnumerable seq))
                return new McpExecutionResult { ToolName = "cad_draw_spline", Json = "{\"ok\":false,\"error\":\"points array is required.\"}" };

            bool closed = CadJson.GetBool(args, "closed", false);
            int degree = Math.Max(1, Math.Min(5, CadJson.GetInt(args, "degree", 3)));
            string layer = CadJson.GetString(args, "layer");

            var pts = new Point3dCollection();
            foreach (object item in seq)
            {
                if (item is System.Collections.IList pair && pair.Count >= 2)
                {
                    double x = Convert.ToDouble(pair[0]);
                    double y = Convert.ToDouble(pair[1]);
                    double z = pair.Count >= 3 ? Convert.ToDouble(pair[2]) : 0.0;
                    pts.Add(new Point3d(x, y, z));
                }
            }

            if (pts.Count < 2)
                return new McpExecutionResult { ToolName = "cad_draw_spline", Json = "{\"ok\":false,\"error\":\"Spline requires at least 2 points.\"}" };

            if (closed && pts.Count >= 3 && pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-6)
                pts.Add(pts[0]);

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_spline");
            if (layerError != null) return layerError;

            int order = Math.Min(pts.Count, degree + 1);
            var db = doc.Database;
            var space = GetCurrentSpace(doc, tr);
            var spline = new Spline(pts, order, 0.0);
            spline.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(layer)) spline.Layer = layer;

            space.AppendEntity(spline);
            tr.AddNewlyCreatedDBObject(spline, true);

            return new McpExecutionResult
            {
                ToolName = "cad_draw_spline",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = spline.Handle.ToString(),
                    ["points"] = pts.Count,
                    ["closed"] = closed
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleDrawCenterline(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            double extension = Math.Max(0.0, CadJson.GetDouble(args, "extension", 0.0));
            string linetype = CadJson.GetString(args, "linetype", "CENTER");
            int colorIndex = CadJson.GetInt(args, "color_index", 1);
            string layer = CadJson.GetString(args, "layer");

            var p1 = new Point3d(x1, y1, 0);
            var p2 = new Point3d(x2, y2, 0);
            Vector3d dir = p2 - p1;
            if (dir.Length < 1e-6)
                return new McpExecutionResult { ToolName = "cad_draw_centerline", Json = "{\"ok\":false,\"error\":\"Centerline endpoints cannot coincide.\"}" };

            if (extension > 0.0)
            {
                Vector3d unit = dir.GetNormal();
                p1 = p1 - unit * extension;
                p2 = p2 + unit * extension;
            }

            var layerError = ValidateLayer(doc, tr, layer, "cad_draw_centerline");
            if (layerError != null) return layerError;

            var db = doc.Database;
            string appliedLinetype = ResolveOrLoadLinetype(db, tr, linetype);

            var line = new Line(p1, p2);
            line.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(layer)) line.Layer = layer;
            if (!string.IsNullOrWhiteSpace(appliedLinetype)) line.Linetype = appliedLinetype;
            if (colorIndex >= 1 && colorIndex <= 255)
                line.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)colorIndex);

            var space = GetCurrentSpace(doc, tr);
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            return new McpExecutionResult
            {
                ToolName = "cad_draw_centerline",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = line.Handle.ToString(),
                    ["linetype"] = line.Linetype,
                    ["length"] = Math.Round(line.Length, 4)
                }),
                MutatesDrawing = true
            };
        }

        internal static string ResolveOrLoadLinetype(Database db, Transaction tr, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return null;
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(requested)) return requested;

            foreach (string file in new[] { "acadiso.lin", "acad.lin" })
            {
                try
                {
                    db.LoadLineTypeFile(requested, file);
                    ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltt.Has(requested)) return requested;
                }
                catch { }
            }
            return null;
        }
    }
}
