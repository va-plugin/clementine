using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Operations;

namespace Clementine.Engines.Images;

/// <summary>
/// Image operations (Magick.NET): conversions between still/animated image formats, PSD/PSB with a verified
/// composite (rebuilt from layers when missing), and image tools. Formats whose encoder isn't present in this
/// ImageMagick build (HEIC today) are probed at runtime and simply not offered.
/// </summary>
public sealed class ImageOperationProvider : IOperationProvider
{
    public string Name => "Images";

    public IEnumerable<IOperation> CreateOperations(EngineContext context)
    {
        MagickRuntime.EnsureInitialized();
        var operations = new List<IOperation>();
        operations.AddRange(Conversions());
        operations.AddRange(ResizeOperation.All());
        operations.AddRange(ExportLayersOperation.All());
        operations.AddRange(CompressOperation.All());
        operations.AddRange(CropOperation.All());
        operations.AddRange(RotateOperation.All());
        operations.AddRange(AdjustOperation.All());
        operations.AddRange(TransparentBackgroundOperation.All());
        operations.AddRange(RemoveMetadataOperation.All());
        operations.Add(new InfoOperation());
        operations.Add(new ReadBarcodeOperation());
        operations.Add(new ImagesToPdfOperation());
        operations.Add(new CollageOperation());
        return operations;
    }

    /// <summary>The Convert wheel: one entry per target (and quality variant) this machine can really encode.</summary>
    static IEnumerable<IOperation> Conversions()
    {
        foreach (var target in ImageTargets.All)
        {
            if (target.RequiresEncodeProbe && !MagickRuntime.CanEncode(target.Format))
                continue;
            foreach (var variant in VariantsFor(target))
                yield return new ConvertOperation(target, variant);
        }
    }

    static IEnumerable<ConvertVariant> VariantsFor(ImageTarget target)
    {
        switch (target.Key)
        {
            case "png":
                // Raster inputs (PSD → PNG above all) get one plain entry; SVGs get a choice of drawing sizes instead.
                yield return new ConvertVariant { Svg = SvgInputs.Excluded, Description = "Lossless, keeps transparency and 16-bit colour." };
                yield return new ConvertVariant
                {
                    IdSuffix = "1x", Label = "1×", TitleDetail = "1× (SVG)", Svg = SvgInputs.Only,
                    Description = "Draws the SVG at its own size — lossless, keeps transparency.",
                };
                yield return new ConvertVariant
                {
                    IdSuffix = "2x", Label = "2×", TitleDetail = "2× (SVG)", SvgScale = 2, Svg = SvgInputs.Only, OutputSuffix = "2x", OrderOffset = 1,
                    Description = "Draws the SVG at twice its size — crisp on high-DPI screens.",
                };
                yield return new ConvertVariant
                {
                    IdSuffix = "4x", Label = "4×", TitleDetail = "4× (SVG)", SvgScale = 4, Svg = SvgInputs.Only, OutputSuffix = "4x", OrderOffset = 2,
                    Description = "Draws the SVG at four times its size — for print or big screens.",
                };
                break;
            case "jpg":
                yield return new ConvertVariant
                {
                    Label = "High 92", TitleDetail = "high quality", Encode = new EncodeOptions { Quality = 92 },
                    Description = "Great quality for photos (quality 92). Transparency becomes white.",
                };
                yield return new ConvertVariant
                {
                    IdSuffix = "medium", Label = "Medium 80", TitleDetail = "medium quality", Encode = new EncodeOptions { Quality = 80 }, OrderOffset = 1,
                    Description = "Smaller file that still looks good for sharing (quality 80).",
                };
                yield return new ConvertVariant
                {
                    IdSuffix = "small", Label = "Small 65", TitleDetail = "small file", Encode = new EncodeOptions { Quality = 65 }, OrderOffset = 2,
                    Description = "Smallest file, fine for previews and email (quality 65).",
                };
                break;
            case "webp":
                yield return new ConvertVariant
                {
                    Label = "Lossy", TitleDetail = "lossy", Encode = new EncodeOptions { Quality = ImageEncoder.DefaultWebPQuality },
                    Description = "Small files for the web; keeps transparency and animation.",
                };
                yield return new ConvertVariant
                {
                    IdSuffix = "lossless", Label = "Lossless", TitleDetail = "lossless", Encode = new EncodeOptions { Lossless = true }, OrderOffset = 1,
                    Description = "Pixel-perfect WebP, usually smaller than PNG; keeps animation.",
                };
                break;
            case "avif":
                yield return new ConvertVariant { Description = "Very small modern format with transparency (slower to save)." };
                break;
            case "heic":
                yield return new ConvertVariant { Description = "Apple's photo format — small files with good quality." };
                break;
            case "pdf":
                yield return new ConvertVariant { Description = "A one-page PDF exactly the size of the image." };
                break;
            case "tiff":
                yield return new ConvertVariant { Description = "Lossless LZW TIFF; keeps transparency and 16-bit colour." };
                break;
            case "gif":
                yield return new ConvertVariant { Description = "256 colours; animations stay animated." };
                break;
            case "ico":
                yield return new ConvertVariant { Description = "Windows icon with every size from 16 to 256 px." };
                break;
            case "bmp":
                yield return new ConvertVariant { Description = "Uncompressed Windows bitmap. Transparency becomes white." };
                break;
            case "jxl":
                yield return new ConvertVariant { Description = "Next-generation JPEG: smaller files at high quality, keeps transparency." };
                break;
        }
    }
}
