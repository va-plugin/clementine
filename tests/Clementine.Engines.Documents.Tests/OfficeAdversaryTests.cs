using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Clementine.Core;
using Clementine.Engines.Documents.OpenXml.Word;
using Clementine.Engines.Documents.Spreadsheets;
using Clementine.Engines.Documents.Tests.Fixtures;
using Clementine.TestSupport;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using UglyToad.PdfPig;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>
/// Adversarial checks for the built-in Word / Excel converters (no LibreOffice): nasty but realistic documents generated
/// with OpenXML — very long documents, huge and nested tables, deep lists, RTL/CJK/emoji, odd fonts, pictures in
/// headers, text boxes, charts, tracked changes, comments, fields, mixed page orientation, damaged and protected files;
/// workbooks with formulas, number formats, 100k cells, very wide sheets, merges, hidden sheets and charts. They must
/// convert (or fail with a friendly message), keep the text, and never run scripts or fetch anything.
/// </summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class OfficeAdversaryTests : IDisposable
{
    static readonly DocumentOperationProvider BuiltIn = new() { IgnoreLibreOffice = true };

    readonly TestWorkspace _workspace = new("office-adversary");

    public void Dispose() => _workspace.Dispose();

    IOperation Op(string id) => _workspace.Operation(BuiltIn, id);

    async Task<(string Path, OperationResult Result)> RunAsync(string id, string input, CancellationToken cancellationToken = default)
    {
        var result = await _workspace.RunAsync(Op(id), [input], cancellationToken: cancellationToken);
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output), output);
        return (output, result);
    }

    static IHtmlDocument Parse(string path) => new HtmlParser().ParseDocument(File.ReadAllText(path));

    static string BodyText(IHtmlDocument page) => Normalize(page.Body!.TextContent);

    static string AllText(string pdf) => Normalize(string.Join(' ', PageTexts(pdf)));

    /// <summary>Nothing in the page can run code or reach the network.</summary>
    static void AssertInert(IHtmlDocument page, string html)
    {
        Assert.Empty(page.QuerySelectorAll("script, iframe, frame, object, embed, applet, base, link"));
        foreach (var element in page.All)
        {
            foreach (var attribute in element.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"Event handler {attribute.Name} on <{element.LocalName}>");
                if (attribute.Name is "src")
                    Assert.StartsWith("data:", attribute.Value, StringComparison.Ordinal);
                if (attribute.Name is "href")
                    Assert.Matches(@"^(#|https?:|mailto:|tel:)", attribute.Value);
            }
        }
        // Outside CSS string literals (a hostile font name may legitimately contain "url(" as text).
        var css = Regex.Replace(string.Join('\n', page.QuerySelectorAll("style").Select(s => s.TextContent)), @"""(?:\\.|[^""\\\n])*""", "\"\"");
        Assert.DoesNotMatch(@"url\s*\(", css);
        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
    }

    // OpenXML helpers -------------------------------------------------------------------------------------------------

    const string WordNamespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"";

    const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    static string X(string text) => SecurityElement.Escape(text);

    static string T(string text, string rPr = "") =>
        $"<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";

    static string P(string text, string pPr = "", string rPr = "") =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}{T(text, rPr)}</w:p>";

    static string Cell(string content, string tcPr = "") => $"<w:tc>{(tcPr.Length > 0 ? $"<w:tcPr>{tcPr}</w:tcPr>" : "")}{content}</w:tc>";

    static string Table(IEnumerable<string> rows, string tblPr = "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders>", string grid = "") =>
        $"<w:tbl><w:tblPr>{tblPr}</w:tblPr><w:tblGrid>{grid}</w:tblGrid>{string.Concat(rows)}</w:tbl>";

    static string Row(params string[] cells) => "<w:tr>" + string.Concat(cells) + "</w:tr>";

    static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    static void FeedBytes(OpenXmlPart part, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        part.FeedData(stream);
    }

    /// <summary>A Word package from a body (WordprocessingML) and optional extra parts.</summary>
    static string Word(string path, string body, Action<MainDocumentPart>? parts = null, string? title = null)
    {
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            parts?.Invoke(main);
            Feed(main, $"{Xml}<w:document {WordNamespaces} mc:Ignorable=\"\"><w:body>{body}</w:body></w:document>");
            if (title is not null)
                document.PackageProperties.Title = title;
        }
        return path;
    }

    static string Header(string tag, string content) => $"{Xml}<w:{tag} {WordNamespaces}>{content}</w:{tag}>";

    static string InlinePicture(string relationship, int widthPt, int heightPt, string attribute = "embed", string descr = "picture") =>
        $"""<w:r><w:drawing><wp:inline><wp:extent cx="{widthPt * 12700}" cy="{heightPt * 12700}"/><wp:docPr id="1" name="Picture" descr="{X(descr)}"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="p"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:{attribute}="{relationship}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{widthPt * 12700}" cy="{heightPt * 12700}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""";

    static string Lorem(int i) =>
        $"Paragraph {i}: the quick brown fox jumps over the lazy dog while the committee reviews the quarterly figures, " +
        "adjusts the forecast, and drafts a long sentence so every line wraps across the page several times over.";

    const string SheetNamespaces = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    const string BasicStyles = $"""
        {Xml}<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
        <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
        <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
        <borders count="1"><border/></borders>
        <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellXfs>
        </styleSheet>
        """;

    /// <summary>A workbook from sheets (name, worksheet inner XML, state) with optional styles, shared strings and extra parts.</summary>
    static string Workbook(string path, (string Name, string Inner, string? State)[] sheets, string? styles = null, string? sharedStrings = null,
        string workbookExtra = "", Action<WorkbookPart, IReadOnlyList<WorksheetPart>>? extra = null, string workbookPr = "")
    {
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            Feed(workbook.AddNewPart<WorkbookStylesPart>("rIdStyles"), styles ?? BasicStyles);
            if (sharedStrings is not null)
                Feed(workbook.AddNewPart<SharedStringTablePart>("rIdStrings"), $"{Xml}<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">{sharedStrings}</sst>");
            var parts = new List<WorksheetPart>();
            var list = new StringBuilder();
            for (var i = 0; i < sheets.Length; i++)
            {
                var part = workbook.AddNewPart<WorksheetPart>($"rIdSheet{i + 1}");
                parts.Add(part);
                list.Append($"<sheet name=\"{X(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rIdSheet{i + 1}\"{(sheets[i].State is { } state ? $" state=\"{state}\"" : "")}/>");
            }
            extra?.Invoke(workbook, parts);
            for (var i = 0; i < sheets.Length; i++)
                Feed(parts[i], $"{Xml}<worksheet {SheetNamespaces}>{sheets[i].Inner}</worksheet>");
            Feed(workbook, $"{Xml}<workbook {SheetNamespaces}>{workbookPr}<sheets>{list}</sheets>{workbookExtra}</workbook>");
        }
        return path;
    }

    static string InlineCell(string reference, string text) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(text)}</t></is></c>";

    static string ColumnName(int column)
    {
        var name = "";
        for (; column > 0; column = (column - 1) / 26)
            name = (char)('A' + (column - 1) % 26) + name;
        return name;
    }

    // Word: size and structure ----------------------------------------------------------------------------------------

    [EdgeFact]
    public async Task A_200_page_document_prints_completely_and_in_time()
    {
        var body = new StringBuilder();
        for (var page = 1; page <= 200; page++)
        {
            body.Append(P($"Chapter {page}", "<w:pStyle w:val=\"Heading1\"/><w:outlineLvl w:val=\"0\"/>", "<w:b/><w:sz w:val=\"32\"/>"));
            for (var i = 0; i < 6; i++)
                body.Append(P(Lorem(page * 10 + i)));
            body.Append(P($"End of page {page}"));
            if (page < 200)
                body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
        }
        body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\"/></w:sectPr>");
        var input = Word(_workspace.Path("long.docx"), body.ToString());

        var html = Parse((await RunAsync("doc.html", input)).Path);
        Assert.Contains("End of page 200", BodyText(html));

        var stopwatch = Stopwatch.StartNew();
        var (pdf, _) = await RunAsync("doc.pdf", input);
        stopwatch.Stop();
        var pages = PageTexts(pdf);
        Assert.InRange(pages.Count, 200, 230);
        Assert.Contains("End of page 200", pages[^1]);
        Assert.Contains("Chapter 1", pages[0]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(90), $"A 200-page document took {stopwatch.Elapsed} to print.");
    }

    [EdgeFact]
    public async Task A_huge_table_spans_many_pages_and_keeps_every_row()
    {
        var rows = new List<string>
        {
            "<w:tr><w:trPr><w:tblHeader/></w:trPr>" + string.Concat(Enumerable.Range(1, 6).Select(c => Cell(P($"Head {c}", rPr: "<w:b/>")))) + "</w:tr>",
        };
        for (var r = 1; r <= 3000; r++)
            rows.Add(Row([.. Enumerable.Range(1, 6).Select(c => Cell(P($"R{r}C{c}")))]));
        var grid = string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"1500\"/>", 6));
        var input = Word(_workspace.Path("big table.docx"), P("Before the table") + Table(rows, grid: grid) + P("After the table"));

        var page = Parse((await RunAsync("doc.html", input)).Path);
        Assert.Equal(3001, page.QuerySelectorAll("tr").Length);
        Assert.Single(page.QuerySelectorAll("thead"));

        var (pdf, _) = await RunAsync("doc.pdf", input);
        var text = AllText(pdf);
        Assert.Contains("R3000C6", text);
        Assert.Contains("After the table", text);
        var pages = PageTexts(pdf);
        Assert.True(pages.Count > 30, $"Only {pages.Count} pages.");
        // The header row repeats on the following pages.
        Assert.True(pages.Count(p => p.Contains("Head 1", StringComparison.Ordinal)) > pages.Count / 2, "The table header doesn't repeat.");
    }

    [Fact]
    public async Task Deeply_nested_and_broken_lists_convert()
    {
        var numbering = $"""
            {Xml}<w:numbering {WordNamespaces}>
            <w:abstractNum w:abstractNumId="0">{string.Concat(Enumerable.Range(0, 9).Select(l =>
                $"<w:lvl w:ilvl=\"{l}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{(l % 3 == 0 ? "decimal" : l % 3 == 1 ? "lowerLetter" : "upperRoman")}\"/><w:lvlText w:val=\"%{l + 1}.\"/><w:pPr><w:ind w:left=\"{(l + 1) * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>"))}</w:abstractNum>
            <w:abstractNum w:abstractNumId="1"><w:numStyleLink w:val="LoopStyle"/></w:abstractNum>
            <w:abstractNum w:abstractNumId="2"><w:lvl w:ilvl="0"><w:start w:val="32767"/><w:numFmt w:val="upperLetter"/><w:lvlText w:val="%1%2%3%9)"/></w:lvl></w:abstractNum>
            <w:abstractNum w:abstractNumId="3"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol"/></w:rPr></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
            <w:num w:numId="3"><w:abstractNumId w:val="2"/></w:num>
            <w:num w:numId="4"><w:abstractNumId w:val="3"/></w:num>
            <w:num w:numId="5"><w:abstractNumId w:val="99"/></w:num>
            </w:numbering>
            """;
        var styles = $"""
            {Xml}<w:styles {WordNamespaces}>
            <w:style w:type="numbering" w:styleId="LoopStyle"><w:name w:val="Loop"/><w:pPr><w:numPr><w:numId w:val="2"/></w:numPr></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="A"><w:name w:val="A"/><w:basedOn w:val="B"/></w:style>
            <w:style w:type="paragraph" w:styleId="B"><w:name w:val="B"/><w:basedOn w:val="A"/><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr></w:style>
            </w:styles>
            """;
        var body = new StringBuilder();
        for (var l = 0; l < 9; l++)
            body.Append(P($"Level {l} item", $"<w:numPr><w:ilvl w:val=\"{l}\"/><w:numId w:val=\"1\"/></w:numPr>"));
        for (var l = 8; l >= 0; l--)
            body.Append(P($"Back at {l}", $"<w:numPr><w:ilvl w:val=\"{l}\"/><w:numId w:val=\"1\"/></w:numPr>"));
        body.Append(P("Level 42 clamps", "<w:numPr><w:ilvl w:val=\"42\"/><w:numId w:val=\"1\"/></w:numPr>"));
        body.Append(P("Negative level", "<w:numPr><w:ilvl w:val=\"-3\"/><w:numId w:val=\"1\"/></w:numPr>"));
        body.Append(P("Style link loop", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr>"));
        body.Append(P("Huge start", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr>"));
        body.Append(P("Huge start again", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr>"));
        body.Append(P("Symbol bullet", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"4\"/></w:numPr>"));
        body.Append(P("Missing abstract", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"5\"/></w:numPr>"));
        body.Append(P("Missing instance", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"77\"/></w:numPr>"));
        body.Append(P("Style cycle", "<w:pStyle w:val=\"A\"/>"));
        for (var i = 0; i < 2000; i++)
            body.Append(P($"Long list {i}", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        var input = Word(_workspace.Path("lists.docx"), body.ToString(), main =>
        {
            Feed(main.AddNewPart<NumberingDefinitionsPart>("rIdNumbering"), numbering);
            Feed(main.AddNewPart<StyleDefinitionsPart>("rIdStyles"), styles);
        });

        var (path, _) = await RunAsync("doc.html", input);
        Assert.True(new FileInfo(path).Length < 5_000_000, $"The page is {new FileInfo(path).Length:N0} bytes.");
        var page = Parse(path);
        var text = BodyText(page);
        foreach (var expected in new[] { "Level 8 item", "Back at 0", "Level 42 clamps", "Negative level", "Style link loop", "Huge start again", "Symbol bullet", "Missing abstract", "Missing instance", "Style cycle", "Long list 1999" })
            Assert.Contains(expected, text);
        Assert.True(page.QuerySelectorAll("li li li li li li li li li").Length > 0, "Nine levels of nesting were flattened.");
        Assert.Contains("2000.", text);
        Assert.Contains("•", text);
    }

    [Fact]
    public async Task Tables_nested_twelve_deep_and_malformed_tables_convert()
    {
        var inner = P("Innermost cell");
        for (var depth = 0; depth < 12; depth++)
            inner = Table([Row(Cell(P($"Depth {depth}") + inner), Cell(P($"Side {depth}")))]) + "<w:p/>";
        var malformed =
            Table([Row(Cell(P("continue without restart"), "<w:vMerge/>"), Cell(P("x"))), Row(Cell(P("below"), "<w:vMerge/>"), Cell(P("y")))]) +
            Table([Row(Cell(P("span 64"), "<w:gridSpan w:val=\"9999\"/>")), Row(Cell(P("narrow")))], grid: "<w:gridCol w:w=\"0\"/><w:gridCol w:w=\"-500\"/>") +
            Table(["<w:tr/>", Row("<w:tc/>"), Row(Cell(P("after empty cells")))]) +
            Table([]) +
            Table([Row(Cell(P("hmerge start"), "<w:hMerge w:val=\"restart\"/>"), Cell(P("hmerge cont"), "<w:hMerge/>"), Cell(P("third")))]) +
            Table([Row(Cell(P("percent width")))], tblPr: "<w:tblW w:w=\"250%\" w:type=\"pct\"/><w:tblInd w:w=\"-99999\" w:type=\"dxa\"/>") +
            Table([Row(Cell(P("in a content control")))]).Replace("<w:tr>", "<w:sdt><w:sdtContent><w:tr>").Replace("</w:tr>", "</w:tr></w:sdtContent></w:sdt>");
        var input = Word(_workspace.Path("nested.docx"), inner + malformed + P("The end"));

        var page = Parse((await RunAsync("doc.html", input)).Path);
        var text = BodyText(page);
        // ("below" is a vMerge continuation cell: like Word, its content is covered by the cell above.)
        foreach (var expected in new[] { "Innermost cell", "Depth 11", "Side 0", "continue without restart", "span 64", "narrow", "after empty cells", "hmerge start", "third", "percent width", "in a content control", "The end" })
            Assert.Contains(expected, text);
        Assert.Equal(12 + 6, page.QuerySelectorAll("table").Length); // the row-less table is dropped
    }

    // Word: scripts and fonts -----------------------------------------------------------------------------------------

    const string Arabic = "مرحبا بالعالم";
    const string Hebrew = "שלום עולם";
    const string Chinese = "你好，世界。表格和中文字体";
    const string Japanese = "こんにちは世界、縦書きテスト";
    const string Korean = "안녕하세요 세계";
    const string Emoji = "Emoji 😀 👩‍👩‍👧‍👦 🇯🇵 👍🏽 ❤️";
    const string Combining = "Zalgo e\u0301\u0302\u0303 and Thai ภาษาไทย and Devanagari हिन्दी";

    [EdgeFact]
    public async Task Rtl_cjk_emoji_and_combining_text_survive()
    {
        var body =
            P(Arabic, "<w:bidi/><w:jc w:val=\"left\"/>", "<w:rtl/><w:rFonts w:cs=\"Arial\"/>") +
            P(Hebrew, "<w:bidi/>", "<w:rtl/>") +
            P(Chinese, rPr: "<w:rFonts w:ascii=\"SimSun\" w:eastAsia=\"SimSun\"/><w:lang w:eastAsia=\"zh-CN\"/>") +
            P(Japanese, "<w:textDirection w:val=\"tbRl\"/>", "<w:rFonts w:eastAsia=\"MS Mincho\"/>") +
            P(Korean, rPr: "<w:rFonts w:eastAsia=\"Malgun Gothic\"/>") +
            P(Emoji) +
            P(Combining) +
            "<w:p><w:r><w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/></w:r><w:r><w:sym w:font=\"Symbol\" w:char=\"F061\"/></w:r><w:r><w:sym w:char=\"ZZZZ\"/></w:r>" + T(" symbols") + "</w:p>" +
            P("Mixed English עברית and العربية in one line", "<w:bidi/>");
        var input = Word(_workspace.Path("scripts.docx"), body);

        var page = Parse((await RunAsync("doc.html", input)).Path);
        var text = BodyText(page);
        foreach (var expected in new[] { Arabic, Hebrew, Chinese, Japanese, Korean, Emoji, Combining, "✓α symbols" })
            Assert.Contains(Normalize(expected), text);
        Assert.Equal("rtl", page.QuerySelectorAll("p").First(p => p.TextContent.Contains(Hebrew)).GetAttribute("dir"));

        var pdf = AllText((await RunAsync("doc.pdf", input)).Path);
        foreach (var expected in new[] { "你好", "世界", "こんにちは", "안녕하세요", "symbols" })
            Assert.Contains(expected, pdf);
    }

    [Fact]
    public async Task Hostile_font_names_and_style_values_cannot_break_out_of_the_stylesheet()
    {
        const string evil = "Evil\"; } </style><script>alert(1)</script><style> body { background: url(http://127.0.0.1:9/x) } \\";
        var styles = $"""
            {Xml}<w:styles {WordNamespaces}>
            <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="{X(evil)}" w:hAnsi="{X(evil)}" w:eastAsia="{X(evil)}"/><w:sz w:val="-4"/></w:rPr></w:rPrDefault></w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="&lt;/style&gt;&lt;script&gt;x()&lt;/script&gt;"/></w:style>
            </w:styles>
            """;
        var fontTable = $"""{Xml}<w:fonts {WordNamespaces}><w:font w:name="{X(evil)}"><w:family w:val="&lt;/style&gt;"/></w:font></w:fonts>""";
        var body =
            P("Evil font run", rPr: $"<w:rFonts w:ascii=\"{X(evil)}\"/><w:color w:val=\"red;}} body{{background:url(//evil)}}\"/><w:highlight w:val=\"red;x\"/><w:u w:val=\"single\" w:color=\"#123;}}\"/>") +
            P("Weird spacing", "<w:spacing w:line=\"-240\" w:before=\"NaN\" w:after=\"1e400\"/><w:ind w:left=\"Infinity\" w:hanging=\"-1e9\"/><w:jc w:val=\"&quot;;}}\"/>") +
            P("Missing font", rPr: "<w:rFonts w:ascii=\"ZZZ Totally Missing Font 9000\"/><w:sz w:val=\"99999\"/>") +
            P("Theme font without a theme", rPr: "<w:rFonts w:asciiTheme=\"majorHAnsi\" w:eastAsiaTheme=\"minorEastAsia\"/>") +
            P("Tabs", "<w:tabs><w:tab w:val=\"&quot;;}}\" w:pos=\"2000\" w:leader=\"&lt;x&gt;\"/></w:tabs>") +
            "<w:p><w:r><w:t>left</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>right</w:t></w:r></w:p>";
        var input = Word(_workspace.Path("fonts.docx"), body, main =>
        {
            Feed(main.AddNewPart<StyleDefinitionsPart>("rIdStyles"), styles);
            Feed(main.AddNewPart<FontTablePart>("rIdFonts"), fontTable);
            Feed(main.AddNewPart<DocumentSettingsPart>("rIdSettings"), $"{Xml}<w:settings {WordNamespaces}><w:defaultTabStop w:val=\"-720\"/></w:settings>");
        }, title: "</title><script>alert('title')</script>");

        var (path, _) = await RunAsync("doc.html", input);
        var html = await File.ReadAllTextAsync(path);
        var page = Parse(path);
        AssertInert(page, html);
        Assert.Equal("</title><script>alert('title')</script>", page.Title);
        // Exactly the generator's own stylesheet; nothing escaped from it.
        Assert.Single(page.QuerySelectorAll("style"));
        var text = BodyText(page);
        foreach (var expected in new[] { "Evil font run", "Weird spacing", "Missing font", "Theme font without a theme", "left", "right" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("alert(1)", text);
    }

    // Word: pictures, shapes, charts ----------------------------------------------------------------------------------

    static byte[] Emf()
    {
        using var stream = new MemoryStream();
        using (var reference = new System.Drawing.Bitmap(1, 1))
        using (var graphics = System.Drawing.Graphics.FromImage(reference))
        {
            var hdc = graphics.GetHdc();
            try
            {
                using var metafile = new System.Drawing.Imaging.Metafile(stream, hdc, System.Drawing.Imaging.EmfType.EmfOnly);
                using var drawing = System.Drawing.Graphics.FromImage(metafile);
                drawing.FillRectangle(System.Drawing.Brushes.OrangeRed, 0, 0, 200, 100);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        return stream.ToArray();
    }

    static byte[] Tiff()
    {
        using var bitmap = new System.Drawing.Bitmap(64, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.Clear(System.Drawing.Color.SeaGreen);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Tiff);
        return stream.ToArray();
    }

    [EdgeFact]
    public async Task Pictures_shapes_charts_and_linked_images_degrade_gracefully_and_fetch_nothing()
    {
        using var server = new ConnectionRecorder();
        var external = $"http://127.0.0.1:{server.Port}/pixel.png";
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="20" height="20"><script>alert(1)</script><image xlink:href="http://127.0.0.1:1/x.png" width="20" height="20"/><rect width="20" height="20" fill="red"/></svg>""";

        var textBox = """
            <w:r><mc:AlternateContent><mc:Choice Requires="wps"><w:drawing><wp:anchor distT="0" distB="0" distL="114300" distR="114300" simplePos="0" relativeHeight="1" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1"><wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="column"><wp:posOffset>99999999999</wp:posOffset></wp:positionH><wp:positionV relativeFrom="paragraph"><wp:posOffset>-5000</wp:posOffset></wp:positionV><wp:extent cx="2000000" cy="600000"/><wp:wrapNone/><wp:docPr id="2" name="Text Box 2"/><a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp><wps:spPr><a:prstGeom prst="rect"/><a:solidFill><a:schemeClr val="accent1"><a:lumMod val="40000"/><a:lumOff val="60000"/></a:schemeClr></a:solidFill><a:ln w="12700"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></a:ln></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:t>Floating text box words</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice><mc:Fallback><w:pict><v:shape style="width:150pt;height:40pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>Fallback text box</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>
            """;
        var chart = """<w:r><w:drawing><wp:inline><wp:extent cx="5486400" cy="3200400"/><wp:docPr id="3" name="Chart 1"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart r:id="rIdNoSuchChart"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""";
        var smartArt = """<w:r><w:drawing><wp:inline><wp:extent cx="1000000" cy="1000000"/><wp:docPr id="4" name="Diagram"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram"/></a:graphic></wp:inline></w:drawing></w:r>""";
        var group = $"""<w:r><w:drawing><wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="2" behindDoc="1" locked="0" layoutInCell="1" allowOverlap="1"><wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="page"><wp:align>center</wp:align></wp:positionH><wp:positionV relativeFrom="page"><wp:posOffset>0</wp:posOffset></wp:positionV><wp:extent cx="3000000" cy="1000000"/><wp:wrapTopAndBottom/><wp:docPr id="5" name="Group"/><a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"><wpg:wgp><wpg:grpSpPr/><wps:wsp><wps:spPr/><wps:txbx><w:txbxContent><w:p><w:r><w:t>Grouped shape text</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp><pic:pic><pic:nvPicPr><pic:cNvPr id="6" name="g"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rIdPng"/></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="500000" cy="500000"/></a:xfrm></pic:spPr></pic:pic></wpg:wgp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>""";
        var shapeNoText = """<w:r><w:drawing><wp:inline><wp:extent cx="500000" cy="500000"/><wp:docPr id="7" name="Arrow"/><a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp><wps:spPr><a:prstGeom prst="rightArrow"/></wps:spPr><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""";
        var vml = $"""<w:r><w:pict><v:shape style="position:absolute;mso-position-horizontal:right;width:2in;height:1in"><v:imagedata r:id="rIdExternalVml" o:href="{external}"/></v:shape><v:rect style="width:100pt;height:20pt" o:hr="t"/></w:pict></w:r>""";
        var ole = """<w:r><w:object><v:shape style="width:60pt;height:40pt"><v:imagedata r:id="rIdEmf"/></v:shape><o:OLEObject Type="Embed" ProgID="Excel.Sheet.12" ShapeID="_x0000_i1025" r:id="rIdNoSuchOle"/></w:object></w:r>""";
        var math = """<w:p><m:oMathPara><m:oMath><m:r><m:t>E=mc²</m:t></m:r></m:oMath></m:oMathPara></w:p>""";
        var body =
            "<w:p>" + T("Pictures: ") + InlinePicture("rIdPng", 80, 40) + InlinePicture("rIdLinked", 40, 40, "link") + InlinePicture("rIdSvg", 20, 20) +
            InlinePicture("rIdEmf", 100, 50) + InlinePicture("rIdTiff", 64, 32) + InlinePicture("rIdCorrupt", 30, 30) + InlinePicture("rIdNoSuchPart", 30, 30) +
            InlinePicture("rIdExternalEmbed", 30, 30) + InlinePicture("rIdHuge", 400, 400) + "</w:p>" +
            "<w:p>" + textBox + T("Paragraph with a floating box") + "</w:p>" +
            "<w:p>" + chart + "</w:p>" + "<w:p>" + smartArt + "</w:p>" + "<w:p>" + group + T("After the group") + "</w:p>" +
            "<w:p>" + shapeNoText + vml + ole + T("Shapes and OLE") + "</w:p>" + math +
            "<w:altChunk r:id=\"rIdChunk\"/>" + P("The end");
        var input = Word(_workspace.Path("pictures.docx"), body, main =>
        {
            FeedBytes(main.AddImagePart(ImagePartType.Png, "rIdPng"), Png(160, 80, new SKColor(0x20, 0x60, 0xE0)));
            FeedBytes(main.AddImagePart(ImagePartType.Svg, "rIdSvg"), Encoding.UTF8.GetBytes(svg));
            FeedBytes(main.AddImagePart(ImagePartType.Emf, "rIdEmf"), Emf());
            FeedBytes(main.AddImagePart(ImagePartType.Tiff, "rIdTiff"), Tiff());
            FeedBytes(main.AddImagePart(ImagePartType.Png, "rIdCorrupt"), Encoding.ASCII.GetBytes("\x89PNG not really"));
            FeedBytes(main.AddImagePart(ImagePartType.Png, "rIdHuge"), Png(6000, 6000, SKColors.Black));
            main.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", new Uri(external), "rIdLinked");
            main.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", new Uri(external + "?vml"), "rIdExternalVml");
            main.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", new Uri(external + "?embed"), "rIdExternalEmbed");
            var chunk = main.AddAlternativeFormatImportPart(AlternativeFormatImportPartType.Html, "rIdChunk");
            Feed(chunk, $"<html><body><script>alert(1)</script><img src=\"{external}?chunk\">Chunk text</body></html>");
        });

        var (htmlPath, htmlResult) = await RunAsync("doc.html", input);
        var html = await File.ReadAllTextAsync(htmlPath);
        var page = Parse(htmlPath);
        AssertInert(page, html);
        Assert.DoesNotContain($":{server.Port}", html);
        var text = BodyText(page);
        foreach (var expected in new[] { "Pictures:", "Floating text box words", "Paragraph with a floating box", "Grouped shape text", "After the group", "Shapes and OLE", "E=mc²", "The end" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("Fallback text box", text);
        Assert.DoesNotContain("Chunk text", text);
        // PNG, SVG, the EMF and TIFF rendered to PNG, the group's PNG, the huge PNG and the OLE preview.
        var images = page.QuerySelectorAll("img").Select(i => i.GetAttribute("src")!).ToList();
        Assert.True(images.Count >= 6, $"Only {images.Count} pictures.");
        Assert.Contains(images, i => i.StartsWith("data:image/svg+xml", StringComparison.Ordinal));
        Assert.True(images.Count(i => i.StartsWith("data:image/png", StringComparison.Ordinal)) >= 4);
        Assert.Contains(htmlResult.Warnings, w => w.Contains("Linked pictures", StringComparison.Ordinal));
        Assert.Contains(htmlResult.Warnings, w => w.Contains("Charts", StringComparison.Ordinal));
        Assert.Contains(htmlResult.Warnings, w => w.Contains("embedded from other files", StringComparison.Ordinal));
        Assert.NotNull(page.QuerySelector("span.chart"));

        var (pdfPath, _) = await RunAsync("doc.pdf", input);
        var pdf = AllText(pdfPath);
        Assert.Contains("Floating text box words", pdf);
        Assert.Contains("The end", pdf);
        Assert.Equal(0, server.Connections);
    }

    [EdgeFact]
    public async Task Pictures_in_headers_and_page_fields_in_footers()
    {
        var header = Header("hdr", "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>" + InlinePicture("rIdLogo", 60, 20) + T("Company header") + "</w:p>");
        var footer = Header("ftr", """
            <w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:t xml:space="preserve">Page </w:t></w:r><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE \* MERGEFORMAT </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r><w:r><w:t xml:space="preserve"> of </w:t></w:r><w:fldSimple w:instr=" NUMPAGES "><w:r><w:t>9</w:t></w:r></w:fldSimple><w:r><w:t xml:space="preserve"> — “quotes” \ back\slash </w:t></w:r></w:p>
            """);
        var body = P("First page") + "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>" + P("Second page") + "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>" + P("Third page") +
                   "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHeader\"/><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/><w:footerReference w:type=\"even\" r:id=\"rIdMissing\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\"/></w:sectPr>";
        var input = Word(_workspace.Path("headers.docx"), body, main =>
        {
            var headerPart = main.AddNewPart<HeaderPart>("rIdHeader");
            FeedBytes(headerPart.AddImagePart(ImagePartType.Png, "rIdLogo"), Png(120, 40, new SKColor(0xE0, 0x40, 0x20)));
            Feed(headerPart, header);
            Feed(main.AddNewPart<FooterPart>("rIdFooter"), footer);
        });

        var (htmlPath, htmlResult) = await RunAsync("doc.html", input);
        var page = Parse(htmlPath);
        Assert.NotNull(page.QuerySelector(".doc-header img[src^='data:image/png']"));
        // The web page shows the header picture: only the PDF leaves it out (and says so).
        Assert.DoesNotContain(htmlResult.Warnings, w => w.Contains("Pictures in headers", StringComparison.Ordinal));

        var (pdf, result) = await RunAsync("doc.pdf", input);
        Assert.Contains(result.Warnings, w => w.Contains("Pictures in headers and footers", StringComparison.Ordinal));
        var pages = PageTexts(pdf);
        Assert.Equal(3, pages.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains("Company header", pages[i]);
            Assert.Contains($"Page {i + 1} of 3", pages[i]);
        }
        Assert.Contains("back\\slash", pages[0]);
    }

    // Word: revisions, comments, fields -------------------------------------------------------------------------------

    [Fact]
    public async Task Tracked_changes_show_the_final_text_and_comments_stay_out()
    {
        const string who = "w:id=\"{0}\" w:author=\"Reviewer\" w:date=\"2026-01-01T00:00:00Z\"";
        string A(int id) => string.Format(CultureInfo.InvariantCulture, who, id);
        var body =
            $"<w:p>{T("Final ")}<w:ins {A(1)}>{T("inserted")}</w:ins><w:del {A(2)}><w:r><w:delText>DELETED-WORDS</w:delText></w:r></w:del>{T(" text.")}</w:p>" +
            $"<w:p><w:moveFrom {A(3)}>{T("MOVED-AWAY-OLD")}</w:moveFrom></w:p>" +
            $"<w:p><w:moveTo {A(4)}>{T("Moved here")}</w:moveTo></w:p>" +
            $"<w:p><w:ins {A(5)}><w:del {A(6)}>{T("INSERTED-THEN-DELETED")}</w:del></w:ins>{T("Survivor")}</w:p>" +
            $"<w:p><w:del {A(7)}>{T("DELETED-WITH-T")}</w:del></w:p>" +
            $"<w:p>{T("Format change", $"<w:b/><w:rPrChange {A(8)}><w:rPr><w:i/></w:rPr></w:rPrChange>")}</w:p>" +
            $"<w:p><w:pPr><w:pPrChange {A(9)}><w:pPr><w:jc w:val=\"center\"/></w:pPr></w:pPrChange></w:pPr>{T("Paragraph property change")}</w:p>" +
            $"<w:del {A(10)}><w:p>{T("BLOCK-LEVEL-DELETED")}</w:p></w:del>" +
            $"<w:ins {A(11)}><w:p>{T("Block level inserted")}</w:p></w:ins>" +
            Table([
                $"<w:tr><w:trPr><w:del {A(12)}/></w:trPr>{Cell($"<w:p><w:del {A(13)}><w:r><w:delText>DELETED-ROW</w:delText></w:r></w:del></w:p>")}</w:tr>",
                $"<w:tr><w:trPr><w:ins {A(14)}/></w:trPr>{Cell(P("Inserted row"))}</w:tr>",
                Row(Cell($"<w:p><w:commentRangeStart w:id=\"0\"/>{T("Commented words")}<w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>")),
            ]) +
            $"<w:p><w:r><w:t>Hidden:</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>VANISHED-TEXT</w:t></w:r><w:r><w:rPr><w:specVanish/></w:rPr><w:t> shown</w:t></w:r></w:p>" +
            $"<w:p><w:pPr><w:rPr><w:vanish/></w:rPr></w:pPr><w:r><w:rPr><w:vanish/></w:rPr><w:t>HIDDEN-PARAGRAPH</w:t></w:r></w:p>" +
            P("The end");
        var input = Word(_workspace.Path("revisions.docx"), body, main =>
        {
            Feed(main.AddNewPart<WordprocessingCommentsPart>("rIdComments"), Header("comments",
                $"<w:comment w:id=\"0\" w:author=\"Reviewer\" w:initials=\"R\"><w:p>{T("SECRET-REVIEWER-COMMENT")}</w:p></w:comment>"));
        });

        var (path, _) = await RunAsync("doc.html", input);
        var page = Parse(path);
        var text = BodyText(page);
        foreach (var expected in new[] { "Final inserted text.", "Moved here", "Survivor", "Format change", "Paragraph property change", "Block level inserted", "Inserted row", "Commented words", "Hidden: shown", "The end" })
            Assert.Contains(expected, text);
        foreach (var hidden in new[] { "DELETED-WORDS", "MOVED-AWAY-OLD", "INSERTED-THEN-DELETED", "DELETED-WITH-T", "BLOCK-LEVEL-DELETED", "DELETED-ROW", "SECRET-REVIEWER-COMMENT", "VANISHED-TEXT", "HIDDEN-PARAGRAPH" })
            Assert.DoesNotContain(hidden, text);
        Assert.Contains("font-weight: 700", page.QuerySelectorAll("style").Single().TextContent);
    }

    [Fact]
    public async Task Fields_show_their_cached_results_and_toc_links_work()
    {
        string Begin(string instruction) =>
            $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\">{X(instruction)}</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>";
        const string end = "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
        string TocLine(string anchor, string title, string pageNumber) =>
            $"<w:p><w:pPr><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9350\"/></w:tabs></w:pPr>" +
            $"<w:hyperlink w:anchor=\"{anchor}\" w:history=\"1\">{T(title)}<w:r><w:tab/></w:r>{Begin($" PAGEREF {anchor} \\h ")}{T(pageNumber)}{end}</w:hyperlink></w:p>";
        var body =
            "<w:sdt><w:sdtPr><w:docPartObj><w:docPartGallery w:val=\"Table of Contents\"/><w:docPartUnique/></w:docPartObj></w:sdtPr><w:sdtContent>" +
            P("Contents") +
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9350\"/></w:tabs></w:pPr>" + Begin(" TOC \\o \"1-3\" \\h \\z \\u ") +
            $"<w:hyperlink w:anchor=\"_Toc1\" w:history=\"1\">{T("Chapter One")}<w:r><w:tab/></w:r>{Begin(" PAGEREF _Toc1 \\h ")}{T("2")}{end}</w:hyperlink></w:p>" +
            TocLine("_Toc2", "Chapter Two", "3") +
            "<w:p>" + end + "</w:p>" +
            "</w:sdtContent></w:sdt>" +
            $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:bookmarkStart w:id=\"1\" w:name=\"_Toc1\"/>{T("Chapter One")}<w:bookmarkEnd w:id=\"1\"/></w:p>" +
            $"<w:p>{T("Today is ")}{Begin(" DATE \\@ \"d MMMM yyyy\" ")}{T("1 March 2026")}{end}{T(", page ")}<w:fldSimple w:instr=\" PAGE \">{T("1")}</w:fldSimple></w:p>" +
            $"<w:p>{T("Dear ")}{Begin(" MERGEFIELD FirstName ")}{T("«FirstName»")}{end}{T(",")}</w:p>" +
            $"<w:p>{Begin(" IF 1 = 1 \"yes\" \"no\" ")}{Begin(" QUOTE \"inner\" ")}{T("nested result")}{end}{end}{T(" after nested")}</w:p>" +
            $"<w:p>{Begin(" HYPERLINK \"javascript:alert(1)\" ")}{T("evil field link")}{end} {Begin(" HYPERLINK \"\\\\\\\\evil-host\\\\share\\\\x.docx\" ")}{T("unc field link")}{end}</w:p>" +
            $"<w:p>{Begin(" HYPERLINK \"https://example.com/a?b=1&c=\\\"2\\\"\" \\o \"tip\" ")}{T("web field link")}{end}</w:p>" +
            $"<w:p><w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"Check1\"/><w:checkBox><w:default w:val=\"1\"/></w:checkBox></w:ffData></w:fldChar></w:r><w:r><w:instrText> FORMCHECKBOX </w:instrText></w:r>{end}{T(" checkbox label")}</w:p>" +
            $"<w:p><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>{T("stray separate")}<w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            $"<w:p>{T("Chapter Two text")}<w:bookmarkStart w:id=\"2\" w:name=\"_Toc2\"/><w:bookmarkEnd w:id=\"2\"/></w:p>" +
            P("The end");
        var input = Word(_workspace.Path("fields.docx"), body, main =>
        {
            main.AddHyperlinkRelationship(new Uri("javascript:alert(2)", UriKind.RelativeOrAbsolute), true, "rIdJs");
        });

        var (path, _) = await RunAsync("doc.html", input);
        var html = await File.ReadAllTextAsync(path);
        var page = Parse(path);
        AssertInert(page, html);
        var text = BodyText(page);
        foreach (var expected in new[] { "Contents", "Chapter One", "Chapter Two", "Today is 1 March 2026, page 1", "Dear «FirstName»,", "nested result after nested", "evil field link", "unc field link", "web field link", "checkbox label", "stray separate", "Chapter Two text", "The end" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("TOC \\o", text);
        Assert.DoesNotContain("PAGEREF", text);
        Assert.DoesNotContain("MERGEFIELD", text);
        Assert.NotNull(page.QuerySelector("a[href='#_Toc1']"));
        Assert.NotNull(page.QuerySelector("a[href='#_Toc2']"));
        Assert.NotNull(page.QuerySelector("a[id='_Toc1']"));
        Assert.Null(page.QuerySelectorAll("a").FirstOrDefault(a => a.TextContent.Contains("unc field link") && a.HasAttribute("href")));
        Assert.NotNull(page.QuerySelectorAll("a[href^='https://example.com/a']").FirstOrDefault());
    }

    // Word: sections -------------------------------------------------------------------------------------------------

    [EdgeFact]
    public async Task Sections_with_mixed_orientation_print_on_the_right_paper()
    {
        const string margins = "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>";
        var body =
            P("Portrait A4 page") +
            $"<w:p><w:pPr><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>{margins}</w:sectPr></w:pPr></w:p>" +
            P("Landscape Letter page") +
            $"<w:p><w:pPr><w:sectPr><w:type w:val=\"nextPage\"/><w:pgSz w:w=\"15840\" w:h=\"12240\" w:orient=\"landscape\"/>{margins}</w:sectPr></w:pPr></w:p>" +
            P("Landscape declared but not swapped") +
            $"<w:p><w:pPr><w:sectPr><w:type w:val=\"oddPage\"/><w:pgSz w:w=\"11906\" w:h=\"16838\" w:orient=\"landscape\"/><w:pgMar w:top=\"-2000\" w:right=\"99999\" w:bottom=\"0\" w:left=\"0\"/></w:sectPr></w:pPr></w:p>" +
            P("Two columns, continuous") + P(Lorem(1)) + P(Lorem(2)) +
            $"<w:p><w:pPr><w:sectPr><w:type w:val=\"continuous\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/>{margins}<w:cols w:num=\"2\" w:space=\"720\"/></w:sectPr></w:pPr></w:p>" +
            P("Back to portrait Letter") +
            $"<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/>{margins}</w:sectPr>";
        var input = Word(_workspace.Path("orientation.docx"), body);

        var (pdf, _) = await RunAsync("doc.pdf", input);
        using var document = PdfDocument.Open(pdf);
        var pages = Enumerable.Range(1, document.NumberOfPages).Select(document.GetPage).ToList();
        (double W, double H) SizeOf(string text) => pages.Where(p => Normalize(p.Text).Contains(text, StringComparison.Ordinal) || Normalize(string.Join(' ', p.GetWords().Select(w => w.Text))).Contains(text, StringComparison.Ordinal))
            .Select(p => (p.Width, p.Height)).First();
        static void AssertSize((double W, double H) actual, double width, double height)
        {
            Assert.InRange(actual.W, width - 1.5, width + 1.5);
            Assert.InRange(actual.H, height - 1.5, height + 1.5);
        }
        AssertSize(SizeOf("Portrait A4 page"), 595.3, 841.9);
        AssertSize(SizeOf("Landscape Letter page"), 792, 612);
        AssertSize(SizeOf("Landscape declared but not swapped"), 841.9, 595.3);
        AssertSize(SizeOf("Back to portrait Letter"), 612, 792);
    }

    // Word: damaged and protected files ---------------------------------------------------------------------------

    [Fact]
    public async Task Damaged_protected_and_hostile_word_files_fail_with_a_friendly_message()
    {
        var cases = new List<(string Name, string Path)>
        {
            ("encrypted", EncryptedOfficeFixtures.Docx(_workspace.Path("bad/encrypted.docx"))),
            ("empty file", _workspace.WriteText("bad/empty.docx", "")),
            ("random bytes", _workspace.WriteText("bad/random.docx", "\u0001\u0002 this is not a zip at all")),
        };
        // A zip with no main document.
        var noMain = _workspace.Path("bad/no main.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(noMain, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            writer.Write("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        }
        cases.Add(("no main part", noMain));
        // Truncated: the first 60% of a real document.
        var real = Word(_workspace.Path("bad/real.docx"), string.Concat(Enumerable.Range(0, 400).Select(Lorem).Select(t => P(t))));
        var bytes = await File.ReadAllBytesAsync(real);
        var truncated = _workspace.Path("bad/truncated.docx");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length * 6 / 10)]);
        cases.Add(("truncated zip", truncated));
        // Invalid XML in the body, and a DTD with an external entity (XXE).
        cases.Add(("invalid xml", RawWord(_workspace.Path("bad/invalid xml.docx"), $"{Xml}<w:document {WordNamespaces}><w:body><w:p><w:r><w:t>unclosed</w:r></w:p></w:body></w:document>")));
        var secret = _workspace.WriteText("bad/secret.txt", "TOP-SECRET-FILE-CONTENTS");
        cases.Add(("xxe", RawWord(_workspace.Path("bad/xxe.docx"),
            $"<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><w:document {WordNamespaces}><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>")));
        cases.Add(("empty body", Word(_workspace.Path("bad/empty body.docx"), "")));

        foreach (var (name, path) in cases)
        {
            foreach (var id in new[] { "doc.html" })
            {
                var before = Directory.GetFiles(Path.GetDirectoryName(path)!).Length;
                try
                {
                    var result = await _workspace.RunAsync(Op(id), [path]);
                    var output = Assert.Single(result.Outputs);
                    Assert.DoesNotContain("TOP-SECRET-FILE-CONTENTS", await File.ReadAllTextAsync(output));
                    Assert.Fail($"{name} → {id} converted ({output}).");
                }
                catch (OperationException error)
                {
                    Assert.False(string.IsNullOrWhiteSpace(error.Message), name);
                    Assert.DoesNotContain("Exception", error.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("TOP-SECRET-FILE-CONTENTS", error.Message);
                    if (name == "encrypted")
                        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
                }
                Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
            }
        }
    }

    string Rename(string path, string relative)
    {
        var target = _workspace.Path(relative);
        File.Move(path, target);
        return target;
    }

    static string RawWord(string path, string documentXml)
    {
        Word(path, P("placeholder"));
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(documentXml);
        }
        return path;
    }

    [Fact]
    public async Task An_unterminated_field_does_not_hide_the_rest_of_the_document()
    {
        // A field "begin" with no "separate" or "end" (left behind by buggy templating tools).
        var body = $"<w:p>{T("Before the field ")}<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r></w:p>" +
                   P("Paragraph after") + Table([Row(Cell(P("Cell after it")))]) +
                   $"<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> MERGEFIELD Name </w:instrText></w:r>{T("text in the same paragraph stays hidden")}</w:p>" +
                   P("The end");
        var input = Word(_workspace.Path("open field.docx"), body);

        var text = BodyText(Parse((await RunAsync("doc.html", input)).Path));
        foreach (var expected in new[] { "Before the field", "Paragraph after", "Cell after it", "The end" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("PAGE", text);
        Assert.DoesNotContain("MERGEFIELD", text);
    }

    [Fact]
    public async Task Footnotes_that_reference_themselves_or_each_other_are_listed_once()
    {
        static string Note(int id, string text, int reference) =>
            $"<w:footnote w:id=\"{id}\"><w:p><w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteRef/></w:r>{T(text)}<w:r><w:footnoteReference w:id=\"{reference}\"/></w:r></w:p></w:footnote>";
        var footnotes = $"{Xml}<w:footnotes {WordNamespaces}>{Note(1, "Loop", 1)}{Note(2, "Ping", 3)}{Note(3, "Pong", 2)}</w:footnotes>";
        var body = $"<w:p>{T("Body text")}<w:r><w:footnoteReference w:id=\"1\"/></w:r>{T(" and ")}<w:r><w:footnoteReference w:id=\"2\"/></w:r>" +
                   $"{T(" and again")}<w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>" + P("The end");
        var input = Word(_workspace.Path("note loop.docx"), body, main => Feed(main.AddNewPart<FootnotesPart>("rIdFootnotes"), footnotes));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var page = Parse((await RunAsync("doc.html", input, timeout.Token)).Path);
        Assert.Equal(3, page.QuerySelectorAll("section.notes > ol > li").Length);
        var text = BodyText(page);
        foreach (var expected in new[] { "Body text", "Loop", "Ping", "Pong", "The end" })
            Assert.Contains(expected, text);
        Assert.Single(page.QuerySelectorAll("#fnref1"));
    }

    [Fact]
    public async Task Crafted_grid_offsets_and_list_starts_stay_small_and_fast()
    {
        var numbering = $"""
            {Xml}<w:numbering {WordNamespaces}>
            <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="2147483647"/><w:numFmt w:val="upperLetter"/><w:lvlText w:val="%1."/></w:lvl></w:abstractNum>
            <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1)"/></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            <w:num w:numId="2"><w:abstractNumId w:val="1"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="-2147483648"/></w:lvlOverride></w:num>
            </w:numbering>
            """;
        var body = new StringBuilder();
        body.Append("<w:tbl><w:tblPr/><w:tblGrid/><w:tr><w:trPr><w:gridBefore w:val=\"200000\"/><w:gridAfter w:val=\"200000\"/></w:trPr>")
            .Append(Cell(P("Shifted cell"))).Append("</w:tr><w:tr>").Append(Cell(P("Next row"))).Append("</w:tr></w:tbl>");
        for (var i = 0; i < 200; i++)
            body.Append(P($"Huge start {i}", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        body.Append(P("Overflowing override", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr>"));
        // A tall vertical merge (each continuation used to search every row above it).
        var merged = new List<string> { Row(Cell(P("Merged top"), "<w:vMerge w:val=\"restart\"/>"), Cell(P("side 0"))) };
        for (var r = 1; r < 3000; r++)
            merged.Add(Row(Cell(P(""), "<w:vMerge/>"), Cell(P($"side {r}"))));
        body.Append(Table(merged)).Append(P("The end"));
        var input = Word(_workspace.Path("crafted.docx"), body.ToString(), main => Feed(main.AddNewPart<NumberingDefinitionsPart>("rIdNumbering"), numbering));

        var stopwatch = Stopwatch.StartNew();
        var (path, _) = await RunAsync("doc.html", input);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        Assert.True(new FileInfo(path).Length < 3_000_000, $"The page is {new FileInfo(path).Length:N0} bytes.");
        var page = Parse(path);
        var text = BodyText(page);
        foreach (var expected in new[] { "Shifted cell", "Next row", "Huge start 199", "Overflowing override", "Merged top", "side 2999", "The end" })
            Assert.Contains(expected, text);
        Assert.Equal("3000", page.QuerySelectorAll("td").First(td => td.TextContent.Contains("Merged top", StringComparison.Ordinal)).GetAttribute("rowspan"));
        Assert.All(page.QuerySelectorAll(".mk"), marker => Assert.True(marker.TextContent.Length <= 60, $"A {marker.TextContent.Length}-character list label."));
    }

    [Fact]
    public async Task Mislabelled_and_damaged_office_files_fail_with_a_friendly_message()
    {
        var docx = Word(_workspace.Path("mislabelled/real.docx"), P("Word text"));
        var xlsx = Workbook(_workspace.Path("mislabelled/real.xlsx"), [("S", "<sheetData><row r=\"1\">" + InlineCell("A1", "cell") + "</row></sheetData>", null)]);
        var xlsxAsDocx = _workspace.Path("mislabelled/workbook.docx");
        var docxAsXlsx = _workspace.Path("mislabelled/document.xlsx");
        var docxAsPptx = _workspace.Path("mislabelled/document.pptx");
        File.Copy(xlsx, xlsxAsDocx);
        File.Copy(docx, docxAsXlsx);
        File.Copy(docx, docxAsPptx);
        var invalidXml = RawWord(_workspace.Path("mislabelled/invalid xml.docx"), $"{Xml}<w:document {WordNamespaces}><w:body><w:p><w:r><w:t>unclosed</w:r></w:p></w:body></w:document>");

        foreach (var (id, input) in new[]
                 {
                     ("doc.html", xlsxAsDocx), ("doc.txt", xlsxAsDocx), ("doc.txt", invalidXml), ("sheet.html", docxAsXlsx), ("sheet.csv", docxAsXlsx),
                     ("slides.txt", docxAsPptx),
                 })
        {
            var error = await Assert.ThrowsAsync<OperationException>(() => _workspace.RunAsync(Op(id), [input]));
            Assert.DoesNotContain("Exception", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("root XML element", error.Message, StringComparison.Ordinal);
        }

        // A theme colour that isn't hex, used with a shade: the default colour is used instead.
        const string theme = $"""
            {Xml}<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Bad"><a:themeElements>
            <a:clrScheme name="Bad"><a:dk1><a:srgbClr val="zzzzzz"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="-12345"/></a:dk2><a:lt2><a:srgbClr val="EEECE1"/></a:lt2>
            <a:accent1><a:srgbClr val="zzzzzz"/></a:accent1><a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4>
            <a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
            <a:fontScheme name="F"><a:majorFont><a:latin typeface="Arial"/></a:majorFont><a:minorFont><a:latin typeface="Arial"/></a:minorFont></a:fontScheme>
            </a:themeElements></a:theme>
            """;
        var themed = Word(_workspace.Path("mislabelled/bad theme.docx"),
            P("Shaded theme colour", rPr: "<w:color w:val=\"000000\" w:themeColor=\"accent1\" w:themeShade=\"BF\"/>") +
            P("Tinted dark text", rPr: "<w:color w:val=\"000000\" w:themeColor=\"text2\" w:themeTint=\"99\"/>"),
            main => Feed(main.AddNewPart<ThemePart>("rIdTheme"), theme));
        var text = BodyText(Parse((await RunAsync("doc.html", themed)).Path));
        Assert.Contains("Shaded theme colour", text);
        Assert.Contains("Tinted dark text", text);
    }

    // Excel ------------------------------------------------------------------------------------------------------------

    const string FormatStyles = $"""
        {Xml}<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
        <numFmts count="12">
          <numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm"/>
          <numFmt numFmtId="165" formatCode="&quot;$&quot;#,##0.00_);[Red]\(&quot;$&quot;#,##0.00\)"/>
          <numFmt numFmtId="166" formatCode="[$€-407]\ #,##0.00"/>
          <numFmt numFmtId="167" formatCode="0.000%"/>
          <numFmt numFmtId="168" formatCode=";;;"/>
          <numFmt numFmtId="169" formatCode="&quot;Yes&quot;;&quot;Yes&quot;;&quot;No&quot;"/>
          <numFmt numFmtId="170" formatCode="[h]:mm:ss"/>
          <numFmt numFmtId="171" formatCode="[$-F800]dddd\,\ mmmm\ dd\,\ yyyy"/>
          <numFmt numFmtId="172" formatCode="[$¥-411]#,##0"/>
          <numFmt numFmtId="173" formatCode="#,##0.00\ [$CHF]"/>
          <numFmt numFmtId="174" formatCode="0;;"/>
          <numFmt numFmtId="175" formatCode="[&gt;=1000000]0.0,,&quot;M&quot;;[&gt;=1000]0.0,&quot;K&quot;;0"/>
        </numFmts>
        <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
        <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
        <borders count="1"><border/></borders>
        <cellXfs count="20">
          <xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="164"/><xf numFmtId="9"/><xf numFmtId="10"/><xf numFmtId="165"/><xf numFmtId="166"/>
          <xf numFmtId="167"/><xf numFmtId="168"/><xf numFmtId="169"/><xf numFmtId="170"/><xf numFmtId="171"/><xf numFmtId="172"/><xf numFmtId="173"/>
          <xf numFmtId="44"/><xf numFmtId="12"/><xf numFmtId="11"/><xf numFmtId="49"/><xf numFmtId="174"/><xf numFmtId="175"/>
        </cellXfs>
        </styleSheet>
        """;

    [Fact]
    public async Task Formulas_show_cached_values_and_formats_match_excel()
    {
        var culture = CultureInfo.CurrentCulture;
        string N(double value, string format) => value.ToString(format, culture);
        // 09:30 isn't exact in binary (it used to show as 09:29).
        var march14 = new DateTime(2026, 3, 14, 9, 30, 0).ToOADate();
        var rows = new (string Label, string Cell, string Expected)[]
        {
            ("sum", "<c r=\"B{0}\"><f>SUM(1,2)</f><v>3</v></c>", "3"),
            ("shared", "<c r=\"B{0}\"><f t=\"shared\" si=\"0\"/><v>42.5</v></c>", N(42.5, "0.#")),
            ("string result", "<c r=\"B{0}\" t=\"str\"><f>\"a\"&amp;\"b\"</f><v>ab</v></c>", "ab"),
            ("error result", "<c r=\"B{0}\" t=\"e\"><f>1/0</f><v>#DIV/0!</v></c>", "#DIV/0!"),
            ("bool result", "<c r=\"B{0}\" t=\"b\"><f>TRUE()</f><v>1</v></c>", "TRUE"),
            ("array", "<c r=\"B{0}\"><f t=\"array\" ref=\"B{0}\">SUM(A1:A2*2)</f><v>8</v></c>", "8"),
            ("date", "<c r=\"B{0}\" s=\"1\"><v>" + march14.ToString(CultureInfo.InvariantCulture) + "</v></c>", new DateTime(2026, 3, 14).ToString(culture.DateTimeFormat.ShortDatePattern.Replace("'", ""), culture)),
            ("datetime", "<c r=\"B{0}\" s=\"2\"><v>" + march14.ToString(CultureInfo.InvariantCulture) + "</v></c>", "2026-03-14 09:30"),
            ("datetime as excel writes it", "<c r=\"B{0}\" s=\"2\"><v>46095.395833333336</v></c>", "2026-03-14 09:30"),
            ("iso date", "<c r=\"B{0}\" t=\"d\" s=\"2\"><v>2026-03-14T09:30:00</v></c>", "2026-03-14 09:30"),
            ("hidden number", "<c r=\"B{0}\" s=\"8\"><v>123456789</v></c>", ""),
            ("hidden inline text", "<c r=\"B{0}\" s=\"8\" t=\"inlineStr\"><is><t>HIDDEN-INLINE</t></is></c>", ""),
            ("hidden shared text", "<c r=\"B{0}\" s=\"8\" t=\"s\"><v>0</v></c>", ""),
            ("yes for zero", "<c r=\"B{0}\" s=\"9\"><v>0</v></c>", "No"),
            ("yes for one", "<c r=\"B{0}\" s=\"9\"><v>1</v></c>", "Yes"),
            ("negatives hidden", "<c r=\"B{0}\" s=\"18\"><v>-5</v></c>", ""),
            ("percent", "<c r=\"B{0}\" s=\"3\"><v>0.256</v></c>", "26%"),
            ("percent2", "<c r=\"B{0}\" s=\"4\"><v>-0.0125</v></c>", "-" + N(1.25, "0.00") + "%"),
            ("dollars negative", "<c r=\"B{0}\" s=\"5\"><v>-1234.5</v></c>", "($" + N(1234.5, "#,##0.00") + ")"),
            ("euro", "<c r=\"B{0}\" s=\"6\"><v>1234.5</v></c>", "€ " + N(1234.5, "#,##0.00")),
            ("percent3", "<c r=\"B{0}\" s=\"7\"><v>0.12345</v></c>", N(12.345, "0.000") + "%"),
            ("yen", "<c r=\"B{0}\" s=\"12\"><v>1234567</v></c>", "¥" + N(1234567, "#,##0")),
            ("chf", "<c r=\"B{0}\" s=\"13\"><v>99.5</v></c>", N(99.5, "#,##0.00") + " CHF"),
            ("elapsed", "<c r=\"B{0}\" s=\"10\"><v>1.75</v></c>", "42:00:00"),
            ("fraction", "<c r=\"B{0}\" s=\"15\"><v>3.5</v></c>", "3 1/2"),
            ("scientific", "<c r=\"B{0}\" s=\"16\"><v>123456</v></c>", N(1.23, "0.00") + "E+05"),
            ("text format", "<c r=\"B{0}\" s=\"17\"><v>00123</v></c>", "123"),
            ("conditional M", "<c r=\"B{0}\" s=\"19\"><v>2500000</v></c>", N(2.5, "0.0") + "M"),
            ("conditional K", "<c r=\"B{0}\" s=\"19\"><v>2500</v></c>", N(2.5, "0.0") + "K"),
            ("big", "<c r=\"B{0}\"><v>1E+300</v></c>", "1E+300"),
            ("tiny", "<c r=\"B{0}\"><v>-1.5E-300</v></c>", "-1.5E-300"),
            ("long general", "<c r=\"B{0}\"><v>0.1234567890123</v></c>", N(0.123456789, "0.#########")),
            ("not a number", "<c r=\"B{0}\"><v>abc</v></c>", "abc"),
            ("bad shared index", "<c r=\"B{0}\" t=\"s\"><v>99999</v></c>", ""),
            ("negative shared index", "<c r=\"B{0}\" t=\"s\"><v>-1</v></c>", ""),
            ("bad style", "<c r=\"B{0}\" s=\"9999\"><v>7</v></c>", "7"),
            ("negative style", "<c r=\"B{0}\" s=\"-3\"><v>8</v></c>", "8"),
            ("shared string", "<c r=\"B{0}\" t=\"s\"><v>0</v></c>", "Shared <b>&amp; text"),
            ("rich string", "<c r=\"B{0}\" t=\"s\"><v>1</v></c>", "Rich run two"),
        };
        var sheet = new StringBuilder("<cols><col min=\"1\" max=\"1\" width=\"20\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"40\" customWidth=\"1\"/></cols><sheetData>");
        for (var i = 0; i < rows.Length; i++)
            sheet.Append($"<row r=\"{i + 1}\">{InlineCell($"A{i + 1}", rows[i].Label)}{string.Format(CultureInfo.InvariantCulture, rows[i].Cell, i + 1)}</row>");
        sheet.Append("</sheetData>");
        var input = Workbook(_workspace.Path("formats.xlsx"), [("Formats", sheet.ToString(), null)], FormatStyles,
            "<si><t>Shared &lt;b&gt;&amp;amp; text</t></si><si><r><rPr><b/><color rgb=\"FFFF0000\"/></rPr><t>Rich run </t></r><r><t>two</t></r><rPh sb=\"0\" eb=\"1\"><t>PHONETIC</t></rPh></si>");

        var page = Parse((await RunAsync("sheet.html", input)).Path);
        var cells = page.QuerySelectorAll("tr").Select(tr => tr.QuerySelectorAll("td").Select(td => td.TextContent).ToList()).ToList();
        var problems = new List<string>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = cells.FirstOrDefault(c => c.Count > 0 && c[0] == rows[i].Label);
            var shown = row is { Count: > 1 } ? row[1] : "(row missing)";
            if (shown != rows[i].Expected)
                problems.Add($"{rows[i].Label}: expected \"{rows[i].Expected}\", got \"{shown}\"");
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Assert.DoesNotContain("PHONETIC", BodyText(page));
        Assert.DoesNotContain("HIDDEN-INLINE", BodyText(page));
    }

    [EdgeFact]
    public async Task A_hundred_thousand_cells_convert_and_print_in_time()
    {
        var data = new StringBuilder("<sheetData>");
        for (var r = 1; r <= 10_000; r++)
        {
            data.Append($"<row r=\"{r}\">");
            for (var c = 1; c <= 10; c++)
                data.Append(c % 2 == 0 ? $"<c r=\"{ColumnName(c)}{r}\"><v>{r * c}.5</v></c>" : InlineCell($"{ColumnName(c)}{r}", $"r{r}c{c}"));
            data.Append("</row>");
        }
        data.Append("</sheetData>");
        var input = Workbook(_workspace.Path("100k.xlsx"), [("Data", data.ToString(), null)]);

        var stopwatch = Stopwatch.StartNew();
        var (html, htmlResult) = await RunAsync("sheet.html", input);
        var htmlTime = stopwatch.Elapsed;
        Assert.Empty(htmlResult.Warnings);
        var page = Parse(html);
        Assert.Equal(10_000, page.QuerySelectorAll("tr").Length);
        Assert.Contains("r10000c9", page.Body!.TextContent);
        Assert.True(htmlTime < TimeSpan.FromSeconds(30), $"HTML took {htmlTime}.");

        // Printing must finish inside Edge's time limit (it takes about a minute on a 2018 laptop).
        var (pdf, _) = await RunAsync("sheet.pdf", input);
        var pages = PageTexts(pdf);
        Assert.Contains("r10000c9", pages[^1]);
        Assert.Contains("r1c1", pages[0]);
    }

    [Fact]
    public void Printed_sheets_are_capped_for_edge_while_web_pages_keep_more()
    {
        static string Sheet(int rows, int columns)
        {
            var data = new StringBuilder("<sheetData>");
            for (var r = 1; r <= rows; r++)
            {
                data.Append($"<row r=\"{r}\">");
                for (var c = 1; c <= columns; c++)
                    data.Append($"<c r=\"{ColumnName(c)}{r}\"><v>{r * c}</v></c>");
                data.Append("</row>");
            }
            return data.Append("</sheetData>").ToString();
        }
        var culture = CultureInfo.InvariantCulture;

        var big = Workbook(_workspace.Path("cap/big.xlsx"), [("Data", Sheet(12_000, 10), null)]);
        var web = XlsxHtml.Convert(big, "big", culture, CancellationToken.None);
        Assert.Equal(120_000, web.Cells);
        Assert.Empty(web.Warnings);
        var print = XlsxHtml.Convert(big, "big", culture, CancellationToken.None, forPrint: true);
        Assert.Equal(XlsxHtml.MaxPrintCells, print.Cells);
        Assert.Contains(print.Warnings, w => w.Contains("\"Data\" is very large", StringComparison.Ordinal));

        // The sheets of a printed workbook share the budget.
        var two = Workbook(_workspace.Path("cap/two.xlsx"), [("One", Sheet(8_000, 10), null), ("Two", Sheet(8_000, 10), null)]);
        var both = XlsxHtml.Convert(two, "two", culture, CancellationToken.None, forPrint: true);
        Assert.Equal(XlsxHtml.MaxPrintCells, both.Cells);
        Assert.Contains(both.Warnings, w => w.Contains("\"Two\" is very large", StringComparison.Ordinal));
        Assert.DoesNotContain(both.Warnings, w => w.Contains("\"One\"", StringComparison.Ordinal));

        var csv = Enumerable.Range(0, 15_000).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 10).Select(c => $"r{r}c{c}").ToList()).ToList();
        Assert.Equal(150_000, CsvHtml.Convert(csv, "csv").Cells);
        var printedCsv = CsvHtml.Convert(csv, "csv", forPrint: true);
        Assert.Equal(XlsxHtml.MaxPrintCells, printedCsv.Cells);
        Assert.Contains(printedCsv.Warnings, w => w.Contains($"{10_000.ToString("N0", CultureInfo.CurrentCulture)} rows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Very_wide_sheets_merges_hidden_sheets_and_charts_degrade_gracefully()
    {
        // 300 columns (more than are shown), a cell far out at XFD, merges that overlap, run backwards or cover a hidden column.
        var wide = new StringBuilder("<cols><col min=\"3\" max=\"3\" hidden=\"1\"/><col min=\"5\" max=\"16384\" width=\"3\"/></cols><sheetData><row r=\"1\">");
        for (var c = 1; c <= 300; c++)
            wide.Append(InlineCell($"{ColumnName(c)}1", $"W{c}"));
        wide.Append(InlineCell("XFD1", "FAR-AWAY")).Append("</row>");
        wide.Append("<row r=\"2\">" + InlineCell("A2", "Merged block") + InlineCell("B2", "COVERED-BY-MERGE") + "</row>");
        wide.Append("<row>" + InlineCell("H3", "row without a number") + "</row>");
        wide.Append("<row r=\"1048576\">" + InlineCell("A1048576", "last row") + "</row>");
        wide.Append("</sheetData><mergeCells><mergeCell ref=\"A2:D5\"/><mergeCell ref=\"B3:F4\"/><mergeCell ref=\"Z9:A1\"/><mergeCell ref=\"A2\"/><mergeCell ref=\"nonsense\"/></mergeCells>");

        var huge = new StringBuilder("<sheetData>");
        for (var r = 1; r <= 30_000; r++)
            huge.Append($"<row r=\"{r}\">{InlineCell($"A{r}", $"row {r}")}</row>");
        huge.Append("</sheetData>");

        var input = Workbook(_workspace.Path("wide.xlsx"),
        [
            ("Wide", wide.ToString(), null),
            ("Secret", "<sheetData><row r=\"1\">" + InlineCell("A1", "HIDDEN-SHEET-TEXT") + "</row></sheetData>", "hidden"),
            ("VerySecret", "<sheetData><row r=\"1\">" + InlineCell("A1", "VERY-HIDDEN-TEXT") + "</row></sheetData>", "veryHidden"),
            ("Chart sheet data", "<sheetData><row r=\"1\">" + InlineCell("A1", "Data with a chart") + "</row></sheetData><drawing r:id=\"rIdDrawing\"/>", null),
            ("Long", huge.ToString(), null),
            ("Empty", "<sheetData/>", null),
            ("<script>alert(1)</script>", "<sheetData><row r=\"1\">" + InlineCell("A1", "<img src=x onerror=alert(1)>") + "</row></sheetData><headerFooter><oddHeader>&amp;L&amp;\"Arial,Bold\"&amp;14&lt;/style&gt;&lt;script&gt;x&amp;C&amp;A&amp;R&amp;P of &amp;N &amp;K00FF00</oddHeader></headerFooter>", null),
        ], extra: (workbook, sheets) =>
        {
            var drawing = sheets[3].AddNewPart<DrawingsPart>("rIdDrawing");
            Feed(drawing, $"{Xml}<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>8</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>15</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to><xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart r:id=\"rIdChart\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>");
            Feed(drawing.AddNewPart<ChartPart>("rIdChart"), $"{Xml}<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart><c:plotArea><c:layout/></c:plotArea></c:chart></c:chartSpace>");
        }, workbookExtra: "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Wide!$A:$A,Wide!$1:$1</definedName><definedName name=\"_xlnm.Print_Area\" localSheetId=\"4\">#REF!</definedName></definedNames>");

        var stopwatch = Stopwatch.StartNew();
        var (path, result) = await RunAsync("sheet.html", input);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Took {stopwatch.Elapsed}.");
        var html = await File.ReadAllTextAsync(path);
        var page = Parse(path);
        AssertInert(page, html);
        var text = BodyText(page);
        Assert.Contains("W1", text);
        Assert.Contains("W256", text);
        Assert.DoesNotContain("W257", text);
        Assert.DoesNotContain("FAR-AWAY", text);
        Assert.Contains("Merged block", text);
        Assert.DoesNotContain("COVERED-BY-MERGE", text);
        Assert.Contains("row without a number", text);
        Assert.DoesNotContain(page.QuerySelectorAll("td"), td => td.TextContent == "W3"); // hidden column C
        Assert.Equal("4", page.QuerySelectorAll("td").First(td => td.TextContent == "Merged block").GetAttribute("rowspan"));
        Assert.DoesNotContain("HIDDEN-SHEET-TEXT", text);
        Assert.DoesNotContain("VERY-HIDDEN-TEXT", text);
        Assert.Contains("Data with a chart", text);
        Assert.Contains("row 20000", text);
        Assert.DoesNotContain("row 20001", text);
        Assert.Contains("<img src=x onerror=alert(1)>", text);
        Assert.Contains(result.Warnings, w => w.Contains("Charts", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("\"Wide\" is very large", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("\"Long\" is very large", StringComparison.Ordinal));
        Assert.Equal(["Wide", "Chart sheet data", "Long", "<script>alert(1)</script>"], page.QuerySelectorAll("h2.sheet-name").Select(h => h.TextContent));
        Assert.Single(page.QuerySelectorAll("style"));
    }

    [Fact]
    public async Task Damaged_protected_and_hostile_workbooks_fail_with_a_friendly_message()
    {
        var secret = _workspace.WriteText("badx/secret.txt", "TOP-SECRET-FILE-CONTENTS");
        var cases = new List<(string Name, string Path)>
        {
            ("encrypted", EncryptedOfficeFixtures.Xlsx(_workspace.Path("badx/encrypted.xlsx"))),
            ("empty file", _workspace.WriteText("badx/empty.xlsx", "")),
            ("random", _workspace.WriteText("badx/random.xlsx", "PK\u0003\u0004 nope")),
            ("no sheets", Workbook(_workspace.Path("badx/no sheets.xlsx"), [])),
            ("only hidden", Workbook(_workspace.Path("badx/only hidden.xlsx"), [("H", "<sheetData><row r=\"1\">" + InlineCell("A1", "HIDDEN") + "</row></sheetData>", "hidden")])),
        };
        var xxe = Workbook(_workspace.Path("badx/xxe.xlsx"), [("S", "<sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row></sheetData>", null)], sharedStrings: "<si><t>placeholder</t></si>");
        using (var zip = System.IO.Compression.ZipFile.Open(xxe, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.EndsWith("sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write($"<?xml version=\"1.0\"?><!DOCTYPE sst [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>&xxe;</t></si></sst>");
        }

        foreach (var (name, path) in cases)
        {
            foreach (var id in new[] { "sheet.html", "sheet.csv" })
            {
                var error = await Assert.ThrowsAsync<OperationException>(() => _workspace.RunAsync(Op(id), [path]));
                Assert.DoesNotContain("Exception", error.Message, StringComparison.Ordinal);
                if (name == "encrypted")
                    Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The XXE workbook may convert (with the shared strings dropped) or fail, but never reads the file.
        try
        {
            var (output, _) = await RunAsync("sheet.html", xxe);
            Assert.DoesNotContain("TOP-SECRET-FILE-CONTENTS", await File.ReadAllTextAsync(output));
        }
        catch (OperationException error)
        {
            Assert.DoesNotContain("TOP-SECRET-FILE-CONTENTS", error.Message);
        }
    }

    [Fact]
    public async Task Hostile_workbook_styles_and_links_stay_inert()
    {
        const string evil = "Evil\"; } </style><script>alert(1)</script> \\";
        var styles = $$"""
            {{Xml}}<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <numFmts count="1"><numFmt numFmtId="164" formatCode="&quot;&lt;/style&gt;&lt;script&gt;&quot;0"/></numFmts>
            <fonts count="2"><font><sz val="NaN"/><name val="{{X(evil)}}"/></font><font><sz val="1e308"/><color rgb="red;}x{"/><name val="{{X(evil)}}"/></font></fonts>
            <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><gradientFill degree="NaN"><stop position="0"><color rgb="FF0000FF"/></stop><stop position="1"><color theme="99"/></stop></gradientFill></fill></fills>
            <borders count="1"><border><left style="&quot;;}"><color indexed="200"/></left></border></borders>
            <cellXfs count="2"><xf numFmtId="164" fontId="1" fillId="2" borderId="0"><alignment horizontal="&quot;;}" vertical="x" indent="999999"/></xf><xf numFmtId="0" fontId="77" fillId="-1" borderId="5"/></cellXfs>
            <colors><indexedColors><rgbColor rgb="ZZZZZZZZ"/></indexedColors></colors>
            </styleSheet>
            """;
        var sheet = "<sheetFormatPr defaultRowHeight=\"-15\" defaultColWidth=\"NaN\"/><cols><col min=\"1\" max=\"1\" width=\"1e308\"/></cols><sheetData><row r=\"1\" ht=\"1e308\" customHeight=\"1\">" +
                    "<c r=\"A1\" s=\"0\"><v>5</v></c>" + InlineCell("B1", "js link") + InlineCell("C1", "unc link") + InlineCell("D1", "web link") + "<c r=\"E1\" s=\"1\"><v>6</v></c></row></sheetData>" +
                    "<hyperlinks><hyperlink ref=\"B1\" r:id=\"rIdJs\"/><hyperlink ref=\"C1\" r:id=\"rIdUnc\"/><hyperlink ref=\"D1\" r:id=\"rIdWeb\"/><hyperlink ref=\"E1\" r:id=\"rIdMissing\"/></hyperlinks>" +
                    "<pageSetup paperSize=\"999\" scale=\"-50\" orientation=\"sideways\"/><pageMargins left=\"-1\" right=\"1e9\" top=\"NaN\" bottom=\"0\" header=\"0\" footer=\"0\"/>";
        var input = Workbook(_workspace.Path("hostile styles.xlsx"), [("S", sheet, null)], styles, extra: (_, sheets) =>
        {
            sheets[0].AddHyperlinkRelationship(new Uri("javascript:alert(1)", UriKind.RelativeOrAbsolute), true, "rIdJs");
            sheets[0].AddHyperlinkRelationship(new Uri(@"\\evil-host\share\x.xlsx", UriKind.RelativeOrAbsolute), true, "rIdUnc");
            sheets[0].AddHyperlinkRelationship(new Uri("https://example.com/ok"), true, "rIdWeb");
        });

        var (path, _) = await RunAsync("sheet.html", input);
        var html = await File.ReadAllTextAsync(path);
        var page = Parse(path);
        AssertInert(page, html);
        Assert.Single(page.QuerySelectorAll("style"));
        Assert.NotNull(page.QuerySelector("a[href='https://example.com/ok']"));
        Assert.Equal(1, page.QuerySelectorAll("a").Length);
        var text = BodyText(page);
        Assert.Contains("js link", text);
        Assert.Contains("unc link", text);
    }

    [Fact]
    public async Task A_comma_decimal_culture_never_leaks_into_the_css()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var xlsx = RichOfficeFixtures.RichXlsx(_workspace.Path("de.xlsx"));
            var docx = RichOfficeFixtures.RichDocx(_workspace.Path("de.docx"));
            foreach (var (id, input) in new[] { ("sheet.html", xlsx), ("doc.html", docx) })
            {
                var page = Parse((await RunAsync(id, input)).Path);
                var css = string.Join('\n', page.QuerySelectorAll("style").Select(s => s.TextContent)) + string.Join('\n', page.All.Select(e => e.GetAttribute("style") ?? ""));
                Assert.DoesNotMatch(@"\d,\d+(pt|px|%|em)", css);
                Assert.DoesNotMatch(@"zoom: \d+,\d", css);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // LibreOffice preference --------------------------------------------------------------------------------------

    [LibreOfficeFact]
    public async Task With_libreoffice_installed_word_and_excel_pdfs_still_use_it()
    {
        var withLibreOffice = new DocumentOperationProvider();
        var xlsx = RichOfficeFixtures.RichXlsx(_workspace.Path("lo.xlsx"));
        var pdf = Assert.Single((await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "sheet.pdf"), [xlsx])).Outputs);
        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("LibreOffice", document.Information.Producer);

        var docx = RichOfficeFixtures.RichDocx(_workspace.Path("lo.docx"));
        var html = Assert.Single((await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "doc.html"), [docx])).Outputs);
        var generator = Parse(html).QuerySelector("meta[name=generator]")?.GetAttribute("content") ?? "";
        Assert.DoesNotContain("Clementine", generator);
    }

    const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";

    /// <summary>A Word file with linked pictures (body, header, VML), an attached template and a web hyperlink.</summary>
    string LinkedPicturesDocx(string name, string external)
    {
        var header = Header("hdr", "<w:p>" + InlinePicture("rIdHeaderLink", 40, 20, "link") + T("Header text") + "</w:p>");
        var vml = $"""<w:r><w:pict><v:shape style="width:40pt;height:40pt"><v:imagedata r:id="rIdVml" o:title="vml"/></v:shape></w:pict></w:r>""";
        var body = "<w:p>" + T("Linked: ") + InlinePicture("rIdLinked", 40, 40, "link") + InlinePicture("rIdPng", 40, 20) + "</w:p>" +
                   "<w:p>" + vml + T("VML picture") + "</w:p>" +
                   $"<w:p><w:hyperlink r:id=\"rIdWeb\">{T("web link")}</w:hyperlink></w:p>" + P("The end") +
                   "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHeader\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\"/></w:sectPr>";
        return Word(_workspace.Path(name), body, main =>
        {
            FeedBytes(main.AddImagePart(ImagePartType.Png, "rIdPng"), Png(80, 40, new SKColor(0x20, 0x60, 0xE0)));
            main.AddExternalRelationship(ImageRelationship, new Uri(external + "?inline"), "rIdLinked");
            main.AddExternalRelationship(ImageRelationship, new Uri(external + "?vml"), "rIdVml");
            main.AddHyperlinkRelationship(new Uri("https://example.com/kept"), true, "rIdWeb");
            var headerPart = main.AddNewPart<HeaderPart>("rIdHeader");
            headerPart.AddExternalRelationship(ImageRelationship, new Uri(external + "?header"), "rIdHeaderLink");
            Feed(headerPart, header);
            var settings = main.AddNewPart<DocumentSettingsPart>("rIdSettings");
            settings.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate", new Uri(external + "?template"), "rIdTemplate");
            Feed(settings, $"{Xml}<w:settings {WordNamespaces}><w:attachedTemplate r:id=\"rIdTemplate\"/></w:settings>");
        });
    }

    [Fact]
    public void Links_to_outside_resources_are_cut_from_the_copy_libreoffice_gets()
    {
        var input = LinkedPicturesDocx("links.docx", "http://127.0.0.1:9/pixel.png");
        // Also an "internal" relationship whose target is spelled as a network path.
        const string relsName = "word/_rels/document.xml.rels";
        using (var zip = System.IO.Compression.ZipFile.Open(input, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(relsName)!;
            string rels;
            using (var reader = new StreamReader(entry.Open()))
                rels = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(relsName).Open(), new UTF8Encoding(false));
            writer.Write(rels.Replace("</Relationships>", """<Relationship Id="rIdUnc" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="\\evil-host\share\x.png"/></Relationships>""", StringComparison.Ordinal));
        }

        Assert.Equal(5, OpenXml.ExternalLinks.Strip(input));
        Assert.Equal(0, OpenXml.ExternalLinks.Strip(input));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(input))
        {
            using var reader = new StreamReader(zip.GetEntry(relsName)!.Open());
            var rels = reader.ReadToEnd();
            Assert.DoesNotContain("evil-host", rels);
            Assert.DoesNotContain("127.0.0.1", rels);
            Assert.Contains("https://example.com/kept", rels);
        }
        using (var document = WordprocessingDocument.Open(input, false))
        {
            var main = document.MainDocumentPart!;
            Assert.Empty(main.ExternalRelationships);
            Assert.Empty(main.HeaderParts.Single().ExternalRelationships);
            Assert.Empty(main.DocumentSettingsPart!.ExternalRelationships);
            Assert.Equal("https://example.com/kept", Assert.Single(main.HyperlinkRelationships).Uri.OriginalString);
            Assert.Single(main.ImageParts);
        }
        // Not a package: left alone. Looks like a zip but isn't one: refused.
        Assert.Equal(0, OpenXml.ExternalLinks.Strip(_workspace.WriteText("plain.rtf", "{\\rtf1 hello}")));
        Assert.Throws<InvalidDataException>(() => OpenXml.ExternalLinks.Strip(_workspace.WriteText("broken.docx", "PK\u0003\u0004 not really")));
    }

    [LibreOfficeFact]
    public async Task With_libreoffice_linked_pictures_are_never_fetched()
    {
        using var server = new ConnectionRecorder();
        var external = $"http://127.0.0.1:{server.Port}/pixel.png";
        var input = LinkedPicturesDocx("linked.docx", external);
        var withLibreOffice = new DocumentOperationProvider();

        var htmlResult = await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "doc.html"), [input]);
        var htmlPath = Assert.Single(htmlResult.Outputs);
        var html = await File.ReadAllTextAsync(htmlPath);
        var page = Parse(htmlPath);
        Assert.DoesNotContain("Clementine", page.QuerySelector("meta[name=generator]")?.GetAttribute("content") ?? "");
        Assert.DoesNotContain($":{server.Port}", html);
        Assert.Empty(page.QuerySelectorAll("script, iframe, object, embed"));
        Assert.All(page.QuerySelectorAll("img[src]"), img => Assert.StartsWith("data:", img.GetAttribute("src")!, StringComparison.Ordinal));
        Assert.Contains("The end", BodyText(page));

        var pdfResult = await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "doc.pdf"), [input]);
        using (var document = PdfDocument.Open(Assert.Single(pdfResult.Outputs)))
            Assert.Contains("LibreOffice", document.Information.Producer);

        // A workbook with a linked picture on its sheet.
        var xlsx = Workbook(_workspace.Path("linked.xlsx"),
            [("Linked", "<sheetData><row r=\"1\">" + InlineCell("A1", "Sheet with a linked picture") + "</row></sheetData><drawing r:id=\"rIdDrawing\"/>", null)],
            extra: (_, sheets) =>
            {
                var drawing = sheets[0].AddNewPart<DrawingsPart>("rIdDrawing");
                drawing.AddExternalRelationship(ImageRelationship, new Uri(external + "?sheet"), "rIdLinked");
                Feed(drawing, $"{Xml}<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"952500\" cy=\"952500\"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:link=\"rIdLinked\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>");
            });
        var sheetResult = await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "sheet.pdf"), [xlsx]);
        using (var document = PdfDocument.Open(Assert.Single(sheetResult.Outputs)))
            Assert.Contains("LibreOffice", document.Information.Producer);

        Assert.Equal(0, server.Connections);
    }
}
