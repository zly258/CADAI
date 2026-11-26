using System.Text;
using CadAI.Core.Office;
using Xunit;

namespace CadAI.Tests;

public class OfficeDocumentServiceTests
{
    private static string GetTestOutputDir()
    {
        string dir = RepoPaths.Resolve(".build", "tests", "office_" + Guid.NewGuid().ToString("N")[..8]);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Excel_Write_And_Read_RoundTrip_Success()
    {
        string outDir = GetTestOutputDir();
        string xlsxPath = Path.Combine(outDir, "test_table.xlsx");

        var rows = new List<List<string>>
        {
            new() { "PartNo", "Name", "Qty", "Material" },
            new() { "P001", "传动轴", "2", "45#钢" },
            new() { "P002", "法兰盘", "4", "Q235" }
        };

        // 1. 写入
        ExcelDocumentService.WriteData(xlsxPath, "零部件清单", rows, overwrite: true);
        Assert.True(File.Exists(xlsxPath), "Excel 文件应当成功创建。");

        // 2. 列出工作表
        var sheets = ExcelDocumentService.ListSheets(xlsxPath);
        Assert.NotEmpty(sheets);
        Assert.Contains(sheets, s => s.Name == "零部件清单");

        // 3. 读取数据
        var readResult = ExcelDocumentService.ReadSheet(xlsxPath, "零部件清单");
        Assert.Equal("零部件清单", readResult.SheetName);
        Assert.Equal(3, readResult.TotalRows);
        Assert.Equal(4, readResult.TotalColumns);
        Assert.Contains("传动轴", readResult.MarkdownTable);
        Assert.Contains("45#钢", readResult.MarkdownTable);

        // 4. 文本检索
        var searchHits = ExcelDocumentService.SearchText(xlsxPath, "法兰盘");
        Assert.NotEmpty(searchHits);
        Assert.Equal("零部件清单", searchHits[0].SheetName);
        Assert.Contains("法兰盘", searchHits[0].Text);
    }

    [Fact]
    public void Excel_ExportBom_Creates_Formatted_Table()
    {
        string outDir = GetTestOutputDir();
        string bomPath = Path.Combine(outDir, "assembly_bom.xlsx");

        var items = new List<Dictionary<string, object>>
        {
            new()
            {
                ["part_number"] = "PART-001",
                ["name"] = "电机支架",
                ["qty"] = 1,
                ["material"] = "HT200",
                ["unit_mass_g"] = "1250",
                ["total_mass_g"] = "1250"
            },
            new()
            {
                ["part_number"] = "PART-002",
                ["name"] = "紧固螺栓 M12",
                ["qty"] = 8,
                ["material"] = "8.8级",
                ["unit_mass_g"] = "45",
                ["total_mass_g"] = "360"
            }
        };

        string writtenPath = ExcelDocumentService.ExportBom(bomPath, "主减速器装配体", items);
        Assert.Equal(bomPath, writtenPath);
        Assert.True(File.Exists(bomPath));

        var read = ExcelDocumentService.ReadSheet(bomPath);
        Assert.Contains("CADAI 工程明细表", read.MarkdownTable);
        Assert.Contains("主减速器装配体", read.MarkdownTable);
        Assert.Contains("电机支架", read.MarkdownTable);
        Assert.Contains("紧固螺栓", read.MarkdownTable);
    }

    [Fact]
    public void Pdf_Info_And_ReadText_Handles_Sample()
    {
        string outDir = GetTestOutputDir();
        string pdfPath = Path.Combine(outDir, "sample.pdf");

        // 创建一个简单的合规 PDF
        string pdfContent =
            "%PDF-1.4\r\n" +
            "1 0 obj\r\n<< /Type /Catalog /Pages 2 0 R >>\r\nendobj\r\n" +
            "2 0 obj\r\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\r\nendobj\r\n" +
            "3 0 obj\r\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\r\nendobj\r\n" +
            "4 0 obj\r\n<< /Length 43 >>\r\nstream\r\nBT /F1 12 Tf 72 712 Td (CADAI Engineering Drawing) Tj ET\r\nendstream\r\nendobj\r\n" +
            "xref\r\n0 5\r\n" +
            "0000000000 65535 f \r\n" +
            "0000000010 00000 n \r\n" +
            "0000000060 00000 n \r\n" +
            "0000000117 00000 n \r\n" +
            "0000000213 00000 n \r\n" +
            "trailer\r\n<< /Size 5 /Root 1 0 R >>\r\nstartxref\r\n307\r\n%%EOF\r\n";

        File.WriteAllBytes(pdfPath, Encoding.ASCII.GetBytes(pdfContent));

        // 1. 验证 GetInfo
        var info = PdfDocumentService.GetInfo(pdfPath);
        Assert.True(info.IsValidPdf);
        Assert.Equal("1.4", info.Version);
        Assert.True(info.PageCount >= 1);

        // 2. 验证 ReadText
        var read = PdfDocumentService.ReadText(pdfPath);
        Assert.Contains("CADAI Engineering Drawing", read.Text);
    }

    [Fact]
    public void Pdf_Merge_Combines_Files()
    {
        string outDir = GetTestOutputDir();
        string p1 = Path.Combine(outDir, "p1.pdf");
        string p2 = Path.Combine(outDir, "p2.pdf");
        string merged = Path.Combine(outDir, "merged.pdf");

        string simplePdfTemplate =
            "%PDF-1.4\r\n" +
            "1 0 obj\r\n<< /Type /Catalog /Pages 2 0 R >>\r\nendobj\r\n" +
            "2 0 obj\r\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\r\nendobj\r\n" +
            "3 0 obj\r\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\r\nendobj\r\n" +
            "xref\r\n0 4\r\n0000000000 65535 f \r\n0000000010 00000 n \r\n0000000060 00000 n \r\n0000000117 00000 n \r\n" +
            "trailer\r\n<< /Size 4 /Root 1 0 R >>\r\nstartxref\r\n210\r\n%%EOF\r\n";

        File.WriteAllBytes(p1, Encoding.ASCII.GetBytes(simplePdfTemplate));
        File.WriteAllBytes(p2, Encoding.ASCII.GetBytes(simplePdfTemplate));

        string res = PdfDocumentService.Merge(new List<string> { p1, p2 }, merged);
        Assert.True(File.Exists(res));

        var info = PdfDocumentService.GetInfo(merged);
        Assert.True(info.IsValidPdf);
        Assert.True(info.PageCount >= 2);
    }
}
