using System.Globalization;
using Clementine.Engines.Documents.External;
using Clementine.Engines.Documents.Office;

namespace Clementine.Engines.Documents.Spreadsheets;

/// <summary>
/// Spreadsheet conversions: XLSX ↔ CSV natively; XLSX / CSV → PDF and HTML built in (LibreOffice when installed);
/// legacy formats through LibreOffice.
/// </summary>
internal static class SpreadsheetOperations
{
    public static IEnumerable<IOperation> Create(DocumentServices services)
    {
        var sheets = DocumentFormats.Sheet;
        var delimited = DocumentFormats.DelimitedText;

        yield return new DocumentOperation(
            Info.Convert("sheet.csv", "CSV", "From Excel", "Spreadsheet → CSV",
                "Cell values as UTF-8 CSV. A workbook with several sheets becomes a folder with one CSV per sheet.", "spreadsheet", ".csv", 30),
            DocumentFormats.SheetOpenXml, (request, progress, cancellationToken) =>
                Task.Run(() => WriteCsv(request, XlsxReader.Read(request.Input.Path, cancellationToken), progress, cancellationToken), cancellationToken));
        yield return new DocumentOperation(
            Info.Convert("sheet.csv.lo", "CSV", "From spreadsheet", "Spreadsheet → CSV",
                "Cell values as UTF-8 CSV (read with LibreOffice). Several sheets become a folder of CSVs.", "spreadsheet", ".csv", 30, heavy: true),
            DocumentFormats.SheetLegacy, async (request, progress, cancellationToken) =>
            {
                var xlsx = await services.LibreOffice.ConvertAsync(request.Input.Path, LibreOfficeTarget.Xlsx, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
                return WriteCsv(request, XlsxReader.Read(xlsx, cancellationToken), progress, cancellationToken);
            })
        { Availability = () => services.LibreOffice.Availability };

        yield return new DocumentOperation(
            Info.Convert("sheet.xlsx", "XLSX", "From CSV", "CSV → Excel (XLSX)",
                "Numbers become real numbers; the header row is bold and frozen. Delimiter and encoding are detected.", "spreadsheet", ".xlsx", 20),
            delimited, CsvToXlsxAsync);
        yield return OfficeOperations.LibreOfficeConversion(services,
            Info.Convert("sheet.xlsx.lo", "XLSX", "From spreadsheet", "Spreadsheet → Excel (XLSX)",
                "Converts to a modern Excel workbook with LibreOffice.", "spreadsheet", ".xlsx", 20),
            DocumentFormats.Except(sheets, ".xlsx"), LibreOfficeTarget.Xlsx);
        yield return OfficeOperations.LibreOfficeConversion(services,
            Info.Convert("sheet.ods", "ODS", null, "Spreadsheet → OpenDocument (ODS)",
                "Converts to LibreOffice Calc's native format.", "spreadsheet", ".ods", 230),
            DocumentFormats.Except(sheets, ".ods", ".fods"), LibreOfficeTarget.Ods);

        // XLSX / CSV → PDF and HTML work without LibreOffice (built-in tables, Edge prints); LibreOffice is used when installed.
        var builtIn = DocumentFormats.Union(DocumentFormats.SheetOpenXml, delimited);
        yield return new DocumentOperation(
            Info.Convert("sheet.pdf", "PDF", "From spreadsheet", "Spreadsheet → PDF",
                "Every sheet as a formatted table fitted to the page; complex layouts may differ. Uses LibreOffice instead when it's installed.", "pdf", ".pdf", 10, heavy: true),
            builtIn, (request, progress, cancellationToken) => BuiltInOffice.PreferLibreOfficeAsync(services, request, progress, cancellationToken,
                () => LibreOfficePdfAsync(services, request, progress, cancellationToken),
                async () =>
                {
                    var converted = await ToHtmlAsync(request, forPrint: true, cancellationToken).ConfigureAwait(false);
                    string path;
                    try
                    {
                        path = await BuiltInOffice.PrintAsync(services, request, converted.Html, progress, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationException failure) when (converted.Cells >= LargeSheetCells && failure.Message.StartsWith("Edge", StringComparison.Ordinal))
                    {
                        // Edge gives up on (or runs out of time for) very big tables without saying why.
                        throw new OperationException(
                            "This spreadsheet is too large to print in one go. Set a print area in Excel (Page Layout › Print Area) " +
                            "to print the part you need, or convert it to CSV or a web page instead.", failure.Details, failure);
                    }
                    return new OperationResult { Outputs = [path], Warnings = converted.Warnings };
                }))
        { Availability = () => BuiltInOffice.PdfAvailability(services) };
        yield return new DocumentOperation(
            Info.Convert("sheet.pdf.lo", "PDF", "From spreadsheet", "Spreadsheet → PDF",
                "Prints every sheet to PDF with LibreOffice.", "pdf", ".pdf", 10, heavy: true),
            DocumentFormats.SheetLegacy, (request, progress, cancellationToken) => LibreOfficePdfAsync(services, request, progress, cancellationToken))
        { Availability = () => services.LibreOffice.Availability };

        yield return new DocumentOperation(
            Info.Convert("sheet.html", "HTML", "From spreadsheet", "Spreadsheet → web page",
                "Every sheet as a formatted table in one HTML file (charts and pictures aren't included).", "code", ".html", 60),
            builtIn, async (request, progress, cancellationToken) =>
            {
                var converted = await ToHtmlAsync(request, forPrint: false, cancellationToken).ConfigureAwait(false);
                var path = await BuiltInOffice.SaveHtmlAsync(request, converted.Html, cancellationToken).ConfigureAwait(false);
                return new OperationResult { Outputs = [path], Warnings = converted.Warnings };
            });
        yield return new DocumentOperation(
            Info.Convert("sheet.html.lo", "HTML", "From spreadsheet", "Spreadsheet → web page",
                "Every sheet as a formatted table in one HTML file (read with LibreOffice).", "code", ".html", 60, heavy: true),
            DocumentFormats.SheetLegacy, async (request, progress, cancellationToken) =>
            {
                var xlsx = await services.LibreOffice.ConvertAsync(request.Input.Path, LibreOfficeTarget.Xlsx, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
                var converted = await Task.Run(() => XlsxHtml.Convert(xlsx, request.Input.BaseName, CultureInfo.CurrentCulture, cancellationToken), cancellationToken).ConfigureAwait(false);
                var path = await BuiltInOffice.SaveHtmlAsync(request, converted.Html, cancellationToken).ConfigureAwait(false);
                return new OperationResult { Outputs = [path], Warnings = converted.Warnings };
            })
        { Availability = () => services.LibreOffice.Availability };
    }

    /// <summary>Workbook or CSV → LibreOffice → PDF (CSV goes through a proper workbook so its encoding survives).</summary>
    static async Task<OperationResult> LibreOfficePdfAsync(DocumentServices services, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var source = request.Input.Path;
        if (request.Input.HasExtension(DocumentFormats.DelimitedText))
        {
            // LibreOffice would guess the CSV's encoding from the system code page; hand it a proper workbook instead.
            source = Path.Combine(request.TempDirectory, "data.xlsx");
            var rows = await ReadDelimitedAsync(request.Input, cancellationToken).ConfigureAwait(false);
            XlsxWriter.Write(source, request.Input.BaseName, rows, cancellationToken);
        }
        var pdf = await services.LibreOffice.ConvertAsync(source, LibreOfficeTarget.Pdf, request.TempDirectory, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(Outputs.CopyFrom(request, pdf, ".pdf"));
    }

    /// <summary>A printed sheet this big that Edge fails on gets the "too large to print" explanation.</summary>
    const int LargeSheetCells = 30_000;

    /// <summary>The built-in workbook / CSV → HTML conversion (<paramref name="forPrint"/>: capped for Edge's print).</summary>
    static async Task<SheetHtmlResult> ToHtmlAsync(OperationRequest request, bool forPrint, CancellationToken cancellationToken)
    {
        if (request.Input.HasExtension(DocumentFormats.DelimitedText))
        {
            var rows = await ReadDelimitedAsync(request.Input, cancellationToken).ConfigureAwait(false);
            return CsvHtml.Convert(rows, request.Input.BaseName, forPrint);
        }
        return await Task.Run(() => XlsxHtml.Convert(request.Input.Path, request.Input.BaseName, CultureInfo.CurrentCulture, cancellationToken, forPrint), cancellationToken).ConfigureAwait(false);
    }

    static async Task<OperationResult> CsvToXlsxAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new JobProgress(0.1, "Reading CSV…"));
        var rows = await ReadDelimitedAsync(request.Input, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
            throw new OperationException("This file is empty.");
        progress.Report(new JobProgress(0.4, "Writing workbook…"));
        var path = Outputs.File(request, ".xlsx", null, working => XlsxWriter.Write(working, request.Input.BaseName, rows, cancellationToken));
        return OperationResult.FromOutputs(path);
    }

    static async Task<IReadOnlyList<IReadOnlyList<string>>> ReadDelimitedAsync(InputFile input, CancellationToken cancellationToken)
    {
        var text = await TextFiles.ReadAsync(input.Path, cancellationToken).ConfigureAwait(false);
        var delimiter = Csv.DetectDelimiter(text, input.Extension);
        return Csv.Parse(text, delimiter).Select(r => (IReadOnlyList<string>)r).ToList();
    }

    /// <summary>One sheet → "name.csv"; several → folder "name (CSV)" with "Sheet name.csv" files.</summary>
    static OperationResult WriteCsv(OperationRequest request, IReadOnlyList<SheetData> sheets, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var withData = sheets.Where(s => !s.IsEmpty).ToList();
        if (withData.Count == 0)
            throw new OperationException("This workbook is empty.");
        string[] warnings = withData.Any(s => s.IsRagged)
            ? ["Some cells sit far away from the rest of the data, so rows weren't padded out with empty columns (the file would have been huge)."]
            : [];
        if (withData.Count == 1)
            return OperationResult.FromOutputs(Outputs.File(request, ".csv", null, path => WriteSheet(path, withData[0], cancellationToken))) with { Warnings = warnings };

        using var folder = request.Output.BeginDirectory(request.Input, "CSV");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < withData.Count; i++)
        {
            var name = OutputPlanner.SanitizeName(withData[i].Name);
            for (var n = 2; !used.Add(name); n++)
                name = $"{OutputPlanner.SanitizeName(withData[i].Name)} ({n})";
            WriteSheet(Path.Combine(folder.WorkingPath, name + ".csv"), withData[i], cancellationToken);
            progress.Report(new JobProgress((i + 1.0) / withData.Count, $"Sheet {i + 1} of {withData.Count}…"));
        }
        return OperationResult.FromOutputs(folder.Commit()) with { Warnings = warnings };
    }

    static void WriteSheet(string path, SheetData sheet, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(path, false, TextFiles.Utf8Bom);
        var r = 0;
        foreach (var row in sheet.Rows())
        {
            if (r++ % 5000 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            writer.Write(Csv.FormatRow(row));
            writer.Write("\r\n");
        }
    }
}
