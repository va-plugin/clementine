using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Subtitles;

/// <summary>
/// Tolerant subtitle parsers. Real-world files have BOMs, mixed CRLF/LF/CR, missing or wrong indices, missing blank
/// lines, "." instead of "," in SRT times, VTT headers and cue settings, ASS files with reordered Format lines.
/// The format is sniffed from the content first (a ".srt" that is really WebVTT is read as WebVTT).
/// </summary>
internal static partial class SubtitleParser
{
    public static SubtitleFormat? FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".srt" => SubtitleFormat.Srt,
        ".vtt" => SubtitleFormat.Vtt,
        ".ass" => SubtitleFormat.Ass,
        ".ssa" => SubtitleFormat.Ssa,
        ".sbv" => SubtitleFormat.Sbv,
        _ => null,
    };

    /// <summary>Parses <paramref name="text"/>; throws a friendly error when it holds no subtitles at all.</summary>
    public static SubtitleDocument Parse(string text, SubtitleFormat hint)
    {
        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var format = Sniff(normalized) ?? hint;
        var document = format switch
        {
            SubtitleFormat.Vtt => ParseVtt(normalized),
            SubtitleFormat.Ass or SubtitleFormat.Ssa => ParseAss(normalized, format),
            SubtitleFormat.Sbv => ParseSbv(normalized),
            _ => ParseSrt(normalized),
        };
        if (document.Cues.Count == 0)
            throw new OperationException("No subtitles were found in this file.");
        return document;
    }

    static SubtitleFormat? Sniff(string text)
    {
        var start = text.TrimStart();
        if (start.StartsWith("WEBVTT", StringComparison.Ordinal))
            return SubtitleFormat.Vtt;
        if (start.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) || AssEventsHeader().IsMatch(text))
            return start.Contains("ScriptType: v4.00+", StringComparison.OrdinalIgnoreCase) || text.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
                ? SubtitleFormat.Ass
                : text.Contains("[V4 Styles]", StringComparison.OrdinalIgnoreCase) ? SubtitleFormat.Ssa : SubtitleFormat.Ass;
        var firstLine = start.Split('\n', 2)[0].Trim();
        if (SbvTiming().IsMatch(firstLine))
            return SubtitleFormat.Sbv;
        if (text.Contains("-->", StringComparison.Ordinal))
            return SubtitleFormat.Srt;
        return null;
    }

    // ---------- SRT ----------

    /// <summary>
    /// Finds every timing line, takes the text up to the next one, and drops a numeric index line directly before a
    /// timing line — so missing indices, missing or extra blank lines and blank lines inside text all work.
    /// </summary>
    internal static SubtitleDocument ParseSrt(string text)
    {
        var document = new SubtitleDocument { Format = SubtitleFormat.Srt };
        var lines = text.Split('\n');
        // A line shaped like a timing line still ends the previous cue even when its times are impossible
        // ("00:00:75,000"); otherwise it and its text would be glued onto the cue before it.
        var timings = new List<(int Line, TimeSpan Start, TimeSpan End, bool Valid)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SrtTiming().Match(lines[i]);
            if (!match.Success)
                continue;
            var valid = SubtitleTime.TryParse(match.Groups["a"].Value, out var start) & SubtitleTime.TryParse(match.Groups["b"].Value, out var end);
            timings.Add((i, start, end, valid));
        }

        var malformed = 0;
        var badTimes = 0;
        for (var t = 0; t < timings.Count; t++)
        {
            if (!timings[t].Valid)
            {
                badTimes++;
                continue;
            }
            var first = timings[t].Line + 1;
            var last = (t + 1 < timings.Count ? timings[t + 1].Line : lines.Length) - 1;
            if (t + 1 < timings.Count && last >= first && IndexLine().IsMatch(lines[last]))
                last--; // the next cue's index
            var body = string.Join('\n', lines[first..(last + 1)]);
            var neutral = SubtitleMarkup.FromSrt(body);
            if (neutral.Length == 0)
            {
                malformed++;
                continue;
            }
            document.Cues.Add(new SubtitleCue
            {
                Start = timings[t].Start,
                End = timings[t].End,
                Text = neutral,
                Source = new SourceText(SubtitleFormat.Srt, neutral, neutral),
            });
        }
        if (malformed > 0)
            document.Warnings.Add($"{malformed} empty cue(s) skipped.");
        if (badTimes > 0)
            document.Warnings.Add($"{badTimes} cue(s) with an impossible timestamp (such as 75 seconds) skipped.");
        return document;
    }

    // ---------- WebVTT ----------

    internal static SubtitleDocument ParseVtt(string text)
    {
        var document = new SubtitleDocument { Format = SubtitleFormat.Vtt };
        var blocks = Blocks(text);
        var index = 0;
        if (blocks.Count > 0 && blocks[0][0].TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            var header = blocks[0];
            var firstLine = header[0].TrimStart()[6..];
            // The header may run straight into the first cue when the blank line is missing.
            var timing = header.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
            if (timing > 0)
            {
                var cueStart = timing - 1 >= 1 && !header[timing - 1].Contains(':') ? timing - 1 : timing;
                document.VttHeader = string.Join('\n', [firstLine, .. header[1..cueStart]]);
                blocks[0] = header[cueStart..];
            }
            else
            {
                document.VttHeader = string.Join('\n', [firstLine, .. header[1..]]);
                index = 1;
            }
        }
        else
        {
            document.Warnings.Add("The file doesn't start with \"WEBVTT\"; read it anyway.");
        }

        var skipped = 0;
        for (; index < blocks.Count; index++)
        {
            foreach (var block in SplitRunOnCues(blocks[index]))
                AddVttBlock(document, block, ref skipped);
        }
        if (skipped > 0)
            document.Warnings.Add($"{skipped} block(s) without a valid timing or text were skipped.");
        return document;
    }

    /// <summary>Broken files sometimes omit the blank line between cues: split a block at every extra timing line.</summary>
    static IEnumerable<List<string>> SplitRunOnCues(List<string> block)
    {
        var timings = block.Select((line, i) => (line, i)).Where(x => x.line.Contains("-->", StringComparison.Ordinal)).Select(x => x.i).ToList();
        if (timings.Count <= 1)
        {
            yield return block;
            yield break;
        }
        var start = 0;
        for (var k = 1; k < timings.Count; k++)
        {
            // A lone number right before the next timing line is that cue's identifier.
            var cut = timings[k] - 1 > timings[k - 1] && IndexLine().IsMatch(block[timings[k] - 1]) ? timings[k] - 1 : timings[k];
            yield return block[start..cut];
            start = cut;
        }
        yield return block[start..];
    }

    static void AddVttBlock(SubtitleDocument document, List<string> block, ref int skipped)
    {
        var head = block[0].TrimStart();
        if (IsVttKeywordBlock(head, "NOTE") || IsVttKeywordBlock(head, "STYLE") || IsVttKeywordBlock(head, "REGION"))
        {
            // STYLE and REGION must come before cues; NOTEs between cues are comments and can go.
            if (document.Cues.Count == 0)
                document.VttBlocks.Add(string.Join('\n', block));
            return;
        }

        var timingLine = block.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
        var match = timingLine < 0 ? Match.Empty : VttTiming().Match(block[timingLine]);
        if (!match.Success
            || !SubtitleTime.TryParse(match.Groups["a"].Value, out var start)
            || !SubtitleTime.TryParse(match.Groups["b"].Value, out var end))
        {
            skipped++;
            return;
        }

        var raw = string.Join('\n', block.Skip(timingLine + 1));
        var neutral = SubtitleMarkup.FromVtt(raw);
        if (neutral.Length == 0)
        {
            skipped++;
            return;
        }
        var settings = match.Groups["settings"].Value.Trim();
        document.Cues.Add(new SubtitleCue
        {
            Start = start,
            End = end,
            Text = neutral,
            Identifier = timingLine > 0 ? string.Join(' ', block.Take(timingLine)).Trim() : null,
            Settings = settings.Length > 0 ? settings : null,
            Source = new SourceText(SubtitleFormat.Vtt, raw, neutral),
        });
    }

    static bool IsVttKeywordBlock(string line, string keyword) =>
        line.StartsWith(keyword, StringComparison.Ordinal) && (line.Length == keyword.Length || char.IsWhiteSpace(line[keyword.Length]));

    // ---------- ASS / SSA ----------

    static readonly string[] DefaultAssFormat = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];
    static readonly string[] DefaultSsaFormat = ["marked", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];

    internal static SubtitleDocument ParseAss(string text, SubtitleFormat hint)
    {
        var script = new AssScript();
        var document = new SubtitleDocument { Format = hint, Ass = script };
        List<string>? current = null;
        var inEvents = false;
        var drawings = 0;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = [];
                script.Sections.Add((trimmed, current));
                inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (current is null)
            {
                if (trimmed.Length > 0)
                {
                    current = [];
                    script.Sections.Add(("[Script Info]", current));
                }
                else
                {
                    continue;
                }
            }
            if (!inEvents)
            {
                current.Add(line);
                ReadScriptInfo(script, trimmed);
                continue;
            }

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                script.EventFormatLine = trimmed;
                script.EventFormat = trimmed[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
                continue;
            }
            var isDialogue = trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase);
            var isComment = trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase);
            if (!isDialogue && !isComment)
            {
                if (trimmed.Length > 0 && !trimmed.StartsWith(';'))
                    script.OtherEventLines.Add(line);
                continue;
            }

            if (script.EventFormat.Count == 0)
            {
                script.EventFormat = [.. script.IsSsa || hint == SubtitleFormat.Ssa ? DefaultSsaFormat : DefaultAssFormat];
                script.EventFormatLine = "Format: " + string.Join(", ", script.EventFormat.Select(Capitalize));
            }
            var body = trimmed[(trimmed.IndexOf(':') + 1)..].TrimStart();
            var values = body.Split(',', script.EventFormat.Count);
            if (values.Length < script.EventFormat.Count)
                continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var f = 0; f < script.EventFormat.Count; f++)
                fields[script.EventFormat[f]] = f == script.EventFormat.Count - 1 ? values[f] : values[f].Trim();
            if (!SubtitleTime.TryParse(fields.GetValueOrDefault("start", ""), out var start) || !SubtitleTime.TryParse(fields.GetValueOrDefault("end", ""), out var end))
                continue;

            var raw = fields.GetValueOrDefault("text", "");
            var neutral = SubtitleMarkup.FromAss(raw, script.WrapStyle, out var hadDrawing);
            if (hadDrawing && !isComment)
                drawings++;
            document.Cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = neutral,
                AssFields = fields,
                IsComment = isComment,
                Source = new SourceText(hint == SubtitleFormat.Ssa || script.IsSsa ? SubtitleFormat.Ssa : SubtitleFormat.Ass, raw, neutral),
            });
        }

        if (script.IsSsa)
            document.Format = SubtitleFormat.Ssa;
        if (drawings > 0)
            document.Warnings.Add($"{drawings} vector drawing(s) can't be shown in other formats and were left out.");
        return document;
    }

    static void ReadScriptInfo(AssScript script, string line)
    {
        if (line.StartsWith("WrapStyle:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[10..].Trim(), out var wrap))
            script.WrapStyle = wrap;
        else if (line.StartsWith("ScriptType:", StringComparison.OrdinalIgnoreCase))
            script.IsSsa = !line.Contains("v4.00+", StringComparison.OrdinalIgnoreCase);
    }

    static string Capitalize(string field) => field switch
    {
        "marginl" => "MarginL",
        "marginr" => "MarginR",
        "marginv" => "MarginV",
        _ => char.ToUpperInvariant(field[0]) + field[1..],
    };

    // ---------- SBV (YouTube) ----------

    internal static SubtitleDocument ParseSbv(string text)
    {
        var document = new SubtitleDocument { Format = SubtitleFormat.Sbv };
        foreach (var block in Blocks(text))
        {
            var match = SbvTiming().Match(block[0].Trim());
            if (!match.Success || !SubtitleTime.TryParse(match.Groups["a"].Value, out var start) || !SubtitleTime.TryParse(match.Groups["b"].Value, out var end))
                continue;
            // SBV has no markup; "[br]" is YouTube's line break.
            var neutral = SubtitleMarkup.TidyLines(string.Join('\n', block.Skip(1)).Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase));
            if (neutral.Length == 0)
                continue;
            document.Cues.Add(new SubtitleCue { Start = start, End = end, Text = neutral, Source = new SourceText(SubtitleFormat.Sbv, neutral, neutral) });
        }
        return document;
    }

    /// <summary>Non-empty line groups separated by blank (or whitespace-only) lines.</summary>
    static List<List<string>> Blocks(string text)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = [];
                blocks.Add(current);
            }
            current.Add(line.TrimEnd());
        }
        return blocks;
    }

    [GeneratedRegex(@"^\s*(?<a>\d{1,4}:\d{1,2}(?::\d{1,2})?(?:[,.:]\d+)?)\s*(?:-{1,3}>|–>|—>|->)\s*(?<b>\d{1,4}:\d{1,2}(?::\d{1,2})?(?:[,.:]\d+)?)")]
    private static partial Regex SrtTiming();

    [GeneratedRegex(@"^\s*(?<a>(?:\d+:)?\d{1,2}:\d{1,2}[.,]\d{1,3})\s+-->\s+(?<b>(?:\d+:)?\d{1,2}:\d{1,2}[.,]\d{1,3})(?<settings>.*)$")]
    private static partial Regex VttTiming();

    [GeneratedRegex(@"^(?<a>\d+:\d{1,2}:\d{1,2}\.\d{1,3}),(?<b>\d+:\d{1,2}:\d{1,2}\.\d{1,3})$")]
    private static partial Regex SbvTiming();

    [GeneratedRegex(@"^\s*\d+\s*$")]
    private static partial Regex IndexLine();

    [GeneratedRegex(@"^\[Events\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AssEventsHeader();
}
