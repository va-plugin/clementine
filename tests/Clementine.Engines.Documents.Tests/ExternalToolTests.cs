using System.Text;
using Clementine.Core;
using Clementine.Engines.Documents.OpenXml;
using Clementine.Engines.Documents.Tests.Fixtures;
using SkiaSharp;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>Integration tests through LibreOffice and Edge. They skip (not fail) where the tool isn't installed.</summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class ExternalToolTests() : DocumentTestBase("external")
{
    static string AllText(string pdf) => Normalize(string.Join(' ', PageTexts(pdf)));

    [LibreOfficeFact]
    public async Task Docx_to_pdf_keeps_text_and_unicode()
    {
        var input = Docx(Workspace.Path("Report – ünïcødé.docx"));
        var pdf = Single(await RunAsync("doc.pdf", input));

        Assert.Equal("Report – ünïcødé.pdf", Path.GetFileName(pdf));
        Assert.Equal(1, PageCount(pdf));
        var text = AllText(pdf);
        Assert.Contains("Quarterly report", text);
        Assert.Contains("Ünïcødé", text);
        Assert.Contains("東京", text);
        Assert.Contains("Alpha 42", text);
    }

    [LibreOfficeFact]
    public async Task Word_formats_convert_between_each_other()
    {
        var docx = Docx(Workspace.Path("chain.docx"));
        var odt = Single(await RunAsync("doc.odt", docx));
        var rtf = Single(await RunAsync("doc.rtf", odt));
        var doc = Single(await RunAsync("doc.doc", rtf));
        var backToDocx = Single(await RunAsync("doc.docx", doc));

        Assert.Equal(new[] { ".odt", ".rtf", ".doc", ".docx" }, new[] { odt, rtf, doc, backToDocx }.Select(Path.GetExtension));
        Assert.Equal("chain (2).docx", Path.GetFileName(backToDocx));
        var text = await File.ReadAllTextAsync(Single(await RunAsync("doc.txt", backToDocx)));
        Assert.Contains("Quarterly report", text);
        Assert.Contains(Unicode, text);

        var legacyText = await File.ReadAllTextAsync(Single(await RunAsync("doc.txt.lo", odt)));
        Assert.Contains("Second step", legacyText);
        Assert.Contains(Unicode, legacyText);
    }

    [LibreOfficeFact]
    public async Task Docx_to_html_is_one_file_with_images_inside()
    {
        var path = Workspace.Path("pictures.docx");
        using (var docx = DocxBuilder.Create(path, "Pictures"))
        {
            docx.Heading(1, [RichRun.Plain("Gallery")]);
            docx.Image(Png(120, 80, SKColors.Orange), "image/png", 120, 80, "Orange block");
            docx.Paragraph([RichRun.Plain("Caption " + Unicode)]);
            docx.Save();
        }
        var html = await File.ReadAllTextAsync(Single(await RunAsync("doc.html", path)));

        Assert.Contains("Gallery", html);
        Assert.Contains(Unicode, html);
        Assert.Contains("data:image/", html);
        Assert.Single(Directory.GetFiles(Workspace.Root, "*.html"));
    }

    [LibreOfficeFact]
    public async Task Spreadsheets_and_csv_print_to_pdf()
    {
        var xlsx = Xlsx(Workspace.Path("numbers.xlsx"), ("Data", [["Item", "Price"], ["Widget", 9.99], ["Ünïcødé", 1]]));
        var fromXlsx = AllText(Single(await RunAsync("sheet.pdf", xlsx)));
        Assert.Contains("Widget", fromXlsx);
        Assert.Contains("9.99", fromXlsx);

        var csv = Workspace.WriteText("table.csv", "City;Country\nTōkyō – 東京;Japan\n");
        var fromCsv = AllText(Single(await RunAsync("sheet.pdf", csv)));
        Assert.Contains("Tōkyō – 東京", fromCsv);
    }

    [LibreOfficeFact]
    public async Task Ods_round_trips_through_libreoffice_to_csv()
    {
        var xlsx = Xlsx(Workspace.Path("sheet.xlsx"), ("First", [["a", 1], ["b", 2.5]]), ("Second", [["Ünïcødé", true]]));
        var ods = Single(await RunAsync("sheet.ods", xlsx));
        var folder = Single(await RunAsync("sheet.csv.lo", ods));

        Assert.Equal("sheet (CSV)", Path.GetFileName(folder));
        Assert.Equal("a,1\r\nb,2.5\r\n", (await File.ReadAllTextAsync(Path.Combine(folder, "First.csv"))).TrimStart('﻿'));
        Assert.Equal("Ünïcødé,TRUE\r\n", (await File.ReadAllTextAsync(Path.Combine(folder, "Second.csv"))).TrimStart('﻿'));

        var backToXlsx = Single(await RunAsync("sheet.xlsx.lo", ods));
        Assert.Equal("sheet (2).xlsx", Path.GetFileName(backToXlsx));
    }

    [LibreOfficeFact]
    public async Task Pptx_to_pdf_png_and_back()
    {
        var pptx = PptxFixture.Create(Workspace.Path("deck.pptx"), ("Welcome", "Hello " + Unicode), ("Agenda", "Plans for 2027"));

        var pdf = Single(await RunAsync("slides.pdf", pptx));
        Assert.Equal(2, PageCount(pdf));
        var text = AllText(pdf);
        Assert.Contains("Welcome", text);
        Assert.Contains("Plans for 2027", text);

        var folder = Single(await RunAsync("slides.png", pptx));
        Assert.Equal("deck (PNG slides)", Path.GetFileName(folder));
        var slides = Directory.GetFiles(folder, "*.png").Order().ToList();
        Assert.Equal(2, slides.Count);
        using (var bitmap = Decode(slides[0]))
        {
            Assert.InRange(bitmap.Width, 1915, 1925);
            Assert.InRange(bitmap.Height, 1075, 1085);
        }

        var odp = Single(await RunAsync("slides.odp", pptx));
        var fromOdp = await File.ReadAllTextAsync(Single(await RunAsync("slides.txt.lo", odp)));
        Assert.Contains("Agenda", fromOdp);
        Assert.Contains("Hello " + Unicode, fromOdp);
    }

    [LibreOfficeFact]
    public async Task Damaged_documents_get_a_friendly_libreoffice_error()
    {
        var input = Workspace.Path("broken.docx");
        await File.WriteAllBytesAsync(input, Encoding.ASCII.GetBytes("PK\u0003\u0004 definitely not a real docx"));
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.pdf", input));
        Assert.StartsWith("LibreOffice couldn't", error.Message);
        Assert.Empty(Directory.GetFiles(Workspace.Root, "*.pdf"));
    }

    [LibreOfficeFact]
    public async Task Cancelling_a_libreoffice_conversion_leaves_nothing_behind()
    {
        var input = Docx(Workspace.Path("cancel.docx"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync("doc.pdf", input, cancellationToken: cancel.Token));
        Assert.Empty(Directory.GetFiles(Workspace.Root, "*.pdf"));
        Assert.Empty(Directory.GetFiles(Workspace.Root, ".*partial*"));

        // LibreOffice recovers from the killed run: the next conversion works.
        Assert.Equal(1, PageCount(Single(await RunAsync("doc.pdf", input))));
    }

    [LibreOfficeFact]
    public async Task Libreoffice_timeouts_are_reported_kindly()
    {
        var input = Docx(Workspace.Path("slow.docx"));
        var original = External.LibreOffice.Timeout;
        External.LibreOffice.Timeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.pdf", input));
            Assert.StartsWith("LibreOffice took longer than", error.Message);
        }
        finally
        {
            External.LibreOffice.Timeout = original;
        }
        Assert.Empty(Directory.GetFiles(Workspace.Root, "*.pdf"));
    }

    [EdgeFact]
    public async Task Markdown_prints_to_pdf_with_edge()
    {
        await File.WriteAllBytesAsync(Workspace.Path("docs/pic.png"), Png(200, 100, SKColors.Blue));
        var input = Workspace.WriteText("docs/guide.md", $"# Setup guide\n\nInstall the **app** – {Unicode}.\n\n![logo](pic.png)\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");
        var pdfResult = await RunAsync("md.pdf", input);
        var pdf = Single(pdfResult);

        var text = AllText(pdf);
        Assert.Contains("Setup guide", text);
        Assert.Contains(Unicode, text);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.NotEmpty(document.GetPage(1).GetImages()); // the relative image resolved
    }

    [EdgeFact]
    public async Task Right_to_left_text_is_shaped_by_edge()
    {
        var input = Workspace.WriteText("mixed.txt", "Hello world\nمرحبا بالعالم\nשלום עולם\n");
        var pdf = Single(await RunAsync("text.pdf", input));

        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(1, document.NumberOfPages);
        Assert.Contains("Skia", document.Information.Producer); // printed by Edge (Chromium), not PDFsharp
        Assert.Contains("Hello world", AllText(pdf));
    }

    [EdgeFact]
    public async Task Html_prints_to_pdf_with_edge()
    {
        var input = Workspace.WriteText("page – ü.html", $"<!doctype html><meta charset=utf-8><title>T</title><h1>Printed page</h1><p>{Unicode}</p>");
        var pdf = Single(await RunAsync("html.pdf", input));
        var text = AllText(pdf);
        Assert.Contains("Printed page", text);
        Assert.Contains(Unicode, text);
    }

    [EdgeFact]
    public async Task Epub_prints_chapters_on_separate_pages()
    {
        var input = Epub(Workspace.Path("tale.epub"));
        var pdf = Single(await RunAsync("epub.pdf", input));

        var pages = PageTexts(pdf);
        Assert.True(pages.Count >= 2, $"{pages.Count} pages");
        var one = pages.FindIndex(p => p.Contains("Chapter One"));
        var two = pages.FindIndex(p => p.Contains("Chapter Two"));
        Assert.True(one >= 0 && two > one, "Chapter two should start on a later page.");
        Assert.Contains("東京", string.Join(' ', pages));
    }
}

static class ListExtensions
{
    public static int FindIndex(this IReadOnlyList<string> list, Func<string, bool> match)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (match(list[i]))
                return i;
        }
        return -1;
    }
}
