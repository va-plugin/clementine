using System.Globalization;
using System.Text;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Clementine.Core;
using Clementine.Engines.Documents.Spreadsheets;
using Clementine.Engines.Documents.Tests.Fixtures;
using Clementine.TestSupport;
using SkiaSharp;
using UglyToad.PdfPig;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;
using static Clementine.Engines.Documents.Tests.Fixtures.RichOfficeFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>
/// Word and Excel conversions without LibreOffice: the provider is told to ignore an installed LibreOffice, so these run
/// Clementine's built-in converters (HTML, printed to PDF by Edge) exactly as on a machine that never had LibreOffice.
/// Set CLEMENTINE_LOOK_DIR to a folder to keep the PDFs, HTML pages and rendered PNG pages for a visual check.
/// </summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class BuiltInOfficeTests : IDisposable
{
    static readonly DocumentOperationProvider WithoutLibreOffice = new() { IgnoreLibreOffice = true };

    readonly TestWorkspace _workspace = new("builtin-office");

    public void Dispose() => _workspace.Dispose();

    IOperation Op(string id) => _workspace.Operation(WithoutLibreOffice, id);

    async Task<string> RunAsync(string id, string input)
    {
        var result = await _workspace.RunAsync(Op(id), [input]);
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output), output);
        return output;
    }

    static IHtmlDocument Parse(string path) => new HtmlParser().ParseDocument(File.ReadAllText(path));

    static string AllText(string pdf) => Normalize(string.Join(' ', PageTexts(pdf)));

    /// <summary>Asserts every expected snippet appears, each after the previous one.</summary>
    static void AssertInOrder(string text, IEnumerable<string> expected)
    {
        var position = 0;
        foreach (var snippet in expected)
        {
            var index = text.IndexOf(Normalize(snippet), position, StringComparison.Ordinal);
            Assert.True(index >= 0, $"\"{snippet}\" is missing after position {position} in:\n{text}");
            position = index + 1;
        }
    }

    /// <summary>Copies an output (and its rendered pages) to CLEMENTINE_LOOK_DIR for a human to look at.</summary>
    async Task KeepForLookingAsync(string path, string name)
    {
        var look = Environment.GetEnvironmentVariable("CLEMENTINE_LOOK_DIR");
        if (string.IsNullOrEmpty(look))
            return;
        Directory.CreateDirectory(look);
        File.Copy(path, Path.Combine(look, name), overwrite: true);
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return;
        var pages = Assert.Single((await _workspace.RunAsync(_workspace.Operation(WithoutLibreOffice, "pdf.png.150"), [path])).Outputs);
        var files = Directory.Exists(pages) ? Directory.GetFiles(pages, "*.png") : [pages];
        foreach (var file in files)
            File.Copy(file, Path.Combine(look, Path.GetFileNameWithoutExtension(name) + " - " + Path.GetFileName(file)), overwrite: true);
    }

    // Availability ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_libreoffice_word_and_excel_stay_available_and_the_rest_explain_why()
    {
        foreach (var id in new[] { "doc.html", "sheet.html" })
            Assert.True(Op(id).CheckAvailability().IsAvailable, id);
        if (LibreOfficeFactAttribute.Tools.Edge is not null)
        {
            foreach (var id in new[] { "doc.pdf", "sheet.pdf" })
                Assert.True(Op(id).CheckAvailability().IsAvailable, id);
        }
        foreach (var id in new[] { "doc.pdf.lo", "doc.html.lo", "sheet.pdf.lo", "sheet.html.lo", "slides.pdf", "slides.png", "doc.odt" })
        {
            var availability = Op(id).CheckAvailability();
            Assert.False(availability.IsAvailable, id);
            Assert.Equal("Needs LibreOffice (free, optional) for PowerPoint and older Office files — libreoffice.org", availability.Reason);
        }

        // The built-in path doesn't promise LibreOffice's fidelity.
        foreach (var id in new[] { "doc.pdf", "doc.html", "sheet.pdf" })
            Assert.Contains("complex layouts may differ", Op(id).Info.Description, StringComparison.OrdinalIgnoreCase);

        // DOCX goes to the built-in operations; .doc / .odt / .xls only to LibreOffice ones (no extra wheel entries).
        InputFile Input(string name) => InputFile.FromPath(_workspace.WriteText(name, ""));
        Assert.True(Op("doc.pdf").Accepts(Input("a.docx")));
        Assert.False(Op("doc.pdf.lo").Accepts(Input("a.docx")));
        Assert.True(Op("doc.pdf.lo").Accepts(Input("a.doc")));
        Assert.False(Op("doc.pdf").Accepts(Input("a.doc")));
        Assert.True(Op("sheet.pdf").Accepts(Input("a.csv")));
        Assert.False(Op("sheet.pdf").Accepts(Input("a.ods")));
        Assert.True(Op("sheet.pdf.lo").Accepts(Input("a.ods")));
    }

    [Fact]
    public void Built_in_provider_satisfies_the_operation_contract()
    {
        var problems = ProviderContract.Validate(WithoutLibreOffice, _workspace.Context);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // Word → HTML -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_to_html_keeps_structure_and_formatting()
    {
        var input = RichDocx(_workspace.Path("Report – ünïcødé.docx"));
        var output = await RunAsync("doc.html", input);
        Assert.Equal("Report – ünïcødé.html", Path.GetFileName(output));
        var html = await File.ReadAllTextAsync(output);
        var page = Parse(output);
        var css = string.Join('\n', page.QuerySelectorAll("style").Select(s => s.TextContent));

        Assert.Equal("Quarterly Report", page.Title);
        Assert.Empty(page.QuerySelectorAll("script"));
        Assert.Contains(page.QuerySelectorAll("meta[http-equiv]"), m => m.GetAttribute("content")!.Contains("script-src 'none'"));

        // Headings.
        Assert.Equal(new[] { "Quarterly Report 2026", "Overview", "Second page" }, page.QuerySelectorAll("h1").Select(h => h.TextContent.Trim()));
        Assert.Equal(new[] { "Lists", "Table", "Picture" }, page.QuerySelectorAll("h2").Select(h => h.TextContent.Trim()));

        // Lists: numbered with a nested lettered list, bulleted with a nested "o" list; markers as Word numbers them.
        var numbered = page.QuerySelectorAll("ol").First(o => o.TextContent.Contains("First step"));
        var items = numbered.Children.Where(c => c.LocalName == "li").ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("1.", items[0].QuerySelector(".mk")!.TextContent);
        Assert.Equal("3.", items[2].QuerySelector(".mk")!.TextContent);
        var nested = items[1].QuerySelector("ol")!;
        Assert.Equal(new[] { "a)", "b)" }, nested.QuerySelectorAll("li > .mk").Select(m => m.TextContent));
        Assert.Contains("Sub-step b", nested.TextContent);
        var bullets = page.QuerySelectorAll("ul").First(u => u.TextContent.Contains("Apples"));
        Assert.Equal("•", bullets.QuerySelector("li > .mk")!.TextContent);
        Assert.Equal("o", bullets.QuerySelector("li ul .mk")!.TextContent);

        // Table: header row repeats, merged cells, borders and shading.
        var table = page.QuerySelector("table")!;
        Assert.Equal(3, table.QuerySelectorAll("col").Length);
        Assert.Contains("Region", table.QuerySelector("thead")!.TextContent);
        Assert.Equal("2", table.QuerySelectorAll("td").Single(td => td.TextContent.Contains("Merged across")).GetAttribute("colspan"));
        Assert.Equal("2", table.QuerySelectorAll("td").Single(td => td.TextContent.Contains("Tall cell")).GetAttribute("rowspan"));
        Assert.Equal(13, table.QuerySelectorAll("td").Length);
        Assert.Contains("background-color: #d9e2f3", css);
        Assert.Contains("background-color: #fff2cc", css);
        Assert.Contains("border-top: 0.5pt solid #000000", css);
        Assert.Contains("double", css);
        Assert.Contains("vertical-align: middle", css);

        // Picture embedded at its size; links; bookmark; footnote.
        var image = page.QuerySelector("img")!;
        Assert.StartsWith("data:image/png;base64,", image.GetAttribute("src"));
        Assert.Contains("width: 150pt; height: 75pt", image.GetAttribute("style"));
        Assert.Equal("Orange block", image.GetAttribute("alt"));
        Assert.NotNull(page.QuerySelector("a[href='https://example.com/report']"));
        Assert.NotNull(page.QuerySelector("a[href='#_Overview']"));
        Assert.NotNull(page.GetElementById("_Overview"));
        Assert.Equal("1", page.QuerySelector("sup.nr a[href='#fn1']")!.TextContent);
        Assert.Contains("Footnote text with ünïcødé.", page.GetElementById("fn1")!.TextContent);

        // Run formatting and paragraph layout become CSS.
        foreach (var expected in new[]
                 {
                     "font-weight: 700", "font-style: italic", "text-decoration-line: underline", "text-decoration-line: line-through",
                     "color: #c00000", "background-color: #ffff00", "font-size: 16pt", "\"Courier New\"", "text-align: center",
                     "text-align: right", "text-align: justify", "text-indent: 36pt", "margin-left: 72pt", "\"Calibri Light\"",
                 })
            Assert.Contains(expected, css);
        Assert.Contains("line-height: 1.83", css); // 1.5 lines of Calibri (1.22 × 1.5)
        Assert.NotNull(page.QuerySelector(".pb")); // the page break
        Assert.NotNull(page.QuerySelector(".ld.dot")); // the dotted tab leader of "Chapter One … 7"

        // Page setup and the header/footer as margin boxes.
        Assert.Contains("size: 595.3pt 841.9pt; margin: 56.7pt 56.7pt 56.7pt 70.9pt;", css);
        Assert.Contains("@top-left { content: \"Clementine Fixture Co.\"", css);
        Assert.Contains("@top-right { content: \"Quarterly report\"", css);
        Assert.Contains("@bottom-center { content: \"Page \" counter(page) \" of \" counter(pages);", css);

        // Every paragraph and cell, in order, with Unicode intact.
        AssertInOrder(Normalize(page.Body!.TextContent), DocxTextInOrder.Where(t => t is not "7"));
        Assert.Contains(RichOfficeFixtures.Unicode, html);
    }

    // Word → PDF ------------------------------------------------------------------------------------------------------

    [EdgeFact]
    public async Task Docx_prints_to_pdf_without_libreoffice()
    {
        var input = RichDocx(_workspace.Path("Report – ünïcødé.docx"));
        var pdf = await RunAsync("doc.pdf", input);
        Assert.Equal("Report – ünïcødé.pdf", Path.GetFileName(pdf));

        using (var document = PdfDocument.Open(pdf))
        {
            Assert.Equal(2, document.NumberOfPages);
            Assert.Contains("Skia", document.Information.Producer); // printed by Edge, not LibreOffice
            var first = document.GetPage(1);
            Assert.InRange(first.Width, 593, 598); // A4 from sectPr
            Assert.InRange(first.Height, 839, 845);
            Assert.NotEmpty(first.GetImages());
        }

        var pages = PageTexts(pdf);
        Assert.Contains("Overview", pages[0]);
        Assert.DoesNotContain("Second page", pages[0]); // the page break moved it
        Assert.Contains("Second page", pages[1]);
        foreach (var (text, number) in pages.Select((t, i) => (t, i + 1)))
        {
            Assert.Contains("Clementine Fixture Co.", text); // header on every page
            Assert.Contains("Quarterly report", text);
            Assert.Contains($"Page {number} of 2", text); // PAGE / NUMPAGES fields
        }
        AssertInOrder(AllText(pdf), DocxTextInOrder);
        Assert.Contains("東京", AllText(pdf));

        // Fidelity spot check on the rendered page: the orange picture and the blue table header are really drawn.
        using var bitmap = await RenderFirstPageAsync(pdf);
        Assert.True(CountNear(bitmap, new SKColor(0xFF, 0x8A, 0x1F)) > 2000, "The picture isn't visible.");
        Assert.True(CountNear(bitmap, new SKColor(0xD9, 0xE2, 0xF3)) > 1000, "The table header shading isn't visible.");
        Assert.True(CountNear(bitmap, new SKColor(0xFF, 0xFF, 0x00)) > 100, "The highlight isn't visible.");
        await KeepForLookingAsync(pdf, "docx-builtin.pdf");
        await KeepForLookingAsync(await RunAsync("doc.html", input), "docx-builtin.html");
    }

    [EdgeFact]
    public async Task Sections_keep_their_own_page_size_headers_and_columns()
    {
        var input = SectionsDocx(_workspace.Path("sections.docx"));
        var html = await File.ReadAllTextAsync(await RunAsync("doc.html", input));
        Assert.Contains("@page s1 { size: 612pt 792pt;", html);
        Assert.Contains("@page s1:first {", html);
        Assert.Contains("@page s2 { size: 595.3pt 419.55pt;", html);
        Assert.Contains("columns: 2; column-gap: 36pt;", html);

        var pdf = await RunAsync("doc.pdf", input);
        using var document = PdfDocument.Open(pdf);
        Assert.InRange(document.NumberOfPages, 3, 4);
        var pages = Enumerable.Range(1, document.NumberOfPages).Select(document.GetPage).ToList();
        var texts = PageTexts(pdf);

        Assert.InRange(pages[0].Width, 610, 614); // Letter portrait
        Assert.InRange(pages[0].Height, 790, 794);
        Assert.Contains("First page header", texts[0]); // titlePg: its own header on page 1…
        Assert.DoesNotContain("Running header", texts[0]);
        Assert.Contains("Cover page", texts[0]);
        Assert.Contains("Running header", texts[1]); // …and the running header after it
        Assert.Contains("Body page", texts[1]); // pageBreakBefore

        Assert.InRange(pages[2].Width, 593, 597); // A5 landscape, header inherited from section 1
        Assert.InRange(pages[2].Height, 418, 422);
        Assert.Contains("Running header", texts[2]);
        Assert.Contains("Landscape section", texts[2]);
        var columnWords = pages[2].GetWords().Where(w => w.Text == "Column").ToList();
        Assert.Contains(columnWords, w => w.BoundingBox.Left < pages[2].Width / 2);
        Assert.Contains(columnWords, w => w.BoundingBox.Left > pages[2].Width / 2); // the second column

        Assert.Contains("Endnote text.", texts[^1]);
        await KeepForLookingAsync(pdf, "sections-builtin.pdf");
    }

    async Task<SKBitmap> RenderFirstPageAsync(string pdf)
    {
        var rendered = Assert.Single((await _workspace.RunAsync(_workspace.Operation(WithoutLibreOffice, "pdf.png.150"), [pdf])).Outputs);
        var first = Directory.Exists(rendered) ? Directory.GetFiles(rendered, "*.png").Order().First() : rendered;
        return Decode(first);
    }

    static int CountNear(SKBitmap bitmap, SKColor color, int tolerance = 12)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - color.Red) <= tolerance && Math.Abs(pixel.Green - color.Green) <= tolerance && Math.Abs(pixel.Blue - color.Blue) <= tolerance)
                    count++;
            }
        }
        return count;
    }

    [Fact]
    public async Task Damaged_docx_gets_a_friendly_error_and_no_output()
    {
        var input = _workspace.Path("broken.docx");
        await File.WriteAllBytesAsync(input, Encoding.ASCII.GetBytes("PK\u0003\u0004 definitely not a real docx"));
        var error = await Assert.ThrowsAsync<OperationException>(() => _workspace.RunAsync(Op("doc.html"), [input]));
        Assert.Contains("doesn't look like a valid Word document", error.Message);
        Assert.Empty(Directory.GetFiles(_workspace.Root, "*.html"));
    }

    // Excel → HTML / PDF ----------------------------------------------------------------------------------------------

    static string Money(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    static string Day(double serial) =>
        $"{DateTime.FromOADate(serial).Day} {CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(DateTime.FromOADate(serial).Month).TrimEnd('.')} {DateTime.FromOADate(serial).Year}";

    static string Percent(double value) => (value * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%";

    /// <summary>The Sales sheet's visible cells in reading order, as Excel displays them in the current culture.</summary>
    static string[] SalesTextInOrder =>
    [
        "Sales Report – 東京",
        "Region", "Units", "Revenue", "Date", "Share",
        "North", Money(1234.5, "#,##0.00"), "€" + Money(99, "#,##0.00"), Day(March14), Percent(0.125),
        "South", "(" + Money(4321, "#,##0") + ")", "€" + Money(198, "#,##0.00"), Day(March14 + 1), Percent(0.875),
        RichOfficeFixtures.Unicode, "Rich bold and normal", "TRUE",
        "This long note wraps inside its cell and keeps going for a while.", "#DIV/0!",
    ];

    [Fact]
    public async Task Xlsx_to_html_shows_cells_as_excel_displays_them()
    {
        var input = RichXlsx(_workspace.Path("Sales – ü.xlsx"));
        var output = await RunAsync("sheet.html", input);
        var page = Parse(output);
        var css = string.Join('\n', page.QuerySelectorAll("style").Select(s => s.TextContent));

        Assert.Equal(new[] { "Sales", "Wide", "Notes" }, page.QuerySelectorAll("h2.sheet-name").Select(h => h.TextContent));
        var sheets = page.QuerySelectorAll("section.sheet").ToList();
        Assert.Equal(3, sheets.Count);

        var sales = sheets[0];
        AssertInOrder(Normalize(sales.TextContent), SalesTextInOrder);
        Assert.Equal("5", sales.QuerySelectorAll("td").Single(td => td.TextContent.StartsWith("Sales Report")).GetAttribute("colspan"));
        Assert.Equal(5, sales.QuerySelectorAll("col").Length); // the hidden column F is left out
        var negative = sales.QuerySelectorAll("td").Single(td => td.TextContent.StartsWith('('));
        Assert.Contains("color: #ff0000", negative.GetAttribute("style")); // [Red] in the number format
        Assert.Contains("an", negative.ClassList); // numbers align right
        Assert.NotNull(sales.QuerySelector("a[href='https://example.com/sales']"));
        var rich = sales.QuerySelectorAll("td span").First(s => s.TextContent == "Rich bold");
        Assert.Contains("font-weight: 700", rich.GetAttribute("style"));
        Assert.Contains("color: #00b050", rich.GetAttribute("style"));
        Assert.Contains("wr", sales.QuerySelectorAll("td").Single(td => td.TextContent.StartsWith("This long note")).ClassList);

        var text = Normalize(page.Body!.TextContent);
        Assert.DoesNotContain("Hidden row secret", text);
        Assert.DoesNotContain("Hidden column secret", text);
        Assert.DoesNotContain("Secret sheet", text); // hidden sheet
        Assert.DoesNotContain("Outside print area", text);
        Assert.Contains("Print area only", text);
        Assert.Contains("R5C30", text);

        // Fills, borders, fonts, page setup and the footer.
        Assert.Contains("border-left: 1px solid #000000", css);
        Assert.Contains("background-color: #fff2cc", css);
        Assert.Contains("font-size: 16pt", css);
        Assert.Contains("@page sh1 { size: 595.3pt 841.9pt;", css); // Sales: A4 portrait (paperSize 9)
        var wide = System.Text.RegularExpressions.Regex.Match(css, @"@page sh2 \{ size: ([0-9.]+)pt ([0-9.]+)pt;");
        Assert.True(wide.Success, "No page rule for the wide sheet.");
        Assert.True(double.Parse(wide.Groups[1].Value, CultureInfo.InvariantCulture) > double.Parse(wide.Groups[2].Value, CultureInfo.InvariantCulture),
            "The wide sheet should be turned to landscape.");
        Assert.Contains("section.sh2 > .fit { zoom: 0.", css); // and scaled to fit the page width
        Assert.Contains("@bottom-left { content: \"Confidential\"", css);
        Assert.Contains("counter(page) \" of \" counter(pages)", css);
    }

    [EdgeFact]
    public async Task Xlsx_prints_every_sheet_without_libreoffice()
    {
        var input = RichXlsx(_workspace.Path("Sales – ü.xlsx"));
        var pdf = await RunAsync("sheet.pdf", input);

        using (var document = PdfDocument.Open(pdf))
        {
            Assert.InRange(document.NumberOfPages, 3, 4);
            Assert.Contains("Skia", document.Information.Producer);
            var first = document.GetPage(1);
            Assert.True(first.Height > first.Width, "Sales prints portrait.");
            var wide = Enumerable.Range(1, document.NumberOfPages).Select(document.GetPage).First(p => p.Text.Contains("R1C1"));
            Assert.True(wide.Width > wide.Height, "The wide sheet prints landscape.");
        }
        var text = AllText(pdf);
        AssertInOrder(text, [.. SalesTextInOrder, "Wide", "R1C1", "R5C30", "Notes", "Print area only", "Ada"]);
        Assert.Contains("Confidential", text);
        Assert.Contains("Page 1 of", text);
        Assert.DoesNotContain("Hidden row secret", text);
        Assert.DoesNotContain("Secret sheet", text);
        Assert.DoesNotContain("Outside print area", text);

        using var bitmap = await RenderFirstPageAsync(pdf);
        Assert.True(CountNear(bitmap, new SKColor(0xFF, 0xF2, 0xCC)) > 200, "The cell fill isn't visible.");
        await KeepForLookingAsync(pdf, "xlsx-builtin.pdf");
        await KeepForLookingAsync(await RunAsync("sheet.html", input), "xlsx-builtin.html");
    }

    [EdgeFact]
    public async Task Csv_prints_and_becomes_a_web_page_without_libreoffice()
    {
        var input = _workspace.WriteText("cities.csv", "City;Country;Population\nTōkyō – 東京;Japan;37400068\n\"Zürich, ZH\";Switzerland;421878\n");
        var pdf = await RunAsync("sheet.pdf", input);
        Assert.Equal(1, PageCount(pdf));
        AssertInOrder(AllText(pdf), ["City", "Country", "Population", "Tōkyō – 東京", "Japan", "37400068", "Zürich, ZH", "Switzerland", "421878"]);

        var page = Parse(await RunAsync("sheet.html", input));
        Assert.Equal(3, page.QuerySelectorAll("thead td").Length);
        Assert.Contains("an", page.QuerySelectorAll("td").Single(td => td.TextContent == "421878").ClassList);
        Assert.Empty(page.QuerySelectorAll("h2"));
    }

    [LibreOfficeFact]
    public async Task With_libreoffice_installed_documents_still_go_through_libreoffice()
    {
        using var workspace = new TestWorkspace("with-lo");
        var input = RichDocx(workspace.Path("report.docx"));
        var result = await workspace.RunAsync(workspace.Operation(new DocumentOperationProvider(), "doc.pdf"), [input]);
        using var document = PdfDocument.Open(Assert.Single(result.Outputs));
        Assert.Contains("LibreOffice", document.Information.Producer);
    }

    // Number formats --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1234.5, "#,##0.00", "1,234.50")]
    [InlineData(1234.5, "0", "1235")]
    [InlineData(0.5, "#.##", ".5")]
    [InlineData(2.675, "0.00", "2.68")]
    [InlineData(-1234.0, "#,##0;[Red](#,##0)", "(1,234)")]
    [InlineData(-1234.0, "#,##0", "-1,234")]
    [InlineData(0.0, "0.0;-0.0;\"zero\"", "zero")]
    [InlineData(0.125, "0.0%", "12.5%")]
    [InlineData(1234567.0, "#,##0,\"K\"", "1,235K")]
    [InlineData(99.0, "[$€-407]#,##0.00", "€99.00")]
    [InlineData(5551234567.0, "(000) 000-0000", "(555) 123-4567")]
    [InlineData(12345.0, "0.00E+00", "1.23E+04")]
    [InlineData(0.00012, "0.0E+0", "1.2E-4")]
    [InlineData(1.25, "# ?/?", "1 1/4")]
    [InlineData(0.333333, "?/?", "1/3")]
    [InlineData(5.5, "# ?/8", "5 4/8")]
    [InlineData(1234.5678, "General", "1234.5678")]
    [InlineData(1.0 / 3, "General", "0.333333333")]
    [InlineData(123456789012345.0, "General", "1.23457E+14")]
    [InlineData(46095.0, "d mmm yyyy", "14 Mar 2026")]
    [InlineData(46095.0, "dddd, mmmm d, yyyy", "Saturday, March 14, 2026")]
    [InlineData(46095.75, "yyyy-mm-dd hh:mm", "2026-03-14 18:00")]
    [InlineData(46095.75, "h:mm AM/PM", "6:00 PM")]
    [InlineData(1.5, "[h]:mm", "36:00")]
    [InlineData(0.000694444, "mm:ss.0", "01:00.0")]
    [InlineData(1.0, "m/d/yyyy", "1/1/1900")]
    [InlineData(59.0, "m/d/yyyy", "2/28/1900")]
    [InlineData(61.0, "m/d/yyyy", "3/1/1900")]
    // Times that aren't exact in binary (09:30 is 0.39583333…, a hair under the minute) with a date part.
    [InlineData(46095 + 570 / 1440.0, "yyyy-mm-dd hh:mm", "2026-03-14 09:30")]
    [InlineData(46095 + 570 / 1440.0, "h:mm AM/PM", "9:30 AM")]
    [InlineData(46095.395833333336, "yyyy-mm-dd hh:mm:ss", "2026-03-14 09:30:00")]
    [InlineData(46095 + 1 / 1440.0, "yyyy-mm-dd hh:mm:ss", "2026-03-14 00:01:00")]
    [InlineData(46095 + 86399 / 86400.0, "yyyy-mm-dd hh:mm:ss", "2026-03-14 23:59:59")]
    [InlineData(46095 + 86399.6 / 86400.0, "yyyy-mm-dd hh:mm:ss", "2026-03-15 00:00:00")]
    [InlineData(46095 + 37230.25 / 86400.0, "hh:mm:ss.00", "10:20:30.25")]
    [InlineData(46095 + 570 / 1440.0, "[h]:mm", "1106289:30")]
    [InlineData(570 / 1440.0, "[mm]:ss", "570:00")]
    [InlineData(1 / 1440.0, "[ss]", "60")]
    // Sections without digit placeholders show only their literals (";;;" hides the value).
    [InlineData(42.0, ";;;", "")]
    [InlineData(-42.0, ";;;", "")]
    [InlineData(0.0, ";;;", "")]
    [InlineData(1.0, "\"Yes\";\"Yes\";\"No\"", "Yes")]
    [InlineData(-1.0, "\"Yes\";\"Yes\";\"No\"", "Yes")]
    [InlineData(0.0, "\"Yes\";\"Yes\";\"No\"", "No")]
    [InlineData(7.0, "\"Total\"", "Total")]
    [InlineData(-5.0, "0;;", "")]
    [InlineData(0.0, "0;;", "")]
    [InlineData(5.0, "0;;", "5")]
    [InlineData(12.5, ".00", "12.50")]
    [InlineData(0.5, ".00", ".50")]
    [InlineData(1234.5, "", "1234.5")]
    public void Number_formats_render_like_excel(double value, string code, string expected) =>
        Assert.Equal(expected, NumberFormats.Format(value, code, CultureInfo.GetCultureInfo("en-US")).Text);

    [Fact]
    public void Every_minute_and_second_of_a_dated_day_shows_exactly()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var wrong = new List<string>();
        for (var minute = 0; minute < 1440; minute++)
        {
            var serial = 46095 + minute / 1440.0;
            var expected = $"{minute / 60:00}:{minute % 60:00}";
            if (NumberFormats.Format(serial, "hh:mm", culture).Text != expected)
                wrong.Add($"{expected} → {NumberFormats.Format(serial, "hh:mm", culture).Text}");
            if (NumberFormats.Format(serial, "hh:mm:ss", culture).Text != expected + ":00")
                wrong.Add($"{expected}:00 → {NumberFormats.Format(serial, "hh:mm:ss", culture).Text}");
            // As stored by an .xlsx (the shortest round-trip text of the double) and by a t="d" cell (ToOADate).
            var stored = double.Parse(serial.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var oaDate = new DateTime(2026, 3, 14).AddMinutes(minute).ToOADate();
            if (NumberFormats.Format(stored, "hh:mm", culture).Text != expected || NumberFormats.Format(oaDate, "hh:mm", culture).Text != expected)
                wrong.Add($"{expected} (stored)");
        }
        for (var second = 0; second < 86400; second += 7)
        {
            var expected = $"{second / 3600:00}:{second / 60 % 60:00}:{second % 60:00}";
            var shown = NumberFormats.Format(46095 + second / 86400.0, "hh:mm:ss", culture).Text;
            if (shown != expected)
                wrong.Add($"{expected} → {shown}");
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} wrong: {string.Join(", ", wrong.Take(10))}");
    }

    [Fact]
    public void Number_formats_follow_the_culture_and_handle_text_sections()
    {
        Assert.Equal("1.234,50", NumberFormats.Format(1234.5, "#,##0.00", CultureInfo.GetCultureInfo("de-DE")).Text);
        Assert.Equal("14 März 2026", NumberFormats.Format(46095, "d mmmm yyyy", CultureInfo.GetCultureInfo("de-DE")).Text);
        Assert.Equal("Note: hello", NumberFormats.FormatText("hello", "0;-0;0;\"Note: \"@", CultureInfo.InvariantCulture).Text);
        Assert.Equal("", NumberFormats.FormatText("hidden", ";;;", CultureInfo.InvariantCulture).Text);
        Assert.Equal("#,##0.00", NumberFormats.BuiltIn(4, CultureInfo.InvariantCulture));
        Assert.True(NumberFormats.IsDate("d-mmm-yy"));
        Assert.False(NumberFormats.IsDate("#,##0.00"));
        Assert.Equal("#ff0000", NumberFormats.Format(-5, "0;[Red]0", CultureInfo.InvariantCulture).Color);
    }
}
