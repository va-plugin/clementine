using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;
using Xunit.Abstractions;

namespace Clementine.Wheel.Tests;

/// <summary>Runs only with CLEMENTINE_AIM_STRESS=1: the statistical menu-aim stress harness (a minute or two, prints a report).</summary>
public sealed class AimStressTheoryAttribute : TheoryAttribute
{
    public AimStressTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_AIM_STRESS") != "1")
            Skip = "Set CLEMENTINE_AIM_STRESS=1 to run the menu-aim stress harness (failure rate per movement style).";
    }
}

/// <inheritdoc cref="AimStressTheoryAttribute"/>
public sealed class AimStressFactAttribute : FactAttribute
{
    public AimStressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_AIM_STRESS") != "1")
            Skip = "Set CLEMENTINE_AIM_STRESS=1 to run the menu-aim stress harness.";
    }
}

/// <summary>
/// Adversarial checks of the wheel's menu-aim with human-like pointer movement: a minimum-jerk speed profile (slow
/// start, fast middle, slow end), mouse-rate sampling snapped to the pixel grid with an event only when the pixel
/// changes (WPF MouseMove) or OLE's DragOver repeated every 50 ms while still, and the window's aim timer, on the real
/// catalogs (.mp4 .mov .gif .psd .svg .wav, a folder; Convert/Tools/Actions) plus big and paged synthetic wheels.
/// Both failure modes are checked: a variant that can't be reached, and browsing that feels sticky. The section
/// "found by the adversarial review" reproduces the problems that review of the first aim fix found (2026-11-01).
/// </summary>
public class AimAdversaryTests(ITestOutputHelper output)
{
    /// <summary>The longest a highlight may lag behind the pointer before browsing feels sticky.</summary>
    const double StickyMs = 300;

    // ------------------------------------------------------------------ pointer model

    /// <summary>One pointer event: offset from the wheel centre and the milliseconds since the previous event.</summary>
    internal readonly record struct Ev(Vector Point, double Dt);

    /// <summary>Marks an event that only lets time pass (click mode: WPF sends no MouseMove while the mouse is still).</summary>
    internal static readonly Vector NoMove = new(double.NaN, double.NaN);

    static double MinJerk(double t) => t * t * t * (10 - 15 * t + 6 * t * t);

    internal static Vector Snap(Vector p, double ppd) =>
        ppd <= 0 ? p : new Vector(Math.Round(p.X * ppd) / ppd, Math.Round(p.Y * ppd) / ppd);

    /// <summary>
    /// A movement along <paramref name="path"/> (u in 0..1) lasting <paramref name="ms"/> with a minimum-jerk speed
    /// profile (or constant speed), sampled every <paramref name="rate"/> ms like a mouse, snapped to the pixel grid;
    /// an event is sent only when the pixel changes, or every <paramref name="repeat"/> ms while still.
    /// </summary>
    internal static List<Ev> Move(Func<double, Vector> path, double ms, double ppd, double rate = 8, double repeat = 0,
        Func<double, Vector>? noise = null, bool constantSpeed = false)
    {
        var events = new List<Ev>();
        var last = Snap(path(0), ppd);
        var since = 0.0;
        var n = Math.Max(1, (int)Math.Ceiling(ms / rate));
        for (var i = 1; i <= n; i++)
        {
            var t = Math.Min(1, i * rate / ms);
            var p = path(constantSpeed ? t : MinJerk(t));
            if (noise is not null && i < n)
                p += noise(i * rate);
            var snapped = Snap(p, ppd);
            since += rate;
            if (snapped != last || (repeat > 0 && since >= repeat))
            {
                events.Add(new Ev(snapped, since));
                last = snapped;
                since = 0;
            }
        }
        return events;
    }

    /// <summary>The pointer stays put for <paramref name="ms"/>: no events (click mode) or OLE's DragOver repeats.</summary>
    internal static List<Ev> Rest(Vector at, double ms, double repeat)
    {
        if (repeat <= 0)
            return [new Ev(NoMove, ms)];
        var events = new List<Ev>();
        var t = 0.0;
        while (t + repeat <= ms)
        {
            events.Add(new Ev(at, repeat));
            t += repeat;
        }
        if (ms - t > 0)
            events.Add(new Ev(NoMove, ms - t));
        return events;
    }

    internal static Func<double, Vector> Line(Vector a, Vector b) => u => a + (b - a) * u;

    internal static Func<double, Vector> Bezier(Vector a, Vector c, Vector b) => u => (1 - u) * (1 - u) * a + 2 * u * (1 - u) * c + u * u * b;

    /// <summary>Quadratic curve bowed by <paramref name="bow"/> × chord, away from the centre when positive.</summary>
    internal static Func<double, Vector> Curve(Vector a, Vector b, double bow)
    {
        var chord = b - a;
        var mid = a + chord / 2;
        var normal = new Vector(-chord.Y, chord.X);
        normal.Normalize();
        if (Vector.Multiply(normal, mid) < 0)
            normal = -normal;
        return Bezier(a, mid + normal * bow * chord.Length, b);
    }

    /// <summary>Physiological hand tremor: a sinusoid of <paramref name="amplitude"/> DIPs at 8–12 Hz on each axis.</summary>
    internal static Func<double, Vector> Tremor(Random random, double amplitude)
    {
        var (fx, fy, px, py) = (8 + random.NextDouble() * 4, 8 + random.NextDouble() * 4, random.NextDouble() * 6.3, random.NextDouble() * 6.3);
        return t => new Vector(amplitude * Math.Sin(px + t / 1000 * fx * 2 * Math.PI), amplitude * Math.Sin(py + t / 1000 * fy * 2 * Math.PI));
    }

    /// <summary>
    /// Drives a session like the wheel window does: pointer events with their timing, and the window's aim timer
    /// (DispatcherTimer at 25 ms, which Windows rounds up to its ~15.6 ms tick: 31.25 ms) while a switch is pending.
    /// </summary>
    internal sealed class Driver(WheelSession session, FakeClock clock, double tickMs = 31.25)
    {
        double? _timerStart;
        int _ticks;

        public WheelSession Session => session;

        public double Now => clock.Now.TotalMilliseconds;

        public void Advance(double ms)
        {
            var end = Now + ms;
            while (_timerStart is { } start)
            {
                var next = start + (_ticks + 1) * tickMs;
                if (next > end)
                    break;
                _ticks++;
                clock.Now = TimeSpan.FromMilliseconds(next);
                session.Tick();
                if (!session.NeedsTick)
                    _timerStart = null;
            }
            clock.Now = TimeSpan.FromMilliseconds(end);
        }

        public void Move(Vector p, double dt)
        {
            Advance(dt);
            if (double.IsNaN(p.X))
                return;
            session.MovePointer(p);
            if (!session.NeedsTick)
            {
                _timerStart = null;
            }
            else if (_timerStart is null)
            {
                _timerStart = Now;
                _ticks = 0;
            }
        }

        public void Run(IEnumerable<Ev> events)
        {
            foreach (var e in events)
                Move(e.Point, e.Dt);
        }

        /// <summary>The pointer left the window's catch area (DragLeave / MouseLeave): the window keeps its timer running.</summary>
        public void Leave()
        {
            session.PointerLeft();
            if (!session.NeedsTick)
            {
                _timerStart = null;
            }
            else if (_timerStart is null)
            {
                _timerStart = Now;
                _ticks = 0;
            }
        }
    }

    // ------------------------------------------------------------------ wheels

    internal sealed record Wheel(string Name, WheelSession Session, FakeClock Clock)
    {
        public bool IsSynthetic => Name.StartsWith("synthetic", StringComparison.Ordinal);
    }

    static readonly Lazy<(OperationCatalog Catalog, string Source)> SharedCatalog = new(() =>
    {
        var services = HostServices.Create([], SettingsStore.InMemory(), new RecordingLog());
        if (RealCatalog.TryCreate(services.Engine, out var reason) is { } real)
            return (real, $"real engines ({real.All.Count} operations)");
        return (Fakes.SampleCatalog(), $"preview samples (real engines unavailable: {reason.Split('\n')[0]})");
    });

    /// <summary>Every wheel the user can open for the asked-for kinds, plus synthetic big/paged wheels; one entry per page.</summary>
    internal static List<Wheel> Wheels(TestWorkspace ws, Action<string> log, bool synthetic = true)
    {
        var (catalog, source) = SharedCatalog.Value;
        log($"Catalog: {source}");
        var folder = Path.Combine(ws.Root, "Footage");
        Directory.CreateDirectory(folder);
        ws.WriteText("Footage/a.mp4", "x");
        var specs = new List<(string Name, IReadOnlyList<InputFile> Files, WheelMode Mode, OperationCatalog Catalog)>();
        foreach (var (file, modes) in new (string, WheelMode[])[]
        {
            ("Festival loop.mp4", [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions]),
            ("Visual 02.mov", [WheelMode.Convert, WheelMode.Tools]),
            ("Sticker.gif", [WheelMode.Convert, WheelMode.Tools]),
            ("Poster artwork.psd", [WheelMode.Convert, WheelMode.Tools]),
            ("Logo.svg", [WheelMode.Convert]),
            ("Kick loop.wav", [WheelMode.Convert, WheelMode.Tools]),
        })
        {
            foreach (var mode in modes)
                specs.Add(($"{file} {mode}", Fakes.Files(ws, file), mode, catalog));
        }
        foreach (var mode in new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions })
            specs.Add(($"folder {mode}", [InputFile.FromPath(folder)], mode, catalog));
        if (synthetic)
        {
            specs.Add(("synthetic 16×10", Fakes.Files(ws, "clip.mp4"), WheelMode.Tools, Fakes.Groups(16, 10)));
            specs.Add(("synthetic 24 paged", Fakes.Files(ws, "clip.mp4"), WheelMode.Tools, Fakes.Groups(24, 10, 8, 3, 1, 4, 9)));
            specs.Add(("synthetic 5×8", Fakes.Files(ws, "clip.mp4"), WheelMode.Tools, Fakes.Groups(5, 8, 9, 10)));
        }

        var wheels = new List<Wheel>();
        foreach (var (name, files, mode, cat) in specs)
        {
            var model = WheelModel.Build(cat, files, mode, new RecordingLog());
            if (model.IsEmpty)
                continue;
            var pages = WheelSession.ForDisplay(model, 1.5).Pages.Count;
            for (var page = 0; page < pages; page++)
            {
                var clock = new FakeClock();
                var session = WheelSession.ForDisplay(model, 1.5, clock.Func);
                for (var p = 0; p < page; p++)
                    session.NextPage();
                var fans = Enumerable.Range(0, session.Segments.Count).Where(s => session.Segments[s].HasVariants)
                    .Select(s => $"{session.Segments[s].Label}({session.Segments[s].Options.Count}: {session.Layout.FanSpanOf(s)!.Value.Sweep:0}°)");
                var label = name + (pages > 1 ? $" p{page + 1}" : "");
                log($"{label}: {session.Segments.Count} segments; fans {string.Join(", ", fans)}");
                wheels.Add(new Wheel(label, session, clock));
            }
        }
        return wheels;
    }

    internal static Wheel RealWheel(TestWorkspace ws, string file, WheelMode mode)
    {
        var (catalog, _) = SharedCatalog.Value;
        var clock = new FakeClock();
        var model = WheelModel.Build(catalog, Fakes.Files(ws, file), mode, new RecordingLog());
        return new Wheel($"{file} {mode}", WheelSession.ForDisplay(model, 1.5, clock.Func), clock);
    }

    internal static int Find(WheelSession session, string group) => session.Segments.ToList().FindIndex(x => x.Label == group);

    internal static void ForEachVariant(WheelSession session, Action<int, int> action)
    {
        for (var s = 0; s < session.Segments.Count; s++)
        {
            if (!session.Segments[s].HasVariants)
                continue;
            for (var v = 0; v < session.Layout.VariantsOf(s).Count; v++)
                action(s, v);
        }
    }

    // ------------------------------------------------------------------ geometry helpers

    internal static double Delta(double from, double to) => Angles.Normalize(to - from + 180) - 180;

    /// <summary>A random point inside segment <paramref name="s"/> of the inner ring, at least <paramref name="edgeMargin"/> DIPs from its edges.</summary>
    internal static Vector PointInSegment(WheelLayout layout, int s, Random random, double edgeMargin = 2)
    {
        var m = layout.Metrics;
        var range = layout.Segments[s];
        var radius = m.InnerRingInnerRadius + 8 + random.NextDouble() * (m.InnerRingOuterRadius - m.InnerRingInnerRadius - 14);
        var marginDeg = Angles.ToDegrees(edgeMargin / radius);
        var angle = range.Start + marginDeg + random.NextDouble() * Math.Max(0, range.Sweep - 2 * marginDeg);
        return Angles.Direction(angle) * radius;
    }

    /// <summary>A random point well inside variant <paramref name="v"/> of segment <paramref name="s"/>.</summary>
    internal static Vector PointInVariant(WheelLayout layout, int s, int v, Random random)
    {
        var m = layout.Metrics;
        var range = layout.VariantsOf(s)[v];
        var radius = m.OuterRingInnerRadius + 8 + random.NextDouble() * (m.OuterRingOuterRadius - m.OuterRingInnerRadius - 16);
        var marginDeg = Math.Min(range.Sweep / 2 - 0.5, Angles.ToDegrees(4 / radius) + 1);
        var angle = range.Start + marginDeg + random.NextDouble() * Math.Max(0, range.Sweep - 2 * marginDeg);
        return Angles.Direction(angle) * radius;
    }

    /// <summary>A point <paramref name="inset"/> DIPs inside segment s's edge on the <paramref name="side"/> (+1 clockwise).</summary>
    internal static Vector NearEdge(WheelLayout layout, int s, int side, double inset, double radius)
    {
        var edge = side > 0 ? layout.Segments[s].End : layout.Segments[s].Start;
        return Angles.Direction(edge - side * Angles.ToDegrees(inset / radius)) * radius;
    }

    /// <summary>The pointer comes out of the centre straight onto <paramref name="at"/> (inside segment s) and rests.</summary>
    internal static bool Enter(Driver driver, int s, Vector at, double restMs, double ppd, double repeat)
    {
        var session = driver.Session;
        session.ClearHover();
        driver.Advance(800);
        driver.Move(new Vector(0, 0), 0);
        driver.Run(Move(Line(new Vector(0, 0), at), 160, ppd, repeat: repeat));
        if (restMs > 0)
            driver.Run(Rest(Snap(at, ppd), restMs, repeat));
        return session.Hover == WheelHit.OnSegment(s);
    }

    /// <summary>
    /// Plays <paramref name="events"/> after the fan of s is open, then rests 400 ms and settles (a drop/click).
    /// Returns null on success, else what went wrong. The fan must never fold on the way (crossing the centre is fine).
    /// </summary>
    internal static string? Play(Driver driver, int s, int v, List<Ev> events, double repeat, bool requireOpenFirst = true)
    {
        var session = driver.Session;
        var layout = session.Layout;
        var opened = !requireOpenFirst;
        var index = 0;
        Vector final = default;
        foreach (var e in events)
        {
            index++;
            driver.Move(e.Point, e.Dt);
            if (double.IsNaN(e.Point.X))
                continue;
            final = e.Point;
            opened |= session.ExpandedSegment == s;
            var hover = session.Hover;
            if (opened && (session.ExpandedSegment != s || (hover.Kind is WheelHitKind.Segment or WheelHitKind.Variant && hover.Segment != s)))
            {
                return $"folded at #{index}/{events.Count} ({Angles.Of(e.Point):0}°, r {e.Point.Length:0}) over {Describe(session, layout.HitTest(e.Point))}: " +
                    $"hover {Describe(session, hover)}";
            }
        }
        driver.Run(Rest(final, 400, repeat));
        session.Settle();
        return session.Hover == WheelHit.OnVariant(s, v) ? null : $"ended on {Describe(session, session.Hover)} (final {Angles.Of(final):0}° r {final.Length:0})";
    }

    // ------------------------------------------------------------------ what works: reachability from the label, both modes

    [Fact]
    public void Every_variant_is_reachable_from_the_label_with_smooth_human_movements()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, output.WriteLine);
            var failures = new List<string>();
            var runs = 0;
            foreach (var wheel in wheels)
            {
                var session = wheel.Session;
                var layout = session.Layout;
                ForEachVariant(session, (s, v) =>
                {
                    var start = layout.SegmentCentroid(s);
                    var target = layout.VariantCentroid(s, v);
                    foreach (var (ms, ppd, drag, rest, bow) in new[]
                    {
                        (150.0, 1.0, false, 0.0, 0.0), (350, 1.5, false, 250, 0), (700, 1, false, 0, 0), (350, 1.25, true, 0, 0),
                        (700, 1.5, true, 250, 0), (150, 1, true, 250, 0), (400, 1, false, 100, 0.15), (400, 1.5, true, 0, 0.15),
                    })
                    {
                        runs++;
                        var repeat = drag ? 50 : 0;
                        var driver = new Driver(session, wheel.Clock);
                        Assert.True(Enter(driver, s, start, rest, ppd, repeat));
                        var path = bow == 0 ? Line(start, target) : Curve(start, target, bow);
                        if (Play(driver, s, v, Move(path, ms, ppd, repeat: repeat), repeat) is { } problem)
                            failures.Add($"{wheel.Name} {(drag ? "drag" : "click")} {ms}ms @{ppd} rest {rest} bow {bow}: {Label(session, s, v)} {problem}");
                    }
                });
            }
            output.WriteLine($"{runs} movements, {failures.Count} failures");
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    // ------------------------------------------------------------------ what works: browsing is not sticky

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hopping_onto_a_neighbour_and_stopping_selects_it_within_300ms(bool drag)
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { });
            var random = new Random(99);
            var lags = new List<double>();
            var failures = new List<string>();
            foreach (var wheel in wheels)
            {
                var session = wheel.Session;
                var layout = session.Layout;
                var count = session.Segments.Count;
                for (var s = 0; s < count; s++)
                {
                    if (!session.Segments[s].HasVariants || count < 2)
                        continue;
                    foreach (var hop in new[] { 1, -1, 2, -2 })
                    {
                        var n = ((s + hop) % count + count) % count;
                        if (n == s)
                            continue;
                        for (var trial = 0; trial < 6; trial++)
                        {
                            var ppd = trial % 2 == 0 ? 1.0 : 1.5;
                            var driver = new Driver(session, wheel.Clock);
                            var start = trial < 2 ? layout.SegmentCentroid(s) : PointInSegment(layout, s, random);
                            var end = trial < 2 ? layout.SegmentCentroid(n) : PointInSegment(layout, n, random, 6);
                            if (!Enter(driver, s, start, random.NextDouble() * 300, ppd, drag ? 50 : 0))
                                continue;
                            var events = Move(trial == 5 ? Curve(start, end, 0.2) : Line(start, end), 150 + random.NextDouble() * 350, ppd, repeat: drag ? 50 : 0);
                            driver.Run(events);
                            var stoppedAt = driver.Now;
                            var final = events[^1].Point;
                            var expected = layout.HitTest(final);
                            while (session.Hover != expected && driver.Now - stoppedAt < 2000)
                            {
                                if (drag)
                                    driver.Move(final, 50);
                                else
                                    driver.Advance(5);
                            }
                            var lag = driver.Now - stoppedAt;
                            lags.Add(lag);
                            if (lag > StickyMs)
                                failures.Add($"{wheel.Name}: {session.Segments[s].Label} → {session.Segments[n].Label} took {lag:0} ms");
                        }
                    }
                }
            }
            lags.Sort();
            output.WriteLine($"{lags.Count} hops; instant {lags.Count(l => l == 0)}; median {lags[lags.Count / 2]:0} ms, p95 {lags[(int)(lags.Count * 0.95)]:0} ms, max {lags[^1]:0} ms");
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// Constant-speed sweeps along the inner ring from every fanned group, 2.5 segments either way. Each segment the
    /// pointer enters must be highlighted within <see cref="StickyMs"/> (or the pointer leaves it sooner).
    /// </summary>
    internal static List<string> Sweeps(IEnumerable<Wheel> wheels, double speed, double radiusShare, double ppd, Random? tremorRandom = null, double tremor = 0)
    {
        var stuck = new List<string>();
        foreach (var wheel in wheels)
        {
            var session = wheel.Session;
            var layout = session.Layout;
            var m = layout.Metrics;
            var radius = m.InnerRingInnerRadius + (m.InnerRingOuterRadius - m.InnerRingInnerRadius) * radiusShare;
            var count = session.Segments.Count;
            if (count < 3)
                continue;
            for (var s = 0; s < count; s++)
            {
                if (!session.Segments[s].HasVariants)
                    continue;
                foreach (var direction in new[] { +1, -1 })
                {
                    var driver = new Driver(session, wheel.Clock);
                    var from = layout.Segments[s].Mid;
                    if (!Enter(driver, s, Angles.Direction(from) * radius, 0, ppd, 0))
                        continue;
                    var to = from + direction * Math.Min(2.5, count - 1) * 360.0 / count;
                    var events = Move(u => Angles.Direction(from + (to - from) * u) * radius, Math.Abs(Angles.ToRadians(to - from)) * radius / speed * 1000, ppd,
                        noise: tremorRandom is null ? null : Tremor(tremorRandom, tremor), constantSpeed: true);
                    var current = s;
                    double? enteredAt = null;
                    var held = "";
                    void Lagged(string outcome)
                    {
                        if (enteredAt is { } at && driver.Now - at > StickyMs)
                        {
                            stuck.Add($"{wheel.Name}: sweep {direction:+0;-0} from {session.Segments[s].Label} at r {radius:0}: " +
                                $"{session.Segments[current].Label} {outcome} after {driver.Now - at:0} ms ({held} was held)");
                        }
                    }
                    foreach (var e in events)
                    {
                        driver.Move(e.Point, e.Dt);
                        var under = layout.HitTest(e.Point);
                        if (under.Kind != WheelHitKind.Segment)
                            continue;
                        if (under.Segment != current)
                        {
                            Lagged("was left unhighlighted");
                            current = under.Segment;
                            enteredAt = driver.Now;
                        }
                        if (enteredAt is not null)
                        {
                            if (session.Hover == under)
                            {
                                Lagged("got highlighted");
                                enteredAt = null;
                            }
                            else
                            {
                                held = Describe(session, session.Hover);
                            }
                        }
                    }
                    Lagged("was still not highlighted");
                }
            }
        }
        return stuck;
    }

    [Fact]
    public void Sweeping_the_ring_at_a_browsing_pace_is_followed_within_300ms()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { }, synthetic: false);
            var failures = new List<string>();
            foreach (var speed in new[] { 100.0, 200, 400, 800 })
            {
                foreach (var radiusShare in new[] { 0.12, 0.5, 0.9 })
                {
                    foreach (var ppd in new[] { 1.0, 1.5 })
                        failures.AddRange(Sweeps(wheels, speed, radiusShare, ppd).Select(f => $"{speed} DIP/s: {f}"));
                }
            }
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    // ------------------------------------------------------------------ found by the adversarial review

    /// <summary>
    /// The pointer rests on a group a few DIPs inside its edge (the user came in near the boundary), then moves off
    /// towards an off-axis variant: slowly at first (any movement from rest starts slowly), so the first samples over
    /// the neighbour can't tell where it is going. The hold has to give it time instead of switching at once. MP4 ›
    /// H.265, H.265 · no audio and H.264 · no audio were the user's report; GIF and PSD fans are checked too, and an
    /// 800 ms movement that takes over 200 ms to cover a judgeable stretch.
    /// </summary>
    [Fact]
    public void Starting_from_rest_just_inside_the_groups_edge_keeps_the_fan()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var failures = new List<string>();
            var paths = 0;
            foreach (var file in new[] { "Festival loop.mp4", "Sticker.gif", "Poster artwork.psd" })
            {
                var wheel = RealWheel(ws, file, WheelMode.Convert);
                var session = wheel.Session;
                var layout = session.Layout;
                var radius = (layout.Metrics.InnerRingInnerRadius + layout.Metrics.InnerRingOuterRadius) / 2;
                ForEachVariant(session, (s, v) =>
                {
                    var side = Math.Sign(Delta(layout.Segments[s].Mid, layout.VariantsOf(s)[v].Mid));
                    if (side == 0)
                        return;
                    foreach (var inset in new[] { 1.5, 2, 3, 4, 5 })
                    {
                        foreach (var ms in new[] { 400.0, 800 })
                        {
                            var driver = new Driver(session, wheel.Clock);
                            var start = Snap(NearEdge(layout, s, side, inset, radius), 1);
                            Assert.True(Enter(driver, s, start, 300, 1, 0));
                            paths++;
                            if (Play(driver, s, v, Move(Line(start, layout.VariantCentroid(s, v)), ms, 1), 0) is { } problem)
                                failures.Add($"{wheel.Name}: {Label(session, s, v)} from {inset} DIPs inside the edge in {ms} ms: {problem}");
                        }
                    }
                });
            }
            output.WriteLine($"{paths} paths");
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// A straight run from MP4's label at 250 and 500 DIPs/s with a 1-DIP, 8–12 Hz hand tremor (an 800-DPI mouse picks
    /// up ~0.03 mm per pixel). The path to an off-axis variant meets the outer band at a shallow angle, where a degree
    /// of heading swings the distance left by about two DIPs: the heading is fitted over a stretch, progress is measured
    /// from the middle of that stretch, and folding needs a whole stretch without progress.
    /// </summary>
    [Fact]
    public void A_one_dip_hand_tremor_keeps_the_fan()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheel = RealWheel(ws, "Festival loop.mp4", WheelMode.Convert);
            var session = wheel.Session;
            var layout = session.Layout;
            var mp4 = Find(session, "MP4");
            var failures = new List<string>();
            foreach (var speed in new[] { 250.0, 500 })
            {
                for (var v = 1; v < session.Segments[mp4].Options.Count; v++)
                {
                    for (var seed = 0; seed < 40; seed++)
                    {
                        var driver = new Driver(session, wheel.Clock);
                        var start = layout.SegmentCentroid(mp4);
                        var target = layout.VariantCentroid(mp4, v);
                        Assert.True(Enter(driver, mp4, start, 0, 1, 0));
                        var events = Move(Line(start, target), (target - start).Length / speed * 1000, 1, noise: Tremor(new Random(seed), 1), constantSpeed: true);
                        if (Play(driver, mp4, v, events, 0) is { } problem)
                            failures.Add($"{Label(session, mp4, v)} at {speed} DIPs/s, tremor seed {seed}: {problem}");
                    }
                }
            }
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// A slow, steady sweep along the ring (20–75 DIPs/s, a second or more per segment while reading labels) is
    /// followed within <see cref="StickyMs"/>: a hold that can't be judged lasts a settle delay, and a sweep is judged
    /// (and given way to) after the same stretch of path whatever its speed.
    /// </summary>
    [Fact]
    public void A_slow_sweep_along_the_ring_is_followed_within_300ms()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { }, synthetic: false);
            var failures = new List<string>();
            foreach (var speed in new[] { 20.0, 45, 55, 65, 75 })
                failures.AddRange(Sweeps(wheels, speed, 0.5, 1).Select(f => $"{speed} DIP/s: {f}"));
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// On the way to a variant the pointer slows to a stop over the neighbour, waits 120 ms (well under the 200 ms
    /// settle delay) and carries on, in click mode (no events while still). Slowing down and setting off again are
    /// too slow to judge, but they are movement, not rest: the hold lasts.
    /// </summary>
    [Fact]
    public void A_short_stop_over_a_neighbour_keeps_the_fan_in_click_mode()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { }, synthetic: false);
            var failures = new List<string>();
            foreach (var wheel in wheels)
            {
                var session = wheel.Session;
                var layout = session.Layout;
                ForEachVariant(session, (s, v) =>
                {
                    var start = layout.SegmentCentroid(s);
                    var target = layout.VariantCentroid(s, v);
                    var path = Line(start, target);
                    var at = -1.0;
                    for (var i = 1; i < 100 && at < 0; i++)
                    {
                        var hit = layout.HitTest(path(i / 100.0));
                        if (hit.Kind == WheelHitKind.Segment && hit.Segment != s)
                            at = Math.Min(0.95, i / 100.0 + 0.04);
                    }
                    if (at < 0)
                        return;
                    var driver = new Driver(session, wheel.Clock);
                    Assert.True(Enter(driver, s, start, 200, 1, 0));
                    var middle = path(at);
                    var length = (target - start).Length;
                    var events = Move(Line(start, middle), length * at / 400 * 1875, 1);
                    events.AddRange(Rest(Snap(middle, 1), 120, 0));
                    events.AddRange(Move(Line(middle, target), length * (1 - at) / 400 * 1875, 1));
                    if (Play(driver, s, v, events, 0) is { } problem)
                        failures.Add($"{wheel.Name}: {Label(session, s, v)}: {problem}");
                });
            }
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// Hopping from a fanned group onto a neighbour with a fan of its own and going straight out along its middle
    /// (at once or after a short pause) opens that neighbour's fan: its default variant right above it is chosen, not
    /// the first group's variant that the open fan shows there.
    /// </summary>
    [Fact]
    public void Hopping_onto_a_neighbour_and_going_straight_out_reaches_its_own_variant()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { }, synthetic: false);
            var failures = new List<string>();
            var runs = 0;
            foreach (var pause in new[] { 0.0, 50, 100, 150 })
            {
                foreach (var wheel in wheels)
                {
                    var session = wheel.Session;
                    var layout = session.Layout;
                    var count = session.Segments.Count;
                    for (var s = 0; s < count; s++)
                    {
                        if (!session.Segments[s].HasVariants || count < 2)
                            continue;
                        foreach (var hop in new[] { 1, -1 })
                        {
                            var n = ((s + hop) % count + count) % count;
                            if (!session.Segments[n].HasVariants)
                                continue;
                            var driver = new Driver(session, wheel.Clock);
                            Assert.True(Enter(driver, s, layout.SegmentCentroid(s), 200, 1, 0));
                            driver.Run(Move(Line(layout.SegmentCentroid(s), layout.SegmentCentroid(n)), 250, 1));
                            driver.Advance(pause);
                            driver.Run(Move(Line(layout.SegmentCentroid(n), layout.VariantCentroid(n, 0)), 250, 1));
                            driver.Advance(400);
                            runs++;
                            if (session.Hover != WheelHit.OnVariant(n, 0))
                            {
                                failures.Add($"{wheel.Name}: {session.Segments[s].Label} → {session.Segments[n].Label} › {session.Segments[n].Options[0].Label} " +
                                    $"after a {pause} ms pause ended on {Describe(session, session.Hover)}");
                            }
                        }
                    }
                }
            }
            output.WriteLine($"{runs} hops");
            Assert.True(runs > 0);
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// Overshooting past the painted catch area (the window gets DragLeave / MouseLeave) and coming straight back
    /// still lands on the variant: the fan stays for <see cref="WheelSession.LeaveGrace"/>. Without a return it folds.
    /// </summary>
    [Fact]
    public void Overshooting_past_the_catch_area_and_back_keeps_the_fan()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var failures = new List<string>();
            foreach (var mode in new[] { WheelMode.Convert, WheelMode.Tools })
            {
                var wheel = RealWheel(ws, "Festival loop.mp4", mode);
                var session = wheel.Session;
                var layout = session.Layout;
                var m = layout.Metrics;
                var leave = m.OuterRingOuterRadius + m.PointerSlack;
                var first = -1;
                ForEachVariant(session, (s, v) =>
                {
                    first = first < 0 ? s : first;
                    foreach (var beyond in new[] { 10.0, 35 })
                    {
                        var driver = new Driver(session, wheel.Clock);
                        var start = layout.SegmentCentroid(s);
                        var target = layout.VariantCentroid(s, v);
                        var far = Angles.Direction(Angles.Of(target)) * (leave + beyond);
                        Assert.True(Enter(driver, s, start, 100, 1, 0));
                        var outside = false;
                        foreach (var e in Move(Line(start, far), 300, 1).Concat(Move(Line(far, target), 200, 1)))
                        {
                            if (e.Point.Length > leave)
                            {
                                driver.Advance(e.Dt);
                                if (!outside)
                                    driver.Leave();
                                outside = true;
                                continue;
                            }
                            outside = false;
                            driver.Move(e.Point, e.Dt);
                        }
                        driver.Advance(400);
                        session.Settle();
                        if (session.Hover != WheelHit.OnVariant(s, v))
                            failures.Add($"{wheel.Name}: {Label(session, s, v)} {beyond} DIPs past the catch area ended on {Describe(session, session.Hover)}");
                    }
                });

                // Leaving for good: the fan folds once the grace has passed, and nothing is left to tick.
                Assert.True(first >= 0);
                var d = new Driver(session, wheel.Clock);
                Assert.True(Enter(d, first, layout.SegmentCentroid(first), 100, 1, 0));
                d.Leave();
                Assert.Equal(WheelHit.None, session.Hover);
                Assert.Equal(first, session.ExpandedSegment);
                Assert.True(session.NeedsTick);
                d.Advance(WheelSession.LeaveGrace.TotalMilliseconds + 40);
                Assert.Equal(-1, session.ExpandedSegment);
                Assert.False(session.NeedsTick);
            }
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    // ------------------------------------------------------------------ stress harness (CLEMENTINE_AIM_STRESS=1)

    public enum Style
    {
        Straight, CurvedOut, CurvedIn, FromCentre, NearBoundary, Jitter3, Jitter1, Tremor3, Hesitate80, Hesitate150,
        OvershootSlack, OvershootBeyond, Flick, Flick3Samples, Slow, SlowGappy, RestedLong,
    }

    public static TheoryData<Style> AllStyles() => new(Enum.GetValues<Style>());

    /// <summary>
    /// One human attempt at variant v of group s from a random rest point in the group (or out of the centre).
    /// Returns null when the generated path doesn't make sense for the style; otherwise null-or-problem.
    /// </summary>
    internal static (string? Problem, string Detail)? Attempt(Wheel wheel, int s, int v, Style style, bool dragMode, double ppd, Random random)
    {
        var session = wheel.Session;
        var layout = session.Layout;
        var m = layout.Metrics;
        var driver = new Driver(session, wheel.Clock);
        var repeat = dragMode ? 50 : 0;
        var target = PointInVariant(layout, s, v, random);
        if (layout.HitTest(Snap(target, ppd), s) != WheelHit.OnVariant(s, v))
            return null;
        var side = Math.Sign(Delta(layout.Segments[s].Mid, layout.VariantsOf(s)[v].Mid));

        Vector start;
        if (style == Style.NearBoundary)
        {
            if (side == 0)
                return null;
            start = NearEdge(layout, s, side, 1.5 + random.NextDouble() * 4.5, 105 + random.NextDouble() * 40);
        }
        else
        {
            start = PointInSegment(layout, s, random);
        }

        var events = new List<Ev>();
        var rest = style == Style.RestedLong ? 900 : random.NextDouble() < 0.5 ? 0 : 60 + random.NextDouble() * 300;
        if (style == Style.FromCentre)
        {
            // Out of the centre and on towards the variant in one curved movement through the group's segment.
            var through = Angles.Direction(layout.Segments[s].Start + layout.Segments[s].Sweep * (0.2 + 0.6 * random.NextDouble()))
                * (m.InnerRingInnerRadius + 15 + random.NextDouble() * 40);
            var path = Bezier(new Vector(0, 0), through, target);
            var inS = 0.0;
            var first = -1;
            var lastP = path(0);
            for (var i = 1; i <= 200; i++)
            {
                var p = path(i / 200.0);
                var hit = layout.HitTest(p);
                if (hit.Kind == WheelHitKind.Segment)
                {
                    if (first < 0)
                        first = hit.Segment;
                    if (hit.Segment == s)
                        inS += (p - lastP).Length;
                }
                lastP = p;
            }
            if (first != s || inS < 12)
                return null;
            session.ClearHover();
            driver.Advance(800);
            driver.Move(new Vector(0, 0), 0);
            events.AddRange(Move(path, 250 + random.NextDouble() * 350, ppd, repeat: repeat));
        }
        else
        {
            if (!Enter(driver, s, start, rest, ppd, repeat))
                return null;
            var duration = 200 + random.NextDouble() * 500;
            switch (style)
            {
                case Style.CurvedOut:
                    events.AddRange(Move(Curve(start, target, 0.08 + random.NextDouble() * 0.25), duration, ppd, repeat: repeat));
                    break;
                case Style.CurvedIn:
                    events.AddRange(Move(Curve(start, target, -(0.05 + random.NextDouble() * 0.15)), duration, ppd, repeat: repeat));
                    break;
                case Style.Jitter3 or Style.Jitter1:
                {
                    var amount = style == Style.Jitter3 ? 3 : 1;
                    events.AddRange(Move(Line(start, target), duration, ppd, repeat: repeat,
                        noise: _ => new Vector((random.NextDouble() * 2 - 1) * amount, (random.NextDouble() * 2 - 1) * amount)));
                    break;
                }
                case Style.Tremor3:
                    events.AddRange(Move(Line(start, target), duration, ppd, repeat: repeat, noise: Tremor(random, 3)));
                    break;
                case Style.Hesitate80 or Style.Hesitate150:
                {
                    var line = Line(start, target);
                    var at = 0.5;
                    for (var i = 1; i < 100; i++)
                    {
                        var hit = layout.HitTest(line(i / 100.0));
                        if (hit.Kind == WheelHitKind.Segment && hit.Segment != s)
                        {
                            at = i / 100.0;
                            break;
                        }
                    }
                    var middle = line(at);
                    events.AddRange(Move(Line(start, middle), duration * at, ppd, repeat: repeat));
                    events.AddRange(Rest(Snap(middle, ppd), style == Style.Hesitate80 ? 80 : 150, repeat));
                    events.AddRange(Move(Line(middle, target), duration * (1 - at), ppd, repeat: repeat));
                    break;
                }
                case Style.OvershootSlack or Style.OvershootBeyond:
                {
                    var beyondRadius = style == Style.OvershootSlack
                        ? m.OuterRingOuterRadius + m.PointerSlack - 2 - random.NextDouble() * 10
                        : m.OuterRingOuterRadius + m.PointerSlack + 8 + random.NextDouble() * 30;
                    var beyond = Angles.Direction(Angles.Of(target) + (random.NextDouble() * 2 - 1) * 4) * beyondRadius;
                    events.AddRange(Move(Line(start, beyond), duration * 0.75, ppd, repeat: repeat));
                    events.AddRange(Move(Line(beyond, target), 180, ppd, repeat: repeat));
                    break;
                }
                case Style.Flick:
                    events.AddRange(Move(Line(start, target), 60 + random.NextDouble() * 60, ppd, repeat: repeat));
                    break;
                case Style.Flick3Samples:
                {
                    var ms = 40 + random.NextDouble() * 40;
                    events.AddRange(Move(Line(start, target), ms, ppd, rate: ms / (3 + random.Next(2)), repeat: repeat));
                    break;
                }
                case Style.Slow:
                    events.AddRange(Move(Line(start, target), 1500 + random.NextDouble() * 1500, ppd, repeat: repeat));
                    break;
                case Style.SlowGappy:
                    events.AddRange(Move(Line(start, target), 700 + random.NextDouble() * 600, ppd, rate: 90 + random.NextDouble() * 70, repeat: 0));
                    break;
                default:
                    events.AddRange(Move(Line(start, target), duration, ppd, repeat: repeat));
                    break;
            }
        }

        // Beyond the painted catch area the window gets DragLeave / MouseLeave (WheelWindow → WheelSession.PointerLeft).
        var leave = m.OuterRingOuterRadius + m.PointerSlack;
        if (events.Any(e => e.Point.Length > leave))
        {
            var outside = false;
            foreach (var e in events)
            {
                if (e.Point.Length > leave)
                {
                    driver.Advance(e.Dt);
                    if (!outside)
                        driver.Leave();
                    outside = true;
                    continue;
                }
                outside = false;
                driver.Move(e.Point, e.Dt);
            }
            driver.Run(Rest(events[^1].Point, 400, repeat));
            session.Settle();
            var hover = session.Hover;
            return (hover == WheelHit.OnVariant(s, v) ? null : $"ended on {Describe(session, hover)}", $"{Label(session, s, v)} overshooting beyond the catch area");
        }
        var problem = Play(driver, s, v, events, repeat, requireOpenFirst: style == Style.FromCentre);
        return (problem, $"{Label(session, s, v)} from ({Angles.Of(start):0}°, r {start.Length:0})");
    }

    [AimStressTheory]
    [MemberData(nameof(AllStyles))]
    public void Stress_reachability(Style style)
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary");
            var wheels = Wheels(ws, _ => { });
            var random = new Random(4711 + (int)style);
            foreach (var drag in new[] { false, true })
            {
                var runs = 0;
                var failures = new List<string>();
                foreach (var wheel in wheels)
                {
                    ForEachVariant(wheel.Session, (s, v) =>
                    {
                        for (var trial = 0; trial < 4; trial++)
                        {
                            var ppd = new[] { 1.0, 1.25, 1.5, 2.0 }[random.Next(4)];
                            if (Attempt(wheel, s, v, style, drag, ppd, random) is not { } result)
                                continue;
                            runs++;
                            if (result.Problem is { } problem)
                                failures.Add($"{wheel.Name} @{ppd}: {result.Detail}: {problem}");
                        }
                    });
                }
                output.WriteLine($"{style} {(drag ? "drag" : "click")}: {runs} runs, {failures.Count} failed ({100.0 * failures.Count / Math.Max(1, runs):0.0}%)");
                foreach (var failure in failures.Take(8))
                    output.WriteLine($"    {failure}");
            }
        });
    }

    // ------------------------------------------------------------------ the real window, both modes

    static readonly ConstructorInfo DragArgsConstructor = typeof(DragEventArgs).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)])!;

    static readonly MethodInfo TrackMethod = typeof(WheelWindow).GetMethod("Track", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo CommitMethod = typeof(WheelWindow).GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Through the real window handlers (DragOver/Drop in drag mode; the mouse-move and click handlers' Track/Commit
    /// in click mode): a smooth diagonal from just below MP4's label to each of its variants converts to that variant.
    /// </summary>
    [Theory]
    [InlineData(WheelInteraction.Drag)]
    [InlineData(WheelInteraction.Click)]
    public void The_window_delivers_every_mp4_variant_along_a_diagonal_path(WheelInteraction interaction)
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary-window");
            var files = Fakes.Files(ws, "Festival loop.mp4");
            var (catalog, _) = SharedCatalog.Value;
            var model = WheelModel.Build(catalog, files, WheelMode.Convert, new RecordingLog());
            var clock = new FakeClock();
            var chosen = new List<string>();
            var window = new WheelWindow(interaction, new RecordingLog());
            window.Chosen += (option, _) => chosen.Add(option.Info.Id);
            try
            {
                var session = WheelSession.ForDisplay(model, 1.5, clock.Func);
                window.View.Show(session, interaction);
                window.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                window.View.Arrange(new Rect(window.View.DesiredSize));
                var data = new DataObject(DataFormats.FileDrop, files.Select(f => f.Path).ToArray());
                var layout = session.Layout;
                void Send(Vector offset, bool drop = false)
                {
                    if (interaction == WheelInteraction.Drag)
                    {
                        var args = (DragEventArgs)DragArgsConstructor.Invoke([data, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey,
                            DragDropEffects.Copy | DragDropEffects.Move, window.View, window.View.Centre + offset]);
                        args.RoutedEvent = drop ? DragDrop.DropEvent : DragDrop.DragOverEvent;
                        window.RaiseEvent(args);
                        Assert.NotEqual(DragDropEffects.Move, args.Effects & DragDropEffects.Move);
                    }
                    else
                    {
                        TrackMethod.Invoke(window, [window.View.Centre + offset]);
                        if (drop)
                            CommitMethod.Invoke(window, [new ScreenPoint(0, 0)]);
                    }
                }

                var mp4 = Find(session, "MP4");
                Assert.True(mp4 >= 0);
                var failures = new List<string>();
                for (var v = 0; v < session.Segments[mp4].Options.Count; v++)
                {
                    chosen.Clear();
                    session.ClearHover();
                    clock.Advance(800);
                    var start = layout.SegmentCentroid(mp4) + new Vector(0, 6);
                    var target = layout.VariantCentroid(mp4, v);
                    foreach (var e in Move(Line(new Vector(0, 0), start), 160, 1.5).Concat(Move(Line(start, target), 260, 1.5)))
                    {
                        clock.Advance(e.Dt);
                        Send(e.Point);
                    }
                    Send(target, drop: true);
                    var expected = session.Segments[mp4].Options[v];
                    if (chosen.Count != 1 || chosen[0] != expected.Info.Id)
                        failures.Add($"MP4 › {expected.Label}: chose [{string.Join(", ", chosen)}]");
                }
                Assert.True(failures.Count == 0, Report(failures));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A drag-mode window over the ".mp4 Convert" wheel on a fake clock, and a way to raise drag events on it.</summary>
    sealed class DragWindow : IDisposable
    {
        readonly DataObject _data;
        bool _closed;

        public DragWindow(TestWorkspace ws)
        {
            var files = Fakes.Files(ws, "Festival loop.mp4");
            var (catalog, _) = SharedCatalog.Value;
            Window = new WheelWindow(WheelInteraction.Drag, new RecordingLog());
            Window.Chosen += (option, _) => Chosen.Add(option.Info.Id);
            Window.Closed += (_, _) => _closed = true;
            Session = WheelSession.ForDisplay(WheelModel.Build(catalog, files, WheelMode.Convert, new RecordingLog()), 1.5, Clock.Func);
            Window.View.Show(Session, WheelInteraction.Drag);
            Window.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Window.View.Arrange(new Rect(Window.View.DesiredSize));
            _data = new DataObject(DataFormats.FileDrop, files.Select(f => f.Path).ToArray());
        }

        public WheelWindow Window { get; }

        public WheelSession Session { get; }

        public FakeClock Clock { get; } = new();

        public List<string> Chosen { get; } = [];

        public DragEventArgs Raise(RoutedEvent e, Vector offset)
        {
            var args = (DragEventArgs)DragArgsConstructor.Invoke([_data, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey,
                DragDropEffects.Copy | DragDropEffects.Move, Window.View, Window.View.Centre + offset]);
            args.RoutedEvent = e;
            Window.RaiseEvent(args);
            return args;
        }

        /// <summary>DragOver along a straight line, 8 DIPs every 10 ms; returns the last event's answer.</summary>
        public DragEventArgs DragAlong(Vector from, Vector to)
        {
            DragEventArgs? last = null;
            foreach (var point in PointerPaths.Line(from, to))
            {
                Clock.Advance(10);
                last = Raise(DragDrop.DragOverEvent, point);
            }
            return last!;
        }

        public bool TimerRunning(string name) =>
            ((System.Windows.Threading.DispatcherTimer)typeof(WheelWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!).IsEnabled;

        public void Dispose()
        {
            if (!_closed)
                Window.Close();
        }
    }

    /// <summary>
    /// OLE answers a release with Drop only if the last DragOver said Copy, and repeats no DragOver while the mouse
    /// rests. While the open fan is held over another group, a drop runs that group (it settles to what is under the
    /// pointer), so DragOver must already say Copy there, not None. Flicks from every fanned group of the ".mp4" wheel to
    /// every other group, through the centre too (ProRes → MOV), released at once.
    /// </summary>
    [Fact]
    public void The_drag_feedback_matches_what_a_drop_would_run_while_the_fan_is_held()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary-window");
            using var w = new DragWindow(ws);
            var session = w.Session;
            var layout = session.Layout;
            var failures = new List<string>();
            var held = 0;
            for (var s = 0; s < session.Segments.Count; s++)
            {
                if (!session.Segments[s].HasVariants)
                    continue;
                for (var n = 0; n < session.Segments.Count; n++)
                {
                    if (n == s || session.Segments[n].Group?.Default is not { } expected)
                        continue;
                    session.ClearHover();
                    w.Clock.Advance(800);
                    w.Raise(DragDrop.DragOverEvent, new Vector(0, 0));
                    w.DragAlong(new Vector(0, 0), layout.SegmentCentroid(s));
                    var answer = w.DragAlong(layout.SegmentCentroid(s), layout.SegmentCentroid(n)).Effects;
                    held += session.IsSwitchPending ? 1 : 0;
                    var flick = $"{session.Segments[s].Label} → {session.Segments[n].Label}";
                    if (answer != (expected.IsAvailable ? DragDropEffects.Copy : DragDropEffects.None))
                        failures.Add($"{flick}: the last DragOver said {answer} (pending {session.IsSwitchPending}, hover {Describe(session, session.Hover)})");
                    w.Chosen.Clear();
                    var drop = w.Raise(DragDrop.DropEvent, layout.SegmentCentroid(n));
                    if (expected.IsAvailable && (drop.Effects != DragDropEffects.Copy || w.Chosen.Count != 1 || w.Chosen[0] != expected.Info.Id))
                        failures.Add($"{flick}: the drop said {drop.Effects} and chose [{string.Join(", ", w.Chosen)}], not {expected.Info.Id}");
                }
            }
            output.WriteLine($"{held} flicks were still held at the release");
            Assert.True(held > 0, "Some flicks should end while the first group's fan is still held.");
            Assert.True(failures.Count == 0, Report(failures));
        });
    }

    /// <summary>
    /// DragLeave keeps the open fan for <see cref="WheelSession.LeaveGrace"/> (the aim timer folds it afterwards), a
    /// DragOver back on the fan picks the variant up again, and closing the window outright (as WheelHost.Stop does)
    /// stops every timer instead of leaving the aim timer ticking on a closed window.
    /// </summary>
    [Fact]
    public void Leaving_the_drag_window_keeps_the_fan_a_moment_and_closing_it_stops_every_timer()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("aim-adversary-window");
            using var w = new DragWindow(ws);
            var session = w.Session;
            var layout = session.Layout;
            var mp4 = Find(session, "MP4");
            var variant = layout.VariantCentroid(mp4, 1);
            w.Raise(DragDrop.DragOverEvent, new Vector(0, 0));
            w.DragAlong(new Vector(0, 0), layout.SegmentCentroid(mp4));
            w.DragAlong(layout.SegmentCentroid(mp4), variant);
            Assert.Equal(WheelHit.OnVariant(mp4, 1), session.Hover);

            // Out past the catch area and straight back.
            w.Raise(DragDrop.DragLeaveEvent, variant * 1.5);
            Assert.Equal(WheelHit.None, session.Hover);
            Assert.Equal(mp4, session.ExpandedSegment);
            Assert.True(w.TimerRunning("_aim"), "The aim timer should run to fold the fan if the pointer doesn't come back.");
            w.Clock.Advance(150);
            var effect = session.Segments[mp4].Options[1].IsAvailable ? DragDropEffects.Copy : DragDropEffects.None;
            Assert.Equal(effect, w.Raise(DragDrop.DragOverEvent, variant).Effects);
            Assert.Equal(WheelHit.OnVariant(mp4, 1), session.Hover);
            Assert.False(w.TimerRunning("_aim"));

            // Out for good: the timer folds the fan once the grace has passed, then stops.
            w.Raise(DragDrop.DragLeaveEvent, variant * 1.5);
            w.Clock.Advance(WheelSession.LeaveGrace.TotalMilliseconds + 1);
            AimTests.PumpFor(TimeSpan.FromMilliseconds(120));
            Assert.Equal(-1, session.ExpandedSegment);
            Assert.False(w.TimerRunning("_aim"));

            // Hold MP4's fan over its neighbour, then close the window outright.
            w.Raise(DragDrop.DragOverEvent, new Vector(0, 0));
            w.DragAlong(new Vector(0, 0), layout.SegmentCentroid(mp4));
            w.DragAlong(layout.SegmentCentroid(mp4), layout.SegmentCentroid(session.Segments.Count - 1));
            Assert.True(session.IsSwitchPending);
            Assert.True(w.TimerRunning("_aim"));
            w.Window.Close();
            foreach (var timer in new[] { "_aim", "_dwell", "_watchdog" })
                Assert.False(w.TimerRunning(timer), $"{timer} still runs after Close.");
        });
    }

    // ------------------------------------------------------------------ text

    internal static string Label(WheelSession session, int s, int v) => $"{session.Segments[s].Label} › {session.Segments[s].Options[v].Label}";

    internal static string Describe(WheelSession session, WheelHit hit) => hit.Kind switch
    {
        WheelHitKind.Segment => session.Segments[hit.Segment].Label,
        WheelHitKind.Variant => Label(session, hit.Segment, hit.Variant),
        _ => hit.Kind.ToString(),
    };

    internal static string Report(List<string> failures)
    {
        var text = new StringBuilder($"{failures.Count} problem(s):");
        foreach (var failure in failures.Take(40))
            text.AppendLine().Append("  ").Append(failure);
        return text.ToString();
    }
}
