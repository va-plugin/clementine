using System.Text;
using Clementine.Core;
using Clementine.Engines.Documents.Markup;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using UglyToad.PdfPig;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Clementine.Engines.Documents.Tests;

public sealed class MarkupTests() : DocumentTestBase("markup")
{
    const string MarkdownSample = """
        # Release notes

        Some **bold**, *italic*, ~~gone~~ and `code` with a [link](https://example.com).

        1. First
        2. Second
           - nested bullet

        > A wise quote.

        ```csharp
        var x = 42;
        ```

        | Name | Value |
        |------|-------|
        | Alpha | 1 |

        ![Red square](pic.png)
        """;

    [Fact]
    public async Task Unicode_survives_txt_to_pdf_to_txt()
    {
        var source = $"{Unicode}\nSecond line with Ελληνικά and Кириллица.\n\nThird paragraph.";
        var input = Workspace.WriteText("unicode.txt", source);
        var pdf = Single(await RunAsync("text.pdf", input));
        var txt = Single(await RunAsync("pdf.txt", pdf));

        var roundTrip = Normalize(await File.ReadAllTextAsync(txt));
        Assert.Equal(Normalize(source), roundTrip);
    }

    [Fact]
    public async Task Long_text_wraps_and_paginates_with_page_numbers()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 30));
        var cjk = string.Concat(Enumerable.Repeat("東京都は日本の首都です。", 40));
        var input = Workspace.WriteText("long.txt", string.Join("\n\n", Enumerable.Repeat(paragraph, 12)) + "\n" + cjk);
        var pdf = Single(await RunAsync("text.pdf", input));

        using var document = PigDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 3, $"{document.NumberOfPages} pages");
        foreach (var page in document.GetPages())
        {
            // Nothing runs off the right edge: every glyph stays inside the page.
            Assert.All(page.Letters, l => Assert.True(l.BoundingBox.Right <= page.Width - 40, $"'{l.Value}' at {l.BoundingBox.Right}"));
        }
        Assert.Contains($"1 / {document.NumberOfPages}", document.GetPage(1).Text);
        Assert.Contains("東京都は日本の首都です", Normalize(string.Concat(document.GetPages().Select(p => p.Text))));
    }

    [Fact]
    public void Monospace_is_chosen_for_logs_and_aligned_text()
    {
        Assert.True(TextPdfWriter.PrefersMonospace("anything", ".log"));
        Assert.True(TextPdfWriter.PrefersMonospace("Name     Size\nfoo      12\nbar      7\n", ".txt"));
        Assert.False(TextPdfWriter.PrefersMonospace("Just a normal letter.\nWith two lines.", ".txt"));
    }

    [Fact]
    public async Task Text_files_in_other_encodings_are_read_correctly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var latin1 = Workspace.Path("latin1.txt");
        await File.WriteAllBytesAsync(latin1, Encoding.GetEncoding(1252).GetBytes("Café déjà vu – naïve"));
        var utf16 = Workspace.Path("utf16.txt");
        await File.WriteAllBytesAsync(utf16, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Grüße 東京")).ToArray());

        Assert.Contains("Café déjà vu – naïve", Normalize(string.Join(' ', PageTexts(Single(await RunAsync("text.pdf", latin1))))));
        Assert.Contains("Grüße 東京", Normalize(string.Join(' ', PageTexts(Single(await RunAsync("text.pdf", utf16))))));
    }

    [Fact]
    public async Task Text_to_docx_keeps_every_line()
    {
        var input = Workspace.WriteText("notes.txt", $"Line one\n\tIndented {Unicode}\n\nLast");
        var docx = Single(await RunAsync("text.docx", input));
        var text = Single(await RunAsync("doc.txt", docx));
        Assert.Equal($"Line one\r\n\tIndented {Unicode}\r\n\r\nLast\r\n", await File.ReadAllTextAsync(text));
    }

    [Fact]
    public async Task Markdown_to_html_is_a_complete_styled_page()
    {
        var input = Workspace.WriteText("notes.md", MarkdownSample);
        var html = await File.ReadAllTextAsync(Single(await RunAsync("md.html", input)));

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Release notes</title>", html);
        Assert.Contains("<h1 id=\"release-notes\">Release notes</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.Contains("prefers-color-scheme: dark", html);
    }

    [Fact]
    public async Task Markdown_to_html_drops_active_content_but_keeps_markup_and_links()
    {
        var input = Workspace.WriteText("raw.md", """
            # Raw HTML

            <script>alert('x')</script>
            <iframe src="https://example.com/frame"></iframe>
            <object data="movie.swf"></object><embed src="movie.swf">
            <p onclick="alert('x')" onmouseover="alert('y')" class="note">Click <b>me</b></p>
            <a href="javascript:alert('x')">bad link</a> <a href=" JaVaScRiPt:alert('x')">bad link 2</a>
            <img src="//evil/leak.png" alt="share"> <img src="pic.png" onerror="alert('z')" alt="local">
            <svg><script>alert('svg')</script><a href="javascript:alert('svg')"><text>svg link</text></a></svg>
            <meta http-equiv="refresh" content="0;url=https://example.com">

            A [normal link](https://example.com/page), a [relative one](other.md#part) and ![web picture](https://example.com/pic.png).
            """);
        var html = await File.ReadAllTextAsync(Single(await RunAsync("md.html", input)));

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<object", html);
        Assert.DoesNotContain("<embed", html);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", html);
        Assert.DoesNotContain("refresh", html);
        Assert.Contains("<h1 id=\"raw-html\">Raw HTML</h1>", html);
        Assert.Contains("<p class=\"note\">Click <b>me</b></p>", html);
        Assert.Contains(">bad link</a>", html);
        Assert.Contains("<img src=\"pic.png\" alt=\"local\">", html);
        Assert.Contains("<a href=\"https://example.com/page\">normal link</a>", html);
        Assert.Contains("<a href=\"other.md#part\">relative one</a>", html);
        Assert.Contains("src=\"https://example.com/pic.png\"", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("script-src 'none'", html);
        Assert.DoesNotContain("img-src", html); // web pictures still show when the page is opened
    }

    [Fact]
    public async Task Markdown_to_docx_embeds_only_images_inside_its_folder()
    {
        await File.WriteAllBytesAsync(Workspace.Path("doc/pics/inside.png"), Png(16, 16, SKColors.Blue));
        var outside = Workspace.Path("outside.png");
        await File.WriteAllBytesAsync(outside, Png(16, 16, SKColors.Green));
        var input = Workspace.WriteText("doc/notes.md", $"""
            # Pictures

            ![inside](pics/inside.png)

            ![inside too](./pics/../pics/inside.png)

            ![parent](../outside.png)

            ![absolute](<{outside.Replace('\\', '/')}>)

            ![file uri]({new Uri(outside).AbsoluteUri})

            ![unc](//127.0.0.1/c$/leak.png)

            ![unc backslashes](\\\\127.0.0.1\c$\leak.png)

            ![file host](file://127.0.0.1/c$/leak.png)

            ![device](//?/C:/leak.png)

            ![device backslashes](\\\\.\C:\leak.png)
            """);
        var result = await RunAsync("md.docx", input);

        using var document = WordprocessingDocument.Open(Single(result), false);
        var main = document.MainDocumentPart!;
        Assert.Equal(2, main.ImageParts.Count());
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("outside the Markdown file's folder", warning);
        var text = main.Document!.Body!.InnerText;
        foreach (var skipped in new[] { "parent", "absolute", "file uri", "unc", "unc backslashes", "file host", "device", "device backslashes" })
            Assert.Contains($"[{skipped}]", text);
    }

    [Fact]
    public async Task Markdown_to_docx_does_not_follow_a_junction_out_of_its_folder()
    {
        await File.WriteAllBytesAsync(Workspace.Path("elsewhere/secret.png"), Png(16, 16, SKColors.Green));
        var link = Path.Combine(Workspace.Root, "doc", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(Workspace.Root, "elsewhere")}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!)
        {
            await mklink.WaitForExitAsync();
        }
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null, "mklink /J should make a junction");
        var input = Workspace.WriteText("doc/notes.md", "# Linked\n\n![secret](linked/secret.png)\n");

        var result = await RunAsync("md.docx", input);
        Directory.Delete(link); // the junction only, before the workspace is cleaned up

        using var document = WordprocessingDocument.Open(Single(result), false);
        Assert.Empty(document.MainDocumentPart!.ImageParts);
        Assert.Contains(result.Warnings, w => w.Contains("outside the Markdown file's folder"));
    }

    [Fact]
    public async Task Markdown_to_docx_maps_structure_to_word_styles()
    {
        await File.WriteAllBytesAsync(Workspace.Path("pic.png"), Png(64, 32, SKColors.Red));
        var input = Workspace.WriteText("notes.md", MarkdownSample);
        var output = Single(await RunAsync("md.docx", input));

        using var document = WordprocessingDocument.Open(output, false);
        var main = document.MainDocumentPart!;
        var paragraphs = main.Document!.Body!.Elements<Paragraph>().ToList();
        string? Style(Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

        Assert.Equal("Release notes", paragraphs.Single(p => Style(p) == "Heading1").InnerText);
        var runs = main.Document.Body.Descendants<Run>().ToList();
        Assert.Contains(runs, r => r.InnerText == "bold" && r.RunProperties?.Bold is not null);
        Assert.Contains(runs, r => r.InnerText == "italic" && r.RunProperties?.Italic is not null);
        Assert.Contains(runs, r => r.InnerText == "gone" && r.RunProperties?.Strike is not null);
        Assert.Contains(runs, r => r.InnerText == "code" && r.RunProperties?.RunStyle?.Val?.Value == "CodeChar");
        Assert.Single(main.HyperlinkRelationships, h => h.Uri.ToString() == "https://example.com/");
        Assert.Equal(3, paragraphs.Count(p => p.ParagraphProperties?.NumberingProperties is not null));
        Assert.Contains(paragraphs, p => Style(p) == "Quote" && p.InnerText == "A wise quote.");
        Assert.Contains(paragraphs, p => Style(p) == "Code" && p.InnerText == "var x = 42;");
        Assert.Single(main.Document.Body.Elements<Table>());
        Assert.Single(main.ImageParts);

        var text = await File.ReadAllTextAsync(Single(await RunAsync("doc.txt", output)));
        Assert.Contains("1. First", text);
        Assert.Contains("2. Second", text);
        Assert.Contains("Alpha\t1", text);
    }

    [Fact]
    public async Task Html_to_text_reads_like_text()
    {
        var input = Workspace.WriteText("page.html", """
            <html><head><title>T</title><style>p { color: red }</style><script>alert('x')</script></head>
            <body><h1>Main title</h1><p>Hello <b>world</b>,
               spread    over lines.</p><ul><li>one</li><li>two</li></ul><ol><li>first</li></ol>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table><pre>  keep   spacing</pre></body></html>
            """);
        var text = await File.ReadAllTextAsync(Single(await RunAsync("html.txt", input)));

        Assert.Equal("""
            Main title
            ==========

            Hello world, spread over lines.

            • one
            • two

            1. first

            A	B
            1	2

              keep   spacing

            """.Replace("\r\n", "\n").Replace("\n", "\r\n"), text);
    }

    [Fact]
    public async Task Empty_text_file_gets_a_friendly_error()
    {
        var input = Workspace.WriteText("empty.txt", "  \n ");
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("text.pdf", input));
        Assert.Equal("This text file is empty.", error.Message);
    }
}
