using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Subtitles;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

public class SubtitleParsingTests
{
    static SubtitleDocument Parse(string text, SubtitleFormat hint = SubtitleFormat.Srt) => SubtitleParser.Parse(text, hint);

    [Fact]
    public void Srt_with_bom_crlf_missing_indices_and_missing_blank_lines()
    {
        var text = "\uFEFF1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>there</i>\r\n" + // no blank line before the next cue
                   "00:00:03.000 --> 00:00:04.000\r\nNo index, dot separator\r\n\r\n\r\n" +
                   "7\r\n0:0:5,5 --> 0:0:6,25\r\nShort times\r\n\r\nsecond line after a blank\r\n";
        var doc = Parse(text);
        Assert.Equal(3, doc.Cues.Count);
        Assert.Equal("Hello <i>there</i>", doc.Cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), doc.Cues[0].End);
        Assert.Equal("No index, dot separator", doc.Cues[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(5500), doc.Cues[2].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(6250), doc.Cues[2].End);
        Assert.Equal("Short times\nsecond line after a blank", doc.Cues[2].Text);
    }

    [Fact]
    public void Srt_text_that_is_a_number_is_kept_unless_it_precedes_a_timing_line()
    {
        var doc = Parse("1\n00:00:01,000 --> 00:00:02,000\n1984\n\n2\n00:00:03,000 --> 00:00:04,000\nNext\n");
        Assert.Equal("1984", doc.Cues[0].Text);
        Assert.Equal("Next", doc.Cues[1].Text);
    }

    [Fact]
    public void Content_is_sniffed_before_the_extension()
    {
        var doc = Parse("WEBVTT\n\n00:01.000 --> 00:02.000\nActually VTT\n", SubtitleFormat.Srt);
        Assert.Equal(SubtitleFormat.Vtt, doc.Format);
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Cues[0].Start);
    }

    [Fact]
    public void Vtt_header_blocks_identifiers_settings_tags_and_entities()
    {
        var text = "WEBVTT - My film\nKind: captions\n\nSTYLE\n::cue { color: yellow }\n\nNOTE written by hand\n\n" +
                   "intro\n00:00:01.000 --> 00:00:03.000 align:start line:0\n<v Narrator>Once &amp; for <i>all</i></v>\n<c.loud>Shout</c> &lt;3\n\n" +
                   "00:04.500 --> 00:05.000\nRuby <ruby>漢<rt>kan</rt></ruby> and <00:00:04.700>karaoke\n";
        var doc = Parse(text, SubtitleFormat.Vtt);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(" - My film\nKind: captions", doc.VttHeader);
        Assert.Equal(2, doc.VttBlocks.Count);
        var first = doc.Cues[0];
        Assert.Equal("intro", first.Identifier);
        Assert.Equal("align:start line:0", first.Settings);
        Assert.Equal("Once & for <i>all</i>\nShout <3", first.Text);
        Assert.Equal("Ruby 漢 and karaoke", doc.Cues[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(4500), doc.Cues[1].Start);
    }

    [Fact]
    public void Vtt_cues_missing_blank_lines_are_split()
    {
        var doc = Parse("WEBVTT\n00:01.000 --> 00:02.000\nOne\n2\n00:03.000 --> 00:04.000\nTwo\nlines\n", SubtitleFormat.Vtt);
        Assert.Equal(["One", "Two\nlines"], doc.Cues.Select(c => c.Text).ToArray());
        Assert.Equal("2", doc.Cues[1].Identifier);
        Assert.Equal(TimeSpan.FromSeconds(3), doc.Cues[1].Start);
    }

    [Fact]
    public void Ass_overrides_become_neutral_tags_and_drawings_are_dropped()
    {
        var text = """
            [Script Info]
            ScriptType: v4.00+
            WrapStyle: 0

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\i1}Hello{\i0}, world\Nsecond line
            Comment: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,a comment
            Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\an8\b1\fs30}Top, bold{\b0} {comment}text\hhard
            Dialogue: 1,0:00:05.00,0:00:06.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100{\p0}
            """;
        var doc = Parse(text, SubtitleFormat.Ass);
        Assert.Equal(SubtitleFormat.Ass, doc.Format);
        var dialogue = doc.Dialogue.ToList();
        Assert.Equal("<i>Hello</i>, world\nsecond line", dialogue[0].Text);
        Assert.Equal("{\\an8}<b>Top, bold</b> text\u00A0hard", dialogue[1].Text);
        Assert.Equal("", dialogue[2].Text);
        Assert.True(doc.Cues[1].IsComment);
        Assert.Contains(doc.Warnings, w => w.Contains("drawing", StringComparison.Ordinal));
    }

    [Fact]
    public void Ass_with_reordered_format_line()
    {
        var text = "[Events]\nFormat: Start, End, Text, Style\nDialogue: 0:00:01.00,0:00:02.00,Hi, there,Default\n";
        var doc = Parse(text, SubtitleFormat.Ass);
        // Text isn't last here, so commas split it; the parser still finds the times.
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(doc.Cues).Start);
    }

    [Fact]
    public void Ssa_v4_script_is_recognised()
    {
        var text = "[Script Info]\nScriptType: v4.00\n\n[V4 Styles]\nFormat: Name, Fontname\nStyle: Default,Arial\n\n[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: Marked=0,0:00:01.00,0:00:02.00,Default,,0000,0000,0000,,Old school\n";
        var doc = Parse(text, SubtitleFormat.Ssa);
        Assert.Equal(SubtitleFormat.Ssa, doc.Format);
        Assert.Equal("Old school", Assert.Single(doc.Cues).Text);
    }

    [Fact]
    public void Sbv_blocks()
    {
        var doc = Parse("0:00:01.000,0:00:02.000\nYouTube style[br]line two\n\n0:00:03.000,0:00:04.500\nSecond\n", SubtitleFormat.Sbv);
        Assert.Equal(SubtitleFormat.Sbv, doc.Format);
        Assert.Equal("YouTube style\nline two", doc.Cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(4500), doc.Cues[1].End);
    }

    [Fact]
    public void Files_without_cues_are_refused() =>
        Assert.Throws<OperationException>(() => Parse("just some text\nwithout timings"));

    [Theory]
    [InlineData("00:00:01,000", 1000)]
    [InlineData("00:00:01.5", 1500)]
    [InlineData("1:02:03.04", 3723040)]
    [InlineData("02:03.456", 123456)]
    [InlineData("00:00:01:250", 1250)]
    [InlineData("100:00:00,000", 360000000)]
    public void Timestamps_parse_leniently(string text, int milliseconds)
    {
        Assert.True(SubtitleTime.TryParse(text, out var time));
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), time);
    }

    [Fact]
    public void Timestamps_format_per_format()
    {
        var time = new TimeSpan(0, 1, 2, 3, 456);
        Assert.Equal("01:02:03,456", SubtitleTime.Srt(time));
        Assert.Equal("01:02:03.456", SubtitleTime.Vtt(time));
        Assert.Equal("1:02:03.46", SubtitleTime.Ass(time));
        Assert.Equal("1:02:03.456", SubtitleTime.Sbv(time));
        Assert.Equal("0:00:00.00", SubtitleTime.Ass(TimeSpan.FromMilliseconds(-5)));
        Assert.Equal("0:01:00.00", SubtitleTime.Ass(TimeSpan.FromMilliseconds(59_996))); // rounding carries into minutes
    }
}

public class SubtitleConversionTests
{
    const string Sample =
        "1\r\n00:00:01,234 --> 00:00:03,456\r\n<i>Italic</i> and <b>bold</b> & co\r\nline two\r\n\r\n" +
        "2\r\n00:01:02,007 --> 00:01:04,993\r\n{\\an8}Top of the screen\r\n\r\n" +
        "3\r\n01:00:00,000 --> 01:00:01,500\r\n<font color=\"#FF8A1F\">Clementine</font> – ünïcødé 東京\r\n";

    [Fact]
    public void Srt_vtt_ass_srt_round_trip_keeps_timings_within_10ms_and_text()
    {
        var original = SubtitleParser.Parse(Sample, SubtitleFormat.Srt);
        var vtt = SubtitleWriter.Write(original, SubtitleFormat.Vtt);
        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:01.234 --> 00:00:03.456", vtt);
        Assert.Contains("<i>Italic</i> and <b>bold</b> &amp; co", vtt);
        Assert.Contains("line:0", vtt); // {\an8} → top

        var ass = SubtitleWriter.Write(SubtitleParser.Parse(vtt, SubtitleFormat.Vtt), SubtitleFormat.Ass);
        Assert.Contains("[V4+ Styles]", ass);
        Assert.Contains("Dialogue: 0,0:00:01.23,0:00:03.46,Default,,0,0,0,,{\\i1}Italic{\\i0} and {\\b1}bold{\\b0} & co\\Nline two", ass);

        var back = SubtitleParser.Parse(SubtitleWriter.Write(SubtitleParser.Parse(ass, SubtitleFormat.Ass), SubtitleFormat.Srt), SubtitleFormat.Srt);
        Assert.Equal(original.Cues.Count, back.Cues.Count);
        for (var i = 0; i < original.Cues.Count; i++)
        {
            Assert.InRange(Math.Abs((back.Cues[i].Start - original.Cues[i].Start).TotalMilliseconds), 0, 10);
            Assert.InRange(Math.Abs((back.Cues[i].End - original.Cues[i].End).TotalMilliseconds), 0, 10);
        }
        Assert.Equal("<i>Italic</i> and <b>bold</b> & co\nline two", back.Cues[0].Text);
        Assert.Equal("Clementine – ünïcødé 東京", back.Cues[2].Text); // colour is dropped by VTT on the way
    }

    [Fact]
    public void Srt_to_ass_keeps_colour_and_position()
    {
        var ass = SubtitleWriter.Write(SubtitleParser.Parse(Sample, SubtitleFormat.Srt), SubtitleFormat.Ass);
        Assert.Contains("{\\c&H1F8AFF&}Clementine{\\c}", ass);
        Assert.Contains(",{\\an8}Top of the screen", ass);
    }

    [Fact]
    public void Srt_output_renumbers_sorts_and_uses_crlf()
    {
        var doc = SubtitleParser.Parse("5\n00:00:05,000 --> 00:00:06,000\nlater\n\n9\n00:00:01,000 --> 00:00:00,500\nearlier, end before start\n", SubtitleFormat.Srt);
        var srt = SubtitleWriter.Write(doc, SubtitleFormat.Srt);
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:01,000\r\nearlier, end before start\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nlater\r\n\r\n", srt);
    }

    [Fact]
    public void Vtt_to_vtt_keeps_voice_tags_and_settings_verbatim()
    {
        var text = "WEBVTT\n\nNOTE keep me\n\nid-1\n00:00:01.000 --> 00:00:02.000 position:10%\n<v Bob>Hi <c.red>there</c></v>\n";
        var doc = SubtitleParser.Parse(text, SubtitleFormat.Vtt);
        SubtitleTools.Shift(doc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var output = SubtitleWriter.Write(doc, SubtitleFormat.Vtt);
        Assert.Equal("WEBVTT\n\nNOTE keep me\n\nid-1\n00:00:02.000 --> 00:00:03.000 position:10%\n<v Bob>Hi <c.red>there</c></v>\n\n", output);
    }

    [Fact]
    public void Ass_to_ass_keeps_every_section_and_override_while_shifting()
    {
        var text = "[Script Info]\r\nTitle: Show\r\nScriptType: v4.00+\r\n\r\n[V4+ Styles]\r\nFormat: Name, Fontname\r\nStyle: Sign,Impact\r\n\r\n" +
                   "[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n" +
                   "Dialogue: 2,0:00:01.00,0:00:02.00,Sign,Bob,10,20,30,fx,{\\pos(100,200)\\blur3}Sign text\r\n" +
                   "Comment: 0,0:00:03.00,0:00:04.00,Sign,,0,0,0,,note\r\n\r\n[Fonts]\r\nfontname: x.ttf\r\n";
        var doc = SubtitleParser.Parse(text, SubtitleFormat.Ass);
        SubtitleTools.Shift(doc, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
        var output = SubtitleWriter.Write(doc, SubtitleFormat.Ass);
        Assert.Contains("Title: Show", output);
        Assert.Contains("Style: Sign,Impact", output);
        Assert.Contains("Dialogue: 2,0:00:01.50,0:00:02.50,Sign,Bob,10,20,30,fx,{\\pos(100,200)\\blur3}Sign text", output);
        Assert.Contains("Comment: 0,0:00:03.50,0:00:04.50,Sign,,0,0,0,,note", output);
        Assert.Contains("[Fonts]\r\nfontname: x.ttf", output);
    }

    [Fact]
    public void Txt_output_is_plain_dialogue_without_rolling_repeats()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\n<i>One</i>\n\n2\n00:00:02,000 --> 00:00:03,000\nOne\nTwo\n", SubtitleFormat.Srt);
        Assert.Equal("One\r\nTwo\r\n", SubtitleWriter.Write(doc, SubtitleFormat.Txt));
    }

    [Fact]
    public void Vtt_escapes_arrows_and_angle_brackets_from_other_formats()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nx --> y, 3 > 2\n", SubtitleFormat.Srt);
        var vtt = SubtitleWriter.Write(doc, SubtitleFormat.Vtt);
        Assert.Contains("x --&gt; y, 3 &gt; 2", vtt);
        Assert.Single(SubtitleParser.Parse(vtt, SubtitleFormat.Vtt).Cues);
    }
}

public class SubtitleToolTests
{
    static SubtitleDocument Doc(string srt) => SubtitleParser.Parse(srt, SubtitleFormat.Srt);

    [Fact]
    public void Shift_moves_later_cues_and_drops_ones_pushed_before_zero()
    {
        var doc = Doc("1\n00:00:00,500 --> 00:00:01,000\nGone\n\n2\n00:00:01,500 --> 00:00:03,000\nClamped\n\n3\n00:00:10,000 --> 00:00:11,000\nMoved\n");
        var removed = SubtitleTools.Shift(doc, TimeSpan.FromSeconds(-2), TimeSpan.Zero);
        Assert.Equal(1, removed);
        Assert.Equal(TimeSpan.Zero, doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(8), doc.Cues[1].Start);
    }

    [Fact]
    public void Shift_from_a_time_leaves_earlier_cues_alone()
    {
        var doc = Doc("1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:00:10,000 --> 00:00:11,000\nB\n");
        SubtitleTools.Shift(doc, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1), doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(11500), doc.Cues[1].Start);
    }

    [Fact]
    public void Frame_rate_change_scales_times()
    {
        var doc = Doc("1\n01:00:00,000 --> 01:00:01,000\nA\n");
        SubtitleTools.ChangeFrameRate(doc, 24000.0 / 1001, 25);
        // 3600 s × 23.976 / 25 = 3452.54 s
        Assert.InRange(doc.Cues[0].Start.TotalSeconds, 3452.53, 3452.55);
    }

    [Theory]
    [InlineData("[door slams]", "")]
    [InlineData("(laughs) Very funny.", "Very funny.")]
    [InlineData("JOHN: Hello there.", "Hello there.")]
    [InlineData("- MAN 2: Run!\n- [screams]", "Run!")]
    [InlineData("- Hi.\n- Hello.", "- Hi.\n- Hello.")]
    [InlineData("♪ ♪", "")]
    [InlineData("<i>[whispering]</i> Come here.", "Come here.")]
    [InlineData("It's 10:30: late.", "It's 10:30: late.")]
    [InlineData("（笑）こんにちは", "こんにちは")]
    public void Hearing_impaired_annotations_are_removed(string input, string expected) =>
        Assert.Equal(expected, SubtitleTools.RemoveHearingImpaired(input));

    [Fact]
    public void Clean_formatting_keeps_position_and_removes_emptied_cues()
    {
        var doc = Doc("1\n00:00:01,000 --> 00:00:02,000\n{\\an8}<i>Top</i> <font color=\"red\">red</font>\n\n2\n00:00:03,000 --> 00:00:04,000\n[music]\n");
        var removed = SubtitleTools.Clean(doc, formatting: true, hearingImpaired: true);
        Assert.Equal(1, removed);
        Assert.Equal("{\\an8}Top red", Assert.Single(doc.Cues).Text);
    }
}

public class SubtitleOperationTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    [Fact]
    public async Task Convert_windows_1252_srt_to_vtt_as_utf8()
    {
        using var workspace = new TestWorkspace("sub-1252");
        var path = workspace.Path("Film – ünïcødé.srt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé crème, déjà vu — naïve façade\r\n"));

        var result = await workspace.RunAsync(Op(workspace, "subtitle.vtt"), [path]);

        var output = Assert.Single(result.Outputs);
        Assert.Equal("Film – ünïcødé.vtt", Path.GetFileName(output));
        Assert.Contains("Café crème, déjà vu — naïve façade", File.ReadAllText(output, Encoding.UTF8));
        Assert.Contains(result.Warnings, w => w.Contains("Windows-1252", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_is_not_offered_to_the_same_format()
    {
        using var workspace = new TestWorkspace("sub-same");
        var srt = InputFile.FromPath(workspace.WriteText("a.srt", "x"));
        var ssa = InputFile.FromPath(workspace.WriteText("a.ssa", "x"));
        Assert.False(Op(workspace, "subtitle.srt").Accepts(srt));
        Assert.True(Op(workspace, "subtitle.vtt").Accepts(srt));
        Assert.True(Op(workspace, "subtitle.ass").Accepts(ssa));
        Assert.True(Op(workspace, "subtitle.txt").Accepts(srt));
        Assert.False(Op(workspace, "subtitle.srt").Accepts(InputFile.FromPath(workspace.WriteText("a.txt", "x"))));
    }

    [Fact]
    public async Task Shift_tool_writes_a_suffixed_copy()
    {
        using var workspace = new TestWorkspace("sub-shift");
        var path = workspace.WriteText("movie.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var before = File.ReadAllText(path);

        var result = await workspace.RunAsync(Op(workspace, "subtitle.shift"), [path], new Dictionary<string, object?> { ["ms"] = "-250" });

        var output = Assert.Single(result.Outputs);
        Assert.Equal("movie.en (-0.25 s).srt", Path.GetFileName(output));
        Assert.Contains("00:00:00,750 --> 00:00:01,750", File.ReadAllText(output));
        Assert.Equal(before, File.ReadAllText(path));

        var zero = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "subtitle.shift"), [path]));
        Assert.Contains("milliseconds", zero.Message);
    }

    [Fact]
    public async Task Frame_rate_tool_and_same_rate_error()
    {
        using var workspace = new TestWorkspace("sub-fps");
        var path = workspace.WriteText("a.vtt", "WEBVTT\n\n00:01:40.000 --> 00:01:41.000\nx\n");
        var result = await workspace.RunAsync(Op(workspace, "subtitle.framerate"), [path], new Dictionary<string, object?> { ["from"] = "25", ["to"] = "23.976" });
        Assert.Equal("a (23.976 fps).vtt", Path.GetFileName(Assert.Single(result.Outputs)));
        Assert.Contains("00:01:44.271 --> 00:01:45.314", File.ReadAllText(result.Outputs[0]));

        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "subtitle.framerate"), [path], new Dictionary<string, object?> { ["from"] = "25", ["to"] = "25" }));
    }

    [Fact]
    public async Task Clean_tool_and_fix_encoding_tool()
    {
        using var workspace = new TestWorkspace("sub-clean");
        var path = workspace.Path("show.srt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\n[музыка]\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n<i>Привет, как дела?</i>\r\n"));

        var cleaned = await workspace.RunAsync(Op(workspace, "subtitle.clean.all"), [path]);
        var text = File.ReadAllText(Assert.Single(cleaned.Outputs));
        Assert.Contains("Привет, как дела?", text);
        Assert.DoesNotContain("музыка", text);
        Assert.DoesNotContain("<i>", text);

        var fixedEncoding = await workspace.RunAsync(Op(workspace, "subtitle.utf8"), [path]);
        var output = Assert.Single(fixedEncoding.Outputs);
        Assert.Equal("show (UTF-8).srt", Path.GetFileName(output));
        var bytes = File.ReadAllBytes(output);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("<i>Привет, как дела?</i>", Encoding.UTF8.GetString(bytes));
        Assert.Contains(fixedEncoding.Warnings, w => w.Contains("Cyrillic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fix_encoding_with_a_chosen_encoding()
    {
        using var workspace = new TestWorkspace("sub-manual");
        var path = workspace.Path("greek.srt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1253).GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nΚαλημέρα\r\n"));
        var result = await workspace.RunAsync(Op(workspace, "subtitle.utf8.manual"), [path], new Dictionary<string, object?> { ["encoding"] = "windows-1253" });
        Assert.Contains("Καλημέρα", File.ReadAllText(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Every_subtitle_operation_runs_on_every_input_format()
    {
        using var workspace = new TestWorkspace("sub-all");
        var inputs = new[]
        {
            workspace.WriteText("in/a.srt", "1\n00:00:01,000 --> 00:00:02,000\n<i>Hello</i> [noise]\n"),
            workspace.WriteText("in/a.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>Hello</i> [noise]\n"),
            workspace.WriteText("in/a.ass", "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}Hello{\\i0} [noise]\n"),
            workspace.WriteText("in/a.ssa", "[Script Info]\nScriptType: v4.00\n\n[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: Marked=0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello [noise]\n"),
            workspace.WriteText("in/a.sbv", "0:00:01.000,0:00:02.000\nHello [noise]\n"),
        };
        var provider = new FilesOperationProvider();
        var operations = provider.CreateOperations(workspace.Context).Where(o => o.Info.Id.StartsWith("subtitle.", StringComparison.Ordinal)).ToList();
        var options = new Dictionary<string, object?> { ["ms"] = 1000.0, ["encoding"] = "windows-1252" };
        var runs = 0;
        foreach (var input in inputs)
        {
            foreach (var op in operations.Where(o => o.Accepts(InputFile.FromPath(input))))
            {
                var result = await workspace.RunAsync(op, [input], options);
                if (op.Info.Id == "subtitle.utf8")
                {
                    Assert.Equal("Already UTF-8", result.TextTitle);
                    continue;
                }
                var output = Assert.Single(result.Outputs);
                if (Path.GetExtension(output) == ".txt")
                {
                    Assert.Contains("Hello", File.ReadAllText(output));
                }
                else
                {
                    var reparsed = SubtitleParser.Parse(File.ReadAllText(output), SubtitleParser.FormatFromExtension(Path.GetExtension(output)) ?? SubtitleFormat.Srt);
                    Assert.Contains("Hello", reparsed.Cues[0].Text);
                }
                runs++;
            }
        }
        Assert.True(runs >= 40, $"only {runs} runs");
    }
}
