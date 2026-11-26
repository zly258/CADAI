using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    internal static class TextService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_text_search", HandleSearch,
                CadSchema.Function("cad_text_search",
                    "Search drawing text (DBText and MText) matching a keyword or regex, with optional 2D spatial bounding box filters (min_x, min_y, max_x, max_y).",
                    CadSchema.Obj(
                        CadSchema.Str("pattern", "Search keyword or regex pattern.", true),
                        CadSchema.Bool("is_regex", "Whether pattern is a regular expression (default false)."),
                        CadSchema.Num("min_x", "Optional spatial filter minimum X."),
                        CadSchema.Num("min_y", "Optional spatial filter minimum Y."),
                        CadSchema.Num("max_x", "Optional spatial filter maximum X."),
                        CadSchema.Num("max_y", "Optional spatial filter maximum Y."),
                        CadSchema.Int("limit", "Max results to return (default 50)."))));

            registry.Register("cad_text_modify", HandleModify,
                CadSchema.Function("cad_text_modify",
                    "Modify the string content of a specific DBText or MText by Handle.",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Entity Handle in hex format.", true),
                        CadSchema.Str("new_text", "New text content.", true))));

            registry.Register("cad_text_batch_replace", HandleBatchReplace,
                CadSchema.Function("cad_text_batch_replace",
                    "Find and replace text across the drawing (useful for spec codes, part numbers, drawing dates).",
                    CadSchema.Obj(
                        CadSchema.Str("find", "Text to find.", true),
                        CadSchema.Str("replace", "Text to replace with.", true),
                        CadSchema.Bool("match_case", "Case sensitive (default true)."))));

            registry.Register("cad_text_merge_to_mtext", HandleMergeToMText,
                CadSchema.Function("cad_text_merge_to_mtext",
                    "Merge multiple single-line text (DBText) entities into one multiline text (MText), strictly preserving visual layout, exact line spacing, width factors, and baseline alignment.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of DBText hex Handles to merge into a single paragraph.", "string", true))));

            registry.Register("cad_text_align", HandleAlign,
                CadSchema.Function("cad_text_align",
                    "Align multiple text entities along a reference axis ('left', 'center', 'right', 'baseline') based on the first selected entity.",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of text hex Handles to align.", "string", true),
                        CadSchema.Str("mode", "Alignment mode: 'left', 'center', 'right', or 'baseline'. Default 'left'."))));

            registry.Register("cad_text_distribute", HandleDistribute,
                CadSchema.Function("cad_text_distribute",
                    "Distribute text entities evenly vertically or horizontally (e.g. for lists of technical requirements or table rows).",
                    CadSchema.Obj(
                        CadSchema.Arr("handles", "List of text hex Handles to distribute.", "string", true),
                        CadSchema.Str("direction", "'vertical' or 'horizontal'. Default 'vertical'."),
                        CadSchema.Num("spacing", "Optional fixed spacing in drawing units (mm). If <= 0, automatically evenly spaces between the first and last entity."))));
        }

        private static McpExecutionResult HandleSearch(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string pattern = CadJson.GetString(args, "pattern");
            bool isRegex = CadJson.GetBool(args, "is_regex", false);
            bool hasBounds = EntityService.TryParseSpatialBounds(args, out double minX, out double minY, out double maxX, out double maxY);
            int limit = Math.Max(1, Math.Min(200, CadJson.GetInt(args, "limit", 50)));

            if (string.IsNullOrEmpty(pattern))
                return new McpExecutionResult { ToolName = "cad_text_search", Json = "{\"ok\":false,\"error\":\"pattern is required.\"}" };

            Regex rx = null;
            if (isRegex)
            {
                try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
                catch (Exception ex) { return new McpExecutionResult { ToolName = "cad_text_search", Json = "{\"ok\":false,\"error\":\"Invalid regex: " + ex.Message.Replace("\"", "'") + "\"}" }; }
            }

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var matches = new List<object>();

            foreach (ObjectId id in ms)
            {
                if (matches.Count >= limit) break;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                string textVal = null;
                Point3d pos = Point3d.Origin;
                double height = 0;

                if (ent is DBText dt)
                {
                    textVal = dt.TextString;
                    pos = dt.Position;
                    height = dt.Height;
                }
                else if (ent is MText mt)
                {
                    textVal = mt.Text;
                    pos = mt.Location;
                    height = mt.TextHeight;
                }

                if (textVal == null) continue;
                if (hasBounds && !EntityService.EntityOverlapsBounds(ent, minX, minY, maxX, maxY)
                    && !(pos.X >= minX && pos.X <= maxX && pos.Y >= minY && pos.Y <= maxY))
                    continue;

                bool isHit = isRegex ? rx.IsMatch(textVal) : (textVal.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
                if (isHit)
                {
                    matches.Add(new Dictionary<string, object>
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer,
                        ["text"] = textVal,
                        ["height"] = Math.Round(height, 3),
                        ["position"] = new[] { Math.Round(pos.X, 3), Math.Round(pos.Y, 3), Math.Round(pos.Z, 3) }
                    });
                }
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["matchCount"] = matches.Count,
                ["results"] = matches
            };

            return new McpExecutionResult { ToolName = "cad_text_search", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleModify(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hexHandle = CadJson.GetString(args, "handle");
            string newText = CadJson.GetString(args, "new_text");

            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                return new McpExecutionResult { ToolName = "cad_text_modify", Json = "{\"ok\":false,\"error\":\"Invalid hex handle.\"}" };

            var db = doc.Database;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                return new McpExecutionResult { ToolName = "cad_text_modify", Json = "{\"ok\":false,\"error\":\"Entity not found.\"}" };

            var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
            string oldText = "";

            if (ent is DBText dt)
            {
                oldText = dt.TextString;
                dt.TextString = newText;
            }
            else if (ent is MText mt)
            {
                oldText = mt.Text;
                mt.Contents = newText;
            }
            else
            {
                return new McpExecutionResult { ToolName = "cad_text_modify", Json = "{\"ok\":false,\"error\":\"Target is not DBText or MText.\"}" };
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = hexHandle,
                ["oldText"] = oldText,
                ["newText"] = newText
            };

            return new McpExecutionResult { ToolName = "cad_text_modify", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleBatchReplace(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string find = CadJson.GetString(args, "find");
            string replace = CadJson.GetString(args, "replace");
            bool matchCase = CadJson.GetBool(args, "match_case", true);

            if (string.IsNullOrEmpty(find))
                return new McpExecutionResult { ToolName = "cad_text_batch_replace", Json = "{\"ok\":false,\"error\":\"find text cannot be empty.\"}" };

            var comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            int replacedCount = 0;

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent is DBText dt)
                {
                    if (dt.TextString != null && dt.TextString.IndexOf(find, comp) >= 0)
                    {
                        dt.UpgradeOpen();
                        dt.TextString = Regex.Replace(dt.TextString, Regex.Escape(find), replace, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                        replacedCount++;
                    }
                }
                else if (ent is MText mt)
                {
                    if (mt.Contents != null && mt.Contents.IndexOf(find, comp) >= 0)
                    {
                        mt.UpgradeOpen();
                        mt.Contents = Regex.Replace(mt.Contents, Regex.Escape(find), replace, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                        replacedCount++;
                    }
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_text_batch_replace",
                Json = "{\"ok\":true,\"replacedCount\":" + replacedCount + "}",
                MutatesDrawing = replacedCount > 0
            };
        }

        private static McpExecutionResult HandleMergeToMText(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_text_merge_to_mtext", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            var db = doc.Database;
            var dbTexts = new List<DBText>();

            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var dt = tr.GetObject(id, OpenMode.ForRead) as DBText;
                        if (dt != null && !dt.IsErased) dbTexts.Add(dt);
                    }
                }
            }

            if (dbTexts.Count < 2)
            {
                return new McpExecutionResult { ToolName = "cad_text_merge_to_mtext", Json = "{\"ok\":false,\"error\":\"At least 2 DBText entities are required to merge.\"}" };
            }

            // 按局部投影从上到下排序
            double rot = dbTexts[0].Rotation;
            Vector3d upDir = Vector3d.YAxis.RotateBy(rot, Vector3d.ZAxis);
            var sorted = dbTexts.OrderByDescending(t => t.Position.X * upDir.X + t.Position.Y * upDir.Y).ToList();

            DBText first = sorted[0];
            double textHeight = first.Height;
            double widthFactor = first.WidthFactor;
            double oblique = first.Oblique;

            // 计算实际行间距
            double totalDelta = 0;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                double d = Math.Abs((sorted[i].Position - sorted[i + 1].Position).DotProduct(upDir));
                totalDelta += d;
            }
            double avgLineSpacing = (sorted.Count > 1) ? (totalDelta / (sorted.Count - 1)) : (textHeight * 1.666667);
            double lineSpacingFactor = Math.Min(4.0, Math.Max(0.25, avgLineSpacing / (textHeight * 1.666667)));

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var mText = new MText();
            mText.SetDatabaseDefaults(db);
            mText.TextStyleId = first.TextStyleId;
            mText.TextHeight = textHeight;
            mText.Rotation = first.Rotation;
            mText.LayerId = first.LayerId;
            mText.Color = first.Color;

            // 严禁自动换行，采用硬换行 \P
            mText.Width = 0;
            mText.Attachment = AttachmentPoint.TopLeft;
            mText.LineSpacingStyle = LineSpacingStyle.Exactly;
            mText.LineSpacingFactor = lineSpacingFactor;

            // 基准点转换：Baseline 向上平移至 TopLeft
            mText.Location = first.Position + upDir * (textHeight * 0.95);

            string formatPrefix = $"\\W{widthFactor:0.###};\\Q{Math.Round(oblique * 180 / Math.PI, 1)};";
            string body = string.Join("\\P", sorted.Select(t => t.TextString));
            mText.Contents = formatPrefix + body;

            space.AppendEntity(mText);
            tr.AddNewlyCreatedDBObject(mText, true);

            // 删除原单行文本
            foreach (var dt in sorted)
            {
                dt.UpgradeOpen();
                dt.Erase();
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["newHandle"] = mText.Handle.ToString(),
                ["mergedCount"] = sorted.Count,
                ["lineSpacing"] = Math.Round(avgLineSpacing, 3)
            };

            return new McpExecutionResult { ToolName = "cad_text_merge_to_mtext", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static McpExecutionResult HandleAlign(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_text_align", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            string mode = CadJson.GetString(args, "mode", "left").ToLowerInvariant();
            var db = doc.Database;
            var entities = new List<Entity>();

            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased) entities.Add(ent);
                    }
                }
            }

            if (entities.Count < 2)
                return new McpExecutionResult { ToolName = "cad_text_align", Json = "{\"ok\":false,\"error\":\"At least 2 entities are required to align.\"}" };

            var refExt = entities[0].GeometricExtents;
            int alignedCount = 0;

            for (int i = 1; i < entities.Count; i++)
            {
                var cur = entities[i];
                var curExt = cur.GeometricExtents;
                double dx = 0, dy = 0;

                switch (mode)
                {
                    case "left":
                        dx = refExt.MinPoint.X - curExt.MinPoint.X;
                        break;
                    case "center":
                        dx = ((refExt.MinPoint.X + refExt.MaxPoint.X) / 2.0) - ((curExt.MinPoint.X + curExt.MaxPoint.X) / 2.0);
                        break;
                    case "right":
                        dx = refExt.MaxPoint.X - curExt.MaxPoint.X;
                        break;
                    case "baseline":
                        dy = refExt.MinPoint.Y - curExt.MinPoint.Y;
                        break;
                }

                if (Math.Abs(dx) > 1e-4 || Math.Abs(dy) > 1e-4)
                {
                    cur.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
                    alignedCount++;
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_text_align",
                Json = "{\"ok\":true,\"alignedCount\":" + alignedCount + ",\"mode\":\"" + mode + "\"}",
                MutatesDrawing = alignedCount > 0
            };
        }

        private static McpExecutionResult HandleDistribute(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("handles", out var raw) || !(raw is System.Collections.IEnumerable list))
                return new McpExecutionResult { ToolName = "cad_text_distribute", Json = "{\"ok\":false,\"error\":\"handles array is required.\"}" };

            string dir = CadJson.GetString(args, "direction", "vertical").ToLowerInvariant();
            double spacing = CadJson.GetDouble(args, "spacing", 0.0);

            var db = doc.Database;
            var entities = new List<Entity>();

            foreach (var item in list)
            {
                string hex = Convert.ToString(item);
                if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                {
                    if (db.TryGetObjectId(new Handle(hVal), out ObjectId id) && !id.IsNull)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null && !ent.IsErased) entities.Add(ent);
                    }
                }
            }

            if (entities.Count < 3 && spacing <= 0)
                return new McpExecutionResult { ToolName = "cad_text_distribute", Json = "{\"ok\":false,\"error\":\"Auto distribution requires at least 3 entities (or specify spacing > 0).\"}" };

            bool isVert = dir.StartsWith("v");
            var sorted = isVert
                ? entities.OrderByDescending(e => e.GeometricExtents.MinPoint.Y).ToList()
                : entities.OrderBy(e => e.GeometricExtents.MinPoint.X).ToList();

            int movedCount = 0;

            if (spacing > 0)
            {
                // 固定间距分布
                for (int i = 1; i < sorted.Count; i++)
                {
                    var prev = sorted[i - 1];
                    var cur = sorted[i];
                    double targetVal = isVert
                        ? (prev.GeometricExtents.MinPoint.Y - spacing)
                        : (prev.GeometricExtents.MaxPoint.X + spacing);

                    double curVal = isVert ? cur.GeometricExtents.MaxPoint.Y : cur.GeometricExtents.MinPoint.X;
                    double delta = targetVal - curVal;

                    if (Math.Abs(delta) > 1e-4)
                    {
                        var vec = isVert ? new Vector3d(0, delta, 0) : new Vector3d(delta, 0, 0);
                        cur.TransformBy(Matrix3d.Displacement(vec));
                        movedCount++;
                    }
                }
            }
            else
            {
                // 首尾固定等距分布
                double startVal = isVert ? sorted[0].GeometricExtents.MinPoint.Y : sorted[0].GeometricExtents.MinPoint.X;
                double endVal = isVert ? sorted[sorted.Count - 1].GeometricExtents.MinPoint.Y : sorted[sorted.Count - 1].GeometricExtents.MinPoint.X;
                double step = (endVal - startVal) / (sorted.Count - 1);

                for (int i = 1; i < sorted.Count - 1; i++)
                {
                    var cur = sorted[i];
                    double targetVal = startVal + step * i;
                    double curVal = isVert ? cur.GeometricExtents.MinPoint.Y : cur.GeometricExtents.MinPoint.X;
                    double delta = targetVal - curVal;

                    if (Math.Abs(delta) > 1e-4)
                    {
                        var vec = isVert ? new Vector3d(0, delta, 0) : new Vector3d(delta, 0, 0);
                        cur.TransformBy(Matrix3d.Displacement(vec));
                        movedCount++;
                    }
                }
            }

            return new McpExecutionResult
            {
                ToolName = "cad_text_distribute",
                Json = "{\"ok\":true,\"movedCount\":" + movedCount + ",\"direction\":\"" + dir + "\"}",
                MutatesDrawing = movedCount > 0
            };
        }
    }
}
