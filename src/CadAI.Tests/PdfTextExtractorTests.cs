using System.IO.Compression;
using System.Text;
using System.Linq;
using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

public sealed class PdfTextExtractorTests
{
    [Fact]
    public void Extract_ReadsLiteralTextFromPlainPdfStream()
    {
        string path = TestPdfPath("plain");
        try
        {
            File.WriteAllBytes(path, BuildPdf("BT /F1 12 Tf 72 720 Td (L=120 mm) Tj [( Ø) 20 (10 H7)] TJ ET", compress: false));

            PdfTextExtractionResult result = PdfTextExtractor.Extract(path);

            Assert.Contains("L=120 mm", result.Text);
            Assert.Contains("10 H7", result.Text);
            Assert.True(result.PageCount >= 1);
            Assert.True(string.IsNullOrWhiteSpace(result.Warning));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Extract_ReadsTextFromFlatePdfStream()
    {
        string path = TestPdfPath("flate");
        try
        {
            File.WriteAllBytes(path, BuildPdf("BT /F1 12 Tf 72 720 Td (孔距 PCD 80) Tj ET", compress: true));

            PdfTextExtractionResult result = PdfTextExtractor.Extract(path);

            Assert.Contains("孔距", result.Text);
            Assert.Contains("PCD 80", result.Text);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Extract_ReturnsWarningForImageOnlyPdf()
    {
        string path = TestPdfPath("empty");
        try
        {
            File.WriteAllBytes(path, BuildPdf("q 100 0 0 100 10 10 cm /Im0 Do Q", compress: false));

            PdfTextExtractionResult result = PdfTextExtractor.Extract(path);

            Assert.True(string.IsNullOrWhiteSpace(result.Text));
            Assert.Contains("未抽取到可读文本", result.Warning);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string TestPdfPath(string label)
    {
        string dir = Path.Combine(RepoPaths.FindRepoRoot(), ".build", "tmp", "pdf-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "CADAI-pdf-" + label + "-" + Guid.NewGuid().ToString("N") + ".pdf");
    }

    private static byte[] BuildPdf(string streamText, bool compress)
    {
        byte[] streamBytes = Encoding.UTF8.GetBytes(streamText);
        string filter = string.Empty;
        if (compress)
        {
            streamBytes = Deflate(streamBytes);
            filter = "/Filter /FlateDecode ";
        }

        string header = "%PDF-1.4\n" +
                        "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
                        "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
                        "3 0 obj << /Type /Page /Parent 2 0 R /Contents 4 0 R >> endobj\n" +
                        "4 0 obj << " + filter + "/Length " + streamBytes.Length + " >>\nstream\n";
        string footer = "\nendstream\nendobj\n%%EOF";
        return Encoding.ASCII.GetBytes(header).Concat(streamBytes).Concat(Encoding.ASCII.GetBytes(footer)).ToArray();
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
