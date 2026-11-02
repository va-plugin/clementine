using System.Windows;

namespace Clementine.Wheel;

/// <summary>
/// Outline of one wheel segment: an annular sector between <see cref="InnerRadius"/> and <see cref="OuterRadius"/>
/// whose straight edges sit half a gap inside the range (so neighbours are separated by a constant-width gap) and
/// whose four corners are rounded with <see cref="Corner"/>. Points are relative to the wheel centre and listed
/// clockwise: start edge (inner → outer), outer arc, end edge (outer → inner), inner arc back.
/// </summary>
public sealed record SectorOutline(
    double InnerRadius,
    double OuterRadius,
    double Corner,
    Point StartInnerEdge,
    Point StartOuterEdge,
    Point StartOuterArc,
    Point EndOuterArc,
    Point EndOuterEdge,
    Point EndInnerEdge,
    Point EndInnerArc,
    Point StartInnerArc,
    bool OuterArcIsLarge,
    bool InnerArcIsLarge)
{
    /// <summary>
    /// Builds the outline, or returns null when the range is a full circle (draw an annulus instead) or too thin to
    /// survive the gap.
    /// </summary>
    public static SectorOutline? Create(AngleRange range, double innerRadius, double outerRadius, double gap, double corner)
    {
        if (range.IsFullCircle || outerRadius <= innerRadius)
            return null;

        var halfGap = Math.Max(0, gap / 2);
        var halfSweep = Angles.ToRadians(Math.Min(range.Sweep, 180) / 2);
        var innerHalfWidth = innerRadius * Math.Sin(halfSweep) - halfGap;
        if (innerHalfWidth <= 0.5)
            return null;

        var c = Math.Max(0, Math.Min(corner, Math.Min((outerRadius - innerRadius) / 2 - 0.5, innerHalfWidth * 0.9)));

        var startDir = Angles.Direction(range.Start);
        var startNormal = Angles.Direction(range.Start + 90);
        var endDir = Angles.Direction(range.End);
        var endNormal = -Angles.Direction(range.End + 90);

        var (startOuterEdge, startOuterArc) = OuterCorner(startDir, startNormal, outerRadius, halfGap, c);
        var (startInnerEdge, startInnerArc) = InnerCorner(startDir, startNormal, innerRadius, halfGap, c);
        var (endOuterEdge, endOuterArc) = OuterCorner(endDir, endNormal, outerRadius, halfGap, c);
        var (endInnerEdge, endInnerArc) = InnerCorner(endDir, endNormal, innerRadius, halfGap, c);

        var outerSpan = Angles.Normalize(Angles.Of(endOuterArc - new Point()) - Angles.Of(startOuterArc - new Point()));
        var innerSpan = Angles.Normalize(Angles.Of(endInnerArc - new Point()) - Angles.Of(startInnerArc - new Point()));

        return new SectorOutline(
            innerRadius, outerRadius, c,
            startInnerEdge, startOuterEdge, startOuterArc, endOuterArc,
            endOuterEdge, endInnerEdge, endInnerArc, startInnerArc,
            OuterArcIsLarge: outerSpan > 180,
            InnerArcIsLarge: innerSpan > 180);
    }

    // Corner circle tangent to the edge line (offset by halfGap) and internally tangent to the outer arc.
    static (Point Edge, Point Arc) OuterCorner(Vector direction, Vector normal, double radius, double halfGap, double c)
    {
        var along = Math.Sqrt(Math.Max(0, (radius - c) * (radius - c) - (halfGap + c) * (halfGap + c)));
        var centre = direction * along + normal * (halfGap + c);
        var edge = direction * along + normal * halfGap;
        var arc = c > 0 ? centre * (radius / (radius - c)) : edge;
        return ((Point)edge, (Point)arc);
    }

    // Corner circle tangent to the edge line and externally tangent to the inner arc.
    static (Point Edge, Point Arc) InnerCorner(Vector direction, Vector normal, double radius, double halfGap, double c)
    {
        var along = Math.Sqrt(Math.Max(0, (radius + c) * (radius + c) - (halfGap + c) * (halfGap + c)));
        var centre = direction * along + normal * (halfGap + c);
        var edge = direction * along + normal * halfGap;
        var arc = c > 0 ? centre * (radius / (radius + c)) : edge;
        return ((Point)edge, (Point)arc);
    }
}
