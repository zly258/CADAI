using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class DimensionService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_dimension_linear_create", HandleLinearCreate,
                CadSchema.Function("cad_dimension_linear_create",
                    "Create a horizontal or vertical linear dimension between two points.",
                    CadSchema.Obj(
                        CadSchema.Num("x1", "First extension line origin X.", true),
                        CadSchema.Num("y1", "First extension line origin Y.", true),
                        CadSchema.Num("x2", "Second extension line origin X.", true),
                        CadSchema.Num("y2", "Second extension line origin Y.", true),
                        CadSchema.Num("line_x", "Dimension line location X.", true),
                        CadSchema.Num("line_y", "Dimension line location Y.", true),
                        CadSchema.Bool("is_vertical", "True for vertical dimension (90 deg), false for horizontal (0 deg). Default false."),
                        CadSchema.Str("text_override", "Optional explicit text override (default <> for measured distance)."),
                        CadSchema.Str("layer", "Optional target layer (defaults to current layer)."))));

            registry.Register("cad_dimension_aligned_create", HandleAlignedCreate,
                CadSchema.Function("cad_dimension_aligned_create",
                    "Create an aligned dimension parallel to the baseline between two points.",
                    CadSchema.Obj(
                        CadSchema.Num("x1", "First point X.", true),
                        CadSchema.Num("y1", "First point Y.", true),
                        CadSchema.Num("x2", "Second point X.", true),
                        CadSchema.Num("y2", "Second point Y.", true),
                        CadSchema.Num("line_x", "Dimension line point X.", true),
                        CadSchema.Num("line_y", "Dimension line point Y.", true),
                        CadSchema.Str("text_override", "Optional text override."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_dimension_style_create", HandleStyleCreate,
                CadSchema.Function("cad_dimension_style_create",
                    "Create a dimension style with optional text height, arrow size, extension line offset, decimal places, overall scale and text style.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "New dimension style name.", true),
                        CadSchema.Num("text_height", "Dimension text height (Dimtxt)."),
                        CadSchema.Num("arrow_size", "Arrow size (Dimasz)."),
                        CadSchema.Num("extension_offset", "Extension line offset (Dimexo)."),
                        CadSchema.Int("decimal_places", "Decimal places (Dimdec), 0-8."),
                        CadSchema.Num("scale", "Overall dimension scale (Dimscale)."),
                        CadSchema.Str("text_style", "Text style name for the dimension text."))));

            registry.Register("cad_dimension_style_set", HandleStyleSet,
                CadSchema.Function("cad_dimension_style_set",
                    "Modify an existing dimension style. Only supplied fields are changed.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Existing dimension style name.", true),
                        CadSchema.Num("text_height", "Dimension text height (Dimtxt)."),
                        CadSchema.Num("arrow_size", "Arrow size (Dimasz)."),
                        CadSchema.Num("extension_offset", "Extension line offset (Dimexo)."),
                        CadSchema.Int("decimal_places", "Decimal places (Dimdec), 0-8."),
                        CadSchema.Num("scale", "Overall dimension scale (Dimscale)."),
                        CadSchema.Str("text_style", "Text style name for the dimension text."))));

            registry.Register("cad_dimension_diametric_create", HandleDiametricCreate,
                CadSchema.Function("cad_dimension_diametric_create",
                    "Create a diametric dimension for a circle or arc by two chord points.",
                    CadSchema.Obj(
                        CadSchema.Num("chord_x", "First chord point X on the circle/arc.", true),
                        CadSchema.Num("chord_y", "First chord point Y on the circle/arc.", true),
                        CadSchema.Num("far_chord_x", "Opposite chord point X.", true),
                        CadSchema.Num("far_chord_y", "Opposite chord point Y.", true),
                        CadSchema.Num("leader_length", "Leader length (optional, default 0)."),
                        CadSchema.Str("text_override", "Optional explicit text override (default <> for measured diameter)."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_dimension_radial_create", HandleRadialCreate,
                CadSchema.Function("cad_dimension_radial_create",
                    "Create a radial dimension for an arc or circle by center and chord point.",
                    CadSchema.Obj(
                        CadSchema.Num("center_x", "Center point X.", true),
                        CadSchema.Num("center_y", "Center point Y.", true),
                        CadSchema.Num("chord_x", "Chord point X on the circle/arc.", true),
                        CadSchema.Num("chord_y", "Chord point Y on the circle/arc.", true),
                        CadSchema.Num("leader_length", "Leader length (optional, default 0)."),
                        CadSchema.Str("text_override", "Optional explicit text override (default <> for measured radius)."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_leader_create", HandleLeaderCreate,
                CadSchema.Function("cad_leader_create",
                    "Create an annotation multileader (MLeader) with arrow, leader line, and text content.",
                    CadSchema.Obj(
                        CadSchema.Num("arrow_x", "Arrow point X.", true),
                        CadSchema.Num("arrow_y", "Arrow point Y.", true),
                        CadSchema.Num("landing_x", "Landing point X (where text begins).", true),
                        CadSchema.Num("landing_y", "Landing point Y.", true),
                        CadSchema.Str("text", "Annotation text content.", true),
                        CadSchema.Num("text_height", "Optional text height."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_dimension_angular_create", HandleAngularCreate,
                CadSchema.Function("cad_dimension_angular_create",
                    "Create a 3-point angular dimension measuring the angle formed at center (vertex_x, vertex_y) between two ray points.",
                    CadSchema.Obj(
                        CadSchema.Num("vertex_x", "Angle vertex X.", true),
                        CadSchema.Num("vertex_y", "Angle vertex Y.", true),
                        CadSchema.Num("x1", "First ray point X.", true),
                        CadSchema.Num("y1", "First ray point Y.", true),
                        CadSchema.Num("x2", "Second ray point X.", true),
                        CadSchema.Num("y2", "Second ray point Y.", true),
                        CadSchema.Num("arc_x", "Dimension arc text/location point X.", true),
                        CadSchema.Num("arc_y", "Dimension arc text/location point Y.", true),
                        CadSchema.Str("text_override", "Optional text override."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_dimension_ordinate_create", HandleOrdinateCreate,
                CadSchema.Function("cad_dimension_ordinate_create",
                    "Create an ordinate (datum coordinate) dimension from a defining point to a leader end point.",
                    CadSchema.Obj(
                        CadSchema.Bool("using_x_axis", "True to measure X ordinate, false to measure Y ordinate (default true)."),
                        CadSchema.Num("def_x", "Feature defining point X.", true),
                        CadSchema.Num("def_y", "Feature defining point Y.", true),
                        CadSchema.Num("leader_x", "Leader end point X.", true),
                        CadSchema.Num("leader_y", "Leader end point Y.", true),
                        CadSchema.Str("text_override", "Optional text override."),
                        CadSchema.Str("layer", "Optional target layer."))));

            registry.Register("cad_dimension_chain_create", HandleChainCreate,
                CadSchema.Function("cad_dimension_chain_create",
                    "Create a collinear chain of linear dimensions along a list of points [[x1, y1], [x2, y2], ...] in one call.",
                    CadSchema.Obj(
                        CadSchema.Arr("points", "Ordered array of [x, y] feature points along the chain.", "array", true),
                        CadSchema.Num("line_x", "Shared dimension line coordinate X.", true),
                        CadSchema.Num("line_y", "Shared dimension line coordinate Y.", true),
                        CadSchema.Bool("is_vertical", "True for vertical chain, false for horizontal chain (default false)."),
                        CadSchema.Str("layer", "Optional target layer."))));
        }

        /// <summary>
        /// 图层预检：不存在的图层名直接赋值会抛 eSetLayerFailed 并回滚整个事务。
        /// </summary>
        private static McpExecutionResult ValidateLayer(
            Transaction tr, Database db, string layer, string toolName)
        {
            if (string.IsNullOrWhiteSpace(layer)) return null;
            if (LayerService.EnsureLayerExists(tr, db, layer, out string error)) return null;

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

        private static McpExecutionResult HandleLinearCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            double lx = CadJson.GetDouble(args, "line_x");
            double ly = CadJson.GetDouble(args, "line_y");
            bool isVert = CadJson.GetBool(args, "is_vertical", false);
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_linear_create");
            if (layerError != null) return layerError;

            var dim = new RotatedDimension
            {
                Rotation = isVert ? (Math.PI / 2.0) : 0.0,
                XLine1Point = new Point3d(x1, y1, 0),
                XLine2Point = new Point3d(x2, y2, 0),
                DimLinePoint = new Point3d(lx, ly, 0),
                DimensionStyle = db.Dimstyle
            };
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = dim.Handle.ToString(),
                ["measurement"] = Math.Round(dim.Measurement, 3)
            };

            return new McpExecutionResult { ToolName = "cad_dimension_linear_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleAlignedCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            double lx = CadJson.GetDouble(args, "line_x");
            double ly = CadJson.GetDouble(args, "line_y");
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_aligned_create");
            if (layerError != null) return layerError;

            var dim = new AlignedDimension
            {
                XLine1Point = new Point3d(x1, y1, 0),
                XLine2Point = new Point3d(x2, y2, 0),
                DimLinePoint = new Point3d(lx, ly, 0),
                DimensionStyle = db.Dimstyle
            };
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = dim.Handle.ToString(),
                ["measurement"] = Math.Round(dim.Measurement, 3)
            };

            return new McpExecutionResult { ToolName = "cad_dimension_aligned_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleStyleCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Fail("cad_dimension_style_create", "name is required.");

            var db = doc.Database;
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (table.Has(name)) return Fail("cad_dimension_style_create", "标注样式已存在: " + name);

            var record = new DimStyleTableRecord { Name = name };
            string error = ApplyStyleValues(record, args, db, tr);
            if (error != null) return Fail("cad_dimension_style_create", error);

            table.UpgradeOpen();
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);

            var data = new Dictionary<string, object> { ["ok"] = true, ["name"] = name, ["created"] = true };
            return new McpExecutionResult { ToolName = "cad_dimension_style_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleStyleSet(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Fail("cad_dimension_style_set", "name is required.");

            var db = doc.Database;
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            if (!table.Has(name)) return Fail("cad_dimension_style_set", "标注样式不存在: " + name);

            var record = (DimStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
            string error = ApplyStyleValues(record, args, db, tr);
            if (error != null) return Fail("cad_dimension_style_set", error);

            var data = new Dictionary<string, object> { ["ok"] = true, ["name"] = name, ["updated"] = true };
            return new McpExecutionResult { ToolName = "cad_dimension_style_set", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static string ApplyStyleValues(DimStyleTableRecord record, Dictionary<string, object> args, Database db, Transaction tr)
        {
            if (args.ContainsKey("text_height")) record.Dimtxt = Math.Max(0.0, CadJson.GetDouble(args, "text_height", record.Dimtxt));
            if (args.ContainsKey("arrow_size")) record.Dimasz = Math.Max(0.0, CadJson.GetDouble(args, "arrow_size", record.Dimasz));
            if (args.ContainsKey("extension_offset")) record.Dimexo = Math.Max(0.0, CadJson.GetDouble(args, "extension_offset", record.Dimexo));
            if (args.ContainsKey("decimal_places"))
            {
                int dec = CadJson.GetInt(args, "decimal_places", record.Dimdec);
                record.Dimdec = Math.Max(0, Math.Min(8, dec));
            }
            if (args.ContainsKey("scale")) record.Dimscale = Math.Max(0.0, CadJson.GetDouble(args, "scale", record.Dimscale));
            if (args.ContainsKey("text_style"))
            {
                string styleName = CadJson.GetString(args, "text_style");
                var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (!styles.Has(styleName)) return "文字样式不存在: " + styleName;
                record.Dimtxsty = styles[styleName];
            }
            return null;
        }

        private static bool TryResolveCircleOrArc(
            Database db, Transaction tr, Dictionary<string, object> args,
            out Point3d center, out double radius, out double angleRad)
        {
            center = Point3d.Origin;
            radius = 0.0;
            angleRad = CadJson.GetDouble(args, "angle_deg", 45.0) * Math.PI / 180.0;

            string handle = CadJson.GetString(args, "entity_handle") ?? CadJson.GetString(args, "handle");
            if (!string.IsNullOrWhiteSpace(handle))
            {
                try
                {
                    long hVal = Convert.ToInt64(handle.Trim(), 16);
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead);
                        if (ent is Circle c)
                        {
                            center = c.Center;
                            radius = c.Radius;
                            return true;
                        }
                        if (ent is Arc a)
                        {
                            center = a.Center;
                            radius = a.Radius;
                            angleRad = (a.StartAngle + a.EndAngle) / 2.0;
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (args.ContainsKey("radius"))
            {
                radius = CadJson.GetDouble(args, "radius");
                center = new Point3d(CadJson.GetDouble(args, "center_x"), CadJson.GetDouble(args, "center_y"), 0.0);
                return radius > 0.0;
            }

            return false;
        }

        private static McpExecutionResult HandleDiametricCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double cx1 = CadJson.GetDouble(args, "chord_x");
            double cy1 = CadJson.GetDouble(args, "chord_y");
            double cx2 = CadJson.GetDouble(args, "far_chord_x");
            double cy2 = CadJson.GetDouble(args, "far_chord_y");
            double leaderLen = CadJson.GetDouble(args, "leader_length", 0.0);
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            if (TryResolveCircleOrArc(db, tr, args, out Point3d center, out double radius, out double angle))
            {
                cx1 = center.X + radius * Math.Cos(angle);
                cy1 = center.Y + radius * Math.Sin(angle);
                cx2 = center.X - radius * Math.Cos(angle);
                cy2 = center.Y - radius * Math.Sin(angle);
            }

            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_diametric_create");
            if (layerError != null) return layerError;

            var dim = new DiametricDimension
            {
                ChordPoint = new Point3d(cx1, cy1, 0),
                FarChordPoint = new Point3d(cx2, cy2, 0),
                LeaderLength = leaderLen,
                DimensionStyle = db.Dimstyle
            };
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = dim.Handle.ToString(),
                ["measurement"] = Math.Round(dim.Measurement, 3)
            };
            return new McpExecutionResult { ToolName = "cad_dimension_diametric_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleRadialCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double cx = CadJson.GetDouble(args, "center_x");
            double cy = CadJson.GetDouble(args, "center_y");
            double px = CadJson.GetDouble(args, "chord_x");
            double py = CadJson.GetDouble(args, "chord_y");
            double leaderLen = CadJson.GetDouble(args, "leader_length", 0.0);
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            if (TryResolveCircleOrArc(db, tr, args, out Point3d center, out double radius, out double angle))
            {
                cx = center.X;
                cy = center.Y;
                px = cx + radius * Math.Cos(angle);
                py = cy + radius * Math.Sin(angle);
            }

            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_radial_create");
            if (layerError != null) return layerError;

            var dim = new RadialDimension
            {
                Center = new Point3d(cx, cy, 0),
                ChordPoint = new Point3d(px, py, 0),
                LeaderLength = leaderLen,
                DimensionStyle = db.Dimstyle
            };
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = dim.Handle.ToString(),
                ["measurement"] = Math.Round(dim.Measurement, 3)
            };
            return new McpExecutionResult { ToolName = "cad_dimension_radial_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleLeaderCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double ax = CadJson.GetDouble(args, "arrow_x");
            double ay = CadJson.GetDouble(args, "arrow_y");
            double lx = CadJson.GetDouble(args, "landing_x");
            double ly = CadJson.GetDouble(args, "landing_y");
            string text = CadJson.GetString(args, "text");
            if (string.IsNullOrWhiteSpace(text)) return Fail("cad_leader_create", "text is required.");
            double textHeight = CadJson.GetDouble(args, "text_height", 0.0);
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_leader_create");
            if (layerError != null) return layerError;

            var mleader = new MLeader();
            mleader.SetDatabaseDefaults(db);
            int leaderIndex = mleader.AddLeader();
            int leaderLineIndex = mleader.AddLeaderLine(leaderIndex);
            mleader.AddFirstVertex(leaderLineIndex, new Point3d(ax, ay, 0));
            mleader.AddLastVertex(leaderLineIndex, new Point3d(lx, ly, 0));

            var mtext = new MText();
            mtext.SetDatabaseDefaults(db);
            mtext.Contents = text;
            mtext.Location = new Point3d(lx, ly, 0);
            if (textHeight > 0) mtext.TextHeight = textHeight;

            mleader.MText = mtext;
            mleader.ContentType = ContentType.MTextContent;
            if (!string.IsNullOrWhiteSpace(layer)) mleader.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(mleader);
            tr.AddNewlyCreatedDBObject(mleader, true);

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = mleader.Handle.ToString(),
                ["text"] = text
            };
            return new McpExecutionResult { ToolName = "cad_leader_create", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleAngularCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double vx = CadJson.GetDouble(args, "vertex_x");
            double vy = CadJson.GetDouble(args, "vertex_y");
            double x1 = CadJson.GetDouble(args, "x1");
            double y1 = CadJson.GetDouble(args, "y1");
            double x2 = CadJson.GetDouble(args, "x2");
            double y2 = CadJson.GetDouble(args, "y2");
            double ax = CadJson.GetDouble(args, "arc_x");
            double ay = CadJson.GetDouble(args, "arc_y");
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_angular_create");
            if (layerError != null) return layerError;

            var dim = new Point3AngularDimension(
                new Point3d(vx, vy, 0),
                new Point3d(x1, y1, 0),
                new Point3d(x2, y2, 0),
                new Point3d(ax, ay, 0),
                string.IsNullOrWhiteSpace(txt) ? null : txt,
                db.Dimstyle);
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            return new McpExecutionResult
            {
                ToolName = "cad_dimension_angular_create",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = dim.Handle.ToString(),
                    ["angle_degrees"] = Math.Round(dim.Measurement * 180.0 / Math.PI, 3)
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleOrdinateCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            bool useX = CadJson.GetBool(args, "using_x_axis", true);
            double dx = CadJson.GetDouble(args, "def_x");
            double dy = CadJson.GetDouble(args, "def_y");
            double lx = CadJson.GetDouble(args, "leader_x");
            double ly = CadJson.GetDouble(args, "leader_y");
            string txt = CadJson.GetString(args, "text_override");
            string layer = CadJson.GetString(args, "layer");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_ordinate_create");
            if (layerError != null) return layerError;

            var dim = new OrdinateDimension(
                useX,
                new Point3d(dx, dy, 0),
                new Point3d(lx, ly, 0),
                string.IsNullOrWhiteSpace(txt) ? null : txt,
                db.Dimstyle);
            dim.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(txt)) dim.DimensionText = txt;
            if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);

            return new McpExecutionResult
            {
                ToolName = "cad_dimension_ordinate_create",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["handle"] = dim.Handle.ToString(),
                    ["measurement"] = Math.Round(dim.Measurement, 3),
                    ["axis"] = useX ? "X" : "Y"
                }),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult HandleChainCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("points", out object rawPts) || !(rawPts is System.Collections.IEnumerable seq))
                return Fail("cad_dimension_chain_create", "points array is required.");

            double lx = CadJson.GetDouble(args, "line_x");
            double ly = CadJson.GetDouble(args, "line_y");
            bool isVert = CadJson.GetBool(args, "is_vertical", false);
            string layer = CadJson.GetString(args, "layer");

            var pts = new List<Point3d>();
            foreach (object item in seq)
            {
                if (item is System.Collections.IList pair && pair.Count >= 2)
                {
                    pts.Add(new Point3d(Convert.ToDouble(pair[0]), Convert.ToDouble(pair[1]), 0));
                }
            }
            if (pts.Count < 2)
                return Fail("cad_dimension_chain_create", "Chain requires at least 2 points.");

            var db = doc.Database;
            var layerError = ValidateLayer(tr, db, layer, "cad_dimension_chain_create");
            if (layerError != null) return layerError;

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var handles = new List<string>();
            for (int i = 1; i < pts.Count; i++)
            {
                Point3d p1 = pts[i - 1];
                Point3d p2 = pts[i];
                double span = isVert ? Math.Abs(p2.Y - p1.Y) : Math.Abs(p2.X - p1.X);
                if (span < 1e-4) continue;

                var dim = new RotatedDimension
                {
                    Rotation = isVert ? (Math.PI / 2.0) : 0.0,
                    XLine1Point = p1,
                    XLine2Point = p2,
                    DimLinePoint = new Point3d(lx, ly, 0),
                    DimensionStyle = db.Dimstyle
                };
                dim.SetDatabaseDefaults(db);
                if (!string.IsNullOrWhiteSpace(layer)) dim.Layer = layer;
                space.AppendEntity(dim);
                tr.AddNewlyCreatedDBObject(dim, true);
                handles.Add(dim.Handle.ToString());
            }

            return new McpExecutionResult
            {
                ToolName = "cad_dimension_chain_create",
                Json = CadJson.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = handles.Count > 0,
                    ["createdCount"] = handles.Count,
                    ["handles"] = handles
                }),
                MutatesDrawing = handles.Count > 0
            };
        }

        private static McpExecutionResult Fail(string tool, string message)
        {
            return new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
        }

    }
}
