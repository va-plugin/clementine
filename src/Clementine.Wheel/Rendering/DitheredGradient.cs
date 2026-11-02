using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clementine.Wheel;

/// <summary>
/// Draws the theme's gradient brushes without banding. WPF quantises a gradient straight to 8 bits per channel, so a
/// subtle dark ramp (the centre's radial glass: 22 levels over 160 DIPs) shows as visible rings. Here the brush is
/// evaluated per device pixel in floating point, an ordered-dither threshold is added before quantising, and the result
/// is drawn as a bitmap exactly on the device pixel grid, clipped to the shape. Linear and radial brushes mapped
/// relative to the bounding box with Pad spread (what the theme uses) are supported; anything else is drawn as is.
/// </summary>
internal static class DitheredGradient
{
    /// <summary>8×8 Bayer matrix: thresholds 0..63, used as (n + 0.5) / 64.</summary>
    static readonly byte[] Bayer =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    const int CacheLimit = 96;

    /// <summary>Entries of the offset → colour table (a step changes a 255-level ramp by 0.06 levels at most).</summary>
    const int TableSize = 4096;

    /// <summary>
    /// Rendered fills, shared by every view (the warm-up render at start-up fills it for the first real wheel) and kept
    /// across opens: the keys are frozen theme brushes, so the same wheel never renders a fill twice.
    /// </summary>
    static readonly Dictionary<(Brush Brush, long X, long Y, long W, long H, long Ppd), (BitmapSource Bitmap, Rect Rect)> Cache = [];

    [ThreadStatic]
    static int _rendered;

    /// <summary>How many fills this thread has rendered (not taken from the cache) so far.</summary>
    internal static int RenderedCount => _rendered;

    /// <summary>Tests switch dithering off on their own thread to render the plain WPF gradients for comparison.</summary>
    [ThreadStatic]
    internal static bool DisabledOnThisThread;

    /// <summary>Whether <paramref name="brush"/> is a gradient this class can dither.</summary>
    public static bool Supports(Brush brush) =>
        !DisabledOnThisThread
        && brush is GradientBrush { MappingMode: BrushMappingMode.RelativeToBoundingBox, SpreadMethod: GradientSpreadMethod.Pad } gradient
        && gradient.GradientStops.Count > 0
        && IsIdentity(gradient.Transform) && IsIdentity(gradient.RelativeTransform)
        && brush is LinearGradientBrush or RadialGradientBrush;

    /// <summary>Fills <paramref name="geometry"/> with <paramref name="brush"/>, dithered for a display at <paramref name="pixelsPerDip"/>.</summary>
    /// <param name="origin">Where this element's (0, 0) lies on the device pixel grid, in device pixels (fractional part only matters).</param>
    public static void Fill(DrawingContext dc, Brush brush, Geometry geometry, double pixelsPerDip, Vector origin = default)
    {
        var bounds = geometry.Bounds;
        if (!Supports(brush) || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1 || !(pixelsPerDip > 0) || !double.IsFinite(pixelsPerDip))
        {
            dc.DrawGeometry(brush, null, geometry);
            return;
        }
        var (bitmap, rect) = Get(brush, bounds, pixelsPerDip, origin);
        dc.PushClip(geometry);
        dc.DrawImage(bitmap, rect);
        dc.Pop();
    }

    static (BitmapSource Bitmap, Rect Rect) Get(Brush brush, Rect bounds, double ppd, Vector origin)
    {
        static long Q(double value) => (long)Math.Round(value * 1000);
        var key = (brush, Q(bounds.X + origin.X / ppd), Q(bounds.Y + origin.Y / ppd), Q(bounds.Width), Q(bounds.Height), Q(ppd));
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
        }
        // Unfrozen brushes can only be read on their own thread; the theme's are frozen.
        var made = Render((GradientBrush)brush, bounds, ppd, origin);
        _rendered++;
        lock (Cache)
        {
            if (Cache.Count >= CacheLimit)
                Cache.Clear();
            Cache[key] = made;
        }
        return made;
    }

    /// <summary>Renders the brush over <paramref name="bounds"/> (DIPs) onto whole device pixels covering it.</summary>
    internal static (BitmapSource Bitmap, Rect Rect) Render(GradientBrush brush, Rect bounds, double ppd, Vector origin = default)
    {
        // Device pixel columns/rows that touch the bounds.
        var left = (int)Math.Floor(bounds.Left * ppd + origin.X);
        var top = (int)Math.Floor(bounds.Top * ppd + origin.Y);
        var right = (int)Math.Ceiling(bounds.Right * ppd + origin.X);
        var bottom = (int)Math.Ceiling(bounds.Bottom * ppd + origin.Y);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        // The colour depends only on the offset along the gradient: a fine table of it keeps the per-pixel work to the
        // geometry (well below a hundredth of a level off).
        var stops = brush.GradientStops.OrderBy(s => s.Offset).Select(s => (s.Offset, Colour: s.Color)).ToArray();
        var opacity = Math.Clamp(brush.Opacity, 0, 1);
        var table = new float[(TableSize + 1) * 4];
        for (var k = 0; k <= TableSize; k++)
        {
            var (a, r, g, b) = ColourAt(stops, (double)k / TableSize);
            table[k * 4] = (float)(a * opacity * 255);
            table[k * 4 + 1] = (float)r;
            table[k * 4 + 2] = (float)g;
            table[k * 4 + 3] = (float)b;
        }

        var offsets = brush is RadialGradientBrush radial ? Radial.From(radial) : (Radial?)null;
        var linear = brush as LinearGradientBrush;
        var (lsx, lsy, lax, lay, lal) = linear is null
            ? (0.0, 0.0, 0.0, 0.0, 0.0)
            : (linear.StartPoint.X, linear.StartPoint.Y, linear.EndPoint.X - linear.StartPoint.X, linear.EndPoint.Y - linear.StartPoint.Y,
                (linear.EndPoint - linear.StartPoint).LengthSquared);

        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            // Pixel centres in DIPs, then relative to the bounding box.
            var v = ((top + y + 0.5 - origin.Y) / ppd - bounds.Top) / bounds.Height;
            var row = ((top + y) & 7) * 8;
            for (var x = 0; x < width; x++)
            {
                var u = ((left + x + 0.5 - origin.X) / ppd - bounds.Left) / bounds.Width;
                var t = offsets is { } o ? o.At(u, v) : lal > 1e-12 ? ((u - lsx) * lax + (v - lsy) * lay) / lal : 0;
                var k = (int)(Math.Clamp(t, 0, 1) * TableSize + 0.5) * 4;
                var threshold = (Bayer[row + ((left + x) & 7)] + 0.5) / 64;
                var alpha = Quantise(table[k], threshold);
                var i = (y * width + x) * 4;
                // Premultiplied BGRA; colour dithered with the same threshold so the grain stays neutral.
                pixels[i] = Quantise(table[k + 3] * alpha, threshold);
                pixels[i + 1] = Quantise(table[k + 2] * alpha, threshold);
                pixels[i + 2] = Quantise(table[k + 1] * alpha, threshold);
                pixels[i + 3] = alpha;
            }
        });

        var bitmap = BitmapSource.Create(width, height, 96 * ppd, 96 * ppd, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var rect = new Rect((left - origin.X) / ppd, (top - origin.Y) / ppd, width / ppd, height / ppd);
        return (bitmap, rect);
    }

    static byte Quantise(double value, double threshold) => (byte)Math.Clamp(Math.Floor(value + threshold), 0, 255);

    /// <summary>Straight (non-premultiplied) colour at <paramref name="t"/>: alpha 0..1, channels 0..1.</summary>
    static (double A, double R, double G, double B) ColourAt((double Offset, Color Colour)[] stops, double t)
    {
        if (t <= stops[0].Offset)
            return Unit(stops[0].Colour);
        for (var i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].Offset)
            {
                var (o0, c0) = stops[i - 1];
                var (o1, c1) = stops[i];
                var f = o1 > o0 ? (t - o0) / (o1 - o0) : 1;
                var a = Unit(c0);
                var b = Unit(c1);
                return (a.A + (b.A - a.A) * f, a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f);
            }
        }
        return Unit(stops[^1].Colour);
    }

    static (double A, double R, double G, double B) Unit(Color c) => (c.A / 255.0, c.R / 255.0, c.G / 255.0, c.B / 255.0);

    /// <summary>
    /// Offset of a focal radial gradient (relative coordinates): the circles run from the gradient origin (offset 0) to
    /// the ellipse around Center with RadiusX/RadiusY (offset 1), their centres moving linearly between the two.
    /// </summary>
    readonly record struct Radial(double Rx, double Ry, double Ox, double Oy, double Dx, double Dy, double Dd, double Cx, double Cy)
    {
        public static Radial From(RadialGradientBrush brush)
        {
            var rx = Math.Max(1e-9, brush.RadiusX);
            var ry = Math.Max(1e-9, brush.RadiusY);
            var ox = brush.GradientOrigin.X / rx;
            var oy = brush.GradientOrigin.Y / ry;
            var dx = brush.Center.X / rx - ox;
            var dy = brush.Center.Y / ry - oy;
            return new Radial(rx, ry, ox, oy, dx, dy, dx * dx + dy * dy, brush.Center.X / rx, brush.Center.Y / ry);
        }

        public double At(double u, double v)
        {
            if (Dd >= 0.999)
            {
                // Focus on or outside the ellipse (never in the theme): plain distance from the centre instead.
                var ex = u / Rx - Cx;
                var ey = v / Ry - Cy;
                return Math.Sqrt(ex * ex + ey * ey);
            }
            // |Q − t·D| = t, with Q the point relative to the origin in ellipse-normalised units.
            var qx = u / Rx - Ox;
            var qy = v / Ry - Oy;
            var qd = qx * Dx + qy * Dy;
            var qq = qx * qx + qy * qy;
            return (-qd + Math.Sqrt(qd * qd + (1 - Dd) * qq)) / (1 - Dd);
        }
    }

    static bool IsIdentity(Transform? transform) => transform is null || transform.Value.IsIdentity;
}
