using System.Windows;
using System.Windows.Media;

namespace Clementine.Wheel;

/// <summary>Frozen WPF geometries for wheel segments and accents.</summary>
internal static class SegmentGeometry
{
    /// <summary>A rounded annular sector (see <see cref="SectorOutline"/>), or a full ring for a 360° range.</summary>
    public static Geometry Create(Point centre, AngleRange range, double innerRadius, double outerRadius, double gap, double corner)
    {
        var outline = SectorOutline.Create(range, innerRadius, outerRadius, gap, corner);
        if (outline is null)
            return range.IsFullCircle ? Ring(centre, innerRadius, outerRadius) : Geometry.Empty;

        Point At(Point p) => new(centre.X + p.X, centre.Y + p.Y);
        var cornerSize = new Size(outline.Corner, outline.Corner);
        var rounded = outline.Corner > 0;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(outline.StartInnerEdge), isFilled: true, isClosed: true);
            context.LineTo(At(outline.StartOuterEdge), true, true);
            if (rounded)
                context.ArcTo(At(outline.StartOuterArc), cornerSize, 0, false, SweepDirection.Clockwise, true, true);
            context.ArcTo(At(outline.EndOuterArc), new Size(outerRadius, outerRadius), 0, outline.OuterArcIsLarge, SweepDirection.Clockwise, true, true);
            if (rounded)
                context.ArcTo(At(outline.EndOuterEdge), cornerSize, 0, false, SweepDirection.Clockwise, true, true);
            context.LineTo(At(outline.EndInnerEdge), true, true);
            if (rounded)
                context.ArcTo(At(outline.EndInnerArc), cornerSize, 0, false, SweepDirection.Clockwise, true, true);
            context.ArcTo(At(outline.StartInnerArc), new Size(innerRadius, innerRadius), 0, outline.InnerArcIsLarge, SweepDirection.Counterclockwise, true, true);
            if (rounded)
                context.ArcTo(At(outline.StartInnerEdge), cornerSize, 0, false, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>An open arc (for strokes) centred on <paramref name="midAngle"/>.</summary>
    public static Geometry Arc(Point centre, double radius, double midAngle, double sweep)
    {
        var start = Angles.PointAt(centre, midAngle - sweep / 2, radius);
        var end = Angles.PointAt(centre, midAngle + sweep / 2, radius);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    static Geometry Ring(Point centre, double innerRadius, double outerRadius)
    {
        var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new EllipseGeometry(centre, outerRadius, outerRadius));
        ring.Children.Add(new EllipseGeometry(centre, innerRadius, innerRadius));
        ring.Freeze();
        return ring;
    }
}
