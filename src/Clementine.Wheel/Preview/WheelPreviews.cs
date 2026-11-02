using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>
/// Renders PNG snapshots (2× scale, RenderTargetBitmap) of representative wheel states for design review. Each
/// scenario uses the app's catalog when it has suitable operations and otherwise the realistic
/// <see cref="PreviewSamples"/>. Must run on an STA thread.
/// </summary>
internal static class WheelPreviews
{
    public const double Scale = 2;
    const double Margin = 8;

    /// <summary>One preview: which files, which wheel, and what the pointer (or keyboard) is on.</summary>
    internal sealed record Scenario(
        string FileName,
        string[] Inputs,
        WheelMode Mode,
        string? HoverGroup = null,
        int HoverVariant = -1,
        bool NeedsUnavailable = false,
        WheelInteraction Interaction = WheelInteraction.Drag,
        bool Keyboard = false,
        bool LightBackdrop = false);

    public static IReadOnlyList<Scenario> Standard { get; } =
    [
        new("wheel-01-video-convert.png", ["Festival loop.mp4"], WheelMode.Convert),
        new("wheel-02-video-convert-prores.png", ["Festival loop.mp4"], WheelMode.Convert, HoverGroup: "ProRes"),
        new("wheel-03-video-convert-hap-alpha.png", ["Festival loop.mp4"], WheelMode.Convert, HoverGroup: "HAP", HoverVariant: 2),
        new("wheel-04-psd-convert.png", ["Poster artwork.psd"], WheelMode.Convert, HoverGroup: "PNG", LightBackdrop: true),
        new("wheel-05-audio-tools.png", ["Kick loop.wav"], WheelMode.Tools, HoverGroup: "Normalize"),
        new("wheel-06-mixed-no-common.png", ["Festival loop.mp4", "Set list.pdf", "Kick loop.wav"], WheelMode.Convert, LightBackdrop: true),
        new("wheel-07-actions.png", ["Festival loop.mp4", "Visual 02.mov", "Poster artwork.psd"], WheelMode.Actions, HoverGroup: "Checksum"),
        new("wheel-08-disabled-hover.png", ["Rider.docx"], WheelMode.Convert, HoverGroup: "PDF", NeedsUnavailable: true),
        new("wheel-09-video-tools.png", ["Festival loop.mp4"], WheelMode.Tools, HoverGroup: "Snapshot"),
        new("wheel-10-click-keyboard.png", ["Festival loop.mp4", "Visual 02.mov"], WheelMode.Convert, HoverGroup: "DXV", HoverVariant: 1,
            Interaction: WheelInteraction.Click, Keyboard: true, LightBackdrop: true),
    ];

    /// <summary>Renders every standard scenario into <paramref name="outputDirectory"/>; returns the files written.</summary>
    public static IReadOnlyList<string> Render(OperationCatalog catalog, string outputDirectory, ILog log)
    {
        Directory.CreateDirectory(outputDirectory);
        var inputs = Path.Combine(AppPaths.TempRoot, $"wheel-preview-{Guid.NewGuid():N}"[..28]);
        Directory.CreateDirectory(inputs);
        var samples = new OperationCatalog(catalog.Context, [PreviewSamples.CreateProvider()]);
        var written = new List<string>();
        try
        {
            foreach (var scenario in Standard)
            {
                try
                {
                    var files = scenario.Inputs.Select(name => InputFile.FromPath(Touch(inputs, name))).ToList();
                    var model = ModelFor(scenario, catalog, samples, files, log);
                    var session = WheelSession.ForDisplay(model, Scale);
                    var pointer = Highlight(session, scenario);
                    var path = Path.Combine(outputDirectory, scenario.FileName);
                    RenderSession(session, scenario.Interaction, pointer, scenario.LightBackdrop, path);
                    written.Add(path);
                }
                catch (Exception ex)
                {
                    log.Warn($"Wheel preview {scenario.FileName} failed", ex);
                }
            }
            log.Info($"Wheel previews: wrote {written.Count} PNGs to {outputDirectory}.");
            return written;
        }
        finally
        {
            try
            {
                Directory.Delete(inputs, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Renders one session (with an optional pointer arrow at <paramref name="pointer"/> from the centre) to a PNG.</summary>
    /// <param name="solidBackdrop">A plain backdrop instead of the desktop-like one (measurements that must not see its gradients).</param>
    public static void RenderSession(WheelSession session, WheelInteraction interaction, Vector? pointer, bool lightBackdrop, string path, double scale = Scale,
        Color? solidBackdrop = null)
    {
        var view = new WheelView { PixelsPerDipOverride = scale };
        view.Show(session, interaction);
        var size = session.Layout.Metrics.Size + Margin * 2;

        var root = new Grid { Width = size, Height = size };
        root.Children.Add(solidBackdrop is { } solid ? new Border { Background = new SolidColorBrush(solid) } : new PreviewBackdrop(lightBackdrop));
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(view);
        if (pointer is { } offset)
            root.Children.Add(new PreviewPointer(new Point(size / 2 + offset.X, size / 2 + offset.Y)));

        root.Measure(new Size(size, size));
        root.Arrange(new Rect(0, 0, size, size));
        root.UpdateLayout();

        var pixels = (int)Math.Ceiling(size * scale);
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static WheelModel ModelFor(Scenario scenario, OperationCatalog real, OperationCatalog samples, IReadOnlyList<InputFile> files, ILog log)
    {
        var model = WheelModel.Build(real, files, scenario.Mode, log);
        var usable = scenario.NeedsUnavailable
            ? model.Groups.Any(g => g.Options.Any(o => !o.IsAvailable))
            : scenario.HoverGroup is null && scenario.Inputs.Length > 1 && scenario.Mode == WheelMode.Convert
                ? real.All.Count > 0
                : !model.IsEmpty;
        return usable ? model : WheelModel.Build(samples, files, scenario.Mode, log);
    }

    /// <summary>Puts the highlight where the scenario wants it; returns the pointer offset to draw (null for none).</summary>
    static Vector? Highlight(WheelSession session, Scenario scenario)
    {
        if (session.Model.IsEmpty)
            return null;

        var (segment, variant) = FindTarget(session, scenario);
        if (segment < 0)
            return null;

        if (scenario.Keyboard)
        {
            for (var i = 0; i <= segment && session.Hover.Segment != segment; i++)
                session.Navigate(WheelNavigation.NextSegment);
            if (variant >= 0)
            {
                session.Navigate(WheelNavigation.Outward);
                for (var v = 0; v < variant; v++)
                    session.Navigate(WheelNavigation.Clockwise);
            }
            return null;
        }

        var layout = session.Layout;
        session.MovePointer(layout.SegmentCentroid(segment));
        if (variant < 0)
            return PointerFor(layout, WheelHit.OnSegment(segment));
        session.MovePointer(layout.VariantCentroid(segment, variant));
        return PointerFor(layout, WheelHit.OnVariant(segment, variant));
    }

    /// <summary>Where to draw the pointer for a hit: near the inner edge of the segment, clear of its label.</summary>
    internal static Vector PointerFor(WheelLayout layout, WheelHit hit)
    {
        var m = layout.Metrics;
        return hit.Kind == WheelHitKind.Variant
            ? Angles.Direction(layout.VariantsOf(hit.Segment)[hit.Variant].Mid) * (m.OuterRingInnerRadius + 9)
            : Angles.Direction(layout.Segments[hit.Segment].Mid) * (m.InnerRingInnerRadius + 9);
    }

    static (int Segment, int Variant) FindTarget(WheelSession session, Scenario scenario)
    {
        for (var page = 0; page < session.Pages.Count; page++)
        {
            var segments = session.Segments;
            if (scenario.NeedsUnavailable)
            {
                for (var s = 0; s < segments.Count; s++)
                {
                    if (segments[s].Group is { IsAvailable: false })
                        return (s, -1);
                    var missing = segments[s].Options.ToList().FindIndex(o => !o.IsAvailable);
                    if (missing >= 0 && segments[s].HasVariants)
                        return (s, missing);
                }
            }
            else if (scenario.HoverGroup is { } name)
            {
                var index = segments.ToList().FindIndex(s => string.Equals(s.Label, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    return (index, Math.Min(scenario.HoverVariant, segments[index].Options.Count - 1));
            }
            if (!session.NextPage())
                break;
        }

        if (scenario.HoverGroup is null && !scenario.NeedsUnavailable)
            return (-1, -1);
        // Not found (e.g. the real catalog names groups differently): use the group with the most variants.
        var best = session.Segments.Select((s, i) => (s, i)).OrderByDescending(x => x.s.Options.Count).First().i;
        return (best, scenario.HoverVariant >= 0 && session.Segments[best].HasVariants ? 0 : -1);
    }

    static string Touch(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            // Realistic sizes for the centre summary; SetLength reserves space instantly without writing data.
            using var stream = File.Create(path);
            stream.SetLength(FileTypes.ExtensionOf(name) switch
            {
                ".mp4" => 38_400_000,
                ".mov" => 64_200_000,
                ".psd" => 86_500_000,
                ".wav" => 4_400_000,
                ".pdf" => 1_300_000,
                ".docx" => 240_000,
                _ => 2_048,
            });
        }
        return path;
    }
}

/// <summary>A soft desktop-like backdrop so previews show how the glass reads over real content.</summary>
internal sealed class PreviewBackdrop(bool light) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        var baseBrush = light
            ? new LinearGradientBrush(Color.FromRgb(0xEE, 0xF1, 0xF6), Color.FromRgb(0xD5, 0xDC, 0xE8), 45)
            : new LinearGradientBrush(Color.FromRgb(0x23, 0x30, 0x4A), Color.FromRgb(0x4A, 0x2F, 0x45), 45);
        dc.DrawRectangle(baseBrush, null, rect);

        void Blob(double x, double y, double radius, Color color)
        {
            var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(brush, null, new Point(rect.Width * x, rect.Height * y), radius, radius);
        }

        if (light)
        {
            Blob(0.2, 0.25, rect.Width * 0.45, Color.FromArgb(0x70, 0xB8, 0xCF, 0xF5));
            Blob(0.85, 0.8, rect.Width * 0.5, Color.FromArgb(0x60, 0xF5, 0xC9, 0xA8));
            // A hint of an Explorer list behind the wheel.
            var row = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            for (var y = 40.0; y < rect.Height - 20; y += 34)
                dc.DrawRoundedRectangle(row, null, new Rect(24, y, rect.Width - 48, 24), 6, 6);
        }
        else
        {
            Blob(0.15, 0.2, rect.Width * 0.5, Color.FromArgb(0x90, 0x2E, 0x6C, 0xC9));
            Blob(0.9, 0.85, rect.Width * 0.55, Color.FromArgb(0x80, 0xC9, 0x4E, 0x2E));
            Blob(0.8, 0.1, rect.Width * 0.35, Color.FromArgb(0x60, 0x7E, 0x3F, 0xB8));
        }
    }
}

/// <summary>A standard arrow pointer, so previews show where the mouse is.</summary>
internal sealed class PreviewPointer(Point tip) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var figure = new PathFigure { StartPoint = tip, IsClosed = true };
        Point P(double x, double y) => new(tip.X + x, tip.Y + y);
        foreach (var point in new[] { P(0, 17), P(4.2, 13.2), P(7, 19.4), P(9.6, 18.3), P(6.9, 12.2), P(12.2, 12.2) })
            figure.Segments.Add(new LineSegment(point, true));
        var geometry = new PathGeometry([figure]);
        dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.1) { LineJoin = PenLineJoin.Round }, geometry);
    }
}
