using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Metadata;

namespace Clementine.Engines.Images.Operations;

public enum CompressMode
{
    Smaller,
    TargetSize,
}

/// <summary>
/// Compress: Smaller · Target size…  JPEG/WebP/AVIF/JPEG XL by quality (binary search for a target size, then
/// downscaling if even the lowest quality is too big); PNG by quantising to ≤ 256 colours with dithering.
/// </summary>
public sealed class CompressOperation : ImageOperation
{
    const int MinQuality = 5, MaxQuality = 95;

    static readonly IReadOnlySet<string> Compressible = new HashSet<string>(
        [.. ImageFormats.Jpeg, ".png", ".webp", ".avif", ".jxl", ".heic", ".heif", ".tif", ".tiff", ".bmp"], StringComparer.OrdinalIgnoreCase);

    static readonly OptionSchema TargetSchema = new("Compress to a size",
    [
        new NumberField("size", "Target size", 500, 5, 1_000_000) { Unit = "KB", Help = "The result will be at most this size (within about 10 %)." },
    ])
    {
        ConfirmLabel = "Compress",
    };

    readonly CompressMode _mode;

    public CompressOperation(CompressMode mode)
        : base(new OperationInfo
        {
            Id = mode == CompressMode.Smaller ? "image.compress.smaller" : "image.compress.target",
            Category = OperationCategory.Tool,
            Group = "Compress",
            Variant = mode == CompressMode.Smaller ? "Smaller" : "Target size…",
            Title = mode == CompressMode.Smaller ? "Compress · smaller" : "Compress · target size",
            Description = mode == CompressMode.Smaller
                ? "A noticeably smaller file that still looks good. PNGs are reduced to 256 colours with dithering."
                : "Shrinks the file to the size you choose, lowering quality (and, if needed, dimensions).",
            IconKey = "compress",
            Order = 20 + (int)mode,
            Weight = mode == CompressMode.TargetSize ? OperationWeight.Heavy : OperationWeight.Light,
        }) => _mode = mode;

    public static IEnumerable<IOperation> All() => [new CompressOperation(CompressMode.Smaller), new CompressOperation(CompressMode.TargetSize)];

    protected override IReadOnlySet<string> InputExtensions => Compressible;

    public override OptionSchema? Options => _mode == CompressMode.TargetSize ? TargetSchema : null;

    public override Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (_mode == CompressMode.TargetSize && inputs.Count > 0)
            values["size"] = Math.Max(5, Math.Round(inputs.Max(i => i.Length) / 1024.0 / 2));
        return Task.FromResult(values);
    }

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var input = request.Input;
        long? target = null;
        if (_mode == CompressMode.TargetSize)
        {
            target = (long)(Math.Max(1, request.Options.GetDouble("size", 500)) * 1024);
            if (input.Length <= target)
                throw new OperationException($"“{input.Name}” is already smaller than {FormatSize(target.Value)}.");
        }

        Report(progress, 0.05, "Reading…");
        using var image = ImageLoader.Load(input, LoadOptions.Animated, cancellationToken);
        var output = OutputFor(input, image);
        if (output.IsFallback)
            image.AddWarning($"Saved as {output.Name}, which compresses far better than {input.Extension}.");
        ImageEncoder.Prepare(image, output.Format);

        Report(progress, 0.2, "Compressing…");
        var bytes = target is { } limit
            ? ToTarget(image, output.Format, limit, progress, cancellationToken)
            : Smaller(image, output.Format, input, cancellationToken);

        if (bytes.Length >= input.Length)
            throw new OperationException($"“{input.Name}” is already well compressed — Clementine couldn't make it smaller.");
        if (target is { } t && bytes.Length > t)
            throw new OperationException($"Couldn't get “{input.Name}” down to {FormatSize(t)} without making it unusable.");

        Report(progress, 0.95, "Saving…");
        var path = SaveBytesNextToInput(request, bytes, output.Extension, "compressed", cancellationToken);
        return Result(path, image);
    }

    static ToolOutputFormat OutputFor(InputFile input, LoadedImage image)
    {
        if (input.HasExtension(ImageFormats.Jpeg))
            return new(MagickFormat.Jpeg, input.Extension, false);
        var native = input.Extension switch
        {
            ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP,
            ".avif" => MagickFormat.Avif,
            ".jxl" => MagickFormat.Jxl,
            _ => MagickFormat.Unknown,
        };
        if (native != MagickFormat.Unknown && MagickRuntime.CanEncode(native))
            return new(native, input.Extension, false);
        return image.HasTransparency ? new(MagickFormat.Png, ".png", true) : new(MagickFormat.Jpeg, ".jpg", true);
    }

    static byte[] Smaller(LoadedImage image, MagickFormat format, InputFile input, CancellationToken cancellationToken)
    {
        switch (format)
        {
            case MagickFormat.Png:
                Quantize(image, 256);
                return ImageEncoder.Encode(image, format, new EncodeOptions { PngMaxCompression = true }, cancellationToken);
            case MagickFormat.Jpeg:
                var quality = image.SourceQuality is { } source ? Math.Clamp((int)source - 12, 40, 75) : 72;
                return ImageEncoder.Encode(image, format, new EncodeOptions { Quality = quality }, cancellationToken);
            case MagickFormat.WebP:
                var lossless = WebPInfo.IsLossless(input.Path);
                return ImageEncoder.Encode(image, format, new EncodeOptions { Quality = lossless ? 85 : 70 }, cancellationToken);
            case MagickFormat.Avif:
                return ImageEncoder.Encode(image, format, new EncodeOptions { Quality = 45 }, cancellationToken);
            default:
                return ImageEncoder.Encode(image, format, new EncodeOptions { Quality = 75 }, cancellationToken);
        }
    }

    static byte[] ToTarget(LoadedImage image, MagickFormat format, long target, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        using var original = (MagickImageCollection)image.Frames.Clone();
        var scale = 1.0;
        byte[]? best = null;
        for (var round = 0; round < 6; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0.2 + round * 0.12, round == 0 ? "Compressing…" : "Reducing dimensions…");
            var (fits, smallest) = format == MagickFormat.Png
                ? FitPng(image, target, cancellationToken)
                : FitQuality(image, format, target, cancellationToken);
            if (fits is not null)
            {
                best = fits;
                break;
            }
            // Even the strongest setting is too big: shrink the pixels by the remaining ratio and try again.
            scale *= Math.Clamp(Math.Sqrt((double)target / smallest.Length) * 0.95, 0.2, 0.95);
            Rescale(image, original, scale);
        }
        if (best is null)
            return [];
        if (scale < 1)
            image.AddWarning($"The dimensions were reduced to {image.First.Width}×{image.First.Height} to reach the target size.");
        return best;
    }

    /// <summary>
    /// Largest quality whose encoding fits (binary search; 4:2:0 chroma throughout so sizes grow smoothly), refined
    /// when the next quality step overshoots by a lot; otherwise null plus the smallest encoding seen.
    /// </summary>
    static (byte[]? Fits, byte[] Smallest) FitQuality(LoadedImage image, MagickFormat format, long target, CancellationToken cancellationToken)
    {
        byte[]? native = null;
        if (format == MagickFormat.WebP && !image.IsAnimated)
        {
            // libwebp can search its own quantiser for a byte budget, far finer than the 1..100 quality scale.
            // On noisy images its size curve has gaps; other noise-shaping strengths fill them.
            foreach (var shaping in new int?[] { null, 100, 0, 75, 25 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = ImageEncoder.Encode(image, format,
                    new EncodeOptions { Quality = 90, TargetBytes = (int)Math.Min(int.MaxValue, target), WebPNoiseShaping = shaping }, cancellationToken);
                if (bytes.Length <= target && (native is null || bytes.Length > native.Length))
                    native = bytes;
                if (native is not null && native.Length >= target * 0.9)
                    return (native, native);
            }
        }

        int low = MinQuality, high = MaxQuality, bestQuality = 0;
        byte[]? fits = null;
        byte[]? smallest = null;
        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = (low + high) / 2;
            var bytes = ImageEncoder.Encode(image, format, Settings(quality), cancellationToken);
            if (smallest is null || bytes.Length < smallest.Length)
                smallest = bytes;
            if (bytes.Length <= target)
            {
                fits = bytes;
                bestQuality = quality;
                low = quality + 1;
            }
            else
            {
                high = quality - 1;
            }
        }
        if (fits is not null && fits.Length < target * 0.92 && bestQuality < MaxQuality && format != MagickFormat.WebP)
            fits = Refine(image, format, bestQuality + 1, target, cancellationToken) is { } refined && refined.Length > fits.Length ? refined : fits;
        if (native is not null && (fits is null || native.Length > fits.Length))
            fits = native;
        return (fits, smallest!);
    }

    /// <summary>
    /// Quality steps can jump 10–25 % in size on detailed images. Between two steps, the next quality with a barely
    /// visible softening (Gaussian σ ≤ 1) lands much closer to the target than dropping a whole step.
    /// </summary>
    static byte[]? Refine(LoadedImage image, MagickFormat format, int quality, long target, CancellationToken cancellationToken)
    {
        using var original = (MagickImageCollection)image.Frames.Clone();
        double lightest = 0, heaviest = 1.0;
        byte[]? best = null;
        try
        {
            for (var step = 0; step < 6; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sigma = (lightest + heaviest) / 2;
                var softened = (MagickImageCollection)original.Clone();
                foreach (var frame in softened)
                    frame.GaussianBlur(0, sigma);
                image.ReplaceAllFrames(softened);
                var bytes = ImageEncoder.Encode(image, format, Settings(quality), cancellationToken);
                if (bytes.Length <= target)
                {
                    best = bytes;
                    heaviest = sigma;
                }
                else
                {
                    lightest = sigma;
                }
            }
            return best;
        }
        finally
        {
            image.ReplaceAllFrames((MagickImageCollection)original.Clone());
        }
    }

    static EncodeOptions Settings(int quality) => new() { Quality = quality, FullChroma = false };

    /// <summary>Lossless first, then 256 → 16 colours; the first that fits wins.</summary>
    static (byte[]? Fits, byte[] Smallest) FitPng(LoadedImage image, long target, CancellationToken cancellationToken)
    {
        var scaled = (MagickImageCollection)image.Frames.Clone();
        byte[]? smallest = null;
        try
        {
            foreach (var colors in new uint[] { 0, 256, 128, 64, 32, 16 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.ReplaceAllFrames((MagickImageCollection)scaled.Clone());
                if (colors > 0)
                    Quantize(image, colors);
                // Palette candidates are cheap to squeeze at level 9; the full-colour one stays at the fast default.
                var bytes = ImageEncoder.Encode(image, MagickFormat.Png, new EncodeOptions { PngMaxCompression = colors > 0 }, cancellationToken);
                if (bytes.Length <= target)
                    return (bytes, bytes);
                if (smallest is null || bytes.Length < smallest.Length)
                    smallest = bytes;
            }
            return (null, smallest!);
        }
        finally
        {
            image.ReplaceAllFrames(scaled);
        }
    }

    static void Rescale(LoadedImage image, MagickImageCollection original, double scale)
    {
        var frames = (MagickImageCollection)original.Clone();
        foreach (var frame in frames)
        {
            var w = (uint)Math.Max(1, Math.Round(frame.Width * scale));
            var h = (uint)Math.Max(1, Math.Round(frame.Height * scale));
            ResizeOperation.ResizeFrame(frame, w, h);
        }
        image.ReplaceAllFrames(frames);
    }

    static void Quantize(LoadedImage image, uint colors)
    {
        var settings = new QuantizeSettings { Colors = colors, DitherMethod = DitherMethod.FloydSteinberg };
        foreach (var frame in image.Frames)
        {
            if (frame.Depth > 8)
                frame.Depth = 8;
            if (frame.TotalColors > colors)
                frame.Quantize(settings);
        }
    }

    static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : $"{Math.Round(bytes / 1024.0)} KB";
}
