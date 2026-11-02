using System.IO;
using System.Windows;
using Clementine.Core;
using Clementine.Core.Hosting;
using Clementine.TestSupport;

namespace Clementine.Wheel.Tests;

public class WheelModelTests
{
    static bool IsVideo(InputFile f) => f.Kind == FileKind.Video;

    static OperationCatalog VideoCatalog() => Fakes.Catalog(
        Fakes.Op(Fakes.Info("video.mp4.h264", "MP4", "H.264", order: 10), IsVideo),
        Fakes.Op(Fakes.Info("video.mp4.h265", "MP4", "H.265", order: 11), IsVideo),
        Fakes.Op(Fakes.Info("video.prores.422hq", "ProRes", "422 HQ", order: 120), IsVideo,
            Availability.Unavailable("Requires FFmpeg")),
        Fakes.Op(Fakes.Info("video.prores.4444", "ProRes", "4444 α", order: 121), IsVideo),
        Fakes.Op(Fakes.Info("video.dxv", "DXV", order: 100), IsVideo, Availability.Unavailable("Requires FFmpeg")),
        Fakes.Op(Fakes.Info("video.trim", "Trim", order: 10, category: OperationCategory.Tool), IsVideo),
        Fakes.Op(Fakes.Info("files.zip", "Compress", order: 10, category: OperationCategory.Action)));

    [Fact]
    public void Groups_follow_the_catalog_order_with_availability()
    {
        using var ws = new TestWorkspace("model");
        var model = WheelModel.Build(VideoCatalog(), Fakes.Files(ws, "clip.mp4"), WheelMode.Convert);
        Assert.Equal(["MP4", "DXV", "ProRes"], model.Groups.Select(g => g.Name));
        var prores = model.Groups[2];
        Assert.True(prores.HasVariants);
        Assert.False(prores.Options[0].IsAvailable);
        Assert.Equal("video.prores.4444", prores.Default.Info.Id);
        Assert.True(prores.IsAvailable);
        Assert.False(model.Groups[1].IsAvailable);
        Assert.Equal("video.dxv", model.Groups[1].Default.Info.Id);
    }

    [Fact]
    public void Each_mode_shows_its_own_category()
    {
        using var ws = new TestWorkspace("model");
        var files = Fakes.Files(ws, "clip.mp4");
        Assert.Equal(["Trim"], WheelModel.Build(VideoCatalog(), files, WheelMode.Tools).Groups.Select(g => g.Name));
        Assert.Equal(["Compress"], WheelModel.Build(VideoCatalog(), files, WheelMode.Actions).Groups.Select(g => g.Name));
    }

    [Fact]
    public void Mixed_selection_only_offers_what_every_file_accepts()
    {
        using var ws = new TestWorkspace("model");
        var files = Fakes.Files(ws, "clip.mp4", "notes.pdf");
        var model = WheelModel.Build(VideoCatalog(), files, WheelMode.Convert);
        Assert.True(model.IsEmpty);
        Assert.Single(WheelModel.Build(VideoCatalog(), files, WheelMode.Actions).Groups);
    }

    [Fact]
    public void A_throwing_availability_check_disables_instead_of_crashing()
    {
        using var ws = new TestWorkspace("model");
        var log = new RecordingLog();
        var bad = new DelegateOperation(Fakes.Info("video.bad", "Bad"), _ => true,
            (_, _, _) => Task.FromResult(new OperationResult()), availability: () => throw new InvalidOperationException("boom"));
        var model = WheelModel.Build(Fakes.Catalog(bad), Fakes.Files(ws, "clip.mp4"), WheelMode.Convert, log);
        Assert.False(model.Groups[0].Options[0].IsAvailable);
        Assert.Contains(log.Lines, l => l.Contains("video.bad"));
    }

    [Fact]
    public void Labels_fall_back_to_the_title()
    {
        var option = new WheelOption(Fakes.Op(Fakes.Info("x.y", "PNG", title: "PNG image")), Availability.Available);
        Assert.Equal("PNG image", option.Label);
        var variant = new WheelOption(Fakes.Op(Fakes.Info("x.z", "JPEG", "High (92)")), Availability.Available);
        Assert.Equal("High (92)", variant.Label);
    }
}

public class SelectionSummaryTests
{
    [Fact]
    public void One_file_shows_its_name_kind_and_size()
    {
        using var ws = new TestWorkspace("summary");
        var path = ws.Path("Festival loop.mp4");
        File.WriteAllBytes(path, new byte[3 * 1024 * 1024]);
        var summary = SelectionSummary.Describe([InputFile.FromPath(path)]);
        Assert.Equal("Festival loop.mp4", summary.Headline);
        Assert.Equal("MP4 video · 3 MB", summary.Detail);
        Assert.Equal(FileKind.Video, summary.Kind);
    }

    [Fact]
    public void Several_of_a_kind_are_counted()
    {
        using var ws = new TestWorkspace("summary");
        var summary = SelectionSummary.Describe(Fakes.Files(ws, "a.mp4", "b.mov", "c.mkv"));
        Assert.Equal("3 videos", summary.Headline);
        Assert.Equal(FileKind.Video, summary.Kind);
    }

    [Fact]
    public void A_mix_lists_the_kinds()
    {
        using var ws = new TestWorkspace("summary");
        var summary = SelectionSummary.Describe(Fakes.Files(ws, "a.mp4", "b.mov", "c.png", "d.pdf", "e.zip"));
        Assert.Equal("5 files", summary.Headline);
        Assert.Equal("2 videos · 1 image · 1 PDF · …", summary.Detail);
        Assert.Null(summary.Kind);
    }

    [Fact]
    public void Folders_and_nouns()
    {
        using var ws = new TestWorkspace("summary");
        Directory.CreateDirectory(ws.Path("Set 1/x"));
        var summary = SelectionSummary.Describe([InputFile.FromPath(ws.Path("Set 1"))]);
        Assert.Equal(("Set 1", "Folder"), (summary.Headline, summary.Detail));
        Assert.Equal("2 audio files", SelectionSummary.Noun(FileKind.Audio, 2));
        Assert.Equal("1 PDF", SelectionSummary.Noun(FileKind.Pdf, 1));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(999, "999 bytes")]
    [InlineData(2048, "2 KB")]
    [InlineData(5_400_000, "5.1 MB")]
    [InlineData(1_300_000_000, "1.2 GB")]
    public void Sizes_are_short(long bytes, string expected) =>
        Assert.Equal(expected, SelectionSummary.FormatSize(bytes).Replace(',', '.'));
}

public class WheelSessionTests
{
    static WheelSession Session(int groups, params int[] variants)
    {
        var ws = new TestWorkspace("session");
        try
        {
            return Fakes.Session(Fakes.Groups(groups, variants), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools);
        }
        finally
        {
            ws.Dispose();
        }
    }

    [Fact]
    public void Pointer_over_a_group_targets_its_default_and_opens_its_fan()
    {
        var session = Session(6, 1, 3);
        Assert.True(session.MovePointer(session.Layout.SegmentCentroid(1)));
        Assert.Equal(WheelHit.OnSegment(1), session.Hover);
        Assert.Equal(1, session.ExpandedSegment);
        Assert.Equal("test.g1.v0", session.Target!.Info.Id);
        Assert.False(session.MovePointer(session.Layout.SegmentCentroid(1)), "Same spot: nothing changes.");

        Assert.True(session.MovePointer(session.Layout.VariantCentroid(1, 2)));
        Assert.Equal(WheelHit.OnVariant(1, 2), session.Hover);
        Assert.Equal("test.g1.v2", session.Target!.Info.Id);

        Assert.True(session.MovePointer(new Vector(0, 0)));
        Assert.Equal(WheelHit.Centre, session.Hover);
        Assert.Null(session.Target);
        Assert.Equal(-1, session.ExpandedSegment);
    }

    [Fact]
    public void Leaving_the_wheel_clears_the_highlight()
    {
        var session = Session(6, 3);
        session.MovePointer(session.Layout.SegmentCentroid(0));
        Assert.True(session.ClearHover());
        Assert.Equal(WheelHit.None, session.Hover);
        Assert.Equal(-1, session.ExpandedSegment);
        Assert.False(session.ClearHover());
    }

    [Fact]
    public void Leaving_the_wheel_keeps_the_open_fan_for_a_moment()
    {
        using var ws = new TestWorkspace("session");
        var clock = new FakeClock();
        var session = new WheelSession(WheelModel.Build(Fakes.Groups(6, 3, 3), Fakes.Files(ws, "clip.mp4"), WheelMode.Tools), clock: clock.Func);
        var variant = session.Layout.VariantCentroid(0, 1);
        session.MovePointer(session.Layout.SegmentCentroid(0));
        session.MovePointer(variant);
        Assert.Equal(WheelHit.OnVariant(0, 1), session.Hover);

        // Out and back onto the fan within the grace: the variant again, not what lies under that angle without the fan.
        Assert.True(session.PointerLeft());
        Assert.Equal(WheelHit.None, session.Hover);
        Assert.Null(session.Target);
        Assert.Equal(0, session.ExpandedSegment);
        Assert.True(session.NeedsTick);
        clock.Advance(WheelSession.LeaveGrace.TotalMilliseconds - 50);
        Assert.False(session.Tick());
        session.MovePointer(variant);
        Assert.Equal(WheelHit.OnVariant(0, 1), session.Hover);
        Assert.False(session.NeedsTick);

        // Back somewhere else: the fan folds first, so that spot is hit as if it had never been open.
        session.PointerLeft();
        clock.Advance(50);
        session.MovePointer(session.Layout.SegmentCentroid(3));
        Assert.Equal(WheelHit.OnSegment(3), session.Hover);
        Assert.Equal(3, session.ExpandedSegment);

        // Not back at all: the fan folds once the grace has passed.
        session.ClearHover();
        session.MovePointer(session.Layout.SegmentCentroid(1));
        Assert.Equal(1, session.ExpandedSegment);
        session.PointerLeft();
        clock.Advance(WheelSession.LeaveGrace.TotalMilliseconds);
        Assert.True(session.Tick());
        Assert.Equal(-1, session.ExpandedSegment);
        Assert.False(session.NeedsTick);
        Assert.False(session.Tick());
    }

    [Fact]
    public void Unavailable_operations_are_described_but_not_targeted()
    {
        using var ws = new TestWorkspace("session");
        var catalog = Fakes.Catalog(
            Fakes.Op(Fakes.Info("doc.pdf", "PDF"), availability: Availability.Unavailable("Requires LibreOffice")),
            Fakes.Op(Fakes.Info("doc.txt", "TXT")));
        var session = Fakes.Session(catalog, Fakes.Files(ws, "a.docx"), WheelMode.Convert);
        session.MovePointer(session.Layout.SegmentCentroid(0));
        Assert.Equal("doc.pdf", session.HoveredOption!.Info.Id);
        Assert.Null(session.Target);
        session.MovePointer(session.Layout.SegmentCentroid(1));
        Assert.Equal("doc.txt", session.Target!.Info.Id);
    }

    [Fact]
    public void Twenty_four_groups_page_through_more()
    {
        var session = Session(24);
        Assert.Equal(2, session.Pages.Count);
        Assert.Equal(16, session.Segments.Count);
        var more = session.Page.MoreSegment;
        Assert.True(session.Segments[more].IsMore);
        Assert.Equal(9, session.Segments[more].HiddenCount);

        session.MovePointer(session.Layout.SegmentCentroid(more));
        Assert.True(session.IsOverMore);
        Assert.Null(session.Target);

        Assert.True(session.NextPage());
        Assert.Equal(1, session.PageIndex);
        Assert.Equal(10, session.Segments.Count);
        Assert.Equal("Stabilize", session.Segments[0].Label);
        Assert.Equal(15, session.Segments[^1].HiddenCount);
        Assert.True(session.NextPage());
        Assert.Equal(0, session.PageIndex);
        Assert.False(Session(12).NextPage());
    }

    [Fact]
    public void Keyboard_walks_the_ring_and_the_fan()
    {
        var session = Session(4, 1, 3);
        Assert.True(session.Navigate(WheelNavigation.Clockwise));
        Assert.Equal(WheelHit.OnSegment(0), session.Hover);
        Assert.True(session.KeyboardActive);
        session.Navigate(WheelNavigation.Clockwise);
        Assert.Equal(WheelHit.OnSegment(1), session.Hover);
        Assert.Equal(1, session.ExpandedSegment);
        session.Navigate(WheelNavigation.Outward);
        Assert.Equal(WheelHit.OnVariant(1, 0), session.Hover);
        session.Navigate(WheelNavigation.CounterClockwise);
        Assert.Equal(WheelHit.OnVariant(1, 2), session.Hover);
        session.Navigate(WheelNavigation.Clockwise);
        Assert.Equal(WheelHit.OnVariant(1, 0), session.Hover);
        session.Navigate(WheelNavigation.Inward);
        Assert.Equal(WheelHit.OnSegment(1), session.Hover);
        session.Navigate(WheelNavigation.PreviousSegment);
        session.Navigate(WheelNavigation.PreviousSegment);
        Assert.Equal(WheelHit.OnSegment(3), session.Hover);
        session.Navigate(WheelNavigation.NextSegment);
        Assert.Equal(WheelHit.OnSegment(0), session.Hover);

        session.MovePointer(new Vector(0, 0));
        Assert.False(session.KeyboardActive);
    }

    [Fact]
    public void Empty_model_has_no_segments_and_only_a_centre()
    {
        using var ws = new TestWorkspace("session");
        var session = Fakes.Session(Fakes.Catalog(), Fakes.Files(ws, "x.bin"), WheelMode.Convert);
        Assert.True(session.Model.IsEmpty);
        Assert.Empty(session.Segments);
        session.MovePointer(new Vector(120, 0));
        Assert.Equal(WheelHit.Centre, session.Hover);
        Assert.False(session.Navigate(WheelNavigation.Clockwise));
    }
}

public class TextLayoutTests
{
    // 6 units per character keeps the arithmetic obvious.
    static double Measure(string text) => text.Length * 6;

    [Fact]
    public void Wraps_greedily_at_spaces()
    {
        var lines = TextWrap.Wrap("Edit ready ten bit master", Measure, _ => 66, 5);
        Assert.Equal(["Edit ready", "ten bit", "master"], lines);
    }

    [Fact]
    public void Each_line_can_have_its_own_width()
    {
        var lines = TextWrap.Wrap("aa bb cc dd ee", Measure, i => i == 0 ? 30 : 60, 5);
        Assert.Equal(["aa bb", "cc dd ee"], lines);
    }

    [Fact]
    public void Overflow_ends_in_an_ellipsis_on_the_last_line()
    {
        var lines = TextWrap.Wrap("one two three four five six", Measure, _ => 48, 2);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith(TextWrap.Ellipsis, lines[1]);
        Assert.True(Measure(lines[1]) <= 48);
    }

    [Fact]
    public void Long_words_are_broken()
    {
        var lines = TextWrap.Wrap("Supercalifragilistic", Measure, _ => 60, 3);
        Assert.Equal("Supercalif", lines[0]);
        Assert.Equal("ragilistic", lines[1]);
    }

    [Fact]
    public void Phrases_break_at_their_separators()
    {
        var lines = TextWrap.WrapPhrases("ProRes 4444 · alpha · with audio", Measure, _ => 120, 3);
        Assert.Equal(["ProRes 4444 · alpha", "with audio"], lines);
        var fallback = TextWrap.WrapPhrases("A very long first phrase here · b", Measure, _ => 60, 4);
        Assert.DoesNotContain(fallback, l => l.EndsWith(" ·") || l.StartsWith("· "));
    }

    [Fact]
    public void Balanced_wrap_avoids_a_lonely_last_word()
    {
        var greedy = TextWrap.WrapPhrases("Keeps the alpha channel ideal for layered clips", Measure, _ => 150, 4);
        Assert.Equal(["Keeps the alpha channel", "ideal for layered clips"], greedy);
        var orphan = TextWrap.WrapPhrases("aaaa bbbb cccc dddd ee", Measure, _ => 120, 4);
        Assert.Equal(["aaaa bbbb cccc dddd", "ee"], orphan);
        var balanced = TextWrap.WrapBalanced("aaaa bbbb cccc dddd ee", Measure, _ => 120, 4);
        Assert.Equal(["aaaa bbbb", "cccc dddd ee"], balanced);
    }

    [Fact]
    public void Ellipsize_keeps_short_text_and_trims_long_text()
    {
        Assert.Equal("short", TextWrap.Ellipsize("short", Measure, 60));
        var cut = TextWrap.Ellipsize("much longer text", Measure, 42);
        Assert.Equal("much l…", cut);
    }

    [Fact]
    public void Label_lines_prefer_the_dot_then_balanced_spaces_then_hyphens()
    {
        var options = TextWrap.LabelLines("HAP Alpha · no audio");
        Assert.Equal(["HAP Alpha · no audio"], options[0]);
        Assert.Equal(["HAP Alpha", "no audio"], options[1]);
        Assert.DoesNotContain(options, o => o.Any(l => l.StartsWith('·') || l.EndsWith('·')));

        Assert.Equal(["Remove", "audio"], TextWrap.LabelLines("Remove audio")[1]);
        Assert.Single(TextWrap.LabelLines("Ping-pong", allowHyphen: false));
        Assert.Equal(["Ping-", "pong"], TextWrap.LabelLines("Ping-pong")[1]);
    }

    [Fact]
    public void Sector_frame_rejects_boxes_that_poke_out()
    {
        var frame = new SectorFrame(new AngleRange(345, 30), 88, 158, 3);
        var centre = Angles.Direction(0) * 123;
        Assert.True(frame.Fits(centre, 40, 16, 4));
        Assert.False(frame.Fits(centre, 90, 16, 4), "too wide for a 30° slice");
        Assert.False(frame.Fits(centre, 20, 70, 4), "taller than the ring");
        Assert.False(frame.Fits(Angles.Direction(30) * 123, 20, 16, 4), "outside the slice");
        Assert.True(new SectorFrame(new AngleRange(0, 360), 88, 158, 3).Fits(Angles.Direction(0) * 123, 120, 30, 4));
    }

    [Fact]
    public void Fitter_shrinks_or_wraps_before_truncating()
    {
        var frame = new SectorFrame(new AngleRange(345, 30), 88, 158, 3);
        static double Measure(string text, double size) => text.Length * size * 0.55;

        var fits = LabelFitter.FitAll([("ProRes", true, frame), ("Remove audio", false, frame)], [13, 12, 11, 10.5], 15, Measure, 4);
        Assert.All(fits, f => Assert.False(f.Truncated));
        Assert.Equal(fits[0].FontSize, fits[1].FontSize);
        Assert.Equal(2, fits[1].Lines.Count);

        var hopeless = LabelFitter.FitAll([("An extraordinarily long label", false, frame)], [10], 0, Measure, 4);
        Assert.True(hopeless[0].Truncated);
        Assert.EndsWith(TextWrap.Ellipsis, hopeless[0].Lines[0]);
    }
}
