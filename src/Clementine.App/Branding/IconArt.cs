using System.Windows;
using System.Windows.Media;

namespace Clementine.App.Branding;

/// <summary>
/// The Clementine mark: a glossy clementine with a small green leaf, drawn as WPF vector art on a 256 × 256 canvas.
/// Small renditions (≤ 24 px) drop fine detail and enlarge the leaf so the shape stays readable.
/// </summary>
public static class IconArt
{
    public const double CanvasSize = 256;

    /// <summary>The full-detail drawing (frozen, safe to share across threads).</summary>
    public static DrawingGroup Create(bool simplified = false)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
            Draw(dc, simplified);
        group.Freeze();
        return group;
    }

    /// <summary>The drawing as an <see cref="ImageSource"/> for in-app logos (crisp at any size).</summary>
    public static DrawingImage CreateImage()
    {
        var image = new DrawingImage(Create());
        image.Freeze();
        return image;
    }

    static void Draw(DrawingContext dc, bool simplified)
    {
        // Transparent square so every rendition keeps the same 256 × 256 framing.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, CanvasSize, CanvasSize));

        var center = new Point(128, 146);
        var rx = simplified ? 112.0 : 106.0;
        var ry = simplified ? 104.0 : 98.0;

        // Peel: warm radial gradient, lit from the top left.
        var peel = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.34, 0.26),
            Center = new Point(0.42, 0.36),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFF, 0xC8, 0x70), 0.0),
                new GradientStop(Color.FromRgb(0xFF, 0x9C, 0x2E), 0.32),
                new GradientStop(Color.FromRgb(0xF4, 0x76, 0x12), 0.70),
                new GradientStop(Color.FromRgb(0xC4, 0x4E, 0x08), 1.0),
            },
        };
        var body = new EllipseGeometry(center, rx, ry);
        dc.DrawGeometry(peel, simplified ? null : new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x8A, 0x32, 0x00)), 2), body);

        if (!simplified)
        {
            // Soft bounce light on the lower right edge.
            var bounce = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xB0, 0x50), 0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xB0, 0x50), 1),
                },
            };
            dc.PushClip(body);
            dc.DrawEllipse(bounce, null, new Point(176, 214), 62, 36);

            // Very faint dimples (a light dot over a darker one) so the peel doesn't look like plastic.
            var dimpleDark = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x30, 0x00));
            var dimpleLight = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xE0, 0xB0));
            dimpleDark.Freeze();
            dimpleLight.Freeze();
            var random = new Random(7);
            for (var i = 0; i < 70; i++)
            {
                var angle = random.NextDouble() * Math.PI * 2;
                var distance = Math.Sqrt(random.NextDouble()) * 0.9;
                var p = new Point(center.X + Math.Cos(angle) * rx * distance, center.Y + Math.Sin(angle) * ry * distance);
                if (p.X < 125 && p.Y < 135)
                    continue; // keep the highlight clean
                dc.DrawEllipse(dimpleDark, null, new Point(p.X + 0.8, p.Y + 0.8), 1.6, 1.6);
                dc.DrawEllipse(dimpleLight, null, p, 1.3, 1.3);
            }
            dc.Pop();
        }

        // Specular highlight.
        var gloss = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x66, 0xFF, 0xF4, 0xE0), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xF0, 0xD8), 1),
            },
        };
        dc.PushTransform(new RotateTransform(-34, 88, 100));
        dc.DrawEllipse(gloss, null, new Point(88, 100), simplified ? 42 : 56, simplified ? 24 : 30);
        dc.Pop();
        if (!simplified)
        {
            dc.PushTransform(new RotateTransform(-34, 72, 92));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), null, new Point(72, 92), 10, 6);
            dc.Pop();
        }

        // Stem nub.
        var stem = new SolidColorBrush(Color.FromRgb(0x5E, 0x4A, 0x18));
        dc.DrawEllipse(stem, null, new Point(128, 52), simplified ? 13 : 10, simplified ? 8 : 6.5);

        // Leaf: two cubic arcs from the stem out to the upper right.
        var leaf = Geometry.Parse(simplified
            ? "M 124,56 C 140,6 196,-6 246,14 C 226,62 170,84 124,56 Z"
            : "M 126,54 C 146,12 196,0 238,18 C 218,60 168,78 126,54 Z");
        var leafFill = new LinearGradientBrush(Color.FromRgb(0x7C, 0xCB, 0x6E), Color.FromRgb(0x2E, 0x7D, 0x32), new Point(0.15, 0), new Point(0.85, 1));
        dc.DrawGeometry(leafFill, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x1B, 0x4D, 0x1F)), simplified ? 0 : 1.5), leaf);
        if (!simplified)
        {
            var vein = new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xE8, 0xF5, 0xD8)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, vein, Geometry.Parse("M 132,52 Q 178,30 228,20"));
        }
    }
}
