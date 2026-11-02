using Clementine.Core;
using PDFtoImage;
using SkiaSharp;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>A rendered page; dispose the bitmap when done.</summary>
internal sealed record RenderedPage(int Number, SKBitmap Bitmap, int Dpi) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Renders PDF pages with PDFium (through PDFtoImage). PDFium is not thread-safe, so every call is serialised
/// process-wide; pages are produced one at a time so memory stays flat for long documents.
/// </summary>
internal static class PdfRenderer
{
    /// <summary>Above this many pixels per page we lower the DPI (a 300 DPI A0 poster would need ~600 MB).</summary>
    public const long MaxPixelsPerPage = 80_000_000;

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<int> GetPageCountAsync(byte[] pdf, string? password, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Translate(() => Conversion.GetPageCount(pdf, password));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Renders every page (or <paramref name="pages"/>, 0-based) at <paramref name="dpi"/> on white and hands each to
    /// <paramref name="consume"/> before rendering the next. Returns warnings (e.g. reduced DPI).
    /// </summary>
    public static async Task<IReadOnlyList<string>> RenderAsync(
        byte[] pdf, int dpi, Func<RenderedPage, int, Task> consume, CancellationToken cancellationToken,
        string? password = null, IReadOnlyList<int>? pages = null)
    {
        var warnings = new List<string>();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = Translate(() => Conversion.GetPageCount(pdf, password));
            if (count == 0)
                throw new OperationException("This PDF has no pages.");
            var sizes = Translate(() => Conversion.GetPageSizes(pdf, password));
            var selection = pages ?? Enumerable.Range(0, count).ToList();
            var reduced = false;
            foreach (var index in selection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = sizes[index];
                var pageDpi = SafeDpi(size.Width, size.Height, dpi);
                reduced |= pageDpi < dpi;
                var options = new RenderOptions(Dpi: pageDpi, WithAnnotations: true, WithFormFill: true,
                    AntiAliasing: PdfAntiAliasing.All, BackgroundColor: SKColors.White, UseTiling: pageDpi >= 400);
                var bitmap = Translate(() => Conversion.ToImage(pdf, index, password, options));
                using var page = new RenderedPage(index + 1, bitmap, pageDpi);
                await consume(page, selection.Count).ConfigureAwait(false);
            }
            if (reduced)
                warnings.Add("Some pages are very large, so they were rendered at a lower resolution to fit in memory.");
        }
        finally
        {
            Gate.Release();
        }
        return warnings;
    }

    /// <summary>Encodes a bitmap as PNG or JPEG and writes it.</summary>
    public static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality = 92)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new OperationException($"Couldn't encode the page as {format}.");
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new OperationException($"Couldn't encode the page as {format}.");
        return data.ToArray();
    }

    static int SafeDpi(float widthPoints, float heightPoints, int dpi)
    {
        double PixelsAt(int d) => widthPoints / 72.0 * d * (heightPoints / 72.0 * d);
        var safe = dpi;
        while (safe > 36 && PixelsAt(safe) > MaxPixelsPerPage)
            safe = (int)(safe * 0.8);
        return safe;
    }

    static T Translate<T>(Func<T> render)
    {
        try
        {
            return render();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationException)
        {
            if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new OperationException("This PDF is password-protected. Use Unlock to remove the password first.", ex.Message, ex);
            throw new OperationException("Couldn't render this PDF — it may be damaged.", ex.ToString(), ex);
        }
    }
}
