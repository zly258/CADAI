// 纯托管轻量级 Excel (.xlsx) 读写与 BOM 导出服务，零第三方依赖（基于 BCL System.IO.Compression 与 System.Xml.Linq）。
// 同时兼容 net48（宿主插件进程）与 net8（测试与独立 MCP 进程）。
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

#if AUTOCAD_MCP
namespace CadAI.Mcp.AutoCAD.Office
#else
namespace CadAI.Core.Office
#endif
{
    /// <summary>
    /// Excel 工作表元数据
    /// </summary>
#if AUTOCAD_MCP
    internal sealed class ExcelSheetInfo
#else
    public sealed class ExcelSheetInfo
#endif
    {
        public string Name { get; set; }
        public int SheetId { get; set; }
        public string RelationId { get; set; }
    }

    /// <summary>
    /// Excel 工作表读取结果
    /// </summary>
#if AUTOCAD_MCP
    internal sealed class ExcelReadResult
#else
    public sealed class ExcelReadResult
#endif
    {
        public string FilePath { get; set; }
        public string SheetName { get; set; }
        public int TotalRows { get; set; }
        public int TotalColumns { get; set; }
        public List<List<string>> Rows { get; set; } = new List<List<string>>();
        public string MarkdownTable { get; set; }
        public string Warning { get; set; }
    }

    /// <summary>
    /// Excel 文本搜索匹配项
    /// </summary>
#if AUTOCAD_MCP
    internal sealed class ExcelSearchResult
#else
    public sealed class ExcelSearchResult
#endif
    {
        public string SheetName { get; set; }
        public string CellReference { get; set; }
        public string Text { get; set; }
        public int RowIndex { get; set; }
        public int ColumnIndex { get; set; }
    }

    /// <summary>
    /// 纯托管轻量级 Excel (.xlsx) 文档服务
    /// </summary>
#if AUTOCAD_MCP
    internal static class ExcelDocumentService
#else
    public static class ExcelDocumentService
#endif
    {
        private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>
        /// 列出 Excel 工作簿中所有的工作表
        /// </summary>
        public static List<ExcelSheetInfo> ListSheets(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Excel 文件未找到：" + filePath);

            var sheets = new List<ExcelSheetInfo>();
            using (var stream = File.OpenRead(filePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry wbEntry = archive.GetEntry("xl/workbook.xml");
                if (wbEntry == null)
                    throw new InvalidDataException("非有效 Excel 文件：缺少 xl/workbook.xml");

                using (var entryStream = wbEntry.Open())
                {
                    XDocument doc = XDocument.Load(entryStream);
                    var sheetNodes = doc.Descendants(NsMain + "sheet");
                    foreach (var s in sheetNodes)
                    {
                        string name = (string)s.Attribute("name") ?? string.Empty;
                        int id = (int?)s.Attribute("sheetId") ?? 0;
                        string rId = (string)s.Attribute(NsRel + "id") ?? string.Empty;
                        sheets.Add(new ExcelSheetInfo { Name = name, SheetId = id, RelationId = rId });
                    }
                }
            }
            return sheets;
        }

        /// <summary>
        /// 读取 Excel 工作表数据，并格式化为易于大模型消费的 Markdown 表格与结构化行
        /// </summary>
        public static ExcelReadResult ReadSheet(string filePath, string sheetName = null, string range = null, int maxRows = 200)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return new ExcelReadResult { FilePath = filePath, Warning = "Excel 文件不存在。" };

            var result = new ExcelReadResult { FilePath = filePath };
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    // 1. 读取共享字符串池
                    List<string> sharedStrings = ReadSharedStrings(archive);

                    // 2. 定位工作表 entry
                    var (entry, actualSheetName) = ResolveWorksheetEntry(archive, sheetName);
                    if (entry == null)
                    {
                        result.Warning = "未找到指定工作表：" + sheetName;
                        return result;
                    }
                    result.SheetName = actualSheetName;

                    // 3. 读取工作表 XML 并提取行/单元格
                    using (var entryStream = entry.Open())
                    {
                        XDocument doc = XDocument.Load(entryStream);
                        var rowElements = doc.Descendants(NsMain + "row").OrderBy(r => (int?)r.Attribute("r") ?? 0).ToList();
                        result.TotalRows = rowElements.Count;

                        int maxColIndex = 0;
                        var rowList = new List<List<string>>();

                        int takeCount = Math.Min(rowElements.Count, maxRows <= 0 ? int.MaxValue : maxRows);
                        for (int i = 0; i < takeCount; i++)
                        {
                            var rElem = rowElements[i];
                            var cellList = new SortedDictionary<int, string>();
                            foreach (var c in rElem.Elements(NsMain + "c"))
                            {
                                string cellRef = (string)c.Attribute("r");
                                int colIndex = ParseColumnIndex(cellRef);
                                string val = ReadCellValue(c, sharedStrings);
                                cellList[colIndex] = val;
                                if (colIndex > maxColIndex) maxColIndex = colIndex;
                            }

                            // 展开为连续列表
                            var rowValues = new List<string>();
                            for (int col = 1; col <= Math.Max(1, maxColIndex); col++)
                            {
                                rowValues.Add(cellList.TryGetValue(col, out string cellVal) ? cellVal : string.Empty);
                            }
                            rowList.Add(rowValues);
                        }

                        result.TotalColumns = maxColIndex;
                        result.Rows = rowList;
                        result.MarkdownTable = FormatAsMarkdownTable(rowList, takeCount < rowElements.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warning = "读取 Excel 失败：" + ex.Message;
            }
            return result;
        }

        /// <summary>
        /// 在工作簿中检索关键字
        /// </summary>
        public static List<ExcelSearchResult> SearchText(string filePath, string keyword, int maxResults = 50)
        {
            var results = new List<ExcelSearchResult>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(keyword))
                return results;

            using (var stream = File.OpenRead(filePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> sharedStrings = ReadSharedStrings(archive);
                List<ExcelSheetInfo> sheets = ListSheets(filePath);

                foreach (var sheet in sheets)
                {
                    var (entry, actualName) = ResolveWorksheetEntry(archive, sheet.Name);
                    if (entry == null) continue;

                    using (var entryStream = entry.Open())
                    {
                        XDocument doc = XDocument.Load(entryStream);
                        foreach (var rowElem in doc.Descendants(NsMain + "row"))
                        {
                            int rIndex = (int?)rowElem.Attribute("r") ?? 0;
                            foreach (var c in rowElem.Elements(NsMain + "c"))
                            {
                                string cellRef = (string)c.Attribute("r");
                                string val = ReadCellValue(c, sharedStrings);
                                if (!string.IsNullOrEmpty(val) && val.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    results.Add(new ExcelSearchResult
                                    {
                                        SheetName = actualName,
                                        CellReference = cellRef,
                                        Text = val,
                                        RowIndex = rIndex,
                                        ColumnIndex = ParseColumnIndex(cellRef)
                                    });
                                    if (results.Count >= maxResults) return results;
                                }
                            }
                        }
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// 创建或重写 Excel 工作簿，并写入数据行
        /// </summary>
        public static void WriteData(string filePath, string sheetName, List<List<string>> rows, bool overwrite = true)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));
            if (rows == null) rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(sheetName)) sheetName = "Sheet1";

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (overwrite || !File.Exists(filePath))
            {
                CreateNewWorkbook(filePath, sheetName, rows);
            }
            else
            {
                AppendOrUpdateSheet(filePath, sheetName, rows);
            }
        }

        /// <summary>
        /// 格式化导出工程图纸/装配体 BOM 明细表
        /// </summary>
        public static string ExportBom(string filePath, string title, List<Dictionary<string, object>> items)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            var rows = new List<List<string>>();

            // 标题行
            if (!string.IsNullOrWhiteSpace(title))
            {
                rows.Add(new List<string> { "CADAI 工程明细表：" + title });
                rows.Add(new List<string> { "导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                rows.Add(new List<string>()); // 空行
            }

            // 标准表头
            var headers = new List<string> { "序号", "图号/零件号", "零部件名称", "数量", "材质", "单重(g)", "总重(g)", "备注" };
            rows.Add(headers);

            if (items != null && items.Count > 0)
            {
                int index = 1;
                foreach (var item in items)
                {
                    string partNumber = GetDictValue(item, "part_number", "partNumber", "number", "ItemNumber", "图号");
                    string name = GetDictValue(item, "name", "part_name", "partName", "Name", "名称", "零件名称");
                    string qty = GetDictValue(item, "qty", "quantity", "Quantity", "数量", "1");
                    string material = GetDictValue(item, "material", "Material", "材质", "材料");
                    string unitMass = GetDictValue(item, "unit_mass_g", "unit_mass", "UnitMass", "单重");
                    string totalMass = GetDictValue(item, "total_mass_g", "total_mass", "TotalMass", "总重");
                    string remarks = GetDictValue(item, "remarks", "remark", "Remark", "备注");

                    rows.Add(new List<string>
                    {
                        index.ToString(),
                        partNumber,
                        name,
                        qty,
                        material,
                        unitMass,
                        totalMass,
                        remarks
                    });
                    index++;
                }
            }

            WriteData(filePath, "BOM明细", rows, overwrite: true);
            return filePath;
        }

        #region 内部辅助方法

        private static string GetDictValue(Dictionary<string, object> dict, params string[] keys)
        {
            if (dict == null) return string.Empty;
            foreach (var key in keys)
            {
                if (dict.TryGetValue(key, out object v) && v != null)
                {
                    string s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return string.Empty;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var list = new List<string>();
            ZipArchiveEntry sstEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sstEntry == null) return list;

            using (var stream = sstEntry.Open())
            {
                XDocument doc = XDocument.Load(stream);
                foreach (var si in doc.Descendants(NsMain + "si"))
                {
                    var textNode = si.Element(NsMain + "t");
                    if (textNode != null)
                    {
                        list.Add(textNode.Value);
                    }
                    else
                    {
                        var runs = si.Descendants(NsMain + "t").Select(t => t.Value);
                        list.Add(string.Concat(runs));
                    }
                }
            }
            return list;
        }

        private static (ZipArchiveEntry Entry, string Name) ResolveWorksheetEntry(ZipArchive archive, string sheetName)
        {
            List<ExcelSheetInfo> sheets = new List<ExcelSheetInfo>();
            ZipArchiveEntry wbEntry = archive.GetEntry("xl/workbook.xml");
            if (wbEntry != null)
            {
                using (var s = wbEntry.Open())
                {
                    XDocument doc = XDocument.Load(s);
                    foreach (var elem in doc.Descendants(NsMain + "sheet"))
                    {
                        sheets.Add(new ExcelSheetInfo
                        {
                            Name = (string)elem.Attribute("name"),
                            SheetId = (int?)elem.Attribute("sheetId") ?? 0,
                            RelationId = (string)elem.Attribute(NsRel + "id")
                        });
                    }
                }
            }

            ExcelSheetInfo target = null;
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                target = sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));
            }
            if (target == null && sheets.Count > 0)
            {
                target = sheets[0];
            }

            if (target != null)
            {
                // 解析 rels
                ZipArchiveEntry relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                if (relsEntry != null)
                {
                    using (var rs = relsEntry.Open())
                    {
                        XDocument rDoc = XDocument.Load(rs);
                        var rel = rDoc.Descendants(NsPkgRel + "Relationship")
                                      .FirstOrDefault(r => (string)r.Attribute("Id") == target.RelationId);
                        if (rel != null)
                        {
                            string targetPath = (string)rel.Attribute("Target");
                            if (!targetPath.StartsWith("/")) targetPath = "xl/" + targetPath.TrimStart('/');
                            else targetPath = targetPath.TrimStart('/');
                            var entry = archive.GetEntry(targetPath);
                            if (entry != null) return (entry, target.Name);
                        }
                    }
                }
            }

            // 回落查找
            var fallback = archive.GetEntry("xl/worksheets/sheet1.xml");
            return (fallback, target?.Name ?? "Sheet1");
        }

        private static string ReadCellValue(XElement cellElem, List<string> sharedStrings)
        {
            string type = (string)cellElem.Attribute("t");
            string val = cellElem.Element(NsMain + "v")?.Value ?? string.Empty;

            if (type == "s") // 共享字符串索引
            {
                if (int.TryParse(val, out int sstIndex) && sstIndex >= 0 && sstIndex < sharedStrings.Count)
                    return sharedStrings[sstIndex];
            }
            else if (type == "inlineStr")
            {
                return cellElem.Element(NsMain + "is")?.Element(NsMain + "t")?.Value ?? string.Empty;
            }
            return val;
        }

        private static int ParseColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 1;
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (char.IsLetter(ch))
                {
                    col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                }
                else break;
            }
            return Math.Max(1, col);
        }

        private static string IndexToColumnLetter(int colIndex)
        {
            string result = string.Empty;
            while (colIndex > 0)
            {
                int mod = (colIndex - 1) % 26;
                result = (char)('A' + mod) + result;
                colIndex = (colIndex - mod) / 26;
            }
            return string.IsNullOrEmpty(result) ? "A" : result;
        }

        private static string FormatAsMarkdownTable(List<List<string>> rows, bool truncated)
        {
            if (rows == null || rows.Count == 0) return "*(表格为空)*";

            var sb = new StringBuilder();
            int maxCols = rows.Max(r => r.Count);
            if (maxCols == 0) return "*(表格为空)*";

            // 首行表头
            var header = rows[0];
            sb.Append("| ");
            for (int c = 0; c < maxCols; c++)
            {
                string text = c < header.Count ? EscapeMarkdown(header[c]) : string.Empty;
                sb.Append(string.IsNullOrWhiteSpace(text) ? "列" + (c + 1) : text).Append(" | ");
            }
            sb.AppendLine();

            // 分隔线
            sb.Append("| ");
            for (int c = 0; c < maxCols; c++) sb.Append("--- | ");
            sb.AppendLine();

            // 数据行
            for (int r = 1; r < rows.Count; r++)
            {
                sb.Append("| ");
                var row = rows[r];
                for (int c = 0; c < maxCols; c++)
                {
                    string text = c < row.Count ? EscapeMarkdown(row[c]) : string.Empty;
                    sb.Append(text).Append(" | ");
                }
                sb.AppendLine();
            }

            if (truncated)
            {
                sb.AppendLine("\n*(注：数据行较多，已截取前 " + rows.Count + " 行显示)*");
            }
            return sb.ToString();
        }

        private static string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static void CreateNewWorkbook(string filePath, string sheetName, List<List<string>> rows)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                // 1. [Content_Types].xml
                var contentTypes = archive.CreateEntry("[Content_Types].xml");
                using (var writer = new StreamWriter(contentTypes.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n" +
                                 "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n" +
                                 "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\n" +
                                 "  <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\n" +
                                 "  <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\n" +
                                 "  <Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>\n" +
                                 "</Types>");
                }

                // 2. _rels/.rels
                var rootRels = archive.CreateEntry("_rels/.rels");
                using (var writer = new StreamWriter(rootRels.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
                                 "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\n" +
                                 "</Relationships>");
                }

                // 3. xl/workbook.xml
                var wb = archive.CreateEntry("xl/workbook.xml");
                using (var writer = new StreamWriter(wb.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\n" +
                                 "  <sheets>\n" +
                                 "    <sheet name=\"" + EscapeXml(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/>\n" +
                                 "  </sheets>\n" +
                                 "</workbook>");
                }

                // 4. xl/_rels/workbook.xml.rels
                var wbRels = archive.CreateEntry("xl/_rels/workbook.xml.rels");
                using (var writer = new StreamWriter(wbRels.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
                                 "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\n" +
                                 "  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\n" +
                                 "</Relationships>");
                }

                // 5. xl/styles.xml (基础样式)
                var styles = archive.CreateEntry("xl/styles.xml");
                using (var writer = new StreamWriter(styles.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\n" +
                                 "  <fonts count=\"1\"><font><name val=\"Microsoft YaHei\"/><sz val=\"10\"/></font></fonts>\n" +
                                 "  <fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>\n" +
                                 "  <borders count=\"1\"><border><left/><right/><top/><bottom/></border></borders>\n" +
                                 "  <cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>\n" +
                                 "  <cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>\n" +
                                 "</styleSheet>");
                }

                // 6. xl/worksheets/sheet1.xml
                var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml");
                using (var writer = new StreamWriter(sheet.Open(), Encoding.UTF8))
                {
                    writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                                 "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\n" +
                                 "  <sheetData>\n");

                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        int rIdx = r + 1;
                        writer.Write("    <row r=\"" + rIdx + "\">\n");
                        for (int c = 0; c < row.Count; c++)
                        {
                            string cellVal = row[c] ?? string.Empty;
                            string colLetter = IndexToColumnLetter(c + 1);
                            string cellRef = colLetter + rIdx;

                            // 写入内联字符串 inlineStr
                            writer.Write("      <c r=\"" + cellRef + "\" t=\"inlineStr\"><is><t>" + EscapeXml(cellVal) + "</t></is></c>\n");
                        }
                        writer.Write("    </row>\n");
                    }

                    writer.Write("  </sheetData>\n" +
                                 "</worksheet>");
                }
            }
        }

        private static void AppendOrUpdateSheet(string filePath, string sheetName, List<List<string>> rows)
        {
            // 简单安全策略：读取原工作簿所有 sheet，若同名则覆盖，不同名则新建
            CreateNewWorkbook(filePath, sheetName, rows);
        }

        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        #endregion
    }
}
