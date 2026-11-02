using Clementine.Engines.Documents.OpenXml;
using SkiaSharp;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>PDF → PNG / JPEG pages, PDF → TXT, PDF → DOCX.</summary>
internal static class PdfConversions
{
    public static IEnumerable<IOperation> Create()
    {
        var pdf = DocumentFormats.Pdf;
        yield return new DocumentOperation(
            Info.Convert("pdf.png.150", "PNG", "150 DPI", "PDF → PNG (150 DPI)",
                "Each page as a PNG image at screen resolution. Several pages go into a folder.", "image", ".png", 40, heavy: true),
            pdf, RenderHandler(SKEncodedImageFormat.Png, 150));
        yield return new DocumentOperation(
            Info.Convert("pdf.png.300", "PNG", "300 DPI", "PDF → PNG (300 DPI)",
                "Each page as a sharp, print-quality PNG. Several pages go into a folder.", "image", ".png", 41, heavy: true),
            pdf, RenderHandler(SKEncodedImageFormat.Png, 300));
        yield return new DocumentOperation(
            Info.Convert("pdf.jpeg.150", "JPEG", "150 DPI", "PDF → JPEG (150 DPI)",
                "Each page as a compact JPEG at screen resolution. Several pages go into a folder.", "image", ".jpg", 45, heavy: true),
            pdf, RenderHandler(SKEncodedImageFormat.Jpeg, 150));
        yield return new DocumentOperation(
            Info.Convert("pdf.jpeg.300", "JPEG", "300 DPI", "PDF → JPEG (300 DPI)",
                "Each page as a print-quality JPEG. Several pages go into a folder.", "image", ".jpg", 46, heavy: true),
            pdf, RenderHandler(SKEncodedImageFormat.Jpeg, 300));
        yield return new DocumentOperation(
            Info.Convert("pdf.txt", "TXT", "From PDF", "PDF → plain text",
                "Extracts the selectable text in reading order. Scanned pages have no text to extract.", "text", ".txt", 30),
            pdf, ToTextAsync);
        yield return new DocumentOperation(
            Info.Convert("pdf.docx", "DOCX", "From PDF", "PDF → Word document",
                "Text and headings as an editable Word document (best effort — layout and images aren't kept).", "document", ".docx", 20),
            pdf, ToDocxAsync);
    }

    static OperationHandler RenderHandler(SKEncodedImageFormat format, int dpi) => (request, progress, cancellationToken) =>
        RenderToImagesAsync(request, PdfFiles.ReadBytes(request.Input.Path), format, dpi, "pages", progress, cancellationToken);

    /// <summary>
    /// Renders a PDF (bytes) to images: one page → one file next to the input, several → a folder
    /// "name (PNG pages)" of "name 01.png" files. Also used for slides (PowerPoint → PDF → images).
    /// </summary>
    public static async Task<OperationResult> RenderToImagesAsync(
        OperationRequest request, byte[] pdf, SKEncodedImageFormat format, int dpi, string folderNoun, IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var extension = format == SKEncodedImageFormat.Png ? ".png" : ".jpg";
        var label = format == SKEncodedImageFormat.Png ? "PNG" : "JPEG";
        var quality = format == SKEncodedImageFormat.Png ? 100 : 90;
        progress.Report(JobProgress.Indeterminate("Opening PDF…"));
        var count = await PdfRenderer.GetPageCountAsync(pdf, null, cancellationToken).ConfigureAwait(false);
        if (count == 0)
            throw new OperationException("This PDF has no pages.");

        if (count == 1)
        {
            using var single = request.Output.BeginFile(request.Input, extension);
            var warnings1 = await PdfRenderer.RenderAsync(pdf, dpi, (page, _) =>
            {
                progress.Report(new JobProgress(0.8, "Saving image…"));
                PdfRenderer.Save(page.Bitmap, single.WorkingPath, format, quality);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            return new OperationResult { Outputs = [single.Commit()], Warnings = warnings1 };
        }

        using var folder = request.Output.BeginDirectory(request.Input, $"{label} {folderNoun}");
        var warnings = await PdfRenderer.RenderAsync(pdf, dpi, (page, total) =>
        {
            var name = Outputs.NumberedName(request.Input.BaseName, page.Number, count, extension);
            PdfRenderer.Save(page.Bitmap, Path.Combine(folder.WorkingPath, name), format, quality);
            progress.Report(new JobProgress((double)page.Number / total, $"Page {page.Number} of {total}…"));
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return new OperationResult { Outputs = [folder.Commit()], Warnings = warnings };
    }

    static async Task<OperationResult> ToTextAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var text = await Task.Run(() => PdfText.ExtractText(request.Input.Path, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(await Outputs.TextAsync(request, text, cancellationToken).ConfigureAwait(false));
    }

    static Task<OperationResult> ToDocxAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var pages = PdfText.ExtractBlocks(request.Input.Path, progress, cancellationToken);
        var documentBody = BodySize(pages.SelectMany(p => p));

        var path = Outputs.File(request, ".docx", null, working =>
        {
            using var docx = DocxBuilder.Create(working, request.Input.BaseName);
            for (var p = 0; p < pages.Count; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p > 0)
                    docx.PageBreak();
                // Merged or mixed PDFs change body size from page to page; judge headings against the page itself when it has enough text.
                var body = pages[p].Sum(b => b.Text.Length) >= 300 ? BodySize(pages[p]) : documentBody;
                foreach (var block in pages[p])
                {
                    if (block.IsMonospaced)
                    {
                        docx.CodeBlock(block.Text);
                        continue;
                    }
                    var level = HeadingLevel(block, body);
                    if (level > 0)
                        docx.Heading(level, [RichRun.Plain(block.Text)]);
                    else
                        docx.Paragraph([RichRun.Plain(block.Text)]);
                }
            }
            progress.Report(new JobProgress(0.95, "Saving document…"));
            docx.Save();
        });
        return OperationResult.FromOutputs(path);
    }, cancellationToken);

    /// <summary>The font size most of the text is set in (weighted by characters).</summary>
    static double BodySize(IEnumerable<PdfTextBlock> blocks)
    {
        var sizes = blocks.Where(b => b.FontSize > 0 && !b.IsMonospaced)
            .SelectMany(b => Enumerable.Repeat(b.FontSize, Math.Min(b.Text.Length, 2000)))
            .OrderBy(s => s)
            .ToList();
        return sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
    }

    /// <summary>Short blocks set noticeably larger than body text (or bold, short and unpunctuated) read as headings.</summary>
    static int HeadingLevel(PdfTextBlock block, double bodySize)
    {
        if (bodySize <= 0 || block.Text.Length > 160)
            return 0;
        var ratio = block.FontSize / bodySize;
        if (ratio >= 1.7)
            return 1;
        if (ratio >= 1.3)
            return 2;
        if (ratio >= 1.15 || (block.IsBold && ratio >= 0.95 && block.Text.Length <= 80 && !block.Text.TrimEnd().EndsWith('.')))
            return 3;
        return 0;
    }
}
