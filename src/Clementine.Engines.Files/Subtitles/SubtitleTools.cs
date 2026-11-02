using System.Globalization;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Subtitles;

/// <summary>Timing and clean-up transformations on a parsed document (in place).</summary>
internal static partial class SubtitleTools
{
    /// <summary>
    /// Moves every cue starting at or after <paramref name="from"/> by <paramref name="delta"/>. Cues pushed entirely
    /// before 0:00 are removed, partly negative ones start at 0:00. Returns the number of cues removed.
    /// </summary>
    public static int Shift(SubtitleDocument document, TimeSpan delta, TimeSpan from)
    {
        foreach (var cue in document.Cues.Where(c => c.Start >= from))
        {
            cue.Start += delta;
            cue.End += delta;
            ShiftInlineTimestamps(cue, t => t + delta);
        }
        return RemoveNegative(document);
    }

    /// <summary>Re-times for a frame-rate change: every time is multiplied by from ÷ to (23.976 → 25 speeds up).</summary>
    public static void ChangeFrameRate(SubtitleDocument document, double fromFps, double toFps)
    {
        var factor = fromFps / toFps;
        TimeSpan Map(TimeSpan t) => TimeSpan.FromTicks((long)Math.Round(t.Ticks * factor));
        foreach (var cue in document.Cues)
        {
            cue.Start = Map(cue.Start);
            cue.End = Map(cue.End);
            ShiftInlineTimestamps(cue, Map);
        }
    }

    /// <summary>Removes formatting and/or hearing-impaired annotations. Returns the number of cues left empty and removed.</summary>
    public static int Clean(SubtitleDocument document, bool formatting, bool hearingImpaired)
    {
        foreach (var cue in document.Dialogue)
        {
            var text = cue.Text;
            if (hearingImpaired)
                text = RemoveHearingImpaired(text);
            if (formatting)
                text = SubtitleMarkup.StripFormatting(text);
            cue.Text = text;
        }
        return document.Cues.RemoveAll(c => !c.IsComment && SubtitleMarkup.ToPlain(c.Text).Trim().Length == 0);
    }

    /// <summary>
    /// Strips [sound effects], (descriptions), ♪-only lines and upper-case speaker labels ("JOHN: Hi" → "Hi"), then
    /// tidies dialogue dashes that no longer introduce two speakers.
    /// </summary>
    internal static string RemoveHearingImpaired(string text)
    {
        var originalLines = text.Split('\n').Length;
        text = Brackets().Replace(text, "");
        text = Parentheses().Replace(text, "");
        text = FullWidthParentheses().Replace(text, "");
        text = SpeakerLabel().Replace(text, "${lead}");

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = EmptyTags().Replace(raw, "");
            line = MultipleSpaces().Replace(line, " ").Trim();
            var visible = SubtitleMarkup.ToPlain(line);
            if (visible.Length == 0 || OnlySymbols().IsMatch(visible))
                continue;
            lines.Add(line);
        }

        // "- Hi.\n- [door]" → "Hi." (one speaker left, the dash no longer makes sense).
        if (lines.Count == 1 && originalLines > 1)
            lines[0] = LeadingDash().Replace(lines[0], "${tags}");
        return string.Join('\n', lines);
    }

    static int RemoveNegative(SubtitleDocument document)
    {
        var removed = document.Cues.RemoveAll(c => c.End <= TimeSpan.Zero);
        foreach (var cue in document.Cues.Where(c => c.Start < TimeSpan.Zero))
            cue.Start = TimeSpan.Zero;
        return removed;
    }

    /// <summary>WebVTT karaoke-style inline timestamps ("&lt;00:00:01.500&gt;") move with their cue.</summary>
    static void ShiftInlineTimestamps(SubtitleCue cue, Func<TimeSpan, TimeSpan> map)
    {
        if (cue.Source is not { Format: SubtitleFormat.Vtt } source || !source.Raw.Contains('<'))
            return;
        var shifted = InlineTimestamp().Replace(source.Raw, match =>
            SubtitleTime.TryParse(match.Groups[1].Value, out var time)
                ? $"<{SubtitleTime.Vtt(map(time) < TimeSpan.Zero ? TimeSpan.Zero : map(time))}>"
                : match.Value);
        cue.Source = source.WithRaw(shifted);
    }

    /// <summary>Frame rates offered by "Change frame rate…" (NTSC rates are exact fractions).</summary>
    public static readonly IReadOnlyList<(string Value, string Label, double Fps)> FrameRates =
    [
        ("23.976", "23.976 (film, NTSC)", 24000.0 / 1001),
        ("24", "24 (film)", 24),
        ("25", "25 (PAL)", 25),
        ("29.97", "29.97 (NTSC)", 30000.0 / 1001),
        ("30", "30", 30),
        ("50", "50", 50),
        ("59.94", "59.94", 60000.0 / 1001),
        ("60", "60", 60),
    ];

    public static double ParseFrameRate(string value)
    {
        var known = FrameRates.FirstOrDefault(r => r.Value == value.Trim());
        if (known.Value is not null)
            return known.Fps;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps is > 1 and < 1000)
            return fps;
        throw new OperationException($"\"{value}\" isn't a frame rate Clementine understands.");
    }

    [GeneratedRegex(@"\[[^\[\]]{0,200}\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\([^()]{0,200}\)")]
    private static partial Regex Parentheses();

    [GeneratedRegex(@"（[^（）]{0,200}）")]
    private static partial Regex FullWidthParentheses();

    /// <summary>"JOHN:", "- MAN 2:", "&lt;i&gt;DR. SMITH:" at the start of a line (two or more capitals, then a colon and a space).</summary>
    [GeneratedRegex(@"(?m)^(?<lead>\s*(?:<[^>]+>|\{\\[^}]*\})*\s*-?\s*)\p{Lu}[\p{Lu}\d .'’\-]*[\p{Lu}\d]\s*:(?=\s|$)")]
    private static partial Regex SpeakerLabel();

    [GeneratedRegex(@"<(i|b|u|s|font)[^>]*>\s*</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyTags();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"^[\s\-–—♪♫♩♬#*~.,!?]*$")]
    private static partial Regex OnlySymbols();

    [GeneratedRegex(@"^(?<tags>(?:<[^>]+>|\{\\[^}]*\})*)\s*[-–—]\s*")]
    private static partial Regex LeadingDash();

    [GeneratedRegex(@"<(\d{1,2}:\d{2}(?::\d{2})?\.\d{3})>")]
    private static partial Regex InlineTimestamp();
}
