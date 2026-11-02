namespace Clementine.Engines.Documents.Ebook;

/// <summary>EPUB → PDF (Edge), TXT and single-file HTML.</summary>
internal static class EpubOperations
{
    public static IEnumerable<IOperation> Create(DocumentServices services)
    {
        var epub = DocumentFormats.Epub;
        yield return new DocumentOperation(
            Info.Convert("epub.pdf", "PDF", "From e-book", "E-book → PDF",
                "Every chapter in reading order with its images, printed with Edge. DRM-protected books can't be read.", "pdf", ".pdf", 10, heavy: true),
            epub, async (request, progress, cancellationToken) =>
            {
                var html = BuildHtml(request, progress, cancellationToken);
                var page = Path.Combine(request.TempDirectory, "book.html");
                await File.WriteAllTextAsync(page, html, TextFiles.Utf8, cancellationToken).ConfigureAwait(false);
                var path = await Outputs.FileAsync(request, ".pdf", null,
                    pdf => services.Edge.PrintAsync(page, pdf, request.TempDirectory, progress, cancellationToken)).ConfigureAwait(false);
                return OperationResult.FromOutputs(path);
            })
        { Availability = () => services.Edge.Availability };
        yield return new DocumentOperation(
            Info.Convert("epub.txt", "TXT", "From e-book", "E-book → plain text",
                "The book's text, chapter by chapter, in reading order.", "text", ".txt", 30),
            epub, async (request, progress, cancellationToken) =>
            {
                var book = EpubBook.Open(request.Input.Path, cancellationToken);
                var text = EpubHtml.ExtractText(book, progress, cancellationToken);
                return OperationResult.FromOutputs(await Outputs.TextAsync(request, text, cancellationToken).ConfigureAwait(false));
            });
        yield return new DocumentOperation(
            Info.Convert("epub.html", "HTML", "From e-book", "E-book → single web page",
                "The whole book as one HTML file with images built in — readable in any browser.", "code", ".html", 60),
            epub, async (request, progress, cancellationToken) =>
            {
                var html = BuildHtml(request, progress, cancellationToken);
                var path = await Outputs.FileAsync(request, ".html", null, file => File.WriteAllTextAsync(file, html, TextFiles.Utf8, cancellationToken)).ConfigureAwait(false);
                return OperationResult.FromOutputs(path);
            });
    }

    static string BuildHtml(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Opening e-book…"));
        var book = EpubBook.Open(request.Input.Path, cancellationToken);
        return EpubHtml.BuildSingleFile(book, progress, cancellationToken);
    }
}
