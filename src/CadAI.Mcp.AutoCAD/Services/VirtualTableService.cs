using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 散线/图元拼装的"伪表格"（Virtual Table）识别与结构化提取服务。
    /// 
    /// 工程图纸中大量表格（材料表、管口表、技术参数表、图纸目录）
    /// 是由线条（Line / Polyline）和分散单行文字（DBText / MText）拼装而成的，
    /// 并非 AutoCAD 原生 Table 实体。
    /// 本服务负责：
    /// 1. 自动提取相交水平/垂直直线，构建拓扑单元格网格；
    /// 2. 空间拓扑匹配：将落入各网格包围盒内的文本归并到指定行列；
    /// 3. 输出标准化二维矩阵，支持导出 CSV / JSON，并可精准按行/列回写或定向翻译。
    /// </summary>
    internal static class VirtualTableService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_virtual_table_detect", HandleDetectVirtualTable,
                CadSchema.Function("cad_virtual_table_detect",
                    "Detect and reconstruct a table from drawn lines (Lines/Polylines) and texts (DBText/MText) in a rectangular area or selection. " +
                    "Returns reconstructed 2D table grid (rows, columns, cell texts, and entity Handles) without requiring native AutoCAD Table entities.",
                    CadSchema.Obj(
                        CadSchema.Num("minX", "Optional bounding window min X (snake_case min_x also accepted). If omitted, uses current selection."),
                        CadSchema.Num("minY", "Optional bounding window min Y (min_y also accepted)."),
                        CadSchema.Num("maxX", "Optional bounding window max X (max_x also accepted)."),
                        CadSchema.Num("maxY", "Optional bounding window max Y (max_y also accepted)."),
                        CadSchema.Arr("handles", "Optional entity handle list defining the table lines and texts.", "string"),
                        CadSchema.Num("tolerance", "Coordinate clustering tolerance in mm (default 2.0)."),
                        CadSchema.Bool("exportCsv", "If true, also generates and returns CSV string of the reconstructed table."))));
        }

        private sealed class TextItem
        {
            public ObjectId Id;
            public string Handle;
            public string Text;
            public Point3d Position;
            public Extents3d? Bounds;
        }

        private sealed class GridCell
        {
            public int Row;
            public int Col;
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
            public readonly List<TextItem> Items = new List<TextItem>();

            public string CombinedText => string.Join(" ", Items.Select(x => x.Text?.Trim()).Where(s => !string.IsNullOrEmpty(s)));
        }

        private static McpExecutionResult HandleDetectVirtualTable(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double tolerance = CadJson.GetDouble(args, "tolerance", 2.0);
            if (tolerance <= 0.01) tolerance = 2.0;

            bool exportCsv = CadJson.GetBool(args, "exportCsv", false);

            List<ObjectId> entityIds = new List<ObjectId>();

            // 1. 来源判定：若传了 minX..maxY 则使用窗口范围选择。
            // 键名同时接受 camelCase 与 snake_case：本工具 schema 写的是 minX，
            // 但同一套 MCP 的取图/窗口类工具用的是 min_x，模型照邻居工具写参数时
            // 会静默落空（2026-10-23 自测：传 min_x 后报"未指定有效的坐标范围"）。
            double minX = ReadCoordinate(args, "minX", "min_x");
            double minY = ReadCoordinate(args, "minY", "min_y");
            double maxX = ReadCoordinate(args, "maxX", "max_x");
            double maxY = ReadCoordinate(args, "maxY", "max_y");

            if (!double.IsNaN(minX) && !double.IsNaN(minY) && !double.IsNaN(maxX) && !double.IsNaN(maxY))
            {
                var ext = new Extents3d(new Point3d(Math.Min(minX, maxX), Math.Min(minY, maxY), -1e6),
                                        new Point3d(Math.Max(minX, maxX), Math.Max(minY, maxY), 1e6));

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.GeometricExtents == null) continue;
                        var b = ent.GeometricExtents;
                        if (b.MaxPoint.X < ext.MinPoint.X || b.MinPoint.X > ext.MaxPoint.X ||
                            b.MaxPoint.Y < ext.MinPoint.Y || b.MinPoint.Y > ext.MaxPoint.Y)
                            continue;
                        entityIds.Add(id);
                    }
                    catch { }
                }

                if (entityIds.Count == 0)
                {
                    return Fail("cad_virtual_table_detect",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "坐标范围 ({0:F1},{1:F1})-({2:F1},{3:F1}) 内没有可用的图元（需要 Line/Polyline/DBText/MText）。",
                            Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Max(minX, maxX), Math.Max(minY, maxY)));
                }
            }
            else if (args != null && args.TryGetValue("handles", out var rawHandles) && rawHandles is System.Collections.IEnumerable handleList)
            {
                foreach (var h in handleList)
                {
                    string hex = Convert.ToString(h)?.Trim();
                    if (string.IsNullOrEmpty(hex)) continue;
                    if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long val) &&
                        doc.Database.TryGetObjectId(new Handle(val), out ObjectId id) && !id.IsNull)
                    {
                        entityIds.Add(id);
                    }
                }
            }
            else
            {
                // 默认使用当前选中实体
                var ed = doc.Editor;
                var sel = ed.SelectImplied();
                if (sel.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK && sel.Value != null)
                {
                    foreach (SelectedObject so in sel.Value)
                    {
                        if (so != null && !so.ObjectId.IsNull) entityIds.Add(so.ObjectId);
                    }
                }
            }

            if (entityIds.Count == 0)
            {
                return Fail("cad_virtual_table_detect",
                    "未指定有效的坐标范围（minX/minY/maxX/maxY，也接受 min_x/min_y/max_x/max_y）、handles，且当前没有选中的图元。");
            }

            // 2. 分离图元：线条 (Lines/Segments) 与 文字 (DBText/MText)
            List<Tuple<Point2d, Point2d>> segments = new List<Tuple<Point2d, Point2d>>();
            List<TextItem> texts = new List<TextItem>();

            foreach (var id in entityIds)
            {
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                if (ent == null) continue;

                if (ent is Line line)
                {
                    segments.Add(Tuple.Create(new Point2d(line.StartPoint.X, line.StartPoint.Y),
                                              new Point2d(line.EndPoint.X, line.EndPoint.Y)));
                }
                else if (ent is Polyline pl)
                {
                    for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                    {
                        segments.Add(Tuple.Create(pl.GetPoint2dAt(i), pl.GetPoint2dAt(i + 1)));
                    }
                    if (pl.Closed && pl.NumberOfVertices > 2)
                    {
                        segments.Add(Tuple.Create(pl.GetPoint2dAt(pl.NumberOfVertices - 1), pl.GetPoint2dAt(0)));
                    }
                }
                else if (ent is DBText dbt)
                {
                    Extents3d? bounds = null;
                    try { bounds = dbt.GeometricExtents; } catch { }
                    texts.Add(new TextItem
                    {
                        Id = id,
                        Handle = dbt.Handle.ToString(),
                        Text = dbt.TextString,
                        Position = dbt.Position,
                        Bounds = bounds
                    });
                }
                else if (ent is MText mt)
                {
                    Extents3d? bounds = null;
                    try { bounds = mt.GeometricExtents; } catch { }
                    texts.Add(new TextItem
                    {
                        Id = id,
                        Handle = mt.Handle.ToString(),
                        Text = mt.Contents,
                        Position = mt.Location,
                        Bounds = bounds
                    });
                }
            }

            // 3. 分析水平线与垂直线提取网格坐标
            List<double> horizY = new List<double>();
            List<double> vertX = new List<double>();

            foreach (var seg in segments)
            {
                double dx = Math.Abs(seg.Item2.X - seg.Item1.X);
                double dy = Math.Abs(seg.Item2.Y - seg.Item1.Y);

                // 水平线：dy 很小且有一定长度
                if (dy <= tolerance && dx >= tolerance)
                {
                    horizY.Add((seg.Item1.Y + seg.Item2.Y) / 2.0);
                }
                // 垂直线：dx 很小且有一定长度
                else if (dx <= tolerance && dy >= tolerance)
                {
                    vertX.Add((seg.Item1.X + seg.Item2.X) / 2.0);
                }
            }

            // 聚类去重
            List<double> distinctY = ClusterValues(horizY, tolerance).OrderByDescending(y => y).ToList(); // Y从上到下排
            List<double> distinctX = ClusterValues(vertX, tolerance).OrderBy(x => x).ToList();           // X从左到右排

            if (distinctY.Count < 2 || distinctX.Count < 2)
            {
                // 线条不足以构建表格边界，尝试从文字本身的行高/列宽投影聚类
                return FallbackGridFromTexts(texts, exportCsv);
            }

            // 4. 构建网格 GridCell (rows = distinctY.Count - 1, cols = distinctX.Count - 1)
            int rowsCount = distinctY.Count - 1;
            int colsCount = distinctX.Count - 1;

            var grid = new GridCell[rowsCount, colsCount];
            for (int r = 0; r < rowsCount; r++)
            {
                double topY = distinctY[r];
                double bottomY = distinctY[r + 1];
                for (int c = 0; c < colsCount; c++)
                {
                    double leftX = distinctX[c];
                    double rightX = distinctX[c + 1];

                    grid[r, c] = new GridCell
                    {
                        Row = r,
                        Col = c,
                        MinX = leftX,
                        MaxX = rightX,
                        MinY = bottomY,
                        MaxY = topY
                    };
                }
            }

            // 5. 将文字分配进单元格
            foreach (var t in texts)
            {
                double px = t.Position.X;
                double py = t.Position.Y;
                if (t.Bounds.HasValue)
                {
                    px = (t.Bounds.Value.MinPoint.X + t.Bounds.Value.MaxPoint.X) / 2.0;
                    py = (t.Bounds.Value.MinPoint.Y + t.Bounds.Value.MaxPoint.Y) / 2.0;
                }

                for (int r = 0; r < rowsCount; r++)
                {
                    if (py < grid[r, 0].MinY - tolerance || py > grid[r, 0].MaxY + tolerance) continue;
                    for (int c = 0; c < colsCount; c++)
                    {
                        var cell = grid[r, c];
                        if (px >= cell.MinX - tolerance && px <= cell.MaxX + tolerance)
                        {
                            cell.Items.Add(t);
                            break;
                        }
                    }
                }
            }

            // 6. 构造输出结构
            var rowsData = new List<object>();
            var cellHandles = new List<object>();

            for (int r = 0; r < rowsCount; r++)
            {
                var rowTexts = new List<string>();
                var rowHandleList = new List<object>();
                for (int c = 0; c < colsCount; c++)
                {
                    var cell = grid[r, c];
                    rowTexts.Add(cell.CombinedText);
                    rowHandleList.Add(cell.Items.Select(x => x.Handle).ToList());
                }
                rowsData.Add(rowTexts);
                cellHandles.Add(rowHandleList);
            }

            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["recognizedType"] = "LineAssembledGrid",
                ["rows"] = rowsCount,
                ["columns"] = colsCount,
                ["rowsData"] = rowsData,
                ["cellHandles"] = cellHandles,
                ["horizontalSeparators"] = distinctY.Select(y => Math.Round(y, 2)).ToList(),
                ["verticalSeparators"] = distinctX.Select(x => Math.Round(x, 2)).ToList()
            };

            if (exportCsv)
            {
                result["csv"] = BuildCsvString(rowsData);
            }

            return new McpExecutionResult
            {
                ToolName = "cad_virtual_table_detect",
                Json = CadJson.Serialize(result),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult FallbackGridFromTexts(List<TextItem> texts, bool exportCsv)
        {
            if (texts.Count == 0)
            {
                return Fail("cad_virtual_table_detect", "选区内既无闭合网格线，亦无文本实体，无法构建虚拟表格。");
            }

            // 按 Y 聚类成行
            double yTol = 4.0;
            var distinctY = ClusterValues(texts.Select(t => t.Position.Y).ToList(), yTol).OrderByDescending(y => y).ToList();

            var rowsData = new List<object>();
            foreach (var y in distinctY)
            {
                var lineTexts = texts.Where(t => Math.Abs(t.Position.Y - y) <= yTol)
                                     .OrderBy(t => t.Position.X)
                                     .Select(t => t.Text?.Trim())
                                     .ToList();
                rowsData.Add(lineTexts);
            }

            var result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["recognizedType"] = "TextPositionClusterFallback",
                ["rows"] = distinctY.Count,
                ["columns"] = rowsData.Count > 0 ? ((List<string>)rowsData[0]).Count : 0,
                ["rowsData"] = rowsData,
                ["note"] = "未检测到足够的网格直线，已退回使用文字几何位置聚类成行。"
            };

            if (exportCsv) result["csv"] = BuildCsvString(rowsData);

            return new McpExecutionResult
            {
                ToolName = "cad_virtual_table_detect",
                Json = CadJson.Serialize(result),
                MutatesDrawing = false
            };
        }

        private static List<double> ClusterValues(List<double> values, double tol)
        {
            var result = new List<double>();
            if (values == null || values.Count == 0) return result;

            var sorted = values.OrderBy(v => v).ToList();
            double curSum = sorted[0];
            int curCount = 1;

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - sorted[i - 1] <= tol)
                {
                    curSum += sorted[i];
                    curCount++;
                }
                else
                {
                    result.Add(curSum / curCount);
                    curSum = sorted[i];
                    curCount = 1;
                }
            }
            result.Add(curSum / curCount);
            return result;
        }

        private static string BuildCsvString(List<object> rows)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in rows)
            {
                if (r is System.Collections.IEnumerable list)
                {
                    var line = new List<string>();
                    foreach (var c in list)
                    {
                        string s = Convert.ToString(c) ?? string.Empty;
                        if (s.Contains("\"") || s.Contains(",") || s.Contains("\n") || s.Contains("\r"))
                        {
                            s = "\"" + s.Replace("\"", "\"\"") + "\"";
                        }
                        line.Add(s);
                    }
                    sb.AppendLine(string.Join(",", line));
                }
            }
            return sb.ToString();
        }

        /// <summary>读窗口坐标：camelCase 与 snake_case 都认，缺省返回 NaN。</summary>
        private static double ReadCoordinate(Dictionary<string, object> args, string camel, string snake)
        {
            if (args == null) return double.NaN;
            if (args.ContainsKey(camel)) return CadJson.GetDouble(args, camel, double.NaN);
            if (args.ContainsKey(snake)) return CadJson.GetDouble(args, snake, double.NaN);
            return double.NaN;
        }

        private static McpExecutionResult Fail(string tool, string message) =>
            new McpExecutionResult { ToolName = tool, Json = CadJson.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = message }), MutatesDrawing = false };
    }
}
