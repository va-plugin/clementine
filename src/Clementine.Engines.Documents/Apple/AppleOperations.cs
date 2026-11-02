using Clementine.Engines.Documents.Pdf;
using PdfSharp.Drawing;
using SkiaSharp;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace Clementine.Engines.Documents.Apple;

/// <summary>Pages / Numbers / Keynote → PDF or JPEG from the preview stored inside the file.</summary>
internal static class AppleOperations
{
    const string PreviewWarning = "This is the preview Pages, Numbers or Keynote saved inside the file — it usually shows only the first page.";

    public static IEnumerable<IOperation> Create()
    {
        var apple = DocumentFormats.Apple;
        yield return new DocumentOperation(
            Info.Convert("apple.pdf", "PDF", "Preview only", "Apple iWork → PDF (preview only)",
                "Saves the preview stored inside the file as a PDF — preview only, often just the first page.", "pdf", ".pdf", 10),
            apple, ToPdfAsync)
        { AcceptsPackages = true };
        yield return new DocumentOperation(
            Info.Convert("apple.jpeg", "JPEG", "Preview image", "Apple iWork → JPEG (preview only)",
                "Saves the preview image stored inside the file — preview only, first page.", "image", ".jpg", 45),
            apple, ToJpegAsync)
        { AcceptsPackages = true };
    }

    static Task<OperationResult> ToPdfAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var preview = ApplePreview.Find(request.Input);
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        if (preview.IsPdf)
        {
            path = Save(request, ".pdf", working => File.WriteAllBytes(working, preview.Data));
            return new OperationResult { Outputs = [path], Warnings = ["This is the preview saved inside the file; it may differ slightly from the original."] };
        }

        path = Save(request, ".pdf", working =>
        {
            using var document = new PdfDocument();
            document.Info.Title = BaseName(request.Input);
            using var stream = new MemoryStream(preview.Data);
            using var image = XImage.FromStream(stream);
            var page = document.AddPage();
            // Fit the preview to an A4-width page, keeping its aspect ratio.
            var width = 595.28;
            var height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(image, 0, 0, width, height);
            PdfFiles.Save(document, working);
        });
        return new OperationResult { Outputs = [path], Warnings = [PreviewWarning] };
    }, cancellationToken);

    static async Task<OperationResult> ToJpegAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var preview = ApplePreview.Find(request.Input);
        byte[] jpeg;
        if (preview.IsPdf)
        {
            jpeg = [];
            await PdfRenderer.RenderAsync(preview.Data, 150, (page, _) =>
            {
                jpeg = PdfRenderer.Encode(page.Bitmap, SKEncodedImageFormat.Jpeg, 90);
                return Task.CompletedTask;
            }, cancellationToken, pages: [0]).ConfigureAwait(false);
        }
        else if (preview.EntryName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            jpeg = preview.Data;
        }
        else
        {
            using var bitmap = SKBitmap.Decode(preview.Data) ?? throw new OperationException("The preview image inside this file is damaged.");
            jpeg = PdfRenderer.Encode(bitmap, SKEncodedImageFormat.Jpeg, 90);
        }
        var path = Save(request, ".jpg", working => File.WriteAllBytes(working, jpeg));
        return new OperationResult { Outputs = [path], Warnings = [PreviewWarning] };
    }

    /// <summary>Saves "Report.pdf" next to "Report.pages" — for package folders too, whose name includes the extension.</summary>
    static string Save(OperationRequest request, string extension, Action<string> write)
    {
        using var output = request.Output.BeginFile(request.Output.ResolveDirectory(request.Input), BaseName(request.Input), extension);
        write(output.WorkingPath);
        return output.Commit();
    }

    static string BaseName(InputFile input) =>
        input.IsDirectory ? Path.GetFileNameWithoutExtension(input.Name) : input.BaseName;
}
