using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

public class RenderingTests
{
    [Fact]
    public void Theme_loads_from_the_shared_palette()
    {
        Sta.Run(() =>
        {
            var palette = WheelPalette.Load(out var error);
            Assert.Null(error);
            var accent = Assert.IsType<SolidColorBrush>(palette.Accent);
            Assert.Equal(Color.FromRgb(0xFF, 0x8A, 0x1F), accent.Color);
            Assert.IsType<LinearGradientBrush>(palette.AccentGradient);
            Assert.True(palette.Disc.IsFrozen);
            Assert.Contains("Segoe Fluent Icons", palette.IconFont.Source);
        });
    }

    [Fact]
    public void Every_icon_key_maps_to_a_glyph_that_really_renders()
    {
        Sta.Run(() =>
        {
            var iconFont = WheelPalette.Current.IconFont;
            var missing = ProviderContract.IconKeys.Where(k => IconGlyphs.ForKey(k) is null).ToList();
            Assert.Empty(missing);

            var fontInstalled = IconGlyphs.Covers("Segoe Fluent Icons", '') || IconGlyphs.Covers("Segoe MDL2 Assets", '');
            if (!fontInstalled)
                return; // Nothing to verify on a machine without the icon fonts; Resolve then draws no glyphs at all.
            var tofu = IconGlyphs.All.Keys.Where(k => IconGlyphs.Resolve(k, iconFont) is null).ToList();
            Assert.Empty(tofu);
            Assert.False(IconGlyphs.Covers("Segoe Fluent Icons", ''), "E7AB is not in the font; the coverage check must say so.");
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    public void Renders_every_ring_size_without_truncated_labels(int groups)
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("render");
            var files = Fakes.Files(ws, "clip.mp4");
            // As the wheel window shows it: a page grows a little rather than cut a label.
            var session = WheelSession.ForDisplay(WheelModel.Build(Fakes.Groups(groups, 1, 3, 1, 10), files, WheelMode.Tools), 1);
            var view = new WheelView { PixelsPerDipOverride = 1 };
            foreach (var page in Enumerable.Range(0, session.Pages.Count))
            {
                view.Show(session, WheelInteraction.Drag);
                var cut = view.TruncatedLabels();
                Assert.True(cut.Count == 0, $"Truncated on page {session.PageIndex + 1}: {string.Join(", ", cut)}");
                session.Highlight(WheelHit.OnSegment(Math.Min(3, session.Segments.Count - 1)));
                view.Refresh();
                var bitmap = Render(view);
                Assert.True(CountOpaquePixels(bitmap) > 1000);
                session.NextPage();
            }
        });
    }

    [Fact]
    public void Sample_scenarios_fit_every_label()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("labels");
            var catalog = Fakes.SampleCatalog();
            string[][] selections = [["a.mp4"], ["a.psd"], ["a.wav"], ["a.docx"], ["a.pdf"], ["a.mp4", "b.psd"]];
            foreach (var names in selections)
            {
                var files = Fakes.Files(ws, names);
                foreach (var mode in Enum.GetValues<WheelMode>())
                {
                    var view = new WheelView { PixelsPerDipOverride = 2 };
                    view.Show(Fakes.Session(catalog, files, mode), WheelInteraction.Drag);
                    var cut = view.TruncatedLabels();
                    Assert.True(cut.Count == 0, $"Truncated for {string.Join("+", names)} {mode}: {string.Join(", ", cut)}");
                }
            }
        });
    }

    [Fact]
    public void Warm_up_renders_off_screen() => Sta.Run(WheelView.WarmUp);

    [Fact]
    public void Renders_design_previews()
    {
        var directory = Fakes.ScratchDirectory("previews");
        foreach (var old in Directory.GetFiles(directory, "wheel-*.png"))
            File.Delete(old);

        Sta.Run(() =>
        {
            var log = new RecordingLog();
            var services = HostServices.Create([PreviewSamples.CreateProvider()], SettingsStore.InMemory(), log);
            new WheelHost(services).RenderPreviews(directory);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("[WARN]") || l.StartsWith("[ERROR]"));

            // Extra review states that the shell's --snapshot-ui doesn't need.
            using var ws = new TestWorkspace("previews");
            var files = Fakes.Files(ws, "Showreel.mov");
            var many = Fakes.Session(Fakes.Groups(24, 1, 3, 1, 2), files, WheelMode.Tools);
            WheelPreviews.RenderSession(many, WheelInteraction.Drag, null, false, Path.Combine(directory, "wheel-11-24-groups-page1.png"));
            var moreHit = WheelHit.OnSegment(many.Page.MoreSegment);
            many.MovePointer(many.Layout.SegmentCentroid(many.Page.MoreSegment));
            WheelPreviews.RenderSession(many, WheelInteraction.Drag, WheelPreviews.PointerFor(many.Layout, moreHit), false, Path.Combine(directory, "wheel-12-24-groups-more.png"));
            many.NextPage();
            WheelPreviews.RenderSession(many, WheelInteraction.Drag, null, true, Path.Combine(directory, "wheel-13-24-groups-page2.png"));
            var single = Fakes.Session(Fakes.Groups(1, 3), files, WheelMode.Tools);
            single.MovePointer(single.Layout.SegmentCentroid(0));
            WheelPreviews.RenderSession(single, WheelInteraction.Drag, WheelPreviews.PointerFor(single.Layout, WheelHit.OnSegment(0)), false, Path.Combine(directory, "wheel-14-single-group.png"));

            // 100 % display scaling: small text must stay crisp without the 2× supersampling.
            var video = Fakes.Session(Fakes.SampleCatalog(), Fakes.Files(ws, "Festival loop.mp4"), WheelMode.Convert);
            var prores = video.Segments.ToList().FindIndex(s => s.Label == "ProRes");
            video.MovePointer(video.Layout.SegmentCentroid(prores));
            video.MovePointer(video.Layout.VariantCentroid(prores, 2));
            WheelPreviews.RenderSession(video, WheelInteraction.Drag, WheelPreviews.PointerFor(video.Layout, video.Hover), false,
                Path.Combine(directory, "wheel-15-at-100-percent.png"), scale: 1);
        });

        var pngs = Directory.GetFiles(directory, "wheel-*.png");
        Assert.True(pngs.Length >= 15, $"Expected at least 15 previews, found {pngs.Length}.");
        foreach (var png in pngs.Where(p => !p.Contains("100-percent")))
        {
            var frame = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.True(frame.PixelWidth >= 1000, $"{Path.GetFileName(png)} should be rendered at 2× scale.");
        }
    }

    static BitmapSource Render(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.DesiredSize.Width, (int)element.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    static int CountOpaquePixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 128)
                count++;
        }
        return count;
    }
}
