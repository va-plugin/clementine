using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Pdf;

/// <summary>Paper sizes for Images → PDF.</summary>
public enum PdfPaper
{
    /// <summary>Each page is exactly the size of its image.</summary>
    Fit,
    A4,
    Letter,
}

public enum PdfOrientation
{
    /// <summary>Landscape pages for landscape images, portrait otherwise.</summary>
    Auto,
    Portrait,
    Landscape,
}

/// <summary>Builds <see cref="PdfImagePage"/>s from loaded images: page geometry plus the best embedding.</summary>
public static class PdfPages
{
    const double PointsPerMillimetre = 72 / 25.4;
    const long PassThroughLimit = 256L * 1024 * 1024;

    /// <summary>A page the size of the image at its stored resolution (72 DPI when none is recorded).</summary>
    public static PdfImagePage FitToImage(LoadedImage image) =>
        Embed(image, PdfImagePage.FitToImage(Flattenable(image), ResolutionOf(image.First)));

    /// <summary>A page of <paramref name="paper"/> with the image fitted inside the margin.</summary>
    public static PdfImagePage OnPaper(LoadedImage image, PdfPaper paper, PdfOrientation orientation, double marginMillimetres)
    {
        if (paper == PdfPaper.Fit)
            return FitToImage(image);
        var (shortSide, longSide) = paper == PdfPaper.A4 ? (595.276, 841.89) : (612.0, 792.0);
        var landscape = orientation switch
        {
            PdfOrientation.Landscape => true,
            PdfOrientation.Portrait => false,
            _ => image.First.Width > image.First.Height,
        };
        var (w, h) = landscape ? (longSide, shortSide) : (shortSide, longSide);
        var margin = Math.Clamp(marginMillimetres, 0, 50) * PointsPerMillimetre;
        return Embed(image, PdfImagePage.OnPage(Flattenable(image), w, h, margin));
    }

    /// <summary>Pixels per inch recorded in the image, or 72 when missing or implausible.</summary>
    public static double ResolutionOf(IMagickImage<QuantumType> image)
    {
        var density = image.Density;
        var dpi = density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
        return dpi is >= 50 and <= 2400 ? dpi : 72;
    }

    static IMagickImage<QuantumType> Flattenable(LoadedImage image)
    {
        ImageEncoder.Prepare(image, MagickFormat.Pdf);
        return image.First;
    }

    static PdfImagePage Embed(LoadedImage image, PdfImagePage page)
    {
        if (CanPassThrough(image))
            return page with { PassThroughJpeg = File.ReadAllBytes(image.Source.Path) };
        var photo = ImageFormats.Jpeg.Contains(image.Source.Extension) || ImageFormats.PhotoLike.Contains(image.Source.Extension)
            || image.Source.HasExtension(".webp");
        return page with { Encoding = photo && !image.HasTransparency ? PdfImageEncoding.Jpeg : PdfImageEncoding.Lossless };
    }

    /// <summary>Upright, 8-bit, RGB/grey JPEGs can be embedded byte for byte (no generation loss).</summary>
    static bool CanPassThrough(LoadedImage image)
    {
        var source = image.Source;
        if (!source.HasExtension(ImageFormats.Jpeg) || image.SourceFormat != MagickFormat.Jpeg || source.Length > PassThroughLimit)
            return false;
        if (image.OriginalOrientation is not (OrientationType.Undefined or OrientationType.TopLeft))
            return false;
        if (image.OriginalColorSpace is not (ColorSpace.sRGB or ColorSpace.Gray) || image.SourceDepth != 8)
            return false;
        var profile = image.First.GetColorProfile();
        return profile is null || profile.ColorSpace is ColorSpace.sRGB or ColorSpace.Gray;
    }
}
