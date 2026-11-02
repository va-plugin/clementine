using System.Text;

namespace Clementine.Engines.Files.Subtitles;

/// <summary>
/// Writes a <see cref="SubtitleDocument"/> in any format. Writing the format a cue was read from reuses its original
/// text (VTT voice/class tags, ASS overrides) unless a tool changed it; ASS/SSA scripts keep every section verbatim.
/// </summary>
internal static class SubtitleWriter
{
    public static string Extension(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => ".srt",
        SubtitleFormat.Vtt => ".vtt",
        SubtitleFormat.Ass => ".ass",
        SubtitleFormat.Ssa => ".ssa",
        SubtitleFormat.Sbv => ".sbv",
        _ => ".txt",
    };

    public static string Write(SubtitleDocument document, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => WriteSrt(document),
        SubtitleFormat.Vtt => WriteVtt(document),
        SubtitleFormat.Ass => WriteAss(document, ssa: false),
        SubtitleFormat.Ssa => WriteAss(document, ssa: true),
        SubtitleFormat.Sbv => WriteSbv(document),
        _ => WriteText(document),
    };

    /// <summary>Dialogue cues in display order, with sane timings (end ≥ start, nothing negative) and some text.</summary>
    static List<SubtitleCue> Playable(SubtitleDocument document) =>
        document.Dialogue
            .Where(c => c.Text.Trim().Length > 0)
            .Select(Normalize)
            .OrderBy(c => c.Start) // OrderBy is stable: simultaneous cues keep their order
            .ToList();

    static SubtitleCue Normalize(SubtitleCue cue)
    {
        if (cue.Start < TimeSpan.Zero)
            cue.Start = TimeSpan.Zero;
        if (cue.End < cue.Start)
            cue.End = cue.Start;
        return cue;
    }

    static string WriteSrt(SubtitleDocument document)
    {
        var text = new StringBuilder();
        var index = 1;
        foreach (var cue in Playable(document))
        {
            var body = SubtitleMarkup.ToSrt(cue.Text);
            if (body.Length == 0)
                continue;
            text.Append(index++).Append("\r\n");
            text.Append(SubtitleTime.Srt(cue.Start)).Append(" --> ").Append(SubtitleTime.Srt(cue.End)).Append("\r\n");
            text.Append(body.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return text.ToString();
    }

    static string WriteVtt(SubtitleDocument document)
    {
        var text = new StringBuilder("WEBVTT");
        if (document.Format == SubtitleFormat.Vtt && !string.IsNullOrEmpty(document.VttHeader))
            text.Append(document.VttHeader.StartsWith(' ') || document.VttHeader.StartsWith('\t') || document.VttHeader.StartsWith('\n') ? "" : " ").Append(document.VttHeader);
        text.Append("\n\n");
        if (document.Format == SubtitleFormat.Vtt)
        {
            foreach (var block in document.VttBlocks)
                text.Append(block).Append("\n\n");
        }

        foreach (var cue in Playable(document))
        {
            string body;
            string? settings;
            if (cue.UnchangedSourceText(SubtitleFormat.Vtt) is { } raw)
            {
                body = raw;
                settings = cue.Settings;
            }
            else
            {
                body = SubtitleMarkup.ToVtt(cue.Text, out var position);
                settings = cue.Settings ?? position;
            }
            body = SubtitleMarkup.TidyLines(body);
            if (body.Length == 0)
                continue;
            if (!string.IsNullOrWhiteSpace(cue.Identifier) && !cue.Identifier.Contains("-->", StringComparison.Ordinal))
                text.Append(cue.Identifier).Append('\n');
            text.Append(SubtitleTime.Vtt(cue.Start)).Append(" --> ").Append(SubtitleTime.Vtt(cue.End));
            if (!string.IsNullOrWhiteSpace(settings))
                text.Append(' ').Append(settings);
            text.Append('\n').Append(body).Append("\n\n");
        }
        return text.ToString();
    }

    static string WriteSbv(SubtitleDocument document)
    {
        var text = new StringBuilder();
        foreach (var cue in Playable(document))
        {
            var body = SubtitleMarkup.ToPlain(cue.Text);
            if (body.Length == 0)
                continue;
            text.Append(SubtitleTime.Sbv(cue.Start)).Append(',').Append(SubtitleTime.Sbv(cue.End)).Append("\r\n");
            text.Append(body.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return text.ToString();
    }

    /// <summary>The dialogue as plain text, one cue per line group, with rolling-caption repeats collapsed.</summary>
    static string WriteText(SubtitleDocument document)
    {
        var text = new StringBuilder();
        string? previous = null;
        foreach (var cue in Playable(document))
        {
            foreach (var line in SubtitleMarkup.ToPlain(cue.Text).Split('\n'))
            {
                var trimmed = line.Trim().Replace('\u00A0', ' ');
                if (trimmed.Length == 0 || trimmed == previous)
                    continue;
                text.Append(trimmed).Append("\r\n");
                previous = trimmed;
            }
        }
        return text.ToString();
    }

    // ---------- ASS / SSA ----------

    const string DefaultStyle =
        "Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,90,90,50,1";

    static string WriteAss(SubtitleDocument document, bool ssa)
    {
        // Keep the original script when it is already in this dialect; SSA → ASS starts a fresh ASS script.
        if (document.Ass is { } script && (document.Format == SubtitleFormat.Ssa) == ssa)
            return RewriteScript(document, script);
        return NewScript(document);
    }

    static string NewScript(SubtitleDocument document)
    {
        var text = new StringBuilder();
        text.Append("[Script Info]\r\n");
        text.Append("; Script generated by Clementine\r\n");
        text.Append("ScriptType: v4.00+\r\n");
        text.Append("PlayResX: 1920\r\nPlayResY: 1080\r\n");
        text.Append("WrapStyle: 0\r\nScaledBorderAndShadow: yes\r\nYCbCr Matrix: TV.709\r\n\r\n");
        text.Append("[V4+ Styles]\r\n");
        text.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
        text.Append(DefaultStyle).Append("\r\n\r\n");
        text.Append("[Events]\r\n");
        text.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        foreach (var cue in Playable(document))
        {
            var body = SubtitleMarkup.ToAss(cue.Text);
            text.Append("Dialogue: 0,").Append(SubtitleTime.Ass(cue.Start)).Append(',').Append(SubtitleTime.Ass(cue.End))
                .Append(",Default,,0,0,0,,").Append(body).Append("\r\n");
        }
        return text.ToString();
    }

    static string RewriteScript(SubtitleDocument document, AssScript script)
    {
        var text = new StringBuilder();
        var wroteEvents = false;
        foreach (var (header, lines) in script.Sections)
        {
            text.Append(header).Append("\r\n");
            if (header.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                WriteEvents(text, document, script);
                wroteEvents = true;
            }
            else
            {
                foreach (var line in TrimTrailingBlank(lines))
                    text.Append(line).Append("\r\n");
            }
            text.Append("\r\n");
        }
        if (!wroteEvents)
        {
            text.Append("[Events]\r\n");
            WriteEvents(text, document, script);
        }
        return text.ToString().TrimEnd() + "\r\n";
    }

    static void WriteEvents(StringBuilder text, SubtitleDocument document, AssScript script)
    {
        var format = script.EventFormat.Count > 0 ? script.EventFormat : ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];
        text.Append(script.EventFormatLine.Length > 0 ? script.EventFormatLine : "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text").Append("\r\n");
        foreach (var cue in document.Cues)
        {
            if (!cue.IsComment && cue.Text.Trim().Length == 0)
                continue; // emptied by a tool
            Normalize(cue);
            var raw = cue.UnchangedSourceText(cue.Source?.Format ?? SubtitleFormat.Ass) ?? SubtitleMarkup.ToAss(cue.Text);
            var values = format.Select(field => field switch
            {
                "start" => SubtitleTime.Ass(cue.Start),
                "end" => SubtitleTime.Ass(cue.End),
                "text" => raw,
                _ => cue.AssFields?.GetValueOrDefault(field) ?? DefaultField(field),
            });
            text.Append(cue.IsComment ? "Comment: " : "Dialogue: ").Append(string.Join(',', values)).Append("\r\n");
        }
        foreach (var line in script.OtherEventLines)
            text.Append(line.TrimEnd()).Append("\r\n");
    }

    static string DefaultField(string field) => field switch
    {
        "style" => "Default",
        "layer" or "marginl" or "marginr" or "marginv" => "0",
        "marked" => "Marked=0",
        _ => "",
    };

    static IEnumerable<string> TrimTrailingBlank(List<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Trim().Length == 0)
            end--;
        return lines.Take(end).Select(l => l.TrimEnd());
    }
}
