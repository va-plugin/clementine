using Clementine.Engines.Documents.OpenXml;

namespace Clementine.Engines.Documents.Markup;

/// <summary>Plain text, Markdown and HTML conversions.</summary>
internal static class MarkupOperations
{
    public static IEnumerable<IOperation> Create(DocumentServices services)
    {
        var text = DocumentFormats.PlainText;
        var markdown = DocumentFormats.Markdown;
        var html = DocumentFormats.Html;

        yield return new DocumentOperation(
            Info.Convert("text.pdf", "PDF", "From text", "Text → PDF",
                "Wraps the text onto pages in a readable font, with page numbers; every script and language renders.", "pdf", ".pdf", 10),
            text, (request, progress, cancellationToken) => TextToPdfAsync(services, request, progress, cancellationToken));
        yield return new DocumentOperation(
            Info.Convert("text.docx", "DOCX", "From text", "Text → Word document",
                "Each line becomes a paragraph you can edit and restyle in Word.", "document", ".docx", 20),
            text, TextToDocxAsync);

        yield return new DocumentOperation(
            Info.Convert("md.html", "HTML", "From Markdown", "Markdown → web page",
                "A clean, styled HTML page (tables, task lists, code blocks) that works offline.", "code", ".html", 15),
            markdown, MarkdownToHtmlAsync);
        yield return new DocumentOperation(
            Info.Convert("md.pdf", "PDF", "From Markdown", "Markdown → PDF",
                "Renders the Markdown with a clean style and prints it to PDF with Edge.", "pdf", ".pdf", 10, heavy: true),
            markdown, (request, progress, cancellationToken) => MarkdownToPdfAsync(services, request, progress, cancellationToken))
        { Availability = () => services.Edge.Availability };
        yield return new DocumentOperation(
            Info.Convert("md.docx", "DOCX", "From Markdown", "Markdown → Word document",
                "Headings, lists, bold/italic, code, tables, links and local images as a styled Word document.", "document", ".docx", 20),
            markdown, MarkdownToDocxAsync);

        yield return new DocumentOperation(
            Info.Convert("html.pdf", "PDF", "From web page", "Web page → PDF",
                "Prints the saved page to PDF with Edge, including its images and styles.", "pdf", ".pdf", 10, heavy: true),
            html, async (request, progress, cancellationToken) =>
            {
                var path = await Outputs.FileAsync(request, ".pdf", null,
                    pdf => services.Edge.PrintAsync(request.Input.Path, pdf, request.TempDirectory, progress, cancellationToken)).ConfigureAwait(false);
                return OperationResult.FromOutputs(path);
            })
        { Availability = () => services.Edge.Availability };
        yield return new DocumentOperation(
            Info.Convert("html.txt", "TXT", "From web page", "Web page → plain text",
                "The readable text of the page — no tags, scripts or styles.", "text", ".txt", 30),
            html, async (request, progress, cancellationToken) =>
            {
                var source = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
                var plain = HtmlToText.Convert(source);
                return OperationResult.FromOutputs(await Outputs.TextAsync(request, plain, cancellationToken).ConfigureAwait(false));
            });
    }

    static async Task<OperationResult> TextToPdfAsync(DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var content = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        if (content.Trim().Length == 0)
            throw new OperationException("This text file is empty.");
        var monospace = TextPdfWriter.PrefersMonospace(content, request.Input.Extension);
        var warnings = new List<string>();
        if (TextPdfWriter.NeedsComplexShaping(content))
        {
            if (services.Edge.Availability.IsAvailable)
                return OperationResult.FromOutputs(await PrintTextWithEdgeAsync(services, request, content, monospace, progress, cancellationToken).ConfigureAwait(false));
            warnings.Add("Right-to-left and complex scripts may not be shaped correctly without Microsoft Edge.");
        }

        var writer = new TextPdfWriter(monospace);
        var path = await Task.Run(() => Outputs.File(request, ".pdf", null,
            pdf => writer.Write(content, pdf, request.Input.BaseName, progress, cancellationToken)), cancellationToken).ConfigureAwait(false);
        return new OperationResult { Outputs = [path], Warnings = warnings };
    }

    /// <summary>Text in Arabic, Hebrew, Indic or Thai script: let Edge shape it, one line per block with its own direction.</summary>
    static async Task<string> PrintTextWithEdgeAsync(
        DocumentServices services, OperationRequest request, string content, bool monospace, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var body = new System.Text.StringBuilder("<div class=\"plain-text\">\n");
        foreach (var line in TextFiles.NormalizeNewlines(content).TrimEnd('\n').Split('\n'))
        {
            body.Append(line.Length == 0 ? "<div class=\"line\">&#8203;</div>" : $"<div class=\"line\" dir=\"auto\">{System.Net.WebUtility.HtmlEncode(line)}</div>");
            body.Append('\n');
        }
        body.Append("</div>");
        var font = monospace ? "\"Cascadia Mono\", Consolas, monospace" : "\"Segoe UI\", system-ui, sans-serif";
        var css = $".plain-text {{ font: 10.5pt/1.45 {font}; tab-size: 4; }} .line {{ white-space: pre-wrap; unicode-bidi: plaintext; text-align: start; }}";
        var html = HtmlPage.Build(request.Input.BaseName, body.ToString(), extraCss: css);
        var page = Path.Combine(request.TempDirectory, "text.html");
        await File.WriteAllTextAsync(page, html, TextFiles.Utf8, cancellationToken).ConfigureAwait(false);
        return await Outputs.FileAsync(request, ".pdf", null,
            pdf => services.Edge.PrintAsync(page, pdf, request.TempDirectory, progress, cancellationToken)).ConfigureAwait(false);
    }

    static async Task<OperationResult> TextToDocxAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var content = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var path = Outputs.File(request, ".docx", null, docxPath =>
        {
            using var docx = DocxBuilder.Create(docxPath, request.Input.BaseName);
            var lines = TextFiles.NormalizeNewlines(content).TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i % 2000 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                docx.PlainLine(lines[i]);
            }
            docx.Save();
        });
        return OperationResult.FromOutputs(path);
    }

    static async Task<OperationResult> MarkdownToHtmlAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var markdown = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        // Relative image links only work beside the Markdown file; when saving elsewhere, point them back at it.
        var savedElsewhere = !string.Equals(Path.GetFullPath(request.Output.ResolveDirectory(request.Input)), Path.GetFullPath(request.Input.DirectoryPath), StringComparison.OrdinalIgnoreCase);
        var html = MarkdownConverter.ToHtmlPage(markdown, request.Input.BaseName, savedElsewhere ? request.Input.DirectoryPath : null);
        var path = await Outputs.FileAsync(request, ".html", null, file => File.WriteAllTextAsync(file, html, TextFiles.Utf8, cancellationToken)).ConfigureAwait(false);
        return OperationResult.FromOutputs(path);
    }

    static async Task<OperationResult> MarkdownToPdfAsync(DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var markdown = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        // The page is printed from the temp folder; <base> points relative images back at the Markdown file's folder.
        var html = MarkdownConverter.ToHtmlPage(markdown, request.Input.BaseName, request.Input.DirectoryPath);
        var page = Path.Combine(request.TempDirectory, "document.html");
        await File.WriteAllTextAsync(page, html, TextFiles.Utf8, cancellationToken).ConfigureAwait(false);
        var path = await Outputs.FileAsync(request, ".pdf", null,
            pdf => services.Edge.PrintAsync(page, pdf, request.TempDirectory, progress, cancellationToken)).ConfigureAwait(false);
        return OperationResult.FromOutputs(path);
    }

    static async Task<OperationResult> MarkdownToDocxAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var markdown = await TextFiles.ReadAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> warnings = [];
        var path = Outputs.File(request, ".docx", null, docx =>
            warnings = MarkdownConverter.WriteDocx(markdown, docx, request.Input.BaseName, request.Input.DirectoryPath, cancellationToken));
        return new OperationResult { Outputs = [path], Warnings = warnings };
    }
}
