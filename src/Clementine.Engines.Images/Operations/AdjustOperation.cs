using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

public enum AdjustPreset
{
    AutoEnhance,
    BlackAndWhite,
    Custom,
}

/// <summary>Adjust: Auto enhance · Black &amp; white · Custom… (brightness, contrast, saturation, gamma, hue).</summary>
public sealed class AdjustOperation : ImageToolOperation
{
    static readonly OptionSchema CustomSchema = new("Adjust colours",
    [
        new NumberField("brightness", "Brightness", 0, -100, 100) { Unit = "%" },
        new NumberField("contrast", "Contrast", 0, -100, 100) { Unit = "%" },
        new NumberField("saturation", "Saturation", 100, 0, 300) { Unit = "%" },
        new NumberField("gamma", "Gamma", 1, 0.1, 5) { Step = 0.05, Decimals = 2, Help = "Above 1 brightens the mid-tones, below 1 darkens them." },
        new NumberField("hue", "Hue shift", 0, -180, 180) { Unit = "°" },
    ])
    {
        ConfirmLabel = "Apply",
    };

    readonly AdjustPreset _preset;

    public AdjustOperation(AdjustPreset preset)
        : base(Describe(preset)) => _preset = preset;

    public static IEnumerable<IOperation> All() => Enum.GetValues<AdjustPreset>().Select(p => new AdjustOperation(p));

    public override OptionSchema? Options => _preset == AdjustPreset.Custom ? CustomSchema : null;

    protected override string WorkingStatus => "Adjusting…";

    protected override string Suffix(LoadedImage image, OptionValues options) => _preset switch
    {
        AdjustPreset.AutoEnhance => "enhanced",
        AdjustPreset.BlackAndWhite => "B&W",
        _ => "adjusted",
    };

    protected override void Transform(LoadedImage image, OptionValues options, CancellationToken cancellationToken)
    {
        foreach (var frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (_preset)
            {
                case AdjustPreset.AutoEnhance:
                    AutoEnhance(frame);
                    break;
                case AdjustPreset.BlackAndWhite:
                    frame.Grayscale(PixelIntensityMethod.Rec709Luminance);
                    break;
                default:
                    Apply(frame, options);
                    break;
            }
        }
    }

    /// <summary>Stretches the tonal range (same curve for all channels, so colours don't shift) and adds a touch of saturation.</summary>
    static void AutoEnhance(IMagickImage<QuantumType> frame)
    {
        frame.ContrastStretch(new Percentage(0.5), new Percentage(0.5));
        frame.Modulate(new Percentage(100), new Percentage(110), new Percentage(100));
    }

    static void Apply(IMagickImage<QuantumType> frame, OptionValues options)
    {
        var brightness = Math.Clamp(options.GetDouble("brightness", 0), -100, 100);
        var contrast = Math.Clamp(options.GetDouble("contrast", 0), -100, 100);
        var saturation = Math.Clamp(options.GetDouble("saturation", 100), 0, 300);
        var gamma = Math.Clamp(options.GetDouble("gamma", 1), 0.1, 5);
        var hue = Math.Clamp(options.GetDouble("hue", 0), -180, 180);

        if (brightness != 0 || contrast != 0)
            frame.BrightnessContrast(new Percentage(brightness), new Percentage(contrast), Channels.RGB);
        if (Math.Abs(gamma - 1) > 0.001)
            frame.GammaCorrect(gamma, Channels.RGB);
        if (Math.Abs(saturation - 100) > 0.01 || hue != 0)
            frame.Modulate(new Percentage(100), new Percentage(saturation), new Percentage(100 + hue / 180 * 100));
    }

    static OperationInfo Describe(AdjustPreset preset)
    {
        var (id, variant, description) = preset switch
        {
            AdjustPreset.AutoEnhance => ("auto", "Auto enhance", "Balances brightness and contrast and adds a little colour."),
            AdjustPreset.BlackAndWhite => ("bw", "Black & white", "Converts to greyscale using perceived brightness."),
            _ => ("custom", "Custom…", "Brightness, contrast, saturation, gamma and hue."),
        };
        return new OperationInfo
        {
            Id = $"image.adjust.{id}",
            Category = OperationCategory.Tool,
            Group = "Adjust",
            Variant = variant,
            Title = $"Adjust · {variant.TrimEnd('…')}",
            Description = description,
            IconKey = "adjust",
            Order = 60 + (int)preset,
        };
    }
}
