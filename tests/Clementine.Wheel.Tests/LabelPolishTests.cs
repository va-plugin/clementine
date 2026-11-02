using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

/// <summary>Label and fan polish: families stay together, labels aren't cut, wrapped text never loses words.</summary>
public class LabelPolishTests
{
    static double Measure(string text) => text.Length * 6;

    [Fact]
    public void Fans_keep_a_family_on_one_side()
    {
        // Balanced lead (1) would put "H.265 · no audio" on the far side of "H.264", away from "H.265".
        Assert.Equal(2, WheelLayout.FanLead(["H.264", "H.264 · no audio", "H.265", "H.265 · no audio"]));
        Assert.Equal(WheelLayout.DefaultLead(4), WheelLayout.FanLead(["MP3", "WAV", "M4A", "FLAC"]));
        Assert.Equal(0, WheelLayout.FanLead(["With audio", "No audio"]));

        var layout = WheelLayout.Create([4], variantLabels: [["H.264", "H.264 · no audio", "H.265", "H.265 · no audio"]]);
        var fan = layout.VariantsOf(0);
        // H.265 and its no-audio twin are neighbours: one ends where the other starts.
        Assert.Equal(Angles.Normalize(fan[2].Start + fan[2].Sweep), Angles.Normalize(fan[3].Start), 6);
        Assert.Equal(Angles.Normalize(fan[0].Start + fan[0].Sweep), Angles.Normalize(fan[1].Start), 6);
    }

    [Theory]
    [InlineData(12, 9)]
    [InlineData(12, 10)]
    [InlineData(16, 10)]
    [InlineData(6, 10)]
    public void Big_fans_stay_on_their_side_of_the_wheel(int groups, int variants)
    {
        var counts = Enumerable.Repeat(1, groups).ToArray();
        counts[2] = variants;
        var layout = WheelLayout.Create(counts);
        var fan = layout.VariantsOf(2);
        Assert.Equal(variants, fan.Count);
        Assert.True(fan.Sum(f => f.Sweep) <= WheelLayout.MaxFanSweep + 1e-9, $"{fan.Sum(f => f.Sweep)}° fan");
        Assert.All(fan, f => Assert.True(f.Sweep >= WheelLayout.CompactVariantSweep - 1e-9));
        Assert.Equal(layout.Segments[2].Mid, fan[0].Mid, 9);
        // Small fans keep their roomy slices.
        Assert.Equal(Math.Clamp(360.0 / groups, WheelLayout.MinVariantSweep, WheelLayout.MaxVariantSweep), WheelLayout.Create([1, 4, .. counts[2..]]).VariantsOf(1)[0].Sweep, 9);
    }

    [Fact]
    public void A_ten_variant_pro_res_fan_fits_every_label()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("prores-fan");
            string[] proRes = ["422 HQ", "422 HQ · no audio", "4444 α", "4444 α · no audio", "422", "422 · no audio", "422 LT", "422 Proxy", "4444 XQ α", "4444 XQ α · no audio"];
            string[] others = ["MP4", "MOV", "MKV", "WebM", "GIF", "WebP", "Audio", "DXV", "HAP", "AVI", "WMV"];
            var ops = new List<IOperation>();
            for (var v = 0; v < proRes.Length; v++)
                ops.Add(Fakes.Op(Fakes.Info($"video.prores.{v}", "ProRes", proRes[v], order: 120 + v, icon: "vj")));
            for (var g = 0; g < others.Length; g++)
            {
                ops.Add(Fakes.Op(Fakes.Info($"video.o{g}.a", others[g], "Variant A", order: 10 + g * 2)));
                ops.Add(Fakes.Op(Fakes.Info($"video.o{g}.b", others[g], "Variant B", order: 11 + g * 2)));
            }
            var model = WheelModel.Build(Fakes.Catalog([.. ops]), Fakes.Files(ws, "Festival loop.mp4"), WheelMode.Convert);
            foreach (var ppd in new[] { 1.0, 1.5, 2.0 })
            {
                var session = WheelSession.ForDisplay(model, ppd);
                var segment = session.Segments.ToList().FindIndex(s => s.Label == "ProRes");
                Assert.True(session.Layout.VariantsOf(segment).Sum(f => f.Sweep) <= WheelLayout.MaxFanSweep + 1e-9);
                session.Highlight(WheelHit.OnSegment(segment));
                var view = new WheelView { PixelsPerDipOverride = ppd };
                view.Show(session, WheelInteraction.Drag);
                Assert.Empty(view.TruncatedLabels());
            }
        });
    }

    [Fact]
    public void Ellipsis_never_follows_punctuation_or_a_space()
    {
        var cut = TextWrap.Ellipsize("Festival loop.mp4", Measure, 6 * 15);
        Assert.Equal("Festival loop…", cut);
        Assert.Equal("Remux…", TextWrap.Ellipsize("Remux (no re-encode)", Measure, 6 * 8));
    }

    [Fact]
    public void Label_splits_avoid_breaking_inside_brackets()
    {
        var options = TextWrap.LabelLines("Remux (no re-encode)", allowHyphen: false);
        Assert.Equal(["Remux", "(no re-encode)"], options[1]);
    }

    [Fact]
    public void Balancing_never_moves_a_break_off_a_phrase_boundary()
    {
        // The centre's first line is narrower than the second (it sits higher on the circle).
        double Width(int line) => line == 0 ? 90 : 120;
        Assert.Equal(["ProRes 422 HQ", "no audio"], TextWrap.WrapBalanced("ProRes 422 HQ · no audio", Measure, Width, 3));
        // Plain sentences still balance.
        Assert.Equal(["aaaa bbbb", "cccc dddd ee"], TextWrap.WrapBalanced("aaaa bbbb cccc dddd ee", Measure, _ => 120, 4));
    }

    [Fact]
    public void Degraded_text_is_detected()
    {
        Assert.False(TextWrap.IsDegraded("ProRes 4444 · alpha", ["ProRes 4444", "alpha"]));
        Assert.True(TextWrap.IsDegraded("ProRes 4444 alpha", ["ProRes 44", "44 alpha"]));
        Assert.True(TextWrap.IsDegraded("Keeps the alpha channel", ["Keeps the…"]));
        Assert.False(TextWrap.IsDegraded("Wait…", ["Wait…"]));
        Assert.True(TextWrap.IsDegraded("text", []));
    }

    [Fact]
    public void Display_sessions_grow_instead_of_cutting_a_long_label()
    {
        Sta.Run(() =>
        {
            using var ws = new TestWorkspace("grow");
            // The shipped image tools for two images: 12 groups, "Transparent" used to be cut to "Transpa…".
            var groups = new (string Name, int Variants)[]
            {
                ("Resize", 5), ("Compress", 2), ("Crop", 6), ("Rotate", 5), ("Adjust", 3), ("Transparent", 2),
                ("Metadata", 2), ("Info", 1), ("Read QR", 1), ("Images → PDF", 1), ("Collage", 1), ("To video", 1),
            };
            var ops = groups.SelectMany((g, i) => Enumerable.Range(0, g.Variants).Select(v => Fakes.Op(
                Fakes.Info($"image.t{i}.{v}", g.Name, g.Variants > 1 ? $"Option {v + 1}" : null, order: i * 10 + v,
                    category: OperationCategory.Tool, icon: "image")))).ToArray();
            var model = WheelModel.Build(Fakes.Catalog(ops), Fakes.Files(ws, "a.jpg", "b.png"), WheelMode.Tools);

            foreach (var ppd in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var plain = new WheelSession(model);
                if (ppd == 1)
                {
                    var before = new WheelView { PixelsPerDipOverride = ppd };
                    before.Show(plain, WheelInteraction.Drag);
                    Assert.Contains(before.TruncatedLabels(), l => l.StartsWith("Transp", StringComparison.Ordinal));
                }
                var shown = WheelSession.ForDisplay(model, ppd);
                var view = new WheelView { PixelsPerDipOverride = ppd };
                view.Show(shown, WheelInteraction.Drag);
                Assert.Empty(view.TruncatedLabels());
                Assert.True(shown.Layout.Metrics.OuterRingOuterRadius >= plain.Layout.Metrics.OuterRingOuterRadius);
                Assert.True(shown.Layout.Metrics.OuterRingOuterRadius <= plain.Layout.Metrics.OuterRingOuterRadius * WheelLayout.MaxLabelGrowth + 0.01);
            }
        });
    }
}
