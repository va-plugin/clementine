using System.IO.Compression;
using System.Text;
using Clementine.Engines.Documents.OpenXml;
using Clementine.Engines.Documents.Typography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Clementine.Engines.Documents.Tests.Fixtures;

/// <summary>Builds small document fixtures in code, and reads results back for assertions.</summary>
internal static class DocumentFixtures
{
    public const string Unicode = "Ünïcødé – 東京";

    /// <summary>A PDF whose page N shows a heading "Page N" and the given body text (A4).</summary>
    public static string TextPdf(string path, params string[] bodies) => PdfWithInfo(path, null, null, bodies);

    public static string PdfWithInfo(string path, string? title, string? author, params string[] bodies)
    {
        FontLibrary.EnsureInstalled();
        using var document = new PdfSharp.Pdf.PdfDocument();
        if (title is not null)
            document.Info.Title = title;
        if (author is not null)
            document.Info.Author = author;
        var heading = new XFont("Arial", 24, XFontStyleEx.Bold);
        var body = new XFont("Arial", 11);
        for (var i = 0; i < bodies.Length; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i + 1}", heading, XBrushes.Black, 60, 90);
            var y = 130.0;
            foreach (var line in bodies[i].Split('\n'))
            {
                gfx.DrawString(line, body, XBrushes.Black, 60, y);
                y += 16;
            }
        }
        document.Save(path);
        return path;
    }

    /// <summary>A one-page PDF holding a large, high-quality photo-like JPEG (for compression tests).</summary>
    public static string PdfWithPhoto(string path, int width = 2400, int height = 1800)
    {
        var jpeg = PhotoJpeg(width, height, 95);
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromMillimeter(297);
        page.Height = XUnit.FromMillimeter(210);
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var image = XImage.FromStream(new MemoryStream(jpeg)))
            gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        document.Save(path);
        return path;
    }

    /// <summary>Gradient plus noise: compresses like a photograph.</summary>
    public static byte[] PhotoJpeg(int width, int height, int quality)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(42);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var noise = random.Next(-40, 40);
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)Math.Clamp(x * 255 / width + noise, 0, 255),
                    (byte)Math.Clamp(y * 255 / height + noise, 0, 255),
                    (byte)Math.Clamp(128 + noise, 0, 255)));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }

    public static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A Word document with a heading, Unicode text, a numbered and a bulleted list and a table.</summary>
    public static string Docx(string path)
    {
        using var docx = DocxBuilder.Create(path, "Fixture");
        docx.Heading(1, [RichRun.Plain("Quarterly report")]);
        docx.Paragraph([RichRun.Plain("Intro with "), new RichRun("bold", RunFormat.Bold), RichRun.Plain(" text and " + Unicode + ".")]);
        var numbered = docx.NewList(ordered: true);
        docx.ListItem(numbered, 0, [RichRun.Plain("First step")]);
        docx.ListItem(numbered, 0, [RichRun.Plain("Second step")]);
        docx.ListItem(docx.BulletList, 0, [RichRun.Plain("A bullet")]);
        docx.Table([[[RichRun.Plain("Name")], [RichRun.Plain("Value")]], [[RichRun.Plain("Alpha")], [RichRun.Plain("42")]]], header: true);
        docx.Paragraph([RichRun.Plain("The end.")]);
        docx.Save();
        return path;
    }

    /// <summary>A workbook with typed cells (numbers, a date, a boolean, text) on several sheets.</summary>
    public static string Xlsx(string path, params (string Name, object?[][] Rows)[] sheets)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var styles = workbook.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(new Font()) { Count = 1U },
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2U },
            new Borders(new Border()) { Count = 1U },
            new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true }) { Count = 2U });
        var strings = workbook.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new SharedStringTable();
        var sheetList = workbook.Workbook.AppendChild(new Sheets());
        uint id = 1;
        foreach (var (name, rows) in sheets)
        {
            var part = workbook.AddNewPart<WorksheetPart>();
            var data = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
            for (var r = 0; r < rows.Length; r++)
            {
                var row = new Row { RowIndex = (uint)(r + 1) };
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var reference = $"{(char)('A' + c)}{r + 1}";
                    var cell = rows[r][c] switch
                    {
                        null => null,
                        string s => new Cell { CellReference = reference, DataType = CellValues.SharedString, CellValue = new CellValue(AddString(strings, s)) },
                        bool b => new Cell { CellReference = reference, DataType = CellValues.Boolean, CellValue = new CellValue(b ? "1" : "0") },
                        DateTime d => new Cell { CellReference = reference, StyleIndex = 1U, CellValue = new CellValue(d.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)) },
                        var n => new Cell { CellReference = reference, CellValue = new CellValue(Convert.ToDouble(n, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture)) },
                    };
                    if (cell is not null)
                        row.Append(cell);
                }
                data.Append(row);
            }
            part.Worksheet = new Worksheet(data);
            sheetList.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = id++, Name = name });
        }
        workbook.Workbook.Save();
        return path;
    }

    static int AddString(SharedStringTablePart part, string text)
    {
        part.SharedStringTable!.Append(new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(text)));
        return part.SharedStringTable.Count() - 1;
    }

    /// <summary>A small EPUB 3: two chapters (spine order ≠ file name order), a stylesheet, an image and a cross-chapter link.</summary>
    public static string Epub(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
            writer.Write("application/epub+zip");
        Add(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Add(zip, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:12345</dc:identifier>
                <dc:title>The Clementine Tale</dc:title>
                <dc:creator>Ada Writer</dc:creator>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="c2" href="text/b-second.xhtml" media-type="application/xhtml+xml"/>
                <item id="c1" href="text/a%20first.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style/book.css" media-type="text/css"/>
                <item id="pic" href="images/pic.png" media-type="image/png"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """);
        Add(zip, "OEBPS/text/a first.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title><link rel="stylesheet" href="../style/book.css"/></head>
            <body><h1>Chapter One</h1><p>It began in Tōkyō – 東京.</p><p><img src="../images/pic.png" alt="A red square"/></p>
            <p><a href="b-second.xhtml#later">Jump ahead</a></p></body></html>
            """);
        Add(zip, "OEBPS/text/b-second.xhtml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Two</title></head>
            <body><h1>Chapter Two</h1><p id="later">And it ended happily.</p></body></html>
            """);
        Add(zip, "OEBPS/style/book.css", "h1 { color: #b34700; } p { text-indent: 1em; }");
        var image = zip.CreateEntry("OEBPS/images/pic.png");
        using (var stream = image.Open())
            stream.Write(Png(40, 30, SKColors.Red));
        return path;
    }

    /// <summary>A Pages/Numbers/Keynote-style zip holding a preview JPEG (iWork 2013+) or QuickLook/Preview.pdf (iWork '09).</summary>
    public static string IWork(string path, byte[]? previewJpeg = null, byte[]? previewPdf = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "Index/Document.iwa", "not a real iwa");
        if (previewJpeg is not null)
        {
            AddBytes(zip, "preview.jpg", previewJpeg);
            AddBytes(zip, "preview-micro.jpg", PhotoJpeg(32, 24, 50));
        }
        if (previewPdf is not null)
            AddBytes(zip, "QuickLook/Preview.pdf", previewPdf);
        return path;
    }

    static void AddBytes(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // Reading results ------------------------------------------------------------------------------------------

    public static int PageCount(string pdf, string? password = null)
    {
        using var document = PigDocument.Open(pdf, new ParsingOptions { Password = password ?? "" });
        return document.NumberOfPages;
    }

    /// <summary>Text of each page (letters joined, whitespace collapsed).</summary>
    public static IReadOnlyList<string> PageTexts(string pdf, string? password = null)
    {
        using var document = PigDocument.Open(pdf, new ParsingOptions { Password = password ?? "" });
        return document.GetPages()
            .Select(p => Normalize(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p)))
            .ToList();
    }

    public static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static SKBitmap Decode(string imagePath) => SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException($"{imagePath} isn't a decodable image.");
}
