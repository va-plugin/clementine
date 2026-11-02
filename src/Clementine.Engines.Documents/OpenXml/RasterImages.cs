using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>
/// Turns pictures browsers can't show (EMF/WMF metafiles, TIFF) into PNG for embedding in an HTML page: Windows GDI+
/// for metafiles and TIFF, SkiaSharp for anything else it decodes.
/// </summary>
internal static class RasterImages
{
    const int MaxSide = 4000;

    /// <summary>PNG bytes at about 2.5× the display size (sharp when printed), or null when the picture can't be read.</summary>
    public static byte[]? ToPng(byte[] data, (double Width, double Height) displaySize)
    {
        return WithGdi(data, displaySize) ?? WithSkia(data);
    }

    static byte[]? WithGdi(byte[] data, (double Width, double Height) displaySize)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
            var (width, height) = TargetSize(image.Width, image.Height, displaySize, image is Metafile);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, new Rectangle(0, 0, width, height));
            }
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException or PlatformNotSupportedException or TypeInitializationException)
        {
            return null;
        }
    }

    static byte[]? WithSkia(byte[] data)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(data);
            if (bitmap is null)
                return null;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static (int Width, int Height) TargetSize(int sourceWidth, int sourceHeight, (double Width, double Height) displaySize, bool vector)
    {
        double width = sourceWidth;
        double height = sourceHeight;
        if (displaySize.Width > 0 && displaySize.Height > 0 && (vector || width * height > 0))
        {
            // 2.5 px per point ≈ 180 DPI: sharp in print without bloating the page.
            var targetWidth = displaySize.Width * 2.5;
            var targetHeight = displaySize.Height * 2.5;
            if (vector || targetWidth < width)
            {
                width = targetWidth;
                height = targetHeight;
            }
        }
        var scale = Math.Min(1, MaxSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
