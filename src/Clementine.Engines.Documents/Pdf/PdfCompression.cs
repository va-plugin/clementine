using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>"Optimize" (lossless stream compression + recompressing oversized photos) and "Rasterize 150 DPI".</summary>
internal static class PdfCompression
{
    /// <summary>Photos larger than this (longest side, pixels) are scaled down when optimising.</summary>
    const int MaxImageSide = 2000;
    const int JpegQuality = 72;

    public static IEnumerable<IOperation> Create()
    {
        var pdf = DocumentFormats.Pdf;
        yield return new DocumentOperation(
            Info.Tool("pdf.compress.optimize", "Compress", "Optimize", "Compress PDF (optimize)",
                "Recompresses photos and internal data. Text stays sharp and selectable.", "compress", ".pdf", 50, heavy: true),
            pdf, OptimizeAsync);
        yield return new DocumentOperation(
            Info.Tool("pdf.compress.rasterize", "Compress", "Rasterize 150 DPI", "Compress PDF (rasterize at 150 DPI)",
                "Turns every page into a 150 DPI image — smallest for scans, but text is no longer selectable.", "compress", ".pdf", 51, heavy: true),
            pdf, RasterizeAsync);
    }

    static Task<OperationResult> OptimizeAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var originalSize = request.Input.Length;
        using var document = PdfFiles.OpenForEdit(request.Input.Path);
        progress.Report(new JobProgress(0.1, "Recompressing images…"));
        var images = RecompressImages(document, cancellationToken);
        progress.Report(new JobProgress(0.7, images > 0 ? $"Recompressed {images} images. Compressing streams…" : "Compressing streams…"));
        ZipUncompressedStreams(document);
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

        using var output = request.Output.BeginFile(request.Input, ".pdf", "compressed");
        PdfFiles.Save(document, output.WorkingPath);
        if (new FileInfo(output.WorkingPath).Length >= originalSize * 0.98)
            throw new OperationException("This PDF is already well optimised — Optimize couldn't make it smaller. Rasterize 150 DPI can shrink scans further.");
        return OperationResult.FromOutputs(output.Commit());
    }, cancellationToken);

    static async Task<OperationResult> RasterizeAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        const int dpi = 150;
        var bytes = PdfFiles.ReadBytes(request.Input.Path);
        using var output = new PdfDocument();
        using (var source = PdfFiles.OpenForImport(request.Input.Path))
            PdfFiles.CopyInfo(source, output);

        var warnings = await PdfRenderer.RenderAsync(bytes, dpi, (page, total) =>
        {
            var jpeg = PdfRenderer.Encode(page.Bitmap, SKEncodedImageFormat.Jpeg, 75);
            var pdfPage = output.AddPage();
            var width = page.Bitmap.Width * 72.0 / page.Dpi;
            var height = page.Bitmap.Height * 72.0 / page.Dpi;
            pdfPage.Width = XUnit.FromPoint(width);
            pdfPage.Height = XUnit.FromPoint(height);
            using var stream = new MemoryStream(jpeg);
            using var image = XImage.FromStream(stream);
            using var gfx = XGraphics.FromPdfPage(pdfPage);
            gfx.DrawImage(image, 0, 0, width, height);
            progress.Report(new JobProgress(0.9 * page.Number / total, $"Page {page.Number} of {total}…"));
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        using var pending = request.Output.BeginFile(request.Input, ".pdf", "compressed");
        PdfFiles.Save(output, pending.WorkingPath);
        if (new FileInfo(pending.WorkingPath).Length >= request.Input.Length)
            throw new OperationException("Rasterizing would make this PDF bigger (it's mostly text). Try Optimize instead.");
        return new OperationResult { Outputs = [pending.Commit()], Warnings = warnings };
    }

    /// <summary>Re-encodes large or high-quality JPEG photos at a sensible size and quality. Returns how many changed.</summary>
    static int RecompressImages(PdfDocument document, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var item in document.Internals.GetAllObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not PdfDictionary image || image.Stream is null || image.Elements.GetName("/Subtype") != "/Image")
                continue;
            if (!IsPlainJpeg(image, document, out var gray))
                continue;
            try
            {
                var original = image.Stream.Value;
                using var decoded = SKBitmap.Decode(original);
                if (decoded is null)
                    continue;
                var scale = Math.Min(1.0, MaxImageSide / (double)Math.Max(decoded.Width, decoded.Height));
                var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
                var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
                var info = new SKImageInfo(width, height, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
                using var resized = new SKBitmap(info);
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.Clear(SKColors.White);
                    using var source = SKImage.FromBitmap(decoded);
                    canvas.DrawImage(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                }
                var recompressed = PdfRenderer.Encode(resized, SKEncodedImageFormat.Jpeg, JpegQuality);
                if (recompressed.Length >= original.Length * 0.9)
                    continue;
                image.Stream.Value = recompressed;
                image.Elements.SetInteger("/Width", width);
                image.Elements.SetInteger("/Height", height);
                image.Elements.Remove("/DecodeParms");
                changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An image we can't decode is simply left as it was.
            }
        }
        return changed;
    }

    /// <summary>8-bit DCT (JPEG) images in RGB or grey without masks or decode arrays — safe to re-encode.</summary>
    static bool IsPlainJpeg(PdfDictionary image, PdfDocument document, out bool gray)
    {
        gray = false;
        var filter = image.Elements["/Filter"];
        var isDct = filter switch
        {
            PdfName name => name.Value == "/DCTDecode",
            PdfArray { Elements.Count: 1 } array => array.Elements[0] is PdfName { Value: "/DCTDecode" },
            _ => false,
        };
        if (!isDct || image.Elements.GetInteger("/BitsPerComponent") != 8)
            return false;
        if (image.Elements.ContainsKey("/Decode") || image.Elements.ContainsKey("/Mask") || image.Elements.GetBoolean("/ImageMask"))
            return false;

        var colorSpace = image.Elements["/ColorSpace"];
        if (colorSpace is PdfReference reference)
            colorSpace = reference.Value;
        switch (colorSpace)
        {
            case PdfName { Value: "/DeviceRGB" }:
                return true;
            case PdfName { Value: "/DeviceGray" }:
                gray = true;
                return true;
            case PdfArray { Elements.Count: 2 } icc when icc.Elements[0] is PdfName { Value: "/ICCBased" }:
                var profile = icc.Elements[1] is PdfReference r ? r.Value as PdfDictionary : icc.Elements[1] as PdfDictionary;
                var components = profile?.Elements.GetInteger("/N") ?? 0;
                gray = components == 1;
                return components is 1 or 3;
            default:
                return false;
        }
    }

    /// <summary>Flate-compresses streams the source stored without any filter (fonts, content, metadata…).</summary>
    static void ZipUncompressedStreams(PdfDocument document)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is PdfDictionary { Stream: not null } dictionary && !dictionary.Elements.ContainsKey("/Filter")
                && dictionary.Elements.GetName("/Type") != "/Metadata" && dictionary.Stream.Length > 64)
            {
                try
                {
                    dictionary.Stream.Zip();
                }
                catch (Exception)
                {
                    // Leave the stream as it is.
                }
            }
        }
    }
}
