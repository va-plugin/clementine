using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

public enum RotateKind
{
    Right,
    Left,
    HalfTurn,
    FlipHorizontal,
    FlipVertical,
}

/// <summary>Rotate: 90° right · 90° left · 180° · Flip horizontal · Flip vertical (EXIF orientation applied first).</summary>
public sealed class RotateOperation : ImageToolOperation
{
    readonly RotateKind _kind;

    public RotateOperation(RotateKind kind)
        : base(Describe(kind)) => _kind = kind;

    public static IEnumerable<IOperation> All() => Enum.GetValues<RotateKind>().Select(k => new RotateOperation(k));

    protected override string WorkingStatus => _kind is RotateKind.FlipHorizontal or RotateKind.FlipVertical ? "Flipping…" : "Rotating…";

    protected override string Suffix(LoadedImage image, OptionValues options) =>
        _kind is RotateKind.FlipHorizontal or RotateKind.FlipVertical ? "flipped" : "rotated";

    protected override void Transform(LoadedImage image, OptionValues options, CancellationToken cancellationToken)
    {
        foreach (var frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (_kind)
            {
                case RotateKind.Right: frame.Rotate(90); break;
                case RotateKind.Left: frame.Rotate(270); break;
                case RotateKind.HalfTurn: frame.Rotate(180); break;
                case RotateKind.FlipHorizontal: frame.Flop(); break;
                case RotateKind.FlipVertical: frame.Flip(); break;
            }
            frame.ResetPage();
        }
    }

    static OperationInfo Describe(RotateKind kind)
    {
        var (id, variant, description, icon) = kind switch
        {
            RotateKind.Right => ("right", "90° right", "Turns the image clockwise.", "rotate"),
            RotateKind.Left => ("left", "90° left", "Turns the image anticlockwise.", "rotate"),
            RotateKind.HalfTurn => ("180", "180°", "Turns the image upside down.", "rotate"),
            RotateKind.FlipHorizontal => ("flip-horizontal", "Flip horizontal", "Mirrors the image left to right.", "flip"),
            _ => ("flip-vertical", "Flip vertical", "Mirrors the image top to bottom.", "flip"),
        };
        return new OperationInfo
        {
            Id = $"image.rotate.{id}",
            Category = OperationCategory.Tool,
            Group = "Rotate",
            Variant = variant,
            Title = kind is RotateKind.FlipHorizontal or RotateKind.FlipVertical ? variant : $"Rotate {variant}",
            Description = description + " JPEGs are re-saved at their original quality.",
            IconKey = icon,
            Order = 40 + (int)kind,
        };
    }
}
