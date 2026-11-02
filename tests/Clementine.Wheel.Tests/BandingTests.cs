using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clementine.Core.Hosting;
using Clementine.TestSupport;
using Xunit.Abstractions;

namespace Clementine.Wheel.Tests;

/// <summary>
/// Gradient banding in the wheel's own drawing, measured on rendered PNGs: inside a region, rows and columns are split
/// into runs of identical pixels, and a <i>band edge</i> is a step of 1–3 levels between two areas that are flat for at
/// least <see cref="MinRun"/> px both along and across the scan line, lying on a contour (it touches another such edge).
/// A banded 8-bit ramp is made of exactly such steps; a flat fill has none, a dithered ramp only short runs or isolated
/// steps, a steep ramp (a soft shadow's edge) no wide flat areas, and text or strokes step by far more than 3 levels.
/// </summary>
public class BandingTests(ITestOutputHelper output)
{
    const int MinRun = 5;

    /// <summary>A plain dark desktop behind the glass, so only the wheel's own gradients can band.</summary>
    static readonly Color Desktop = Color.FromRgb(0x2B, 0x30, 0x3A);

    internal sealed record BandStats(int Pixels, int Lines, int StepEdges, int BandEdges, int LongestBandRun)
    {
        public override string ToString() =>
            $"{BandEdges} band edges on contours ({StepEdges} 1–3 level steps in all) over {Lines} lines ({Pixels} px), longest flat run at a step {LongestBandRun} px";
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(1.5)]
    [InlineData(1.0)]
    public void The_wheel_draws_its_gradients_without_banding(double scale)
    {
        var directory = Fakes.ScratchDirectory("banding");
        Sta.Run(() =>
        {
            // The same states drawn with WPF's own 8-bit gradients, then dithered: the first must band (or the
            // measurement proves nothing), the second must not.
            var plain = Regions(directory, scale, dithered: false);
            var dithered = Regions(directory, scale, dithered: true);
            foreach (var (name, before) in plain)
            {
                output.WriteLine($"@{scale}x {name,-14} plain WPF: {before}");
                output.WriteLine($"@{scale}x {name,-14} dithered:  {dithered[name]}");
            }
            Assert.True(plain.Values.Sum(r => r.BandEdges) > 500,
                "The plain gradients should band, or the measurement is broken: " + string.Join("; ", plain.Select(r => $"{r.Key}: {r.Value}")));
            var banded = dithered.Where(r => r.Value.BandEdges > 4).ToList();
            Assert.True(banded.Count == 0, $"Banding @{scale}x: " + string.Join("; ", banded.Select(r => $"{r.Key}: {r.Value}")));
        });
    }

    [Fact]
    public void Dithered_fills_are_quick_to_make_and_reused_across_opens()
    {
        Sta.Run(() =>
        {
            // The largest fill the wheel makes: the disc's shadow (radius 187 DIPs) on a 200 % display.
            var brush = new RadialGradientBrush(Color.FromArgb(0x80, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
            brush.Freeze();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            DitheredGradient.Render(brush, new Rect(0, 0, 40, 40), 1); // JIT
            var cold = clock.Elapsed.TotalMilliseconds;
            // Best of three, so a busy test machine doesn't decide the result.
            var best = double.MaxValue;
            var shadow = (Bitmap: (BitmapSource?)null, Rect: Rect.Empty);
            for (var i = 0; i < 3; i++)
            {
                clock.Restart();
                shadow = DitheredGradient.Render(brush, new Rect(0, 0, 374, 374), 2);
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }
            Assert.NotNull(shadow.Bitmap);
            output.WriteLine($"First (tiny) fill {cold:F1} ms; shadow fill {shadow.Bitmap.PixelWidth}×{shadow.Bitmap.PixelHeight} px in {best:F1} ms (best of 3)");
            Assert.Equal(748, shadow.Bitmap.PixelWidth);
            Assert.Equal(new Rect(0, 0, 374, 374), shadow.Rect);
            // Typically ~25 ms; the generous limit only catches real regressions, not a busy machine during a full test run.
            Assert.True(best < 150, $"Rendering the shadow fill took {best:F1} ms.");

            // Opening the same wheel again reuses every fill.
            using var ws = new TestWorkspace("banding");
            var session = Fakes.Session(Fakes.SampleCatalog(), Fakes.Files(ws, "Festival loop.mp4"), WheelMode.Convert);
            session.Highlight(WheelHit.OnSegment(0));
            var view = new WheelView { PixelsPerDipOverride = 1.25 };
            void Draw()
            {
                view.Show(session, WheelInteraction.Drag);
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                var target = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                target.Render(view);
            }
            Draw();
            var before = DitheredGradient.RenderedCount;
            Draw();
            new WheelView { PixelsPerDipOverride = 1.25 }.Show(session, WheelInteraction.Drag);
            Draw();
            Assert.Equal(before, DitheredGradient.RenderedCount);
        });
    }

    /// <summary>Renders the video wheel (ProRes hovered) and a one-group wheel (whole ring lit) and measures each gradient area.</summary>
    Dictionary<string, BandStats> Regions(string directory, double scale, bool dithered)
    {
        DitheredGradient.DisabledOnThisThread = !dithered;
        try
        {
            using var ws = new TestWorkspace("banding");
            var suffix = $"{scale}x-{(dithered ? "dithered" : "plain")}";
            var video = Fakes.Session(Fakes.SampleCatalog(), Fakes.Files(ws, "Festival loop.mp4"), WheelMode.Convert);
            var prores = video.Segments.ToList().FindIndex(s => s.Label == "ProRes");
            video.Highlight(WheelHit.OnSegment(prores));
            var videoPng = Path.Combine(directory, $"banding-video-{suffix}.png");
            WheelPreviews.RenderSession(video, WheelInteraction.Drag, null, false, videoPng, scale, Desktop);

            // One group: its hovered segment is the whole inner ring, the biggest accent gradient the wheel draws.
            var single = Fakes.Session(Fakes.Groups(1, 3), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools);
            single.Highlight(WheelHit.OnSegment(0));
            var singlePng = Path.Combine(directory, $"banding-single-{suffix}.png");
            WheelPreviews.RenderSession(single, WheelInteraction.Drag, null, false, singlePng, scale, Desktop);

            var m = video.Layout.Metrics;
            var sm = single.Layout.Metrics;
            return new Dictionary<string, BandStats>
            {
                ["centre disc"] = Measure(videoPng, m, scale, (r, _) => r < m.CentreRadius - 3),
                ["glass ring"] = Measure(videoPng, m, scale, (r, a) => r > m.InnerRingInnerRadius + 2 && r < m.DiscRadius - 2 && !InSegment(video.Layout, prores, a)),
                ["hovered group"] = Measure(videoPng, m, scale, (r, a) => r > m.InnerRingInnerRadius + 4 && r < m.InnerRingOuterRadius - 4 && InSegment(video.Layout, prores, a, inset: 3)),
                ["whole ring lit"] = Measure(singlePng, sm, scale, (r, _) => r > sm.InnerRingInnerRadius + 4 && r < sm.InnerRingOuterRadius - 4),
            };
        }
        finally
        {
            DitheredGradient.DisabledOnThisThread = false;
        }
    }

    static bool InSegment(WheelLayout layout, int segment, double angle, double inset = 0)
    {
        var range = layout.Segments[segment];
        return Angles.Normalize(angle - range.Start - inset) < range.Sweep - 2 * inset;
    }

    /// <summary>Counts band edges along every row and column of the region <paramref name="inside"/>(radius, angle) in DIPs from the wheel centre.</summary>
    internal static BandStats Measure(string png, WheelMetrics metrics, double scale, Func<double, double, bool> inside)
    {
        var frame = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);

        // The PNG is the wheel plus a margin, centred.
        var centre = width / 2.0;
        var mask = new bool[width * height];
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = new Vector((x + 0.5 - centre) / scale, (y + 0.5 - centre) / scale);
                if (inside(offset.Length, Angles.Of(offset)))
                {
                    mask[y * width + x] = true;
                    count++;
                }
            }
        }

        // Length of the flat run through each pixel, across rows and down columns. A band is flat both ways: a steep
        // ramp whose contours merely run along the scan line (a soft shadow's edge near the top of a circle) is not.
        var across = new int[width * height];
        var down = new int[width * height];
        void RunLengths(int[] into, Func<int, int> index, int length)
        {
            var start = 0;
            for (var i = 1; i <= length; i++)
            {
                if (i < length && mask[index(i)] && mask[index(i - 1)] && pixels[index(i)] == pixels[index(i - 1)])
                    continue;
                for (var j = start; j < i; j++)
                    into[index(j)] = i - start;
                start = i;
            }
        }
        for (var y = 0; y < height; y++)
            RunLengths(across, x => y * width + x, width);
        for (var x = 0; x < width; x++)
            RunLengths(down, y => y * width + x, height);

        int lines = 0, edges = 0, longest = 0;
        var marks = new List<int>();
        void Scan(Func<int, int> index, int length, int[] perpendicular)
        {
            var runs = new List<(uint Colour, int Length, int Start)>();
            void Flush()
            {
                if (runs.Count > 0)
                    lines++;
                for (var i = 1; i < runs.Count; i++)
                {
                    var (a, la, _) = runs[i - 1];
                    var (b, lb, start) = runs[i];
                    var step = MaxChannelDifference(a, b);
                    if (la >= MinRun && lb >= MinRun && step is >= 1 and <= 3
                        && perpendicular[index(start - 1)] >= MinRun && perpendicular[index(start)] >= MinRun)
                    {
                        edges++;
                        longest = Math.Max(longest, Math.Max(la, lb));
                        marks.Add(index(start));
                    }
                }
                runs.Clear();
            }
            for (var i = 0; i < length; i++)
            {
                var k = index(i);
                if (!mask[k])
                {
                    Flush();
                    continue;
                }
                if (runs.Count > 0 && runs[^1].Colour == pixels[k])
                    runs[^1] = (pixels[k], runs[^1].Length + 1, runs[^1].Start);
                else
                    runs.Add((pixels[k], 1, i));
            }
            Flush();
        }
        var contourMarks = new List<int>();
        var allMarks = new List<int>();
        // A band is a contour line: its edges touch the edges found on the neighbouring rows/columns. An ordered dither
        // leaves only isolated steps (one per 8×8 tile at most), which nobody sees as a band.
        int Contour(Action scans)
        {
            marks.Clear();
            scans();
            var map = new HashSet<int>(marks);
            var joined = 0;
            foreach (var k in map)
            {
                int x = k % width, y = k / width;
                var neighbours = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if ((dx != 0 || dy != 0) && x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height && map.Contains(k + dy * width + dx))
                            neighbours++;
                    }
                }
                if (neighbours > 0)
                {
                    joined++;
                    contourMarks.Add(k);
                }
            }
            return joined;
        }
        var contour = Contour(() =>
        {
            for (var y = 0; y < height; y++)
                Scan(x => y * width + x, width, down);
            allMarks.AddRange(marks);
        });
        contour += Contour(() =>
        {
            for (var x = 0; x < width; x++)
                Scan(y => y * width + x, height, across);
            allMarks.AddRange(marks);
        });

        // Next to the PNG: the measured region dimmed, isolated steps in yellow and contour (band) edges in red.
        var marked = (uint[])pixels.Clone();
        for (var i = 0; i < marked.Length; i++)
        {
            if (!mask[i])
                marked[i] = 0xFF000000 | ((marked[i] >> 2) & 0x003F3F3F);
        }
        foreach (var k in allMarks)
            marked[k] = 0xFFFFD020;
        foreach (var k in contourMarks)
            marked[k] = 0xFFFF2020;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, marked, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.ChangeExtension(png, null) + $"-edges-{count}.png"))
            encoder.Save(stream);
        return new BandStats(count, lines, edges, contour, longest);
    }

    static int MaxChannelDifference(uint a, uint b)
    {
        var max = 0;
        for (var shift = 0; shift < 32; shift += 8)
            max = Math.Max(max, Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)));
        return max;
    }
}
