// 与 ScopeRoutingTable 等共享源码相同的约定：本文件同时编译进
// net48（ChatCore，无 nullable 上下文）与 net8（测试工程，nullable 启用）。
// 显式关闭 nullable，使两侧行为一致、不产生 CS8632/CS8603 噪音。
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

#if AUTOCAD_MCP
namespace CadAI.Mcp.AutoCAD.Office
#else
namespace CadAI.Core
#endif
{
    /// <summary>
    /// Best-effort PDF text extractor for user attachments.
    ///
    /// It is intentionally small and dependency-free: the chat attachment pipeline can extract
    /// dimensions and title text from many vector engineering PDFs without shipping a PDF renderer.
    /// Scanned/image-only PDFs still need a real rasterizer/OCR stage; callers must treat an empty
    /// result as "no reliable PDF facts" rather than guessing.
    /// </summary>
    internal static class PdfTextExtractor
    {
        private const int DefaultMaxBytes = 25 * 1024 * 1024;
        private static readonly Regex LiteralTextOperator = new Regex(@"\((?<text>(?:\\.|[^\\)])*)\)\s*Tj", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ArrayTextOperator = new Regex(@"\[(?<array>[\s\S]*?)\]\s*TJ", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HexTextOperator = new Regex(@"<(?<hex>[0-9A-Fa-f\s]+)>\s*Tj", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LiteralInArray = new Regex(@"\((?<text>(?:\\.|[^\\)])*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HexInArray = new Regex(@"<(?<hex>[0-9A-Fa-f\s]+)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static PdfTextExtractionResult Extract(string path, int maxChars = 20000, int maxBytes = DefaultMaxBytes)
        {
            var result = new PdfTextExtractionResult { Path = path ?? string.Empty };
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    result.Warning = "PDF 文件不存在。";
                    return result;
                }

                var info = new FileInfo(path);
                result.Bytes = info.Length;
                if (info.Length <= 0)
                {
                    result.Warning = "PDF 文件为空。";
                    return result;
                }
                if (info.Length > maxBytes)
                {
                    result.Warning = "PDF 文件过大，已跳过文本抽取。";
                    return result;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (!LooksLikePdf(bytes))
                {
                    result.Warning = "文件头不是 PDF。";
                    return result;
                }

                string raw = Latin1(bytes);
                result.PageCount = EstimatePageCount(raw);

                var segments = new List<string>();
                foreach (byte[] streamBytes in EnumeratePdfStreams(bytes, raw))
                {
                    string text = ExtractTextOperators(Latin1(streamBytes));
                    if (!string.IsNullOrWhiteSpace(text)) segments.Add(text);
                    if (segments.Sum(s => s.Length) >= maxChars) break;
                }

                if (segments.Count == 0)
                {
                    // Some simple PDFs keep text outside normal content streams. Scan the whole file
                    // as a last resort; this is noisy but bounded and still better than guessing.
                    string text = ExtractTextOperators(raw);
                    if (!string.IsNullOrWhiteSpace(text)) segments.Add(text);
                }

                string combined = NormalizeText(string.Join("\n", segments));
                if (combined.Length > maxChars)
                {
                    combined = combined.Substring(0, maxChars) + "\n...[已截断，PDF 文本超过 " + maxChars + " 字符]";
                    result.Truncated = true;
                }
                result.Text = combined;
                if (string.IsNullOrWhiteSpace(result.Text) && string.IsNullOrWhiteSpace(result.Warning))
                    result.Warning = "未抽取到可读文本；这可能是扫描件或纯图像 PDF。";
            }
            catch (Exception ex)
            {
                result.Warning = "PDF 文本抽取失败：" + ex.Message;
            }
            return result;
        }

        private static string Latin1(byte[] bytes)
            => bytes == null || bytes.Length == 0 ? string.Empty : Encoding.GetEncoding(28591).GetString(bytes);

        private static bool LooksLikePdf(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 5) return false;
            return bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F' && bytes[4] == (byte)'-';
        }

        private static int EstimatePageCount(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            int count = Regex.Matches(raw, @"/Type\s*/Page\b", RegexOptions.CultureInvariant).Count;
            return Math.Max(0, count);
        }

        private static IEnumerable<byte[]> EnumeratePdfStreams(byte[] fileBytes, string raw)
        {
            int search = 0;
            while (search >= 0 && search < raw.Length)
            {
                int streamIndex = raw.IndexOf("stream", search, StringComparison.Ordinal);
                if (streamIndex < 0) yield break;
                int dataStart = streamIndex + "stream".Length;
                if (dataStart < fileBytes.Length && fileBytes[dataStart] == (byte)'\r') dataStart++;
                if (dataStart < fileBytes.Length && fileBytes[dataStart] == (byte)'\n') dataStart++;

                int endIndex = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (endIndex < 0) yield break;
                int dataEnd = endIndex;
                while (dataEnd > dataStart && (fileBytes[dataEnd - 1] == (byte)'\r' || fileBytes[dataEnd - 1] == (byte)'\n')) dataEnd--;

                int length = Math.Max(0, dataEnd - dataStart);
                if (length > 0)
                {
                    byte[] data = new byte[length];
                    Buffer.BlockCopy(fileBytes, dataStart, data, 0, length);
                    string header = HeaderBeforeStream(raw, streamIndex);
                    if (header.IndexOf("/FlateDecode", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        byte[] inflated = TryInflate(data);
                        if (inflated != null && inflated.Length > 0) yield return inflated;
                    }
                    else
                    {
                        yield return data;
                    }
                }

                search = endIndex + "endstream".Length;
            }
        }

        private static string HeaderBeforeStream(string raw, int streamIndex)
        {
            int start = raw.LastIndexOf("obj", Math.Max(0, streamIndex), StringComparison.Ordinal);
            if (start < 0 || streamIndex <= start) start = Math.Max(0, streamIndex - 1200);
            int length = Math.Min(2400, streamIndex - start);
            return length <= 0 ? string.Empty : raw.Substring(start, length);
        }

        private static byte[] TryInflate(byte[] data)
        {
            byte[] first = InflateCore(data, skipBytes: 0);
            if (first != null) return first;
            // PDF Flate streams often carry a zlib header. Some framework DeflateStream builds want
            // raw deflate, so retry after skipping the two-byte header.
            if (data.Length > 2 && data[0] == 0x78)
                return InflateCore(data, skipBytes: 2);
            return null;
        }

        private static byte[] InflateCore(byte[] data, int skipBytes)
        {
            try
            {
                using (var input = new MemoryStream(data, skipBytes, data.Length - skipBytes))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractTextOperators(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            var sb = new StringBuilder();

            foreach (Match match in LiteralTextOperator.Matches(content))
                AppendText(sb, DecodePdfLiteral(match.Groups["text"].Value));

            foreach (Match match in HexTextOperator.Matches(content))
                AppendText(sb, DecodePdfHex(match.Groups["hex"].Value));

            foreach (Match match in ArrayTextOperator.Matches(content))
            {
                string array = match.Groups["array"].Value;
                var line = new StringBuilder();
                foreach (Match literal in LiteralInArray.Matches(array))
                    line.Append(DecodePdfLiteral(literal.Groups["text"].Value));
                foreach (Match hex in HexInArray.Matches(array))
                    line.Append(DecodePdfHex(hex.Groups["hex"].Value));
                AppendText(sb, line.ToString());
            }

            return sb.ToString();
        }

        private static void AppendText(StringBuilder sb, string text)
        {
            text = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(text);
        }

        private static string DecodePdfLiteral(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var bytes = new List<byte>();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\')
                {
                    bytes.Add((byte)(c <= 255 ? c : '?'));
                    continue;
                }

                if (++i >= value.Length) break;
                char esc = value[i];
                switch (esc)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(':
                    case ')':
                    case '\\': bytes.Add((byte)esc); break;
                    case '\r':
                        if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                        break;
                    case '\n':
                        break;
                    default:
                        if (esc >= '0' && esc <= '7')
                        {
                            int octal = esc - '0';
                            int taken = 0;
                            while (taken < 2 && i + 1 < value.Length && value[i + 1] >= '0' && value[i + 1] <= '7')
                            {
                                i++;
                                taken++;
                                octal = octal * 8 + (value[i] - '0');
                            }
                            bytes.Add((byte)Math.Min(255, octal));
                        }
                        else
                        {
                            bytes.Add((byte)(esc <= 255 ? esc : '?'));
                        }
                        break;
                }
            }
            return DecodePdfStringBytes(bytes.ToArray());
        }

        private static string DecodePdfHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
            string compact = Regex.Replace(hex, @"\s+", string.Empty);
            if (compact.Length == 0) return string.Empty;
            if (compact.Length % 2 == 1) compact += "0";
            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                try { bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16); }
                catch { return string.Empty; }
            }
            return DecodePdfStringBytes(bytes);
        }

        private static string DecodePdfStringBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            // Heuristic for UTF-16BE strings without BOM, common in CID-font PDFs.
            int zerosOnEven = 0;
            for (int i = 0; i + 1 < bytes.Length; i += 2)
                if (bytes[i] == 0) zerosOnEven++;
            if (bytes.Length >= 4 && zerosOnEven >= Math.Max(1, bytes.Length / 6))
                return Encoding.BigEndianUnicode.GetString(bytes);

            // Some generators embed UTF-8 directly in literal strings. Accept it only when the
            // byte sequence is *strictly* valid UTF-8 and actually contains a multi-byte character;
            // otherwise fall back to PDFDocEncoding/Latin-1 so lone high bytes (Ø, ä, °) stay intact.
            string utf8 = TryDecodeStrictUtf8(bytes);
            if (utf8 != null) return utf8;

            return Encoding.GetEncoding(28591).GetString(bytes);
        }

        private static string TryDecodeStrictUtf8(byte[] bytes)
        {
            bool hasHighByte = false;
            bool hasMultiByteSequence = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80) hasHighByte = true;
                if (bytes[i] >= 0xC2) hasMultiByteSequence = true;
            }
            if (!hasHighByte || !hasMultiByteSequence) return null;

            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                string decoded = strict.GetString(bytes);
                // Reject the replacement-character free but semantically wrong case where the
                // multi-byte sequence decoded into control characters.
                foreach (char c in decoded)
                    if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') return null;
                return decoded;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace('\0', ' ').Replace('\u00A0', ' ');
            text = Regex.Replace(text, @"[ \t\f\v]+", " ");
            text = Regex.Replace(text, @"\s*\r?\n\s*", "\n");
            var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return string.Join("\n", lines);
        }
    }

    internal sealed class PdfTextExtractionResult
    {
        public string Path { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public int PageCount { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Warning { get; set; } = string.Empty;
        public bool Truncated { get; set; }
    }
}
