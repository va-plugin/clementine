using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

public enum ResizePreset
{
    Half,
    Quarter,
    Max1920,
    Max1080,
    Custom,
}

/// <summary>Resize: 50 % · 25 % · Max 1920 px · Max 1080 px · Custom…  (high-quality Lanczos in linear light).</summary>
public sealed class ResizeOperation : ImageToolOperation
{
    static readonly OptionSchema CustomSchema = new("Resize image",
    [
        new ChoiceField("mode", "Resize by",
        [
            new OptionChoice("percent", "Percentage"),
            new OptionChoice("width", "Width"),
            new OptionChoice("height", "Height"),
            new OptionChoice("fit", "Fit inside"),
            new OptionChoice("exact", "Exact size"),
        ], "percent"),
        new NumberField("percent", "Scale", 50, 1, 800) { Unit = "%", VisibleWhen = new OptionCondition("mode", "percent") },
        new NumberField("width", "Width", 1280, 1, 30000) { Unit = "px", VisibleWhen = new OptionCondition("mode", "width", "fit", "exact") },
        new NumberField("height", "Height", 720, 1, 30000) { Unit = "px", VisibleWhen = new OptionCondition("mode", "height", "fit", "exact") },
    ])
    {
        ConfirmLabel = "Resize",
        Description = "Width/Height keep the aspect ratio; Fit inside scales to fit the box; Exact size stretches.",
    };

    readonly ResizePreset _preset;

    public ResizeOperation(ResizePreset preset)
        : base(Describe(preset)) => _preset = preset;

    public static IEnumerable<IOperation> All() => Enum.GetValues<ResizePreset>().Select(p => new ResizeOperation(p));

    public override OptionSchema? Options => _preset == ResizePreset.Custom ? CustomSchema : null;

    protected override string WorkingStatus => "Resizing…";

    public override async Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (_preset != ResizePreset.Custom || inputs.Count == 0)
            return values;
        if (await ImageProbe.SizeAsync(inputs[0], cancellationToken) is { } size)
        {
            values["width"] = Math.Max(1, (double)Math.Round(size.Width / 2.0));
            values["height"] = Math.Max(1, (double)Math.Round(size.Height / 2.0));
        }
        return values;
    }

    protected override string Suffix(LoadedImage image, OptionValues options)
    {
        var (w, h) = TargetSize(image.First.Width, image.First.Height, options, out _);
        return _preset switch
        {
            ResizePreset.Half => "50%",
            ResizePreset.Quarter => "25%",
            ResizePreset.Max1920 => "1920px",
            ResizePreset.Max1080 => "1080px",
            _ when options.GetString("mode", "percent") == "percent" => $"{Math.Round(options.GetDouble("percent", 50))}%",
            _ => $"{w}x{h}",
        };
    }

    protected override void Transform(LoadedImage image, OptionValues options, CancellationToken cancellationToken)
    {
        var (w, h) = TargetSize(image.First.Width, image.First.Height, options, out var unchanged);
        if (unchanged)
        {
            image.AddWarning($"Already {image.First.Width}×{image.First.Height} — no larger than {Limit()} px, so the size is unchanged.");
            return;
        }
        foreach (var frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResizeFrame(frame, w, h);
        }
    }

    /// <summary>Resamples in linear light (no darkening of fine detail), keeping the colour profile.</summary>
    public static void ResizeFrame(IMagickImage<QuantumType> frame, uint width, uint height)
    {
        if (frame.Width == width && frame.Height == height)
            return;
        var profile = frame.GetColorProfile();
        var linear = frame.ColorSpace == ColorSpace.sRGB && (long)width * height < (long)frame.Width * frame.Height;
        if (linear)
            frame.ColorSpace = ColorSpace.RGB;
        frame.FilterType = FilterType.Lanczos;
        frame.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        if (linear)
            frame.ColorSpace = ColorSpace.sRGB;
        if (profile is not null && frame.GetColorProfile() is null)
            frame.SetProfile(profile);
        frame.ResetPage();
    }

    (uint Width, uint Height) TargetSize(uint width, uint height, OptionValues options, out bool unchanged)
    {
        unchanged = false;
        double scaleX, scaleY;
        switch (_preset)
        {
            case ResizePreset.Half:
                scaleX = scaleY = 0.5;
                break;
            case ResizePreset.Quarter:
                scaleX = scaleY = 0.25;
                break;
            case ResizePreset.Max1920 or ResizePreset.Max1080:
                var longest = Math.Max(width, height);
                if (longest <= Limit())
                {
                    unchanged = true;
                    return (width, height);
                }
                scaleX = scaleY = (double)Limit() / longest;
                break;
            default:
                var w = Math.Max(1, options.GetDouble("width", 1280));
                var h = Math.Max(1, options.GetDouble("height", 720));
                switch (options.GetString("mode", "percent"))
                {
                    case "width":
                        scaleX = scaleY = w / width;
                        break;
                    case "height":
                        scaleX = scaleY = h / height;
                        break;
                    case "fit":
                        scaleX = scaleY = Math.Min(w / width, h / height);
                        break;
                    case "exact":
                        return ((uint)Math.Round(w), (uint)Math.Round(h));
                    default:
                        scaleX = scaleY = Math.Clamp(options.GetDouble("percent", 50), 1, 800) / 100;
                        break;
                }
                break;
        }
        return ((uint)Math.Max(1, Math.Round(width * scaleX)), (uint)Math.Max(1, Math.Round(height * scaleY)));
    }

    uint Limit() => _preset == ResizePreset.Max1080 ? 1080u : 1920u;

    static OperationInfo Describe(ResizePreset preset)
    {
        var (id, variant, title, description, order) = preset switch
        {
            ResizePreset.Half => ("50", "50%", "Resize · 50%", "Half the width and height.", 10),
            ResizePreset.Quarter => ("25", "25%", "Resize · 25%", "A quarter of the width and height.", 11),
            ResizePreset.Max1920 => ("max1920", "Max 1920 px", "Resize · max 1920 px", "Longest side at most 1920 px; smaller images stay as they are.", 12),
            ResizePreset.Max1080 => ("max1080", "Max 1080 px", "Resize · max 1080 px", "Longest side at most 1080 px; smaller images stay as they are.", 13),
            _ => ("custom", "Custom…", "Resize · custom", "Choose a percentage, width, height or box to fit.", 14),
        };
        return new OperationInfo
        {
            Id = $"image.resize.{id}",
            Category = OperationCategory.Tool,
            Group = "Resize",
            Variant = variant,
            Title = title,
            Description = description,
            IconKey = "resize",
            Order = order,
        };
    }
}
