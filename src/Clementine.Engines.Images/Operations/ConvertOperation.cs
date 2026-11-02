using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

/// <summary>One flavour of a conversion target ("JPEG · Medium 80", "PNG · 2×").</summary>
public sealed record ConvertVariant
{
    /// <summary>Appended to "image.&lt;target&gt;" ("medium" → "image.jpg.medium"); null for the default variant.</summary>
    public string? IdSuffix { get; init; }

    /// <summary>Outer-ring label; required when the target has several variants.</summary>
    public string? Label { get; init; }

    /// <summary>Appended to the target name in the title ("high quality").</summary>
    public string? TitleDetail { get; init; }

    public required string Description { get; init; }

    public EncodeOptions Encode { get; init; } = EncodeOptions.Default;

    /// <summary>SVG rasterisation scale.</summary>
    public double SvgScale { get; init; } = 1;

    /// <summary>Whether SVG inputs get this variant (PNG offers SVGs drawing sizes instead of the plain entry).</summary>
    public SvgInputs Svg { get; init; } = SvgInputs.Any;

    /// <summary>File name suffix ("2x" → "logo (2x).png").</summary>
    public string? OutputSuffix { get; init; }

    public int OrderOffset { get; init; }
}

/// <summary>Which inputs a <see cref="ConvertVariant"/> is for, as far as SVG goes.</summary>
public enum SvgInputs
{
    /// <summary>Every readable image, SVG included.</summary>
    Any,

    /// <summary>SVG/SVGZ only (PNG's 1× / 2× / 4× drawing sizes).</summary>
    Only,

    /// <summary>Everything but SVG, which gets the <see cref="Only"/> variants instead.</summary>
    Excluded,
}

/// <summary>Converts an image to <see cref="ImageTarget"/>, following the shared conversion rules.</summary>
public sealed class ConvertOperation : ImageOperation
{
    readonly ImageTarget _target;
    readonly ConvertVariant _variant;

    public ConvertOperation(ImageTarget target, ConvertVariant variant)
        : base(new OperationInfo
        {
            Id = variant.IdSuffix is null ? $"image.{target.Key}" : $"image.{target.Key}.{variant.IdSuffix}",
            Category = OperationCategory.Convert,
            Group = target.Group,
            Variant = variant.Label,
            Title = variant.TitleDetail is null ? target.Group : $"{target.Group} · {variant.TitleDetail}",
            Description = variant.Description,
            IconKey = target.IconKey,
            OutputExtension = target.Extension,
            Order = target.Order + variant.OrderOffset,
            Weight = target.Weight,
        })
    {
        _target = target;
        _variant = variant;
    }

    public ImageTarget Target => _target;

    public override bool Accepts(InputFile file) =>
        AcceptsKind(file)
        && _variant.Svg switch
        {
            SvgInputs.Only => file.HasExtension(ImageFormats.Svg),
            SvgInputs.Excluded => !file.HasExtension(ImageFormats.Svg),
            _ => true,
        };

    /// <summary>
    /// A selection mixing SVGs with other images still gets the plain entry (its SVGs are drawn at their own size);
    /// only an all-SVG selection switches to the SVG drawing sizes.
    /// </summary>
    public override bool AcceptsAll(IReadOnlyList<InputFile> files) =>
        _variant.Svg == SvgInputs.Excluded
            ? files.Count > 0 && files.All(AcceptsKind) && files.Any(f => !f.HasExtension(ImageFormats.Svg))
            : base.AcceptsAll(files);

    bool AcceptsKind(InputFile file) =>
        base.Accepts(file)
        && !_target.SameFormat.Contains(file.Extension)
        // Animated PNG belongs to the media engine's animated GIF/WebP; here it would keep only its first frame.
        && !(ImageTargets.Traits(_target.Format).Animation && file.HasExtension(ImageFormats.AnimatedPng));

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        Report(progress, 0.05, "Reading…");
        var keepAnimation = ImageTargets.Traits(_target.Format).Animation;
        using var image = ImageLoader.Load(request.Input,
            new LoadOptions { KeepAnimation = keepAnimation, SvgScale = _variant.SvgScale }, cancellationToken);

        Report(progress, 0.5, $"Converting to {_target.Group}…");
        var output = SaveNextToInput(request, image, _target.Format, _target.Extension, _variant.OutputSuffix, _variant.Encode, cancellationToken);
        Report(progress, 1, "Done");
        return Result(output, image);
    }
}
