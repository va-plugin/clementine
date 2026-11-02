using AngleSharp.Html.Parser;
using Clementine.Engines.Documents.External;
using Clementine.Engines.Documents.Markup;
using Clementine.Engines.Documents.OpenXml;
using Clementine.Engines.Documents.OpenXml.Word;
using Clementine.Engines.Documents.Pdf;
using SkiaSharp;

namespace Clementine.Engines.Documents.Office;

/// <summary>
/// Word processor and presentation conversions: DOCX → PDF / HTML built in (or LibreOffice when installed), LibreOffice
/// for presentations and legacy formats, OpenXML for text.
/// </summary>
internal static class OfficeOperations
{
    /// <summary>Slides render at 144 DPI: a 13.33 × 7.5 in (16:9) slide becomes 1920 × 1080.</summary>
    const int SlideDpi = 144;

    public static IEnumerable<IOperation> Create(DocumentServices services)
    {
        var word = DocumentFormats.Word;
        var slides = DocumentFormats.Slides;

        // Word processing ----------------------------------------------------------------------------------------
        // DOCX → PDF / HTML work without LibreOffice (built-in converter, Edge prints); LibreOffice is used when installed.
        yield return new DocumentOperation(
            Info.Convert("doc.pdf", "PDF", "From document", "Document → PDF",
                "Converts most documents faithfully; complex layouts may differ. Uses LibreOffice instead when it's installed.", "pdf", ".pdf", 10, heavy: true),
            DocumentFormats.WordOpenXml, (request, progress, cancellationToken) => BuiltInOffice.PreferLibreOfficeAsync(services, request, progress, cancellationToken,
                () => LibreOfficeAsync(services, request, LibreOfficeTarget.Pdf, null, progress, cancellationToken),
                () => DocxToPdfAsync(services, request, progress, cancellationToken)))
        { Availability = () => BuiltInOffice.PdfAvailability(services) };
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.pdf.lo", "PDF", "From document", "Document → PDF",
                "Prints the document to PDF with LibreOffice, keeping its layout, fonts and images.", "pdf", ".pdf", 10),
            DocumentFormats.WordLegacy, LibreOfficeTarget.Pdf);
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.docx", "DOCX", "From document", "Document → Word (DOCX)",
                "Converts to a modern Word document with LibreOffice.", "document", ".docx", 20),
            DocumentFormats.Except(word, ".docx"), LibreOfficeTarget.Docx);
        yield return new DocumentOperation(
            Info.Convert("doc.txt", "TXT", "From document", "Word document → plain text",
                "The document's text with list markers and tab-separated tables.", "text", ".txt", 30),
            DocumentFormats.WordOpenXml, async (request, progress, cancellationToken) =>
            {
                var text = await Task.Run(() => DocxText.Extract(request.Input.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
                return OperationResult.FromOutputs(await Outputs.TextAsync(request, text, cancellationToken).ConfigureAwait(false));
            });
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.txt.lo", "TXT", "From document", "Document → plain text",
                "The document's text, extracted with LibreOffice.", "text", ".txt", 30),
            DocumentFormats.WordLegacy, LibreOfficeTarget.Txt);
        yield return new DocumentOperation(
            Info.Convert("doc.html", "HTML", "From document", "Document → web page",
                "A single HTML file with the document's formatting and pictures built in. Complex layouts may differ.", "code", ".html", 60, heavy: true),
            DocumentFormats.WordOpenXml, (request, progress, cancellationToken) => BuiltInOffice.PreferLibreOfficeAsync(services, request, progress, cancellationToken,
                () => ToHtmlAsync(services, request, progress, cancellationToken),
                () => DocxToHtmlAsync(request, progress, cancellationToken), builtInNeedsEdge: false));
        yield return new DocumentOperation(
            Info.Convert("doc.html.lo", "HTML", "From document", "Document → web page",
                "A single HTML file with the document's formatting and images built in.", "code", ".html", 60, heavy: true),
            DocumentFormats.WordLegacy, (request, progress, cancellationToken) => ToHtmlAsync(services, request, progress, cancellationToken))
        { Availability = () => services.LibreOffice.Availability };
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.odt", "ODT", null, "Document → OpenDocument Text",
                "Converts to LibreOffice's native ODT format.", "document", ".odt", 210),
            DocumentFormats.Except(word, ".odt", ".ott", ".fodt"), LibreOfficeTarget.Odt);
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.doc", "DOC", null, "Document → Word 97–2003 (DOC)",
                "The old Word format, for very old software.", "document", ".doc", 220),
            DocumentFormats.Except(word, ".doc", ".dot"), LibreOfficeTarget.Doc);
        yield return LibreOfficeConversion(services,
            Info.Convert("doc.rtf", "RTF", null, "Document → Rich Text (RTF)",
                "Rich Text Format opens in almost any word processor, including WordPad.", "document", ".rtf", 230),
            DocumentFormats.Except(word, ".rtf"), LibreOfficeTarget.Rtf);

        // Presentations ------------------------------------------------------------------------------------------
        yield return LibreOfficeConversion(services,
            Info.Convert("slides.pdf", "PDF", "From slides", "Presentation → PDF",
                "One page per slide, printed with LibreOffice.", "pdf", ".pdf", 10),
            slides, LibreOfficeTarget.Pdf);
        yield return new DocumentOperation(
            Info.Convert("slides.png", "PNG", "Slides", "Presentation → PNG slides",
                "Every slide as a 1080p PNG image (in a folder for several slides).", "image", ".png", 40, heavy: true),
            slides, async (request, progress, cancellationToken) =>
            {
                var pdf = await services.LibreOffice.ConvertAsync(request.Input.Path, LibreOfficeTarget.Pdf, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
                return await PdfConversions.RenderToImagesAsync(request, await File.ReadAllBytesAsync(pdf, cancellationToken).ConfigureAwait(false),
                    SKEncodedImageFormat.Png, SlideDpi, "slides", progress, cancellationToken).ConfigureAwait(false);
            })
        { Availability = () => services.LibreOffice.Availability };
        yield return new DocumentOperation(
            Info.Convert("slides.txt", "TXT", "From slides", "Presentation → plain text",
                "The text of every slide in order, followed by its speaker notes.", "text", ".txt", 30),
            DocumentFormats.SlidesOpenXml, async (request, progress, cancellationToken) =>
            {
                var text = await Task.Run(() => PptxText.Extract(request.Input.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
                return OperationResult.FromOutputs(await Outputs.TextAsync(request, text, cancellationToken).ConfigureAwait(false));
            });
        yield return new DocumentOperation(
            Info.Convert("slides.txt.lo", "TXT", "From slides", "Presentation → plain text",
                "The text of every slide in order, followed by its speaker notes.", "text", ".txt", 30, heavy: true),
            DocumentFormats.SlidesLegacy, async (request, progress, cancellationToken) =>
            {
                var pptx = await services.LibreOffice.ConvertAsync(request.Input.Path, LibreOfficeTarget.Pptx, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
                var text = PptxText.Extract(pptx, cancellationToken);
                return OperationResult.FromOutputs(await Outputs.TextAsync(request, text, cancellationToken).ConfigureAwait(false));
            })
        { Availability = () => services.LibreOffice.Availability };
        yield return LibreOfficeConversion(services,
            Info.Convert("slides.pptx", "PPTX", null, "Presentation → PowerPoint (PPTX)",
                "Converts to a modern PowerPoint file with LibreOffice.", "presentation", ".pptx", 200),
            DocumentFormats.Except(slides, ".pptx"), LibreOfficeTarget.Pptx);
        yield return LibreOfficeConversion(services,
            Info.Convert("slides.odp", "ODP", null, "Presentation → OpenDocument (ODP)",
                "Converts to LibreOffice Impress's native format.", "presentation", ".odp", 240),
            DocumentFormats.Except(slides, ".odp", ".fodp"), LibreOfficeTarget.Odp);
    }

    /// <summary>A plain "LibreOffice converts the file" operation (heavy, unavailable without LibreOffice).</summary>
    public static DocumentOperation LibreOfficeConversion(
        DocumentServices services, OperationInfo info, IReadOnlySet<string> accepts, LibreOfficeTarget target, string? suffix = null) =>
        new(info with { Weight = OperationWeight.Heavy }, accepts,
            (request, progress, cancellationToken) => LibreOfficeAsync(services, request, target, suffix, progress, cancellationToken))
        {
            Availability = () => services.LibreOffice.Availability,
        };

    /// <summary>Converts the input with LibreOffice and saves the result next to it.</summary>
    public static async Task<OperationResult> LibreOfficeAsync(
        DocumentServices services, OperationRequest request, LibreOfficeTarget target, string? suffix, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var produced = await services.LibreOffice.ConvertAsync(request.Input.Path, target, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(new JobProgress(0.95, "Saving…"));
        return OperationResult.FromOutputs(Outputs.CopyFrom(request, produced, target.Extension, suffix));
    }

    /// <summary>Clementine's built-in Word converter → HTML → PDF with Edge.</summary>
    static async Task<OperationResult> DocxToPdfAsync(DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading document…"));
        var converted = await Task.Run(() => WordHtmlConverter.Convert(request.Input.Path, request.Input.BaseName, forPrint: true, cancellationToken), cancellationToken).ConfigureAwait(false);
        var path = await BuiltInOffice.PrintAsync(services, request, converted.Html, progress, cancellationToken).ConfigureAwait(false);
        return new OperationResult { Outputs = [path], Warnings = converted.Warnings };
    }

    /// <summary>Clementine's built-in Word converter → one self-contained HTML file.</summary>
    static async Task<OperationResult> DocxToHtmlAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading document…"));
        var converted = await Task.Run(() => WordHtmlConverter.Convert(request.Input.Path, request.Input.BaseName, forPrint: false, cancellationToken), cancellationToken).ConfigureAwait(false);
        var path = await BuiltInOffice.SaveHtmlAsync(request, converted.Html, cancellationToken).ConfigureAwait(false);
        return new OperationResult { Outputs = [path], Warnings = converted.Warnings };
    }

    /// <summary>
    /// LibreOffice's XHTML export, with the images it wrote as separate files embedded so the page is one file. Like the
    /// built-in converter's page, it never points anywhere else: pictures that would load from the web or from other
    /// files (linked pictures) are dropped, and scripts, event handlers and the like are locked out.
    /// </summary>
    static async Task<OperationResult> ToHtmlAsync(DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var produced = await services.LibreOffice.ConvertAsync(request.Input.Path, LibreOfficeTarget.Html, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(produced)! + Path.DirectorySeparatorChar;
        var document = new HtmlParser().ParseDocument(TextFiles.Decode(await File.ReadAllBytesAsync(produced, cancellationToken).ConfigureAwait(false)));
        var dropped = false;
        foreach (var image in document.QuerySelectorAll("img"))
        {
            image.RemoveAttribute("srcset");
            var source = image.GetAttribute("src");
            if (source is null || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (EmbeddedFile(directory, source) is { } file)
            {
                var type = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".svg" => "image/svg+xml",
                    _ => "image/png",
                };
                image.SetAttribute("src", $"data:{type};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false))}");
            }
            else
            {
                image.RemoveAttribute("src");
                dropped = true;
            }
        }
        if (document.Title is not { Length: > 0 } || document.Title == "document")
            document.Title = request.Input.BaseName;
        var html = HtmlLockdown.ForWebPage("<!DOCTYPE html>\n" + document.DocumentElement.OuterHtml, null);
        var path = await Outputs.FileAsync(request, ".html", null,
            output => File.WriteAllTextAsync(output, html, TextFiles.Utf8, cancellationToken)).ConfigureAwait(false);
        return new OperationResult { Outputs = [path], Warnings = dropped ? ["Linked pictures (stored outside the document) were left out."] : [] };
    }

    /// <summary>A picture LibreOffice wrote beside its HTML ("document_html_1a2b.png"); null for anything else (web, other folders).</summary>
    static string? EmbeddedFile(string directory, string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out _) || source.StartsWith('/') || source.StartsWith('\\'))
            return null;
        try
        {
            var file = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(source)));
            return file.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? file : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
