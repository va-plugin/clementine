using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

public enum CropPreset
{
    Square,
    Widescreen,
    Vertical,
    Portrait,
    TrimBorders,
    Custom,
}

/// <summary>Crop: 1:1 · 16:9 · 9:16 · 4:5 · Trim borders · Custom… (largest centred area of the ratio by default).</summary>
public sealed class CropOperation : ImageToolOperation
{
    static readonly OptionChoice[] Anchors =
    [
        new("center", "Centre"),
        new("top", "Top"),
        new("bottom", "Bottom"),
        new("left", "Left"),
        new("right", "Right"),
    ];

    static readonly OptionSchema CustomSchema = new("Crop image",
    [
        new ChoiceField("mode", "Crop to", [new("ratio", "Aspect ratio"), new("size", "Exact size")], "ratio"),
        new NumberField("ratio_w", "Ratio width", 3, 1, 100) { VisibleWhen = new OptionCondition("mode", "ratio") },
        new NumberField("ratio_h", "Ratio height", 2, 1, 100) { VisibleWhen = new OptionCondition("mode", "ratio") },
        new NumberField("width", "Width", 1000, 1, 30000) { Unit = "px", VisibleWhen = new OptionCondition("mode", "size") },
        new NumberField("height", "Height", 1000, 1, 30000) { Unit = "px", VisibleWhen = new OptionCondition("mode", "size") },
        new ChoiceField("anchor", "Keep", Anchors, "center"),
    ])
    {
        ConfirmLabel = "Crop",
    };

    readonly CropPreset _preset;

    public CropOperation(CropPreset preset)
        : base(Describe(preset)) => _preset = preset;

    public static IEnumerable<IOperation> All() => Enum.GetValues<CropPreset>().Select(p => new CropOperation(p));

    public override OptionSchema? Options => _preset == CropPreset.Custom ? CustomSchema : null;

    protected override string WorkingStatus => _preset == CropPreset.TrimBorders ? "Trimming…" : "Cropping…";

    public override async Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        var values = new OptionValues();
        if (_preset == CropPreset.Custom && inputs.Count > 0 && await ImageProbe.SizeAsync(inputs[0], cancellationToken) is { } size)
        {
            values["width"] = (double)size.Width;
            values["height"] = (double)size.Height;
        }
        return values;
    }

    protected override string Suffix(LoadedImage image, OptionValues options) => _preset switch
    {
        CropPreset.Square => "1x1",
        CropPreset.Widescreen => "16x9",
        CropPreset.Vertical => "9x16",
        CropPreset.Portrait => "4x5",
        CropPreset.TrimBorders => "trimmed",
        _ when options.GetString("mode", "ratio") == "ratio" => $"{options.GetInt("ratio_w", 3)}x{options.GetInt("ratio_h", 2)}",
        _ => "cropped",
    };

    protected override void Transform(LoadedImage image, OptionValues options, CancellationToken cancellationToken)
    {
        var area = _preset == CropPreset.TrimBorders ? TrimArea(image) : CropArea(image.First.Width, image.First.Height, options);
        foreach (var frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Crop(area);
            frame.ResetPage();
        }
    }

    MagickGeometry CropArea(uint width, uint height, OptionValues options)
    {
        double ratio;
        uint w, h;
        var anchor = _preset == CropPreset.Custom ? options.GetString("anchor", "center") : "center";
        if (_preset == CropPreset.Custom && options.GetString("mode", "ratio") == "size")
        {
            w = (uint)Math.Clamp(options.GetInt("width", (int)width), 1, (int)width);
            h = (uint)Math.Clamp(options.GetInt("height", (int)height), 1, (int)height);
        }
        else
        {
            ratio = _preset switch
            {
                CropPreset.Square => 1,
                CropPreset.Widescreen => 16.0 / 9,
                CropPreset.Vertical => 9.0 / 16,
                CropPreset.Portrait => 4.0 / 5,
                _ => Math.Max(1, options.GetDouble("ratio_w", 3)) / Math.Max(1, options.GetDouble("ratio_h", 2)),
            };
            if ((double)width / height > ratio)
            {
                h = height;
                w = (uint)Math.Clamp(Math.Round(height * ratio), 1, width);
            }
            else
            {
                w = width;
                h = (uint)Math.Clamp(Math.Round(width / ratio), 1, height);
            }
        }

        var x = (int)(width - w) / 2;
        var y = (int)(height - h) / 2;
        switch (anchor)
        {
            case "top": y = 0; break;
            case "bottom": y = (int)(height - h); break;
            case "left": x = 0; break;
            case "right": x = (int)(width - w); break;
        }
        return new MagickGeometry(x, y, w, h);
    }

    /// <summary>The union of every frame's content box (borders of a near-uniform colour removed).</summary>
    static MagickGeometry TrimArea(LoadedImage image)
    {
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        foreach (var frame in image.Frames)
        {
            frame.ColorFuzz = new Percentage(8);
            if (frame.BoundingBox is not { } box)
                continue;
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.X + (int)box.Width);
            bottom = Math.Max(bottom, box.Y + (int)box.Height);
        }
        var first = image.First;
        if (left == int.MaxValue || right <= left || bottom <= top
            || (left == 0 && top == 0 && right >= first.Width && bottom >= first.Height))
            throw new OperationException("There are no plain borders to trim in this image.");
        return new MagickGeometry(left, top, (uint)(right - left), (uint)(bottom - top));
    }

    static OperationInfo Describe(CropPreset preset)
    {
        var (id, variant, description) = preset switch
        {
            CropPreset.Square => ("square", "1:1", "Largest centred square."),
            CropPreset.Widescreen => ("16x9", "16:9", "Largest centred 16:9 area — widescreen video and slides."),
            CropPreset.Vertical => ("9x16", "9:16", "Largest centred 9:16 area — stories and reels."),
            CropPreset.Portrait => ("4x5", "4:5", "Largest centred 4:5 area — portrait posts."),
            CropPreset.TrimBorders => ("trim", "Trim borders", "Removes plain borders around the picture."),
            _ => ("custom", "Custom…", "Any aspect ratio or exact size, anchored where you like."),
        };
        return new OperationInfo
        {
            Id = $"image.crop.{id}",
            Category = OperationCategory.Tool,
            Group = "Crop",
            Variant = variant,
            Title = $"Crop · {variant.TrimEnd('…')}",
            Description = description,
            IconKey = "crop",
            Order = 30 + (int)preset,
        };
    }
}
