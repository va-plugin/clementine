using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clementine.App.Theme;

/// <summary>
/// A linear gradient rendered into a bitmap with dithering. WPF fills gradients with 8-bit colour and no dithering,
/// so a large, dark, slow gradient (the window background moves only ~16 levels across the whole window) shows as
/// visible steps. Here the gradient is computed at full precision and each pixel gets a sub-level threshold from
/// interleaved gradient noise before it is rounded: the average colour is exact, the noise is far below what the eye
/// can see (at most one level), and there are no flat bands with hard edges between them.
/// </summary>
public sealed class DitheredGradient
{
    /// <summary>Larger renders are made at a reduced resolution and stretched (memory guard for maximised 4K windows).</summary>
    public const int MaxPixels = 4_200_000;

    const int LutSize = 4096;
    const int Tile = 64;
    static readonly int[] Thresholds = CreateThresholds();

    readonly (double Offset, double R, double G, double B)[] _stops;
    readonly int[] _lutR = new int[LutSize];
    readonly int[] _lutG = new int[LutSize];
    readonly int[] _lutB = new int[LutSize];

    /// <param name="startPoint">Start of the gradient axis, relative to the painted area (0..1), like <see cref="LinearGradientBrush.StartPoint"/>.</param>
    /// <param name="endPoint">End of the gradient axis, relative to the painted area.</param>
    /// <param name="stops">Colour stops (offset 0..1). Interpolated in sRGB like WPF's default.</param>
    public DitheredGradient(Point startPoint, Point endPoint, IEnumerable<(double Offset, Color Color)> stops)
    {
        StartPoint = startPoint;
        EndPoint = endPoint;
        _stops = stops.OrderBy(s => s.Offset).Select(s => (s.Offset, (double)s.Color.R, (double)s.Color.G, (double)s.Color.B)).ToArray();
        if (_stops.Length == 0)
            throw new ArgumentException("A gradient needs at least one stop.", nameof(stops));
        for (var i = 0; i < LutSize; i++)
        {
            var (r, g, b) = Sample((double)i / (LutSize - 1));
            // 8 fractional bits: the dither threshold decides how the fraction rounds.
            _lutR[i] = (int)Math.Round(r * 256);
            _lutG[i] = (int)Math.Round(g * 256);
            _lutB[i] = (int)Math.Round(b * 256);
        }
        var (ar, ag, ab) = Average();
        Fallback = Color.FromRgb((byte)Math.Round(ar), (byte)Math.Round(ag), (byte)Math.Round(ab));
    }

    public Point StartPoint { get; }

    public Point EndPoint { get; }

    /// <summary>The gradient's average colour (over a square): a flat stand-in for the moment before the first render.</summary>
    public Color Fallback { get; }

    /// <summary>Reads stops and axis from a theme brush (relative mapping, the default for XAML gradients).</summary>
    public static DitheredGradient FromBrush(LinearGradientBrush brush)
    {
        if (brush.MappingMode != BrushMappingMode.RelativeToBoundingBox)
            throw new ArgumentException("Only relative gradients can be dithered.", nameof(brush));
        return new DitheredGradient(brush.StartPoint, brush.EndPoint, brush.GradientStops.Select(s => (s.Offset, s.Color)));
    }

    /// <summary>The exact (un-rounded) colour at gradient position <paramref name="t"/>, 0..255 per channel.</summary>
    public (double R, double G, double B) Sample(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t <= _stops[0].Offset)
            return (_stops[0].R, _stops[0].G, _stops[0].B);
        for (var i = 1; i < _stops.Length; i++)
        {
            var (o1, r1, g1, b1) = _stops[i];
            if (t > o1)
                continue;
            var (o0, r0, g0, b0) = _stops[i - 1];
            var f = o1 - o0 < 1e-9 ? 1 : (t - o0) / (o1 - o0);
            return (r0 + (r1 - r0) * f, g0 + (g1 - g0) * f, b0 + (b1 - b0) * f);
        }
        var last = _stops[^1];
        return (last.R, last.G, last.B);
    }

    /// <summary>
    /// Gradient position of the point (<paramref name="x"/>, <paramref name="y"/>) in an area of
    /// <paramref name="width"/>×<paramref name="height"/>. Like WPF, the relative start and end points are scaled to
    /// the area first and the colour bands run perpendicular to that axis in the area's own (non-square) space.
    /// </summary>
    public double PositionAt(double x, double y, double width, double height)
    {
        var (a, b, c) = Plane(width, height);
        return a * x + b * y + c;
    }

    /// <summary>t = a·x + b·y + c over an area of the given size.</summary>
    (double A, double B, double C) Plane(double width, double height)
    {
        var dx = (EndPoint.X - StartPoint.X) * width;
        var dy = (EndPoint.Y - StartPoint.Y) * height;
        var length = dx * dx + dy * dy;
        if (length < 1e-12)
            return (0, 0, 0);
        var a = dx / length;
        var b = dy / length;
        return (a, b, -StartPoint.X * width * a - StartPoint.Y * height * b);
    }

    /// <summary>
    /// Renders the gradient at exactly <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/> device pixels
    /// (pass the area's size times its DPI scale, so every bitmap pixel lands on one screen pixel).
    /// </summary>
    public BitmapSource Render(int pixelWidth, int pixelHeight, double dpiX = 96, double dpiY = 96)
    {
        if (pixelWidth < 1 || pixelHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "The bitmap needs at least one pixel.");
        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        // Evaluated at pixel centres (x + ½, y + ½); stepping x adds a.
        var (a, b, c) = Plane(pixelWidth, pixelHeight);
        c += (a + b) * 0.5;
        const int last = LutSize - 1;
        for (var y = 0; y < pixelHeight; y++)
        {
            var t = b * y + c;
            var row = y * stride;
            var thresholdRow = (y % Tile) * Tile;
            for (var x = 0; x < pixelWidth; x++, t += a)
            {
                var i = t <= 0 ? 0 : t >= 1 ? last : (int)(t * last + 0.5);
                var threshold = Thresholds[thresholdRow + (x % Tile)];
                var o = row + x * 4;
                pixels[o] = Quantize(_lutB[i] + threshold);
                pixels[o + 1] = Quantize(_lutG[i] + threshold);
                pixels[o + 2] = Quantize(_lutR[i] + threshold);
                pixels[o + 3] = 0xFF;
            }
        }
        var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Bgr32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A frozen brush that paints an area of <paramref name="width"/>×<paramref name="height"/> device-independent
    /// units at the given DPI scale, one bitmap pixel per screen pixel.
    /// </summary>
    public ImageBrush CreateBrush(double width, double height, double scaleX, double scaleY)
    {
        var (pw, ph) = PixelSize(width, height, scaleX, scaleY);
        var brush = new ImageBrush(Render(pw, ph, 96 * scaleX, 96 * scaleY)) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    /// <summary>The bitmap size for an area, capped at <see cref="MaxPixels"/>.</summary>
    public static (int Width, int Height) PixelSize(double width, double height, double scaleX, double scaleY)
    {
        var pw = Math.Max(1, (int)Math.Round(width * scaleX));
        var ph = Math.Max(1, (int)Math.Round(height * scaleY));
        if ((long)pw * ph > MaxPixels)
        {
            var shrink = Math.Sqrt((double)MaxPixels / ((long)pw * ph));
            pw = Math.Max(1, (int)(pw * shrink));
            ph = Math.Max(1, (int)(ph * shrink));
        }
        return (pw, ph);
    }

    static byte Quantize(int value)
    {
        var v = value >> 8;
        return (byte)(v > 255 ? 255 : v);
    }

    (double R, double G, double B) Average()
    {
        double r = 0, g = 0, b = 0;
        const int n = 64;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var (sr, sg, sb) = Sample(PositionAt(x + 0.5, y + 0.5, n, n));
                r += sr;
                g += sg;
                b += sb;
            }
        }
        return (r / (n * n), g / (n * n), b / (n * n));
    }

    /// <summary>
    /// Interleaved gradient noise (Jimenez 2014): a cheap, deterministic, blue-ish threshold pattern with no visible
    /// structure, scaled to 0..255 (fractions of one 8-bit level).
    /// </summary>
    static int[] CreateThresholds()
    {
        var table = new int[Tile * Tile];
        for (var y = 0; y < Tile; y++)
        {
            for (var x = 0; x < Tile; x++)
            {
                var inner = 0.06711056 * x + 0.00583715 * y;
                var noise = 52.9829189 * (inner - Math.Floor(inner));
                noise -= Math.Floor(noise);
                table[y * Tile + x] = Math.Clamp((int)(noise * 256), 0, 255);
            }
        }
        return table;
    }
}
