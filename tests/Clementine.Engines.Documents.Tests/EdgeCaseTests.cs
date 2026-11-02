using System.Diagnostics;
using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Documents.Common;
using Clementine.Engines.Documents.Tests.Fixtures;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Clementine.Engines.Documents.Tests;

/// <summary>Adversarial inputs for the native (no external tool) operations: odd PDFs, odd page ranges, odd workbooks.</summary>
public sealed class EdgeCaseTests() : DocumentTestBase("edgecases")
{
    // Page ranges -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1 3", 5, new[] { 1, 3 })]
    [InlineData("1 3 5", 5, new[] { 1, 3, 5 })]
    [InlineData("2 - 4", 5, new[] { 2, 3, 4 })]
    [InlineData("1,,3", 5, new[] { 1, 3 })]
    [InlineData("1-", 1, new[] { 1 })]
    [InlineData("１-３", 5, new[] { 1, 2, 3 })]
    [InlineData("1−3", 5, new[] { 1, 2, 3 })]
    [InlineData("1‐2，4", 5, new[] { 1, 2, 4 })]
    [InlineData("4 -", 5, new[] { 4, 5 })]
    [InlineData("1\t2", 5, new[] { 1, 2 })]
    public void Page_ranges_accept_what_people_type(string text, int pageCount, int[] expected) =>
        Assert.Equal(expected, PageRanges.Expand(PageRanges.Parse(text, pageCount)));

    [Theory]
    [InlineData(",,")]
    [InlineData(" , ; ")]
    [InlineData("1-2-3")]
    [InlineData("--3")]
    [InlineData("99999999999")]
    [InlineData("1.5")]
    public void Odd_page_ranges_are_rejected_kindly(string text)
    {
        var error = Assert.Throws<OperationException>(() => PageRanges.Parse(text, 10));
        Assert.DoesNotContain("Exception", error.Message);
    }

    [Fact]
    public void A_document_without_pages_has_no_ranges()
    {
        var error = Assert.Throws<OperationException>(() => PageRanges.Parse("1-", 0));
        Assert.Contains("no pages", error.Message);
        Assert.Throws<OperationException>(() => PageRanges.Parse("all", 0));
    }

    [Fact]
    public async Task Extracting_many_scattered_pages_keeps_the_file_name_short()
    {
        var input = TextPdf(Workspace.Path("long.pdf"), Enumerable.Range(1, 200).Select(i => $"body {i}").ToArray());
        var pages = string.Join(", ", Enumerable.Range(0, 100).Select(i => (i * 2 + 1).ToString(CultureInfo.InvariantCulture)));
        var output = Single(await RunAsync("pdf.pages.extract", input, new() { ["pages"] = pages }));

        Assert.True(Path.GetFileName(output).Length <= 80, Path.GetFileName(output));
        Assert.Equal(100, PageCount(output));
    }

    [Fact]
    public async Task Empty_pdfs_get_friendly_errors()
    {
        var input = ZeroPagePdf(Workspace.Path("empty.pdf"));
        foreach (var (id, options) in new (string, Dictionary<string, object?>?)[]
                 {
                     ("pdf.png.150", null), ("pdf.split.ranges", new() { ["ranges"] = "1-" }), ("pdf.pages.extract", new() { ["pages"] = "1-" }),
                     ("pdf.pages.reorder", new() { ["order"] = "1-" }), ("pdf.split.pages", null), ("pdf.compress.rasterize", null),
                 })
        {
            var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync(id, input, options));
            Assert.False(string.IsNullOrWhiteSpace(error.Message), id);
        }
        Assert.Contains("Pages: 0", (await RunAsync("pdf.info", input)).Text);
    }

    // Encrypted PDFs ----------------------------------------------------------------------------------------------

    public static TheoryData<string> Encryptions => new() { "rc4-128", "aes-128", "aes-256" };

    [Theory]
    [MemberData(nameof(Encryptions))]
    public async Task Owner_password_only_pdfs_open_everywhere_reading_is_allowed(string encryption)
    {
        var input = OwnerOnlyPdf(Workspace.Path($"owner-{encryption}.pdf"), encryption, "first page", "second page");

        Assert.Contains("second page", await File.ReadAllTextAsync(Single(await RunAsync("pdf.txt", input))));
        Assert.Equal(2, Directory.GetFiles(Single(await RunAsync("pdf.png.150", input))).Length);
        var info = (await RunAsync("pdf.info", input)).Text!;
        Assert.Contains("Pages: 2", info);
        Assert.Contains("Encryption: Yes", info);

        var other = TextPdf(Workspace.Path($"plain-{encryption}.pdf"), "plain");
        var merged = Single(await RunAsync("pdf.merge", [input, other]));
        Assert.Equal(3, PageCount(merged));
        Assert.Equal(1, PageCount(Single(await RunAsync("pdf.pages.extract", input, new() { ["pages"] = "2" }))));

        var unlocked = Single(await RunAsync("pdf.unlock", input));
        using (var document = PigDocument.Open(unlocked))
            Assert.False(document.IsEncrypted);
        Assert.Contains("first page", string.Join(' ', PageTexts(unlocked)));
    }

    [Fact]
    public async Task Owner_password_only_pdfs_explain_how_to_edit_them()
    {
        var input = OwnerOnlyPdf(Workspace.Path("owner.pdf"), "aes-256", "text");
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.rotate.right", input));
        Assert.Contains("Unlock", error.Message);
    }

    // Damaged and unusual PDFs --------------------------------------------------------------------------------------

    [Fact]
    public async Task Pdfs_with_a_broken_xref_table_still_work()
    {
        var input = BrokenXrefPdf(Workspace.Path("broken xref.pdf"), "alpha", "beta", "gamma");

        Assert.Contains("gamma", await File.ReadAllTextAsync(Single(await RunAsync("pdf.txt", input))));
        Assert.Equal(3, Directory.GetFiles(Single(await RunAsync("pdf.png.150", input))).Length);
        var reversed = Single(await RunAsync("pdf.pages.reverse", input));
        Assert.Equal("Page 3 gamma", PageTexts(reversed)[0]);
        Assert.Equal(2, PageCount(Single(await RunAsync("pdf.pages.extract", input, new() { ["pages"] = "1-2" }))));
        Assert.Contains("Pages: 3", (await RunAsync("pdf.info", input)).Text);
    }

    [Fact]
    public async Task Truncated_pdfs_fail_kindly()
    {
        var full = await File.ReadAllBytesAsync(TextPdf(Workspace.Path("whole.pdf"), "a", "b"));
        var input = Workspace.Path("truncated.pdf");
        await File.WriteAllBytesAsync(input, full[..(full.Length / 3)]);
        foreach (var id in new[] { "pdf.txt", "pdf.png.150", "pdf.pages.reverse", "pdf.info", "pdf.docx", "pdf.compress.optimize" })
        {
            try
            {
                await RunAsync(id, input);
            }
            catch (OperationException)
            {
                // A friendly failure is fine; anything else (a crash) fails the test.
            }
        }
    }

    [Fact]
    public async Task Rotated_and_mixed_size_pages_are_reported_as_displayed()
    {
        var input = Workspace.Path("rotated.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            page.Rotate = 90;
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawString("Turned", new XFont("Arial", 20), XBrushes.Black, 60, 90);
            var second = document.AddPage();
            second.Width = XUnit.FromMillimeter(148);
            second.Height = XUnit.FromMillimeter(210);
            document.Save(input);
        }

        var info = (await RunAsync("pdf.info", input)).Text!;
        Assert.Contains("Page size: 297 × 210 mm (A4, landscape)", info);
        Assert.Contains("sizes vary", info);

        var folder = Single(await RunAsync("pdf.png.150", input));
        using var first = Decode(Directory.GetFiles(folder).Order().First());
        Assert.True(first.Width > first.Height, $"{first.Width}×{first.Height}: the rotated page should render landscape.");
    }

    [Fact]
    public async Task Five_hundred_page_pdfs_are_handled_in_reasonable_time()
    {
        var input = TextPdf(Workspace.Path("big.pdf"), Enumerable.Range(1, 500).Select(i => $"body of page {i}").ToArray());
        var stopwatch = Stopwatch.StartNew();
        Assert.Contains("Pages: 500", (await RunAsync("pdf.info", input)).Text);
        var text = await File.ReadAllTextAsync(Single(await RunAsync("pdf.txt", input)));
        Assert.Contains("body of page 500", text);
        var parts = Single(await RunAsync("pdf.split.ranges", input, new() { ["ranges"] = "1-250, 251-" }));
        Assert.Equal(2, Directory.GetFiles(parts).Length);
        Assert.Equal(500, PageCount(Single(await RunAsync("pdf.pages.reverse", input))));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Huge_pages_render_at_a_lower_resolution_instead_of_running_out_of_memory()
    {
        var input = Workspace.Path("poster.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(14_400); // 200 in, the largest page PDF allows
            page.Height = XUnit.FromPoint(14_400);
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawRectangle(XBrushes.Orange, 0, 0, 7200, 7200);
            document.Save(input);
        }
        var result = await RunAsync("pdf.png.300", input);
        using var bitmap = Decode(Single(result));
        Assert.True((long)bitmap.Width * bitmap.Height <= Pdf.PdfRenderer.MaxPixelsPerPage, $"{bitmap.Width}×{bitmap.Height}");
        Assert.Contains(result.Warnings, w => w.Contains("lower resolution"));
    }

    // Protected Office files read natively ------------------------------------------------------------------------

    [Fact]
    public async Task Password_protected_office_files_are_explained_by_native_readers()
    {
        var docx = EncryptedOfficeFixtures.Docx(Workspace.Path("secret.docx"));
        Assert.Contains("password", (await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.txt", docx))).Message);
        var xlsx = EncryptedOfficeFixtures.Xlsx(Workspace.Path("secret.xlsx"));
        Assert.Contains("password", (await Assert.ThrowsAsync<OperationException>(() => RunAsync("sheet.csv", xlsx))).Message);
        var pptx = EncryptedOfficeFixtures.Pptx(Workspace.Path("secret.pptx"));
        Assert.Contains("password", (await Assert.ThrowsAsync<OperationException>(() => RunAsync("slides.txt", pptx))).Message);
    }

    // Spreadsheets ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stray_cell_in_the_far_corner_does_not_blow_up_csv_export()
    {
        var input = Workspace.Path("stray.xlsx");
        using (var document = SpreadsheetDocument.Create(input, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Name")) },
                        new Cell { CellReference = "B1", CellValue = new CellValue("42") }) { RowIndex = 1U },
                new Row(new Cell { CellReference = "XFD1048576", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("oops")) }) { RowIndex = 1048576U }));
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = 1U, Name = "Data" }));
            workbook.Workbook.Save();
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync("sheet.csv", input);
        var csv = Single(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        Assert.True(new FileInfo(csv).Length < 50L * 1024 * 1024, $"{new FileInfo(csv).Length} bytes");
        using (var reader = new StreamReader(csv))
            Assert.Equal("Name,42", (await reader.ReadLineAsync())!.TrimStart('﻿'));
        Assert.Contains(result.Warnings, w => w.Contains("empty", StringComparison.OrdinalIgnoreCase));

        // Nothing is lost: the far cell is still on row 1,048,576 in column 16,384.
        var lines = await File.ReadAllLinesAsync(csv);
        Assert.Equal(1_048_576, lines.Length);
        Assert.Equal(new string(',', 16_383) + "oops", lines[^1]);
        Assert.All(lines[1..^1], l => Assert.Equal("", l));
    }

    [Fact]
    public async Task Rows_and_columns_beyond_excels_limits_are_clamped_not_crashed_on()
    {
        var input = Workspace.Path("overflow.xlsx");
        using (var document = SpreadsheetDocument.Create(input, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { CellReference = "A1", CellValue = new CellValue("1") }) { RowIndex = 1U },
                // A damaged file: row 4294967295 (int overflow) and a column reference far past XFD.
                new Row(new Cell { CellReference = "ZZZZZZZZZ9", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("far")) }) { RowIndex = uint.MaxValue }));
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = 1U, Name = "Data" }));
            workbook.Workbook.Save();
        }

        var lines = await File.ReadAllLinesAsync(Single(await RunAsync("sheet.csv", input)));
        Assert.Equal("1", lines[0].TrimStart('﻿'));
        Assert.Equal(1_048_576, lines.Length);
        Assert.EndsWith("far", lines[^1]);
    }

    [Fact]
    public async Task Ordinary_sheets_with_gaps_stay_rectangular()
    {
        var input = Xlsx(Workspace.Path("gaps.xlsx"), ("Data", [["a", null, "c"], [], [null, "e"]]));
        var result = await RunAsync("sheet.csv", input);
        var lines = await File.ReadAllLinesAsync(Single(result));
        Assert.Equal(["a,,c", ",,", ",e,"], lines.Select(l => l.TrimStart('﻿')).ToArray());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Large_workbooks_convert_to_csv_quickly()
    {
        const int rows = 100_000;
        var input = Workspace.Path("large.xlsx");
        using (var document = SpreadsheetDocument.Create(input, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var part = workbook.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(part))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                for (var r = 1; r <= rows; r++)
                {
                    writer.WriteStartElement(new Row { RowIndex = (uint)r });
                    for (var c = 0; c < 10; c++)
                        writer.WriteElement(new Cell { CellReference = $"{(char)('A' + c)}{r}", CellValue = new CellValue((r * 10 + c).ToString(CultureInfo.InvariantCulture)) });
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = 1U, Name = "Big" }));
            workbook.Workbook.Save();
        }

        var stopwatch = Stopwatch.StartNew();
        var csv = Single(await RunAsync("sheet.csv", input));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        var lines = await File.ReadAllLinesAsync(csv);
        Assert.Equal(rows, lines.Length);
        Assert.Equal("1000000,1000001,1000002,1000003,1000004,1000005,1000006,1000007,1000008,1000009", lines[^1]);
    }

    // Fixtures ------------------------------------------------------------------------------------------------

    /// <summary>Encrypted with an owner (permissions) password only: opens without a password but forbids changes.</summary>
    static string OwnerOnlyPdf(string path, string encryption, params string[] bodies)
    {
        TextPdf(path, bodies);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var settings = document.SecuritySettings;
        settings.OwnerPassword = "owner-secret";
        settings.PermitModifyDocument = false;
        settings.PermitAssembleDocument = false;
        settings.PermitAnnotations = false;
        switch (encryption)
        {
            case "rc4-128":
                document.SecurityHandler.SetEncryptionToV2With128Bits();
                break;
            case "aes-128":
                document.SecurityHandler.SetEncryptionToV4UsingAES();
                break;
            default:
                document.SecurityHandler.SetEncryptionToV5();
                break;
        }
        var temp = path + ".tmp";
        document.Save(temp);
        document.Dispose();
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>A valid PDF whose cross-reference offsets all point at the wrong bytes (a classic "damaged" file).</summary>
    static string BrokenXrefPdf(string path, params string[] bodies)
    {
        TextPdf(path, bodies);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        // Shift every object one byte later than the xref says by inserting a space after the header.
        var broken = text.Insert(text.IndexOf('\n') + 1, "% damaged by a bad editor\n");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(broken));
        return path;
    }

    /// <summary>A structurally valid PDF with an empty page tree.</summary>
    static string ZeroPagePdf(string path)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.Latin1);
        return path;
    }
}
