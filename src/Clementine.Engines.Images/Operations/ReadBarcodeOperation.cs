using Clementine.Engines.Images.Imaging;
using ZXing;
using ZXing.Common;

namespace Clementine.Engines.Images.Operations;

/// <summary>Read QR/barcode (ZXing.Net): every code in the image as text.</summary>
public sealed class ReadBarcodeOperation : ImageOperation
{
    public ReadBarcodeOperation()
        : base(new OperationInfo
        {
            Id = "image.barcode.read",
            Category = OperationCategory.Tool,
            Group = "Read QR",
            Title = "Read QR code / barcode",
            Description = "Finds QR codes and barcodes (EAN, UPC, Code 128, Data Matrix, PDF417 …) and shows their contents.",
            IconKey = "qr",
            Order = 95,
        })
    {
    }

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        Report(progress, null, "Looking for codes…");
        using var image = ImageLoader.Load(request.Input, LoadOptions.Still, cancellationToken);
        var results = Decode(image.First, cancellationToken);
        if (results.Count == 0)
            throw new OperationException("No QR code or barcode was found in this image.");

        if (results.Count == 1)
            return OperationResult.FromText($"{Name(results[0].BarcodeFormat)} contents", results[0].Text);
        var text = string.Join(Environment.NewLine + Environment.NewLine, results.Select(r => $"{Name(r.BarcodeFormat)}:{Environment.NewLine}{r.Text}"));
        return OperationResult.FromText($"{results.Count} codes found", text);
    }

    /// <summary>All distinct codes, trying the image as is, then scaled for very large or very small images.</summary>
    public static IReadOnlyList<Result> Decode(IMagickImage<QuantumType> source, CancellationToken cancellationToken = default)
    {
        using var flat = source.Clone();
        if (flat.HasAlpha)
        {
            flat.BackgroundColor = MagickColors.White;
            flat.Alpha(AlphaOption.Remove);
        }
        flat.ColorSpace = ColorSpace.sRGB;

        var longest = Math.Max(flat.Width, flat.Height);
        var scales = new List<double> { 1 };
        if (longest > 2400)
            scales.Add(1600.0 / longest);
        if (longest < 500)
            scales.Add(2);
        foreach (var scale in scales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var candidate = flat.Clone();
            if (scale != 1)
                candidate.Resize(new Percentage(scale * 100));
            var found = DecodeOnce(candidate);
            if (found.Count > 0)
                return found;
        }
        return [];
    }

    static List<Result> DecodeOnce(IMagickImage<QuantumType> image)
    {
        using var pixels = image.GetPixelsUnsafe();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? [];
        var luminance = new RGBLuminanceSource(rgb, (int)image.Width, (int)image.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var results = reader.DecodeMultiple(luminance)?.ToList() ?? [];
        if (results.Count == 0 && reader.Decode(luminance) is { } single)
            results.Add(single);
        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .GroupBy(r => (r.BarcodeFormat, r.Text))
            .Select(g => g.First())
            .ToList();
    }

    static string Name(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR code",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.PDF_417 => "PDF417",
        BarcodeFormat.AZTEC => "Aztec code",
        BarcodeFormat.EAN_13 => "EAN-13",
        BarcodeFormat.EAN_8 => "EAN-8",
        BarcodeFormat.UPC_A => "UPC-A",
        BarcodeFormat.UPC_E => "UPC-E",
        BarcodeFormat.CODE_128 => "Code 128",
        BarcodeFormat.CODE_39 => "Code 39",
        BarcodeFormat.CODE_93 => "Code 93",
        BarcodeFormat.ITF => "ITF",
        BarcodeFormat.CODABAR => "Codabar",
        _ => format.ToString().Replace('_', ' '),
    };
}
