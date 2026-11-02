using Clementine.Core;
using Clementine.Engines.Documents.Tests.Fixtures;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

public sealed class PdfConversionTests() : DocumentTestBase("pdfconv")
{
    [Fact]
    public async Task Single_page_pdf_becomes_one_png_at_300_dpi()
    {
        var input = TextPdf(Workspace.Path("one.pdf"), "Hello");
        var output = Single(await RunAsync("pdf.png.300", input));

        Assert.Equal(Path.Combine(Workspace.Root, "one.png"), output);
        using var bitmap = Decode(output);
        Assert.InRange(bitmap.Width, 2475, 2485); // 210 mm at 300 DPI = 2480 px
        Assert.InRange(bitmap.Height, 3502, 3512);
        Assert.Equal(255, bitmap.GetPixel(5, 5).Red); // white background, not transparent black
        Assert.Equal(255, bitmap.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public async Task Multi_page_pdf_becomes_a_folder_of_jpegs()
    {
        var input = TextPdf(Workspace.Path("report.pdf"), "one", "two", "three");
        var progress = new List<JobProgress>();
        var result = await Workspace.RunAsync(Op("pdf.jpeg.150"), [input], progress: new SyncProgress(progress));
        var folder = Single(result);

        Assert.Equal("report (JPEG pages)", Path.GetFileName(folder));
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { "report 01.jpg", "report 02.jpg", "report 03.jpg" }, files);
        using var bitmap = Decode(Path.Combine(folder, "report 02.jpg"));
        Assert.InRange(bitmap.Width, 1236, 1244); // 210 mm at 150 DPI = 1240 px
        Assert.Contains(progress, p => p.Fraction > 0.9);
    }

    [Fact]
    public async Task Pdf_to_text_extracts_every_page_in_order()
    {
        var input = TextPdf(Workspace.Path("text.pdf"), "Alpha beta gamma", "Delta epsilon\nZeta eta");
        var output = Single(await RunAsync("pdf.txt", input));

        var text = Normalize(await File.ReadAllTextAsync(output));
        Assert.Equal("Page 1 Alpha beta gamma Page 2 Delta epsilon Zeta eta", text);
    }

    [Fact]
    public async Task Pdf_without_text_reports_it_is_probably_scanned()
    {
        var input = PdfWithPhoto(Workspace.Path("scan.pdf"), 300, 200);
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("pdf.txt", input));
        Assert.Contains("scanned", error.Message);
        Assert.False(File.Exists(Workspace.Path("scan.txt")));
    }

    [Fact]
    public async Task Pdf_to_docx_keeps_headings_and_paragraphs()
    {
        var input = TextPdf(Workspace.Path("doc.pdf"), "This is the body text of the first page.", "Second page body.");
        var output = Single(await RunAsync("pdf.docx", input));

        using var document = WordprocessingDocument.Open(output, false);
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();
        var headings = paragraphs.Where(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.StartsWith("Heading") == true).Select(p => p.InnerText).ToList();
        Assert.Equal(new[] { "Page 1", "Page 2" }, headings);
        Assert.Contains(paragraphs, p => p.InnerText == "This is the body text of the first page.");
    }

    [Fact]
    public async Task Unicode_paths_with_spaces_work()
    {
        var input = TextPdf(Workspace.Path("Clip – ünïcødé 01 東京.pdf"), "Hello");
        var output = Single(await RunAsync("pdf.png.150", input));
        Assert.Equal("Clip – ünïcødé 01 東京.png", Path.GetFileName(output));
    }

    [Fact]
    public async Task Existing_outputs_are_never_overwritten()
    {
        var input = TextPdf(Workspace.Path("same.pdf"), "Hello");
        await File.WriteAllTextAsync(Workspace.Path("same.txt"), "keep me");
        var output = Single(await RunAsync("pdf.txt", input));

        Assert.Equal("same (2).txt", Path.GetFileName(output));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Workspace.Path("same.txt")));
    }

    [Fact]
    public async Task Cancelled_render_leaves_no_output()
    {
        var input = TextPdf(Workspace.Path("cancel.pdf"), Enumerable.Range(1, 12).Select(i => $"page {i}").ToArray());
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress([], p =>
        {
            if (p.Fraction > 0.1)
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Workspace.RunAsync(Op("pdf.png.150"), [input], progress: progress, cancellationToken: cancel.Token));
        Assert.Equal(new[] { "cancel.pdf" }, Directory.GetFileSystemEntries(Workspace.Root).Select(Path.GetFileName).Where(n => n != "_temp").ToList());
    }

    /// <summary>Progress that records synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    internal sealed class SyncProgress(List<JobProgress> log, Action<JobProgress>? onReport = null) : IProgress<JobProgress>
    {
        public void Report(JobProgress value)
        {
            lock (log)
                log.Add(value);
            onReport?.Invoke(value);
        }
    }
}
