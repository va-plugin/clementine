using System.Globalization;
using PdfSharp.Pdf;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>Merge, split, extract, delete, reorder and rotate pages with PDFsharp.</summary>
internal static class PdfPageTools
{
    public static IEnumerable<IOperation> Create()
    {
        var pdf = DocumentFormats.Pdf;

        yield return new DocumentOperation(
            Info.Tool("pdf.merge", "Merge", null, "Merge PDFs",
                "Joins the PDFs into one, in name order, with a bookmark for each file.", "merge", ".pdf", 10),
            pdf, MergeAsync)
        { Mode = InputMode.Combined, Minimum = 2 };

        yield return new DocumentOperation(
            Info.Tool("pdf.split.pages", "Split", "Every page", "Split into single pages",
                "Saves every page as its own PDF in a folder.", "split", null, 20),
            pdf, SplitEveryPageAsync);

        yield return new DocumentOperation(
            Info.Tool("pdf.split.ranges", "Split", "Ranges…", "Split by page ranges",
                "Saves each range (e.g. \"1-3, 4-9, 10-\") as its own PDF.", "split", null, 21),
            pdf, SplitRangesAsync)
        {
            Schema = new OptionSchema("Split PDF", [
                new TextField("ranges", "Page ranges", "1-") { Placeholder = "1-3, 4-9, 10-", Help = "Each comma-separated range becomes one PDF. \"8-\" means page 8 to the end." },
            ]) { ConfirmLabel = "Split" },
            DynamicDefaults = SplitDefaultsAsync,
        };

        yield return new DocumentOperation(
            Info.Tool("pdf.pages.extract", "Extract pages", null, "Extract pages",
                "Copies the pages you choose into a new PDF.", "pages", ".pdf", 30),
            pdf, ExtractAsync)
        {
            Schema = new OptionSchema("Extract pages", [
                new TextField("pages", "Pages to keep", "1") { Placeholder = "1-3, 5, 8-", Help = "Pages are copied in the order you list them." },
            ]) { ConfirmLabel = "Extract" },
        };

        yield return new DocumentOperation(
            Info.Tool("pdf.pages.delete", "Delete pages", null, "Delete pages",
                "Saves a copy without the pages you choose.", "erase", ".pdf", 31),
            pdf, DeleteAsync)
        {
            Schema = new OptionSchema("Delete pages", [
                new TextField("pages", "Pages to delete", "1") { Placeholder = "2, 5-7" },
            ]) { ConfirmLabel = "Delete pages" },
        };

        yield return new DocumentOperation(
            Info.Tool("pdf.pages.reorder", "Reorder", "Custom order…", "Reorder pages",
                "Puts the pages in the order you type, e.g. \"3, 1-2, 4-\".", "pages", ".pdf", 32),
            pdf, ReorderAsync)
        {
            Schema = new OptionSchema("Reorder pages", [
                new TextField("order", "New page order", "1-") { Placeholder = "3, 1-2, 4-", Help = "Pages you leave out are kept at the end in their original order." },
            ]) { ConfirmLabel = "Reorder" },
            DynamicDefaults = ReorderDefaultsAsync,
        };

        yield return new DocumentOperation(
            Info.Tool("pdf.pages.reverse", "Reorder", "Reverse", "Reverse page order",
                "Last page first — handy for scans fed the wrong way round.", "reverse", ".pdf", 33),
            pdf, ReverseAsync);

        yield return new DocumentOperation(
            Info.Tool("pdf.rotate.right", "Rotate", "90° right", "Rotate pages 90° right", "Turns every page clockwise.", "rotate", ".pdf", 40),
            pdf, RotateHandler(90));
        yield return new DocumentOperation(
            Info.Tool("pdf.rotate.left", "Rotate", "90° left", "Rotate pages 90° left", "Turns every page anticlockwise.", "rotate", ".pdf", 41),
            pdf, RotateHandler(270));
        yield return new DocumentOperation(
            Info.Tool("pdf.rotate.180", "Rotate", "180°", "Rotate pages 180°", "Turns every page upside down.", "rotate", ".pdf", 42),
            pdf, RotateHandler(180));
    }

    static Task<OperationResult> MergeAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var inputs = NaturalComparer.Sort(request.Inputs);
        using var merged = new PdfDocument();
        for (var i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobProgress((double)i / inputs.Count * 0.9, $"Adding {inputs[i].Name}…"));
            using var source = PdfFiles.OpenForImport(inputs[i].Path);
            if (i == 0)
                PdfFiles.CopyInfo(source, merged);
            PdfPage? first = null;
            foreach (var page in source.Pages)
            {
                var added = merged.AddPage(page);
                first ??= added;
            }
            if (first is not null)
                merged.Outlines.Add(inputs[i].BaseName, first, true);
        }
        progress.Report(new JobProgress(0.95, "Saving…"));
        merged.Info.Title = string.IsNullOrWhiteSpace(merged.Info.Title) ? inputs[0].BaseName : merged.Info.Title;
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "merged", path => PdfFiles.Save(merged, path), inputs[0]));
    }, cancellationToken);

    static Task<OperationResult> SplitEveryPageAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        var count = source.PageCount;
        if (count < 2)
            throw new OperationException("This PDF has only one page, so there's nothing to split.");
        using var folder = request.Output.BeginDirectory(request.Input, "pages");
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var part = new PdfDocument();
            PdfFiles.CopyInfo(source, part);
            part.AddPage(source.Pages[i]);
            PdfFiles.Save(part, Path.Combine(folder.WorkingPath, Outputs.NumberedName(request.Input.BaseName, i + 1, count, ".pdf")));
            progress.Report(new JobProgress((i + 1.0) / count, $"Page {i + 1} of {count}…"));
        }
        return OperationResult.FromOutputs(folder.Commit());
    }, cancellationToken);

    static Task<OperationResult> SplitRangesAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        var ranges = PageRanges.Parse(request.Options.GetString("ranges", ""), source.PageCount);
        if (ranges.Count == 1)
        {
            var only = ranges[0];
            var path = Outputs.File(request, ".pdf", PagesLabel(only), working => SaveSelection(source, only.Pages(), working));
            return OperationResult.FromOutputs(path);
        }

        using var folder = request.Output.BeginDirectory(request.Input, "split");
        for (var i = 0; i < ranges.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = OutputPlanner.SanitizeName($"{request.Input.BaseName} ({PagesLabel(ranges[i])})") + ".pdf";
            var target = Path.Combine(folder.WorkingPath, name);
            if (File.Exists(target))
                target = Path.Combine(folder.WorkingPath, OutputPlanner.SanitizeName($"{request.Input.BaseName} ({PagesLabel(ranges[i])}) {i + 1}") + ".pdf");
            SaveSelection(source, ranges[i].Pages(), target);
            progress.Report(new JobProgress((i + 1.0) / ranges.Count, $"Part {i + 1} of {ranges.Count}…"));
        }
        return OperationResult.FromOutputs(folder.Commit());
    }, cancellationToken);

    static Task<OperationResult> ExtractAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        var ranges = PageRanges.Parse(request.Options.GetString("pages", ""), source.PageCount);
        var pages = PageRanges.Expand(ranges);
        var described = PageRanges.Describe(ranges);
        // "report (pages 1-3, 5).pdf", but a long scattered selection would make an unusable (or too long) file name.
        var label = ranges.Count == 1 ? PagesLabel(ranges[0])
            : described.Length <= 32 ? $"pages {described}"
            : $"{pages.Count.ToString(CultureInfo.InvariantCulture)} pages";
        cancellationToken.ThrowIfCancellationRequested();
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", label, path => SaveSelection(source, pages, path)));
    }, cancellationToken);

    static Task<OperationResult> DeleteAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        var remove = PageRanges.Expand(PageRanges.Parse(request.Options.GetString("pages", ""), source.PageCount)).ToHashSet();
        var keep = Enumerable.Range(1, source.PageCount).Where(p => !remove.Contains(p)).ToList();
        if (keep.Count == 0)
            throw new OperationException("That would delete every page. Choose fewer pages to delete.");
        cancellationToken.ThrowIfCancellationRequested();
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "pages removed", path => SaveSelection(source, keep, path)));
    }, cancellationToken);

    static Task<OperationResult> ReorderAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        var order = PageRanges.Expand(PageRanges.Parse(request.Options.GetString("order", ""), source.PageCount)).ToList();
        var mentioned = order.ToHashSet();
        order.AddRange(Enumerable.Range(1, source.PageCount).Where(p => !mentioned.Contains(p)));
        if (order.SequenceEqual(Enumerable.Range(1, source.PageCount)))
            throw new OperationException("That's the order the pages are already in.");
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "reordered", path => SaveSelection(source, order, path)));
    }, cancellationToken);

    static Task<OperationResult> ReverseAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(request.Input.Path);
        if (source.PageCount < 2)
            throw new OperationException("This PDF has only one page.");
        var order = Enumerable.Range(1, source.PageCount).Reverse().ToList();
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "reversed", path => SaveSelection(source, order, path)));
    }, cancellationToken);

    static OperationHandler RotateHandler(int degrees) => (request, progress, cancellationToken) => Task.Run(() =>
    {
        using var document = PdfFiles.OpenForEdit(request.Input.Path);
        foreach (var page in document.Pages)
            page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        cancellationToken.ThrowIfCancellationRequested();
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "rotated", path => PdfFiles.Save(document, path)));
    }, cancellationToken);

    /// <summary>Saves the given 1-based pages of <paramref name="source"/> (in order, repeats allowed) as a new PDF.</summary>
    static void SaveSelection(PdfDocument source, IEnumerable<int> pages, string path)
    {
        using var output = new PdfDocument();
        PdfFiles.CopyInfo(source, output);
        foreach (var page in pages)
            output.AddPage(source.Pages[page - 1]);
        PdfFiles.Save(output, path);
    }

    static string PagesLabel(PageRange range) => range.First == range.Last
        ? $"page {range.First.ToString(CultureInfo.InvariantCulture)}"
        : $"pages {range}";

    static Task<OptionValues> SplitDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var count = PageCount(inputs[0]);
        var half = Math.Max(1, count / 2);
        return new OptionValues { ["ranges"] = count < 2 ? "1" : $"1-{half}, {half + 1}-" };
    }, cancellationToken);

    static Task<OptionValues> ReorderDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var count = PageCount(inputs[0]);
        return new OptionValues { ["order"] = count <= 1 ? "1" : $"1-{count}" };
    }, cancellationToken);

    static int PageCount(InputFile input)
    {
        using var document = PdfFiles.OpenForImport(input.Path);
        return document.PageCount;
    }
}
