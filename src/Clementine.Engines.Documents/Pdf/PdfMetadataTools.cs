using System.Globalization;
using System.Text;
using PdfSharp.Pdf;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>Remove or edit document metadata, and the Info report.</summary>
internal static class PdfMetadataTools
{
    static readonly string[] PageMetadataKeys = ["/Metadata", "/PieceInfo"];

    public static IEnumerable<IOperation> Create()
    {
        var pdf = DocumentFormats.Pdf;
        yield return new DocumentOperation(
            Info.Tool("pdf.metadata.remove", "Metadata", "Remove all", "Remove PDF metadata",
                "Saves a copy without title, author, subject, keywords, dates, app names or XMP data.", "metadata", ".pdf", 70),
            pdf, RemoveAsync);
        yield return new DocumentOperation(
            Info.Tool("pdf.metadata.edit", "Metadata", "Edit…", "Edit PDF metadata",
                "Changes the title, author, subject and keywords readers show.", "metadata", ".pdf", 71),
            pdf, EditAsync)
        {
            Schema = new OptionSchema("Edit PDF metadata", [
                new TextField("title", "Title", ""),
                new TextField("author", "Author", ""),
                new TextField("subject", "Subject", ""),
                new TextField("keywords", "Keywords", "") { Placeholder = "comma, separated" },
            ]) { ConfirmLabel = "Save" },
            DynamicDefaults = CurrentMetadataAsync,
        };
        yield return new DocumentOperation(
            Info.Tool("pdf.info", "Info", null, "PDF info",
                "Pages, page size, title, author, producer, encryption and PDF version.", "info", null, 80),
            pdf, InfoAsync);
    }

    static Task<OperationResult> RemoveAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var document = PdfFiles.OpenForEdit(request.Input.Path);
        document.Info.Elements.Clear();
        // PDFsharp fills in Creator/Producer on save when they're missing; present-but-empty values are kept as they are.
        document.Info.Elements.SetString("/Creator", "");
        document.Info.Elements.SetString("/Producer", "");
        document.Internals.Catalog.Elements.Remove("/Metadata");
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        foreach (var page in document.Pages)
        {
            foreach (var key in PageMetadataKeys)
                page.Elements.Remove(key);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var path = Outputs.File(request, ".pdf", "no metadata", working => PdfFiles.Save(document, working));
        return OperationResult.FromOutputs(path);
    }, cancellationToken);

    static Task<OperationResult> EditAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var document = PdfFiles.OpenForEdit(request.Input.Path);
        document.Info.Title = request.Options.GetString("title", "").Trim();
        document.Info.Author = request.Options.GetString("author", "").Trim();
        document.Info.Subject = request.Options.GetString("subject", "").Trim();
        document.Info.Keywords = request.Options.GetString("keywords", "").Trim();
        // Readers prefer the XMP packet over the Info dictionary; drop the stale one so the new values show everywhere.
        document.Internals.Catalog.Elements.Remove("/Metadata");
        cancellationToken.ThrowIfCancellationRequested();
        var path = Outputs.File(request, ".pdf", "edited", working => PdfFiles.Save(document, working));
        return OperationResult.FromOutputs(path);
    }, cancellationToken);

    static Task<OptionValues> CurrentMetadataAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var document = PdfText.Open(inputs[0].Path);
        var info = document.Information;
        return new OptionValues
        {
            ["title"] = info.Title ?? "",
            ["author"] = info.Author ?? "",
            ["subject"] = info.Subject ?? "",
            ["keywords"] = info.Keywords ?? "",
        };
    }, cancellationToken);

    static Task<OperationResult> InfoAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var input = request.Input;
        var report = new StringBuilder();
        Line(report, "File", $"{input.Name} ({FormatSize(input.Length)})");
        if (PdfFiles.RequiresPassword(input.Path))
        {
            Line(report, "Encryption", "Password required to open");
            report.Append("Other details are hidden until the password is removed.\n");
            return OperationResult.FromText($"{input.Name} — PDF info", report.ToString());
        }

        using var document = PdfText.Open(input.Path);
        var info = document.Information;
        Line(report, "PDF version", document.Version.ToString("0.0", CultureInfo.InvariantCulture));
        Line(report, "Pages", document.NumberOfPages.ToString(CultureInfo.InvariantCulture));
        if (document.NumberOfPages > 0)
        {
            var first = document.GetPage(1);
            var sizes = Enumerable.Range(1, Math.Min(document.NumberOfPages, 200))
                .Select(n => document.GetPage(n))
                .Select(p => (Math.Round(p.Width), Math.Round(p.Height)))
                .Distinct()
                .Count();
            Line(report, "Page size", DescribePageSize(first.Width, first.Height) + (sizes > 1 ? " (first page; sizes vary)" : ""));
        }
        Line(report, "Title", info.Title);
        Line(report, "Author", info.Author);
        Line(report, "Subject", info.Subject);
        Line(report, "Keywords", info.Keywords);
        Line(report, "Creator", info.Creator);
        Line(report, "Producer", info.Producer);
        Line(report, "Created", FormatDate(info.GetCreatedDateTimeOffset()));
        Line(report, "Modified", FormatDate(info.GetModifiedDateTimeOffset()));
        Line(report, "Encryption", document.IsEncrypted ? "Yes (permissions restricted, opens without a password)" : "None");
        Line(report, "Text", PdfText.HasText(document) ? "Selectable" : "None found on the first pages (probably scanned)");
        Line(report, "Bookmarks", document.TryGetBookmarks(out var bookmarks) ? bookmarks.GetNodes().Count().ToString(CultureInfo.InvariantCulture) : "None");
        Line(report, "Form fields", document.TryGetForm(out var form) ? form.Fields.Count.ToString(CultureInfo.InvariantCulture) : "None");
        return OperationResult.FromText($"{input.Name} — PDF info", report.ToString());
    }, cancellationToken);

    static void Line(StringBuilder report, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            report.Append(label).Append(": ").Append(value.Trim()).Append('\n');
    }

    /// <summary>"210 × 297 mm (A4, portrait)".</summary>
    public static string DescribePageSize(double widthPoints, double heightPoints)
    {
        var widthMm = widthPoints / 72 * 25.4;
        var heightMm = heightPoints / 72 * 25.4;
        var orientation = Math.Abs(widthMm - heightMm) < 1 ? "square" : widthMm > heightMm ? "landscape" : "portrait";
        (string Name, double W, double H)[] known =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("A6", 105, 148), ("Letter", 215.9, 279.4),
            ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8), ("B5", 176, 250),
        ];
        var shortSide = Math.Min(widthMm, heightMm);
        var longSide = Math.Max(widthMm, heightMm);
        var name = known.FirstOrDefault(k => Math.Abs(k.W - shortSide) < 2.5 && Math.Abs(k.H - longSide) < 2.5).Name;
        var size = string.Create(CultureInfo.InvariantCulture, $"{widthMm:0} × {heightMm:0} mm");
        return name is null ? $"{size} ({orientation})" : $"{size} ({name}, {orientation})";
    }

    static string? FormatDate(DateTimeOffset? date) => date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };
}
