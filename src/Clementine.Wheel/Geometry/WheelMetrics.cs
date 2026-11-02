namespace Clementine.Wheel;

/// <summary>
/// Radii and spacing of the wheel in device-independent pixels (1/96 inch). When a page holds more segments than
/// fit comfortably the radii grow together (<see cref="Scaled"/>); gaps, corners and padding stay constant so the
/// wheel keeps its crisp proportions.
/// </summary>
public sealed record WheelMetrics
{
    /// <summary>The standard wheel, used for up to <see cref="WheelLayout.ComfortableSegments"/> segments.</summary>
    public static WheelMetrics Default { get; } = new();

    /// <summary>Radius of the centre disc that describes the files and the hovered operation.</summary>
    public double CentreRadius { get; init; } = 82;

    /// <summary>Inner edge of the inner ring. The pointer closer to the centre than this is in the cancel dead zone.</summary>
    public double InnerRingInnerRadius { get; init; } = 88;

    public double InnerRingOuterRadius { get; init; } = 158;

    public double OuterRingInnerRadius { get; init; } = 164;

    public double OuterRingOuterRadius { get; init; } = 234;

    /// <summary>Width of the gap between neighbouring segments (constant along the radius).</summary>
    public double SegmentGap { get; init; } = 3;

    /// <summary>Corner rounding of segments (clamped for small segments).</summary>
    public double CornerRadius { get; init; } = 7;

    /// <summary>How far beyond the outer ring the pointer still selects by angle.</summary>
    public double PointerSlack { get; init; } = 26;

    /// <summary>Transparent margin around the wheel for its shadow and the pointer slack.</summary>
    public double Padding { get; init; } = 30;

    /// <summary>Radius of the glass disc behind the centre and the inner ring.</summary>
    public double DiscRadius => InnerRingOuterRadius + 5;

    /// <summary>Half the window size: the wheel centre sits at (<see cref="Extent"/>, <see cref="Extent"/>).</summary>
    public double Extent => OuterRingOuterRadius + Padding;

    /// <summary>Width and height of the wheel window.</summary>
    public double Size => Extent * 2;

    /// <summary>A copy whose radii are multiplied by <paramref name="factor"/>.</summary>
    public WheelMetrics Scaled(double factor)
    {
        if (factor <= 0 || double.IsNaN(factor))
            throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1)
            return this;
        return this with
        {
            CentreRadius = CentreRadius * factor,
            InnerRingInnerRadius = InnerRingInnerRadius * factor,
            InnerRingOuterRadius = InnerRingOuterRadius * factor,
            OuterRingInnerRadius = OuterRingInnerRadius * factor,
            OuterRingOuterRadius = OuterRingOuterRadius * factor,
        };
    }
}
