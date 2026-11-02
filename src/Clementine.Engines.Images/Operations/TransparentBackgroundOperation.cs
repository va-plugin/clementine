using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

/// <summary>
/// Transparent background: removes a plain (e.g. white) background by flood-filling from the corners with a colour
/// tolerance, softens the cut edge, and saves a PNG. Only background connected to the edges is removed, so white
/// inside the subject stays.
/// </summary>
public sealed class TransparentBackgroundOperation : ImageOperation
{
    const double DefaultTolerance = 12;

    static readonly OptionSchema CustomSchema = new("Transparent background",
    [
        new NumberField("tolerance", "Colour tolerance", DefaultTolerance, 1, 60)
        {
            Unit = "%",
            Help = "How different from the corner colour a pixel may be and still count as background.",
        },
        new BoolField("smooth", "Smooth edges", true),
    ])
    {
        ConfirmLabel = "Remove background",
    };

    readonly bool _custom;

    public TransparentBackgroundOperation(bool custom)
        : base(new OperationInfo
        {
            Id = custom ? "image.background.remove.custom" : "image.background.remove",
            Category = OperationCategory.Tool,
            Group = "Transparent",
            Variant = custom ? "Custom…" : "Auto",
            Title = custom ? "Transparent background · custom" : "Transparent background",
            Description = custom
                ? "Choose how close to the background colour a pixel must be to disappear."
                : "Makes a plain background (white, black or any solid colour) transparent and saves a PNG.",
            IconKey = "transparent",
            Order = 70 + (custom ? 1 : 0),
        }) => _custom = custom;

    public static IEnumerable<IOperation> All() => [new TransparentBackgroundOperation(false), new TransparentBackgroundOperation(true)];

    public override OptionSchema? Options => _custom ? CustomSchema : null;

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var tolerance = _custom ? Math.Clamp(request.Options.GetDouble("tolerance", DefaultTolerance), 1, 60) : DefaultTolerance;
        var smooth = !_custom || request.Options.GetBool("smooth", true);

        Report(progress, 0.05, "Reading…");
        using var image = ImageLoader.Load(request.Input, LoadOptions.Still, cancellationToken);
        if (CornersAreTransparent(image.First))
            throw new OperationException("This image already has a transparent background.");
        Report(progress, 0.4, "Removing background…");
        var removed = RemoveBackground(image.First, tolerance, smooth);
        if (removed < 0.001)
            throw new OperationException("Couldn't find a plain background around the edges of this image.");
        if (removed > 0.995)
            throw new OperationException("The whole image matched the background colour — try Custom… with a lower tolerance.");

        Report(progress, 0.8, "Saving…");
        var path = SaveNextToInput(request, image, MagickFormat.Png, ".png", "transparent", EncodeOptions.Default, cancellationToken);
        return Result(path, image);
    }

    /// <summary>Clears the corner-connected background; returns the fraction of the image that became transparent.</summary>
    public static double RemoveBackground(IMagickImage<QuantumType> frame, double tolerancePercent, bool smoothEdges)
    {
        if (frame.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray)
            frame.ColorSpace = ColorSpace.sRGB;
        frame.Alpha(AlphaOption.Set);
        var before = TransparentFraction(frame);
        frame.ColorFuzz = new Percentage(tolerancePercent);
        var right = (int)frame.Width - 1;
        var bottom = (int)frame.Height - 1;
        foreach (var (x, y) in new[] { (0, 0), (right, 0), (0, bottom), (right, bottom) })
        {
            using var pixels = frame.GetPixelsUnsafe();
            var alphaIndex = pixels.GetChannelIndex(PixelChannel.Alpha);
            var pixel = pixels.GetPixel(x, y);
            if (alphaIndex is { } a && pixel.GetChannel(a) <= 0)
                continue; // already cleared by an earlier corner
            frame.FloodFill(MagickColors.Transparent, x, y);
        }

        if (smoothEdges)
            SoftenEdges(frame);
        return Math.Max(0, TransparentFraction(frame) - before);
    }

    static bool CornersAreTransparent(IMagickImage<QuantumType> frame)
    {
        if (!frame.HasAlpha)
            return false;
        using var pixels = frame.GetPixelsUnsafe();
        if (pixels.GetChannelIndex(PixelChannel.Alpha) is not { } alpha)
            return false;
        int right = (int)frame.Width - 1, bottom = (int)frame.Height - 1;
        return new[] { (0, 0), (right, 0), (0, bottom), (right, bottom) }
            .All(c => pixels.GetPixel(c.Item1, c.Item2).GetChannel(alpha) <= Quantum.Max * 0.02);
    }

    /// <summary>Anti-aliases the cut by feathering the alpha inwards only (no halo of the old background).</summary>
    static void SoftenEdges(IMagickImage<QuantumType> frame)
    {
        using var alpha = frame.Separate(Channels.Alpha).First();
        using var blurred = alpha.Clone();
        blurred.Blur(0, 0.8);
        alpha.Composite(blurred, CompositeOperator.Darken);
        frame.Composite(alpha, CompositeOperator.CopyAlpha);
    }

    static double TransparentFraction(IMagickImage<QuantumType> frame)
    {
        using var alpha = frame.Separate(Channels.Alpha).First();
        var histogramClear = 0L;
        using var pixels = alpha.GetPixelsUnsafe();
        var values = pixels.ToArray() ?? [];
        var stride = (int)alpha.ChannelCount;
        for (var i = 0; i < values.Length; i += stride)
        {
            if (values[i] <= Quantum.Max * 0.02)
                histogramClear++;
        }
        return values.Length == 0 ? 0 : histogramClear / (double)(values.Length / stride);
    }
}
