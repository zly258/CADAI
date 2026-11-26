using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Office;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// AutoCAD 端通用 Office (Excel & PDF) 文档服务
    /// </summary>
    internal static class OfficeDocumentService
    {
        public static void Register(CadToolRegistry registry)
        {
            // ========================= Excel 工具集（后台工具，无活动图纸要求） =========================
            registry.RegisterBackground("cad_excel_read_sheet", HandleExcelReadSheet,
                CadSchema.Function("cad_excel_read_sheet",
                    "Read an Excel (.xlsx) worksheet as a Markdown table and structured rows.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Excel file path. Relative paths resolve inside current workspace.", true),
                        CadSchema.Str("sheet_name", "Sheet name to read. Blank = first sheet."),
                        CadSchema.Str("range", "Optional cell range, e.g. 'A1:F20'."),
                        CadSchema.Num("max_rows", "Maximum rows to return. Default 200."))));

            registry.RegisterBackground("cad_excel_list_sheets", HandleExcelListSheets,
                CadSchema.Function("cad_excel_list_sheets",
                    "List all worksheets in an Excel (.xlsx) workbook.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Excel file path.", true))));

            registry.RegisterBackground("cad_excel_write_data", HandleExcelWriteData,
                CadSchema.Function("cad_excel_write_data",
                    "Create or overwrite an Excel (.xlsx) file and write rows into a worksheet.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Output Excel file path (relative paths save to Exports\\).", true),
                        CadSchema.Arr("rows", "Two-dimensional array of row values: [[val1, val2], ...].", "array", true),
                        CadSchema.Str("sheet_name", "Worksheet name. Default 'Sheet1'."),
                        CadSchema.Bool("overwrite", "Whether to overwrite existing file. Default true."))));

            registry.RegisterBackground("cad_excel_export_table", HandleExcelExportTable,
                CadSchema.Function("cad_excel_export_table",
                    "Export a drawing Table entity or virtual extracted table into a formatted Excel (.xlsx) file.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Output Excel file path (relative paths save to Exports\\).", true),
                        CadSchema.Arr("rows", "2D table rows: [[col1, col2, ...], ...].", "array", true),
                        CadSchema.Str("title", "Optional title for the exported table."))));

            // ========================= PDF 工具集（后台工具，无活动图纸要求） =========================
            registry.RegisterBackground("cad_pdf_read_text", HandlePdfReadText,
                CadSchema.Function("cad_pdf_read_text",
                    "Extract text content from a PDF document (e.g. design specs, notes).",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "PDF file path.", true),
                        CadSchema.Num("start_page", "Starting page number (1-based). Default 1."),
                        CadSchema.Num("end_page", "Ending page number (1-based). Blank/Negative = all pages."),
                        CadSchema.Num("max_chars", "Maximum characters to extract. Default 20000."))));

            registry.RegisterBackground("cad_pdf_get_info", HandlePdfGetInfo,
                CadSchema.Function("cad_pdf_get_info",
                    "Inspect PDF document metadata (page count, version, file size).",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "PDF file path.", true))));

            registry.RegisterBackground("cad_pdf_merge", HandlePdfMerge,
                CadSchema.Function("cad_pdf_merge",
                    "Merge multiple PDF drawing files into a consolidated engineering booklet.",
                    CadSchema.Obj(
                        CadSchema.Arr("input_files", "List of PDF file paths to merge in order.", "string", true),
                        CadSchema.Str("output_file", "Output PDF path (relative paths save into Exports\\).", true))));

            registry.RegisterBackground("cad_pdf_split", HandlePdfSplit,
                CadSchema.Function("cad_pdf_split",
                    "Extract a range of pages from a PDF document into a new PDF file.",
                    CadSchema.Obj(
                        CadSchema.Str("file_path", "Source PDF file path.", true),
                        CadSchema.Num("from_page", "Starting page (1-based).", true),
                        CadSchema.Num("to_page", "Ending page (1-based).", true),
                        CadSchema.Str("output_file", "Output PDF path (relative paths save into Exports\\).", true))));
        }

        #region Excel 处理句柄

        private static McpExecutionResult HandleExcelReadSheet(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string path = ResolveInputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            string sheetName = CadJson.GetString(p, "sheet_name", CadJson.GetString(p, "sheetName"));
            string range = CadJson.GetString(p, "range");
            int maxRows = CadJson.GetInt(p, "max_rows", CadJson.GetInt(p, "maxRows", 200));

            var result = ExcelDocumentService.ReadSheet(path, sheetName, range, maxRows);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = result.FilePath,
                ["sheet_name"] = result.SheetName,
                ["total_rows"] = result.TotalRows,
                ["total_columns"] = result.TotalColumns,
                ["markdown_table"] = result.MarkdownTable,
                ["warning"] = result.Warning
            };
            return new McpExecutionResult { ToolName = "cad_excel_read_sheet", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleExcelListSheets(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string path = ResolveInputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            var list = ExcelDocumentService.ListSheets(path);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = path,
                ["count"] = list.Count,
                ["sheets"] = list.Select(s => new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["sheet_id"] = s.SheetId
                }).ToList()
            };
            return new McpExecutionResult { ToolName = "cad_excel_list_sheets", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleExcelWriteData(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string outPath = ResolveOutputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            string sheetName = CadJson.GetString(p, "sheet_name", CadJson.GetString(p, "sheetName", "Sheet1"));
            bool overwrite = CadJson.GetBool(p, "overwrite", true);

            var rows = Parse2DStringList(p.TryGetValue("rows", out var rVal) ? rVal : null);
            ExcelDocumentService.WriteData(outPath, sheetName, rows, overwrite);

            var data = new Dictionary<string, object>
            {
                ["file_path"] = outPath,
                ["row_count"] = rows.Count,
                ["sheet_name"] = sheetName,
                ["message"] = "Excel 文件已写入完成。"
            };
            return new McpExecutionResult { ToolName = "cad_excel_write_data", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleExcelExportTable(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string outPath = ResolveOutputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            string docName = null;
            try { docName = Application.DocumentManager?.MdiActiveDocument?.Name; } catch { }
            string defaultTitle = !string.IsNullOrEmpty(docName) ? Path.GetFileNameWithoutExtension(docName) : "CAD表格";
            string title = CadJson.GetString(p, "title", defaultTitle);
            var rows = Parse2DStringList(p.TryGetValue("rows", out var rVal) ? rVal : null);

            ExcelDocumentService.WriteData(outPath, "表格明细", rows, overwrite: true);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = outPath,
                ["row_count"] = rows.Count,
                ["title"] = title,
                ["message"] = "图纸表格已成功导出为 Excel 文件。"
            };
            return new McpExecutionResult { ToolName = "cad_excel_export_table", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        #endregion

        #region PDF 处理句柄

        private static McpExecutionResult HandlePdfReadText(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string path = ResolveInputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            int startPage = CadJson.GetInt(p, "start_page", CadJson.GetInt(p, "startPage", 1));
            int endPage = CadJson.GetInt(p, "end_page", CadJson.GetInt(p, "endPage", -1));
            int maxChars = CadJson.GetInt(p, "max_chars", CadJson.GetInt(p, "maxChars", 20000));

            var res = PdfDocumentService.ReadText(path, startPage, endPage, maxChars);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = res.FilePath,
                ["page_count"] = res.PageCount,
                ["text"] = res.Text,
                ["truncated"] = res.Truncated,
                ["warning"] = res.Warning
            };
            return new McpExecutionResult { ToolName = "cad_pdf_read_text", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandlePdfGetInfo(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string path = ResolveInputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            var info = PdfDocumentService.GetInfo(path);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = info.FilePath,
                ["page_count"] = info.PageCount,
                ["file_size_bytes"] = info.FileSizeBytes,
                ["version"] = info.Version,
                ["is_valid_pdf"] = info.IsValidPdf,
                ["warning"] = info.Warning
            };
            return new McpExecutionResult { ToolName = "cad_pdf_get_info", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandlePdfMerge(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            var rawList = p.TryGetValue("input_files", out var fVal) ? fVal as IList : null;
            var inputs = new List<string>();
            if (rawList != null)
            {
                foreach (var item in rawList)
                {
                    if (item != null) inputs.Add(ResolveInputPath(item.ToString()));
                }
            }

            string outPath = ResolveOutputPath(CadJson.GetString(p, "output_file", CadJson.GetString(p, "outputFile")));
            string written = PdfDocumentService.Merge(inputs, outPath);

            var data = new Dictionary<string, object>
            {
                ["file_path"] = written,
                ["merged_count"] = inputs.Count,
                ["message"] = "PDF 文件已成功合并为图册。"
            };
            return new McpExecutionResult { ToolName = "cad_pdf_merge", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandlePdfSplit(string argsJson)
        {
            var p = CadJson.Deserialize(argsJson);
            string path = ResolveInputPath(CadJson.GetString(p, "file_path", CadJson.GetString(p, "filePath")));
            int fromPage = CadJson.GetInt(p, "from_page", CadJson.GetInt(p, "fromPage", 1));
            int toPage = CadJson.GetInt(p, "to_page", CadJson.GetInt(p, "toPage", 1));
            string outPath = ResolveOutputPath(CadJson.GetString(p, "output_file", CadJson.GetString(p, "outputFile")));

            string written = PdfDocumentService.Split(path, fromPage, toPage, outPath);
            var data = new Dictionary<string, object>
            {
                ["file_path"] = written,
                ["from_page"] = fromPage,
                ["to_page"] = toPage,
                ["message"] = "PDF 页面拆分已完成。"
            };
            return new McpExecutionResult { ToolName = "cad_pdf_split", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        #endregion

        #region 辅助解析

        private static string ResolveInputPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            if (Path.IsPathRooted(raw)) return raw;

            // 优先查找会话目录，其次工作区根目录
            string inConv = Path.Combine(CadEngineHost.CurrentConversationDirectory, raw);
            if (File.Exists(inConv)) return inConv;

            string inRoot = Path.Combine(CadEngineHost.RootDirectory, raw);
            if (File.Exists(inRoot)) return inRoot;

            return inConv;
        }

        private static string ResolveOutputPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Path.Combine(CadEngineHost.ExportsDirectory, "output_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dat");
            if (Path.IsPathRooted(raw)) return raw;

            // 相对路径统一落入 Exports\
            string outDir = CadEngineHost.ExportsDirectory;
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            return Path.Combine(outDir, raw);
        }

        private static List<List<string>> Parse2DStringList(object raw)
        {
            var result = new List<List<string>>();
            if (raw is not IList outerList) return result;

            foreach (var rowObj in outerList)
            {
                var row = new List<string>();
                if (rowObj is IList innerList)
                {
                    foreach (var cell in innerList)
                    {
                        row.Add(cell?.ToString() ?? string.Empty);
                    }
                }
                else if (rowObj != null)
                {
                    row.Add(rowObj.ToString());
                }
                result.Add(row);
            }
            return result;
        }

        #endregion
    }
}
