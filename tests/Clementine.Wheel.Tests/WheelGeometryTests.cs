using System.Windows;
using Clementine.Core.Hosting;

namespace Clementine.Wheel.Tests;

public class AngleTests
{
    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(1, 0, 90)]
    [InlineData(0, 1, 180)]
    [InlineData(-1, 0, 270)]
    [InlineData(1, -1, 45)]
    [InlineData(-1, -1, 315)]
    public void Angle_zero_is_up_and_grows_clockwise(double x, double y, double expected) =>
        Assert.Equal(expected, Angles.Of(new Vector(x, y)), 6);

    [Theory]
    [InlineData(-30, 330)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    [InlineData(-1e-15, 0)]
    public void Normalize_maps_into_0_360(double input, double expected) =>
        Assert.Equal(expected, Angles.Normalize(input), 9);

    [Fact]
    public void Direction_round_trips_through_Of()
    {
        for (var a = 0.0; a < 360; a += 7.5)
            Assert.Equal(a, Angles.Of(Angles.Direction(a)), 6);
    }

    [Fact]
    public void Range_contains_across_twelve_o_clock()
    {
        var range = new AngleRange(345, 30);
        Assert.True(range.Contains(350));
        Assert.True(range.Contains(0));
        Assert.True(range.Contains(14.9));
        Assert.False(range.Contains(15));
        Assert.False(range.Contains(344.9));
        Assert.Equal(0, range.Mid, 9);
    }
}

public class WheelLayoutTests
{
    static Vector At(double angle, double radius) => Angles.Direction(angle) * radius;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    public void Segments_partition_the_circle_starting_at_twelve(int count)
    {
        var layout = WheelLayout.Create(Enumerable.Repeat(1, count).ToList());
        Assert.Equal(count, layout.SegmentCount);
        Assert.Equal(0, layout.Segments[0].Mid, 9);
        Assert.Equal(360, layout.Segments.Sum(s => s.Sweep), 9);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i, layout.SegmentAt(layout.Segments[i].Mid));
            var next = layout.Segments[(i + 1) % count];
            Assert.Equal(Angles.Normalize(layout.Segments[i].End), next.Start, 9);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    public void Hit_test_finds_every_segment_at_its_centroid(int count)
    {
        var layout = WheelLayout.Create(Enumerable.Repeat(1, count).ToList());
        for (var i = 0; i < count; i++)
            Assert.Equal(WheelHit.OnSegment(i), layout.HitTest(layout.SegmentCentroid(i)));
    }

    [Fact]
    public void Centre_is_a_dead_zone_and_far_away_is_nothing()
    {
        var layout = WheelLayout.Create([1, 1, 1, 1, 1, 1]);
        var m = layout.Metrics;
        Assert.Equal(WheelHit.Centre, layout.HitTest(new Vector(0, 0)));
        Assert.Equal(WheelHit.Centre, layout.HitTest(At(10, m.InnerRingInnerRadius - 0.5)));
        Assert.Equal(WheelHitKind.Segment, layout.HitTest(At(10, m.InnerRingInnerRadius + 0.5)).Kind);
        Assert.Equal(WheelHit.None, layout.HitTest(At(10, m.OuterRingOuterRadius + m.PointerSlack + 1)));
    }

    [Fact]
    public void Empty_page_is_all_centre()
    {
        var layout = WheelLayout.Create([]);
        Assert.Equal(WheelHit.Centre, layout.HitTest(At(45, 120)));
        Assert.Equal(-1, layout.SegmentAt(45));
    }

    [Fact]
    public void Beyond_the_inner_ring_a_segment_without_variants_is_still_picked_by_angle()
    {
        var layout = WheelLayout.Create([1, 1, 1, 1]);
        var m = layout.Metrics;
        Assert.Equal(WheelHit.OnSegment(1), layout.HitTest(At(90, m.OuterRingInnerRadius + 10)));
        Assert.Equal(WheelHit.OnSegment(1), layout.HitTest(At(90, m.OuterRingOuterRadius + m.PointerSlack - 1)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(10)]
    public void Variant_fan_puts_the_default_directly_outward_and_keeps_cyclic_order(int variants)
    {
        var layout = WheelLayout.Create([1, variants, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
        var group = layout.Segments[1];
        var fan = layout.VariantsOf(1);
        Assert.Equal(variants, fan.Count);
        Assert.True(fan[0].Contains(group.Mid), "The recommended variant must sit outward of its group.");
        Assert.Equal(group.Mid, fan[0].Mid, 9);
        Assert.True(fan.Sum(f => f.Sweep) <= 360 + 1e-9);
        for (var v = 0; v < variants; v++)
        {
            // Each variant is clockwise-adjacent to the previous one (wrapping around inside the fan).
            var next = fan[(v + 1) % variants];
            var gap = Angles.Normalize(next.Start - Angles.Normalize(fan[v].End));
            if (v != variants - 1 - (variants - 1) / 2)
                Assert.True(gap < 1e-6 || 360 - gap < 1e-6, $"variant {v} → {v + 1} not adjacent");
            Assert.Equal(WheelHit.OnVariant(1, v), layout.HitTest(layout.VariantCentroid(1, v), expandedSegment: 1));
        }
    }

    [Fact]
    public void Ten_variants_on_a_single_group_fill_the_circle_without_overlap()
    {
        var layout = WheelLayout.Create([10]);
        var fan = layout.VariantsOf(0);
        Assert.Equal(10, fan.Count);
        Assert.Equal(360, fan.Sum(f => f.Sweep), 9);
        for (var a = 0.5; a < 360; a += 3)
            Assert.Equal(1, fan.Count(f => f.Contains(a)));
    }

    [Fact]
    public void More_than_ten_variants_are_capped()
    {
        var layout = WheelLayout.Create([14, 1]);
        Assert.Equal(WheelLayout.MaxVariants, layout.VariantsOf(0).Count);
    }

    [Fact]
    public void An_open_fan_wins_over_the_segments_under_it()
    {
        // 12 segments; segment 0 (top) has 8 variants that fan far around the wheel.
        var layout = WheelLayout.Create([8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
        var far = layout.VariantsOf(0)[3];
        var point = Angles.Direction(far.Mid) * ((layout.Metrics.OuterRingInnerRadius + layout.Metrics.OuterRingOuterRadius) / 2);
        Assert.Equal(WheelHit.OnVariant(0, 3), layout.HitTest(point, expandedSegment: 0));
        // Without the fan open, the same point selects the segment underneath by angle.
        Assert.Equal(WheelHit.OnSegment(layout.SegmentAt(far.Mid)), layout.HitTest(point, expandedSegment: -1));
    }

    [Fact]
    public void A_highlighted_fan_reaches_a_little_beyond_its_two_ends()
    {
        // Segment 0 (top) has 3 variants fanned over [-45°, 45°); segment 1's area starts beyond 45°.
        var layout = WheelLayout.Create([3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
        var span = layout.FanSpanOf(0)!.Value;
        var radius = layout.Metrics.OuterRingOuterRadius + 10;
        var reach = 12.0;
        var justPast = Angles.Direction(span.End + Angles.ToDegrees(reach / radius) * 0.8) * radius;
        var justBefore = Angles.Direction(span.Start - Angles.ToDegrees(reach / radius) * 0.8) * radius;
        var last = layout.VariantAt(0, span.End - 0.01);
        var first = layout.VariantAt(0, span.Start + 0.01);
        Assert.Equal(WheelHit.OnVariant(0, last), layout.HitTest(justPast, 0, fanEndReach: reach));
        Assert.Equal(WheelHit.OnVariant(0, first), layout.HitTest(justBefore, 0, fanEndReach: reach));
        // Without the hysteresis, or further out, the segment under that angle is picked again.
        Assert.Equal(WheelHit.OnSegment(layout.SegmentAt(Angles.Of(justPast))), layout.HitTest(justPast, 0));
        var farPast = Angles.Direction(span.End + Angles.ToDegrees(reach / radius) * 1.5) * radius;
        Assert.Equal(WheelHit.OnSegment(layout.SegmentAt(Angles.Of(farPast))), layout.HitTest(farPast, 0, fanEndReach: reach));
        // The inner ring is unaffected.
        var inner = Angles.Direction(span.End + 1) * layout.Metrics.InnerRingOuterRadius;
        Assert.Equal(WheelHit.OnSegment(layout.SegmentAt(span.End + 1)), layout.HitTest(inner, 0, fanEndReach: reach));
    }

    [Fact]
    public void Outer_band_opens_the_fan_of_the_segment_under_the_pointer()
    {
        var layout = WheelLayout.Create([1, 3, 1, 1]);
        var point = Angles.Direction(90) * (layout.Metrics.OuterRingInnerRadius + 20);
        Assert.Equal(WheelHit.OnVariant(1, 0), layout.HitTest(point, expandedSegment: -1));
    }

    [Theory]
    [InlineData(12, 1.0)]
    [InlineData(13, 1.045)]
    [InlineData(16, 1.18)]
    [InlineData(30, 1.18)]
    public void Radii_grow_gently_past_twelve_segments(int count, double scale)
    {
        Assert.Equal(scale, WheelLayout.ScaleFor(count), 6);
        var layout = WheelLayout.Create(Enumerable.Repeat(1, Math.Min(count, 16)).ToList());
        Assert.Equal(WheelMetrics.Default.OuterRingOuterRadius * WheelLayout.ScaleFor(Math.Min(count, 16)), layout.Metrics.OuterRingOuterRadius, 6);
        Assert.Equal(WheelMetrics.Default.SegmentGap, layout.Metrics.SegmentGap);
    }

    [Fact]
    public void Metrics_keep_the_slack_inside_the_window()
    {
        var m = WheelMetrics.Default;
        Assert.True(m.PointerSlack <= m.Padding);
        Assert.True(m.CentreRadius < m.InnerRingInnerRadius);
        Assert.True(m.InnerRingOuterRadius < m.OuterRingInnerRadius);
        Assert.Equal(m.Extent * 2, m.Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Scaled(0));
    }
}

public class WheelPagerTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(12, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(24, 2)]
    [InlineData(31, 3)]
    public void Pages_hold_at_most_sixteen_segments(int groups, int pages)
    {
        var result = WheelPager.Paginate(groups);
        Assert.Equal(pages, result.Count);
        Assert.All(result, p => Assert.InRange(p.SegmentCount, 0, WheelPager.MaxSegmentsPerPage));
        Assert.Equal(groups, result.Sum(p => p.Count));
        Assert.Equal(pages > 1, result.All(p => p.HasMore));
        var expectedFirst = 0;
        foreach (var page in result)
        {
            Assert.Equal(expectedFirst, page.First);
            expectedFirst += page.Count;
        }
    }

    [Fact]
    public void Twenty_four_groups_split_fifteen_plus_nine_each_with_more()
    {
        var pages = WheelPager.Paginate(24);
        Assert.Equal((0, 15, true), (pages[0].First, pages[0].Count, pages[0].HasMore));
        Assert.Equal((15, 9, true), (pages[1].First, pages[1].Count, pages[1].HasMore));
        Assert.Equal(15, pages[0].MoreSegment);
        Assert.Equal(9, pages[1].MoreSegment);
    }
}

public class SectorOutlineTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(345, 30)]
    [InlineData(100, 120)]
    [InlineData(0, 180)]
    [InlineData(10, 15)]
    public void Corners_sit_on_their_arcs_and_inside_the_gap(double start, double sweep)
    {
        const double inner = 88, outer = 158, gap = 3, corner = 7;
        var outline = SectorOutline.Create(new AngleRange(start, sweep), inner, outer, gap, corner);
        Assert.NotNull(outline);
        Assert.Equal(outer, (outline!.StartOuterArc - new Point()).Length, 6);
        Assert.Equal(outer, (outline.EndOuterArc - new Point()).Length, 6);
        Assert.Equal(inner, (outline.StartInnerArc - new Point()).Length, 6);
        Assert.Equal(inner, (outline.EndInnerArc - new Point()).Length, 6);

        // Straight edges are exactly half a gap inside the range boundaries.
        var startNormal = Angles.Direction(start + 90);
        var endNormal = -Angles.Direction(start + sweep + 90);
        Assert.Equal(gap / 2, (outline.StartOuterEdge - new Point()) * startNormal, 6);
        Assert.Equal(gap / 2, (outline.StartInnerEdge - new Point()) * startNormal, 6);
        Assert.Equal(gap / 2, (outline.EndOuterEdge - new Point()) * endNormal, 6);
        Assert.Equal(gap / 2, (outline.EndInnerEdge - new Point()) * endNormal, 6);
        Assert.True(outline.Corner is > 0 and <= corner);
        Assert.False(outline.InnerArcIsLarge);
    }

    [Fact]
    public void Full_circle_and_hopelessly_thin_ranges_have_no_outline()
    {
        Assert.Null(SectorOutline.Create(new AngleRange(0, 360), 88, 158, 3, 7));
        Assert.Null(SectorOutline.Create(new AngleRange(0, 1), 88, 158, 3, 7));
    }

    [Fact]
    public void Corner_is_clamped_for_narrow_segments()
    {
        var outline = SectorOutline.Create(new AngleRange(0, 15), 88, 158, 3, 20);
        Assert.NotNull(outline);
        Assert.True(outline!.Corner < 10);
    }
}

public class WheelPlacementTests
{
    static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Centre_stays_put_when_the_disc_fits() =>
        Assert.Equal(new ScreenPoint(960, 500), WheelPlacement.ClampCentre(new ScreenPoint(960, 500), 240, Work));

    [Theory]
    [InlineData(5, 5, 240, 240)]
    [InlineData(1915, 1035, 1680, 800)]
    [InlineData(-300, 500, 240, 500)]
    public void Disc_is_pushed_inside_the_work_area(int x, int y, int expectedX, int expectedY) =>
        Assert.Equal(new ScreenPoint(expectedX, expectedY), WheelPlacement.ClampCentre(new ScreenPoint(x, y), 240, Work));

    [Fact]
    public void Works_on_a_secondary_monitor_with_negative_coordinates()
    {
        var left = new PixelRect(-2560, 0, 0, 1400);
        Assert.Equal(new ScreenPoint(-290, 290), WheelPlacement.ClampCentre(new ScreenPoint(-10, 10), 290, left));
        Assert.Equal(new ScreenPoint(-1280, 700), WheelPlacement.ClampCentre(new ScreenPoint(-1280, 700), 290, left));
    }

    [Fact]
    public void Too_small_work_area_centres_the_disc() =>
        Assert.Equal(new ScreenPoint(200, 150), WheelPlacement.ClampCentre(new ScreenPoint(0, 0), 400, new PixelRect(0, 0, 400, 300)));

    [Fact]
    public void Top_left_centres_the_window() =>
        Assert.Equal(new ScreenPoint(700, 240), WheelPlacement.TopLeft(new ScreenPoint(960, 500), 520));
}
