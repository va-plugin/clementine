using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;
using Xunit.Abstractions;

namespace Clementine.Wheel.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(10);

    public Func<TimeSpan> Func => () => Now;

    public void Advance(double milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);
}

/// <summary>Pointer paths sampled like OLE DragOver delivers them: a point every few DIPs.</summary>
internal static class PointerPaths
{
    /// <summary>Points after <paramref name="from"/> up to and including <paramref name="to"/>, about <paramref name="step"/> apart.</summary>
    public static IEnumerable<Vector> Line(Vector from, Vector to, double step = 8)
    {
        var length = (to - from).Length;
        var count = Math.Max(1, (int)Math.Ceiling(length / step));
        for (var i = 1; i <= count; i++)
            yield return from + (to - from) * ((double)i / count);
    }

    /// <summary>A quadratic curve from <paramref name="from"/> to <paramref name="to"/> bowed by <paramref name="bow"/>
    /// × its length, away from the wheel centre when positive and towards it when negative.</summary>
    public static IEnumerable<Vector> Curve(Vector from, Vector to, double bow, double step = 8)
    {
        var chord = to - from;
        var middle = from + chord / 2;
        var normal = new Vector(-chord.Y, chord.X);
        normal.Normalize();
        if (Vector.Multiply(normal, middle) < 0)
            normal = -normal;
        var control = middle + normal * (bow * chord.Length);
        // Sample finely, then keep a point every `step` DIPs of arc length.
        var last = from;
        var travelled = 0.0;
        for (var i = 1; i <= 400; i++)
        {
            var t = i / 400.0;
            var point = (1 - t) * (1 - t) * from + 2 * t * (1 - t) * control + t * t * to;
            travelled += (point - last).Length;
            last = point;
            if (travelled >= step || i == 400)
            {
                travelled = 0;
                yield return point;
            }
        }
    }

    /// <summary>Along a circle of <paramref name="radius"/> from one wheel angle to another (degrees, either direction).</summary>
    public static IEnumerable<Vector> Arc(double radius, double fromDegrees, double toDegrees, double step = 8, Func<double, double>? wobble = null)
    {
        var sweep = toDegrees - fromDegrees;
        var length = Math.Abs(Angles.ToRadians(sweep)) * radius;
        var count = Math.Max(1, (int)Math.Ceiling(length / step));
        for (var i = 1; i <= count; i++)
        {
            var f = (double)i / count;
            yield return Angles.Direction(fromDegrees + sweep * f) * (radius + (wobble?.Invoke(f) ?? 0));
        }
    }

    /// <summary>Snaps points to the physical pixel grid of a monitor at <paramref name="pixelsPerDip"/>, as DragOver reports them.</summary>
    public static IEnumerable<Vector> OnPixelGrid(this IEnumerable<Vector> points, double pixelsPerDip) =>
        pixelsPerDip <= 0 ? points : points.Select(p => new Vector(Math.Round(p.X * pixelsPerDip) / pixelsPerDip, Math.Round(p.Y * pixelsPerDip) / pixelsPerDip));
}

/// <summary>
/// Menu-aim ("safe triangle") behaviour of the wheel: every variant of every group is reachable along natural paths
/// (straight, curved, overshooting, brushing the gap) on the real catalogs, while browsing the inner ring stays
/// instant, resting on a neighbour selects it within the settle delay, and the centre still resets.
/// </summary>
public class AimTests(ITestOutputHelper output)
{
    const double StepMs = 10;

    /// <summary>The wheels the user complained about plus the other big families (real engines when the app is built).</summary>
    static readonly (string[] Files, WheelMode Mode)[] RealWheels =
    [
        (["Festival loop.mp4"], WheelMode.Convert),
        (["Visual 02.mov"], WheelMode.Convert),
        (["Poster artwork.psd"], WheelMode.Convert),
        (["Photo.jpg"], WheelMode.Convert),
        (["Kick loop.wav"], WheelMode.Tools),
        (["Festival loop.mp4"], WheelMode.Tools),
        (["Festival loop.mp4"], WheelMode.Actions),
        (["Rider.docx"], WheelMode.Convert),
    ];

    // ------------------------------------------------------------------ reachability on the real catalogs

    [Fact]
    public void Every_variant_is_reachable_in_a_straight_line_on_the_real_wheels() => ForRealWheels(StraightPaths);

    [Fact]
    public void Every_variant_is_reachable_on_curved_overshooting_and_gap_brushing_paths() => ForRealWheels(CurvedPaths);

    [Fact]
    public void Sweeping_along_the_inner_ring_switches_at_once_even_under_a_wide_fan() => ForRealWheels(SweepPaths);

    [Fact]
    public void Every_variant_stays_reachable_with_a_shaky_hand() => ForRealWheels(JitterPaths);

    /// <summary>Stress: straight paths with up to ±1.5 DIP random jitter on every sample (hand tremor plus rounding).</summary>
    internal static int JitterPaths(string name, WheelSession session, FakeClock clock, List<string> failures)
    {
        var paths = 0;
        var random = new Random(1234);
        foreach (var amount in new[] { 1.0, 1.5 })
        {
            ForEachVariant(session, (s, v) =>
            {
                paths++;
                var path = PointerPaths.Line(session.Layout.SegmentCentroid(s), session.Layout.VariantCentroid(s, v))
                    .Select(p => p + new Vector((random.NextDouble() * 2 - 1) * amount, (random.NextDouble() * 2 - 1) * amount))
                    .ToList();
                path[^1] = session.Layout.VariantCentroid(s, v);
                Check(failures, $"{name}: {Label(session, s, v)} jitter ±{amount}", session, clock, s, v, path, 0);
            });
        }
        return paths;
    }

    internal static int StraightPaths(string name, WheelSession session, FakeClock clock, List<string> failures)
    {
        var paths = 0;
        // Speed (DIPs per 10 ms sample), rest on the group's label first, and the monitor's pixel grid (0 = exact).
        foreach (var (speed, rest, grid) in new[] { (8.0, 0.0, 0.0), (8.0, 150.0, 0.0), (4.0, 60.0, 0.0), (16.0, 0.0, 0.0), (8.0, 0.0, 1.0), (6.0, 0.0, 1.5), (12.0, 30.0, 1.25) })
        {
            ForEachVariant(session, (s, v) =>
            {
                paths++;
                var target = session.Layout.VariantCentroid(s, v);
                Check(failures, $"{name}: {Label(session, s, v)} straight @{speed}px rest {rest}ms grid {grid}", session, clock, s, v,
                    PointerPaths.Line(session.Layout.SegmentCentroid(s), target, speed).OnPixelGrid(grid), rest);
            });
        }
        return paths;
    }

    internal static int CurvedPaths(string name, WheelSession session, FakeClock clock, List<string> failures)
    {
        var paths = 0;
        ForEachVariant(session, (s, v) =>
        {
            var layout = session.Layout;
            var m = layout.Metrics;
            var start = layout.SegmentCentroid(s);
            var target = layout.VariantCentroid(s, v);
            var mid = layout.VariantsOf(s)[v].Mid;
            foreach (var bow in new[] { 0.12, 0.25, -0.08 })
            {
                foreach (var grid in new[] { 0.0, 1.0 })
                {
                    paths++;
                    Check(failures, $"{name}: {Label(session, s, v)} curve {bow:+0.00;-0.00} grid {grid}", session, clock, s, v,
                        PointerPaths.Curve(start, target, bow).OnPixelGrid(grid), 40);
                }
            }

            // Overshoots past the outer edge (into the pointer slack), then settles back on the label.
            paths++;
            var beyond = Angles.Direction(mid) * (m.OuterRingOuterRadius + m.PointerSlack - 3);
            Check(failures, $"{name}: {Label(session, s, v)} overshoot", session, clock, s, v,
                PointerPaths.Line(start, beyond).Concat(PointerPaths.Line(beyond, target)), 40);

            // Out to the gap first, then round along it, wobbling a few DIPs into the inner ring and back.
            paths++;
            var gap = (m.InnerRingOuterRadius + m.OuterRingInnerRadius) / 2;
            var segmentMid = layout.Segments[s].Mid;
            var delta = Angles.Normalize(mid - segmentMid + 180) - 180;
            var around = PointerPaths.Line(start, Angles.Direction(segmentMid) * gap)
                .Concat(PointerPaths.Arc(gap, segmentMid, segmentMid + delta, 8, f => -5.5 * Math.Sin(f * Math.PI * 3)))
                .Concat(PointerPaths.Line(Angles.Direction(mid) * gap, target));
            Check(failures, $"{name}: {Label(session, s, v)} along the gap", session, clock, s, v, around, 40);
        });
        return paths;
    }

    /// <summary>
    /// Sweeps round the inner ring from each group with a fan, two segments either way, and checks the highlight
    /// follows the pointer into each segment within <see cref="SweepLag"/> DIPs.
    /// </summary>
    internal static int SweepPaths(string name, WheelSession session, FakeClock clock, List<string> failures)
    {
        var paths = 0;
        var layout = session.Layout;
        var radius = (layout.Metrics.InnerRingInnerRadius + layout.Metrics.InnerRingOuterRadius) / 2;
        var count = session.Segments.Count;
        for (var s = 0; s < count; s++)
        {
            if (!session.Segments[s].HasVariants || count < 3)
                continue;
            foreach (var (direction, grid) in new[] { (+1, 0.0), (-1, 0.0), (+1, 1.0), (-1, 1.5), (+1, 1.25) })
            {
                paths++;
                var neighbour = (s + direction + count) % count;
                var further = (s + 2 * direction + count) % count;
                Start(session, clock, s);
                var from = layout.Segments[s].Mid;
                var to = from + direction * 360.0 / count * 2;
                var label = $"{name}: sweep {session.Segments[s].Label} → {session.Segments[neighbour].Label} → {session.Segments[further].Label} grid {grid}";
                var entered = layout.HitTest(layout.SegmentCentroid(s));
                var inside = 0.0;
                var last = layout.SegmentCentroid(s);
                foreach (var point in PointerPaths.Arc(radius, from, to).OnPixelGrid(grid))
                {
                    clock.Advance(StepMs);
                    session.MovePointer(point);
                    var under = layout.HitTest(point);
                    inside = under == entered ? inside + (point - last).Length : 0;
                    entered = under;
                    last = point;
                    if (session.Hover != under && inside > SweepLag)
                    {
                        failures.Add($"{label}: {inside:0} DIPs into {Describe(session, under)} at {Angles.Of(point):0}° but {Describe(session, session.Hover)} is highlighted");
                        break;
                    }
                }
            }
        }
        return paths;
    }

    /// <summary>
    /// How far into a segment a sweep may get before the highlight follows: about four DragOver samples, 40 ms at a
    /// brisk 800 DIPs/s. A sweep only looks different from aiming at the fan once its ray has visibly turned.
    /// </summary>
    const double SweepLag = 32;

    // ------------------------------------------------------------------ synthetic worst cases

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    public void Every_variant_is_reachable_for_every_ring_size_and_fan_width(int groups)
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim");
            var failures = new List<string>();
            var clock = new FakeClock();
            var model = WheelModel.Build(Fakes.Groups(groups, 10, 2, 4, 1, 8, 3, 6), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools);
            var session = WheelSession.ForDisplay(model, 1.5, clock.Func);
            for (var page = 0; page < session.Pages.Count; page++)
            {
                ForEachVariant(session, (s, v) =>
                {
                    var start = session.Layout.SegmentCentroid(s);
                    var target = session.Layout.VariantCentroid(s, v);
                    Check(failures, $"{groups} groups p{page + 1}: {Label(session, s, v)} straight", session, clock, s, v, PointerPaths.Line(start, target), 0);
                    Check(failures, $"{groups} groups p{page + 1}: {Label(session, s, v)} curve", session, clock, s, v, PointerPaths.Curve(start, target, 0.15), 0);
                });
                session.ClearHover();
                session.NextPage();
            }
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    [Fact]
    public void A_lone_group_reaches_all_ten_variants_round_the_whole_ring()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim");
            var failures = new List<string>();
            var clock = new FakeClock();
            var session = WheelSession.ForDisplay(WheelModel.Build(Fakes.Groups(1, 10), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools), 1.5, clock.Func);
            Assert.Equal(360, session.Layout.FanSpanOf(0)!.Value.Sweep, 3);
            ForEachVariant(session, (s, v) =>
                Check(failures, $"lone {Label(session, s, v)}", session, clock, s, v,
                    PointerPaths.Line(session.Layout.SegmentCentroid(s), session.Layout.VariantCentroid(s, v)), 0));
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    // ------------------------------------------------------------------ browsing, dwelling, centre, drop

    [Fact]
    public void Moving_inwards_or_through_the_centre_drops_the_fan_at_once() => ForRealWheels((name, session, clock, failures) =>
    {
        var paths = 0;
        ForEachVariant(session, (s, v) =>
        {
            if (v > 2)
                return;
            paths++;
            var layout = session.Layout;
            Start(session, clock, s);
            foreach (var point in PointerPaths.Line(layout.SegmentCentroid(s), layout.VariantCentroid(s, v)))
            {
                clock.Advance(StepMs);
                session.MovePointer(point);
            }
            // Straight back towards the middle, the way one cancels.
            foreach (var point in PointerPaths.Line(layout.VariantCentroid(s, v), new Vector(2, -3)))
            {
                clock.Advance(StepMs);
                session.MovePointer(point);
                var under = layout.HitTest(point);
                if (under.Kind is WheelHitKind.Centre && (session.Hover != WheelHit.Centre || session.ExpandedSegment != -1))
                {
                    failures.Add($"{name}: back from {Label(session, s, v)}: in the centre but hover {Describe(session, session.Hover)}, fan {session.ExpandedSegment}");
                    break;
                }
                // (The highlighted fan reaches FanHysteresis DIPs into the inner ring, so dipping in a little doesn't drop it.)
                if (under.Kind is WheelHitKind.Segment && under.Segment == s && session.Hover != under
                    && point.Length < layout.Metrics.InnerRingOuterRadius - WheelSession.FanHysteresis)
                {
                    failures.Add($"{name}: back from {Label(session, s, v)}: over its group but hover {Describe(session, session.Hover)}");
                    break;
                }
            }
        });
        return paths;
    });

    [Fact]
    public void Resting_on_a_neighbour_selects_it_within_the_settle_delay()
    {
        Sta.Run(() =>
        {
            var (session, clock, mp4, _) = VideoWheel();
            var layout = session.Layout;
            foreach (var neighbour in new[] { 1, session.Segments.Count - 1 })
            {
                // A straight hop from MP4's label to the neighbour's: it looks just like aiming at MP4's fan, so MP4 is
                // held while moving, but the pointer stops there.
                Start(session, clock, mp4);
                foreach (var point in PointerPaths.Line(layout.SegmentCentroid(mp4), layout.SegmentCentroid(neighbour)))
                {
                    clock.Advance(StepMs);
                    session.MovePointer(point);
                }
                var stoppedAt = clock.Now;
                while (session.Hover != WheelHit.OnSegment(neighbour) && clock.Now - stoppedAt < TimeSpan.FromSeconds(1))
                {
                    clock.Advance(25); // the window's aim timer
                    session.Tick();
                }
                Assert.Equal(WheelHit.OnSegment(neighbour), session.Hover);
                Assert.True(clock.Now - stoppedAt <= WheelSession.AimSettleDelay + TimeSpan.FromMilliseconds(25),
                    $"Took {(clock.Now - stoppedAt).TotalMilliseconds} ms to select the neighbour.");
                Assert.False(session.IsSwitchPending);
                Assert.Equal(session.Segments[neighbour].Group!.Default, session.HoveredOption);
            }
        });
    }

    [Fact]
    public void Resting_in_the_centre_after_crossing_it_folds_the_fan_and_targets_nothing()
    {
        Sta.Run(() =>
        {
            var (session, clock, _, prores) = VideoWheel();
            var layout = session.Layout;
            // ProRes's far variants sit beyond the centre: aim at one, but stop inside the dead zone.
            var far = Enumerable.Range(0, session.Segments[prores].Options.Count)
                .OrderByDescending(v => Math.Abs(Angles.Normalize(layout.VariantsOf(prores)[v].Mid - layout.Segments[prores].Mid + 180) - 180))
                .First();
            Start(session, clock, prores);
            var sawCentre = false;
            foreach (var point in PointerPaths.Line(layout.SegmentCentroid(prores), layout.VariantCentroid(prores, far)))
            {
                clock.Advance(StepMs);
                session.MovePointer(point);
                if (layout.HitTest(point).Kind == WheelHitKind.Centre)
                {
                    sawCentre = true;
                    Assert.Equal(WheelHit.Centre, session.Hover);
                    Assert.Null(session.Target);
                    Assert.Equal(prores, session.ExpandedSegment);
                    break;
                }
            }
            Assert.True(sawCentre, "The straight path to ProRes's farthest variant should cross the dead zone.");
            clock.Advance(WheelSession.AimSettleDelay.TotalMilliseconds + 1);
            Assert.True(session.Tick());
            Assert.Equal(WheelHit.Centre, session.Hover);
            Assert.Equal(-1, session.ExpandedSegment);
        });
    }

    [Fact]
    public void A_drop_or_click_while_the_fan_is_held_takes_what_is_under_the_pointer()
    {
        Sta.Run(() =>
        {
            var (session, clock, mp4, _) = VideoWheel();
            var layout = session.Layout;
            var neighbour = session.Segments.Count - 1;
            Start(session, clock, mp4);
            foreach (var point in PointerPaths.Line(layout.SegmentCentroid(mp4), layout.SegmentCentroid(neighbour)))
            {
                clock.Advance(StepMs);
                session.MovePointer(point);
            }
            Assert.True(session.IsSwitchPending, "The hop should have held MP4's fan.");
            Assert.Equal(mp4, session.Hover.Segment);
            // Drag feedback already describes the drop: the neighbour (if it can run), not the held group.
            var neighbourDefault = session.Segments[neighbour].Group!.Default;
            Assert.Equal(neighbourDefault.IsAvailable ? neighbourDefault : null, session.DropTarget);
            Assert.True(session.Settle());
            Assert.Equal(WheelHit.OnSegment(neighbour), session.Hover);
            Assert.False(session.Settle());
        });
    }

    [Fact]
    public void Keyboard_navigation_and_paging_ignore_a_held_fan()
    {
        Sta.Run(() =>
        {
            var (session, clock, mp4, _) = VideoWheel();
            var layout = session.Layout;
            Start(session, clock, mp4);
            foreach (var point in PointerPaths.Line(layout.SegmentCentroid(mp4), layout.SegmentCentroid(session.Segments.Count - 1)))
            {
                clock.Advance(StepMs);
                session.MovePointer(point);
            }
            Assert.True(session.IsSwitchPending);
            session.Navigate(WheelNavigation.Outward);
            Assert.False(session.IsSwitchPending);
            Assert.Equal(WheelHit.OnVariant(mp4, 0), session.Hover);
            session.Navigate(WheelNavigation.Clockwise);
            Assert.Equal(WheelHit.OnVariant(mp4, 1), session.Hover);
            clock.Advance(1000);
            Assert.False(session.Tick());
            Assert.Equal(WheelHit.OnVariant(mp4, 1), session.Hover);

            using var ws = new TestWorkspace("aim");
            var paged = new WheelSession(WheelModel.Build(Fakes.Groups(24, 3), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools), clock: clock.Func);
            Start(paged, clock, 0);
            foreach (var point in PointerPaths.Line(paged.Layout.SegmentCentroid(0), paged.Layout.SegmentCentroid(1)))
            {
                clock.Advance(StepMs);
                paged.MovePointer(point);
            }
            Assert.True(paged.NextPage());
            Assert.False(paged.IsSwitchPending);
            Assert.Equal(1, paged.PageIndex);
        });
    }

    [Fact]
    public void Renders_the_aim_states_for_review()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClementineDev", "aim-wheel", "shots");
        System.IO.Directory.CreateDirectory(directory);
        Sta.Run(() =>
        {
            var (session, clock, mp4, prores) = VideoWheel();
            var layout = session.Layout;
            var written = new List<string>();
            void Shot(string name, Vector pointer)
            {
                var path = System.IO.Path.Combine(directory, name);
                WheelPreviews.RenderSession(session, WheelInteraction.Drag, pointer, false, path);
                written.Add(path);
            }
            Vector Drive(IEnumerable<Vector> path, Func<Vector, bool>? stopWhen = null)
            {
                var last = new Vector();
                foreach (var point in path)
                {
                    clock.Advance(StepMs);
                    session.MovePointer(point);
                    last = point;
                    if (stopWhen?.Invoke(point) == true)
                        break;
                }
                return last;
            }

            // MP4 › H.265 sits above AVI/WMV: on the way the pointer crosses WMV, yet MP4 and its fan stay up.
            var h265 = session.Segments[mp4].Options.ToList().FindIndex(o => o.Label == "H.265");
            var wmv = session.Segments.Count - 1;
            Start(session, clock, mp4);
            Shot("aim-01-mp4-open.png", layout.SegmentCentroid(mp4));
            var at = Drive(PointerPaths.Line(layout.SegmentCentroid(mp4), layout.VariantCentroid(mp4, h265)),
                p => layout.HitTest(p) == WheelHit.OnSegment(wmv) && p.Length > layout.SegmentCentroid(0).Length + 6);
            Assert.Equal(WheelHit.OnSegment(mp4), session.Hover);
            Shot("aim-02-crossing-wmv-held.png", at);
            at = Drive(PointerPaths.Line(at, layout.VariantCentroid(mp4, h265)));
            Assert.Equal(WheelHit.OnVariant(mp4, h265), session.Hover);
            Shot("aim-03-mp4-h265.png", at);

            // ProRes's far side: the straight line crosses the dead zone; nothing is targeted there, the fan stays.
            var far = Enumerable.Range(0, session.Segments[prores].Options.Count)
                .OrderByDescending(v => Math.Abs(Angles.Normalize(layout.VariantsOf(prores)[v].Mid - layout.Segments[prores].Mid + 180) - 180)).First();
            Start(session, clock, prores);
            at = Drive(PointerPaths.Line(layout.SegmentCentroid(prores), layout.VariantCentroid(prores, far)), p => layout.HitTest(p).Kind == WheelHitKind.Centre);
            Assert.Equal(prores, session.ExpandedSegment);
            Shot("aim-04-prores-through-centre-held.png", at);
            at = Drive(PointerPaths.Line(at, layout.VariantCentroid(prores, far)));
            Assert.Equal(WheelHit.OnVariant(prores, far), session.Hover);
            Shot("aim-05-prores-far-variant.png", at);

            // A hop from MP4 onto WMV that stops there: WMV takes over after the settle delay.
            Start(session, clock, mp4);
            at = Drive(PointerPaths.Line(layout.SegmentCentroid(mp4), layout.SegmentCentroid(wmv)));
            Shot("aim-06-hop-to-wmv-held.png", at);
            clock.Advance(WheelSession.AimSettleDelay.TotalMilliseconds + 1);
            session.Tick();
            Assert.Equal(WheelHit.OnSegment(wmv), session.Hover);
            Shot("aim-07-rest-on-wmv.png", at);
            output.WriteLine(string.Join(Environment.NewLine, written));
        });
    }

    // ------------------------------------------------------------------ the window's timer and drop

    static readonly ConstructorInfo DragArgsConstructor = typeof(DragEventArgs).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)])!;

    [Fact]
    public void The_drag_window_gives_way_to_a_neighbour_while_no_drag_events_arrive_and_drops_on_what_is_under_the_pointer()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-window");
            var files = Fakes.Files(ws, "clip.mp4");
            var clock = new FakeClock();
            var catalog = Fakes.Groups(6, 4, 1);
            var chosen = new List<string>();
            var window = new WheelWindow(WheelInteraction.Drag, new RecordingLog());
            window.Chosen += (option, _) => chosen.Add(option.Info.Id);
            try
            {
                var session = new WheelSession(WheelModel.Build(catalog, files, WheelMode.Tools), clock: clock.Func);
                window.View.Show(session, WheelInteraction.Drag);
                window.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                window.View.Arrange(new Rect(window.View.DesiredSize));
                var data = new DataObject(DataFormats.FileDrop, files.Select(f => f.Path).ToArray());
                DragEventArgs Raise(RoutedEvent e, Vector offset)
                {
                    var args = (DragEventArgs)DragArgsConstructor.Invoke([data, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey,
                        DragDropEffects.Copy | DragDropEffects.Move, window.View, window.View.Centre + offset]);
                    args.RoutedEvent = e;
                    window.RaiseEvent(args);
                    return args;
                }

                var layout = session.Layout;
                void Hop(int to)
                {
                    foreach (var point in PointerPaths.Line(new Vector(0, 0), layout.SegmentCentroid(0)).Concat(PointerPaths.Line(layout.SegmentCentroid(0), layout.SegmentCentroid(to))))
                    {
                        clock.Advance(StepMs);
                        Raise(DragDrop.DragOverEvent, point);
                    }
                }

                // Group 0 ("Compress", 4 variants fanned from -60° to 100°) open; hop to group 1 (under the fan) and
                // stop: no more DragOver events arrive, the window's timer has to make the switch.
                Hop(1);
                Assert.True(session.IsSwitchPending, "The hop should hold group 0's fan.");
                Assert.Equal(0, session.Hover.Segment);
                clock.Advance(WheelSession.AimSettleDelay.TotalMilliseconds + 5);
                PumpFor(TimeSpan.FromMilliseconds(120));
                Assert.Equal(WheelHit.OnSegment(1), session.Hover);
                Assert.False(session.IsSwitchPending);

                // Same hop, but drop at once: the drop takes group 1, not the held group 0.
                Raise(DragDrop.DragOverEvent, new Vector(0, 0));
                Hop(1);
                Assert.True(session.IsSwitchPending);
                Assert.Equal(DragDropEffects.Copy, Raise(DragDrop.DropEvent, layout.SegmentCentroid(1)).Effects);
                Assert.Equal(["test.g1.v0"], chosen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    internal static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The ".mp4 Convert" wheel with MP4 and ProRes, from the real engines when available.</summary>
    static (WheelSession Session, FakeClock Clock, int Mp4, int ProRes) VideoWheel()
    {
        var clock = new FakeClock();
        var catalog = Catalog(out _);
        var ws = new TestWorkspace("aim");
        try
        {
            var session = WheelSession.ForDisplay(WheelModel.Build(catalog, Fakes.Files(ws, "Festival loop.mp4"), WheelMode.Convert), 1.5, clock.Func);
            var mp4 = session.Segments.ToList().FindIndex(s => s.Label == "MP4");
            var prores = session.Segments.ToList().FindIndex(s => s.Label == "ProRes");
            Assert.True(mp4 >= 0 && prores >= 0);
            Assert.True(session.Segments[mp4].Options.Count >= 4, "MP4 should offer H.264/H.265 with and without audio.");
            return (session, clock, mp4, prores);
        }
        finally
        {
            ws.Dispose();
        }
    }

    static readonly Lazy<(OperationCatalog Catalog, string Source)> SharedCatalog = new(() => (LoadCatalog(out var source), source));

    static OperationCatalog Catalog(out string source)
    {
        (var catalog, source) = SharedCatalog.Value;
        return catalog;
    }

    static OperationCatalog LoadCatalog(out string source)
    {
        var log = new RecordingLog();
        var services = HostServices.Create([], SettingsStore.InMemory(), log);
        if (RealCatalog.TryCreate(services.Engine, out var reason) is { } real)
        {
            source = $"real engines ({real.All.Count} operations)";
            return real;
        }
        source = $"preview samples (real engines unavailable: {reason.Split('\n')[0]})";
        return Fakes.SampleCatalog();
    }

    internal delegate int WheelCheck(string name, WheelSession session, FakeClock clock, List<string> failures);

    void ForRealWheels(WheelCheck check)
    {
        Sta.Run(() =>
        {
            var failures = new List<string>();
            var total = RunWheels(check, null, failures, output.WriteLine);
            output.WriteLine($"{total} paths, {failures.Count} failures");
            Assert.True(total > 0);
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    internal static int RunWheels(WheelCheck check, AimTuning? tuning, List<string> failures, Action<string> log)
    {
        var catalog = Catalog(out var source);
        log($"Catalog: {source}");
        using var ws = new TestWorkspace("aim");
        var total = 0;
        foreach (var (names, mode) in RealWheels)
        {
            var clock = new FakeClock();
            var model = WheelModel.Build(catalog, Fakes.Files(ws, names), mode, new RecordingLog());
            if (model.IsEmpty)
                continue;
            var session = WheelSession.ForDisplay(model, 1.5, clock.Func);
            if (tuning is not null)
                session.AimTuning = tuning;
            for (var page = 0; page < session.Pages.Count; page++)
            {
                var name = $"{string.Join("+", names)} {mode}" + (session.Pages.Count > 1 ? $" p{page + 1}" : "");
                var paths = check(name, session, clock, failures);
                log($"{name}: {session.Segments.Count} segments, {paths} paths");
                total += paths;
                session.ClearHover();
                session.NextPage();
            }
        }
        return total;
    }

    static void ForEachVariant(WheelSession session, Action<int, int> action)
    {
        for (var s = 0; s < session.Segments.Count; s++)
        {
            if (!session.Segments[s].HasVariants)
                continue;
            for (var v = 0; v < session.Layout.VariantsOf(s).Count; v++)
                action(s, v);
        }
    }

    /// <summary>The pointer comes out of the centre onto segment <paramref name="s"/>'s label, which opens its fan.</summary>
    internal static void Start(WheelSession session, FakeClock clock, int s, double restMs = 0)
    {
        session.ClearHover();
        clock.Advance(500);
        session.MovePointer(new Vector(0, 0));
        foreach (var point in PointerPaths.Line(new Vector(0, 0), session.Layout.SegmentCentroid(s)))
        {
            clock.Advance(StepMs);
            session.MovePointer(point);
        }
        clock.Advance(restMs);
        Assert.Equal(WheelHit.OnSegment(s), session.Hover);
    }

    /// <summary>Drives <paramref name="path"/> from segment s's label and checks the fan of s never folds and v ends up chosen.</summary>
    internal static void Check(List<string> failures, string label, WheelSession session, FakeClock clock, int s, int v, IEnumerable<Vector> path, double restMs)
    {
        Start(session, clock, s, restMs);
        var expected = WheelHit.OnVariant(s, v);
        var index = 0;
        foreach (var point in path)
        {
            index++;
            clock.Advance(StepMs);
            session.MovePointer(point);
            if (session.ExpandedSegment != s || (session.Hover.Kind is WheelHitKind.Segment or WheelHitKind.Variant && session.Hover.Segment != s))
            {
                failures.Add($"{label}: at sample {index} ({Angles.Of(point):0}°, r {point.Length:0}) over {Describe(session, session.Layout.HitTest(point))} " +
                    $"the fan of {session.Segments[s].Label} folded: hover {Describe(session, session.Hover)}, open {session.ExpandedSegment}");
                return;
            }
        }
        // Resting on the label afterwards changes nothing.
        clock.Advance(1000);
        session.Tick();
        if (session.Hover != expected || session.IsSwitchPending)
        {
            failures.Add($"{label}: ended on {Describe(session, session.Hover)} (pending {session.IsSwitchPending})");
            return;
        }
        var option = session.Segments[s].Options[v];
        if (session.HoveredOption != option || (option.IsAvailable && session.Target != option))
            failures.Add($"{label}: highlighted the right slice but targets {session.Target?.Info.Id ?? "nothing"}");
    }

    static string Label(WheelSession session, int s, int v) => $"{session.Segments[s].Label} › {session.Segments[s].Options[v].Label}";

    static string Describe(WheelSession session, WheelHit hit) => hit.Kind switch
    {
        WheelHitKind.Segment => session.Segments[hit.Segment].Label,
        WheelHitKind.Variant => Label(session, hit.Segment, hit.Variant),
        _ => hit.Kind.ToString(),
    };

    static string Report(List<string> failures)
    {
        var text = new StringBuilder($"{failures.Count} path(s) failed:");
        foreach (var failure in failures.Take(40))
            text.AppendLine().Append("  ").Append(failure);
        return text.ToString();
    }
}
