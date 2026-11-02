using System.Windows;

namespace Clementine.Wheel;

/// <summary>What the pointer is over.</summary>
public enum WheelHitKind
{
    /// <summary>Outside the wheel.</summary>
    None,

    /// <summary>The centre dead zone: dropping here cancels.</summary>
    Centre,

    /// <summary>An inner-ring segment (a group, or the "More" segment).</summary>
    Segment,

    /// <summary>An outer-ring variant of <see cref="WheelHit.Segment"/>.</summary>
    Variant,
}

/// <summary>Result of a hit test. <see cref="Segment"/> and <see cref="Variant"/> are -1 when not applicable.</summary>
public readonly record struct WheelHit(WheelHitKind Kind, int Segment = -1, int Variant = -1)
{
    public static WheelHit None => new(WheelHitKind.None);

    public static WheelHit Centre => new(WheelHitKind.Centre);

    public static WheelHit OnSegment(int segment) => new(WheelHitKind.Segment, segment);

    public static WheelHit OnVariant(int segment, int variant) => new(WheelHitKind.Variant, segment, variant);
}

/// <summary>
/// Pure geometry of one wheel page: inner-ring segments evenly around the circle (the first centred at 12 o'clock)
/// and, for each segment with variants, an outer-ring fan centred on that segment's angle. All coordinates are
/// relative to the wheel centre, in device-independent pixels.
/// </summary>
public sealed class WheelLayout
{
    /// <summary>Most variants a fan shows (the operation contract allows at most 10 per group).</summary>
    public const int MaxVariants = 10;

    /// <summary>Segments that fit the standard radii; more segments grow the wheel a little.</summary>
    public const int ComfortableSegments = 12;

    /// <summary>Narrowest angle a variant gets, so its two-line label always fits.</summary>
    public const double MinVariantSweep = 26;

    /// <summary>Widest angle a variant gets, so small fans stay compact around their group.</summary>
    public const double MaxVariantSweep = 40;

    /// <summary>
    /// Most of the circle one fan covers while other groups share the ring: a big family (ProRes, HAP) then gets
    /// narrower slices rather than wrapping round to the far side of the wheel, away from its group.
    /// </summary>
    public const double MaxFanSweep = 240;

    /// <summary>Narrowest slice a big fan may use to stay within <see cref="MaxFanSweep"/> (labels still fit on two lines).</summary>
    public const double CompactVariantSweep = 24;

    const double GrowthPerExtraSegment = 0.045;

    /// <summary>Most a page may grow beyond <see cref="ScaleFor"/> so long labels fit (see <c>WheelSession</c>).</summary>
    public const double MaxLabelGrowth = 1.16;

    readonly AngleRange[] _segments;
    readonly AngleRange[][] _variants;
    readonly AngleRange?[] _fanSpans;

    WheelLayout(WheelMetrics metrics, AngleRange[] segments, AngleRange[][] variants, AngleRange?[] fanSpans)
    {
        Metrics = metrics;
        _segments = segments;
        _variants = variants;
        _fanSpans = fanSpans;
    }

    public WheelMetrics Metrics { get; }

    public int SegmentCount => _segments.Length;

    public IReadOnlyList<AngleRange> Segments => _segments;

    /// <summary>
    /// Lays out <paramref name="variantCounts"/>.Count segments; entry i is the number of operations in segment i
    /// (a fan is created when it is greater than one).
    /// </summary>
    /// <param name="growth">Extra radius factor (1 to <see cref="MaxLabelGrowth"/>) when labels need more room.</param>
    /// <param name="variantLabels">Optional variant labels per segment, so fans keep families such as
    /// "H.265" / "H.265 · no audio" next to each other (see <see cref="FanLead"/>).</param>
    public static WheelLayout Create(
        IReadOnlyList<int> variantCounts, WheelMetrics? metrics = null, double growth = 1,
        IReadOnlyList<IReadOnlyList<string>>? variantLabels = null)
    {
        ArgumentNullException.ThrowIfNull(variantCounts);
        var count = variantCounts.Count;
        var scaled = (metrics ?? WheelMetrics.Default).Scaled(ScaleFor(count) * Math.Clamp(growth, 1, MaxLabelGrowth));
        var sweep = count == 0 ? 360 : 360.0 / count;

        var segments = new AngleRange[count];
        var variants = new AngleRange[count][];
        var spans = new AngleRange?[count];
        for (var i = 0; i < count; i++)
        {
            segments[i] = new AngleRange(Angles.Normalize(i * sweep - sweep / 2), sweep);
            var fanCount = Math.Min(variantCounts[i], MaxVariants);
            var labels = variantLabels is not null && i < variantLabels.Count ? variantLabels[i] : null;
            (variants[i], spans[i]) = Fan(segments[i].Mid, sweep, fanCount, labels is not null && labels.Count >= fanCount ? FanLead(labels.Take(fanCount).ToList()) : DefaultLead(fanCount));
        }
        return new WheelLayout(scaled, segments, variants, spans);
    }

    /// <summary>How much the radii grow for <paramref name="segmentCount"/> segments (1 up to <see cref="ComfortableSegments"/>).</summary>
    public static double ScaleFor(int segmentCount) =>
        segmentCount <= ComfortableSegments
            ? 1
            : 1 + (Math.Min(segmentCount, WheelPager.MaxSegmentsPerPage) - ComfortableSegments) * GrowthPerExtraSegment;

    /// <summary>The outer-ring fan of <paramref name="segment"/> (empty when it has no variants).</summary>
    public IReadOnlyList<AngleRange> VariantsOf(int segment) => _variants[segment];

    /// <summary>The whole arc the fan of <paramref name="segment"/> covers (null when it has no fan).</summary>
    public AngleRange? FanSpanOf(int segment) => segment >= 0 && segment < _fanSpans.Length ? _fanSpans[segment] : null;

    /// <summary>The segment at <paramref name="angle"/> (-1 when the page is empty).</summary>
    public int SegmentAt(double angle)
    {
        var count = _segments.Length;
        if (count == 0)
            return -1;
        var sweep = 360.0 / count;
        var index = (int)Math.Floor(Angles.Normalize(angle + sweep / 2) / sweep);
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>The variant of <paramref name="segment"/> whose fan slice contains <paramref name="angle"/>, or -1.</summary>
    public int VariantAt(int segment, double angle)
    {
        if (segment < 0 || segment >= _variants.Length)
            return -1;
        var fan = _variants[segment];
        for (var variant = 0; variant < fan.Length; variant++)
        {
            if (fan[variant].Contains(angle))
                return variant;
        }
        return -1;
    }

    /// <summary>
    /// Polar hit test. Inside the inner ring's inner edge is the dead zone; the inner ring picks a segment by angle;
    /// beyond it (the gap between the rings included) the fan of <paramref name="expandedSegment"/> wins, so the
    /// pointer can travel around an open fan; otherwise the segment under that angle — or one of its variants — is
    /// picked. Beyond the outer ring plus <see cref="WheelMetrics.PointerSlack"/> nothing is hit.
    /// </summary>
    /// <param name="fanReach">How far (DIPs) the open fan reaches down into the inner ring: hysteresis while a variant
    /// is already highlighted, so brushing the inner ring's edge on the way round the fan doesn't drop it.</param>
    /// <param name="fanEndReach">How far (DIPs, along the outer band) the open fan reaches beyond its two ends: the same
    /// hysteresis for overshooting sideways past the last variant.</param>
    public WheelHit HitTest(Vector offset, int expandedSegment = -1, double fanReach = 0, double fanEndReach = 0)
    {
        var radius = offset.Length;
        var limit = Metrics.OuterRingOuterRadius + Metrics.PointerSlack;
        if (radius > limit)
            return WheelHit.None;
        if (radius < Metrics.InnerRingInnerRadius || _segments.Length == 0)
            return WheelHit.Centre;

        var angle = Angles.Of(offset);
        var segment = SegmentAt(angle);
        if (radius <= Metrics.InnerRingOuterRadius)
        {
            if (fanReach > 0 && radius > Metrics.InnerRingOuterRadius - fanReach && VariantAt(expandedSegment, angle) is var held and >= 0)
                return WheelHit.OnVariant(expandedSegment, held);
            return WheelHit.OnSegment(segment);
        }

        if (VariantAt(expandedSegment, angle) is var open and >= 0)
            return WheelHit.OnVariant(expandedSegment, open);
        if (fanEndReach > 0 && FanSpanOf(expandedSegment) is { Sweep: < 360 } span)
        {
            var reach = Angles.ToDegrees(fanEndReach / radius);
            if (Angles.Normalize(span.Start - angle) is var before and > 0 && before <= reach)
                return WheelHit.OnVariant(expandedSegment, VariantAt(expandedSegment, span.Start + 1e-6));
            if (Angles.Normalize(angle - span.End) is var after and > 0 && after <= reach)
                return WheelHit.OnVariant(expandedSegment, VariantAt(expandedSegment, span.End - 1e-6));
        }
        return VariantAt(segment, angle) is var variant and >= 0
            ? WheelHit.OnVariant(segment, variant)
            : WheelHit.OnSegment(segment);
    }

    /// <summary>
    /// Menu-aim geometry: how far a pointer at <paramref name="from"/> moving along <paramref name="direction"/> has to
    /// travel to reach the fan of <paramref name="segment"/>, i.e. the outer band (from the inner ring's outer edge, so
    /// the gap between the rings counts, out to the outer edge plus <see cref="WheelMetrics.PointerSlack"/>) within the
    /// fan's arc widened by <paramref name="margin"/> degrees on each side. 0 when the pointer is already there, null
    /// when the ray misses. A ray passing within <paramref name="centreClearance"/> of the centre heads into the dead
    /// zone rather than at the fan, so only its stretch before the closest approach counts; rays that merely skirt the
    /// dead zone keep going, which is how a wide fan's far side (ProRes spans up to 240°) stays reachable in a straight line.
    /// </summary>
    public double? AimDistance(int segment, Vector from, Vector direction, double margin = 0, double centreClearance = 0)
    {
        if (FanSpanOf(segment) is not { } fan)
            return null;
        var length = direction.Length;
        if (!(length > 1e-9) || !double.IsFinite(length))
            return null;
        var d = direction / length;
        var inner = Metrics.InnerRingOuterRadius;
        var outer = Metrics.OuterRingOuterRadius + Metrics.PointerSlack;

        // |from + t·d|² = t² + 2bt + c
        var b = from.X * d.X + from.Y * d.Y;
        var c = from.LengthSquared;
        var outerRoot = b * b - c + outer * outer;
        if (outerRoot <= 0)
            return null;
        var exit = -b + Math.Sqrt(outerRoot);
        if (exit <= 0)
            return null;
        var entry = Math.Max(0, -b - Math.Sqrt(outerRoot));
        if (b < 0 && Math.Sqrt(Math.Max(0, c - b * b)) < centreClearance)
            exit = Math.Min(exit, -b);

        var arc = fan.Sweep + 2 * margin >= 360 ? new AngleRange(0, 360) : new AngleRange(Angles.Normalize(fan.Start - margin), fan.Sweep + 2 * margin);
        var innerRoot = b * b - c + inner * inner;
        if (innerRoot <= 0)
            return EnterArc(from, d, arc, entry, exit);
        // The ray crosses the inner disc: the band is split into the stretch before it and the stretch after it.
        var hole = Math.Sqrt(innerRoot);
        return EnterArc(from, d, arc, entry, Math.Min(exit, -b - hole)) ?? EnterArc(from, d, arc, Math.Max(entry, -b + hole), exit);
    }

    /// <summary>First t in [t0, t1] where from + t·d lies inside <paramref name="arc"/> (the angle along a line is monotonic).</summary>
    static double? EnterArc(Vector from, Vector d, AngleRange arc, double t0, double t1)
    {
        if (t1 < t0)
            return null;
        var a0 = Angles.Of(from + d * t0);
        if (arc.Contains(a0))
            return t0;
        var a1 = Angles.Of(from + d * t1);
        // Wheel angles grow clockwise on screen; along the ray they grow when cross(from, d) > 0.
        var clockwise = from.X * d.Y - from.Y * d.X >= 0;
        var edge = clockwise ? arc.Start : Angles.Normalize(arc.End);
        var travel = clockwise ? Angles.Normalize(a1 - a0) : Angles.Normalize(a0 - a1);
        var toEdge = clockwise ? Angles.Normalize(edge - a0) : Angles.Normalize(a0 - edge);
        if (toEdge > travel + 1e-9)
            return null;
        // Where the ray crosses the radial line through that edge: from + t·d = s·u.
        var u = Angles.Direction(edge);
        var denominator = d.X * u.Y - d.Y * u.X;
        if (Math.Abs(denominator) < 1e-12)
            return t0;
        return Math.Clamp((u.X * from.Y - u.Y * from.X) / denominator, t0, t1);
    }

    /// <summary>Middle of a segment (where its label goes), relative to the centre.</summary>
    public Vector SegmentCentroid(int segment) =>
        Angles.Direction(_segments[segment].Mid) * ((Metrics.InnerRingInnerRadius + Metrics.InnerRingOuterRadius) / 2);

    /// <summary>Middle of a variant slice, relative to the centre.</summary>
    public Vector VariantCentroid(int segment, int variant) =>
        Angles.Direction(_variants[segment][variant].Mid) * ((Metrics.OuterRingInnerRadius + Metrics.OuterRingOuterRadius) / 2);

    /// <summary>How many variants of a fan of <paramref name="count"/> sit counter-clockwise of the default: about half.</summary>
    public static int DefaultLead(int count) => Math.Max(0, (count - 1) / 2);

    /// <summary>
    /// How many of the last variants wrap to the counter-clockwise side of the default. Starts from the balanced
    /// <see cref="DefaultLead"/> and moves by one when that would split a family across the two ends of the fan:
    /// variants whose label starts the same before " · " ("H.265" and "H.265 · no audio") stay neighbours.
    /// </summary>
    public static int FanLead(IReadOnlyList<string> labels)
    {
        var count = labels.Count;
        var lead = DefaultLead(count);
        if (count < 3)
            return lead;
        string Family(int i) => labels[i].Split(" · ", 2)[0].Trim();
        // The counter-clockwise run is variants [count - lead, count); a split is clean when the run's first member
        // starts a new family.
        bool Clean(int l) => l > 0 && l < count && !string.Equals(Family(count - l - 1), Family(count - l), StringComparison.OrdinalIgnoreCase);
        if (Clean(lead))
            return lead;
        foreach (var candidate in new[] { lead + 1, lead - 1 })
        {
            if (candidate >= 1 && candidate <= count - 2 && Clean(candidate))
                return candidate;
        }
        return lead;
    }

    /// <summary>
    /// The fan of one segment, indexed by variant. The recommended variant (index 0) sits directly outward of its
    /// group; the rest follow clockwise and the last <paramref name="lead"/> wrap around to the counter-clockwise side,
    /// so the reading order is kept (cyclically) and the most likely choices are closest to where the pointer already is.
    /// </summary>
    static (AngleRange[] Variants, AngleRange? Span) Fan(double mid, double segmentSweep, int count, int lead)
    {
        if (count <= 1)
            return ([], null);
        var each = Math.Clamp(segmentSweep, MinVariantSweep, MaxVariantSweep);
        // A lone group owns the whole ring, so its fan may go all the way round.
        if (segmentSweep < 360 && each * count > MaxFanSweep)
            each = Math.Max(CompactVariantSweep, MaxFanSweep / count);
        if (each * count > 360)
            each = 360.0 / count;
        lead = Math.Clamp(lead, 0, count - 1);
        var defaultStart = mid - each / 2;
        var fan = new AngleRange[count];
        for (var variant = 0; variant < count; variant++)
        {
            var slot = (variant + lead) % count;
            fan[variant] = new AngleRange(Angles.Normalize(defaultStart + (slot - lead) * each), each);
        }
        return (fan, new AngleRange(Angles.Normalize(defaultStart - lead * each), Math.Min(360, each * count)));
    }
}
