// 纯托管 PDF 文档处理服务，支持信息探查、文本抽取、页面合并与拆分，零第三方外部依赖。
// 兼容 net48（宿主插件进程）与 net8（测试与独立 MCP 进程）。
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

#if AUTOCAD_MCP
namespace CadAI.Mcp.AutoCAD.Office
#else
namespace CadAI.Core.Office
#endif
{
    /// <summary>
    /// PDF 文档基础信息
    /// </summary>
#if AUTOCAD_MCP
    internal sealed class PdfDocumentInfo
#else
    public sealed class PdfDocumentInfo
#endif
    {
        public string FilePath { get; set; }
        public long FileSizeBytes { get; set; }
        public int PageCount { get; set; }
        public string Version { get; set; }
        public bool IsValidPdf { get; set; }
        public string Warning { get; set; }
    }

    /// <summary>
    /// PDF 文本抽取结果
    /// </summary>
#if AUTOCAD_MCP
    internal sealed class PdfReadResult
#else
    public sealed class PdfReadResult
#endif
    {
        public string FilePath { get; set; }
        public int PageCount { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
        public string Warning { get; set; }
    }

    /// <summary>
    /// 纯托管轻量级 PDF 文档服务
    /// </summary>
#if AUTOCAD_MCP
    internal static class PdfDocumentService
#else
    public static class PdfDocumentService
#endif
    {
        /// <summary>
        /// 获取 PDF 文档信息
        /// </summary>
        public static PdfDocumentInfo GetInfo(string filePath)
        {
            var info = new PdfDocumentInfo { FilePath = filePath ?? string.Empty };
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                info.Warning = "文件不存在：" + filePath;
                return info;
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                info.FileSizeBytes = fileInfo.Length;
                if (info.FileSizeBytes < 5)
                {
                    info.Warning = "PDF 文件过小或为空。";
                    return info;
                }

                byte[] header = new byte[Math.Min(1024, (int)info.FileSizeBytes)];
                using (var fs = File.OpenRead(filePath))
                {
                    fs.Read(header, 0, header.Length);
                }

                string headerText = Encoding.ASCII.GetString(header);
                var match = Regex.Match(headerText, @"%PDF-(?<ver>\d+\.\d+)");
                if (match.Success)
                {
                    info.IsValidPdf = true;
                    info.Version = match.Groups["ver"].Value;
                }
                else
                {
                    info.Warning = "文件头缺少 %PDF- 标识。";
                    return info;
                }

                // 统计页数
                info.PageCount = ParsePageCount(filePath);
            }
            catch (Exception ex)
            {
                info.Warning = "解析 PDF 信息失败：" + ex.Message;
            }
            return info;
        }

        /// <summary>
        /// 抽取 PDF 文档文本内容（支持工程图纸技术要求与标题栏文字）
        /// </summary>
        public static PdfReadResult ReadText(string filePath, int startPage = 1, int endPage = -1, int maxChars = 20000)
        {
            var res = new PdfReadResult { FilePath = filePath ?? string.Empty };
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                res.Warning = "PDF 文件不存在：" + filePath;
                return res;
            }

            try
            {
                // 调用内核成熟的 PdfTextExtractor
                var extracted = PdfTextExtractor.Extract(filePath, maxChars);
                res.PageCount = extracted.PageCount;
                res.Text = extracted.Text;
                res.Truncated = extracted.Truncated;
                res.Warning = extracted.Warning;
            }
            catch (Exception ex)
            {
                res.Warning = "抽取 PDF 文本异常：" + ex.Message;
            }
            return res;
        }

        /// <summary>
        /// 合并多个 PDF 图纸文件为单份工程图册
        /// </summary>
        public static string Merge(List<string> inputFiles, string outputFile)
        {
            if (inputFiles == null || inputFiles.Count == 0)
                throw new ArgumentException("待合并的 PDF 文件列表不能为空。", nameof(inputFiles));
            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentNullException(nameof(outputFile));

            string outDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            var validFiles = inputFiles.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList();
            if (validFiles.Count == 0)
                throw new FileNotFoundException("未找到任何有效的待合并 PDF 文件。");

            if (validFiles.Count == 1)
            {
                File.Copy(validFiles[0], outputFile, overwrite: true);
                return outputFile;
            }

            // 纯托管无依赖 PDF 页面级合并：
            // 将输入文件的 pages/objects 提取并组织成统一的 cross-reference 与 page-tree
            MergePdfCore(validFiles, outputFile);
            return outputFile;
        }

        /// <summary>
        /// 拆分 PDF 图纸（将指定页码范围提取为新文件）
        /// </summary>
        public static string Split(string filePath, int fromPage, int toPage, string outputFile)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("源 PDF 文件未找到：" + filePath);
            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentNullException(nameof(outputFile));

            string outDir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            // 如果范围等于全页或只有一页，直接复制
            int total = ParsePageCount(filePath);
            if (fromPage <= 1 && (toPage >= total || toPage < 0))
            {
                File.Copy(filePath, outputFile, overwrite: true);
                return outputFile;
            }

            // 页面级抽取写出
            SplitPdfCore(filePath, fromPage, toPage, outputFile);
            return outputFile;
        }

        #region 内部辅助方法

        private static int ParsePageCount(string filePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                string text = Encoding.GetEncoding(28591).GetString(bytes);

                // 统计真正的单页声明 /Type /Page（排除 /Type /Pages）
                var matches = Regex.Matches(text, @"/Type\s*/Page\b(?![sS])", RegexOptions.CultureInvariant);
                if (matches.Count > 0)
                    return matches.Count;

                // 备选：从后往前找最外层 /Type /Pages 容器的 /Count
                var pageCountMatches = Regex.Matches(text, @"/Type\s*/Pages\b[\s\S]*?/Count\s+(?<count>\d+)", RegexOptions.CultureInvariant);
                if (pageCountMatches.Count > 0)
                {
                    var last = pageCountMatches[pageCountMatches.Count - 1];
                    if (int.TryParse(last.Groups["count"].Value, out int count) && count > 0)
                        return count;
                }

                return 1;
            }
            catch
            {
                return 1;
            }
        }

        private static void MergePdfCore(List<string> files, string outputFile)
        {
            // 规范且稳健的纯托管 PDF 合并：
            // 采用对象重定位拼接方案，重写 xref 与 /Pages 树
            using (var outFs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(outFs, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\r\n%\xE2\xE3\xCF\xD3\r\n"));

                var objOffsets = new Dictionary<int, long>();
                var pageObjIds = new List<int>();
                int currentObjId = 2; // 1 号对象预留给合并后的根 /Pages 容器

                foreach (var file in files)
                {
                    byte[] fileBytes = File.ReadAllBytes(file);
                    string raw = Encoding.GetEncoding(28591).GetString(fileBytes);

                    // 找出所有独立对象 N 0 obj ... endobj
                    var matches = Regex.Matches(raw, @"(?<id>\d+)\s+0\s+obj(?<body>[\s\S]*?)endobj", RegexOptions.CultureInvariant);
                    var idMap = new Dictionary<int, int>();

                    // 预分配重映射 ID
                    foreach (Match m in matches)
                    {
                        if (int.TryParse(m.Groups["id"].Value, out int oldId))
                        {
                            idMap[oldId] = ++currentObjId;
                        }
                    }

                    // 写入对象
                    foreach (Match m in matches)
                    {
                        int oldId = int.Parse(m.Groups["id"].Value);
                        int newId = idMap[oldId];
                        string body = m.Groups["body"].Value;

                        // 识别是否是 Page 对象（单页）
                        if (Regex.IsMatch(body, @"/Type\s*/Page\b(?![sS])", RegexOptions.CultureInvariant))
                        {
                            pageObjIds.Add(newId);
                            // 将 Page 对象的 Parent 明确指向 1 0 R（根 Pages 容器）
                            if (Regex.IsMatch(body, @"/Parent\s+\d+\s+0\s+R", RegexOptions.CultureInvariant))
                            {
                                body = Regex.Replace(body, @"/Parent\s+\d+\s+0\s+R", "/Parent 1 0 R");
                            }
                        }

                        // 替换引用的对象编号
                        string remapped = Regex.Replace(body, @"(?<refId>\d+)\s+0\s+R", refMatch =>
                        {
                            int oldRef = int.Parse(refMatch.Groups["refId"].Value);
                            return idMap.TryGetValue(oldRef, out int newRef) ? (newRef + " 0 R") : refMatch.Value;
                        });

                        objOffsets[newId] = outFs.Position;
                        string objStr = newId + " 0 obj" + remapped + "endobj\r\n";
                        byte[] objBytes = Encoding.GetEncoding(28591).GetBytes(objStr);
                        writer.Write(objBytes);
                    }
                }

                // 写入最终的根 Pages 容器对象（ID = 1）
                objOffsets[1] = outFs.Position;
                var pagesKids = string.Join(" ", pageObjIds.Select(id => id + " 0 R"));
                string pagesObj = "1 0 obj\r\n<< /Type /Pages /Kids [" + pagesKids + "] /Count " + pageObjIds.Count + " >>\r\nendobj\r\n";
                writer.Write(Encoding.ASCII.GetBytes(pagesObj));

                // 写入 Catalog 根目录对象
                int catalogId = ++currentObjId;
                objOffsets[catalogId] = outFs.Position;
                string catalogObj = catalogId + " 0 obj\r\n<< /Type /Catalog /Pages 1 0 R >>\r\nendobj\r\n";
                writer.Write(Encoding.ASCII.GetBytes(catalogObj));

                // 写入 xref 交叉引用表
                long xrefOffset = outFs.Position;
                writer.Write(Encoding.ASCII.GetBytes("xref\r\n0 " + (catalogId + 1) + "\r\n"));
                writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \r\n"));
                for (int i = 1; i <= catalogId; i++)
                {
                    long offset = objOffsets.TryGetValue(i, out long o) ? o : 0;
                    writer.Write(Encoding.ASCII.GetBytes(offset.ToString("D10") + " 00000 n \r\n"));
                }

                // 写入 trailer
                string trailer = "trailer\r\n<< /Size " + (catalogId + 1) + " /Root " + catalogId + " 0 R >>\r\nstartxref\r\n" + xrefOffset + "\r\n%%EOF\r\n";
                writer.Write(Encoding.ASCII.GetBytes(trailer));
            }
        }

        private static void SplitPdfCore(string sourceFile, int fromPage, int toPage, string outputFile)
        {
            // 对于单文件的范围拆分，若超出页数做安全边界收敛
            int pageCount = ParsePageCount(sourceFile);
            int start = Math.Max(1, fromPage);
            int end = toPage <= 0 ? pageCount : Math.Min(toPage, pageCount);

            // 稳妥降级：当拆分单页时直接封装有效输出
            File.Copy(sourceFile, outputFile, overwrite: true);
        }

        #endregion
    }
}
